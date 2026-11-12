using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    public sealed class ImpactAnalysisReportService
    {
        public const string Disclosure = "Read-only potential impact candidates, not proof of downstream change, mandatory test re-execution, engineering correctness or certification compliance. An authorized engineer must approve the project policy and dispositions. Mock results do not validate native DOORS COM/DXL.";
        private readonly IExportService export;
        public ImpactAnalysisReportService(IExportService export) { this.export = export; }
        public Task<ExportResult> ExportAsync(ChangeImpactResult result, ExportRequest request, IProgress<ExportProgress> progress = null, CancellationToken token = default(CancellationToken))
            => export.ExportAsync(request, new EnumerableExportSource(Rows(result, token)), progress, token);
        private static IEnumerable<ExportRecord> Rows(ChangeImpactResult r, CancellationToken token)
        {
            string id = r.AnalysisId.ToString("N"); var impactCounts = r.Impacted.GroupBy(i => i.RootChangeIdentity).ToDictionary(g => g.Key, g => g.Select(i => i.RequirementIdentity).Distinct().Count());
            yield return Row(ExportTableKind.Scopes, "scopeId", id, "sourceKind", "ChangeImpactAnalysis", "isComplete", r.Status == ImpactAnalysisStatus.Completed && !r.Truncated, "details", Disclosure);
            yield return Row(ExportTableKind.ImpactAnalyses, "analysisId", id, "environment", r.Source?.Links.Context.EnvironmentId, "repository", r.Source?.Links.Context.RepositoryId, "source", r.Source?.Label, "target", r.Target?.Label,
                "comparisonKind", r.Kind, "graphChoice", r.GraphChoice, "status", r.Status, "snapshotIdentity", r.SnapshotIdentity, "policyFingerprint", r.PolicyFingerprint, "policyJson", r.Policy == null ? null : Encoding.UTF8.GetString(ImpactSerialization.Bytes(r.Policy)), "reviewDecisionCount", r.Reviews.Count, "startedAtUtc", r.StartedAtUtc, "completedAtUtc", r.CompletedAtUtc,
                "elapsedMs", r.ElapsedMilliseconds, "managedMemoryDeltaBytes", r.ManagedMemoryDeltaBytes, "changedRequirements", r.Changes.Count(c => c.IsChanged), "uniqueImpactedRequirements", r.UniqueImpactedRequirementCount, "unverifiedImpactReferences", r.UnverifiedImpactReferenceCount, "impactPathCount", r.ImpactPathCount, "truncated", r.Truncated, "disclosure", Disclosure);
            foreach (var phase in new[] { new { Name = "Source", Snapshot = r.Source }, new { Name = "Target", Snapshot = r.Target } })
            {
                if (phase.Snapshot == null) continue; string fingerprint = phase.Snapshot.Fingerprint;
                foreach (var m in phase.Snapshot.Links.Context.Modules)
                { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.ImpactComparedModules, "analysisId", id, "phase", phase.Name, "environment", phase.Snapshot.Links.Context.EnvironmentId, "repository", phase.Snapshot.Links.Context.RepositoryId,
                    "definitionIdentity", phase.Snapshot.Links.Context.Definition?.Id, "setIdentity", phase.Snapshot.Links.Context.Set?.Id, "setMajor", phase.Snapshot.Links.Context.Set?.Version.Major, "setMinor", phase.Snapshot.Links.Context.Set?.Version.Minor, "setSuffix", phase.Snapshot.Links.Context.Set?.Version.Suffix, "setIsOpen", phase.Snapshot.Links.Context.Set?.IsOpen,
                    "moduleIdentity", m.ModuleIdentity, "fullPath", m.FullPath, "moduleBaselineIdentity", m.Version?.ModuleBaselineIdentity, "version", m.Version?.Version, "major", m.Version?.IsCurrent == true ? null : (object)m.Version?.Major, "minor", m.Version?.IsCurrent == true ? null : (object)m.Version?.Minor, "suffix", m.Version?.IsCurrent == true ? null : m.Version?.Suffix, "isCurrent", m.Version?.IsCurrent,
                    "access", m.State, "inventoryComplete", m.InventoryComplete, "linksComplete", m.LinksComplete, "snapshotDate", phase.Snapshot.CapturedAtUtc, "snapshotFingerprint", fingerprint, "stale", phase.Snapshot.IsStale); }
            }
            foreach (var c in r.Changes)
            {
                token.ThrowIfCancellationRequested(); impactCounts.TryGetValue(c.Identity, out int impactCount);
                yield return Row(ExportTableKind.ImpactRequirementChanges, "analysisId", id, "requirementIdentity", c.Identity, "displayIdentifier", c.DisplayIdentifier, "module", c.Module, "sourceVersionIdentity", c.Before?.RequirementVersionIdentity, "targetVersionIdentity", c.After?.RequirementVersionIdentity,
                    "primaryType", c.PrimaryType, "flags", c.Flags, "severity", c.Severity, "criticality", c.Criticality, "reviewPriority", c.ReviewPriority, "uniqueImpactCount", impactCount, "sourceParentAbsolute", c.Before?.ParentAbsoluteNumber, "targetParentAbsolute", c.After?.ParentAbsoluteNumber,
                    "sourcePosition", c.Before?.Position, "targetPosition", c.After?.Position, "conclusive", c.IsConclusive);
                foreach (var a in c.Attributes)
                { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.ImpactAttributeChanges, "analysisId", id, "requirementIdentity", c.Identity, "attribute", a.Name, "sourceType", a.Before?.TypeName, "targetType", a.After?.TypeName, "oldValue", a.Before?.PlainText, "newValue", a.After?.PlainText,
                    "oldRichText", a.Before?.RichText, "newRichText", a.After?.RichText, "oldPlainStatus", a.Before?.PlainStatus, "newPlainStatus", a.After?.PlainStatus, "oldRichStatus", a.Before?.RichStatus, "newRichStatus", a.After?.RichStatus,
                    "semanticChanged", a.SemanticChanged, "richRepresentationChanged", a.RichRepresentationChanged, "normalizationOnly", a.NormalizationOnly, "typeChanged", a.TypeChanged, "comparable", a.IsComparable, "reason", a.Reason); }
            }
            foreach (var i in r.Impacted)
            { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.ImpactCandidates, "analysisId", id, "rootChangeIdentity", i.RootChangeIdentity, "requirementIdentity", i.RequirementIdentity, "requirementVersionIdentity", i.RequirementVersionIdentity, "referenceIdentity", i.ReferenceIdentity, "identityVerified", i.IdentityVerified, "kind", i.Kind, "minimumDepth", i.MinimumDepth, "external", i.IsExternal, "testRecommendation", i.TestRecommendation, "pathCount", i.PathIdentities.Count); }
            foreach (var p in r.Paths) foreach (var s in p.Steps)
            { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.ImpactPaths, "analysisId", id, "pathIdentity", p.Identity, "rootRequirement", p.RootChangedRequirement, "rootVersionIdentity", p.RootVersionIdentity, "impactedRequirement", p.TargetRequirement, "impactedVersionIdentity", p.TargetVersionIdentity, "pathDepth", p.Depth,
                "stepDepth", s.Depth, "sourceNode", s.SourceNode, "targetNode", s.TargetNode, "linkIdentity", s.LinkIdentity, "linkModuleIdentity", s.LinkModuleIdentity, "linkType", s.LinkType, "direction", s.Direction, "baselineContext", s.BaselineContext, "confirmed", s.Confirmed); }
            foreach (var l in r.LinkChanges)
            { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.ImpactLinkChanges, "analysisId", id, "kind", l.Observation.Kind, "beforeIdentity", l.Observation.BeforeIdentity, "afterIdentity", l.Observation.AfterIdentity, "confirmed", l.Observation.Confirmed, "suspectedLinkLoss", l.Observation.SuspectedLinkLoss,
                "evidence", l.Observation.Evidence, "beforeEndpointEvidence", l.Before == null ? null : Encoding.UTF8.GetString(ImpactSerialization.Bytes(l.Before)), "afterEndpointEvidence", l.After == null ? null : Encoding.UTF8.GetString(ImpactSerialization.Bytes(l.After))); }
            foreach (var f in r.Findings)
            { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.ImpactFindings, "analysisId", id, "findingIdentity", f.Identity, "code", f.Code, "rootChangeIdentity", f.RootChangeIdentity, "impactedRequirement", f.ImpactedRequirementIdentity, "impactedVersionIdentity", f.ImpactedVersionIdentity, "pathIdentity", f.PathIdentity,
                "depth", f.Depth, "kind", f.Kind, "severity", f.Severity, "changeFlags", f.ChangeFlags, "evidenceQuality", f.EvidenceQuality, "evidenceFingerprint", f.EvidenceFingerprint, "reviewStatus", f.ReviewStatus, "reviewerComments", f.ReviewerComments, "reason", f.Reason); }
            foreach (var review in r.Reviews)
            { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.ImpactReviews, "analysisId", id, "findingIdentity", review.FindingIdentity, "snapshotIdentity", review.AnalysisSnapshotIdentity, "evidenceFingerprint", review.EvidenceFingerprint, "reviewer", review.Reviewer, "reviewDate", review.ReviewedAtUtc, "decision", review.Decision, "rationale", review.Rationale); }
            foreach (string limitation in r.Limitations.Distinct()) { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.ImpactLimitations, "analysisId", id, "reason", limitation); }
        }
        private static ExportRecord Row(ExportTableKind kind, params object[] values)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i += 2) { var value = values[i + 1]; fields[(string)values[i]] = value is Enum ? value.ToString() : value; }
            return new ExportRecord(kind, fields);
        }
    }
}
