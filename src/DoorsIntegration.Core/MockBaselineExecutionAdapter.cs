using System;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    // Explicit mock only. This does not open COM and never represents a native DOORS baseline.
    public sealed class MockBaselineExecutionAdapter : IBaselineExecutionAdapter
    {
        public BaselineMetadataSource Source => BaselineMetadataSource.Mock;
        public int Attempts { get; private set; }
        public Task<BaselineModuleExecutionResult> ExecuteBaselineAsync(BaselineExecutionAuthorization authorization, CancellationToken cancellationToken)
        {
            if (authorization == null) throw new ArgumentNullException(nameof(authorization)); cancellationToken.ThrowIfCancellationRequested();
            var start = DateTime.UtcNow; bool valid = authorization.Item.Precondition.Facts.Source == Source && authorization.TryBeginWrite();
            if (valid) Attempts++;
            return Task.FromResult(new BaselineModuleExecutionResult(authorization.Review.JobId, authorization.Item,
                valid ? BaselineWriteOutcome.Created : BaselineWriteOutcome.Blocked, Source, valid,
                valid ? "Simulated baseline; no DOORS data was changed." : "Mock source or single-use approval rejected.", start, DateTime.UtcNow));
        }
    }
}
