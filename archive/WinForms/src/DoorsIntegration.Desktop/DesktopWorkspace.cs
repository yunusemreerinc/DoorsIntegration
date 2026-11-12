using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Desktop
{
    internal sealed class DesktopWorkspace : UserControl
    {
        private readonly TabControl pages = new DarkBlueTabControl();
        private readonly Button cancel;
        private readonly Label status = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        private readonly RichTextBox failureDetails = WorkspaceUi.Output("WorkspaceErrorDetails", 85);
        private readonly IDisposable confirmationBinding;
        private readonly RequirementWritePanel requirementWrites;
        private readonly ModuleCreationPanel moduleCreation;
        private readonly LinkWritePanel linkWrites;
        private readonly RequirementToolsPanel requirementTools;
        private readonly DxlWorkspacePanel dxl;
        private CancellationTokenSource cancellation;
        private bool running, hostBusy;
        public DesktopWorkspace(DesktopWorkspaceServices services, Func<DoorsVersion> target,
            Func<DoorsConnectionMode> connection, Func<DiscoverySnapshot> discovery, Action invalidateDiscovery)
        {
            Services = services; Target = target; Connection = connection; Discovery = discovery; this.invalidateDiscovery = invalidateDiscovery;
            Dock = DockStyle.Fill; BackColor = DarkBlueTheme.Surface; ForeColor = DarkBlueTheme.Text;
            DoubleBuffered = true;
            confirmationBinding = services.Confirmation.Attach(this);
            var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cancel = WorkspaceUi.Button("WorkspaceCancel", "Cancel / stop waiting", (s, e) =>
            { cancellation?.Cancel(); SetStatus("Cancellation requested. An in-flight COM call must return before another operation can start."); });
            cancel.Enabled = false;
            status.Text = "Offline. Select an operation; nothing runs on tab selection.";
            status.Name = "WorkspaceStatus";
            var commands = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            commands.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            commands.Controls.Add(cancel, 0, 0); commands.Controls.Add(status, 1, 0);
            layout.Controls.Add(commands, 0, 0); pages.Dock = DockStyle.Fill;
            requirementTools = new RequirementToolsPanel(this); Add("Requirements", requirementTools);
            requirementWrites = new RequirementWritePanel(this); Add("Requirement editor", requirementWrites);
            moduleCreation = new ModuleCreationPanel(this); Add("Create module", moduleCreation);
            Add("Links", new LinkWorkspacePanel(this));
            linkWrites = new LinkWritePanel(this); Add("Link editor", linkWrites);
            Add("Export", new ExportWorkspacePanel(this));
            dxl = new DxlWorkspacePanel(this); Add("Run DXL", dxl); Add("Diagnostics", new DiagnosticsWorkspacePanel(this));
            layout.Controls.Add(pages, 0, 1); failureDetails.Visible = false; layout.Controls.Add(failureDetails, 0, 2); Controls.Add(layout);
        }
        public DesktopWorkspaceServices Services { get; }
        public Func<DoorsVersion> Target { get; }
        public Func<DoorsConnectionMode> Connection { get; }
        public Func<DiscoverySnapshot> Discovery { get; }
        private readonly Action invalidateDiscovery;
        public string StatusText => status.Text;
        public void InvalidateCaches() { Data.SetRequirements(null); Data.SetLinks(null); invalidateDiscovery?.Invoke(); }
        public DesktopSnapshotStore Data { get; } = new DesktopSnapshotStore();
        public event Action<bool> BusyChanged;
        public bool IsRunning => running;
        private void Add(string text, Control panel)
        {
            var page = new TabPage(text) { Name = "Workspace" + text.Replace(" ", "") + "Tab", BackColor = DarkBlueTheme.Surface, UseVisualStyleBackColor = false };
            page.Controls.Add(panel); pages.TabPages.Add(page);
        }
        public void SetHostBusy(bool busy) { hostBusy = busy; RefreshAvailability(); }
        public void RefreshAvailability() { pages.Enabled = !running && !hostBusy && !Services.Dxl.IsBusy; cancel.Enabled = running && cancellation != null && !cancellation.IsCancellationRequested; }
        public void ContextChanged() { requirementWrites.InvalidateSchema(); moduleCreation.InvalidatePreview(); linkWrites.InvalidatePreview(); requirementTools.InvalidateBatch(); }
        public void SetStatus(string text) { if (!IsDisposed) status.Text = text; }
        public async Task RunAsync(string description, Func<CancellationToken, Task> action)
        {
            if (running || hostBusy) return;
            running = true; cancellation = new CancellationTokenSource(); failureDetails.Clear(); failureDetails.Visible = false;
            RefreshAvailability(); BusyChanged?.Invoke(true); SetStatus(description);
            try { await action(cancellation.Token); }
            catch (OperationCanceledException) { SetStatus("Cancelled. No automatic retry. Review any earlier write result."); }
            catch (Exception ex)
            {
                Exception displayed = ex;
                if (ex is ProcessingException processing) displayed = new IntegrationException(IntegrationErrorCode.ProcessingFailed, processing.Code + ": " + processing.Message, processing);
                if (ex is ArgumentException || ex is FormatException || ex is OverflowException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
                    displayed = new IntegrationException(IntegrationErrorCode.InvalidConfiguration, "Input or file operation failed: " + ex.Message, ex);
                var failure = Services.Errors.Handle(displayed, Guid.NewGuid());
                SetStatus(failure.Code + ": " + failure.Message);
                failureDetails.Text = failure.Code + ": " + failure.Message + "\nRequest: " + failure.CorrelationId.ToString("N") +
                    (ex is DxlScriptException script ? "\nDXL reported:\n" + WorkspaceUi.Clip(script.ScriptError, 65536) : "");
                failureDetails.Visible = true;
            }
            finally
            {
                var active = Services.Dxl.ActiveOperation;
                if (active != null && !active.Completion.IsCompleted)
                {
                    SetStatus("Waiting for the original COM call and cleanup. Do not submit the operation again.");
                    try { await active.Completion; } catch { /* The operation's owner reports/disposes its result. */ }
                }
                cancellation.Dispose(); cancellation = null; running = false; BusyChanged?.Invoke(false); RefreshAvailability();
            }
        }
        public async Task<RequirementWriteResult> ApplyAsync(RequirementWritePreview preview, CancellationToken token)
        {
            InvalidateCaches();
            try { return await Services.Requirements.ApplyWriteAsync(preview, token); }
            catch (RequirementWriteUncertainException ex)
            {
                SetStatus("Write outcome is uncertain. Waiting for the original completion; no retry. Plan " + ex.PlanId.ToString("N"));
                try { return await ex.Completion; }
                catch (Exception late) { throw new RequirementWriteUncertainException(ex.PlanId, ex.OperationId, ex.Completion, late); }
            }
        }
        public async Task<FormalModuleCreationResult> CreateAsync(FormalModuleCreationPreview preview, CancellationToken token)
        {
            InvalidateCaches();
            try { return await Services.Modules.CreateFormalModuleAsync(preview, token); }
            catch (ModuleCreationUncertainException ex)
            {
                SetStatus("Module creation outcome is uncertain. Waiting for original completion; no retry. Plan " + ex.PlanId.ToString("N"));
                try { return await ex.Completion; }
                catch (Exception late) { throw new ModuleCreationUncertainException(ex.PlanId, ex.OperationId, ex.Completion, late); }
            }
        }
        public async Task<LinkWriteResult> ApplyLinkAsync(LinkWritePreview preview, CancellationToken token)
        {
            InvalidateCaches();
            try { return await Services.Links.ApplyWriteAsync(preview, token); }
            catch (LinkWriteUncertainException ex)
            {
                SetStatus("Link outcome is uncertain. Waiting for original completion; no retry. Plan " + ex.PlanId.ToString("N"));
                try { return await ex.Completion; }
                catch (Exception late) { throw new LinkWriteUncertainException(ex.PlanId, ex.OperationId, ex.Completion, late); }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed) { confirmationBinding.Dispose(); cancellation?.Cancel(); Data.Dispose(); dxl.DisposeResult(); }
            base.Dispose(disposing);
        }
    }
}
