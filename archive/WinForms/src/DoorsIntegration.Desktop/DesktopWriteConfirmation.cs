using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Desktop
{
    public sealed class DesktopWriteReview
    {
        public DesktopWriteReview(string title, string target, string body, DateTime expiresAtUtc, bool canApply)
        { Title = title; Target = target; Body = body; ExpiresAtUtc = expiresAtUtc; CanApply = canApply; }
        public string Title { get; }
        public string Target { get; }
        public string Body { get; }
        public DateTime ExpiresAtUtc { get; }
        public bool CanApply { get; }
        public static DesktopWriteReview From(RequirementWritePreview preview)
        {
            var request = preview.Request; var inspection = preview.Inspection; var schema = inspection.Schema;
            var text = new StringBuilder().AppendLine("This operation changes the DOORS database. No automatic rollback or retry.")
                .AppendLine("Module: " + request.Target.ModulePath).AppendLine("Database: " + schema.DatabaseId + "; Actor: " + schema.UserName)
                .AppendLine("Module ID: " + schema.QualifiedModuleId).AppendLine("Profile: " + DoorsVersions.Format(request.Target.ClientVersion) + "; Mode: " + request.Target.ConnectionMode)
                .AppendLine("Object: " + (request.AbsoluteNumber?.ToString() ?? "CREATE NEW") + "; Position: " + request.Position.Kind + "; Anchor: " + request.Position.AnchorAbsoluteNumber)
                .AppendLine("Required fields: " + string.Join(", ", request.RequiredAttributes)).AppendLine("Fingerprint: " + preview.ApprovalFingerprint)
                .AppendLine("Expires UTC: " + preview.ExpiresAtUtc.ToString("O"));
            foreach (var issue in schema.Issues) text.AppendLine(issue.Severity + ": " + issue.Code + " " + issue.AttributeName);
            foreach (var change in request.Changes)
            {
                var before = inspection.BeforeValues.FirstOrDefault(v => v.Name == change.Name);
                text.AppendLine("\nATTRIBUTE: " + change.Name + "; Format: " + change.Format);
                if (before != null) text.AppendLine("BEFORE PLAIN:\n" + before.PlainText).AppendLine("BEFORE RICH/OLE (raw):\n" + before.RichTextWithOle);
                text.AppendLine("REQUESTED:\n" + change.Value);
                if (change.ExpectedPlainText != null) text.AppendLine("EXPECTED PLAIN:\n" + change.ExpectedPlainText);
            }
            text.AppendLine("\nSteps: " + string.Join(" → ", inspection.Steps.Select(s => s.Kind + " " + s.AttributeName)));
            return new DesktopWriteReview("Review requirement write", request.Target.ModulePath, text.ToString(), preview.ExpiresAtUtc, preview.CanApply);
        }
        public static DesktopWriteReview From(FormalModuleCreationPreview preview) => new DesktopWriteReview("Review formal module creation",
            preview.Request.FullPath, "This operation creates a formal module. No automatic rollback or retry.\nTarget: " + preview.Request.FullPath +
            "\nDescription:\n" + preview.Request.Description + "\nDatabase: " + preview.Checks.DatabaseId + "; Actor: " + preview.Checks.UserName +
            "\nFolder ID: " + preview.Checks.FolderId + "\nProfile: " + DoorsVersions.Format(preview.Request.Target) + "; Mode: " + preview.Request.ConnectionMode +
            "\n" + preview.Checks.Summary + "\nFingerprint: " + preview.ApprovalFingerprint + "\nExpires UTC: " + preview.ExpiresAtUtc.ToString("O"),
            preview.ExpiresAtUtc, preview.CanCreate);
        public static DesktopWriteReview From(LinkWritePreview preview, bool removal = false)
        {
            var request = preview.Request; var inspection = preview.Inspection;
            string target = (removal ? "DELETE " : "") + request.DisplayTarget;
            var body = new StringBuilder().AppendLine(removal ? "SEPARATE REMOVAL APPROVAL: permanently remove this single link. Link deletion cannot be undone." :
                "Review link change: " + request.Action + ". Applying changes the DOORS database.")
                .AppendLine(request.DisplayTarget).AppendLine("Current versions only. Existing linkset required. No automatic rollback or retry.")
                .AppendLine("Database: " + inspection.DatabaseId + "; Actor: " + inspection.UserName)
                .AppendLine("Module IDs: " + inspection.SourceModuleId + " -> " + inspection.TargetModuleId + "; Link module: " + inspection.LinkModuleId)
                .AppendLine("Object identifiers: " + inspection.SourceIdentifier + " -> " + inspection.TargetIdentifier)
                .AppendLine("Profile: " + DoorsVersions.Format(request.ClientVersion) + "; Mode: " + request.ConnectionMode)
                .AppendLine("Exclusive source/link-module locks and object permissions are rechecked at commit.")
                .AppendLine("Trace scope: " + inspection.TraceabilityScope).AppendLine("Before edges: " + inspection.Before.Count)
                .AppendLine("Expected change: " + (request.Action == LinkWriteAction.Create ? "one edge added; none removed" : "one edge removed; none added"))
                .AppendLine("Fingerprint: " + preview.ApprovalFingerprint).AppendLine("Expires UTC: " + preview.ExpiresAtUtc.ToString("O"));
            foreach (var issue in inspection.Issues) body.AppendLine("BLOCKED: " + issue);
            foreach (var edge in inspection.Before) body.AppendLine("BEFORE: " + edge.TargetModulePath + " #" + edge.TargetAbsoluteNumber +
                "; ID=" + edge.TargetModuleId + "; " + edge.VersionKind + " " + edge.Version);
            return new DesktopWriteReview(removal ? "Explicit link removal approval" : "Review link change", target, body.ToString(),
                preview.ExpiresAtUtc, preview.CanApply && (!removal || request.Action == LinkWriteAction.Remove));
        }
    }

    public interface IDesktopReviewDialog
    {
        bool Show(Control owner, DesktopWriteReview review, CancellationToken cancellationToken);
    }

    // No window attached, hidden/disposed owner, cancellation, expiry or an unavailable plan always means denial.
    public sealed class DesktopWriteConfirmation : IRequirementWriteConfirmation, IModuleCreationConfirmation, ILinkWriteConfirmation, IBatchConfirmation
    {
        private readonly IDesktopReviewDialog dialog;
        private Control owner;
        private readonly ConcurrentDictionary<TaskCompletionSource<bool>, Control> pending = new ConcurrentDictionary<TaskCompletionSource<bool>, Control>();
        public DesktopWriteConfirmation(IDesktopReviewDialog dialog) { this.dialog = dialog ?? throw new ArgumentNullException(nameof(dialog)); }
        public IDisposable Attach(Control window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            if (Interlocked.CompareExchange(ref owner, window, null) != null) throw new InvalidOperationException("A confirmation owner is already attached.");
            return new Binding(this, window);
        }
        public Task<bool> ConfirmAsync(RequirementWritePreview preview, CancellationToken cancellationToken) => ReviewAsync(DesktopWriteReview.From(preview), cancellationToken);
        public Task<bool> ConfirmAsync(FormalModuleCreationPreview preview, CancellationToken cancellationToken) => ReviewAsync(DesktopWriteReview.From(preview), cancellationToken);
        public Task<bool> ConfirmAsync(LinkWritePreview preview, CancellationToken cancellationToken) => ReviewAsync(DesktopWriteReview.From(preview), cancellationToken);
        public Task<bool> ConfirmRemovalAsync(LinkWritePreview preview, CancellationToken cancellationToken) => ReviewAsync(DesktopWriteReview.From(preview, true), cancellationToken);
        public Task<bool> ConfirmAsync(BatchPreview preview, CancellationToken token)
        {
            var body = new StringBuilder().AppendLine("Review captured batch intent. Rows are independent writes; no atomic transaction or rollback.")
                .AppendLine("The DOORS adapter will request a fresh native preview and explicit confirmation for each record.")
                .AppendLine("Repository: " + preview.Options.RepositoryId + "; Namespace: " + preview.Options.OperationNamespace)
                .AppendLine("Records=" + preview.RecordCount + "; Ready=" + preview.ReadyCount + "; Blocked=" + preview.BlockedCount + "; Partial processing=" + preview.Options.ContinueOnError)
                .AppendLine("Full immutable preview: " + preview.FilePath).AppendLine("Fingerprint: " + preview.Fingerprint)
                .AppendLine("Retry: only definite no-mutation transient failures; max lifetime attempts per key=" + preview.Options.MaxAttempts)
                .AppendLine(ProcessingDisclosure.Statement).AppendLine("First 100 rows follow; review all rows in the batch preview/report before applying.");
            foreach (var row in preview.ReadRows().Take(100))
            {
                body.AppendLine("\nRecord " + row.RecordNumber + "; Key=" + row.Change?.Key + "; Status=" + row.Status + "; Code=" + row.Code)
                    .AppendLine(row.Change?.Action + " " + row.Change?.Target + " #" + row.Change?.ObjectId).AppendLine("Payload hash: " + row.Change?.PayloadHash);
                if (row.Change != null) foreach (var value in row.Change.Attributes) body.AppendLine(value.Name + " [" + value.Format + "]; requested characters=" + value.Value.Length);
            }
            return ReviewAsync(new DesktopWriteReview("Review batch intent", preview.Options.OperationNamespace, body.ToString(), preview.ExpiresAtUtc, preview.CanApply), token);
        }
        public Task<bool> ReviewAsync(DesktopWriteReview review, CancellationToken cancellationToken)
        {
            var window = Volatile.Read(ref owner);
            if (cancellationToken.IsCancellationRequested || !review.CanApply || review.ExpiresAtUtc <= DateTime.UtcNow ||
                window == null || window.IsDisposed || !window.IsHandleCreated) return Task.FromResult(false);
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.TryAdd(result, window);
            try
            {
                window.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (result.Task.IsCompleted || window.IsDisposed || !window.Visible || !ReferenceEquals(Volatile.Read(ref owner), window) ||
                            cancellationToken.IsCancellationRequested || review.ExpiresAtUtc <= DateTime.UtcNow) { result.TrySetResult(false); return; }
                        bool approved = dialog.Show(window, review, cancellationToken);
                        result.TrySetResult(approved && !cancellationToken.IsCancellationRequested && review.ExpiresAtUtc > DateTime.UtcNow &&
                            ReferenceEquals(Volatile.Read(ref owner), window) && !window.IsDisposed);
                    }
                    catch (Exception ex) { result.TrySetException(ex); }
                }));
            }
            catch (InvalidOperationException) { result.TrySetResult(false); }
            return AwaitReviewAsync(result, review.ExpiresAtUtc, cancellationToken);
        }
        private async Task<bool> AwaitReviewAsync(TaskCompletionSource<bool> result, DateTime expiresAtUtc, CancellationToken token)
        {
            using (var expiry = new CancellationTokenSource())
            using (token.Register(() => result.TrySetResult(false)))
            {
                try
                {
                    var remaining = expiresAtUtc - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) result.TrySetResult(false);
                    else
                    {
                        var timeout = Task.Delay(remaining, expiry.Token);
                        if (await Task.WhenAny(result.Task, timeout).ConfigureAwait(false) == timeout) result.TrySetResult(false);
                    }
                    return await result.Task.ConfigureAwait(false);
                }
                finally { expiry.Cancel(); pending.TryRemove(result, out var ignored); }
            }
        }
        private sealed class Binding : IDisposable
        {
            private readonly DesktopWriteConfirmation parent; private readonly Control window;
            public Binding(DesktopWriteConfirmation parent, Control window) { this.parent = parent; this.window = window; window.Disposed += OnDisposed; }
            private void OnDisposed(object sender, EventArgs args) { Dispose(); }
            public void Dispose()
            {
                window.Disposed -= OnDisposed; Interlocked.CompareExchange(ref parent.owner, null, window);
                foreach (var item in parent.pending) if (ReferenceEquals(item.Value, window)) item.Key.TrySetResult(false);
            }
        }
    }

    public sealed class DesktopReviewDialog : IDesktopReviewDialog
    {
        public bool Show(Control owner, DesktopWriteReview review, CancellationToken cancellationToken)
        {
            using (var form = new Form { Text = review.Title, Width = 900, Height = 740, MinimumSize = new System.Drawing.Size(650, 480),
                StartPosition = FormStartPosition.CenterParent, BackColor = DarkBlueTheme.Surface, ForeColor = DarkBlueTheme.Text, ShowInTaskbar = false })
            using (var timer = new System.Windows.Forms.Timer { Interval = 250 })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                for (int i = 1; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var details = WorkspaceUi.Output("WriteReviewDetails"); details.Dock = DockStyle.Fill; details.Text = review.Body;
                var typed = WorkspaceUi.Input("WriteReviewTarget"); typed.Width = 700;
                var acknowledgement = WorkspaceUi.Check("WriteReviewAcknowledgement", "I authorize this exact database change / script execution.");
                var apply = WorkspaceUi.Button("WriteReviewApply", "Apply to DOORS", (s, e) => form.DialogResult = DialogResult.OK);
                apply.Enabled = false;
                var cancel = WorkspaceUi.Button("WriteReviewCancel", "Cancel", (s, e) => form.DialogResult = DialogResult.Cancel);
                Action refresh = () => apply.Enabled = review.CanApply && acknowledgement.Checked && typed.Text == review.Target &&
                    !cancellationToken.IsCancellationRequested && review.ExpiresAtUtc > DateTime.UtcNow;
                typed.TextChanged += (s, e) => refresh(); acknowledgement.CheckedChanged += (s, e) => refresh();
                timer.Tick += (s, e) => { refresh(); if (cancellationToken.IsCancellationRequested || review.ExpiresAtUtc <= DateTime.UtcNow) form.DialogResult = DialogResult.Cancel; };
                layout.Controls.Add(details, 0, 0); layout.Controls.Add(WorkspaceUi.Row("Type the exact target below:", typed), 0, 1);
                layout.Controls.Add(acknowledgement, 0, 2); layout.Controls.Add(WorkspaceUi.Row(apply, cancel), 0, 3);
                form.Controls.Add(layout); form.CancelButton = cancel; timer.Start();
                return form.ShowDialog(owner) == DialogResult.OK && apply.Enabled;
            }
        }
    }
}
