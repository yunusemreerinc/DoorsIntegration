using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.ConfigurationManagement;
using DoorsIntegration.Wpf.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DocumentFormat.OpenXml.Packaging;

namespace DoorsIntegration.Tests
{
    [TestClass,TestCategory("DashboardMock"),DoNotParallelize]
    public class DashboardApplicationTests
    {
        internal static ClientSession Session(string root) => new ClientSessionFactory(null,null,null,root,new TestBaselineJournalProtector()).Create(new ClientProfile(ClientMode.Mock,DoorsVersion.V97,DoorsConnectionMode.AttachExisting),new BaselineTestLog());
        [TestMethod]
        public async Task ExistingMockAdapterRunsOverviewTraceQualityImpactAndCachesWithoutWritingDoors()
        {
            using(var files=new JobTestFixture(1))
            {
                var session=Session(files.Root); var runtime=session.Dashboard; var request=DashboardSerialization.Copy(runtime.DefaultRequest); runtime.Bind(request);
                var existing=await runtime.Service.RefreshAsync(request); Assert.IsNotNull(existing.Snapshot,existing.Error); Assert.IsNull(DashboardTestData.Metric(existing.Snapshot,"Coverage"),"Existing refresh must not silently run traceability.");
                var analyzed=await runtime.Service.RefreshAsync(request,mode:DashboardRefreshMode.RunNewAnalysis); Assert.IsNotNull(analyzed.Snapshot,analyzed.Error); Assert.AreNotEqual("Failed",analyzed.Status,analyzed.Error);
                Assert.AreEqual(4m,DashboardTestData.Metric(analyzed.Snapshot,"FormalModules")); Assert.AreEqual(4m,DashboardTestData.Metric(analyzed.Snapshot,"Requirements")); Assert.AreEqual(100m,DashboardTestData.Metric(analyzed.Snapshot,"Coverage"),System.Text.Encoding.UTF8.GetString(DashboardSerialization.Bytes(analyzed.Snapshot.Sections.Single(s=>s.Section==DashboardSections.Traceability)))); Assert.IsTrue(DashboardTestData.Metric(analyzed.Snapshot,"PendingReviews")>0);
                Assert.AreEqual(0L,analyzed.Snapshot.ComCallCount); Assert.IsFalse(analyzed.CacheWriteFailed); Assert.IsTrue(runtime.Analyses.Read(request,CancellationToken.None).Any(a=>a.Kind==DashboardAnalysisKind.Quality));
                var cache=await runtime.Service.RefreshAsync(request,mode:DashboardRefreshMode.CacheOnly); Assert.AreEqual(analyzed.Snapshot.SnapshotId,cache.Snapshot.SnapshotId); Assert.IsTrue(cache.FromCache);
            }
        }
        [DataTestMethod,DataRow(ExportFormat.Json),DataRow(ExportFormat.Csv),DataRow(ExportFormat.Xlsx),DataRow(ExportFormat.Html)]
        public async Task SixDashboardReportsReuseWritersAndKeepUnknownScopeMetadata(ExportFormat format)
        {
            using(var files=new JobTestFixture(1))
            {
                Directory.CreateDirectory(files.Root); var session=Session(files.Root); var fixture=new DashboardTestData(); var snapshot=fixture.Run();
                snapshot.Sections.Single(s=>s.Section==DashboardSections.Quality).Findings[0].Explanation="=HYPERLINK(\"bad\") <script>alert(1)</script> 注釈";
                foreach(DashboardReportKind kind in Enum.GetValues(typeof(DashboardReportKind)))
                {
                    var report=await session.Dashboard.Reports.ExportAsync(snapshot,kind,new ExportRequest(Path.Combine(files.Root,"report-"+kind+"-"+format),format)); Assert.AreEqual(ExportStatus.Completed,report.Status);
                    if(format==ExportFormat.Xlsx) using(var book=SpreadsheetDocument.Open(Path.Combine(report.DirectoryPath,"data.xlsx"),false)) Assert.IsTrue(book.WorkbookPart.WorksheetParts.Any());
                    if(format==ExportFormat.Html) { string html=File.ReadAllText(Path.Combine(report.DirectoryPath,"data.html")); StringAssert.Contains(html,"snapshotTimestamp"); Assert.IsFalse(html.Contains("<script>alert")); if(kind==DashboardReportKind.ConfigurationSummary) StringAssert.Contains(html,"aria-label=\""); }
                    if(format==ExportFormat.Json) StringAssert.Contains(File.ReadAllText(Path.Combine(report.DirectoryPath,"data.json")),"sourceAnalysisIds");
                    if(format==ExportFormat.Csv&&kind==DashboardReportKind.ConfigurationSummary) Assert.IsTrue(report.CsvProtectedCellCount>0);
                }
            }
        }
        [TestMethod]
        public async Task BaselineSetScopeReusesReadOnlyVerificationAndNativeSetVersionIsIndependent()
        {
            using(var files=new JobTestFixture(1))
            {
                var session=Session(files.Root); var request=DashboardSerialization.Copy(session.Dashboard.DefaultRequest);
                request.Scope.Kind=DashboardScopeKind.SelectedBaselineSet; request.Scope.SetFolder="/Traceability"; request.Scope.DefinitionIdentity=MockBaselineSetVerificationAdapter.DemoDefinitionId; request.Scope.SetIdentity=MockBaselineSetVerificationAdapter.DemoSetId;
                var result=await session.Dashboard.Service.RefreshAsync(request,mode:DashboardRefreshMode.RunNewAnalysis); Assert.IsNotNull(result.Snapshot,result.Error); Assert.AreNotEqual("Failed",result.Status,result.Error); Assert.IsTrue(result.Snapshot.Sections.Single(s=>s.Section==DashboardSections.Traceability).AnalysisIds.Count>0);
                Assert.IsTrue(result.Snapshot.ReadOnly); Assert.IsFalse(session.NativeBaselineSets.Service.ListJobs().Any());
            }
        }
        [TestMethod]
        public void MeterCountsOnlyExistingComMemberInvocationAttempts()
        {
            var sessions=new ExecutorTestSessions(); var meter=new DashboardComCallMeter(sessions);
            using(var session=meter.Open(DoorsConnectionMode.AttachExisting)) { session.SetResult("prefix|PENDING"); session.RunString("no native execution"); session.ReadResult(); }
            Assert.AreEqual(3L,meter.Count); Assert.AreEqual(1,sessions.OpenCalls); Assert.AreEqual(DoorsConnectionMode.AttachExisting,sessions.Mode);
        }
    }
}
