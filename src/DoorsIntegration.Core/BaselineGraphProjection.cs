using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DoorsIntegration.Core
{
    internal static class BaselineGraphProjection
    {
        internal static TraceabilityGraph Build(BaselineLinkSnapshot snapshot, BaselineSetVerificationRequest request, IReadOnlyList<BaselineLinkVerificationResult> verified, CancellationToken token)
        {
            var context = snapshot.Context;
            if (string.IsNullOrWhiteSpace(context.RepositoryId)) return null;
            var versions = context.Modules.Where(m => m.Version != null && m.SelectedForVerification).Select(m => m.Version)
                .Concat(snapshot.Requirements.Where(n => n.Version != null).Select(n => n.Version)).GroupBy(v => v.ModuleBaselineIdentity).Select(g => g.First()).ToArray();
            if (versions.Length == 0) return null;
            if (versions.Length > 64) { snapshot.Truncated = true; snapshot.Limitations.Add("Existing graph scope header limit is 64 module versions; results are incomplete."); }
            var scopes = versions.Take(64).Select(v => new TraceabilityModuleScope(v.FullPath, ScopeRole(v, request, context), v.Baseline, v.IsCurrent ? null : v.Version)).ToArray();
            var readRequest = new TraceabilityGraphRequest(context.RepositoryId, scopes, request.LinkModulePaths, request.Policy,
                maxNodes: request.MaxRequirements, maxObservations: request.MaxLinks, maxFindings: request.MaxFindings, readTimeout: request.Timeout, target: request.Target, maxMetadataCharacters: request.MaxMetadataCharacters);
            var nodes = new Dictionary<string, TraceabilityNode>(StringComparer.Ordinal); var byIdentity = new Dictionary<string, TraceabilityNode>(StringComparer.Ordinal);
            foreach (var r in snapshot.Requirements)
            {
                token.ThrowIfCancellationRequested(); if (r.Version == null || r.AbsoluteNumber <= 0) continue;
                var node = Node(r.Version, r.AbsoluteNumber, r.RequirementType, r.RequirementStatus, r.NodeIdentity);
                node.IsInventoryObject = true; node.IsDeleted = r.IsDeleted; node.ObjectIdentifier = r.DisplayIdentifier ?? "";
                node.Observe(r.Access == BaselineVerificationReadState.Verified ? LinkEndpointResolution.Resolved : r.Access == BaselineVerificationReadState.AccessDenied ? LinkEndpointResolution.AccessDenied : LinkEndpointResolution.ReadFailed);
                if (byIdentity.ContainsKey(r.NodeIdentity)) throw GraphGuard.Failure("Duplicate versioned requirement identity.");
                byIdentity.Add(r.NodeIdentity, node); nodes.Add(node.Id, node);
            }
            var edges = new Dictionary<string, TraceabilityEdge>();
            var observations = new List<TraceabilityObservation>(); int sequence = 0;
            foreach (var group in verified.Where(x => x.Link != null).GroupBy(x => x.Link.Identity?.VersionedKey ?? x.Link.ObservationId))
            {
                token.ThrowIfCancellationRequested(); var result = group.First(); var link = result.Link;
                var source = Endpoint(link.SourceVersion, link.SourceObject, link.SourcePath, link.SourceAccess, link.ObservationId + ":source", context, request, nodes, byIdentity);
                var target = Endpoint(link.TargetVersion, link.TargetObject, link.TargetPath, link.TargetAccess, link.ObservationId + ":target", context, request, nodes, byIdentity);
                if (source == null || target == null) { snapshot.Truncated = true; snapshot.Limitations.Add("Graph endpoint/node limit reached."); break; }
                var edge = new TraceabilityEdge(source.Id, target.Id, link.LinkModulePath, link.LinkModuleIdentity,
                    group.Any(x => x.CountsAsValid) ? TraceabilityEdgeState.Confirmed : link.Kind == LinkRecordKind.IncomingSourceReference ? TraceabilityEdgeState.Reference : TraceabilityEdgeState.Possible, link.Kind, group.Any(x => x.Link.IsEchoed));
                if (!edges.TryGetValue(edge.Id, out var capturedEdge)) { capturedEdge = edge; edges.Add(edge.Id, edge); }
                foreach (var captured in group)
                {
                    var l = captured.Link;
                    var record = new LinkRecord(++sequence, l.Direction, l.Kind, captured.CountsAsValid ? LinkResolution.Resolved : LinkResolution.EndpointUnresolved, l.IsEchoed,
                        l.LinkModulePath, string.IsNullOrEmpty(l.LinkModuleIdentity) ? LinkModuleResolution.FilterOnly : LinkModuleResolution.Verified, LegacyEndpoint(l.SourceVersion, l.SourceObject, l.SourcePath, l.SourceAccess), LegacyEndpoint(l.TargetVersion, l.TargetObject, l.TargetPath, l.TargetAccess));
                    var observation = new TraceabilityObservation(snapshot.SnapshotId.ToString("N"), record); capturedEdge.Observe(observation); observations.Add(observation);
                }
            }
            var cycles = new List<TraceabilityCycle>();
            foreach (var rule in request.Policy.CycleRules)
            {
                var candidates = edges.Values.Where(e => e.State == TraceabilityEdgeState.Confirmed && (rule.LinkModulePaths.Count == 0 || rule.LinkModulePaths.Contains(e.LinkModulePath)) &&
                    (rule.Roles.Count == 0 || rule.Roles.Contains(nodes[e.SourceId].Role) && rule.Roles.Contains(nodes[e.TargetId].Role))).ToArray();
                var bySource = GraphAlgorithms.Adjacency(candidates, true);
                foreach (var component in GraphAlgorithms.Components(candidates, token))
                {
                    var members = new HashSet<string>(component); var inside = component.SelectMany(n => GraphAlgorithms.Get(bySource, n)).Where(e => members.Contains(e.TargetId)).ToArray();
                    if (component.Count > 1 || inside.Any(e => e.SourceId == e.TargetId)) cycles.Add(new TraceabilityCycle(rule.Id, rule.Behavior, component, inside.Select(e => e.Id)));
                }
            }
            bool complete = context.MetadataComplete && !snapshot.Truncated && !snapshot.ContextChanged && context.Modules.Where(m => m.SelectedForVerification).All(m => m.State == BaselineVerificationReadState.Verified && m.InventoryComplete && m.LinksComplete);
            return new TraceabilityGraph(snapshot.SnapshotId, readRequest, nodes, edges.Values, observations, Array.Empty<TraceabilityScopeEvidence>(), Array.Empty<TraceabilityFinding>(), cycles, complete);
        }
        private static LinkEndpoint LegacyEndpoint(BaselineVersionReference v, int? absolute, string path, BaselineVerificationReadState access) => new LinkEndpoint(v?.FullPath ?? path, v == null ? LinkVersionKind.Unknown : v.IsCurrent ? LinkVersionKind.Current : LinkVersionKind.Baseline,
            v == null || v.IsCurrent ? "" : v.Version, absolute, "", access == BaselineVerificationReadState.Verified ? LinkEndpointResolution.Resolved : access == BaselineVerificationReadState.AccessDenied ? LinkEndpointResolution.AccessDenied : LinkEndpointResolution.ReadFailed);
        private static TraceabilityNode Node(BaselineVersionReference version, int absolute, string role, string status, string identity)
        { return new TraceabilityNode(version.RepositoryId, version.FullPath, version.ModuleIdentity, version.IsCurrent ? LinkVersionKind.Current : LinkVersionKind.Baseline, version.Version, version.IdentityVerified, absolute, version.ModuleBaselineIdentity,
            string.IsNullOrWhiteSpace(role) ? "Unknown" : role, version.ModuleBaselineIdentity) { ModuleBaselineIdentity = version.ModuleBaselineIdentity, ObjectIdentity = version.ObjectIdentity(absolute), VerificationNodeIdentity = identity, RequirementStatus = status ?? "Unknown" }; }
        private static string ScopeRole(BaselineVersionReference version, BaselineSetVerificationRequest request, BaselineVerificationContext context)
        { string role = request.ModuleRoles.TryGetValue(version.ModuleIdentity, out var mapped) ? mapped : context.Modules.FirstOrDefault(m => m.ModuleIdentity == version.ModuleIdentity)?.RequirementType; return string.IsNullOrWhiteSpace(role) ? "Requirement" : role; }
        private static TraceabilityNode Endpoint(BaselineVersionReference version, int? absolute, string path, BaselineVerificationReadState access, string discriminator,
            BaselineVerificationContext context, BaselineSetVerificationRequest request, Dictionary<string, TraceabilityNode> nodes, Dictionary<string, TraceabilityNode> index)
        {
            if (version != null && absolute > 0)
            {
                string id = version.NodeIdentity(absolute.Value); if (index.TryGetValue(id, out var existing)) return existing;
                if (nodes.Count >= request.MaxRequirements) return null;
                string role = request.TypeAttribute.Length > 0 ? "Unknown" : request.ModuleRoles.TryGetValue(version.ModuleIdentity, out string mapped) ? mapped : context.Modules.FirstOrDefault(m => m.ModuleIdentity == version.ModuleIdentity)?.RequirementType;
                var node = Node(version, absolute.Value, role, "NotInventoried", id);
                node.Observe(access == BaselineVerificationReadState.Verified ? LinkEndpointResolution.Resolved : access == BaselineVerificationReadState.AccessDenied ? LinkEndpointResolution.AccessDenied : LinkEndpointResolution.ReadFailed);
                index.Add(id, node); nodes.Add(node.Id, node); return node;
            }
            var unknown = new TraceabilityNode(context.RepositoryId ?? "Unverified", path ?? "", null, LinkVersionKind.Unknown, "", false, absolute, discriminator, "Unknown", null);
            unknown.Observe(access == BaselineVerificationReadState.AccessDenied ? LinkEndpointResolution.AccessDenied : LinkEndpointResolution.ReadFailed);
            if (!nodes.TryGetValue(unknown.Id, out var found)) { if (nodes.Count >= request.MaxRequirements) return null; nodes.Add(unknown.Id, found = unknown); } return found;
        }
    }
}
