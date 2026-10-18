using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MusicBar
{
    internal sealed class LyricPalette
    {
        internal readonly string Name, Base, Start, End, TranslationBase, TranslationStart, TranslationEnd;
        internal LyricPalette(string name, string basis, string start, string end,
            string translationBase, string translationStart, string translationEnd)
        {
            Name = name; Base = basis; Start = start; End = end;
            TranslationBase = translationBase; TranslationStart = translationStart; TranslationEnd = translationEnd;
        }
        internal void Apply(AppSettings settings)
        {
            settings.TextColor = Base; settings.ActiveColor = Start; settings.GradientEndColor = End;
            settings.TranslationColor = TranslationBase; settings.TranslationActiveColor = TranslationStart;
            settings.TranslationGradientEndColor = TranslationEnd;
        }
        internal bool Matches(AppSettings settings)
        {
            return Equal(Base, settings.TextColor) && Equal(Start, settings.ActiveColor) &&
                Equal(TranslationBase, settings.TranslationColor) && Equal(TranslationStart, settings.TranslationActiveColor) &&
                (settings.ColorMode != "gradient" || Equal(End, settings.GradientEndColor) && Equal(TranslationEnd, settings.TranslationGradientEndColor));
        }
        private static bool Equal(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        internal static readonly LyricPalette[] All = {
            new LyricPalette("冰川蓝", "#D5DEE9", "#7CCEFF", "#C4A5FF", "#AAB8C8", "#EAF2FA", "#FFB8D9"),
            new LyricPalette("薄荷极光", "#CDE3DF", "#6CE8C2", "#82B8FF", "#A5C5BE", "#B4F3DD", "#BBCDFF"),
            new LyricPalette("落日晚霞", "#E9D9D4", "#FFC078", "#FF8EB5", "#CDB6B2", "#FFDCAB", "#F9B9D8"),
            new LyricPalette("紫雾星河", "#DDD8EB", "#C0A2FF", "#FF9DDB", "#BEB0D1", "#DDD0FF", "#FFC5E6"),
            new LyricPalette("玫瑰奶油", "#EADAE0", "#FFACCA", "#FFE2A8", "#CEB6C1", "#FFD0E1", "#FFF0CD"),
            new LyricPalette("海盐青柠", "#DCE5D4", "#D0F584", "#74E1CB", "#B9C7AE", "#E2F9B5", "#B0F0E0"),
            new LyricPalette("琥珀流光", "#E8DECD", "#FFD879", "#FFA56E", "#C7BCA5", "#FFE8AD", "#FFD0AF"),
            new LyricPalette("月光银", "#CED4DE", "#F5F7FF", "#A8C4EF", "#9EAABD", "#DCE5F5", "#B8C9E7")
        };
    }

    internal sealed class LyricPaletteButton : Button
    {
        internal readonly LyricPalette Palette;
        internal bool Selected, Gradient;
        private bool hover;
        internal LyricPaletteButton(LyricPalette palette)
        {
            Palette = palette; Text = palette.Name; AccessibleName = "配色：" + palette.Name;
            AccessibleDescription = "应用原文、译文及渐变颜色搭配";
            Cursor = Cursors.Hand; BackColor = Theme.Surface; ForeColor = Theme.Text;
            Font = Theme.Font(9, FontStyle.Regular); FlatStyle = FlatStyle.Flat;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = DeviceDpi / 96f;
            Graphics g = e.Graphics; g.Clear(Theme.ParentBackground(Parent)); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundPath(new RectangleF(.5f, .5f, Width - 1, Height - 1), 9 * scale))
            {
                using (var brush = new SolidBrush(Selected ? Theme.AccentSurface : hover ? Theme.Input : Theme.Background)) g.FillPath(brush, path);
                using (var pen = new Pen(Selected || Focused ? Theme.Accent : Theme.Border, Selected ? 1.5f : 1)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Palette.Name, Font,
                new Rectangle((int)(12 * scale), (int)(6 * scale), Width - (int)(38 * scale), (int)(22 * scale)),
                Selected ? Theme.Text : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Selected) TextRenderer.DrawText(g, "✓", Font, new Rectangle(Width - (int)(28 * scale), (int)(6 * scale), (int)(20 * scale), (int)(22 * scale)), Theme.Accent, TextFormatFlags.VerticalCenter);
            RectangleF sample = new RectangleF(12 * scale, 33 * scale, Math.Max(1, Width - 24 * scale), 5 * scale);
            Color first = ColorTranslator.FromHtml(Palette.Start), last = ColorTranslator.FromHtml(Palette.End);
            using (var path = Theme.RoundPath(sample, 2.5f * scale))
            using (Brush brush = Gradient ? (Brush)new LinearGradientBrush(sample, first, last, 0f) : new SolidBrush(first)) g.FillPath(brush, path);
            RectangleF translated = new RectangleF(sample.X, 43 * scale, sample.Width, 3 * scale);
            using (var path = Theme.RoundPath(translated, 1.5f * scale))
            using (Brush brush = Gradient ? (Brush)new LinearGradientBrush(translated, ColorTranslator.FromHtml(Palette.TranslationStart), ColorTranslator.FromHtml(Palette.TranslationEnd), 0f) : new SolidBrush(ColorTranslator.FromHtml(Palette.TranslationStart))) g.FillPath(brush, path);
        }
    }
}
