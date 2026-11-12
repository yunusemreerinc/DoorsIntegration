using System;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    public sealed class DesktopOperationResult
    {
        internal DesktopOperationResult(Guid correlationId, string message, string script = null,
            IntegrationFailure failure = null)
        {
            CorrelationId = correlationId;
            Message = message;
            Script = script;
            Failure = failure;
        }

        public Guid CorrelationId { get; }
        public string Message { get; }
        public string Script { get; }
        public IntegrationFailure Failure { get; }
        public bool Succeeded => Failure == null;
    }

    // UI-independent use cases. The entire COM lifecycle runs inside the STA delegate.
    public sealed class DesktopOperations
    {
        private readonly IDoorsDiagnostics diagnostics;
        private readonly IDxlProbeProtocol protocol;
        private readonly IStaOperationRunner runner;
        private readonly IntegrationErrorHandler errors;
        private readonly IIntegrationLogger logger;

        public DesktopOperations(IDoorsDiagnostics diagnostics, IDxlProbeProtocol protocol,
            IStaOperationRunner runner, IntegrationErrorHandler errors, IIntegrationLogger logger)
        {
            this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
            this.errors = errors ?? throw new ArgumentNullException(nameof(errors));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public DesktopOperationResult CreatePreview(DoorsVersion target)
        {
            var id = Guid.NewGuid();
            try
            {
                var script = protocol.CreateScript(target, id);
                logger.Write(IntegrationLogLevel.Information, "PreviewCreated", id,
                    "TargetProfile=" + DoorsVersions.Format(target));
                return new DesktopOperationResult(id, "Preview ready. DOORS was not contacted.", script);
            }
            catch (Exception ex) { return Failed(ex, id); }
        }

        public async Task<DesktopOperationResult> RunProbeAsync(DoorsVersion target,
            DoorsConnectionMode mode = DoorsConnectionMode.Activate)
        {
            var id = Guid.NewGuid();
            try
            {
                DoorsVersions.Format(target);
                await runner.RunAsync(() => diagnostics.Probe(target, id, mode)).ConfigureAwait(false);
                return new DesktopOperationResult(id,
                    "Read-only probe completed for target profile " + DoorsVersions.Format(target) + ".");
            }
            catch (Exception ex) { return Failed(ex, id); }
        }

        private DesktopOperationResult Failed(Exception exception, Guid id)
        {
            var failure = errors.Handle(exception, id);
            return new DesktopOperationResult(id, failure.Code + ": " + failure.Message, failure: failure);
        }
    }
}
