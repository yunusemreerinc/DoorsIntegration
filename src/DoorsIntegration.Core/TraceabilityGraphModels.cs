using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class TraceabilityNode
    {
        private readonly List<LinkEndpointResolution> access = new List<LinkEndpointResolution>();
        internal TraceabilityNode(string repository, string module, string nativeId, LinkVersionKind kind, string version, bool selectedVersion,
            int? absolute, string discriminator, string role, string scopeId)
        {
            RepositoryId = repository; ModulePath = module; QualifiedModuleId = nativeId; VersionKind = kind; Version = version;
            HasSelectedVersionBinding = selectedVersion; AbsoluteNumber = absolute; Role = role; ScopeId = scopeId;
            Id = "n" + GraphGuard.Hash(repository, string.IsNullOrEmpty(nativeId) ? "PathOnly" : "Native", nativeId ?? module,
                kind.ToString(), selectedVersion ? "Selected" : "Opaque", version, absolute?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", discriminator);
            AccessStatuses = access.AsReadOnly();
        }
        internal void Observe(LinkEndpointResolution status) { if (!access.Contains(status)) access.Add(status); }
        public string Id { get; }
        public string RepositoryId { get; }
        public string ModulePath { get; }
        public string QualifiedModuleId { get; }
        public string IdentityEvidence => string.IsNullOrEmpty(QualifiedModuleId) ? "PathScoped_NoNativeModuleId" : "AdapterObservedQualifiedModuleId";
        public string ObjectIdentityEvidence => AbsoluteNumber.HasValue ? "AbsoluteNumberWithinVersionedModule" : "UnresolvedReference_NoPersistentObjectIdentity";
        public LinkVersionKind VersionKind { get; }
        public string Version { get; }
        public bool HasSelectedVersionBinding { get; }
        public int? AbsoluteNumber { get; }
        public string Role { get; }
        public string ScopeId { get; }
        public string ObjectIdentifier { get; internal set; } = "";
        public string ModuleBaselineIdentity { get; internal set; }
        public string ObjectIdentity { get; internal set; }
        public string VerificationNodeIdentity { get; internal set; }
        public string RequirementType => Role;
        public string RequirementStatus { get; internal set; }
        public bool IsInventoryObject { get; internal set; }
        public bool IsDeleted { get; internal set; }
        public IReadOnlyList<LinkEndpointResolution> AccessStatuses { get; }
        public bool HasUncertainAccess => access.Any(a => a != LinkEndpointResolution.Resolved && a != LinkEndpointResolution.DeletedObject);
    }

    public sealed class TraceabilityObservation
    {
        internal TraceabilityObservation(string scopeId, LinkRecord record) { ScopeId = scopeId; Record = record; Id = "o" + GraphGuard.Hash(scopeId, record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        public string Id { get; }
        public string ScopeId { get; }
        // Original endpoint baseline labels, access, surrogate and source-reference evidence are retained verbatim.
        public LinkRecord Record { get; }
        public string EdgeId { get; internal set; }
    }

    public sealed class TraceabilityEdge
    {
        private readonly List<string> observations = new List<string>();
        private readonly List<LinkDirection> directions = new List<LinkDirection>();
        internal TraceabilityEdge(string source, string target, string module, string nativeId, TraceabilityEdgeState state, LinkRecordKind kind, bool echoed)
        { SourceId = source; TargetId = target; LinkModulePath = module; QualifiedLinkModuleId = nativeId; State = state; Kind = kind; IsEchoed = echoed;
            Id = "e" + GraphGuard.Hash(source, target, module, nativeId ?? "", state.ToString(), kind.ToString(), echoed.ToString());
            ObservationIds = observations.AsReadOnly(); ObservedDirections = directions.AsReadOnly(); }
        internal void Observe(TraceabilityObservation observation) { observations.Add(observation.Id); if (!directions.Contains(observation.Record.Direction)) directions.Add(observation.Record.Direction); observation.EdgeId = Id; }
        public string Id { get; }
        public string SourceId { get; }
        public string TargetId { get; }
        public string LinkModulePath { get; }
        public string QualifiedLinkModuleId { get; }
        public string LinkModuleIdentityEvidence => string.IsNullOrEmpty(QualifiedLinkModuleId) ? "PathOnly_NativeIdUnavailable" : "AdapterObservedQualifiedModuleId";
        public TraceabilityEdgeState State { get; }
        public LinkRecordKind Kind { get; }
        public bool IsEchoed { get; }
        // Count of observations, not the physical multiplicity of DOORS links.
        public IReadOnlyList<string> ObservationIds { get; }
        public IReadOnlyList<LinkDirection> ObservedDirections { get; }
    }

    public sealed class TraceabilityScopeEvidence
    {
        internal TraceabilityScopeEvidence(TraceabilityModuleScope module, RequirementReadSummary requirements)
        { Module = module; Requirements = requirements; Id = "s" + requirements.CorrelationId.ToString("N"); IsComplete = requirements.IsCompleteForReadableObjects; }
        internal TraceabilityScopeEvidence(TraceabilityModuleScope module, LinkReadSummary links)
        { Module = module; Links = links; Id = "s" + links.CorrelationId.ToString("N"); IsComplete = links.IsCompleteForReadableScope; }
        public string Id { get; }
        public TraceabilityModuleScope Module { get; }
        public RequirementReadSummary Requirements { get; }
        public LinkReadSummary Links { get; }
        public bool IsComplete { get; }
    }
    public sealed class TraceabilityFinding
    {
        internal TraceabilityFinding(string code, TraceabilityState state, TraceabilitySeverity severity, string node, string edge, string rule,
            string scope, string message, int? count = null)
        { Code = code; State = state; Severity = severity; NodeId = node; EdgeId = edge; RuleId = rule; ScopeId = scope; Message = message; ObservedCount = count;
            Id = "f" + GraphGuard.Hash(code, node ?? "", edge ?? "", rule ?? "", scope ?? "", message); }
        public string Id { get; }
        public string Code { get; }
        public TraceabilityState State { get; }
        public TraceabilitySeverity Severity { get; }
        public string NodeId { get; }
        public string EdgeId { get; }
        public string RuleId { get; }
        public string ScopeId { get; }
        public string Message { get; }
        public int? ObservedCount { get; }
    }
    public sealed class TraceabilityCycle
    {
        internal TraceabilityCycle(string rule, TraceabilityCycleBehavior behavior, IEnumerable<string> nodes, IEnumerable<string> edges)
        { RuleId = rule; Behavior = behavior; NodeIds = Array.AsReadOnly(nodes.OrderBy(n => n, StringComparer.Ordinal).ToArray()); EdgeIds = Array.AsReadOnly(edges.OrderBy(e => e, StringComparer.Ordinal).ToArray()); Id = "c" + GraphGuard.Hash(new[] { rule }.Concat(NodeIds).Concat(EdgeIds).ToArray()); }
        public string Id { get; }
        public string RuleId { get; }
        public TraceabilityCycleBehavior Behavior { get; }
        // Strongly connected component containing a cycle, not an enumeration of all elementary cycles.
        public IReadOnlyList<string> NodeIds { get; }
        public IReadOnlyList<string> EdgeIds { get; }
    }
    public sealed class TraceabilityGraph
    {
        private readonly bool? observedCompleteScope;
        internal TraceabilityGraph(Guid id, TraceabilityGraphRequest request, IDictionary<string, TraceabilityNode> nodes, IEnumerable<TraceabilityEdge> edges,
            IEnumerable<TraceabilityObservation> observations, IEnumerable<TraceabilityScopeEvidence> scopes, IEnumerable<TraceabilityFinding> findings, IEnumerable<TraceabilityCycle> cycles, bool? observedCompleteScope = null)
        { GraphId = id; Request = request; BuiltAtUtc = DateTime.UtcNow; Nodes = new ReadOnlyDictionary<string, TraceabilityNode>(new Dictionary<string, TraceabilityNode>(nodes, StringComparer.Ordinal));
            Edges = Array.AsReadOnly(edges.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray()); Observations = Array.AsReadOnly(observations.ToArray()); Scopes = Array.AsReadOnly(scopes.ToArray());
            Findings = Array.AsReadOnly(findings.ToArray()); Cycles = Array.AsReadOnly(cycles.ToArray()); this.observedCompleteScope = observedCompleteScope; }
        public Guid GraphId { get; }
        public DateTime BuiltAtUtc { get; }
        public TraceabilityGraphRequest Request { get; }
        public IReadOnlyDictionary<string, TraceabilityNode> Nodes { get; }
        public IReadOnlyList<TraceabilityEdge> Edges { get; }
        public IReadOnlyList<TraceabilityObservation> Observations { get; }
        public IReadOnlyList<TraceabilityScopeEvidence> Scopes { get; }
        public IReadOnlyList<TraceabilityFinding> Findings { get; }
        public IReadOnlyList<TraceabilityCycle> Cycles { get; }
        public bool IsCompleteWithinRequestedScope => observedCompleteScope ?? Scopes.All(s => s.IsComplete);
        public bool HasExternalBoundary => Nodes.Values.Any(n => !n.IsInventoryObject);
        public string RepositoryEvidence => "CallerBoundRepository_NonAtomicReadSnapshots";
    }
    public sealed class TraceabilityImpactVisit
    {
        internal TraceabilityImpactVisit(string node, int distance, string predecessor, string edge, bool confirmed)
        { NodeId = node; Distance = distance; PredecessorId = predecessor; EdgeId = edge; IsConfirmedPath = confirmed; }
        public string NodeId { get; }
        public int Distance { get; }
        public string PredecessorId { get; }
        public string EdgeId { get; }
        public bool IsConfirmedPath { get; }
    }
    public sealed class TraceabilityImpactResult
    {
        internal TraceabilityImpactResult(TraceabilityGraph graph, TraceabilityDirection direction, int depth, bool possible, IEnumerable<TraceabilityImpactVisit> visits,
            IEnumerable<string> edges, IEnumerable<string> boundary, bool truncated)
        { GraphId = graph.GraphId; Direction = direction; MaxDepth = depth; IncludesPossibleEdges = possible; Visits = Array.AsReadOnly(visits.ToArray());
            TraversedEdgeIds = Array.AsReadOnly(edges.OrderBy(e => e, StringComparer.Ordinal).ToArray()); BoundaryNodeIds = Array.AsReadOnly(boundary.OrderBy(e => e, StringComparer.Ordinal).ToArray());
            IsTruncated = truncated; GraphScopeComplete = graph.IsCompleteWithinRequestedScope; }
        public Guid GraphId { get; }
        public TraceabilityDirection Direction { get; }
        public int MaxDepth { get; }
        public bool IncludesPossibleEdges { get; }
        public IReadOnlyList<TraceabilityImpactVisit> Visits { get; }
        public IReadOnlyList<string> TraversedEdgeIds { get; }
        public IReadOnlyList<string> BoundaryNodeIds { get; }
        public bool IsTruncated { get; }
        public bool GraphScopeComplete { get; }
        public bool IsConclusiveWithinRequestedScope => GraphScopeComplete && !IsTruncated && BoundaryNodeIds.Count == 0 && Visits.All(v => v.IsConfirmedPath);
    }
    public sealed class TraceabilityGraphProgress
    {
        internal TraceabilityGraphProgress(string phase, int scopes, int nodes, int observations) { Phase = phase; ScopesRead = scopes; Nodes = nodes; Observations = observations; }
        public string Phase { get; }
        public int ScopesRead { get; }
        public int Nodes { get; }
        public int Observations { get; }
    }
    public interface ITraceabilityGraphService
    {
        Task<TraceabilityGraph> BuildAsync(TraceabilityGraphRequest request, IProgress<TraceabilityGraphProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken));
        TraceabilityImpactResult AnalyzeImpact(TraceabilityGraph graph, IEnumerable<string> seeds, TraceabilityDirection direction = TraceabilityDirection.Outgoing,
            int maxDepth = 10, bool includePossibleEdges = false, int maxVisits = 100000, int maxTraversedEdges = 250000, CancellationToken cancellationToken = default(CancellationToken));
    }
    public interface ITraceabilityGraphExporter
    {
        void Export(Stream destination, TraceabilityGraph graph, TraceabilityGraphFormat format, TraceabilityImpactResult impact = null, CancellationToken cancellationToken = default(CancellationToken));
        void ExportFile(string path, TraceabilityGraph graph, TraceabilityGraphFormat format, TraceabilityImpactResult impact = null, CancellationToken cancellationToken = default(CancellationToken));
    }
}
