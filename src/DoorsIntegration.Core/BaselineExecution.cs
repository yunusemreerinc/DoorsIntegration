using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum BaselineWriteOutcome { NotRun, Declined, Blocked, Created, NeedsReconciliation }
    public sealed class BaselineExecutionReview
    {
        internal BaselineExecutionReview(BulkBaselineDryRunPlan plan) : this(plan, Guid.NewGuid()) { }
        internal BaselineExecutionReview(BulkBaselineDryRunPlan plan, Guid jobId) { JobId = jobId; Plan = plan; }
        public Guid JobId { get; }
        public BulkBaselineDryRunPlan Plan { get; }
        public string Fingerprint => Plan.Fingerprint;
        public bool AutomaticallySavesChanges => false;
        public bool ClosesCreatedModule => true;
        public bool CanUndo => false;
    }
    public interface IBaselineExecutionConfirmation
    {
        // Present exact stable IDs/paths, current/expected versions, suffix, annotation,
        // fingerprint and irreversible close-on-create behavior. True requires explicit human approval.
        Task<bool> ConfirmAsync(BaselineExecutionReview review, CancellationToken cancellationToken);
    }
    public sealed class RejectBaselineExecutionConfirmation : IBaselineExecutionConfirmation
    {
        public Task<bool> ConfirmAsync(BaselineExecutionReview review, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(false); }
    }
    // A fingerprint is not authorization. Only the service constructs this single-use capability,
    // after confirmation AND immediate fresh preflight. Public adapters cannot mint capabilities.
    public sealed class BaselineExecutionAuthorization
    {
        private int used;
        private readonly IBaselinePreflightClock clock;
        private readonly CancellationToken dispatchCancellation;
        internal BaselineExecutionAuthorization(BaselineExecutionReview review, BulkBaselineDryRunItem item, IBaselinePreflightClock clock)
            : this(review, item, clock, null) { }
        internal BaselineExecutionAuthorization(BaselineExecutionReview review, BulkBaselineDryRunItem item, IBaselinePreflightClock clock, BaselineJobVerification retryVerification)
            : this(review, item, clock, retryVerification, CancellationToken.None) { }
        internal BaselineExecutionAuthorization(BaselineExecutionReview review, BulkBaselineDryRunItem item, IBaselinePreflightClock clock, BaselineJobVerification retryVerification, CancellationToken dispatchCancellation)
        {
            Review = review; Item = item; this.clock = clock; RetryVerification = retryVerification; this.dispatchCancellation = dispatchCancellation;
            var observationExpiry = item.Precondition.ObservedAtUtc.Add(item.Probe.Request.MaxObservationAge);
            var planExpiry = review.Plan.CreatedAtUtc.Add(review.Plan.Request.MaxPlanAge);
            ExpiresAtUtc = observationExpiry < planExpiry ? observationExpiry : planExpiry;
        }
        public BaselineExecutionReview Review { get; }
        public BulkBaselineDryRunItem Item { get; }
        // Only the durable runner supplies this after separate retry approval and renewed exact-target verification.
        public BaselineJobVerification RetryVerification { get; }
        public DateTime ExpiresAtUtc { get; }
        public bool IsFreshForDispatch => !dispatchCancellation.IsCancellationRequested && clock.UtcNow >= Item.Precondition.ObservedAtUtc && clock.UtcNow >= Review.Plan.CreatedAtUtc && clock.UtcNow < ExpiresAtUtc &&
            (RetryVerification == null || HasFreshAuthoritativeRetryVerification);
        public bool HasFreshAuthoritativeRetryVerification => RetryVerification != null && RetryVerification.Status == BaselineJobVerificationStatus.AbsentAuthoritatively &&
            RetryVerification.Module.Matches(Item.Module) && RetryVerification.ExpectedVersion.Equals(Item.ExpectedVersion) && RetryVerification.Annotation == Item.Annotation &&
            RetryVerification.DatabaseIdentity == Item.Precondition.Facts.DatabaseIdentity && RetryVerification.UserId == Item.Precondition.Facts.CurrentUserId &&
            RetryVerification.Source == Item.Precondition.Facts.Source && clock.UtcNow >= RetryVerification.ObservedAtUtc && clock.UtcNow - RetryVerification.ObservedAtUtc <= Item.Probe.Request.MaxObservationAge;
        public bool TryBeginWrite() => IsFreshForDispatch && Interlocked.CompareExchange(ref used, 1, 0) == 0;
    }
    public sealed class BaselineModuleExecutionResult
    {
        public BaselineModuleExecutionResult(Guid jobId, BulkBaselineDryRunItem item, BaselineWriteOutcome outcome,
            BaselineMetadataSource source, bool writeAttempted, string reason, DateTime startedAtUtc, DateTime completedAtUtc,
            Guid? operationId = null, Task<BaselineModuleExecutionResult> lateCompletion = null)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("Job ID required.");
            Item = item ?? throw new ArgumentNullException(nameof(item)); BulkBaselineGuard.Defined(outcome); BulkBaselineGuard.Defined(source);
            StartedAtUtc = BulkBaselineGuard.Utc(startedAtUtc); CompletedAtUtc = BulkBaselineGuard.Utc(completedAtUtc);
            if (completedAtUtc < startedAtUtc) throw new ArgumentException("Invalid execution timestamps.");
            if (outcome == BaselineWriteOutcome.Created && (!writeAttempted || item.ExpectedVersion == null)) throw new ArgumentException("Created requires verified target and attempt.");
            if (writeAttempted && outcome != BaselineWriteOutcome.Created && outcome != BaselineWriteOutcome.NeedsReconciliation) throw new ArgumentException("Attempted writes require a known success or reconciliation.");
            if (lateCompletion != null && outcome != BaselineWriteOutcome.NeedsReconciliation) throw new ArgumentException("Late completion is only an uncertainty observation.");
            JobId = jobId; Outcome = outcome; Source = source; WriteAttempted = writeAttempted;
            Reason = BulkBaselineGuard.Text(reason, nameof(reason), 4096); OperationId = operationId; LateCompletion = lateCompletion;
        }
        public Guid JobId { get; }
        public BulkBaselineDryRunItem Item { get; }
        public BaselineModuleReference Module => Item.Module;
        public BaselineVersion CurrentVersion => Item.CurrentVersion;
        public BaselineVersion ExpectedVersion => Item.ExpectedVersion;
        public string Suffix => Item.Probe.Suffix;
        public string Annotation => Item.Probe.Annotation;
        public BaselineWriteOutcome Outcome { get; }
        public BaselineMetadataSource Source { get; }
        // On timeout this means a write MAY have been dispatched; never interpret it as proof of creation.
        public bool WriteAttempted { get; }
        public bool AutomaticRetryAllowed => false;
        public string Reason { get; }
        public DateTime StartedAtUtc { get; }
        public DateTime CompletedAtUtc { get; }
        public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;
        public Guid? OperationId { get; }
        public Task<BaselineModuleExecutionResult> LateCompletion { get; }
    }
    public sealed class BulkBaselineExecutionReport
    {
        internal BulkBaselineExecutionReport(BaselineExecutionReview review, IEnumerable<BaselineModuleExecutionResult> items)
        { Review = review; Items = BulkBaselineGuard.List(items, 10000); }
        public BaselineExecutionReview Review { get; }
        public Guid JobId => Review.JobId;
        public IReadOnlyList<BaselineModuleExecutionResult> Items { get; }
        public bool NeedsReconciliation => Items.Any(i => i.Outcome == BaselineWriteOutcome.NeedsReconciliation);
        public bool AutomaticRetryAllowed => false;
    }
    public interface IBaselineExecutionAdapter
    {
        BaselineMetadataSource Source { get; }
        Task<BaselineModuleExecutionResult> ExecuteBaselineAsync(BaselineExecutionAuthorization authorization, CancellationToken cancellationToken);
    }
    public interface IBaselineExecutionService
    {
        Task<BulkBaselineExecutionReport> ExecuteAsync(BulkBaselineDryRunPlan reviewedPlan, CancellationToken cancellationToken = default(CancellationToken));
    }
    public sealed class BaselineExecutionService : IBaselineExecutionService
    {
        private readonly IBulkBaselinePreflightService preflight;
        private readonly IBaselineExecutionAdapter adapter;
        private readonly IBaselineExecutionConfirmation confirmation;
        private readonly IBaselinePreflightClock clock;
        private readonly IIntegrationLogger logger;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly HashSet<Guid> consumed = new HashSet<Guid>();
        public BaselineExecutionService(IBulkBaselinePreflightService preflight, IBaselineExecutionAdapter adapter,
            IBaselineExecutionConfirmation confirmation, IBaselinePreflightClock clock, IIntegrationLogger logger)
        { this.preflight = preflight ?? throw new ArgumentNullException(nameof(preflight)); this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter)); this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation)); this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }
        public async Task<BulkBaselineExecutionReport> ExecuteAsync(BulkBaselineDryRunPlan reviewedPlan, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reviewedPlan == null) throw new ArgumentNullException(nameof(reviewedPlan)); cancellationToken.ThrowIfCancellationRequested();
            if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("A baseline job is already active; no retry was scheduled.");
            try
            {
                // Validate integrity/expiry before asking for approval. Revalidate again after approval.
                if (consumed.Contains(reviewedPlan.PlanId)) throw new InvalidOperationException("This plan was already used. Inspect the previous outcome and obtain a new review.");
                var initial = await preflight.RevalidateAsync(reviewedPlan, cancellationToken).ConfigureAwait(false);
                var review = new BaselineExecutionReview(reviewedPlan); var results = new List<BaselineModuleExecutionResult>();
                if (initial.EligibleCount == 0) return ReportAll(review, BaselineWriteOutcome.Blocked, "Preflight has no eligible modules.");
                if (!await confirmation.ConfirmAsync(review, cancellationToken).ConfigureAwait(false)) return ReportAll(review, BaselineWriteOutcome.Declined, "Explicit approval was not granted.");
                if (consumed.Count >= 10000) throw new InvalidOperationException("Execution history capacity reached; no write started.");
                consumed.Add(reviewedPlan.PlanId);
                bool stopped = false;
                foreach (var original in reviewedPlan.Items)
                {
                    if (stopped || cancellationToken.IsCancellationRequested || original.Status != BulkBaselineItemStatus.Ready)
                    {
                        results.Add(Result(review, original, BaselineWriteOutcome.NotRun, "Skipped, stopped or cancelled before dispatch."));
                        if (cancellationToken.IsCancellationRequested || reviewedPlan.Request.ExecutionPolicy == ExecutionPolicy.StopOnFirstError) stopped = true;
                        continue;
                    }
                    // Fresh semantic comparison immediately before EACH module. Never promote blocked rows.
                    // Scope revalidation to this original item. A successful earlier baseline legitimately
                    // changes its own metadata; StopOnFirstError must not invalidate the remaining rows.
                    var dispatchPlan = new BulkBaselineDryRunPlan(reviewedPlan.PlanId, reviewedPlan.Request, new[] { original },
                        reviewedPlan.CreatedAtUtc, reviewedPlan.RequestFingerprint);
                    BaselinePreflightRevalidation fresh;
                    try { fresh = await preflight.RevalidateAsync(dispatchPlan, cancellationToken).ConfigureAwait(false); }
                    catch (Exception)
                    {
                        results.Add(Result(review, original, BaselineWriteOutcome.NotRun, "Dispatch preflight failed or was cancelled; no write dispatched for this module."));
                        stopped = true; continue;
                    }
                    var checkedItem = fresh.Items.Single(i => ReferenceEquals(i.Original, original));
                    if (!checkedItem.EligibleForPreview || checkedItem.Fresh.Precondition.Facts.Source != adapter.Source)
                    {
                        results.Add(Result(review, original, BaselineWriteOutcome.Blocked, "Precondition changed or Test/Mock/Real source mismatch."));
                        if (checkedItem.Fresh.UnsafeToContinue || reviewedPlan.Request.ExecutionPolicy == ExecutionPolicy.StopOnFirstError) stopped = true;
                        continue;
                    }
                    var capability = new BaselineExecutionAuthorization(review, checkedItem.Fresh, clock);
                    try { Log("BaselineWriteApproved", review.JobId, "PlanId=" + reviewedPlan.PlanId.ToString("N") + "; AutomaticRetry=False"); }
                    catch (Exception)
                    {
                        results.Add(Result(review, original, BaselineWriteOutcome.NotRun, "Approval audit logging failed; no write dispatched for this module."));
                        stopped = true; continue;
                    }
                    BaselineModuleExecutionResult result;
                    try
                    {
                        result = await adapter.ExecuteBaselineAsync(capability, cancellationToken).ConfigureAwait(false);
                        if (result == null || result.JobId != review.JobId || !ReferenceEquals(result.Item, capability.Item) || result.Source != adapter.Source)
                            throw new InvalidOperationException("Adapter result context mismatch.");
                    }
                    catch (Exception)
                    {
                        // Unknown adapter failures may follow dispatch. Do not erase previous successes or retry.
                        result = Result(review, capability.Item, BaselineWriteOutcome.NeedsReconciliation, "Adapter failed after authorization; inspect DOORS and operation logs before further writes.", true);
                    }
                    results.Add(result);
                    // A failing log sink cannot erase an already created baseline from the returned report.
                    try { Log("BaselineWriteCompleted", review.JobId, "Outcome=" + result.Outcome + "; AutomaticRetry=False"); }
                    catch (Exception) { stopped = true; }
                    if (result.Outcome == BaselineWriteOutcome.NeedsReconciliation || (result.Outcome != BaselineWriteOutcome.Created && reviewedPlan.Request.ExecutionPolicy == ExecutionPolicy.StopOnFirstError)) stopped = true;
                }
                return new BulkBaselineExecutionReport(review, results);
            }
            finally { gate.Release(); }
        }
        private BulkBaselineExecutionReport ReportAll(BaselineExecutionReview review, BaselineWriteOutcome outcome, string reason) =>
            new BulkBaselineExecutionReport(review, review.Plan.Items.Select(i => Result(review, i, outcome, reason)));
        private BaselineModuleExecutionResult Result(BaselineExecutionReview review, BulkBaselineDryRunItem item, BaselineWriteOutcome outcome, string reason, bool attempted = false) =>
            new BaselineModuleExecutionResult(review.JobId, item, outcome, adapter.Source, attempted, reason, clock.UtcNow, clock.UtcNow);
        private void Log(string name, Guid id, string message) => logger.Write(IntegrationLogLevel.Information, name, id, message);
    }
}
