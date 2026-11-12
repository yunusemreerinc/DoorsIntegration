using System;
using DoorsIntegration.Core;
using DoorsIntegration.Com;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Wpf.Services
{
    public sealed class ClientTools
    {
        public ClientTools(IModuleService modules, IDxlExecutor dxl, IClientReview review, ProcessingStorageOptions storage, DoorsBatchServiceFactory batches,
            DxlRunLimitAutomationSettings runLimits = null, IDoorsClientProcessLocator processes = null)
        { Modules = modules; Dxl = dxl; Review = review; Storage = storage; Batches = batches; RunLimits = runLimits; Processes = processes; }
        public DxlRunLimitAutomationSettings RunLimits { get; }
        public IDoorsClientProcessLocator Processes { get; }
        public IModuleService Modules { get; }
        public IDxlExecutor Dxl { get; }
        public IClientReview Review { get; }
        public ProcessingStorageOptions Storage { get; }
        public DoorsBatchServiceFactory Batches { get; }
        public static IDoorsSmokeSuite CreateSmoke(IIntegrationLogger logger)
        {
            var gate = DoorsOperationGate.Shared; var sessions = new ComDxlSessionFactory(new DoorsComSessionFactory());
            var workspace = new DxlWorkspaceFactory();
            var executor = new DxlExecutor(sessions, new Utf8DxlScriptLoader(), workspace, logger, gate);
            return new DoorsSmokeSuite(new DoorsSmokeEnvironmentInspector(new ComRegistrationInspector()),
                new DoorsSmokeProbe(sessions, new ReadOnlyProbeProtocol(), workspace, gate,
                    new DxlHierarchyReader(executor, new DiscoveryScriptOptions(), logger), new DxlLinkReader(executor, new LinkScriptOptions(), logger)), logger);
        }
    }
}
