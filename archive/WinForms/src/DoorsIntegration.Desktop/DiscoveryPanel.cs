using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    internal sealed class DiscoveryPanel : UserControl
    {
        private readonly IProjectService projects;
        private readonly IModuleService modules;
        private readonly IDiscoveryJsonExporter exporter;
        private readonly IntegrationErrorHandler errors;
        private readonly Func<DoorsVersion> target;
        private readonly Func<DoorsConnectionMode> connection;
        private readonly ComboBox kind = new DarkBlueComboBox();
        private readonly TextBox path = new TextBox();
        private readonly CheckBox recursive = new CheckBox();
        private readonly Button discover = new DarkBlueButton();
        private readonly Button export = new DarkBlueButton();
        private readonly DataGridView grid = new DataGridView();
        private readonly RichTextBox issues = new RichTextBox();
        private readonly Label summary = new Label();
        private bool busy;
        private bool hostBusy;

        public DiscoveryPanel(IProjectService projects, IModuleService modules, IDiscoveryJsonExporter exporter,
            IntegrationErrorHandler errors, Func<DoorsVersion> target, Func<DoorsConnectionMode> connection)
        {
            this.projects = projects ?? throw new ArgumentNullException(nameof(projects));
            this.modules = modules ?? throw new ArgumentNullException(nameof(modules));
            this.exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
            this.errors = errors ?? throw new ArgumentNullException(nameof(errors));
            this.target = target ?? throw new ArgumentNullException(nameof(target));
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Dock = DockStyle.Fill;
            AutoScroll = true;
            AutoScrollMinSize = new Size(0, 300);
            BackColor = DarkBlueTheme.Surface;
            ForeColor = DarkBlueTheme.Text;
            BuildLayout();
            discover.Click += async (sender, args) => await DiscoverAsync();
            export.Click += async (sender, args) => await ExportAsync();
            UpdateActions();
        }

        public event Action<bool> BusyChanged;
        public DiscoverySnapshot Snapshot { get; private set; }
        public IntegrationFailure LastFailure { get; private set; }
        public string OperationDescription { get; private set; }

        public void SetHostBusy(bool value) { hostBusy = value; UpdateActions(); }
        public void InvalidateSnapshot()
        {
            Snapshot = null; grid.Rows.Clear(); issues.Clear(); summary.Text = "Snapshot invalidated after a write/custom script. Discover again."; UpdateActions();
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 6,
                MinimumSize = new Size(0, 300) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.Controls.Add(new Label { AutoSize = true, ForeColor = DarkBlueTheme.SecondaryText,
                Text = "Readable hierarchy metadata only. Modules stay closed; hidden items cannot be enumerated." }, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            kind.Name = "DiscoveryKindComboBox";
            kind.DropDownStyle = ComboBoxStyle.DropDownList;
            DarkBlueTheme.StyleComboBox(kind);
            kind.Items.AddRange(new object[] { "Projects, folders & formal modules", "Projects", "Folders", "Formal modules", "Known module path" });
            kind.SelectedIndex = 0;
            kind.Width = 270;
            actions.Controls.Add(kind);
            recursive.Text = "Recursive";
            recursive.Checked = true;
            recursive.AutoSize = true;
            actions.Controls.Add(recursive);
            discover.Name = "DiscoverButton";
            discover.Text = "Discover";
            discover.AutoSize = true;
            discover.Padding = new Padding(8, 3, 8, 3);
            DarkBlueTheme.StyleButton(discover, true);
            export.Name = "ExportDiscoveryButton";
            export.Text = "Export JSON...";
            export.AutoSize = true;
            export.Padding = new Padding(8, 3, 8, 3);
            DarkBlueTheme.StyleButton(export, false);
            actions.Controls.Add(discover);
            actions.Controls.Add(export);
            layout.Controls.Add(actions, 0, 1);
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.Controls.Add(new Label { Text = "Full path", AutoSize = true }, 0, 0);
            path.Name = "DiscoveryPathTextBox";
            path.Text = "/";
            path.BackColor = DarkBlueTheme.Input;
            path.ForeColor = DarkBlueTheme.Text;
            path.BorderStyle = BorderStyle.FixedSingle;
            path.Dock = DockStyle.Fill;
            pathRow.Controls.Add(path, 1, 0);
            layout.Controls.Add(pathRow, 0, 2);
            grid.Name = "DiscoveryGrid";
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AutoGenerateColumns = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = DarkBlueTheme.Editor;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = DarkBlueTheme.Border;
            grid.EnableHeadersVisualStyles = false;
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = DarkBlueTheme.Editor, ForeColor = DarkBlueTheme.Text,
                SelectionBackColor = DarkBlueTheme.Selection, SelectionForeColor = DarkBlueTheme.Text };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = DarkBlueTheme.Input,
                ForeColor = DarkBlueTheme.AccentText, SelectionBackColor = DarkBlueTheme.Input };
            foreach (var column in new[] { "Full path", "Name", "Type", "Access status" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                    FillWeight = column == "Full path" ? 200 : 100, SortMode = DataGridViewColumnSortMode.NotSortable });
            layout.Controls.Add(grid, 0, 3);
            summary.Name = "DiscoverySummaryLabel";
            summary.Text = "Ready. Discovery starts only when you click Discover.";
            summary.AutoSize = true;
            summary.ForeColor = DarkBlueTheme.SecondaryText;
            layout.Controls.Add(summary, 0, 4);
            issues.Name = "DiscoveryIssuesTextBox";
            issues.Dock = DockStyle.Fill;
            issues.ReadOnly = true;
            issues.BackColor = DarkBlueTheme.Editor;
            issues.ForeColor = DarkBlueTheme.SecondaryText;
            issues.BorderStyle = BorderStyle.None;
            issues.DetectUrls = false;
            layout.Controls.Add(issues, 0, 5);
            Controls.Add(layout);
        }

        private Task DiscoverAsync() => RunAsync("Running read-only discovery...", async () =>
        {
            Snapshot = null;
            grid.Rows.Clear();
            issues.Clear();
            var scope = new DiscoveryScope(path.Text, recursive.Checked, target: target(), connectionMode: connection());
            DiscoverySnapshot result;
            switch (kind.SelectedIndex)
            {
                case 1: result = await projects.DiscoverProjectsAsync(scope); break;
                case 2: result = await projects.DiscoverFoldersAsync(scope); break;
                case 3: result = await modules.DiscoverFormalModulesAsync(scope); break;
                case 4: result = await modules.GetFormalModuleAsync(scope); break;
                default: result = await projects.DiscoverHierarchyAsync(scope); break;
            }
            Snapshot = result;
            foreach (var item in result.Items)
            {
                int row = grid.Rows.Add(item.FullPath, item.Name, item.Type, item.AccessStatus);
                grid.Rows[row].Cells[3].ToolTipText = item.AccessDescription + (item.MetadataVerified ? "" : " Identity from requested path only.");
            }
            issues.Text = string.Join(Environment.NewLine, result.Issues.Select(issue => issue.FullPath + " | " + issue.Code + ": " + issue.Message));
            summary.Text = result.Items.Count + " items; " + result.Issues.Count + " issues; " +
                (result.IsCompleteForVisibleScope ? "visible scope complete" : "partial/unavailable scope") + "; Request: " + result.CorrelationId.ToString("N");
            summary.ForeColor = result.IsCompleteForVisibleScope ? DarkBlueTheme.Success : DarkBlueTheme.AccentText;
        });

        private async Task ExportAsync()
        {
            var snapshot = Snapshot;
            if (snapshot == null || busy || hostBusy) return;
            using (var dialog = new SaveFileDialog { Title = "Export discovery JSON", Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json", FileName = "DoorsDiscovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                await RunAsync("Exporting discovery JSON...", async () =>
                {
                    await Task.Run(() => exporter.ExportFile(dialog.FileName, snapshot, overwrite: true));
                    summary.Text = "Discovery JSON exported. Request: " + snapshot.CorrelationId.ToString("N");
                    summary.ForeColor = DarkBlueTheme.Success;
                });
            }
        }

        private async Task RunAsync(string description, Func<Task> action)
        {
            if (busy || hostBusy) return;
            busy = true;
            OperationDescription = description;
            LastFailure = null;
            summary.Text = description;
            summary.ForeColor = DarkBlueTheme.SecondaryText;
            UpdateActions();
            BusyChanged?.Invoke(true);
            try { await action(); }
            catch (Exception ex)
            {
                LastFailure = errors.Handle(ex, Snapshot?.CorrelationId ?? Guid.NewGuid());
                summary.Text = LastFailure.Code + ": " + LastFailure.Message;
                summary.ForeColor = DarkBlueTheme.Error;
                issues.Text = LastFailure.Message + Environment.NewLine + "Request: " + LastFailure.CorrelationId.ToString("N");
            }
            finally
            {
                busy = false;
                UpdateActions();
                BusyChanged?.Invoke(false);
            }
        }

        private void UpdateActions()
        {
            bool enabled = !busy && !hostBusy;
            discover.Enabled = enabled;
            kind.Enabled = enabled;
            path.Enabled = enabled;
            recursive.Enabled = enabled;
            export.Enabled = enabled && Snapshot != null;
        }
    }
}
