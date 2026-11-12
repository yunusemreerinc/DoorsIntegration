using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class DxlExecutionOperation
    {
        private readonly TaskCompletionSource<DxlExecutionResult> completion =
            new TaskCompletionSource<DxlExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IIntegrationLogger logger;
        private int stopBeforeDispatch;
        private int promptAutomationCancelled;
        private readonly ConcurrentQueue<CancellationToken> promptCancellationTokens = new ConcurrentQueue<CancellationToken>();
        private string workspaceDirectory;

        internal DxlExecutionOperation(Guid id, IIntegrationLogger logger, TimeSpan timeout, bool watchdogEnabled = true)
        {
            CorrelationId = id;
            this.logger = logger;
            EffectiveTimeout = timeout;
            WatchdogEnabled = watchdogEnabled;
            // A caller may stop waiting; observe late faults without hiding them from later awaits.
            completion.Task.ContinueWith(task => { var observed = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public Guid CorrelationId { get; }
        public Task<DxlExecutionResult> Completion => completion.Task;
        public string WorkspaceDirectory => Volatile.Read(ref workspaceDirectory);
        public TimeSpan EffectiveTimeout { get; }
        public bool WatchdogEnabled { get; }
        internal bool PromptAutomationCancelled => Volatile.Read(ref promptAutomationCancelled) != 0 ||
            promptCancellationTokens.Any(token => token.IsCancellationRequested);

        public Task<DxlExecutionResult> WaitAsync(CancellationToken cancellationToken = default(CancellationToken)) =>
            WaitEffectiveAsync(EffectiveTimeout, cancellationToken);

        public async Task<DxlExecutionResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
        {
            DxlExecutionRequest.ValidateTimeout(timeout);
            return await WaitEffectiveAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        private async Task<DxlExecutionResult> WaitEffectiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Keep observing explicit cancellation even if this particular wait ends with a soft timeout first.
            if (cancellationToken.CanBeCanceled) promptCancellationTokens.Enqueue(cancellationToken);
            if (Completion.IsCompleted) return await Completion.ConfigureAwait(false);
            using (var delayStop = new CancellationTokenSource())
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    try
                    {
                        await Task.WhenAny(Completion, Task.Delay(timeout, delayStop.Token), cancelled.Task).ConfigureAwait(false);
                        if (Completion.IsCompleted) return await Completion.ConfigureAwait(false);
                        var code = cancellationToken.IsCancellationRequested ? IntegrationErrorCode.DxlWaitCancelled : IntegrationErrorCode.DxlTimeout;
                        if (code == IntegrationErrorCode.DxlWaitCancelled) Interlocked.Exchange(ref promptAutomationCancelled, 1);
                        Interlocked.CompareExchange(ref stopBeforeDispatch, (int)code, 0);
                        DxlLog.Write(logger, IntegrationLogLevel.Error,
                            code == IntegrationErrorCode.DxlTimeout ? "DxlWaitTimedOut" : "DxlWaitCancelled", CorrelationId,
                            "Code=" + code + "; ExecutionMayContinue=True");
                        throw new DxlExecutionWaitException(code, this);
                    }
                    finally { delayStop.Cancel(); }
                }
            }
        }

        internal void CheckBeforeDispatch()
        {
            var code = Volatile.Read(ref stopBeforeDispatch);
            if (code != 0)
                throw new IntegrationException((IntegrationErrorCode)code,
                    "Bekleme sona erdi; güvenli kontrol noktasında DXL dispatch durduruldu. Başlamış COM çağrısına müdahale edilmedi.");
        }
        internal void SetWorkspace(string path) => Volatile.Write(ref workspaceDirectory, path);
        internal void Complete(DxlExecutionResult result) => completion.SetResult(result);
        internal void Fail(Exception exception) => completion.SetException(exception);
    }

    internal static class DxlLog
    {
        public static void Write(IIntegrationLogger logger, IntegrationLogLevel level, string eventName, Guid id, string metadata)
        {
            try { logger.Write(level, eventName, id, metadata); }
            catch (Exception) { /* Log sinks must not strand a worker, its gate or COM reference. */ }
        }
    }
}
