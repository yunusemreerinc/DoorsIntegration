using System;
using System.Linq;
using DoorsIntegration.Core;

namespace DoorsIntegration.Com
{
    // Registry/file metadata only. Never activates DOORS or claims the active session's executable version.
    public sealed class ComBaselineJobAuditContextProvider : IBaselineJobAuditContextProvider
    {
        private readonly IComRegistrationInspector registrations;
        public ComBaselineJobAuditContextProvider(IComRegistrationInspector registrations) { this.registrations = registrations ?? throw new ArgumentNullException(nameof(registrations)); }
        public BaselineJobAuditContext Capture(BulkBaselineDryRunPlan plan, BaselineMetadataSource source)
        {
            var context = new BaselineJobAuditContextProvider().Capture(plan, source);
            if (source != BaselineMetadataSource.NativeDxl) return context;
            try
            {
                var architecture = Environment.Is64BitProcess ? ComArchitecture.X64 : ComArchitecture.X86;
                var registration = registrations.Inspect(architecture);
                if (registration.State == ComRegistrationState.Registered && !string.IsNullOrWhiteSpace(registration.FileVersion))
                    return new BaselineJobAuditContext(context.Operator, context.DatabaseIdentifier, context.EnvironmentIdentifier + " / process=" + architecture,
                        registration.FileVersion, "RegisteredBinaryOnly; architecture=" + architecture + "; requested=" + DoorsVersions.Format(plan.Request.Target) + "; active session executable version NOT_VERIFIED");
            }
            catch (Exception) { /* Absence/unreadable metadata is explicit, never replaced with an invented native version. */ }
            return context;
        }
    }
}
