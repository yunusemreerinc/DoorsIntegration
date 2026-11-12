using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed class NativeBaselineSetViewModel : ObservableModel
    {
        private readonly WorkspaceViewModel owner; private readonly BulkBaselineManagerViewModel selection;
        private readonly List<UiCommand> commands = new List<UiCommand>();
        private string folder = "/Demo", name = "Review set", description = "", suffix = "", annotation = "", report = "Select formal modules above, choose an action, then Preflight. Read-only is the default.";
        private string exportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DoorsIntegration", "NativeSetReports");
        private BaselineSetAction action; private ExportFormat format = ExportFormat.Json;
        private BaselineSetPreflight plan; private BaselineSetJob job, selectedHistory;
        private BaselineSetDefinitionDetails definition; private NativeBaselineSetDetails set;
        private IReadOnlyList<BaselineSetDefinitionDetails> definitions = Array.Empty<BaselineSetDefinitionDetails>();
        private IReadOnlyList<NativeBaselineSetDetails> sets = Array.Empty<NativeBaselineSetDetails>();
        private IReadOnlyList<BaselineSetJob> history = Array.Empty<BaselineSetJob>();
        public NativeBaselineSetViewModel(WorkspaceViewModel owner, BulkBaselineManagerViewModel selection)
        {
            this.owner = owner; this.selection = selection;
            RefreshDefinitionsCommand = Command(RefreshDefinitions); LoadDefinitionCommand = Command(LoadDefinition, () => Definition != null);
            PreflightCommand = Command(Preflight); ReviewCommand = Command(Review, () => plan?.Ready == true && !owner.IsReadOnly && Runtime.Service.WritesValidated);
            ExecuteCommand = Command(Execute, () => !owner.IsReadOnly && Runtime.Approval.HasApproval(plan) && Runtime.Service.WritesValidated);
            VerifyCommand = Command(Verify, () => Definition != null && SelectedSet != null); HistoryCommand = Command(LoadHistory);
            OpenJobCommand = Command(OpenJob, () => SelectedHistory != null); ReconcileCommand = Command(Reconcile, () => job != null);
            ExportCommand = Command(Export, () => job != null && !string.IsNullOrWhiteSpace(ExportRoot));
        }
        private NativeBaselineSetSession Runtime => owner.Session.NativeBaselineSets;
        private bool Idle => owner.IsIdle && !owner.IsClosing && selection.IsNative && Runtime != null;
        public string Banner => owner.Mode == ClientMode.Mock ? "MOCK — native set simulation / local journal" : "REAL DOORS — writes disabled until build/state/link validation; second approval required";
        public Array Actions => Enum.GetValues(typeof(BaselineSetAction)); public Array Formats => Enum.GetValues(typeof(ExportFormat));
        public string Folder { get => folder; set { if (owner.IsIdle && Set(ref folder, value)) { definition = null; set = null; definitions = Array.Empty<BaselineSetDefinitionDetails>(); sets = Array.Empty<NativeBaselineSetDetails>(); NotifyDetails(); Invalidate(); } } }
        public string DefinitionName { get => name; set { if (owner.IsIdle && Set(ref name, value)) Invalidate(); } }
        public string Description { get => description; set { if (owner.IsIdle && Set(ref description, value)) Invalidate(); } }
        public string Suffix { get => suffix; set { if (owner.IsIdle && Set(ref suffix, value)) Invalidate(); } }
        public string Annotation { get => annotation; set { if (owner.IsIdle && Set(ref annotation, value)) Invalidate(); } }
        public BaselineSetAction Action { get => action; set { if (owner.IsIdle && Set(ref action, value)) Invalidate(); } }
        public ExportFormat Format { get => format; set { if (owner.IsIdle) Set(ref format, value); } }
        public string ExportRoot { get => exportRoot; set { if (owner.IsIdle) Set(ref exportRoot, value); } }
        public string Report { get => report; private set => Set(ref report, value); }
        public BaselineSetDefinitionDetails Definition { get => definition; set { if (owner.IsIdle && Set(ref definition, value)) { set = null; sets = Array.Empty<NativeBaselineSetDetails>(); NotifyDetails(); Invalidate(); } } }
        public NativeBaselineSetDetails SelectedSet { get => set; set { if (owner.IsIdle && Set(ref set, value)) { NotifyDetails(); Invalidate(); } } }
        public BaselineSetJob SelectedHistory { get => selectedHistory; set { Set(ref selectedHistory, value); Refresh(); } }
        public IReadOnlyList<BaselineSetDefinitionDetails> Definitions => definitions; public IReadOnlyList<NativeBaselineSetDetails> Sets => sets;
        public IReadOnlyList<BaselineSetModuleReference> DefinitionModules => Definition?.Modules ?? (IReadOnlyList<BaselineSetModuleReference>)Array.Empty<BaselineSetModuleReference>();
        public IReadOnlyList<BaselineSetJob> History => history; public BaselineSetJob CurrentJob => job;
        public IEnumerable<NativeSetMemberRow> Members => DefinitionModules.Concat(SelectedSet?.Members.Select(member => member.Module) ?? Enumerable.Empty<BaselineSetModuleReference>()).GroupBy(reference => reference.ModuleId).Select(group => group.First()).Select(reference => new NativeSetMemberRow(reference, SelectedSet?.Members.SingleOrDefault(member => member.Module.ModuleId == reference.ModuleId), owner.Mode == ClientMode.Mock));
        public string SetSummary => SelectedSet == null ? "No set selected" : "Set version=" + SelectedSet.Version + " · Open=" + SelectedSet.IsOpen + " · Members=" + SelectedSet.Members.Count + "/" + DefinitionModules.Count + " · Missing=" + Members.Count(m => m.Missing);
        public string Approval => Runtime?.Approval.HasApproval(plan) == true ? "APPROVED — expires " + plan.ExpiresAtUtc.ToString("HH:mm:ss 'UTC'") : "No current approval — Execute disabled";
        public UiCommand RefreshDefinitionsCommand { get; } public UiCommand LoadDefinitionCommand { get; } public UiCommand PreflightCommand { get; } public UiCommand ReviewCommand { get; } public UiCommand ExecuteCommand { get; }
        public UiCommand VerifyCommand { get; } public UiCommand HistoryCommand { get; } public UiCommand OpenJobCommand { get; } public UiCommand ReconcileCommand { get; } public UiCommand ExportCommand { get; }
        private UiCommand Command(Func<Task> work, Func<bool> available = null) { var c = new UiCommand(work, () => Idle && (available?.Invoke() ?? true)); commands.Add(c); return c; }
        public void Refresh() { foreach (var c in commands) c.Refresh(); Changed(nameof(Approval)); Changed(nameof(Banner)); }
        public void Invalidate() { plan = null; Runtime?.Approval.Clear(); Refresh(); }
        public void Reset() { Invalidate(); definitions = Array.Empty<BaselineSetDefinitionDetails>(); sets = Array.Empty<NativeBaselineSetDetails>(); history = Array.Empty<BaselineSetJob>(); definition = null; set = null; job = null; selectedHistory = null; NotifyDetails(); Changed(nameof(History)); }
        private void NotifyDetails() { foreach (string p in new[] { nameof(Definitions), nameof(Definition), nameof(Sets), nameof(SelectedSet), nameof(DefinitionModules), nameof(Members), nameof(SetSummary), nameof(CurrentJob) }) Changed(p); }
        private Task RefreshDefinitions()
        { var runtime = Runtime; var path = Folder; var target = owner.Version; Invalidate(); return owner.RunToolAsync("Native definitions (read-only)", async (t, p) => { var list = await runtime.Service.ListBaselineSetDefinitions(path, target, t).ConfigureAwait(false); return () => { definitions = list; definition = null; set = null; sets = Array.Empty<NativeBaselineSetDetails>(); NotifyDetails(); Report = "Definitions=" + list.Count + ". Select one and load details." + (owner.Mode == ClientMode.RealDoors ? " Read-only visibility-limited observation; cached definition freshness and hidden membership are unknown." : ""); }; }); }
        private Task LoadDefinition()
        { var runtime = Runtime; var id = Definition.Id; var path = Folder; var target = owner.Version; Invalidate(); return owner.RunToolAsync("Native definition/set details", async (t, p) => { var d = await runtime.Service.GetBaselineSetDefinition(path, id, target, t).ConfigureAwait(false); var list = await runtime.Service.ListBaselineSets(path, id, target, t).ConfigureAwait(false); return () => { definition = d; sets = list; set = list.FirstOrDefault(x => x.IsOpen) ?? list.LastOrDefault(); NotifyDetails(); Report = SetSummary; }; }); }
        private Task Preflight()
        { var runtime = Runtime; var action = Action; var folder = Folder; var definition = Definition?.Id ?? ""; var set = SelectedSet?.Id ?? ""; var name = DefinitionName; var description = Description; var modules = selection.NativeModules(); var suffix = Suffix; var annotation = Annotation; var version = owner.Version; Invalidate(); return owner.RunToolAsync("Native set preflight / dry-run — no writes", async (t, p) => { var request = new BaselineSetRequest(action, folder, definition, set, name, description, modules, suffix, annotation, version); var result = await runtime.Service.PreflightAsync(request, t).ConfigureAwait(false); return () => { plan = result; Report = "Action=" + request.Action + " · Ready=" + result.Ready + " · Selected=" + request.Modules.Count + "\n" + string.Join("\n", result.Problems) + "\nFingerprint=" + result.Fingerprint; Refresh(); }; }); }
        private Task Review()
        { var runtime = Runtime; var captured = plan; return owner.RunToolAsync("Explicit native set review", async (t, p) => { bool yes = await runtime.Approval.ReviewAsync(captured, t).ConfigureAwait(false); return () => { Report = yes ? "Approved; Execute requires unchanged, fresh plan." : "Approval declined."; Refresh(); }; }); }
        private Task Execute()
        { var runtime = Runtime; var captured = plan; return owner.RunToolAsync("Native set operation — active COM cannot be assumed cancellable", async (t, p) => { p.Report("Durable intent, renewed preflight, one native operation. No automatic retry/close."); var result = await runtime.Service.ExecuteAsync(captured, t).ConfigureAwait(false); return () => { Apply(result); plan = null; Refresh(); }; }); }
        private void Apply(BaselineSetJob result)
        { job = result; folder = result.Request.FolderPath; Changed(nameof(Folder)); var snapshot = result.After ?? result.Before; definitions = snapshot.Definitions; definition = definitions.SingleOrDefault(d => d.Id == result.DefinitionId); sets = snapshot.Sets.Where(s => s.DefinitionId == result.DefinitionId).ToArray(); set = sets.SingleOrDefault(s => s.Id == result.SetId); Report = result.JobId + " · " + result.Status + " · " + result.Reason + "\nMissing=" + string.Join(",", result.MissingModules) + "\nLinks=" + (result.LinkVerification == null ? "Not verified" : result.LinkVerification.Verified.ToString()) + "\n" + string.Join("\n", result.LinkVerification == null ? new List<string>() : result.LinkVerification.Problems.Concat(result.LinkVerification.Missing).Concat(result.LinkVerification.Unexpected)); NotifyDetails(); }
        private Task Verify()
        { var runtime = Runtime; var path = Folder; var d = Definition.Id; var s = SelectedSet.Id; var target = owner.Version; return owner.RunToolAsync("Traceability verification (read-only)", async (t, p) => { var result = await runtime.Service.VerifyBaselineSet(path, d, s, target, t).ConfigureAwait(false); return () => Report = "Verified=" + result.Verified + "\n" + string.Join("\n", result.Problems.Concat(result.Missing).Concat(result.Unexpected)); }); }
        private Task LoadHistory()
        { var runtime = Runtime; return owner.RunToolAsync("Native set job history", (t, p) => { t.ThrowIfCancellationRequested(); var list = runtime.Service.ListJobs(); return Task.FromResult<System.Action>(() => { history = list; Changed(nameof(History)); }); }); }
        private Task OpenJob()
        { var runtime = Runtime; var id = SelectedHistory.JobId; Invalidate(); return owner.RunToolAsync("Open recorded native job — no writes", (t, p) => { var record = runtime.Service.GetJob(id); return Task.FromResult<System.Action>(() => Apply(record)); }); }
        private Task Reconcile()
        { var runtime = Runtime; var id = job.JobId; Invalidate(); return owner.RunToolAsync("Native set reconciliation (read-only, no retry)", async (t, p) => { var result = await runtime.Service.ReconcileAsync(id, t).ConfigureAwait(false); return () => Apply(result); }); }
        private Task Export()
        { var runtime = Runtime; var id = job.JobId; var request = new ExportRequest(Path.Combine(ExportRoot, id.ToString("N") + "-" + Guid.NewGuid().ToString("N")), Format); return owner.RunToolAsync("Export native set journal/report", async (t, p) => { var result = await runtime.Reports.ExportAsync(id, request, token: t).ConfigureAwait(false); return () => Report = result.Status + " · " + result.DirectoryPath; }); }
    }
    public sealed class NativeSetMemberRow
    {
        public NativeSetMemberRow(BaselineSetModuleReference module, BaselineSetModuleBaseline baseline, bool absenceVerified = true) { ModuleId = module.ModuleId; FullPath = module.FullPath; Version = baseline?.ModuleBaselineVersion ?? (absenceVerified ? "Missing" : "Unknown"); Resolution = baseline?.Resolution ?? (absenceVerified ? "Missing" : "Unknown"); Missing = baseline == null && absenceVerified; }
        public string ModuleId { get; } public string FullPath { get; } public string Version { get; } public string Resolution { get; } public bool Missing { get; }
    }
}
