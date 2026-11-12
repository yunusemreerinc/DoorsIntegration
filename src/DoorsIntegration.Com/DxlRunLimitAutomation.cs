using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    public sealed class DxlRunLimitAutomation : IDxlRunLimitAutomation
    {
        private readonly DxlRunLimitAutomationSettings settings;
        private readonly IIntegrationLogger logger;
        private readonly IRunLimitPromptBackend backend;
        private int workerActive;
        internal bool HasActiveWorker => Volatile.Read(ref workerActive) != 0;

        public DxlRunLimitAutomation(DxlRunLimitAutomationSettings settings, IIntegrationLogger logger)
            : this(settings, logger, new WindowsRunLimitPromptBackend()) { }

        internal DxlRunLimitAutomation(DxlRunLimitAutomationSettings settings, IIntegrationLogger logger, IRunLimitPromptBackend backend)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public IDisposable Watch(Guid correlationId, Func<bool> mayContinue)
        {
            if (mayContinue == null) throw new ArgumentNullException(nameof(mayContinue));
            var snapshot = settings.Capture();
            if (!snapshot.Enabled || snapshot.Client == null) return new NoDxlRunLimitAutomation().Watch(correlationId, mayContinue);
            if (Interlocked.CompareExchange(ref workerActive, 1, 0) != 0)
            {
                Log("DxlRunLimitAutomationUnavailable", correlationId, "Previous UI Automation worker has not returned; manual handling required.");
                return new NoDxlRunLimitAutomation().Watch(correlationId, mayContinue);
            }
            var scope = new Scope();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => Run(scope, snapshot, correlationId, mayContinue, ready))
            { IsBackground = true, Name = "DOORS run-limit UI Automation MTA" };
            thread.SetApartmentState(ApartmentState.MTA);
            try { thread.Start(); }
            catch
            {
                Interlocked.Exchange(ref workerActive, 0);
                scope.Dispose();
                Log("DxlRunLimitAutomationUnavailable", correlationId, "Cannot start UI Automation worker; manual handling required.");
                return scope;
            }
            // On the DXL worker, not the desktop UI thread. A blocked provider must not block COM dispatch indefinitely.
            if (!ready.Task.Wait(TimeSpan.FromSeconds(2)) || !ready.Task.Result)
            {
                scope.Dispose();
                Log("DxlRunLimitAutomationUnavailable", correlationId, "Initial inspection unavailable or an existing prompt is open; manual handling required.");
            }
            else scope.Arm();
            return scope;
        }

        private void Run(Scope scope, DxlRunLimitAutomationSnapshot snapshot, Guid id,
            Func<bool> mayContinue, TaskCompletionSource<bool> ready)
        {
            try
            {
                if (!settings.IsCurrent(snapshot) || scope.Stopped || !mayContinue()) { ready.TrySetResult(false); return; }
                // Do not adopt a prompt that was already present before this COM dispatch.
                var initial = backend.Scan(snapshot.Client);
                if (initial.Any(p => RunLimitPromptMatcher.Match(p) != null)) { ready.TrySetResult(false); return; }
                ready.TrySetResult(true);
                while (!scope.Armed && !scope.Stopped) Thread.Sleep(10);
                var policy = new RunLimitPromptPolicy();
                var reported = new HashSet<string>(StringComparer.Ordinal);
                Func<bool> mayAct = () => !scope.Stopped && settings.IsCurrent(snapshot) && mayContinue();
                while (mayAct())
                {
                    var prompts = backend.Scan(snapshot.Client);
                    if (!mayAct()) break;
                    foreach (var prompt in prompts.Where(p => p.Modal &&
                        (p.Text ?? string.Empty).IndexOf("DXL", StringComparison.OrdinalIgnoreCase) >= 0 && RunLimitPromptMatcher.Match(p) == null))
                        if (reported.Add(prompt.Identity)) Log("DxlRunLimitPromptUnrecognized", id,
                            "ProcessId=" + snapshot.Client.ProcessId + "; Dialog structure or action patterns unsupported; manual handling required.");
                    if (policy.Tick(prompts, prompt => backend.TryDouble(snapshot.Client, prompt, mayAct)))
                        Log("DxlRunLimitDoubleSelected", id, "ProcessId=" + snapshot.Client.ProcessId + "; Count=" + policy.Doublings);
                    if (policy.Stopped)
                    {
                        Log("DxlRunLimitAutomationStopped", id, "Extension budget exhausted, ambiguous dialog or action not confirmed; manual handling required.");
                        break;
                    }
                    Thread.Sleep(150);
                }
            }
            catch (Exception ex)
            {
                Log("DxlRunLimitAutomationStopped", id, "ExceptionType=" + ex.GetType().FullName + "; HResult=0x" + ex.HResult.ToString("X8") + "; ManualHandlingRequired=True");
            }
            finally { ready.TrySetResult(false); Interlocked.Exchange(ref workerActive, 0); }
        }

        private void Log(string name, Guid id, string message)
        {
            try { logger.Write(IntegrationLogLevel.Information, name, id, message); }
            catch { /* Logging cannot strand a COM worker or cause a second UI action. */ }
        }

        private sealed class Scope : IDisposable
        {
            private int stopped;
            private int armed;
            public bool Stopped => Volatile.Read(ref stopped) != 0;
            public bool Armed => Volatile.Read(ref armed) != 0;
            public void Arm() { Interlocked.Exchange(ref armed, 1); }
            // Never join/abort a UIA call: providers can block just like COM. The next action checks Stopped.
            public void Dispose() { Interlocked.Exchange(ref stopped, 1); }
        }
    }
}
