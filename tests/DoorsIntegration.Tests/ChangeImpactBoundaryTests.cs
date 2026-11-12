using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("ChangeImpactMock")]
    public class ChangeImpactBoundaryTests
    {
        [TestMethod]
        public async Task UnidentifiedReferencesAreNotCountedAsUniqueStableRequirements()
        {
            var p = new MockChangeImpactSnapshotProvider(); var link = p.After.Links.Links.First(); link.TargetVersion = null; link.TargetObject = null; link.TargetAccess = BaselineVerificationReadState.Unknown;
            var r = await ChangeImpactEngineTests.Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest());
            Assert.AreEqual(3, r.UniqueImpactedRequirementCount, "The three identified source-snapshot candidates remain distinct from the unverified target reference."); Assert.AreEqual(1, r.UnverifiedImpactReferenceCount);
            var unknown = r.Impacted.Single(i => !i.IdentityVerified); Assert.IsNull(unknown.RequirementIdentity); Assert.IsNotNull(unknown.ReferenceIdentity); Assert.AreEqual(ImpactTestRecommendation.Unknown, unknown.TestRecommendation); Assert.AreEqual(ImpactAnalysisStatus.Incomplete, r.Status);
        }
        [TestMethod]
        public void LocalReviewAtomicTemporaryFileFitsTheFinalJournalPathBudget()
        {
            using (var files = new JobTestFixture(1))
            {
                string root = Path.Combine(files.Root, new string('r', 60));
                var store = new FileImpactReviewRepository(new BulkBaselineJournalOptions(root), new TestBaselineJournalProtector());
                var decision = new ImpactReviewDecision { FindingIdentity = "finding", AnalysisSnapshotIdentity = "snapshot", EvidenceFingerprint = "evidence", Reviewer = "engineer", ReviewedAtUtc = DateTime.UtcNow, Decision = ImpactReviewStatus.NeedsInvestigation, Rationale = "Long-path atomic append regression." };
                store.Append(decision); Assert.AreEqual(1, store.Read("finding").Count()); Assert.AreEqual(1, Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length);
            }
        }
        [TestMethod]
        public async Task HistoricalTestsDoNotMaskMissingTargetTestChain()
        {
            var p = new MockChangeImpactSnapshotProvider(); p.After.Links.Links.RemoveAll(l => l.SourceModuleIdentity == "HLR");
            var r = await ChangeImpactEngineTests.Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest());
            Assert.IsTrue(r.Findings.Any(f => f.Code == "ExpectedTestLinkageNotObserved:Target")); Assert.IsFalse(r.Findings.Any(f => f.Code == "ExpectedTestLinkageNotObserved:Source"));
            Assert.IsTrue(r.Impacted.Any(i => i.Kind == ImpactCandidateKind.TestImpactCandidate), "Historical regression candidates must still be preserved.");
        }
        [TestMethod]
        public async Task NoImpactPathFoundIsNotAClaimOfNoEngineeringImpact()
        {
            var p = new MockChangeImpactSnapshotProvider(); p.Before.Links.Links.Clear(); p.After.Links.Links.Clear();
            var r = await ChangeImpactEngineTests.Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest());
            Assert.AreEqual(0, r.ImpactPathCount); var f = r.Findings.Single(v => v.Code == "NoImpactPathFound"); Assert.AreEqual(ImpactCandidateKind.NoImpactPathFound, f.Kind); StringAssert.Contains(f.Reason, "not proof");
        }
        [TestMethod]
        public async Task IncomingPolicyCannotAccidentallyTraverseOutgoingLinks()
        {
            var policy = new ImpactAnalysisPolicy(); policy.PropagationRules[0].Direction = TraceabilityDirection.Incoming;
            var r = await ChangeImpactEngineTests.Service(new MockChangeImpactSnapshotProvider()).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest(policy)); Assert.AreEqual(0, r.ImpactPathCount);
        }
        [TestMethod]
        public async Task WrongActualBaselineVersionIsNotReplacedWithSelectedVersion()
        {
            var p = new MockChangeImpactSnapshotProvider(); var l = p.After.Links.Links.First(); l.TargetVersion = new BaselineVersionReference(l.TargetVersion.RepositoryId, l.TargetVersion.ModuleIdentity, l.TargetVersion.FullPath, 99, 1, "OTHER");
            var r = await ChangeImpactEngineTests.Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest());
            Assert.AreEqual(ImpactAnalysisStatus.Incomplete, r.Status); Assert.IsTrue(r.Target.Links.Links.Any(v => v.TargetVersion.Major == 99)); Assert.IsTrue(r.Findings.Any(f => f.Kind == ImpactCandidateKind.UnresolvedImpact));
            Assert.IsTrue(r.LinkChanges.Any(c => c.Observation.Kind == BaselineLinkChangeKind.ChangedModuleVersion));
        }
        [TestMethod]
        public async Task ExternalEchoDoesNotImplyLostLinks()
        {
            var p = new MockChangeImpactSnapshotProvider(); foreach (var l in p.Before.Links.Links.Concat(p.After.Links.Links)) l.IsEchoed = true;
            var r = await ChangeImpactEngineTests.Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest());
            Assert.AreEqual(ImpactAnalysisStatus.Completed, r.Status); Assert.IsFalse(r.LinkChanges.Any(l => l.Observation.SuspectedLinkLoss || l.Observation.Kind == BaselineLinkChangeKind.RemovedLink));
        }
        [TestMethod]
        public async Task DeniedModuleAndUnavailableReferenceCannotBecomeConfirmedDeletion()
        {
            var p = new MockChangeImpactSnapshotProvider(); p.Before.Links.Context.MetadataComplete = false; p.Before.Requirements.Clear(); p.Before.Links.Requirements.Clear(); p.Before.Links.Links.Clear(); p.After.Links.Context.Modules.Single(m => m.ModuleIdentity == "HLR").State = BaselineVerificationReadState.AccessDenied;
            var r = await ChangeImpactEngineTests.Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest()); Assert.AreEqual(ImpactAnalysisStatus.Incomplete, r.Status);
            Assert.IsFalse(r.Changes.Any(c => c.PrimaryType == RequirementChangeType.Added || c.PrimaryType == RequirementChangeType.Deleted)); Assert.IsFalse(r.LinkChanges.Any(c => c.Observation.Kind == BaselineLinkChangeKind.RemovedLink && c.Observation.Confirmed));
        }
        [TestMethod]
        public async Task CriticalityAndVerificationChangesArePolicyCandidatesWithSeparatePriority()
        {
            var p = new MockChangeImpactSnapshotProvider(); var sys = p.After.Requirements.Single(v => v.Version.ModuleIdentity == "SYS"); MockChangeImpactSnapshotProvider.SetAttribute(sys, "Safety Level", "A"); MockChangeImpactSnapshotProvider.SetAttribute(sys, "Verification Method", "Analysis");
            var r = await ChangeImpactEngineTests.Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest()); var c = r.Changes.Single();
            Assert.AreEqual(ImpactSeverity.Critical, c.Severity); Assert.AreEqual("A", c.Criticality); Assert.AreEqual(1, c.ReviewPriority); Assert.IsTrue(r.Impacted.Any(i => i.TestRecommendation == ImpactTestRecommendation.ReviewRequired));
        }
        [TestMethod]
        public async Task StaleSnapshotIsVisibleAndCannotBeCompleted()
        {
            var p = new MockChangeImpactSnapshotProvider(); p.Before.IsStale = true;
            var r = await ChangeImpactEngineTests.Service(p).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest()); Assert.AreEqual(ImpactAnalysisStatus.Incomplete, r.Status); Assert.IsTrue(r.Limitations.Any(l => l.Contains("stale")));
        }
        [TestMethod]
        public async Task ComTimeoutInSourceDoesNotDispatchTargetOrRetry()
        {
            var provider = new TimeoutSnapshotProvider(); var r = await ChangeImpactEngineTests.Service(provider).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest());
            Assert.AreEqual(1, provider.Calls); Assert.AreEqual(ImpactAnalysisStatus.Unknown, r.Status); Assert.IsNull(r.Target); Assert.AreEqual(0, r.ImpactPathCount);
        }
        private sealed class TimeoutSnapshotProvider : IImpactSnapshotProvider
        {
            internal int Calls;
            public Task<ImpactAnalysisSnapshot> CaptureAsync(ImpactSnapshotSelection s, ImpactAnalysisPolicy p, IProgress<string> progress, CancellationToken token)
            { Calls++; var snapshot = new MockChangeImpactSnapshotProvider().Before; snapshot.TransportUncertain = true; return Task.FromResult(snapshot); }
        }
    }
}
