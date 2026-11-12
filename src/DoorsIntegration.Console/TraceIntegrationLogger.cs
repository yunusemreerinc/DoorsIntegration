using System;
using System.Diagnostics;
using DoorsIntegration.Core;

namespace DoorsIntegration.ConsoleApp
{
    public sealed class TraceIntegrationLogger : IIntegrationLogger, IDisposable
    {
        private readonly TraceSource source = new TraceSource("DoorsIntegration");

        public void Write(IntegrationLogLevel level, string eventName, Guid correlationId, string message)
        {
            var type = level == IntegrationLogLevel.Error ? TraceEventType.Error : TraceEventType.Information;
            source.TraceEvent(type, 0, "Utc={0:O}; Event={1}; CorrelationId={2:N}; {3}",
                DateTime.UtcNow, eventName, correlationId, message);
            source.Flush();
        }

        public void Dispose() => source.Close();
    }
}
