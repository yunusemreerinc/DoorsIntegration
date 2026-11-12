using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Com;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("Integration"), TestCategory("NativeReadOnlyReal"), DoNotParallelize]
    public class NativeReadOnlyIntegrationTests
    {
        public TestContext TestContext { get; set; }
        [DataTestMethod]
        [DataRow(DoorsVersion.V95, "Session"), DataRow(DoorsVersion.V97, "Session")]
        [DataRow(DoorsVersion.V95, "ModuleState"), DataRow(DoorsVersion.V97, "ModuleState")]
        [DataRow(DoorsVersion.V95, "Definitions"), DataRow(DoorsVersion.V97, "Definitions")]
        [DataRow(DoorsVersion.V95, "CurrentLinks"), DataRow(DoorsVersion.V97, "CurrentLinks")]
        public async Task ReadOnlyNativeProbe(DoorsVersion release, string scenario)
        {
            if (Environment.GetEnvironmentVariable("DOORS_TEST_MODE") != "real-readonly" || Environment.GetEnvironmentVariable("DOORS_NATIVE_READ_VERSION") != DoorsVersions.Format(release))
                Assert.Inconclusive("NOT_RUN: explicit real-readonly mode and a matching 9.5/9.7 client are required. Unit/protocol tests are not native acceptance.");
            string path = Environment.GetEnvironmentVariable("DOORS_NATIVE_READ_MODULE"), folder = Environment.GetEnvironmentVariable("DOORS_NATIVE_READ_FOLDER"), linkPath = Environment.GetEnvironmentVariable("DOORS_NATIVE_READ_LINK_MODULE");
            if (scenario == "ModuleState" || scenario == "CurrentLinks") if (string.IsNullOrWhiteSpace(path)) Assert.Inconclusive("NOT_RUN: provide DOORS_NATIVE_READ_MODULE (canonical readable formal module path).");
            if (scenario == "CurrentLinks" && string.IsNullOrWhiteSpace(linkPath)) Assert.Inconclusive("NOT_RUN: provide DOORS_NATIVE_READ_LINK_MODULE.");
            if (scenario == "Definitions" && string.IsNullOrWhiteSpace(folder)) Assert.Inconclusive("NOT_RUN: provide DOORS_NATIVE_READ_FOLDER.");
            var timer = Stopwatch.StartNew(); var logger = new BaselineTestLog();
            var executor = new DxlExecutor(new ComDxlSessionFactory(new DoorsComSessionFactory()), new Utf8DxlScriptLoader(), new DxlWorkspaceFactory(), logger,
                DoorsOperationGate.Shared, new NoDxlRunLimitAutomation(), new DxlWatchdogSettings(), DoorsStaExecutionQueue.Shared);
            var source = new DxlNativeReadObservationSource(executor, new NativeReadScriptOptions());
            var observation = await source.ReadAsync(new NativeReadQuery(release, scenario == "Definitions" ? folder : "",
                scenario == "ModuleState" || scenario == "CurrentLinks" ? new[] { path } : Array.Empty<string>(), scenario == "CurrentLinks" ? new[] { linkPath } : Array.Empty<string>()), CancellationToken.None);
            TestContext.WriteLine("READ_ONLY source={0} release={1} client={2} database={3} session={4} durationMs={5}", observation.Source, release, observation.ClientVersion, observation.DatabaseId, observation.SessionId, timer.ElapsedMilliseconds);
            Assert.IsTrue(observation.SessionObserved); Assert.IsTrue(observation.ReadOnlyObservation); Assert.IsFalse(observation.Complete, "A transport-complete observation must not claim hidden/cached definition completeness.");
            if (scenario == "ModuleState") { Assert.AreEqual(BaselineCheckState.Passed, observation.ModuleStates.Single().CanRead); Assert.AreEqual(DoorsItemKind.FormalModule, observation.ModuleStates.Single().Kind); }
            if (scenario == "Definitions") { Assert.IsTrue(observation.CanRead); Assert.IsTrue(observation.Definitions.Count > 0, "Prepare a folder with a known existing definition."); Assert.IsTrue(observation.Sets.SelectMany(s => s.Members).All(m => m.TypedVersion?.IdentityVerified == true)); }
            if (scenario == "CurrentLinks")
            {
                var module = observation.ModuleStates.Single(); Assert.AreEqual(BaselineCheckState.Passed, module.CanRead); Assert.IsTrue(observation.ObservedLinkModuleIds.ContainsKey(linkPath));
                var context = new BaselineVerificationContext { Source = BaselineMetadataSource.NativeDxl, EnvironmentId = observation.DatabaseId, RepositoryId = observation.RepositoryId, Operator = observation.UserId, ClientVersion = observation.ClientVersion, SessionId = observation.SessionId, SessionObserved = true, LinkModuleIdentities = new Dictionary<string, string>(observation.ObservedLinkModuleIds) };
                var selected = new ModuleBaselineVerificationResult { ModuleIdentity = module.ModuleIdentity, FullPath = module.FullPath, Version = new BaselineVersionReference(observation.RepositoryId, module.ModuleIdentity, module.FullPath, 0, 0, isCurrent: true) };
                var request = new BaselineSetVerificationRequest("native-read-probe", "/", "probe", "probe", new TraceabilityPolicy("read"), new[] { linkPath }, target: release);
                var result = await new DxlReadOnlyLinkEvidenceReader(executor).ReadAsync(request, context, selected, linkPath, CancellationToken.None);
                TestContext.WriteLine("links={0} complete={1} reason={2}", result.Links.Count, result.Complete, result.Reason); Assert.IsTrue(result.Complete, result.Reason);
            }
        }
    }
}
