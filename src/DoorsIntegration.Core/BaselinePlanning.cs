using System;
using System.Collections.Generic;
using System.Linq;

namespace DoorsIntegration.Core
{
    // Pure planning: no adapter, repository, locks, COM or side effects.
    public sealed class BaselinePlanner
    {
        public BaselinePlan Build(Guid jobId, BulkBaselineRequest request, IReadOnlyList<BaselineModuleMetadata> metadata, DateTime utc)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("A job identity is required.", nameof(jobId));
            if (request == null) throw new ArgumentNullException(nameof(request));
            BulkBaselineGuard.Utc(utc);
            if (metadata == null || metadata.Count != request.Modules.Count) throw new ArgumentException("Metadata must match the selection in order.", nameof(metadata));
            var items = new List<BaselinePlanItem>();
            for (int index = 0; index < request.Modules.Count; index++)
            {
                var selection = request.Modules[index]; var observed = metadata[index];
                string suffix = selection.SuffixOverride ?? request.Suffix, annotation = selection.AnnotationOverride ?? request.Annotation;
                BaselineVersion expected = null; var status = BaselinePlanItemStatus.Planned; string reason = null;
                if (observed == null || !selection.Module.Matches(observed.Module))
                {
                    observed = BaselineModuleMetadata.Unavailable(selection.Module, "Metadata identity/path does not match the selected stable module identity.", utc);
                    status = BaselinePlanItemStatus.Blocked; reason = observed.FailureReason;
                }
                else if (observed.Status != BaselineMetadataStatus.Available)
                { status = observed.Status == BaselineMetadataStatus.Unavailable ? BaselinePlanItemStatus.Unavailable : BaselinePlanItemStatus.Blocked; reason = observed.FailureReason ?? "Module metadata is not available: " + observed.Status + "."; }
                else if (observed.FormalModule != BaselineCheckState.Passed)
                { status = observed.FormalModule == BaselineCheckState.Unknown ? BaselinePlanItemStatus.Unavailable : BaselinePlanItemStatus.Blocked; reason = "Formal module type is not confirmed."; }
                else if ((request.Type == BaselineType.Major && observed.CurrentVersion.Major == 1000000) ||
                    (request.Type == BaselineType.Minor && observed.CurrentVersion.Minor == 1000000))
                { status = BaselinePlanItemStatus.Blocked; reason = "The proposed version exceeds the supported version bound."; }
                else expected = request.Type == BaselineType.Major ? new BaselineVersion(observed.CurrentVersion.Major + 1, 0, suffix) :
                    new BaselineVersion(observed.CurrentVersion.Major, observed.CurrentVersion.Minor + 1, suffix);
                items.Add(new BaselinePlanItem(jobId, selection, observed, expected, suffix, annotation, status, reason));
            }
            return new BaselinePlan(jobId, request, items, utc);
        }
    }

    // A fresh, read-only observation is required. Passing checks neither reserves a version nor retains a lock.
    public sealed class BaselinePreflightChecker
    {
        public BaselinePreflightResult Check(BaselinePlan plan, IReadOnlyList<BaselineModuleMetadata> metadata, DateTime utc)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            BulkBaselineGuard.Utc(utc);
            if (metadata == null || metadata.Count != plan.Items.Count) throw new ArgumentException("Metadata must match plan items in order.", nameof(metadata));
            var results = new List<BaselinePreflightItemResult>();
            for (int index = 0; index < plan.Items.Count; index++)
            {
                var item = plan.Items[index]; var observed = metadata[index];
                var status = BaselinePreflightStatus.Passed; string reason = null;
                if (item.Status != BaselinePlanItemStatus.Planned)
                { status = item.Status == BaselinePlanItemStatus.Unavailable ? BaselinePreflightStatus.Unavailable : BaselinePreflightStatus.Blocked; reason = item.FailureReason; }
                else if (observed == null || !item.Module.Matches(observed.Module))
                { status = BaselinePreflightStatus.Blocked; reason = "Stable module identity or canonical path changed; create a new plan."; }
                else if (observed.Status != BaselineMetadataStatus.Available)
                { status = observed.Status == BaselineMetadataStatus.Unavailable ? BaselinePreflightStatus.Unavailable : BaselinePreflightStatus.Blocked; reason = observed.FailureReason ?? "Module metadata is not available: " + observed.Status + "."; }
                else if (observed.ObservedAtUtc < plan.PlannedAtUtc || observed.ObservedAtUtc > utc || utc - observed.ObservedAtUtc > TimeSpan.FromMinutes(5))
                { status = BaselinePreflightStatus.Unavailable; reason = "Fresh UTC metadata is required (after plan creation, at most five minutes old)."; }
                else if (!item.CurrentVersion.Equals(observed.CurrentVersion))
                { status = BaselinePreflightStatus.Blocked; reason = "Current version changed since planning; create a new plan."; }
                else if (observed.FormalModule == BaselineCheckState.Failed || observed.CreatePermission == BaselineCheckState.Failed || observed.LockAvailable == BaselineCheckState.Failed)
                { status = BaselinePreflightStatus.Blocked; reason = "Formal module, baseline creation permission or module lock check failed."; }
                else if (observed.ExistingVersions.Contains(item.ExpectedVersion))
                { status = BaselinePreflightStatus.Blocked; reason = "The proposed major/minor/suffix version already exists."; }
                else if (observed.FormalModule != BaselineCheckState.Passed || observed.CreatePermission != BaselineCheckState.Passed ||
                    observed.LockAvailable != BaselineCheckState.Passed || !observed.VersionListComplete)
                { status = BaselinePreflightStatus.Unavailable; reason = "Module type, creation permission, lock availability or complete version list is unknown."; }
                results.Add(new BaselinePreflightItemResult(item, observed, status, reason));
            }
            return new BaselinePreflightResult(plan, results, utc);
        }
    }
}
