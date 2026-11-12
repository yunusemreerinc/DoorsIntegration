using System;
using System.Threading;
using System.Threading.Tasks;
namespace DoorsIntegration.Core
{
    // Shared orchestration primitive; actual native calls still use the existing global STA/executor gate.
    public sealed class BaselineOperationCoordinator
    {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken token)
        {
            if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) throw BaselineJobGuard.Failure("A job coordinator is already active. No second job was scheduled.");
            try { return await Task.Run(work, token).ConfigureAwait(false); } finally { gate.Release(); }
        }
    }
}
