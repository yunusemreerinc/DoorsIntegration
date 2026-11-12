using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    public class ExportFormatTests
    {
        [DataTestMethod]
        [DataRow("=HYPERLINK(\"bad\",\"x\")")]
        [DataRow(" +SUM(1,2)")]
        [DataRow("-1+1")]
        [DataRow("@formula")]
        [DataRow("\t=1+1")]
        [DataRow("\0=1+1")]
        [DataRow("＝1+1")]
        [DataRow("＋1+1")]
        [DataRow("－1+1")]
        [DataRow("＠formula")]
        [DataRow("\uFEFF=1+1")]
        public async Task CsvProtectsFormulaAndControlPrefixesWithoutDroppingOriginalText(string value)
        {
            using (var context = new ExportTestContext())
            {
                var result = await context.Service.ExportAsync(context.Request(ExportFormat.Csv), new EnumerableExportSource(new[] {
                    ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P", "name", value) }));
                var rows = ExportTestCsv.Read(Path.Combine(result.DirectoryPath, "Projects.csv"));
                Assert.AreEqual("\t" + value, ExportTestCsv.Cell(rows, "name"));
                Assert.AreEqual(1L, result.CsvProtectedCellCount);
                Assert.AreEqual(1L, ExportTestContext.Json<ExportTestReport>(result.ReportPath).ProtectedCells);
            }
        }
        [TestMethod]
        public async Task CsvNullFieldsDistinguishesNullFromReadableEmptyString()
        {
            using (var context = new ExportTestContext())
            {
                var result = await context.Service.ExportAsync(context.Request(ExportFormat.Csv), new EnumerableExportSource(new[] {
                    ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P/Null", "name", null),
                    ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P/Empty", "name", "") }));
                var rows = ExportTestCsv.Read(Path.Combine(result.DirectoryPath, "Projects.csv"));
                Assert.AreEqual("", ExportTestCsv.Cell(rows, "name", 1)); Assert.AreEqual("", ExportTestCsv.Cell(rows, "name", 2));
                Assert.IsTrue(ExportTestCsv.Cell(rows, "nullFields", 1).Split(',').Contains("name"));
                Assert.IsFalse(ExportTestCsv.Cell(rows, "nullFields", 2).Split(',').Contains("name"));
            }
        }
        [TestMethod]
        public async Task CsvPreservesUnicodeQuotingMultilineAndInvariantDatesWithUtf8Bom()
        {
            using (var context = new ExportTestContext())
            {
                const string value = "Türkçe,\"quoted\";😀\r\nnew\tline\\end";
                var result = await context.Service.ExportAsync(context.Request(ExportFormat.Csv), new EnumerableExportSource(new[] {
                    ExportTestContext.Row(ExportTableKind.Projects, "fullPath", "/P", "name", value, "collectedAtUtc", new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.FromHours(3))) }));
                string path = Path.Combine(result.DirectoryPath, "Projects.csv");
                var rows = ExportTestCsv.Read(path);
                Assert.AreEqual(value, ExportTestCsv.Cell(rows, "name"));
                Assert.AreEqual("2026-01-01T00:00:00.0000000Z", ExportTestCsv.Cell(rows, "collectedAtUtc"));
                CollectionAssert.AreEqual(new byte[] { 239, 187, 191 }, File.ReadAllBytes(path).Take(3).ToArray());
            }
        }
        [DataTestMethod]
        [DataRow(ExportFormat.Json)]
        [DataRow(ExportFormat.Csv)]
        [DataRow(ExportFormat.Xlsx)]
        public async Task LongRichTextAndSurrogatePairsCanBeReconstructedExactlyFromChunks(ExportFormat format)
        {
            using (var context = new ExportTestContext())
            {
                string rich = new string('x', 3999) + "😀{\\object\\objdata ABCD}\0\r\n_x0000_" + new string('z', 40000);
                var result = await context.Service.ExportAsync(context.Request(format), new EnumerableExportSource(new[] {
                    ExportTestContext.Row(ExportTableKind.Attributes, "modulePath", "/P/M", "name", "Object Text", "richTextWithOle", rich, "plainText", "text", "richTextFormat", "DoorsRtfWithOle") }));
                Assert.AreEqual(ExportStatus.Completed, result.Status);
                string reconstructed;
                if (format == ExportFormat.Json)
                {
                    var document = ExportTestContext.Json<ExportTestDocument>(Path.Combine(result.DirectoryPath, "data.json"));
                    var attribute = document.Tables.Attributes.Single();
                    Assert.AreEqual("text", attribute.Plain);
                    StringAssert.Contains(attribute.Rich, "ValueChunks:");
                    reconstructed = string.Concat(document.Tables.Chunks.Where(chunk => chunk.Table == "Attributes" && chunk.Owner == attribute.Id && chunk.Column == "richTextWithOle").OrderBy(chunk => chunk.Index).Select(chunk => chunk.Value));
                }
                else if (format == ExportFormat.Csv)
                {
                    var rows = ExportTestCsv.Read(Path.Combine(result.DirectoryPath, "ValueChunks.csv"));
                    int valueColumn = Array.IndexOf(rows[0], "value");
                    reconstructed = string.Concat(rows.Skip(1).Select(row => row[valueColumn]));
                }
                else
                {
                    using (var document = SpreadsheetDocument.Open(Path.Combine(result.DirectoryPath, "data.xlsx"), false))
                    {
                        Validate(document);
                        var rows = XlsxRows(document, "ValueChunks");
                        int valueColumn = Array.IndexOf(rows[0], "value");
                        reconstructed = string.Concat(rows.Skip(1).Select(row => row[valueColumn]));
                        Assert.IsNull(document.WorkbookPart.SharedStringTablePart);
                    }
                }
                Assert.AreEqual(rich, reconstructed);
            }
        }
        [TestMethod]
        public async Task ManyNewlinesAreChunkedBelowExcelCellLineFeedLimit()
        {
            using (var context = new ExportTestContext())
            {
                string value = string.Concat(Enumerable.Repeat("line\n", 600));
                var result = await context.Service.ExportAsync(context.Request(), new EnumerableExportSource(new[] {
                    ExportTestContext.Row(ExportTableKind.Requirements, "modulePath", "/M", "absoluteNumber", 1, "text", value) }));
                var document = ExportTestContext.Json<ExportTestDocument>(Path.Combine(result.DirectoryPath, "data.json"));
                Assert.AreEqual(3, document.Tables.Chunks.Length);
                Assert.IsTrue(document.Tables.Chunks.All(chunk => chunk.Value.Count(c => c == '\n') <= 250));
                Assert.AreEqual(value, string.Concat(document.Tables.Chunks.Select(chunk => chunk.Value)));
            }
        }
        [TestMethod]
        public async Task XlsxUsesLiteralTextAndPreservesControlsEscapeLiteralsDatesAndLongNumbers()
        {
            using (var context = new ExportTestContext())
            {
                const string text = "=1+1\t\r\n\0\u0001\uFFFE\uFFFF_x0000__x005F_😀Türkçe";
                var result = await context.Service.ExportAsync(context.Request(ExportFormat.Xlsx), new EnumerableExportSource(new[] {
                    ExportTestContext.Row(ExportTableKind.Attributes, "modulePath", "/M", "name", text, "typedValue", 12345678901234567890.12345678m, "typedDateUtc", new DateTimeOffset(2026,1,1,1,2,3,TimeSpan.FromHours(3))),
                    ExportTestContext.Row(ExportTableKind.Requirements, "modulePath", "/M", "absoluteNumber", long.MaxValue, "isDeleted", true) }));
                Assert.AreEqual(ExportStatus.Completed, result.Status);
                using (var document = SpreadsheetDocument.Open(Path.Combine(result.DirectoryPath, "data.xlsx"), false))
                {
                    Validate(document); var attributes = XlsxRows(document, "Attributes");
                    Assert.AreEqual(text, attributes[1][Array.IndexOf(attributes[0], "name")]);
                    Assert.AreEqual("12345678901234567890.12345678", attributes[1][Array.IndexOf(attributes[0], "typedValue")]);
                    Assert.AreEqual("2025-12-31T22:02:03.0000000Z", attributes[1][Array.IndexOf(attributes[0], "typedDateUtc")]);
                    var requirements = XlsxRows(document, "Requirements");
                    Assert.AreEqual(long.MaxValue.ToString(CultureInfo.InvariantCulture), requirements[1][Array.IndexOf(requirements[0], "absoluteNumber")]);
                    Assert.IsFalse(document.WorkbookPart.WorksheetParts.Any(part => part.Worksheet.Descendants<CellFormula>().Any()));
                    var attributePart = Part(document, "Attributes_1");
                    Assert.IsTrue(attributePart.Worksheet.Descendants<Cell>().Where(cell => cell.InlineString != null).All(cell => cell.DataType.Value == CellValues.InlineString));
                }
            }
        }
        [TestMethod]
        public async Task WorksheetRolloverRepeatsHeadersAndPreservesEveryRow()
        {
            using (var context = new ExportTestContext())
            {
                var request = new ExportRequest(context.Destination, ExportFormat.Xlsx, rowsPerWorksheet: 2);
                var result = await context.Service.ExportAsync(request, new EnumerableExportSource(new ExportServiceTests.OnePassRows(5)));
                using (var document = SpreadsheetDocument.Open(Path.Combine(result.DirectoryPath, "data.xlsx"), false))
                {
                    Validate(document);
                    var names = document.WorkbookPart.Workbook.Sheets.Elements<Sheet>().Where(sheet => sheet.Name.Value.StartsWith("Projects_", StringComparison.Ordinal)).Select(sheet => sheet.Name.Value).ToArray();
                    CollectionAssert.AreEqual(new[] { "Projects_1", "Projects_2", "Projects_3" }, names);
                    Assert.AreEqual(3, Part(document, names[0]).Worksheet.GetFirstChild<SheetData>().Elements<Row>().Count());
                    Assert.AreEqual(2, Part(document, names[2]).Worksheet.GetFirstChild<SheetData>().Elements<Row>().Count());
                    Assert.AreEqual(5L, result.RowCounts[ExportTableKind.Projects]);
                }
            }
        }
        [TestMethod]
        public async Task WorksheetCountLimitDiscardsWorkbookAndPublishesReportOnly()
        {
            using (var context = new ExportTestContext())
            {
                var request = new ExportRequest(context.Destination, ExportFormat.Xlsx, rowsPerWorksheet: 1, maxWorksheets: 8);
                var result = await context.Service.ExportAsync(request, new EnumerableExportSource(new ExportServiceTests.OnePassRows(2)));
                Assert.AreEqual(ExportStatus.Failed, result.Status); context.AssertOnlyReport();
                Assert.AreEqual("LimitReached", ExportTestContext.Json<ExportTestReport>(result.ReportPath).Issues.Last().Code);
            }
        }
        [TestMethod]
        public async Task LargeSourceRemainsSinglePassAcrossAllFormats()
        {
            foreach (var format in new[] { ExportFormat.Json, ExportFormat.Csv, ExportFormat.Xlsx })
            using (var context = new ExportTestContext())
            {
                var rows = new ExportServiceTests.OnePassRows(20000);
                var result = await context.Service.ExportAsync(context.Request(format), new EnumerableExportSource(rows));
                Assert.AreEqual(ExportStatus.Completed, result.Status); Assert.AreEqual(20000L, result.RecordsAccepted);
                Assert.AreEqual(1, rows.Enumerations); Assert.IsTrue(rows.Disposed);
            }
        }
        private static void Validate(SpreadsheetDocument document)
        {
            var errors = new OpenXmlValidator().Validate(document).Take(10).ToArray();
            Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(error => error.Description)));
        }
        private static WorksheetPart Part(SpreadsheetDocument document, string name)
        {
            var sheet = document.WorkbookPart.Workbook.Sheets.Elements<Sheet>().Single(item => item.Name.Value == name);
            return (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id.Value);
        }
        private static List<string[]> XlsxRows(SpreadsheetDocument document, string prefix)
        {
            var result = new List<string[]>(); bool first = true;
            foreach (var sheet in document.WorkbookPart.Workbook.Sheets.Elements<Sheet>().Where(item => item.Name.Value.StartsWith(prefix + "_", StringComparison.Ordinal)))
            {
                var part = (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id.Value);
                foreach (var row in part.Worksheet.GetFirstChild<SheetData>().Elements<Row>().Skip(first ? 0 : 1))
                    result.Add(row.Elements<Cell>().Select(cell => Regex.Replace(cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? "", "_[xX]([0-9A-Fa-f]{4})_", match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())).ToArray());
                first = false;
            }
            return result;
        }
    }
}
