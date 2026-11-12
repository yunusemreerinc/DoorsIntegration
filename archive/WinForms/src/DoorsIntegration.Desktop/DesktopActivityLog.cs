using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    public sealed class DesktopLogEntry
    {
        internal DesktopLogEntry(long sequence, IntegrationLogLevel level, string eventName, Guid id, string message)
        {
            Sequence = sequence;
            TimestampUtc = DateTime.UtcNow;
            Level = level;
            EventName = eventName;
            CorrelationId = id;
            Message = message;
        }

        public long Sequence { get; }
        public DateTime TimestampUtc { get; }
        public IntegrationLogLevel Level { get; }
        public string EventName { get; }
        public Guid CorrelationId { get; }
        public string Message { get; }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "{0:O}  {1}  {2}  Request={3:N}  {4}", TimestampUtc, Level, EventName, CorrelationId, Message);
    }

    public sealed class DesktopActivityLog : IIntegrationLogger, IDisposable
    {
        private const int Capacity = 500;
        private readonly object gate = new object();
        private readonly Queue<DesktopLogEntry> entries = new Queue<DesktopLogEntry>();
        private readonly TraceSource trace = new TraceSource("DoorsIntegration.Desktop");
        private long sequence;
        private bool disposed;

        public void Write(IntegrationLogLevel level, string eventName, Guid correlationId, string message)
        {
            lock (gate)
            {
                if (disposed) return;
                var entry = new DesktopLogEntry(++sequence, level, eventName, correlationId, message);
                entries.Enqueue(entry);
                while (entries.Count > Capacity) entries.Dequeue();
                try
                {
                    trace.TraceEvent(level == IntegrationLogLevel.Error ? TraceEventType.Error : TraceEventType.Information,
                        0, entry.ToString());
                    trace.Flush();
                }
                catch (Exception)
                {
                    // Activity stays available if an optional trace listener is misconfigured.
                }
            }
        }

        public DesktopLogEntry[] ReadAfter(long lastSequence)
        {
            lock (gate) return entries.Where(entry => entry.Sequence > lastSequence).ToArray();
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                try { trace.Close(); }
                catch (Exception) { /* A trace listener must not prevent application shutdown. */ }
            }
        }
    }
}
