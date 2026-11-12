using System;
using System.Drawing;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    public sealed class SettingsPanel : UserControl
    {
        private readonly DxlWatchdogSettings settings;
        private readonly IDesktopSettingsStore store;
        private readonly IntegrationErrorHandler errors;
        private readonly IIntegrationLogger logger;
        private readonly IDoorsOperationGate gate;
        private readonly CheckBox watchdog = new CheckBox();
        private readonly Button save = new DarkBlueButton();
        private readonly Label result = new Label();
        public event EventHandler SettingsSaved;

        public SettingsPanel(DxlWatchdogSettings settings, IDesktopSettingsStore store, IntegrationErrorHandler errors,
            IIntegrationLogger logger, IDoorsOperationGate gate)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.store = store;
            this.errors = errors ?? throw new ArgumentNullException(nameof(errors));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
            Dock = DockStyle.Fill;
            BackColor = DarkBlueTheme.Surface;
            ForeColor = DarkBlueTheme.Text;
            AutoScroll = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
                Padding = new Padding(20) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            watchdog.Name = "DxlWatchdogEnabledCheckBox";
            watchdog.Text = "Enable DXL execution-cycle watchdog (recommended)";
            watchdog.AutoSize = true;
            watchdog.Checked = settings.Enabled;
            watchdog.Margin = new Padding(0, 0, 0, 16);
            layout.Controls.Add(watchdog);
            var explanation = new Label { AutoSize = true, Dock = DockStyle.Fill,
                Text = "Enabled: sets a finite 1,000,000-cycle limit for each new integration DXL call.\r\n" +
                    "Disabled: uses runLim, 0; a faulty script can run indefinitely and keep DOORS busy.\r\n" +
                    "The application wait timeout does not forcibly stop COM or DXL.\r\n\r\n" +
                    "Save applies to subsequent calls and is remembered for this Windows user.\r\n" +
                    "It does not execute DXL or change database data. Existing calls keep their captured setting.",
                ForeColor = DarkBlueTheme.SecondaryText, Margin = new Padding(0, 0, 0, 18) };
            layout.Controls.Add(explanation);
            save.Name = "SaveSettingsButton";
            save.Text = "Save settings";
            save.AutoSize = true;
            save.Padding = new Padding(12, 5, 12, 5);
            DarkBlueTheme.StyleButton(save, true);
            save.Click += (sender, args) => Save();
            layout.Controls.Add(save);
            result.Name = "SettingsStatusLabel";
            result.AutoSize = true;
            result.Dock = DockStyle.Fill;
            result.Margin = new Padding(0, 16, 0, 0);
            layout.Controls.Add(result);
            layout.SizeChanged += (sender, args) =>
            {
                int width = Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal);
                explanation.MaximumSize = new Size(width, 0);
                result.MaximumSize = new Size(width, 0);
            };
            Controls.Add(layout);
            RefreshAvailability();
        }

        public void RefreshAvailability()
        {
            watchdog.Enabled = !gate.IsBusy;
            save.Enabled = !gate.IsBusy;
        }

        private void Save()
        {
            try
            {
                // Acquiring the shared gate closes the race between checking IsBusy and saving.
                using (gate.Enter())
                {
                    store?.SaveWatchdogEnabled(watchdog.Checked);
                    settings.Enabled = watchdog.Checked;
                }
                try { logger.Write(IntegrationLogLevel.Information, "DxlWatchdogSettingSaved", Guid.NewGuid(), "Enabled=" + settings.Enabled); }
                catch { }
                result.ForeColor = DarkBlueTheme.Success;
                result.Text = store == null ? "Applied for this application session." : "Settings saved. Applies to subsequent DXL calls.";
                SettingsSaved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                var failure = errors.Handle(ex, Guid.NewGuid());
                result.ForeColor = DarkBlueTheme.Error;
                result.Text = "Settings were not applied. " + failure.Message;
            }
            RefreshAvailability();
        }
    }
}
