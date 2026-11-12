using System.Threading;

namespace DoorsIntegration.Core
{
    public sealed class DxlWatchdogSettings
    {
        public const int EnabledCycleLimit = 1000000;
        private int enabled = 1;

        public bool Enabled
        {
            get => Volatile.Read(ref enabled) != 0;
            set => Interlocked.Exchange(ref enabled, value ? 1 : 0);
        }
    }
}
