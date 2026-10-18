using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace MusicBar
{
    /// <summary>A local preview of a single color. The caller applies it only after OK.</summary>
    public sealed class ColorPaletteDialog : Form
    {
        private readonly Color originalColor;
        private Color selectedColor;
        private readonly List<ColorSwatch> swatches = new List<ColorSwatch>();
        private readonly TextBox hexBox;
        private readonly NumericUpDown red, green, blue;
        private readonly Label validation;
        private readonly ColorPreview preview;
        private bool updating;

        public Color SelectedColor { get { return selectedColor; } }

        public ColorPaletteDialog(string targetName, Color initialColor, IEnumerable<string> recentColors)
        {
            originalColor = Color.FromArgb(initialColor.R, initialColor.G, initialColor.B);
            selectedColor = originalColor;
            Text = "颜色库 · " + (String.IsNullOrWhiteSpace(targetName) ? "歌词颜色" : targetName);
            Icon = Theme.CreateIcon();
            ClientSize = new Size(620, 590);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.Font(9, FontStyle.Regular);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            AccessibleName = "歌词颜色库";

            AddLabel("选择一种心情", 24, 16, 400, 28, 16, Theme.Text, FontStyle.Bold);
            AddLabel("为" + (String.IsNullOrWhiteSpace(targetName) ? "歌词" : targetName) + "挑选颜色，确认后生效", 25, 44, 570, 22, 9, Theme.Muted, FontStyle.Regular);
            preview = new ColorPreview(originalColor, selectedColor) { Bounds = new Rectangle(24, 78, 572, 72), TabStop = false };
            Controls.Add(preview);

            AddLabel("灵感色板", 24, 163, 180, 22, 10, Theme.Text, FontStyle.Bold);
            AddLabel("点击色卡预览", 406, 164, 190, 20, 8.5f, Theme.Muted, FontStyle.Regular).TextAlign = ContentAlignment.MiddleRight;
            AddGroup("清透", 191, new string[] { "晴空", "冰蓝", "薄荷", "海盐", "湖光", "青柠", "月白", "雾紫" },
                new string[] { "7CCEFF", "A6DCFF", "8DE3C7", "A7E7DF", "73D7E5", "C9E7A2", "E7F1FF", "C6C5F4" });
            AddGroup("柔和", 236, new string[] { "樱粉", "桃夭", "杏仁", "奶油", "玫瑰", "浅藤", "暮云", "鼠尾草" },
                new string[] { "F1BBCD", "F3C7AF", "E9D3B5", "F2E4B4", "DDA8BC", "D5C1E8", "B9C8E1", "B6CCBC" });
            AddGroup("明亮", 281, new string[] { "珊瑚", "落日", "蜜柑", "暖阳", "鲜绿", "碧海", "鸢尾", "莓果" },
                new string[] { "FF8189", "FFA981", "FFC778", "F4DD7A", "9EDD8B", "57CADB", "B3A0F2", "E493C9" });
            AddGroup("中性", 326, new string[] { "霜白", "银灰", "烟灰", "岩石", "墨蓝", "深海", "墨绿", "夜色" },
                new string[] { "F1F4F8", "CAD1DC", "9CA8B8", "758194", "506888", "3D7C91", "527A70", "303844" });

            AddLabel("最近使用", 24, 382, 180, 20, 9, Theme.Muted, FontStyle.Regular);
            int recentCount = 0;
            var unique = new HashSet<int>();
            if (recentColors != null)
            {
                foreach (string value in recentColors)
                {
                    Color color;
                    if (!TryParseHex(value, out color) || !unique.Add(color.ToArgb())) continue;
                    var swatch = AddSwatch("#" + Hex(color), color, 24 + recentCount * 72, 405, 68, 36, true);
                    swatch.AccessibleName = "最近使用的颜色 #" + Hex(color);
                    if (++recentCount == 8) break;
                }
            }
            if (recentCount == 0) AddLabel("使用过的颜色会收在这里", 24, 406, 572, 30, 9, Theme.Muted, FontStyle.Regular);

            AddLabel("自定义颜色", 24, 451, 130, 22, 10, Theme.Text, FontStyle.Bold);
            AddLabel("HEX", 24, 481, 40, 27, 8.5f, Theme.Muted, FontStyle.Regular);
            hexBox = new TextBox { Bounds = new Rectangle(67, 481, 126, 27), BackColor = Theme.Input, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle, Font = Theme.Font(10, FontStyle.Regular), MaxLength = 7,
                AccessibleName = "十六进制颜色", AccessibleDescription = "输入六位颜色，如 #7CCEFF；也支持三位缩写，如 #ABC。", TabIndex = 50 };
            Controls.Add(hexBox);
            red = AddChannel("R", "红色", 217, 51);
            green = AddChannel("G", "绿色", 344, 52);
            blue = AddChannel("B", "蓝色", 471, 53);
            validation = AddLabel("支持 #RRGGBB 或 #RGB", 67, 509, 529, 19, 8, Theme.Muted, FontStyle.Regular);
            hexBox.TextChanged += delegate
            {
                if (updating) return;
                Color color;
                bool valid = TryParseHex(hexBox.Text, out color);
                validation.Text = valid ? "支持 #RRGGBB 或 #RGB" : "请输入有效颜色，例如 #7CCEFF";
                validation.ForeColor = valid ? Theme.Muted : Color.FromArgb(255, 164, 152);
                if (valid) SelectColor(color, false);
            };
            hexBox.Leave += delegate { Color color; if (TryParseHex(hexBox.Text, out color)) SelectColor(color, true); };
            EventHandler channelChanged = delegate
            {
                if (!updating) SelectColor(Color.FromArgb((int)red.Value, (int)green.Value, (int)blue.Value), true);
            };
            red.ValueChanged += channelChanged; green.ValueChanged += channelChanged; blue.ValueChanged += channelChanged;

            var cancel = Theme.Button("取消", false);
            cancel.Bounds = new Rectangle(380, 542, 100, 36);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.TabIndex = 54;
            cancel.AccessibleName = "取消颜色修改";
            Controls.Add(cancel);
            var use = Theme.Button("使用此颜色", true);
            use.Bounds = new Rectangle(490, 542, 106, 36);
            use.TabIndex = 55;
            use.AccessibleName = "使用选择的颜色";
            use.Click += delegate
            {
                Color color;
                if (!TryParseHex(hexBox.Text, out color))
                {
                    validation.Text = "请输入有效颜色，例如 #7CCEFF";
                    validation.ForeColor = Color.FromArgb(255, 164, 152);
                    hexBox.Focus(); hexBox.SelectAll(); return;
                }
                selectedColor = color;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(use);
            AcceptButton = use;
            CancelButton = cancel;
            SelectColor(selectedColor, true);
            Theme.ApplyDarkControls(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyDarkWindow(Handle);
        }

        private Label AddLabel(string text, int x, int y, int width, int height, float size, Color color, FontStyle style)
        {
            var label = Theme.Label(text, size, color);
            label.Font = Theme.Font(size, style);
            label.SetBounds(x, y, width, height);
            label.TabStop = false;
            Controls.Add(label);
            return label;
        }

        private NumericUpDown AddChannel(string label, string accessibleName, int x, int tabIndex)
        {
            AddLabel(label, x, 481, 22, 27, 8.5f, Theme.Muted, FontStyle.Regular);
            var number = Theme.Number(0, 255, 0, 0);
            number.Bounds = new Rectangle(x + 25, 481, 100, 27);
            // The last field ends on the common right edge of the dialog content.
            number.Width = 100;
            number.AccessibleName = accessibleName + "通道";
            number.TabIndex = tabIndex;
            Controls.Add(number);
            return number;
        }

        private void AddGroup(string title, int y, string[] names, string[] colors)
        {
            AddLabel(title, 24, y + 1, 57, 30, 9, Theme.Muted, FontStyle.Regular);
            for (int i = 0; i < names.Length; i++)
            {
                Color color;
                TryParseHex(colors[i], out color);
                AddSwatch(names[i], color, 84 + i * 64, y, 60, 42, false);
            }
        }

        private ColorSwatch AddSwatch(string name, Color color, int x, int y, int width, int height, bool compact)
        {
            var swatch = new ColorSwatch(name, color, compact) { Bounds = new Rectangle(x, y, width, height),
                TabIndex = swatches.Count, AccessibleName = name + " #" + Hex(color) };
            swatch.Click += delegate { SelectColor(color, true); };
            swatches.Add(swatch);
            Controls.Add(swatch);
            return swatch;
        }

        private void SelectColor(Color color, bool updateHex)
        {
            selectedColor = Color.FromArgb(color.R, color.G, color.B);
            updating = true;
            try
            {
                if (updateHex) hexBox.Text = "#" + Hex(selectedColor);
                red.Value = selectedColor.R; green.Value = selectedColor.G; blue.Value = selectedColor.B;
            }
            finally { updating = false; }
            validation.Text = "支持 #RRGGBB 或 #RGB";
            validation.ForeColor = Theme.Muted;
            preview.Current = selectedColor;
            foreach (ColorSwatch swatch in swatches)
            {
                Color sample = swatch.Sample;
                swatch.IsSelected = sample.ToArgb() == selectedColor.ToArgb();
            }
        }

        private static string Hex(Color color) { return color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2"); }

        internal static bool TryParseHex(string value, out Color color)
        {
            color = Color.Empty;
            string text = (value ?? "").Trim();
            if (text.StartsWith("#", StringComparison.Ordinal)) text = text.Substring(1);
            if (text.Length == 3) text = new String(new char[] { text[0], text[0], text[1], text[1], text[2], text[2] });
            int rgb;
            if (text.Length != 6 || !Int32.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb)) return false;
            color = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            return true;
        }

        private sealed class ColorSwatch : Button
        {
            internal readonly Color Sample;
            private bool selected, hover;
            internal bool IsSelected { set { if (selected == value) return; selected = value; AccessibleDescription = selected ? "当前选中" : "点击预览"; Invalidate(); } }
            internal ColorSwatch(string name, Color color, bool compact)
            {
                Text = name; Sample = color;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Font = Theme.Font(compact ? 7.5f : 8, FontStyle.Regular);
                ForeColor = Theme.Muted; BackColor = Theme.Background; FlatStyle = FlatStyle.Flat;
                Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.PushButton;
            }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.ParentBackground(Parent));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = e.Graphics.DpiX / 96f;
                int textHeight = Math.Max(14, (int)(16 * scale));
                var sample = new RectangleF(3 * scale, 3 * scale, Width - 6 * scale, Height - textHeight - 5 * scale);
                using (var path = Theme.RoundPath(sample, 5 * scale))
                {
                    using (var brush = new SolidBrush(Sample)) e.Graphics.FillPath(brush, path);
                    using (var pen = new Pen(Color.FromArgb(60, Theme.Text), scale)) e.Graphics.DrawPath(pen, path);
                }
                if (selected || hover || (Focused && ShowFocusCues))
                {
                    var ring = RectangleF.Inflate(sample, 2 * scale, 2 * scale);
                    using (var path = Theme.RoundPath(ring, 7 * scale))
                    using (var pen = new Pen(selected ? Theme.Accent : Theme.Muted, (selected ? 1.6f : 1) * scale)) e.Graphics.DrawPath(pen, path);
                }
                if (selected)
                {
                    bool darkText = Sample.R * .2126 + Sample.G * .7152 + Sample.B * .0722 > 155;
                    float cx = sample.X + sample.Width / 2, cy = sample.Y + sample.Height / 2;
                    using (var pen = new Pen(darkText ? Theme.Background : Theme.Text, 1.8f * scale))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        e.Graphics.DrawLines(pen, new PointF[] { new PointF(cx - 4 * scale, cy), new PointF(cx - scale, cy + 3 * scale), new PointF(cx + 5 * scale, cy - 3 * scale) });
                    }
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, Height - textHeight, Width, textHeight), selected ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        private sealed class ColorPreview : Control
        {
            private readonly Color original;
            private Color current;
            internal Color Current { set { current = value; Invalidate(); } }
            internal ColorPreview(Color original, Color current)
            {
                this.original = original; this.current = current;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                AccessibleName = "当前颜色与新颜色的歌词预览";
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.ParentBackground(Parent));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = e.Graphics.DpiX / 96f;
                using (var path = Theme.RoundPath(new RectangleF(.5f, .5f, Width - 1, Height - 1), 10 * scale))
                {
                    using (var brush = new SolidBrush(Theme.Surface)) e.Graphics.FillPath(brush, path);
                    using (var pen = new Pen(Theme.Border)) e.Graphics.DrawPath(pen, path);
                }
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, Width / 2, (int)(15 * scale), Width / 2, Height - (int)(15 * scale));
                DrawHalf(e.Graphics, new Rectangle(0, 0, Width / 2, Height), "当前颜色", original, scale);
                DrawHalf(e.Graphics, new Rectangle(Width / 2, 0, Width - Width / 2, Height), "新颜色", current, scale);
            }
            private void DrawHalf(Graphics graphics, Rectangle area, string title, Color color, float scale)
            {
                int padding = (int)(18 * scale);
                using (var labelFont = Theme.Font(8, FontStyle.Regular))
                using (var lyricFont = Theme.Font(12.5f, FontStyle.Regular))
                {
                    TextRenderer.DrawText(graphics, title, labelFont, new Rectangle(area.Left + padding, (int)(10 * scale), area.Width - padding * 2, (int)(18 * scale)), Theme.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(graphics, "风经过，留下温柔的回声", lyricFont, new Rectangle(area.Left + padding, (int)(32 * scale), area.Width - padding * 2, (int)(28 * scale)), color,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }
        }
    }
}
