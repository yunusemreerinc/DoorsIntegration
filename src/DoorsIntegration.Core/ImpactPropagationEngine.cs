using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DoorsIntegration.Core
{
    public sealed class ImpactPropagationEngine : IImpactPropagationEngine
    {
        private sealed class Arc { internal TraceabilityNode From, To; internal TraceabilityEdge Edge; internal BaselineLinkInfo Link; internal TraceabilityDirection Direction; }
        private sealed class State { internal TraceabilityNode Node; internal List<ImpactPathStep> Steps; internal HashSet<string> Seen; }
        public void Propagate(ChangeImpactResult result, ImpactAnalysisPolicy policy, IProgress<string> progress, CancellationToken token)
        {
            var watch = Stopwatch.StartNew(); var analysisIdentity = result.SnapshotIdentity;
            var changed = result.Changes.Where(c => c.IsChanged).OrderBy(c => c.Identity, StringComparer.Ordinal).ToArray();
            var impacted = new Dictionary<string, ImpactedRequirement>(StringComparer.Ordinal);
            var seenStates = new HashSet<string>(StringComparer.Ordinal); var pathCounts = new Dictionary<string, int>();
            var processedRoots = new HashSet<string>();
            var selected = result.GraphChoice == ImpactGraphChoice.Source ? new[] { result.Source } : result.GraphChoice == ImpactGraphChoice.Target ? new[] { result.Target } : new[] { result.Source, result.Target };
            foreach (var snapshot in selected)
            {
                var scopeTests = new HashSet<string>(); string snapshotIdentity = snapshot.Fingerprint;
                token.ThrowIfCancellationRequested(); progress?.Report("Indexing baseline-aware impact graph: " + snapshot.Label);
                var paths = snapshot.Links.Context.LinkModuleIdentities.Keys.Concat(snapshot.Links.Links.Select(l => l.LinkModulePath)).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
                if (paths.Length == 0) { result.Status = ImpactAnalysisStatus.Incomplete; result.Limitations.Add("No observed link-module scope in " + snapshot.Label); continue; }
                var request = new BaselineSetVerificationRequest("impact", "/", snapshot.Links.Context.Definition?.Id ?? "module-scope", snapshot.Links.Context.Set?.Id ?? "module-scope",
                    new TraceabilityPolicy(policy.Id, cycleRules: new[] { new TraceabilityCycleRule(behavior: policy.CycleBehavior) }), paths,
                    maxRequirements: policy.MaximumRequirements, maxLinks: policy.MaximumLinks);
                var verified = new BaselineTraceabilityAnalyzer().ClassifyReadOnlyLinkEvidence(snapshot.Links, request);
                var graph = TraceabilityGraphService.BuildBaselineGraph(snapshot.Links, request, verified, token);
                if (graph == null) { result.Status = ImpactAnalysisStatus.Incomplete; result.Limitations.Add("No verified module/version inventory could be projected for " + snapshot.Label); continue; }
                if (ReferenceEquals(snapshot, result.Source)) { result.SourceGraph = graph; result.SourceGraphSnapshotIdentity = snapshotIdentity; }
                else { result.TargetGraph = graph; result.TargetGraphSnapshotIdentity = snapshotIdentity; }
                if (!graph.IsCompleteWithinRequestedScope || snapshot.IsStale) result.Status = ImpactAnalysisStatus.Incomplete;
                if (graph.Cycles.Count > 0) result.Limitations.Add(snapshot.Label + ": cycle components=" + graph.Cycles.Count + "; policy=" + policy.CycleBehavior + "; traversal never repeats a node within a path. A cycle is not automatically an error.");
                var requirements = snapshot.Requirements.ToDictionary(v => v.RequirementVersionIdentity, StringComparer.Ordinal);
                var verifiedVersions = new HashSet<string>(snapshot.Requirements.Select(v => v.Version).Concat(snapshot.Links.Links.SelectMany(l => new[] { l.SourceVersion, l.TargetVersion })).Where(v => v != null && v.IdentityVerified).Select(v => v.ModuleBaselineIdentity));
                var rootVersions = snapshot.Requirements.ToDictionary(v => v.RequirementIdentity, StringComparer.Ordinal);
                var nodeIndex = graph.Nodes.Values.Where(n => n.VerificationNodeIdentity != null).ToDictionary(n => n.VerificationNodeIdentity, StringComparer.Ordinal);
                var evidence = snapshot.Links.Links.Where(l => l.Identity != null).GroupBy(l => GraphGuard.Hash(l.SourceVersion.NodeIdentity(l.SourceObject.Value), l.TargetVersion.NodeIdentity(l.TargetObject.Value), l.LinkModuleIdentity)).ToDictionary(g => g.Key, g => g.First());
                var adjacency = new Dictionary<string, List<Arc>>(StringComparer.Ordinal);
                foreach (var edge in graph.Edges)
                {
                    token.ThrowIfCancellationRequested(); var from = graph.Nodes[edge.SourceId]; var to = graph.Nodes[edge.TargetId];
                    evidence.TryGetValue(GraphGuard.Hash(from.VerificationNodeIdentity ?? "", to.VerificationNodeIdentity ?? "", edge.QualifiedLinkModuleId ?? ""), out var link);
                    AddArc(adjacency, new Arc { From = from, To = to, Edge = edge, Link = link, Direction = TraceabilityDirection.Outgoing }, policy);
                    AddArc(adjacency, new Arc { From = to, To = from, Edge = edge, Link = link, Direction = TraceabilityDirection.Incoming }, policy);
                }
                foreach (var change in changed)
                {
                    token.ThrowIfCancellationRequested();
                    if (watch.Elapsed.TotalSeconds >= policy.TimeoutSeconds) { Limit(result, "Propagation timeout; no running COM call was forcibly cancelled."); return; }
                    if (!rootVersions.TryGetValue(change.Identity, out var version) || !nodeIndex.TryGetValue(version.RequirementVersionIdentity, out var seed)) continue;
                    processedRoots.Add(change.Identity);
                    var queue = new Queue<State>(); queue.Enqueue(new State { Node = seed, Steps = new List<ImpactPathStep>(), Seen = new HashSet<string> { seed.Id } });
                    while (queue.Count > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (watch.Elapsed.TotalSeconds >= policy.TimeoutSeconds) { Limit(result, "Propagation timeout; remaining paths were not enumerated."); return; }
                        var state = queue.Dequeue();
                        if (!adjacency.TryGetValue(state.Node.Id, out var next)) continue;
                        foreach (var arc in next)
                        {
                            token.ThrowIfCancellationRequested();
                            if (state.Seen.Contains(arc.To.Id)) continue;
                            if (state.Steps.Count >= policy.MaximumDepth) { Limit(result, "Maximum link depth reached; deeper paths remain unanalysed."); break; }
                            string versionId = arc.To.VerificationNodeIdentity ?? arc.To.Id, objectId = arc.To.ObjectIdentity ?? arc.To.Id;
                            bool identityVerified = arc.To.ObjectIdentity != null && arc.To.ModuleBaselineIdentity != null && verifiedVersions.Contains(arc.To.ModuleBaselineIdentity);
                            string stateKey = GraphGuard.Hash(change.Identity, versionId); pathCounts.TryGetValue(stateKey, out int count);
                            if (count >= policy.MaximumPathsPerRequirement) { Limit(result, "Maximum alternate paths per root/requirement reached."); continue; }
                            if (result.Paths.Count >= policy.MaximumPaths || !seenStates.Contains(stateKey) && seenStates.Count >= policy.MaximumNodes) { Limit(result, "Maximum impact paths/nodes reached; remaining traversal stopped."); return; }
                            seenStates.Add(stateKey); pathCounts[stateKey] = count + 1;
                            bool confirmed = arc.Edge.State == TraceabilityEdgeState.Confirmed && !arc.To.HasUncertainAccess;
                            var steps = new List<ImpactPathStep>(state.Steps) { new ImpactPathStep { SourceNode = arc.From.VerificationNodeIdentity ?? arc.From.Id, TargetNode = versionId,
                                LinkIdentity = arc.Link?.Identity?.VersionedKey ?? arc.Edge.Id, LinkModuleIdentity = arc.Edge.QualifiedLinkModuleId, LinkType = arc.Link?.LinkType ?? arc.Edge.LinkModulePath,
                                Direction = arc.Direction, Depth = state.Steps.Count + 1, BaselineContext = snapshot.Label + ":" + (arc.Link?.SourceVersion?.Version ?? "Unknown") + " -> " + (arc.Link?.TargetVersion?.Version ?? "Unknown"), Confirmed = confirmed } };
                            var path = new ImpactPropagationPath { RootChangedRequirement = change.Identity, RootVersionIdentity = version.RequirementVersionIdentity, GraphSnapshotIdentity = snapshotIdentity, GraphSnapshotLabel = snapshot.Label, TargetRequirement = identityVerified ? objectId : null, TargetVersionIdentity = identityVerified ? versionId : null, TargetReferenceIdentity = versionId, Steps = steps };
                            path.Identity = GraphGuard.Hash(new[] { path.RootVersionIdentity, snapshotIdentity }.Concat(steps.Select(s => GraphGuard.Hash(s.LinkIdentity, s.Direction.ToString()))).ToArray()); result.Paths.Add(path);
                            requirements.TryGetValue(versionId, out var target);
                            bool test = policy.TestTypes.Contains(arc.To.Role) || target != null && policy.TestTypeValues.Contains(target.Value(policy.TestTypeAttribute));
                            if (test && confirmed) scopeTests.Add(change.Identity);
                            bool external = !snapshot.ComparedModuleIdentities.Contains(arc.To.QualifiedModuleId);
                            var kind = !confirmed ? ImpactCandidateKind.UnresolvedImpact : test ? ImpactCandidateKind.TestImpactCandidate : external ? ImpactCandidateKind.ExternalImpactCandidate : steps.Count == 1 ? ImpactCandidateKind.DirectImpactCandidate : ImpactCandidateKind.IndirectImpactCandidate;
                            if (!impacted.TryGetValue(stateKey, out var candidate))
                            {
                                candidate = new ImpactedRequirement { RequirementIdentity = identityVerified ? objectId : null, RequirementVersionIdentity = identityVerified ? versionId : null, ReferenceIdentity = versionId, IdentityVerified = identityVerified, RootChangeIdentity = change.Identity, Kind = kind, MinimumDepth = steps.Count, IsExternal = external,
                                    TestRecommendation = !confirmed ? ImpactTestRecommendation.Unknown : !test ? ImpactTestRecommendation.NoActionRecommended : change.Flags.HasFlag(RequirementChangeFlags.Deleted) ? ImpactTestRecommendation.TraceabilityUpdateCandidate : change.Attributes.Any(a => a.Name == policy.VerificationAttribute && a.SemanticChanged) ? ImpactTestRecommendation.ReviewRequired : ImpactTestRecommendation.RegressionTestCandidate };
                                impacted.Add(stateKey, candidate); result.Impacted.Add(candidate);
                            }
                            candidate.MinimumDepth = Math.Min(candidate.MinimumDepth, steps.Count); candidate.PathIdentities.Add(path.Identity);
                            AddFinding(result, analysisIdentity, change, kind, identityVerified ? objectId : null, identityVerified ? versionId : null, path, confirmed ? "ImpactCandidate" : "UnresolvedReference",
                                confirmed ? "Observed policy-permitted link path. Engineering review is required; this does not mandate changing a requirement or rerunning a test." : "Endpoint/version/access is unresolved. No data-loss claim and no inferred downstream chain.");
                            if (!confirmed) { result.Status = ImpactAnalysisStatus.Incomplete; continue; }
                            queue.Enqueue(new State { Node = arc.To, Steps = steps, Seen = new HashSet<string>(state.Seen) { arc.To.Id } });
                        }
                    }
                    if (!version.IsDeleted && policy.RequiredTestForTypes.Contains(version.Value(policy.TypeAttribute)) && !scopeTests.Contains(change.Identity))
                        AddFinding(result, analysisIdentity, change, graph.IsCompleteWithinRequestedScope && !result.Truncated ? ImpactCandidateKind.NoImpactPathFound : ImpactCandidateKind.UnresolvedImpact, null, version.RequirementVersionIdentity, null,
                            "ExpectedTestLinkageNotObserved:" + (ReferenceEquals(snapshot, result.Source) ? "Source" : "Target"), snapshot.Label + ": no qualifying test path observed within the project policy/limits. Historical tests in another snapshot do not establish target coverage. Engineering review is required.");
                }
            }
            var rootsWithPaths = new HashSet<string>(result.Paths.Select(p => p.RootChangedRequirement));
            foreach (var change in changed)
            {
                if (!processedRoots.Contains(change.Identity)) { result.Status = ImpactAnalysisStatus.Incomplete; result.Limitations.Add("Changed requirement absent from the explicitly selected propagation graph: " + change.Identity + "; select a graph containing its observed version."); }
                if (!rootsWithPaths.Contains(change.Identity)) AddFinding(result, analysisIdentity, change, result.Status == ImpactAnalysisStatus.Completed ? ImpactCandidateKind.NoImpactPathFound : ImpactCandidateKind.UnresolvedImpact,
                    null, null, null, "NoImpactPathFound", "No path found in the explicitly selected graph scope. This is not proof of no engineering impact.");
            }
        }
        private static void AddArc(Dictionary<string, List<Arc>> adjacency, Arc arc, ImpactAnalysisPolicy policy)
        {
            bool Match(string pattern, string value) => pattern == "*" || pattern == value;
            if (!policy.PropagationRules.Any(r => (r.Direction == TraceabilityDirection.Both || r.Direction == arc.Direction) && Match(r.SourceType, arc.From.Role) && Match(r.TargetType, arc.To.Role) && Match(r.LinkModuleIdentity, arc.Edge.QualifiedLinkModuleId) && Match(r.LinkType, arc.Link?.LinkType ?? arc.Edge.LinkModulePath))) return;
            if (!adjacency.TryGetValue(arc.From.Id, out var list)) adjacency.Add(arc.From.Id, list = new List<Arc>()); list.Add(arc);
        }
        internal static void Limit(ChangeImpactResult result, string reason) { result.Truncated = true; result.Status = ImpactAnalysisStatus.Incomplete; if (!result.Limitations.Contains(reason)) result.Limitations.Add(reason); }
        private static void AddFinding(ChangeImpactResult result, string analysisIdentity, RequirementChange change, ImpactCandidateKind kind, string target, string targetVersion, ImpactPropagationPath path, string code, string reason)
        {
            string id = GraphGuard.Hash(change.Identity, (change.After ?? change.Before).RequirementVersionIdentity, targetVersion ?? "", path?.Identity ?? "", code);
            result.Findings.Add(new ImpactAnalysisFinding { Identity = id, Code = code, RootChangeIdentity = change.Identity, ImpactedRequirementIdentity = target, ImpactedVersionIdentity = targetVersion, PathIdentity = path?.Identity, Depth = path?.Depth ?? 0,
                Kind = kind, ChangeFlags = change.Flags, Severity = change.Severity, Reason = reason, EvidenceQuality = kind == ImpactCandidateKind.UnresolvedImpact || !change.IsConclusive ? ImpactEvidenceQuality.PartialEvidence : path == null ? ImpactEvidenceQuality.Unverified : ImpactEvidenceQuality.ObservedPath,
                EvidenceFingerprint = GraphGuard.Hash(analysisIdentity, id, reason) });
        }
    }
}
