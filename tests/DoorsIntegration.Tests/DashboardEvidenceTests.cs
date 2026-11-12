using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.ConfigurationManagement;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("DashboardMock")]
    public class DashboardEvidenceTests
    {
        [TestMethod] public async Task ExistingImpactResultsReReadApplicableLocalReviewDecisions()
        {
            using(var files=new JobTestFixture(1))
            {
                var f=new DashboardTestData(); f.Request.Scope.Kind=DashboardScopeKind.SelectedFormalModules; f.Request.Scope.Modules.Add(f.Evidence.Modules[0].Health.Version);
                var record=f.Analysis(DashboardAnalysisKind.Impact); var finding=record.Findings[0]; finding.ReviewEvidenceFingerprint="exact-evidence";
                var ledger=new ReviewLedger { Decision=new ImpactReviewDecision { FindingIdentity=finding.Id,AnalysisSnapshotIdentity=record.SnapshotIdentity,EvidenceFingerprint="exact-evidence",Decision=ImpactReviewStatus.ConfirmedImpacted } };
                var repository=new DashboardAnalysisRepository(Path.Combine(files.Root,"analyses"),new TestBaselineJournalProtector()); repository.Save(record);
                var provider=new ServiceDashboardDataProvider(new DashboardReadPorts { Reviews=new ImpactReviewService(ledger) },repository,f.Clock,new BaselineTestLog());
                var observed=await provider.CollectAsync(f.Request,DashboardSections.Impact,false,null,CancellationToken.None); Assert.AreEqual("ConfirmedImpacted",observed.Analyses.Single().Findings[0].ReviewStatus); Assert.AreEqual(0L,observed.ComCallCount);
                ledger.Decision.EvidenceFingerprint="changed-evidence"; observed=await provider.CollectAsync(f.Request,DashboardSections.Impact,false,null,CancellationToken.None); Assert.AreEqual("NotReviewed",observed.Analyses.Single().Findings[0].ReviewStatus);
            }
        }
        [TestMethod] public void CorruptSavedAnalysisProducesDataQualityWarningInsteadOfInventingCounts()
        {
            using(var files=new JobTestFixture(1)) { string root=Path.Combine(files.Root,"analyses"); Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"broken.analysis"),"invalid"); var repository=new DashboardAnalysisRepository(root,new TestBaselineJournalProtector()); Assert.AreEqual(0,repository.Read(new DashboardRequest(),CancellationToken.None).Count()); Assert.IsTrue(repository.ReadWarnings.Count>0); }
        }
        [TestMethod] public async Task MockAdapterPartialInventoryRetainsDeniedModuleAndReadableCounts()
        {
            using(var files=new JobTestFixture(1))
            {
                var adapter=new MockDoorsAdapter(); var snapshot=adapter.ChangeImpactSnapshots.Before; var denied=snapshot.Links.Context.Modules[0]; denied.State=BaselineVerificationReadState.AccessDenied; denied.InventoryComplete=false; snapshot.Requirements.RemoveAll(r=>r.Version.ModuleIdentity==denied.ModuleIdentity);
                var request=new DashboardRequest { Environment=snapshot.Links.Context.EnvironmentId,DatabaseIdentity=snapshot.Links.Context.RepositoryId,Scope=new DashboardScope { Projects=new List<string>{"/Traceability"} } };
                var clock=new DashboardTestClock(); var provider=new ServiceDashboardDataProvider(new DashboardReadPorts { MockSnapshot=(r,t)=>Task.FromResult(snapshot) },new DashboardAnalysisRepository(Path.Combine(files.Root,"analyses"),new TestBaselineJournalProtector()),clock,new BaselineTestLog());
                var evidence=await provider.CollectAsync(request,DashboardSections.Overview,false,null,CancellationToken.None); var result=new DashboardAggregationService(clock,new DashboardMetricsCalculator()).Aggregate(request,evidence,DashboardSections.Overview);
                Assert.AreEqual(4m,DashboardTestData.Metric(result,"FormalModules")); Assert.AreEqual(3m,DashboardTestData.Metric(result,"Requirements")); Assert.AreEqual(1,result.DataQuality.AccessDeniedModules); Assert.IsFalse(result.DataQuality.VerifiedComplete);
            }
        }
        private sealed class ReviewLedger : IImpactReviewRepository
        { internal ImpactReviewDecision Decision; public IEnumerable<ImpactReviewDecision> Read(string id)=>Decision?.FindingIdentity==id?new[]{Decision}:Array.Empty<ImpactReviewDecision>(); public void Append(ImpactReviewDecision decision)=>throw new InvalidOperationException("Read-only test must not write reviews."); }
    }
}
