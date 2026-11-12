using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DoorsIntegration.Desktop
{
    // Presentation palette shared by the form and its painted controls.
    internal static class DarkBlueTheme
    {
        public static readonly Color Background = Color.FromArgb(11, 18, 32);
        public static readonly Color Surface = Color.FromArgb(17, 30, 50);
        public static readonly Color Input = Color.FromArgb(23, 41, 65);
        public static readonly Color Selection = Color.FromArgb(28, 52, 84);
        public static readonly Color Border = Color.FromArgb(45, 73, 108);
        public static readonly Color Text = Color.FromArgb(241, 245, 249);
        public static readonly Color SecondaryText = Color.FromArgb(175, 196, 223);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentHover = Color.FromArgb(47, 111, 235);
        public static readonly Color AccentPressed = Color.FromArgb(29, 78, 216);
        public static readonly Color AccentText = Color.FromArgb(148, 199, 255);
        public static readonly Color Notice = Color.FromArgb(20, 45, 75);
        public static readonly Color Editor = Color.FromArgb(13, 25, 43);
        public static readonly Color Code = Color.FromArgb(198, 218, 244);
        public static readonly Color Success = Color.FromArgb(125, 211, 176);
        public static readonly Color Error = Color.FromArgb(253, 164, 175);

        public static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Selection;
            button.FlatAppearance.MouseDownBackColor = primary ? AccentPressed : Notice;
            button.BackColor = primary ? Accent : Input;
            button.ForeColor = Text;
            button.UseVisualStyleBackColor = false;
        }

        public static void StyleComboBox(ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = Input;
            combo.ForeColor = Text;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += (sender, args) =>
            {
                bool selected = (args.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Selection : Input))
                    args.Graphics.FillRectangle(brush, args.Bounds);
                if (args.Index >= 0)
                {
                    var bounds = args.Bounds;
                    bounds.Inflate(-6, 0);
                    TextRenderer.DrawText(args.Graphics, combo.GetItemText(combo.Items[args.Index]), combo.Font,
                        bounds, combo.Enabled ? Text : SecondaryText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                args.DrawFocusRectangle();
            };
        }

        public static void ApplyTitleBar(IntPtr handle)
        {
            // Documented optional DWM attribute. Older Windows versions retain their native title bar.
            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref enabled, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }

    internal sealed class DarkBlueButton : Button
    {
        private bool hovered;
        private bool pressed;

        protected override void OnPaint(PaintEventArgs e)
        {
            var background = !Enabled ? DarkBlueTheme.Surface : pressed ? FlatAppearance.MouseDownBackColor :
                hovered ? FlatAppearance.MouseOverBackColor : BackColor;
            e.Graphics.Clear(background);
            using (var pen = new Pen(Enabled && (Focused || IsDefault) ? DarkBlueTheme.AccentText : FlatAppearance.BorderColor))
                e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                Enabled ? ForeColor : DarkBlueTheme.SecondaryText, flags);
            if (Focused && ShowFocusCues)
            {
                var focus = ClientRectangle;
                focus.Inflate(-4, -4);
                ControlPaint.DrawFocusRectangle(e.Graphics, focus, DarkBlueTheme.AccentText, background);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) pressed = true;
            base.OnMouseDown(e);
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; base.OnMouseUp(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) pressed = true;
            base.OnKeyDown(e);
            Invalidate();
        }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; base.OnKeyUp(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e)
        {
            pressed = false;
            hovered = false;
            base.OnEnabledChanged(e);
            Invalidate();
        }
    }

    internal sealed class DarkBlueComboBox : ComboBox
    {
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            const int wmPaint = 0x000F, wmPrint = 0x0317, wmPrintClient = 0x0318;
            if (message.Msg != wmPaint && message.Msg != wmPrint && message.Msg != wmPrintClient) return;
            // Preserve the native dropdown, keyboard behavior and accessibility; paint its frame/arrow only.
            using (var canvas = message.Msg == wmPaint || message.WParam == IntPtr.Zero ?
                Graphics.FromHwnd(Handle) : Graphics.FromHdc(message.WParam))
            {
                int arrowWidth = SystemInformation.VerticalScrollBarWidth + 2;
                var button = new Rectangle(Math.Max(0, Width - arrowWidth), 0, arrowWidth, Height);
                using (var brush = new SolidBrush(DarkBlueTheme.Input)) canvas.FillRectangle(brush, button);
                using (var pen = new Pen(Focused ? DarkBlueTheme.AccentText : DarkBlueTheme.Border))
                    canvas.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
                int x = button.Left + button.Width / 2, y = Height / 2;
                using (var brush = new SolidBrush(Enabled ? DarkBlueTheme.AccentText : DarkBlueTheme.SecondaryText))
                    canvas.FillPolygon(brush, new[] { new Point(x - 4, y - 2), new Point(x + 4, y - 2), new Point(x, y + 2) });
            }
        }
    }

    internal sealed class DarkBlueTabControl : TabControl
    {
        public DarkBlueTabControl()
        {
            // Paint the complete strip and frame so the unused native tab area stays dark too.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(144, 38);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(DarkBlueTheme.Background);
            var content = DisplayRectangle;
            content.Inflate(1, 1);
            using (var brush = new SolidBrush(DarkBlueTheme.Surface))
                e.Graphics.FillRectangle(brush, content);
            using (var pen = new Pen(DarkBlueTheme.Border))
                e.Graphics.DrawRectangle(pen, content);
            for (int index = 0; index < TabCount; index++)
            {
                var bounds = GetTabRect(index);
                bool selected = index == SelectedIndex;
                using (var brush = new SolidBrush(selected ? DarkBlueTheme.Surface : DarkBlueTheme.Background))
                    e.Graphics.FillRectangle(brush, bounds);
                if (selected)
                    using (var brush = new SolidBrush(DarkBlueTheme.Accent))
                        e.Graphics.FillRectangle(brush, bounds.X, bounds.Y, bounds.Width, 3);
                TextRenderer.DrawText(e.Graphics, TabPages[index].Text, Font, bounds,
                    selected ? DarkBlueTheme.Text : DarkBlueTheme.SecondaryText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (selected && Focused && ShowFocusCues)
                {
                    bounds.Inflate(-5, -6);
                    ControlPaint.DrawFocusRectangle(e.Graphics, bounds, DarkBlueTheme.AccentText, DarkBlueTheme.Surface);
                }
            }
            base.OnPaint(e);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    internal sealed class DarkBlueBusyIndicator : Control
    {
        private readonly Timer animation = new Timer { Interval = 40 };

        public DarkBlueBusyIndicator()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            AccessibleName = "DOORS operation in progress";
            AccessibleRole = AccessibleRole.ProgressBar;
            animation.Tick += (sender, args) => Invalidate();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            animation.Enabled = Visible;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(DarkBlueTheme.Input);
            int segment = Math.Max(12, Width / 3);
            int position = (int)((uint)Environment.TickCount % 1600) * (Width + segment) / 1600 - segment;
            using (var brush = new SolidBrush(DarkBlueTheme.Accent))
                e.Graphics.FillRectangle(brush, position, 0, segment, Height);
            using (var pen = new Pen(DarkBlueTheme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            base.OnPaint(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) animation.Dispose();
            base.Dispose(disposing);
        }
    }
}
