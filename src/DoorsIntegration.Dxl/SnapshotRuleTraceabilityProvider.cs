using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Dxl
{
    // Indexes a retained snapshot once; no COM call or requirement/link mutation.
    public sealed class SnapshotRuleTraceabilityProvider : IRuleTraceabilityProvider
    {
        private readonly LinkReadSummary summary;
        private readonly string repository, baselineLabel;
        private readonly Dictionary<string, List<RuleTraceLink>> rows = new Dictionary<string, List<RuleTraceLink>>(StringComparer.Ordinal);
        public SnapshotRuleTraceabilityProvider(ILinkSnapshot snapshot, string repositoryId, string observedBaselineLabel = null, int maxRecords = 100000, CancellationToken token = default(CancellationToken))
        {
            if (snapshot == null || maxRecords < 1 || maxRecords > 1000000) throw new ArgumentException();
            summary = snapshot.Summary; repository = repositoryId; baselineLabel = observedBaselineLabel;
            // Validate caller-bound namespace/version using the shared identity model.
            Identity(1); int count = 0;
            foreach (var row in snapshot.ReadLinks())
            {
                token.ThrowIfCancellationRequested(); if (++count > maxRecords) throw new ProcessingException("RuleTraceLimit", "Trace evidence index exceeded its record budget.");
                var anchor = row.Direction == LinkDirection.Outgoing ? row.Source : row.Target;
                var other = row.Direction == LinkDirection.Outgoing ? row.Target : row.Source;
                if (anchor?.AbsoluteNumber == null || anchor.ModulePath != summary.Request.ModulePath || row.LinkModulePath != summary.Request.LinkModulePath ||
                    anchor.VersionKind != (summary.Request.Baseline == null ? LinkVersionKind.Current : LinkVersionKind.Baseline) || (row.Direction != LinkDirection.Incoming && row.Direction != LinkDirection.Outgoing))
                    throw new ProcessingException("RuleTraceContextMismatch", "Trace row does not match its anchor scope.");
                ArtifactIdentity target = null; bool known = other != null && other.VersionKind == LinkVersionKind.Current;
                if (other?.AbsoluteNumber != null && !string.IsNullOrEmpty(other.ModulePath))
                {
                    var kind = other.VersionKind == LinkVersionKind.Current ? ArtifactVersion.Current : other.VersionKind == LinkVersionKind.Baseline ? ArtifactVersion.Baseline : ArtifactVersion.Unknown;
                    string version = kind == ArtifactVersion.Baseline ? other.Version : "";
                    if (kind == ArtifactVersion.Baseline && other.ModulePath == anchor.ModulePath && other.Version == anchor.Version && !string.IsNullOrEmpty(baselineLabel)) { version = baselineLabel; known = true; }
                    target = new ArtifactIdentity(repository, other.ModulePath, other.AbsoluteNumber.Value.ToString(CultureInfo.InvariantCulture), kind, version);
                }
                var resolution = row.Kind == LinkRecordKind.IncomingSourceReference || row.IsEchoed || row.LinkModuleResolution != LinkModuleResolution.Verified ? RuleTraceResolution.ReferenceOnly :
                    row.Source.Resolution == LinkEndpointResolution.DeletedObject || row.Target.Resolution == LinkEndpointResolution.DeletedObject ? RuleTraceResolution.DeletedEndpoint :
                    other?.Resolution == LinkEndpointResolution.AccessDenied || other?.Resolution == LinkEndpointResolution.DeniedOrNotFound ? RuleTraceResolution.Inaccessible :
                    row.Resolution == LinkResolution.Resolved && row.Source.Resolution == LinkEndpointResolution.Resolved && row.Target.Resolution == LinkEndpointResolution.Resolved && target != null ? RuleTraceResolution.Confirmed : RuleTraceResolution.Unresolved;
                string key = anchor.AbsoluteNumber.Value.ToString(CultureInfo.InvariantCulture) + ":" + row.Direction;
                if (!rows.TryGetValue(key, out var links)) { links = new List<RuleTraceLink>(); rows.Add(key, links); }
                links.Add(new RuleTraceLink(target, resolution, known));
            }
            if (count != summary.RecordCount) throw new ProcessingException("RuleTraceContextMismatch", "Trace record count disagrees with snapshot metadata.");
        }
        private ArtifactIdentity Identity(int absolute) => new ArtifactIdentity(repository, summary.Request.ModulePath, absolute.ToString(CultureInfo.InvariantCulture),
            summary.Request.Baseline == null ? ArtifactVersion.Current : string.IsNullOrEmpty(baselineLabel) ? ArtifactVersion.Unknown : ArtifactVersion.Baseline, summary.Request.Baseline == null ? "" : baselineLabel ?? "");
        public IEnumerable<RuleTraceabilityEvidence> Read(ArtifactIdentity artifact, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(artifact.ObjectId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || Identity(number).Key != artifact.Key) yield break;
            foreach (var direction in new[] { CoverageDirection.Incoming, CoverageDirection.Outgoing })
            {
                if (summary.Request.Direction != LinkDirection.Both && summary.Request.Direction.ToString() != direction.ToString()) continue;
                rows.TryGetValue(artifact.ObjectId + ":" + direction, out var links);
                yield return new RuleTraceabilityEvidence(artifact, summary.Request.LinkModulePath, direction, summary.IsCompleteForReadableScope,
                    "CallerBoundRepositoryAndBaseline;LinkRequest=" + summary.CorrelationId.ToString("N"), links ?? Enumerable.Empty<RuleTraceLink>(), summary.QualifiedLinkModuleId);
            }
        }
    }
}
