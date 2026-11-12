using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    // Inputs are enumerated once. The caller retains snapshot ownership, including lazy using/yield lifetimes.
    public sealed class SnapshotExportSource : IExportSource
    {
        private readonly IEnumerable<DiscoverySnapshot> discovery;
        private readonly IEnumerable<IRequirementSnapshot> requirements;
        private readonly IEnumerable<ILinkSnapshot> links;
        public SnapshotExportSource(IEnumerable<DiscoverySnapshot> discovery = null, IEnumerable<IRequirementSnapshot> requirements = null, IEnumerable<ILinkSnapshot> links = null)
        { this.discovery = discovery; this.requirements = requirements; this.links = links; }
        public long? EstimatedRecordCount => null;
        public IEnumerable<ExportRecord> Read(CancellationToken cancellationToken)
        {
            foreach (var snapshot in discovery ?? Enumerable.Empty<DiscoverySnapshot>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = snapshot.CorrelationId.ToString("N");
                yield return Row(ExportTableKind.Scopes, "scopeId", id, "sourceKind", "Discovery", "modulePath", snapshot.Scope.RootPath, "targetProfile", DoorsVersions.Format(snapshot.Scope.Target),
                    "visibility", snapshot.Visibility, "isComplete", snapshot.IsCompleteForVisibleScope, "isTruncated", snapshot.IsTruncated, "itemCount", snapshot.Items.Count,
                    "visitedCount", snapshot.VisitedFolders, "collectedAtUtc", snapshot.CollectedAtUtc,
                    "details", string.Join("\n", snapshot.Issues.Select(issue => issue.Code + ": " + issue.FullPath)));
                foreach (var item in snapshot.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool module = item.Type == DoorsItemKind.FormalModule || item.Type == DoorsItemKind.LinkModule || item.Type == DoorsItemKind.DescriptiveModule || snapshot.Kind == DiscoveryKind.ModuleByPath;
                    yield return Row(module ? ExportTableKind.Modules : ExportTableKind.Projects, "scopeId", id, "fullPath", item.FullPath, "name", item.Name, "type", item.Type.ToString(),
                        "accessStatus", item.AccessStatus.ToString(), "metadataVerified", item.MetadataVerified, "collectedAtUtc", snapshot.CollectedAtUtc);
                }
            }
            foreach (var snapshot in requirements ?? Enumerable.Empty<IRequirementSnapshot>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var summary = snapshot.Summary;
                var request = summary.Request;
                string id = summary.CorrelationId.ToString("N"), kind = request.Baseline == null ? "Current" : "Baseline", version = Version(request.Baseline);
                yield return Row(ExportTableKind.Scopes, "scopeId", id, "sourceKind", "Requirements", "modulePath", request.ModulePath, "versionKind", kind, "version", version,
                    "targetProfile", DoorsVersions.Format(request.Target), "visibility", summary.Visibility, "isComplete", summary.IsCompleteForReadableObjects, "isTruncated", summary.IsTruncated,
                    "enumerationFailed", summary.EnumerationFailed, "readStatus", summary.ModuleStatus.ToString(), "ownership", summary.Ownership.ToString(), "cleanupStatus", summary.CleanupStatus.ToString(),
                    "itemCount", summary.ObjectCount, "visitedCount", summary.VisitedObjectCount, "deniedCount", summary.DeniedObjectCount, "deletedExcludedCount", summary.DeletedExcludedCount,
                    "unavailableCount", summary.UnavailableObjectCount, "fieldProblemCount", summary.FieldProblemCount);
                foreach (var item in snapshot.ReadObjects())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return Row(ExportTableKind.Requirements, "scopeId", id, "modulePath", request.ModulePath, "versionKind", kind, "version", version, "absoluteNumber", item.AbsoluteNumber,
                        "objectIdentifier", item.ObjectIdentifier, "objectNumber", item.ObjectNumber, "numberStatus", item.NumberStatus.ToString(), "sourceOrder", item.SourceOrder,
                        "hierarchyLevel", item.HierarchyLevel, "parentAbsoluteNumber", item.ParentAbsoluteNumber, "parentStatus", item.ParentStatus.ToString(), "isDeleted", item.IsDeleted,
                        "heading", item.Heading.PlainText, "text", item.Text.PlainText);
                    foreach (var value in item.Values.Values)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return Row(ExportTableKind.Attributes, "scopeId", id, "modulePath", request.ModulePath, "versionKind", kind, "version", version, "ownerKind", "Object", "absoluteNumber", item.AbsoluteNumber,
                            "name", value.Name, "typeName", value.TypeName, "plainStatus", value.PlainStatus.ToString(), "plainText", value.PlainText,
                            "richTextStatus", value.RichTextStatus.ToString(), "richTextWithOle", value.RichTextWithOle, "richTextFormat", value.RichTextFormat);
                    }
                }
            }
            foreach (var snapshot in links ?? Enumerable.Empty<ILinkSnapshot>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var summary = snapshot.Summary; var request = summary.Request;
                string id = summary.CorrelationId.ToString("N");
                yield return Row(ExportTableKind.Scopes, "scopeId", id, "sourceKind", "Links", "modulePath", request.ModulePath, "linkModulePath", request.LinkModulePath,
                    "versionKind", request.Baseline == null ? "Current" : "Baseline", "version", Version(request.Baseline), "targetProfile", DoorsVersions.Format(request.Target), "visibility", summary.Visibility,
                    "isComplete", summary.IsCompleteForReadableScope, "isTruncated", summary.IsTruncated, "enumerationFailed", summary.EnumerationFailed, "readStatus", summary.ModuleStatus.ToString(),
                    "linkModuleStatus", summary.LinkModuleStatus.ToString(), "ownership", summary.Ownership.ToString(), "itemCount", summary.RecordCount, "visitedCount", summary.VisitedObjectCount,
                    "deniedCount", summary.DeniedObjectCount, "deletedExcludedCount", summary.DeletedExcludedCount, "unresolvedCount", summary.UnresolvedLinkCount, "sourceReferenceCount", summary.SourceReferenceCount,
                    "surrogateProblemCount", summary.SurrogateProblemCount, "openedModuleCount", summary.OpenedModuleCount, "cleanupFailureCount", summary.CleanupFailureCount);
                foreach (var item in snapshot.ReadLinks())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fields = new Dictionary<string, object> { ["scopeId"] = id, ["sequence"] = item.Sequence, ["direction"] = item.Direction.ToString(), ["recordKind"] = item.Kind.ToString(),
                        ["resolution"] = item.Resolution.ToString(), ["isEchoed"] = item.IsEchoed, ["linkModulePath"] = item.LinkModulePath, ["linkModuleResolution"] = item.LinkModuleResolution.ToString() };
                    AddEndpoint(fields, "source", item.Source); AddEndpoint(fields, "target", item.Target);
                    yield return new ExportRecord(ExportTableKind.Links, fields);
                }
            }
        }
        private static string Version(RequirementBaseline baseline) => baseline == null ? "" : baseline.Major.ToString(CultureInfo.InvariantCulture) + "." + baseline.Minor.ToString(CultureInfo.InvariantCulture) + (baseline.Suffix == "" ? "" : "(" + baseline.Suffix + ")");
        private static void AddEndpoint(IDictionary<string, object> fields, string prefix, LinkEndpoint endpoint)
        {
            fields[prefix + "ModulePath"] = endpoint.ModulePath; fields[prefix + "VersionKind"] = endpoint.VersionKind.ToString(); fields[prefix + "Version"] = endpoint.Version;
            fields[prefix + "AbsoluteNumber"] = endpoint.AbsoluteNumber; fields[prefix + "ObjectIdentifier"] = endpoint.ObjectIdentifier; fields[prefix + "Resolution"] = endpoint.Resolution.ToString();
            fields[prefix + "SurrogateStatus"] = endpoint.SurrogateStatus.ToString(); fields[prefix + "ExternalReference"] = endpoint.ExternalReference; fields[prefix + "ReferenceStatus"] = endpoint.ReferenceStatus.ToString();
        }
        private static ExportRecord Row(ExportTableKind table, params object[] pairs)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) fields.Add((string)pairs[i], pairs[i + 1]);
            return new ExportRecord(table, fields);
        }
    }
}
