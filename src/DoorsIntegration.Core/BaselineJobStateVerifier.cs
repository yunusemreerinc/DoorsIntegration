using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    // Reuses read-only metadata/state ports and their shared native execution queue.
    public sealed class BaselineJobStateVerifier : IBaselineJobStateVerifier
    {
        private readonly IBaselineMetadataService metadata;
        private readonly IBaselinePreflightStateReader states;
        private readonly IBaselinePreflightClock clock;
        public BaselineJobStateVerifier(IBaselineMetadataService metadata, IBaselinePreflightStateReader states, IBaselinePreflightClock clock)
        { this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata)); this.states = states ?? throw new ArgumentNullException(nameof(states)); this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); }
        public async Task<BaselineJobVerification> VerifyAsync(BulkBaselineJobRecord job, BulkBaselineJobModule module, CancellationToken cancellationToken)
        {
            if (job == null || module == null || module.ExpectedVersion == null || !job.Modules.Contains(module)) throw new ArgumentException("An exact journal module/target is required.");
            cancellationToken.ThrowIfCancellationRequested();
            BaselineJobVerification Result(BaselineJobVerificationStatus status, string evidence) => new BaselineJobVerification(module.Module, module.ExpectedVersion,
                module.Annotation, module.DatabaseIdentity, module.UserId, job.Source, status, clock.UtcNow, evidence);
            try
            {
                var request = new BaselineHistoryRequest(module.Module, true, suffix: module.Suffix, collisionTarget: module.ExpectedVersion,
                    timeout: job.Timeout, target: job.Target, connectionMode: job.ConnectionMode);
                var result = await metadata.ReadAsync(request, cancellationToken).ConfigureAwait(false);
                var s = result?.Snapshot;
                if (s == null || !ReferenceEquals(s.Request, request) || s.Status != BaselineHistoryStatus.Available || !s.HistoryComplete || s.Source != job.Source ||
                    !s.Module.Matches(module.Module) || !Fresh(s.ObservedAtUtc, job) || result.Collision == null || !module.ExpectedVersion.Equals(result.Collision.Target))
                    return Result(BaselineJobVerificationStatus.Unknown, "Metadata completeness/context/freshness was not verified.");
                var stateRequest = job.Request(module); var probe = new BaselinePreflightProbe(stateRequest, stateRequest.Baseline.Modules.Single(), module.ExpectedVersion);
                var observation = await states.ReadAsync(probe, cancellationToken).ConfigureAwait(false); var f = observation?.Facts;
                if (observation == null || !ReferenceEquals(observation.Probe, probe) || !Fresh(s.ObservedAtUtc, job) || !Fresh(observation.ObservedAtUtc, job) ||
                    observation.ObservedAtUtc < s.ObservedAtUtc || f == null || !f.Module.Matches(module.Module) ||
                    f.Source != job.Source || f.DatabaseIdentity != module.DatabaseIdentity || f.CurrentUserId != module.UserId || f.SessionValid != BaselineCheckState.Passed ||
                    f.Exists != BaselineCheckState.Passed || f.Type != DoorsItemKind.FormalModule || f.ReadAccess != BaselineCheckState.Passed)
                    return Result(BaselineJobVerificationStatus.Unknown, "Current module, user, database, session or read evidence is unavailable.");
                var matching = s.History.Where(b => b.Identity.Module.Matches(module.Module) && b.Identity.Version.Equals(module.ExpectedVersion) && b.Existence == BaselineExistenceStatus.Exists).ToArray();
                if (result.Collision.Status == BaselineExistenceStatus.Exists && f.TargetAvailability == BaselineTargetAvailability.Exists)
                {
                    bool exact = matching.Length == 1 && matching[0].Annotation == module.Annotation && matching[0].Creator == module.UserId;
                    return Result(exact ? BaselineJobVerificationStatus.ExistsMatching : BaselineJobVerificationStatus.ExistsConflicting,
                        exact ? "Exact baseline identity, annotation and creator observed; this is desired-state evidence, not causal ownership proof." : "Target exists but recorded identity/annotation/creator evidence conflicts.");
                }
                bool quiescent = f.LockState == BaselineLockState.Unlocked && (f.EditState == BaselineEditState.Closed || f.EditState == BaselineEditState.ReadOnly) &&
                    f.UnsavedChanges == BaselineUnsavedState.None && f.RevisionsReliable == BaselineCheckState.Passed && !string.IsNullOrWhiteSpace(f.ContentRevision) &&
                    !string.IsNullOrWhiteSpace(f.PermissionRevision) && !string.IsNullOrWhiteSpace(f.LockRevision) && !string.IsNullOrWhiteSpace(f.SessionId) && !string.IsNullOrWhiteSpace(f.ConnectionEpoch);
                if (matching.Length == 0 && result.Collision.Status == BaselineExistenceStatus.AbsentOrUnreadable && f.TargetAvailability == BaselineTargetAvailability.Free && quiescent)
                    return Result(BaselineJobVerificationStatus.AbsentAuthoritatively, "Read-only exact-target visibility/absence and clean unlocked session evidence observed; fresh preflight and operator approval remain required.");
                return Result(BaselineJobVerificationStatus.Unknown, "Negative baselineExists alone, conflicting observations or an active/unknown session cannot authorize retry.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { cancellationToken.ThrowIfCancellationRequested(); return Result(BaselineJobVerificationStatus.Unknown, "Metadata/state verification failed; no retry was authorized."); }
        }
        private bool Fresh(DateTime utc, BulkBaselineJobRecord job) => utc.Kind == DateTimeKind.Utc && clock.UtcNow >= utc && clock.UtcNow - utc <= job.MaxObservationAge;
    }
}
