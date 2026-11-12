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
    [TestClass, TestCategory("ChangeImpactMock")]
    public class ChangeImpactEngineTests
    {
        public TestContext TestContext { get; set; }
        internal static ChangeImpactAnalysisService Service(IImpactSnapshotProvider provider, ImpactReviewService reviews = null) => new ChangeImpactAnalysisService(provider, new RequirementChangeDetector(), new ImpactPropagationEngine(), new ImpactClassificationService(), reviews, new BaselineTestLog(), new NullBaselineVerificationAuditSink());
        [DataTestMethod, DataRow(ChangeImpactComparisonKind.ModuleBaselines), DataRow(ChangeImpactComparisonKind.BaselineToCurrent), DataRow(ChangeImpactComparisonKind.BaselineSets)]
        public async Task BaselineAwareDirectIndirectAndTestCandidates(ChangeImpactComparisonKind kind)
        {
            var result = await Service(new MockChangeImpactSnapshotProvider()).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest(kind: kind));
            Assert.AreEqual(ImpactAnalysisStatus.Completed, result.Status, string.Join("\n", result.Limitations));
            Assert.IsTrue(result.Paths.Any(p => p.Depth == 1)); Assert.IsTrue(result.Paths.Any(p => p.Depth == 3));
            Assert.IsTrue(result.Impacted.Any(i => i.Kind == ImpactCandidateKind.TestImpactCandidate)); Assert.AreEqual(3, result.UniqueImpactedRequirementCount);
            Assert.AreEqual(6, result.ImpactPathCount); Assert.IsTrue(result.Paths.All(p => p.Steps.All(s => !string.IsNullOrEmpty(s.LinkIdentity) && !string.IsNullOrEmpty(s.BaselineContext))));
            Assert.AreNotEqual(result.SourceGraphSnapshotIdentity, result.TargetGraphSnapshotIdentity);
            Assert.IsTrue(result.Paths.All(p => p.GraphSnapshotIdentity == result.SourceGraphSnapshotIdentity || p.GraphSnapshotIdentity == result.TargetGraphSnapshotIdentity));
            Assert.IsFalse(result.Changes.Any(c => c.Flags.HasFlag(RequirementChangeFlags.LinkRemoved)));
            if (kind == ChangeImpactComparisonKind.BaselineToCurrent) Assert.IsTrue(result.Target.Requirements.Single(v => v.Version.ModuleIdentity == "SYS").Version.IsCurrent);
        }
        [TestMethod]
        public async Task DeletedRequirementUsesSourceHistoricalTestLinks()
        {
            var p = new MockChangeImpactSnapshotProvider(); p.After.Requirements.RemoveAll(item => item.Version.ModuleIdentity == "SYS"); p.After.Links.Requirements.RemoveAll(item => item.Version.ModuleIdentity == "SYS"); p.After.Links.Links.RemoveAll(l => l.SourceModuleIdentity == "SYS");
            var r = await Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest());
            Assert.IsTrue(r.Changes.Any(c => c.PrimaryType == RequirementChangeType.Deleted && c.Before.Value("Object Text") != null));
            Assert.IsTrue(r.Impacted.Any(i => i.Kind == ImpactCandidateKind.TestImpactCandidate && i.TestRecommendation == ImpactTestRecommendation.TraceabilityUpdateCandidate));
        }
        [TestMethod]
        public async Task LinkTargetChangeSeedsAnOtherwiseUnchangedRequirement()
        {
            var p = new MockChangeImpactSnapshotProvider(); p.After.Requirements.Single(v => v.Version.ModuleIdentity == "SYS").Attributes = ImpactSerialization.Clone(p.Before.Requirements.Single(v => v.Version.ModuleIdentity == "SYS").Attributes);
            var l = p.After.Links.Links.Single(v => v.SourceModuleIdentity == "SYS"); var target = p.After.Requirements.Single(v => v.Version.ModuleIdentity == "LLR"); l.TargetVersion = target.Version; l.TargetObject = target.AbsoluteNumber; l.TargetPath = target.Version.FullPath;
            var r = await Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest()); Assert.IsTrue(r.LinkChanges.Any(c => c.Observation.Kind == BaselineLinkChangeKind.ChangedLinkTarget)); Assert.AreEqual(RequirementChangeType.LinkTargetChanged, r.Changes.Single().PrimaryType);
        }
        [TestMethod]
        public async Task NoReferenceDoesNotInventRemovedLinkAndDeniedTargetIsUnresolved()
        {
            var p = new MockChangeImpactSnapshotProvider(); p.After.Links.Links.First().TargetAccess = BaselineVerificationReadState.AccessDenied;
            var r = await Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest());
            Assert.AreEqual(ImpactAnalysisStatus.Incomplete, r.Status); Assert.IsTrue(r.Findings.Any(f => f.Kind == ImpactCandidateKind.UnresolvedImpact));
            Assert.IsFalse(r.LinkChanges.Any(c => c.Observation.Kind == BaselineLinkChangeKind.RemovedLink && c.Observation.Confirmed));
        }
        [DataTestMethod, DataRow(0, 1000, 1000), DataRow(8, 1, 1000), DataRow(8, 1000, 1)]
        public async Task DepthNodeAndPathLimitsAreIncomplete(int depth, int nodes, int paths)
        {
            var policy = new ImpactAnalysisPolicy { MaximumDepth = depth, MaximumNodes = nodes, MaximumPaths = paths };
            var r = await Service(new MockChangeImpactSnapshotProvider()).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest(policy));
            Assert.IsTrue(r.Truncated); Assert.AreEqual(ImpactAnalysisStatus.Incomplete, r.Status);
        }
        [TestMethod]
        public async Task CycleTerminatesAndAlternatePathsArePreservedWithinLimits()
        {
            var p = new MockChangeImpactSnapshotProvider();
            foreach (var s in new[] { p.Before, p.After })
            {
                var original = s.Links.Links.First(); var direct = ImpactSerialization.Clone(original); var test = s.Requirements.Single(item => item.Version.ModuleIdentity == "TEST");
                direct.TargetVersion = test.Version; direct.TargetObject = test.AbsoluteNumber; direct.TargetPath = test.Version.FullPath; direct.ObservationId = "direct-test"; s.Links.Links.Add(direct);
                var back = ImpactSerialization.Clone(original); var llr = s.Requirements.Single(item => item.Version.ModuleIdentity == "LLR"); var sys = s.Requirements.Single(item => item.Version.ModuleIdentity == "SYS"); back.SourceVersion = llr.Version; back.SourceObject = llr.AbsoluteNumber; back.TargetVersion = sys.Version; back.TargetObject = sys.AbsoluteNumber; back.ObservationId = "cycle"; s.Links.Links.Add(back);
            }
            var r = await Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest(new ImpactAnalysisPolicy { MaximumPathsPerRequirement = 6 }));
            Assert.IsFalse(r.Truncated); Assert.IsTrue(r.Limitations.Any(l => l.Contains("cycle components="))); Assert.IsTrue(r.Impacted.Any(i => i.PathIdentities.Count > 1)); Assert.IsTrue(r.Paths.All(path => path.Steps.Select(s => s.TargetNode).Distinct().Count() == path.Depth));
        }
        [TestMethod]
        public async Task LocalReviewSurvivesRestartOnlyForIdenticalVersionPolicyAndEvidence()
        {
            string root = Path.Combine(Path.GetTempPath(), "impact-review-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new FileImpactReviewRepository(new BulkBaselineJournalOptions(root), new TestBaselineJournalProtector()); var reviews = new ImpactReviewService(repository); var provider = new MockChangeImpactSnapshotProvider(); var request = MockChangeImpactSnapshotProvider.DemoRequest();
                var r = await Service(provider, reviews).AnalyzeAsync(request); var finding = r.Findings.First(f => f.PathIdentity != null);
                reviews.Decide(r, finding.Identity, "reviewer", ImpactReviewStatus.ConfirmedImpacted, "Review affected verification evidence.");
                var reopened = new ImpactReviewService(new FileImpactReviewRepository(new BulkBaselineJournalOptions(root), new TestBaselineJournalProtector())); var same = await Service(provider, reopened).AnalyzeAsync(request);
                Assert.AreEqual(ImpactReviewStatus.ConfirmedImpacted, same.Findings.Single(f => f.Identity == finding.Identity).ReviewStatus);
                MockChangeImpactSnapshotProvider.SetAttribute(provider.After.Requirements.First(), "Priority", "99"); var changed = await Service(provider, reopened).AnalyzeAsync(request);
                Assert.IsTrue(changed.Findings.All(f => f.ReviewStatus == ImpactReviewStatus.NotReviewed)); Assert.AreEqual(1, repository.Read(finding.Identity).Count());
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        [TestMethod, TestCategory("Performance")]
        public async Task TwelveThousandRequirementsDenseGraphHasBoundedWorkAndMeasuredMemory()
        {
            var provider = new MockChangeImpactSnapshotProvider(3000, 10); var watch = Stopwatch.StartNew();
            var result = await Service(provider).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest(new ImpactAnalysisPolicy { MaximumPaths = 2000, MaximumPathsPerRequirement = 2 })); watch.Stop();
            Assert.AreEqual(12000, result.Source.Requirements.Count); Assert.AreEqual(90000, result.Source.Links.Links.Count); Assert.IsTrue(result.Paths.Count <= 2000); Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(120));
            TestContext.WriteLine("CHANGE_IMPACT_METRIC requirements=12000 links=90000 elapsedMs=" + watch.ElapsedMilliseconds + " measuredManagedDeltaBytes=" + result.ManagedMemoryDeltaBytes + " paths=" + result.ImpactPathCount + "; source=Mock; nativeValidated=False");
        }
        [TestMethod]
        public async Task CancellationDoesNotDispatchAnySnapshotRead()
        { await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => Service(new MockChangeImpactSnapshotProvider()).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest(), token: new CancellationToken(true))); }
    }
}
