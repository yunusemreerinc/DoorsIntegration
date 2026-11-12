using System;
using System.Linq;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("Integration"), TestCategory("BaselineVerificationReal"), DoNotParallelize]
    public class BaselineVerificationIntegrationTests
    {
        [DataTestMethod, DataRow(DoorsVersion.V95), DataRow(DoorsVersion.V97)]
        public async Task ReadOnlyNativeSetMembershipActualEndpointVersionsAndEcho(DoorsVersion target)
        {
            if (Environment.GetEnvironmentVariable("DOORS_TEST_MODE") != "real-baseline-verification") Assert.Inconclusive("NOT_RUN: real release-specific read-only baseline traceability environment is not configured; Mock does not validate COM/DXL.");
            string typeName = Environment.GetEnvironmentVariable("DOORS_BASELINE_VERIFICATION_ENVIRONMENT_TYPE");
            if (string.IsNullOrWhiteSpace(typeName)) Assert.Inconclusive("NOT_RUN: supply an assembly-qualified IBaselineTraceabilityAcceptanceEnvironment with reviewed manual/runtime evidence.");
            var fixture = Activator.CreateInstance(Type.GetType(typeName, true)) as IBaselineTraceabilityAcceptanceEnvironment; Assert.IsNotNull(fixture);
            if (fixture.Target != target) Assert.Inconclusive("NOT_RUN: fixture targets another DOORS family.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(fixture.ManualAndRuntimeEvidence)); Assert.IsTrue(fixture.ReadOnlyRequest.ReadOnly); StringAssert.StartsWith(fixture.ExactClientBuild, DoorsVersions.Format(target));
            var result = await fixture.Service.VerifyAsync(fixture.ReadOnlyRequest);
            Assert.AreEqual(BaselineMetadataSource.NativeDxl, result.Snapshot.Context.Source); Assert.AreEqual(fixture.DatabaseIdentity, result.Snapshot.Context.EnvironmentId); Assert.AreEqual(fixture.ExactClientBuild, result.Snapshot.Context.ClientVersion);
            Assert.IsTrue(result.Snapshot.Context.MetadataComplete); Assert.AreNotEqual(BaselineVerificationStatus.Unknown, result.Status);
            Assert.IsTrue(result.Links.Any(l => l.Link != null), "Prepare a known nonempty read-only baseline link fixture.");
            foreach (var link in result.Links.Where(l => l.CountsAsValid)) { Assert.IsNotNull(link.Link.Identity); Assert.IsTrue(link.Link.SourceVersion.IdentityVerified); Assert.IsTrue(link.Link.TargetVersion.IdentityVerified); StringAssert.Contains(link.Link.Evidence, "Native"); }
            // NO mutation method, write confirmation, save, baseline create, close-set or repair call.
        }
    }
}
