using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    // No UI dependency and no COM calls on the coordinator thread. Native adapter dispatches to its shared STA.
    public sealed class BulkBaselineJobRunner : IBulkBaselineJobRunner
    {
        private readonly IBulkBaselineJobJournal journal;
        private readonly IBulkBaselinePreflightService preflight;
        private readonly IBaselineExecutionAdapter adapter;
        private readonly IBaselineExecutionConfirmation confirmation;
        private readonly IBulkBaselineRetryConfirmation retryConfirmation;
        private readonly IBaselineJobStateVerifier verifier;
        private readonly IBaselinePreflightClock clock;
        private readonly IIntegrationLogger logger;
        private readonly IBaselineJobAuditContextProvider auditContext;
        private readonly BaselineOperationCoordinator coordinator = new BaselineOperationCoordinator();
        public BulkBaselineJobRunner(IBulkBaselineJobJournal journal, IBulkBaselinePreflightService preflight, IBaselineExecutionAdapter adapter,
            IBaselineExecutionConfirmation confirmation, IBulkBaselineRetryConfirmation retryConfirmation, IBaselineJobStateVerifier verifier,
            IBaselinePreflightClock clock, IIntegrationLogger logger)
            : this(journal, preflight, adapter, confirmation, retryConfirmation, verifier, clock, logger, new BaselineJobAuditContextProvider()) { }
        public BulkBaselineJobRunner(IBulkBaselineJobJournal journal, IBulkBaselinePreflightService preflight, IBaselineExecutionAdapter adapter,
            IBaselineExecutionConfirmation confirmation, IBulkBaselineRetryConfirmation retryConfirmation, IBaselineJobStateVerifier verifier,
            IBaselinePreflightClock clock, IIntegrationLogger logger, IBaselineJobAuditContextProvider auditContext)
        { this.journal = journal ?? throw new ArgumentNullException(nameof(journal)); this.preflight = preflight ?? throw new ArgumentNullException(nameof(preflight)); this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter)); this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation)); this.retryConfirmation = retryConfirmation ?? throw new ArgumentNullException(nameof(retryConfirmation)); this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier)); this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); this.auditContext = auditContext ?? throw new ArgumentNullException(nameof(auditContext)); }
        public Task<BulkBaselineJobRecord> CreateAsync(BulkBaselineDryRunPlan plan, CancellationToken cancellationToken = default(CancellationToken)) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan == null || plan.Items.Count != plan.Request.Baseline.Modules.Count || plan.Fingerprint != BaselinePreflightFingerprint.Plan(plan) ||
                plan.RequestFingerprint != BaselinePreflightFingerprint.Request(plan.Request)) throw BaselineJobGuard.Failure("Complete, intact dry-run plan required.");
            if (plan.Items.Any(i => i.Status == BulkBaselineItemStatus.Ready && (i.Precondition == null || i.Precondition.Facts.Source != adapter.Source)))
                throw BaselineJobGuard.Failure("Mock/Real plan and execution source mismatch.");
            var job = new BulkBaselineJobRecord(plan, adapter.Source, clock.UtcNow, auditContext.Capture(plan, adapter.Source));
            using (var lease = journal.Acquire(job.JobId)) { lease.Save(job); return lease.Current; }
        }, cancellationToken);
        public Task<BulkBaselineJobRecord> OpenAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken)) => WorkAsync(async () =>
        {
            using (var lease = journal.Acquire(jobId))
            {
                var run = new RunContext(lease, null, clock, logger, true); RequireJob(run);
                await RecoverAsync(run, cancellationToken).ConfigureAwait(false); return Finish(run);
            }
        }, cancellationToken);
        public Task<BulkBaselineJobRecord> RunAsync(Guid jobId, IProgress<BulkBaselineJobProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken)) =>
            WorkAsync(() => RunCoreAsync(jobId, null, progress, cancellationToken), cancellationToken);
        public Task<BulkBaselineJobRecord> RetryAsync(Guid jobId, string moduleId, IProgress<BulkBaselineJobProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            BulkBaselineGuard.Text(moduleId, nameof(moduleId), 4096);
            return WorkAsync(() => RunCoreAsync(jobId, moduleId, progress, cancellationToken), cancellationToken);
        }
        private Task<BulkBaselineJobRecord> WorkAsync(Func<Task<BulkBaselineJobRecord>> work, CancellationToken token) => coordinator.RunAsync(work, token);
        private async Task<BulkBaselineJobRecord> RunCoreAsync(Guid id, string retryModule, IProgress<BulkBaselineJobProgress> progress, CancellationToken token)
        {
            using (var lease = journal.Acquire(id))
            {
                var run = new RunContext(lease, progress, clock, logger); RequireJob(run);
                if (run.Job.Source != adapter.Source) throw BaselineJobGuard.Failure("Journal and adapter sources differ; no native dispatch.");
                run.State(run.Job.Status, action: BaselineAuditAction.RunStarted);
                await RecoverAsync(run, token).ConfigureAwait(false);
                int retryIndex = retryModule == null ? -1 : run.Job.Modules.ToList().FindIndex(m => m.ModuleId == retryModule);
                if (retryModule != null && retryIndex < 0) throw BaselineJobGuard.Failure("Retry module is not part of this immutable job.");
                if (retryIndex >= 0 && run.Job.Modules[retryIndex].EverSucceeded) return Finish(run); // Sticky success is never retryable.
                if (run.Job.Modules.Where((m, index) => index != retryIndex).Any(Uncertain)) return Finish(run);
                if (retryIndex >= 0)
                {
                    var item = run.Job.Modules[retryIndex];
                    if (item.ExpectedVersion == null || (item.Status != BulkBaselineModuleStatus.Failed && item.Status != BulkBaselineModuleStatus.Skipped && !Uncertain(item)))
                        throw BaselineJobGuard.Failure("Only a failed/skipped/uncertain exact target can be explicitly retried; never retarget this job.");
                }
                run.State(BulkBaselineModuleStatus.Pending, false);
                bool stopped = false;
                for (int index = 0; index < run.Job.Modules.Count; index++)
                {
                    if (retryIndex >= 0 && index != retryIndex) continue;
                    if (token.IsCancellationRequested) { CancelRemaining(run, index); break; }
                    var item = run.Job.Modules[index];
                    if (item.EverSucceeded) continue;
                    bool ordinary = item.Status == BulkBaselineModuleStatus.Pending || item.Status == BulkBaselineModuleStatus.Ready || item.Status == BulkBaselineModuleStatus.Prechecking ||
                        (item.Status == BulkBaselineModuleStatus.CancelRequested && item.Attempts == 0);
                    if (retryIndex < 0 && !ordinary)
                    {
                        if (run.Job.Policy == ExecutionPolicy.StopOnFirstError && item.Status != BulkBaselineModuleStatus.Succeeded) stopped = true;
                        continue;
                    }
                    if (stopped)
                    { run.Module(index, item.With(BulkBaselineModuleStatus.Skipped, "Stopped by StopOnFirstError; explicit retry/review required.", run.Now)); continue; }
                    try { await ExecuteOneAsync(run, index, retryIndex >= 0, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { CancelRemaining(run, index); break; }
                    item = run.Job.Modules[index];
                    if (Uncertain(item)) { stopped = true; break; } // No ContinueOnError after an uncertain native outcome.
                    if (item.Status != BulkBaselineModuleStatus.Succeeded && run.Job.Policy == ExecutionPolicy.StopOnFirstError) stopped = true;
                }
                if (token.IsCancellationRequested) CancelRemaining(run, 0);
                return Finish(run);
            }
        }
        private async Task ExecuteOneAsync(RunContext run, int index, bool retry, CancellationToken token)
        {
            var previous = run.Job.Modules[index];
            run.Module(index, previous.With(BulkBaselineModuleStatus.Prechecking, "", run.Now));
            var evidence = await VerifyAsync(run.Job, run.Job.Modules[index], token).ConfigureAwait(false);
            if (!AbsentOrSettle(run, index, evidence)) return;
            BulkBaselineDryRunPlan plan;
            try { plan = await preflight.CreatePlanAsync(run.Job.Request(run.Job.Modules[index]), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { run.Module(index, run.Job.Modules[index].With(BulkBaselineModuleStatus.Skipped, "Fresh preflight failed; no write dispatched.", run.Now)); return; }
            var item = plan.Items.Single(); var record = run.Job.Modules[index];
            if (!SameTarget(run.Job, record, item))
            {
                var status = item.ExpectedVersion != null && !item.ExpectedVersion.Equals(record.ExpectedVersion) ? BulkBaselineModuleStatus.NeedsReconciliation : BulkBaselineModuleStatus.Skipped;
                run.Module(index, record.With(status, "Fresh preflight blocked or the original target/context changed. No retargeting. " + item.Reason, run.Now, preflight: item)); return;
            }
            run.Module(index, record.With(BulkBaselineModuleStatus.Ready, "", run.Now, verification: evidence, precondition: item.Precondition.Fingerprint, preflight: item));
            if (retry && !await retryConfirmation.ConfirmAsync(new BaselineJobRetryReview(run.Job, previous, evidence, plan), token).ConfigureAwait(false))
            { run.Module(index, run.Job.Modules[index].With(previous.Status, "Operator retry approval was not granted.", run.Now)); return; }
            var review = new BaselineExecutionReview(plan, run.Job.JobId);
            if (!await confirmation.ConfirmAsync(review, token).ConfigureAwait(false))
            { run.Module(index, run.Job.Modules[index].With(BulkBaselineModuleStatus.Skipped, "Explicit baseline creation approval was not granted.", run.Now)); return; }
            var approved = (confirmation as IBaselineApprovalAuditSource)?.CaptureAcceptedApproval(review, run.Now) ??
                BaselineApprovedPlan.Capture(plan, run.Job.JobId, run.Now, item.Precondition.Facts.CurrentUserId, "ConfiguredConfirmationPort; single-module acceptance");
            run.Approval(index, approved);
            token.ThrowIfCancellationRequested();
            evidence = await VerifyAsync(run.Job, run.Job.Modules[index], token).ConfigureAwait(false); // Do not reuse pre-dialog absence evidence.
            if (!AbsentOrSettle(run, index, evidence)) return;
            BaselinePreflightRevalidation revalidation;
            try { revalidation = await preflight.RevalidateAsync(plan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            { run.Module(index, run.Job.Modules[index].With(BulkBaselineModuleStatus.NeedsReconciliation, "Post-approval read/preflight outcome is uncertain; no write dispatched.", run.Now)); return; }
            var fresh = revalidation.Items.Single(); record = run.Job.Modules[index];
            if (!fresh.EligibleForPreview || !SameTarget(run.Job, record, fresh.Fresh))
            { run.Module(index, record.With(BulkBaselineModuleStatus.Skipped, "Post-approval preconditions changed; no write dispatched.", run.Now)); return; }
            token.ThrowIfCancellationRequested();
            var authorization = new BaselineExecutionAuthorization(review, fresh.Fresh, clock, retry ? evidence : null, token);
            // Write-ahead intent is durably replaced BEFORE dispatch. Crash here is uncertain, never auto retry.
            run.Module(index, record.With(BulkBaselineModuleStatus.Running, "", run.Now, beginAttempt: true, verification: evidence, precondition: fresh.Fresh.Precondition.Fingerprint));
            if (token.IsCancellationRequested)
            { run.Module(index, run.Job.Modules[index].With(BulkBaselineModuleStatus.CancelRequested, "Cancelled after intent checkpoint but before native dispatch.", run.Now, mayHaveWritten: false), true); return; }
            try { logger.Write(IntegrationLogLevel.Information, "BulkBaselineJobDispatchApproved", run.Job.JobId, "AttemptId=" + run.Job.Modules[index].AttemptId + "; AutomaticRetry=False"); }
            catch (Exception)
            { run.Module(index, run.Job.Modules[index].With(BulkBaselineModuleStatus.Failed, "Approval audit logging failed before dispatch.", run.Now, mayHaveWritten: false)); return; }
            BaselineModuleExecutionResult result;
            try
            {
                // After durable intent, cancellation stops FUTURE modules; it cannot abort this COM call.
                var active = adapter.ExecuteBaselineAsync(authorization, CancellationToken.None);
                _ = active.ContinueWith(t => { var observed = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                result = await WaitForActiveAsync(run, index, active, token).ConfigureAwait(false);
                if (result == null || result.JobId != run.Job.JobId || !ReferenceEquals(result.Item, authorization.Item) || result.Source != run.Job.Source)
                    throw BaselineJobGuard.Failure("Adapter response context mismatch.");
            }
            catch (BulkBaselineJournalException) { throw; }
            catch (Exception)
            {
                run.Module(index, run.Job.Modules[index].With(BulkBaselineModuleStatus.Unknown, "Adapter/COM outcome unknown; no automatic retry.", run.Now));
                run.Module(index, run.Job.Modules[index].With(BulkBaselineModuleStatus.NeedsReconciliation, "Inspect exact target and reconcile the recorded dispatch intent.", run.Now)); return;
            }
            record = run.Job.Modules[index];
            if (result.Outcome == BaselineWriteOutcome.NeedsReconciliation)
            {
                run.Module(index, record.With(BulkBaselineModuleStatus.Unknown, result.Reason, run.Now, result: result));
                run.Module(index, run.Job.Modules[index].With(BulkBaselineModuleStatus.NeedsReconciliation, result.Reason, run.Now));
            }
            else
            {
                var status = result.Outcome == BaselineWriteOutcome.Created ? BulkBaselineModuleStatus.Succeeded :
                    result.Outcome == BaselineWriteOutcome.Blocked || result.Outcome == BaselineWriteOutcome.Declined ? BulkBaselineModuleStatus.Skipped : BulkBaselineModuleStatus.Failed;
                // Commit after EVERY module. Failure leaves durable Running/CancelRequested intent and aborts the job.
                run.Module(index, record.With(status, result.Reason, run.Now, result: result));
            }
        }
        private async Task<BaselineModuleExecutionResult> WaitForActiveAsync(RunContext run, int index, Task<BaselineModuleExecutionResult> active, CancellationToken token)
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => signal.TrySetResult(true)))
            {
                bool noticed = false;
                while (!active.IsCompleted)
                {
                    var tick = Task.Delay(500);
                    await (noticed ? Task.WhenAny(active, tick) : Task.WhenAny(active, signal.Task, tick)).ConfigureAwait(false);
                    if (!noticed && token.IsCancellationRequested)
                    {
                        noticed = true;
                        run.Module(index, run.Job.Modules[index].With(BulkBaselineModuleStatus.CancelRequested, "Cancellation requested; active COM call is still being observed.", run.Now), true);
                    }
                    run.Report(index);
                }
                return await active.ConfigureAwait(false);
            }
        }
        private async Task RecoverAsync(RunContext run, CancellationToken token)
        {
            for (int index = 0; index < run.Job.Modules.Count; index++)
            {
                token.ThrowIfCancellationRequested(); var item = run.Job.Modules[index];
                if (item.ExpectedVersion == null) continue;
                bool activeIntent = item.Attempts > 0 && (item.Status == BulkBaselineModuleStatus.Running || item.Status == BulkBaselineModuleStatus.CancelRequested || Uncertain(item));
                if (item.EverSucceeded || activeIntent)
                {
                    if (item.Status == BulkBaselineModuleStatus.Running || item.Status == BulkBaselineModuleStatus.CancelRequested)
                    { run.Module(index, item.With(BulkBaselineModuleStatus.Unknown, "Interrupted persisted dispatch; outcome must be checked before any new write.", run.Now)); item = run.Job.Modules[index]; }
                    var evidence = await VerifyAsync(run.Job, item, token).ConfigureAwait(false);
                    bool recovered = evidence.Status == BaselineJobVerificationStatus.ExistsMatching && (item.EverSucceeded || item.WriteMayHaveOccurred);
                    run.Module(index, item.With(recovered ? BulkBaselineModuleStatus.Succeeded : BulkBaselineModuleStatus.NeedsReconciliation,
                        recovered ? "Exact desired baseline verified read-only; no create repeated." : "Journal and DOORS state are unresolved or contradictory; explicit reconciliation required.", run.Now, verification: evidence));
                }
                else if (item.Status == BulkBaselineModuleStatus.Prechecking || item.Status == BulkBaselineModuleStatus.Ready)
                    run.Module(index, item.With(BulkBaselineModuleStatus.Pending, "Interrupted before durable dispatch; new preflight and approval required.", run.Now));
            }
        }
        private async Task<BaselineJobVerification> VerifyAsync(BulkBaselineJobRecord job, BulkBaselineJobModule module, CancellationToken token)
        {
            BaselineJobVerification value = null;
            try { value = await verifier.VerifyAsync(job, module, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { token.ThrowIfCancellationRequested(); }
            if (value != null && value.Module.Matches(module.Module) && module.ExpectedVersion.Equals(value.ExpectedVersion) && value.Annotation == module.Annotation &&
                value.DatabaseIdentity == module.DatabaseIdentity && value.UserId == module.UserId && value.Source == job.Source && clock.UtcNow >= value.ObservedAtUtc && clock.UtcNow - value.ObservedAtUtc <= job.MaxObservationAge) return value;
            return new BaselineJobVerification(module.Module, module.ExpectedVersion, module.Annotation, module.DatabaseIdentity, module.UserId, job.Source,
                BaselineJobVerificationStatus.Unknown, clock.UtcNow, "Missing, stale, failed or foreign-context verification cannot authorize retry.");
        }
        private static bool AbsentOrSettle(RunContext run, int index, BaselineJobVerification evidence)
        {
            if (evidence.Status == BaselineJobVerificationStatus.AbsentAuthoritatively) return true;
            var record = run.Job.Modules[index]; bool recovered = evidence.Status == BaselineJobVerificationStatus.ExistsMatching && (record.EverSucceeded || record.WriteMayHaveOccurred);
            run.Module(index, record.With(recovered ? BulkBaselineModuleStatus.Succeeded : BulkBaselineModuleStatus.NeedsReconciliation,
                recovered ? "Previously dispatched desired baseline is present; no duplicate create." : "Target existence/conflict/unknown state requires reconciliation; no write dispatched.", run.Now, verification: evidence)); return false;
        }
        private static bool SameTarget(BulkBaselineJobRecord job, BulkBaselineJobModule record, BulkBaselineDryRunItem item) =>
            item != null && item.Status == BulkBaselineItemStatus.Ready && item.ExpectedVersion != null && record.ExpectedVersion.Equals(item.ExpectedVersion) &&
            Equals(record.CurrentVersion, item.CurrentVersion) && item.Module.Matches(record.Module) && item.Suffix == record.Suffix && item.Annotation == record.Annotation &&
            item.Precondition != null && item.Precondition.Facts.Source == job.Source && item.Precondition.Facts.DatabaseIdentity == record.DatabaseIdentity && item.Precondition.Facts.CurrentUserId == record.UserId;
        private static bool Uncertain(BulkBaselineJobModule item) => item.Status == BulkBaselineModuleStatus.Unknown || item.Status == BulkBaselineModuleStatus.NeedsReconciliation;
        private static void RequireJob(RunContext run) { if (run.Job == null) throw BaselineJobGuard.Failure("Job was not found; journals are never silently recreated."); }
        private static void CancelRemaining(RunContext run, int from)
        {
            for (int i = from; i < run.Job.Modules.Count; i++)
            {
                var item = run.Job.Modules[i];
                if (item.Attempts == 0 && (item.Status == BulkBaselineModuleStatus.Pending || item.Status == BulkBaselineModuleStatus.Ready || item.Status == BulkBaselineModuleStatus.Prechecking))
                    run.Module(i, item.With(BulkBaselineModuleStatus.CancelRequested, "Cancelled before dispatch; a later explicit Run requires fresh review.", run.Now), true);
            }
            if (!run.Job.CancellationRequested) run.State(BulkBaselineModuleStatus.CancelRequested, true);
        }
        private static BulkBaselineJobRecord Finish(RunContext run)
        {
            var status = run.Job.Modules.Any(Uncertain) ? BulkBaselineModuleStatus.NeedsReconciliation : run.Job.CancellationRequested ? BulkBaselineModuleStatus.CancelRequested :
                run.Job.Modules.Any(m => m.Status == BulkBaselineModuleStatus.Failed) ? BulkBaselineModuleStatus.Failed :
                run.Job.Modules.Any(m => m.Status == BulkBaselineModuleStatus.Skipped) ? BulkBaselineModuleStatus.Skipped :
                run.Job.Modules.All(m => m.Status == BulkBaselineModuleStatus.Succeeded) ? BulkBaselineModuleStatus.Succeeded : BulkBaselineModuleStatus.Pending;
            run.State(status, action: run.IsRecovery ? BaselineAuditAction.Reconciled : BaselineAuditAction.RunCompleted); return run.Job;
        }
        private sealed class RunContext
        {
            private readonly IBulkBaselineJobJournalLease lease; private readonly IProgress<BulkBaselineJobProgress> progress;
            private readonly IBaselinePreflightClock clock; private readonly IIntegrationLogger logger;
            private readonly Stopwatch watch = Stopwatch.StartNew(); private readonly TimeSpan previousElapsed;
            internal RunContext(IBulkBaselineJobJournalLease lease, IProgress<BulkBaselineJobProgress> progress, IBaselinePreflightClock clock, IIntegrationLogger logger, bool recovery = false)
            { this.lease = lease; this.progress = progress; this.clock = clock; this.logger = logger; previousElapsed = lease.Current?.Elapsed ?? TimeSpan.Zero; IsRecovery = recovery; }
            internal bool IsRecovery { get; }
            internal BulkBaselineJobRecord Job => lease.Current;
            internal DateTime Now => clock.UtcNow < Job.UpdatedAtUtc ? Job.UpdatedAtUtc : clock.UtcNow;
            private TimeSpan Elapsed => previousElapsed + watch.Elapsed;
            internal void Module(int index, BulkBaselineJobModule value, bool? cancel = null)
            {
                var overall = value.Status == BulkBaselineModuleStatus.Succeeded || value.Status == BulkBaselineModuleStatus.Skipped || value.Status == BulkBaselineModuleStatus.Failed ? BulkBaselineModuleStatus.Running : value.Status;
                lease.Save(Job.With(overall, Now, Elapsed, index, value, cancel, IsRecovery ? BaselineAuditAction.Reconciled : BaselineAuditAction.ModuleCheckpoint));
                try { logger.Write(IntegrationLogLevel.Information, "BulkBaselineJobCheckpoint", Job.JobId,
                    "Revision=" + Job.Revision + "; Index=" + index + "; Status=" + value.Status + "; AttemptId=" + value.AttemptId + "; OperationId=" + value.OperationId); }
                catch (Exception) { /* A committed journal is authoritative even if its secondary log sink fails. */ }
                Report(index);
            }
            internal void State(BulkBaselineModuleStatus status, bool? cancel = null, BaselineAuditAction action = BaselineAuditAction.Checkpoint)
            { lease.Save(Job.With(status, Now, Elapsed, cancelled: cancel, action: action)); Report(-1); }
            internal void Approval(int index, BaselineApprovedPlan approval)
            { var value = Job.Modules[index].With(Job.Modules[index].Status, Job.Modules[index].FailureReason, Now, approvedPlan: approval);
                lease.Save(Job.With(Job.Status, Now, Elapsed, index, value, action: BaselineAuditAction.PlanApproved, approval: approval)); Report(index); }
            internal void Report(int index)
            {
                try { progress?.Report(new BulkBaselineJobProgress(Job, index < 0 ? null : Job.Modules[index], Elapsed)); }
                catch (Exception) { try { logger.Write(IntegrationLogLevel.Error, "BulkBaselineProgressObserverFailed", Job.JobId, "Journal state is retained; progress observer failed."); } catch (Exception) { } }
            }
        }
    }
}
