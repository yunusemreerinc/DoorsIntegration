using System.Collections.Generic;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    // External, release-specific read-only fixture. No write, save or repair contract exists here.
    public interface IChangeImpactAcceptanceEnvironment
    {
        DoorsVersion Target { get; }
        string ExactClientBuild { get; }
        string ManualAndRuntimeEvidence { get; }
        string DatabaseIdentity { get; }
        IChangeImpactAnalysisService Service { get; }
        IReadOnlyDictionary<string, ChangeImpactRequest> ReadOnlyScenarios { get; }
    }
}
