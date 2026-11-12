using System;
using System.Linq;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("Integration"), TestCategory("ChangeImpactReal"), DoNotParallelize]
    public class ChangeImpactIntegrationTests
    {
        public TestContext TestContext { get; set; }
        [DataTestMethod]
        [DataRow(DoorsVersion.V95, "BaselineLoad"), DataRow(DoorsVersion.V97, "BaselineLoad")]
        [DataRow(DoorsVersion.V95, "Current"), DataRow(DoorsVersion.V97, "Current")]
        [DataRow(DoorsVersion.V95, "Deleted"), DataRow(DoorsVersion.V97, "Deleted")]
        [DataRow(DoorsVersion.V95, "RichText"), DataRow(DoorsVersion.V97, "RichText")]
        [DataRow(DoorsVersion.V95, "HistoricalLinks"), DataRow(DoorsVersion.V97, "HistoricalLinks")]
        [DataRow(DoorsVersion.V95, "BaselineSets"), DataRow(DoorsVersion.V97, "BaselineSets")]
        [DataRow(DoorsVersion.V95, "Permissions"), DataRow(DoorsVersion.V97, "Permissions")]
        [DataRow(DoorsVersion.V95, "LargeModule"), DataRow(DoorsVersion.V97, "LargeModule")]
        public async Task NativeReadOnlyReleaseSpecificImpact(DoorsVersion version, string scenario)
        {
            if (Environment.GetEnvironmentVariable("DOORS_TEST_MODE") != "real-change-impact") Assert.Inconclusive("NOT_RUN: actual release-specific read-only DOORS environment is absent/disabled; Mock is not COM/DXL evidence.");
            string type = Environment.GetEnvironmentVariable("DOORS_CHANGE_IMPACT_ENVIRONMENT_TYPE");
            if (string.IsNullOrWhiteSpace(type)) Assert.Inconclusive("NOT_RUN: provide an assembly-qualified IChangeImpactAcceptanceEnvironment with reviewed native API/runtime evidence.");
            var fixture = Activator.CreateInstance(Type.GetType(type, true)) as IChangeImpactAcceptanceEnvironment; Assert.IsNotNull(fixture);
            if (fixture.Target != version) Assert.Inconclusive("NOT_RUN: environment is for a different DOORS release family.");
            StringAssert.StartsWith(fixture.ExactClientBuild, DoorsVersions.Format(version)); Assert.IsFalse(string.IsNullOrWhiteSpace(fixture.ManualAndRuntimeEvidence));
            if (!fixture.ReadOnlyScenarios.TryGetValue(scenario, out var request)) Assert.Inconclusive("NOT_RUN: prepare the explicit read-only scenario " + scenario);
            Assert.IsTrue(request.ReadOnly); var result = await fixture.Service.AnalyzeAsync(request); Assert.IsTrue(result.ReadOnly); Assert.IsNotNull(result.Source); Assert.IsNotNull(result.Target);
            Assert.AreEqual(BaselineMetadataSource.NativeDxl, result.Source.Links.Context.Source); Assert.AreEqual(BaselineMetadataSource.NativeDxl, result.Target.Links.Context.Source);
            Assert.AreEqual(fixture.DatabaseIdentity, result.Source.Links.Context.EnvironmentId);
            if (scenario == "Permissions") { Assert.AreNotEqual(ImpactAnalysisStatus.Completed, result.Status); Assert.IsTrue(result.Source.Links.Context.Modules.Concat(result.Target.Links.Context.Modules).Any(m => m.State == BaselineVerificationReadState.AccessDenied || m.State == BaselineVerificationReadState.Unknown)); }
            else Assert.AreEqual(ImpactAnalysisStatus.Completed, result.Status, string.Join("\n", result.Limitations));
            if (scenario == "Deleted") Assert.IsTrue(result.Changes.Any(c => c.PrimaryType == RequirementChangeType.Deleted && c.Before?.Version.IdentityVerified == true && c.Before.Value("Object Text") != null));
            if (scenario == "RichText") Assert.IsTrue(result.Changes.Any(c => c.Attributes.Any(a => a.RichRepresentationChanged)));
            if (scenario == "HistoricalLinks") Assert.IsTrue(result.Paths.Any(p => p.Steps.Any(s => s.BaselineContext.StartsWith(result.Source.Label, StringComparison.Ordinal))));
            if (scenario == "Current") Assert.AreEqual(ChangeImpactComparisonKind.BaselineToCurrent, result.Kind);
            if (scenario == "BaselineSets") Assert.AreEqual(ChangeImpactComparisonKind.BaselineSets, result.Kind);
            if (scenario == "LargeModule") Assert.IsTrue(result.Source.Requirements.Count >= 10000);
            TestContext.WriteLine("Native read-only scenario=" + scenario + "; client=" + fixture.ExactClientBuild + "; elapsedMs=" + result.ElapsedMilliseconds + "; managedDeltaBytes=" + result.ManagedMemoryDeltaBytes);
        }
    }
}
