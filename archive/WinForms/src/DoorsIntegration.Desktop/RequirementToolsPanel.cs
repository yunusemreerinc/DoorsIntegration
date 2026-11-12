using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Desktop
{
    internal sealed class RequirementToolsPanel : UserControl
    {
        private readonly BatchWorkspacePanel batch;
        public RequirementToolsPanel(DesktopWorkspace workspace)
        {
            Name = "RequirementToolsPanel"; Dock = DockStyle.Fill;
            var tabs = new DarkBlueTabControl { Dock = DockStyle.Fill, Name = "RequirementToolTabs", ItemSize = new System.Drawing.Size(190, 38) };
            Add(tabs, "Read", new RequirementReadPanel(workspace));
            batch = new BatchWorkspacePanel(workspace); Add(tabs, "Batch import / update", batch);
            Add(tabs, "Quality", new QualityWorkspacePanel(workspace)); Add(tabs, "Coverage", new CoverageWorkspacePanel(workspace)); Controls.Add(tabs);
        }
        private static void Add(TabControl tabs, string title, Control panel)
        { var page = new TabPage(title) { BackColor = DarkBlueTheme.Surface, UseVisualStyleBackColor = false }; page.Controls.Add(panel); tabs.TabPages.Add(page); }
        public void InvalidateBatch() => batch.InvalidatePlan();
    }
    internal sealed class BatchWorkspacePanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox path = WorkspaceUi.Input("BatchInputPath"), repository = WorkspaceUi.Input("BatchRepository"), operation = WorkspaceUi.Input("BatchNamespace");
        private readonly ComboBox format = WorkspaceUi.Choice("BatchInputFormat", new[] { "JSONL", "CSV" });
        private readonly NumericUpDown attempts = WorkspaceUi.Number("BatchMaxAttempts", 3, 1, 5), timeout = WorkspaceUi.Number("BatchTimeout", 120, 1, 600),
            maxRecords = WorkspaceUi.Number("BatchMaxRecords", 10000, 1, 100000), page = WorkspaceUi.Number("BatchPage", 1, 1, 1000);
        private readonly CheckBox partial = WorkspaceUi.Check("BatchAllowPartial", "Process valid records despite other failures (non-atomic)");
        private readonly DataGridView grid = WorkspaceUi.Grid("BatchGrid", "Record", "Key", "Action", "Target", "Object #", "Status", "Code");
        private readonly RichTextBox report = WorkspaceUi.Output("BatchReport", 220);
        private readonly Button apply, retry;
        private BatchService service;
        private BatchPreview preview;
        private BatchReport result;
        public BatchWorkspacePanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "BatchWorkspacePanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Input file", path, "Format", format, WorkspaceUi.Button("BatchBrowse", "Browse...", (s, e) =>
            { using (var dialog = new OpenFileDialog { Filter = "Batch input|*.jsonl;*.csv|All files|*.*" }) if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.FileName; })));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Expected DOORS database ID", repository, "Stable operation namespace", operation));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Max records", maxRecords, "Lifetime attempts per key", attempts, "Per-call timeout sec", timeout));
            WorkspaceUi.Add(form, WorkspaceUi.Row(partial));
            apply = WorkspaceUi.Button("ApplyBatch", "Review and apply...", async (s, e) => await workspace.RunAsync("Reviewing batch intent...", async token =>
            {
                apply.Enabled = false; retry.Enabled = false; workspace.InvalidateCaches();
                if (preview == null || service == null) throw WorkspaceUi.Invalid("Preview the batch first.");
                result = await service.ApplyAsync(preview, token, Progress());
                report.Text = "Batch rows=" + result.RecordCount + "; Applied/historical=" + result.AppliedCount + "; Failed/not attempted=" + result.FailedCount +
                    "; Stopped=" + result.Stopped + "\nRows: " + result.RowsPath + "\nFailures: " + result.FailuresPath +
                    "\nRetry only offers safe failed/not-attempted records and requires a fresh preview and approval.\n" + ProcessingDisclosure.Statement;
                retry.Enabled = result.ReadRows().Any(r => r.CanRepreview); await ShowPageAsync(); workspace.SetStatus("Batch finished. Review per-row results; no rollback.");
            }));
            retry = WorkspaceUi.Button("RetryBatchPreview", "Preview safe failed records", async (s, e) => await workspace.RunAsync("Revalidating safe retry candidates...", async token =>
            {
                if (preview == null || result == null || service == null) throw WorkspaceUi.Invalid("No matching batch report is retained.");
                var old = preview; var replacement = await service.PreviewRetryAsync(old, result, token, Progress());
                preview = replacement; old.Dispose(); result = null; retry.Enabled = false; apply.Enabled = preview.CanApply; page.Value = 1; await ShowPageAsync(); ShowSummary();
            }));
            apply.Enabled = retry.Enabled = false;
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("PreviewBatch", "Preview / dry-run", async (s, e) => await workspace.RunAsync("Capturing and inspecting batch input without writing...", async token =>
            {
                InvalidatePlan(); service = workspace.Services.Batches.Create(workspace.Target(), workspace.Connection(), TimeSpan.FromSeconds((double)timeout.Value));
                IBatchRecordSource source = (string)format.SelectedItem == "CSV" ? (IBatchRecordSource)new CsvBatchSource(path.Text) : new JsonLinesBatchSource(path.Text);
                preview = await service.PreviewAsync(source, new BatchOptions(repository.Text, operation.Text, partial.Checked, (int)attempts.Value, maxRecords: (int)maxRecords.Value), token, Progress());
                page.Value = 1; apply.Enabled = preview.CanApply; await ShowPageAsync(); ShowSummary(); workspace.SetStatus("Batch dry-run complete. CanApply=" + preview.CanApply + ". Each DOORS record retains its native review/approval.");
            })), apply, retry, WorkspaceUi.Button("BatchFailureCsv", "Failure CSV...", async (s, e) => await workspace.RunAsync("Exporting local failure report...", token =>
            {
                if (result == null) throw WorkspaceUi.Invalid("No batch report available."); string destination = WorkspaceUi.SavePath(this, "New failure CSV", "csv");
                if (destination != null) BatchReportExporter.ExportFailuresCsv(result, destination); return Task.CompletedTask;
            }))));
            WorkspaceUi.Add(form, grid); WorkspaceUi.Add(form, WorkspaceUi.Row("Page (100 rows)", page,
                WorkspaceUi.Button("BatchShowPage", "Show page", async (s, e) => await workspace.RunAsync("Loading retained batch page...", token => ShowPageAsync())), WorkspaceUi.Button("BatchDetails", "Selected row details", (s, e) => Details())));
            WorkspaceUi.Add(form, report); report.Text = "CSV/JSONL → captured read-only preview → batch intent approval → fresh explicit DOORS review per record.\nStable namespace + key prevent replay in the shared local journal. Unknown/partial writes are never retried automatically.\n" + ProcessingDisclosure.Statement;
            foreach (var box in new[] { path, repository, operation }) box.TextChanged += (s, e) => InvalidatePlan();
            foreach (var number in new[] { attempts, timeout, maxRecords }) number.ValueChanged += (s, e) => InvalidatePlan();
            format.SelectedIndexChanged += (s, e) => InvalidatePlan(); partial.CheckedChanged += (s, e) => InvalidatePlan();
        }
        private IProgress<BatchProgress> Progress() => new Progress<BatchProgress>(p => workspace.SetStatus(p.Phase + "; Record=" + p.RecordNumber + "; Completed=" + p.Completed));
        public void InvalidatePlan() { preview?.Dispose(); preview = null; result = null; service = null; apply.Enabled = retry.Enabled = false; grid.Rows.Clear(); }
        private void ShowSummary()
        { report.Text = "Records=" + preview.RecordCount + "; Ready=" + preview.ReadyCount + "; Blocked=" + preview.BlockedCount + "; Complete=" + preview.IsComplete +
            "; CanApply=" + preview.CanApply + "\nFingerprint: " + preview.Fingerprint + "\nCaptured preview: " + preview.FilePath + "\nExpires UTC: " + preview.ExpiresAtUtc.ToString("O") +
            "\nUse row details to inspect old plain/rich values and requested changes. Batch is not atomic. Native per-record approval remains mandatory."; }
        private async Task ShowPageAsync()
        {
            grid.Rows.Clear(); if (preview == null) return; int skip = checked(((int)page.Value - 1) * 100);
            var plan = preview; var actual = result;
            if (actual == null) foreach (var row in await Task.Run(() => plan.ReadRows().Skip(skip).Take(100).ToArray()))
            { int i = grid.Rows.Add(row.RecordNumber, WorkspaceUi.Clip(row.Change?.Key), row.Change?.Action, WorkspaceUi.Clip(row.Change?.Target), row.Change?.ObjectId, row.Status, row.Code); grid.Rows[i].Tag = row; }
            else foreach (var row in await Task.Run(() => actual.ReadRows().Skip(skip).Take(100).ToArray()))
            { int i = grid.Rows.Add(row.RecordNumber, WorkspaceUi.Clip(row.Key), "", "", row.ObjectId, row.Status, row.Code); grid.Rows[i].Tag = row; }
        }
        private void Details()
        {
            if (grid.SelectedRows.Count == 0) return; object selected = grid.SelectedRows[0].Tag;
            if (selected is BatchRowResult actual) { report.Text = "Record=" + actual.RecordNumber + "; Key=" + actual.Key + "; Status=" + actual.Status + "; Code=" + actual.Code +
                "; Attempts=" + actual.Attempts + "; Object=" + actual.ObjectId + "; Audit=" + actual.AuditRecorded + "; CanRepreview=" + actual.CanRepreview + "\nReviewed stamp: " + actual.ReviewedStamp; return; }
            var row = selected as BatchPreviewRow; if (row == null) return;
            var text = new StringBuilder().AppendLine("Record=" + row.RecordNumber + "; Status=" + row.Status + "; Code=" + row.Code)
                .AppendLine("Repository=" + row.Inspection?.RepositoryId + "; Actor=" + row.Inspection?.Actor + "; Target ID=" + row.Inspection?.TargetId);
            if (row.Inspection != null)
            {
                foreach (var issue in row.Inspection.Issues) text.AppendLine("CHECK: " + issue);
                foreach (var old in row.Inspection.BeforeValues) text.AppendLine("BEFORE " + old.Name + "\n" + WorkspaceUi.Clip(old.PlainText, 65536) + "\nRAW RICH/OLE:\n" + WorkspaceUi.Clip(old.RichText, 65536));
            }
            if (row.Change != null) foreach (var value in row.Change.Attributes) text.AppendLine("REQUESTED " + value.Name + " [" + value.Format + "]\n" + WorkspaceUi.Clip(value.Value, 65536));
            report.Text = text.ToString();
        }
        protected override void Dispose(bool disposing) { if (disposing) preview?.Dispose(); base.Dispose(disposing); }
    }
    internal sealed class QualityWorkspacePanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox profilePath = WorkspaceUi.Input("QualityProfilePath"), repository = WorkspaceUi.Input("QualityRepository"), baseline = WorkspaceUi.Input("QualityBaselineLabel");
        private readonly RichTextBox report = WorkspaceUi.Output("QualityReport", 340);
        public QualityWorkspacePanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "QualityWorkspacePanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row("XML review profile", profilePath, WorkspaceUi.Button("QualityBrowse", "Browse...", (s, e) =>
            { using (var dialog = new OpenFileDialog { Filter = "Quality profile|*.xml" }) if (dialog.ShowDialog(this) == DialogResult.OK) profilePath.Text = dialog.FileName; })));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Caller-bound repository ID", repository, "Exact observed baseline label (baseline only)", baseline));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Uses the retained Requirements snapshot. Request the profile's mapped custom attributes in Read first. No DOORS call is made."));
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("RunQuality", "Analyze retained requirements", async (s, e) => await workspace.RunAsync("Running configured review checks offline...", async token =>
            {
                var snapshot = workspace.Data.Requirements; if (snapshot == null) throw WorkspaceUi.Invalid("Read requirements before quality analysis.");
                var profile = QualityProfile.LoadXml(profilePath.Text); string repo = repository.Text, label = baseline.Text;
                if (string.IsNullOrWhiteSpace(repo)) throw WorkspaceUi.Invalid("Bind the retained snapshot to an explicit repository ID.");
                string directory = Path.Combine(workspace.Services.ProcessingStorage.DirectoryPath, "analysis", "quality-" + Guid.NewGuid().ToString("N"));
                var context = new QualityContext("CallerBoundRepository;PathIdentities;RequirementRequest=" + snapshot.Summary.CorrelationId.ToString("N"), snapshot.Summary.IsCompleteForReadableObjects, DateTime.UtcNow);
                var summary = await Task.Run(() => { using (var output = new FileQualityReport(directory)) return workspace.Services.Quality.Evaluate(
                    ProcessingSnapshotAdapter.Requirements(snapshot, repo, label.Length == 0 ? null : label), profile, context, output, token); });
                report.Text = "Profile=" + profile.Id + "; Revision=" + profile.Revision + "; ExampleOnly=" + profile.ExampleOnly + "\nProfile fingerprint: " + profile.Fingerprint +
                    "\nArtifacts=" + summary.Artifacts + "; Checks=" + summary.Evaluations + "; Findings=" + summary.Findings + "; NotEvaluated=" + summary.NotEvaluated +
                    "; RuleErrors=" + summary.RuleErrors + "; FullyEvaluated=" + summary.FullyEvaluated + "; Cancelled=" + summary.Cancelled + "; Truncated=" + summary.Truncated +
                    "\nComplianceAssessed=False; ToolQualified=False\nReport: " + directory + "\n" + ProcessingDisclosure.Statement;
                workspace.SetStatus("Quality review complete: " + summary.Findings + " findings; " + summary.NotEvaluated + " not evaluated. Human review required.");
            }))));
            WorkspaceUi.Add(form, report); report.Text = ProcessingDisclosure.Statement + "\nExample review profiles are in scripts/quality. Map fields and thresholds to your approved project checklist; no standard objectives are inferred.";
        }
    }
    internal sealed class CoverageWorkspacePanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox repository = WorkspaceUi.Input("CoverageRepository"), baseline = WorkspaceUi.Input("CoverageBaselineLabel"), counterpart = WorkspaceUi.Input("CoverageTargetContainers", multiline: true);
        private readonly ComboBox direction = WorkspaceUi.Choice<CoverageDirection>("CoverageDirection"), versions = WorkspaceUi.Choice<CoverageVersionPolicy>("CoverageVersions");
        private readonly NumericUpDown minimum = WorkspaceUi.Number("CoverageMinimum", 1, 1, 1024);
        private readonly CheckBox identityConfirmed = WorkspaceUi.Check("CoverageRepositoryConfirmed", "I verified both snapshots belong to this repository/version (caller attestation)");
        private readonly RichTextBox report = WorkspaceUi.Output("CoverageReport", 270);
        public CoverageWorkspacePanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "CoverageWorkspacePanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Caller-bound repository ID", repository, "Exact observed baseline label (baseline only)", baseline));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Required link direction", direction, "Min distinct counterparts", minimum, "Version policy", versions));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Allowed counterpart module paths (one per line; blank = any)", counterpart));
            WorkspaceUi.Add(form, WorkspaceUi.Row(identityConfirmed));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Uses matching retained Requirements + Links snapshots and the Links snapshot's exact link module. No connection or automatic link creation."));
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("RunCoverage", "Analyze traceability coverage", async (s, e) => await workspace.RunAsync("Analyzing retained link coverage offline...", async token =>
            {
                var requirements = workspace.Data.Requirements; var links = workspace.Data.Links;
                if (requirements == null || links == null) throw WorkspaceUi.Invalid("Read both requirements and links before coverage analysis.");
                var r = requirements.Summary.Request; var l = links.Summary.Request;
                if (r.ModulePath != l.ModulePath || !SameBaseline(r.Baseline, l.Baseline)) throw WorkspaceUi.Invalid("Retained snapshots must use the same anchor module and baseline.");
                var selectedDirection = WorkspaceUi.Selected<CoverageDirection>(direction);
                if (l.Direction != LinkDirection.Both && l.Direction.ToString() != selectedDirection.ToString()) throw WorkspaceUi.Invalid("Read the required link direction or Both before this analysis.");
                string repo = repository.Text, label = baseline.Text;
                if (string.IsNullOrWhiteSpace(repo)) throw WorkspaceUi.Invalid("An explicit caller-bound repository ID is required.");
                var policy = new CoveragePolicy("required-trace", l.LinkModulePath, selectedDirection, (int)minimum.Value, WorkspaceUi.Lines(counterpart.Text), WorkspaceUi.Selected<CoverageVersionPolicy>(versions));
                bool complete = identityConfirmed.Checked && requirements.Summary.IsCompleteForReadableObjects && links.Summary.IsCompleteForReadableScope && (r.Baseline == null || label.Length > 0);
                var context = new CoverageContext(repo, complete, "CallerAttestedRepository=" + identityConfirmed.Checked + ";PathIdentities;Requirements=" + requirements.Summary.CorrelationId.ToString("N") + ";Links=" + links.Summary.CorrelationId.ToString("N"));
                string directory = Path.Combine(workspace.Services.ProcessingStorage.DirectoryPath, "analysis", "coverage-" + Guid.NewGuid().ToString("N"));
                var summary = await Task.Run(() => { using (var output = new FileCoverageReport(directory)) return workspace.Services.Coverage.Analyze(
                    ProcessingSnapshotAdapter.Nodes(requirements, repo, label.Length == 0 ? null : label), ProcessingSnapshotAdapter.Relations(links, repo), new[] { policy }, context, output, token); });
                report.Text = "Covered=" + summary.Covered + "; Uncovered=" + summary.Uncovered + "; Unknown=" + summary.Unknown + "; Excluded=" + summary.Excluded +
                    "; Duplicate observations=" + summary.DuplicateObservations + "\nKnown nodes=" + summary.KnownNodes + "; Relations read=" + summary.RelationsRead +
                    "; ScopeComplete=" + summary.ScopeComplete + "; Coverage=" + (summary.CoveragePercent.HasValue ? summary.CoveragePercent.Value.ToString("0.##") + "%" : "Unavailable for incomplete scope") +
                    "\nReport: " + directory + "\nCoverage applies only to the configured policy and retained, non-atomic snapshot scope.\n" + ProcessingDisclosure.Statement;
                workspace.SetStatus("Coverage review: " + summary.Covered + " covered; " + summary.Unknown + " unknown. ScopeComplete=" + summary.ScopeComplete);
            }))));
            WorkspaceUi.Add(form, report); report.Text = "Unresolved/echoed/deleted endpoints do not count as verified coverage. Incomplete scopes have Unknown results and no complete percentage.\n" + ProcessingDisclosure.Statement;
        }
        private static bool SameBaseline(RequirementBaseline a, RequirementBaseline b) => a == null ? b == null : b != null && a.Major == b.Major && a.Minor == b.Minor && a.Suffix == b.Suffix;
    }
}
