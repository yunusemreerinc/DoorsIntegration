using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DoorsIntegration.Dxl
{
    public interface IDoorsStaExecutionQueue
    {
        void Enqueue(Action action);
    }
    // Process-lifetime queue. Background STA never carries a COM object across apartment boundaries.
    // Actions own their own COM session and cleanup; a synchronous call is never forcefully interrupted.
    public sealed class DoorsStaExecutionQueue : IDoorsStaExecutionQueue
    {
        public static DoorsStaExecutionQueue Shared { get; } = new DoorsStaExecutionQueue();
        private readonly BlockingCollection<Action> pending = new BlockingCollection<Action>(1024);
        private DoorsStaExecutionQueue()
        {
            var thread = new Thread(() =>
            {
                foreach (var action in pending.GetConsumingEnumerable())
                    try { action(); } catch { /* Executor actions publish their own failures; keep the shared STA alive. */ }
            }) { IsBackground = true, Name = "DOORS shared COM/DXL STA queue" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        public void Enqueue(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (!pending.TryAdd(action)) throw new InvalidOperationException("DOORS STA queue capacity reached; no work was dispatched.");
        }
    }
}
