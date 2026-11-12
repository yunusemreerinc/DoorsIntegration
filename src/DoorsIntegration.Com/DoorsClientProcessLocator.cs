using System;
using System.Diagnostics;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    public sealed class DoorsClientProcessLocator : IDoorsClientProcessLocator
    {
        public DoorsClientProcess FindSingleClient()
        {
            var processes = Process.GetProcessesByName("doors");
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    Process found = null;
                    foreach (var process in processes)
                    {
                        if (process.SessionId != current.SessionId) continue;
                        if (found != null) throw new InvalidOperationException("Multiple DOORS clients are running in this Windows session. Keep one client open before binding automation.");
                        found = process;
                    }
                    if (found == null) throw new InvalidOperationException("No running doors.exe client was found in this Windows session. Start DOORS and sign in first.");
                    return new DoorsClientProcess(found.Id, found.StartTime.ToUniversalTime().Ticks,
                        found.MainModule.FileName, found.SessionId);
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        internal bool Matches(DoorsClientProcess expected)
        {
            if (expected == null) return false;
            var actual = FindSingleClient();
            return SameIdentity(actual, expected);
        }

        internal static bool SameIdentity(DoorsClientProcess actual, DoorsClientProcess expected) =>
            actual != null && expected != null && actual.ProcessId == expected.ProcessId &&
            actual.StartTimeUtcTicks == expected.StartTimeUtcTicks && actual.SessionId == expected.SessionId &&
            string.Equals(actual.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }
}
