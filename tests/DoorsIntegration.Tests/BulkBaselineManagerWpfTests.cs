using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf;
using DoorsIntegration.Wpf.Controls;
using DoorsIntegration.Wpf.Services;
using DoorsIntegration.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class ManagerReviewRecorder : IClientReview
    {
        internal readonly List<WriteReview> Reviews = new List<WriteReview>();
        internal bool Approve = true;
        internal Action<WriteReview> OnReview { get; set; }
        public Task<bool> ReviewAsync(WriteReview review, CancellationToken token)
        { token.ThrowIfCancellationRequested(); lock (Reviews) Reviews.Add(review); OnReview?.Invoke(review); return Task.FromResult(Approve); }
    }
    internal sealed class ManagerWpfFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "DoorsIntegrationManagerTests", Guid.NewGuid().ToString("N"));
        internal readonly ManagerReviewRecorder Review = new ManagerReviewRecorder();
        internal WorkspaceViewModel Model;
        internal BulkBaselineManagerViewModel Ui => Model.BaselineManager;
        internal ManagerWpfFixture() { Model = NewModel(); }
        internal WorkspaceViewModel NewModel() => new WorkspaceViewModel(new ClientSessionFactory(null, null, Review, Root, new TestBaselineJournalProtector()), new ClientLog());
        internal async Task Prepare(int selected = 2)
        {
            Ui.RootPath = "/Bulk/P01/F01"; await Ui.LoadTreeCommand.ExecuteAsync(); Ui.Tree.Single().IsSelected = true;
            await Ui.DiscoverCommand.ExecuteAsync();
            foreach (var row in Ui.Rows.Where(r => r.ModuleName.EndsWith("001", StringComparison.Ordinal) || selected > 1 && r.ModuleName.EndsWith("002", StringComparison.Ordinal)).ToArray()) row.IsSelected = true;
            Ui.Annotation = "UI review\nİnceleme 注釈";
        }
        public void Dispose()
        {
            Model.Dispose(); string resolved = Path.GetFullPath(Root); string container = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DoorsIntegrationManagerTests"));
            if (!string.Equals(Path.GetDirectoryName(resolved), container, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test cleanup root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    [TestClass, DoNotParallelize]
    public class BulkBaselineManagerWpfTests
    {
        [TestMethod]
        public Task ReadOnlyAndUnreviewedPlansNeverEnableExecute() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null)); Assert.IsTrue(f.Model.IsReadOnly);
                await f.Prepare(); await f.Ui.PreflightCommand.ExecuteAsync(); Assert.IsTrue(f.Ui.AllEligible);
                Assert.IsFalse(f.Ui.ReviewCommand.CanExecute(null)); Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null));
                f.Model.IsReadOnly = false; Assert.IsTrue(f.Ui.ReviewCommand.CanExecute(null)); Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null));
                f.Review.Approve = false; await f.Ui.ReviewCommand.ExecuteAsync(); Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null)); Assert.IsNull(f.Ui.CurrentJob);
                Assert.AreEqual("", f.Model.ErrorDetails);
            }
        });
        [TestMethod]
        public Task MockBatchRunsThroughDurableRunnerAndExportsJsonCsv() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Model.IsReadOnly = false; await f.Ui.PreflightCommand.ExecuteAsync();
                await f.Ui.ReviewCommand.ExecuteAsync(); Assert.IsTrue(f.Ui.ExecuteCommand.CanExecute(null), f.Model.ErrorDetails + f.Ui.Report);
                await f.Ui.ExecuteCommand.ExecuteAsync();
                Assert.AreEqual("", f.Model.ErrorDetails); Assert.IsNotNull(f.Ui.CurrentJob, f.Ui.Report);
                Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, f.Ui.CurrentJob.Status, string.Join("\n", f.Ui.ProgressRows.Select(r => r.Error)));
                Assert.AreEqual(2, f.Ui.ProgressRows.Count); Assert.AreEqual(100.0, f.Ui.OverallProgress); Assert.AreEqual(1, f.Review.Reviews.Count, "The one exact batch approval covers unchanged module scopes.");
                StringAssert.Contains(f.Review.Reviews[0].Body, "Modül sayısı: 2"); StringAssert.Contains(f.Review.Reviews[0].Body, "İnceleme");
                Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null));
                f.Ui.ExportRoot = Path.Combine(f.Root, "exports"); await f.Ui.ExportResultCommand.ExecuteAsync();
                Assert.AreEqual(1, Directory.GetFiles(f.Ui.ExportRoot, "data.json", SearchOption.AllDirectories).Length, f.Ui.Report);
                f.Ui.ExportFormat = ExportFormat.Csv; await f.Ui.ExportResultCommand.ExecuteAsync();
                var csv = File.ReadAllText(Directory.GetFiles(f.Ui.ExportRoot, "BulkBaselineModules.csv", SearchOption.AllDirectories).Single()); StringAssert.Contains(csv, "Succeeded");
                f.Ui.ExportFormat = ExportFormat.Xlsx; await f.Ui.ExportResultCommand.ExecuteAsync();
                Assert.AreEqual(1, Directory.GetFiles(f.Ui.ExportRoot, "data.xlsx", SearchOption.AllDirectories).Length, f.Ui.Report);
            }
        });
        [TestMethod]
        public Task SearchAndSelectedOnlyPreserveSelectionAndApproval() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Model.IsReadOnly = false; await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.ReviewCommand.ExecuteAsync();
                f.Ui.Search = "module 001"; Assert.AreEqual(1, f.Ui.Rows.Count); f.Ui.SelectedOnly = true;
                Assert.IsTrue(f.Ui.ExecuteCommand.CanExecute(null)); f.Ui.Search = "no match"; Assert.AreEqual(0, f.Ui.Rows.Count);
                f.Ui.Search = ""; Assert.AreEqual(2, f.Ui.Rows.Count); Assert.IsTrue(f.Ui.Rows.All(r => r.IsSelected));
                Assert.IsTrue(f.Ui.ExecuteCommand.CanExecute(null)); f.Ui.SelectedOnly = false; Assert.AreEqual(32, f.Ui.Rows.Count);
                await f.Ui.ExecuteCommand.ExecuteAsync(); Assert.AreEqual(2, f.Ui.CurrentJob.Modules.Count); Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, f.Ui.CurrentJob.Status);
            }
        });
        [DataTestMethod, DataRow("suffix"), DataRow("annotation"), DataRow("type"), DataRow("policy"), DataRow("selection"), DataRow("readonly"), DataRow("refresh")]
        public Task ContextChangesInvalidateApproval(string change) => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Model.IsReadOnly = false; await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.ReviewCommand.ExecuteAsync(); Assert.IsTrue(f.Ui.ExecuteCommand.CanExecute(null));
                switch (change)
                {
                    case "suffix": f.Ui.Suffix = "changed"; break;
                    case "annotation": f.Ui.Annotation = "changed"; break;
                    case "type": f.Ui.BaselineType = BaselineType.Major; break;
                    case "policy": f.Ui.Policy = ExecutionPolicy.StopOnFirstError; break;
                    case "selection": f.Ui.Rows.First(r => r.IsSelected).IsSelected = false; break;
                    case "readonly": f.Model.IsReadOnly = true; f.Model.IsReadOnly = false; break;
                    default: await f.Ui.RefreshCommand.ExecuteAsync(); break;
                }
                Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null)); Assert.IsNull(f.Ui.CurrentJob);
                if (change == "refresh") Assert.AreEqual(2, f.Ui.Rows.Count(r => r.IsSelected));
            }
        });
        [TestMethod]
        public Task BlockedRowsShowLockReasonsAndRequireExplicitEligibleSelection() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); await f.Ui.SelectAllCommand.ExecuteAsync(); f.Model.IsReadOnly = false;
                await f.Ui.PreflightCommand.ExecuteAsync(); Assert.IsFalse(f.Ui.AllEligible); Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null));
                var locked = f.Ui.Rows.Single(r => r.ModuleName.EndsWith("003", StringComparison.Ordinal)); StringAssert.Contains(locked.LockStatus, "OtherSession"); StringAssert.Contains(locked.Eligibility, "Blocked");
                await f.Ui.SelectEligibleCommand.ExecuteAsync(); Assert.AreEqual(28, f.Ui.Rows.Count(r => r.IsSelected));
                await f.Ui.PreflightCommand.ExecuteAsync(); Assert.IsTrue(f.Ui.AllEligible); Assert.IsTrue(f.Ui.ReviewCommand.CanExecute(null));
            }
        });
        [TestMethod]
        public Task InvalidAnnotationDisablesPreflightAndClearsOldApproval() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Model.IsReadOnly = false; await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.ReviewCommand.ExecuteAsync();
                f.Ui.Annotation = "bad\0"; Assert.IsFalse(f.Ui.PreflightCommand.CanExecute(null)); Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null)); Assert.IsTrue(f.Ui.Validation.Length > 0);
            }
        });
        [TestMethod]
        public Task PersistentMockHistoryReopensAndReconcilesWithoutDuplicateBaselines() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Model.IsReadOnly = false; await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.ReviewCommand.ExecuteAsync(); await f.Ui.ExecuteCommand.ExecuteAsync();
                var id = f.Ui.CurrentJob.JobId; int count = Directory.GetFiles(Path.Combine(f.Root, "Mock", "9.7", "mock-baselines"), "*.json").Length;
                f.Model.Dispose(); f.Model = f.NewModel(); await f.Ui.OpenHistoryCommand.ExecuteAsync();
                Assert.AreEqual(1, f.Ui.History.Count); Assert.AreEqual(1, f.Ui.SelectedPane); await f.Ui.LoadJobCommand.ExecuteAsync();
                Assert.AreEqual(id, f.Ui.CurrentJob.JobId); Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, f.Ui.CurrentJob.Status, f.Model.ErrorDetails);
                Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null)); await f.Ui.ReconcileCommand.ExecuteAsync();
                Assert.AreEqual(count, Directory.GetFiles(Path.Combine(f.Root, "Mock", "9.7", "mock-baselines"), "*.json").Length); Assert.AreEqual("", f.Model.ErrorDetails);
            }
        });
        [TestMethod]
        public Task RealModeIsDistinctAndDefaultNativeGatesKeepExecuteDisabled() => WpfTestHost.Run(() =>
        {
            using (var f = new ManagerWpfFixture())
            {
                f.Model.Mode = ClientMode.RealDoors; f.Model.IsReadOnly = false;
                Assert.IsTrue(f.Ui.IsReal); StringAssert.Contains(f.Ui.ModeBanner, "REAL DOORS"); StringAssert.Contains(f.Ui.Limitation, "Yazma kapalı");
                Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null)); Assert.IsFalse(f.Ui.ReviewCommand.CanExecute(null)); Assert.AreEqual(0, f.Review.Reviews.Count);
            }
            return Task.CompletedTask;
        });
        [TestMethod]
        public Task LargeModuleGridRecyclesContainersAndFiltersWithoutLosingChecks() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                f.Ui.RootPath = "/Bulk"; await f.Ui.LoadTreeCommand.ExecuteAsync(); f.Ui.Tree.Single().IsSelected = true;
                await f.Ui.DiscoverCommand.ExecuteAsync(); while (f.Ui.LoadMoreCommand.CanExecute(null)) await f.Ui.LoadMoreCommand.ExecuteAsync();
                Assert.IsTrue(f.Ui.Rows.Count > 1000, f.Ui.Summary); f.Ui.Rows.First(r => r.CanSelect).IsSelected = true;
                var view = new BulkBaselineManagerView { DataContext = f.Ui, Width = 1390, Height = 780 };
                var window = new Window { Content = view, Width = 1420, Height = 840 }; window.Show(); await Task.Delay(50); window.UpdateLayout();
                var grid = (DataGrid)view.FindName("ManagerModuleGrid");
                Assert.IsTrue(grid.EnableRowVirtualization); Assert.IsTrue(grid.EnableColumnVirtualization);
                int realized = Visual<DataGridRow>(grid).Count(); Assert.IsTrue(realized > 0 && realized < 100, "Realized=" + realized);
                f.Ui.Search = "no match"; f.Ui.Search = ""; Assert.AreEqual(1, f.Ui.Rows.Count(r => r.IsSelected)); window.Close();
            }
        });
        [TestMethod]
        public Task ManagerViewRendersApprovedAndCompletedStatesWithoutBindingErrors() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Model.IsReadOnly = false; await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.ReviewCommand.ExecuteAsync();
                var listener = new ManagerBindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                var view = new BulkBaselineManagerView { DataContext = f.Ui }; var window = new Window { Content = view, Width = 1460, Height = 950, Title = "Bulk Baseline Manager · MOCK" };
                try
                {
                    window.Show(); await Task.Delay(40); window.UpdateLayout();
                    Assert.IsTrue(((Button)view.FindName("ManagerExecute")).IsEnabled);
                    Save(window, "approved"); await f.Ui.ExecuteCommand.ExecuteAsync(); await Task.Delay(30); window.UpdateLayout(); Save(window, "completed");
                    Assert.IsFalse(((Button)view.FindName("ManagerExecute")).IsEnabled); Assert.AreEqual(2, ((DataGrid)view.FindName("ManagerProgressGrid")).Items.Count);
                    Assert.AreEqual(0, listener.Errors.Count, string.Join("\n", listener.Errors));
                }
                finally { window.Close(); PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
            }
        });
        [TestMethod]
        public Task CancelRemainingKeepsCompletedModulesAndResumesTheSameJob() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); await f.Ui.SelectAllCommand.ExecuteAsync(); f.Model.IsReadOnly = false;
                await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.SelectEligibleCommand.ExecuteAsync(); await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.ReviewCommand.ExecuteAsync();
                var active = f.Ui.ExecuteCommand.ExecuteAsync(); await WpfTestHost.Until(() => f.Ui.ProgressRows.Any(r => r.Status == BulkBaselineModuleStatus.Running));
                Assert.IsTrue(f.Ui.CancelRemainingCommand.CanExecute(null)); await f.Ui.CancelRemainingCommand.ExecuteAsync(); await active;
                Assert.AreEqual("", f.Model.ErrorDetails); var id = f.Ui.CurrentJob.JobId;
                Assert.AreEqual(BulkBaselineModuleStatus.CancelRequested, f.Ui.CurrentJob.Status);
                Assert.IsTrue(f.Ui.ProgressRows.Any(r => r.Status == BulkBaselineModuleStatus.CancelRequested));
                int completed = f.Ui.ProgressRows.Count(r => r.Status == BulkBaselineModuleStatus.Succeeded); Assert.IsTrue(completed > 0 && completed < 28);
                await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.ReviewCommand.ExecuteAsync(); Assert.IsTrue(f.Ui.ExecuteCommand.CanExecute(null), f.Model.ErrorDetails + f.Ui.Report);
                await f.Ui.ExecuteCommand.ExecuteAsync(); Assert.AreEqual("", f.Model.ErrorDetails);
                Assert.AreEqual(id, f.Ui.CurrentJob.JobId); Assert.AreEqual(BulkBaselineModuleStatus.Succeeded, f.Ui.CurrentJob.Status);
                Assert.AreEqual(28, f.Ui.ProgressRows.Count); Assert.IsTrue(f.Ui.CurrentJob.Modules.All(m => m.Attempts == 1));
                Assert.AreEqual(28, Directory.GetFiles(Path.Combine(f.Root, "Mock", "9.7", "mock-baselines"), "*.json").Length);
            }
        });
        [TestMethod]
        public Task MultiFolderTreeScopesDeduplicateAndIncludeSubfoldersIsPerFolder() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                f.Ui.RootPath = "/Bulk/P01"; await f.Ui.LoadTreeCommand.ExecuteAsync(); var root = f.Ui.Tree.Single();
                root.IsSelected = true; root.IncludeSubfolders = false; root.Children.First().IsSelected = true;
                await f.Ui.DiscoverCommand.ExecuteAsync(); Assert.AreEqual(32, f.Ui.Rows.Count);
                root.IncludeSubfolders = true; await f.Ui.DiscoverCommand.ExecuteAsync(); while (f.Ui.LoadMoreCommand.CanExecute(null)) await f.Ui.LoadMoreCommand.ExecuteAsync();
                Assert.AreEqual(256, f.Ui.Rows.Count); Assert.AreEqual(256, f.Ui.Rows.Select(r => r.FullPath).Distinct().Count());
                Assert.IsTrue(f.Ui.Rows.All(r => r.Candidate.Type == DoorsItemKind.FormalModule));
            }
        });
        [TestMethod]
        public Task RealReviewRequiresTwoSeparatePositiveAnswers() => WpfTestHost.Run(async () =>
        {
            using (var fixture = new BaselineExecutionTransportFixture())
            {
                await fixture.Prepare(); var old = fixture.Authorization.Review.Plan;
                // Fake NativeDxl facts are only UI/protocol fixtures; no COM invocation occurs.
                var plan = BaselineExecutionTransportFixture.Internal<BulkBaselineDryRunPlan>(Guid.NewGuid(), old.Request, new[] { fixture.Authorization.Item }, DateTime.UtcNow, old.RequestFingerprint);
                var no = new ManagerReviewRecorder(); no.OnReview = _ => { if (no.Reviews.Count == 2) no.Approve = false; };
                var rejected = new BaselineManagerApproval(no, ClientMode.RealDoors, () => true);
                Assert.IsFalse(await rejected.ReviewAsync(plan, CancellationToken.None)); Assert.IsFalse(rejected.IsApproved); Assert.AreEqual(2, no.Reviews.Count);
                var yes = new ManagerReviewRecorder(); var approved = new BaselineManagerApproval(yes, ClientMode.RealDoors, () => true);
                Assert.IsTrue(await approved.ReviewAsync(plan, CancellationToken.None)); Assert.AreEqual(2, yes.Reviews.Count);
                StringAssert.Contains(yes.Reviews[1].Title, "ikinci"); StringAssert.Contains(yes.Reviews[1].Body, "geri alınamayan");
                Assert.AreEqual(0, fixture.Files.Sessions.OpenCalls);
            }
        });
        [TestMethod]
        public Task RealReviewCannotAcceptMockFactsAsNativeEvidence() => WpfTestHost.Run(async () =>
        {
            var f = new PreflightTestFixture(); var plan = await f.Service.CreatePlanAsync(f.Request()); var recorder = new ManagerReviewRecorder();
            var gate = new BaselineManagerApproval(recorder, ClientMode.RealDoors, () => true);
            Assert.IsFalse(await gate.ReviewAsync(plan, CancellationToken.None)); Assert.AreEqual(0, recorder.Reviews.Count);
        });
        [TestMethod]
        public Task ActualButtonsAndTypedRiskDialogCompleteMockUiScenario() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                f.Model.Dispose(); Window window = null; WorkspaceViewModel model = null;
                var dialog = new WpfReviewDialog(() => window, () => model != null && !model.IsReadOnly && window?.IsVisible == true);
                model = new WorkspaceViewModel(new ClientSessionFactory(null, null, dialog, f.Root, new TestBaselineJournalProtector()), new ClientLog()); f.Model = model;
                var main = new MainWindow(model); window = main; main.Show();
                await WpfTestHost.Until(() => model.IsIdle && model.Requirements.Count > 0);
                ((TabControl)main.FindName("WorkspaceTabs")).SelectedIndex = 8; main.UpdateLayout();
                await f.Prepare(); model.IsReadOnly = false; await f.Ui.PreflightCommand.ExecuteAsync(); main.UpdateLayout();
                var view = Visual<BulkBaselineManagerView>(main).Single(); var execute = (Button)view.FindName("ManagerExecute"); Assert.IsFalse(execute.IsEnabled);
                bool dialogSeen = false; Exception modalFailure = null;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                timer.Tick += (s, e) =>
                {
                    var modal = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w != main && w.Title.Contains("kesin plan"));
                    if (modal == null) return;
                    timer.Stop(); dialogSeen = true;
                    try
                    {
                        var apply = Visual<Button>(modal).Single(b => Equals(b.Content, "Onayla ve uygula")); Assert.IsFalse(apply.IsEnabled);
                        Visual<TextBox>(modal).Single(t => !t.IsReadOnly).Text = "MOCK BASELINE 2";
                        Visual<CheckBox>(modal).Single().IsChecked = true; Assert.IsTrue(apply.IsEnabled);
                        ((IInvokeProvider)new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke)).Invoke();
                    }
                    catch (Exception ex) { modalFailure = ex; modal.Close(); }
                };
                try
                {
                    timer.Start(); ((IInvokeProvider)new ButtonAutomationPeer((Button)view.FindName("ManagerReview")).GetPattern(PatternInterface.Invoke)).Invoke();
                    await WpfTestHost.Until(() => dialogSeen && model.IsIdle);
                    Assert.IsNull(modalFailure, modalFailure?.ToString()); Assert.IsTrue(execute.IsEnabled, model.ErrorDetails);
                    ((IInvokeProvider)new ButtonAutomationPeer(execute).GetPattern(PatternInterface.Invoke)).Invoke();
                    await WpfTestHost.Until(() => model.IsIdle && f.Ui.CurrentJob?.Status == BulkBaselineModuleStatus.Succeeded);
                    Assert.AreEqual(2, ((DataGrid)view.FindName("ManagerProgressGrid")).Items.Count); Assert.IsFalse(execute.IsEnabled);
                    await Task.Delay(40); main.UpdateLayout();
                    Assert.IsTrue(Visual<DataGridRow>((DataGrid)view.FindName("ManagerProgressGrid")).Any());
                    StringAssert.Contains(model.Status, "Succeeded");
                    Save(main, "main-window");
                }
                finally { timer.Stop(); main.Close(); await WpfTestHost.Until(() => !main.IsVisible); }
            }
        });
        [TestMethod]
        public Task BatchTicketRejectsChangedMetadataAndCanOnlyApproveEachModuleOnce() => WpfTestHost.Run(async () =>
        {
            var f = new PreflightTestFixture(2); var plan = await f.Service.CreatePlanAsync(f.Request()); var recorder = new ManagerReviewRecorder();
            var gate = new BaselineManagerApproval(recorder, ClientMode.Mock, () => true); Assert.IsTrue(await gate.ReviewAsync(plan, CancellationToken.None));
            var id = Guid.NewGuid(); gate.BindJob(id);
            var scoped = new BulkBaselinePreflightRequest(new BulkBaselineRequest(new[] { f.Request().Baseline.Modules[0] }, suffix: "SRR", annotation: f.Request().Baseline.Annotation));
            var fresh = await f.Service.CreatePlanAsync(scoped);
            var review = BaselineExecutionTransportFixture.Internal<BaselineExecutionReview>(fresh, id);
            Assert.IsTrue(await gate.ConfirmAsync(review, CancellationToken.None)); Assert.IsFalse(await gate.ConfirmAsync(review, CancellationToken.None));
            gate.Clear(); await gate.ReviewAsync(plan, CancellationToken.None); gate.BindJob(id);
            f.States[0].ContentRevision = "new-content"; fresh = await f.Service.CreatePlanAsync(scoped);
            Assert.IsFalse(await gate.ConfirmAsync(BaselineExecutionTransportFixture.Internal<BaselineExecutionReview>(fresh, id), CancellationToken.None));
        });
        [TestMethod]
        public Task RetryDialogIncludesPriorFailureAndFreshExistenceEvidence() => WpfTestHost.Run(async () =>
        {
            using (var f = new JobTestFixture(1))
            {
                var job = await f.Create(); f.Adapter.Outcome = _ => BaselineWriteOutcome.NotRun; job = await f.Runner().RunAsync(job.JobId);
                var fresh = await f.Preflight.Service.CreatePlanAsync(f.Preflight.Request()); var proof = await f.Server.VerifyAsync(job, job.Modules[0], CancellationToken.None);
                var review = BaselineExecutionTransportFixture.Internal<BaselineJobRetryReview>(job, job.Modules[0], proof, fresh);
                var recorder = new ManagerReviewRecorder(); var gate = new BaselineManagerApproval(recorder, ClientMode.Mock, () => true);
                Assert.IsTrue(await gate.ConfirmAsync(review, CancellationToken.None)); StringAssert.Contains(recorder.Reviews.Single().Body, "EXPLICIT RETRY");
                StringAssert.Contains(recorder.Reviews.Single().Body, "Previous status=Failed"); StringAssert.Contains(recorder.Reviews.Single().Body, "AbsentAuthoritatively");
            }
        });
        [TestMethod]
        public Task AuditStoresTheWholeReviewedBatchOnceAndHistoryRereportDoesNotMutateJournal() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Model.IsReadOnly = false; await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.ReviewCommand.ExecuteAsync(); await f.Ui.ExecuteCommand.ExecuteAsync();
                var runtime = ((ClientSession)typeof(WorkspaceViewModel).GetProperty("Session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(f.Model)).BaselineManager; var job = f.Ui.CurrentJob;
                var report = await runtime.Reports.ReadAsync(job.JobId);
                var plans = report.ReadAudit().Where(e => e.ApprovedPlan != null).Select(e => e.ApprovedPlan).ToArray();
                Assert.AreEqual(1, plans.Length); Assert.AreEqual(2, plans[0].Modules.Count); Assert.AreEqual("WPF explicit batch review", plans[0].Scope);
                Assert.IsFalse(plans[0].SecondRealConfirmation); Assert.IsTrue(report.Modules.All(m => m.ApprovalId == plans[0].ApprovalId));
                f.Model.IsReadOnly = true; await f.Ui.OpenHistoryCommand.ExecuteAsync(); Assert.AreEqual(1, f.Ui.HistoryItems.Count);
                await f.Ui.ReportHistoryCommand.ExecuteAsync(); Assert.AreEqual(2, f.Ui.SelectedPane); Assert.AreEqual(2, f.Ui.AuditModules.Count);
                StringAssert.Contains(f.Ui.AuditSummary, "Success=2"); StringAssert.Contains(f.Ui.AuditContextSummary, "Operator=");
                Assert.AreEqual(job.Revision, (await runtime.JobHistory.OpenAsync(job.JobId)).Revision); Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null));
                f.Ui.HistoryOperator = "does-not-exist"; await f.Ui.OpenHistoryCommand.ExecuteAsync(); Assert.AreEqual(0, f.Ui.History.Count);
                f.Ui.HistoryOperator = ""; f.Ui.HistorySearch = job.JobId.ToString(); f.Ui.HistoryOutcome = "Success"; await f.Ui.OpenHistoryCommand.ExecuteAsync(); Assert.AreEqual(1, f.Ui.HistoryItems.Count);
                f.Ui.HistoryFrom = DateTime.Today.AddDays(1); await f.Ui.OpenHistoryCommand.ExecuteAsync(); Assert.AreEqual(0, f.Ui.HistoryItems.Count);
            }
        });
        [TestMethod]
        public Task AuditAndHistoryViewsRenderWithoutBindingErrorsAndLinkToReconciliation() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(); f.Model.IsReadOnly = false; await f.Ui.PreflightCommand.ExecuteAsync(); await f.Ui.ReviewCommand.ExecuteAsync(); await f.Ui.ExecuteCommand.ExecuteAsync();
                await f.Ui.OpenHistoryCommand.ExecuteAsync(); await f.Ui.ReportHistoryCommand.ExecuteAsync();
                var listener = new ManagerBindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                var view = new BulkBaselineManagerView { DataContext = f.Ui }; var window = new Window { Content = view, Width = 1460, Height = 1000, Title = "Bulk Baseline Reporting · MOCK" };
                try
                {
                    window.Show(); await Task.Delay(50); window.UpdateLayout(); Save(window, "audit-report");
                    f.Ui.SelectedPane = 1; await Task.Delay(50); window.UpdateLayout(); Save(window, "job-history");
                    var previousEnd = f.Ui.AuditModules[0].EndedAtUtc;
                    Assert.IsTrue(f.Ui.ReconcileCommand.CanExecute(null)); await f.Ui.ReconcileCommand.ExecuteAsync(); Assert.AreEqual(2, f.Ui.AuditModules.Count);
                    Assert.AreEqual(previousEnd, f.Ui.AuditModules[0].EndedAtUtc); StringAssert.Contains(f.Ui.AuditSummary, "Success=2");
                    Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null)); Assert.AreEqual(0, listener.Errors.Count, string.Join("\n", listener.Errors));
                }
                finally { window.Close(); PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
            }
        });
        [TestMethod]
        public Task HistoryPagesExposeOlderJobsAndResetWhenFiltersChange() => WpfTestHost.Run(async () =>
        {
            using (var f = new ManagerWpfFixture())
            {
                await f.Prepare(1);
                var runtime = ((ClientSession)typeof(WorkspaceViewModel).GetProperty("Session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(f.Model)).BaselineManager;
                var module = f.Ui.Rows.Single(r => r.IsSelected).Candidate.Module;
                var plan = await runtime.Preflight.CreatePlanAsync(new BulkBaselinePreflightRequest(new BulkBaselineRequest(new[] { new BaselineModuleSelection(module) }, BaselineType.Major, suffix: "REVIEW", annotation: "History inputs")));
                for (int i = 0; i < 101; i++) await runtime.Runner.CreateAsync(plan);
                await f.Ui.OpenHistoryCommand.ExecuteAsync(); Assert.AreEqual("", f.Model.ErrorDetails);
                Assert.AreEqual(100, f.Ui.History.Count); Assert.IsFalse(f.Ui.PreviousHistoryPageCommand.CanExecute(null)); Assert.IsTrue(f.Ui.NextHistoryPageCommand.CanExecute(null));
                var firstPage = new HashSet<Guid>(f.Ui.History.Select(j => j.JobId));
                await f.Ui.NextHistoryPageCommand.ExecuteAsync(); Assert.AreEqual(1, f.Ui.History.Count);
                Assert.IsFalse(firstPage.Contains(f.Ui.SelectedHistory.JobId)); Assert.IsFalse(f.Ui.NextHistoryPageCommand.CanExecute(null)); Assert.IsTrue(f.Ui.PreviousHistoryPageCommand.CanExecute(null));
                await f.Ui.ReportHistoryCommand.ExecuteAsync(); Assert.AreEqual(BaselineType.Major, f.Ui.BaselineType); Assert.AreEqual("REVIEW", f.Ui.Suffix); Assert.AreEqual("History inputs", f.Ui.Annotation);
                Assert.IsTrue(f.Model.IsReadOnly); Assert.IsFalse(f.Ui.ExecuteCommand.CanExecute(null));
                await f.Ui.PreviousHistoryPageCommand.ExecuteAsync(); Assert.AreEqual(100, f.Ui.History.Count);
                f.Ui.HistorySearch = f.Ui.SelectedHistory.JobId.ToString(); Assert.IsFalse(f.Ui.NextHistoryPageCommand.CanExecute(null));
                await f.Ui.OpenHistoryCommand.ExecuteAsync(); Assert.AreEqual(1, f.Ui.History.Count); StringAssert.Contains(f.Ui.HistoryPageLabel, "1–1 / 1");
                Assert.AreEqual(1, f.Ui.Rows.Count(r => r.IsSelected), "History filters preserve module selection.");
            }
        });
        private sealed class ManagerBindingErrors : TraceListener
        { internal readonly List<string> Errors = new List<string>(); public override void Write(string message) { } public override void WriteLine(string message) { if (message.Contains("Error")) Errors.Add(message); } }
        private static IEnumerable<T> Visual<T>(DependencyObject node) where T : DependencyObject
        { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var child = VisualTreeHelper.GetChild(node, i); if (child is T value) yield return value; foreach (var nested in Visual<T>(child)) yield return nested; } }
        private static void Save(Window window, string name)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "..", "artifacts", "BulkBaselineReporting", Environment.Is64BitProcess ? "x64" : "x86"); Directory.CreateDirectory(root);
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var output = File.Create(Path.Combine(root, name + ".png"))) encoder.Save(output);
        }
    }
}
