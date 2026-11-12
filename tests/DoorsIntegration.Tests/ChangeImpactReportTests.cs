using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DoorsIntegration.Core;
using DoorsIntegration.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass, TestCategory("ChangeImpactMock")]
    public class ChangeImpactReportTests
    {
        public TestContext TestContext { get; set; }
        [DataTestMethod, DataRow(ExportFormat.Json), DataRow(ExportFormat.Csv), DataRow(ExportFormat.Xlsx), DataRow(ExportFormat.Html)]
        public async Task ExistingExportWritersPreserveDiffPathsVersionsAndSafeText(ExportFormat format)
        {
            string root = Environment.GetEnvironmentVariable("DOORS_CHANGE_IMPACT_ARTIFACT_ROOT") ?? Path.Combine(Path.GetTempPath(), "DoorsChangeImpactProof");
            Directory.CreateDirectory(root); var provider = new MockChangeImpactSnapshotProvider(); var sys = provider.After.Requirements.Single(v => v.Version.ModuleIdentity == "SYS");
            MockChangeImpactSnapshotProvider.SetAttribute(sys, "Object Text", "=HYPERLINK(\"unsafe\") <script>alert(1)</script> Türkçe 注釈");
            var result = await ChangeImpactEngineTests.Service(provider).AnalyzeAsync(MockChangeImpactSnapshotProvider.DemoRequest());
            var reviews = new ImpactReviewService(new FileImpactReviewRepository(new BulkBaselineJournalOptions(Path.Combine(root, "reviews-" + Guid.NewGuid().ToString("N"))), new TestBaselineJournalProtector()));
            reviews.Decide(result, result.Findings.First().Identity, "project-reviewer", ImpactReviewStatus.NeedsInvestigation, "Inspect the changed evidence <b>manually</b>.");
            var exporter = new ExportService(new IExportFormatWriter[] { new JsonExportWriter(), new CsvExportWriter(), new XlsxExportWriter(), new HtmlExportWriter() }, new BaselineTestLog());
            var report = await new ImpactAnalysisReportService(exporter).ExportAsync(result, new ExportRequest(Path.Combine(root, "report-" + format + "-" + Guid.NewGuid().ToString("N")), format));
            Assert.AreEqual(ExportStatus.Completed, report.Status); Assert.IsTrue(report.RowCounts.ContainsKey(ExportTableKind.ImpactPaths));
            if (format == ExportFormat.Json) { string text = File.ReadAllText(Path.Combine(report.DirectoryPath, "data.json")); StringAssert.Contains(text, "uniqueImpactedRequirements"); StringAssert.Contains(text, "impactPathCount"); StringAssert.Contains(text, "oldRichText"); StringAssert.Contains(text, "注釈"); TestContext.AddResultFile(Path.Combine(report.DirectoryPath, "data.json")); }
            if (format == ExportFormat.Csv) { string text = File.ReadAllText(Path.Combine(report.DirectoryPath, "ImpactAttributeChanges.csv")); StringAssert.Contains(text, "\t=HYPERLINK"); Assert.IsTrue(report.CsvProtectedCellCount > 0); TestContext.AddResultFile(Path.Combine(report.DirectoryPath, "ImpactAttributeChanges.csv")); }
            if (format == ExportFormat.Xlsx) using (var book = SpreadsheetDocument.Open(Path.Combine(report.DirectoryPath, "data.xlsx"), false)) { Assert.IsTrue(book.WorkbookPart.WorksheetParts.Count() >= 7); Assert.IsFalse(book.WorkbookPart.WorksheetParts.SelectMany(p => p.Worksheet.Descendants<CellFormula>()).Any()); TestContext.AddResultFile(Path.Combine(report.DirectoryPath, "data.xlsx")); }
            if (format == ExportFormat.Html) { string text = File.ReadAllText(Path.Combine(report.DirectoryPath, "data.html")); StringAssert.Contains(text, "&lt;script&gt;"); Assert.IsFalse(text.Contains("<script>")); StringAssert.Contains(text, "ImpactAnalyses"); StringAssert.Contains(text, "ImpactRequirementChanges"); StringAssert.Contains(text, "ImpactFindings"); StringAssert.Contains(text, "ImpactReviews"); StringAssert.Contains(text, "&lt;b&gt;manually&lt;/b&gt;"); TestContext.AddResultFile(Path.Combine(report.DirectoryPath, "data.html")); }
        }
    }
}
