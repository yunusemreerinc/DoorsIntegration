using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.Infrastructure;

namespace DoorsIntegration.Wpf.ViewModels
{
    public sealed class ToolField : ObservableModel
    {
        private string value;
        private bool flag;
        private readonly Action changed;
        public ToolField(string key, string label, string initial, Action changed, string[] choices = null, bool boolean = false, bool multiline = false)
        {
            Key = key; Label = label; value = initial; flag = initial == "true"; this.changed = changed; Choices = choices; IsBoolean = boolean; Multiline = multiline;
            Browse = new UiCommand(() => {
                if (key == "save" || key == "failure") { var d = new Microsoft.Win32.SaveFileDialog { OverwritePrompt = true }; if (d.ShowDialog() == true) Value = d.FileName; }
                else { var d = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Filter = "Tüm dosyalar|*.*" }; if (d.ShowDialog() == true) Value = d.FileName; }
                return System.Threading.Tasks.Task.CompletedTask;
            }, () => true);
        }
        public bool HasBrowser => Key == "file" || Key == "profile" || Key == "quality" || Key == "save" || Key == "failure";
        public UiCommand Browse { get; }
        public string Key { get; }
        public string Label { get; }
        public string[] Choices { get; }
        public bool IsChoice => Choices != null;
        public bool IsBoolean { get; }
        public bool Multiline { get; }
        public string Value { get => value; set { if (Set(ref this.value, value)) changed(); } }
        public bool Flag { get => flag; set { if (Set(ref flag, value)) changed(); } }
    }
    public sealed class ToolPage : ObservableModel
    {
        private string report;
        public ToolPage(string title, string description) { Title = title; Description = description; report = description; }
        public string Title { get; }
        public string Description { get; }
        public ObservableCollection<ToolField> Fields { get; } = new ObservableCollection<ToolField>();
        public ObservableCollection<ToolAction> Actions { get; } = new ObservableCollection<ToolAction>();
        public ObservableCollection<AttributeEditRow> Attributes { get; } = new ObservableCollection<AttributeEditRow>();
        public bool HasAttributes => Attributes.Count > 0;
        public string Report { get => report; set => Set(ref report, value); }
        public void SchemaChanged() => Changed(nameof(HasAttributes));
        public string Text(string key) => Fields.Single(f => f.Key == key).Value ?? "";
        public bool Flag(string key) => Fields.Single(f => f.Key == key).Flag;
        public ToolField Field(string key) => Fields.Single(f => f.Key == key);
    }
    public sealed class ToolAction
    {
        public ToolAction(string label, UiCommand command) { Label = label; Command = command; }
        public string Label { get; }
        public UiCommand Command { get; }
    }
    public sealed class AttributeEditRow : ObservableModel
    {
        private readonly Action changed;
        private bool include, required;
        private string value = "", expected = "";
        private RequirementWriteFormat format;
        public AttributeEditRow(RequirementAttributeSchema schema, Action changed)
        { Schema = schema; this.changed = changed; }
        public RequirementAttributeSchema Schema { get; }
        public string Name => Schema.Name;
        public string Type => Schema.TypeName + " / " + Schema.BaseType;
        public string Metadata => Type + "; Multi=" + Schema.IsMultiValued + "; WriteHint=" + Schema.IsWritableHint +
            "; Required=" + Schema.RequiredRule + "; Enum=" + string.Join(" | ", Schema.EnumerationValues);
        public bool CanEdit => Schema.IsObjectAttribute && !Schema.IsGeneratedByDxl && (!Schema.IsSystem || Name == "Object Heading" || Name == "Object Text") &&
            !new[] { "Absolute Number", "Object Identifier", "Object Number", "Created On", "Created By", "Last Modified On", "Last Modified By" }.Contains(Name);
        public bool Include { get => include; set { if (CanEdit && Set(ref include, value)) changed(); } }
        public bool Required { get => required; set { if (Set(ref required, value)) changed(); } }
        public string Value { get => value; set { if (CanEdit && Set(ref this.value, value)) changed(); } }
        public string ExpectedPlain { get => expected; set { if (CanEdit && Set(ref expected, value)) changed(); } }
        public RequirementWriteFormat Format { get => format; set { if (CanEdit && Set(ref format, value)) changed(); } }
        public Array Formats => Enum.GetValues(typeof(RequirementWriteFormat));
        public IEnumerable<string> EnumerationValues => Schema.EnumerationValues;
        public bool SingleEnum => Schema.EnumerationValues.Count > 0 && !Schema.IsMultiValued;
    }
}
