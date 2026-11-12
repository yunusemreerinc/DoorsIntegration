using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using DoorsIntegration.Core;
using DoorsIntegration.Export;
using DoorsIntegration.Com;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class BulkBaselineReportingTests
    {
        [DataTestMethod]
        [DataRow("Authorization: Bearer PRIVATE_AUTH_TOKEN")]
        [DataRow("authorization=Basic PRIVATE_BASIC_TOKEN")]
        [DataRow("Proxy-Authorization: Digest username=operator, response=PRIVATE_DIGEST")]
        [DataRow("{\"Authorization\":\"Basic PRIVATE_JSON_AUTH\"}")]
        public void AuthenticationHeadersAreEntirelyMasked(string header)
        {
            var text = BaselineAuditSanitizer.Text(header + "; module=/Project/Module");
            Assert.IsFalse(text.Contains("PRIVATE_"), text);
            StringAssert.Contains(text, "[REDACTED]"); StringAssert.Contains(text, "module=/Project/Module");
        }
        [TestMethod]
        public void QuotedCredentialKeysAreMasked()
        {
            var text = BaselineAuditSanitizer.Text("{\"password\":\"PRIVATE_JSON_PASSWORD\",\"token\":\"PRIVATE_JSON_TOKEN\"}");
            Assert.IsFalse(text.Contains("PRIVATE_"), text); StringAssert.Contains(text, "[REDACTED]");
        }
        [TestMethod]
        public async Task ReconciliationPreservesCompletedAttemptTimeAndDuration()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); await f.Runner().RunAsync(job.JobId);
                // Compare durable values: the existing JSON contract stores DateTime at millisecond precision.
                var expectedEnd = f.Raw(job.JobId).Modules[0].CompletedAtUtc;
                var expectedDuration = (await Reports(f).ReadAsync(job.JobId)).Modules[0].DurationSeconds;
                f.Preflight.Clock.Offset = TimeSpan.FromSeconds(30);
                var reconciled = await f.Runner().OpenAsync(job.JobId);
                Assert.AreEqual(expectedEnd, reconciled.Modules[0].CompletedAtUtc);
                Assert.AreEqual(expectedDuration, (await Reports(f).ReadAsync(job.JobId)).Modules[0].DurationSeconds);
                f.Server.Targets[job.Modules[0].ModuleId] = BaselineJobVerificationStatus.Unknown;
                var uncertain = await f.Runner().OpenAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.NeedsReconciliation, uncertain.Modules[0].Status);
                Assert.AreEqual(expectedEnd, uncertain.Modules[0].CompletedAtUtc);
                Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task ApprovalScopeIsMaskedBeforeAuditPublication()
        {
            var fixture = new PreflightTestFixture(); var plan = await fixture.Service.CreatePlanAsync(fixture.Request());
            var approval = BaselineApprovedPlan.Capture(plan, Guid.NewGuid(), DateTime.UtcNow, "operator", "Authorization: Bearer PRIVATE_SCOPE_TOKEN");
            Assert.IsFalse(approval.Scope.Contains("PRIVATE_")); StringAssert.Contains(approval.Scope, "[REDACTED]");
        }
        private sealed class MovingHistoryJournal : IBulkBaselineJobJournal
        {
            private readonly BulkBaselineJobRecord before, after;
            private int reads;
            internal MovingHistoryJournal(BulkBaselineJobRecord before, BulkBaselineJobRecord after) { this.before = before; this.after = after; }
            public IBulkBaselineJobJournalLease Acquire(Guid id) => new SnapshotLease(++reads == 1 ? before : after);
            private sealed class SnapshotLease : IBulkBaselineJobJournalLease
            {
                internal SnapshotLease(BulkBaselineJobRecord job) { Current = job; }
                public BulkBaselineJobRecord Current { get; }
                public void Save(BulkBaselineJobRecord record) => throw new InvalidOperationException("History may not write.");
                public void Dispose() { }
            }
        }
        [TestMethod]
        public async Task HistoryDoesNotDisplayAChangedRevisionUnderStaleFilterResults()
        {
            using (var f = new JobTestFixture(1))
            {
                var initial = await f.Create(); var completed = await f.Runner().RunAsync(initial.JobId); int reads = f.Server.Reads;
                var history = new JobHistoryService(new MovingHistoryJournal(initial, completed), f.Journal);
                var result = await history.QueryAsync(new BaselineJobHistoryFilter(outcome: BaselineBatchOutcome.Indeterminate));
                Assert.AreEqual(1, result.Matched); Assert.AreEqual(0, result.Items.Count); Assert.AreEqual(1, result.Issues.Count);
                StringAssert.Contains(result.Issues[0], "changed during history query"); Assert.AreEqual(reads, f.Server.Reads);
            }
        }
        private static BulkBaselineReportService Reports(JobTestFixture f) => new BulkBaselineReportService(f.Journal, f.Journal,
            new ExportService(new IExportFormatWriter[] { new JsonExportWriter(), new CsvExportWriter(), new XlsxExportWriter() }, f.Preflight.Log));
        private static string[] Frames(JobTestFixture f, Guid id) => Directory.GetFiles(Path.Combine(f.Root, id.ToString("N") + ".audit"), "*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        [TestMethod]
        public async Task CommittedAuditFramesRemainUnchangedAcrossExecutionAndReconciliation()
        {
            using (var f = new JobTestFixture(2))
            {
                var job = await f.Create(); var genesis = Frames(f, job.JobId).Single(); byte[] original = File.ReadAllBytes(genesis);
                var completed = await f.Runner().RunAsync(job.JobId); var before = Frames(f, job.JobId).ToDictionary(p => p, File.ReadAllBytes);
                await f.Runner().OpenAsync(job.JobId);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(genesis));
                foreach (var pair in before) CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key));
                var entries = f.Journal.ReadAudit(job.JobId, long.MaxValue, CancellationToken.None).ToArray();
                Assert.IsTrue(entries.Length > before.Count); Assert.AreEqual("Created", entries[0].Action);
                for (int i = 1; i < entries.Length; i++) { Assert.AreEqual(entries[i - 1].Hash, entries[i].PreviousHash); Assert.AreEqual(entries[i - 1].Revision + 1, entries[i].Revision); }
                Assert.IsTrue(entries.Last().Revision > completed.Revision);
                Assert.IsTrue(entries.Any(e => e.Action == "RunStarted")); Assert.IsTrue(entries.Any(e => e.Action == "RunCompleted"));
                Assert.AreEqual("Reconciled", entries.Last().Action);
            }
        }
        [DataTestMethod, DataRow(false), DataRow(true)]
        public async Task AuditHeadRecoversLaggingOrMissingReplaceableCache(bool missing)
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); byte[] old = File.ReadAllBytes(f.PathOf(job.JobId));
                var completed = await f.Runner().RunAsync(job.JobId);
                if (missing) File.Delete(f.PathOf(job.JobId)); else File.WriteAllBytes(f.PathOf(job.JobId), old);
                using (var lease = f.Journal.Acquire(job.JobId)) { Assert.AreEqual(completed.Revision, lease.Current.Revision); Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, lease.Current.Status); }
                await f.Runner().OpenAsync(job.JobId); Assert.AreEqual(1, f.Adapter.Calls);
                Assert.IsTrue(File.Exists(f.PathOf(job.JobId)), "Recovered cache can be atomically re-materialized.");
            }
        }
        [DataTestMethod, DataRow("projection"), DataRow("ciphertext"), DataRow("middle"), DataRow("tail")]
        public async Task AuditCorruptionOrMissingEvidenceFailsClosed(string kind)
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); await f.Runner().RunAsync(job.JobId); var frames = Frames(f, job.JobId);
                if (kind == "middle") File.Delete(frames[1]);
                else if (kind == "tail") File.Delete(frames.Last());
                else { string path = frames[1], data = File.ReadAllText(path); data = kind == "projection" ? data.Replace("RunStarted", "RunAltered") : data.Replace("\"ProtectedDelta\":\"", "\"ProtectedDelta\":\"INVALID"); File.WriteAllText(path, data); }
                Assert.ThrowsException<BulkBaselineJournalException>(() => f.Journal.Acquire(job.JobId)); Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task AuditCommitBeforeFailedCachePublicationSurvivesRestartWithoutDispatch()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create();
                using (new FileStream(f.PathOf(job.JobId), FileMode.Open, FileAccess.Read, FileShare.Read))
                    await Assert.ThrowsExceptionAsync<BulkBaselineJournalException>(() => f.Runner().RunAsync(job.JobId));
                Assert.AreEqual(0, f.Adapter.Calls);
                using (var lease = f.Journal.Acquire(job.JobId)) { Assert.AreEqual(job.Revision + 1, lease.Current.Revision); Assert.AreEqual(BaselineAuditAction.RunStarted, lease.Current.LastAuditAction); }
                await f.Runner().OpenAsync(job.JobId); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task ProtectionFailureHasNoPlaintextFallbackAndNoAdapterDispatch()
        {
            using (var f = new JobTestFixture(1))
            {
                var broken = new FileBulkBaselineJobJournal(new BulkBaselineJournalOptions(f.Root), new RejectProtection());
                var plan = await f.Preflight.Service.CreatePlanAsync(f.Preflight.Request());
                var error = await Assert.ThrowsExceptionAsync<BulkBaselineJournalException>(() => f.Runner(broken).CreateAsync(plan));
                Assert.IsFalse(error.Message.Contains("PRIVATE_")); Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Length); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        private sealed class RejectProtection : IBaselineJournalProtector
        { public byte[] Protect(byte[] data) => throw new CryptographicException("PRIVATE_PROTECTION_DETAIL"); public byte[] Unprotect(byte[] data) => throw new CryptographicException(); }
        [TestMethod]
        public async Task ApprovalAuditCheckpointFailurePreventsWriteDispatch()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); var fault = new FaultingJobJournal(f.Journal) { BeforeSave = j => j.LastAuditAction == BaselineAuditAction.PlanApproved };
                await Assert.ThrowsExceptionAsync<BulkBaselineJournalException>(() => f.Runner(fault).RunAsync(job.JobId));
                Assert.AreEqual(0, f.Adapter.Calls); Assert.IsFalse(f.Journal.ReadAudit(job.JobId, long.MaxValue, CancellationToken.None).Any(e => e.ApprovedPlan != null));
            }
        }
        [TestMethod]
        public async Task PublicAuditAndReportsMaskCredentialsWhileProtectedRecoveryRetainsExactInput()
        {
            const string annotation = "Review 注釈\npassword=PRIVATE_PASSWORD; token=PRIVATE_TOKEN; https://user:PRIVATE_URI@example.invalid/";
            using (var f = new JobTestFixture(1))
            {
                var plan = await f.Preflight.Service.CreatePlanAsync(f.Preflight.Request(annotation: annotation));
                var job = await f.Runner().CreateAsync(plan); await f.Runner().RunAsync(job.JobId);
                Assert.AreEqual(annotation, f.Journal.ReadCachedSnapshot(job.JobId).JobAnnotation);
                foreach (var file in Frames(f, job.JobId).Concat(new[] { f.PathOf(job.JobId) }))
                { string text = File.ReadAllText(file); Assert.IsFalse(text.Contains("PRIVATE_"), file); }
                var report = await Reports(f).ReadAsync(job.JobId); StringAssert.Contains(report.Annotation, "[REDACTED]");
                Assert.IsFalse(report.ReadAudit().SelectMany(e => e.Modules).Any(m => m.Annotation.Contains("PRIVATE_")));
                Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task VerifiedSuccessRecordsActualAndExplicitFallbackApprovalScope()
        {
            using (var f = new JobTestFixture(2))
            {
                var job = await f.Create(); await f.Runner().RunAsync(job.JobId); int reads = f.Server.Reads;
                var report = await Reports(f).ReadAsync(job.JobId);
                Assert.AreEqual(BaselineBatchOutcome.Success, report.Outcome); Assert.IsTrue(report.IsFullSuccess);
                Assert.AreEqual(2, report.SuccessCount); Assert.AreEqual(0, report.UnknownCount + report.FailedCount + report.SkippedCount);
                Assert.IsTrue(report.Modules.All(m => m.ActualBaseline == m.ExpectedBaseline && m.ActualBaseline != null && m.DurationSeconds != null));
                Assert.IsNotNull(report.StartedAtUtc); Assert.IsNotNull(report.EndedAtUtc); Assert.AreEqual("mock-user", report.Context.Operator);
                Assert.AreEqual("mock-database", report.Context.DatabaseIdentifier); StringAssert.Contains(report.Context.ClientVersionEvidence, "Simulated");
                var approvals = report.ReadAudit().Where(e => e.ApprovedPlan != null).Select(e => e.ApprovedPlan).ToArray();
                Assert.AreEqual(2, approvals.Length); Assert.IsTrue(approvals.All(p => p.Modules.Count == 1 && p.Scope.Contains("single-module")));
                Assert.AreEqual(reads, f.Server.Reads, "Reporting makes no DOORS verification call.");
                Assert.AreEqual("FromCreation", report.AuditCoverage); StringAssert.Contains(BulkBaselineReport.Disclosure, "does not replace DOORS native history");
            }
        }
        [TestMethod]
        public async Task PartialFailureIsNeverFullSuccess()
        {
            using (var f = new JobTestFixture(3))
            {
                var job = await f.Create(); f.Adapter.Outcome = a => a.Item.ModuleId == job.Modules[0].ModuleId ? BaselineWriteOutcome.NotRun : BaselineWriteOutcome.Created;
                await f.Runner().RunAsync(job.JobId); var report = await Reports(f).ReadAsync(job.JobId);
                Assert.AreEqual(BaselineBatchOutcome.PartialSuccess, report.Outcome); Assert.IsFalse(report.IsFullSuccess); Assert.AreEqual(2, report.SuccessCount); Assert.AreEqual(1, report.FailedCount);
                Assert.IsNull(report.Group(BaselineReportGroup.Failed).Single().ActualBaseline);
            }
        }
        [TestMethod]
        public async Task LockedAndOtherSkippedModulesHaveSeparateGroupsAndExclusiveCounts()
        {
            using (var f = new JobTestFixture(3))
            {
                f.Preflight.States[0].LockState = BaselineLockState.OwnedByOtherSession;
                f.Preflight.States[0].EditState = BaselineEditState.ExclusiveOtherSession;
                f.Preflight.States[1].CreateAccess = BaselineCheckState.Failed;
                var job = await f.Create(); await f.Runner().RunAsync(job.JobId); var report = await Reports(f).ReadAsync(job.JobId);
                Assert.AreEqual(1, report.LockedCount); Assert.AreEqual(2, report.SkippedCount); Assert.AreEqual(1, report.SuccessCount);
                Assert.AreEqual(1, report.Group(BaselineReportGroup.Skipped).Count()); Assert.AreEqual(3, report.SuccessCount + report.FailedCount + report.SkippedCount + report.UnknownCount);
                Assert.AreEqual(BaselineBatchOutcome.PartialSuccess, report.Outcome); Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task UnknownAndPendingOutcomesRemainUnverifiableWithoutActualBaseline()
        {
            using (var f = new JobTestFixture(2))
            {
                var job = await f.Create(); f.Adapter.Outcome = _ => BaselineWriteOutcome.NeedsReconciliation; await f.Runner().RunAsync(job.JobId);
                var report = await Reports(f).ReadAsync(job.JobId); Assert.AreEqual(2, report.UnknownCount); Assert.IsFalse(report.IsFullSuccess);
                Assert.AreEqual(BaselineBatchOutcome.Indeterminate, report.Outcome); Assert.IsTrue(report.Modules.All(m => m.ActualBaseline == null));
            }
        }
        [TestMethod]
        public async Task ContradictedStickySuccessLosesActualAtNewRevisionButPreservesHistoricalEvidence()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); await f.Runner().RunAsync(job.JobId); var old = await Reports(f).ReadAsync(job.JobId);
                f.Server.Targets[job.Modules[0].ModuleId] = BaselineJobVerificationStatus.Unknown; await f.Runner().OpenAsync(job.JobId);
                var report = await Reports(f).ReadAsync(job.JobId); Assert.AreEqual(1, report.UnknownCount); Assert.AreEqual(0, report.SuccessCount); Assert.IsNull(report.Modules[0].ActualBaseline);
                Assert.IsNotNull(old.Modules[0].ActualBaseline); Assert.AreEqual(old.Revision, old.ReadAudit().Last().Revision); Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [DataTestMethod, DataRow(ExportFormat.Json), DataRow(ExportFormat.Csv), DataRow(ExportFormat.Xlsx)]
        public async Task ExportsUseSharedFormatsAndPreservePlanResultsUnicodeUtcAndSafeCells(ExportFormat format)
        {
            using (var f = new JobTestFixture(2))
            {
                const string annotation = "=SUM(1,2)\nİnceleme 注釈";
                var job = await f.Runner().CreateAsync(await f.Preflight.Service.CreatePlanAsync(f.Preflight.Request(annotation: annotation)));
                await f.Runner().RunAsync(job.JobId);
                var result = await Reports(f).ExportAsync(job.JobId, new ExportRequest(Path.Combine(f.Root, "export"), format, ExportErrorPolicy.StopOnInvalidRow));
                Assert.AreEqual(ExportStatus.Completed, result.Status, File.ReadAllText(result.ReportPath));
                Assert.AreEqual(1L, result.RowCounts[ExportTableKind.BulkBaselineJobs]); Assert.AreEqual(2L, result.RowCounts[ExportTableKind.BulkBaselineModules]);
                Assert.AreEqual(2L, result.RowCounts[ExportTableKind.BulkBaselineApprovedPlans]); Assert.IsTrue(result.RowCounts[ExportTableKind.BulkBaselineAuditEvents] > 2);
                if (format == ExportFormat.Json)
                {
                    var json = File.ReadAllText(Path.Combine(result.DirectoryPath, "data.json")); StringAssert.Contains(json, "İnceleme 注釈"); StringAssert.Contains(json, "Z\""); StringAssert.Contains(json, "actualBaseline");
                }
                else if (format == ExportFormat.Csv)
                {
                    Assert.IsTrue(result.CsvProtectedCellCount > 0); var csv = File.ReadAllText(Path.Combine(result.DirectoryPath, "BulkBaselineModules.csv")); StringAssert.Contains(csv, "\t=SUM(1,2)"); StringAssert.Contains(csv, "İnceleme 注釈");
                    CollectionAssert.AreEqual(new byte[] { 239, 187, 191 }, File.ReadAllBytes(Path.Combine(result.DirectoryPath, "BulkBaselineModules.csv")).Take(3).ToArray());
                }
                else using (var document = SpreadsheetDocument.Open(Path.Combine(result.DirectoryPath, "data.xlsx"), false))
                {
                    Assert.AreEqual(0, new OpenXmlValidator().Validate(document).Count());
                    Assert.IsTrue(document.WorkbookPart.Workbook.Sheets.Elements<Sheet>().Any(s => s.Name.Value.StartsWith("BulkBaselineApprovedPlans", StringComparison.Ordinal)));
                    Assert.IsFalse(document.WorkbookPart.WorksheetParts.Any(p => p.Worksheet.Descendants<CellFormula>().Any()));
                    Assert.IsTrue(document.WorkbookPart.WorksheetParts.SelectMany(p => p.Worksheet.Descendants<Cell>()).Any(c => c.InnerText == annotation.Replace("\n", "_x000A_")));
                }
            }
        }
        [TestMethod]
        public async Task HistoryFiltersPagesAndReportsUnavailableJobsWithoutCreatingOrReconciling()
        {
            using (var f = new JobTestFixture(1))
            {
                var first = await f.Create(); var second = await f.Create(); await f.Runner().RunAsync(first.JobId);
                Guid corrupt = Guid.NewGuid(); File.WriteAllText(f.PathOf(corrupt), "INVALID"); int reads = f.Server.Reads;
                var history = new JobHistoryService(f.Journal, f.Journal);
                var page = await history.QueryAsync(new BaselineJobHistoryFilter(actor: "MOCK-USER", database: "database", source: BaselineMetadataSource.Mock, repository: "repo", target: DoorsVersion.V97, limit: 1));
                Assert.AreEqual(2, page.Matched); Assert.AreEqual(1, page.Items.Count); Assert.AreEqual(1, page.Issues.Count);
                Assert.AreEqual(1, (await history.QueryAsync(new BaselineJobHistoryFilter(outcome: BaselineBatchOutcome.Success))).Matched);
                Assert.AreEqual(1, (await history.QueryAsync(new BaselineJobHistoryFilter(search: second.JobId.ToString()))).Matched);
                Assert.AreEqual(0, (await history.QueryAsync(new BaselineJobHistoryFilter(database: "unrelated"))).Matched);
                Assert.AreEqual(0, (await history.QueryAsync(new BaselineJobHistoryFilter(fromUtc: DateTime.UtcNow.AddDays(1)))).Matched);
                Assert.AreEqual(1, (await history.QueryAsync(new BaselineJobHistoryFilter(offset: 1, limit: 1))).Items.Count);
                Assert.IsTrue((await history.QueryAsync(new BaselineJobHistoryFilter(maxScan: 1))).ScanLimited);
                var opened = await history.OpenAsync(first.JobId); Assert.AreEqual((await Reports(f).ReadAsync(first.JobId)).Revision, opened.Revision);
                Assert.AreEqual(reads, f.Server.Reads); Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task ExportOfCapturedAuditPrefixDetectsMissingTailAndPublishesNoPartialPayload()
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); await f.Runner().RunAsync(job.JobId); var report = await Reports(f).ReadAsync(job.JobId);
                File.Delete(Frames(f, job.JobId).Last());
                var exporter = new ExportService(new IExportFormatWriter[] { new JsonExportWriter() }, f.Preflight.Log);
                var result = await exporter.ExportAsync(new ExportRequest(Path.Combine(f.Root, "captured"), ExportFormat.Json), new BulkBaselineReportSource(report));
                Assert.AreEqual(ExportStatus.Failed, result.Status); Assert.IsFalse(File.Exists(Path.Combine(result.DirectoryPath, "data.json"))); Assert.IsTrue(File.Exists(result.ReportPath));
            }
        }
        [DataContract]
        private sealed class LegacyEnvelope
        {
            [DataMember(Order = 0)] internal int Schema = 1;
            [DataMember(Order = 1)] internal string Checksum;
            [DataMember(Order = 2)] internal BulkBaselineJobRecord Job;
        }
        private static void Set(object value, string name, object field) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance).SetValue(value, field);
        [TestMethod]
        public async Task LegacySnapshotImportsWithExplicitCoverageAndWithoutFabricatedApprovals()
        {
            using (var f = new JobTestFixture(1))
            {
                var created = await f.Create(); var job = f.Raw(created.JobId);
                // Produce the pre-audit schema: new optional members are absent from the wire.
                Set(job, "AuditContext", null); Set(job, "JobSuffix", null); Set(job, "JobAnnotation", null); Set(job, "LastAuditAction", BaselineAuditAction.Checkpoint);
                foreach (var m in job.Modules) { Set(m, "PreflightReason", null); Set(m, "ObservedLockState", null); }
                var guard = typeof(BulkBaselineJobRecord).Assembly.GetType("DoorsIntegration.Core.BaselineJobGuard");
                Set(job, "InputFingerprint", guard.GetMethod("InputHash", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { job }));
                byte[] bytes; var serializer = new DataContractJsonSerializer(typeof(BulkBaselineJobRecord));
                using (var data = new MemoryStream()) { serializer.WriteObject(data, job); bytes = data.ToArray(); }
                string checksum; using (var sha = SHA256.Create()) checksum = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                foreach (var file in Frames(f, job.JobId)) File.Delete(file); // This test owns the synthetic legacy fixture only.
                using (var output = File.Create(f.PathOf(job.JobId))) new DataContractJsonSerializer(typeof(LegacyEnvelope)).WriteObject(output, new LegacyEnvelope { Job = job, Checksum = checksum });
                var legacy = await Reports(f).ReadAsync(job.JobId); StringAssert.Contains(legacy.AuditCoverage, "LegacySnapshotOnly"); Assert.AreEqual(0, legacy.ReadAudit().Count());
                await f.Runner().OpenAsync(job.JobId);
                var imported = await Reports(f).ReadAsync(job.JobId); Assert.AreEqual("LegacyImported", imported.ReadAudit().First().Action);
                StringAssert.Contains(imported.AuditCoverage, "ImportedFromRevision");
                Assert.IsTrue(imported.ReadAudit().All(e => e.ApprovedPlan == null)); Assert.AreEqual(0, f.Adapter.Calls);
                StringAssert.Contains(File.ReadAllText(f.PathOf(job.JobId)), "\"Schema\":2");
            }
        }
        private sealed class RegistrationEvidence : IComRegistrationInspector
        { public ComRegistrationSnapshot Inspect(ComArchitecture architecture) => new ComRegistrationSnapshot(architecture, ComRegistrationState.Registered, fileVersion: "9.7.2.6", versionMajor: 9, versionMinor: 7); }
        [TestMethod]
        public async Task RegisteredClientVersionIsRecordedWithEvidenceWithoutClaimingActiveSessionVersion()
        {
            var f = new PreflightTestFixture(); var plan = await f.Service.CreatePlanAsync(f.Request());
            var context = new ComBaselineJobAuditContextProvider(new RegistrationEvidence()).Capture(plan, BaselineMetadataSource.NativeDxl);
            Assert.AreEqual("9.7.2.6", context.ClientVersion); StringAssert.Contains(context.ClientVersionEvidence, "RegisteredBinaryOnly"); StringAssert.Contains(context.ClientVersionEvidence, "NOT_VERIFIED");
        }
        [DataTestMethod, DataRow(false), DataRow(true)]
        public void AuditAndReportServicesResolveWithoutReplacingHostProvidedProtection(bool desktop)
        {
            var services = new ServiceCollection(); var protector = new TestBaselineJournalProtector(); services.AddSingleton<IBaselineJournalProtector>(protector);
            if (desktop) DoorsIntegration.Desktop.ServiceCollectionExtensions.AddDoorsDesktop(services); else DoorsIntegration.ConsoleApp.ServiceCollectionExtensions.AddDoorsIntegration(services);
            using (var provider = services.BuildServiceProvider())
            {
                Assert.AreSame(protector, provider.GetRequiredService<IBaselineJournalProtector>());
                Assert.IsNotNull(provider.GetRequiredService<IBulkBaselineReportService>()); Assert.IsNotNull(provider.GetRequiredService<IJobHistoryService>());
                Assert.AreSame<object>(provider.GetRequiredService<IBulkBaselineJobJournal>(), provider.GetRequiredService<IBaselineJobAuditStore>());
                Assert.IsNotNull(provider.GetRequiredService<IBulkBaselineJobRunner>());
            }
        }
    }
}
