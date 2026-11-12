using System;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    internal sealed class LinkWritePanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox source = WorkspaceUi.Input("WriteLinkSourceModule"), target = WorkspaceUi.Input("WriteLinkTargetModule"),
            linkModule = WorkspaceUi.Input("WriteLinkModule");
        private readonly NumericUpDown sourceAbs = WorkspaceUi.Number("WriteLinkSourceAbsolute", 1, 1, int.MaxValue),
            targetAbs = WorkspaceUi.Number("WriteLinkTargetAbsolute", 1, 1, int.MaxValue), timeout = WorkspaceUi.Number("WriteLinkTimeout", 120, 1, 600),
            maxLinks = WorkspaceUi.Number("WriteLinkMaxTraceLinks", 2048, 1, 2048);
        private readonly ComboBox action = WorkspaceUi.Choice<LinkWriteAction>("LinkWriteAction");
        private readonly RichTextBox report = WorkspaceUi.Output("LinkWriteReport", 340);
        private readonly Button apply;
        private LinkWritePreview preview;
        public LinkWritePanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "LinkWritePanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Source formal module", source, "Absolute #", sourceAbs));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Target formal module", target, "Absolute #", targetAbs));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Existing link module", linkModule, "Action", action));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Timeout seconds", timeout, "Max trace edges", maxLinks));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Close the involved modules in DOORS before preview/apply. Current versions and an existing linkset are required."));
            apply = WorkspaceUi.Button("ApplyLinkWrite", "Review and apply...", async (s, e) => await workspace.RunAsync("Reviewing link change...", async token =>
            {
                var approved = preview; InvalidatePreview();
                if (approved == null) throw WorkspaceUi.Invalid("Preview the link change first.");
                var result = await workspace.ApplyLinkAsync(approved, token);
                var text = new StringBuilder(DesktopWriteReview.From(approved).Body).AppendLine("\nRESULT: " + result.Outcome +
                    "; Persistence=" + result.Persistence + "; Cleanup=" + result.Cleanup + "; Audit=" + result.AuditRecorded)
                    .AppendLine("After verified: " + result.AfterVerified + "; Scope: " + result.TraceabilityScope);
                if (result.Comparison != null)
                {
                    foreach (var edge in result.Comparison.Added) text.AppendLine("ADDED: " + edge.TargetModulePath + " #" + edge.TargetAbsoluteNumber + " " + edge.VersionKind + " " + edge.Version);
                    foreach (var edge in result.Comparison.Removed) text.AppendLine("REMOVED: " + edge.TargetModulePath + " #" + edge.TargetAbsoluteNumber + " " + edge.VersionKind + " " + edge.Version);
                }
                else text.AppendLine("After trace is unavailable; no successful comparison was inferred.");
                foreach (var issue in result.Issues) text.AppendLine("ISSUE: " + issue);
                report.Text = text.ToString(); workspace.SetStatus("Link " + result.Outcome + "; Saved=" + result.Persistence + "; Cleanup=" + result.Cleanup + "; Success=" + result.Succeeded + ". No automatic retry.");
            }));
            apply.Enabled = false;
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("PreviewLinkWrite", "Preview / dry-run", async (s, e) =>
                await workspace.RunAsync("Inspecting link change without writing...", async token =>
                {
                    InvalidatePreview();
                    var request = new LinkWriteRequest(WorkspaceUi.Selected<LinkWriteAction>(action), source.Text, (int)sourceAbs.Value,
                        target.Text, (int)targetAbs.Value, linkModule.Text, workspace.Target(), workspace.Connection(),
                        TimeSpan.FromSeconds((double)timeout.Value), (int)maxLinks.Value);
                    preview = await workspace.Services.Links.PreviewWriteAsync(request, token);
                    report.Text = DesktopWriteReview.From(preview).Body; apply.Enabled = preview.CanApply;
                    workspace.SetStatus("Link dry-run: CanApply=" + preview.CanApply + ". Apply requires explicit approval; removal requires a second approval.");
                })), apply));
            WorkspaceUi.Add(form, report);
            foreach (var box in new[] { source, target, linkModule }) box.TextChanged += (s, e) => InvalidatePreview();
            foreach (var number in new[] { sourceAbs, targetAbs, timeout, maxLinks }) number.ValueChanged += (s, e) => InvalidatePreview();
            action.SelectedIndexChanged += (s, e) => InvalidatePreview();
        }
        public void InvalidatePreview() { preview = null; apply.Enabled = false; }
    }
}
