using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("Integration"),TestCategory("DashboardReal"),DoNotParallelize]
    public class DashboardIntegrationTests
    {
        public TestContext TestContext { get; set; }
        [DataTestMethod]
        [DataRow(DoorsVersion.V95,"Metadata"),DataRow(DoorsVersion.V97,"Metadata")]
        [DataRow(DoorsVersion.V95,"Baselines"),DataRow(DoorsVersion.V97,"Baselines")]
        [DataRow(DoorsVersion.V95,"BaselineSets"),DataRow(DoorsVersion.V97,"BaselineSets")]
        [DataRow(DoorsVersion.V95,"Requirements"),DataRow(DoorsVersion.V97,"Requirements")]
        [DataRow(DoorsVersion.V95,"Links"),DataRow(DoorsVersion.V97,"Links")]
        [DataRow(DoorsVersion.V95,"Attributes"),DataRow(DoorsVersion.V97,"Attributes")]
        [DataRow(DoorsVersion.V95,"ReadOnlyConsistency"),DataRow(DoorsVersion.V97,"ReadOnlyConsistency")]
        [DataRow(DoorsVersion.V95,"ComExceptions"),DataRow(DoorsVersion.V97,"ComExceptions")]
        [DataRow(DoorsVersion.V95,"LargeRefresh"),DataRow(DoorsVersion.V97,"LargeRefresh")]
        [DataRow(DoorsVersion.V95,"Cache"),DataRow(DoorsVersion.V97,"Cache")]
        public async Task NativeReadOnlyDashboardReleaseAcceptance(DoorsVersion release,string scenario)
        {
            string version=DoorsVersions.Format(release);
            if(Environment.GetEnvironmentVariable("DOORS_TEST_MODE")!="real-dashboard"||Environment.GetEnvironmentVariable("DOORS_DASHBOARD_VERSION")!=version) Assert.Inconclusive("NOT_RUN: requires explicitly selected real-dashboard mode and actual matching DOORS 9.5/9.7 client. Mock is not COM/DXL evidence.");
            string root=Environment.GetEnvironmentVariable("DOORS_DASHBOARD_SCENARIO_ROOT");
            if(string.IsNullOrWhiteSpace(root)||!File.Exists(Path.Combine(root,version+"-"+scenario+".json"))) Assert.Inconclusive("NOT_RUN: prepare reviewed read-only scenario file <version>-<scenario>.json.");
            var request=DashboardSerialization.Read<DashboardRequest>(File.ReadAllBytes(Path.Combine(root,version+"-"+scenario+".json"))); request.Validate(); Assert.IsFalse(request.IsMock); Assert.AreEqual(release,request.ClientVersion); Assert.IsTrue(request.ReadOnly);
            if(scenario=="BaselineSets") Assert.AreEqual(DashboardScopeKind.SelectedBaselineSet,request.Scope.Kind);
            using(var files=new JobTestFixture(1))
            {
                var logger=new BaselineTestLog(); var client=new ClientSessionFactory(null,null,null,files.Root,new TestBaselineJournalProtector()).Create(new ClientProfile(ClientMode.RealDoors,release,DoorsConnectionMode.AttachExisting),logger);
                var before=await client.NativeReads.ReadAsync(new NativeReadQuery(release,request.Scope.SetFolder??"",request.Scope.Modules.Select(m=>m.FullPath)),CancellationToken.None);
                Assert.IsTrue(before.ReadOnlyObservation); Assert.IsTrue(before.SessionObserved); Assert.AreEqual(request.Environment,before.DatabaseId); Assert.AreEqual(request.DatabaseIdentity,before.RepositoryId); StringAssert.StartsWith(before.ClientVersion,version);
                var sections=scenario=="Baselines"||scenario=="BaselineSets"?DashboardSections.Baseline:scenario=="Links"?DashboardSections.Overview|DashboardSections.Traceability:DashboardSections.Overview;
                var watch=Stopwatch.StartNew(); var result=await client.Dashboard.Service.RefreshAsync(request,sections,scenario=="Links"?DashboardRefreshMode.RunNewAnalysis:DashboardRefreshMode.RefreshExistingResults);
                if(scenario=="ComExceptions") Assert.IsTrue(result.Status=="Failed"||result.Snapshot?.DataQuality.VerifiedComplete==false,"Prepare an unavailable/denied/timeout fixture; no success inference.");
                else { Assert.IsNotNull(result.Snapshot,result.Error); Assert.AreNotEqual("Failed",result.Status,result.Error); Assert.IsTrue(result.Snapshot.ReadOnly); Assert.AreEqual(request.Environment,result.Snapshot.Request.Environment); }
                if(scenario=="Requirements"||scenario=="LargeRefresh") Assert.IsTrue(DashboardTestData.Metric(result.Snapshot,"Requirements")>=(scenario=="LargeRefresh"?10000m:1m));
                if(scenario=="Links") Assert.IsTrue(result.Snapshot.Sections.Single(s=>s.Section==DashboardSections.Traceability).AnalysisIds.Count>0);
                if(scenario=="Cache") { long count=client.DashboardComCalls(); var cached=await client.Dashboard.Service.RefreshAsync(request,sections,DashboardRefreshMode.CacheOnly); Assert.IsTrue(cached.FromCache); Assert.AreEqual(result.Snapshot.SnapshotId,cached.Snapshot.SnapshotId); Assert.AreEqual(count,client.DashboardComCalls()); }
                await client.DrainAsync();
                TestContext.WriteLine("READ_ONLY native release="+version+"; client="+before.ClientVersion+"; scenario="+scenario+"; status="+result.Status+"; ms="+watch.Elapsed.TotalMilliseconds+"; COM member attempts="+result.Snapshot?.ComCallCount+". Manual native history/lock/dirty-state consistency inspection remains required.");
            }
        }
    }
}
