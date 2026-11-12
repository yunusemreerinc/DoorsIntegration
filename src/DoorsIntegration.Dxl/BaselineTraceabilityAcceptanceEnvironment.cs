using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    // Controlled site fixture; the shipped host does not manufacture release/runtime evidence.
    public interface IBaselineTraceabilityAcceptanceEnvironment
    {
        DoorsVersion Target { get; }
        string ExactClientBuild { get; }
        string DatabaseIdentity { get; }
        string ManualAndRuntimeEvidence { get; }
        IBaselineSetVerificationService Service { get; }
        BaselineSetVerificationRequest ReadOnlyRequest { get; }
    }
}
