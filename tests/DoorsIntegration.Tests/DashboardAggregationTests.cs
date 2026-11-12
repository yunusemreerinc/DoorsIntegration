using System;
using System.Linq;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    internal sealed class DashboardTestClock : IDashboardClock { public DateTime UtcNow { get; set; }=new DateTime(2026,10,30,12,0,0,DateTimeKind.Utc); }
    internal sealed class DashboardTestData
    {
        internal readonly DashboardTestClock Clock=new DashboardTestClock();
        internal readonly DashboardRequest Request=new DashboardRequest();
        internal readonly DashboardEvidence Evidence;
        internal readonly DashboardAggregationService Aggregation;
        internal DashboardTestData()
        {
            Request.Scope.Projects.Clear(); Request.Scope.Projects.Add("/P");
            var current=new BaselineVersionReference(Request.DatabaseIdentity,"M","/P/M",0,0,isCurrent:true);
            var baseline=new BaselineVersionReference(Request.DatabaseIdentity,"M","/P/M",1,4);
            Evidence=new DashboardEvidence { Environment=Request.Environment,DatabaseIdentity=Request.DatabaseIdentity,IsMock=true,CollectedAtUtc=Clock.UtcNow,CollectedSections=DashboardSections.All,DiscoveryComplete=true,SetInventoryComplete=true,BaselineSetCount=2 };
            Evidence.ProjectPaths.Add("/P"); Evidence.FolderPaths.Add("/P/F");
            Evidence.Modules.Add(new DashboardModuleFact { Health=new DashboardModuleHealth { ModuleIdentity="M",ModulePath="/P/M",Project="/P",Version=current,Access="Readable",LatestBaseline=baseline.Version,LatestMajorVersion=1,LatestMinorVersion=4 },LatestVersion=baseline,BaselineCount=2,HistoryComplete=true,InventoryComplete=true,AttributeCatalogComplete=true,AttributeCatalog={"Status","Criticality"} });
            for(int n=1;n<=4;n++) Evidence.Requirements.Add(new DashboardRequirementFact { Version=current,AbsoluteNumber=n,Type=new[]{"SYS","HLR","LLR","TEST"}[n-1],Status="Approved",Criticality="High" });
            foreach(var kind in new[]{DashboardAnalysisKind.Traceability,DashboardAnalysisKind.Quality,DashboardAnalysisKind.Impact})
            {
                var record=new DashboardAnalysisRecord { Kind=kind,AnalysisId=kind+"-1",SnapshotIdentity=kind+"-snapshot",Environment=Request.Environment,DatabaseIdentity=Request.DatabaseIdentity,IsMock=true,TimestampUtc=Clock.UtcNow,PolicyVersion=Request.TraceabilityPolicyVersion,PolicyFingerprint="trace-v1",RequestedPolicyKey=DashboardAnalysisRecord.PolicyKey(Request),ProfileVersion=Request.QualityProfileVersion,ProfileFingerprint="quality-v1",RequestedProfileKey=DashboardAnalysisRecord.ProfileKey(Request),Standard="General",ProfileApproved=true,AnalysisVersion=Request.AnalysisSnapshotVersion,Completeness=DashboardCompleteness.Complete,Status="Succeeded",Modules={current},SourceVersions={baseline},Requirements=Evidence.Requirements.ToList() };
                Evidence.Analyses.Add(record);
            }
            var trace=Analysis(DashboardAnalysisKind.Traceability);
            foreach(var req in Evidence.Requirements) trace.Coverage.Add(new DashboardCoverageFact { RequirementVersionIdentity=req.VersionIdentity,EligibilityKnown=true,Applicable=req.Type!="TEST",State=req.Type=="TEST"?TraceabilityState.Excluded:TraceabilityState.Satisfied });
            for(int n=1;n<=3;n++) trace.Links.Add(new DashboardLinkFact { Identity="L"+n,SourceVersionIdentity=current.NodeIdentity(n),TargetVersionIdentity=current.NodeIdentity(n+1),LinkModuleIdentity="LM",Resolution="Confirmed" });
            var quality=Analysis(DashboardAnalysisKind.Quality); quality.Evaluations=Evidence.Requirements.Select(q=>new DashboardQualityEvaluation { RequirementVersionIdentity=q.VersionIdentity,FullyEvaluated=true }).ToList(); quality.Findings.Add(Finding("quality-1",1,DashboardSeverity.High));
            var impact=Analysis(DashboardAnalysisKind.Impact); impact.ConclusiveComparison=true; impact.ComparedModuleIdentities.Add("M"); impact.ImpactPathCount=3;
            impact.Changes.Add(new DashboardChangeFact { ModuleIdentity="M",RequirementIdentity=current.ObjectIdentity(1),RequirementVersionIdentity=current.NodeIdentity(1),PrimaryType=RequirementChangeType.Modified,Flags=RequirementChangeFlags.Content,Severity=DashboardSeverity.High });
            impact.Findings.Add(Finding("impact-1",2,DashboardSeverity.High)); impact.Findings.Add(Finding("impact-2",2,DashboardSeverity.Medium)); impact.Findings[1].ReviewStatus="NotImpacted";
            Aggregation=new DashboardAggregationService(Clock,new DashboardMetricsCalculator());
        }
        internal DashboardAnalysisRecord Analysis(DashboardAnalysisKind kind) => Evidence.Analyses.Single(a=>a.Kind==kind);
        internal DashboardFinding Finding(string id,int number,DashboardSeverity severity) { var version=Evidence.Modules[0].Health.Version; return new DashboardFinding { Id=id,ModuleIdentity="M",RequirementIdentity=version.ObjectIdentity(number),RequirementVersionIdentity=version.NodeIdentity(number),AbsoluteNumber=number,Category=id.StartsWith("quality")?"Quality":"Impact",Severity=severity,ReviewStatus="Pending",ImpactKind="DirectImpactCandidate",TimestampUtc=Clock.UtcNow,Destination=DashboardDestination.ImpactAnalysis,Explanation="Explicit Mock evidence",AnalysisIdentity=id.StartsWith("quality")?"Quality-1":"Impact-1" }; }
        internal DashboardSnapshot Run() => Aggregation.Aggregate(Request,Evidence,DashboardSections.All);
        internal static decimal? Metric(DashboardSnapshot snapshot,string id) => snapshot.Summary.Metrics.Single(m=>m.Id==id).Value;
    }
    [TestClass,TestCategory("DashboardMock")]
    public class DashboardAggregationTests
    {
        [TestMethod] public void OverviewDeduplicatesOverlappingFoldersModulesAndObjects()
        { var f=new DashboardTestData(); f.Evidence.Modules.Add(f.Evidence.Modules[0]); f.Evidence.Requirements.Add(f.Evidence.Requirements[0]); var s=f.Run(); Assert.AreEqual(1m,DashboardTestData.Metric(s,"FormalModules")); Assert.AreEqual(4m,DashboardTestData.Metric(s,"Requirements")); Assert.AreEqual(3m,DashboardTestData.Metric(s,"TotalLinks")); }
        [TestMethod] public void MultiProjectAndFolderSelectionUsePathBoundariesAndPerFolderRecursion()
        {
            var f=new DashboardTestData(); f.Request.Scope.Kind=DashboardScopeKind.SelectedFolders; f.Request.Scope.Folders.Add(new DashboardFolderSelection { FullPath="/P",IncludeSubfolders=false });
            Assert.AreEqual(1m,DashboardTestData.Metric(f.Run(),"FormalModules")); f.Request.Scope.Folders[0].FullPath="/P/Misc"; Assert.AreEqual(0m,DashboardTestData.Metric(f.Run(),"FormalModules"));
        }
        [TestMethod] public void CompleteCoverageAndUnknownDenominatorAreDifferent()
        {
            var f=new DashboardTestData(); Assert.AreEqual(100m,DashboardTestData.Metric(f.Run(),"Coverage"));
            f.Analysis(DashboardAnalysisKind.Traceability).Coverage[1].State=TraceabilityState.Unknown;
            Assert.IsNull(DashboardTestData.Metric(f.Run(),"Coverage")); Assert.AreEqual(1m,DashboardTestData.Metric(f.Run(),"UnknownTraceability"));
        }
        [TestMethod] public void MissingLinkIsNotAnUnresolvedReference()
        { var f=new DashboardTestData(); f.Analysis(DashboardAnalysisKind.Traceability).Coverage[1].State=TraceabilityState.Violation; var s=f.Run(); Assert.AreEqual(1m,DashboardTestData.Metric(s,"MissingLinks")); Assert.AreEqual(0m,DashboardTestData.Metric(s,"UnknownTraceability")); Assert.AreEqual(200m/3m,DashboardTestData.Metric(s,"Coverage")); }
        [TestMethod] public void AccessDeniedAndPartialInventoryNeverProduceVerifiedCompleteCoverage()
        { var f=new DashboardTestData(); f.Evidence.Modules[0].InventoryComplete=false; f.Evidence.Modules[0].Health.Access="AccessDenied"; var s=f.Run(); Assert.IsNull(DashboardTestData.Metric(s,"Coverage")); Assert.AreEqual(1,s.DataQuality.AccessDeniedModules); Assert.IsFalse(s.DataQuality.VerifiedComplete); }
        [TestMethod] public void BaselineHealthRequiresConclusiveLatestBaselineToCurrentEvidence()
        {
            var f=new DashboardTestData(); var s=f.Run(); Assert.AreEqual(1m,DashboardTestData.Metric(s,"Baseline.ChangedSinceBaseline"));
            f.Analysis(DashboardAnalysisKind.Impact).ConclusiveComparison=false; Assert.AreEqual(1m,DashboardTestData.Metric(f.Run(),"Baseline.VerificationRequired"));
            f.Evidence.Modules[0].NeverBaselinedProven=true; f.Evidence.Modules[0].LatestVersion=null; Assert.AreEqual(1m,DashboardTestData.Metric(f.Run(),"Baseline.NeverBaselined"));
        }
        [TestMethod] public void MultipleRootsDoNotDoubleCountUniqueImpactedRequirements()
        { var f=new DashboardTestData(); var s=f.Run(); Assert.AreEqual(2m,DashboardTestData.Metric(s,"ImpactCount")); Assert.AreEqual(1m,DashboardTestData.Metric(s,"UniqueImpacted")); Assert.AreEqual(1m,DashboardTestData.Metric(s,"PendingReviews")); Assert.AreEqual(3m,DashboardTestData.Metric(s,"ImpactPaths")); }
        [TestMethod] public void DifferentQualityProfilesAndImpactContextsAreNotBlindlySummed()
        {
            var f=new DashboardTestData(); var other=DashboardSerialization.Copy(f.Analysis(DashboardAnalysisKind.Quality)); other.RequestedProfileKey="different"; other.Findings[0].Id="other"; f.Evidence.Analyses.Add(other);
            var s=f.Run(); Assert.AreEqual(1m,DashboardTestData.Metric(s,"QualityFindings")); Assert.IsTrue(s.DataQuality.Warnings.Any(w=>w.Contains("not merged")));
        }
        [TestMethod] public void MissingClassificationFilterProducesPartialTotals()
        { var f=new DashboardTestData(); f.Request.Filters.RequirementStatus="Approved"; f.Evidence.Requirements[0].Status=null; var s=f.Run(); Assert.AreEqual(3m,DashboardTestData.Metric(s,"Requirements")); Assert.IsNull(DashboardTestData.Metric(s,"Coverage")); Assert.IsTrue(s.DataQuality.Warnings.Any(w=>w.Contains("filters"))); }
        [TestMethod] public void ConfigurableScoreHasTransparentWeightsAndRejectsStaleInputs()
        { var f=new DashboardTestData(); f.Request.Score.Enabled=true; var s=f.Run(); Assert.AreEqual(62.5m,s.Summary.ConfigurationHealthScore); Assert.AreEqual("Available",s.Summary.ScoreStatus); f.Clock.UtcNow=f.Clock.UtcNow.AddDays(2); s=f.Run(); Assert.IsNull(s.Summary.ConfigurationHealthScore); Assert.AreEqual("Incomplete",s.Summary.ScoreStatus); }
        [TestMethod] public void SnapshotContainsNoRawRequirementOrLinkInventory()
        { var f=new DashboardTestData(); var s=f.Run(); string json=System.Text.Encoding.UTF8.GetString(DashboardSerialization.Bytes(s)); Assert.IsFalse(json.Contains("CustomAttributeNames")); Assert.IsTrue(s.NavigationModules.Count>0); Assert.AreEqual(3,s.Trends.Count); }
    }
}
