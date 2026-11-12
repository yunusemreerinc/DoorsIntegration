using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class UnavailableBaselineMetadataProvider : IBaselineMetadataProvider
    {
        public Task<BaselineModuleMetadata> ReadAsync(BaselineModuleReference module, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(BaselineModuleMetadata.Unavailable(module,
                "NOT_RUN: a verified DOORS baseline metadata adapter is not installed in Task 19.", DateTime.UtcNow));
        }
    }

    public sealed class ReadOnlyBaselineExecutor : IBaselineExecutor
    {
        public Task<BaselineExecutionResult> PreviewAsync(BaselinePlan plan, BaselinePreflightResult preflight,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var utc = DateTime.UtcNow;
            return Task.FromResult(BaselineExecutionResult.Preview(plan, preflight, utc, utc));
        }
    }

    // Session-lifetime storage, bounded with no silent eviction. Durable storage can implement the same port.
    public sealed class InMemoryBaselineJobRepository : IBaselineJobRepository
    {
        private readonly object sync = new object();
        private readonly Dictionary<Guid, BaselineJob> jobs = new Dictionary<Guid, BaselineJob>();
        private readonly int maxJobs;
        public InMemoryBaselineJobRepository(int maxJobs = 1000)
        { if (maxJobs < 1 || maxJobs > 100000) throw new ArgumentOutOfRangeException(nameof(maxJobs)); this.maxJobs = maxJobs; }
        public Task AddAsync(BaselineJob job, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (job.Revision != 0 || jobs.ContainsKey(job.JobId)) throw BulkBaselineGuard.Failure("Job already exists or is not an initial revision.");
                if (jobs.Count == maxJobs) throw BulkBaselineGuard.Failure("Baseline job repository capacity reached; no jobs were evicted.");
                jobs.Add(job.JobId, job);
            }
            return Task.CompletedTask;
        }
        public Task<BaselineJob> GetAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            lock (sync) { cancellationToken.ThrowIfCancellationRequested(); jobs.TryGetValue(jobId, out var job); return Task.FromResult(job); }
        }
        public Task<bool> TryUpdateAsync(BaselineJob job, long expectedRevision, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!jobs.TryGetValue(job.JobId, out var current) || current.Revision != expectedRevision) return Task.FromResult(false);
                if (job.Revision != expectedRevision + 1 || !ReferenceEquals(current.Plan, job.Plan) || job.CreatedAtUtc != current.CreatedAtUtc || job.UpdatedAtUtc < current.UpdatedAtUtc)
                    throw BulkBaselineGuard.Failure("Invalid baseline job revision or plan replacement.");
                jobs[job.JobId] = job; return Task.FromResult(true);
            }
        }
    }
}
