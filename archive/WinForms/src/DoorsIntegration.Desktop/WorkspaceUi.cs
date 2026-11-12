using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    internal static class WorkspaceUi
    {
        public static TextBox Input(string name, string value = "", bool multiline = false) => new TextBox
        {
            Name = name, Text = value, Width = 330, MaxLength = 65536, Multiline = multiline, Height = multiline ? 78 : 27,
            ScrollBars = multiline ? ScrollBars.Both : ScrollBars.None, AcceptsReturn = multiline,
            BackColor = DarkBlueTheme.Input, ForeColor = DarkBlueTheme.Text, BorderStyle = BorderStyle.FixedSingle
        };
        public static NumericUpDown Number(string name, decimal value, decimal minimum, decimal maximum) => new NumericUpDown
        { Name = name, Minimum = minimum, Maximum = maximum, Value = value, Width = 110, BackColor = DarkBlueTheme.Input, ForeColor = DarkBlueTheme.Text };
        public static ComboBox Choice(string name, IEnumerable<string> values)
        {
            var box = new DarkBlueComboBox { Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Width = 185 };
            DarkBlueTheme.StyleComboBox(box); box.Items.AddRange(values.Cast<object>().ToArray()); box.SelectedIndex = 0; return box;
        }
        public static ComboBox Choice<T>(string name) => Choice(name, Enum.GetNames(typeof(T)));
        public static T Selected<T>(ComboBox box) where T : struct => (T)Enum.Parse(typeof(T), (string)box.SelectedItem);
        public static CheckBox Check(string name, string text, bool selected = false) => new CheckBox
        { Name = name, Text = text, AutoSize = true, Checked = selected, ForeColor = DarkBlueTheme.Text, Margin = new Padding(6) };
        public static Button Button(string name, string text, EventHandler click)
        {
            var button = new DarkBlueButton { Name = name, Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(4) };
            DarkBlueTheme.StyleButton(button, true); button.Click += click; return button;
        }
        public static RichTextBox Output(string name, int height = 160) => new RichTextBox
        {
            Name = name, ReadOnly = true, DetectUrls = false, WordWrap = false, MaxLength = int.MaxValue, Height = height, Dock = DockStyle.Top,
            BackColor = DarkBlueTheme.Editor, ForeColor = DarkBlueTheme.Code, BorderStyle = BorderStyle.FixedSingle
        };
        public static DataGridView Grid(string name, params string[] columns)
        {
            var grid = new DataGridView
            {
                Name = name, ReadOnly = true, Height = 220, Dock = DockStyle.Top, AutoGenerateColumns = false,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, EnableHeadersVisualStyles = false,
                BackgroundColor = DarkBlueTheme.Editor, GridColor = DarkBlueTheme.Border, BorderStyle = BorderStyle.None
            };
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = DarkBlueTheme.Editor, ForeColor = DarkBlueTheme.Text,
                SelectionBackColor = DarkBlueTheme.Selection, SelectionForeColor = DarkBlueTheme.Text };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = DarkBlueTheme.Input, ForeColor = DarkBlueTheme.AccentText };
            foreach (string column in columns) grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = column, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            return grid;
        }
        public static FlowLayoutPanel Row(params object[] items)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 3, 0, 3) };
            foreach (object item in items) row.Controls.Add(item is Control control ? control : new Label
            { Text = (string)item, AutoSize = true, Margin = new Padding(4, 7, 4, 3), ForeColor = DarkBlueTheme.SecondaryText });
            return row;
        }
        public static TableLayoutPanel Form(Control owner)
        {
            owner.Dock = DockStyle.Fill; owner.BackColor = DarkBlueTheme.Surface; owner.ForeColor = DarkBlueTheme.Text;
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var form = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10) };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); scroll.Controls.Add(form); owner.Controls.Add(scroll); return form;
        }
        public static void Add(TableLayoutPanel form, Control control)
        { control.Margin = new Padding(0, 3, 0, 3); form.RowStyles.Add(new RowStyle(SizeType.AutoSize)); form.Controls.Add(control, 0, form.RowCount++); }
        public static string Clip(string text, int max = 256) => text == null ? "" : text.Length <= max ? text : text.Substring(0, max) + " … [preview truncated; export preserves full data]";
        public static string[] Lines(string text) => text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        public static IntegrationException Invalid(string message) => new IntegrationException(IntegrationErrorCode.InvalidConfiguration, message);
        public static string SavePath(Control owner, string title, string extension)
        {
            using (var dialog = new SaveFileDialog { Title = title, DefaultExt = extension, Filter = extension + " files|*." + extension + "|All files|*.*", OverwritePrompt = true })
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
        }
    }

    internal sealed class BaselineFields
    {
        public readonly CheckBox Enabled;
        private readonly NumericUpDown major, minor;
        private readonly TextBox suffix;
        public BaselineFields(string prefix)
        {
            Enabled = WorkspaceUi.Check(prefix + "BaselineEnabled", "Read a baseline");
            major = WorkspaceUi.Number(prefix + "BaselineMajor", 1, 0, 1000000);
            minor = WorkspaceUi.Number(prefix + "BaselineMinor", 0, 0, 1000000);
            suffix = WorkspaceUi.Input(prefix + "BaselineSuffix"); suffix.Width = 120;
            Enabled.CheckedChanged += (s, e) => Refresh(); Refresh();
        }
        private void Refresh() { major.Enabled = minor.Enabled = suffix.Enabled = Enabled.Checked; }
        public Control Row => WorkspaceUi.Row(Enabled, "Major", major, "Minor", minor, "Suffix", suffix);
        public RequirementBaseline Value => Enabled.Checked ? new RequirementBaseline((int)major.Value, (int)minor.Value, suffix.Text) : null;
    }
}
