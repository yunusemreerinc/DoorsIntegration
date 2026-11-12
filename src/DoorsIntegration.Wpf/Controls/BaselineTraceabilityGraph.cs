using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DoorsIntegration.Core;
using DoorsIntegration.Wpf.ViewModels;

namespace DoorsIntegration.Wpf.Controls
{
    // Bounded drawing surface: no WPF element per captured requirement/link.
    public sealed class BaselineTraceabilityGraph : FrameworkElement
    {
        public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(BaselineGraphScene), typeof(BaselineTraceabilityGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty SelectedNodeIdProperty = DependencyProperty.Register(nameof(SelectedNodeId), typeof(string), typeof(BaselineTraceabilityGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty SelectedEdgeIdProperty = DependencyProperty.Register(nameof(SelectedEdgeId), typeof(string), typeof(BaselineTraceabilityGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
        public BaselineGraphScene Scene { get => (BaselineGraphScene)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
        public string SelectedNodeId { get => (string)GetValue(SelectedNodeIdProperty); set => SetValue(SelectedNodeIdProperty, value); }
        public string SelectedEdgeId { get => (string)GetValue(SelectedEdgeIdProperty); set => SetValue(SelectedEdgeIdProperty, value); }
        protected override Size MeasureOverride(Size availableSize) => new Size(Scene?.Width ?? 1000, Scene?.Height ?? 350);
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing); drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 24, 40)), null, new Rect(RenderSize)); if (Scene == null) return;
            foreach (var edge in Scene.Edges)
            {
                var a = From(edge); var b = To(edge); var color = edge.Edge.Id == SelectedEdgeId ? Colors.White : edge.Edge.State == TraceabilityEdgeState.Confirmed ? Color.FromRgb(73, 174, 229) : Color.FromRgb(222, 171, 90);
                var pen = new Pen(new SolidColorBrush(color), edge.Edge.Id == SelectedEdgeId ? 3 : 1.5); if (edge.Edge.State != TraceabilityEdgeState.Confirmed) pen.DashStyle = DashStyles.Dash;
                drawing.DrawLine(pen, a, b); var delta = a - b; if (delta.Length > 1) { delta.Normalize(); var perpendicular = new Vector(-delta.Y, delta.X); drawing.DrawLine(pen, b, b + delta * 10 + perpendicular * 4); drawing.DrawLine(pen, b, b + delta * 10 - perpendicular * 4); }
            }
            foreach (var node in Scene.Nodes)
            {
                var rect = new Rect(node.X, node.Y, 210, 57); var fill = node.Node.IsInventoryObject ? Color.FromRgb(27, 61, 91) : Color.FromRgb(64, 51, 43);
                var pen = new Pen(new SolidColorBrush(node.Id == SelectedNodeId ? Colors.White : node.Node.HasUncertainAccess ? Colors.Orange : Color.FromRgb(57, 135, 184)), node.Id == SelectedNodeId ? 2.5 : 1);
                drawing.DrawRoundedRectangle(new SolidColorBrush(fill), pen, rect, 6, 6);
                var text = new FormattedText(node.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = 192, MaxTextHeight = 48, Trimming = TextTrimming.CharacterEllipsis };
                drawing.DrawText(text, new Point(node.X + 9, node.Y + 6));
            }
        }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e); if (Scene == null) return; var point = e.GetPosition(this);
            foreach (var node in Scene.Nodes) if (new Rect(node.X, node.Y, 210, 57).Contains(point)) { SetCurrentValue(SelectedNodeIdProperty, null); SetCurrentValue(SelectedEdgeIdProperty, null); SetCurrentValue(SelectedNodeIdProperty, node.Id); e.Handled = true; return; }
            foreach (var edge in Scene.Edges) if (Distance(point, From(edge), To(edge)) < 7) { SetCurrentValue(SelectedEdgeIdProperty, null); SetCurrentValue(SelectedEdgeIdProperty, edge.Edge.Id); e.Handled = true; return; }
        }
        private static Point From(BaselineVisualEdge edge) => new Point(edge.Source.X + 210, edge.Source.Y + 28);
        private static Point To(BaselineVisualEdge edge) => new Point(edge.Target.X, edge.Target.Y + 28);
        private static double Distance(Point p, Point a, Point b) { var v = b - a; double square = v.LengthSquared; if (square < 0.01) return (p - a).Length; double t = Math.Max(0, Math.Min(1, Vector.Multiply(p - a, v) / square)); return (p - (a + t * v)).Length; }
    }
}
