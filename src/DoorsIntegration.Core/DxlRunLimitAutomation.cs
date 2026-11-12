using System;

namespace DoorsIntegration.Core
{
    // Process identity is captured explicitly by the host; a PID alone can be reused by Windows.
    public sealed class DoorsClientProcess
    {
        public DoorsClientProcess(int processId, long startTimeUtcTicks, string executablePath, int sessionId)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("Executable path is required.", nameof(executablePath));
            ProcessId = processId;
            StartTimeUtcTicks = startTimeUtcTicks;
            ExecutablePath = executablePath;
            SessionId = sessionId;
        }
        public int ProcessId { get; }
        public long StartTimeUtcTicks { get; }
        public string ExecutablePath { get; }
        public int SessionId { get; }
    }

    public sealed class DxlRunLimitAutomationSettings
    {
        private readonly object sync = new object();
        private DoorsClientProcess client;
        private bool enabled;
        private int revision;
        public bool Enabled
        {
            get { lock (sync) return enabled; }
            set
            {
                lock (sync)
                {
                    if (value && client == null) throw new InvalidOperationException("Bind a running DOORS client first.");
                    if (enabled != value) { enabled = value; revision++; }
                }
            }
        }
        public DoorsClientProcess Client { get { lock (sync) return client; } }
        // A finite guard against repeatedly extending an infinite loop. Each DXL call starts a new budget.
        public const int MaximumDoublingsPerCall = 4;
        public void Bind(DoorsClientProcess value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            lock (sync) { client = value; enabled = false; revision++; }
        }
        public DxlRunLimitAutomationSnapshot Capture()
        {
            lock (sync) return new DxlRunLimitAutomationSnapshot(client, enabled, revision);
        }
        public bool IsCurrent(DxlRunLimitAutomationSnapshot snapshot)
        {
            lock (sync) return snapshot != null && enabled && snapshot.Enabled && revision == snapshot.Revision;
        }
    }

    public sealed class DxlRunLimitAutomationSnapshot
    {
        internal DxlRunLimitAutomationSnapshot(DoorsClientProcess client, bool enabled, int revision)
        { Client = client; Enabled = enabled; Revision = revision; }
        public DoorsClientProcess Client { get; }
        public bool Enabled { get; }
        internal int Revision { get; }
    }

    public interface IDoorsClientProcessLocator
    {
        // Must fail if zero/multiple clients exist or the process identity cannot be established.
        DoorsClientProcess FindSingleClient();
    }

    public interface IDxlRunLimitAutomation
    {
        // Called only around an actual integration runStr/runFile dispatch, never for arbitrary DOORS work.
        IDisposable Watch(Guid correlationId, Func<bool> mayContinue);
    }

    public sealed class NoDxlRunLimitAutomation : IDxlRunLimitAutomation
    {
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public IDisposable Watch(Guid correlationId, Func<bool> mayContinue) => new Lease();
    }
}
