using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    public sealed class MainForm : Form
    {
        private readonly DesktopOperations operations;
        private readonly DesktopActivityLog activity;
        private readonly IntegrationErrorHandler errors;
        private readonly IDoorsOperationGate operationGate;
        private readonly DxlRunLimitAutomationSettings runLimitSettings;
        private readonly IDoorsClientProcessLocator clientProcesses;
        private readonly CheckBox autoDoubleRunLimit = new CheckBox();
        private readonly Button bindRunLimitClient = new DarkBlueButton();
        private readonly DxlWatchdogSettings watchdogSettings;
        private readonly SettingsPanel settingsPanel;
        private readonly DiscoveryPanel discovery;
        private readonly DesktopWorkspace workspace;
        private readonly ComboBox target = new DarkBlueComboBox();
        private readonly ComboBox connectionMode = new DarkBlueComboBox();
        private readonly Button previewButton = new DarkBlueButton();
        private readonly Button probeButton = new DarkBlueButton();
        private readonly Button saveButton = new DarkBlueButton();
        private readonly Label status = new Label();
        private readonly Label request = new Label();
        private readonly DarkBlueBusyIndicator progress = new DarkBlueBusyIndicator();
        private readonly RichTextBox preview = new RichTextBox();
        private readonly RichTextBox log = new RichTextBox();
        private readonly TabControl tabs = new DarkBlueTabControl();
        private readonly Timer refreshTimer = new Timer { Interval = 250 };
        private readonly Font codeFont = new Font("Consolas", 10);
        private readonly Icon applicationIcon;
        private long lastLogSequence;
        private bool busy;
        private bool closeRequested;
        private bool bindingClient;

        public MainForm(DesktopOperations operations, DesktopActivityLog activity, IntegrationErrorHandler errors)
            : this(operations, activity, errors, null, null, null, DoorsOperationGate.Shared) { }

        public MainForm(DesktopOperations operations, DesktopActivityLog activity, IntegrationErrorHandler errors,
            IProjectService projects, IModuleService modules, IDiscoveryJsonExporter exporter, IDoorsOperationGate operationGate)
            : this(operations, activity, errors, projects, modules, exporter, operationGate,
                new DxlRunLimitAutomationSettings(), null) { }

        public MainForm(DesktopOperations operations, DesktopActivityLog activity, IntegrationErrorHandler errors,
            IProjectService projects, IModuleService modules, IDiscoveryJsonExporter exporter, IDoorsOperationGate operationGate,
            DxlRunLimitAutomationSettings runLimitSettings,
            IDoorsClientProcessLocator clientProcesses)
            : this(operations, activity, errors, projects, modules, exporter, operationGate,
                runLimitSettings, clientProcesses, new DxlWatchdogSettings(), null) { }

        public MainForm(DesktopOperations operations, DesktopActivityLog activity, IntegrationErrorHandler errors,
            IProjectService projects, IModuleService modules, IDiscoveryJsonExporter exporter, IDoorsOperationGate operationGate,
            DxlRunLimitAutomationSettings runLimitSettings,
            IDoorsClientProcessLocator clientProcesses, DxlWatchdogSettings watchdogSettings, IDesktopSettingsStore settingsStore,
            DesktopWorkspaceServices workspaceServices = null)
        {
            this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
            this.activity = activity ?? throw new ArgumentNullException(nameof(activity));
            this.errors = errors ?? throw new ArgumentNullException(nameof(errors));
            this.operationGate = operationGate ?? throw new ArgumentNullException(nameof(operationGate));
            this.runLimitSettings = runLimitSettings ?? throw new ArgumentNullException(nameof(runLimitSettings));
            this.clientProcesses = clientProcesses;
            this.watchdogSettings = watchdogSettings ?? throw new ArgumentNullException(nameof(watchdogSettings));
            settingsPanel = new SettingsPanel(watchdogSettings, settingsStore, errors, activity, operationGate);
            settingsPanel.SettingsSaved += (sender, args) =>
            {
                RefreshWatchdogControls();
                ShowPreview(false);
            };
            if (projects != null && modules != null && exporter != null)
            {
                discovery = new DiscoveryPanel(projects, modules, exporter, errors, () => SelectedTarget,
                    () => connectionMode.SelectedIndex == 1 ? DoorsConnectionMode.AttachExisting : DoorsConnectionMode.Activate);
                discovery.BusyChanged += OnDiscoveryBusyChanged;
            }
            if (workspaceServices != null)
            {
                workspace = new DesktopWorkspace(workspaceServices, () => SelectedTarget,
                    () => connectionMode.SelectedIndex == 1 ? DoorsConnectionMode.AttachExisting : DoorsConnectionMode.Activate,
                    () => discovery?.Snapshot, () => discovery?.InvalidateSnapshot());
                workspace.BusyChanged += running =>
                {
                    SetBusy(running); status.Text = workspace.StatusText; status.ForeColor = ForeColor;
                    request.Text = running ? "Workspace operation active. COM cannot be forcibly stopped." : "See Workspace for details.";
                };
            }
            Text = "DOORS Integration";
            Font = new Font("Segoe UI", 10);
            BackColor = DarkBlueTheme.Background;
            ForeColor = DarkBlueTheme.Text;
            ClientSize = new Size(1200, 850);
            MinimumSize = new Size(1000, 700);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("DoorsIntegration.Desktop.AppIcon.ico"))
            using (var embeddedIcon = new Icon(stream))
                applicationIcon = (Icon)embeddedIcon.Clone();
            Icon = applicationIcon;
            ShowIcon = true;
            BuildLayout();
            RefreshWatchdogControls();
            AcceptButton = previewButton;
            previewButton.Click += (sender, args) => ShowPreview();
            target.SelectedIndexChanged += (sender, args) => { workspace?.ContextChanged(); ShowPreview(false); };
            connectionMode.SelectedIndexChanged += (sender, args) => workspace?.ContextChanged();
            probeButton.Click += async (sender, args) => await RunProbeAsync();
            saveButton.Click += (sender, args) => SaveActivity();
            refreshTimer.Tick += (sender, args) =>
            {
                RefreshActivity();
                settingsPanel.RefreshAvailability();
                workspace?.RefreshAvailability();
                RefreshWatchdogControls();
                if (closeRequested && !busy && !this.operationGate.IsBusy) Close();
            };
            refreshTimer.Start();
            ShowPreview();
        }

        private void BuildLayout()
        {
            SuspendLayout();
            var root = new BufferedTableLayoutPanel
            {
                Name = "MainLayout", Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4
            };
            root.SuspendLayout(); tabs.SuspendLayout();
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var options = new FlowLayoutPanel
            {
                Name = "SessionContextStrip", Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty
            };
            options.Controls.Add(new Label
            {
                Text = "Target profile", AutoSize = true, Margin = new Padding(0, 8, 12, 0)
            });
            target.Name = "TargetProfileComboBox";
            target.DropDownStyle = ComboBoxStyle.DropDownList;
            DarkBlueTheme.StyleComboBox(target);
            target.Items.AddRange(new object[] { "9.5", "9.7" });
            target.SelectedIndex = 1;
            target.Width = 90;
            target.Margin = new Padding(0, 4, 20, 0);
            options.Controls.Add(target);
            ConfigureButton(previewButton, "Preview DXL", false);
            ConfigureButton(probeButton, "Run read-only probe", true);
            ConfigureButton(saveButton, "Save activity...", false);
            probeButton.Name = "ProbeButton";
            previewButton.Name = "PreviewButton";
            options.Controls.Add(new Label
            {
                Text = "Connection mode", AutoSize = true, Margin = new Padding(0, 7, 12, 0)
            });
            connectionMode.Name = "ConnectionModeComboBox";
            connectionMode.DropDownStyle = ComboBoxStyle.DropDownList;
            DarkBlueTheme.StyleComboBox(connectionMode);
            connectionMode.Items.AddRange(new object[] { "Activate registered client (may launch)", "Attach to existing session (ROT only)" });
            connectionMode.SelectedIndex = 0;
            connectionMode.Width = 365;
            options.Controls.Add(connectionMode);
            options.Controls.Add(new Label { Text = "Applies to the next request", AutoSize = true, ForeColor = DarkBlueTheme.SecondaryText, Margin = new Padding(12, 7, 0, 0) });
            var actionRows = new BufferedTableLayoutPanel { Name = "SessionToolbar", BackColor = DarkBlueTheme.Surface,
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 4, 0, 4) };
            actionRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actionRows.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); actionRows.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            actionRows.Controls.Add(options, 0, 0);
            var nativeOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            autoDoubleRunLimit.Name = "AutoDoubleRunLimitCheckBox";
            autoDoubleRunLimit.Text = "Auto-select DOORS 'Double execution time'";
            autoDoubleRunLimit.AutoSize = true;
            autoDoubleRunLimit.Margin = new Padding(0, 8, 16, 0);
            autoDoubleRunLimit.Enabled = runLimitSettings.Client != null && watchdogSettings.Enabled;
            autoDoubleRunLimit.Checked = runLimitSettings.Enabled;
            autoDoubleRunLimit.CheckedChanged += (sender, args) =>
            {
                runLimitSettings.Enabled = autoDoubleRunLimit.Checked;
                activity.Write(IntegrationLogLevel.Information, "DxlRunLimitPolicyChanged", Guid.NewGuid(),
                    "Enabled=" + runLimitSettings.Enabled + "; MaximumDoublingsPerCall=" + DxlRunLimitAutomationSettings.MaximumDoublingsPerCall);
            };
            ConfigureButton(bindRunLimitClient, "Bind running DOORS...", false);
            bindRunLimitClient.Name = "BindRunLimitClientButton";
            bindRunLimitClient.Enabled = clientProcesses != null;
            bindRunLimitClient.Click += async (sender, args) => await BindRunLimitClientAsync();
            nativeOptions.Controls.Add(autoDoubleRunLimit);
            nativeOptions.Controls.Add(bindRunLimitClient);
            nativeOptions.Controls.Add(new Label { Text = "Up to 4 extensions per DXL call", AutoSize = true,
                ForeColor = DarkBlueTheme.SecondaryText, Margin = new Padding(0, 8, 0, 0) });
            actionRows.Controls.Add(nativeOptions, 0, 1);
            root.Controls.Add(actionRows, 0, 1);

            var state = new BufferedTableLayoutPanel
            {
                Name = "OperationStatusStrip", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty
            };
            state.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); state.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            state.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            status.Name = "StatusLabel";
            status.AutoSize = false; status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            status.Margin = new Padding(0, 0, 0, 6);
            request.AutoSize = false; request.AutoEllipsis = true; request.Dock = DockStyle.Fill;
            request.ForeColor = DarkBlueTheme.SecondaryText;
            request.Margin = Padding.Empty;
            state.Controls.Add(status, 0, 0);
            state.Controls.Add(request, 0, 1);
            progress.Dock = DockStyle.Fill;
            progress.Visible = false;
            state.Controls.Add(progress, 1, 0);
            root.Controls.Add(state, 0, 2);

            tabs.Dock = DockStyle.Fill;
            tabs.Name = "MainTabs"; tabs.Margin = Padding.Empty;
            AddTab("DXL preview", preview, "PreviewTextBox", WorkspaceUi.Row(previewButton, probeButton,
                "Offline preview. Probe reads the client without changing database data."));
            AddTab("Activity", log, "ActivityTextBox", WorkspaceUi.Row(saveButton));
            if (discovery != null)
            {
                var page = new TabPage("Discovery") { BackColor = DarkBlueTheme.Surface, UseVisualStyleBackColor = false };
                page.Controls.Add(discovery);
                tabs.TabPages.Add(page);
            }
            if (workspace != null)
            {
                var workspacePage = new TabPage("Workspace") { Name = "WorkspaceTabPage", BackColor = DarkBlueTheme.Surface, UseVisualStyleBackColor = false };
                workspacePage.Controls.Add(workspace); tabs.TabPages.Add(workspacePage);
            }
            var settingsPage = new TabPage("Settings") { Name = "SettingsTabPage", BackColor = DarkBlueTheme.Surface,
                UseVisualStyleBackColor = false };
            settingsPage.Controls.Add(settingsPanel);
            tabs.TabPages.Add(settingsPage);
            root.Controls.Add(tabs, 0, 0);
            var footer = new Label
            {
                Text = "Profiles do not detect or switch the installed client. Sign in to DOORS before running a probe.",
                ForeColor = DarkBlueTheme.SecondaryText, AutoSize = true, Margin = Padding.Empty
            };
            root.Controls.Add(footer, 0, 3);
            root.SizeChanged += (sender, args) =>
            {
                int textWidth = Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal);
                footer.MaximumSize = new Size(textWidth, 200);
            };
            Controls.Add(root);
            tabs.ResumeLayout(false); root.ResumeLayout(true); ResumeLayout(true);
        }

        private async Task BindRunLimitClientAsync()
        {
            bindingClient = true;
            bindRunLimitClient.Enabled = false;
            autoDoubleRunLimit.Checked = false;
            autoDoubleRunLimit.Enabled = false;
            try
            {
                var client = await Task.Run(() => clientProcesses.FindSingleClient());
                if (IsDisposed) return;
                runLimitSettings.Bind(client);
                autoDoubleRunLimit.Enabled = watchdogSettings.Enabled;
                bindRunLimitClient.Text = "Rebind DOORS (PID " + client.ProcessId + ")";
                status.Text = "Bound " + client.ExecutablePath + " (PID " + client.ProcessId +
                    "). Select Auto-select to enable for the next DXL call.";
                activity.Write(IntegrationLogLevel.Information, "DxlRunLimitClientBound", Guid.NewGuid(), "ProcessId=" + client.ProcessId);
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                errors.Handle(ex, Guid.NewGuid());
                status.Text = "DOORS automation binding failed: " + ex.Message;
            }
            finally
            {
                bindingClient = false;
                if (!IsDisposed) { bindRunLimitClient.Enabled = true; RefreshWatchdogControls(); }
            }
        }

        private void RefreshWatchdogControls()
        {
            if (!watchdogSettings.Enabled)
            {
                autoDoubleRunLimit.Checked = false;
                runLimitSettings.Enabled = false;
            }
            autoDoubleRunLimit.Enabled = !bindingClient && watchdogSettings.Enabled && runLimitSettings.Client != null;
        }

        private static void ConfigureButton(Button button, string text, bool accent)
        {
            button.Text = text;
            button.AutoSize = true;
            button.Padding = new Padding(12, 5, 12, 5);
            button.Margin = new Padding(0, 0, 10, 0);
            DarkBlueTheme.StyleButton(button, accent);
        }

        private void AddTab(string title, RichTextBox editor, string name, Control actions)
        {
            var page = new TabPage(title) { Padding = new Padding(12), BackColor = DarkBlueTheme.Surface,
                ForeColor = DarkBlueTheme.Text, UseVisualStyleBackColor = false };
            editor.Name = name;
            editor.Dock = DockStyle.Fill;
            editor.ReadOnly = true;
            editor.BorderStyle = BorderStyle.None;
            editor.BackColor = DarkBlueTheme.Editor;
            editor.ForeColor = DarkBlueTheme.Code;
            editor.Font = codeFont;
            editor.WordWrap = false;
            editor.DetectUrls = false;
            var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(actions, 0, 0); layout.Controls.Add(editor, 0, 1); page.Controls.Add(layout);
            tabs.TabPages.Add(page);
        }

        private DoorsVersion SelectedTarget => DoorsVersions.Parse((string)target.SelectedItem);

        private void ShowPreview(bool selectPreview = true)
        {
            if (busy) return;
            var result = operations.CreatePreview(SelectedTarget);
            preview.Text = result.Script ?? string.Empty;
            ApplyResult(result);
            if (selectPreview) tabs.SelectedIndex = 0;
        }

        private async Task RunProbeAsync()
        {
            if (busy) return;
            var selectedTarget = SelectedTarget;
            var selectedMode = connectionMode.SelectedIndex == 1 ? DoorsConnectionMode.AttachExisting : DoorsConnectionMode.Activate;
            SetBusy(true);
            status.ForeColor = ForeColor;
            status.Text = "Running read-only probe. Complete any sign-in prompt in DOORS.";
            request.Text = "Waiting for the client response...";
            try
            {
                ApplyResult(await operations.RunProbeAsync(selectedTarget, selectedMode));
                tabs.SelectedIndex = 1;
            }
            finally
            {
                SetBusy(false);
                if (closeRequested) Close();
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            settingsPanel.Enabled = !value;
            target.Enabled = !value;
            connectionMode.Enabled = !value;
            previewButton.Enabled = !value;
            probeButton.Enabled = !value;
            saveButton.Enabled = !value;
            progress.Visible = value;
            discovery?.SetHostBusy(value);
            workspace?.SetHostBusy(value);
        }

        private void OnDiscoveryBusyChanged(bool running)
        {
            SetBusy(running);
            if (running)
            {
                status.Text = discovery.OperationDescription;
                status.ForeColor = ForeColor;
                request.Text = "Waiting for completion; an in-flight COM call cannot be forcibly cancelled.";
            }
            else if (discovery.LastFailure != null)
            {
                var failure = discovery.LastFailure;
                ApplyResult(new DesktopOperationResult(failure.CorrelationId, failure.Code + ": " + failure.Message, failure: failure));
            }
            else if (discovery.Snapshot != null)
            {
                var snapshot = discovery.Snapshot;
                status.Text = "Discovery: " + snapshot.Items.Count + " items; " + snapshot.Issues.Count + " issues. See the Discovery tab.";
                status.ForeColor = snapshot.IsCompleteForVisibleScope ? DarkBlueTheme.Success : DarkBlueTheme.AccentText;
                request.Text = "Request: " + snapshot.CorrelationId.ToString("N");
            }
        }

        private void ApplyResult(DesktopOperationResult result)
        {
            status.Text = result.Message;
            status.ForeColor = result.Succeeded ? DarkBlueTheme.Success : DarkBlueTheme.Error;
            request.Text = "Request: " + result.CorrelationId.ToString("N");
            RefreshActivity();
        }

        private void RefreshActivity()
        {
            var snapshot = activity.ReadAfter(0);
            if (snapshot.Length == 0 || snapshot[snapshot.Length - 1].Sequence == lastLogSequence) return;
            log.Text = string.Join(Environment.NewLine, snapshot.Select(entry => entry.ToString()));
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
            lastLogSequence = snapshot[snapshot.Length - 1].Sequence;
        }

        private void SaveActivity()
        {
            using (var dialog = new SaveFileDialog
            {
                Title = "Save activity", Filter = "Text files (*.txt)|*.txt", DefaultExt = "txt",
                FileName = "DoorsIntegration-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllLines(dialog.FileName, activity.ReadAfter(0).Select(entry => entry.ToString()));
                    status.Text = "Activity saved.";
                    status.ForeColor = DarkBlueTheme.Success;
                }
                catch (Exception ex)
                {
                    var failure = errors.Handle(ex, Guid.NewGuid());
                    ApplyResult(new DesktopOperationResult(failure.CorrelationId,
                        failure.Code + ": " + failure.Message, failure: failure));
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy || operationGate.IsBusy)
            {
                e.Cancel = true;
                closeRequested = true;
                status.Text = "The window will close when the DOORS operation and COM cleanup finish. Check any DOORS prompts.";
            }
            base.OnFormClosing(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkBlueTheme.ApplyTitleBar(Handle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) refreshTimer.Dispose();
            base.Dispose(disposing);
            if (disposing)
            {
                codeFont.Dispose();
                applicationIcon.Dispose();
            }
        }
    }
}
