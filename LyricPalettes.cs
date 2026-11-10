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
            new LyricPalette("月光银", "#CED4DE", "#F5F7FF", "#A8C4EF", "#9EAABD", "#DCE5F5", "#B8C9E7"),
            new LyricPalette("晴空青", "#D0E8F2", "#55D9F2", "#587BFF", "#AFCBD8", "#A9F1FA", "#B5C7FF"),
            new LyricPalette("深海蓝", "#C9D7EA", "#376FE8", "#39C6E8", "#A3B5CD", "#A8C7FF", "#9CEEFF"),
            new LyricPalette("青柚苏打", "#D4E8D8", "#4DE6C1", "#B7F05B", "#ABC8B2", "#A4F6DE", "#E0F99B"),
            new LyricPalette("松林薄雾", "#CCDFD2", "#46C99A", "#66D7D1", "#A5BEB0", "#9BEBC7", "#A7F0E8"),
            new LyricPalette("薰衣草", "#DDD5F0", "#9675F5", "#E08CFF", "#B8ACD2", "#C4AEFF", "#F0B8FF"),
            new LyricPalette("蓝莓奶昔", "#D6D8F0", "#697CFF", "#C16BFA", "#B0B3D0", "#AEBBFF", "#E0AFFF"),
            new LyricPalette("樱花雨", "#F0D7E1", "#FF79B7", "#FF9CC9", "#D0B0BE", "#FFC0DC", "#FFE0ED"),
            new LyricPalette("莓果汽水", "#E9D1E3", "#F04FA7", "#9B65F5", "#C8ACC3", "#FF9BD0", "#C4A4FF"),
            new LyricPalette("珊瑚橘", "#F0D9CF", "#FF765E", "#FFB347", "#D0B6AD", "#FFAE91", "#FFD18A"),
            new LyricPalette("蜜桃乌龙", "#EFDDD0", "#FF9D72", "#F6C35B", "#CFB9AB", "#FFC4A5", "#FFE3A0"),
            new LyricPalette("鎏金暮色", "#E8DEC9", "#E7A83E", "#F06F63", "#C6B89F", "#F6CF78", "#FFAAA0"),
            new LyricPalette("柠檬海盐", "#E3E8CC", "#E1F45E", "#62E4B2", "#C0C5A9", "#F1FAA0", "#A5F4D5"),
            new LyricPalette("青提微风", "#D5E8D0", "#8CEB69", "#4CCFA8", "#B2C8AC", "#C6F7A5", "#9BEBD0"),
            new LyricPalette("电光蓝", "#D1E1F2", "#24B8FF", "#5A68FF", "#AABDD2", "#8DE0FF", "#A7B0FF"),
            new LyricPalette("极光绿", "#CFE6DD", "#35E6A3", "#42B8FF", "#A7C5B9", "#91F4C5", "#9ADFFF"),
            new LyricPalette("幻彩紫", "#DDD3EA", "#A855F7", "#F05AD8", "#B9AAC8", "#D4A5FF", "#FFA5E8"),
            new LyricPalette("玫瑰红", "#ECD4D9", "#FF536B", "#FF8A72", "#CDB0B6", "#FF9BA9", "#FFC1A8"),
            new LyricPalette("暖阳金", "#EAE0C9", "#FFD84D", "#FF9D42", "#C9BDA5", "#FFEB88", "#FFC37B"),
            new LyricPalette("冰蓝薄荷", "#D0E7E8", "#69E7F2", "#78E8B5", "#A9C6C7", "#AFF7F7", "#B3F5D4"),
            new LyricPalette("葡萄汽泡", "#DDD7E9", "#7555D9", "#D957C8", "#B7AFC5", "#B8A6FF", "#F29BEA"),
            new LyricPalette("夜幕蓝", "#BFCFE2", "#5276D8", "#42B8C9", "#98A9BF", "#9AB7FF", "#8DE4EB"),
            new LyricPalette("暮光紫", "#D5CBE6", "#6653B8", "#AA5ACB", "#B0A4C1", "#A99AEF", "#D895E6"),
            new LyricPalette("赤焰流星", "#E9D1CD", "#E94B55", "#F08A3C", "#C8AAA6", "#FF9694", "#FFC078"),
            new LyricPalette("彩虹糖", "#E4D7ED", "#52C8F5", "#F45DAB", "#C1B0CB", "#9DEAFF", "#FFB1D5")
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
