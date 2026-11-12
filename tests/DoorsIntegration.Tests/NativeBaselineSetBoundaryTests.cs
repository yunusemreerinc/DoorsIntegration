using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Export;
using DoorsIntegration.Wpf.Controls;
using DoorsIntegration.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DocumentFormat.OpenXml.Packaging;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("NativeBaselineSetMock")]
    public class NativeBaselineSetBoundaryTests
    {
        [TestMethod]
        public async Task DefaultNativeCompatibilityPerformsNoComOrScriptExecution()
        {
            var executor = new NeverNativeExecutor(); var adapter = new DxlNativeBaselineSetAdapter(executor, new NativeBaselineSetScriptOptions(), new UnvalidatedNativeBaselineSetSafetyProvider(), new UnvalidatedNativeBaselineSetCompatibility());
            var result = await adapter.InspectAsync(new BaselineSetRequest(BaselineSetAction.CreateDefinition, "/Project"), CancellationToken.None);
            Assert.IsFalse(result.Complete); Assert.IsFalse(adapter.WritesValidated); StringAssert.Contains(result.Reason, "NOT_RUN"); Assert.AreEqual(0, executor.Calls);
        }
        [TestMethod]
        public void ProtocolPreservesSeparateSetAndMemberVersionsAndEscapedUnicode()
        {
            Guid id = Guid.NewGuid(); string text = "NATIVE_BASELINE_SET_READ\t1\t" + id.ToString("N") + "\nD\tReview\tİnceleme\\n注釈\nM\tReview\tA\t/P/A\nS\tReview\t2\t0\tR\ttrue\tNote\tuser\t1700000000\nB\tReview\t2\t0\tR\tA\t/P/A\t1.5[R]\nEND\tOK\n";
            var facts = new BaselineSetInspection { Complete = true, FolderIdentity = "folder-id", RepositoryId = "repo" };
            var result = NativeBaselineSetProtocol.Read(text, id, "/P", facts);
            Assert.AreEqual("İnceleme\n注釈", result.Definitions.Single().Description); Assert.AreEqual(new NativeBaselineSetVersion(2, 0, "R"), result.Sets.Single().Version);
            Assert.AreEqual("1.5[R]", result.Sets.Single().Members.Single().ModuleBaselineVersion); StringAssert.StartsWith(result.Definitions.Single().Id, "logical-definition:");
            Assert.ThrowsException<FormatException>(() => NativeBaselineSetProtocol.Read(text, Guid.NewGuid(), "/P", facts));
            Assert.ThrowsException<FormatException>(() => NativeBaselineSetProtocol.Read(text.Replace("END\tOK", "END\tINCOMPLETE"), id, "/P", facts));
            Assert.ThrowsException<FormatException>(() => NativeBaselineSetProtocol.Read(text.Replace("1.5[R]", "bad\\q"), id, "/P", facts));
        }
        [DataTestMethod, DataRow(ExportFormat.Json), DataRow(ExportFormat.Csv), DataRow(ExportFormat.Xlsx)]
        public async Task ReportExportsApprovedIntentActualStateMissingAndVersionSeparation(ExportFormat format)
        {
            using (var f = new SetFixture())
            {
                var d = await f.Definition(); var s = await f.Run(f.Request(BaselineSetAction.CreateMajorSet, d.DefinitionId));
                var populated = await f.Run(f.Request(BaselineSetAction.AddModuleBaselines, d.DefinitionId, s.SetId, f.Modules));
                var exporter = new ExportService(new IExportFormatWriter[] { new JsonExportWriter(), new CsvExportWriter(), new XlsxExportWriter() }, new BaselineTestLog());
                var result = await new NativeBaselineSetReportService(f.Repository, exporter).ExportAsync(populated.JobId, new ExportRequest(Path.Combine(f.Files.Root, "report-" + format), format));
                Assert.AreEqual(ExportStatus.Completed, result.Status);
                if (format == ExportFormat.Json) { string json = File.ReadAllText(Path.Combine(result.DirectoryPath, "data.json")); StringAssert.Contains(json, "NativeBaselineSetMembers"); StringAssert.Contains(json, "1.5[REVIEW]"); StringAssert.Contains(json, "explicitApproval"); }
                if (format == ExportFormat.Csv) StringAssert.Contains(File.ReadAllText(Path.Combine(result.DirectoryPath, "NativeBaselineSetMembers.csv")), "1.5[REVIEW]");
                if (format == ExportFormat.Xlsx) using (var workbook = SpreadsheetDocument.Open(Path.Combine(result.DirectoryPath, "data.xlsx"), false)) { Assert.IsNotNull(workbook.WorkbookPart); Assert.IsTrue(workbook.WorkbookPart.WorksheetParts.Count() >= 5); }
                string artifactRoot = Environment.GetEnvironmentVariable("DOORS_NATIVE_SET_ARTIFACT_ROOT");
                if (!string.IsNullOrWhiteSpace(artifactRoot))
                {
                    string destination = Path.Combine(artifactRoot, "report-" + format + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(destination);
                    foreach (string file in Directory.EnumerateFiles(result.DirectoryPath, "*", SearchOption.AllDirectories))
                    { string relative = file.Substring(result.DirectoryPath.Length).TrimStart(Path.DirectorySeparatorChar); string target = Path.Combine(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, false); }
                }
            }
        }
        [TestMethod]
        public async Task LinkVerificationDetectsDroppedDuplicateAndWrongBaselineBinding()
        {
            using (var f = new SetFixture())
            {
                var d = await f.Definition(); var s = await f.Run(f.Request(BaselineSetAction.CreateMajorSet, d.DefinitionId)); var p = await f.Run(f.Request(BaselineSetAction.AddModuleBaselines, d.DefinitionId, s.SetId, f.Modules));
                var def = p.After.Definitions.Single(); var set = p.After.Sets.Single(); var before = await f.Adapter.CaptureAsync(def, null, CancellationToken.None); var after = await f.Adapter.CaptureAsync(def, set, CancellationToken.None);
                before.Edges.Add(before.Edges[0]); Assert.IsFalse(BaselineSetLinks.Compare(before, after, def, set).Verified);
                before.Edges.RemoveAt(1); after.Edges[0].TargetVersion = "Current"; Assert.IsFalse(BaselineSetLinks.Compare(before, after, def, set).Verified);
                before.Edges[0].TargetModuleId = after.Edges[0].TargetModuleId = "external";
                after.Edges[0].TargetVersion = "9.9"; Assert.IsFalse(BaselineSetLinks.Compare(before, after, def, set).Verified);
            }
        }
        private sealed class NeverNativeExecutor : IDxlExecutor
        {
            internal int Calls; public DxlExecutionOperation ActiveOperation => null; public bool IsBusy => false;
            public DxlExecutionOperation Start(DxlExecutionRequest request) { Calls++; throw new InvalidOperationException("Must not start COM."); }
            public Task<DxlExecutionResult> ExecuteAsync(DxlExecutionRequest request, CancellationToken token) { Calls++; throw new InvalidOperationException("Must not start COM."); }
        }
    }
    [TestClass, DoNotParallelize, TestCategory("NativeBaselineSetMock")]
    public class NativeBaselineSetWpfTests
    {
        public TestContext TestContext { get; set; }
        [TestMethod]
        public Task NativeModeRendersAlongsideSharedVirtualizedSelection() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Ui.Mode = BaselineManagerMode.NativeBaselineSet;
                var view = new BulkBaselineManagerView { DataContext = f.Ui };
                var window = new Window { Title = "Native baseline set — MOCK", Width = 1400, Height = 1000, Content = view, ShowInTaskbar = false };
                try
                {
                    window.Show(); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var native = FindNative(view); Assert.IsNotNull(native); Assert.IsTrue(native.IsVisible, "Native panel visibility must follow the OUTER manager mode.");
                    var grid = (System.Windows.Controls.DataGrid)native.FindName("NativeMemberGrid"); Assert.IsTrue(grid.ActualHeight > 40, "Member list must retain usable height.");
                    Assert.IsTrue(System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing((System.Windows.Controls.DataGrid)view.FindName("ManagerModuleGrid")));
                    string root = Environment.GetEnvironmentVariable("DOORS_NATIVE_SET_ARTIFACT_ROOT") ?? Path.Combine(Path.GetTempPath(), "DoorsIntegrationNativeSetProof"); Directory.CreateDirectory(root);
                    string path = Path.Combine(root, "native-set-mock-" + Guid.NewGuid().ToString("N") + ".png");
                    var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(path)) encoder.Save(output);
                    TestContext.AddResultFile(path);
                }
                finally { window.Close(); }
            }
        });
        private static NativeBaselineSetView FindNative(DependencyObject parent)
        { if (parent is NativeBaselineSetView found) return found; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = FindNative(VisualTreeHelper.GetChild(parent, i)); if (child != null) return child; } return null; }
        [TestMethod]
        public Task MockUiEndToEndAndRestartUsesExistingSelectionAndExplicitReview() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); Assert.AreEqual(BaselineManagerMode.IndependentModuleBaselines, f.Ui.Mode);
                f.Ui.Mode = BaselineManagerMode.NativeBaselineSet; var ui = f.Ui.Native; ui.Folder = "/Bulk/P01";
                await ui.PreflightCommand.ExecuteAsync(); Assert.IsFalse(ui.ReviewCommand.CanExecute(null)); Assert.IsFalse(ui.ExecuteCommand.CanExecute(null));
                f.Model.IsReadOnly = false;
                Func<BaselineSetAction, Task> run = async action => { ui.Action = action; await ui.PreflightCommand.ExecuteAsync(); Assert.IsTrue(ui.ReviewCommand.CanExecute(null), ui.Report + f.Model.ErrorDetails); await ui.ReviewCommand.ExecuteAsync(); Assert.IsTrue(ui.ExecuteCommand.CanExecute(null), ui.Report); await ui.ExecuteCommand.ExecuteAsync(); Assert.AreEqual("", f.Model.ErrorDetails); Assert.AreEqual(BaselineSetJobStatus.Succeeded, ui.CurrentJob.Status, ui.Report); };
                await run(BaselineSetAction.CreateDefinition); Assert.AreEqual(2, ui.DefinitionModules.Count);
                await run(BaselineSetAction.CreateMajorSet); Assert.IsTrue(ui.SelectedSet.IsOpen); Assert.AreEqual(2, ui.Members.Count(m => m.Missing));
                await run(BaselineSetAction.AddModuleBaselines); Assert.IsFalse(ui.Members.Any(m => m.Missing));
                await run(BaselineSetAction.CloseSet); Assert.IsFalse(ui.SelectedSet.IsOpen); Assert.IsTrue(ui.CurrentJob.LinkVerification.Verified);
                Assert.AreEqual(4, f.Review.Reviews.Count); var id = ui.CurrentJob.JobId;
                await ui.HistoryCommand.ExecuteAsync(); Assert.AreEqual(4, ui.History.Count);
                f.Model.Dispose(); f.Model = f.NewModel(); f.Ui.Mode = BaselineManagerMode.NativeBaselineSet; ui = f.Ui.Native;
                await ui.HistoryCommand.ExecuteAsync(); ui.SelectedHistory = ui.History.Single(j => j.JobId == id); await ui.OpenJobCommand.ExecuteAsync();
                Assert.AreEqual("/Bulk/P01", ui.Folder, "Opening a job restores its scoped definition folder.");
                await ui.ReconcileCommand.ExecuteAsync(); Assert.AreEqual(BaselineSetJobStatus.Succeeded, ui.CurrentJob.Status, ui.Report); Assert.IsFalse(ui.ExecuteCommand.CanExecute(null));
            }
        });
        [TestMethod]
        public Task NativeApprovalInvalidatesOnModeReadOnlyAndSelectionChange() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Model.IsReadOnly = false; f.Ui.Mode = BaselineManagerMode.NativeBaselineSet; var ui = f.Ui.Native;
                await ui.PreflightCommand.ExecuteAsync(); await ui.ReviewCommand.ExecuteAsync(); Assert.IsTrue(ui.ExecuteCommand.CanExecute(null));
                f.Ui.Mode = BaselineManagerMode.IndependentModuleBaselines; Assert.IsFalse(ui.ExecuteCommand.CanExecute(null)); f.Ui.Mode = BaselineManagerMode.NativeBaselineSet; Assert.IsFalse(ui.ExecuteCommand.CanExecute(null));
                await ui.PreflightCommand.ExecuteAsync(); await ui.ReviewCommand.ExecuteAsync(); f.Model.IsReadOnly = true; f.Model.IsReadOnly = false; Assert.IsFalse(ui.ExecuteCommand.CanExecute(null));
                await ui.PreflightCommand.ExecuteAsync(); await ui.ReviewCommand.ExecuteAsync(); f.Ui.Rows.First(r => r.IsSelected).IsSelected = false; Assert.IsFalse(ui.ExecuteCommand.CanExecute(null));
                var view = new BulkBaselineManagerView { DataContext = f.Ui }; Assert.IsNotNull(view.FindName("ManagerMode"));
                var native = new NativeBaselineSetView { DataContext = ui }; Assert.IsNotNull(native.FindName("NativeExecute"));
            }
        });
    }
}
