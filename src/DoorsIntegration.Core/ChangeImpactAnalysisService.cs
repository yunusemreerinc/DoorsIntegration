using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class ChangeImpactAnalysisService : IChangeImpactAnalysisService
    {
        private readonly IImpactSnapshotProvider snapshots; private readonly IRequirementChangeDetector detector;
        private readonly IImpactPropagationEngine propagation; private readonly ImpactClassificationService classification;
        private readonly ImpactReviewService reviews; private readonly IIntegrationLogger logger; private readonly IBaselineVerificationAuditSink audit;
        public ChangeImpactAnalysisService(IImpactSnapshotProvider snapshots, IRequirementChangeDetector detector, IImpactPropagationEngine propagation,
            ImpactClassificationService classification, ImpactReviewService reviews, IIntegrationLogger logger, IBaselineVerificationAuditSink audit)
        { this.snapshots = snapshots; this.detector = detector; this.propagation = propagation; this.classification = classification; this.reviews = reviews; this.logger = logger; this.audit = audit; }
        public Task<ChangeImpactResult> AnalyzeAsync(ChangeImpactRequest request, IProgress<string> progress = null, CancellationToken token = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            request = new ChangeImpactRequest(CopySelection(request.Source), CopySelection(request.Target), ImpactSerialization.Clone(request.Policy), request.Kind, request.Graph);
            return Task.Run(async () =>
            {
                var r = new ChangeImpactResult { AnalysisId = Guid.NewGuid(), StartedAtUtc = DateTime.UtcNow, Kind = request.Kind, GraphChoice = request.Graph, PolicyFingerprint = request.Policy.Fingerprint, Policy = request.Policy };
                var watch = Stopwatch.StartNew(); long memory = GC.GetTotalMemory(false); bool auditAvailable = Record(r, "ImpactStarted", request.Source.EnvironmentId, "", "Started", "");
                using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    bounded.CancelAfter(TimeSpan.FromSeconds(request.Policy.TimeoutSeconds));
                    try
                    {
                        r.Source = await snapshots.CaptureAsync(request.Source, request.Policy, progress, bounded.Token).ConfigureAwait(false);
                        bounded.Token.ThrowIfCancellationRequested();
                        if (r.Source.TransportUncertain) throw new IntegrationException(IntegrationErrorCode.DxlTimeout, "Source transport outcome uncertain; target capture was not dispatched.");
                        r.Target = await snapshots.CaptureAsync(request.Target, request.Policy, progress, bounded.Token).ConfigureAwait(false);
                        bounded.Token.ThrowIfCancellationRequested();
                        if (r.Target.TransportUncertain) throw new IntegrationException(IntegrationErrorCode.DxlTimeout, "Target transport outcome uncertain; analysis was not declared complete.");
                        progress?.Report("Comparing persistent module/object identities and typed attributes.");
                        r.Changes.AddRange(detector.Detect(r.Source, r.Target, request.Policy, bounded.Token));
                        bool complete = new[] { r.Source, r.Target }.All(s => !s.IsStale && !s.Links.Truncated && !s.Links.ContextChanged && s.Links.Context.MetadataComplete && s.Links.Context.Modules.Where(m => m.SelectedForVerification).All(m => m.State == BaselineVerificationReadState.Verified && m.InventoryComplete && m.LinksComplete));
                        r.Status = complete && r.Changes.All(c => c.IsConclusive) ? ImpactAnalysisStatus.Completed : ImpactAnalysisStatus.Incomplete;
                        r.Limitations.AddRange(r.Source.Limitations.Concat(r.Target.Limitations));
                        if (r.Source.IsStale || r.Target.IsStale) r.Limitations.Add("A supplied snapshot is marked stale; analysis is incomplete. No implicit cache refresh or hidden current-version substitution occurred.");
                        CompareLinks(r, request.Policy, bounded.Token);
                        foreach (var c in r.Changes) classification.Classify(c, request.Policy);
                        propagation.Propagate(r, request.Policy, progress, bounded.Token);
                        try { reviews?.Apply(r); } catch (Exception) { r.Status = ImpactAnalysisStatus.Incomplete; r.Limitations.Add("Protected local review history unavailable; no decisions were silently restored."); }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { ImpactPropagationEngine.Limit(r, "Analysis timeout; native COM may still be running. No automatic retry or write was attempted."); }
                    catch (OperationCanceledException) { Record(r, "ImpactCancelled", request.Source.EnvironmentId, "", "Cancelled", ""); throw; }
                    catch (IntegrationException ex) { r.Status = ImpactAnalysisStatus.Unknown; r.Limitations.Add("Read-only evidence unavailable: " + ex.Code + ". COM outcome may be uncertain; no automatic retry."); }
                }
                token.ThrowIfCancellationRequested(); r.CompletedAtUtc = DateTime.UtcNow; r.ElapsedMilliseconds = watch.ElapsedMilliseconds; r.ManagedMemoryDeltaBytes = Math.Max(0, GC.GetTotalMemory(false) - memory);
                auditAvailable &= Record(r, "ImpactCompleted", request.Source.EnvironmentId, "", r.Status.ToString(), r.Source == null || r.Target == null ? "" : r.SnapshotIdentity);
                if (!auditAvailable) { r.Status = ImpactAnalysisStatus.Incomplete; r.Limitations.Add("Protected local audit unavailable; no plaintext fallback."); }
                try { logger.Write(IntegrationLogLevel.Information, "ChangeImpactCompleted", r.AnalysisId, "ReadOnly=True; Status=" + r.Status + "; UniqueRequirements=" + r.UniqueImpactedRequirementCount + "; Paths=" + r.ImpactPathCount + "; ElapsedMs=" + r.ElapsedMilliseconds); } catch (Exception) { }
                return r;
            }, token);
        }
        private void CompareLinks(ChangeImpactResult result, ImpactAnalysisPolicy policy, CancellationToken token)
        {
            // Version mapping is supported by independently captured stable module identities, not a guessed native echo rule.
            var target = result.Target.Links.Context.Modules.ToDictionary(m => m.ModuleIdentity, StringComparer.Ordinal);
            var mappings = result.Source.Links.Context.Modules.Where(m => m.Version != null && m.State == BaselineVerificationReadState.Verified && m.InventoryComplete && m.LinksComplete && target.TryGetValue(m.ModuleIdentity, out var t) && t.Version != null && t.State == BaselineVerificationReadState.Verified && t.InventoryComplete && t.LinksComplete)
                .Select(m => new BaselineVersionRebinding { BeforeVersionIdentity = m.Version.ModuleBaselineIdentity, AfterVersionIdentity = target[m.ModuleIdentity].Version.ModuleBaselineIdentity, AdapterValidated = true, Evidence = "Stable module identity and explicitly selected independently observed comparison versions; not a claim of automatic DOORS echo/rebinding." }).ToArray();
            var old = result.Source.Links.Links.Where(l => l.Identity != null).GroupBy(l => l.Identity.VersionedKey).ToDictionary(g => g.Key, g => g.First());
            var current = result.Target.Links.Links.Where(l => l.Identity != null).GroupBy(l => l.Identity.VersionedKey).ToDictionary(g => g.Key, g => g.First());
            var changes = result.Changes.ToDictionary(c => c.Identity);
            foreach (var observation in new BaselineTraceabilityComparisonService().Compare(result.Source.Links, result.Target.Links, mappings, token))
            {
                BaselineLinkInfo a = null, b = null; if (observation.BeforeIdentity != null) old.TryGetValue(observation.BeforeIdentity, out a); if (observation.AfterIdentity != null) current.TryGetValue(observation.AfterIdentity, out b);
                result.LinkChanges.Add(new LinkChange { Observation = observation, Before = a, After = b });
                if (!observation.Confirmed && observation.Kind != BaselineLinkChangeKind.ExpectedVersionRebinding || observation.Kind == BaselineLinkChangeKind.ChangedModuleVersion) result.Status = ImpactAnalysisStatus.Incomplete;
                var endpoints = new[] { a, b }.Where(l => l != null).SelectMany(l => new[] { l.SourceVersion != null && l.SourceObject > 0 ? l.SourceVersion.ObjectIdentity(l.SourceObject.Value) : null, l.TargetVersion != null && l.TargetObject > 0 ? l.TargetVersion.ObjectIdentity(l.TargetObject.Value) : null }).Where(id => id != null).Distinct();
                foreach (string endpoint in endpoints)
                {
                if (!changes.TryGetValue(endpoint, out var c)) continue;
                if (!observation.Confirmed && observation.Kind != BaselineLinkChangeKind.ExpectedVersionRebinding) { c.Flags |= RequirementChangeFlags.Unknown; result.Status = ImpactAnalysisStatus.Incomplete; }
                if (observation.Confirmed) switch (observation.Kind)
                {
                    case BaselineLinkChangeKind.AddedLink: c.Flags |= RequirementChangeFlags.LinkAdded; break;
                    case BaselineLinkChangeKind.RemovedLink: c.Flags |= RequirementChangeFlags.LinkRemoved; break;
                    case BaselineLinkChangeKind.ChangedLinkTarget: c.Flags |= RequirementChangeFlags.LinkTargetChanged; break;
                    case BaselineLinkChangeKind.UnexpectedDuplicateLink: c.Flags |= RequirementChangeFlags.Unknown; result.Status = ImpactAnalysisStatus.Incomplete; break;
                    case BaselineLinkChangeKind.ChangedModuleVersion: c.Flags |= RequirementChangeFlags.Unknown; result.Status = ImpactAnalysisStatus.Incomplete; break;
                }
                RequirementChangeDetector.SetPrimary(c);
                }
            }
            foreach (var link in result.Target.Links.Links.Where(l => l.Identity == null))
            {
                result.Status = ImpactAnalysisStatus.Incomplete;
                result.LinkChanges.Add(new LinkChange { After = link, Observation = new BaselineTraceabilityChange { Kind = BaselineLinkChangeKind.UnresolvedLink, Confirmed = false, Evidence = "Actual endpoint/module/object identity is unavailable; raw observation retained. Not a removed-link or data-loss claim." } });
                if (link.SourceVersion != null && link.SourceObject > 0 && changes.TryGetValue(link.SourceVersion.ObjectIdentity(link.SourceObject.Value), out var change)) { change.Flags |= RequirementChangeFlags.Unknown; RequirementChangeDetector.SetPrimary(change); }
            }
        }
        private bool Record(ChangeImpactResult r, string phase, string environment, string set, string status, string fingerprint)
        { try { audit?.Record(r.AnalysisId, phase, r.Source?.Links.Context.Source ?? BaselineMetadataSource.NotRun, environment, set, status, fingerprint); return audit != null; } catch (Exception) { return false; } }
        private static ImpactSnapshotSelection CopySelection(ImpactSnapshotSelection s) => new ImpactSnapshotSelection { Label = s.Label, MockSnapshotKey = s.MockSnapshotKey, EnvironmentId = s.EnvironmentId, RepositoryId = s.RepositoryId, ClientVersion = s.ClientVersion, BaselineSet = s.BaselineSet, Modules = s.Modules.ToList(), LinkModulePaths = s.LinkModulePaths.ToList() };
    }
}
