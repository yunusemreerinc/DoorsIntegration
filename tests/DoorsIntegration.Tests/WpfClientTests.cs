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
using DoorsIntegration.Wpf;
using DoorsIntegration.Wpf.Services;
using DoorsIntegration.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal static class WpfTestHost
    {
        private static readonly Task<Dispatcher> dispatcher = Start();
        private static Task<Dispatcher> Start()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DoorsIntegration.Wpf;component/Themes/Dark.xaml", UriKind.Relative) }); ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }
                catch (Exception ex) { ready.TrySetException(ex); }
            }) { IsBackground = true, Name = "WPF client test dispatcher" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task;
        }
        public static async Task Run(Func<Task> work)
        {
            var owner = await dispatcher; var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = owner.BeginInvoke(new Action(async () => { try { await work(); completion.SetResult(true); } catch (Exception ex) { completion.SetException(ex); } }));
            await completion.Task;
        }
        public static async Task Until(Func<bool> condition)
        { var deadline = DateTime.UtcNow.AddSeconds(10); while (!condition()) { if (DateTime.UtcNow >= deadline) Assert.Fail("WPF operation did not complete."); await Task.Delay(10); } }
        public static WorkspaceViewModel Model(IClientSessionFactory factory = null) => new WorkspaceViewModel(factory ?? new ClientSessionFactory(), new ClientLog());
    }
    internal sealed class RecordingWpfAdapter : IDoorsHierarchyReader, IRequirementReader, ILinkReader
    {
        private readonly MockDoorsAdapter source = new MockDoorsAdapter();
        internal readonly List<int> Threads = new List<int>();
        private void Record() { lock (Threads) Threads.Add(Thread.CurrentThread.ManagedThreadId); }
        public Task<DiscoverySnapshot> DiscoverAsync(DiscoveryKind kind, DiscoveryScope scope, CancellationToken token) { Record(); return source.DiscoverAsync(kind, scope, token); }
        public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken token) { Record(); return source.ReadAsync(request, token); }
        Task<ILinkSnapshot> ILinkReader.ReadAsync(LinkReadRequest request, CancellationToken token) { Record(); return ((ILinkReader)source).ReadAsync(request, token); }
    }
    internal sealed class WpfFactory : IClientSessionFactory
    {
        internal Func<ClientProfile, IIntegrationLogger, ClientSession> Build;
        internal readonly List<ClientProfile> Profiles = new List<ClientProfile>();
        public ClientSession Create(ClientProfile profile, IIntegrationLogger logger) { Profiles.Add(profile); return Build?.Invoke(profile, logger) ?? MockDoorsAdapter.CreateSession(logger); }
    }
    internal sealed class CleanupThrowReader : IRequirementReader
    {
        public async Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken token) =>
            new Snapshot(await new MockDoorsAdapter().ReadAsync(request, token));
        private sealed class Snapshot : IRequirementSnapshot
        {
            private readonly IRequirementSnapshot source;
            internal Snapshot(IRequirementSnapshot source) { this.source = source; }
            public RequirementReadSummary Summary => source.Summary;
            public IEnumerable<RequirementRecord> ReadObjects() => source.ReadObjects();
            public Stream OpenRawStream() => source.OpenRawStream();
            public void Dispose() { source.Dispose(); throw new IOException("Mock private cleanup failure."); }
        }
    }
    [TestClass, DoNotParallelize]
    public class WpfClientTests
    {
        [TestMethod]
        public Task StartsInMockReadOnlyWithoutNativeCalls() => WpfTestHost.Run(() =>
        {
            var factory = new WpfFactory(); using (var model = WpfTestHost.Model(factory))
            { Assert.AreEqual(ClientMode.Mock, model.Mode); Assert.IsTrue(model.IsReadOnly); Assert.AreEqual(1, factory.Profiles.Count); Assert.AreEqual(0, model.Tree.Count); Assert.IsFalse(model.ExportCommand.CanExecute(null)); }
            return Task.CompletedTask;
        });
        [TestMethod]
        public Task DiscoveryPreservesTreeAndDeniedModuleMetadata() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            { await model.DiscoverCommand.ExecuteAsync(); Assert.AreEqual(1, model.Tree.Count); var root = model.Tree.Single(); Assert.AreEqual("Demo", root.Label);
                Assert.IsTrue(root.Children.Any(value => value.Item.Type == DoorsItemKind.FormalModule)); Assert.IsTrue(root.Children.Any(value => value.Item.AccessStatus == DoorsAccessStatus.AccessDenied)); }
        });
        [TestMethod]
        public Task GlobalSearchPagingAndDeletedFilterUseFullSnapshot() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await model.ReadCommand.ExecuteAsync(); Assert.AreEqual(100, model.Requirements.Count); StringAssert.Contains(model.PageLabel, "239");
                await model.NextCommand.ExecuteAsync(); Assert.IsTrue(model.Requirements.First().Id > 100);
                model.Search = "Input validation 220"; await model.SearchCommand.ExecuteAsync(); Assert.AreEqual(220, model.Requirements.Single().Id);
                model.Search = ""; model.Filter = RequirementFilter.Deleted; await model.SearchCommand.ExecuteAsync();
                Assert.IsTrue(model.Requirements.Single().IsDeleted); Assert.AreEqual("NotRequested", model.Requirements.Single().ContentStatus);
            }
        });
        [TestMethod]
        public Task AttributesAndRelatedLinkDetailsRetainReadStatusAndOpaqueRichText() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await model.ReadCommand.ExecuteAsync(); await model.LinksCommand.ExecuteAsync(); model.SelectedRequirement = model.Requirements.First(); await model.DetailsCommand.ExecuteAsync();
                Assert.AreEqual(3, model.Attributes.Count); Assert.IsTrue(model.Attributes.Any(value => value.Name == "Object Text" && value.RawRich.StartsWith("{\\rtf1", StringComparison.Ordinal)));
                Assert.AreEqual(1, model.RelatedLinks.Count); StringAssert.Contains(model.RelatedLinks.Single().Target, "Test cases");
            }
        });
        [TestMethod]
        public Task GraphRetainsUnresolvedEdgesWithoutCoverageClaims() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            { await model.LinksCommand.ExecuteAsync(); Assert.AreEqual(24, model.GraphLinks.Count); Assert.IsFalse(model.GraphLinks.Last().IsResolved); model.SelectGraphEdge(model.GraphLinks.Last());
                StringAssert.Contains(model.GraphDetail, "AccessDenied"); StringAssert.Contains(model.LinkSummary, "complete=False"); }
        });
        [TestMethod]
        public Task BaselineUsesActualCoreComparisonAndPagedAttributeDeltas() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await model.CompareCommand.ExecuteAsync(); Assert.AreEqual(100, model.DiffRows.Count); StringAssert.Contains(model.DiffSummary, "Modified=");
                Assert.AreEqual("Deleted", model.DiffRows.Single(value => value.Id == 8).Status); Assert.AreEqual("Added", model.DiffRows.Single(value => value.Id == 9).Status);
                model.SelectedDiff = model.DiffRows.Single(value => value.Id == 7); await model.DiffDetailsCommand.ExecuteAsync(); StringAssert.Contains(model.DiffDetails, "Object Text");
                await model.DiffNextCommand.ExecuteAsync(); Assert.IsTrue(model.DiffRows.First().Id > 100);
                model.AfterSuffix = "C"; Assert.AreEqual(0, model.DiffRows.Count); model.ExportScope = ClientExportScope.BaselineDiff; Assert.IsFalse(model.ExportCommand.CanExecute(null));
            }
        });
        [TestMethod]
        public Task ProfileSwitchClearsAllRetainedEvidenceAndGraphSelection() => WpfTestHost.Run(async () =>
        {
            var factory = new WpfFactory(); using (var model = WpfTestHost.Model(factory))
            {
                await model.DiscoverCommand.ExecuteAsync(); await model.ReadCommand.ExecuteAsync(); await model.LinksCommand.ExecuteAsync(); await model.CompareCommand.ExecuteAsync();
                model.SelectGraphEdge(model.GraphLinks.Last()); model.Mode = ClientMode.RealDoors;
                Assert.AreEqual(ClientMode.RealDoors, factory.Profiles.Last().Mode); Assert.AreEqual(0, model.Tree.Count); Assert.AreEqual(0, model.Requirements.Count);
                Assert.AreEqual(0, model.GraphLinks.Count); Assert.AreEqual(0, model.DiffRows.Count); Assert.IsFalse(model.GraphDetail.Contains("AccessDenied")); Assert.IsFalse(model.ExportCommand.CanExecute(null));
            }
        });
        [TestMethod]
        public Task ModuleAttributeAndLinkPathChangesInvalidateCorrectSnapshots() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                await model.ReadCommand.ExecuteAsync(); await model.LinksCommand.ExecuteAsync(); model.LinkPath = "/other/links";
                Assert.AreEqual(0, model.GraphLinks.Count); Assert.AreEqual(100, model.Requirements.Count);
                model.CustomAttributes = "Status,Owner"; Assert.AreEqual(0, model.Requirements.Count); Assert.IsFalse(model.SearchCommand.CanExecute(null));
            }
        });
        [TestMethod]
        public Task CleanupFailureCannotCrashProfileSwitchOrRetainStaleRows() => WpfTestHost.Run(async () =>
        {
            var factory = new WpfFactory { Build = (profile, log) => { var source = MockDoorsAdapter.CreateSession(log); return new ClientSession(source.Projects,
                new RequirementService(new CleanupThrowReader()), source.Links, source.Baselines, source.Export, source.Health); } };
            using (var model = WpfTestHost.Model(factory))
            { await model.ReadCommand.ExecuteAsync(); model.Mode = ClientMode.RealDoors; Assert.AreEqual(0, model.Requirements.Count); StringAssert.Contains(model.ErrorDetails, "snapshot temizlenemedi"); }
        });
        [TestMethod]
        public Task CancellationDoesNotPublishPartialSnapshotAndPreventsProfileChangesDuringRun() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            {
                var pending = model.ReadCommand.ExecuteAsync(); Assert.IsTrue(model.IsBusy); model.Mode = ClientMode.RealDoors; Assert.AreEqual(ClientMode.Mock, model.Mode);
                Assert.IsFalse(model.DiscoverCommand.CanExecute(null)); model.Cancel(); await pending;
                Assert.IsTrue(model.IsIdle); Assert.AreEqual(0, model.Requirements.Count); StringAssert.Contains(model.Status, "İptal");
            }
        });
        [TestMethod]
        public Task NativeDrainKeepsBusyAndClosePendingUntilOriginalCompletion() => WpfTestHost.Run(async () =>
        {
            var drain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new WpfFactory { Build = (profile, log) => { var source = MockDoorsAdapter.CreateSession(log); return new ClientSession(source.Projects, source.Requirements,
                source.Links, source.Baselines, source.Export, source.Health, () => drain.Task); } };
            var model = WpfTestHost.Model(factory);
            var operation = model.ReadCommand.ExecuteAsync(); await WpfTestHost.Until(() => model.Requirements.Count > 0);
            Assert.IsTrue(model.IsBusy); Assert.IsFalse(model.PingCommand.CanExecute(null)); var close = model.PrepareCloseAsync(); Assert.IsFalse(close.IsCompleted);
            drain.SetResult(true); await operation; await close; model.Dispose();
        });
        [TestMethod]
        public Task ServicesInvokeOffDispatcherAndPropertyChangesReturnToDispatcher() => WpfTestHost.Run(async () =>
        {
            int uiThread = Thread.CurrentThread.ManagedThreadId; var changedThreads = new List<int>(); var adapter = new RecordingWpfAdapter();
            var factory = new WpfFactory { Build = (profile, log) => { var source = MockDoorsAdapter.CreateSession(log); return new ClientSession(new ProjectService(adapter), new RequirementService(adapter),
                new LinkService(adapter), new BaselineComparisonService(adapter, log), source.Export, source.Health); } };
            using (var model = WpfTestHost.Model(factory))
            {
                model.PropertyChanged += (sender, args) => changedThreads.Add(Thread.CurrentThread.ManagedThreadId);
                await model.DiscoverCommand.ExecuteAsync(); await model.ReadCommand.ExecuteAsync(); await model.LinksCommand.ExecuteAsync(); await model.CompareCommand.ExecuteAsync();
                Assert.IsTrue(adapter.Threads.Count >= 5); Assert.IsTrue(adapter.Threads.All(value => value != uiThread)); Assert.IsTrue(changedThreads.All(value => value == uiThread));
            }
        });
        [TestMethod]
        public Task DiagnosticWorkerOwnsSeparateStaAndMockReportIsExplicitlySimulated() => WpfTestHost.Run(async () =>
        {
            int ui = Thread.CurrentThread.ManagedThreadId;
            var actual = await new StaDiagnosticWorker().RunAsync(() => Tuple.Create(Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState()), CancellationToken.None);
            Assert.AreNotEqual(ui, actual.Item1); Assert.AreEqual(ApartmentState.STA, actual.Item2);
            using (var model = WpfTestHost.Model()) { await model.PingCommand.ExecuteAsync(); Assert.AreEqual(6, model.HealthSteps.Count); StringAssert.Contains(model.DiagnosticsText, "simülasyon"); StringAssert.Contains(model.DiagnosticsText, "Apartment=STA"); }
        });
        [DataTestMethod]
        [DataRow(ExportFormat.Json)] [DataRow(ExportFormat.Csv)] [DataRow(ExportFormat.Xlsx)]
        public Task SharedExportIncludesEntireSnapshotDespitePagingAndSearch(ExportFormat format) => WpfTestHost.Run(async () =>
        {
            using (var context = new ExportTestContext())
            using (var model = WpfTestHost.Model())
            {
                await model.ReadCommand.ExecuteAsync(); model.Search = "Input validation 220"; await model.SearchCommand.ExecuteAsync(); Assert.AreEqual(1, model.Requirements.Count);
                model.ExportScope = ClientExportScope.Requirements; model.ExportFormat = format; model.ExportRoot = context.Root;
                await model.ExportCommand.ExecuteAsync(); Assert.IsTrue(Directory.Exists(model.LastExportPath));
                var report = ExportTestContext.Json<ExportTestReport>(Path.Combine(model.LastExportPath, "report.json")); Assert.AreEqual("Completed", report.Status);
                if (format == ExportFormat.Csv) Assert.AreEqual(241, ExportTestCsv.Read(Path.Combine(model.LastExportPath, "Requirements.csv")).Count);
            }
        });
        [TestMethod]
        public Task BaselineDiffExportUsesBaselineReportSourceAndNoMissingScopeFallback() => WpfTestHost.Run(async () =>
        {
            using (var context = new ExportTestContext())
            using (var model = WpfTestHost.Model())
            {
                model.ExportScope = ClientExportScope.BaselineDiff; Assert.IsFalse(model.ExportCommand.CanExecute(null)); await model.CompareCommand.ExecuteAsync();
                model.ExportRoot = context.Root; await model.ExportCommand.ExecuteAsync(); StringAssert.Contains(File.ReadAllText(Path.Combine(model.LastExportPath, "data.json")), "BaselineEvents");
                model.ExportScope = ClientExportScope.Workspace; Assert.IsFalse(model.ExportCommand.CanExecute(null));
            }
        });
        [TestMethod]
        public Task ReadErrorsAreCentralizedAndDoNotPopulateFakeSuccessRows() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            { model.ModulePath = "/Demo/Restricted"; await model.ReadCommand.ExecuteAsync(); Assert.AreEqual(0, model.Requirements.Count); StringAssert.Contains(model.ErrorDetails, "ComAccessDenied"); Assert.IsTrue(model.IsIdle); }
        });
        [TestMethod]
        public Task LongSearchAndInvalidBaselineNumbersAreReportedWithoutEscapingAsyncCommands() => WpfTestHost.Run(async () =>
        {
            using (var model = WpfTestHost.Model())
            { await model.ReadCommand.ExecuteAsync(); model.Search = new string('x', 257); await model.SearchCommand.ExecuteAsync(); StringAssert.Contains(model.ErrorDetails, "InvalidConfiguration");
                model.BeforeMajor = "invalid"; await model.CompareCommand.ExecuteAsync(); Assert.AreEqual(0, model.DiffRows.Count); StringAssert.Contains(model.ErrorDetails, "InvalidConfiguration"); }
        });
        [TestMethod]
        public Task BothCompositionsKeepWriteServicesFailClosed() => WpfTestHost.Run(async () =>
        {
            foreach (var mode in new[] { ClientMode.Mock, ClientMode.RealDoors })
            {
                var session = new ClientSessionFactory().Create(new ClientProfile(mode, DoorsVersion.V97, DoorsConnectionMode.AttachExisting), new ClientLog());
                var request = new RequirementWriteRequest(new RequirementWriteTarget("/P/M"),
                    new[] { new RequirementAttributeChange("Object Text", "test") });
                var failure = await Assert.ThrowsExceptionAsync<IntegrationException>(() => session.Requirements.PreviewWriteAsync(request));
                Assert.AreEqual(IntegrationErrorCode.RequirementWriteApprovalRequired, failure.Code);
            }
        });
        [TestMethod]
        public Task RealWpfWindowRendersAllTabsWithoutBindingErrorsAndClosesCleanly() => WpfTestHost.Run(async () =>
        {
            var listener = new WpfBindingLog(); PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var model = WpfTestHost.Model(); var window = new MainWindow(model) { Width = 1440, Height = 920 };
            try
            {
                window.Show(); await WpfTestHost.Until(() => model.IsIdle && model.Requirements.Count > 0);
                await model.LinksCommand.ExecuteAsync(); await model.CompareCommand.ExecuteAsync();
                ((DataGrid)window.FindName("RequirementsGrid")).SelectedIndex = 0;
                await WpfTestHost.Until(() => model.IsIdle && model.Attributes.Count > 0);
                await model.PingCommand.ExecuteAsync();
                Assert.AreEqual(Color.FromRgb(11, 21, 35), ((SolidColorBrush)window.Background).Color);
                var tabs = (TabControl)window.FindName("WorkspaceTabs"); Assert.AreEqual(12, tabs.Items.Count);
                CollectionAssert.AreEqual(new[] { "Workspace", "Baseline diff", "Traceability graph", "Connection diagnostics", "Export", "İşlemler & Settings", "Bulk baseline / Modül seçimi", "Bulk baseline / Dry run", "Bulk Baseline Manager", "Baseline Traceability", "Change Impact Analysis", "Configuration Dashboard" },
                    tabs.Items.Cast<TabItem>().Select(tab => (string)tab.Header).ToArray());
                string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "..", "artifacts", "WpfClient", Environment.Is64BitProcess ? "x64" : "x86"));
                Directory.CreateDirectory(root);
                for (int index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index; window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var image = new RenderTargetBitmap((int)window.RenderSize.Width, (int)window.RenderSize.Height, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var file = File.Create(Path.Combine(root, "tab-" + index + ".png"))) encoder.Save(file);
                }
                Assert.AreEqual(0, listener.Errors.Count, string.Join("\n", listener.Errors.Take(8)));
            }
            finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await model.PrepareCloseAsync(); window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        });
    }
    internal sealed class WpfBindingLog : TraceListener
    {
        internal readonly List<string> Errors = new List<string>();
        public override void Write(string message) { }
        public override void WriteLine(string message) { Errors.Add(message); }
    }
}
