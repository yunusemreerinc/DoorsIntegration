using System;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    public sealed class DoorsEnvironmentDiagnostics : IDoorsEnvironmentDiagnostics
    {
        private readonly IComRegistrationInspector registration;
        public DoorsEnvironmentDiagnostics() : this(new ComRegistrationInspector()) { }
        public DoorsEnvironmentDiagnostics(IComRegistrationInspector registration)
        {
            this.registration = registration ?? throw new ArgumentNullException(nameof(registration));
        }

        public DoorsCompatibilityReport Inspect(DoorsVersion target)
        {
            DoorsVersions.Format(target);
            // Read-only registry/file metadata. No ProgID resolution, activation, ROT or DXL calls.
            return new DoorsCompatibilityReport(target, Environment.Is64BitOperatingSystem, Environment.Is64BitProcess,
                Thread.CurrentThread.GetApartmentState().ToString(), new[]
                {
                    registration.Inspect(ComArchitecture.X86), registration.Inspect(ComArchitecture.X64)
                });
        }
    }
}
