using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    internal static class ExportJson
    {
        public static void String(TextWriter writer, string value)
        {
            if (value == null) { writer.Write("null"); return; }
            writer.Write('"');
            foreach (char c in value)
            {
                if (c == '\\' || c == '"') { writer.Write('\\'); writer.Write(c); }
                else if (c < 32 || c == '\u2028' || c == '\u2029') { writer.Write("\\u"); writer.Write(((int)c).ToString("x4", CultureInfo.InvariantCulture)); }
                else writer.Write(c);
            }
            writer.Write('"');
        }
        public static void Row(TextWriter writer, ExportTableSchema schema, string[] values)
        {
            writer.Write('{');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) writer.Write(','); String(writer, schema.Columns[i].Name); writer.Write(':');
                if (values[i] == null) writer.Write("null");
                else if (schema.Columns[i].Type == ExportColumnType.Text || schema.Columns[i].Type == ExportColumnType.DateTime) String(writer, values[i]);
                else writer.Write(values[i]);
            }
            writer.Write('}');
        }
    }
    public sealed class JsonExportWriter : IExportFormatWriter
    {
        public ExportFormat Format => ExportFormat.Json;
        public void Write(IExportWriteContext context, CancellationToken cancellationToken)
        {
            using (var stream = context.OpenFile("data.json"))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true), 16384))
            {
                writer.Write("{\"schemaVersion\":1,\"tables\":{"); bool firstTable = true;
                foreach (var schema in context.Data.Schemas)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!firstTable) writer.Write(','); firstTable = false;
                    ExportJson.String(writer, schema.Kind.ToString()); writer.Write(":["); bool firstRow = true;
                    foreach (var row in context.Data.ReadRows(schema.Kind, cancellationToken))
                    {
                        if (!firstRow) writer.Write(','); firstRow = false;
                        ExportJson.Row(writer, schema, row); context.RowWritten();
                    }
                    writer.Write(']');
                }
                writer.Write("}}");
            }
        }
    }
    public sealed class CsvExportWriter : IExportFormatWriter
    {
        public ExportFormat Format => ExportFormat.Csv;
        public void Write(IExportWriteContext context, CancellationToken cancellationToken)
        {
            foreach (var schema in context.Data.Schemas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = context.OpenFile(schema.Kind + ".csv"))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true, true), 16384))
                {
                    for (int i = 0; i < schema.Columns.Count; i++) { if (i > 0) writer.Write(','); Cell(writer, schema.Columns[i].Name); }
                    writer.Write("\r\n");
                    foreach (var row in context.Data.ReadRows(schema.Kind, cancellationToken))
                    {
                        for (int i = 0; i < row.Length; i++)
                        {
                            if (i > 0) writer.Write(','); string value = row[i] ?? "";
                            if ((schema.Columns[i].Type == ExportColumnType.Text || schema.Columns[i].Type == ExportColumnType.DateTime) && Dangerous(value))
                            { value = "\t" + value; context.CsvCellProtected(); }
                            Cell(writer, value);
                        }
                        writer.Write("\r\n"); context.RowWritten();
                    }
                }
            }
        }
        internal static bool Dangerous(string value)
        {
            int index = 0;
            while (index < value.Length && (char.IsWhiteSpace(value[index]) || char.IsControl(value[index]) || char.GetUnicodeCategory(value[index]) == UnicodeCategory.Format)) index++;
            return value.Length > 0 && (char.IsControl(value[0]) || (index < value.Length && "=+-@＝＋－＠".IndexOf(value[index]) >= 0));
        }
        private static void Cell(TextWriter writer, string value) { writer.Write('"'); foreach (char c in value) { if (c == '"') writer.Write('"'); writer.Write(c); } writer.Write('"'); }
    }
}
