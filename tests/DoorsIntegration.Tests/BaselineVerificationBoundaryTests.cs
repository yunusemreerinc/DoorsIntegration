using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Export;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("BaselineVerificationMock")]
    public class BaselineVerificationBoundaryTests
    {
        public TestContext TestContext { get; set; }
        [DataTestMethod, DataRow(ExportFormat.Json), DataRow(ExportFormat.Csv), DataRow(ExportFormat.Xlsx), DataRow(ExportFormat.Html)]
        public async Task AllReportsPreserveVersionIdentityStatusCoverageRichTextAndSafeText(ExportFormat format)
        {
            using (var files = new JobTestFixture(1))
            {
                Directory.CreateDirectory(files.Root);
                var adapter = new MockBaselineSetVerificationAdapter(); adapter.Fixture.Requirements[0].Text = "=HYPERLINK(\"unsafe\") <script>alert(1)</script> 注釈 password=secret-test";
                var result = await BaselineVerificationTests.Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest());
                var export = new ExportService(new IExportFormatWriter[] { new JsonExportWriter(), new CsvExportWriter(), new XlsxExportWriter(), new HtmlExportWriter() }, new BaselineTestLog());
                var report = await new BaselineVerificationReportService(export).ExportAsync(result, new ExportRequest(Path.Combine(files.Root, "verification-" + format), format));
                Assert.AreEqual(ExportStatus.Completed, report.Status); Assert.IsTrue(report.RowCounts.ContainsKey(ExportTableKind.BaselineTraceabilityCoverage));
                if (format == ExportFormat.Json) { string content = File.ReadAllText(Path.Combine(report.DirectoryPath, "data.json")); StringAssert.Contains(content, "Passed"); StringAssert.Contains(content, "moduleBaselineIdentity"); StringAssert.Contains(content, "richTextWithOle"); StringAssert.Contains(content, "注釈"); Assert.IsFalse(content.Contains("secret-test")); }
                if (format == ExportFormat.Csv) { string content = File.ReadAllText(Path.Combine(report.DirectoryPath, "BaselineVerificationRequirements.csv")); StringAssert.Contains(content, "\t=HYPERLINK"); Assert.IsTrue(report.CsvProtectedCellCount > 0); Assert.IsFalse(content.Contains("secret-test")); }
                if (format == ExportFormat.Xlsx) using (var workbook = SpreadsheetDocument.Open(Path.Combine(report.DirectoryPath, "data.xlsx"), false)) { Assert.IsNotNull(workbook.WorkbookPart); Assert.IsTrue(workbook.WorkbookPart.WorksheetParts.Count() > 5); }
                if (format == ExportFormat.Html) { string content = File.ReadAllText(Path.Combine(report.DirectoryPath, "data.html")); Assert.IsFalse(content.Contains("<script>alert")); StringAssert.Contains(content, "&lt;script&gt;"); StringAssert.Contains(content, "Content-Security-Policy"); StringAssert.Contains(content, "TraceabilityCoverage"); }
                string root = Environment.GetEnvironmentVariable("DOORS_BASELINE_VERIFICATION_ARTIFACT_ROOT");
                if (!string.IsNullOrWhiteSpace(root))
                { string destination = Path.Combine(root, "report-" + format + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(destination); foreach (string path in Directory.EnumerateFiles(report.DirectoryPath, "*", SearchOption.AllDirectories)) { string target = Path.Combine(destination, path.Substring(report.DirectoryPath.Length).TrimStart(Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(path, target, false); } }
            }
        }
        [TestMethod]
        public void NativeVersionProtocolUsesTypedActualVersionsAndEchoWithoutCurrentFallback()
        {
            Guid id = Guid.NewGuid(); string text = "BASELINE_LINK_VERSIONS\t1\t" + id.ToString("N") + "\nL\tOutgoing\ttrue\t/P/Links\tLINK-ID\t/P/S\tS-ID\tBaseline\t2\t0\tS\t2.0(S)\t42\tVerified\t/P/T\tT-ID\tBaseline\t1\t7\tT\t1.7(T)\t71\tVerified\nEND\ttrue\t1\n";
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            { var result = BaselineNativeLinkEvidenceProtocol.Read(stream, id, "repo", 10, "/P/Links", "LINK-ID"); Assert.IsTrue(result.Complete); Assert.IsTrue(result.Links.Single().IsEchoed); Assert.AreEqual("2.0[S]", result.Links[0].SourceVersion.Version); Assert.AreEqual("1.7[T]", result.Links[0].TargetVersion.Version); Assert.AreEqual(42, result.Links[0].SourceObject); }
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.Replace("END\ttrue\t1\n", "")))) Assert.ThrowsException<FormatException>(() => BaselineNativeLinkEvidenceProtocol.Read(stream, id, "repo", 10, "/P/Links", "LINK-ID"));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text))) Assert.ThrowsException<FormatException>(() => BaselineNativeLinkEvidenceProtocol.Read(stream, Guid.NewGuid(), "repo", 10, "/P/Links", "LINK-ID"));
        }
        [TestMethod]
        public async Task UnvalidatedNativeVersionGateNeverStartsCom()
        {
            var executor = new NeverExecutor(); var reader = new DxlBaselineNativeLinkEvidenceReader(executor, new BaselineLinkEvidenceScriptOptions(), new UnvalidatedBaselineLinkEvidenceCompatibility()); var mock = new MockBaselineSetVerificationAdapter();
            var result = await reader.ReadAsync(MockBaselineSetVerificationAdapter.DemoRequest(), mock.Fixture.Context, mock.Fixture.Context.Modules[0], MockBaselineSetVerificationAdapter.DemoLinkPath, CancellationToken.None);
            Assert.IsFalse(result.Complete); StringAssert.Contains(result.Reason, "NOT_RUN"); Assert.AreEqual(0, executor.Calls);
        }
        [TestMethod]
        public async Task ProtectedAuditIsAppendOnlyAndAuditFailureCannotBePassed()
        {
            using (var files = new JobTestFixture(1))
            {
                var mock = new MockBaselineSetVerificationAdapter(); var audit = new FileBaselineVerificationAuditSink(new BulkBaselineJournalOptions(Path.Combine(files.Root, "verification-audit")), new TestBaselineJournalProtector());
                var service = new BaselineSetVerificationService(new BaselineLinkSnapshotService(mock, mock), mock, new BaselineTraceabilityAnalyzer(), new BaselineTraceabilityComparisonService(), audit, new BaselineTestLog());
                var result = await service.VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest()); var entries = audit.Read(result.VerificationJobId).ToArray(); Assert.AreEqual(2, entries.Length); Assert.AreEqual("Started", entries[0].Phase); Assert.AreEqual("Passed", entries[1].Status);
                byte[] first = File.ReadAllBytes(Directory.GetFiles(Path.Combine(files.Root, "verification-audit"), "00000001.json", SearchOption.AllDirectories).Single());
                audit.Record(result.VerificationJobId, "Exported", BaselineMetadataSource.Mock, "mock", "set", "ReadOnly", "fp"); CollectionAssert.AreEqual(first, File.ReadAllBytes(Directory.GetFiles(Path.Combine(files.Root, "verification-audit"), "00000001.json", SearchOption.AllDirectories).Single()));
                service = new BaselineSetVerificationService(new BaselineLinkSnapshotService(mock, mock), mock, new BaselineTraceabilityAnalyzer(), new BaselineTraceabilityComparisonService(), new FailingAudit(), new BaselineTestLog());
                result = await service.VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest()); Assert.AreEqual(BaselineVerificationStatus.Incomplete, result.Status); Assert.IsTrue(result.Findings.Any(f => f.Code == "AuditUnavailable"));
            }
        }
        [TestMethod]
        public async Task IncomingPolicyAndProjectJsonUseExistingRuleTypes()
        {
            var profile = BaselineVerificationPolicyProfile.ReadJson(BaselineVerificationPolicyProfile.MockExampleJson); Assert.AreEqual(3, profile.ToPolicy().LinkRules.Count);
            var defaults = BaselineVerificationPolicyProfile.ReadJson(BaselineVerificationPolicyProfile.MockExampleJson.Replace(",\"minimumLinks\":1", "")); Assert.IsTrue(defaults.ToPolicy().LinkRules.All(r => r.MinimumLinks == 1), "Omitting minimumLinks must not silently widen the policy to zero.");
            var adapter = new MockBaselineSetVerificationAdapter();
            var policy = new TraceabilityPolicy("incoming", new[] { new TraceabilityLinkRule("HLR-from-SYS", "HLR", new[] { "SYS" }, MockBaselineSetVerificationAdapter.DemoLinkPath, TraceabilityDirection.Incoming) }, orphanRoles: Array.Empty<string>());
            var result = await BaselineVerificationTests.Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest(policy: policy)); Assert.AreEqual(BaselineVerificationStatus.Passed, result.Status); Assert.AreEqual(1, result.Coverage.RequirementsRequiringLinks); Assert.AreEqual(100.0, result.Coverage.TraceabilityCoveragePercentage);
        }
        [TestMethod]
        public async Task MissingVersionFactAndRequirementDenialRemainUnknownNotMissing()
        {
            var mock = new MockBaselineSetVerificationAdapter(); mock.Fixture.Context.Modules[1].Version = null;
            var result = await BaselineVerificationTests.Service(mock, mock).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest());
            Assert.AreEqual(BaselineVerificationReadState.Unknown, result.Snapshot.Context.Modules.Single(m => m.ModuleIdentity == "HLR").State); Assert.AreEqual(BaselineVerificationStatus.Incomplete, result.Status);
            Assert.IsFalse(result.Snapshot.Context.Modules.Any(m => m.State == BaselineVerificationReadState.Missing));
            mock = new MockBaselineSetVerificationAdapter(); mock.Fixture.Requirements.Single(n => n.RequirementType == "HLR").Access = BaselineVerificationReadState.AccessDenied;
            result = await BaselineVerificationTests.Service(mock, mock).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest()); Assert.AreEqual(1, result.Coverage.AccessDeniedRequirements); Assert.IsTrue(result.Coverage.UnresolvedRequirements > 0); Assert.AreEqual(BaselineVerificationStatus.Incomplete, result.Status);
        }
        [TestMethod]
        public async Task AdapterMetadataAndLegacyReferenceImportNeverDispatchNativeMutations()
        {
            var mock = new MockBaselineSetVerificationAdapter(); var spy = new ReadOnlyAdapterSpy(mock); var source = new NativeBaselineSetVerificationSource(spy, new FixtureMetadata(mock));
            var result = await BaselineVerificationTests.Service(source, mock).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest()); Assert.AreEqual(BaselineVerificationStatus.Passed, result.Status); Assert.AreEqual(2, spy.Reads); Assert.AreEqual(0, spy.Writes);
            using (var f = new SetFixture())
            {
                var definition = await f.Definition(); var created = await f.Run(f.Request(BaselineSetAction.CreateMajorSet, definition.DefinitionId)); int writes = f.Adapter.Writes;
                var imported = BaselineLinkSnapshotService.ImportNativeReference(created);
                Assert.AreEqual(writes, f.Adapter.Writes); Assert.IsTrue(imported.Links.All(l => l.SourceVersion.IsCurrent && l.TargetVersion.IsCurrent)); Assert.IsTrue(imported.Context.Modules.All(m => !m.InventoryComplete)); Assert.IsTrue(imported.Limitations.Any());
                var changes = new BaselineTraceabilityComparisonService().Compare(imported, mock.Fixture); Assert.IsFalse(changes.Any(c => c.Kind == BaselineLinkChangeKind.RemovedLink && c.Confirmed));
            }
        }
        [TestMethod]
        public async Task IncompleteEnumerationCannotPassEvenWhenObservedMinimumsAreSatisfied()
        {
            var mock = new MockBaselineSetVerificationAdapter(); foreach (var m in mock.Fixture.Context.Modules) m.LinksComplete = false;
            var result = await BaselineVerificationTests.Service(mock, mock).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest());
            Assert.AreEqual(BaselineVerificationStatus.Incomplete, result.Status); Assert.IsTrue(result.Findings.Any(f => f.Code == "ModuleScopeIncomplete")); Assert.IsFalse(result.Graph.IsCompleteWithinRequestedScope);
        }
        private sealed class ReadOnlyAdapterSpy : IBaselineSetAdapter
        {
            private readonly MockBaselineSetVerificationAdapter mock; internal int Reads, Writes;
            internal ReadOnlyAdapterSpy(MockBaselineSetVerificationAdapter mock) { this.mock = mock; }
            public BaselineMetadataSource Source => BaselineMetadataSource.Mock; public bool WritesValidated => false;
            public Task<BaselineSetInspection> InspectAsync(BaselineSetRequest r, CancellationToken t) { Reads++; return Task.FromResult(new BaselineSetInspection { Source = Source, Definitions = new System.Collections.Generic.List<BaselineSetDefinitionDetails> { mock.Fixture.Context.Definition }, Sets = new System.Collections.Generic.List<NativeBaselineSetDetails> { mock.Fixture.Context.Set } }); }
            public Task<BaselineSetAdapterResult> ExecuteAsync(BaselineSetAuthorization a, CancellationToken t) { Writes++; throw new InvalidOperationException("Verification may not dispatch mutations."); }
        }
        private sealed class FixtureMetadata : IBaselineSetVerificationMetadataProvider
        {
            private readonly MockBaselineSetVerificationAdapter mock; internal FixtureMetadata(MockBaselineSetVerificationAdapter mock) { this.mock = mock; }
            public Task<BaselineVerificationContext> BindAsync(BaselineSetVerificationRequest r, BaselineSetInspection i, CancellationToken t) => mock.ReadContextAsync(r, t);
        }
        private sealed class FailingAudit : IBaselineVerificationAuditSink
        { public void Record(Guid id, string phase, BaselineMetadataSource source, string environment, string set, string status, string fingerprint) => throw new IOException("Audit unavailable"); }
        private sealed class NeverExecutor : IDxlExecutor
        { internal int Calls; public DxlExecutionOperation ActiveOperation => null; public bool IsBusy => false; public DxlExecutionOperation Start(DxlExecutionRequest request) { Calls++; throw new InvalidOperationException(); } public Task<DxlExecutionResult> ExecuteAsync(DxlExecutionRequest request, CancellationToken token) { Calls++; throw new InvalidOperationException(); } }
    }
}
