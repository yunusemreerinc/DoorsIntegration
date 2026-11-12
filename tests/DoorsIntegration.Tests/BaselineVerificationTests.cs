using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("BaselineVerificationMock")]
    public class BaselineVerificationTests
    {
        public TestContext TestContext { get; set; }
        internal static BaselineSetVerificationService Service(IBaselineSetVerificationSource source, IBaselineLinkResolver resolver) => new BaselineSetVerificationService(new BaselineLinkSnapshotService(source, resolver), source, new BaselineTraceabilityAnalyzer(), new BaselineTraceabilityComparisonService(), new NullBaselineVerificationAuditSink(), new BaselineTestLog());
        [DataTestMethod]
        [DataRow(BaselineVerificationMockScenario.CompleteChain, BaselineVerificationStatus.Passed)]
        [DataRow(BaselineVerificationMockScenario.MissingHlrLlr, BaselineVerificationStatus.Failed)]
        [DataRow(BaselineVerificationMockScenario.WrongTargetVersion, BaselineVerificationStatus.Failed)]
        [DataRow(BaselineVerificationMockScenario.InaccessibleTarget, BaselineVerificationStatus.Incomplete)]
        [DataRow(BaselineVerificationMockScenario.MissingModuleBaseline, BaselineVerificationStatus.Failed)]
        [DataRow(BaselineVerificationMockScenario.OpenComplete, BaselineVerificationStatus.PassedWithWarnings)]
        [DataRow(BaselineVerificationMockScenario.OpenPartial, BaselineVerificationStatus.Incomplete)]
        [DataRow(BaselineVerificationMockScenario.ValidExternalLink, BaselineVerificationStatus.Passed)]
        [DataRow(BaselineVerificationMockScenario.EchoedExternalLink, BaselineVerificationStatus.Passed)]
        [DataRow(BaselineVerificationMockScenario.AccessDenied, BaselineVerificationStatus.Incomplete)]
        [DataRow(BaselineVerificationMockScenario.RiskyShareable, BaselineVerificationStatus.PassedWithWarnings)]
        [DataRow(BaselineVerificationMockScenario.DuplicateLink, BaselineVerificationStatus.Failed)]
        public async Task ReadOnlyScenarioClassification(BaselineVerificationMockScenario scenario, BaselineVerificationStatus expected)
        {
            var adapter = new MockBaselineSetVerificationAdapter(scenario: scenario); var result = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest());
            Assert.AreEqual(expected, result.Status, string.Join("\n", result.Findings.Select(f => f.Code + ": " + f.Explanation))); Assert.IsTrue(result.ReadOnly);
            if (scenario == BaselineVerificationMockScenario.CompleteChain)
            { Assert.IsFalse(result.Snapshot.Context.Set.IsOpen); Assert.AreEqual(4, result.Coverage.TotalRequirements); Assert.AreEqual(3, result.Coverage.RequirementsRequiringLinks); Assert.AreEqual(3, result.Coverage.RequirementsWithValidLinks); Assert.AreEqual(100.0, result.Coverage.TraceabilityCoveragePercentage); Assert.AreEqual(0, result.Coverage.BrokenTraceabilityChains); }
            if (scenario == BaselineVerificationMockScenario.MissingHlrLlr) { Assert.AreEqual(1, result.Coverage.RequirementsMissingLinks); Assert.AreEqual(2, result.Coverage.BrokenTraceabilityChains); Assert.IsTrue(result.Links.Any(l => l.Status == BaselineLinkStatus.MissingExpectedLink)); }
            if (scenario == BaselineVerificationMockScenario.InaccessibleTarget || scenario == BaselineVerificationMockScenario.AccessDenied)
            { Assert.IsTrue(result.Links.Any(l => l.Status == BaselineLinkStatus.AccessDenied)); Assert.IsFalse(result.Snapshot.Context.Modules.Any(m => m.State == BaselineVerificationReadState.Missing)); Assert.IsNull(result.Coverage.TraceabilityCoveragePercentage); Assert.IsTrue(result.Coverage.UnenumeratedModules > 0); }
            if (scenario == BaselineVerificationMockScenario.EchoedExternalLink) Assert.IsTrue(result.Links.Any(l => l.Status == BaselineLinkStatus.EchoedLink && l.CountsAsValid));
            if (scenario == BaselineVerificationMockScenario.WrongTargetVersion) Assert.IsTrue(result.Links.Any(l => l.Status == BaselineLinkStatus.UnexpectedVersionTarget));
            Assert.IsFalse(result.ReferenceAvailable); Assert.IsFalse(result.Changes.Any(c => c.Kind == BaselineLinkChangeKind.RemovedLink));
        }
        [TestMethod]
        public async Task DifferentVersionsOfSamePersistentObjectAreDifferentGraphNodes()
        {
            var adapter = new MockBaselineSetVerificationAdapter(scenario: BaselineVerificationMockScenario.TwoVersions);
            var result = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest()); var nodes = result.Graph.Nodes.Values.Where(n => n.QualifiedModuleId == "SYS" && n.AbsoluteNumber == 1).ToArray();
            Assert.AreEqual(2, nodes.Length); Assert.AreEqual(2, nodes.Select(n => n.Id).Distinct().Count()); Assert.AreEqual(1, nodes.Select(n => n.ObjectIdentity).Distinct().Count()); Assert.AreEqual(2, nodes.Select(n => n.ModuleBaselineIdentity).Distinct().Count());
        }
        [DataTestMethod, DataRow(TraceabilityCycleBehavior.Allow, BaselineVerificationStatus.Passed), DataRow(TraceabilityCycleBehavior.Report, BaselineVerificationStatus.PassedWithWarnings), DataRow(TraceabilityCycleBehavior.Prohibit, BaselineVerificationStatus.Failed)]
        public async Task CyclesFollowProjectPolicy(TraceabilityCycleBehavior behavior, BaselineVerificationStatus expected)
        {
            var adapter = new MockBaselineSetVerificationAdapter(scenario: BaselineVerificationMockScenario.Cycle); var result = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest(policy: MockBaselineSetVerificationAdapter.DemoPolicy(behavior)));
            Assert.AreEqual(expected, result.Status, string.Join(";", result.Findings.Select(f => f.Code))); Assert.AreEqual(1, result.Graph.Cycles.Count);
        }
        [TestMethod]
        public async Task GraphImpactIncomingOutgoingAndSubsetRemainVersionBound()
        {
            var adapter = new MockBaselineSetVerificationAdapter(); var result = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest());
            var source = result.Graph.Nodes.Values.Single(n => n.Role == "SYS").Id; var target = result.Graph.Nodes.Values.Single(n => n.Role == "TEST").Id;
            Assert.AreEqual(4, new TraceabilityGraphService(new RequirementService(new MockDoorsAdapter()), new LinkService(new MockDoorsAdapter()), new BaselineTestLog()).AnalyzeImpact(result.Graph, new[] { source }, TraceabilityDirection.Outgoing, 10, false, 100, 100, CancellationToken.None).Visits.Count);
            Assert.AreEqual(4, new TraceabilityGraphService(new RequirementService(new MockDoorsAdapter()), new LinkService(new MockDoorsAdapter()), new BaselineTestLog()).AnalyzeImpact(result.Graph, new[] { target }, TraceabilityDirection.Incoming, 10, false, 100, 100, CancellationToken.None).Visits.Count);
            var subset = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest(selected: new[] { "HLR", "LLR", "TEST" }));
            Assert.AreEqual(4, subset.Snapshot.Context.Modules.Count); Assert.AreEqual(3, subset.Snapshot.Context.Modules.Count(m => m.SelectedForVerification)); Assert.AreEqual(3, subset.Coverage.TotalRequirements);
        }
        [TestMethod]
        public async Task SnapshotComparisonDistinguishesAccessUncertaintyFromSuspectedLoss()
        {
            var adapter = new MockBaselineSetVerificationAdapter(scenario: BaselineVerificationMockScenario.RiskyShareable); var first = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest());
            adapter.Fixture.Links.RemoveAt(1);
            var after = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest(first.Snapshot));
            Assert.IsTrue(after.Changes.Any(c => c.Kind == BaselineLinkChangeKind.RemovedLink && c.Confirmed && c.SuspectedLinkLoss)); Assert.IsTrue(after.Findings.Any(f => f.Code == "SuspectedLinkLoss"));
            adapter.Fixture.Context.Modules[1].LinksComplete = false;
            var inaccessible = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest(first.Snapshot));
            Assert.IsFalse(inaccessible.Changes.Any(c => c.Kind == BaselineLinkChangeKind.RemovedLink && c.Confirmed)); Assert.IsFalse(inaccessible.Changes.Any(c => c.SuspectedLinkLoss));
        }
        [TestMethod]
        public async Task ChangedTargetAndExpectedVersionRebindingAreNotRemovedLinks()
        {
            var adapter = new MockBaselineSetVerificationAdapter(2); var original = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest());
            var changed = original.Snapshot.Copy(); changed.Links[0].TargetObject = 2;
            var differences = new BaselineTraceabilityComparisonService().Compare(original.Snapshot, changed);
            Assert.IsTrue(differences.Any(c => c.Kind == BaselineLinkChangeKind.ChangedLinkTarget)); Assert.IsFalse(differences.Any(c => c.Kind == BaselineLinkChangeKind.RemovedLink));
            changed = original.Snapshot.Copy(); var before = changed.Context.Modules[0].Version; var next = new BaselineVersionReference(before.RepositoryId, before.ModuleIdentity, before.FullPath, 3, 0, "NEXT");
            foreach (var l in changed.Links.Where(l => l.SourceVersion.ModuleIdentity == "SYS")) l.SourceVersion = next;
            var map = new[] { new BaselineVersionRebinding { BeforeVersionIdentity = before.ModuleBaselineIdentity, AfterVersionIdentity = next.ModuleBaselineIdentity, AdapterValidated = true, Evidence = "Mock explicit transition" } };
            differences = new BaselineTraceabilityComparisonService().Compare(original.Snapshot, changed, map);
            Assert.IsTrue(differences.Any(c => c.Kind == BaselineLinkChangeKind.ExpectedVersionRebinding)); Assert.IsFalse(differences.Any(c => c.Kind == BaselineLinkChangeKind.RemovedLink));
        }
        [TestMethod]
        public async Task IncomingOutgoingObservationsAreNotPhysicalDuplicates()
        {
            var adapter = new MockBaselineSetVerificationAdapter(); var echo = adapter.Fixture.Copy().Links[0]; echo.Direction = LinkDirection.Incoming; echo.ObservationId = "incoming-observation"; adapter.Fixture.Links.Add(echo);
            var result = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest()); Assert.AreEqual(BaselineVerificationStatus.Passed, result.Status); Assert.AreEqual(3, result.Graph.Edges.Count); Assert.IsFalse(result.Changes.Any(c => c.Kind == BaselineLinkChangeKind.UnexpectedDuplicateLink));
            Assert.AreEqual(4, result.Graph.Observations.Count); Assert.IsTrue(result.Graph.Edges.Any(e => e.ObservedDirections.Contains(LinkDirection.Incoming) && e.ObservedDirections.Contains(LinkDirection.Outgoing)));
        }
        [TestMethod]
        public async Task MetadataDriftLimitsAndUnknownEligibilityCannotPass()
        {
            var adapter = new MockBaselineSetVerificationAdapter { ChangeContextDuringRead = true }; var drift = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest()); Assert.AreEqual(BaselineVerificationStatus.Incomplete, drift.Status);
            adapter = new MockBaselineSetVerificationAdapter(); adapter.Fixture.Requirements[0].RequirementType = "";
            var role = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest()); Assert.AreEqual(1, role.Coverage.UnknownPolicyEligibility); Assert.IsNull(role.Coverage.TraceabilityCoveragePercentage); Assert.AreNotEqual(BaselineVerificationStatus.Passed, role.Status);
            var limited = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest(maxRequirements: 2)); Assert.IsTrue(limited.Snapshot.Truncated); Assert.AreNotEqual(BaselineVerificationStatus.Passed, limited.Status);
        }
        [TestMethod, TestCategory("Performance")]
        public async Task TwelveThousandRequirementsNinetyThousandLinksUseBoundedIterativeAnalysis()
        {
            var adapter = new MockBaselineSetVerificationAdapter(3000, 10); var clock = Stopwatch.StartNew(); var result = await Service(adapter, adapter).VerifyAsync(MockBaselineSetVerificationAdapter.DemoRequest()); clock.Stop();
            Assert.AreEqual(BaselineVerificationStatus.Passed, result.Status); Assert.AreEqual(12000, result.Coverage.TotalRequirements); Assert.AreEqual(90000, result.Graph.Edges.Count); Assert.AreEqual(100.0, result.Coverage.TraceabilityCoveragePercentage); Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(90), "Bounded Mock analysis exceeded 90 seconds.");
            TestContext.WriteLine("BASELINE_VERIFICATION_METRIC requirements=12000 links=90000 elapsedMs=" + clock.ElapsedMilliseconds + "; source=Mock; nativeValidated=False");
        }
    }
}
