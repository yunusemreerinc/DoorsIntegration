using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using DoorsIntegration.Core;
using DoorsIntegration.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoorsIntegration.Tests
{
    [TestClass]
    public class BaselineComparisonExportTests
    {
        [DataTestMethod]
        [DataRow(ExportFormat.Json)] [DataRow(ExportFormat.Xlsx)]
        public async Task ReportsPreserveLongUnicodeAndOleValuesWithSafeTextCellsAndExactChunks(ExportFormat format)
        {
            const string oldValue = "=HYPERLINK(\"https://example.invalid\",\"Türkçe 😀\")";
            string raw = "{\\rtf1\\object\\objdata " + string.Concat(Enumerable.Repeat("00FF😀Türkçe\r\n", 4000)) + "}";
            using (var context = new ExportTestContext())
            using (var comparison = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(plain: "before") },
                new[] { BaselineFixture.Object(plain: oldValue, rich: raw) })).CompareAsync(BaselineFixture.Request()))
            {
                var exported = await context.Service.ExportAsync(context.Request(format), new BaselineComparisonExportSource(comparison));
                Assert.AreEqual(ExportStatus.Completed, exported.Status);
                Assert.AreEqual(1L, exported.RowCounts[ExportTableKind.BaselineComparisons]);
                Assert.AreEqual(1L, exported.RowCounts[ExportTableKind.BaselineObjects]);
                Assert.AreEqual(2L, exported.RowCounts[ExportTableKind.BaselineAttributes]);
                Assert.AreEqual(1L, exported.RowCounts[ExportTableKind.BaselineEvents]);
                if (format == ExportFormat.Json)
                {
                    var report = ExportTestContext.Json<ComparisonJson>(Path.Combine(exported.DirectoryPath, "data.json"));
                    Assert.AreEqual("Modified", report.Tables.Objects.Single().Status);
                    var text = report.Tables.Attributes.Single(value => value.Name == "Object Text");
                    Assert.AreEqual(oldValue, text.AfterPlain);
                    string restored = string.Concat(report.Tables.Chunks.Where(value => value.Table == "BaselineAttributes" && value.Column == "afterRichTextWithOle")
                        .OrderBy(value => value.Index).Select(value => value.Value));
                    Assert.AreEqual(raw, restored);
                    Assert.AreEqual(comparison.ReadEvents().Single().EventId, report.Tables.Events.Single().EventId);
                }
                else
                {
                    using (var document = SpreadsheetDocument.Open(Path.Combine(exported.DirectoryPath, "data.xlsx"), false))
                    {
                        Assert.AreEqual(0, new OpenXmlValidator().Validate(document).Count());
                        var workbook = document.WorkbookPart;
                        var sheet = workbook.Workbook.Sheets.Elements<Sheet>().Single(value => value.Name.Value == "BaselineAttributes_1");
                        var part = (WorksheetPart)workbook.GetPartById(sheet.Id.Value);
                        var cells = part.Worksheet.Descendants<Cell>().ToArray();
                        Assert.IsTrue(cells.Any(value => value.DataType?.Value == CellValues.InlineString && value.InnerText == oldValue));
                        Assert.IsFalse(document.WorkbookPart.WorksheetParts.Any(value => value.Worksheet.Descendants<CellFormula>().Any()));
                        Assert.IsFalse(document.WorkbookPart.WorksheetParts.Any(value => value.HyperlinkRelationships.Any()));
                        var chunkSheet = workbook.Workbook.Sheets.Elements<Sheet>().Single(value => value.Name.Value == "ValueChunks_1");
                        var chunkPart = (WorksheetPart)workbook.GetPartById(chunkSheet.Id.Value);
                        var rows = chunkPart.Worksheet.Descendants<Row>().Select(value => value.Elements<Cell>().Select(cell =>
                            Regex.Replace(cell.InnerText, "_[xX]([0-9A-Fa-f]{4})_", match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())).ToArray()).ToArray();
                        int tableColumn = Array.IndexOf(rows[0], "ownerTable"), columnColumn = Array.IndexOf(rows[0], "column"), valueColumn = Array.IndexOf(rows[0], "value");
                        string restored = string.Concat(rows.Skip(1).Where(value => value[tableColumn] == "BaselineAttributes" && value[columnColumn] == "afterRichTextWithOle").Select(value => value[valueColumn]));
                        Assert.AreEqual(raw, restored);
                    }
                }
            }
        }
        [TestMethod]
        public async Task InconclusiveFieldsPublishWarningAndRetainOriginalAccessStatus()
        {
            var denied = BaselineFixture.Value("Object Text", "", "", RequirementValueStatus.AccessDenied, RequirementValueStatus.AccessDenied);
            using (var context = new ExportTestContext())
            using (var comparison = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object(value: denied) }, new[] { BaselineFixture.Object(value: denied) }))
                .CompareAsync(BaselineFixture.Request()))
            {
                var exported = await context.Service.ExportAsync(context.Request(), new BaselineComparisonExportSource(comparison));
                Assert.AreEqual(ExportStatus.CompletedWithIssues, exported.Status);
                var json = ExportTestContext.Json<ComparisonJson>(Path.Combine(exported.DirectoryPath, "data.json"));
                Assert.AreEqual("Indeterminate", json.Tables.Objects.Single().Status);
                Assert.AreEqual("AccessDenied", json.Tables.Attributes.Single(value => value.Name == "Object Text").BeforePlainStatus);
                Assert.IsTrue(json.Tables.Events.Single().RequiresReview);
            }
        }
        [TestMethod]
        public async Task DisposedComparisonProducesOnlyFailedExportReportWithoutPartialPayload()
        {
            using (var context = new ExportTestContext())
            {
                var comparison = await BaselineFixture.Service(BaselineFixture.Adapter(new[] { BaselineFixture.Object() }, new[] { BaselineFixture.Object(plain: "different") }))
                    .CompareAsync(BaselineFixture.Request());
                comparison.Dispose();
                var exported = await context.Service.ExportAsync(context.Request(), new BaselineComparisonExportSource(comparison));
                Assert.AreEqual(ExportStatus.Failed, exported.Status); context.AssertOnlyReport();
            }
        }
    }
    [DataContract] public sealed class ComparisonJson
    { [DataMember(Name = "tables")] public ComparisonTables Tables { get; set; } }
    [DataContract] public sealed class ComparisonTables
    {
        [DataMember(Name = "BaselineObjects")] public ComparisonObjectJson[] Objects { get; set; }
        [DataMember(Name = "BaselineAttributes")] public ComparisonAttributeJson[] Attributes { get; set; }
        [DataMember(Name = "BaselineEvents")] public ComparisonEventJson[] Events { get; set; }
        [DataMember(Name = "ValueChunks")] public ExportTestChunk[] Chunks { get; set; }
    }
    [DataContract] public sealed class ComparisonObjectJson
    { [DataMember(Name = "status")] public string Status { get; set; } }
    [DataContract] public sealed class ComparisonAttributeJson
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "afterPlainText")] public string AfterPlain { get; set; }
        [DataMember(Name = "beforePlainStatus")] public string BeforePlainStatus { get; set; }
    }
    [DataContract] public sealed class ComparisonEventJson
    {
        [DataMember(Name = "eventId")] public string EventId { get; set; }
        [DataMember(Name = "requiresReview")] public bool RequiresReview { get; set; }
    }
}
