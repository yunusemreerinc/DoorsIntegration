using System.Threading;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.ConfigurationManagement
{
    // Counts member-invocation attempts, not activations/releases. Wrapping preserves the same STA ownership.
    public sealed class DashboardComCallMeter : IDxlSessionFactory
    {
        private readonly IDxlSessionFactory inner; private long count;
        public DashboardComCallMeter(IDxlSessionFactory inner) { this.inner=inner; }
        public long Count => Interlocked.Read(ref count);
        public IDxlExecutionSession Open(DoorsConnectionMode mode) => new Session(inner.Open(mode),this);
        private sealed class Session : IDxlExecutionSession
        {
            private readonly IDxlExecutionSession inner; private readonly DashboardComCallMeter meter;
            internal Session(IDxlExecutionSession inner,DashboardComCallMeter meter) { this.inner=inner; this.meter=meter; }
            public void SetResult(string value) { Interlocked.Increment(ref meter.count); inner.SetResult(value); }
            public void RunString(string script) { Interlocked.Increment(ref meter.count); inner.RunString(script); }
            public void RunFile(string path) { Interlocked.Increment(ref meter.count); inner.RunFile(path); }
            public string ReadResult() { Interlocked.Increment(ref meter.count); return inner.ReadResult(); }
            public void Dispose() => inner.Dispose();
        }
    }
}
