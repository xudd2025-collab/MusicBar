using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MusicBar
{
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(16, 18, 22);
        public static readonly Color Surface = Color.FromArgb(25, 28, 34);
        public static readonly Color Input = Color.FromArgb(34, 38, 46);
        public static readonly Color Text = Color.FromArgb(241, 244, 248);
        public static readonly Color Muted = Color.FromArgb(159, 167, 183);
        public static readonly Color Accent = Color.FromArgb(124, 206, 255);
        public static readonly Color Border = Color.FromArgb(48, 53, 64);
        public static readonly Color Navigation = Color.FromArgb(20, 23, 28);
        public static readonly Color AccentSurface = Color.FromArgb(29, 47, 65);
        public static Font Font(float size, FontStyle style) { return new Font("Microsoft YaHei UI", size, style); }
        public static Label Label(string text, float size, Color color)
        {
            return new Label { Text = text, Font = Font(size, FontStyle.Regular), ForeColor = color, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
        }
        public static Button Button(string text, bool primary)
        {
            var button = new RoundedButton { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Input, ForeColor = primary ? Background : Text, Font = Font(10, FontStyle.Regular), Cursor = Cursors.Hand, Height = 38, UseVisualStyleBackColor = false, Margin = new Padding(0, 0, 10, 0) };
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(155, 218, 255) : Color.FromArgb(38, 53, 68);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(93, 178, 229) : Surface;
            return button;
        }
        public static CheckBox Check(string text, bool value)
        {
            return new SwitchCheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = Text, BackColor = Background, Font = Font(10, FontStyle.Regular), Cursor = Cursors.Hand, Padding = new Padding(0, 3, 0, 3) };
        }
        public static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals)
        {
            return new DarkNumber { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), DecimalPlaces = decimals, Increment = decimals > 0 ? 0.5M : 1, Font = Font(10, FontStyle.Regular), BackColor = Input, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Width = 160, Height = 28, ThousandsSeparator = false };
        }
        public static ComboBox Combo()
        {
            var combo = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Input, ForeColor = Text, FlatStyle = FlatStyle.Flat, Font = Font(10, FontStyle.Regular), Width = 240, Height = 30, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 24 };
            combo.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                using (var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? AccentSurface : Input)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            return combo;
        }
        public static Panel Card() { return new BorderPanel { Width = 750, BackColor = Surface, Padding = new Padding(20), Margin = new Padding(0, 0, 16, 16) }; }
        public static ContextMenuStrip Menu()
        {
            return new ContextMenuStrip { BackColor = Surface, ForeColor = Text, Font = Font(10, FontStyle.Regular), Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Padding = new Padding(6), DropShadowEnabled = true };
        }
        internal static GraphicsPath RoundPath(RectangleF bounds, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            if (diameter < 1) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
        }
        internal static Color ParentBackground(Control parent)
        {
            for (Control current = parent; current != null; current = current.Parent)
                if (current.BackColor.A == 255) return current.BackColor;
            return Background;
        }
        private static IntPtr themeModule;
        private static NativeTheme.AllowDarkMode allowDarkMode;
        public static void EnableNativeDarkMode()
        {
            try
            {
                if (Environment.OSVersion.Version.Build < 18362) return;
                if (themeModule != IntPtr.Zero) return;
                themeModule = NativeTheme.LoadLibraryEx("uxtheme.dll", IntPtr.Zero, 0x800);
                if (themeModule == IntPtr.Zero) return;
                IntPtr proc = NativeTheme.GetProcAddress(themeModule, new IntPtr(135));
                if (proc != IntPtr.Zero) ((NativeTheme.PreferredAppMode)Marshal.GetDelegateForFunctionPointer(proc, typeof(NativeTheme.PreferredAppMode)))(2);
                proc = NativeTheme.GetProcAddress(themeModule, new IntPtr(133));
                if (proc != IntPtr.Zero) allowDarkMode = (NativeTheme.AllowDarkMode)Marshal.GetDelegateForFunctionPointer(proc, typeof(NativeTheme.AllowDarkMode));
            }
            catch { }
        }
        public static void ApplyDarkControls(Control control)
        {
            control.HandleCreated += delegate { ApplyDarkControl(control); };
            if (control.IsHandleCreated) ApplyDarkControl(control);
            foreach (Control child in control.Controls) ApplyDarkControls(child);
        }
        private static void ApplyDarkControl(Control control)
        {
            try
            {
                if (allowDarkMode != null) allowDarkMode(control.Handle, true);
                if (control is TextBoxBase || control is ComboBox || control is UpDownBase || control is ListBox || (control is ScrollableControl && !(control is Form)))
                    NativeTheme.SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            }
            catch { }
        }
        public static void ApplyDarkWindow(IntPtr handle)
        {
            try
            {
                int dark = 1;
                if (NativeTheme.DwmSetWindowAttribute(handle, 20, ref dark, 4) != 0) NativeTheme.DwmSetWindowAttribute(handle, 19, ref dark, 4);
                int background = ColorTranslator.ToWin32(Background), text = ColorTranslator.ToWin32(Text), border = ColorTranslator.ToWin32(Border);
                NativeTheme.DwmSetWindowAttribute(handle, 35, ref background, 4);
                NativeTheme.DwmSetWindowAttribute(handle, 36, ref text, 4);
                NativeTheme.DwmSetWindowAttribute(handle, 34, ref border, 4);
            }
            catch { }
        }
        private static class NativeTheme
        {
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int PreferredAppMode(int mode);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate bool AllowDarkMode(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] bool allow);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
            [DllImport("kernel32.dll")] internal static extern IntPtr GetProcAddress(IntPtr module, IntPtr name);
            [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] internal static extern int SetWindowTheme(IntPtr handle, string app, string id);
            [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
        }
        public static Icon CreateIcon()
        {
            // Use the same multi-size artwork as Explorer, shortcuts and Setup.
            using (var stream = typeof(Theme).Assembly.GetManifestResourceStream("MusicBar.AppIcon.ico"))
            {
                if (stream != null)
                    using (var icon = new Icon(stream, 32, 32)) return (Icon)icon.Clone();
            }
            using (var bitmap = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Accent)) g.FillEllipse(b, 1, 1, 30, 30);
                    using (var p = new Pen(Background, 3))
                    {
                        g.DrawLine(p, 17, 7, 17, 22); g.DrawLine(p, 17, 7, 24, 9);
                    }
                    using (var b = new SolidBrush(Background)) g.FillEllipse(b, 9, 19, 9, 6);
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (var original = Icon.FromHandle(handle)) return (Icon)original.Clone(); }
                finally { NativeIcon.DestroyIcon(handle); }
            }
        }
        private static class NativeIcon
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
        }
    }
    public sealed class DarkComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0xF && m.Msg != 0x317 && m.Msg != 0x318) return;
            using (var graphics = m.Msg == 0xF ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam))
            {
                int arrowWidth = Math.Max(20, SystemInformation.VerticalScrollBarWidth);
                var area = new Rectangle(Width - arrowWidth - 1, 1, arrowWidth, Height - 2);
                using (var brush = new SolidBrush(Theme.Input)) graphics.FillRectangle(brush, area);
                using (var pen = new Pen(Theme.Border)) graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                int x = area.Left + area.Width / 2, y = Height / 2;
                using (var pen = new Pen(Enabled ? Theme.Muted : Theme.Border, 1.5f)) { graphics.DrawLine(pen, x - 4, y - 2, x, y + 2); graphics.DrawLine(pen, x, y + 2, x + 4, y - 2); }
            }
        }
    }
    public sealed class DarkNumber : NumericUpDown
    {
        public DarkNumber()
        {
            foreach (Control child in Controls)
            {
                if (child is TextBox) continue;
                var chrome = new NumberButtons(child);
                child.HandleCreated += delegate { chrome.AssignHandle(child.Handle); };
                child.HandleDestroyed += delegate { chrome.ReleaseHandle(); };
                if (child.IsHandleCreated) chrome.AssignHandle(child.Handle);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        private sealed class NumberButtons : NativeWindow
        {
            private readonly Control control;
            internal NumberButtons(Control control) { this.control = control; }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0xF && m.Msg != 0x317 && m.Msg != 0x318) return;
                using (var graphics = m.Msg == 0xF ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam))
                {
                    graphics.Clear(Theme.Input);
                    using (var pen = new Pen(Theme.Border)) { graphics.DrawLine(pen, 0, 0, 0, control.Height); graphics.DrawLine(pen, 0, control.Height / 2, control.Width, control.Height / 2); }
                    int x = control.Width / 2;
                    using (var pen = new Pen(control.Enabled ? Theme.Muted : Theme.Border, 1.4f))
                    {
                        int top = control.Height / 4, bottom = control.Height * 3 / 4;
                        graphics.DrawLine(pen, x - 3, top + 1, x, top - 2); graphics.DrawLine(pen, x, top - 2, x + 3, top + 1);
                        graphics.DrawLine(pen, x - 3, bottom - 1, x, bottom + 2); graphics.DrawLine(pen, x, bottom + 2, x + 3, bottom - 1);
                    }
                }
            }
        }
    }
    public sealed class BorderPanel : Panel
    {
        public BorderPanel() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.ParentBackground(Parent));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundPath(new RectangleF(.5f, .5f, Width - 1, Height - 1), 14))
            using (var brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
            using (var path = Theme.RoundPath(new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 14)) e.Graphics.DrawPath(pen, path);
        }
    }
    public sealed class RoundedButton : Button
    {
        private bool hover, down;
        public bool NavigationButton;
        public bool Selected;
        public bool NotificationDot;
        public bool ShowColorSample;
        public Color ColorSample;
        public RoundedButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.ParentBackground(Parent));e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color background = down ? FlatAppearance.MouseDownBackColor : hover ? FlatAppearance.MouseOverBackColor : BackColor;
            if (NavigationButton) background = Selected ? Theme.AccentSurface : hover ? Theme.Input : Theme.Navigation;
            if (!Enabled) background = Theme.Surface;
            using (var path = Theme.RoundPath(new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 9))
            {
                using (var brush = new SolidBrush(background)) e.Graphics.FillPath(brush, path);
                if (!NavigationButton) using (var pen = new Pen(Enabled ? FlatAppearance.BorderColor : Theme.Border)) e.Graphics.DrawPath(pen, path);
            }
            if (NavigationButton && Selected) using (var brush = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(brush, 0, 12, 3, Math.Max(1, Height - 24));
            var bounds = new Rectangle(Padding.Left, Padding.Top, Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical));
            if (ShowColorSample)
            {
                float diameter = Math.Max(12, Height * .34f);
                var sample = new RectangleF(12, (Height - diameter) / 2, diameter, diameter);
                using (var brush = new SolidBrush(ColorSample)) e.Graphics.FillEllipse(brush, sample);
                using (var pen = new Pen(Color.FromArgb(110, Theme.Text))) e.Graphics.DrawEllipse(pen, sample);
                int inset = (int)(diameter + 20); bounds.X += inset; bounds.Width = Math.Max(1, bounds.Width - inset);
            }
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            flags |= TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : Theme.Muted, flags);
            if (NotificationDot) using (var brush = new SolidBrush(Color.FromArgb(255, 105, 105))) e.Graphics.FillEllipse(brush, Width - 14, 6, 7, 7);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ForeColor, background);
        }
    }
    public sealed class DownloadProgress : Control
    {
        private readonly Timer animation = new Timer { Interval = 33 };
        private int value;
        private bool indeterminate;
        private float phase;
        public int Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(100, value)); AccessibleDescription = this.value + "%"; Invalidate(); }
        }
        public bool Indeterminate
        {
            get { return indeterminate; }
            set { indeterminate = value; UpdateAnimation(); Invalidate(); }
        }
        public DownloadProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            AccessibleRole = AccessibleRole.ProgressBar; AccessibleName = "更新下载进度";
            animation.Tick += delegate { phase = (phase + .018f) % 1; Invalidate(); };
        }
        private void UpdateAnimation() { if (Visible && indeterminate) animation.Start(); else animation.Stop(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateAnimation(); }
        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Theme.ParentBackground(Parent)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0, (Height - 8) / 2f, Width, 8);
            using (var path = Theme.RoundPath(track, 4))
            using (var brush = new SolidBrush(Theme.Input)) e.Graphics.FillPath(brush, path);
            float fillWidth = indeterminate ? Width * .24f : Width * value / 100f;
            if (fillWidth <= 0) return;
            GraphicsState state = e.Graphics.Save();
            try
            {
                using (var clip = Theme.RoundPath(track, 4)) e.Graphics.SetClip(clip);
                float x = indeterminate ? (Width + fillWidth) * phase - fillWidth : 0;
                var fill = new RectangleF(x, track.Top, fillWidth, track.Height);
                using (var path = Theme.RoundPath(fill, 4))
                using (var brush = new LinearGradientBrush(fill, Color.FromArgb(93, 164, 239), Theme.Accent, 0f)) e.Graphics.FillPath(brush, path);
            }
            finally { e.Graphics.Restore(state); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { animation.Stop(); animation.Dispose(); }
            base.Dispose(disposing);
        }
    }
    public sealed class SwitchCheckBox : CheckBox
    {
        public SwitchCheckBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); BackColor = Theme.ParentBackground(Parent); }
        public override Size GetPreferredSize(Size proposedSize) { var text = TextRenderer.MeasureText(Text, Font); return new Size(text.Width + 55, Math.Max(30, text.Height + 8)); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Theme.ParentBackground(Parent)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.ParentBackground(Parent));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Font.SizeInPoints / 10f, width = 36 * scale, height = 20 * scale, top = (Height - height) / 2;
            using (var path = Theme.RoundPath(new RectangleF(0, top, width, height), height / 2))
            using (var brush = new SolidBrush(Checked ? Theme.Accent : Theme.Border)) e.Graphics.FillPath(brush, path);
            using (var brush = new SolidBrush(Checked ? Theme.Background : Theme.Text)) e.Graphics.FillEllipse(brush, Checked ? width - height + 3 * scale : 3 * scale, top + 3 * scale, height - 6 * scale, height - 6 * scale);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle((int)(width + 12 * scale), 0, Math.Max(1, Width - (int)(width + 12 * scale)), Height), Enabled ? ForeColor : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
        }
    }
    public sealed class DarkMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Theme.Surface); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { using (var b = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using (var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Theme.Input : Theme.Surface)) e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted; base.OnRenderItemText(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 6, e.Item.Height / 2, e.Item.Width - 6, e.Item.Height / 2); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Muted; base.OnRenderArrow(e); }
    }
}
