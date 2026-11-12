using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    // Site-supplied controlled test fixture. No default implementation enables native writes.
    // Use release/build-specific validated safety/link providers and interactive confirmation.
    public interface INativeBaselineSetAcceptanceEnvironment
    {
        DoorsVersion Target { get; }
        string Evidence { get; }
        BaselineSetRequest InspectionRequest { get; }
        IBaselineSetAdapter Adapter { get; }
        INativeBaselineSetService ConfirmedService { get; }
        BaselineModuleReference[] TestModules { get; }
        string UnusedDefinitionName { get; }
    }
}
