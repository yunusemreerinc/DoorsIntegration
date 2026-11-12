using System;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DoorsIntegration.Core;

namespace DoorsIntegration.Desktop
{
    internal sealed class RequirementWritePanel : UserControl
    {
        private readonly DesktopWorkspace workspace;
        private readonly TextBox path = WorkspaceUi.Input("WriteRequirementModule");
        private readonly ComboBox action = WorkspaceUi.Choice("RequirementWriteAction", new[] { "Create", "Update", "Move" }),
            position = WorkspaceUi.Choice("RequirementWritePosition", new[] { "FirstInModule", "After", "FirstChild", "LastChild" });
        private readonly NumericUpDown absolute = WorkspaceUi.Number("RequirementWriteAbsolute", 1, 1, int.MaxValue),
            anchor = WorkspaceUi.Number("RequirementWriteAnchor", 1, 1, int.MaxValue), timeout = WorkspaceUi.Number("RequirementWriteTimeout", 120, 1, 600);
        private readonly DataGridView attributes = WorkspaceUi.Grid("RequirementAttributeCatalog");
        private readonly RichTextBox report = WorkspaceUi.Output("RequirementWriteReport", 220);
        private readonly Button apply;
        private RequirementModuleSchema schema;
        private RequirementWritePreview preview;
        public RequirementWritePanel(DesktopWorkspace workspace)
        {
            this.workspace = workspace; Name = "RequirementWritePanel"; var form = WorkspaceUi.Form(this);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Formal module (closed in client)", path, "Timeout seconds", timeout,
                WorkspaceUi.Button("LoadRequirementSchema", "Load attributes", async (s, e) => await workspace.RunAsync("Reading live attribute catalog...", async token =>
                {
                    InvalidateSchema(); schema = await workspace.Services.Requirements.GetAttributeSchemaAsync(Target(), token);
                    FillSchema(); report.Text = "Module: " + schema.ModulePath + "; Actor=" + schema.UserName + "; DB=" + schema.DatabaseId +
                        "\n" + string.Join("\n", schema.Issues.Select(i => i.Severity + ": " + i.Code + " " + i.AttributeName)) +
                        "\nRequired rules are not inferred from standard metadata. Select business-required fields explicitly.";
                    workspace.SetStatus("Catalog: " + schema.Attributes.Count + " attributes; Available=" + schema.IsAvailable);
                }))));
            WorkspaceUi.Add(form, WorkspaceUi.Row("Action", action, "Object absolute #", absolute, "Position", position, "Anchor absolute #", anchor));
            attributes.ReadOnly = false; attributes.Height = 300;
            attributes.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Change", HeaderText = "Change", Width = 60 });
            attributes.Columns.Add(new DataGridViewTextBoxColumn { Name = "Attribute", HeaderText = "Attribute", ReadOnly = true, Width = 170 });
            attributes.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type / flags", ReadOnly = true, Width = 160 });
            attributes.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Requested value (double-click to edit)", Width = 240 });
            var format = new DataGridViewComboBoxColumn { Name = "Format", HeaderText = "Format", Width = 130 };
            format.Items.AddRange(Enum.GetNames(typeof(RequirementWriteFormat))); attributes.Columns.Add(format);
            attributes.Columns.Add(new DataGridViewTextBoxColumn { Name = "ExpectedPlain", HeaderText = "Expected plain (rich text)", Width = 160 });
            attributes.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Required", HeaderText = "Business required", Width = 100 });
            attributes.CellValueChanged += (s, e) => InvalidatePreview();
            attributes.CurrentCellDirtyStateChanged += (s, e) => { if (attributes.IsCurrentCellDirty) attributes.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            attributes.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex >= 3) EditValue(attributes.Rows[e.RowIndex]); };
            WorkspaceUi.Add(form, attributes);
            apply = WorkspaceUi.Button("ApplyRequirementWrite", "Review and apply...", async (s, e) => await workspace.RunAsync("Requesting explicit requirement write approval...", async token =>
            {
                var approved = preview ?? throw WorkspaceUi.Invalid("Obtain a fresh preview first."); InvalidatePreview();
                var result = await workspace.ApplyAsync(approved, token);
                report.Text = "Outcome=" + result.Outcome + "; Persistence=" + result.Persistence + "; Cleanup=" + result.Cleanup + "; Audit=" + result.AuditRecorded +
                    "\nModule=" + result.Schema.QualifiedModuleId + "; Absolute=" + result.AbsoluteNumber + "; Identifier=" + result.ObjectIdentifier +
                    "; Parent=" + result.ParentAbsoluteNumber + "; Level=" + result.HierarchyLevel +
                    "\n" + string.Join("\n", result.Schema.Issues.Select(i => i.Severity + ": " + i.Code + " " + i.AttributeName)) +
                    "\n" + string.Join("\n", result.Steps.Select(s2 => s2.Kind + " " + s2.AttributeName + ": " + s2.Status + " " + s2.Error)) +
                    "\nIf partial/unknown, inspect the module and audit before any further write. Unsaved owned modules may remain open for review.";
                workspace.SetStatus("Requirement result: " + result.Outcome + "; Saved=" + result.Persistence + "; Cleanup=" + result.Cleanup + "; Audit=" + result.AuditRecorded);
            }));
            apply.Enabled = false;
            WorkspaceUi.Add(form, WorkspaceUi.Row(WorkspaceUi.Button("PreviewRequirementWrite", "Dry-run / preview", async (s, e) => await workspace.RunAsync("Previewing requirement write (no mutation)...", async token =>
            {
                attributes.EndEdit(); InvalidatePreview();
                if (schema == null || schema.ModulePath != Target().ModulePath || !schema.IsAvailable) throw WorkspaceUi.Invalid("Load the selected module's attribute catalog first.");
                var changes = (string)action.SelectedItem == "Move" ? new RequirementAttributeChange[0] : attributes.Rows.Cast<DataGridViewRow>()
                    .Where(r => Convert.ToBoolean(r.Cells[0].Value)).Select(r => new RequirementAttributeChange((string)r.Cells[1].Value,
                        Convert.ToString(r.Cells[3].Value), (RequirementWriteFormat)Enum.Parse(typeof(RequirementWriteFormat), (string)r.Cells[4].Value),
                        (string)r.Cells[4].Value == "DoorsRichText" ? Convert.ToString(r.Cells[5].Value) : null)).ToArray();
                var required = attributes.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToBoolean(r.Cells[6].Value)).Select(r => (string)r.Cells[1].Value).ToArray();
                var kind = WorkspaceUi.Selected<RequirementPositionKind>(position);
                var request = new RequirementWriteRequest(Target(), changes, (string)action.SelectedItem == "Create" ? (int?)null : (int)absolute.Value,
                    new RequirementPosition(kind, kind == RequirementPositionKind.Keep || kind == RequirementPositionKind.FirstInModule ? (int?)null : (int)anchor.Value), required);
                preview = await workspace.Services.Requirements.PreviewWriteAsync(request, token);
                report.Text = DesktopWriteReview.From(preview).Body; apply.Enabled = preview.CanApply;
                workspace.SetStatus("Dry-run: CanApply=" + preview.CanApply + ". Applying requires a separate explicit confirmation.");
            })), apply));
            WorkspaceUi.Add(form, report);
            path.TextChanged += (s, e) => InvalidateSchema(); timeout.ValueChanged += (s, e) => InvalidatePreview();
            absolute.ValueChanged += (s, e) => InvalidatePreview(); anchor.ValueChanged += (s, e) => InvalidatePreview(); position.SelectedIndexChanged += (s, e) => { InvalidatePreview(); RefreshAnchor(); };
            action.SelectedIndexChanged += (s, e) => { InvalidatePreview(); RefreshAction(); }; RefreshAction();
        }
        private RequirementWriteTarget Target() => new RequirementWriteTarget(path.Text, workspace.Target(), workspace.Connection(), TimeSpan.FromSeconds((double)timeout.Value));
        private void RefreshAction()
        {
            bool create = (string)action.SelectedItem == "Create", move = (string)action.SelectedItem == "Move";
            absolute.Enabled = !create; attributes.Enabled = !move;
            position.Items.Clear(); position.Items.AddRange(create ? new object[] { "FirstInModule", "After", "FirstChild", "LastChild" } :
                move ? new object[] { "After", "FirstChild", "LastChild" } : new object[] { "Keep", "After", "FirstChild", "LastChild" });
            position.SelectedIndex = 0; RefreshAnchor();
        }
        private void RefreshAnchor() { anchor.Enabled = position.SelectedItem != null && (string)position.SelectedItem != "Keep" && (string)position.SelectedItem != "FirstInModule"; }
        private void FillSchema()
        {
            foreach (var definition in schema.Attributes)
            {
                int index = attributes.Rows.Add(false, definition.Name, definition.TypeName + " / " + definition.BaseType +
                    (definition.IsGeneratedByDxl ? " DXL" : "") + (definition.IsSystem ? " System" : ""), "", "PlainText", "", false);
                var row = attributes.Rows[index]; row.Tag = definition;
                bool writable = definition.IsObjectAttribute && !definition.IsGeneratedByDxl && (!definition.IsSystem || definition.Name == "Object Heading" || definition.Name == "Object Text");
                foreach (int cell in new[] { 0, 3, 4, 5 }) row.Cells[cell].ReadOnly = !writable;
                row.Cells[6].ReadOnly = !definition.IsObjectAttribute || definition.IsGeneratedByDxl;
                row.Cells[1].ToolTipText = "Read=" + definition.IsReadable + "; Write hint=" + definition.IsWritableHint + "; Multi=" + definition.IsMultiValued +
                    "; Required=" + definition.RequiredRule + "; Enum=" + string.Join(" | ", definition.EnumerationValues);
            }
        }
        private void EditValue(DataGridViewRow row)
        {
            if (row.Cells[3].ReadOnly) return;
            var definition = (RequirementAttributeSchema)row.Tag;
            using (var dialog = new AttributeValueDialog(definition, Convert.ToString(row.Cells[3].Value), Convert.ToString(row.Cells[4].Value), Convert.ToString(row.Cells[5].Value)))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                row.Cells[3].Value = dialog.Value; row.Cells[4].Value = dialog.Format; row.Cells[5].Value = dialog.ExpectedPlain; row.Cells[0].Value = true;
            }
        }
        private void InvalidatePreview() { preview = null; if (apply != null) apply.Enabled = false; }
        public void InvalidateSchema() { InvalidatePreview(); schema = null; attributes.Rows.Clear(); }
    }

    internal sealed class AttributeValueDialog : Form
    {
        private readonly TextBox value, expected;
        private readonly ComboBox format;
        private readonly CheckedListBox choices;
        public AttributeValueDialog(RequirementAttributeSchema definition, string original, string originalFormat, string originalExpected)
        {
            Text = definition.Name + " — " + definition.TypeName; Width = 780; Height = 600;
            StartPosition = FormStartPosition.CenterParent; BackColor = DarkBlueTheme.Surface; ForeColor = DarkBlueTheme.Text;
            var form = WorkspaceUi.Form(this);
            format = WorkspaceUi.Choice("AttributeValueFormat", definition.BaseType == "Text" || definition.BaseType == "String" ? Enum.GetNames(typeof(RequirementWriteFormat)) : new[] { "PlainText" });
            if (format.Items.Contains(originalFormat)) format.SelectedItem = originalFormat;
            value = WorkspaceUi.Input("AttributeValueText", original, true); value.Width = 680; value.Height = 260; value.MaxLength = 65536;
            expected = WorkspaceUi.Input("AttributeExpectedPlain", originalExpected, true); expected.Width = 680; expected.MaxLength = 65536;
            WorkspaceUi.Add(form, WorkspaceUi.Row("Format", format));
            if (definition.BaseType == "Enumeration")
            {
                choices = new CheckedListBox { Width = 680, Height = 260, CheckOnClick = true, BackColor = DarkBlueTheme.Input, ForeColor = DarkBlueTheme.Text };
                var selected = original.Replace("\r\n", "\n").Split('\n');
                foreach (string label in definition.EnumerationValues) choices.Items.Add(label, selected.Contains(label));
                choices.ItemCheck += (s, e) => { if (!definition.IsMultiValued && e.NewValue == CheckState.Checked) for (int i = 0; i < choices.Items.Count; i++) if (i != e.Index) choices.SetItemChecked(i, false); };
                WorkspaceUi.Add(form, choices);
            }
            else WorkspaceUi.Add(form, value);
            WorkspaceUi.Add(form, WorkspaceUi.Row("Expected plain text for rich text", expected));
            var ok = WorkspaceUi.Button("AttributeValueAccept", "Use value", (s, e) => DialogResult = DialogResult.OK);
            var cancel = WorkspaceUi.Button("AttributeValueCancel", "Cancel", (s, e) => DialogResult = DialogResult.Cancel);
            WorkspaceUi.Add(form, WorkspaceUi.Row(ok, cancel)); CancelButton = cancel;
        }
        public string Value => choices == null ? value.Text : string.Join("\n", choices.CheckedItems.Cast<string>());
        public string Format => (string)format.SelectedItem;
        public string ExpectedPlain => expected.Text;
    }
}
