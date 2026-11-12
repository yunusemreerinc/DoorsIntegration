using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("BaselineVerificationMock")]
    public class BaselineVerificationReviewTests
    {
        public TestContext TestContext { get; set; }
        [DataTestMethod, DataRow(false, BaselineVerificationReadState.AccessDenied), DataRow(true, BaselineVerificationReadState.Unknown)]
        public async Task ExplicitDenialWithoutModuleIdIsPreservedOnlyForTheRequestedScope(bool wrongScope, BaselineVerificationReadState expected)
        {
            var mock = new MockBaselineSetVerificationAdapter(); var context = mock.Fixture.Copy().Context;
            foreach (var m in context.Modules) m.SelectedForVerification = m.ModuleIdentity == "SYS";
            var read = new DeniedRequirements(wrongScope); var links = new NoLinkReads();
            var resolver = new ServiceBaselineLinkResolver(new RequirementService(read), new LinkService(links));
            var snapshot = await resolver.ResolveAsync(MockBaselineSetVerificationAdapter.DemoRequest(), context, null, CancellationToken.None);
            var module = snapshot.Context.Modules.Single(m => m.ModuleIdentity == "SYS");
            Assert.AreEqual(expected, module.State); Assert.IsFalse(module.InventoryComplete); Assert.IsFalse(module.LinksComplete);
            Assert.AreEqual(0, snapshot.Requirements.Count); Assert.AreEqual(0, links.Calls); Assert.AreEqual(1, read.Disposals);
            if (!wrongScope) StringAssert.Contains(module.Reason, "AccessDenied");
        }
        [DataTestMethod, DataRow("External"), DataRow("Denied"), DataRow("DifferentVersion")]
        public void SuspectedLossRequiresObservedReadableMatchingTargetVersion(string targetCase)
        {
            var mock = new MockBaselineSetVerificationAdapter(scenario: targetCase == "External" ? BaselineVerificationMockScenario.ValidExternalLink : BaselineVerificationMockScenario.CompleteChain);
            foreach (var m in mock.Fixture.Context.Modules) { m.SelectedForVerification = true; m.ShareableConfiguration = BaselineShareableConfiguration.Configured; }
            var before = mock.Fixture.Copy(); var after = before.Copy();
            var removed = targetCase == "External" ? after.Links.Single(l => l.TargetModuleIdentity == "EXT") : after.Links.Single(l => l.SourceModuleIdentity == "SYS");
            after.Links.Remove(removed);
            if (targetCase == "Denied") after.Context.Modules.Single(m => m.ModuleIdentity == removed.TargetModuleIdentity).State = BaselineVerificationReadState.AccessDenied;
            if (targetCase == "DifferentVersion")
            { var m = after.Context.Modules.Single(x => x.ModuleIdentity == removed.TargetModuleIdentity); m.Version = new BaselineVersionReference(m.Version.RepositoryId, m.ModuleIdentity, m.FullPath, 9, 9, "OTHER"); }
            var changes = new BaselineTraceabilityComparisonService().Compare(before, after);
            Assert.IsTrue(changes.Any(c => c.Kind == BaselineLinkChangeKind.RemovedLink && c.Confirmed), "Readable source-scope absence is recorded independently of its cause.");
            Assert.IsFalse(changes.Any(c => c.SuspectedLinkLoss), "Unknown/denied/different target version is not positive link-loss evidence.");
        }
        [TestMethod, TestCategory("Performance")]
        public void DenseSnapshotComparisonHandlesFortyFiveThousandRemovalsWithoutQuadraticRescanning()
        {
            var mock = new MockBaselineSetVerificationAdapter(3000, 10); var before = mock.Fixture.Copy();
            foreach (var m in before.Context.Modules) m.SelectedForVerification = true;
            var after = before.Copy(); after.Links = after.Links.Where((l, i) => i % 2 == 0).ToList();
            var watch = Stopwatch.StartNew(); var changes = new BaselineTraceabilityComparisonService().Compare(before, after); watch.Stop();
            Assert.AreEqual(45000, changes.Count(c => c.Kind == BaselineLinkChangeKind.RemovedLink && c.Confirmed));
            Assert.IsFalse(changes.Any(c => c.SuspectedLinkLoss)); Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(60), "Comparison exceeded its explicit Mock performance bound.");
            TestContext.WriteLine("BASELINE_VERIFICATION_METRIC comparisonBeforeLinks=90000 comparisonAfterLinks=45000 removals=45000 elapsedMs=" + watch.ElapsedMilliseconds + "; source=Mock; nativeValidated=False");
        }
        private sealed class DeniedRequirements : IRequirementReader
        {
            private readonly bool wrongScope; internal int Disposals;
            internal DeniedRequirements(bool wrongScope) { this.wrongScope = wrongScope; }
            public Task<IRequirementSnapshot> ReadAsync(RequirementReadRequest request, CancellationToken token)
            { var echo = wrongScope ? new RequirementReadRequest("/Other/Module", baseline: request.Baseline, target: request.Target) : request;
                return Task.FromResult<IRequirementSnapshot>(new DeniedSnapshot(new RequirementReadSummary(Guid.NewGuid(), echo, RequirementModuleStatus.AccessDenied, RequirementModuleOwnership.NotLoaded, RequirementCleanupStatus.NotOwned, 0, 0, 0, 0, 0, 0, false, false, null), () => Disposals++)); }
        }
        private sealed class DeniedSnapshot : IRequirementSnapshot
        {
            private readonly Action disposed; internal DeniedSnapshot(RequirementReadSummary summary, Action disposed) { Summary = summary; this.disposed = disposed; }
            public RequirementReadSummary Summary { get; }
            public IEnumerable<RequirementRecord> ReadObjects() => throw new AssertFailedException("Denied module objects must not be enumerated.");
            public Stream OpenRawStream() => throw new AssertFailedException("No payload exists for a denied module.");
            public void Dispose() => disposed();
        }
        private sealed class NoLinkReads : ILinkReader
        { internal int Calls; public Task<ILinkSnapshot> ReadAsync(LinkReadRequest request, CancellationToken token) { Calls++; throw new AssertFailedException("Denied or mismatched module must not trigger link reads."); } }
    }
}
