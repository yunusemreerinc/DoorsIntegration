using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    public sealed class DxlPingResult
    {
        internal DxlPingResult(Guid id, TimeSpan duration) { RequestId = id; Duration = duration; }
        public Guid RequestId { get; }
        public TimeSpan Duration { get; }
        public string Payload => "DXL_PING_OK";
    }

    // Uses the existing STA executor and token-bound transport protocol, not the discovery parser.
    public sealed class DxlPingService
    {
        private readonly IDxlExecutor executor;
        private readonly IDiscoveryDebugStore store;
        private readonly IIntegrationLogger logger;
        public DxlPingService(IDxlExecutor executor, IDiscoveryDebugStore store, IIntegrationLogger logger)
        { this.executor = executor ?? throw new ArgumentNullException(nameof(executor)); this.store = store ?? throw new ArgumentNullException(nameof(store)); this.logger = logger ?? throw new ArgumentNullException(nameof(logger)); }

        public async Task<DxlPingResult> PingAsync(DoorsVersion target = DoorsVersion.V97,
            DoorsConnectionMode mode = DoorsConnectionMode.AttachExisting, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new DxlExecutionRequest(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "dxl", "discovery-ping.dxl"),
                DxlInvocationMethod.RunString, timeout: timeout, connectionMode: mode, target: target);
            var capture = new DiscoveryDebugCapture(store, logger, Guid.Empty);
            request.DebugCapture = capture;
            var elapsed = Stopwatch.StartNew();
            var operation = executor.Start(request);
            try
            {
                using (var result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Result must first pass the executor's fresh GUID frame validation.
                    // Plain DXL_PING_OK, old GUID frames, whitespace and case variants cannot pass.
                    if (result.IsFile || !string.Equals(result.Text, "DXL_PING_OK", StringComparison.Ordinal))
                        throw new IntegrationException(IntegrationErrorCode.DxlProtocolFailure, "Independent DXL ping returned an unexpected payload.");
                    return new DxlPingResult(result.CorrelationId, elapsed.Elapsed);
                }
            }
            catch (DxlExecutionWaitException)
            {
                // Soft cancellation cannot stop COM. Keep ownership until the worker really completes.
                _ = operation.Completion.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else { var observed = task.Exception; }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            catch (Exception ex)
            {
                capture.Failed(ex, "IndependentPingFailed");
                throw new IntegrationException((ex as IntegrationException)?.Code ?? IntegrationErrorCode.Unexpected,
                    "Independent DXL ping failed. DebugRequestId=" + operation.CorrelationId.ToString("N") +
                    "; ProtectedCapture=" + (capture.Saved ? "Saved" : "Unavailable"), ex);
            }
        }
    }
}
