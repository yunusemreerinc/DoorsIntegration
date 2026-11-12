using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed class BaselineFolderNode : ObservableModel
    {
        private readonly BulkBaselineManagerViewModel manager;
        private bool chosen, recursive = true, expanded, loaded;
        internal BaselineFolderNode(DoorsDiscoveryItem item, BulkBaselineManagerViewModel manager)
        {
            Item = item; this.manager = manager;
            LoadCommand = new UiCommand(() => manager.LoadChildren(this), () => manager.IsIdle && CanChoose && !loaded);
        }
        public DoorsDiscoveryItem Item { get; }
        public string Path => Item.FullPath;
        public string Label => Item.Name + (CanChoose ? "" : " · " + Item.AccessStatus);
        public bool CanChoose => Item.AccessStatus == DoorsAccessStatus.Readable;
        public ObservableCollection<BaselineFolderNode> Children { get; } = new ObservableCollection<BaselineFolderNode>();
        public UiCommand LoadCommand { get; }
        public bool IsSelected { get => chosen; set { if (manager.IsIdle && CanChoose && Set(ref chosen, value)) manager.ScopeChanged(this); } }
        public bool IncludeSubfolders { get => recursive; set { if (manager.IsIdle && Set(ref recursive, value) && IsSelected) manager.ScopeChanged(this); } }
        public bool IsExpanded { get => expanded; set { if (Set(ref expanded, value) && value && !loaded) LoadCommand.Execute(null); } }
        internal void SetChildren(IEnumerable<BaselineFolderNode> nodes) { loaded = true; Children.Clear(); foreach (var node in nodes) Children.Add(node); LoadCommand.Refresh(); }
        internal void Refresh() { LoadCommand.Refresh(); foreach (var child in Children) child.Refresh(); }
    }
    public sealed class BaselineManagerModuleRow
    {
        private readonly Action<string, bool> select;
        private bool selected;
        internal BaselineManagerModuleRow(ModuleSelectionCandidate item, BulkBaselineDryRunItem plan, BaselinePreflightFacts facts, BulkBaselineJobModule result, Action<string, bool> select)
        { Candidate = item; Plan = plan; Facts = facts; Result = result; this.select = select; selected = item.IsSelected; }
        public ModuleSelectionCandidate Candidate { get; }
        public BulkBaselineDryRunItem Plan { get; }
        public BaselinePreflightFacts Facts { get; }
        public BulkBaselineJobModule Result { get; }
        public bool CanSelect => Candidate.CanSelect;
        public bool IsSelected { get => selected; set { if (CanSelect && value != selected) { selected = value; select(Candidate.Module.ModuleId, value); } } }
        public string ModuleName => Candidate.Name;
        public string FullPath => Candidate.FullPath;
        public string LastVersion => Result?.EverSucceeded == true ? Result.ExpectedVersion.ToString() : Candidate.Metadata.LatestBaseline?.ToString() ?? Candidate.Metadata.BaselineStatus.ToString();
        public string TargetVersion => Plan?.ExpectedVersion?.ToString() ?? Result?.ExpectedVersion?.ToString() ?? "—";
        public string LockStatus => Facts == null ? "Unknown" : Facts.LockState + " / " + Facts.EditState;
        public string Eligibility => Result?.EverSucceeded == true ? "Succeeded · no replay" : Plan == null ? Candidate.CanSelect ? "Preflight required" : Candidate.Access.ToString() : Plan.Status + " / " + Plan.ReasonCode;
        public string Details => FullPath + "\nID=" + Candidate.Module?.ModuleId + "\n" + Plan?.Reason + "\nÖneri: " + Plan?.RecommendedResolution + "\nWarning: " + Plan?.Warning;
    }
    public sealed class BaselineManagerProgressRow
    {
        public BaselineManagerProgressRow(BulkBaselineJobModule module) { Module = module; }
        public BulkBaselineJobModule Module { get; }
        public string Name => DoorsItemPath.LeafName(Module.CanonicalFullPath);
        public string FullPath => Module.CanonicalFullPath;
        public string Previous => Module.CurrentVersion?.ToString() ?? "None";
        public string Target => Module.ExpectedVersion?.ToString() ?? "Unavailable";
        public BulkBaselineModuleStatus Status => Module.Status;
        public string Error => Module.FailureReason;
        public string Duration => !Module.StartedAtUtc.HasValue ? "—" : ((Module.CompletedAtUtc ?? DateTime.UtcNow) - Module.StartedAtUtc.Value).TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s";
    }
    public sealed class BulkBaselineManagerViewModel : ObservableModel, IDisposable
    {
        private readonly WorkspaceViewModel owner;
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private readonly DispatcherTimer timer;
        private readonly List<UiCommand> commands = new List<UiCommand>();
        private readonly Dictionary<string, BaselineFolderNode> scopes = new Dictionary<string, BaselineFolderNode>(StringComparer.Ordinal);
        private ModuleSelectionSession selection;
        private BaselinePreflightRevalidation dryRun;
        private Dictionary<string, BaselinePreflightFacts> facts = new Dictionary<string, BaselinePreflightFacts>();
        private IReadOnlyList<BaselineManagerModuleRow> rows = Array.Empty<BaselineManagerModuleRow>();
        private IReadOnlyList<BaselineManagerProgressRow> progressRows = Array.Empty<BaselineManagerProgressRow>();
        private IReadOnlyList<BulkBaselineJobRecord> history = Array.Empty<BulkBaselineJobRecord>();
        private BulkBaselineJobRecord job, selectedHistory;
        private Guid? resumeId;
        private BaselineManagerProgressRow selectedProgress;
        private string repository, rootPath = "/", search = "", suffix = "", annotation = "", report = "Klasör ağacını yükleyin; klasörleri işaretleyin ve modülleri keşfedin.", exportRoot;
        private bool selectedOnly;
        private BaselineType type = BaselineType.Minor;
        private ExecutionPolicy policy = ExecutionPolicy.ContinueOnError;
        private int runGeneration;
        private int selectedPane;
        private TimeSpan elapsed;
        private string historySearch = "", historyOperator = "", historyDatabase = "", historyOutcome = "All";
        private DateTime? historyFrom, historyTo;
        private ExportFormat exportFormat = ExportFormat.Json;
        private BulkBaselineReport auditReport;
        private IReadOnlyList<BaselineJobHistoryItem> historyItems = Array.Empty<BaselineJobHistoryItem>();
        private BaselineJobHistoryItem selectedHistoryItem;
        private const int HistoryPageSize = 100;
        private int historyOffset, historyMatched;
        private bool historyCriteriaChanged;
        public string HistoryPageLabel => historyCriteriaChanged ? "Filters changed · refresh history" : History.Count == 0 ? "0 / " + historyMatched : (historyOffset + 1) + "–" + (historyOffset + History.Count) + " / " + historyMatched;
        private void HistoryFilterChanged() { historyCriteriaChanged = true; Changed(nameof(HistoryPageLabel)); Refresh(); }
        private void ClearAuditHistory()
        { HistoryItems = Array.Empty<BaselineJobHistoryItem>(); SelectedHistoryItem = null; historyOffset = historyMatched = 0; historyCriteriaChanged = false; auditReport = null; Changed(nameof(HistoryPageLabel)); Changed(nameof(AuditSummary)); Changed(nameof(AuditModules)); Changed(nameof(AuditContextSummary)); }
        public BulkBaselineManagerViewModel(WorkspaceViewModel owner)
        {
            this.owner = owner; repository = MockDoorsAdapter.BaselineRepositoryId;
            Native = new NativeBaselineSetViewModel(owner, this);
            exportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DoorsIntegration", "BaselineResults");
            LoadTreeCommand = Command(LoadTree);
            DiscoverCommand = Command(() => LoadModules(false), () => scopes.Count > 0);
            LoadMoreCommand = Command(() => LoadModules(false), () => selection?.HasMore == true);
            RefreshCommand = Command(() => LoadModules(true), () => scopes.Count > 0);
            SelectAllCommand = Command(() => SelectVisible(true), () => selection != null);
            ClearSelectionCommand = Command(() => { selection.DeselectAll(); InvalidatePlan(); Project(); return Task.CompletedTask; }, () => selection != null);
            SelectEligibleCommand = Command(SelectEligible, () => selection != null && dryRun != null);
            PreflightCommand = Command(Preflight, () => SelectedModules().Count > 0 && Validation.Length == 0);
            ReviewCommand = Command(Review, () => AllEligible && !owner.IsReadOnly && Runtime.CanWrite);
            ExecuteCommand = Command(Execute, () => AllEligible && !owner.IsReadOnly && Runtime.CanWrite && Runtime.Approval.IsApproved && Runtime.Approval.Fingerprint == dryRun.Plan.Fingerprint);
            CancelRemainingCommand = new UiCommand(() => { owner.Cancel(); return Task.CompletedTask; }, () => owner.CancelCommand.CanExecute(null) && !owner.IsClosing);
            OpenHistoryCommand = Command(OpenHistory);
            PreviousHistoryPageCommand = Command(() => QueryHistory(Math.Max(0, historyOffset - HistoryPageSize)), () => !historyCriteriaChanged && historyOffset > 0);
            NextHistoryPageCommand = Command(() => QueryHistory(historyOffset + HistoryPageSize), () => !historyCriteriaChanged && historyOffset + HistoryPageSize < historyMatched);
            ReportHistoryCommand = Command(ReadHistoryReport, () => SelectedHistory != null);
            LoadJobCommand = Command(LoadJob, () => SelectedHistory != null);
            ReconcileCommand = Command(Reconcile, () => job != null);
            RetryCommand = Command(Retry, () => !owner.IsReadOnly && Runtime.CanWrite && SelectedProgress != null && !SelectedProgress.Module.EverSucceeded &&
                (SelectedProgress.Status == BulkBaselineModuleStatus.Failed || SelectedProgress.Status == BulkBaselineModuleStatus.Skipped || SelectedProgress.Status == BulkBaselineModuleStatus.NeedsReconciliation || SelectedProgress.Status == BulkBaselineModuleStatus.Unknown));
            ExportResultCommand = Command(Export, () => job != null && !string.IsNullOrWhiteSpace(ExportRoot));
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += OnTick; timer.Start();
        }
        private BaselineManagerSession Runtime => owner.Session.BaselineManager;
        public bool IsIdle => owner.IsIdle && !owner.IsClosing;
        public bool IsSupported => Runtime != null && Runtime.Mode == owner.Mode && Runtime.Version == owner.Version;
        public bool IsReal => owner.Mode == ClientMode.RealDoors;
        public string ModeBanner => IsReal ? "REAL DOORS · GERÇEK VERİTABANI · İKİNCİ ONAY GEREKİR" : "MOCK · SİMÜLE BASELINE · GERÇEK DOORS DEĞİŞMEZ";
        public string Limitation => IsSupported ? Runtime.Limitation : "Bu bağlantı factory'si Bulk Baseline Manager servisi sağlamıyor; yazma kapalı.";
        public string ReadOnlyLabel => owner.IsReadOnly ? "SALT OKUNUR · Execute kapalı. Yazma için üstteki salt okunur modunu kapatıp planı onaylayın." : "YAZMA MODU · her batch kesin onay gerektirir";
        public ObservableCollection<BaselineFolderNode> Tree { get; } = new ObservableCollection<BaselineFolderNode>();
        public IReadOnlyList<BaselineManagerModuleRow> Rows { get => rows; private set => Set(ref rows, value); }
        public IReadOnlyList<BaselineManagerProgressRow> ProgressRows { get => progressRows; private set => Set(ref progressRows, value); }
        public IReadOnlyList<BulkBaselineJobRecord> History { get => history; private set => Set(ref history, value); }
        public BulkBaselineJobRecord CurrentJob => job;
        public Array ExportFormats => Enum.GetValues(typeof(ExportFormat));
        public ExportFormat ExportFormat { get => exportFormat; set { if (IsIdle) Set(ref exportFormat, value); } }
        public string HistorySearch { get => historySearch; set { if (IsIdle && Set(ref historySearch, value)) HistoryFilterChanged(); } }
        public string HistoryOperator { get => historyOperator; set { if (IsIdle && Set(ref historyOperator, value)) HistoryFilterChanged(); } }
        public string HistoryDatabase { get => historyDatabase; set { if (IsIdle && Set(ref historyDatabase, value)) HistoryFilterChanged(); } }
        public string[] HistoryOutcomes => new[] { "All", "Success", "PartialSuccess", "Failed", "Skipped", "Indeterminate" };
        public string HistoryOutcome { get => historyOutcome; set { if (IsIdle && Set(ref historyOutcome, value)) HistoryFilterChanged(); } }
        public DateTime? HistoryFrom { get => historyFrom; set { if (IsIdle && Set(ref historyFrom, value)) HistoryFilterChanged(); } }
        public DateTime? HistoryTo { get => historyTo; set { if (IsIdle && Set(ref historyTo, value)) HistoryFilterChanged(); } }
        public IReadOnlyList<BaselineJobHistoryItem> HistoryItems { get => historyItems; private set => Set(ref historyItems, value); }
        public BaselineJobHistoryItem SelectedHistoryItem { get => selectedHistoryItem; set { Set(ref selectedHistoryItem, value); SelectedHistory = value?.Snapshot; } }
        public string AuditDisclosure => BulkBaselineReport.Disclosure;
        public string AuditContextSummary => auditReport == null ? "" : "JobId=" + auditReport.JobId + " · Operator=" + auditReport.Context.Operator + " · Database=" + auditReport.Context.DatabaseIdentifier + "\nEnvironment=" + auditReport.Context.EnvironmentIdentifier + " · Client=" + auditReport.Context.ClientVersion + " · " + auditReport.Context.ClientVersionEvidence + "\nStart=" + auditReport.StartedAtUtc?.ToString("O") + " · End=" + auditReport.EndedAtUtc?.ToString("O") + " · Coverage=" + auditReport.AuditCoverage;
        public string AuditSummary => auditReport == null ? "Audit report not loaded" : auditReport.Outcome + " · Success=" + auditReport.SuccessCount + " · Failed=" + auditReport.FailedCount + " · Skipped=" + auditReport.SkippedCount + " (Locked=" + auditReport.LockedCount + ") · Unknown=" + auditReport.UnknownCount;
        public IReadOnlyList<BaselineAuditModuleState> AuditModules => auditReport?.Modules.OrderBy(m => m.Group).ToArray() ?? Array.Empty<BaselineAuditModuleState>();
        public int SelectedPane { get => selectedPane; set => Set(ref selectedPane, value); }
        internal void ApplyDashboardContext(DashboardNavigationIntent intent,string detail)
        {
            InvalidateApproval();
            if(intent.Destination==DashboardDestination.BulkBaseline||intent.Destination==DashboardDestination.JobHistory) { ResetConnection(); repository=intent.DatabaseIdentity; rootPath=intent.Scope?.SetFolder??intent.Scope?.Projects.FirstOrDefault()??"/"; report=detail; Changed(nameof(RepositoryId)); Changed(nameof(RootPath)); Changed(nameof(Report)); historySearch=intent.JobId??""; historyDatabase=intent.Environment; Changed(nameof(HistorySearch)); Changed(nameof(HistoryDatabase)); }
        }
        public BulkBaselineJobRecord SelectedHistory { get => selectedHistory; set { Set(ref selectedHistory, value); Refresh(); } }
        public BaselineManagerProgressRow SelectedProgress { get => selectedProgress; set { Set(ref selectedProgress, value); Refresh(); } }
        public string RepositoryId { get => repository; set { if (IsIdle && Set(ref repository, value)) { ResetSelection(); job = null; History = Array.Empty<BulkBaselineJobRecord>(); SelectedHistory = null; ClearAuditHistory(); ProgressRows = Array.Empty<BaselineManagerProgressRow>(); Changed(nameof(CurrentJob)); Changed(nameof(JobSummary)); Changed(nameof(OverallProgress)); } } }
        public string RootPath { get => rootPath; set { if (IsIdle) Set(ref rootPath, value); } }
        public string Search { get => search; set { if (IsIdle && Set(ref search, value)) Project(); } }
        public bool SelectedOnly { get => selectedOnly; set { if (IsIdle && Set(ref selectedOnly, value)) Project(); } }
        public Array BaselineTypes => Enum.GetValues(typeof(BaselineType));
        public Array Policies => Enum.GetValues(typeof(ExecutionPolicy));
        public BaselineType BaselineType { get => type; set { if (IsIdle && Set(ref type, value)) InvalidatePlan(); } }
        public ExecutionPolicy Policy { get => policy; set { if (IsIdle && Set(ref policy, value)) InvalidatePlan(); } }
        public string Suffix { get => suffix; set { if (IsIdle && Set(ref suffix, value)) InvalidatePlan(); } }
        public string Annotation { get => annotation; set { if (IsIdle && Set(ref annotation, value)) InvalidatePlan(); } }
        public string ExportRoot { get => exportRoot; set { if (IsIdle) { Set(ref exportRoot, value); Refresh(); } } }
        public string Report { get => report; private set => Set(ref report, value); }
        public string Summary => (selection == null ? "0 yüklü / 0 seçili" : selection.Snapshot().Candidates.Count + " yüklü / " + selection.SelectedCount + " seçili · devam=" + selection.HasMore) + " · " + scopes.Count + " klasör";
        public string JobSummary => job == null ? "Job yok" : "JobId=" + job.JobId + " · " + job.Status + " · " + job.Modules.Count(m => m.Status == BulkBaselineModuleStatus.Succeeded) + "/" + job.Modules.Count + " başarılı · elapsed " + elapsed.TotalSeconds.ToString("F1") + " s";
        public double OverallProgress => job == null ? 0 : job.Modules.Count(m => m.Status == BulkBaselineModuleStatus.Succeeded || m.Status == BulkBaselineModuleStatus.Failed || m.Status == BulkBaselineModuleStatus.Skipped || m.Status == BulkBaselineModuleStatus.NeedsReconciliation) * 100.0 / job.Modules.Count;
        public string ApprovalLabel => IsSupported && Runtime.Approval.IsApproved ? "ONAYLANDI · Execute hazır · " + Runtime.Approval.ExpiresAtUtc.ToString("HH:mm:ss 'UTC'") : "ONAY YOK · Execute disabled";
        public string Validation
        {
            get
            {
                try { var valid = new BulkBaselineRequest(new[] { new BaselineModuleSelection(new BaselineModuleReference("validation", "input", "/input")) }, BaselineType, suffix: Suffix, annotation: Annotation); return ""; }
                catch (Exception) { return "Suffix/Annotation geçersiz: suffix ≤128; annotation ≤16384; geçerli Unicode, yasak kontrol karakteri yok."; }
            }
        }
        public bool AllEligible => IsSupported && dryRun != null && dryRun.Items.Count > 0 && dryRun.EligibleCount == dryRun.Items.Count && DateTime.UtcNow < dryRun.Plan.CreatedAtUtc + dryRun.Plan.Request.MaxPlanAge &&
            dryRun.Items.All(i => i.Fresh.Precondition != null && DateTime.UtcNow >= i.Fresh.Precondition.ObservedAtUtc && DateTime.UtcNow - i.Fresh.Precondition.ObservedAtUtc <= dryRun.Plan.Request.MaxObservationAge) &&
            (!resumeId.HasValue || job == null || !job.Modules.Any(m => m.Status == BulkBaselineModuleStatus.NeedsReconciliation || m.Status == BulkBaselineModuleStatus.Unknown));
        public UiCommand LoadTreeCommand { get; }
        public UiCommand DiscoverCommand { get; }
        public UiCommand LoadMoreCommand { get; }
        public UiCommand RefreshCommand { get; }
        public UiCommand SelectAllCommand { get; }
        public UiCommand ClearSelectionCommand { get; }
        public UiCommand SelectEligibleCommand { get; }
        public UiCommand PreflightCommand { get; }
        public UiCommand ReviewCommand { get; }
        public UiCommand ExecuteCommand { get; }
        public UiCommand CancelRemainingCommand { get; }
        public UiCommand OpenHistoryCommand { get; }
        public UiCommand PreviousHistoryPageCommand { get; }
        public UiCommand NextHistoryPageCommand { get; }
        public UiCommand ReportHistoryCommand { get; }
        public UiCommand LoadJobCommand { get; }
        public UiCommand ReconcileCommand { get; }
        public UiCommand RetryCommand { get; }
        public UiCommand ExportResultCommand { get; }
        private UiCommand Command(Func<Task> action, Func<bool> available = null)
        { var command = new UiCommand(action, () => IsIdle && IsSupported && (available?.Invoke() ?? true)); commands.Add(command); return command; }
        public NativeBaselineSetViewModel Native { get; }
        private BaselineManagerMode mode;
        public Array Modes => Enum.GetValues(typeof(BaselineManagerMode));
        public BaselineManagerMode Mode { get => mode; set { if (IsIdle && Set(ref mode, value)) { InvalidatePlan(); Changed(nameof(IsNative)); } } }
        public bool IsNative => Mode == BaselineManagerMode.NativeBaselineSet;
        internal BaselineModuleReference[] NativeModules() => (selection?.GetSelectedModules() ?? Array.Empty<BaselineModuleSelection>()).Select(m => m.Module).ToArray();
        public void Refresh()
        { Native?.Refresh(); foreach (var command in commands) command.Refresh(); CancelRemainingCommand?.Refresh(); foreach (var node in Tree) node.Refresh(); Changed(nameof(IsIdle)); Changed(nameof(IsReal)); Changed(nameof(ModeBanner)); Changed(nameof(Limitation)); Changed(nameof(ReadOnlyLabel)); Changed(nameof(ApprovalLabel)); }
        private void OnTick(object sender, EventArgs args) { Refresh(); }
        public void InvalidateApproval() { Native?.Invalidate(); Runtime?.Approval.Clear(); Changed(nameof(ApprovalLabel)); Refresh(); }
        private void InvalidatePlan() { Native?.Invalidate(); InvalidateApproval(); dryRun = null; facts.Clear(); resumeId = null; Changed(nameof(Validation)); Project(); }
        private void ResetSelection() { selection = null; InvalidatePlan(); Project(); }
        public void ResetConnection()
        { Native?.Reset(); runGeneration++; Runtime?.Approval.Clear(); scopes.Clear(); Tree.Clear(); selection = null; dryRun = null; facts.Clear(); job = null; resumeId = null; History = Array.Empty<BulkBaselineJobRecord>(); SelectedHistory = null; ClearAuditHistory(); ProgressRows = Array.Empty<BaselineManagerProgressRow>(); repository = IsReal ? "" : MockDoorsAdapter.BaselineRepositoryId; Changed(nameof(RepositoryId)); Project(); Changed(nameof(JobSummary)); Changed(nameof(OverallProgress)); Refresh(); }
        internal void ScopeChanged(BaselineFolderNode node)
        { if (node.IsSelected) scopes[node.Path] = node; else scopes.Remove(node.Path); ResetSelection(); }
        private Task LoadTree()
        {
            var runtime = Runtime; var path = RootPath; var version = owner.Version; var connection = owner.Connection;
            return owner.RunToolAsync("Bulk baseline klasör ağacı", async (token, p) =>
            {
                var snapshot = await runtime.Hierarchy.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope(path, false, target: version, connectionMode: connection), token).ConfigureAwait(false);
                return () =>
                {
                    // Explicit scope refresh clears scope selection; search/filter never does.
                    scopes.Clear(); ResetSelection(); Tree.Clear();
                    var containers = snapshot.Items.Where(IsContainer).ToArray(); var root = containers.FirstOrDefault(i => i.FullPath == DoorsItemPath.Normalize(path));
                    if (root != null) { var node = new BaselineFolderNode(root, this); node.SetChildren(containers.Where(i => i.FullPath != root.FullPath).Select(i => new BaselineFolderNode(i, this))); Tree.Add(node); }
                    Report = "Klasör kutularını işaretleyin. Alt klasörler ayrı seçilebilir; çocukları yüklemek için + kullanın.";
                };
            });
        }
        internal Task LoadChildren(BaselineFolderNode node)
        {
            var runtime = Runtime; var version = owner.Version; var connection = owner.Connection;
            return owner.RunToolAsync("Alt klasörler yükleniyor", async (token, p) =>
            { var snapshot = await runtime.Hierarchy.DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope(node.Path, false, target: version, connectionMode: connection), token).ConfigureAwait(false);
                return () => node.SetChildren(snapshot.Items.Where(i => IsContainer(i) && i.FullPath != node.Path).Select(i => new BaselineFolderNode(i, this))); });
        }
        private static bool IsContainer(DoorsDiscoveryItem item) => item.Type == DoorsItemKind.DatabaseRoot || item.Type == DoorsItemKind.Project || item.Type == DoorsItemKind.Folder;
        private Task LoadModules(bool refresh)
        {
            var runtime = Runtime; var repository = RepositoryId; var folders = scopes.Values.Select(n => new ModuleSelectionFolder(n.Path, n.IncludeSubfolders)).ToArray(); var version = owner.Version; var connection = owner.Connection;
            var cursor = selection; var profile = refresh && cursor != null ? cursor.CreateProfile("Manager refresh") : null;
            InvalidatePlan();
            return owner.RunToolAsync("Formal modüller keşfediliyor", async (token, p) =>
            {
                var request = new ModuleSelectionRequest(repository, folders, version, connection);
                if (cursor == null || refresh) cursor = runtime.Selection.Create(request, profile);
                var snapshot = await cursor.LoadNextAsync(100, 5, token).ConfigureAwait(false);
                return () => { selection = cursor; Report = "Formal modüller: " + snapshot.Candidates.Count + " · kısmi=" + snapshot.IsIncomplete + " · pending profile=" + snapshot.PendingProfileSelections + "\n" + string.Join("\n", snapshot.Issues.Take(5)); Project(); };
            });
        }
        private void SelectOne(string id, bool value) { if (!IsIdle) return; selection.SetSelected(id, value); InvalidatePlan(); Project(); }
        private Task SelectVisible(bool value) { selection.SelectFiltered(value, new ModuleSelectionFilter(Search, selectedOnly: SelectedOnly)); InvalidatePlan(); Project(); return Task.CompletedTask; }
        private Task SelectEligible()
        { var ids = new HashSet<string>(dryRun.Items.Where(i => i.EligibleForPreview).Select(i => i.Original.ModuleId)); selection.DeselectAll(); foreach (var id in ids) selection.SetSelected(id, true); InvalidatePlan(); Project(); return Task.CompletedTask; }
        private void Project()
        {
            var plan = dryRun?.Items.ToDictionary(i => i.Fresh.ModuleId, i => i.Fresh) ?? new Dictionary<string, BulkBaselineDryRunItem>();
            var results = job?.Modules.ToDictionary(m => m.ModuleId) ?? new Dictionary<string, BulkBaselineJobModule>();
            Rows = selection == null ? Array.Empty<BaselineManagerModuleRow>() : selection.Query(new ModuleSelectionFilter(Search, selectedOnly: SelectedOnly)).Select(c =>
                new BaselineManagerModuleRow(c, c.Module != null && plan.TryGetValue(c.Module.ModuleId, out var row) ? row : null,
                    c.Module != null && facts.TryGetValue(c.Module.ModuleId, out var state) ? state : null,
                    c.Module != null && results.TryGetValue(c.Module.ModuleId, out var result) ? result : null, SelectOne)).ToArray();
            Changed(nameof(Summary)); Refresh();
        }
        private IReadOnlyList<BaselineModuleSelection> SelectedModules() => resumeId.HasValue && job != null ? job.Modules.Where(m => !m.EverSucceeded &&
            (m.Status == BulkBaselineModuleStatus.Pending || (m.Status == BulkBaselineModuleStatus.CancelRequested && m.Attempts == 0))).Select(m => new BaselineModuleSelection(m.Module, m.Suffix, m.Annotation)).ToArray() : selection?.GetSelectedModules() ?? Array.Empty<BaselineModuleSelection>();
        private Task Preflight()
        {
            var modules = SelectedModules(); var runtime = Runtime; var id = resumeId;
            var type = BaselineType; var suffix = Suffix; var annotation = Annotation; var policy = Policy; var version = owner.Version; var connection = owner.Connection;
            InvalidatePlan(); resumeId = id;
            return owner.RunToolAsync("Bulk baseline Preflight / Dry Run", async (token, p) =>
            {
                var request = new BulkBaselinePreflightRequest(new BulkBaselineRequest(modules, type, suffix: suffix, annotation: annotation), policy, target: version, connectionMode: connection);
                var result = await runtime.Preflight.DryRunAsync(request, token).ConfigureAwait(false); var observed = new Dictionary<string, BaselinePreflightFacts>();
                foreach (var item in result.Items)
                {
                    var probe = new BaselinePreflightProbe(request, request.Baseline.Modules.Single(m => m.Module.ModuleId == item.Original.ModuleId), item.Fresh.ExpectedVersion);
                    observed[item.Original.ModuleId] = (await runtime.States.ReadAsync(probe, token).ConfigureAwait(false)).Facts;
                }
                return () => { dryRun = result; facts = observed; Report = result.Summary + "\n" + string.Join("\n", result.Items.Where(i => !i.EligibleForPreview || !string.IsNullOrEmpty(i.Fresh.Warning)).Select(i =>
                    i.Fresh.FullPath + ": " + i.Fresh.Reason + " · Öneri: " + i.Fresh.RecommendedResolution + " · Warning: " + i.Fresh.Warning)); Project(); };
            });
        }
        private Task Review()
        { var runtime = Runtime; var plan = dryRun.Plan; return owner.RunToolAsync("Kesin batch onayı", async (token, p) => { bool yes = await runtime.Approval.ReviewAsync(plan, token).ConfigureAwait(false); return () => { Report = yes ? "Plan kesin olarak onaylandı. Execute için onay süresi ve değişmemiş seçim/girdiler gerekir." : "Onay verilmedi; Execute kapalı."; Refresh(); }; }); }
        private Task Execute()
        {
            var runtime = Runtime; var plan = dryRun.Plan; var resume = resumeId; int generation = ++runGeneration; SelectedPane = 0;
            return owner.RunToolAsync("Bulk baseline job yürütülüyor", async (token, p) =>
            {
                try
                {
                    await owner.InvalidateBeforeWriteAsync().ConfigureAwait(false);
                    var current = resume.HasValue ? await runtime.Runner.OpenAsync(resume.Value, token).ConfigureAwait(false) : await runtime.Runner.CreateAsync(plan, token).ConfigureAwait(false);
                    runtime.Approval.BindJob(current.JobId);
                    await dispatcher.InvokeAsync(() => ApplyJob(current));
                    var result = await runtime.Runner.RunAsync(current.JobId, Observer(runtime, generation), token).ConfigureAwait(false);
                    return () => { ApplyJob(result); dryRun = null; resumeId = result.Status == BulkBaselineModuleStatus.CancelRequested || result.Status == BulkBaselineModuleStatus.Pending ? (Guid?)result.JobId : null;
                        owner.ReportToolOutcome("Bulk baseline job: " + result.Status + " · " + result.Modules.Count(m => m.Status == BulkBaselineModuleStatus.Succeeded) + "/" + result.Modules.Count + " başarılı");
                        Report = "Job kaydedildi. Batch transactional değildir; başarılı baseline'lar geri alınmadı. İptal satırları aynı JobId ile yeni Preflight/onaydan sonra sürdürülür."; Project(); };
                }
                finally { runtime.Approval.Clear(); }
            });
        }
        private IProgress<BulkBaselineJobProgress> Observer(BaselineManagerSession runtime, int generation) => new JobObserver(value =>
            dispatcher.BeginInvoke(new Action(() => { if (generation == runGeneration && ReferenceEquals(Runtime, runtime) && (job == null || value.Job.JobId != job.JobId || value.Job.Revision >= job.Revision)) { ApplyJob(value.Job); elapsed = value.Elapsed; Changed(nameof(JobSummary)); } })));
        private sealed class JobObserver : IProgress<BulkBaselineJobProgress>
        { private readonly Action<BulkBaselineJobProgress> report; internal JobObserver(Action<BulkBaselineJobProgress> report) { this.report = report; } public void Report(BulkBaselineJobProgress value) => report(value); }
        private void ApplyJob(BulkBaselineJobRecord value)
        { auditReport = null; Changed(nameof(AuditSummary)); Changed(nameof(AuditModules)); Changed(nameof(AuditContextSummary)); job = value; elapsed = value.Elapsed; var selected = SelectedProgress?.Module.ModuleId; ProgressRows = value.Modules.Select(m => new BaselineManagerProgressRow(m)).ToArray(); SelectedProgress = ProgressRows.FirstOrDefault(r => r.Module.ModuleId == selected) ?? ProgressRows.FirstOrDefault(); Changed(nameof(CurrentJob)); Changed(nameof(JobSummary)); Changed(nameof(OverallProgress)); }
        private Task OpenHistory() => QueryHistory(0);
        private Task QueryHistory(int offset)
        {
            var runtime = Runtime;
            return owner.RunToolAsync("Job history / filters", async (token, p) =>
            {
                var filter = new BaselineJobHistoryFilter(HistorySearch, HistoryOperator, HistoryDatabase,
                    IsReal ? BaselineMetadataSource.NativeDxl : BaselineMetadataSource.Mock, RepositoryId, owner.Version,
                    HistoryFrom.HasValue ? DateTime.SpecifyKind(HistoryFrom.Value.Date, DateTimeKind.Local).ToUniversalTime() : (DateTime?)null,
                    HistoryTo.HasValue ? DateTime.SpecifyKind(HistoryTo.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime() : (DateTime?)null,
                    HistoryOutcome == "All" ? (BaselineBatchOutcome?)null : (BaselineBatchOutcome)Enum.Parse(typeof(BaselineBatchOutcome), HistoryOutcome), offset: offset, limit: HistoryPageSize);
                var page = await runtime.JobHistory.QueryAsync(filter, token).ConfigureAwait(false);
                return () => { HistoryItems = page.Items; History = page.Items.Select(i => i.Snapshot).ToArray(); SelectedHistoryItem = page.Items.FirstOrDefault(); SelectedPane = 1;
                    historyOffset = page.Offset; historyMatched = page.Matched; historyCriteriaChanged = false; Changed(nameof(HistoryPageLabel)); Refresh();
                    Report = HistoryPageLabel + " jobs · Scan limited=" + page.ScanLimited + "\n" + string.Join("\n", page.Issues); };
            });
        }
        private Task ReadHistoryReport()
        {
            var runtime = Runtime; var id = SelectedHistory.JobId;
            return owner.RunToolAsync("Read-only audit report", async (token, p) =>
            {
                var snapshot = await runtime.JobHistory.OpenAsync(id, token).ConfigureAwait(false);
                var result = await runtime.Reports.ReadAsync(id, token).ConfigureAwait(false);
                if (snapshot.Revision != result.Revision) throw new BulkBaselineJournalException("Job changed while opening its report; refresh history and read again.");
                return () => { InvalidateApproval(); dryRun = null; resumeId = snapshot.JobId; ApplyJob(snapshot); RestoreJobInputs(snapshot); ShowAuditReport(result); SelectedPane = 2; Project(); };
            });
        }
        private Task LoadJob()
        {
            InvalidateApproval(); var runtime = Runtime; var id = SelectedHistory.JobId;
            return owner.RunToolAsync("Job açılıyor / read-only reconciliation", async (token, p) => { var result = await runtime.Runner.OpenAsync(id, token).ConfigureAwait(false); return () =>
            { ApplyJob(result); SelectedPane = 0; dryRun = null; resumeId = result.JobId; RestoreJobInputs(result); Report = "Job açıldı; Succeeded tekrar oluşturulmaz. Devam için Preflight ve yeni kesin onay gerekir."; Project(); }; });
        }
        private void RestoreJobInputs(BulkBaselineJobRecord snapshot)
        { type = snapshot.Type; policy = snapshot.Policy; suffix = snapshot.JobSuffix ?? ""; annotation = snapshot.JobAnnotation ?? ""; Changed(nameof(BaselineType)); Changed(nameof(Policy)); Changed(nameof(Suffix)); Changed(nameof(Annotation)); Changed(nameof(Validation)); }
        private void ShowAuditReport(BulkBaselineReport value)
        { auditReport = value; Changed(nameof(AuditSummary)); Changed(nameof(AuditModules)); Changed(nameof(AuditContextSummary)); }
        private Task Reconcile()
        {
            InvalidateApproval(); var runtime = Runtime; var id = job.JobId;
            return owner.RunToolAsync("Read-only job reconciliation", async (token, p) =>
            {
                var result = await runtime.Runner.OpenAsync(id, token).ConfigureAwait(false);
                var summary = await runtime.Reports.ReadAsync(id, token).ConfigureAwait(false);
                if (result.Revision != summary.Revision) throw new BulkBaselineJournalException("Job changed after reconciliation; refresh to read its current revision.");
                return () => { ApplyJob(result); RestoreJobInputs(result); ShowAuditReport(summary); dryRun = null; resumeId = result.JobId; Report = "Reconcile tamamlandı; rapor yenilendi. Bu komut baseline oluşturmaz. NeedsReconciliation otomatik retry yetkisi değildir."; Project(); };
            });
        }
        private Task Retry()
        {
            var runtime = Runtime; var id = job.JobId; var module = SelectedProgress.Module.ModuleId; int generation = ++runGeneration; InvalidateApproval();
            return owner.RunToolAsync("Explicit retry · fresh metadata ve operatör onayı", async (token, p) =>
            { try { var result = await runtime.Runner.RetryAsync(id, module, Observer(runtime, generation), token).ConfigureAwait(false); return () => { ApplyJob(result); dryRun = null; Report = "Retry sonucu journal'a kaydedildi; otomatik tekrar yapılmaz."; Project(); }; } finally { runtime.Approval.Clear(); } });
        }
        private Task Export()
        {
            var runtime = Runtime; var id = job.JobId; var format = ExportFormat;
            var root = Path.GetFullPath(ExportRoot); var path = Path.Combine(root, "baseline-job-" + id.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
            return owner.RunToolAsync("Baseline audit export " + format, async (token, p) =>
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(root);
                var result = await runtime.Reports.ExportAsync(id, new ExportRequest(path, format, ExportErrorPolicy.StopOnInvalidRow), new AuditExportProgress(p), token).ConfigureAwait(false);
                var summary = await runtime.Reports.ReadAsync(id, token).ConfigureAwait(false);
                return () => { auditReport = summary; Changed(nameof(AuditSummary)); Changed(nameof(AuditModules)); Changed(nameof(AuditContextSummary)); Report = "Export " + result.Status + ": " + result.DirectoryPath + " · issues=" + result.IssueCount; };
            });
        }
        private sealed class AuditExportProgress : IProgress<ExportProgress>
        {
            private readonly IProgress<string> progress;
            internal AuditExportProgress(IProgress<string> progress) { this.progress = progress; }
            public void Report(ExportProgress value) => progress.Report(value.Phase + " · rows=" + value.RowsWritten + " · issues=" + value.IssueCount);
        }
        public void Dispose() { timer.Stop(); timer.Tick -= OnTick; Runtime?.Approval.Clear(); runGeneration++; }
    }
}
