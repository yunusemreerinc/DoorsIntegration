using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed class SelectionFolderRow : ObservableModel
    {
        private bool recursive;
        private readonly Action invalidate;
        public SelectionFolderRow(string path, bool includeSubfolders, Action invalidate)
        { Path = path; recursive = includeSubfolders; this.invalidate = invalidate; }
        public string Path { get; }
        public bool IncludeSubfolders { get => recursive; set { if (Set(ref recursive, value)) invalidate(); } }
    }
    public sealed class SelectionModuleRow : ObservableModel
    {
        private bool selected;
        private readonly Action<string, bool> change;
        public SelectionModuleRow(ModuleSelectionCandidate candidate, Action<string, bool> change)
        { Candidate = candidate; selected = candidate.IsSelected; this.change = change; }
        public ModuleSelectionCandidate Candidate { get; }
        public bool IsSelected { get => selected; set { if (value != selected && Candidate.CanSelect) { selected = value; Changed(); change(Candidate.Module.ModuleId, value); } } }
        public string Baseline => Candidate.Metadata.BaselineStatus == SelectionBaselineStatus.Available ? Candidate.Metadata.LatestBaseline.ToString() : Candidate.Metadata.BaselineStatus.ToString();
    }
    public sealed class ModuleSelectionViewModel : ObservableModel
    {
        private readonly WorkspaceViewModel owner;
        private readonly IModuleSelectionProfileStore profiles;
        private readonly List<UiCommand> commands = new List<UiCommand>();
        private ModuleSelectionSession selection;
        private string repository, folderPath = "/Bulk", search = "", report = "Klasör ekleyin; yükleme yalnız açık komutla ilerler.", profileName = "Module selection";
        private string profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DoorsIntegration", "module-selection-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        private bool includeSubfolders = true, selectedOnly;
        private ModuleSelectionAccessFilter access;
        private SelectionFolderRow selectedFolder;
        private IReadOnlyList<SelectionModuleRow> rows = Array.Empty<SelectionModuleRow>();
        private BaselinePreflightRevalidation dryRun;
        private IReadOnlyList<BulkBaselineDryRunItem> preflightRows = Array.Empty<BulkBaselineDryRunItem>();
        private BulkBaselineDryRunItem selectedPreflightItem;
        private BaselineType baselineType = BaselineType.Minor;
        private ExecutionPolicy executionPolicy = ExecutionPolicy.ContinueOnError;
        private string baselineSuffix = "", baselineAnnotation = "";
        public ModuleSelectionViewModel(WorkspaceViewModel owner, IModuleSelectionProfileStore profiles = null)
        {
            this.owner = owner; this.profiles = profiles ?? new JsonModuleSelectionProfileStore();
            repository = owner.Mode == ClientMode.Mock ? MockDoorsAdapter.BaselineRepositoryId : "";
            AddFolderCommand = Command(AddFolder); RemoveFolderCommand = Command(() => { Folders.Remove(SelectedFolder); SelectedFolder = null; Invalidate(); return Task.CompletedTask; }, () => SelectedFolder != null);
            LoadNextCommand = Command(LoadNext, () => Folders.Count > 0 && (selection == null || selection.HasMore));
            RestartCommand = Command(() => { Invalidate(); return Task.CompletedTask; });
            SelectAllCommand = Command(() => SelectFiltered(true), () => selection != null);
            DeselectVisibleCommand = Command(() => SelectFiltered(false), () => selection != null);
            DeselectAllCommand = Command(() => { selection.DeselectAll(); InvalidatePlan(); Project(); return Task.CompletedTask; }, () => selection != null);
            SelectFolderCommand = Command(() => SelectFolder(true), () => selection != null && SelectedFolder != null);
            DeselectFolderCommand = Command(() => SelectFolder(false), () => selection != null && SelectedFolder != null);
            SaveProfileCommand = Command(SaveProfile, () => selection != null);
            LoadProfileCommand = Command(LoadProfile);
            PlanCommand = Command(PreviewPlan, () => selection != null && selection.SelectedCount > 0);
            RevalidateCommand = Command(RevalidatePlan, () => dryRun != null);
        }
        public ObservableCollection<SelectionFolderRow> Folders { get; } = new ObservableCollection<SelectionFolderRow>();
        public IReadOnlyList<SelectionModuleRow> Rows { get => rows; private set => Set(ref rows, value); }
        public string RepositoryId { get => repository; set { if (owner.IsIdle && Set(ref repository, value)) Invalidate(); } }
        public string FolderPath { get => folderPath; set => Set(ref folderPath, value); }
        public bool IncludeSubfolders { get => includeSubfolders; set => Set(ref includeSubfolders, value); }
        public SelectionFolderRow SelectedFolder { get => selectedFolder; set { Set(ref selectedFolder, value); Refresh(); } }
        public string Search { get => search; set { if (owner.IsIdle && Set(ref search, value)) Project(); } }
        public bool SelectedOnly { get => selectedOnly; set { if (owner.IsIdle && Set(ref selectedOnly, value)) Project(); } }
        public ModuleSelectionAccessFilter Access { get => access; set { if (owner.IsIdle && Set(ref access, value)) Project(); } }
        public Array AccessFilters => Enum.GetValues(typeof(ModuleSelectionAccessFilter));
        public string ProfileName { get => profileName; set => Set(ref profileName, value); }
        public string ProfilePath { get => profilePath; set => Set(ref profilePath, value); }
        public string Report { get => report; private set => Set(ref report, value); }
        public string Summary => selection == null ? "Yükleme başlamadı · 0 seçili" : Describe(selection.Snapshot());
        public UiCommand AddFolderCommand { get; }
        public UiCommand RemoveFolderCommand { get; }
        public UiCommand LoadNextCommand { get; }
        public UiCommand RestartCommand { get; }
        public UiCommand SelectAllCommand { get; }
        public UiCommand DeselectVisibleCommand { get; }
        public UiCommand DeselectAllCommand { get; }
        public UiCommand SelectFolderCommand { get; }
        public UiCommand DeselectFolderCommand { get; }
        public UiCommand SaveProfileCommand { get; }
        public UiCommand LoadProfileCommand { get; }
        public UiCommand PlanCommand { get; }
        public UiCommand RevalidateCommand { get; }
        public event Action PlanViewRequested;
        public Array BaselineTypes => Enum.GetValues(typeof(BaselineType));
        public Array ExecutionPolicies => Enum.GetValues(typeof(ExecutionPolicy));
        public BaselineType BaselineType { get => baselineType; set { if (owner.IsIdle && Set(ref baselineType, value)) InvalidatePlan(); } }
        public ExecutionPolicy ExecutionPolicy { get => executionPolicy; set { if (owner.IsIdle && Set(ref executionPolicy, value)) InvalidatePlan(); } }
        public string BaselineSuffix { get => baselineSuffix; set { if (owner.IsIdle && Set(ref baselineSuffix, value)) InvalidatePlan(); } }
        public string BaselineAnnotation { get => baselineAnnotation; set { if (owner.IsIdle && Set(ref baselineAnnotation, value)) InvalidatePlan(); } }
        public IReadOnlyList<BulkBaselineDryRunItem> PreflightRows { get => preflightRows; private set => Set(ref preflightRows, value); }
        public BaselinePreflightRevalidation DryRunResult => dryRun;
        public string PreflightSummary => dryRun == null ? "Plan yok. Modül seçimini tamamlayıp Dry Run çalıştırın." : dryRun.Plan.Summary + "\n" + dryRun.Summary;
        public string PreflightPlanIdentity => dryRun == null ? "" : "PlanId=" + dryRun.Plan.PlanId + " · SHA256=" + dryRun.Plan.Fingerprint;
        public BulkBaselineDryRunItem SelectedPreflightItem { get => selectedPreflightItem; set { if (Set(ref selectedPreflightItem, value)) Changed(nameof(PreflightItemDetails)); } }
        public string PreflightItemDetails => selectedPreflightItem == null ? "Reason, çözüm önerisi ve ön koşul kanıtını görmek için bir satır seçin." :
            selectedPreflightItem.FullPath + "\nModuleId=" + selectedPreflightItem.ModuleId + "\nCurrent=" + (selectedPreflightItem.CurrentVersion?.ToString() ?? "None/Unavailable") +
            " · Expected=" + (selectedPreflightItem.ExpectedVersion?.ToString() ?? "Unavailable") + "\n" + selectedPreflightItem.Status + " / " + selectedPreflightItem.ReasonCode +
            "\n" + selectedPreflightItem.Reason + "\nÖnerilen çözüm: " + selectedPreflightItem.RecommendedResolution + "\nWarning: " + selectedPreflightItem.Warning +
            "\nPrecondition=" + (selectedPreflightItem.Precondition?.Fingerprint ?? "Unavailable");
        private UiCommand Command(Func<Task> action, Func<bool> available = null)
        { var command = new UiCommand(action, () => owner.IsIdle && !owner.IsClosing && (available?.Invoke() ?? true)); commands.Add(command); return command; }
        public void Refresh() { foreach (var command in commands) command.Refresh(); }
        private ModuleSelectionRequest Request() => new ModuleSelectionRequest(RepositoryId,
            Folders.Select(f => new ModuleSelectionFolder(f.Path, f.IncludeSubfolders)), owner.Version, owner.Connection);
        public void Invalidate() { selection = null; Rows = Array.Empty<SelectionModuleRow>(); InvalidatePlan(); Report = "Kapsam değişti; seçimleri yeniden yükleyin."; Changed(nameof(Summary)); Refresh(); }
        private void InvalidatePlan()
        { dryRun = null; PreflightRows = Array.Empty<BulkBaselineDryRunItem>(); SelectedPreflightItem = null; Changed(nameof(DryRunResult)); Changed(nameof(PreflightSummary)); Changed(nameof(PreflightPlanIdentity)); Refresh(); }
        public void ResetConnection()
        { Invalidate(); Folders.Clear(); selectedFolder = null; Changed(nameof(SelectedFolder)); RepositoryId = owner.Mode == ClientMode.Mock ? MockDoorsAdapter.BaselineRepositoryId : ""; FolderPath = owner.Mode == ClientMode.Mock ? "/Bulk" : "/"; }
        private Task AddFolder()
        {
            var path = FolderPath; var recursive = IncludeSubfolders;
            return owner.RunToolAsync("Seçim klasörü ekleniyor", (token, progress) =>
            {
                var folder = new ModuleSelectionFolder(path, recursive);
                if (Folders.Any(f => f.Path == folder.CanonicalFullPath)) throw new ArgumentException("Folder is already selected.");
                return Task.FromResult<Action>(() => { Folders.Add(new SelectionFolderRow(folder.CanonicalFullPath, folder.IncludeSubfolders, Invalidate)); Invalidate(); });
            });
        }
        private async Task LoadNext()
        {
            var request = selection == null ? RequestSafely() : null;
            if (selection == null && request == null) return;
            var current = selection;
            await owner.RunToolAsync("Formal modül seçim sayfası yükleniyor", async (token, progress) =>
            {
                if (current == null) current = owner.Session.ModuleSelection.Create(request);
                await current.LoadNextAsync(100, 5, token).ConfigureAwait(false);
                return () => Report = "Sayfa tamamlandı. Sonraki grup yalnız 'Sonraki grubu yükle' ile okunur.";
            });
            selection = current; Project();
            if (selection != null && selection.Snapshot().Issues.Count > 0) Report += "\n" + string.Join("\n", selection.Snapshot().Issues.Take(20));
        }
        private ModuleSelectionRequest RequestSafely()
        {
            try { return Request(); }
            catch (Exception ex) when (ex is ArgumentException || ex is IntegrationException)
            { Report = "Repository ve canonical proje/klasör yollarını kontrol edin: " + ex.GetType().Name; return null; }
        }
        private ModuleSelectionFilter Filter() => new ModuleSelectionFilter((Search ?? "").Substring(0, Math.Min(256, (Search ?? "").Length)), Access, SelectedOnly);
        private void Project()
        {
            Rows = selection == null ? Array.Empty<SelectionModuleRow>() : selection.Query(Filter()).Select(c => new SelectionModuleRow(c, SelectOne)).ToArray();
            Changed(nameof(Summary)); Refresh();
        }
        private void SelectOne(string id, bool value) { selection.SetSelected(id, value); InvalidatePlan(); Project(); }
        private Task SelectFiltered(bool value) { selection.SelectFiltered(value, Filter()); InvalidatePlan(); Project(); return Task.CompletedTask; }
        private Task SelectFolder(bool value) { selection.SelectFolder(SelectedFolder.Path, value); InvalidatePlan(); Project(); return Task.CompletedTask; }
        private static string Describe(ModuleSelectionSnapshot snapshot) => snapshot.Candidates.Count + " yüklü · " + snapshot.SelectedCount + " seçili · " + snapshot.VisitedFolders +
            " klasör · devam=" + snapshot.HasMore + " · kısmi=" + snapshot.IsIncomplete + " · profilden bekleyen=" + snapshot.PendingProfileSelections;
        private Task SaveProfile()
        {
            var path = ProfilePath; var name = ProfileName; var current = selection;
            return owner.RunToolAsync("Module Selection Profile kaydediliyor", (token, progress) =>
            { token.ThrowIfCancellationRequested(); profiles.Save(path, current.CreateProfile(name)); return Task.FromResult<Action>(() => Report = "Profil yeni dosyaya kaydedildi. DOORS verisi değiştirilmedi: " + path); });
        }
        private Task LoadProfile()
        {
            var path = ProfilePath; var expectedRepository = RepositoryId; var version = owner.Version; var connection = owner.Connection;
            return owner.RunToolAsync("Module Selection Profile okunuyor", (token, progress) =>
            {
                var profile = profiles.Load(path); token.ThrowIfCancellationRequested();
                if (expectedRepository != profile.RepositoryId) throw new ArgumentException("Profile belongs to a different repository context.");
                var cursor = owner.Session.ModuleSelection.Create(new ModuleSelectionRequest(profile.RepositoryId, profile.Folders, version, connection), profile);
                return Task.FromResult<Action>(() =>
                {
                    Folders.Clear(); foreach (var folder in profile.Folders) Folders.Add(new SelectionFolderRow(folder.CanonicalFullPath, folder.IncludeSubfolders, Invalidate));
                    selectedFolder = null; Changed(nameof(SelectedFolder)); ProfileName = profile.Name; selection = cursor; InvalidatePlan();
                    Report = "Profil yüklendi. Stabil kimlik/yol ve güncel erişim yeniden doğrulanana kadar modüller seçime alınmaz."; Project();
                });
            });
        }
        private Task PreviewPlan()
        {
            var modules = selection.GetSelectedModules();
            var type = BaselineType; var suffix = BaselineSuffix; var annotation = BaselineAnnotation; var policy = ExecutionPolicy;
            var target = owner.Version; var connection = owner.Connection;
            InvalidatePlan();
            return owner.RunToolAsync("Bulk baseline read-only ön kontrol ve yeniden doğrulama", async (token, progress) =>
            {
                var request = new BulkBaselinePreflightRequest(new BulkBaselineRequest(modules, type, suffix: suffix, annotation: annotation), policy, target: target, connectionMode: connection);
                var result = await owner.Session.BaselinePreflight.DryRunAsync(request, token).ConfigureAwait(false);
                return () => ApplyDryRun(result);
            });
        }
        private Task RevalidatePlan()
        {
            var plan = dryRun.Plan;
            InvalidatePlan();
            return owner.RunToolAsync("Dry-run ön koşulları yeniden okunuyor", async (token, progress) =>
            {
                var result = await owner.Session.BaselinePreflight.RevalidateAsync(plan, token).ConfigureAwait(false);
                return () => ApplyDryRun(result);
            });
        }
        private void ApplyDryRun(BaselinePreflightRevalidation result)
        {
            dryRun = result; PreflightRows = result.Items.Select(i => i.Fresh).ToArray(); SelectedPreflightItem = PreflightRows.FirstOrDefault();
            Report = result.Plan.Summary + "\n" + result.Summary + "\n" + string.Join("\n", PreflightRows.Where(i => i.WillBeSkipped).Take(20).Select(i => i.FullPath + ": " + i.Reason + " Öneri: " + i.RecommendedResolution));
            Changed(nameof(DryRunResult)); Changed(nameof(PreflightSummary)); Changed(nameof(PreflightPlanIdentity)); Refresh(); PlanViewRequested?.Invoke();
        }
    }
}
