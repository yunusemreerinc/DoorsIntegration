using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using DoorsIntegration.Core;
using DoorsIntegration.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("BulkBaselineAcceptance"), DoNotParallelize]
    public class BulkBaselineAcceptanceTests
    {
        public TestContext TestContext { get; set; }
        private static BulkBaselineReportService Reports(JobTestFixture f) => new BulkBaselineReportService(f.Journal, f.Journal,
            new ExportService(new IExportFormatWriter[] { new JsonExportWriter(), new CsvExportWriter(), new XlsxExportWriter() }, f.Preflight.Log));
        private static void Version(PreflightTestFixture f, int index, int major, int minor, bool none = false)
        {
            var m = f.States[index].Module; var version = new BaselineVersion(major, minor);
            var history = none ? Array.Empty<BaselineHistoryEntry>() : new[] { new BaselineHistoryEntry(new BaselineIdentity(m, version, "mock-history-" + index),
                "Mock fixture", "mock-operator", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "2026-01-01 UTC") };
            f.Metadata.Modules[m.ModuleId] = new MockBaselineModuleState(m, history, none ? null : version,
                new BaselineVersion(none ? 1 : major + 1, 0), new BaselineVersion(none ? 0 : major, none ? 1 : minor + 1));
        }
        private static async Task<ModuleSelectionSession> Selection(params ModuleSelectionFolder[] folders)
        {
            var source = new SelectionTestAdapter(); var session = source.Create(new ModuleSelectionRequest(DoorsIntegration.Wpf.Services.MockDoorsAdapter.BaselineRepositoryId, folders));
            await SelectionTestAdapter.Complete(session); return session;
        }
        [TestMethod]
        public async Task Acceptance_A01_TwentyFormalModulesFromMultipleFolders()
        {
            var session = await Selection(new ModuleSelectionFolder("/Bulk/P01/F01", false), new ModuleSelectionFolder("/Bulk/P02/F01", false));
            foreach (var group in session.Query().Where(c => c.CanSelect).GroupBy(c => c.FullPath.Substring(0, c.FullPath.LastIndexOf('/'))))
                foreach (var module in group.Take(10)) session.SetSelected(module.Module.ModuleId, true);
            Assert.AreEqual(20, session.SelectedCount); Assert.AreEqual(2, session.GetSelectedModules().Select(m => m.Module.CanonicalFullPath.Substring(0, m.Module.CanonicalFullPath.LastIndexOf('/'))).Distinct().Count());
            Assert.IsTrue(session.Query(new ModuleSelectionFilter(selectedOnly: true)).All(c => c.Type == DoorsItemKind.FormalModule));
        }
        [TestMethod]
        public async Task Acceptance_A02_OverlappingFoldersDoNotDuplicateStableIdentities()
        {
            var session = await Selection(new ModuleSelectionFolder("/Bulk/P01"), new ModuleSelectionFolder("/Bulk/P01/F01", false), new ModuleSelectionFolder("/Bulk/P02/F01", false));
            session.SelectFiltered(true); var modules = session.GetSelectedModules();
            Assert.IsTrue(modules.Count > 20); Assert.AreEqual(modules.Count, modules.Select(m => m.Module.ModuleId).Distinct().Count());
            Assert.AreEqual(32, session.Query().Count(c => c.FullPath.StartsWith("/Bulk/P01/F01/", StringComparison.Ordinal)));
        }
        [DataTestMethod, DataRow(BaselineType.Major, 2, 0), DataRow(BaselineType.Minor, 1, 5)]
        public async Task Acceptance_A03_OnePointFourAdvancesToExpectedMockVersion(BaselineType type, int major, int minor)
        {
            var f = new PreflightTestFixture(); Version(f, 0, 1, 4);
            var plan = await f.Service.CreatePlanAsync(f.Request(type: type, suffix: ""));
            Assert.AreEqual(new BaselineVersion(1, 4), plan.Items[0].CurrentVersion); Assert.AreEqual(new BaselineVersion(major, minor), plan.Items[0].ExpectedVersion);
            Assert.AreEqual(BulkBaselineItemStatus.Ready, plan.Items[0].Status);
        }
        [DataTestMethod, DataRow(BaselineType.Major), DataRow(BaselineType.Minor)]
        public async Task Acceptance_A04_MixedLastVersionsRemainModuleSpecific(BaselineType type)
        {
            var f = new PreflightTestFixture(4); var versions = new[] { new BaselineVersion(0, 1), new BaselineVersion(1, 4), new BaselineVersion(2, 9), new BaselineVersion(7, 99) };
            for (int i = 0; i < versions.Length; i++) Version(f, i, versions[i].Major, versions[i].Minor);
            var plan = await f.Service.CreatePlanAsync(f.Request(type: type, suffix: ""));
            for (int i = 0; i < versions.Length; i++)
            { var row = plan.Items.Single(p => p.ModuleId == f.States[i].Module.ModuleId); Assert.AreEqual(versions[i], row.CurrentVersion); Assert.AreEqual(type == BaselineType.Major ? new BaselineVersion(versions[i].Major + 1, 0) : new BaselineVersion(versions[i].Major, versions[i].Minor + 1), row.ExpectedVersion); }
        }
        [DataTestMethod, DataRow(BaselineType.Major, 1, 0), DataRow(BaselineType.Minor, 0, 1)]
        public async Task Acceptance_A05_FirstBaselineUsesExplicitMockProposal(BaselineType type, int major, int minor)
        {
            var f = new PreflightTestFixture(); Version(f, 0, 0, 0, true);
            var plan = await f.Service.CreatePlanAsync(f.Request(type: type, suffix: ""));
            Assert.IsNull(plan.Items[0].CurrentVersion); Assert.AreEqual(new BaselineVersion(major, minor), plan.Items[0].ExpectedVersion); Assert.AreEqual(BulkBaselineItemStatus.Ready, plan.Items[0].Status);
        }
        [TestMethod]
        public async Task Acceptance_A06_UnicodeSuffixAnnotationAndOverridesSurviveExecutionAndAudit()
        {
            using (var f = new JobTestFixture(2))
            {
                var selection = f.Preflight.States.Select((s, i) => new BaselineModuleSelection(s.Module, i == 0 ? "ÖZEL" : null, i == 0 ? "Override 注釈" : null));
                var plan = await f.Preflight.Service.CreatePlanAsync(new BulkBaselinePreflightRequest(new BulkBaselineRequest(selection, suffix: "RÉV-1", annotation: "İnceleme\n注釈")));
                var job = await f.Runner().CreateAsync(plan); await f.Runner().RunAsync(job.JobId); var report = await Reports(f).ReadAsync(job.JobId);
                Assert.AreEqual("RÉV-1", report.Suffix); Assert.AreEqual("İnceleme\n注釈", report.Annotation); Assert.AreEqual("ÖZEL", report.Modules[0].Suffix); Assert.AreEqual("Override 注釈", report.Modules[0].Annotation);
                Assert.IsTrue(report.Modules.All(m => m.ActualBaseline == m.ExpectedBaseline)); Assert.AreEqual(2, report.ReadAudit().Count(e => e.ApprovedPlan != null));
            }
        }
        [TestMethod]
        public async Task Acceptance_A07_TargetCollisionBlocksDispatch()
        {
            using (var f = new JobTestFixture(1))
            {
                f.Preflight.Reader.OnRead = (probe, read) => { f.Preflight.States[0].TargetAvailability = probe.ExpectedVersion == null ? BaselineTargetAvailability.Free : BaselineTargetAvailability.Exists; return f.Preflight.States[0].ToFacts(); };
                var job = await f.Create(); var result = await f.Runner().RunAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.Skipped, result.Modules[0].Status); Assert.AreEqual(BaselinePreflightReason.CollisionExists, result.Modules[0].PreflightReason); Assert.AreEqual(0, f.Adapter.Calls);
            }
        }
        [TestMethod]
        public async Task Acceptance_A08_DeniedAndOtherSessionLockAreSkippedWithoutUnlocking()
        {
            using (var f = new JobTestFixture(3))
            {
                f.Preflight.States[0].CreateAccess = BaselineCheckState.Failed; PreflightTestFixture.LockOther(f.Preflight.States[1]);
                var job = await f.Create(); var result = await f.Runner().RunAsync(job.JobId); var report = await Reports(f).ReadAsync(job.JobId);
                Assert.AreEqual(BaselinePreflightReason.CreateDenied, result.Modules[0].PreflightReason); Assert.AreEqual(BaselinePreflightReason.OtherSessionLock, result.Modules[1].PreflightReason);
                Assert.AreEqual(2, report.SkippedCount); Assert.AreEqual(1, report.LockedCount); Assert.AreEqual(1, f.Adapter.Calls); Assert.AreEqual(BaselineLockState.OwnedByOtherSession, f.Preflight.States[1].LockState);
            }
        }
        [DataTestMethod, DataRow(ExecutionPolicy.ContinueOnError, 3), DataRow(ExecutionPolicy.StopOnFirstError, 1)]
        public async Task Acceptance_A09_PartialFailurePreservesSuccessAndPolicy(ExecutionPolicy policy, int calls)
        {
            using (var f = new JobTestFixture(3))
            {
                var job = await f.Create(policy); f.Adapter.Outcome = a => a.Item.ModuleId == job.Modules[0].ModuleId ? BaselineWriteOutcome.NotRun : BaselineWriteOutcome.Created;
                await f.Runner().RunAsync(job.JobId); var report = await Reports(f).ReadAsync(job.JobId);
                Assert.AreEqual(calls, f.Adapter.Calls); Assert.AreEqual(1, report.FailedCount); Assert.IsFalse(report.IsFullSuccess);
                Assert.AreEqual(policy == ExecutionPolicy.ContinueOnError ? BaselineBatchOutcome.PartialSuccess : BaselineBatchOutcome.Failed, report.Outcome);
                Assert.AreEqual(policy == ExecutionPolicy.ContinueOnError ? 2 : 0, report.SuccessCount);
            }
        }
        [TestMethod]
        public async Task Acceptance_A10_CancelStopsOnlyRemainingAndObservesActiveOperation()
        {
            using (var f = new JobTestFixture(3)) using (var cancel = new CancellationTokenSource())
            {
                var job = await f.Create(); var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var progress = new JobProgressObserver();
                f.Adapter.BeforeExecute = async _ => { entered.TrySetResult(true); await release.Task; };
                var running = f.Runner().RunAsync(job.JobId, progress, cancel.Token);
                try
                {
                    Assert.AreEqual(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10000))); cancel.Cancel();
                    Assert.AreEqual(progress.CancelSeen.Task, await Task.WhenAny(progress.CancelSeen.Task, Task.Delay(10000)));
                    Assert.IsFalse(running.IsCompleted); Assert.IsFalse(f.Adapter.ReceivedCancelableToken);
                }
                finally { release.TrySetResult(true); }
                var stopped = await running; Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, stopped.Modules[0].Status);
                Assert.IsTrue(stopped.Modules.Skip(1).All(m => m.Status == BulkBaselineModuleStatus.CancelRequested && m.Attempts == 0)); Assert.AreEqual(1, f.Adapter.Calls);
                f.Adapter.BeforeExecute = null; var resumed = await f.Runner().RunAsync(job.JobId); Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, resumed.Status); Assert.IsTrue(f.Server.Creates.Values.All(c => c == 1));
            }
        }
        [TestMethod]
        public async Task Acceptance_A11_SimulatedComTimeoutIsUnknownUntilReadOnlyReconciliation()
        {
            using (var f = new JobTestFixture(2))
            {
                var job = await f.Create(); f.Adapter.Outcome = _ => BaselineWriteOutcome.NeedsReconciliation; var progress = new JobProgressObserver();
                var uncertain = await f.Runner().RunAsync(job.JobId, progress);
                Assert.IsTrue(progress.Events.Any(p => p.CurrentModule?.Status == BulkBaselineModuleStatus.Unknown)); Assert.AreEqual(BulkBaselineModuleStatus.NeedsReconciliation, uncertain.Status);
                Assert.AreEqual(2, (await Reports(f).ReadAsync(job.JobId)).UnknownCount); await f.Runner().RunAsync(job.JobId); Assert.AreEqual(1, f.Adapter.Calls);
                // Simulate a late server commit; this is not an actual COM call or timeout validation.
                f.Server.Targets[job.Modules[0].ModuleId] = BaselineJobVerificationStatus.ExistsMatching; f.Server.Creates[job.Modules[0].ModuleId] = 1;
                var reconciled = await f.Runner().OpenAsync(job.JobId);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, reconciled.Modules[0].Status); Assert.AreEqual(BulkBaselineModuleStatus.Pending, reconciled.Modules[1].Status); Assert.AreEqual(1, f.Adapter.Calls);
            }
        }
        [DataTestMethod, DataRow("before-checkpoint"), DataRow("after-checkpoint")]
        public async Task Acceptance_A12_AbruptChildProcessCrashRecoversJournalInANewProcess(string phase)
        {
            using (var f = new JobTestFixture(1))
            {
                Assert.AreNotEqual(0, await Worker("crash", f.Root, phase)); Assert.IsTrue(File.Exists(Path.Combine(f.Root, "crash-proof.txt")));
                Assert.AreEqual(0, await Worker("recover", f.Root, phase)); var result = ExportTestContext.Json<WorkerReceipt>(Path.Combine(f.Root, "recovered.json"));
                Assert.AreEqual(ExportTestContext.Json<WorkerReceipt>(Path.Combine(f.Root, "job.json")).JobId, result.JobId);
                Assert.AreEqual("Mock", result.Source); Assert.AreEqual("Succeeded", result.Status); Assert.AreEqual("Succeeded", result.FirstStatusAfterOpen);
                Assert.AreEqual(phase == "before-checkpoint" ? "Running" : "Succeeded", result.StatusBeforeRecovery);
                Assert.AreEqual(0, result.WritesDuringReconciliation); Assert.AreEqual(1, result.WritesAfterResume); Assert.AreEqual(1, result.WritesAfterRepeat);
                Assert.AreEqual(2, result.BaselineCount); Assert.AreEqual(2, result.ModuleCount); CollectionAssert.AreEqual(new[] { 1, 1 }, result.Attempts);
            }
        }
        private static async Task<int> Worker(string mode, string root, string phase)
        {
            string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DoorsIntegration.AcceptanceHost.exe"); Assert.IsTrue(File.Exists(executable), executable);
            using (var child = new Process { StartInfo = new ProcessStartInfo(executable, mode + " \"" + root + "\" " + phase) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true } })
            {
                child.Start(); var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(60000)) { child.Kill(); child.WaitForExit(5000); Assert.Fail("Owned Mock crash worker exceeded 60 seconds."); }
                string stderr = await error; await output; if (mode == "recover") Assert.AreEqual(0, child.ExitCode, stderr); return child.ExitCode;
            }
        }
        [DataContract] public sealed class WorkerReceipt
        {
            [DataMember] public Guid JobId { get; set; }
            [DataMember] public string Source { get; set; }
            [DataMember] public string Status { get; set; }
            [DataMember] public string StatusBeforeRecovery { get; set; }
            [DataMember] public string FirstStatusAfterOpen { get; set; }
            [DataMember] public int WritesDuringReconciliation { get; set; }
            [DataMember] public int WritesAfterResume { get; set; }
            [DataMember] public int WritesAfterRepeat { get; set; }
            [DataMember] public int BaselineCount { get; set; }
            [DataMember] public int ModuleCount { get; set; }
            [DataMember] public int[] Attempts { get; set; }
        }
        [TestMethod]
        public async Task Acceptance_A13_ReopeningAndRepeatingTheSameJobNeverCreatesDuplicates()
        {
            using (var f = new JobTestFixture(5))
            {
                var job = await f.Create(); await f.Runner().RunAsync(job.JobId);
                var restarted = f.Runner(new FileBulkBaselineJobJournal(new BulkBaselineJournalOptions(f.Root), new TestBaselineJournalProtector()));
                await restarted.OpenAsync(job.JobId); await restarted.RunAsync(job.JobId); await restarted.RunAsync(job.JobId);
                Assert.AreEqual(5, f.Adapter.Calls); Assert.AreEqual(5, f.Server.Creates.Count); Assert.IsTrue(f.Server.Creates.Values.All(c => c == 1));
            }
        }
        [DataTestMethod, DataRow(1), DataRow(2), DataRow(3)]
        public async Task Acceptance_A14_ThousandModuleSelectionAndDryRunPerformance(int repetition)
        {
            var watch = Stopwatch.StartNew(); var session = await Selection(new ModuleSelectionFolder("/Bulk"), new ModuleSelectionFolder("/Bulk/P01/F01"));
            foreach (var c in session.Query().Where(c => c.CanSelect).Take(1000)) session.SetSelected(c.Module.ModuleId, true);
            var modules = session.GetSelectedModules(); long selectionMs = watch.ElapsedMilliseconds;
            Assert.AreEqual(1000, modules.Count); Assert.AreEqual(1000, modules.Select(m => m.Module.ModuleId).Distinct().Count());
            var clock = new BaselinePreflightClock(); var logger = new BaselineTestLog();
            var states = modules.Select(m => new MockBaselinePreflightState(m.Module)).ToArray();
            var history = new MockBaselineHistoryAdapter(modules.Select(m => new MockBaselineModuleState(m.Module, Array.Empty<BaselineHistoryEntry>(), null, new BaselineVersion(1, 0), new BaselineVersion(0, 1))));
            var service = new BulkBaselinePreflightService(new MockBaselinePreflightStateReader(states, clock), new BaselineMetadataService(history, history, logger), clock, logger);
            watch.Restart(); var dryRun = await service.DryRunAsync(new BulkBaselinePreflightRequest(new BulkBaselineRequest(modules), maxPlanAge: TimeSpan.FromMinutes(30), maxObservationAge: TimeSpan.FromMinutes(10)));
            long dryRunMs = watch.ElapsedMilliseconds;
            Assert.AreEqual(1000, dryRun.Plan.Items.Count); Assert.AreEqual(1000, dryRun.EligibleCount); Assert.IsTrue(states.All(s => s.LockState == BaselineLockState.Unlocked && s.UnsavedChanges == BaselineUnsavedState.None));
            int selectionBudget = Budget("DOORS_ACCEPTANCE_SELECTION_BUDGET_MS", 30000), dryRunBudget = Budget("DOORS_ACCEPTANCE_DRYRUN_BUDGET_MS", 120000);
            TestContext.WriteLine("ACCEPTANCE_METRIC repetition={0}; selectionMs={1}; dryRunMs={2}; selectionBudgetMs={3}; dryRunBudgetMs={4}; processBits={5}; peakWorkingSetBytes={6}; source=Mock; nativeLatencyValidated=false", repetition, selectionMs, dryRunMs, selectionBudget, dryRunBudget, Environment.Is64BitProcess ? 64 : 32, Process.GetCurrentProcess().PeakWorkingSet64);
            Assert.IsTrue(selectionMs <= selectionBudget, "Mock selection exceeded its configured regression budget."); Assert.IsTrue(dryRunMs <= dryRunBudget, "Mock dry-run exceeded its configured regression budget.");
        }
        private static int Budget(string name, int fallback)
        { string raw = Environment.GetEnvironmentVariable(name); if (string.IsNullOrEmpty(raw)) return fallback; Assert.IsTrue(int.TryParse(raw, out var value) && value >= 1 && value <= 600000, "Invalid acceptance budget " + name); return value; }
        [DataTestMethod, DataRow(ExportFormat.Json), DataRow(ExportFormat.Csv), DataRow(ExportFormat.Xlsx)]
        public async Task Acceptance_A15_ReportCountsIdentitiesAndOutcomesAreAccurate(ExportFormat format)
        {
            using (var f = new JobTestFixture(5))
            {
                f.Preflight.States[1].CreateAccess = BaselineCheckState.Failed; PreflightTestFixture.LockOther(f.Preflight.States[2]);
                var job = await f.Runner().CreateAsync(await f.Preflight.Service.CreatePlanAsync(f.Preflight.Request(annotation: "=SUM(1,2)\nTürkçe 注釈")));
                f.Adapter.Outcome = a => a.Item.ModuleId == job.Modules[3].ModuleId ? BaselineWriteOutcome.NotRun : a.Item.ModuleId == job.Modules[4].ModuleId ? BaselineWriteOutcome.NeedsReconciliation : BaselineWriteOutcome.Created;
                await f.Runner().RunAsync(job.JobId); var service = Reports(f); var report = await service.ReadAsync(job.JobId);
                Assert.AreEqual(1, report.SuccessCount); Assert.AreEqual(1, report.FailedCount); Assert.AreEqual(2, report.SkippedCount); Assert.AreEqual(1, report.LockedCount); Assert.AreEqual(1, report.UnknownCount); Assert.IsFalse(report.IsFullSuccess);
                var exported = await service.ExportAsync(job.JobId, new ExportRequest(Path.Combine(f.Root, "report"), format, ExportErrorPolicy.StopOnInvalidRow));
                Assert.AreEqual(ExportStatus.CompletedWithIssues, exported.Status); Assert.AreEqual(5L, exported.RowCounts[ExportTableKind.BulkBaselineModules]); Assert.AreEqual(3L, exported.RowCounts[ExportTableKind.BulkBaselineApprovedPlans]);
                var rows = ReadRows(exported.DirectoryPath, format);
                Assert.AreEqual(5, rows.Count); Assert.AreEqual(5, rows.Select(r => r["stableIdentity"]).Distinct().Count());
                foreach (var expected in report.Modules)
                {
                    var row = rows.Single(r => r["stableIdentity"] == expected.StableIdentity);
                    Assert.AreEqual(expected.FullPath, row["fullPath"]); Assert.AreEqual(expected.Group, row["group"]); Assert.AreEqual(expected.ResultStatus, row["resultStatus"]);
                    Assert.AreEqual(expected.ExpectedBaseline ?? "", row["expectedBaseline"] ?? ""); Assert.AreEqual(expected.ActualBaseline ?? "", row["actualBaseline"] ?? "");
                    Assert.AreEqual(expected.ErrorMessage ?? "", row["errorMessage"] ?? "");
                    Assert.AreEqual((format == ExportFormat.Csv ? "\t" : "") + expected.Annotation, row["annotation"]);
                }
                if (format == ExportFormat.Csv) Assert.IsTrue(exported.CsvProtectedCellCount > 0);
                TestContext.WriteLine("MOCK report outcome=Indeterminate; counts=Success:1,Failed:1,Skipped:2,Locked:1,Unknown:1; format=" + format);
                string evidenceRoot = Environment.GetEnvironmentVariable("DOORS_ACCEPTANCE_ARTIFACT_ROOT") ?? TestContext.TestResultsDirectory;
                string evidence = Path.Combine(evidenceRoot, "mock-baseline-report-" + format + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(evidence);
                foreach (string file in Directory.GetFiles(exported.DirectoryPath))
                { string copy = Path.Combine(evidence, Path.GetFileName(file)); File.Copy(file, copy, false); TestContext.AddResultFile(copy); }
            }
        }
        private static List<Dictionary<string, string>> ReadRows(string root, ExportFormat format)
        {
            if (format == ExportFormat.Json)
            {
                var document = ExportTestContext.Json<ReportDocument>(Path.Combine(root, "data.json"));
                Assert.AreEqual(1, document.Tables.Jobs.Single().Success); Assert.AreEqual(1, document.Tables.Jobs[0].Failed); Assert.AreEqual(2, document.Tables.Jobs[0].Skipped); Assert.AreEqual(1, document.Tables.Jobs[0].Unknown); Assert.AreEqual(1, document.Tables.Jobs[0].Locked); Assert.IsFalse(document.Tables.Jobs[0].FullSuccess); Assert.AreEqual("Indeterminate", document.Tables.Jobs[0].Outcome);
                Assert.IsTrue(document.Tables.Jobs[0].Started.EndsWith("Z", StringComparison.Ordinal)); Assert.IsTrue(document.Tables.Jobs[0].Ended.EndsWith("Z", StringComparison.Ordinal));
                return document.Tables.Modules.Select(m => new Dictionary<string, string> { ["stableIdentity"] = m.Identity, ["fullPath"] = m.Path, ["group"] = m.Group, ["resultStatus"] = m.Status, ["expectedBaseline"] = m.Expected, ["actualBaseline"] = m.Actual, ["annotation"] = m.Annotation, ["errorMessage"] = m.Error }).ToList();
            }
            var headerRows = FlatRows(root, format, "BulkBaselineJobs"); var header = headerRows[0]; var values = headerRows[1];
            Assert.AreEqual("1", values[Array.IndexOf(header, "successCount")]); Assert.AreEqual("1", values[Array.IndexOf(header, "failedCount")]); Assert.AreEqual("2", values[Array.IndexOf(header, "skippedCount")]); Assert.AreEqual("1", values[Array.IndexOf(header, "lockedCount")]); Assert.AreEqual("1", values[Array.IndexOf(header, "unknownCount")]); Assert.AreEqual("Indeterminate", values[Array.IndexOf(header, "outcome")]);
            Assert.AreEqual(format == ExportFormat.Csv ? "false" : "0", values[Array.IndexOf(header, "isFullSuccess")]);
            Assert.IsTrue(values[Array.IndexOf(header, "startedAtUtc")].EndsWith("Z", StringComparison.Ordinal)); Assert.IsTrue(values[Array.IndexOf(header, "endedAtUtc")].EndsWith("Z", StringComparison.Ordinal));
            var modules = FlatRows(root, format, "BulkBaselineModules"); string[] columns = modules[0]; return modules.Skip(1).Select(r => columns.Select((name, index) => new { name, value = r[index] }).ToDictionary(p => p.name, p => p.value)).ToList();
        }
        private static List<string[]> FlatRows(string root, ExportFormat format, string table)
        {
            List<string[]> rows;
            if (format == ExportFormat.Csv) rows = ExportTestCsv.Read(Path.Combine(root, table + ".csv"));
            else using (var file = SpreadsheetDocument.Open(Path.Combine(root, "data.xlsx"), false))
            {
                Assert.AreEqual(0, new OpenXmlValidator().Validate(file).Count()); Assert.IsFalse(file.WorkbookPart.WorksheetParts.Any(p => p.Worksheet.Descendants<CellFormula>().Any()));
                var sheet = file.WorkbookPart.Workbook.Sheets.Elements<Sheet>().Single(s => s.Name.Value == table + "_1");
                rows = ((WorksheetPart)file.WorkbookPart.GetPartById(sheet.Id.Value)).Worksheet.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c =>
                    Regex.Replace(c.InnerText, "_[xX]([0-9A-Fa-f]{4})_", m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())).ToArray()).ToList();
            }
            return rows;
        }
        [DataContract] public sealed class ReportDocument { [DataMember(Name = "tables")] public ReportTables Tables { get; set; } }
        [DataContract] public sealed class ReportTables
        { [DataMember(Name = "BulkBaselineJobs")] public ReportJob[] Jobs { get; set; } [DataMember(Name = "BulkBaselineModules")] public ReportModule[] Modules { get; set; } }
        [DataContract] public sealed class ReportJob
        {
            [DataMember(Name = "successCount")] public int Success { get; set; }
            [DataMember(Name = "failedCount")] public int Failed { get; set; }
            [DataMember(Name = "skippedCount")] public int Skipped { get; set; }
            [DataMember(Name = "unknownCount")] public int Unknown { get; set; }
            [DataMember(Name = "isFullSuccess")] public bool FullSuccess { get; set; }
            [DataMember(Name = "lockedCount")] public int Locked { get; set; }
            [DataMember(Name = "outcome")] public string Outcome { get; set; }
            [DataMember(Name = "startedAtUtc")] public string Started { get; set; }
            [DataMember(Name = "endedAtUtc")] public string Ended { get; set; }
        }
        [DataContract] public sealed class ReportModule
        {
            [DataMember(Name = "stableIdentity")] public string Identity { get; set; }
            [DataMember(Name = "fullPath")] public string Path { get; set; }
            [DataMember(Name = "group")] public string Group { get; set; }
            [DataMember(Name = "resultStatus")] public string Status { get; set; }
            [DataMember(Name = "expectedBaseline")] public string Expected { get; set; }
            [DataMember(Name = "actualBaseline")] public string Actual { get; set; }
            [DataMember(Name = "annotation")] public string Annotation { get; set; }
            [DataMember(Name = "errorMessage")] public string Error { get; set; }
        }
    }
}
