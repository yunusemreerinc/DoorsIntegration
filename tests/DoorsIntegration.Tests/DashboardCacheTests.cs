using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class DashboardTestProvider : IDashboardDataProvider
    {
        internal readonly DashboardTestData Data=new DashboardTestData(); internal int Calls,Analyses; internal bool DenyNavigation; internal TaskCompletionSource<bool> Block;
        public async Task<DashboardEvidence> CollectAsync(DashboardRequest request,DashboardSections sections,bool run,IProgress<string> progress,CancellationToken token)
        { Calls++; if(run) Analyses++; if(Block!=null) using(token.Register(()=>Block.TrySetCanceled())) await Block.Task; token.ThrowIfCancellationRequested(); Data.Evidence.CollectedAtUtc=Data.Clock.UtcNow; return Data.Evidence; }
        public Task ValidateNavigationAsync(DashboardNavigationIntent intent,CancellationToken token) { token.ThrowIfCancellationRequested(); if(DenyNavigation) throw new InvalidOperationException("Requested source no longer accessible."); return Task.CompletedTask; }
    }
    [TestClass,TestCategory("DashboardMock")]
    public class DashboardCacheTests
    {
        [TestMethod]
        public async Task CacheOnlyLoadNeverCallsProviderAndStaleDataIsVisible()
        {
            using(var files=new JobTestFixture(1))
            {
                var p=new DashboardTestProvider(); var store=new DashboardSnapshotService(Path.Combine(files.Root,"dashboard"),new TestBaselineJournalProtector()); var cache=new DashboardCacheService(store); var calculator=new DashboardMetricsCalculator();
                var service=new ConfigurationDashboardService(new DashboardRefreshCoordinator(p,cache,p.Data.Aggregation,calculator,p.Data.Clock));
                var empty=await service.RefreshAsync(p.Data.Request,mode:DashboardRefreshMode.CacheOnly); Assert.IsNull(empty.Snapshot); Assert.AreEqual(0,p.Calls);
                var fresh=await service.RefreshAsync(p.Data.Request); Assert.AreEqual(1,p.Calls); Assert.AreEqual(0,p.Analyses);
                p.Data.Clock.UtcNow=p.Data.Clock.UtcNow.AddDays(2);
                var old=await service.RefreshAsync(p.Data.Request,mode:DashboardRefreshMode.CacheOnly); Assert.IsTrue(old.FromCache); Assert.IsTrue(old.Snapshot.DataQuality.IsStale); Assert.AreEqual(1,p.Calls); Assert.IsTrue(old.CacheHitRatio>0);
                Assert.AreEqual(fresh.Snapshot.SnapshotId,old.Snapshot.SnapshotId);
            }
        }
        [TestMethod]
        public void InvalidationSurvivesRestartWithoutDeletingSnapshotHistory()
        {
            using(var files=new JobTestFixture(1))
            {
                var f=new DashboardTestData(); var store=new DashboardSnapshotService(Path.Combine(files.Root,"dashboard"),new TestBaselineJournalProtector()); var s=f.Run(); store.Save(s);
                new DashboardCacheService(store).Invalidate(f.Request.Key,f.Clock.UtcNow); Assert.IsNull(new DashboardCacheService(store).Get(f.Request.Key,null)); Assert.AreEqual(1,store.History(f.Request.Key).Count);
                var next=f.Run(); store.Save(next); Assert.AreEqual(next.SnapshotId,store.LoadLatest(f.Request.Key).SnapshotId,"Same-clock fresh snapshots must not be invalidated with the old generation.");
            }
        }
        [TestMethod]
        public void CorruptLatestSnapshotFallsBackWithWarningAndCannotInventMetrics()
        {
            using(var files=new JobTestFixture(1))
            {
                var f=new DashboardTestData(); var store=new DashboardSnapshotService(Path.Combine(files.Root,"dashboard"),new TestBaselineJournalProtector()); var old=f.Run(); store.Save(old); f.Clock.UtcNow=f.Clock.UtcNow.AddMinutes(1); store.Save(f.Run());
                var last=Directory.GetFiles(Path.Combine(files.Root,"dashboard",f.Request.Key),"*.snapshot").OrderBy(p=>p,StringComparer.Ordinal).Last(); File.WriteAllText(last,"broken");
                var warnings=new System.Collections.Generic.List<string>(); Assert.AreEqual(old.SnapshotId,store.LoadLatest(f.Request.Key,warnings).SnapshotId); Assert.AreEqual(1,warnings.Count);
            }
        }
        [TestMethod]
        public async Task CancellationRetainsPreviouslyCommittedSnapshotAndDoesNotStartAnalysisOnRefresh()
        {
            using(var files=new JobTestFixture(1)) using(var cancel=new CancellationTokenSource())
            {
                var p=new DashboardTestProvider(); var store=new DashboardSnapshotService(Path.Combine(files.Root,"dashboard"),new TestBaselineJournalProtector()); var service=new ConfigurationDashboardService(new DashboardRefreshCoordinator(p,new DashboardCacheService(store),p.Data.Aggregation,new DashboardMetricsCalculator(),p.Data.Clock));
                var previous=await service.RefreshAsync(p.Data.Request); p.Block=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending=service.RefreshAsync(p.Data.Request,token:cancel.Token); cancel.Cancel(); var cancelled=await pending;
                Assert.IsTrue(cancelled.Cancelled); Assert.AreEqual(previous.Snapshot.SnapshotId,cancelled.Snapshot.SnapshotId); Assert.AreEqual(0,p.Analyses); Assert.AreEqual(1,store.History(p.Data.Request.Key).Count);
            }
        }
        [TestMethod]
        public async Task PartialRefreshPreservesOtherSectionAsOfAndExplicitAnalysisRunsOnlyWhenRequested()
        {
            using(var files=new JobTestFixture(1))
            {
                var p=new DashboardTestProvider(); var store=new DashboardSnapshotService(Path.Combine(files.Root,"dashboard"),new TestBaselineJournalProtector()); var service=new ConfigurationDashboardService(new DashboardRefreshCoordinator(p,new DashboardCacheService(store),p.Data.Aggregation,new DashboardMetricsCalculator(),p.Data.Clock));
                var old=await service.RefreshAsync(p.Data.Request); DateTime trace=old.Snapshot.Sections.Single(s=>s.Section==DashboardSections.Traceability).CollectedAtUtc;
                p.Data.Clock.UtcNow=p.Data.Clock.UtcNow.AddMinutes(2); var next=await service.RefreshAsync(p.Data.Request,DashboardSections.Overview,DashboardRefreshMode.RunNewAnalysis);
                Assert.AreEqual(trace,next.Snapshot.Sections.Single(s=>s.Section==DashboardSections.Traceability).CollectedAtUtc); Assert.AreEqual(1,p.Analyses); Assert.AreEqual(2,store.History(p.Data.Request.Key).Count);
            }
        }
        [TestMethod]
        public void ScopeProfilesAreReusableAndRejectPathTraversal()
        {
            using(var files=new JobTestFixture(1))
            {
                var store=new DashboardSnapshotService(Path.Combine(files.Root,"dashboard"),new TestBaselineJournalProtector()); var r=new DashboardRequest(); store.SaveProfile("review",r); Assert.AreEqual(r.Key,store.LoadProfile("review").Key);
                Assert.ThrowsException<ArgumentException>(()=>store.SaveProfile("../outside",r));
            }
        }
        [TestMethod]
        public async Task DrillDownPreservesExactVersionAndRejectsDeniedOrForeignSources()
        {
            var p=new DashboardTestProvider(); var s=p.Data.Run(); var navigation=new DashboardNavigationService(p); var finding=s.Sections.Single(v=>v.Section==DashboardSections.Impact).Findings.First();
            var intent=navigation.Create(s,DashboardDestination.ImpactAnalysis,finding:finding); Assert.AreEqual(finding.RequirementIdentity,intent.RequirementIdentity); Assert.AreEqual(finding.RequirementVersionIdentity,intent.Version.NodeIdentity(intent.AbsoluteNumber.Value)); await navigation.ValidateAsync(s,intent,CancellationToken.None);
            p.DenyNavigation=true; await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>navigation.ValidateAsync(s,intent,CancellationToken.None));
            intent.Environment="other"; await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>navigation.ValidateAsync(s,intent,CancellationToken.None));
        }
    }
}
