using System;
using System.Threading.Tasks;
using DoorsIntegration.Com;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("Integration"), TestCategory("DiscoveryDiagnosticsReal"), DoNotParallelize]
    public class DiscoveryDiagnosticsIntegrationTests
    {
        [DataTestMethod, DataRow(DoorsVersion.V95, "Ping"), DataRow(DoorsVersion.V97, "Ping"), DataRow(DoorsVersion.V95, "Discovery"), DataRow(DoorsVersion.V97, "Discovery")]
        public async Task ExplicitReadOnlyRealAcceptance(DoorsVersion version, string scenario)
        {
            if (Environment.GetEnvironmentVariable("DOORS_TEST_MODE") != "real-readonly" ||
                Environment.GetEnvironmentVariable("DOORS_DISCOVERY_DIAGNOSTICS_VERSION") != DoorsVersions.Format(version))
                Assert.Inconclusive("NOT_RUN: requires explicit real-readonly mode, matching 9.5/9.7 client, and DOORS_DISCOVERY_DIAGNOSTICS_VERSION. Mock is not COM validation.");
            var logger = new ExecutorTestLog();
            var executor = new DxlExecutor(new ComDxlSessionFactory(new DoorsComSessionFactory()), new Utf8DxlScriptLoader(), new DxlWorkspaceFactory(), logger,
                DoorsOperationGate.Shared, new NoDxlRunLimitAutomation(), new DxlWatchdogSettings(), DoorsStaExecutionQueue.Shared);
            var store = new ProtectedDiscoveryDebugStore();
            if (scenario == "Ping")
            {
                var result = await new DxlPingService(executor, store, logger).PingAsync(version);
                Assert.AreEqual("DXL_PING_OK", result.Payload);
            }
            else
            {
                string path = Environment.GetEnvironmentVariable("DOORS_DISCOVERY_DIAGNOSTICS_PATH");
                if (string.IsNullOrWhiteSpace(path)) Assert.Inconclusive("NOT_RUN: provide a known readable DOORS_DISCOVERY_DIAGNOSTICS_PATH.");
                var result = await new DxlHierarchyReader(executor, new DiscoveryScriptOptions(), logger, store).DiscoverAsync(DiscoveryKind.Hierarchy, new DiscoveryScope(path, recursive: false, target: version));
                Assert.IsFalse(result.IsTruncated);
                Assert.AreEqual(0, result.Issues.Count, "Review discovery warnings; do not treat an incomplete inventory as complete acceptance.");
            }
        }
    }
}
