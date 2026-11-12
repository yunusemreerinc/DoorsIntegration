using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.ConfigurationManagement;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("DashboardMock"),TestCategory("DashboardPerformance"),DoNotParallelize]
    public class DashboardPerformanceTests
    {
        public TestContext TestContext { get; set; }
        [TestMethod] public async Task HundredModulesTenThousandRequirementsHundredThousandLinksAndThousandsOfFindings()
        {
            // Deterministic scale expansion of the existing MockDoorsAdapter's typed identities/schema.
            // This measures normalized aggregation/index/cache, not native discovery or link traversal performance.
            var adapter=new MockDoorsAdapter(); var fixture=adapter.ChangeImpactSnapshots.Before;
            var f=new DashboardTestData(); f.Request.Environment=fixture.Links.Context.EnvironmentId; f.Request.DatabaseIdentity=fixture.Links.Context.RepositoryId; f.Request.MaximumModules=200; f.Request.MaximumRequirements=20000; f.Request.MaximumLinks=150000;
            f.Request.Scope.Projects=new List<string>{"/ScaleP1","/ScaleP2"}; f.Evidence.Environment=f.Request.Environment; f.Evidence.DatabaseIdentity=f.Request.DatabaseIdentity; f.Evidence.Modules.Clear(); f.Evidence.Requirements.Clear(); f.Evidence.Analyses.Clear(); f.Evidence.ProjectPaths=f.Request.Scope.Projects.ToList(); f.Evidence.FolderPaths=new List<string>{"/ScaleP1/F","/ScaleP2/F"}; f.Evidence.BaselineSetCount=3; f.Evidence.ComCallCount=0;
            var versions=new List<BaselineVersionReference>(); var baselines=new List<BaselineVersionReference>();
            for(int m=0;m<100;m++)
            {
                string project=m<50?"/ScaleP1":"/ScaleP2",path=project+"/F/M"+m.ToString("D3"),id="mock-scale-QUID-"+m.ToString("D3");
                var v=new BaselineVersionReference(f.Request.DatabaseIdentity,id,path,0,0,isCurrent:true); var b=new BaselineVersionReference(f.Request.DatabaseIdentity,id,path,1,4,"S"); versions.Add(v); baselines.Add(b);
                f.Evidence.Modules.Add(new DashboardModuleFact { Health=new DashboardModuleHealth { ModuleIdentity=id,ModulePath=path,Version=v,Project=project,Access="Readable" },LatestVersion=b,BaselineCount=2,HistoryComplete=true,InventoryComplete=true,AttributeCatalogComplete=true,AttributeCatalog={"Requirement Type","Status","Criticality"} });
                for(int n=1;n<=100;n++) f.Evidence.Requirements.Add(new DashboardRequirementFact { Version=v,AbsoluteNumber=n,ProjectPath=project,Type=fixture.Requirements[m%4].Value("Requirement Type"),Status="Approved",Criticality="B" });
            }
            foreach(var kind in new[]{DashboardAnalysisKind.Traceability,DashboardAnalysisKind.Quality,DashboardAnalysisKind.Impact})
            {
                var a=DashboardResultMapper.Record(f.Request,kind,"scale-"+kind,f.Clock.UtcNow,f.Evidence.Requirements); a.PolicyFingerprint="mock-scale-policy-v1"; a.ProfileFingerprint="mock-scale-quality-v1"; a.ProfileApproved=true; a.Completeness=DashboardCompleteness.Complete; a.SourceVersions=baselines; a.ConclusiveComparison=true; a.ComparedModuleIdentities=versions.Select(v=>v.ModuleIdentity).ToList(); a.BaselineLinkVerificationAvailable=false; a.BrokenChainCount=0; f.Evidence.Analyses.Add(a);
            }
            var trace=f.Analysis(DashboardAnalysisKind.Traceability); var quality=f.Analysis(DashboardAnalysisKind.Quality); var impact=f.Analysis(DashboardAnalysisKind.Impact); var ids=f.Evidence.Requirements.Select(q=>q.VersionIdentity).ToArray();
            for(int n=0;n<10000;n++) { trace.Coverage.Add(new DashboardCoverageFact { RequirementVersionIdentity=ids[n],EligibilityKnown=true,Applicable=true,State=TraceabilityState.Satisfied }); quality.Evaluations.Add(new DashboardQualityEvaluation { RequirementVersionIdentity=ids[n],FullyEvaluated=true }); }
            for(int n=0;n<100000;n++) trace.Links.Add(new DashboardLinkFact { Identity="mock-scale-link-"+n.ToString("D6"),SourceVersionIdentity=ids[n%10000],TargetVersionIdentity=ids[(n%10000+n/10000+1)%10000],LinkModuleIdentity="mock-scale-LM",Resolution="Confirmed" });
            for(int n=0;n<5000;n++) { var q=f.Evidence.Requirements[n]; var finding=DashboardResultMapper.Finding(quality,"scale-quality-"+n,"Quality","Ambiguous",DashboardSeverity.Medium,q,"Deterministic scale fixture",false,DashboardDestination.RequirementQuality,"rule-"+(n%5)); quality.Findings.Add(finding); }
            for(int n=0;n<2500;n++) { var q=f.Evidence.Requirements[n*2]; var finding=DashboardResultMapper.Finding(impact,"scale-impact-"+n,"Impact","Candidate",DashboardSeverity.High,q,"Deterministic scale fixture",false,DashboardDestination.ImpactAnalysis); finding.ImpactKind="IndirectImpactCandidate"; impact.Findings.Add(finding); }
            for(int n=0;n<100;n++) { var q=f.Evidence.Requirements[n*100]; impact.Changes.Add(new DashboardChangeFact { ModuleIdentity=q.ModuleIdentity,RequirementIdentity=q.Identity,RequirementVersionIdentity=q.VersionIdentity,PrimaryType=RequirementChangeType.Modified,Flags=RequirementChangeFlags.Content }); } impact.ImpactPathCount=10000;
            using(var files=new JobTestFixture(1))
            {
                var provider=new ScaleProvider(f.Evidence); var store=new DashboardSnapshotService(Path.Combine(files.Root,"dashboard"),new TestBaselineJournalProtector()); var cache=new DashboardCacheService(store); var service=new ConfigurationDashboardService(new DashboardRefreshCoordinator(provider,cache,f.Aggregation,new DashboardMetricsCalculator(),f.Clock));
                long before=GC.GetTotalMemory(false); var timer=Stopwatch.StartNew(); var empty=await service.RefreshAsync(f.Request,mode:DashboardRefreshMode.CacheOnly); double initial=timer.Elapsed.TotalMilliseconds; Assert.IsNull(empty.Snapshot); Assert.AreEqual(0,provider.Calls);
                timer.Restart(); var fresh=await service.RefreshAsync(f.Request); double refresh=timer.Elapsed.TotalMilliseconds; Assert.IsNotNull(fresh.Snapshot,fresh.Error); Assert.IsFalse(fresh.CacheWriteFailed); var s=fresh.Snapshot;
                Assert.AreEqual(100m,DashboardTestData.Metric(s,"FormalModules")); Assert.AreEqual(10000m,DashboardTestData.Metric(s,"Requirements")); Assert.AreEqual(100000m,DashboardTestData.Metric(s,"TotalLinks")); Assert.AreEqual(5000m,DashboardTestData.Metric(s,"QualityFindings")); Assert.AreEqual(2500m,DashboardTestData.Metric(s,"UniqueImpacted")); Assert.AreEqual(10000m,DashboardTestData.Metric(s,"ImpactPaths"));
                var repository=new DashboardAnalysisRepository(Path.Combine(files.Root,"analysis"),new TestBaselineJournalProtector()); timer.Restart(); repository.Save(trace); double evidenceSave=timer.Elapsed.TotalMilliseconds; timer.Restart(); Assert.AreEqual(100000,repository.Read(f.Request,CancellationToken.None).Single().Links.Count); double evidenceLoad=timer.Elapsed.TotalMilliseconds;
                timer.Restart(); var restartCache=new DashboardCacheService(store); var loaded=restartCache.Get(f.Request.Key,null); double snapshotLoad=timer.Elapsed.TotalMilliseconds; Assert.AreEqual(s.SnapshotId,loaded.SnapshotId);
                timer.Restart(); var hit=await service.RefreshAsync(f.Request,mode:DashboardRefreshMode.CacheOnly); double memoryLoad=timer.Elapsed.TotalMilliseconds; Assert.IsTrue(hit.FromCache); Assert.AreEqual(1,provider.Calls); Assert.IsTrue(hit.CacheHitRatio>0);
                long peak; using(var process=Process.GetCurrentProcess()) peak=process.PeakWorkingSet64;
                var measurements=new Dictionary<string,object> { ["kind"]="Mock-derived normalized aggregation; not COM/DXL or native SLA evidence",["utc"]=DateTime.UtcNow.ToString("O"),["architecture"]=Environment.Is64BitProcess?"x64":"x86",["os"]=Environment.OSVersion.VersionString,["cpu"]=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),["logicalProcessors"]=Environment.ProcessorCount,["formalModules"]=100,["requirements"]=10000,["links"]=100000,["qualityFindings"]=5000,["impactFindings"]=2500,["baselineSets"]=3,["initialEmptyCacheLoadMs"]=initial,["aggregationMs"]=s.AggregationMilliseconds,["refreshIncludingPublicationMs"]=refresh,["diskSnapshotLoadMs"]=snapshotLoad,["memoryCacheLoadMs"]=memoryLoad,["analysisEvidenceSaveMs"]=evidenceSave,["analysisEvidenceLoadMs"]=evidenceLoad,["managedDeltaBytes"]=Math.Max(0,GC.GetTotalMemory(false)-before),["peakProcessWorkingSetBytes"]=peak,["comCallCount"]=0,["providerCalls"]=provider.Calls,["cacheHitRatio"]=hit.CacheHitRatio };
                string root=Environment.GetEnvironmentVariable("DOORS_DASHBOARD_ARTIFACT_ROOT")??Path.Combine(Path.GetTempPath(),"DoorsDashboardProof"); Directory.CreateDirectory(root); string path=Path.Combine(root,"performance-"+(Environment.Is64BitProcess?"x64":"x86")+".json"); File.WriteAllBytes(path,DashboardSerialization.Bytes(measurements)); TestContext.AddResultFile(path); TestContext.WriteLine(Encoding.UTF8.GetString(DashboardSerialization.Bytes(measurements)));
            }
        }
        private sealed class ScaleProvider : IDashboardDataProvider
        {
            private readonly DashboardEvidence data; public int Calls;
            public ScaleProvider(DashboardEvidence data) { this.data=data; }
            public Task<DashboardEvidence> CollectAsync(DashboardRequest r,DashboardSections s,bool run,IProgress<string> p,CancellationToken t) { t.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(data); }
            public Task ValidateNavigationAsync(DashboardNavigationIntent intent,CancellationToken token)=>Task.CompletedTask;
        }
    }
}
