using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Wpf.Infrastructure;
using DoorsIntegration.Wpf.Services;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed class ImpactBaselineChoice
    {
        public int Major { get; set; } public int Minor { get; set; } public string Suffix { get; set; }
        public string Label => Major + "." + Minor + "[" + Suffix + "]";
    }
    public sealed class ImpactChangeRow
    {
        public RequirementChange Change { get; set; } public AttributeChange Attribute { get; set; } public int ImpactCount { get; set; }
        public string RequirementId => Change.DisplayIdentifier; public string Module => Change.Module;
        public string ChangeType => Change.PrimaryType + " / " + Change.Flags; public string AttributeName => Attribute?.Name ?? "Hierarchy / link / lifecycle";
        public string OldValue => Clip(Attribute?.Before?.PlainText); public string NewValue => Clip(Attribute?.After?.PlainText);
        private static string Clip(string text) => text == null || text.Length <= 2048 ? text : text.Substring(0, 2048) + "… [full value in export]";
        public ImpactSeverity Severity => Change.Severity;
    }
    public sealed class ChangeImpactViewModel : ObservableModel
    {
        private readonly WorkspaceViewModel owner; private readonly List<UiCommand> commands = new List<UiCommand>();
        private ChangeImpactResult result; private ImpactChangeRow selectedChange; private ImpactAnalysisFinding selectedFinding;
        private string environment = "mock-verification-database", repository = "mock-verification-repository", module = "SYS", path = "/Traceability/SYS",
            sourceMajor = "2", sourceMinor = "0", sourceSuffix = "R0", targetMajor = "3", targetMinor = "0", targetSuffix = "R1", folder = "/Traceability",
            definition = MockBaselineSetVerificationAdapter.DemoDefinitionId, sourceSet = MockBaselineSetVerificationAdapter.DemoSetId, targetSet = "Mock-Set-B",
            links = MockBaselineSetVerificationAdapter.DemoLinkPath, depth = "8", policy = Encoding.UTF8.GetString(ImpactSerialization.Bytes(new ImpactAnalysisPolicy())), policyPath = "",
            search = "", details = "Select a change, finding or versioned graph node.", nodeId, reviewer = Environment.UserName, comments = "",
            exportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DoorsIntegration", "ChangeImpact");
        private ChangeImpactComparisonKind kind; private ImpactGraphChoice graphChoice = ImpactGraphChoice.Both; private ImpactGraphChoice displayGraph;
        private ImpactReviewStatus disposition = ImpactReviewStatus.InReview; private ExportFormat format = ExportFormat.Json;
        private int page, graphLimit = 100, displayDepth = 3; private bool unchanged;
        private IReadOnlyList<ImpactChangeRow> changeRows = Array.Empty<ImpactChangeRow>(); private IReadOnlyList<ImpactAnalysisFinding> findings = Array.Empty<ImpactAnalysisFinding>();
        private BaselineGraphScene scene = new BaselineGraphScene(); private IReadOnlyList<ImpactBaselineChoice> baselineChoices = Array.Empty<ImpactBaselineChoice>();
        private ImpactBaselineChoice selectedSourceBaseline, selectedTargetBaseline;
        public ChangeImpactViewModel(WorkspaceViewModel owner)
        {
            this.owner = owner;
            RunCommand = Command(Run); ExportCommand = Command(Export, () => result != null);
            LoadPolicyCommand = Command(LoadPolicy); LoadBaselinesCommand = Command(LoadBaselines);
            ObserveContextCommand = Command(ObserveContext, () => owner.Mode == ClientMode.RealDoors && owner.Session.NativeReads != null);
            UseNativeSetCommand = Command(() => { var n = owner.BaselineManager.Native; Folder = n.Folder; DefinitionId = n.Definition.Id; TargetSetId = n.SelectedSet.Id; return Task.CompletedTask; }, () => owner.BaselineManager.Native.Definition != null && owner.BaselineManager.Native.SelectedSet != null);
            NextCommand = Command(() => { page++; ProjectRows(); return Task.CompletedTask; }, () => (page + 1) * 100 < FilteredChanges().Count());
            PreviousCommand = Command(() => { page--; ProjectRows(); return Task.CompletedTask; }, () => page > 0);
            ExpandGraphCommand = Command(() => { graphLimit = Math.Min(300, graphLimit + 100); ProjectGraph(); return Task.CompletedTask; }, () => result != null && graphLimit < 300);
            SaveReviewCommand = Command(() => SaveReview(Disposition), CanReview); ApproveImpactCommand = Command(() => SaveReview(ImpactReviewStatus.ConfirmedImpacted), CanReview); RejectImpactCommand = Command(() => SaveReview(ImpactReviewStatus.NotImpacted), CanReview);
        }
        private ChangeImpactSession Runtime => owner.Session.ChangeImpact;
        private UiCommand Command(Func<Task> action, Func<bool> available = null) { var c = new UiCommand(action, () => IsIdle && (available?.Invoke() ?? true)); commands.Add(c); return c; }
        public bool IsIdle => owner.IsIdle && !owner.IsClosing && Runtime != null;
        public string Banner => owner.ProfileDescription + " · CHANGE IMPACT · READ-ONLY · local reviewer decisions only";
        public ChangeImpactResult CurrentResult => result;
        public string Summary => result == null ? "Select Source → Target and run analysis. Potential impact requires engineering review." : result.Status + " · changes=" + result.Changes.Count(c => c.IsChanged) + " · unique identified requirement candidates=" + result.UniqueImpactedRequirementCount + " · unverified references=" + result.UnverifiedImpactReferenceCount + " · paths=" + result.ImpactPathCount + " · " + result.ElapsedMilliseconds + " ms" + (result.Truncated ? " · TRUNCATED" : "");
        public string Limitations => result == null ? "Real metadata / historical link evidence remain subject to release-specific validation gates." : string.Join("\n", result.Limitations.Distinct());
        private void Input(ref string field, string value, string name) { if (owner.IsIdle && Set(ref field, value, name)) Reset(); }
        public string EnvironmentId { get => environment; set => Input(ref environment, value, nameof(EnvironmentId)); }
        public string RepositoryId { get => repository; set => Input(ref repository, value, nameof(RepositoryId)); }
        public string ModuleIdentity { get => module; set => Input(ref module, value, nameof(ModuleIdentity)); }
        public string ModulePath { get => path; set => Input(ref path, value, nameof(ModulePath)); }
        public string SourceMajor { get => sourceMajor; set => Input(ref sourceMajor, value, nameof(SourceMajor)); }
        public string SourceMinor { get => sourceMinor; set => Input(ref sourceMinor, value, nameof(SourceMinor)); }
        public string SourceSuffix { get => sourceSuffix; set => Input(ref sourceSuffix, value, nameof(SourceSuffix)); }
        public string TargetMajor { get => targetMajor; set => Input(ref targetMajor, value, nameof(TargetMajor)); }
        public string TargetMinor { get => targetMinor; set => Input(ref targetMinor, value, nameof(TargetMinor)); }
        public string TargetSuffix { get => targetSuffix; set => Input(ref targetSuffix, value, nameof(TargetSuffix)); }
        public string Folder { get => folder; set => Input(ref folder, value, nameof(Folder)); }
        public string DefinitionId { get => definition; set => Input(ref definition, value, nameof(DefinitionId)); }
        public string SourceSetId { get => sourceSet; set => Input(ref sourceSet, value, nameof(SourceSetId)); }
        public string TargetSetId { get => targetSet; set => Input(ref targetSet, value, nameof(TargetSetId)); }
        public string LinkModules { get => links; set => Input(ref links, value, nameof(LinkModules)); }
        public string MaximumDepth { get => depth; set => Input(ref depth, value, nameof(MaximumDepth)); }
        public string PolicyJson { get => policy; set => Input(ref policy, value, nameof(PolicyJson)); }
        public string PolicyPath { get => policyPath; set => Set(ref policyPath, value); }
        public ChangeImpactComparisonKind ComparisonKind { get => kind; set { if (owner.IsIdle && Set(ref kind, value)) Reset(); } }
        public ImpactGraphChoice GraphChoice { get => graphChoice; set { if (owner.IsIdle && Set(ref graphChoice, value)) Reset(); } }
        public ImpactGraphChoice DisplayGraph { get => displayGraph; set { if (owner.IsIdle && Set(ref displayGraph, value)) ProjectGraph(); } }
        public string Search { get => search; set { if (owner.IsIdle && Set(ref search, value)) { page = 0; ProjectRows(); ProjectGraph(); } } }
        public bool ShowUnchanged { get => unchanged; set { if (owner.IsIdle && Set(ref unchanged, value)) { page = 0; ProjectRows(); } } }
        public int DisplayDepth { get => displayDepth; set { if (owner.IsIdle && Set(ref displayDepth, Math.Max(0, Math.Min(100, value)))) ProjectGraph(); } }
        public string Reviewer { get => reviewer; set { Set(ref reviewer, value); Refresh(); } }
        public string ReviewerComments { get => comments; set { Set(ref comments, value); Refresh(); } }
        public ImpactReviewStatus Disposition { get => disposition; set => Set(ref disposition, value); }
        public ExportFormat Format { get => format; set => Set(ref format, value); }
        public string ExportRoot { get => exportRoot; set => Set(ref exportRoot, value); }
        public string Details { get => details; private set => Set(ref details, value); }
        public string SelectedNodeId { get => nodeId; set { if (Set(ref nodeId, value)) ShowNode(); } }
        public ImpactChangeRow SelectedChange { get => selectedChange; set { if (Set(ref selectedChange, value)) { ProjectGraph(); Details = DescribeChange(value); } } }
        public ImpactAnalysisFinding SelectedFinding { get => selectedFinding; set { if (Set(ref selectedFinding, value)) { ReviewerComments = value?.ReviewerComments ?? ""; Disposition = value?.ReviewStatus ?? ImpactReviewStatus.InReview; Details = value == null ? "" : value.Reason + "\n" + PathText(value); Refresh(); } } }
        public IReadOnlyList<ImpactChangeRow> ChangeRows => changeRows;
        public IReadOnlyList<ImpactAnalysisFinding> Findings => findings;
        public IReadOnlyList<ImpactBaselineChoice> BaselineChoices => baselineChoices;
        public ImpactBaselineChoice SelectedSourceBaseline { get => selectedSourceBaseline; set { if (Set(ref selectedSourceBaseline, value) && value != null) { SourceMajor = value.Major.ToString(CultureInfo.InvariantCulture); SourceMinor = value.Minor.ToString(CultureInfo.InvariantCulture); SourceSuffix = value.Suffix; } } }
        public ImpactBaselineChoice SelectedTargetBaseline { get => selectedTargetBaseline; set { if (Set(ref selectedTargetBaseline, value) && value != null) { TargetMajor = value.Major.ToString(CultureInfo.InvariantCulture); TargetMinor = value.Minor.ToString(CultureInfo.InvariantCulture); TargetSuffix = value.Suffix; } } }
        public BaselineGraphScene Scene => scene;
        public string PageLabel => "Changes page " + (page + 1) + " · 100 requirements/page; findings shown " + findings.Count + " / " + (result?.Findings.Count ?? 0);
        public string GraphSummary => "Explicit " + DisplayGraph + " snapshot · nodes=" + scene.Nodes.Count + " (render cap " + graphLimit + "/300) · edges=" + scene.Edges.Count + "/1000 · depth≤" + DisplayDepth + ". Expand loads another bounded slice; analysis/export retain recorded paths.";
        public Array ComparisonKinds => Enum.GetValues(typeof(ChangeImpactComparisonKind)); public Array GraphChoices => Enum.GetValues(typeof(ImpactGraphChoice));
        public ImpactGraphChoice[] DisplayGraphs => new[] { ImpactGraphChoice.Source, ImpactGraphChoice.Target };
        public Array ReviewStatuses => Enum.GetValues(typeof(ImpactReviewStatus)); public Array ExportFormats => Enum.GetValues(typeof(ExportFormat));
        public UiCommand RunCommand { get; } public UiCommand ExportCommand { get; } public UiCommand LoadPolicyCommand { get; } public UiCommand LoadBaselinesCommand { get; } public UiCommand UseNativeSetCommand { get; }
        public UiCommand ObserveContextCommand { get; }
        public UiCommand NextCommand { get; } public UiCommand PreviousCommand { get; } public UiCommand ExpandGraphCommand { get; } public UiCommand SaveReviewCommand { get; } public UiCommand ApproveImpactCommand { get; } public UiCommand RejectImpactCommand { get; }
        public void Refresh() { foreach (var command in commands) command.Refresh(); Changed(nameof(IsIdle)); Changed(nameof(Banner)); }
        public void Reset() { result = null; selectedChange = null; selectedFinding = null; page = 0; graphLimit = 100; changeRows = Array.Empty<ImpactChangeRow>(); findings = Array.Empty<ImpactAnalysisFinding>(); scene = new BaselineGraphScene(); Details = "Input changed; rerun the explicit Source → Target comparison."; Notify(); }
        public void ResetConnection()
        {
            if (owner.Mode == ClientMode.RealDoors) { environment = ""; repository = ""; module = ""; path = owner.ModulePath; }
            else { environment = "mock-verification-database"; repository = "mock-verification-repository"; module = "SYS"; path = "/Traceability/SYS"; }
            baselineChoices = Array.Empty<ImpactBaselineChoice>(); selectedSourceBaseline = selectedTargetBaseline = null;
            foreach (string name in new[] { nameof(EnvironmentId), nameof(RepositoryId), nameof(ModuleIdentity), nameof(ModulePath), nameof(BaselineChoices), nameof(SelectedSourceBaseline), nameof(SelectedTargetBaseline) }) Changed(name);
            Reset();
        }
        internal void ApplyDashboardContext(DashboardNavigationIntent intent)
        {
            Reset(); environment=intent.Environment; repository=intent.DatabaseIdentity;
            module=intent.ModuleIdentity??""; path=intent.ModulePath??"";
            if(intent.Version!=null) { targetMajor=intent.Version.Major.ToString(CultureInfo.InvariantCulture); targetMinor=intent.Version.Minor.ToString(CultureInfo.InvariantCulture); targetSuffix=intent.Version.Suffix; kind=intent.Version.IsCurrent?ChangeImpactComparisonKind.BaselineToCurrent:ChangeImpactComparisonKind.ModuleBaselines; }
            Details="Selected dashboard evidence: analysis="+intent.AnalysisIdentity+"; snapshot="+intent.AnalysisSnapshot+"; finding="+intent.FindingIdentity+"; requirement="+intent.RequirementIdentity+". Source comparison must be selected explicitly before a new analysis.";
            foreach(string property in new[]{nameof(EnvironmentId),nameof(RepositoryId),nameof(ModuleIdentity),nameof(ModulePath),nameof(TargetMajor),nameof(TargetMinor),nameof(TargetSuffix),nameof(ComparisonKind)}) Changed(property);
        }
        private void Notify() { foreach (string n in new[] { nameof(CurrentResult), nameof(Summary), nameof(Limitations), nameof(ChangeRows), nameof(Findings), nameof(Scene), nameof(PageLabel), nameof(GraphSummary), nameof(SelectedChange), nameof(SelectedFinding) }) Changed(n); Refresh(); }
        private Task Run()
        {
            // Capture all editable UI values before dispatching work to the shared background pipeline.
            string json = PolicyJson, depthText = MaximumDepth, env = EnvironmentId, repo = RepositoryId, moduleId = ModuleIdentity, modulePath = ModulePath,
                am = SourceMajor, an = SourceMinor, ax = SourceSuffix, bm = TargetMajor, bn = TargetMinor, bx = TargetSuffix, f = Folder, d = DefinitionId, aset = SourceSetId, bset = TargetSetId, lm = LinkModules;
            var selectedKind = ComparisonKind; var graph = GraphChoice; var version = owner.Version; var runtime = Runtime; Reset();
            return owner.RunToolAsync("Read-only change impact analysis", async (t, p) =>
            {
                var selectedPolicy = new JsonImpactPolicyProvider().ReadJson(json); selectedPolicy.MaximumDepth = int.Parse(depthText, CultureInfo.InvariantCulture); selectedPolicy.Validate();
                var paths = lm.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
                var a = new ImpactSnapshotSelection { Label = "Source " + (selectedKind == ChangeImpactComparisonKind.BaselineSets ? aset : am + "." + an + "[" + ax + "]"), MockSnapshotKey = "A", EnvironmentId = env, RepositoryId = repo, ClientVersion = version, LinkModulePaths = paths };
                var b = new ImpactSnapshotSelection { Label = "Target " + (selectedKind == ChangeImpactComparisonKind.BaselineSets ? bset : selectedKind == ChangeImpactComparisonKind.BaselineToCurrent ? "Current" : bm + "." + bn + "[" + bx + "]"), MockSnapshotKey = "B", EnvironmentId = env, RepositoryId = repo, ClientVersion = version, LinkModulePaths = paths };
                if (selectedKind == ChangeImpactComparisonKind.BaselineSets)
                {
                    var policyGraph = new TraceabilityPolicy(selectedPolicy.Id);
                    a.BaselineSet = new BaselineSetVerificationRequest(selectedPolicy.Id, f, d, aset, policyGraph, paths, target: version, typeAttribute: selectedPolicy.TypeAttribute, statusAttribute: selectedPolicy.StatusAttribute);
                    b.BaselineSet = new BaselineSetVerificationRequest(selectedPolicy.Id, f, d, bset, policyGraph, paths, target: version, typeAttribute: selectedPolicy.TypeAttribute, statusAttribute: selectedPolicy.StatusAttribute);
                }
                else
                {
                    a.Modules.Add(new BaselineVersionReference(repo, moduleId, modulePath, int.Parse(am, CultureInfo.InvariantCulture), int.Parse(an, CultureInfo.InvariantCulture), ax));
                    b.Modules.Add(selectedKind == ChangeImpactComparisonKind.BaselineToCurrent ? new BaselineVersionReference(repo, moduleId, modulePath, 0, 0, "", true) : new BaselineVersionReference(repo, moduleId, modulePath, int.Parse(bm, CultureInfo.InvariantCulture), int.Parse(bn, CultureInfo.InvariantCulture), bx));
                }
                var analysed = await runtime.Service.AnalyzeAsync(new ChangeImpactRequest(a, b, selectedPolicy, selectedKind, graph), p, t).ConfigureAwait(false);
                return () => { result = analysed; displayGraph = graph == ImpactGraphChoice.Target ? ImpactGraphChoice.Target : ImpactGraphChoice.Source; Changed(nameof(DisplayGraph)); ProjectRows(); ProjectGraph(); Details = DescribeChange(selectedChange); Notify(); owner.ReportToolOutcome(Summary); };
            });
        }
        private Task LoadPolicy()
        {
            string file = PolicyPath;
            return owner.RunToolAsync("Load impact project policy", (t, p) => { t.ThrowIfCancellationRequested(); if (new FileInfo(file).Length > 131072) throw new ArgumentException("Policy file exceeds its bound."); string text = File.ReadAllText(file, new UTF8Encoding(false, true)); new JsonImpactPolicyProvider().ReadJson(text); return Task.FromResult<Action>(() => { policy = text; Changed(nameof(PolicyJson)); Reset(); }); });
        }
        private Task ObserveContext()
        {
            var reader = owner.Session.NativeReads; string selectedPath = ModulePath; var target = owner.Version;
            var selectedLinks = LinkModules.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
            return owner.RunToolAsync("Observe read-only DOORS context", async (token, progress) =>
            {
                progress.Report("Reading client session and qualified module identity...");
                var read = await reader.ReadAsync(new NativeReadQuery(target, modules: new[] { selectedPath }, links: selectedLinks), token).ConfigureAwait(false);
                var state = read.ModuleStates.Single();
                if (!read.SessionObserved || state.Kind != DoorsItemKind.FormalModule || state.CanRead != BaselineCheckState.Passed || string.IsNullOrWhiteSpace(state.ModuleIdentity))
                    throw new IntegrationException(IntegrationErrorCode.BaselineComparisonFailed, "Selected path is not an observed readable formal module. Check the path and permissions.");
                return (Action)(() => {
                    environment = read.DatabaseId; repository = read.RepositoryId; module = state.ModuleIdentity; path = state.FullPath;
                    foreach (string name in new[] { nameof(EnvironmentId), nameof(RepositoryId), nameof(ModuleIdentity), nameof(ModulePath) }) Changed(name);
                    Reset(); Details = "Observed " + read.ClientVersion + " / session " + read.SessionId + ". Read-only; exact-client DXL runtime acceptance remains separate.";
                });
            });
        }
        private Task LoadBaselines()
        {
            var service = owner.Session.BaselineMetadata; var selectedModule = new BaselineModuleReference(RepositoryId, ModuleIdentity, ModulePath); var target = owner.Version; bool mock = owner.Mode == ClientMode.Mock;
            return owner.RunToolAsync("Read-only baseline selection metadata", async (t, p) =>
            {
                ImpactBaselineChoice[] choices;
                if (mock) choices = new[] { new ImpactBaselineChoice { Major = 2, Minor = 0, Suffix = "R0" }, new ImpactBaselineChoice { Major = 3, Minor = 0, Suffix = "R1" } };
                else { var history = await service.ReadAsync(new BaselineHistoryRequest(selectedModule, target: target), t).ConfigureAwait(false); choices = history.Snapshot.History.Where(h => h.Existence == BaselineExistenceStatus.Exists).Select(h => new ImpactBaselineChoice { Major = h.Major, Minor = h.Minor, Suffix = h.Suffix }).ToArray(); }
                return () => { baselineChoices = choices; Changed(nameof(BaselineChoices)); Details = choices.Length + " existing baseline choices. Metadata/permissions may limit visibility; manual explicit versions remain available."; };
            });
        }
        private IEnumerable<RequirementChange> FilteredChanges() => (result?.Changes ?? new List<RequirementChange>()).Where(c => (ShowUnchanged || c.IsChanged) && (string.IsNullOrWhiteSpace(Search) || ((c.DisplayIdentifier ?? "") + " " + c.Module + " " + c.PrimaryType).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0));
        private void ProjectRows()
        {
            var candidates = result?.Impacted.GroupBy(i => i.RootChangeIdentity).ToDictionary(g => g.Key, g => g.Select(i => i.RequirementIdentity).Distinct().Count()) ?? new Dictionary<string, int>();
            changeRows = FilteredChanges().Skip(page * 100).Take(100).SelectMany(c => { candidates.TryGetValue(c.Identity, out int count); var attrs = c.Attributes.Where(a => a.SemanticChanged || a.RichRepresentationChanged || a.TypeChanged || !a.IsComparable).ToArray(); return attrs.Length == 0 ? new[] { new ImpactChangeRow { Change = c, ImpactCount = count } } : attrs.Take(64).Select(a => new ImpactChangeRow { Change = c, Attribute = a, ImpactCount = count }); }).ToArray();
            findings = (result?.Findings ?? new List<ImpactAnalysisFinding>()).Where(f => string.IsNullOrWhiteSpace(Search) || (f.Code + " " + f.Reason + " " + f.RootChangeIdentity).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0).Take(1000).ToArray();
            selectedChange = changeRows.FirstOrDefault(); Changed(nameof(ChangeRows)); Changed(nameof(Findings)); Changed(nameof(SelectedChange)); Changed(nameof(PageLabel)); Refresh();
        }
        private TraceabilityGraph DisplayedGraph => DisplayGraph == ImpactGraphChoice.Target ? result?.TargetGraph : result?.SourceGraph;
        private void ProjectGraph()
        {
            var graph = DisplayedGraph; if (graph == null) { scene = new BaselineGraphScene(); Changed(nameof(Scene)); Changed(nameof(GraphSummary)); return; }
            string snapshotIdentity = DisplayGraph == ImpactGraphChoice.Target ? result.TargetGraphSnapshotIdentity : result.SourceGraphSnapshotIdentity;
            string root = selectedChange?.Change.Identity; var levels = new Dictionary<string, int>();
            string NodeKey(TraceabilityNode node) => node.VerificationNodeIdentity ?? node.Id;
            foreach (var n in graph.Nodes.Values.Where(n => root == null || n.ObjectIdentity == root)) levels[NodeKey(n)] = 0;
            foreach (var p in result.Paths.Where(p => p.GraphSnapshotIdentity == snapshotIdentity && (root == null || p.RootChangedRequirement == root))) foreach (var step in p.Steps.Where(s => s.Depth <= DisplayDepth)) { if (!levels.ContainsKey(step.SourceNode)) levels[step.SourceNode] = Math.Max(0, step.Depth - 1); if (!levels.TryGetValue(step.TargetNode, out int level) || level > step.Depth) levels[step.TargetNode] = step.Depth; }
            var columns = new Dictionary<int, int>();
            var nodes = graph.Nodes.Values.Where(n => levels.ContainsKey(NodeKey(n)) && (string.IsNullOrWhiteSpace(Search) || (n.Role + " " + n.ObjectIdentifier + " " + n.ModulePath + " " + n.Version).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(n => levels[NodeKey(n)]).ThenBy(n => n.Id, StringComparer.Ordinal).Take(graphLimit)
                .Select(n => { int column = levels[NodeKey(n)]; columns.TryGetValue(column, out int row); columns[column] = row + 1; return new BaselineVisualNode { Node = n, X = 16 + column * 245, Y = 16 + row * 76 }; }).ToArray();
            var byId = nodes.ToDictionary(n => n.Id); var edges = graph.Edges.Where(e => byId.ContainsKey(e.SourceId) && byId.ContainsKey(e.TargetId)).Take(1000).Select(e => new BaselineVisualEdge { Edge = e, Source = byId[e.SourceId], Target = byId[e.TargetId] }).ToArray();
            scene = new BaselineGraphScene { Nodes = nodes, Edges = edges, Width = Math.Max(1000, nodes.Select(n => n.X + 235).DefaultIfEmpty(1000).Max()), Height = Math.Max(260, nodes.Select(n => n.Y + 80).DefaultIfEmpty(260).Max()) }; Changed(nameof(Scene)); Changed(nameof(GraphSummary));
        }
        private void ShowNode()
        {
            var graph = DisplayedGraph; if (graph == null || SelectedNodeId == null || !graph.Nodes.TryGetValue(SelectedNodeId, out var node)) return;
            var snapshot = DisplayGraph == ImpactGraphChoice.Target ? result.Target : result.Source; var v = snapshot.Requirements.FirstOrDefault(r => r.RequirementVersionIdentity == node.VerificationNodeIdentity);
            string Clip(string text) => text == null || text.Length <= 2048 ? text : text.Substring(0, 2048) + "… [full value in export]";
            Details = (node.ObjectIdentity == null ? "Unverified graph reference=" + node.Id + "\nNo stable requirement identity was inferred." : "Persistent requirement identity=" + node.ObjectIdentity) + "\nVersion identity=" + node.VerificationNodeIdentity + "\nModule=" + node.ModulePath + " · baseline=" + node.Version + "\nAbsolute Number=" + node.AbsoluteNumber + "\n" + string.Join("\n", v?.Attributes.Select(a => a.Key + ": " + Clip(a.Value.PlainText) + " [" + a.Value.PlainStatus + "]") ?? Array.Empty<string>());
        }
        private string PathText(ImpactAnalysisFinding finding) => result?.Paths.FirstOrDefault(candidate => candidate.Identity == finding.PathIdentity) is ImpactPropagationPath p ? string.Join("\n", p.Steps.Select(s => s.Depth + ": " + s.SourceNode + " → " + s.TargetNode + " · " + s.LinkType + " · " + s.BaselineContext)) : "No verified path is available for this finding.";
        private static string DescribeChange(ImpactChangeRow row)
        {
            if (row == null) return "No changed requirement in this filter. Inspect limitations before interpreting absence.";
            string Clip(string text) => text == null || text.Length <= 4000 ? text : text.Substring(0, 4000) + "… [full value in report]";
            return row.Change.Identity + "\n" + row.Change.Module + " · " + row.Change.PrimaryType + " / " + row.Change.Flags + "\nSource=" + row.Change.Before?.Version.Version + " → Target=" + row.Change.After?.Version.Version +
                (row.Attribute == null ? "" : "\nAttribute=" + row.Attribute.Name + " · comparable=" + row.Attribute.IsComparable + "\nOld=" + Clip(row.Attribute.Before?.PlainText) + "\nNew=" + Clip(row.Attribute.After?.PlainText) + "\nOpaque rich-text difference=" + row.Attribute.RichRepresentationChanged + " (not semantic/visual RTF equivalence)\nOld rich=" + Clip(row.Attribute.Before?.RichText) + "\nNew rich=" + Clip(row.Attribute.After?.RichText));
        }
        private bool CanReview() => result != null && selectedFinding != null && !string.IsNullOrWhiteSpace(Reviewer) && !string.IsNullOrWhiteSpace(ReviewerComments);
        private Task SaveReview(ImpactReviewStatus status)
        {
            var runtime = Runtime; var analysed = result; var finding = SelectedFinding; string who = Reviewer, reason = ReviewerComments;
            return owner.RunToolAsync("Save local impact review (no DOORS write)", (t, p) => { t.ThrowIfCancellationRequested(); runtime.Reviews.Decide(analysed, finding.Identity, who, status, reason); return Task.FromResult<Action>(() => { ProjectRows(); Details = "Local decision saved: " + status + ". Analysis evidence/status was not changed."; }); });
        }
        private Task Export()
        {
            var runtime = Runtime; var analysed = result; string root = ExportRoot; var selectedFormat = Format;
            return owner.RunToolAsync("Export impact analysis", async (t, p) => { Directory.CreateDirectory(root); var exported = await runtime.Reports.ExportAsync(analysed, new ExportRequest(Path.Combine(root, analysed.AnalysisId.ToString("N") + "-" + Guid.NewGuid().ToString("N")), selectedFormat), token: t).ConfigureAwait(false); return () => Details = exported.Status + " · " + exported.DirectoryPath; });
        }
    }
}
