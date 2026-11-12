using System;
using System.Globalization;
using System.Text;
using System.Threading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    public sealed class XlsxExportWriter : IExportFormatWriter
    {
        public ExportFormat Format => ExportFormat.Xlsx;
        public void Write(IExportWriteContext context, CancellationToken cancellationToken)
        {
            using (var stream = context.OpenFile("data.xlsx"))
            using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
            {
                var workbook = document.AddWorkbookPart(); workbook.Workbook = new Workbook();
                var sheets = workbook.Workbook.AppendChild(new Sheets());
                var styles = workbook.AddNewPart<WorkbookStylesPart>();
                styles.Stylesheet = new Stylesheet(
                    new Fonts(new Font(), new Font(new Bold(), new Color { Rgb = "FFFFFFFF" })),
                    new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                        new Fill(new PatternFill(new ForegroundColor { Rgb = "FF194D80" }, new BackgroundColor { Indexed = 64U }) { PatternType = PatternValues.Solid })),
                    new Borders(new Border()), new CellStyleFormats(new CellFormat()),
                    new CellFormats(new CellFormat(), new CellFormat { FontId = 1U, FillId = 2U, ApplyFont = true, ApplyFill = true }));
                styles.Stylesheet.Save(); uint sheetId = 0;
                foreach (var schema in context.Data.Schemas)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var rows = context.Data.ReadRows(schema.Kind, cancellationToken).GetEnumerator())
                    {
                        bool next = rows.MoveNext(); int part = 0;
                        do
                        {
                            if (++sheetId > context.MaxWorksheets) throw ByteBudget.Limit();
                            part++;
                            var worksheet = workbook.AddNewPart<WorksheetPart>();
                            sheets.Append(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = sheetId, Name = schema.Kind + "_" + part.ToString(CultureInfo.InvariantCulture) });
                            using (var writer = OpenXmlWriter.Create(worksheet))
                            {
                                writer.WriteStartElement(new Worksheet());
                                writer.WriteElement(new SheetViews(new SheetView(new Pane { VerticalSplit = 1D, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen }) { WorkbookViewId = 0U }));
                                writer.WriteStartElement(new SheetData()); writer.WriteStartElement(new Row { RowIndex = 1U });
                                foreach (var column in schema.Columns) WriteText(writer, column.Name, 1U);
                                writer.WriteEndElement(); int rowNumber = 0;
                                while (next && rowNumber < context.RowsPerWorksheet)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    writer.WriteStartElement(new Row { RowIndex = (uint)(++rowNumber + 1) });
                                    var values = rows.Current;
                                    for (int i = 0; i < values.Length; i++)
                                    {
                                        string value = values[i];
                                        if (value == null) writer.WriteElement(new Cell());
                                        else if (schema.Columns[i].Type == ExportColumnType.Boolean)
                                            writer.WriteElement(new Cell(new CellValue(value == "true" ? "1" : "0")) { DataType = CellValues.Boolean });
                                        else if (schema.Columns[i].Type == ExportColumnType.Integer && value.TrimStart('-').Length <= 15)
                                            writer.WriteElement(new Cell(new CellValue(value)) { DataType = CellValues.Number });
                                        else WriteText(writer, value, 0U); // Dates, decimals and long IDs keep exact invariant text.
                                    }
                                    writer.WriteEndElement(); context.RowWritten(); next = rows.MoveNext();
                                }
                                writer.WriteEndElement(); writer.WriteEndElement();
                            }
                        } while (next);
                    }
                }
                workbook.Workbook.Save();
            }
        }
        private static void WriteText(OpenXmlWriter writer, string value, uint style)
        {
            writer.WriteElement(new Cell(new InlineString(new Text(SpreadsheetText(value)) { Space = SpaceProcessingModeValues.Preserve }))
                { DataType = CellValues.InlineString, StyleIndex = style });
        }
        internal static string SpreadsheetText(string value)
        {
            var output = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool literalEscape = c == '_' && i + 6 < value.Length && (value[i + 1] == 'x' || value[i + 1] == 'X') && value[i + 6] == '_' &&
                    IsHex(value[i + 2]) && IsHex(value[i + 3]) && IsHex(value[i + 4]) && IsHex(value[i + 5]);
                if (literalEscape) output.Append("_x005F_");
                else if (c < 32 || c == '\uFFFE' || c == '\uFFFF') output.Append("_x").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
                else output.Append(c);
            }
            return output.ToString();
        }
        private static bool IsHex(char c) => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F';
    }
}
