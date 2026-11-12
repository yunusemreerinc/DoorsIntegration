using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;
using DoorsIntegration.Export;

namespace DoorsIntegration.Desktop
{
    internal sealed class ExportWorkspacePanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox destination = WorkspaceUi.Input("ExportDestination");
        private readonly ComboBox format = WorkspaceUi.Choice<ExportFormat>("ExportFormat"), policy = WorkspaceUi.Choice<ExportErrorPolicy>("ExportErrorPolicy");
        private readonly CheckBox discovery = WorkspaceUi.Check("ExportIncludeDiscovery", "Latest Discovery snapshot", true),
            requirements = WorkspaceUi.Check("ExportIncludeRequirements", "Latest requirements + object attributes", true),
            links = WorkspaceUi.Check("ExportIncludeLinks", "Latest link snapshot", true);
        private readonly NumericUpDown records = WorkspaceUi.Number("ExportMaxRecords", 10000000, 1, 1000000000),
            issues = WorkspaceUi.Number("ExportMaxIssues", 10000, 1, 100000), maxField = WorkspaceUi.Number("ExportMaxFieldCharacters", 16777216, 1, 67108864),
            spoolMiB = WorkspaceUi.Number("ExportMaxSpoolMiB", 8192, 1, 1048576), outputMiB = WorkspaceUi.Number("ExportMaxOutputMiB", 8192, 1, 1048576),
            worksheetRows = WorkspaceUi.Number("ExportRowsPerWorksheet", 1048575, 1, 1048575), worksheets = WorkspaceUi.Number("ExportMaxWorksheets", 1024, 8, 4096);
        private readonly RichTextBox report = WorkspaceUi.Output("ExportReport", 230);
        public ExportWorkspacePanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "ExportWorkspacePanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row(discovery, requirements, links,
                WorkspaceUi.Button("ExportShowSources", "Show available sources", (s, e) => ShowSources())));
            WorkspaceUi.Add(form, WorkspaceUi.Row("New destination directory", destination, WorkspaceUi.Button("ExportChooseParent", "Choose parent...", (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "Choose existing parent; export creates a new child directory" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) destination.Text = Path.Combine(dialog.SelectedPath, "DoorsExport-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            })));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Format", format, "Invalid-row policy", policy));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Max records", records, "Max issues", issues, "Max field chars", maxField));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Max spool MiB", spoolMiB, "Max output MiB", outputMiB, "Rows per sheet", worksheetRows, "Max sheets", worksheets));
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("RunExport", "Export selected snapshots", async (s, e) => await ExportAsync())));
            WorkspaceUi.Add(form, report);
            report.Text = "Read/Discover first, then export retained snapshots. Export does not contact DOORS. Existing destination directories are never replaced.\nCSV cells are protected; Unicode, rich-text chunks, completeness issues and report.json are included by the export service.";
        }
        private void ShowSources()
        {
            var d = workspace.Discovery(); var r = workspace.Data.Requirements; var l = workspace.Data.Links;
            report.Text = "Discovery: " + (d == null ? "unavailable" : d.Scope.RootPath + "; Items=" + d.Items.Count + "; Complete=" + d.IsCompleteForVisibleScope) +
                "\nRequirements: " + (r == null ? "unavailable" : r.Summary.Request.ModulePath + "; Objects=" + r.Summary.ObjectCount + "; Complete=" + r.Summary.IsCompleteForReadableObjects) +
                "\nLinks: " + (l == null ? "unavailable" : l.Summary.Request.ModulePath + "; Records=" + l.Summary.RecordCount + "; Complete=" + l.Summary.IsCompleteForReadableScope);
        }
        private Task ExportAsync() => workspace.RunAsync("Exporting selected snapshots...", async token =>
        {
            var d = discovery.Checked ? workspace.Discovery() : null;
            var r = requirements.Checked ? workspace.Data.Requirements : null;
            var l = links.Checked ? workspace.Data.Links : null;
            if ((discovery.Checked && d == null) || (requirements.Checked && r == null) || (links.Checked && l == null))
                throw WorkspaceUi.Invalid("A selected source is unavailable. Read it first or deselect it; missing sources are never silently omitted.");
            if (d == null && r == null && l == null) throw WorkspaceUi.Invalid("Select at least one available source.");
            var request = new ExportRequest(destination.Text, WorkspaceUi.Selected<ExportFormat>(format), WorkspaceUi.Selected<ExportErrorPolicy>(policy),
                (long)records.Value, (int)issues.Value, (int)maxField.Value, (long)spoolMiB.Value * 1048576, (long)outputMiB.Value * 1048576,
                (int)worksheetRows.Value, (int)worksheets.Value);
            var source = new SnapshotExportSource(d == null ? null : new[] { d }, r == null ? null : new[] { r }, l == null ? null : new[] { l });
            var progress = new Progress<ExportProgress>(p => workspace.SetStatus("Export: " + p.Phase + "; Read=" + p.RecordsRead + "; Written=" + p.RowsWritten + "; Issues=" + p.IssueCount));
            var result = await workspace.Services.Export.ExportAsync(request, source, progress, token);
            report.Text = "Status=" + result.Status + "; Payload=" + result.HasPayload + "\nDirectory=" + result.DirectoryPath + "\nReport=" + result.ReportPath +
                "\nRead=" + result.RecordsRead + "; Accepted=" + result.RecordsAccepted + "; Skipped=" + result.RecordsSkipped + "; Issues=" + result.IssueCount +
                "; CSV protected=" + result.CsvProtectedCellCount;
            workspace.SetStatus("Export: " + result.Status + "; Report=" + result.ReportPath);
        });
    }
}
