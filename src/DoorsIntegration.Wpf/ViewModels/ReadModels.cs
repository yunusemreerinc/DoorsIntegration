using System;
using System.Collections.Generic;
using System.Linq;
using DoorsIntegration.Core;

namespace DoorsIntegration.Wpf.ViewModels
{
    public enum RequirementFilter { Active, Deleted, All }
    public enum ClientExportScope { Workspace, Requirements, Links, BaselineDiff }
    public sealed class ProjectTreeNode
    {
        public ProjectTreeNode(DoorsDiscoveryItem item) { Item = item; }
        public DoorsDiscoveryItem Item { get; }
        public string Label => Item.Name;
        public string Detail => Item.Type + " · " + Item.AccessStatus;
        public List<ProjectTreeNode> Children { get; } = new List<ProjectTreeNode>();
        public static IReadOnlyList<ProjectTreeNode> Build(DiscoverySnapshot snapshot)
        {
            var nodes = snapshot.Items.ToDictionary(item => item.FullPath, item => new ProjectTreeNode(item), StringComparer.Ordinal);
            var roots = new List<ProjectTreeNode>();
            foreach (var node in nodes.Values.OrderBy(item => item.Item.FullPath, StringComparer.Ordinal))
            {
                int slash = node.Item.FullPath.LastIndexOf('/'); string parent = slash <= 0 ? "/" : node.Item.FullPath.Substring(0, slash);
                if (parent != node.Item.FullPath && nodes.TryGetValue(parent, out var ancestor)) ancestor.Children.Add(node); else roots.Add(node);
            }
            return roots;
        }
    }
    public sealed class RequirementListRow
    {
        public RequirementListRow(RequirementRecord record)
        { Id = record.AbsoluteNumber; Identifier = record.ObjectIdentifier; Number = record.ObjectNumber; Heading = Clip(record.Heading.PlainText, 180); Text = Clip(record.Text.PlainText, 260); IsDeleted = record.IsDeleted; ContentStatus = record.Text.PlainStatus.ToString(); }
        public int Id { get; }
        public string Identifier { get; }
        public string Number { get; }
        public string Heading { get; }
        public string Text { get; }
        public bool IsDeleted { get; }
        public string ContentStatus { get; }
        public static string Clip(string value, int length = 16384) => value == null || value.Length <= length ? value : value.Substring(0, length) + "\n[… görüntü kısaltıldı; tam veri export içinde korunur]";
    }
    public sealed class AttributeDetailRow
    {
        public AttributeDetailRow(RequirementValue value)
        { Name = value.Name; Type = value.TypeName; PlainStatus = value.PlainStatus.ToString(); RichStatus = value.RichTextStatus.ToString(); Plain = RequirementListRow.Clip(value.PlainText); RawRich = RequirementListRow.Clip(value.RichTextWithOle); }
        public string Name { get; }
        public string Type { get; }
        public string PlainStatus { get; }
        public string RichStatus { get; }
        public string Plain { get; }
        public string RawRich { get; }
    }
    public sealed class LinkDetailRow
    {
        public LinkDetailRow(LinkRecord value)
        {
            Source = Endpoint(value.Source); Target = Endpoint(value.Target); LinkModule = value.LinkModulePath;
            Resolution = value.Resolution + " / " + value.Source.Resolution + " → " + value.Target.Resolution;
            IsResolved = value.Kind == LinkRecordKind.Link && value.Resolution == LinkResolution.Resolved && !value.IsEchoed &&
                value.Source.Resolution == LinkEndpointResolution.Resolved && value.Target.Resolution == LinkEndpointResolution.Resolved;
            Direction = value.Direction.ToString();
            Detail = Source + "\n→ " + Target + "\nLink module: " + LinkModule + "\n" + Resolution +
                "\nSource surrogate: " + value.Source.SurrogateStatus + "; Target surrogate: " + value.Target.SurrogateStatus;
        }
        private static string Endpoint(LinkEndpoint endpoint) => endpoint.ModulePath + " #" + endpoint.AbsoluteNumber + " (" + endpoint.VersionKind + " " + endpoint.Version + ")";
        public string Source { get; }
        public string Target { get; }
        public string LinkModule { get; }
        public string Resolution { get; }
        public bool IsResolved { get; }
        public string Direction { get; }
        public string Detail { get; }
    }
    public sealed class BaselineDiffRow
    {
        public BaselineDiffRow(BaselineObjectChange change)
        { Id = change.Identity.AbsoluteNumber; Identifier = change.After?.ObjectIdentifier ?? change.Before?.ObjectIdentifier; Status = change.Status.ToString(); IsConclusive = change.IsConclusive;
            Fields = string.Join(", ", change.Attributes.Where(value => value.Status != BaselineAttributeStatus.Unchanged).Select(value => value.Name + ":" + value.Status)); }
        public int Id { get; }
        public string Identifier { get; }
        public string Status { get; }
        public bool IsConclusive { get; }
        public string Fields { get; }
    }
}
