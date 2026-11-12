using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DoorsIntegration.Wpf.ViewModels;

namespace DoorsIntegration.Wpf.Controls
{
    // Bounded, read-only edge diagram. Dashed amber edges retain unresolved/reference evidence.
    public sealed class TraceabilityGraph : FrameworkElement
    {
        public static readonly DependencyProperty LinksProperty = DependencyProperty.Register(nameof(Links), typeof(IReadOnlyList<LinkDetailRow>), typeof(TraceabilityGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (sender, args) => ((TraceabilityGraph)sender).Resize()));
        public IReadOnlyList<LinkDetailRow> Links { get => (IReadOnlyList<LinkDetailRow>)GetValue(LinksProperty); set => SetValue(LinksProperty, value); }
        public TraceabilityGraph() { MinWidth = 700; Height = 240; Cursor = Cursors.Hand; }
        public event Action<LinkDetailRow> EdgeSelected;
        private void Resize() { Height = Math.Max(240, Math.Min(150, Links?.Count ?? 0) * 62 + 60); }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(12, 25, 42)), null, new Rect(RenderSize));
            if (Links == null || Links.Count == 0) { Text(dc, "Linkleri okuyun · unresolved ilişkiler ayrı gösterilir", new Point(20, 25), Brushes.LightSteelBlue); return; }
            double width = ActualWidth > 700 ? ActualWidth : 700, box = Math.Min(330, (width - 120) / 2);
            for (int i = 0; i < Math.Min(150, Links.Count); i++)
            {
                var edge = Links[i]; double y = 40 + i * 62; var color = edge.IsResolved ? Brushes.DeepSkyBlue : Brushes.Goldenrod;
                var pen = new Pen(color, 1.8); if (!edge.IsResolved) pen.DashStyle = DashStyles.Dash;
                double start = box + 20, end = width - box - 20;
                dc.DrawLine(pen, new Point(start, y + 18), new Point(end, y + 18));
                dc.DrawLine(pen, new Point(end - 8, y + 12), new Point(end, y + 18)); dc.DrawLine(pen, new Point(end - 8, y + 24), new Point(end, y + 18));
                Node(dc, new Rect(14, y, box, 39), edge.Source, color); Node(dc, new Rect(width - box - 14, y, box, 39), edge.Target, color);
            }
        }
        private static void Node(DrawingContext dc, Rect rect, string label, Brush color)
        { dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(27, 48, 71)), new Pen(color, 1), rect, 5, 5); Text(dc, label, new Point(rect.X + 9, rect.Y + 10), Brushes.White, rect.Width - 16); }
        private static void Text(DrawingContext dc, string text, Point point, Brush brush, double width = 680)
        {
            var formatted = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, 1.0)
                { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(formatted, point);
        }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        { base.OnMouseDown(e); int index = (int)((e.GetPosition(this).Y - 40) / 62); if (e.GetPosition(this).Y >= 40 && index >= 0 && index < (Links?.Count ?? 0)) EdgeSelected?.Invoke(Links[index]); }
    }
}
