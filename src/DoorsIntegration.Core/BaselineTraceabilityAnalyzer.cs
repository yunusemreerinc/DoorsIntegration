using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DoorsIntegration.Core
{
    public sealed class BaselineTraceabilityAnalyzer
    {
        // Shared endpoint/version classification without imposing baseline-set-only restrictions
        // on an explicitly selected Current-module impact snapshot.
        internal IReadOnlyList<BaselineLinkVerificationResult> ClassifyReadOnlyLinkEvidence(BaselineLinkSnapshot snapshot, BaselineSetVerificationRequest request)
        {
            var modules = snapshot.Context.Modules.ToDictionary(m => m.ModuleIdentity, StringComparer.Ordinal);
            return snapshot.Links.Select(l => VerifyLink(l, modules, snapshot.Context, request)).ToArray();
        }
        public void Analyze(BaselineSetVerificationRequest request, BaselineSetVerificationResult result, CancellationToken token = default(CancellationToken))
        {
            var snapshot = result.Snapshot; var context = snapshot.Context;
            Action<string, TraceabilitySeverity, string, string, bool, string> finding = (code, severity, node, explanation, uncertain, rule) =>
            { if (result.Findings.Count >= request.MaxFindings) { snapshot.Truncated = true; return; } result.Findings.Add(new TraceabilityViolation { Code = code, Severity = severity, NodeIdentity = node, Explanation = explanation, Uncertain = uncertain, RuleId = rule, Recommendation = uncertain ? "Review access, version bindings and scope; do not infer loss or repair links." : "Review the project traceability policy and recorded evidence." }); };
            if (!context.MetadataComplete) finding("MetadataIncomplete", TraceabilitySeverity.Warning, null, context.Reason ?? "Set metadata is not authoritative.", true, null);
            bool contextValid = context.Definition != null && context.Set?.Version != null && context.Definition.Id == request.DefinitionId && context.Set.Id == request.SetId && context.Set.DefinitionId == context.Definition.Id && !string.IsNullOrWhiteSpace(context.RepositoryId) && !string.IsNullOrWhiteSpace(context.EnvironmentId);
            if (!contextValid) finding("SetIdentityUnknown", TraceabilitySeverity.Warning, null, "Exact definition/set identity could not be verified.", true, null);
            if (context.Set?.IsOpen == true) finding("OpenBaselineSet", TraceabilitySeverity.Warning, null, "Set is open; membership and link state can still change.", false, null);
            foreach (var declared in context.Definition?.Modules ?? new List<BaselineSetModuleReference>())
                if (!context.Modules.Any(m => m.ModuleIdentity == declared.ModuleId))
                { context.Modules.Add(new ModuleBaselineVerificationResult { ModuleIdentity = declared.ModuleId, FullPath = declared.FullPath, State = BaselineVerificationReadState.Unknown, Reason = "No authoritative per-module baseline/access fact.", SelectedForVerification = request.SelectedModuleIds.Count == 0 || request.SelectedModuleIds.Contains(declared.ModuleId) }); context.MetadataComplete = false; }
            var modules = context.Modules.ToDictionary(m => m.ModuleIdentity, StringComparer.Ordinal);
            foreach (string id in request.SelectedModuleIds.Where(id => !modules.ContainsKey(id))) finding("SelectionOutsideDefinition", TraceabilitySeverity.Error, null, "Requested stable module identity is outside the observed set definition: " + id, false, null);
            foreach (var m in context.Modules)
            {
                token.ThrowIfCancellationRequested();
                if (m.State == BaselineVerificationReadState.Verified && (m.Version == null || !m.InSet)) { m.State = BaselineVerificationReadState.Unknown; m.InventoryComplete = m.LinksComplete = false; m.Reason = "A verified baseline requires explicit set membership and typed version identity."; }
                if (m.SelectedForVerification && (!m.InventoryComplete || !m.LinksComplete)) finding("ModuleScopeIncomplete", TraceabilitySeverity.Warning, null, m.ModuleIdentity + ": inventory/link enumeration is incomplete, even if an observed link satisfies a minimum rule.", true, null);
                if (m.Version != null && (m.Version.IsCurrent || !m.Version.IdentityVerified || m.Version.ModuleIdentity != m.ModuleIdentity || m.Version.RepositoryId != context.RepositoryId || m.Version.FullPath != m.FullPath)) m.State = BaselineVerificationReadState.Invalid;
                if (m.State == BaselineVerificationReadState.Missing) finding("MissingModuleBaseline", context.Set?.IsOpen == true ? TraceabilitySeverity.Warning : TraceabilitySeverity.Error, null, m.ModuleIdentity + ": authoritative absence of a module baseline; " + (context.Set?.IsOpen == true ? "open partial set" : "closed set"), context.Set?.IsOpen == true, null);
                else if (m.State == BaselineVerificationReadState.AccessDenied || m.State == BaselineVerificationReadState.Unknown) finding(m.State.ToString(), TraceabilitySeverity.Warning, null, m.ModuleIdentity + ": " + m.Reason, true, null);
                else if (m.State == BaselineVerificationReadState.Invalid || m.Version != null && (m.Version.IsCurrent || !m.Version.IdentityVerified || m.Version.ModuleIdentity != m.ModuleIdentity || m.Version.RepositoryId != context.RepositoryId || m.Version.FullPath != m.FullPath))
                    finding("InvalidModuleBaselineIdentity", TraceabilitySeverity.Error, null, m.ModuleIdentity + ": baseline/module identity or major/minor/suffix binding is invalid.", false, null);
                if (m.SelectedForVerification && m.ShareableConfiguration != BaselineShareableConfiguration.NotConfigured)
                    finding(m.ShareableConfiguration == BaselineShareableConfiguration.Configured ? "ShareableEditRisk" : "ShareableConfigurationUnknown", TraceabilitySeverity.Warning, null,
                        m.ModuleIdentity + ": configuration (not merely current edit mode) is " + m.ShareableConfiguration + ". This is not proof of data loss.", m.ShareableConfiguration == BaselineShareableConfiguration.Unknown, null);
            }
            foreach (var link in snapshot.Links)
            {
                token.ThrowIfCancellationRequested(); var verified = VerifyLink(link, modules, context, request); result.Links.Add(verified);
                bool uncertain = verified.UnderlyingStatus == BaselineLinkStatus.Unknown || verified.UnderlyingStatus == BaselineLinkStatus.AccessDenied || verified.UnderlyingStatus == BaselineLinkStatus.UnresolvedSource || verified.UnderlyingStatus == BaselineLinkStatus.UnresolvedTarget || verified.Status == BaselineLinkStatus.EchoedLink && !verified.CountsAsValid;
                if (!verified.CountsAsValid || link.IsEchoed) finding(verified.Status.ToString(), verified.Status == BaselineLinkStatus.UnexpectedVersionTarget ? TraceabilitySeverity.Error : uncertain ? TraceabilitySeverity.Warning : TraceabilitySeverity.Information,
                    link.SourceVersion != null && link.SourceObject > 0 ? link.SourceVersion.NodeIdentity(link.SourceObject.Value) : null, verified.Reason, uncertain, null);
            }
            result.Graph = TraceabilityGraphService.BuildBaselineGraph(snapshot, request, result.Links, token);
            var coverage = new TraceabilityCoverageResult(); result.Coverage = coverage;
            var inventory = snapshot.Requirements.Where(n => !n.IsDeleted && context.Modules.Any(m => m.SelectedForVerification && m.ModuleIdentity == n.Version?.ModuleIdentity && m.Version?.ModuleBaselineIdentity == n.Version.ModuleBaselineIdentity)).ToArray();
            coverage.TotalRequirements = inventory.Length;
            coverage.AccessDeniedRequirements = inventory.Count(n => n.Access == BaselineVerificationReadState.AccessDenied);
            coverage.UnenumeratedModules = context.Modules.Count(m => m.SelectedForVerification && (!m.InventoryComplete || m.State != BaselineVerificationReadState.Verified));
            var bySource = result.Links.Where(v => v.Link?.SourceVersion != null && v.Link.SourceObject > 0).GroupBy(v => v.Link.SourceVersion.NodeIdentity(v.Link.SourceObject.Value)).ToDictionary(g => g.Key, g => g.ToArray());
            var byTarget = result.Links.Where(v => v.Link?.TargetVersion != null && v.Link.TargetObject > 0).GroupBy(v => v.Link.TargetVersion.NodeIdentity(v.Link.TargetObject.Value)).ToDictionary(g => g.Key, g => g.ToArray());
            var roles = snapshot.Requirements.Where(n => n.Version != null).GroupBy(n => n.NodeIdentity).ToDictionary(g => g.Key, g => g.First().RequirementType);
            var unresolved = new HashSet<string>(); var required = new HashSet<string>();
            foreach (var node in inventory)
            {
                token.ThrowIfCancellationRequested();
                if (node.Access != BaselineVerificationReadState.Verified) finding(node.Access == BaselineVerificationReadState.AccessDenied ? "RequirementAccessDenied" : "RequirementReadUnknown", TraceabilitySeverity.Warning, node.NodeIdentity, "Requirement-level read limitation; no missing-object or data-loss inference.", true, null);
                if (string.IsNullOrWhiteSpace(node.RequirementType)) { coverage.UnknownPolicyEligibility++; finding("UnknownRequirementType", TraceabilitySeverity.Warning, node.NodeIdentity, "Policy eligibility cannot be determined; object is not silently excluded from the overall denominator.", true, null); continue; }
                var rules = request.Policy.LinkRules.Where(r => r.AnchorRole == node.RequirementType).ToArray();
                bool requires = rules.Any(r => r.MinimumLinks > 0); if (requires) { required.Add(node.NodeIdentity); coverage.RequirementsRequiringLinks++; }
                bool unknown = node.Access != BaselineVerificationReadState.Verified, missing = false, satisfied = true;
                var module = modules[node.Version.ModuleIdentity];
                foreach (var rule in rules)
                {
                    var index = rule.Direction == TraceabilityDirection.Outgoing ? bySource : byTarget;
                    var candidates = index.TryGetValue(node.NodeIdentity, out var captured) ? captured.Where(v => v.Link.LinkModulePath == rule.LinkModulePath).ToArray() : Array.Empty<BaselineLinkVerificationResult>();
                    var valid = candidates.Where(v => v.CountsAsValid && Qualifies(v.Link, rule, context, roles, request)).Select(v => v.Link.Identity.VersionedKey).Distinct().Count();
                    bool maybe = !module.LinksComplete || !module.InventoryComplete || node.Access != BaselineVerificationReadState.Verified || candidates.Any(v => IsUncertain(v) || v.CountsAsValid && !CounterpartRoleKnown(v.Link, rule, roles, context, request));
                    if (rule.RejectUnexpectedCounterparts && candidates.Any(v => v.CountsAsValid && CounterpartRoleKnown(v.Link, rule, roles, context, request) && !rule.CounterpartRoles.Contains(CounterpartRole(v.Link, rule, roles, context, request))))
                        finding("UnexpectedCounterpartType", TraceabilitySeverity.Error, node.NodeIdentity, "A resolved link points to a requirement type outside the project's allowed relationship.", false, rule.Id);
                    if (valid < rule.MinimumLinks)
                    {
                        satisfied = false;
                        if (maybe) { unknown = true; finding("ExpectedLinkUnresolved", TraceabilitySeverity.Warning, node.NodeIdentity, "Insufficient confirmed links, but absence is not provable in this scope.", true, rule.Id); }
                        else { missing = true; finding("MissingExpectedLink", TraceabilitySeverity.Error, node.NodeIdentity, "Policy requires " + rule.MinimumLinks + " link(s); observed qualifying links=" + valid + ".", false, rule.Id);
                            result.Links.Add(new BaselineLinkVerificationResult { Status = BaselineLinkStatus.MissingExpectedLink, UnderlyingStatus = BaselineLinkStatus.MissingExpectedLink, Reason = node.NodeIdentity + "; Rule=" + rule.Id }); }
                    }
                    if (rule.MaximumLinks.HasValue && valid > rule.MaximumLinks.Value) { satisfied = false; finding("MaximumExpectedLinksExceeded", TraceabilitySeverity.Error, node.NodeIdentity, "Observed qualifying links exceed policy maximum.", false, rule.Id); }
                }
                if (requires)
                { if (unknown) { unresolved.Add(node.NodeIdentity); coverage.UnresolvedRequirements++; }
                    else if (missing) coverage.RequirementsMissingLinks++; else if (satisfied) coverage.RequirementsWithValidLinks++; }
                var adjacent = (bySource.TryGetValue(node.NodeIdentity, out var outgoing) ? outgoing : Array.Empty<BaselineLinkVerificationResult>()).Concat(byTarget.TryGetValue(node.NodeIdentity, out var incoming) ? incoming : Array.Empty<BaselineLinkVerificationResult>());
                if (request.Policy.OrphanRoles.Contains(node.RequirementType) && !adjacent.Any(v => v.CountsAsValid)) finding(module.LinksComplete && !unknown ? "OrphanRequirement" : "OrphanUnresolved", request.Policy.OrphanSeverity, node.NodeIdentity, "No confirmed incoming/outgoing link in the requested policy scope.", !module.LinksComplete || unknown, null);
            }
            coverage.DenominatorComplete = context.MetadataComplete && !snapshot.Truncated && !snapshot.ContextChanged && coverage.UnenumeratedModules == 0 && coverage.UnknownPolicyEligibility == 0;
            if (coverage.RequirementsRequiringLinks > 0)
            { coverage.KnownScopeLowerBoundPercentage = 100.0 * coverage.RequirementsWithValidLinks / coverage.RequirementsRequiringLinks; coverage.KnownScopeUpperBoundPercentage = 100.0 * (coverage.RequirementsWithValidLinks + coverage.UnresolvedRequirements) / coverage.RequirementsRequiringLinks;
                if (coverage.DenominatorComplete) coverage.TraceabilityCoveragePercentage = coverage.KnownScopeLowerBoundPercentage; }
            AnalyzeChains(request, result, required, unresolved, finding, token);
            if (result.Graph != null) foreach (var cycle in result.Graph.Cycles)
                finding("CycleDetected", cycle.Behavior == TraceabilityCycleBehavior.Prohibit ? TraceabilitySeverity.Error : cycle.Behavior == TraceabilityCycleBehavior.Report ? TraceabilitySeverity.Warning : TraceabilitySeverity.Information,
                    null, "Strongly connected component; policy=" + cycle.Behavior + "; nodes=" + cycle.NodeIds.Count, false, cycle.RuleId);
            if (snapshot.Truncated || snapshot.ContextChanged) finding("IncompleteCapture", TraceabilitySeverity.Warning, null, string.Join("; ", snapshot.Limitations), true, null);
            result.Status = !contextValid ? BaselineVerificationStatus.Unknown : result.Findings.Any(f => f.Severity == TraceabilitySeverity.Error) ? BaselineVerificationStatus.Failed :
                !coverage.DenominatorComplete || result.Findings.Any(f => f.Uncertain) || context.Modules.Any(m => m.State == BaselineVerificationReadState.Missing || m.State == BaselineVerificationReadState.AccessDenied || m.State == BaselineVerificationReadState.Unknown) ? BaselineVerificationStatus.Incomplete :
                result.Findings.Any(f => f.Severity == TraceabilitySeverity.Warning) ? BaselineVerificationStatus.PassedWithWarnings : BaselineVerificationStatus.Passed;
        }
        private static BaselineLinkVerificationResult VerifyLink(BaselineLinkInfo link, Dictionary<string, ModuleBaselineVerificationResult> modules, BaselineVerificationContext context, BaselineSetVerificationRequest request)
        {
            BaselineLinkStatus status; string reason; bool valid = false;
            if (link.SourceAccess == BaselineVerificationReadState.AccessDenied || link.TargetAccess == BaselineVerificationReadState.AccessDenied) { status = BaselineLinkStatus.AccessDenied; reason = "Endpoint access denied; no loss claim."; }
            else if (link.SourceVersion == null || link.SourceObject <= 0 || !link.SourceObject.HasValue || !link.SourceVersion.IdentityVerified || link.SourceAccess != BaselineVerificationReadState.Verified) { status = BaselineLinkStatus.UnresolvedSource; reason = "Source stable identity/object/version evidence is unresolved."; }
            else if (link.TargetVersion == null || link.TargetObject <= 0 || !link.TargetObject.HasValue || !link.TargetVersion.IdentityVerified || link.TargetAccess != BaselineVerificationReadState.Verified) { status = BaselineLinkStatus.UnresolvedTarget; reason = "Target stable identity/object/version evidence is unresolved."; }
            else if (link.Identity == null || link.SourceVersion.RepositoryId != context.RepositoryId || link.TargetVersion.RepositoryId != context.RepositoryId) { status = BaselineLinkStatus.Unknown; reason = "Link module or repository identity is unverified."; }
            else
            {
                modules.TryGetValue(link.SourceVersion.ModuleIdentity, out var source); modules.TryGetValue(link.TargetVersion.ModuleIdentity, out var target);
                if (source != null && source.State != BaselineVerificationReadState.Verified || target != null && target.State != BaselineVerificationReadState.Verified) { status = BaselineLinkStatus.Unknown; reason = "Set member baseline/access state is unresolved or missing."; }
                else if (source != null && source.Version?.ModuleBaselineIdentity != link.SourceVersion.ModuleBaselineIdentity || target != null && target.Version?.ModuleBaselineIdentity != link.TargetVersion.ModuleBaselineIdentity)
                { status = BaselineLinkStatus.UnexpectedVersionTarget; reason = "Actual source/target version differs from that module's selected set baseline; numeric set version is not a module version."; }
                else if (source != null && target != null && source.InSet && target.InSet) { status = BaselineLinkStatus.ValidInternalBaselineLink; valid = true; reason = "Both endpoints bind to their independently observed set module baselines."; }
                else { status = BaselineLinkStatus.ValidExternalLink; valid = request.AllowExternalLinks; reason = "Explicitly resolved external endpoint version; subject to project policy."; }
            }
            if (link.IsEchoed && status == BaselineLinkStatus.ValidInternalBaselineLink) { valid = false; reason = "Echo within the selected set requires counterpart/context review; echo alone does not prove loss."; }
            return new BaselineLinkVerificationResult { Link = link, UnderlyingStatus = status, Status = link.IsEchoed && (status == BaselineLinkStatus.ValidExternalLink || status == BaselineLinkStatus.ValidInternalBaselineLink) ? BaselineLinkStatus.EchoedLink : status, CountsAsValid = valid, Reason = reason };
        }
        private static bool IsUncertain(BaselineLinkVerificationResult v) => v.UnderlyingStatus == BaselineLinkStatus.Unknown || v.UnderlyingStatus == BaselineLinkStatus.AccessDenied || v.UnderlyingStatus == BaselineLinkStatus.UnresolvedSource || v.UnderlyingStatus == BaselineLinkStatus.UnresolvedTarget || v.Status == BaselineLinkStatus.EchoedLink && !v.CountsAsValid;
        private static string CounterpartRole(BaselineLinkInfo link, TraceabilityLinkRule rule, Dictionary<string, string> roles, BaselineVerificationContext context, BaselineSetVerificationRequest request)
        { var version = rule.Direction == TraceabilityDirection.Outgoing ? link.TargetVersion : link.SourceVersion; int? absolute = rule.Direction == TraceabilityDirection.Outgoing ? link.TargetObject : link.SourceObject;
            if (version == null || !absolute.HasValue) return null; if (roles.TryGetValue(version.NodeIdentity(absolute.Value), out var role)) return role;
            return request.TypeAttribute.Length > 0 ? null : request.ModuleRoles.TryGetValue(version.ModuleIdentity, out var mapped) ? mapped : context.Modules.FirstOrDefault(m => m.ModuleIdentity == version.ModuleIdentity)?.RequirementType; }
        private static bool CounterpartRoleKnown(BaselineLinkInfo link, TraceabilityLinkRule rule, Dictionary<string, string> roles, BaselineVerificationContext context, BaselineSetVerificationRequest request) => !string.IsNullOrWhiteSpace(CounterpartRole(link, rule, roles, context, request));
        private static bool Qualifies(BaselineLinkInfo link, TraceabilityLinkRule rule, BaselineVerificationContext context, Dictionary<string, string> roles, BaselineSetVerificationRequest request)
        {
            var version = rule.Direction == TraceabilityDirection.Outgoing ? link.TargetVersion : link.SourceVersion;
            if (rule.RequiredNativeLinkModuleId != null && link.LinkModuleIdentity != rule.RequiredNativeLinkModuleId || !rule.CounterpartRoles.Contains(CounterpartRole(link, rule, roles, context, request))) return false;
            return rule.VersionPolicy == TraceabilityVersionPolicy.AnyExplicitVersion || rule.VersionPolicy == TraceabilityVersionPolicy.CurrentCounterpart && version.IsCurrent ||
                rule.VersionPolicy == TraceabilityVersionPolicy.SameVersion && context.Modules.Any(m => m.InSet && m.Version?.ModuleBaselineIdentity == version.ModuleBaselineIdentity);
        }
        private static void AnalyzeChains(BaselineSetVerificationRequest request, BaselineSetVerificationResult result, HashSet<string> required, HashSet<string> unresolved,
            Action<string, TraceabilitySeverity, string, string, bool, string> finding, CancellationToken token)
        {
            var graph = result.Graph; if (graph == null) return;
            var relationships = request.Policy.LinkRules.Where(r => r.MinimumLinks > 0).SelectMany(r => r.CounterpartRoles.Select(other => r.Direction == TraceabilityDirection.Outgoing ? Tuple.Create(r.AnchorRole, other) : Tuple.Create(other, r.AnchorRole))).ToArray();
            var anchors = new HashSet<string>(relationships.Select(r => r.Item1));
            var terminals = new HashSet<string>(relationships.Select(r => r.Item2).Where(r => !anchors.Contains(r)));
            var seeds = graph.Nodes.Values.Where(n => n.IsInventoryObject && !n.HasUncertainAccess && !n.IsDeleted && terminals.Contains(n.Role)).Select(n => n.Id).ToArray();
            if (terminals.Count == 0) return; // A cyclic policy need not define an end-to-end terminal.
            var eligible = graph.Edges.Where(e => e.State == TraceabilityEdgeState.Confirmed && request.Policy.LinkRules.Any(r => r.LinkModulePath == e.LinkModulePath &&
                (r.Direction == TraceabilityDirection.Outgoing ? r.AnchorRole == graph.Nodes[e.SourceId].Role && r.CounterpartRoles.Contains(graph.Nodes[e.TargetId].Role) : r.AnchorRole == graph.Nodes[e.TargetId].Role && r.CounterpartRoles.Contains(graph.Nodes[e.SourceId].Role)))).ToArray();
            var directed = new TraceabilityGraph(graph.GraphId, graph.Request, graph.Nodes.ToDictionary(x => x.Key, x => x.Value), eligible, graph.Observations, graph.Scopes, graph.Findings, graph.Cycles, graph.IsCompleteWithinRequestedScope);
            var reached = seeds.Length == 0 ? new HashSet<string>() : new HashSet<string>(GraphAlgorithms.Impact(directed, seeds, TraceabilityDirection.Incoming, 100000, false, request.MaxRequirements, request.MaxLinks, token).Visits.Select(v => v.NodeId));
            var uncertainSeeds = graph.Nodes.Values.Where(n => unresolved.Contains(n.VerificationNodeIdentity ?? "") || terminals.Contains(n.Role) && n.HasUncertainAccess).Select(n => n.Id).ToArray();
            var uncertain = uncertainSeeds.Length == 0 ? new HashSet<string>() : new HashSet<string>(GraphAlgorithms.Impact(directed, uncertainSeeds, TraceabilityDirection.Incoming, 100000, false, request.MaxRequirements, request.MaxLinks, token).Visits.Select(v => v.NodeId));
            foreach (var node in graph.Nodes.Values.Where(n => n.IsInventoryObject && required.Contains(n.VerificationNodeIdentity)))
                if (!reached.Contains(node.Id))
                { bool unknown = uncertain.Contains(node.Id); if (!unknown) result.Coverage.BrokenTraceabilityChains++; finding(unknown ? "BrokenChainUnresolved" : "BrokenTraceabilityChain", unknown ? TraceabilitySeverity.Warning : TraceabilitySeverity.Error,
                    node.VerificationNodeIdentity, "No confirmed policy path to a terminal requirement type.", unknown, null); }
        }
    }
}
