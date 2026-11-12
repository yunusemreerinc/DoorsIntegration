using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class BulkBaselineService : IBulkBaselineService
    {
        private readonly IBaselineMetadataProvider metadata;
        private readonly IBaselineExecutor executor;
        private readonly IBaselineJobRepository jobs;
        private readonly IIntegrationLogger logger;
        private readonly BaselinePlanner planner;
        private readonly BaselinePreflightChecker preflight;
        public BulkBaselineService(IBaselineMetadataProvider metadata, IBaselineExecutor executor, IBaselineJobRepository jobs,
            IIntegrationLogger logger, BaselinePlanner planner, BaselinePreflightChecker preflight)
        {
            this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
            this.preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        }
        public async Task<BaselineJob> CreatePlanAsync(BulkBaselineRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var id = Guid.NewGuid();
            var observed = await ReadAllAsync(request.Modules, id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var utc = DateTime.UtcNow;
            var job = new BaselineJob(planner.Build(id, request, observed, utc), null, null, 0, utc, utc);
            await jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            Log("BulkBaselinePlanned", id, "ReadOnly=True; Modules=" + request.Modules.Count + "; Ready=" + job.Plan.IsReadyForPreflight);
            return job;
        }
        public async Task<BaselineJob> PreflightAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var job = await RequireAsync(jobId, cancellationToken).ConfigureAwait(false);
            var observed = await ReadAllAsync(job.Request.Modules, jobId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var utc = DateTime.UtcNow;
            var updated = job.WithPreflight(preflight.Check(job.Plan, observed, utc), Later(utc, job.UpdatedAtUtc));
            await SaveAsync(updated, job.Revision, cancellationToken).ConfigureAwait(false);
            Log("BulkBaselinePreflight", jobId, "ReadOnly=True; Passed=" + updated.Preflight.Passed + "; CanExecute=False");
            return updated;
        }
        public async Task<BaselineJob> PreviewExecutionAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var job = await RequireAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job.Execution != null) return job;
            if (job.Preflight == null) throw BulkBaselineGuard.Failure("Run preflight before requesting an execution preview.");
            var result = await executor.PreviewAsync(job.Plan, job.Preflight, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == null || !ReferenceEquals(result.Plan, job.Plan) || !ReferenceEquals(result.Preflight, job.Preflight) ||
                !result.IsPreview || result.WriteAttempted || result.Status != BaselineExecutionStatus.NotRun)
                throw BulkBaselineGuard.Failure("Executor returned an invalid read-only preview; no result was committed.");
            var updated = job.WithPreview(result, Later(DateTime.UtcNow, job.UpdatedAtUtc));
            await SaveAsync(updated, job.Revision, cancellationToken).ConfigureAwait(false);
            Log("BulkBaselineExecutionPreview", jobId, "Status=NOT_RUN; WriteAttempted=False");
            return updated;
        }
        public Task<BaselineJob> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken)) => RequireAsync(jobId, cancellationToken);
        private async Task<BaselineJob> RequireAsync(Guid id, CancellationToken token)
        {
            if (id == Guid.Empty) throw new ArgumentException("A job identity is required.", nameof(id));
            return await jobs.GetAsync(id, token).ConfigureAwait(false) ?? throw BulkBaselineGuard.Failure("Baseline job was not found in this repository/session.");
        }
        private async Task SaveAsync(BaselineJob job, long revision, CancellationToken token)
        {
            if (!await jobs.TryUpdateAsync(job, revision, token).ConfigureAwait(false))
                throw BulkBaselineGuard.Failure("Baseline job changed concurrently; inspect the latest job. No automatic retry was performed.");
        }
        private async Task<IReadOnlyList<BaselineModuleMetadata>> ReadAllAsync(IReadOnlyList<BaselineModuleSelection> selections, Guid id, CancellationToken token)
        {
            var observations = new List<BaselineModuleMetadata>();
            foreach (var selection in selections)
            {
                token.ThrowIfCancellationRequested();
                BaselineModuleMetadata observation;
                try { observation = await metadata.ReadAsync(selection.Module, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Never retry or disclose native error messages, paths, annotation or credentials in logs.
                    Log("BulkBaselineMetadataUnavailable", id, "ExceptionType=" + ex.GetType().FullName);
                    observation = BaselineModuleMetadata.Unavailable(selection.Module, "Metadata read failed; review correlated diagnostics. No retry was attempted.", DateTime.UtcNow);
                }
                observations.Add(observation);
            }
            return observations;
        }
        private static DateTime Later(DateTime utc, DateTime previous) => utc < previous ? previous : utc;
        private void Log(string name, Guid id, string message)
        { try { logger.Write(IntegrationLogLevel.Information, name, id, message); } catch { /* Read-only diagnostics are best effort. */ } }
    }
}
