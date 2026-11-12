using System;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    internal sealed class ModuleCreationPanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox folder = WorkspaceUi.Input("CreateModuleFolder"), moduleName = WorkspaceUi.Input("CreateModuleName"),
            description = WorkspaceUi.Input("CreateModuleDescription", "", true);
        private readonly NumericUpDown timeout = WorkspaceUi.Number("CreateModuleTimeout", 30, 1, 600);
        private readonly RichTextBox report = WorkspaceUi.Output("CreateModuleReport", 250);
        private readonly Button apply;
        private FormalModuleCreationPreview preview;
        public ModuleCreationPanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "ModuleCreationPanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Target folder", folder, "Module name", moduleName));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Description", description, "Timeout seconds", timeout));
            apply = WorkspaceUi.Button("ApplyModuleCreation", "Review and create...", async (s, e) => await workspace.RunAsync("Requesting explicit module creation approval...", async token =>
            {
                var approved = preview ?? throw WorkspaceUi.Invalid("Obtain a fresh preview first."); InvalidatePreview();
                var result = await workspace.CreateAsync(approved, token);
                report.Text = "Outcome=" + result.Outcome + "\nPath=" + result.FullPath + "\nModule ID=" + result.ModuleId +
                    "\nQualified ID=" + result.QualifiedModuleId + "\nCleanup=" + result.Cleanup + "\n" + result.Checks.Summary +
                    "\nRequest=" + result.CorrelationId.ToString("N") + "\nInspect any uncertain result before obtaining another preview. No automatic retry.";
                workspace.SetStatus("Module creation: " + result.Outcome + "; ID=" + result.QualifiedModuleId + "; Cleanup=" + result.Cleanup);
            }));
            apply.Enabled = false;
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("PreviewModuleCreation", "Dry-run / preview", async (s, e) => await workspace.RunAsync("Previewing module creation (no mutation)...", async token =>
            {
                InvalidatePreview();
                preview = await workspace.Services.Modules.PreviewFormalModuleCreationAsync(new FormalModuleCreateRequest(folder.Text,
                    moduleName.Text, description.Text, workspace.Target(), workspace.Connection(), TimeSpan.FromSeconds((double)timeout.Value)), token);
                report.Text = DesktopWriteReview.From(preview).Body; apply.Enabled = preview.CanCreate;
                workspace.SetStatus("Module dry-run: CanCreate=" + preview.CanCreate);
            })), apply));
            WorkspaceUi.Add(form, report);
            folder.TextChanged += (s, e) => InvalidatePreview(); moduleName.TextChanged += (s, e) => InvalidatePreview();
            description.TextChanged += (s, e) => InvalidatePreview(); timeout.ValueChanged += (s, e) => InvalidatePreview();
        }
        public void InvalidatePreview() { preview = null; if (apply != null) apply.Enabled = false; }
    }
}
