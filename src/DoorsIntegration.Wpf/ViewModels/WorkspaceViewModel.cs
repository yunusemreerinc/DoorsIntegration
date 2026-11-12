using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Export;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed partial class WorkspaceViewModel : ObservableModel, IDisposable
    {
        private const int PageSize = 100;
        private readonly IClientSessionFactory factory;
        private readonly IIntegrationLogger logger;
        private readonly IntegrationErrorHandler errors;
        private readonly StaDiagnosticWorker sta = new StaDiagnosticWorker();
        private readonly List<UiCommand> commands = new List<UiCommand>();
        private ClientSession session;
        private CancellationTokenSource cancellation;
        private TaskCompletionSource<bool> idle;
        private bool busy, closing, disposed;
        private bool readOnly = true;
        private readonly System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        private ClientMode mode;
        private DoorsVersion version = DoorsVersion.V97;
        private DoorsConnectionMode connection = DoorsConnectionMode.AttachExisting;
        private DiscoverySnapshot discovery;
        private IRequirementSnapshot requirements;
        private ILinkSnapshot links;
        private IBaselineComparisonResult comparison;
        private string modulePath = MockDoorsAdapter.ModulePath, rootPath = "/", customAttributes = "Status", search = "", linkPath = MockDoorsAdapter.LinkPath;
        private string status = "Mock hazır. Salt okunur istemci.", error = "", progressText = "Hazır", diagnostics = "Henüz tanılama çalıştırılmadı.", exportPath = "";
        private double progressValue;
        private bool indeterminate;
        private int page, total, diffPage;
        private RequirementFilter filter;
        private RequirementListRow selectedRequirement;
        private AttributeDetailRow selectedAttribute;
        private BaselineDiffRow selectedDiff;
        private string diffDetails = "Bir değişiklik seçin.", diffSummary = "İki mevcut baseline seçin.", linkSummary = "Link kapsamı henüz okunmadı.";
        private string graphDetail = "Bir graph ilişkisine tıklayarak ayrıntısını inceleyin.";
        private string beforeMajor = "1", beforeMinor = "0", beforeSuffix = "A", afterMajor = "2", afterMinor = "0", afterSuffix = "B";
        private bool exactText;
        private ClientExportScope exportScope;
        private ExportFormat exportFormat = ExportFormat.Json;
        private string exportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DoorsIntegration", "Exports");
        private IReadOnlyList<ProjectTreeNode> tree = Array.Empty<ProjectTreeNode>();
        private IReadOnlyList<RequirementListRow> rows = Array.Empty<RequirementListRow>();
        private IReadOnlyList<AttributeDetailRow> attributes = Array.Empty<AttributeDetailRow>();
        private IReadOnlyList<LinkDetailRow> related = Array.Empty<LinkDetailRow>(), graph = Array.Empty<LinkDetailRow>();
        private IReadOnlyList<BaselineDiffRow> diffRows = Array.Empty<BaselineDiffRow>();
        private IReadOnlyList<DoorsHealthStepResult> healthSteps = Array.Empty<DoorsHealthStepResult>();

        public WorkspaceViewModel(IClientSessionFactory factory, IIntegrationLogger logger, IClientSettingsStore settings = null, DxlWatchdogSettings watchdog = null)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (factory is ClientSessionFactory clientFactory) clientFactory.BaselineWritesAllowed = () => !IsReadOnly && !IsClosing && !disposed;
            errors = new IntegrationErrorHandler(logger); session = factory.Create(Profile, logger);
            DiscoverCommand = Command(DiscoverAsync);
            ReadCommand = Command(ReadAsync, () => !string.IsNullOrWhiteSpace(ModulePath));
            SearchCommand = Command(() => PageAsync(0), () => requirements != null);
            NextCommand = Command(() => PageAsync(page + 1), () => requirements != null && (page + 1) * PageSize < total);
            PreviousCommand = Command(() => PageAsync(page - 1), () => requirements != null && page > 0);
            DetailsCommand = Command(DetailsAsync, () => requirements != null && selectedRequirement != null);
            LinksCommand = Command(LinksAsync);
            CompareCommand = Command(CompareAsync);
            DiffNextCommand = Command(() => DiffPageAsync(diffPage + 1), () => comparison != null && (diffPage + 1) * PageSize < comparison.Summary.Counts.Values.Sum());
            DiffPreviousCommand = Command(() => DiffPageAsync(diffPage - 1), () => comparison != null && diffPage > 0);
            DiffDetailsCommand = Command(DiffDetailsAsync, () => comparison != null && selectedDiff != null);
            DiagnoseCommand = Command(() => DiagnoseAsync(false)); PingCommand = Command(() => DiagnoseAsync(true));
            IsolatedPingCommand = Command(IsolatedPingAsync);
            ExportCommand = Command(ExportAsync, CanExport);
            CancelCommand = new UiCommand(() => { Cancel(); return Task.CompletedTask; }, () => IsBusy && cancellation != null && !cancellation.IsCancellationRequested);
            Tools = new OperationsViewModel(this, logger, settings ?? new ClientSettingsStore(ClientSettingsStore.DefaultPath, logger), watchdog ?? new DxlWatchdogSettings());
            BulkSelection = new ModuleSelectionViewModel(this);
            BaselineManager = new BulkBaselineManagerViewModel(this);
            BaselineTraceability = new BaselineTraceabilityViewModel(this);
            ChangeImpact = new ChangeImpactViewModel(this);
            Dashboard = new ConfigurationDashboardViewModel(this);
            OpenDashboardSourceCommand=Command(()=>Dashboard.OpenSourceModule(SelectedDashboardSource.ModuleIdentity),()=>SelectedDashboardSource!=null);
            Find=new RequirementFindViewModel(this);
            GoTo=new RequirementGoToViewModel(this);
        }
        private UiCommand Command(Func<Task> action, Func<bool> available = null)
        { var command = new UiCommand(action, () => IsIdle && !closing && (available?.Invoke() ?? true)); commands.Add(command); return command; }
        private ClientProfile Profile => new ClientProfile(Mode, Version, Connection);
        public bool IsBusy => busy;
        public bool IsIdle => !busy && !disposed;
        public bool IsReadOnly { get => readOnly; set { if (IsIdle && Set(ref readOnly, value)) { Tools.InvalidateContext(); BaselineManager?.InvalidateApproval(); Refresh(); Changed(nameof(ModeLabel)); } } }
        public string ModeLabel => IsReadOnly ? "SALT OKUNUR" : "YAZMA AÇIK · HER İŞLEMDE ONAY";
        public OperationsViewModel Tools { get; }
        public ModuleSelectionViewModel BulkSelection { get; }
        public BulkBaselineManagerViewModel BaselineManager { get; }
        public BaselineTraceabilityViewModel BaselineTraceability { get; }
        public ChangeImpactViewModel ChangeImpact { get; }
        public ConfigurationDashboardViewModel Dashboard { get; }
        public UiCommand OpenDashboardSourceCommand { get; }
        private BaselineVersionReference dashboardSource;
        public IReadOnlyList<BaselineVersionReference> DashboardSources => Dashboard?.CurrentSnapshot?.ObservedModules ?? (IReadOnlyList<BaselineVersionReference>)Array.Empty<BaselineVersionReference>();
        public BaselineVersionReference SelectedDashboardSource { get=>dashboardSource; set { Set(ref dashboardSource,value); Refresh(); } }
        public event Action<DashboardDestination> DashboardNavigationRequested;
        private string dashboardContext = "";
        public string DashboardContext { get => dashboardContext; private set => Set(ref dashboardContext, value); }
        internal void ApplyDashboardNavigation(DashboardNavigationIntent intent, IRequirementSnapshot opened, Tuple<List<RequirementListRow>, int> projection)
        {
            ClearModule(); Tools.InvalidateContext(); BaselineManager.InvalidateApproval();
            discovery=null; Tree=Array.Empty<ProjectTreeNode>(); Changed(nameof(DashboardSources));
            modulePath = intent.ModulePath ?? ""; Changed(nameof(ModulePath));
            DashboardContext = "Dashboard source · " + intent.Environment + " · module=" + intent.ModuleIdentity + " · version=" + intent.Version?.Version + " · requirement=" + intent.RequirementIdentity + " · analysis=" + intent.AnalysisIdentity + " · finding=" + intent.FindingIdentity + " · job=" + intent.JobId + "\n" + Dashboard.Details;
            if(intent.Destination==DashboardDestination.RequirementQuality) { Tools.Selected=Tools.Quality; Tools.Quality.Report=DashboardContext; }
            BaselineManager.ApplyDashboardContext(intent,DashboardContext);
            if (opened != null) { requirements = opened; Find?.ResetSnapshot(); GoTo?.ResetSnapshot(); ApplyPage(projection, 0); }
            if (intent.Version != null && !intent.Version.IsCurrent) { beforeMajor = intent.Version.Major.ToString(CultureInfo.InvariantCulture); beforeMinor = intent.Version.Minor.ToString(CultureInfo.InvariantCulture); beforeSuffix = intent.Version.Suffix; Changed(nameof(BeforeMajor)); Changed(nameof(BeforeMinor)); Changed(nameof(BeforeSuffix)); }
            ChangeImpact.ApplyDashboardContext(intent); BaselineTraceability.ApplyDashboardContext(intent);
            DashboardNavigationRequested?.Invoke(intent.Destination);
        }
        internal Tuple<List<RequirementListRow>, int> ProjectDashboardRequirements(IRequirementSnapshot opened, int? absolute, CancellationToken token)
        {
            var result = new List<RequirementListRow>(); int count = 0;
            foreach (var item in opened.ReadObjects()) { token.ThrowIfCancellationRequested(); if (absolute.HasValue && item.AbsoluteNumber != absolute.Value) continue; count++; if(result.Count < PageSize) result.Add(new RequirementListRow(item)); }
            return Tuple.Create(result,count);
        }
        internal bool IsClosing => closing;
        internal ClientSession Session => session;
        internal IRequirementSnapshot RequirementSnapshot => requirements;
        internal ILinkSnapshot LinkSnapshot => links;
        internal IBaselineComparisonResult ComparisonSnapshot => comparison;
        internal void InvalidateReadSettings() { if (IsIdle) { ClearModule(); discovery = null; Tree = Array.Empty<ProjectTreeNode>(); } }
        internal Task RunToolAsync(string description, Func<CancellationToken, IProgress<string>, Task<Action>> work) => RunAsync(description, work);
        internal void ReportToolOutcome(string text) { Status = text; ProgressText = text; }
        internal Task InvalidateBeforeWriteAsync() => dispatcher.InvokeAsync(() => { ClearModule(); discovery = null; Tree = Array.Empty<ProjectTreeNode>(); LastExportPath = ""; }).Task;
        public ClientMode Mode { get => mode; set { if (IsIdle && Set(ref mode, value)) ChangeProfile(); } }
        public DoorsVersion Version { get => version; set { if (IsIdle && Set(ref version, value)) ChangeProfile(); } }
        public DoorsConnectionMode Connection { get => connection; set { if (IsIdle && Set(ref connection, value)) ChangeProfile(); } }
        public string ProfileDescription => Mode == ClientMode.Mock ? "MOCK · örnek veriler · gerçek DOORS bağlantısı yok" : "REAL DOORS · " + DoorsVersions.Format(Version) + " · " + Connection;
        public Array Modes => Enum.GetValues(typeof(ClientMode));
        public Array Versions => Enum.GetValues(typeof(DoorsVersion));
        public Array Connections => Enum.GetValues(typeof(DoorsConnectionMode));
        public Array Filters => Enum.GetValues(typeof(RequirementFilter));
        public Array ExportFormats => Enum.GetValues(typeof(ExportFormat));
        public Array ExportScopes => Enum.GetValues(typeof(ClientExportScope));
        public string RootPath { get => rootPath; set { if (IsIdle) Set(ref rootPath, value); } }
        public string ModulePath { get => modulePath; set { if (IsIdle && Set(ref modulePath, value)) ClearModule(); } }
        public string CustomAttributes { get => customAttributes; set { if (IsIdle && Set(ref customAttributes, value)) ClearModule(); } }
        public string LinkPath { get => linkPath; set { if (IsIdle && Set(ref linkPath, value)) { Release(links); links = null; ClearLinks(); } } }
        public string Search { get => search; set { if (IsIdle) Set(ref search, value); } }
        public RequirementFilter Filter { get => filter; set { if (IsIdle) Set(ref filter, value); } }
        public string Status { get => status; private set => Set(ref status, value); }
        public string ErrorDetails { get => error; private set => Set(ref error, value); }
        public string ProgressText { get => progressText; private set => Set(ref progressText, value); }
        public double ProgressValue { get => progressValue; private set => Set(ref progressValue, value); }
        public bool ProgressIndeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
        public string DiagnosticsText { get => diagnostics; private set => Set(ref diagnostics, value); }
        public string LastExportPath { get => exportPath; private set => Set(ref exportPath, value); }
        public string ExportRoot { get => exportRoot; set { if (IsIdle) Set(ref exportRoot, value); } }
        public ClientExportScope ExportScope { get => exportScope; set { if (IsIdle && Set(ref exportScope, value)) Refresh(); } }
        public ExportFormat ExportFormat { get => exportFormat; set { if (IsIdle) Set(ref exportFormat, value); } }
        public IReadOnlyList<ProjectTreeNode> Tree { get => tree; private set => Set(ref tree, value); }
        public IReadOnlyList<RequirementListRow> Requirements { get => rows; private set => Set(ref rows, value); }
        public IReadOnlyList<AttributeDetailRow> Attributes { get => attributes; private set => Set(ref attributes, value); }
        public IReadOnlyList<LinkDetailRow> RelatedLinks { get => related; private set => Set(ref related, value); }
        public IReadOnlyList<LinkDetailRow> GraphLinks { get => graph; private set => Set(ref graph, value); }
        public IReadOnlyList<BaselineDiffRow> DiffRows { get => diffRows; private set => Set(ref diffRows, value); }
        public IReadOnlyList<DoorsHealthStepResult> HealthSteps { get => healthSteps; private set => Set(ref healthSteps, value); }
        public string PageLabel => "Sayfa " + (page + 1) + " · " + Requirements.Count + " / " + total + " eşleşme · tam snapshot export edilir";
        public string DiffPageLabel => "Sayfa " + (diffPage + 1) + " · ilk " + PageSize + " satır / sayfa";
        public string LinkSummary { get => linkSummary; private set => Set(ref linkSummary, value); }
        public string DiffSummary { get => diffSummary; private set => Set(ref diffSummary, value); }
        public string DiffDetails { get => diffDetails; private set => Set(ref diffDetails, value); }
        public string GraphDetail { get => graphDetail; private set => Set(ref graphDetail, value); }
        public void SelectGraphEdge(LinkDetailRow row) { if (IsIdle) GraphDetail = row?.Detail ?? "Bir ilişki seçin."; }
        public RequirementListRow SelectedRequirement { get => selectedRequirement; set { if (IsIdle && Set(ref selectedRequirement, value)) { Attributes = Array.Empty<AttributeDetailRow>(); SelectedAttribute = null; RelatedLinks = Array.Empty<LinkDetailRow>(); Refresh(); } } }
        public AttributeDetailRow SelectedAttribute { get => selectedAttribute; set => Set(ref selectedAttribute, value); }
        public BaselineDiffRow SelectedDiff { get => selectedDiff; set { if (IsIdle && Set(ref selectedDiff, value)) Refresh(); } }
        public string BeforeMajor { get => beforeMajor; set { if (IsIdle && Set(ref beforeMajor, value)) ClearComparison(); } }
        public string BeforeMinor { get => beforeMinor; set { if (IsIdle && Set(ref beforeMinor, value)) ClearComparison(); } }
        public string BeforeSuffix { get => beforeSuffix; set { if (IsIdle && Set(ref beforeSuffix, value)) ClearComparison(); } }
        public string AfterMajor { get => afterMajor; set { if (IsIdle && Set(ref afterMajor, value)) ClearComparison(); } }
        public string AfterMinor { get => afterMinor; set { if (IsIdle && Set(ref afterMinor, value)) ClearComparison(); } }
        public string AfterSuffix { get => afterSuffix; set { if (IsIdle && Set(ref afterSuffix, value)) ClearComparison(); } }
        public bool ExactText { get => exactText; set { if (IsIdle && Set(ref exactText, value)) ClearComparison(); } }
        public UiCommand DiscoverCommand { get; }
        public UiCommand ReadCommand { get; }
        public UiCommand SearchCommand { get; }
        public UiCommand NextCommand { get; }
        public UiCommand PreviousCommand { get; }
        public UiCommand DetailsCommand { get; }
        public UiCommand LinksCommand { get; }
        public UiCommand CompareCommand { get; }
        public UiCommand DiffNextCommand { get; }
        public UiCommand DiffPreviousCommand { get; }
        public UiCommand DiffDetailsCommand { get; }
        public UiCommand DiagnoseCommand { get; }
        public UiCommand PingCommand { get; }
        public UiCommand IsolatedPingCommand { get; }
        private Task IsolatedPingAsync() => RunAsync("Independent DXL ping...", async (token, progress) =>
        {
            if (session.IsolatedPing == null)
                return (Action)(() => { DiagnosticsText = "NOT_RUN: independent COM ping requires Real mode. Mock is not COM/DXL validation."; });
            var result = await session.IsolatedPing.PingAsync(Version, Connection, cancellationToken: token).ConfigureAwait(false);
            return () => { DiagnosticsText = "PASS DXL_PING_OK; RequestId=" + result.RequestId.ToString("N") +
                "; DurationMs=" + result.Duration.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                ". Token-bound runStr / Result. Client version remains separately verified by diagnostics."; };
        });
        public UiCommand ExportCommand { get; }
        public UiCommand CancelCommand { get; }
        private void Refresh() { foreach (var command in commands) command.Refresh(); CancelCommand?.Refresh(); Tools?.Refresh(); BulkSelection?.Refresh(); BaselineManager?.Refresh(); BaselineTraceability?.Refresh(); ChangeImpact?.Refresh(); Dashboard?.RefreshCommands(); Find?.Refresh(); GoTo?.Refresh(); Changed(nameof(IsIdle)); Changed(nameof(IsBusy)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }

        private async Task RunAsync(string description, Func<CancellationToken, IProgress<string>, Task<Action>> work)
        {
            if (!IsIdle || closing) return;
            busy = true; cancellation = new CancellationTokenSource(); idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var currentCancellation = cancellation; var currentSession = session;
            ErrorDetails = ""; Status = description; ProgressText = description; ProgressValue = 0; ProgressIndeterminate = true; Refresh();
            var progress = new Progress<string>(text => { if (ReferenceEquals(cancellation, currentCancellation) && !disposed) ProgressText = text; });
            try
            {
                // All service invocation, snapshot enumeration and projection happen off the WPF dispatcher.
                var commit = await Task.Run(() => work(currentCancellation.Token, progress));
                // Even while closing, retain returned leases so Dispose can release them after native drain.
                commit?.Invoke();
            }
            catch (OperationCanceledException) { Status = "İptal edildi. Otomatik yeniden deneme yapılmadı."; }
            catch (Exception ex)
            {
                var failure = errors.Handle(ex is ArgumentException || ex is FormatException || ex is OverflowException ?
                    new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Girdi geçersiz; yolları, sayıları ve seçimleri kontrol edin.", ex) : ex, Guid.NewGuid());
                Status = failure.Code + ": " + failure.Message;
                ErrorDetails = Status + "\nRequest=" + failure.CorrelationId.ToString("N") + "\nExceptionType=" + ex.GetType().FullName + "; HResult=0x" + ex.HResult.ToString("X8");
            }
            finally
            {
                string finalStatus = Status;
                try
                {
                    var drain = currentSession.DrainAsync();
                    if (!drain.IsCompleted) { Status = "Orijinal COM çağrısı ve cleanup bekleniyor; yeni işlem başlatılmaz."; ProgressText = Status; }
                    await drain;
                }
                catch (Exception ex) { ErrorDetails += "\nCleanup=" + ex.GetType().FullName; }
                Status = finalStatus; ProgressText = finalStatus; ProgressIndeterminate = false; ProgressValue = 100;
                cancellation.Dispose(); cancellation = null; busy = false; Refresh(); idle.TrySetResult(true);
            }
        }
        public void Cancel()
        { cancellation?.Cancel(); if (IsBusy) { Status = "İptal istendi. Başlamış COM çağrısı zorla kesilmez; tamamlanması beklenir."; ProgressText = Status; } CancelCommand.Refresh(); }
        public Task PrepareCloseAsync() { closing = true; Cancel(); Refresh(); return idle?.Task ?? Task.CompletedTask; }

        private Task DiscoverAsync() => RunAsync("Proje / modül ağacı okunuyor…", async (token, progress) =>
        {
            var scope = Tools.DiscoveryRequest(RootPath);
            DiscoverySnapshot snapshot;
            switch ((DiscoveryKind)Enum.Parse(typeof(DiscoveryKind), Tools.ReadOptions.Text("kind")))
            {
                case DiscoveryKind.Projects: snapshot = await session.Projects.DiscoverProjectsAsync(scope, token).ConfigureAwait(false); break;
                case DiscoveryKind.Folders: snapshot = await session.Projects.DiscoverFoldersAsync(scope, token).ConfigureAwait(false); break;
                case DiscoveryKind.FormalModules: snapshot = await (session.Modules ?? throw new InvalidOperationException("Modül keşif servisi yapılandırılmamış.")).DiscoverFormalModulesAsync(scope, token).ConfigureAwait(false); break;
                case DiscoveryKind.ModuleByPath: snapshot = await (session.Modules ?? throw new InvalidOperationException("Modül keşif servisi yapılandırılmamış.")).GetFormalModuleAsync(scope, token).ConfigureAwait(false); break;
                default: snapshot = await session.Projects.DiscoverHierarchyAsync(scope, token).ConfigureAwait(false); break;
            }
            token.ThrowIfCancellationRequested(); var nodes = ProjectTreeNode.Build(snapshot);
            return () => { discovery = snapshot; Tree = nodes; Status = snapshot.Items.Count + " öğe · " + (snapshot.IsCompleteForVisibleScope ? "görünür kapsam tamamlandı" : "kısmi / erişim kısıtları var"); };
        });
        private string[] AttributeNames() => (CustomAttributes ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length != 0).ToArray();
        private Task ReadAsync() => RunAsync("Formal modül salt okunur açılıyor…", async (token, progress) =>
        {
            var request = Tools.RequirementRequest(ModulePath, AttributeNames());
            if (!await Tools.ApproveAttributeDxl(request, token).ConfigureAwait(false)) return () => Status = "Attribute DXL onaylanmadı; okuma başlatılmadı.";
            if (request.AllowAttributeDxl) await InvalidateBeforeWriteAsync().ConfigureAwait(false);
            var snapshot = await session.Requirements.ReadAsync(request, token).ConfigureAwait(false);
            try
            {
                if (snapshot.Summary.ModuleStatus != RequirementModuleStatus.Available)
                    throw new IntegrationException(IntegrationErrorCode.RequirementProtocolFailure, "Modül okunamadı: " + snapshot.Summary.ModuleStatus);
                var projection = ReadPage(snapshot, 0, token, progress); token.ThrowIfCancellationRequested();
                return (Action)(() => { Release(requirements); requirements = snapshot; Find?.ResetSnapshot(); GoTo?.ResetSnapshot(); ApplyPage(projection, 0); Status = "Salt okunur snapshot: " + snapshot.Summary.ObjectCount + " nesne · complete=" + snapshot.Summary.IsCompleteForReadableObjects; });
            }
            catch { Release(snapshot, false); throw; }
        });
        private Task PageAsync(int requested) => RunAsync("Snapshot aranıyor / sayfa hazırlanıyor…", (token, progress) =>
        {
            var projection = ReadPage(requirements, Math.Max(0, requested), token, progress);
            return Task.FromResult<Action>(() => { ApplyPage(projection, Math.Max(0, requested)); Status = PageLabel; });
        });
        private Tuple<List<RequirementListRow>, int> ReadPage(IRequirementSnapshot snapshot, int requested, CancellationToken token, IProgress<string> progress) => ReadPage(snapshot,requested,Search,Filter,token,progress);
        private Tuple<List<RequirementListRow>, int> ReadPage(IRequirementSnapshot snapshot, int requested,string quickFilter,RequirementFilter objectFilter,CancellationToken token,IProgress<string> progress)
        {
            if ((quickFilter?.Length ?? 0) > 256) throw new ArgumentException("Search limit 256.");
            var found = new List<RequirementListRow>(); int matches = 0, scanned = 0;
            foreach (var record in snapshot.ReadObjects())
            {
                token.ThrowIfCancellationRequested(); if (++scanned % 1000 == 0) progress.Report(scanned + " nesne tarandı");
                if (!InWorkspaceView(record,quickFilter,objectFilter)) continue;
                if (matches >= requested * PageSize && found.Count < PageSize) found.Add(new RequirementListRow(record)); matches++;
            }
            return Tuple.Create(found, matches);
        }
        private static bool Contains(string text, string search) => text?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool InWorkspaceView(RequirementRecord record,string text,RequirementFilter mode) =>
            !((mode==RequirementFilter.Active&&record.IsDeleted)||(mode==RequirementFilter.Deleted&&!record.IsDeleted)) &&
            (string.IsNullOrEmpty(text)||Contains(record.ObjectIdentifier,text)||Contains(record.ObjectNumber,text)||record.Values.Values.Any(v=>v.PlainStatus==RequirementValueStatus.Readable&&Contains(v.PlainText,text)));
        private void ApplyPage(Tuple<List<RequirementListRow>, int> projection, int requested)
        { Requirements = projection.Item1; total = projection.Item2; page = requested; selectedRequirement = null; Changed(nameof(SelectedRequirement)); Attributes = Array.Empty<AttributeDetailRow>(); SelectedAttribute = null; RelatedLinks = Array.Empty<LinkDetailRow>(); Changed(nameof(PageLabel)); }
        private Task DetailsAsync()
        {
            int selected = selectedRequirement.Id;
            return RunAsync("Nesne ayrıntıları okunuyor…", (token, progress) =>
            {
                RequirementRecord record = null;
                foreach (var candidate in requirements.ReadObjects()) { token.ThrowIfCancellationRequested(); if (candidate.AbsoluteNumber == selected) { record = candidate; break; } }
                if (record == null) throw new IntegrationException(IntegrationErrorCode.RequirementProtocolFailure, "Nesne snapshot içinde bulunamadı.");
                var values = record.Values.Values.Select(value => new AttributeDetailRow(value)).ToArray();
                var relatedRows = new List<LinkDetailRow>();
                if (links != null) foreach (var link in links.ReadLinks())
                {
                    token.ThrowIfCancellationRequested();
                    if ((link.Source.ModulePath == requirements.Summary.Request.ModulePath && link.Source.AbsoluteNumber == selected) ||
                        (link.Target.ModulePath == requirements.Summary.Request.ModulePath && link.Target.AbsoluteNumber == selected))
                        if (relatedRows.Count < 100) relatedRows.Add(new LinkDetailRow(link));
                }
                return Task.FromResult<Action>(() => { Attributes = values; SelectedAttribute = values.FirstOrDefault(); RelatedLinks = relatedRows;
                    Status = "Object ID " + selected + " · salt okunur ayrıntı · link listesi en çok 100 satır"; });
            });
        }
        private Task LinksAsync() => RunAsync("Incoming / outgoing linkler okunuyor…", async (token, progress) =>
        {
            var snapshot = await session.Links.ReadAsync(Tools.LinkRequest(ModulePath, LinkPath), token).ConfigureAwait(false);
            try
            {
                if (snapshot.Summary.ModuleStatus != RequirementModuleStatus.Available || snapshot.Summary.LinkModuleStatus != LinkModuleStatus.Available)
                    throw new IntegrationException(IntegrationErrorCode.LinkProtocolFailure, "Link kapsamı okunamadı: " + snapshot.Summary.ModuleStatus + " / " + snapshot.Summary.LinkModuleStatus);
                var graphRows = new List<LinkDetailRow>();
                foreach (var link in snapshot.ReadLinks()) { token.ThrowIfCancellationRequested(); if (graphRows.Count < 150) graphRows.Add(new LinkDetailRow(link)); }
                token.ThrowIfCancellationRequested();
                string summary = snapshot.Summary.RecordCount + " kayıt · graph ilk " + graphRows.Count + " kayıt · kapsam complete=" + snapshot.Summary.IsCompleteForReadableScope +
                    " · unresolved=" + snapshot.Summary.UnresolvedLinkCount + " · incoming references=" + snapshot.Summary.SourceReferenceCount;
                return (Action)(() => { Release(links); links = snapshot; GraphLinks = graphRows; GraphDetail = "Bir graph ilişkisi seçin."; LinkSummary = summary; RelatedLinks = Array.Empty<LinkDetailRow>(); Status = summary; });
            }
            catch { Release(snapshot, false); throw; }
        });
        private static int Number(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        private Task CompareAsync() => RunAsync("İki baseline karşılaştırılıyor…", async (token, progress) =>
        {
            var request = new BaselineComparisonRequest(ModulePath, new RequirementBaseline(Number(BeforeMajor), Number(BeforeMinor), BeforeSuffix),
                new RequirementBaseline(Number(AfterMajor), Number(AfterMinor), AfterSuffix), AttributeNames(),
                ExactText ? BaselineTextNormalization.Exact : BaselineTextNormalization.UnicodeNfcAndLineEndings, target: Version, connectionMode: Connection);
            var result = await session.Baselines.CompareAsync(request, token).ConfigureAwait(false);
            try
            {
                var projected = DiffPage(result, 0, token); token.ThrowIfCancellationRequested();
                string summary = string.Join(" · ", result.Summary.Counts.Select(value => value.Key + "=" + value.Value)) + " · conclusive=" + result.Summary.IsConclusive;
                return (Action)(() => { Release(comparison); comparison = result; DiffRows = projected; diffPage = 0; DiffSummary = summary; DiffDetails = "Bir nesne seçip ayrıntısını açın. Ham rich veri export içinde korunur."; Changed(nameof(DiffPageLabel)); Status = summary; });
            }
            catch { Release(result, false); throw; }
        });
        private static List<BaselineDiffRow> DiffPage(IBaselineComparisonResult result, int requested, CancellationToken token) =>
            result.ReadChanges(token).Skip(requested * PageSize).Take(PageSize).Select(value => new BaselineDiffRow(value)).ToList();
        private Task DiffPageAsync(int requested) => RunAsync("Değişiklik sayfası hazırlanıyor…", (token, progress) =>
        { var projected = DiffPage(comparison, requested, token); return Task.FromResult<Action>(() => { DiffRows = projected; diffPage = requested; Changed(nameof(DiffPageLabel)); Status = DiffSummary; }); });
        private Task DiffDetailsAsync()
        {
            int selected = selectedDiff.Id;
            return RunAsync("Alan bazlı baseline farkları okunuyor…", (token, progress) =>
            {
                var change = comparison.ReadChanges(token).First(value => value.Identity.AbsoluteNumber == selected);
                var text = new StringBuilder().AppendLine(change.Identity.Key + " · " + change.Status + " · conclusive=" + change.IsConclusive).AppendLine("Hierarchy changed=" + change.HierarchyChanged);
                foreach (var value in change.Attributes)
                    text.AppendLine("\n" + value.Name + " · " + value.Status + " · plain=" + value.PlainTextChanged + " rich=" + value.RichTextChanged + " type=" + value.TypeChanged + " normalized=" + value.HasNormalizationDifference)
                        .AppendLine("ÖNCE [" + value.Before?.PlainStatus + "] " + RequirementListRow.Clip(value.Before?.PlainText, 4096))
                        .AppendLine("SONRA [" + value.After?.PlainStatus + "] " + RequirementListRow.Clip(value.After?.PlainText, 4096));
                return Task.FromResult<Action>(() => { DiffDetails = text.ToString(); Status = "Baseline alan ayrıntıları hazır."; });
            });
        }
        private Task DiagnoseAsync(bool live) => RunAsync(live ? "Salt okunur ping / tanılama…" : "Offline COM kaydı tanılanıyor…", async (token, progress) =>
        {
            var report = await sta.RunAsync(() => session.Health.Run(Version, Guid.NewGuid(), live, Connection, step => progress.Report(step.Step + " · " + step.Status)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var text = new StringBuilder().AppendLine(ProfileDescription).AppendLine("Request=" + report.CorrelationId.ToString("N") + "; ExitCode=" + report.ExitCode)
                .AppendLine("OS64=" + report.Environment.Is64BitOperatingSystem + "; Process64=" + report.Environment.Is64BitProcess + "; Apartment=" + report.Environment.Apartment);
            foreach (var registration in report.Environment.Registrations) text.AppendLine(registration.Architecture + " " + registration.State + " " + registration.FileVersion + " · " + registration.ServerPath);
            foreach (var step in report.Steps) text.AppendLine(step.Step + " " + step.Status + " " + step.ErrorCode + " · " + step.Message);
            return () => { HealthSteps = report.Steps; DiagnosticsText = text.ToString(); Status = "Tanılama tamamlandı · ExitCode=" + report.ExitCode; };
        });
        private Task ExportAsync() => RunAsync("Export hazırlanıyor…", async (token, progress) =>
        {
            IExportSource source;
            switch (ExportScope)
            {
                case ClientExportScope.BaselineDiff: if (comparison == null) throw new ArgumentException(); source = new BaselineComparisonExportSource(comparison); break;
                case ClientExportScope.Requirements: if (requirements == null) throw new ArgumentException(); source = new SnapshotExportSource(requirements: new[] { requirements }); break;
                case ClientExportScope.Links: if (links == null) throw new ArgumentException(); source = new SnapshotExportSource(links: new[] { links }); break;
                default: source = new SnapshotExportSource(discovery == null ? null : new[] { discovery }, requirements == null ? null : new[] { requirements }, links == null ? null : new[] { links }); break;
            }
            string destination = Path.Combine(Path.GetFullPath(ExportRoot), "Wpf-" + ExportScope + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var adapter = new ExportProgressAdapter(progress);
            var result = await session.Export.ExportAsync(Tools.ExportRequest(destination, ExportFormat), source, adapter, token).ConfigureAwait(false);
            return () => { LastExportPath = result.DirectoryPath; Status = "Export " + result.Status + " · " + result.RecordsAccepted + " kayıt · " + result.IssueCount + " issue · " + result.ReportPath; };
        });
        private bool CanExport() => ExportScope == ClientExportScope.BaselineDiff ? comparison != null : ExportScope == ClientExportScope.Requirements ? requirements != null :
            ExportScope == ClientExportScope.Links ? links != null : discovery != null || requirements != null || links != null;
        private sealed class ExportProgressAdapter : IProgress<ExportProgress>
        {
            private readonly IProgress<string> progress;
            public ExportProgressAdapter(IProgress<string> progress) { this.progress = progress; }
            public void Report(ExportProgress value) => progress.Report(value.Phase + " · read=" + value.RecordsRead + " · written=" + value.RowsWritten + " · issues=" + value.IssueCount);
        }
        private void ChangeProfile()
        { ClearModule(); discovery = null; Tree = Array.Empty<ProjectTreeNode>(); HealthSteps = Array.Empty<DoorsHealthStepResult>(); DiagnosticsText = "Profil değişti; yeniden tanılayın.";
            Tools.InvalidateContext(); readOnly = true; Changed(nameof(IsReadOnly)); Changed(nameof(ModeLabel));
            LastExportPath = ""; DashboardContext = ""; BaselineManager?.InvalidateApproval(); session = factory.Create(Profile, logger); BulkSelection.ResetConnection(); BaselineManager?.ResetConnection(); BaselineTraceability?.Reset(); ChangeImpact?.ResetConnection(); Dashboard?.ResetConnection(); Changed(nameof(ProfileDescription)); Status = ProfileDescription; Refresh(); }
        private void ClearLinks() { GraphLinks = Array.Empty<LinkDetailRow>(); RelatedLinks = Array.Empty<LinkDetailRow>(); GraphDetail = "Bir graph ilişkisi seçin."; LinkSummary = "Link kapsamı henüz okunmadı."; Refresh(); }
        private void ClearComparison() { Release(comparison); comparison = null; DiffRows = Array.Empty<BaselineDiffRow>(); selectedDiff = null; Changed(nameof(SelectedDiff)); DiffDetails = ""; DiffSummary = "Girdi değişti; yeniden karşılaştırın."; Refresh(); }
        private void ClearModule()
        {
            Release(requirements); requirements = null; Release(links); links = null; Requirements = Array.Empty<RequirementListRow>();
            Find?.ResetSnapshot(); GoTo?.ResetSnapshot();
            Attributes = Array.Empty<AttributeDetailRow>(); SelectedAttribute = null; selectedRequirement = null; Changed(nameof(SelectedRequirement)); total = 0; page = 0;
            Changed(nameof(PageLabel)); ClearLinks(); ClearComparison();
        }
        private void Release(IDisposable resource, bool report = true)
        {
            if (resource == null) return;
            try { resource.Dispose(); }
            catch (Exception ex)
            {
                try { logger.Write(IntegrationLogLevel.Error, "WpfSnapshotCleanupFailed", Guid.NewGuid(), "ExceptionType=" + ex.GetType().FullName); } catch { }
                // Worker cleanup preserves its original error. UI cleanup clears stale evidence even if local deletion failed.
                if (report) ErrorDetails = "Yerel snapshot temizlenemedi; eski sonuçlar görünümden kaldırıldı. ExceptionType=" + ex.GetType().FullName;
            }
        }
        public void Dispose() { if (disposed) return; if (busy) throw new InvalidOperationException("Await PrepareCloseAsync before disposing."); BaselineManager.Dispose(); Tools.Dispose(); ClearModule(); disposed = true; Refresh(); }
    }
}
