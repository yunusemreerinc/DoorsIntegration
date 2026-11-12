using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;
using DoorsIntegration.Wpf;
using DoorsIntegration.Wpf.Services;
using DoorsIntegration.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class UnifiedReview : IClientReview
    {
        internal readonly List<WriteReview> Reviews = new List<WriteReview>();
        internal readonly Queue<bool> Answers = new Queue<bool>();
        internal TaskCompletionSource<bool> Pending;
        public async Task<bool> ReviewAsync(WriteReview review, CancellationToken token)
        {
            Reviews.Add(review);
            if (Pending == null) return Answers.Count > 0 && Answers.Dequeue();
            using (token.Register(() => Pending.TrySetResult(false))) return await Pending.Task;
        }
    }
    internal sealed class UnifiedFixture : IClientSessionFactory, IDisposable
    {
        internal readonly ExecutorTestContext Transport = new ExecutorTestContext();
        internal readonly WpfWorkspaceTestData Data = new WpfWorkspaceTestData();
        internal readonly UnifiedReview Review = new UnifiedReview();
        internal readonly ClientConfirmation Confirmation;
        internal readonly MockDoorsAdapter Source = new MockDoorsAdapter();
        internal readonly LinkWriteTestContext Link = new LinkWriteTestContext();
        internal readonly DxlWatchdogSettings Watchdog = new DxlWatchdogSettings();
        internal readonly DxlRunLimitAutomationSettings NativeLimits = new DxlRunLimitAutomationSettings();
        internal readonly UnifiedProcessLocator Processes = new UnifiedProcessLocator();
        internal readonly ClientSettingsStore Settings;
        internal UnifiedFixture()
        { Confirmation = new ClientConfirmation(Review); Settings = new ClientSettingsStore(Path.Combine(Transport.Root, "settings.xml"), Transport.Log); }
        public ClientSession Create(ClientProfile profile, IIntegrationLogger logger)
        {
            var requirements = new RequirementService(Data, Data, Confirmation, new TestRequirementWriteAudit());
            var links = new LinkService(Data, Link.Gateway, Confirmation, Link.Audit);
            var tools = new ClientTools(new ModuleService(Source, Data, Confirmation), Transport.Executor, Confirmation, new ProcessingStorageOptions(Transport.Root), null, NativeLimits, Processes);
            return new ClientSession(new ProjectService(Source), requirements, links, new BaselineComparisonService(Source, logger),
                MockDoorsAdapter.CreateSession(logger).Export, Data, async () => {
                    var active = Transport.Executor.ActiveOperation; if (active != null) try { await active.Completion; } catch { }
                }, tools);
        }
        internal WorkspaceViewModel Model()
        { var model = new WorkspaceViewModel(this, Transport.Log, Settings, Watchdog); model.Mode = ClientMode.RealDoors; return model; }
        public void Dispose() { Link.Dispose(); Transport.Dispose(); }
    }
    internal sealed class UnifiedProcessLocator : IDoorsClientProcessLocator
    {
        internal int Calls;
        public DoorsClientProcess FindSingleClient() { Calls++; return new DoorsClientProcess(12345, DateTime.UtcNow.Ticks, @"C:\Fake\doors.exe", 1); }
    }
    [TestClass, DoNotParallelize]
    public class UnifiedWpfTests
    {
        private static Task Click(ToolPage page, string starts) => page.Actions.Single(a => a.Label.StartsWith(starts, StringComparison.Ordinal)).Command.ExecuteAsync();
        private static bool Enabled(ToolPage page, string starts) => page.Actions.Single(a => a.Label.StartsWith(starts, StringComparison.Ordinal)).Command.CanExecute(null);
        private static void Set(ToolPage page, string key, string value) => page.Field(key).Value = value;
        private static async Task PrepareRequirement(WorkspaceViewModel model)
        {
            var p = model.Tools.Requirements; Set(p, "module", "/P/M"); await Click(p, "Attribute");
            var heading = p.Attributes.Single(a => a.Name == "Object Heading"); heading.Include = true; heading.Value = "Türkçe gereksinim 👋";
            await Click(p, "Dry-run");
        }
        [TestMethod]
        public Task ReadOnlyGatesAllMutationCommandsAndEnablingDiscardsPlans() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                var p = m.Tools.Module; Set(p, "folder", "/P"); Set(p, "name", "New"); await Click(p, "Dry-run");
                Assert.IsFalse(Enabled(p, "İncele")); Assert.AreEqual(0, f.Data.ModuleCommits);
                m.IsReadOnly = false; Assert.IsFalse(Enabled(p, "İncele"));
                await Click(p, "Dry-run"); Assert.IsTrue(Enabled(p, "İncele"));
                Assert.IsFalse(Enabled(m.Tools.Requirements, "İncele")); Assert.IsFalse(Enabled(m.Tools.Links, "İncele")); Assert.IsFalse(Enabled(m.Tools.Batch, "İncele"));
                m.IsReadOnly = true; Assert.IsFalse(Enabled(p, "İncele")); Assert.AreEqual(0, f.Review.Reviews.Count);
            }
        });
        [DataTestMethod, DataRow(false), DataRow(true)]
        public Task ModuleCreationRequiresReviewAndReturnsIdentity(bool approve) => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                m.IsReadOnly = false; var p = m.Tools.Module; Set(p, "folder", "/P"); Set(p, "name", "New");
                f.Review.Answers.Enqueue(approve); await Click(p, "Dry-run"); await Click(p, "İncele");
                Assert.AreEqual(1, f.Review.Reviews.Count); Assert.AreEqual("/P/New", f.Review.Reviews[0].Target);
                Assert.AreEqual(approve ? 1 : 0, f.Data.ModuleCommits); Assert.IsFalse(Enabled(p, "İncele"));
                if (approve) StringAssert.Contains(p.Report, "test-db:new-module");
                await Click(p, "İncele"); Assert.AreEqual(approve ? 1 : 0, f.Data.ModuleCommits);
            }
        });
        [TestMethod]
        public Task InputsAndProfilesInvalidateModuleAndRequirementPlans() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                m.IsReadOnly = false; await PrepareRequirement(m); Assert.IsTrue(Enabled(m.Tools.Requirements, "İncele"));
                m.Tools.Requirements.Attributes[0].Value = "changed"; Assert.IsFalse(Enabled(m.Tools.Requirements, "İncele"));
                await Click(m.Tools.Requirements, "Dry-run"); Assert.IsTrue(Enabled(m.Tools.Requirements, "İncele"));
                Set(m.Tools.Requirements, "module", "/Other"); Assert.AreEqual(0, m.Tools.Requirements.Attributes.Count);
                m.Version = DoorsVersion.V95; Assert.IsTrue(m.IsReadOnly); Assert.IsFalse(Enabled(m.Tools.Requirements, "İncele"));
            }
        });
        [DataTestMethod, DataRow("Create"), DataRow("Update"), DataRow("Move")]
        public Task RequirementWritesUseCatalogAndReviewedService(string action) => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                m.IsReadOnly = false; await PrepareRequirement(m); var p = m.Tools.Requirements;
                Set(p, "action", action); if (action != "Create") Set(p, "object", "12"); if (action == "Move") Set(p, "anchor", "2");
                Assert.IsFalse(p.Attributes.Single(a => a.Name == "Calculated").CanEdit);
                Assert.AreEqual(2, p.Attributes.Single(a => a.Name == "Priority").Schema.EnumerationValues.Count);
                await Click(p, "Dry-run"); Assert.IsTrue(Enabled(p, "İncele"), p.Report);
                f.Review.Answers.Enqueue(true); await Click(p, "İncele");
                Assert.AreEqual(1, f.Data.WriteCommits, m.ErrorDetails); Assert.AreEqual(1, f.Review.Reviews.Count);
                Assert.AreEqual(action == "Create", f.Data.WriteRequest.IsCreate);
                Assert.AreEqual(action == "Move" ? 0 : 1, f.Data.WriteRequest.Changes.Count);
                StringAssert.Contains(p.Report, "Audit=True"); Assert.IsFalse(Enabled(p, "İncele"));
            }
        });
        [TestMethod]
        public Task CancellingPendingApprovalPreventsRequirementDispatch() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                m.IsReadOnly = false; await PrepareRequirement(m); f.Review.Pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var work = Click(m.Tools.Requirements, "İncele"); await WpfTestHost.Until(() => f.Review.Reviews.Count == 1);
                Assert.IsTrue(m.IsBusy); Assert.IsFalse(m.Tools.Dxl.Actions[1].Command.CanExecute(null)); m.Cancel(); await work;
                Assert.AreEqual(0, f.Data.WriteCommits); Assert.IsTrue(m.IsIdle);
            }
        });
        [DataTestMethod, DataRow(false), DataRow(true)]
        public Task LinkRemovalRequestsSeparateDeletionReview(bool deleteApproval) => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                f.Link.Request = LinkWriteTests.Request(LinkWriteAction.Remove); f.Link.Before.Add(f.Link.Selected());
                m.IsReadOnly = false; var p = m.Tools.Links;
                Set(p, "action", "Remove"); Set(p, "source", "/P/Source"); Set(p, "sourceId", "1"); Set(p, "target", "/P/Target"); Set(p, "targetId", "2"); Set(p, "link", "/P/Links");
                await Click(p, "Dry-run"); Assert.IsTrue(Enabled(p, "İncele"), p.Report);
                f.Review.Answers.Enqueue(true); f.Review.Answers.Enqueue(deleteApproval); await Click(p, "İncele");
                Assert.AreEqual(2, f.Review.Reviews.Count); StringAssert.StartsWith(f.Review.Reviews[1].Target, "DELETE ");
                Assert.AreEqual(deleteApproval ? 1 : 0, f.Link.Modes.Count(x => x == "commit"));
                if (deleteApproval) StringAssert.Contains(p.Report, "Removed=1");
            }
        });
        [TestMethod]
        public Task BatchPreservesBatchAndPerRecordApprovalAndJournalIdempotency() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                string path = Path.Combine(f.Transport.Root, "update.csv"); File.WriteAllText(path, "key,action,target,objectId,attr:Priority\nr1,Update,/P/M,17,High\n");
                m.IsReadOnly = false; var p = m.Tools.Batch; Set(p, "file", path); Set(p, "format", "CSV"); Set(p, "repository", "test-db"); Set(p, "namespace", "review-batch");
                await Click(p, "Dry-run"); Assert.IsTrue(Enabled(p, "İncele"), p.Report + m.ErrorDetails); Assert.AreEqual(0, f.Data.WriteCommits);
                f.Review.Answers.Enqueue(true); f.Review.Answers.Enqueue(true); await Click(p, "İncele");
                Assert.AreEqual(1, f.Data.WriteCommits, m.ErrorDetails + p.Report); Assert.AreEqual(2, f.Review.Reviews.Count); Assert.IsFalse(Enabled(p, "İncele"));
                await Click(p, "Dry-run"); StringAssert.Contains(p.Report, "AlreadyApplied"); Assert.AreEqual(1, f.Data.WriteCommits);
            }
        });
        [TestMethod]
        public Task BatchInputChangeInvalidatesCapturedPreview() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                string path = Path.Combine(f.Transport.Root, "update.csv"); File.WriteAllText(path, "key,action,target,objectId,attr:Priority\nr1,Update,/P/M,17,High\n");
                m.IsReadOnly = false; var p = m.Tools.Batch; Set(p, "file", path); Set(p, "format", "CSV"); Set(p, "repository", "test-db"); Set(p, "namespace", "one");
                await Click(p, "Dry-run"); Assert.IsTrue(Enabled(p, "İncele")); Set(p, "namespace", "two"); Assert.IsFalse(Enabled(p, "İncele"));
                await Click(p, "İncele"); Assert.AreEqual(0, f.Data.WriteCommits);
            }
        });
        [DataTestMethod, DataRow(false), DataRow(true)]
        public Task DxlReviewFreezesSourceAndSavesCompleteInlineOrFileResult(bool fileMode) => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                string script = Path.Combine(f.Transport.Root, "original.dxl"), saved = Path.Combine(f.Transport.Root, "result.txt");
                File.WriteAllText(script, "// frozen Türkçe source"); f.Transport.Sessions.Session.Payload = "Unicode 👋 result";
                if (fileMode) { f.Transport.Sessions.Session.FileResult = true; f.Transport.Sessions.Session.OnRun = session => {
                    string resultPath = f.Transport.ResultPath(f.Transport.Executor.ActiveOperation); File.WriteAllText(resultPath, "Unicode 👋 result"); }; }
                m.IsReadOnly = false; var p = m.Tools.Dxl; Set(p, "file", script); Set(p, "parameters", "input=quote\"; DXL injection? = data"); Set(p, "result", fileMode ? "File" : "Inline");
                f.Review.Answers.Enqueue(true); await Click(p, "Kaynağı incele");
                Assert.AreEqual(1, f.Review.Reviews.Count); StringAssert.Contains(f.Review.Reviews[0].Body, "frozen Türkçe source");
                StringAssert.Contains(p.Report, "Unicode 👋 result"); Assert.AreEqual(1, f.Transport.Sessions.OpenCalls);
                Set(p, "save", saved); await Click(p, "Tam sonucu"); Assert.AreEqual("Unicode 👋 result", File.ReadAllText(saved));
                await Click(p, "Tam sonucu"); Assert.AreEqual("Unicode 👋 result", File.ReadAllText(saved));
            }
        });
        [TestMethod]
        public Task DxlCancelRetainsOriginalCompletionAndBlocksProfileAndClose() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                string path = Path.Combine(f.Transport.Root, "slow.dxl"); File.WriteAllText(path, "// bounded fake native work");
                m.IsReadOnly = false; var p = m.Tools.Dxl; Set(p, "file", path); f.Review.Answers.Enqueue(true); f.Transport.Sessions.Session.BlockRun = true;
                var work = Click(p, "Kaynağı incele"); await WpfTestHost.Until(() => f.Transport.Sessions.Session.EnteredRun.IsSet);
                m.Cancel(); await Task.Delay(30); Assert.IsTrue(m.IsBusy); m.Mode = ClientMode.Mock; Assert.AreEqual(ClientMode.RealDoors, m.Mode);
                var close = m.PrepareCloseAsync(); Assert.IsFalse(close.IsCompleted); Assert.IsFalse(m.Tools.Module.Actions[0].Command.CanExecute(null));
                f.Transport.Sessions.Session.ReleaseRun.Set(); await work; await close;
                Assert.AreEqual(1, f.Transport.Sessions.OpenCalls); Assert.IsTrue(f.Transport.Sessions.Session.Disposed);
            }
        });
        [TestMethod]
        public Task ReadOptionsForwardBaselineLimitsAndSurrogateAndInvalidateSnapshot() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                var p = m.Tools.ReadOptions; p.Field("baseline").Flag = true; Set(p, "major", "2"); Set(p, "suffix", "B"); Set(p, "objects", "75");
                await m.ReadCommand.ExecuteAsync(); Assert.AreEqual(2, f.Data.ReadRequest.Baseline.Major); Assert.AreEqual(75, f.Data.ReadRequest.MaxObjects);
                Assert.IsTrue(m.Requirements.Count > 0); Set(p, "timeout", "45"); Assert.AreEqual(0, m.Requirements.Count);
                p.Field("surrogate").Flag = true; Set(p, "marker", "External"); Set(p, "markerValue", "Yes"); Set(p, "direction", "Incoming");
                await m.LinksCommand.ExecuteAsync(); Assert.AreEqual(LinkDirection.Incoming, f.Data.LinkRequest.Direction); Assert.AreEqual("External", f.Data.LinkRequest.SurrogateMapping.MarkerAttribute);
            }
        });
        [TestMethod]
        public Task AttributeDxlRequiresWriteModeAndSeparateConsent() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                m.Tools.ReadOptions.Field("dxl").Flag = true; await m.ReadCommand.ExecuteAsync(); Assert.AreEqual(0, f.Data.RequirementReads);
                m.IsReadOnly = false; f.Review.Answers.Enqueue(false); await m.ReadCommand.ExecuteAsync(); Assert.AreEqual(0, f.Data.RequirementReads);
                f.Review.Answers.Enqueue(true); await m.ReadCommand.ExecuteAsync(); Assert.AreEqual(1, f.Data.RequirementReads); Assert.IsTrue(f.Data.ReadRequest.AllowAttributeDxl);
            }
        });
        [TestMethod]
        public Task WatchdogSettingPersistsOnlyOnSaveAndChangesFutureExecutorPolicy() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                Assert.IsTrue(f.Watchdog.Enabled); m.Tools.Settings.Field("watchdog").Flag = false; Assert.IsTrue(f.Watchdog.Enabled);
                await Click(m.Tools.Settings, "Ayarı"); Assert.IsFalse(f.Watchdog.Enabled); Assert.IsFalse(f.Settings.LoadWatchdogEnabled()); Assert.IsTrue(m.IsReadOnly);
            }
        });
        [TestMethod]
        public Task NativeDoubleRequiresExplicitClientBindingAndResetsWithWatchdogOrProfile() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                var page = m.Tools.Settings; page.Field("nativeDouble").Flag = true; await Click(page, "Ayarı");
                Assert.IsFalse(f.NativeLimits.Enabled); Assert.IsNull(f.NativeLimits.Client);
                await Click(page, "Çalışan"); Assert.AreEqual(1, f.Processes.Calls); Assert.IsFalse(page.Flag("nativeDouble"));
                page.Field("nativeDouble").Flag = true; await Click(page, "Ayarı"); Assert.IsTrue(f.NativeLimits.Enabled); Assert.IsTrue(m.IsReadOnly);
                page.Field("watchdog").Flag = false; await Click(page, "Ayarı"); Assert.IsFalse(f.NativeLimits.Enabled); Assert.IsFalse(page.Flag("nativeDouble"));
                m.Version = DoorsVersion.V95; Assert.IsFalse(f.NativeLimits.Enabled); Assert.IsTrue(m.IsReadOnly);
            }
        });
        [TestMethod]
        public void ProductionFactoryComposesToolsWithoutOpeningNativeSession()
        {
            var session = new ClientSessionFactory(new ClientConfirmation(new UnifiedReview())).Create(new ClientProfile(ClientMode.RealDoors, DoorsVersion.V97, DoorsConnectionMode.AttachExisting), new ClientLog());
            Assert.IsNotNull(session.Tools); Assert.IsNotNull(session.Modules); Assert.IsFalse(session.Tools.RunLimits.Enabled); Assert.IsNull(session.Tools.RunLimits.Client);
        }
        [TestMethod]
        public Task SmokeDefaultCreatesElevenReadOnlyCaseLogsWithoutNativeCalls() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture()) using (var m = f.Model())
            {
                string path = Path.Combine(f.Transport.Root, "smoke"); Set(m.Tools.Smoke, "output", path);
                await Click(m.Tools.Smoke, "Smoke"); Assert.IsTrue(File.Exists(Path.Combine(path, "report.json")), m.ErrorDetails);
                Assert.AreEqual(11, Directory.GetFiles(Path.Combine(path, "logs"), "*.jsonl").Length); Assert.AreEqual(0, f.Transport.Sessions.OpenCalls);
                StringAssert.Contains(File.ReadAllText(Path.Combine(path, "report.json")), "\"liveRequested\":false");
            }
        });
        [TestMethod]
        public Task MockRefusesLiveSmokeAndAllWriteButtons() => WpfTestHost.Run(async () =>
        {
            using (var m = WpfTestHost.Model())
            {
                m.IsReadOnly = false; Assert.IsFalse(Enabled(m.Tools.Dxl, "Kaynağı incele"));
                m.Tools.Smoke.Field("live").Flag = true; await Click(m.Tools.Smoke, "Smoke"); StringAssert.Contains(m.ErrorDetails, "InvalidConfiguration");
            }
        });
        [TestMethod]
        public Task ActivityIsBoundedAndCanBeSavedFromUnifiedDesktop() => WpfTestHost.Run(async () =>
        {
            using (var files = new ExecutorTestContext())
            {
                var log = new ClientLog(); for (int i = 0; i < 2005; i++) log.Write(IntegrationLogLevel.Information, "Case" + i, Guid.NewGuid(), "MetadataOnly");
                Assert.AreEqual(2000, log.ReadActivity().Length); Assert.IsFalse(log.ReadActivity()[0].Contains(" Case0 "));
                using (var m = new WorkspaceViewModel(new ClientSessionFactory(), log))
                {
                    await Click(m.Tools.Activity, "Logları"); StringAssert.Contains(m.Tools.Activity.Report, "Case2004");
                    string path = Path.Combine(files.Root, "activity.txt"); Set(m.Tools.Activity, "save", path); await Click(m.Tools.Activity, "Activity kaydet");
                    Assert.AreEqual(2000, File.ReadAllLines(path).Length);
                }
            }
        });
        [TestMethod]
        public Task EventsQualityCoverageAndExportsUseRetainedEvidence() => WpfTestHost.Run(async () =>
        {
            using (var files = new ExecutorTestContext()) using (var m = WpfTestHost.Model())
            {
                await m.ReadCommand.ExecuteAsync(); await m.LinksCommand.ExecuteAsync(); await m.CompareCommand.ExecuteAsync();
                await Click(m.Tools.Evidence, "Değişiklik"); StringAssert.Contains(m.Tools.Evidence.Report, "Event=");
                string profile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "quality", "do178c-review.example.xml");
                Set(m.Tools.Quality, "profile", profile); Set(m.Tools.Quality, "repository", "mock-repo"); await Click(m.Tools.Quality, "Kalite"); StringAssert.Contains(m.Tools.Quality.Report, "Findings=");
                Set(m.Tools.Coverage, "repository", "mock-repo"); await Click(m.Tools.Coverage, "Coverage"); StringAssert.Contains(m.Tools.Coverage.Report, "ScopeComplete=False");
                string trace = Path.Combine(files.Root, "trace.json"); Set(m.Tools.Evidence, "save", trace); Set(m.Tools.Evidence, "format", "Json"); await Click(m.Tools.Evidence, "Traceability"); Assert.IsTrue(File.Exists(trace));
            }
        });
        [TestMethod]
        public Task ReviewDialogWithoutVisibleOwnerOrWritePermissionDenies() => WpfTestHost.Run(async () =>
        {
            var review = new WriteReview("Test", "/P/M", "Exact request", DateTime.UtcNow.AddMinutes(1), true);
            var dialog = new WpfReviewDialog(() => null, () => true); Assert.IsFalse(await dialog.ReviewAsync(review, CancellationToken.None));
            var parent = new Window(); dialog = new WpfReviewDialog(() => parent, () => false); Assert.IsFalse(await dialog.ReviewAsync(review, CancellationToken.None));
        });
        [DataTestMethod, DataRow(false), DataRow(true)]
        public Task ActualWpfReviewRequiresExactTargetAndAcknowledgement(bool approve) => WpfTestHost.Run(async () =>
        {
            var parent = new Window { Width = 700, Height = 500 }; parent.Show();
            var token = new CancellationTokenSource();
            try
            {
                var dialog = new WpfReviewDialog(() => parent, () => true);
                var work = dialog.ReviewAsync(new WriteReview("Unified review test", "/P/M", "Mock-only exact review body", DateTime.UtcNow.AddMinutes(1), true), token.Token);
                await WpfTestHost.Until(() => Application.Current.Windows.Cast<Window>().Any(w => w.Title == "Unified review test"));
                var window = Application.Current.Windows.Cast<Window>().Single(w => w.Title == "Unified review test");
                var fields = VisualChildren<TextBox>(window).Where(t => !t.IsReadOnly).ToArray(); Assert.AreEqual(1, fields.Length);
                var acknowledgement = VisualChildren<CheckBox>(window).Single();
                var apply = VisualChildren<Button>(window).Single(b => Equals(b.Content, "Onayla ve uygula"));
                Assert.IsFalse(apply.IsEnabled); fields[0].Text = "/P/M"; Assert.IsFalse(apply.IsEnabled);
                acknowledgement.IsChecked = true; Assert.IsTrue(apply.IsEnabled);
                fields[0].Text = "/P/OTHER"; Assert.IsFalse(apply.IsEnabled); fields[0].Text = "/P/M";
                if (approve) apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); else token.Cancel();
                Assert.AreEqual(approve, await work);
            }
            finally { token.Dispose(); parent.Close(); }
        });
        private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match; foreach (var nested in VisualChildren<T>(child)) yield return nested; }
        }
        [TestMethod]
        public Task EveryToolPageAndEditableCatalogRenderWithoutBindingErrors() => WpfTestHost.Run(async () =>
        {
            using (var f = new UnifiedFixture())
            {
                var m = f.Model(); await PrepareRequirement(m); var window = new MainWindow(m) { Width = 1440, Height = 920 };
                var listener = new WpfBindingLog(); PresentationTraceSources.DataBindingSource.Listeners.Add(listener); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                try
                {
                    window.Show(); var tabs = (TabControl)window.FindName("WorkspaceTabs"); tabs.SelectedIndex = 5;
                    string root = Path.Combine(f.Transport.Root, "renders"); Directory.CreateDirectory(root);
                    foreach (var page in m.Tools.Pages)
                    {
                        m.Tools.Selected = page; window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        var image = new RenderTargetBitmap((int)window.RenderSize.Width, (int)window.RenderSize.Height, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var output = File.Create(Path.Combine(root, m.Tools.Pages.IndexOf(page) + ".png"))) encoder.Save(output);
                    }
                    // Preserve the rendered views as review artifacts, outside the fixture-owned temporary directory.
                    string artifact = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "..", "artifacts", "UnifiedWpf", Environment.Is64BitProcess ? "x64" : "x86")); Directory.CreateDirectory(artifact);
                    foreach (var file in Directory.GetFiles(root)) File.Copy(file, Path.Combine(artifact, Path.GetFileName(file)), true);
                    Assert.AreEqual(0, listener.Errors.Count, string.Join("\n", listener.Errors.Take(15)));
                }
                finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await m.PrepareCloseAsync(); window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
            }
        });
    }
}
