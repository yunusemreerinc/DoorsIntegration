using System;
using System.Threading;

namespace DoorsIntegration.Core
{
    public interface IDxlExecutionSession : IDisposable
    {
        void RunString(string script);
        void RunFile(string absolutePath);
        void SetResult(string value);
        string ReadResult();
    }

    public interface IDxlSessionFactory
    {
        IDxlExecutionSession Open(DoorsConnectionMode mode);
    }

    public interface IDoorsOperationGate
    {
        IDisposable Enter();
        bool IsBusy { get; }
    }

    // Shared by the fixed diagnostic and the general executor in this process.
    public sealed class DoorsOperationGate : IDoorsOperationGate
    {
        public static DoorsOperationGate Shared { get; } = new DoorsOperationGate();
        private int busy;
        public bool IsBusy => Volatile.Read(ref busy) != 0;

        public IDisposable Enter()
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                throw new IntegrationException(IntegrationErrorCode.DoorsOperationBusy,
                    "Önceki DOORS işlemi veya COM temizliği sürüyor. Yeni işlem başlatılmadı.");
            return new Lease(this);
        }

        private sealed class Lease : IDisposable
        {
            private DoorsOperationGate owner;
            public Lease(DoorsOperationGate owner) { this.owner = owner; }
            public void Dispose()
            {
                var released = Interlocked.Exchange(ref owner, null);
                if (released != null) Interlocked.Exchange(ref released.busy, 0);
            }
        }
    }
}
