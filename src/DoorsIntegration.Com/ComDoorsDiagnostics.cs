using System;
using System.Reflection;
using System.Runtime.InteropServices;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    public sealed class ComDoorsDiagnostics : IDoorsDiagnostics
    {
        private readonly IDxlProbeProtocol protocol;
        private readonly IIntegrationLogger logger;
        private readonly IDoorsComSessionFactory factory;
        private readonly IDoorsOperationGate gate;
        private readonly IDxlRunLimitAutomation runLimitAutomation;

        public ComDoorsDiagnostics(IDxlProbeProtocol protocol, IIntegrationLogger logger)
            : this(protocol, logger, new DoorsComSessionFactory()) { }

        public ComDoorsDiagnostics(IDxlProbeProtocol protocol, IIntegrationLogger logger,
            IDoorsComSessionFactory factory)
            : this(protocol, logger, factory, DoorsOperationGate.Shared) { }

        public ComDoorsDiagnostics(IDxlProbeProtocol protocol, IIntegrationLogger logger,
            IDoorsComSessionFactory factory, IDoorsOperationGate gate)
            : this(protocol, logger, factory, gate, new NoDxlRunLimitAutomation()) { }

        public ComDoorsDiagnostics(IDxlProbeProtocol protocol, IIntegrationLogger logger,
            IDoorsComSessionFactory factory, IDoorsOperationGate gate, IDxlRunLimitAutomation runLimitAutomation)
        {
            this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
            this.runLimitAutomation = runLimitAutomation ?? throw new ArgumentNullException(nameof(runLimitAutomation));
        }

        public ProbeResult Probe(DoorsVersion target, Guid correlationId,
            DoorsConnectionMode mode = DoorsConnectionMode.Activate)
        {
            DoorsVersions.Format(target);
            StaGuard.EnsureSta();
            var id = correlationId;
            var script = protocol.CreateScript(target, id);
            logger.Write(IntegrationLogLevel.Information, "ProbeStarted", id,
                "TargetProfile=" + DoorsVersions.Format(target) + "; ConnectionMode=" + mode);
            try
            {
                using (gate.Enter()) ExecuteProbe(script, id, mode);
                logger.Write(IntegrationLogLevel.Information, "ProbeCompleted", id, "Status=OK");
                return new ProbeResult(target, id);
            }
            catch (TargetInvocationException ex)
            {
                throw ComErrors.Translate(ex, "tanılaması");
            }
            catch (COMException ex)
            {
                throw ComErrors.Translate(ex, "tanılaması");
            }
        }

        private void ExecuteProbe(string script, Guid id, DoorsConnectionMode mode)
        {
            var session = factory.Connect(mode);
            Exception primaryFailure = null;
            try
            {
                using (runLimitAutomation.Watch(id, () => true)) session.RunString(script);
                protocol.ValidateResponse(id, session.ReadResult());
            }
            catch (Exception ex)
            {
                primaryFailure = ex;
                throw;
            }
            finally
            {
                try { session.Dispose(); }
                catch (Exception) when (primaryFailure != null)
                {
                    // Cleanup must not replace the error that caused this operation to fail.
                    logger.Write(IntegrationLogLevel.Error, "ComCleanupFailed", id, "Status=Failed");
                }
            }
        }

    }
}
