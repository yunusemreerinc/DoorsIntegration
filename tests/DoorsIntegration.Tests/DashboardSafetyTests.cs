using System;
using System.Linq;
using DoorsIntegration.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("DashboardMock")]
    public class DashboardSafetyTests
    {
        [TestMethod] public void StaleConclusiveComparisonCannotProveCurrentBaselineHealth()
        { var f=new DashboardTestData(); f.Analysis(DashboardAnalysisKind.Impact).TimestampUtc=f.Clock.UtcNow.AddDays(-2); Assert.AreEqual(1m,DashboardTestData.Metric(f.Run(),"Baseline.VerificationRequired")); }
        [TestMethod] public void SelectedHistoricalVersionRejectsCurrentInventoryInsteadOfSubstituting()
        { var f=new DashboardTestData(); f.Request.Scope.Kind=DashboardScopeKind.SelectedBaselineSnapshot; f.Request.Scope.Modules.Add(f.Evidence.Modules[0].LatestVersion); Assert.ThrowsException<ArgumentException>(()=>f.Run()); }
        [TestMethod] public void UnobservedModuleIdentityKeepsTotalsPartialWithoutInventingAnIdentity()
        { var f=new DashboardTestData(); f.Evidence.UnresolvedModulePaths.Add("/P/Denied"); var s=f.Run(); Assert.AreEqual(1,s.DataQuality.UnknownModules); Assert.IsFalse(s.Summary.Metrics.Single(m=>m.Id=="FormalModules").Completeness==DashboardCompleteness.Complete); Assert.IsNull(DashboardTestData.Metric(s,"Coverage")); }
        [TestMethod] public void CompleteObservedCoverageWithIncompleteAnalysisNeverClaimsVerifiedCompleteness()
        { var f=new DashboardTestData(); f.Analysis(DashboardAnalysisKind.Traceability).Completeness=DashboardCompleteness.Partial; var s=f.Run(); Assert.IsNull(DashboardTestData.Metric(s,"Coverage")); Assert.IsTrue(s.Sections.Single(v=>v.Section==DashboardSections.Traceability).Charts.All(c=>c.Quality!="Complete")); }
        [TestMethod] public void MissingMembersAndOpenSetAreSeparateFromDeniedModule()
        {
            var f=new DashboardTestData(); var verified=DashboardSerialization.Copy(f.Analysis(DashboardAnalysisKind.Traceability)); verified.Kind=DashboardAnalysisKind.BaselineVerification; verified.AnalysisId="verification"; verified.SetIdentity="set"; verified.OpenSet=true; verified.MissingSetMembers=2; f.Evidence.Analyses.Add(verified);
            Assert.AreEqual(1m,DashboardTestData.Metric(f.Run(),"OpenBaselineSets")); Assert.AreEqual(2m,DashboardTestData.Metric(f.Run(),"MissingSetMembers"));
            f.Evidence.Modules[0].Health.Access="AccessDenied"; Assert.AreEqual(2m,DashboardTestData.Metric(f.Run(),"MissingSetMembers"));
        }
        [TestMethod] public void MultipleProjectsAndNestedFolderFiltersDoNotCrossCount()
        {
            var f=new DashboardTestData(); f.Request.Scope.Projects.Add("/Q"); var m=DashboardSerialization.Copy(f.Evidence.Modules[0]); m.Health.ModuleIdentity="N"; m.Health.ModulePath="/Q/F/N"; m.Health.Project="/Q"; m.Health.Version=new BaselineVersionReference(f.Request.DatabaseIdentity,"N","/Q/F/N",0,0,isCurrent:true); f.Evidence.Modules.Add(m); f.Evidence.Requirements.Add(new DashboardRequirementFact { Version=m.Health.Version,AbsoluteNumber=1,ProjectPath="/Q" });
            Assert.AreEqual(2m,DashboardTestData.Metric(f.Run(),"TotalProjects")); f.Request.Scope.Kind=DashboardScopeKind.SelectedFolders; f.Request.Scope.Folders.Add(new DashboardFolderSelection { FullPath="/Q",IncludeSubfolders=false }); Assert.AreEqual(0m,DashboardTestData.Metric(f.Run(),"FormalModules")); f.Request.Scope.Folders[0].IncludeSubfolders=true; Assert.AreEqual(1m,DashboardTestData.Metric(f.Run(),"FormalModules"));
        }
        [TestMethod] public void QualityFindingSeverityAndUniqueRequirementCountsRemainSeparate()
        { var f=new DashboardTestData(); var a=f.Analysis(DashboardAnalysisKind.Quality); a.Findings.Add(f.Finding("quality-2",1,DashboardSeverity.Critical)); var s=f.Run(); Assert.AreEqual(2m,DashboardTestData.Metric(s,"QualityFindings")); Assert.AreEqual(1m,DashboardTestData.Metric(s,"UniqueQualityAffected")); Assert.AreEqual(1m,DashboardTestData.Metric(s,"Quality.Critical")); }
        [TestMethod] public void DifferentBaselineComparisonScopesRemainSeparateTrendSeries()
        { var f=new DashboardTestData(); var extra=DashboardSerialization.Copy(f.Analysis(DashboardAnalysisKind.Impact)); extra.AnalysisId="other-source"; extra.SourceVersions[0]=new BaselineVersionReference(f.Request.DatabaseIdentity,"M","/P/M",1,1); f.Evidence.Analyses.Add(extra); var s=f.Run(); Assert.AreEqual(2,s.Trends.Where(t=>t.MetricId=="ChangedRequirements").Select(t=>t.ComparableScopeKey).Distinct().Count()); Assert.AreEqual(2m,DashboardTestData.Metric(s,"ImpactCount")); }
    }
}
