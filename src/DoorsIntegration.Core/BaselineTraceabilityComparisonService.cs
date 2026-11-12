using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class BaselineTraceabilityComparisonService
    {
        public IReadOnlyList<BaselineTraceabilityChange> Compare(BaselineLinkSnapshot before, BaselineLinkSnapshot after,
            IEnumerable<BaselineVersionRebinding> rebindings = null, CancellationToken token = default(CancellationToken))
        {
            if (after == null) throw new ArgumentNullException(nameof(after)); var changes = new List<BaselineTraceabilityChange>();
            foreach (var duplicate in after.Links.Where(l => l.Identity != null && l.PhysicalMultiplicityVerified && !string.IsNullOrEmpty(l.PhysicalLinkIdentity)).GroupBy(l => l.Identity.VersionedKey))
            {
                token.ThrowIfCancellationRequested(); int physical = duplicate.Select(l => l.PhysicalLinkIdentity).Distinct().Count();
                if (physical > 1) changes.Add(new BaselineTraceabilityChange { Kind = BaselineLinkChangeKind.UnexpectedDuplicateLink, AfterIdentity = duplicate.Key, Confirmed = true, Evidence = "Adapter established distinct physical links=" + physical + "; incoming/outgoing observations and echoes alone are not duplicates." });
            }
            if (before == null) return changes; // No invented historical state.
            bool sameContext = before.Context.Source == after.Context.Source && before.Context.RepositoryId == after.Context.RepositoryId && before.Context.EnvironmentId == after.Context.EnvironmentId && before.Context.Definition?.Id == after.Context.Definition?.Id;
            if (!sameContext) { changes.Add(new BaselineTraceabilityChange { Kind = BaselineLinkChangeKind.Indeterminate, Evidence = "Reference repository/environment/definition/source context differs; no historical loss claim." }); return changes; }
            var bindings = (rebindings ?? Enumerable.Empty<BaselineVersionRebinding>()).Where(r => r.AdapterValidated && !string.IsNullOrWhiteSpace(r.Evidence)).ToArray();
            var old = before.Links.Where(Resolved).GroupBy(l => l.Identity.VersionedKey).ToDictionary(g => g.Key, g => g.First());
            var current = after.Links.Where(Resolved).GroupBy(l => l.Identity.VersionedKey).ToDictionary(g => g.Key, g => g.First());
            foreach (string key in old.Keys.Intersect(current.Keys).ToArray()) { old.Remove(key); current.Remove(key); }
            var oldPersistent = old.Values.GroupBy(l => l.Identity.PersistentKey).ToDictionary(g => g.Key, g => g.ToArray());
            var newPersistent = current.Values.GroupBy(l => l.Identity.PersistentKey).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var group in oldPersistent)
            {
                token.ThrowIfCancellationRequested();
                if (group.Value.Length != 1 || !newPersistent.TryGetValue(group.Key, out var candidates) || candidates.Length != 1) continue;
                var a = group.Value[0]; var b = candidates[0];
                bool expected = Rebound(a.SourceVersion, b.SourceVersion, bindings) && Rebound(a.TargetVersion, b.TargetVersion, bindings);
                changes.Add(new BaselineTraceabilityChange { Kind = expected ? BaselineLinkChangeKind.ExpectedVersionRebinding : BaselineLinkChangeKind.ChangedModuleVersion,
                    BeforeIdentity = a.Identity.VersionedKey, AfterIdentity = b.Identity.VersionedKey, Confirmed = true, Evidence = expected ? "Explicit adapter-validated module-version rebinding; not a removed link." : "Persistent endpoints/link-module are unchanged; actual module-version context changed." });
                old.Remove(a.Identity.VersionedKey); current.Remove(b.Identity.VersionedKey);
            }
            var targets = current.Values.GroupBy(l => l.Identity.AnchorKey).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var group in old.Values.GroupBy(l => l.Identity.AnchorKey).ToArray())
            {
                token.ThrowIfCancellationRequested(); var previous = group.ToArray();
                if (previous.Length != 1 || !targets.TryGetValue(group.Key, out var next) || next.Length != 1) continue;
                var a = previous[0]; var b = next[0]; bool comparable = Complete(before, a) && Complete(after, b) && Rebound(a.SourceVersion, b.SourceVersion, bindings);
                changes.Add(new BaselineTraceabilityChange { Kind = comparable ? BaselineLinkChangeKind.ChangedLinkTarget : BaselineLinkChangeKind.Indeterminate,
                    BeforeIdentity = a.Identity.VersionedKey, AfterIdentity = b.Identity.VersionedKey, Confirmed = comparable, Evidence = "Unique source-object/link-module anchor; " + (comparable ? "target changed in comparable complete scopes." : "scope/version transition is not proved comparable.") });
                old.Remove(a.Identity.VersionedKey); current.Remove(b.Identity.VersionedKey);
            }
            // Build once: rescanning every new observation for every removed link is quadratic.
            var unresolvedSources = new HashSet<string>(after.Links.Where(l => !Resolved(l) || l.TargetAccess != BaselineVerificationReadState.Verified)
                .Where(l => !string.IsNullOrWhiteSpace(l.SourceModuleIdentity) && l.SourceObject > 0)
                .Select(l => SourceObjectIdentity(after.Context.RepositoryId, l.SourceModuleIdentity, l.SourceObject.Value)), StringComparer.Ordinal);
            var targetFactsByModule = after.Context.Modules.GroupBy(m => m.ModuleIdentity).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            foreach (var link in old.Values)
            {
                token.ThrowIfCancellationRequested();
                bool afterScope = EquivalentSourceScope(after, link.SourceVersion, bindings);
                bool complete = Complete(before, link) && afterScope && !after.Truncated && !after.ContextChanged && after.Context.MetadataComplete && after.CapturedLinkModuleIdentities.Contains(link.LinkModuleIdentity);
                bool unresolved = unresolvedSources.Contains(link.SourceVersion.ObjectIdentity(link.SourceObject.Value));
                complete &= !unresolved;
                bool risky = before.Context.Modules.Concat(after.Context.Modules).Any(m => m.ShareableConfiguration == BaselineShareableConfiguration.Configured && (m.ModuleIdentity == link.SourceVersion.ModuleIdentity || m.ModuleIdentity == link.TargetVersion.ModuleIdentity));
                bool targetVerified = targetFactsByModule.TryGetValue(link.TargetVersion.ModuleIdentity, out var targetFacts) && targetFacts.Length > 0 &&
                    targetFacts.All(m => m.State == BaselineVerificationReadState.Verified && m.Version != null && Rebound(link.TargetVersion, m.Version, bindings));
                changes.Add(new BaselineTraceabilityChange { Kind = unresolved ? BaselineLinkChangeKind.UnresolvedLink : complete ? BaselineLinkChangeKind.RemovedLink : BaselineLinkChangeKind.Indeterminate,
                    BeforeIdentity = link.Identity.VersionedKey, Confirmed = complete, SuspectedLinkLoss = complete && risky && targetVerified,
                    Evidence = complete ? "Reference link absent in a complete comparable source/link-module scope. Cause is unproven; no automatic repair." : "Reference link cannot be verified in the new access/version/scope context; no loss assertion." });
            }
            foreach (var link in current.Values)
            {
                token.ThrowIfCancellationRequested(); bool known = Complete(after, link) && EquivalentTargetScope(before, link.SourceVersion, bindings) && before.CapturedLinkModuleIdentities.Contains(link.LinkModuleIdentity) && !before.Truncated && !before.ContextChanged && before.Context.MetadataComplete;
                changes.Add(new BaselineTraceabilityChange { Kind = known ? BaselineLinkChangeKind.AddedLink : BaselineLinkChangeKind.Indeterminate, AfterIdentity = link.Identity.VersionedKey, Confirmed = known, Evidence = known ? "New observed link in comparable complete scopes." : "Newly observed link; prior absence is not established." });
            }
            foreach (var link in after.Links.Where(l => !Resolved(l))) changes.Add(new BaselineTraceabilityChange { Kind = BaselineLinkChangeKind.UnresolvedLink, AfterIdentity = link.Identity?.VersionedKey, Evidence = "Unresolved identity/access/version observation; not evidence of loss." });
            return changes;
        }
        private static bool Resolved(BaselineLinkInfo l) => l.Identity != null && l.SourceVersion.IdentityVerified && l.TargetVersion.IdentityVerified && l.SourceAccess == BaselineVerificationReadState.Verified && l.TargetAccess == BaselineVerificationReadState.Verified;
        private static string SourceObjectIdentity(string repository, string module, int absolute) => GraphGuard.Hash(repository, module, absolute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        private static bool Rebound(BaselineVersionReference a, BaselineVersionReference b, BaselineVersionRebinding[] bindings) => a.ModuleBaselineIdentity == b.ModuleBaselineIdentity || a.RepositoryId == b.RepositoryId && a.ModuleIdentity == b.ModuleIdentity && bindings.Any(r => r.BeforeVersionIdentity == a.ModuleBaselineIdentity && r.AfterVersionIdentity == b.ModuleBaselineIdentity);
        private static bool Complete(BaselineLinkSnapshot s, BaselineLinkInfo link) => !s.Truncated && !s.ContextChanged && s.Context.MetadataComplete && s.CapturedLinkModuleIdentities.Contains(link.LinkModuleIdentity) &&
            s.Context.Modules.Any(m => m.SelectedForVerification && m.Version?.ModuleBaselineIdentity == link.SourceVersion.ModuleBaselineIdentity && m.InventoryComplete && m.LinksComplete && m.State == BaselineVerificationReadState.Verified);
        private static bool EquivalentSourceScope(BaselineLinkSnapshot s, BaselineVersionReference previous, BaselineVersionRebinding[] bindings) => s.Context.Modules.Any(m => m.SelectedForVerification && m.Version != null && Rebound(previous, m.Version, bindings) && m.State == BaselineVerificationReadState.Verified && m.InventoryComplete && m.LinksComplete);
        private static bool EquivalentTargetScope(BaselineLinkSnapshot s, BaselineVersionReference next, BaselineVersionRebinding[] bindings) => s.Context.Modules.Any(m => m.SelectedForVerification && m.Version != null && Rebound(m.Version, next, bindings) && m.State == BaselineVerificationReadState.Verified && m.InventoryComplete && m.LinksComplete);
    }
    public sealed class BaselineSetVerificationService : IBaselineSetVerificationService
    {
        private readonly BaselineLinkSnapshotService snapshots; private readonly IBaselineSetVerificationSource source;
        private readonly BaselineTraceabilityAnalyzer analyzer; private readonly BaselineTraceabilityComparisonService comparison;
        private readonly IBaselineVerificationAuditSink audit; private readonly IIntegrationLogger logger;
        public BaselineSetVerificationService(BaselineLinkSnapshotService snapshots, IBaselineSetVerificationSource source, BaselineTraceabilityAnalyzer analyzer,
            BaselineTraceabilityComparisonService comparison, IBaselineVerificationAuditSink audit, IIntegrationLogger logger)
        { this.snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots)); this.source = source ?? throw new ArgumentNullException(nameof(source)); this.analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer)); this.comparison = comparison ?? throw new ArgumentNullException(nameof(comparison)); this.audit = audit ?? throw new ArgumentNullException(nameof(audit)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }
        public Task<BaselineSetVerificationResult> VerifyAsync(BaselineSetVerificationRequest request, IProgress<string> progress = null, CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return Task.Run(async () =>
            {
                var result = new BaselineSetVerificationResult { VerificationJobId = Guid.NewGuid(), StartedAtUtc = DateTime.UtcNow, ProjectId = request.ProjectId, PolicyId = request.Policy.Id, PolicyFingerprint = request.Policy.Fingerprint, Status = BaselineVerificationStatus.Unknown,
                    Snapshot = new BaselineLinkSnapshot { Context = new BaselineVerificationContext { Source = source.Source, Reason = "No verified read context." } }, Coverage = new TraceabilityCoverageResult() };
                bool auditAvailable = TryAudit(result.VerificationJobId, "Started", "", request.SetId, "ReadOnly", request.Policy.Fingerprint);
                try
                {
                    result.Snapshot = await snapshots.CaptureAsync(request, progress, token).ConfigureAwait(false); progress?.Report("Analyze baseline bindings, policy coverage and graph");
                    analyzer.Analyze(request, result, token);
                    result.ReferenceAvailable = request.Reference != null; result.Changes = comparison.Compare(request.Reference, result.Snapshot, request.Rebindings, token).ToList();
                    if (!result.ReferenceAvailable) result.Findings.Add(new TraceabilityViolation { Code = "ReferenceSnapshotUnavailable", Severity = TraceabilitySeverity.Information, Explanation = "No pre-set reference snapshot; no retrospective removed-link or link-loss claim is made." });
                    foreach (var change in result.Changes)
                        if (change.Kind == BaselineLinkChangeKind.UnexpectedDuplicateLink || change.SuspectedLinkLoss || change.Kind == BaselineLinkChangeKind.RemovedLink)
                        { result.Findings.Add(new TraceabilityViolation { Code = change.SuspectedLinkLoss ? "SuspectedLinkLoss" : change.Kind.ToString(), Severity = change.Kind == BaselineLinkChangeKind.UnexpectedDuplicateLink ? TraceabilitySeverity.Error : TraceabilitySeverity.Warning,
                            LinkIdentity = change.BeforeIdentity ?? change.AfterIdentity, Evidence = change.Evidence, Explanation = "Investigate recorded snapshot differences; no automatic link recreation or DOORS mutation is available.", Recommendation = "Inspect native history, access and IBM shareable-edit alert in a controlled environment." });
                          if (change.Kind == BaselineLinkChangeKind.UnexpectedDuplicateLink) result.Status = BaselineVerificationStatus.Failed;
                          else if (result.Status == BaselineVerificationStatus.Passed) result.Status = BaselineVerificationStatus.PassedWithWarnings; }
                    if (result.Changes.Any(c => c.Kind == BaselineLinkChangeKind.Indeterminate || c.Kind == BaselineLinkChangeKind.UnresolvedLink) && result.Status != BaselineVerificationStatus.Failed && result.Status != BaselineVerificationStatus.Unknown) result.Status = BaselineVerificationStatus.Incomplete;
                    foreach (var change in result.Changes.Where(c => c.Kind != BaselineLinkChangeKind.UnexpectedDuplicateLink && c.Kind != BaselineLinkChangeKind.RemovedLink))
                    {
                        if (result.Findings.Count >= request.MaxFindings) { result.Snapshot.Truncated = true; if (result.Status != BaselineVerificationStatus.Failed && result.Status != BaselineVerificationStatus.Unknown) result.Status = BaselineVerificationStatus.Incomplete; break; }
                        bool uncertain = change.Kind == BaselineLinkChangeKind.Indeterminate || change.Kind == BaselineLinkChangeKind.UnresolvedLink;
                        result.Findings.Add(new TraceabilityViolation { Code = change.Kind.ToString(), Severity = uncertain ? TraceabilitySeverity.Warning : TraceabilitySeverity.Information, LinkIdentity = change.BeforeIdentity ?? change.AfterIdentity, Evidence = change.Evidence, Explanation = change.Evidence, Uncertain = uncertain, Recommendation = uncertain ? "Review reference scope, permissions and explicit version binding; do not infer lost data." : "Review the recorded before/after relation." });
                    }
                }
                catch (OperationCanceledException) { TryAudit(result.VerificationJobId, "Cancelled", "", request.SetId, "Incomplete", ""); throw; }
                catch (IntegrationException)
                { result.Status = BaselineVerificationStatus.Unknown; result.Findings.Add(new TraceabilityViolation { Code = "ReadUnavailable", Severity = TraceabilitySeverity.Warning, Uncertain = true, Explanation = "Read-only COM/DXL evidence is unavailable or uncertain. Active COM can continue; no automatic retry or loss inference." }); }
                result.CompletedAtUtc = DateTime.UtcNow;
                if (!auditAvailable && result.Status != BaselineVerificationStatus.Failed && result.Status != BaselineVerificationStatus.Unknown) result.Status = BaselineVerificationStatus.Incomplete;
                auditAvailable &= TryAudit(result.VerificationJobId, "Completed", result.Snapshot?.Context.EnvironmentId ?? "", request.SetId, result.Status.ToString(), result.Snapshot?.Fingerprint ?? "");
                if (!auditAvailable)
                { result.Findings.Add(new TraceabilityViolation { Code = "AuditUnavailable", Severity = TraceabilitySeverity.Warning, Uncertain = true, Explanation = "Protected local audit persistence failed. Read-only analysis was retained; no plaintext fallback or DOORS mutation occurred." });
                    if (result.Status != BaselineVerificationStatus.Failed && result.Status != BaselineVerificationStatus.Unknown) result.Status = BaselineVerificationStatus.Incomplete; }
                try { logger.Write(IntegrationLogLevel.Information, "BaselineSetVerificationCompleted", result.VerificationJobId, "ReadOnly=True; Status=" + result.Status + "; Source=" + source.Source + "; AutomaticRepair=False"); } catch (Exception) { }
                return result;
            }, token);
        }
        private bool TryAudit(Guid id, string phase, string environment, string set, string status, string fingerprint)
        { try { audit.Record(id, phase, source.Source, environment, set, status, fingerprint); return true; } catch (Exception) { return false; } }
    }
}
