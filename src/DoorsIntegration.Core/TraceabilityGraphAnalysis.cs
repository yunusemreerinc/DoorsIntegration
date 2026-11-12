using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DoorsIntegration.Core
{
    internal delegate void GraphFinding(string code, TraceabilityState state, TraceabilitySeverity severity, string node, string edge, string rule, string scope, string message, int? count = null);
    internal sealed class GraphPolicyAnalysis
    {
        private readonly TraceabilityGraphRequest request;
        private readonly IDictionary<string, TraceabilityNode> nodes;
        private readonly TraceabilityEdge[] edges;
        private readonly Dictionary<string, Dictionary<string, TraceabilityScopeEvidence>> linkScopes = new Dictionary<string, Dictionary<string, TraceabilityScopeEvidence>>(StringComparer.Ordinal);
        private readonly HashSet<string> completeRequirements = new HashSet<string>(StringComparer.Ordinal);
        private readonly GraphFinding finding;
        private readonly CancellationToken token;
        private readonly Dictionary<string, List<TraceabilityEdge>> incoming, outgoing;
        internal GraphPolicyAnalysis(TraceabilityGraphRequest request, IDictionary<string, TraceabilityNode> nodes, TraceabilityEdge[] edges,
            IReadOnlyList<TraceabilityScopeEvidence> scopes, GraphFinding finding, CancellationToken token)
        { this.request = request; this.nodes = nodes; this.edges = edges; this.finding = finding; this.token = token;
            incoming = GraphAlgorithms.Adjacency(edges, false); outgoing = GraphAlgorithms.Adjacency(edges, true);
            foreach (var scope in scopes) {
                if (scope.Requirements != null) { if (scope.IsComplete) completeRequirements.Add(scope.Module.Key); continue; }
                if (!linkScopes.TryGetValue(scope.Module.Key, out var modules)) { modules = new Dictionary<string, TraceabilityScopeEvidence>(StringComparer.Ordinal); linkScopes.Add(scope.Module.Key, modules); }
                modules.Add(scope.Links.Request.LinkModulePath, scope);
            }
        }
        private IEnumerable<TraceabilityEdge> Adjacent(TraceabilityNode node, TraceabilityDirection direction) =>
            GraphAlgorithms.Get(direction == TraceabilityDirection.Outgoing ? outgoing : incoming, node.Id);
        private bool Complete(TraceabilityNode node, string link, string native = null) => !node.HasUncertainAccess && node.ScopeId != null && completeRequirements.Contains(node.ScopeId) &&
            linkScopes.TryGetValue(node.ScopeId, out var modules) && modules.TryGetValue(link, out var scope) && scope.IsComplete &&
            (native == null || scope.Links.QualifiedLinkModuleId == native);
        private static bool? VersionMatches(TraceabilityNode anchor, TraceabilityNode other, TraceabilityVersionPolicy policy)
        {
            if (other.VersionKind == LinkVersionKind.Unknown || anchor.VersionKind == LinkVersionKind.Unknown) return null;
            if (policy == TraceabilityVersionPolicy.AnyExplicitVersion) return true;
            if (policy == TraceabilityVersionPolicy.CurrentCounterpart) return other.VersionKind == LinkVersionKind.Current;
            if (anchor.VersionKind != other.VersionKind) return false;
            if (anchor.VersionKind == LinkVersionKind.Current) return true;
            // Comparing opaque labels to a selected major/minor/suffix would silently guess versionString syntax.
            if (anchor.HasSelectedVersionBinding != other.HasSelectedVersionBinding) return null;
            return anchor.Version == other.Version;
        }
        internal void Validate()
        {
            foreach (var node in nodes.Values.Where(n => n.IsInventoryObject).OrderBy(n => n.Id, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (request.Policy.OrphanRoles.Contains(node.Role, StringComparer.Ordinal))
                {
                    var related = GraphAlgorithms.Get(incoming, node.Id).Concat(GraphAlgorithms.Get(outgoing, node.Id)).Distinct().ToArray();
                    int degree = related.Count(e => e.State == TraceabilityEdgeState.Confirmed);
                    bool complete = request.LinkModulePaths.All(p => Complete(node, p)) && completeRequirements.Contains(node.ScopeId);
                    string code = node.IsDeleted ? "DeletedObjectExcluded" : degree > 0 ? "NotOrphan" : related.Length > 0 || !complete ? "OrphanUnknown" : "OrphanRequirement";
                    finding(code, node.IsDeleted ? TraceabilityState.Excluded : degree > 0 ? TraceabilityState.Satisfied : complete && related.Length == 0 ? TraceabilityState.Observed : TraceabilityState.Unknown,
                        code == "OrphanRequirement" ? request.Policy.OrphanSeverity : code == "OrphanUnknown" ? TraceabilitySeverity.Warning : TraceabilitySeverity.Information,
                        node.Id, null, null, node.ScopeId, "Confirmed incident relationships within explicitly requested link modules.", degree);
                }
                foreach (var rule in request.Policy.LinkRules.Where(r => r.AnchorRole == node.Role)) ValidateRule(node, rule);
            }
        }
        private void ValidateRule(TraceabilityNode node, TraceabilityLinkRule rule)
        {
            token.ThrowIfCancellationRequested();
            if (node.IsDeleted) { finding("DeletedObjectExcluded", TraceabilityState.Excluded, TraceabilitySeverity.Information, node.Id, null, rule.Id, node.ScopeId, "Deleted inventory objects are not required to satisfy active link policies."); return; }
            var matched = new HashSet<string>(StringComparer.Ordinal); bool possible = false;
            foreach (var edge in Adjacent(node, rule.Direction).Where(e => e.LinkModulePath == rule.LinkModulePath))
            {
                token.ThrowIfCancellationRequested(); var other = nodes[rule.Direction == TraceabilityDirection.Outgoing ? edge.TargetId : edge.SourceId];
                bool? version = VersionMatches(node, other, rule.VersionPolicy);
                bool? native = rule.RequiredNativeLinkModuleId == null ? (bool?)true : edge.QualifiedLinkModuleId == null ? null : (bool?)(edge.QualifiedLinkModuleId == rule.RequiredNativeLinkModuleId);
                bool? role = other.Role == null ? (bool?)null : rule.CounterpartRoles.Contains(other.Role, StringComparer.Ordinal);
                bool invalid = version == false || native == false || role == false;
                if (invalid && edge.State == TraceabilityEdgeState.Confirmed && rule.RejectUnexpectedCounterparts)
                    finding("UnexpectedLinkModel", TraceabilityState.Violation, TraceabilitySeverity.Error, node.Id, edge.Id, rule.Id, node.ScopeId,
                        "Observed counterpart role/version or native link-module ID is not allowed by this rule.");
                if (rule.RejectUnexpectedCounterparts && (edge.State != TraceabilityEdgeState.Confirmed || version == null || native == null || role == null))
                    finding("LinkModelUnknown", TraceabilityState.Unknown, TraceabilitySeverity.Warning, node.Id, edge.Id, rule.Id, node.ScopeId,
                        "Relationship or counterpart metadata is insufficient to verify the expected link model.");
                if (!invalid && edge.State == TraceabilityEdgeState.Confirmed && version == true && native == true && role == true && !other.IsDeleted)
                    matched.Add(other.Id);
                else if (!invalid || edge.State != TraceabilityEdgeState.Confirmed) possible = true;
            }
            if (rule.MaximumLinks.HasValue && matched.Count > rule.MaximumLinks.Value)
                finding("MaximumLinksExceeded", TraceabilityState.Violation, TraceabilitySeverity.Error, node.Id, null, rule.Id, node.ScopeId, "Distinct verified counterparts exceed the policy maximum.", matched.Count);
            bool enough = matched.Count >= rule.MinimumLinks;
            bool complete = Complete(node, rule.LinkModulePath, rule.RequiredNativeLinkModuleId);
            string code = enough ? "ExpectedLinkSatisfied" : !possible && complete ? "MissingExpectedLink" : "ExpectedLinkUnknown";
            finding(code, enough ? TraceabilityState.Satisfied : !possible && complete ? TraceabilityState.Violation : TraceabilityState.Unknown,
                enough ? TraceabilitySeverity.Information : code == "MissingExpectedLink" ? TraceabilitySeverity.Error : TraceabilitySeverity.Warning,
                node.Id, null, rule.Id, node.ScopeId, "Counts distinct counterpart objects; duplicate/incoming/outgoing observations do not multiply coverage.", matched.Count);
        }
        internal IReadOnlyList<TraceabilityCycle> Cycles()
        {
            var result = new List<TraceabilityCycle>();
            foreach (var rule in request.Policy.CycleRules)
            {
                token.ThrowIfCancellationRequested();
                var selected = edges.Where(e => e.State == TraceabilityEdgeState.Confirmed &&
                    (rule.LinkModulePaths.Count == 0 || rule.LinkModulePaths.Contains(e.LinkModulePath, StringComparer.Ordinal)) &&
                    (rule.Roles.Count == 0 || (rule.Roles.Contains(nodes[e.SourceId].Role, StringComparer.Ordinal) && rule.Roles.Contains(nodes[e.TargetId].Role, StringComparer.Ordinal)))).ToArray();
                var components = GraphAlgorithms.Components(selected, token).ToArray();
                var componentOf = new Dictionary<string, int>(StringComparer.Ordinal);
                var internalEdges = new Dictionary<int, List<string>>();
                var selfLoops = new HashSet<string>(selected.Where(e => e.SourceId == e.TargetId).Select(e => e.SourceId), StringComparer.Ordinal);
                for (int i = 0; i < components.Length; i++) foreach (var node in components[i]) componentOf.Add(node, i);
                foreach (var edge in selected)
                {
                    token.ThrowIfCancellationRequested(); int index = componentOf[edge.SourceId]; if (index != componentOf[edge.TargetId]) continue;
                    if (!internalEdges.TryGetValue(index, out var list)) { list = new List<string>(); internalEdges.Add(index, list); } list.Add(edge.Id);
                }
                for (int i = 0; i < components.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); var component = components[i];
                    if (component.Count == 1 && !selfLoops.Contains(component[0])) continue;
                    var cycle = new TraceabilityCycle(rule.Id, rule.Behavior, component, internalEdges[i]);
                    result.Add(cycle);
                    finding(rule.Behavior == TraceabilityCycleBehavior.Prohibit ? "CycleProhibited" : rule.Behavior == TraceabilityCycleBehavior.Allow ? "CycleAllowed" : "CycleObserved",
                        rule.Behavior == TraceabilityCycleBehavior.Prohibit ? TraceabilityState.Violation : TraceabilityState.Observed,
                        rule.Behavior == TraceabilityCycleBehavior.Prohibit ? TraceabilitySeverity.Error : TraceabilitySeverity.Information,
                        cycle.NodeIds[0], null, rule.Id, null, "Strongly connected component: " + cycle.Id, cycle.NodeIds.Count);
                }
            }
            return result;
        }
    }

    internal static class GraphAlgorithms
    {
        internal static Dictionary<string, List<TraceabilityEdge>> Adjacency(IEnumerable<TraceabilityEdge> edges, bool source)
        {
            var result = new Dictionary<string, List<TraceabilityEdge>>(StringComparer.Ordinal);
            foreach (var edge in edges) { string id = source ? edge.SourceId : edge.TargetId; if (!result.TryGetValue(id, out var list)) { list = new List<TraceabilityEdge>(); result.Add(id, list); } list.Add(edge); }
            foreach (var list in result.Values) list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id)); return result;
        }
        internal static IEnumerable<TraceabilityEdge> Get(Dictionary<string, List<TraceabilityEdge>> map, string key) => map.TryGetValue(key, out var list) ? (IEnumerable<TraceabilityEdge>)list : Enumerable.Empty<TraceabilityEdge>();
        internal static IEnumerable<List<string>> Components(TraceabilityEdge[] edges, CancellationToken token)
        {
            var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal); var incoming = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                if (!outgoing.ContainsKey(edge.SourceId)) { outgoing.Add(edge.SourceId, new List<string>()); incoming.Add(edge.SourceId, new List<string>()); }
                if (!outgoing.ContainsKey(edge.TargetId)) { outgoing.Add(edge.TargetId, new List<string>()); incoming.Add(edge.TargetId, new List<string>()); }
                outgoing[edge.SourceId].Add(edge.TargetId); incoming[edge.TargetId].Add(edge.SourceId);
            }
            var visited = new HashSet<string>(StringComparer.Ordinal); var order = new List<string>();
            foreach (var root in outgoing.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!visited.Add(root)) continue;
                var stack = new Stack<Tuple<string, int>>(); stack.Push(Tuple.Create(root, 0));
                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested(); var frame = stack.Pop(); var adjacent = outgoing[frame.Item1];
                    if (frame.Item2 >= adjacent.Count) { order.Add(frame.Item1); continue; }
                    stack.Push(Tuple.Create(frame.Item1, frame.Item2 + 1)); string next = adjacent[frame.Item2];
                    if (visited.Add(next)) stack.Push(Tuple.Create(next, 0));
                }
            }
            visited.Clear();
            for (int i = order.Count - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested(); if (!visited.Add(order[i])) continue;
                var component = new List<string>(); var stack = new Stack<string>(); stack.Push(order[i]);
                while (stack.Count > 0) { token.ThrowIfCancellationRequested(); var node = stack.Pop(); component.Add(node); foreach (var next in incoming[node]) if (visited.Add(next)) stack.Push(next); }
                yield return component;
            }
        }
        internal static TraceabilityImpactResult Impact(TraceabilityGraph graph, IEnumerable<string> seeds, TraceabilityDirection direction, int depth, bool possible,
            int maxVisits, int maxEdges, CancellationToken token)
        {
            if (graph == null || seeds == null) throw new ArgumentNullException();
            if (!Enum.IsDefined(typeof(TraceabilityDirection), direction) || depth < 0 || depth > 100000 || maxVisits < 1 || maxVisits > 1000000 || maxEdges < 1 || maxEdges > 1000000) throw new ArgumentException("Invalid impact limits.");
            token.ThrowIfCancellationRequested();
            var supplied = seeds.Take(maxVisits + 1).ToArray();
            if (supplied.Length > maxVisits) throw new ArgumentException("Impact seed count exceeds its explicit limit.");
            var roots = supplied.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            if (roots.Length == 0 || roots.Length > maxVisits || roots.Any(s => s == null || !graph.Nodes.ContainsKey(s))) throw new ArgumentException("Impact requires known graph node IDs within the visit limit.");
            var incoming = Adjacency(graph.Edges, false); var outgoing = Adjacency(graph.Edges, true);
            var visits = new Dictionary<string, TraceabilityImpactVisit>(StringComparer.Ordinal); var queue = new Queue<string>();
            var edgeIds = new HashSet<string>(StringComparer.Ordinal); var boundary = new HashSet<string>(StringComparer.Ordinal); bool truncated = false;
            foreach (var id in roots) { visits.Add(id, new TraceabilityImpactVisit(id, 0, null, null, !graph.Nodes[id].IsDeleted && !graph.Nodes[id].HasUncertainAccess)); queue.Enqueue(id); }
            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested(); string id = queue.Dequeue(); var previous = visits[id];
                if (!graph.Nodes[id].IsInventoryObject || graph.Nodes[id].HasUncertainAccess) boundary.Add(id);
                var adjacent = direction == TraceabilityDirection.Outgoing ? Get(outgoing, id) : direction == TraceabilityDirection.Incoming ? Get(incoming, id) : Get(outgoing, id).Concat(Get(incoming, id));
                foreach (var edge in adjacent)
                {
                    token.ThrowIfCancellationRequested(); if (!possible && edge.State != TraceabilityEdgeState.Confirmed) { boundary.Add(id); continue; }
                    string next = edge.SourceId == id ? edge.TargetId : edge.SourceId;
                    if (previous.Distance >= depth) { if (!visits.ContainsKey(next)) { truncated = true; boundary.Add(id); } continue; }
                    if (!edgeIds.Contains(edge.Id) && edgeIds.Count >= maxEdges) { truncated = true; boundary.Add(id); continue; }
                    edgeIds.Add(edge.Id); if (visits.ContainsKey(next)) continue;
                    if (visits.Count >= maxVisits) { truncated = true; boundary.Add(id); continue; }
                    visits.Add(next, new TraceabilityImpactVisit(next, previous.Distance + 1, id, edge.Id, previous.IsConfirmedPath && edge.State == TraceabilityEdgeState.Confirmed)); queue.Enqueue(next);
                }
            }
            return new TraceabilityImpactResult(graph, direction, depth, possible, visits.Values.OrderBy(v => v.Distance).ThenBy(v => v.NodeId, StringComparer.Ordinal), edgeIds, boundary, truncated);
        }
    }
}
