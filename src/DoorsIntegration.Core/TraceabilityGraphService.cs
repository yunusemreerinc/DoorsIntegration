using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class TraceabilityGraphService : ITraceabilityGraphService
    {
        // Projection of already captured version evidence; never reopens a current module.
        public static TraceabilityGraph BuildBaselineGraph(BaselineLinkSnapshot snapshot, BaselineSetVerificationRequest request,
            IReadOnlyList<BaselineLinkVerificationResult> links, CancellationToken token = default(CancellationToken)) => BaselineGraphProjection.Build(snapshot, request, links, token);
        private readonly IRequirementService requirements;
        private readonly ILinkService links;
        private readonly IIntegrationLogger logger;
        public TraceabilityGraphService(IRequirementService requirements, ILinkService links, IIntegrationLogger logger)
        { this.requirements = requirements ?? throw new ArgumentNullException(nameof(requirements)); this.links = links ?? throw new ArgumentNullException(nameof(links)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }
        public Task<TraceabilityGraph> BuildAsync(TraceabilityGraphRequest request, IProgress<TraceabilityGraphProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return Task.Run(async () =>
            {
                var id = Guid.NewGuid(); var builder = new GraphCapture(request, cancellationToken);
                Log("TraceabilityGraphStarted", id, "ReadOnly=True; AutomaticRetry=False");
                foreach (var module in request.Modules)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = new RequirementReadRequest(module.ModulePath, baseline: module.Baseline, deletedObjects: DeletedObjectMode.MetadataOnly,
                        allowAttributeDxl: false, openIfNeeded: true, maxObjects: request.MaxNodes, timeout: request.ReadTimeout, connectionMode: request.ConnectionMode, target: request.Target);
                    using (var snapshot = await requirements.ReadAsync(read, cancellationToken).ConfigureAwait(false)) builder.Capture(module, snapshot);
                    Progress(progress, builder.Progress("Requirements"));
                }
                foreach (var module in request.Modules) foreach (string linkModule in request.LinkModulePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = new LinkReadRequest(module.ModulePath, linkModule, LinkDirection.Both, module.Baseline, true, true, false,
                        maxObjects: request.MaxNodes, maxLinks: Math.Min(1000000, request.MaxObservations), timeout: request.ReadTimeout, connectionMode: request.ConnectionMode, target: request.Target);
                    // No retry and no catch-and-continue around native waits. Original completion remains owned by the adapter/host.
                    using (var snapshot = await links.ReadAsync(read, cancellationToken).ConfigureAwait(false)) builder.Capture(module, snapshot, read);
                    Progress(progress, builder.Progress("Links"));
                }
                var graph = builder.Complete(id); Progress(progress, builder.Progress("Completed"));
                Log("TraceabilityGraphCompleted", id, "Nodes=" + graph.Nodes.Count + "; Edges=" + graph.Edges.Count + "; ScopeComplete=" + graph.IsCompleteWithinRequestedScope);
                return graph;
            }, cancellationToken);
        }
        public TraceabilityImpactResult AnalyzeImpact(TraceabilityGraph graph, IEnumerable<string> seeds, TraceabilityDirection direction = TraceabilityDirection.Outgoing,
            int maxDepth = 10, bool includePossibleEdges = false, int maxVisits = 100000, int maxTraversedEdges = 250000, CancellationToken cancellationToken = default(CancellationToken)) =>
            GraphAlgorithms.Impact(graph, seeds, direction, maxDepth, includePossibleEdges, maxVisits, maxTraversedEdges, cancellationToken);
        private void Log(string name, Guid id, string message) { try { logger.Write(IntegrationLogLevel.Information, name, id, message); } catch { } }
        private static void Progress(IProgress<TraceabilityGraphProgress> progress, TraceabilityGraphProgress value) { try { progress?.Report(value); } catch { } }
    }

    internal sealed class GraphCapture
    {
        private readonly TraceabilityGraphRequest request;
        private readonly CancellationToken token;
        private readonly Dictionary<string, TraceabilityNode> nodes = new Dictionary<string, TraceabilityNode>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> nativeModules = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> nativeLinkModules = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TraceabilityModuleScope> versions = new Dictionary<string, TraceabilityModuleScope>(StringComparer.Ordinal);
        private readonly List<TraceabilityScopeEvidence> scopes = new List<TraceabilityScopeEvidence>();
        private readonly Dictionary<string, TraceabilityScopeEvidence> scopesById = new Dictionary<string, TraceabilityScopeEvidence>(StringComparer.Ordinal);
        private readonly List<TraceabilityObservation> observations = new List<TraceabilityObservation>();
        private readonly HashSet<string> correlations = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<TraceabilityFinding> findings = new List<TraceabilityFinding>();
        private readonly HashSet<string> findingIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Tuple<TraceabilityModuleScope, int, string, bool>> inventory = new List<Tuple<TraceabilityModuleScope, int, string, bool>>();
        private long metadataCharacters;
        internal GraphCapture(TraceabilityGraphRequest request, CancellationToken token)
        { this.request = request; this.token = token; foreach (var scope in request.Modules) if (scope.ObservedBaselineVersion != null) BindVersion(scope, scope.ObservedBaselineVersion); }
        internal TraceabilityGraphProgress Progress(string phase) => new TraceabilityGraphProgress(phase, scopes.Count, Math.Max(nodes.Count, inventory.Count), observations.Count);
        internal void Capture(TraceabilityModuleScope module, IRequirementSnapshot snapshot)
        {
            if (snapshot?.Summary == null) throw GraphGuard.Failure("Missing requirement snapshot metadata.");
            var summary = snapshot.Summary;
            if (summary.Request == null || summary.Request.ModulePath != module.ModulePath || !SameBaseline(summary.Request.Baseline, module.Baseline) || summary.Request.AllowAttributeDxl ||
                summary.Request.Target != request.Target || summary.Request.ConnectionMode != request.ConnectionMode || summary.Request.DeletedObjects != DeletedObjectMode.MetadataOnly)
                throw GraphGuard.Failure("Requirement scope echo mismatch.");
            AddScope(new TraceabilityScopeEvidence(module, summary));
            Native(nativeModules, module.ModulePath, summary.QualifiedModuleId);
            int count = 0; var absolute = new HashSet<int>();
            foreach (var record in snapshot.ReadObjects())
            {
                token.ThrowIfCancellationRequested();
                if (record == null || record.AbsoluteNumber <= 0 || !absolute.Add(record.AbsoluteNumber)) throw GraphGuard.Failure("Invalid or duplicate persistent object identity.");
                GraphGuard.Limit(inventory.Count >= request.MaxNodes);
                Metadata(record.ObjectIdentifier);
                inventory.Add(Tuple.Create(module, record.AbsoluteNumber, GraphGuard.Text(record.ObjectIdentifier ?? "", 1024, true), record.IsDeleted)); count++;
            }
            if (count != summary.ObjectCount) throw GraphGuard.Failure("Requirement record count disagrees with snapshot summary.");
        }
        internal void Capture(TraceabilityModuleScope module, ILinkSnapshot snapshot, LinkReadRequest read)
        {
            if (snapshot?.Summary == null) throw GraphGuard.Failure("Missing link snapshot metadata.");
            var summary = snapshot.Summary; var echo = summary.Request;
            if (echo == null || echo.ModulePath != read.ModulePath || echo.LinkModulePath != read.LinkModulePath || echo.Direction != LinkDirection.Both ||
                !SameBaseline(echo.Baseline, read.Baseline) || echo.Target != read.Target || echo.ConnectionMode != read.ConnectionMode || !echo.ResolveEndpoints)
                throw GraphGuard.Failure("Link scope echo mismatch.");
            AddScope(new TraceabilityScopeEvidence(module, summary));
            Native(nativeModules, module.ModulePath, summary.QualifiedAnchorModuleId);
            Native(nativeLinkModules, read.LinkModulePath, summary.QualifiedLinkModuleId);
            var sequences = new HashSet<int>(); int count = 0;
            foreach (var record in snapshot.ReadLinks())
            {
                token.ThrowIfCancellationRequested(); GraphGuard.Limit(observations.Count >= request.MaxObservations);
                if (record == null || record.Source == null || record.Target == null || record.Sequence <= 0 || !sequences.Add(record.Sequence) ||
                    (record.Direction != LinkDirection.Incoming && record.Direction != LinkDirection.Outgoing) || record.LinkModulePath != read.LinkModulePath ||
                    !Enum.IsDefined(typeof(LinkRecordKind), record.Kind) || !Enum.IsDefined(typeof(LinkResolution), record.Resolution) || !Enum.IsDefined(typeof(LinkModuleResolution), record.LinkModuleResolution))
                    throw GraphGuard.Failure("Invalid link record or mismatched link module.");
                var anchor = record.Direction == LinkDirection.Outgoing ? record.Source : record.Target;
                if (anchor.ModulePath != module.ModulePath || !anchor.AbsoluteNumber.HasValue || anchor.AbsoluteNumber <= 0 ||
                    anchor.VersionKind != (module.Baseline == null ? LinkVersionKind.Current : LinkVersionKind.Baseline)) throw GraphGuard.Failure("Link anchor identity/version mismatch.");
                if (module.Baseline != null) BindVersion(module, anchor.Version);
                Metadata(record.LinkModulePath, record.Source.ModulePath, record.Source.Version, record.Source.ObjectIdentifier, record.Source.ExternalReference,
                    record.Target.ModulePath, record.Target.Version, record.Target.ObjectIdentifier, record.Target.ExternalReference);
                observations.Add(new TraceabilityObservation(scopes[scopes.Count - 1].Id, record)); count++;
            }
            if (count != summary.RecordCount) throw GraphGuard.Failure("Link record count disagrees with snapshot summary.");
        }
        private void AddScope(TraceabilityScopeEvidence scope)
        {
            if (!correlations.Add(scope.Id)) throw GraphGuard.Failure("Snapshot request IDs must be unique.");
            scopes.Add(scope); scopesById.Add(scope.Id, scope);
            if (!scope.IsComplete) Finding("ScopeIncomplete", TraceabilityState.Unknown, TraceabilitySeverity.Warning, null, null, null, scope.Id,
                "Read scope is incomplete; absence of a link cannot prove missing coverage.");
        }
        private void Metadata(params string[] values)
        { foreach (var value in values) metadataCharacters += value?.Length ?? 0; GraphGuard.Limit(metadataCharacters > request.MaxMetadataCharacters); }
        private static void Native(Dictionary<string, string> map, string path, string id)
        {
            if (string.IsNullOrEmpty(id)) return; GraphGuard.Text(id, 1024);
            if (map.TryGetValue(path, out var previous) && previous != id) throw GraphGuard.Failure("Native module identity changed between snapshots.");
            map[path] = id;
        }
        private static string VersionKey(string path, string raw) => GraphGuard.Hash(path, raw);
        private void BindVersion(TraceabilityModuleScope module, string raw)
        {
            GraphGuard.Text(raw, 512); string key = VersionKey(module.ModulePath, raw);
            if (versions.TryGetValue(key, out var previous) && previous.Key != module.Key) throw GraphGuard.Failure("Opaque baseline label ambiguously maps to selected baselines.");
            // Caller-supplied alias is not silently replaced with another observed versionString.
            if (module.ObservedBaselineVersion != null && module.ObservedBaselineVersion != raw) throw GraphGuard.Failure("Caller baseline label disagrees with native anchor evidence.");
            versions[key] = module;
        }
        private TraceabilityNode CreateNode(string path, string native, LinkVersionKind kind, string version, bool selected, int? absolute, string discriminator, string role, string scope) =>
            new TraceabilityNode(request.RepositoryId, path, native, kind, version, selected, absolute, discriminator, role, scope);
        private void Add(TraceabilityNode node) { GraphGuard.Limit(nodes.Count >= request.MaxNodes); nodes.Add(node.Id, node); }
        private TraceabilityNode Endpoint(LinkEndpoint endpoint, TraceabilityObservation observation, bool source)
        {
            GraphGuard.Text(endpoint.ModulePath ?? "", 4096, true); GraphGuard.Text(endpoint.Version ?? "", 512, true); GraphGuard.Text(endpoint.ObjectIdentifier ?? "", 1024, true);
            if (!Enum.IsDefined(typeof(LinkVersionKind), endpoint.VersionKind) || !Enum.IsDefined(typeof(LinkEndpointResolution), endpoint.Resolution) || endpoint.AbsoluteNumber <= 0)
                throw GraphGuard.Failure("Invalid link endpoint metadata.");
            if (!string.IsNullOrEmpty(endpoint.ModulePath)) GraphGuard.Path(endpoint.ModulePath);
            if (endpoint.VersionKind == LinkVersionKind.Baseline && string.IsNullOrEmpty(endpoint.Version)) throw GraphGuard.Failure("Baseline endpoint lacks an explicit version label.");
            TraceabilityModuleScope selected = null;
            if (endpoint.VersionKind == LinkVersionKind.Current) selected = request.Modules.FirstOrDefault(m => m.ModulePath == endpoint.ModulePath && m.Baseline == null);
            else if (endpoint.VersionKind == LinkVersionKind.Baseline) versions.TryGetValue(VersionKey(endpoint.ModulePath, endpoint.Version), out selected);
            var role = request.Modules.FirstOrDefault(m => m.ModulePath == endpoint.ModulePath)?.Role;
            string native = null;
            // Native module IDs are version-independent, but never attach an ID to an unidentified source reference.
            if (!string.IsNullOrEmpty(endpoint.ModulePath)) nativeModules.TryGetValue(endpoint.ModulePath, out native);
            string discriminator = endpoint.AbsoluteNumber.HasValue && endpoint.VersionKind != LinkVersionKind.Unknown && !string.IsNullOrEmpty(endpoint.ModulePath) ? "" : observation.Id + (source ? "source" : "target");
            var node = CreateNode(endpoint.ModulePath ?? "", native, endpoint.VersionKind, selected?.Version ?? endpoint.Version ?? "", selected != null,
                endpoint.AbsoluteNumber, discriminator, role, selected?.Key);
            if (!nodes.TryGetValue(node.Id, out var existing)) { Add(node); existing = node; }
            existing.Observe(endpoint.Resolution); existing.IsDeleted |= endpoint.Resolution == LinkEndpointResolution.DeletedObject;
            if (existing.ObjectIdentifier.Length == 0) existing.ObjectIdentifier = endpoint.ObjectIdentifier ?? "";
            return existing;
        }
        internal TraceabilityGraph Complete(Guid id)
        {
            foreach (var seed in inventory)
            {
                token.ThrowIfCancellationRequested(); var module = seed.Item1; nativeModules.TryGetValue(module.ModulePath, out var native);
                var node = CreateNode(module.ModulePath, native, module.Baseline == null ? LinkVersionKind.Current : LinkVersionKind.Baseline, module.Version, true, seed.Item2, "", module.Role, module.Key);
                if (nodes.ContainsKey(node.Id)) throw GraphGuard.Failure("Duplicate native module/object identity across selected paths.");
                node.ObjectIdentifier = seed.Item3; node.IsInventoryObject = true; node.IsDeleted = seed.Item4;
                node.Observe(seed.Item4 ? LinkEndpointResolution.DeletedObject : LinkEndpointResolution.Resolved); Add(node);
            }
            var endpoints = new List<Tuple<TraceabilityObservation, TraceabilityNode, TraceabilityNode>>();
            foreach (var observation in observations) { token.ThrowIfCancellationRequested(); endpoints.Add(Tuple.Create(observation, Endpoint(observation.Record.Source, observation, true), Endpoint(observation.Record.Target, observation, false))); }
            var edges = new Dictionary<string, TraceabilityEdge>(StringComparer.Ordinal);
            foreach (var tuple in endpoints)
            {
                token.ThrowIfCancellationRequested(); var observation = tuple.Item1; var record = observation.Record; var source = tuple.Item2; var target = tuple.Item3;
                var state = record.Kind == LinkRecordKind.IncomingSourceReference ? TraceabilityEdgeState.Reference :
                    record.Resolution == LinkResolution.Resolved && record.LinkModuleResolution == LinkModuleResolution.Verified && !record.IsEchoed &&
                    record.Source.Resolution == LinkEndpointResolution.Resolved && record.Target.Resolution == LinkEndpointResolution.Resolved &&
                    !source.HasUncertainAccess && !target.HasUncertainAccess && !source.IsDeleted && !target.IsDeleted && source.AbsoluteNumber.HasValue && target.AbsoluteNumber.HasValue &&
                    source.VersionKind != LinkVersionKind.Unknown && target.VersionKind != LinkVersionKind.Unknown ? TraceabilityEdgeState.Confirmed : TraceabilityEdgeState.Possible;
                string native = scopesById[observation.ScopeId].Links.QualifiedLinkModuleId;
                if (string.IsNullOrEmpty(native)) native = null;
                var edge = new TraceabilityEdge(source.Id, target.Id, record.LinkModulePath, native, state, record.Kind, record.IsEchoed);
                if (!edges.TryGetValue(edge.Id, out var existing)) { existing = edge; edges.Add(edge.Id, edge); }
                existing.Observe(observation);
                if (record.Source.Resolution != LinkEndpointResolution.Resolved && record.Source.Resolution != LinkEndpointResolution.DeletedObject)
                    Finding("InaccessibleSource", TraceabilityState.Unknown, TraceabilitySeverity.Warning, source.Id, edge.Id, null, observation.ScopeId, record.Source.Resolution.ToString());
                if (record.Target.Resolution != LinkEndpointResolution.Resolved && record.Target.Resolution != LinkEndpointResolution.DeletedObject)
                    Finding("InaccessibleTarget", TraceabilityState.Unknown, TraceabilitySeverity.Warning, target.Id, edge.Id, null, observation.ScopeId, record.Target.Resolution.ToString());
            }
            foreach (var linkModule in request.LinkModulePaths) if (!nativeLinkModules.ContainsKey(linkModule))
                Finding("NativeLinkModuleIdUnavailable", TraceabilityState.Unknown, TraceabilitySeverity.Information, null, null, null, null, linkModule);
            var analysis = new GraphPolicyAnalysis(request, nodes, edges.Values.ToArray(), scopes, Finding, token);
            analysis.Validate(); var cycles = analysis.Cycles();
            return new TraceabilityGraph(id, request, nodes, edges.Values, observations, scopes, findings, cycles);
        }
        private void Finding(string code, TraceabilityState state, TraceabilitySeverity severity, string node, string edge, string rule, string scope, string message, int? count = null)
        { var value = new TraceabilityFinding(code, state, severity, node, edge, rule, scope, message, count); if (!findingIds.Add(value.Id)) return;
            GraphGuard.Limit(findings.Count >= request.MaxFindings); findings.Add(value); }
        private static bool SameBaseline(RequirementBaseline a, RequirementBaseline b) => a == null ? b == null : b != null && a.Major == b.Major && a.Minor == b.Minor && a.Suffix == b.Suffix;
    }
}
