using System;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public interface IBulkBaselineService
    {
        Task<BaselineJob> CreatePlanAsync(BulkBaselineRequest request, CancellationToken cancellationToken = default(CancellationToken));
        Task<BaselineJob> PreflightAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken));
        Task<BaselineJob> PreviewExecutionAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken));
        Task<BaselineJob> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken));
    }

    // All providers must be read-only; they must report unknown permissions/locks explicitly.
    public interface IBaselineMetadataProvider
    {
        Task<BaselineModuleMetadata> ReadAsync(BaselineModuleReference module, CancellationToken cancellationToken = default(CancellationToken));
    }

    // Task 19 exposes no write-capable operation, authorization bypass, retry or create-baseline command.
    // A future writer needs a separate, explicit authorization contract and verified native adapter.
    public interface IBaselineExecutor
    {
        Task<BaselineExecutionResult> PreviewAsync(BaselinePlan plan, BaselinePreflightResult preflight,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    // Atomic optimistic concurrency; false means the caller must inspect the latest state, not retry automatically.
    public interface IBaselineJobRepository
    {
        Task AddAsync(BaselineJob job, CancellationToken cancellationToken = default(CancellationToken));
        Task<BaselineJob> GetAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> TryUpdateAsync(BaselineJob job, long expectedRevision, CancellationToken cancellationToken = default(CancellationToken));
    }
}
