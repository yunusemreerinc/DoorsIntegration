using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using DoorsIntegration.Core;
using DoorsIntegration.Com;
using DoorsIntegration.Dxl;
using DoorsIntegration.Export;
using DoorsIntegration.Processing;
using DoorsIntegration.ConfigurationManagement;

namespace DoorsIntegration.Wpf.Services
{
    public enum ClientMode { Mock, RealDoors }
    public sealed class ClientProfile
    {
        public ClientProfile(ClientMode mode, DoorsVersion version, DoorsConnectionMode connection)
        { Mode = mode; Version = version; Connection = connection; }
        public ClientMode Mode { get; }
        public DoorsVersion Version { get; }
        public DoorsConnectionMode Connection { get; }
    }
    public interface IClientSessionFactory { ClientSession Create(ClientProfile profile, IIntegrationLogger logger); }
    // Tools are optional; the default/test composition retains read-only services. The production host supplies explicit review.
    public sealed class ClientSession
    {
        private readonly Func<Task> drain;
        public ClientSession(IProjectService projects, IRequirementService requirements, ILinkService links,
            IBaselineComparisonService baselines, IExportService export, IDoorsHealthChecks health, Func<Task> drain = null, ClientTools tools = null, IModuleService modules = null)
            : this(projects, requirements, links, baselines, export, health, drain, tools, modules,
                  ClientSessionFactory.CreateBulkBaselineService(new UnavailableBaselineMetadataProvider(), new ClientLog())) { }
        public ClientSession(IProjectService projects, IRequirementService requirements, ILinkService links,
            IBaselineComparisonService baselines, IExportService export, IDoorsHealthChecks health, Func<Task> drain, ClientTools tools,
            IModuleService modules, IBulkBaselineService bulkBaselines)
            : this(projects, requirements, links, baselines, export, health, drain, tools, modules, bulkBaselines,
                  new FolderModuleSelectionService(new ProjectHierarchy(projects), new UnavailableModuleSelectionMetadataProvider(), new ClientLog())) { }
        public ClientSession(IProjectService projects, IRequirementService requirements, ILinkService links,
            IBaselineComparisonService baselines, IExportService export, IDoorsHealthChecks health, Func<Task> drain, ClientTools tools,
            IModuleService modules, IBulkBaselineService bulkBaselines, IFolderModuleSelectionService moduleSelection)
            : this(projects, requirements, links, baselines, export, health, drain, tools, modules, bulkBaselines, moduleSelection,
                  new BaselineMetadataService(new UnavailableBaselineHistoryAdapter(), new UnavailableBaselineHistoryAdapter(), new ClientLog())) { }
        public ClientSession(IProjectService projects, IRequirementService requirements, ILinkService links,
            IBaselineComparisonService baselines, IExportService export, IDoorsHealthChecks health, Func<Task> drain, ClientTools tools,
            IModuleService modules, IBulkBaselineService bulkBaselines, IFolderModuleSelectionService moduleSelection, IBaselineMetadataService baselineMetadata)
            : this(projects, requirements, links, baselines, export, health, drain, tools, modules, bulkBaselines, moduleSelection, baselineMetadata,
                  new BulkBaselinePreflightService(new UnavailableBaselinePreflightStateReader(), baselineMetadata, new BaselinePreflightClock(), new ClientLog())) { }
        public ClientSession(IProjectService projects, IRequirementService requirements, ILinkService links,
            IBaselineComparisonService baselines, IExportService export, IDoorsHealthChecks health, Func<Task> drain, ClientTools tools,
            IModuleService modules, IBulkBaselineService bulkBaselines, IFolderModuleSelectionService moduleSelection, IBaselineMetadataService baselineMetadata,
            IBulkBaselinePreflightService baselinePreflight)
        { Projects = projects; Requirements = requirements; Links = links; Baselines = baselines; Export = export; Health = health; this.drain = drain ?? (() => Task.CompletedTask); Tools = tools; Modules = modules ?? tools?.Modules; BulkBaselines = bulkBaselines ?? throw new ArgumentNullException(nameof(bulkBaselines)); ModuleSelection = moduleSelection ?? throw new ArgumentNullException(nameof(moduleSelection)); BaselineMetadata = baselineMetadata ?? throw new ArgumentNullException(nameof(baselineMetadata)); BaselinePreflight = baselinePreflight ?? throw new ArgumentNullException(nameof(baselinePreflight)); }
        public IBulkBaselinePreflightService BaselinePreflight { get; }
        public BaselineManagerSession BaselineManager { get; internal set; }
        public NativeBaselineSetSession NativeBaselineSets { get; internal set; }
        public BaselineVerificationSession BaselineVerification { get; internal set; }
        public ChangeImpactSession ChangeImpact { get; internal set; }
        public INativeReadObservationSource NativeReads { get; internal set; }
        public Func<long> DashboardComCalls { get; internal set; }
        public DashboardSession Dashboard { get; internal set; }
        public IBaselineMetadataService BaselineMetadata { get; }
        public IFolderModuleSelectionService ModuleSelection { get; }
        private sealed class ProjectHierarchy : IDoorsHierarchyReader
        {
            private readonly IProjectService projects;
            internal ProjectHierarchy(IProjectService projects) { this.projects = projects; }
            public Task<DiscoverySnapshot> DiscoverAsync(DiscoveryKind kind, DiscoveryScope scope, CancellationToken cancellationToken) => projects.DiscoverHierarchyAsync(scope, cancellationToken);
        }
        public IBulkBaselineService BulkBaselines { get; }
        public IModuleService Modules { get; }
        public ClientTools Tools { get; }
        public IProjectService Projects { get; }
        public IRequirementService Requirements { get; }
        public ILinkService Links { get; }
        public ITraceabilityGraphService CreateGraphService(IIntegrationLogger logger) => new TraceabilityGraphService(Requirements, Links, logger);
        public ITraceabilityGraphExporter CreateGraphExporter() => new TraceabilityGraphExporter();
        public IBaselineComparisonService Baselines { get; }
        public IExportService Export { get; }
        public IDoorsHealthChecks Health { get; }
        public DxlPingService IsolatedPing { get; internal set; }
        public Task DrainAsync() => drain();
    }
    public sealed class ClientSessionFactory : IClientSessionFactory
    {
        private readonly ClientConfirmation confirmation;
        private readonly DxlWatchdogSettings watchdog;
        private readonly IClientReview baselineReview;
        private readonly string baselineStorageRoot;
        private readonly IBaselineJournalProtector journalProtector;
        public Func<bool> BaselineWritesAllowed { get; set; } = () => false;
        public ClientSessionFactory(ClientConfirmation confirmation = null, DxlWatchdogSettings watchdog = null)
            : this(confirmation, watchdog, confirmation, null) { }
        public ClientSessionFactory(ClientConfirmation confirmation, DxlWatchdogSettings watchdog, IClientReview baselineReview, string baselineStorageRoot, IBaselineJournalProtector journalProtector = null)
        { this.confirmation = confirmation; this.watchdog = watchdog ?? new DxlWatchdogSettings(); this.baselineReview = baselineReview; this.baselineStorageRoot = baselineStorageRoot; this.journalProtector = journalProtector; }
        public ClientSession Create(ClientProfile profile, IIntegrationLogger logger)
        {
            if (profile.Mode == ClientMode.Mock)
            {
                var mockSession = MockDoorsAdapter.CreateSession(logger);
                mockSession.BaselineManager = BaselineManagerSession.Create(profile, logger, baselineReview, baselineStorageRoot, () => BaselineWritesAllowed(), null, protector: journalProtector);
                mockSession.NativeBaselineSets = NativeBaselineSetSession.Create(profile, logger, baselineReview, baselineStorageRoot, () => BaselineWritesAllowed(), mockSession.Links, journalProtector);
                mockSession.BaselineVerification = BaselineVerificationSession.Create(profile, mockSession, logger, baselineStorageRoot, journalProtector, mockSession.NativeBaselineSets.ReadAdapter);
                mockSession.ChangeImpact = ChangeImpactSession.Create(profile, mockSession, logger, baselineStorageRoot, journalProtector);
                mockSession.Dashboard = DashboardSession.Create(profile, mockSession, logger, baselineStorageRoot, journalProtector);
                return mockSession;
            }
            var gate = DoorsOperationGate.Shared;
            var com = new DoorsComSessionFactory();
            var runLimits = new DxlRunLimitAutomationSettings();
            var automation = new DxlRunLimitAutomation(runLimits, logger);
            var comMeter = new DashboardComCallMeter(new ComDxlSessionFactory(com));
            var executor = new DxlExecutor(comMeter, new Utf8DxlScriptLoader(), new DxlWorkspaceFactory(), logger, gate, automation, watchdog, DoorsStaExecutionQueue.Shared);
            var nativeReads = new DxlNativeReadObservationSource(executor, new NativeReadScriptOptions());
            var baselineHistory = new DxlBaselineHistoryAdapter(executor, new BaselineMetadataScriptOptions());
            var baselineMetadata = new BaselineMetadataService(baselineHistory, baselineHistory, logger);
            var reader = new DxlRequirementReader(executor, new RequirementScriptOptions(), logger);
            var hierarchy = new DxlHierarchyReader(executor, new DiscoveryScriptOptions(), logger);
            var linkReader = new DxlLinkReader(executor, new LinkScriptOptions(), logger);
            var errors = new IntegrationErrorHandler(logger);
            var health = new DoorsHealthChecks(new DoorsEnvironmentDiagnostics(), com, new ReadOnlyProbeProtocol(watchdog), gate, errors, logger, automation);
            var requirementAudit = new FileRequirementWriteAuditSink(new RequirementWriteAuditOptions());
            var linkAudit = new FileLinkWriteAuditSink(new LinkWriteAuditOptions());
            var requirements = confirmation == null ? new RequirementService(reader) : new RequirementService(reader,
                new DxlRequirementWriteGateway(executor, new RequirementWriteScriptOptions(), logger, requirementAudit), confirmation, requirementAudit);
            var links = confirmation == null ? new LinkService(linkReader) : new LinkService(linkReader,
                new DxlLinkWriteGateway(executor, new LinkWriteScriptOptions(), logger, linkAudit), confirmation, linkAudit);
            var storage = new ProcessingStorageOptions();
            var tools = confirmation == null ? null : new ClientTools(new ModuleService(hierarchy,
                new DxlModuleCreationGateway(executor, new ModuleCreationScriptOptions(), logger), confirmation), executor, confirmation, storage,
                new DoorsBatchServiceFactory(requirements, new FileBatchJournal(storage), new FileBatchAuditSink(storage), confirmation, storage, new ProcessingClock(), new IBatchChangeValidator[0]), runLimits, new DoorsClientProcessLocator());
            var realSession = new ClientSession(new ProjectService(hierarchy), requirements, links,
                new BaselineComparisonService(reader, logger), CreateExporter(logger), health, async () =>
                {
                    var active = executor.ActiveOperation;
                    if (active != null && !active.Completion.IsCompleted)
                        try { await active.Completion.ConfigureAwait(false); } catch { /* Reader owns late file cleanup and exception observation. */ }
                }, tools, tools?.Modules ?? new ModuleService(hierarchy),
                CreateBulkBaselineService(new UnavailableBaselineMetadataProvider(), logger),
                new FolderModuleSelectionService(hierarchy, new DxlModuleSelectionMetadataProvider(executor, new ModuleSelectionScriptOptions()), logger),
                baselineMetadata, new BulkBaselinePreflightService(new DxlBaselinePreflightStateReader(nativeReads), baselineMetadata, new BaselinePreflightClock(), logger));
            realSession.BaselineManager = BaselineManagerSession.Create(profile, logger, baselineReview, baselineStorageRoot, () => BaselineWritesAllowed(), hierarchy, executor, journalProtector);
            realSession.NativeReads = nativeReads;
            realSession.IsolatedPing = new DxlPingService(executor, new ProtectedDiscoveryDebugStore(), logger);
            realSession.NativeBaselineSets = NativeBaselineSetSession.Create(profile, logger, baselineReview, baselineStorageRoot, () => BaselineWritesAllowed(), realSession.Links, journalProtector, executor: executor);
            realSession.BaselineVerification = BaselineVerificationSession.Create(profile, realSession, logger, baselineStorageRoot, journalProtector, realSession.NativeBaselineSets.ReadAdapter, executor: executor);
            realSession.ChangeImpact = ChangeImpactSession.Create(profile, realSession, logger, baselineStorageRoot, journalProtector,
                native: new DxlReadOnlyLinkEvidenceReader(executor), readContext: new DxlImpactReadContextProvider(nativeReads));
            realSession.DashboardComCalls = () => comMeter.Count;
            realSession.Dashboard = DashboardSession.Create(profile, realSession, logger, baselineStorageRoot, journalProtector);
            return realSession;
        }
        internal static IBulkBaselineService CreateBulkBaselineService(IBaselineMetadataProvider metadata, IIntegrationLogger logger) =>
            new BulkBaselineService(metadata, new ReadOnlyBaselineExecutor(), new InMemoryBaselineJobRepository(), logger,
                new BaselinePlanner(), new BaselinePreflightChecker());
        internal static IExportService CreateExporter(IIntegrationLogger logger) => new ExportService(new IExportFormatWriter[]
            { new JsonExportWriter(), new CsvExportWriter(), new XlsxExportWriter(), new HtmlExportWriter() }, logger);
    }
    public sealed class StaDiagnosticWorker
    {
        public Task<T> RunAsync<T>(Func<T> action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { token.ThrowIfCancellationRequested(); completion.SetResult(action()); }
                catch (OperationCanceledException) { completion.SetCanceled(); }
                catch (Exception ex) { completion.SetException(ex); }
            }) { IsBackground = true, Name = "DOORS WPF diagnostic STA" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            // No token registration detaches this Task: original COM and cleanup must finish first.
            return completion.Task;
        }
    }
    public sealed class ClientLog : IIntegrationLogger
    {
        private readonly ConcurrentQueue<string> entries = new ConcurrentQueue<string>();
        public void Write(IntegrationLogLevel level, string eventName, Guid correlationId, string message)
        {
            string entry = DateTime.UtcNow.ToString("O") + " " + level + " " + eventName + " " + correlationId.ToString("N") + " " + message;
            entries.Enqueue(entry); while (entries.Count > 2000) entries.TryDequeue(out var ignored);
            System.Diagnostics.Trace.WriteLine(entry);
        }
        public string[] ReadActivity() => entries.ToArray();
    }
}
