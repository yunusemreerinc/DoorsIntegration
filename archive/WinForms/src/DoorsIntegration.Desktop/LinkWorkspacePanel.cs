using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    internal sealed class LinkWorkspacePanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox module = WorkspaceUi.Input("LinkAnchorModule"), linkModule = WorkspaceUi.Input("LinkModulePath"),
            marker = WorkspaceUi.Input("SurrogateMarkerAttribute"), markerValue = WorkspaceUi.Input("SurrogateMarkerValue"), reference = WorkspaceUi.Input("SurrogateReferenceAttribute");
        private readonly ComboBox direction = WorkspaceUi.Choice("LinkDirection", new[] { "Both", "Incoming", "Outgoing" });
        private readonly BaselineFields baseline = new BaselineFields("Links");
        private readonly CheckBox open = WorkspaceUi.Check("LinksOpenIfNeeded", "Open modules read-only if needed", true),
            resolve = WorkspaceUi.Check("LinksResolveEndpoints", "Resolve endpoints", true), includeDeleted = WorkspaceUi.Check("LinksIncludeDeleted", "Include deleted objects"),
            surrogate = WorkspaceUi.Check("LinksUseSurrogate", "Use explicit surrogate mapping");
        private readonly NumericUpDown timeout = WorkspaceUi.Number("LinksTimeout", 120, 1, 86400), maxLinks = WorkspaceUi.Number("LinksMaxLinks", 100000, 1, 1000000),
            maxObjects = WorkspaceUi.Number("LinksMaxObjects", 1000000, 1, 1000000), maxModules = WorkspaceUi.Number("LinksMaxModules", 64, 0, 256),
            maxFile = WorkspaceUi.Number("LinksMaxFileMiB", 256, 1, 1024), page = WorkspaceUi.Number("LinksPage", 1, 1, 10000);
        private readonly DataGridView grid = WorkspaceUi.Grid("LinksGrid", "Sequence", "Direction", "Source module", "Source #", "Target module", "Target #", "Resolution", "Surrogate S/T");
        private readonly RichTextBox details = WorkspaceUi.Output("LinkDetails", 180);
        private readonly Label summary = new Label { AutoSize = true };
        public LinkWorkspacePanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "LinkWorkspacePanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Anchor formal module", module, "Link module", linkModule, "Direction", direction));
            WorkspaceUi.Add(form, baseline.Row); WorkspaceUi.Add(form, WorkspaceUi.Row(open, resolve, includeDeleted));
            WorkspaceUi.Add(form, WorkspaceUi.Row(surrogate, "Marker attribute", marker, "Exact marker value", markerValue, "External reference attribute (optional)", reference));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Timeout seconds", timeout, "Max objects", maxObjects, "Max links", maxLinks, "Max linked modules", maxModules, "Max file MiB", maxFile));
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("ReadLinks", "Read links", async (s, e) => await ReadAsync()),
                WorkspaceUi.Button("TraceabilityJson", "Traceability JSON...", async (s, e) => await ExportAsync(TraceabilityFormat.Json)),
                WorkspaceUi.Button("TraceabilityCsv", "Traceability CSV...", async (s, e) => await ExportAsync(TraceabilityFormat.Csv))));
            WorkspaceUi.Add(form, summary); WorkspaceUi.Add(form, grid);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Page (100 records)", page, WorkspaceUi.Button("LinksShowPage", "Show page", async (s, e) => await workspace.RunAsync("Reading cached link page...", t => LoadPageAsync())),
                WorkspaceUi.Button("LinkShowDetails", "Selected link details", async (s, e) => await DetailsAsync())));
            WorkspaceUi.Add(form, details); summary.Text = "Explicit link-module scope. Unknown/unresolved endpoints remain visible; no links are created or modified.";
            surrogate.CheckedChanged += (s, e) => RefreshMapping(); RefreshMapping();
            workspace.Data.LinksChanged += () => { grid.Rows.Clear(); details.Clear(); if (workspace.Data.Links == null) summary.Text = "No retained link snapshot. Read again after a write or custom DXL execution."; };
        }
        private void RefreshMapping() { marker.Enabled = markerValue.Enabled = reference.Enabled = surrogate.Checked; }
        private Task ReadAsync() => workspace.RunAsync("Reading Classic link modules...", async token =>
        {
            var request = new LinkReadRequest(module.Text, linkModule.Text, WorkspaceUi.Selected<LinkDirection>(direction), baseline.Value,
                open.Checked, resolve.Checked, includeDeleted.Checked, surrogate.Checked ? new SurrogateMapping(marker.Text, markerValue.Text, reference.Text.Length == 0 ? null : reference.Text) : null,
                (int)maxObjects.Value, (int)maxLinks.Value, (int)maxModules.Value, (long)maxFile.Value * 1048576,
                TimeSpan.FromSeconds((double)timeout.Value), workspace.Connection(), workspace.Target());
            grid.Rows.Clear(); details.Clear(); workspace.Data.SetLinks(null);
            var snapshot = await workspace.Services.Links.ReadAsync(request, token); workspace.Data.SetLinks(snapshot); page.Value = 1;
            var result = snapshot.Summary; page.Maximum = Math.Max(1, (result.RecordCount + 99) / 100);
            summary.Text = request.ModulePath + " → " + request.LinkModulePath + "; Module=" + result.ModuleStatus + "; Link module=" + result.LinkModuleStatus +
                "; Links=" + result.LinkCount + "; Source references=" + result.SourceReferenceCount + "; Unresolved=" + result.UnresolvedLinkCount +
                "; Denied=" + result.DeniedObjectCount + "; Deleted excluded=" + result.DeletedExcludedCount + "; Surrogate issues=" + result.SurrogateProblemCount +
                "; Truncated=" + result.IsTruncated + "; Enumeration failed=" + result.EnumerationFailed + "; Cleanup failures=" + result.CleanupFailureCount +
                "; Complete=" + result.IsCompleteForReadableScope + "; Request=" + result.CorrelationId.ToString("N");
            await LoadPageAsync(); workspace.SetStatus("Link snapshot retained. Complete=" + result.IsCompleteForReadableScope);
        });
        private async Task LoadPageAsync()
        {
            var snapshot = workspace.Data.Links ?? throw WorkspaceUi.Invalid("Read links first."); int selected = (int)page.Value - 1;
            var rows = await Task.Run(() => DesktopSnapshotPages.Links(snapshot, selected));
            grid.Rows.Clear(); foreach (var row in rows) grid.Rows.Add(row.Cast<object>().ToArray());
        }
        private Task DetailsAsync() => workspace.RunAsync("Reading cached link details...", async token =>
        {
            if (grid.CurrentRow == null) throw WorkspaceUi.Invalid("Select a link record first.");
            int sequence = int.Parse((string)grid.CurrentRow.Cells[0].Value, System.Globalization.CultureInfo.InvariantCulture);
            var snapshot = workspace.Data.Links ?? throw WorkspaceUi.Invalid("Read links first.");
            var link = await Task.Run(() => snapshot.ReadLinks().First(l => l.Sequence == sequence));
            details.Text = "Kind=" + link.Kind + "; Direction=" + link.Direction + "; Resolution=" + link.Resolution + "; Echoed=" + link.IsEchoed +
                "\nLink module=" + link.LinkModulePath + "; Link module resolution=" + link.LinkModuleResolution +
                "\nSOURCE\n" + Endpoint(link.Source) + "\nTARGET\n" + Endpoint(link.Target);
        });
        private static string Endpoint(LinkEndpoint endpoint) => endpoint.ModulePath + "\nVersion=" + endpoint.VersionKind + " " + endpoint.Version +
            "; Absolute=" + endpoint.AbsoluteNumber + "; Identifier=" + endpoint.ObjectIdentifier + "; Resolution=" + endpoint.Resolution +
            "\nSurrogate=" + endpoint.SurrogateStatus + "; External=" + endpoint.ExternalReference + "; Reference status=" + endpoint.ReferenceStatus;
        private async Task ExportAsync(TraceabilityFormat format)
        {
            string destination = WorkspaceUi.SavePath(this, "Save traceability output", format == TraceabilityFormat.Json ? "json" : "csv");
            if (destination == null) return;
            await workspace.RunAsync("Exporting retained link snapshot...", async token =>
            {
                var snapshot = workspace.Data.Links ?? throw WorkspaceUi.Invalid("Read links first.");
                await Task.Run(() => workspace.Services.Traceability.ExportFile(destination, snapshot, format, overwrite: true), token);
                workspace.SetStatus("Traceability " + format + " exported; unresolved/reference-only records retained.");
            });
        }
    }
}
