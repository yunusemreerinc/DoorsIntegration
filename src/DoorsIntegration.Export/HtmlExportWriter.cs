using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Globalization;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    // Uses the same normalized, bounded, disk-spooled data and atomic publication as other writers.
    public sealed class HtmlExportWriter : IExportFormatWriter
    {
        public ExportFormat Format => ExportFormat.Html;
        public void Write(IExportWriteContext context, CancellationToken token)
        {
            using (var output = context.OpenFile("data.html"))
            using (var writer = new StreamWriter(output, new UTF8Encoding(false, true), 16384))
            {
                writer.Write("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>DOORS verification report</title><style>body{font:14px system-ui;background:#0c1726;color:#dce9fa;margin:24px}h1,h2{color:#7ecaff}section{overflow:auto;margin-bottom:28px}table{border-collapse:collapse}th,td{padding:8px;border:1px solid #304962;text-align:left;vertical-align:top;white-space:pre-wrap;max-width:700px}th{background:#17334e}p{max-width:1000px}</style></head><body><h1>DOORS verification / export</h1><p>Read-only evidence report. Mock results do not validate native COM/DXL. Unknown/Incomplete is not Passed. This report is not a certification decision or a replacement for DOORS history.</p>");
                foreach (var schema in context.Data.Schemas)
                {
                    token.ThrowIfCancellationRequested(); if (context.Data.Count(schema.Kind) == 0) continue;
                    writer.Write("<section><h2>"); writer.Write(WebUtility.HtmlEncode(schema.Kind.ToString())); writer.Write("</h2><table><thead><tr>");
                    foreach (var c in schema.Columns) { writer.Write("<th>"); writer.Write(WebUtility.HtmlEncode(c.Name)); writer.Write("</th>"); }
                    writer.Write("</tr></thead><tbody>");
                    foreach (var row in context.Data.ReadRows(schema.Kind, token))
                    { token.ThrowIfCancellationRequested(); writer.Write("<tr>"); int index=0; foreach (string cell in row) { writer.Write("<td>"); writer.Write(WebUtility.HtmlEncode(cell ?? ""));
                        if(schema.Kind==ExportTableKind.DashboardCharts && schema.Columns[index].Name=="percentage" && decimal.TryParse(cell,NumberStyles.Float,CultureInfo.InvariantCulture,out decimal percentage) && percentage>=0 && percentage<=100)
                            writer.Write("<div role=\"img\" aria-label=\""+percentage.ToString("0.##",CultureInfo.InvariantCulture)+" percent\" style=\"width:200px;background:#203650;height:12px\"><div style=\"height:12px;background:#48a9ee;width:"+percentage.ToString("0.##",CultureInfo.InvariantCulture)+"%\"></div></div>");
                        writer.Write("</td>"); index++; } writer.Write("</tr>"); context.RowWritten(); }
                    writer.Write("</tbody></table></section>");
                }
                writer.Write("</body></html>");
            }
        }
    }
}
