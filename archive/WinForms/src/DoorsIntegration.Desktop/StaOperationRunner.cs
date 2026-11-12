using System;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    public interface IStaOperationRunner
    {
        Task<T> RunAsync<T>(Func<T> operation);
    }

    public sealed class StaOperationRunner : IStaOperationRunner
    {
        private int running;

        public Task<T> RunAsync<T>(Func<T> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                return Task.FromException<T>(new IntegrationException(IntegrationErrorCode.InvalidConfiguration,
                    "A desktop operation is already running. Wait for it to finish."));

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var worker = new Thread(() =>
                {
                    T result = default(T);
                    Exception failure = null;
                    try { result = operation(); }
                    catch (Exception ex) { failure = ex; }
                    finally { Interlocked.Exchange(ref running, 0); }

                    if (failure == null) completion.SetResult(result);
                    else completion.SetException(failure);
                })
                {
                    IsBackground = true,
                    Name = "DOORS integration STA"
                };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref running, 0);
                completion.SetException(ex);
            }
            return completion.Task;
        }
    }
}
