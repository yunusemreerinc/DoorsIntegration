using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    public sealed class DoorsSmokeEnvironmentInspector : IDoorsSmokeEnvironmentInspector
    {
        private readonly IComRegistrationInspector registration;
        public DoorsSmokeEnvironmentInspector(IComRegistrationInspector registration) { this.registration = registration ?? throw new ArgumentNullException(nameof(registration)); }
        public Task<DoorsSmokeEnvironment> InspectAsync(DoorsVersion target, CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested(); var x86Started = DateTime.UtcNow; var clock = Stopwatch.StartNew();
            var x86 = registration.Inspect(ComArchitecture.X86); double x86Ms = clock.Elapsed.TotalMilliseconds;
            var x64Started = DateTime.UtcNow; clock.Restart(); var x64 = registration.Inspect(ComArchitecture.X64); double x64Ms = clock.Elapsed.TotalMilliseconds;
            var processStarted = DateTime.UtcNow; clock.Restart(); int sessionId; using (var current = Process.GetCurrentProcess()) sessionId = current.SessionId;
            var clients = new List<SmokeClientProcess>(); bool complete = true;
            var processes = Process.GetProcessesByName("doors");
            try
            {
                foreach (var process in processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int pid = process.Id, observedSession = -1; DateTime? started = null; string path = null, fileVersion = null, machine = null;
                    int? major = null, minor = null; string status = "Observed";
                    try
                    {
                        observedSession = process.SessionId; if (observedSession != sessionId) continue;
                        started = process.StartTime.ToUniversalTime(); path = process.MainModule.FileName;
                        var binary = new ServerBinaryInspector().Inspect(path); fileVersion = binary.Version; major = binary.Major; minor = binary.Minor; machine = binary.Machine;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                    { complete = false; status = "Unavailable:" + ex.GetType().Name; }
                    clients.Add(new SmokeClientProcess(pid, observedSession, started, path, fileVersion, major, minor, machine, status));
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            return new DoorsSmokeEnvironment(new DoorsCompatibilityReport(target, Environment.Is64BitOperatingSystem, Environment.Is64BitProcess,
                Thread.CurrentThread.GetApartmentState().ToString(), new[] { x86, x64 }), clients, complete, sessionId, x86Ms, x64Ms, clock.Elapsed.TotalMilliseconds,
                x86Started, x64Started, processStarted);
        }, cancellationToken);
    }
}
