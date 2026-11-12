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
    public sealed class BaselineVisualNode
    {
        public TraceabilityNode Node { get; set; }
        public double X { get; set; } public double Y { get; set; }
        public string Id => Node.Id;
        public string Label => Node.Role + " · #" + Node.AbsoluteNumber + "\n" + Node.Version;
    }
    public sealed class BaselineVisualEdge
    {
        public TraceabilityEdge Edge { get; set; }
        public BaselineVisualNode Source { get; set; }
        public BaselineVisualNode Target { get; set; }
    }
    public sealed class BaselineGraphScene
    {
        public IReadOnlyList<BaselineVisualNode> Nodes { get; set; } = Array.Empty<BaselineVisualNode>();
        public IReadOnlyList<BaselineVisualEdge> Edges { get; set; } = Array.Empty<BaselineVisualEdge>();
        public double Width { get; set; } = 1000; public double Height { get; set; } = 400;
    }
    public sealed class BaselineTraceabilityViewModel : ObservableModel
    {
        private readonly WorkspaceViewModel owner; private readonly List<UiCommand> commands = new List<UiCommand>();
        private string folder = "/Traceability", definition = MockBaselineSetVerificationAdapter.DemoDefinitionId, set = MockBaselineSetVerificationAdapter.DemoSetId,
            links = MockBaselineSetVerificationAdapter.DemoLinkPath, selectedModules = "", policy = BaselineVerificationPolicyProfile.MockExampleJson,
            search = "", roleFilter = "All", findingFilter = "All", selectedNode, selectedEdge, details = "Select a versioned node or link. Object Number / display ID is not an identity key.",
            exportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DoorsIntegration", "BaselineVerification"), depth = "3";
        private BaselineSetVerificationResult result; private BaselineLinkSnapshot reference; private BaselineGraphScene scene = new BaselineGraphScene();
        private ExportFormat format = ExportFormat.Json; private TraceabilityDirection direction = TraceabilityDirection.Outgoing; private HashSet<string> impact;
        public BaselineTraceabilityViewModel(WorkspaceViewModel owner)
        {
            this.owner = owner;
            DemoCommand = Command(() => { Folder = "/Traceability"; DefinitionId = MockBaselineSetVerificationAdapter.DemoDefinitionId; SetId = MockBaselineSetVerificationAdapter.DemoSetId; LinkModules = MockBaselineSetVerificationAdapter.DemoLinkPath; PolicyJson = BaselineVerificationPolicyProfile.MockExampleJson; SelectedModuleIds = ""; return Task.CompletedTask; }, () => owner.Mode == ClientMode.Mock);
            UseNativeSelectionCommand = Command(() => { var native = owner.BaselineManager.Native; Folder = native.Folder; DefinitionId = native.Definition.Id; SetId = native.SelectedSet.Id; return Task.CompletedTask; }, () => owner.BaselineManager.Native.Definition != null && owner.BaselineManager.Native.SelectedSet != null);
            VerifyCommand = Command(Verify); CaptureReferenceCommand = Command(CaptureReference, () => result?.Snapshot?.Context.MetadataComplete == true);
            ImportReferenceCommand = Command(ImportReference, () => result?.Snapshot?.Context.Set != null && owner.Session.NativeBaselineSets != null);
            ClearReferenceCommand = Command(() => { reference = null; Changed(nameof(ReferenceLabel)); return Task.CompletedTask; });
            ExportCommand = Command(Export, () => result != null); ImpactCommand = Command(Impact, () => result?.Graph != null && selectedNode != null);
            ClearImpactCommand = Command(() => { impact = null; Project(); return Task.CompletedTask; });
        }
        private BaselineVerificationSession Runtime => owner.Session.BaselineVerification;
        internal void ApplyDashboardContext(DashboardNavigationIntent intent)
        {
            Reset(); folder=intent.Scope?.SetFolder??""; definition=intent.DefinitionIdentity??""; set=intent.SetIdentity??""; selectedModules=intent.ModuleIdentity??"";
            Details="Dashboard source: "+intent.RequirementIdentity+" / "+intent.Version?.Version+" · analysis="+intent.AnalysisIdentity+" · finding="+intent.FindingIdentity+". No verification was implicitly rerun.";
            foreach(string property in new[]{nameof(Folder),nameof(DefinitionId),nameof(SetId),nameof(SelectedModuleIds)}) Changed(property);
        }
        private bool Idle => owner.IsIdle && !owner.IsClosing && Runtime != null;
        public bool IsIdle => Idle;
        public string Banner => owner.ProfileDescription + " · READ-ONLY VERIFICATION · no create/save/link repair";
        public string Folder { get => folder; set { if (owner.IsIdle && Set(ref folder, value)) InputsChanged(); } }
        public string DefinitionId { get => definition; set { if (owner.IsIdle && Set(ref definition, value)) InputsChanged(); } }
        public string SetId { get => set; set { if (owner.IsIdle && Set(ref set, value)) InputsChanged(); } }
        public string LinkModules { get => links; set { if (owner.IsIdle && Set(ref links, value)) InputsChanged(); } }
        public string SelectedModuleIds { get => selectedModules; set { if (owner.IsIdle && Set(ref selectedModules, value)) InputsChanged(); } }
        public string PolicyJson { get => policy; set { if (owner.IsIdle && Set(ref policy, value)) InputsChanged(); } }
        public string Search { get => search; set { if (owner.IsIdle && Set(ref search, value)) Project(); } }
        public string RoleFilter { get => roleFilter; set { if (owner.IsIdle && Set(ref roleFilter, value)) Project(); } }
        public string FindingFilter { get => findingFilter; set { if (Set(ref findingFilter, value)) Changed(nameof(Findings)); } }
        public string ExportRoot { get => exportRoot; set { if (owner.IsIdle) Set(ref exportRoot, value); } }
        public ExportFormat Format { get => format; set { if (owner.IsIdle) Set(ref format, value); } }
        public string ImpactDepth { get => depth; set { if (owner.IsIdle) Set(ref depth, value); } }
        public TraceabilityDirection ImpactDirection { get => direction; set { if (owner.IsIdle) Set(ref direction, value); } }
        public string SelectedNodeId { get => selectedNode; set { if (Set(ref selectedNode, value)) { NodeDetails(); Refresh(); } } }
        public string SelectedEdgeId { get => selectedEdge; set { if (Set(ref selectedEdge, value)) EdgeDetails(); } }
        public string Details { get => details; private set => Set(ref details, value); }
        public BaselineGraphScene Scene { get => scene; private set => Set(ref scene, value); }
        public BaselineSetVerificationResult CurrentResult => result;
        public IEnumerable<string> Roles => new[] { "All" }.Concat(result?.Graph?.Nodes.Values.Select(n => n.Role).Distinct().OrderBy(n => n) ?? Enumerable.Empty<string>());
        public string[] FindingFilters => new[] { "All", "Error", "Warning", "Information", "Unknown / AccessDenied" };
        public Array Formats => Enum.GetValues(typeof(ExportFormat)); public Array Directions => Enum.GetValues(typeof(TraceabilityDirection));
        public string ReferenceLabel => reference == null ? "No reference — historical loss is not inferred" : "Reference captured " + reference.CapturedAtUtc.ToString("O") + " · " + reference.Context.Source + " · " + reference.SnapshotId;
        public string Overview => result == null ? "Select a Native set or load the Mock demo. No DOORS data is modified." :
            (result.Snapshot.Context.Definition?.Name ?? "Definition unavailable") + " · Set " + result.Snapshot.Context.Set?.Version + " · Suffix=" + result.Snapshot.Context.Set?.Version?.Suffix +
            " · " + (result.Snapshot.Context.Set == null ? "Open/Closed unknown" : result.Snapshot.Context.Set.IsOpen ? "OPEN" : "CLOSED") +
            " · Modules=" + result.Snapshot.Context.Modules.Count + " · Selected=" + result.Snapshot.Context.Modules.Count(m => m.SelectedForVerification) + " · Missing=" + result.Snapshot.Context.Modules.Count(m => m.State == BaselineVerificationReadState.Missing) +
            " · Denied/Unknown=" + result.Snapshot.Context.Modules.Count(m => m.State == BaselineVerificationReadState.AccessDenied || m.State == BaselineVerificationReadState.Unknown);
        public string Summary => result == null ? "No verification result" : "Job=" + result.VerificationJobId + " · " + result.Status + " · " + result.Snapshot.Context.Source + " · Errors=" + result.Findings.Count(f => f.Severity == TraceabilitySeverity.Error) + " · Warnings=" + result.Findings.Count(f => f.Severity == TraceabilitySeverity.Warning);
        public string Coverage => result?.Coverage == null ? "Coverage unavailable" : "Confirmed coverage=" + (result.Coverage.TraceabilityCoveragePercentage.HasValue ? result.Coverage.TraceabilityCoveragePercentage.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%" : "Unknown") +
            (result.Coverage.TraceabilityCoveragePercentage.HasValue ? "" : " (overall denominator unknown)") + " · Required=" + result.Coverage.RequirementsRequiringLinks + " · Valid=" + result.Coverage.RequirementsWithValidLinks + " · Missing=" + result.Coverage.RequirementsMissingLinks + " · Unresolved=" + result.Coverage.UnresolvedRequirements +
            " · Known-scope bounds=" + result.Coverage.KnownScopeLowerBoundPercentage?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "–" + result.Coverage.KnownScopeUpperBoundPercentage?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "% · Unenumerated modules=" + result.Coverage.UnenumeratedModules + " · Broken chains=" + result.Coverage.BrokenTraceabilityChains;
        public string RenderSummary => "Display limit: " + Scene.Nodes.Count + "/" + (result?.Graph?.Nodes.Count ?? 0) + " nodes, " + Scene.Edges.Count + "/" + (result?.Graph?.Edges.Count ?? 0) + " edges. Export retains the full captured scope.";
        public IEnumerable<ModuleBaselineVerificationResult> Modules => result?.Snapshot.Context.Modules ?? Enumerable.Empty<ModuleBaselineVerificationResult>();
        public IEnumerable<TraceabilityViolation> Findings => (result?.Findings ?? new List<TraceabilityViolation>()).Where(f => FindingFilter == "All" || FindingFilter == "Unknown / AccessDenied" && (f.Uncertain || f.Code == "AccessDenied") || f.Severity.ToString() == FindingFilter).Take(2000);
        public IEnumerable<BaselineLinkVerificationResult> VerifiedLinks => (result?.Links ?? new List<BaselineLinkVerificationResult>()).Take(2000);
        public UiCommand DemoCommand { get; } public UiCommand UseNativeSelectionCommand { get; } public UiCommand VerifyCommand { get; } public UiCommand CaptureReferenceCommand { get; }
        public UiCommand ClearReferenceCommand { get; } public UiCommand ExportCommand { get; } public UiCommand ImpactCommand { get; } public UiCommand ClearImpactCommand { get; }
        public UiCommand ImportReferenceCommand { get; }
        private UiCommand Command(Func<Task> work, Func<bool> available = null) { var c = new UiCommand(work, () => Idle && (available?.Invoke() ?? true)); commands.Add(c); return c; }
        public void Refresh() { foreach (var c in commands) c.Refresh(); Changed(nameof(Banner)); Changed(nameof(IsIdle)); }
        private void InputsChanged() { result = null; impact = null; roleFilter = "All"; selectedNode = selectedEdge = null; Changed(nameof(RoleFilter)); Notify(); }
        public void Reset() { reference = null; InputsChanged(); Changed(nameof(ReferenceLabel)); }
        private void Notify() { foreach (string p in new[] { nameof(CurrentResult), nameof(Overview), nameof(Summary), nameof(Coverage), nameof(Modules), nameof(Findings), nameof(VerifiedLinks), nameof(Roles) }) Changed(p); Project(); Refresh(); }
        private Task Verify()
        {
            var runtime = Runtime; var capturedPolicy = PolicyJson; var capturedFolder = Folder; var d = DefinitionId; var s = SetId; var linkPaths = Split(LinkModules); var ids = Split(SelectedModuleIds); var target = owner.Version; var capturedReference = reference;
            return owner.RunToolAsync("Baseline traceability verification (read-only)", async (token, p) =>
            {
                var profile = BaselineVerificationPolicyProfile.ReadJson(capturedPolicy); var request = new BaselineSetVerificationRequest(profile.ProjectId, capturedFolder, d, s, profile.ToPolicy(), linkPaths, ids,
                    profile.ModuleRoles, capturedReference, target: target, typeAttribute: profile.TypeAttribute ?? "", statusAttribute: profile.StatusAttribute ?? "");
                var captured = await runtime.Service.VerifyAsync(request, p, token).ConfigureAwait(false);
                return () => { result = captured; impact = null; selectedNode = selectedEdge = null; Changed(nameof(SelectedNodeId)); Changed(nameof(SelectedEdgeId)); Details = "Select a node/link from the current verification result."; Notify(); owner.ReportToolOutcome("Baseline traceability: " + result.Status + " · " + result.Snapshot.Context.Source); };
            });
        }
        private Task CaptureReference() { reference = result.Snapshot.Copy(); Changed(nameof(ReferenceLabel)); Details = "Captured the observed read-only state now. This does not assert a pre-creation state; use it only as an explicit reference for subsequent comparison."; return Task.CompletedTask; }
        private Task ImportReference()
        { var service = owner.Session.NativeBaselineSets.Service; var captured = result.Snapshot.Context; return owner.RunToolAsync("Import recorded pre-set reference (read-only)", (t, p) =>
            { t.ThrowIfCancellationRequested(); var origin = service.ListJobs().FirstOrDefault(j => j.SetId == captured.Set.Id && j.DefinitionId == captured.Definition.Id && j.Source == captured.Source && j.Before.DatabaseId == captured.EnvironmentId && j.ReferenceLinks != null && (j.Request.Action == BaselineSetAction.CreateMajorSet || j.Request.Action == BaselineSetAction.CreateMinorSet));
                var imported = origin == null ? null : BaselineLinkSnapshotService.ImportNativeReference(origin); return Task.FromResult<Action>(() => { if (imported == null) Details = "No matching recorded pre-set reference. No historical links were inferred."; else { reference = imported; Changed(nameof(ReferenceLabel)); Details = string.Join("\n", imported.Limitations); } }); }); }
        private Task Export()
        { var runtime = Runtime; var captured = result; var root = ExportRoot; var selectedFormat = Format; return owner.RunToolAsync("Baseline verification report export", async (t, p) => { Directory.CreateDirectory(root); var report = await runtime.Reports.ExportAsync(captured, new ExportRequest(Path.Combine(root, captured.VerificationJobId.ToString("N") + "-" + Guid.NewGuid().ToString("N")), selectedFormat), token: t).ConfigureAwait(false); return () => Details = report.Status + " · " + report.DirectoryPath; }); }
        private Task Impact()
        { var runtime = Runtime; var graph = result.Graph; var seed = selectedNode; var capturedDepth = ImpactDepth; var capturedDirection = ImpactDirection;
            return owner.RunToolAsync("Baseline-aware multi-hop impact (read-only)", (t, p) => { if (!int.TryParse(capturedDepth, out int hops) || hops < 0 || hops > 100) throw new ArgumentException("Impact depth must be 0–100."); var reach = runtime.Graphs.AnalyzeImpact(graph, new[] { seed }, capturedDirection, hops, true, 100000, 250000, t); return Task.FromResult<Action>(() => { impact = new HashSet<string>(reach.Visits.Select(v => v.NodeId)); Project(); Details = "Impact visits=" + reach.Visits.Count + " · includes possible edges=" + reach.IncludesPossibleEdges + " · conclusive=" + reach.IsConclusiveWithinRequestedScope + " · truncated=" + reach.IsTruncated; }); }); }
        private void Project()
        {
            if (result?.Graph == null) { Scene = new BaselineGraphScene(); Changed(nameof(RenderSummary)); return; }
            var graph = result.Graph; var filtered = graph.Nodes.Values.Where(n => (RoleFilter == "All" || n.Role == RoleFilter) && (impact == null || impact.Contains(n.Id)) &&
                (string.IsNullOrEmpty(Search) || (n.ModulePath + " " + n.Role + " " + n.ObjectIdentifier + " " + n.Version + " " + n.AbsoluteNumber).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(n => n.Role).ThenBy(n => n.AbsoluteNumber).Take(300).ToArray();
            var preferred = graph.Request.Policy.LinkRules.SelectMany(r => r.Direction == TraceabilityDirection.Outgoing ? new[] { r.AnchorRole }.Concat(r.CounterpartRoles) : r.CounterpartRoles.Concat(new[] { r.AnchorRole }));
            var visibleRoles = new HashSet<string>(filtered.Select(n => n.Role)); var columns = preferred.Concat(visibleRoles.OrderBy(r => r)).Where(visibleRoles.Contains).Distinct().ToArray(); var rows = new Dictionary<string, int>();
            var nodes = filtered.Select(n => { rows.TryGetValue(n.Role, out int row); rows[n.Role] = row + 1; return new BaselineVisualNode { Node = n, X = 35 + Array.IndexOf(columns, n.Role) * 260, Y = 40 + row * 86 }; }).ToArray();
            var index = nodes.ToDictionary(n => n.Id); var edges = graph.Edges.Where(e => index.ContainsKey(e.SourceId) && index.ContainsKey(e.TargetId)).Take(1000).Select(e => new BaselineVisualEdge { Edge = e, Source = index[e.SourceId], Target = index[e.TargetId] }).ToArray();
            Scene = new BaselineGraphScene { Nodes = nodes, Edges = edges, Width = Math.Max(1000, columns.Length * 260 + 70), Height = Math.Max(350, (rows.Count == 0 ? 0 : rows.Values.Max()) * 86 + 80) }; Changed(nameof(RenderSummary));
        }
        private void NodeDetails()
        {
            if (result?.Graph == null || selectedNode == null || !result.Graph.Nodes.TryGetValue(selectedNode, out var n)) return;
            var record = result.Snapshot.Requirements.FirstOrDefault(r => r.NodeIdentity == n.VerificationNodeIdentity);
            Details = "Module=" + n.ModulePath + "\nStable module ID=" + n.QualifiedModuleId + "\nModule baseline=" + n.Version + "\nBaseline identity=" + n.ModuleBaselineIdentity + "\nPersistent object identity=" + n.ObjectIdentity + "\nAbsolute Number=" + n.AbsoluteNumber + "\nType=" + n.RequirementType + "\nStatus=" + n.RequirementStatus + "\nAccess=" + string.Join(",", n.AccessStatuses) +
                "\nHeading=" + record?.Heading + "\nText=" + record?.Text + "\nRich text (original representation):\n" + record?.RichText + "\nFindings:\n" + string.Join("\n", result.Findings.Where(f => f.NodeIdentity == n.VerificationNodeIdentity).Select(f => f.Code + ": " + f.Explanation));
        }
        private void EdgeDetails()
        {
            var edge = result?.Graph?.Edges.FirstOrDefault(e => e.Id == selectedEdge); if (edge == null) return;
            var from = result.Graph.Nodes[edge.SourceId]; var to = result.Graph.Nodes[edge.TargetId];
            Details = "Source=" + from.ModulePath + " @ " + from.Version + " #" + from.AbsoluteNumber + "\nSource baseline identity=" + from.ModuleBaselineIdentity + "\nTarget=" + to.ModulePath + " @ " + to.Version + " #" + to.AbsoluteNumber + "\nTarget baseline identity=" + to.ModuleBaselineIdentity + "\nLink module=" + edge.LinkModulePath + "\nStable link-module ID=" + edge.QualifiedLinkModuleId + "\nResolution=" + edge.State + "\nEchoed=" + edge.IsEchoed + "\nEcho alone is not proof of missing data.";
        }
        private static string[] Split(string value) => (value ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
    }
}
