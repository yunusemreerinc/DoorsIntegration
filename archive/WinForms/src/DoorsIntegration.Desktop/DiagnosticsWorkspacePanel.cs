using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    internal sealed class DiagnosticsWorkspacePanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly DataGridView steps = WorkspaceUi.Grid("HealthCheckSteps", "Step", "Status", "Error code", "Explanation");
        private readonly RichTextBox report = WorkspaceUi.Output("HealthCheckReport", 240);
        public DiagnosticsWorkspacePanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "DiagnosticsWorkspacePanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("DiagnoseOffline", "Diagnose offline (no connection)", async (s, e) => await DiagnoseAsync(false)),
                WorkspaceUi.Button("DiagnoseLive", "Ping / diagnose live", async (s, e) => await DiagnoseAsync(true)),
                WorkspaceUi.Button("SaveDiagnosticReport", "Save report...", async (s, e) =>
                {
                    string path = WorkspaceUi.SavePath(this, "Save diagnostic report", "txt");
                    if (path != null) await workspace.RunAsync("Saving diagnostic report...", async token =>
                    { string text = report.Text; await Task.Run(() => File.WriteAllText(path, text, new UTF8Encoding(false, true)), token); workspace.SetStatus("Diagnostic report saved."); });
                })));
            WorkspaceUi.Add(form, steps); WorkspaceUi.Add(form, report);
            report.Text = "Offline: both COM registry views and registered binary metadata. Live: COM connection, DXL execution, Result response and cleanup on one STA. The live diagnostic does not modify database data.\nRegistered binary version does not prove the running client's version. Cancellation cannot forcibly stop a synchronous COM diagnostic.";
        }
        private Task DiagnoseAsync(bool live) => workspace.RunAsync(live ? "Running live step-by-step health checks..." : "Inspecting offline COM registration...", async token =>
        {
            var target = workspace.Target(); var connection = workspace.Connection(); var id = Guid.NewGuid(); steps.Rows.Clear();
            var progress = new Progress<DoorsHealthStepResult>(p => workspace.SetStatus("Diagnostic: " + p.Step + " " + p.Status));
            var result = await workspace.Services.Sta.RunAsync(() => workspace.Services.Health.Run(target, id, live, connection, p => ((IProgress<DoorsHealthStepResult>)progress).Report(p)));
            foreach (var step in result.Steps) steps.Rows.Add(step.Step, step.Status, step.ErrorCode, step.Message);
            var environment = result.Environment;
            var text = new StringBuilder().AppendLine("Request=" + id.ToString("N") + "; Target=" + target + "; Live=" + live + "; ExitCode=" + result.ExitCode)
                .AppendLine("OS64=" + environment.Is64BitOperatingSystem + "; Process64=" + environment.Is64BitProcess + "; Apartment=" + environment.Apartment);
            foreach (var registration in environment.Registrations)
                text.AppendLine("\n" + registration.Architecture + ": " + registration.State + "; CLSID=" + registration.Clsid + "\nServer=" + registration.ServerPath +
                    "\nFile version=" + registration.FileVersion + "; PE=" + registration.PeMachine + "; Matches profile=" + registration.MatchesTarget(target) + "\n" + registration.Note);
            foreach (var step in result.Steps) text.AppendLine("\n" + step.Step + ": " + step.Status + " " + step.ErrorCode + "\n" + step.Message);
            report.Text = text.ToString(); workspace.SetStatus("Diagnostic completed. ExitCode=" + result.ExitCode);
        });
    }
}
