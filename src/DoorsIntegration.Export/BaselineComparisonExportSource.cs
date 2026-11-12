using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    // Result lease must stay alive until ExportAsync completes. Existing JSON/XLSX writers retain raw
    // values through ValueChunks, text-typed cells, Unicode checks, quotas and atomic publication.
    public sealed class BaselineComparisonExportSource : IExportSource
    {
        private readonly IBaselineComparisonResult result;
        public BaselineComparisonExportSource(IBaselineComparisonResult result) { this.result = result ?? throw new ArgumentNullException(nameof(result)); }
        public long? EstimatedRecordCount => null;
        public IEnumerable<ExportRecord> Read(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = result.ComparisonId.ToString("N"); var request = result.Request; var summary = result.Summary;
            yield return Row(ExportTableKind.Scopes, "scopeId", id, "sourceKind", "BaselineComparison", "modulePath", request.ModulePath,
                "isComplete", summary.IsConclusive, "visibility", "SelectedAttributesAndReadableObjects", "collectedAtUtc", result.ComparedAtUtc,
                "details", "Read-only; opaque rich comparison; no downstream impact or standards compliance guarantee.");
            yield return Row(ExportTableKind.BaselineComparisons, "comparisonId", id, "qualifiedModuleId", result.QualifiedModuleId,
                "modulePath", request.ModulePath, "beforeMajor", request.Before.Major, "beforeMinor", request.Before.Minor, "beforeSuffix", request.Before.Suffix,
                "afterMajor", request.After.Major, "afterMinor", request.After.Minor, "afterSuffix", request.After.Suffix, "normalization", request.Normalization.ToString(),
                "isConclusive", summary.IsConclusive, "added", summary.Counts[BaselineChangeStatus.Added], "deleted", summary.Counts[BaselineChangeStatus.Deleted],
                "modified", summary.Counts[BaselineChangeStatus.Modified], "unchanged", summary.Counts[BaselineChangeStatus.Unchanged],
                "indeterminate", summary.Counts[BaselineChangeStatus.Indeterminate], "normalizationDifferences", summary.NormalizationDifferenceCount, "comparedAtUtc", result.ComparedAtUtc);
            foreach (var change in result.ReadChanges(cancellationToken))
            {
                var left = change.Before; var right = change.After;
                yield return Row(ExportTableKind.BaselineObjects, "comparisonId", id, "identity", change.Identity.Key, "qualifiedModuleId", change.Identity.QualifiedModuleId,
                    "absoluteNumber", change.Identity.AbsoluteNumber, "status", change.Status.ToString(), "isConclusive", change.IsConclusive, "hierarchyChanged", change.HierarchyChanged,
                    "beforeIdentifier", left?.ObjectIdentifier, "afterIdentifier", right?.ObjectIdentifier, "beforeObjectNumber", left?.ObjectNumber, "afterObjectNumber", right?.ObjectNumber,
                    "beforeOrder", left?.SourceOrder, "afterOrder", right?.SourceOrder, "beforeParentId", left?.ParentAbsoluteNumber, "afterParentId", right?.ParentAbsoluteNumber,
                    "beforeLevel", left?.HierarchyLevel, "afterLevel", right?.HierarchyLevel, "beforeParentStatus", left?.ParentStatus.ToString(), "afterParentStatus", right?.ParentStatus.ToString(),
                    "beforeDeleted", left?.IsDeleted, "afterDeleted", right?.IsDeleted);
                foreach (var field in change.Attributes)
                {
                    yield return Row(ExportTableKind.BaselineAttributes, "comparisonId", id, "identity", change.Identity.Key, "name", field.Name, "status", field.Status.ToString(),
                        "isConclusive", field.IsConclusive, "plainChanged", field.PlainTextChanged, "richChanged", field.RichTextChanged, "typeChanged", field.TypeChanged,
                        "normalizationDifference", field.HasNormalizationDifference, "beforeType", field.Before?.TypeName, "afterType", field.After?.TypeName,
                        "beforePlainStatus", field.Before?.PlainStatus.ToString(), "afterPlainStatus", field.After?.PlainStatus.ToString(),
                        "beforePlainText", field.Before?.PlainText, "afterPlainText", field.After?.PlainText,
                        "beforeRichStatus", field.Before?.RichTextStatus.ToString(), "afterRichStatus", field.After?.RichTextStatus.ToString(),
                        "beforeRichTextWithOle", field.Before?.RichTextWithOle, "afterRichTextWithOle", field.After?.RichTextWithOle, "richComparison", "ExactOpaqueDoorsRtfWithOle");
                }
            }
            foreach (var changeEvent in result.ReadEvents(cancellationToken))
                yield return Row(ExportTableKind.BaselineEvents, "eventSchemaVersion", changeEvent.SchemaVersion, "eventId", changeEvent.EventId, "comparisonId", id,
                    "identity", changeEvent.Change.Identity.Key, "status", changeEvent.Change.Status.ToString(), "requiresReview", changeEvent.RequiresReview,
                    "isImpactCandidate", changeEvent.IsImpactCandidate, "beforeVersion", BaselineComparisonRequest.Version(request.Before),
                    "afterVersion", BaselineComparisonRequest.Version(request.After), "changedAttributes", string.Join("\n", changeEvent.Change.Attributes
                        .Where(value => value.Status != BaselineAttributeStatus.Unchanged && value.Status != BaselineAttributeStatus.NormalizationOnly).Select(value => value.Name)),
                    "hierarchyChanged", changeEvent.Change.HierarchyChanged);
        }
        private static ExportRecord Row(ExportTableKind table, params object[] values)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i += 2) fields.Add((string)values[i], values[i + 1]);
            return new ExportRecord(table, fields);
        }
    }
}
