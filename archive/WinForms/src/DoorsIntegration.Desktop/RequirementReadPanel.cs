using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    internal sealed class RequirementReadPanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox module = WorkspaceUi.Input("ReadModulePath"), attributes = WorkspaceUi.Input("ReadCustomAttributes", "", true);
        private readonly BaselineFields baseline = new BaselineFields("Requirements");
        private readonly ComboBox deleted = WorkspaceUi.Choice<DeletedObjectMode>("ReadDeletedMode");
        private readonly CheckBox allowDxl = WorkspaceUi.Check("ReadAllowAttributeDxl", "Evaluate attribute DXL (may run installed code)"),
            open = WorkspaceUi.Check("ReadOpenIfNeeded", "Open selected version read-only if needed", true);
        private readonly NumericUpDown timeout = WorkspaceUi.Number("ReadTimeoutSeconds", 120, 1, 86400),
            objects = WorkspaceUi.Number("ReadMaxObjects", 1000000, 1, 1000000),
            field = WorkspaceUi.Number("ReadMaxFieldCharacters", 4194304, 1, 16777216),
            objectSize = WorkspaceUi.Number("ReadMaxObjectCharacters", 16777216, 1024, 67108864),
            file = WorkspaceUi.Number("ReadMaxFileMiB", 256, 1, 1024), page = WorkspaceUi.Number("RequirementPage", 1, 1, 10000);
        private readonly DataGridView grid = WorkspaceUi.Grid("RequirementsGrid", "Absolute", "Identifier", "Number", "Parent", "Level", "Deleted", "Heading", "Text");
        private readonly RichTextBox details = WorkspaceUi.Output("RequirementDetails", 180);
        private readonly Label summary = new Label { AutoSize = true };
        private readonly TextBox valueName = WorkspaceUi.Input("RequirementSaveValueName", "Object Text");
        private readonly CheckBox saveRich = WorkspaceUi.Check("RequirementSaveRich", "Save raw rich text/OLE");
        public RequirementReadPanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "RequirementReadPanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Formal module", module, WorkspaceUi.Button("ReadRequirements", "Read requirements", async (s, e) => await ReadAsync())));
            WorkspaceUi.Add(form, baseline.Row);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Deleted objects", deleted, open, allowDxl));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Custom attributes (one per line)", attributes));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Timeout seconds", timeout, "Max objects", objects, "Max field chars", field, "Max object chars", objectSize, "Max file MiB", file));
            WorkspaceUi.Add(form, summary); WorkspaceUi.Add(form, grid);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Page (100 objects)", page, WorkspaceUi.Button("RequirementsShowPage", "Show page", async (s, e) => await workspace.RunAsync("Reading cached requirement page...", t => LoadPageAsync())),
                WorkspaceUi.Button("RequirementShowDetails", "Selected object details", async (s, e) => await ShowDetailsAsync())));
            WorkspaceUi.Add(form, details);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Attribute to save", valueName, saveRich, WorkspaceUi.Button("RequirementSaveValue", "Save selected value...", async (s, e) => await SaveValueAsync())));
            summary.Text = "Read explicitly. Grids show bounded previews; Export includes the complete retained snapshot. Rich/OLE is displayed as raw text.";
            workspace.Data.RequirementsChanged += () => { grid.Rows.Clear(); details.Clear(); if (workspace.Data.Requirements == null) summary.Text = "No retained requirement snapshot. Read again after a write or custom DXL execution."; };
        }
        private Task ReadAsync() => workspace.RunAsync("Reading formal module...", async token =>
        {
            var request = new RequirementReadRequest(module.Text, WorkspaceUi.Lines(attributes.Text), baseline.Value,
                WorkspaceUi.Selected<DeletedObjectMode>(deleted), allowDxl.Checked, open.Checked, (int)objects.Value,
                (int)field.Value, (int)objectSize.Value, (long)file.Value * 1048576, TimeSpan.FromSeconds((double)timeout.Value), workspace.Connection(), workspace.Target());
            if (request.AllowAttributeDxl)
            {
                var review = new DesktopWriteReview("Review installed attribute DXL evaluation", request.ModulePath,
                    "Selected calculated attributes can run installed DXL code. Its effects cannot be inferred or rolled back by this reader.\nModule: " + request.ModulePath +
                    "\nProfile: " + request.Target + "; Connection: " + request.ConnectionMode + "\nRequested custom attributes:\n" + string.Join("\n", request.CustomAttributes),
                    DateTime.UtcNow.AddMinutes(5), true);
                if (!await workspace.Services.Confirmation.ReviewAsync(review, token)) { workspace.SetStatus("Attribute DXL evaluation declined; no read dispatched."); return; }
                token.ThrowIfCancellationRequested(); workspace.InvalidateCaches();
            }
            grid.Rows.Clear(); details.Clear(); workspace.Data.SetRequirements(null);
            var snapshot = await workspace.Services.Requirements.ReadAsync(request, token);
            workspace.Data.SetRequirements(snapshot); page.Value = 1;
            var result = snapshot.Summary;
            page.Maximum = Math.Max(1, (result.ObjectCount + 99) / 100);
            summary.Text = request.ModulePath + "; Version=" + (request.Baseline == null ? "Current" : request.Baseline.Major + "." + request.Baseline.Minor + " " + request.Baseline.Suffix) +
                "; Module=" + result.ModuleStatus + "; Objects=" + result.ObjectCount + "; Complete=" + result.IsCompleteForReadableObjects +
                "; Denied=" + result.DeniedObjectCount + "; Deleted excluded=" + result.DeletedExcludedCount + "; Unavailable=" + result.UnavailableObjectCount +
                "; Field issues=" + result.FieldProblemCount + "; Truncated=" + result.IsTruncated + "; Enumeration failed=" + result.EnumerationFailed +
                "; Cleanup=" + result.CleanupStatus + "; Request=" + result.CorrelationId.ToString("N");
            await LoadPageAsync(); workspace.SetStatus("Requirement snapshot retained. " + result.ModuleStatus + "; Complete=" + result.IsCompleteForReadableObjects);
        });
        private async Task LoadPageAsync()
        {
            var snapshot = workspace.Data.Requirements ?? throw WorkspaceUi.Invalid("Read requirements first.");
            int selected = (int)page.Value - 1;
            var rows = await Task.Run(() => DesktopSnapshotPages.Requirements(snapshot, selected));
            grid.Rows.Clear(); foreach (var row in rows) grid.Rows.Add(row.Cast<object>().ToArray());
        }
        private int SelectedAbsolute() => grid.CurrentRow == null ? throw WorkspaceUi.Invalid("Select an object first.") : int.Parse((string)grid.CurrentRow.Cells[0].Value, System.Globalization.CultureInfo.InvariantCulture);
        private Task<RequirementRecord> SelectedObjectAsync()
        {
            int absolute = SelectedAbsolute(); var snapshot = workspace.Data.Requirements ?? throw WorkspaceUi.Invalid("Read requirements first.");
            return Task.Run(() => snapshot.ReadObjects().First(o => o.AbsoluteNumber == absolute));
        }
        private Task ShowDetailsAsync() => workspace.RunAsync("Reading cached object details...", async token =>
        {
            var value = await SelectedObjectAsync();
            var text = new StringBuilder().AppendLine("Absolute: " + value.AbsoluteNumber + "; Identifier: " + value.ObjectIdentifier + "; Number: " + value.ObjectNumber + "; Number status: " + value.NumberStatus)
                .AppendLine("Parent: " + value.ParentAbsoluteNumber + "; Parent status: " + value.ParentStatus + "; Level: " + value.HierarchyLevel + "; Deleted: " + value.IsDeleted);
            foreach (var attribute in value.Values.Values)
                text.AppendLine("\n" + attribute.Name + " [" + attribute.TypeName + "] Plain=" + attribute.PlainStatus + "; Rich=" + attribute.RichTextStatus)
                    .AppendLine(WorkspaceUi.Clip(attribute.PlainText, 4096)).AppendLine("RAW RICH/OLE:\n" + WorkspaceUi.Clip(attribute.RichTextWithOle, 4096));
            details.Text = text.ToString();
        });
        private async Task SaveValueAsync()
        {
            string path = WorkspaceUi.SavePath(this, "Save selected attribute representation", saveRich.Checked ? "rtf" : "txt");
            if (path == null) return;
            await workspace.RunAsync("Saving complete attribute representation...", async token =>
            {
                string name = valueName.Text; bool rich = saveRich.Checked; var value = await SelectedObjectAsync();
                if (!value.Values.TryGetValue(name, out var attribute)) throw WorkspaceUi.Invalid("This attribute was not read.");
                if ((rich ? attribute.RichTextStatus : attribute.PlainStatus) != RequirementValueStatus.Readable) throw WorkspaceUi.Invalid("The selected representation is unavailable: " + (rich ? attribute.RichTextStatus : attribute.PlainStatus));
                await Task.Run(() => File.WriteAllText(path, rich ? attribute.RichTextWithOle : attribute.PlainText, new UTF8Encoding(false, true)), token);
                workspace.SetStatus("Complete representation saved. DOORS rich text may be a fragment, not a standalone RTF document.");
            });
        }
    }
}
