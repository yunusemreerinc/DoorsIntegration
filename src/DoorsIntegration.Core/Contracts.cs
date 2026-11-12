using System;

namespace DoorsIntegration.Core
{
    public interface IDoorsDiagnostics
    {
        ProbeResult Probe(DoorsVersion target, Guid correlationId,
            DoorsConnectionMode mode = DoorsConnectionMode.Activate);
    }

    // Implementations must return a fixed, database-read-only diagnostic script.
    public interface IDxlProbeProtocol
    {
        string CreateScript(DoorsVersion target, Guid correlationId);
        void ValidateResponse(Guid correlationId, string response);
    }

    public interface IIntegrationLogger
    {
        void Write(IntegrationLogLevel level, string eventName, Guid correlationId, string message);
    }

    public enum IntegrationLogLevel { Information, Error }

    public sealed class ProbeResult
    {
        public ProbeResult(DoorsVersion target, Guid correlationId)
        {
            DoorsVersions.Format(target);
            Target = target;
            CorrelationId = correlationId;
        }

        // Configured compatibility profile, not a detected client version.
        public DoorsVersion Target { get; }
        public Guid CorrelationId { get; }
    }
}
