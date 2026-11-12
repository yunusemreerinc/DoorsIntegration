using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("Integration"), TestCategory("NativeBaselineSetReal"), DoNotParallelize]
    public class NativeBaselineSetIntegrationTests
    {
        private static INativeBaselineSetAcceptanceEnvironment EnvironmentFor(DoorsVersion target)
        {
            if (Environment.GetEnvironmentVariable("DOORS_TEST_MODE") != "real-native-sets") Assert.Inconclusive("NOT_RUN: real DOORS and a reviewed native baseline-set test environment are not enabled. Mock is not native validation.");
            string typeName = Environment.GetEnvironmentVariable("DOORS_NATIVE_SET_TEST_ENVIRONMENT_TYPE");
            if (string.IsNullOrWhiteSpace(typeName)) Assert.Inconclusive("NOT_RUN: supply an assembly-qualified INativeBaselineSetAcceptanceEnvironment fixture for this exact release/build.");
            var type = Type.GetType(typeName, true); var fixture = Activator.CreateInstance(type) as INativeBaselineSetAcceptanceEnvironment;
            Assert.IsNotNull(fixture); if (fixture.Target != target) Assert.Inconclusive("NOT_RUN: fixture targets another DOORS family.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(fixture.Evidence)); return fixture;
        }
        [DataTestMethod, DataRow(DoorsVersion.V95), DataRow(DoorsVersion.V97)]
        public async Task RealReadOnlyDefinitionSetAndModuleMetadata(DoorsVersion target)
        {
            var f = EnvironmentFor(target); Assert.AreEqual(BaselineMetadataSource.NativeDxl, f.Adapter.Source);
            var result = await f.Adapter.InspectAsync(f.InspectionRequest, CancellationToken.None);
            Assert.IsTrue(result.Complete, result.Reason); StringAssert.StartsWith(result.ClientVersion, DoorsVersions.Format(target));
            Assert.IsTrue(result.Definitions.All(d => d.Modules.Select(m => m.ModuleId).Distinct().Count() == d.Modules.Count));
            Assert.IsTrue(result.Sets.GroupBy(s => s.DefinitionId).All(g => g.Count(s => s.IsOpen) <= 1));
        }
        [DataTestMethod, DataRow(DoorsVersion.V95), DataRow(DoorsVersion.V97)]
        public async Task ExplicitTestOnlyMajorMinorPopulationCloseAndLinkVerification(DoorsVersion target)
        {
            var f = EnvironmentFor(target);
            if (Environment.GetEnvironmentVariable("DOORS_NATIVE_SET_TEST_WRITE_ACK") != "CREATE_NATIVE_BASELINE_SET_IN_TEST_FOLDER") Assert.Inconclusive("NOT_RUN: test-folder native writes were not explicitly authorized. Interactive service approvals are additionally required.");
            Assert.IsTrue(f.ConfirmedService.WritesValidated); Assert.IsTrue(f.TestModules.Length > 0); Assert.IsFalse(string.IsNullOrWhiteSpace(f.UnusedDefinitionName));
            string folder = f.InspectionRequest.FolderPath;
            Func<BaselineSetRequest, Task<BaselineSetJob>> run = async request =>
            { var plan = await f.ConfirmedService.PreflightAsync(request); Assert.IsTrue(plan.Ready, string.Join("\n", plan.Problems)); var job = await f.ConfirmedService.ExecuteAsync(plan); Assert.AreEqual(BaselineMetadataSource.NativeDxl, job.Source); Assert.IsTrue(job.ExplicitApproval && job.SecondRealApproval); Assert.AreEqual(BaselineSetJobStatus.Succeeded, job.Status, job.Reason); return job; };
            var definition = await run(new BaselineSetRequest(BaselineSetAction.CreateDefinition, folder, name: f.UnusedDefinitionName, modules: f.TestModules, target: target));
            foreach (var action in new[] { BaselineSetAction.CreateMajorSet, BaselineSetAction.CreateMinorSet })
            {
                var created = await run(new BaselineSetRequest(action, folder, definition.DefinitionId, suffix: "ACCEPT", annotation: "Controlled native acceptance; retained for audit", target: target));
                var added = await run(new BaselineSetRequest(BaselineSetAction.AddModuleBaselines, folder, definition.DefinitionId, created.SetId, modules: f.TestModules, target: target));
                Assert.AreEqual(0, added.MissingModules.Count); Assert.IsTrue(added.LinkVerification.Verified);
                var closed = await run(new BaselineSetRequest(BaselineSetAction.CloseSet, folder, definition.DefinitionId, created.SetId, target: target)); Assert.IsTrue(closed.LinkVerification.Verified);
            }
            // Retain native artifacts and audit. No deletion or automatic rollback.
        }
    }
}
