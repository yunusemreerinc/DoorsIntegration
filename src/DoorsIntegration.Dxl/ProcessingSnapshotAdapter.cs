using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DoorsIntegration.Core;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Dxl
{
    // Converts already retained snapshots. Never contacts DOORS or evaluates installed attribute DXL.
    // Repository ID is caller-bound; these legacy snapshots contain paths, not verified database/qualified-module IDs.
    public static class ProcessingSnapshotAdapter
    {
        public static IEnumerable<QualityArtifact> Requirements(IRequirementSnapshot snapshot, string repositoryId, string observedBaselineLabel = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            foreach (var row in snapshot.ReadObjects())
                yield return new QualityArtifact(Identity(snapshot, repositoryId, row.AbsoluteNumber, observedBaselineLabel),
                    row.Values.Values.Select(v => new QualityField(v.Name, Availability(v.PlainStatus), v.PlainText,
                        v.RichTextStatus == RequirementValueStatus.Readable ? v.RichTextWithOle : null)), row.IsDeleted);
        }
        public static IEnumerable<CoverageNode> Nodes(IRequirementSnapshot snapshot, string repositoryId, string observedBaselineLabel = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            foreach (var row in snapshot.ReadObjects()) yield return new CoverageNode(Identity(snapshot, repositoryId, row.AbsoluteNumber, observedBaselineLabel), row.IsDeleted);
        }
        public static IEnumerable<TraceRelation> Relations(ILinkSnapshot snapshot, string repositoryId)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            foreach (var row in snapshot.ReadLinks())
            {
                var source = Endpoint(row.Source, repositoryId); var target = Endpoint(row.Target, repositoryId);
                bool resolved = row.Kind == LinkRecordKind.Link && row.Resolution == LinkResolution.Resolved && row.LinkModuleResolution == LinkModuleResolution.Verified &&
                    source != null && target != null && source.VersionKind != ArtifactVersion.Unknown && target.VersionKind != ArtifactVersion.Unknown &&
                    row.Source.Resolution == LinkEndpointResolution.Resolved && row.Target.Resolution == LinkEndpointResolution.Resolved;
                yield return new TraceRelation(source, target, row.LinkModulePath, resolved, row.IsEchoed,
                    row.Source.Resolution == LinkEndpointResolution.DeletedObject || row.Target.Resolution == LinkEndpointResolution.DeletedObject);
            }
        }
        private static ArtifactIdentity Identity(IRequirementSnapshot snapshot, string repository, int absolute, string baselineLabel)
        {
            bool baseline = snapshot.Summary.Request.Baseline != null;
            return new ArtifactIdentity(repository, snapshot.Summary.Request.ModulePath, absolute.ToString(CultureInfo.InvariantCulture),
                !baseline ? ArtifactVersion.Current : string.IsNullOrEmpty(baselineLabel) ? ArtifactVersion.Unknown : ArtifactVersion.Baseline,
                baseline ? baselineLabel ?? "" : "");
        }
        private static ArtifactIdentity Endpoint(LinkEndpoint endpoint, string repository)
        {
            if (endpoint == null || !endpoint.AbsoluteNumber.HasValue || endpoint.AbsoluteNumber.Value <= 0 || string.IsNullOrEmpty(endpoint.ModulePath)) return null;
            ArtifactVersion kind = endpoint.VersionKind == LinkVersionKind.Current ? ArtifactVersion.Current : endpoint.VersionKind == LinkVersionKind.Baseline ? ArtifactVersion.Baseline : ArtifactVersion.Unknown;
            return new ArtifactIdentity(repository, endpoint.ModulePath, endpoint.AbsoluteNumber.Value.ToString(CultureInfo.InvariantCulture), kind, kind == ArtifactVersion.Baseline ? endpoint.Version : "");
        }
        private static QualityAvailability Availability(RequirementValueStatus status)
        {
            switch (status)
            {
                case RequirementValueStatus.Readable: return QualityAvailability.Available;
                case RequirementValueStatus.AccessDenied: return QualityAvailability.Denied;
                case RequirementValueStatus.NotRequested: return QualityAvailability.NotRequested;
                case RequirementValueStatus.TooLarge: return QualityAvailability.TooLarge;
                case RequirementValueStatus.DeniedOrNotFound: return QualityAvailability.Missing;
                default: return QualityAvailability.Failed;
            }
        }
    }
}
