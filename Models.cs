using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.Serialization;

namespace MusicBar
{
    public enum MusicPlayer { None, QQMusic, NetEase }

    public sealed class MusicSnapshot
    {
        public MusicPlayer Player;
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string PlatformTrackId = "";
        public double PositionSeconds;
        public double DurationSeconds;
        public bool IsPlaying;
        public bool HasTimeline;
        public bool InterpolateTimeline = true;
        public double MaximumInterpolationSeconds = double.PositiveInfinity;
        public double PlaybackRate = 1;
        public DateTime TimestampUtc = DateTime.UtcNow;
        public string Status = "";
        public string LiveLyric = "";
        public string TrackKey { get { return Player.ToString() + "|" + Title.Trim() + "|" + Artist.Trim() + "|" + Album.Trim(); } }
        // Keep TrackKey compatible with existing local lyric bindings. Playback identity
        // also includes the native ID so different recordings with identical names switch.
        public string PlaybackKey { get { return TrackKey + (string.IsNullOrEmpty(PlatformTrackId) ? "" : "|id:" + PlatformTrackId); } }
        internal static bool ValidNetEaseTrackId(string id)
        {
            long number;
            if (string.IsNullOrEmpty(id) || id.Length > 19) return false;
            foreach (char c in id) if (c < '0' || c > '9') return false;
            return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
        }
        public bool HasTrack { get { return Player != MusicPlayer.None && !string.IsNullOrWhiteSpace(Title); } }
        internal MusicSnapshot Copy() { return (MusicSnapshot)MemberwiseClone(); }
        public double CurrentPosition
        {
            get
            {
                double elapsed = IsPlaying && HasTimeline && InterpolateTimeline ? Math.Max(0, (DateTime.UtcNow - TimestampUtc).TotalSeconds) : 0;
                elapsed = Math.Min(elapsed, Math.Max(0, MaximumInterpolationSeconds));
                double position = Math.Max(0, PositionSeconds + elapsed * PlaybackRate);
                return DurationSeconds > 0 ? Math.Min(DurationSeconds, position) : position;
            }
        }
        public static string PlayerName(MusicPlayer player)
        {
            return player == MusicPlayer.QQMusic ? "QQ 音乐" : player == MusicPlayer.NetEase ? "网易云音乐" : "未连接";
        }
    }

    public sealed class LyricLine
    {
        public double Seconds;
        public string Text;
        public double EndSeconds;
        public List<LyricWord> Words = new List<LyricWord>();
        public LyricLine() { Text = ""; }
        public LyricLine(double seconds, string text) { Seconds = seconds; Text = text ?? ""; }
    }

    public sealed class LyricWord
    {
        public int StartIndex, Length;
        public double StartSeconds, EndSeconds;
    }

    public sealed class LyricDocument
    {
        public List<LyricLine> Lines = new List<LyricLine>();
        public string PlainText = "";
        public string Source = "";
        public string Translation = "";
        public List<LyricLine> TranslationLines = new List<LyricLine>();
        public string TranslationSource = "";
        public string TranslationStatus = "";
        public string WordTiming = "", TranslationWordTiming = "";
        public bool WordTimingChecked;
        public bool HasTimedLyrics { get { return Lines.Count > 0; } }
        public bool HasTranslation
        {
            get
            {
                if (TranslationLines != null) foreach (var line in TranslationLines)
                    if (!string.IsNullOrWhiteSpace(line.Text)) return true;
                return false;
            }
        }
        public int FindLine(double seconds)
        {
            int low = 0, high = Lines.Count - 1, result = -1;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (Lines[mid].Seconds <= seconds) { result = mid; low = mid + 1; }
                else { high = mid - 1; }
            }
            return result;
        }
    }

    public sealed class LyricSearchResult
    {
        public MusicPlayer Player;
        public string Id = "";
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public double DurationSeconds;
        public List<string> TitleAliases = new List<string>();
        public List<string> AlbumAliases = new List<string>();
        public override string ToString() { return Title + "  ·  " + Artist + (string.IsNullOrEmpty(Album) ? "" : "  ·  " + Album); }
    }

    internal sealed class LyricTextSegment
    {
        internal string Text;
        internal double Progress;

        internal static void SelectPair(string original, string translation, int maximum,
            double originalProgress, double translationProgress,
            out LyricTextSegment first, out LyricTextSegment second)
        {
            original = original ?? ""; translation = translation ?? "";
            originalProgress = SafeProgress(originalProgress); translationProgress = SafeProgress(translationProgress);
            if (maximum <= 0 || original.Length == 0 || translation.Length == 0)
            {
                first = Select(original, maximum, originalProgress);
                second = Select(translation, maximum, translationProgress);
                return;
            }
            int[] originalElements = StringInfo.ParseCombiningCharacters(original);
            int[] translatedElements = StringInfo.ParseCombiningCharacters(translation);
            int pages = (Math.Max(originalElements.Length, translatedElements.Length) - 1) / maximum + 1;
            int page;
            if (pages <= originalElements.Length)
            {
                int element = ElementAt(originalElements, originalProgress * original.Length);
                page = (int)((((long)element + 1) * pages - 1) / originalElements.Length);
            }
            else page = Math.Min(pages - 1, (int)Math.Floor(originalProgress * pages));
            // Both rows belong to one lyric sentence. Use the original singing
            // position to select their common page; translation length or word
            // timing must not advance the translated row to another page.
            first = SelectPage(original, originalElements, pages, page, originalProgress);
            second = SelectPage(translation, translatedElements, pages, page, translationProgress);
        }

        private static LyricTextSegment SelectPage(string text, int[] elements, int pages, int page, double progress)
        {
            int localPages = Math.Min(pages, elements.Length);
            int localPage = (int)((long)page * localPages / pages);
            int first = (int)((long)localPage * elements.Length / localPages);
            int after = (int)(((long)localPage + 1) * elements.Length / localPages);
            int start = elements[first], end = after < elements.Length ? elements[after] : text.Length;
            return new LyricTextSegment { Text = text.Substring(start, end - start),
                Progress = Math.Max(0, Math.Min(1, (progress * text.Length - start) / (end - start))) };
        }

        private static double SafeProgress(double progress)
        { return double.IsNaN(progress) || double.IsInfinity(progress) ? 0 : Math.Max(0, Math.Min(1, progress)); }

        private static int ElementAt(int[] elements, double position)
        {
            int element = Array.BinarySearch(elements, (int)Math.Floor(position + .00000001));
            if (element < 0) element = ~element - 1;
            return Math.Max(0, Math.Min(elements.Length - 1, element));
        }

        // Karaoke progress is measured in UTF-16 offsets. Split only at text
        // element boundaries so a surrogate pair or combining mark stays intact.
        internal static LyricTextSegment Select(string text, int maximum, double progress)
        {
            text = text ?? "";
            progress = SafeProgress(progress);
            if (maximum <= 0 || text.Length == 0) return new LyricTextSegment { Text = text, Progress = progress };
            int[] elements = StringInfo.ParseCombiningCharacters(text);
            if (elements.Length <= maximum) return new LyricTextSegment { Text = text, Progress = progress };
            double position = progress * text.Length;
            int element = ElementAt(elements, position);
            int first = element / maximum * maximum;
            int after = Math.Min(elements.Length, first + maximum);
            int start = elements[first], end = after < elements.Length ? elements[after] : text.Length;
            return new LyricTextSegment { Text = text.Substring(start, end - start),
                Progress = Math.Max(0, Math.Min(1, (position - start) / (end - start))) };
        }
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember] public bool QQEnabled = true;
        [DataMember] public bool NetEaseEnabled = true;
        [DataMember] public bool OverlayEnabled = true;
        [DataMember] public bool OnlineLyrics = true;
        [DataMember] public bool AutoCheckUpdates = true;
        [DataMember] public bool HideWhenPaused = true;
        [DataMember] public bool HideInstrumental = true;
        [DataMember] public double InstrumentalHoldSeconds = 10;
        [DataMember] public bool TwoLines = false;
        [DataMember] public bool ShowTranslation = true;
        [DataMember] public bool LongLineScroll = true;
        [DataMember] public int MaxCharactersPerLine = 0;
        [DataMember] public bool ClickThrough = true;
        [DataMember] public bool ShowSongWhenMissing = true;
        [DataMember] public bool FollowTaskbar = true;
        [DataMember] public bool PreviewEnabled = false;
        [DataMember] public string FontFamily = "Microsoft YaHei UI";
        [DataMember] public string TranslationFontFamily = "";
        [DataMember] public float FontSize = 16;
        [DataMember] public int LyricBrightness = 100;
        [DataMember] public string TextColor = "#D5DEE9";
        [DataMember] public string ActiveColor = "#7CCEFF";
        [DataMember] public string TranslationColor = "#AAB8C8";
        [DataMember] public string TranslationActiveColor = "#EAF2FA";
        [DataMember] public string ColorMode = "solid";
        [DataMember] public string GradientEndColor = "#C4A5FF";
        [DataMember] public string TranslationGradientEndColor = "#FFB8D9";
        [DataMember] public List<string> RecentColors = new List<string>();
        [DataMember] public bool AuthorNoticeShown = false;
        [DataMember] public bool AuthorWatermarkDismissed = false;
        [DataMember] public bool KaraokeEnabled = true;
        [DataMember] public bool BoldLyrics = true;
        [DataMember] public bool HideTaskbarIcon = false;
        [DataMember] public bool EnableHotkey = true;
        [DataMember] public int HotkeyPreset = 0;
        [DataMember] public bool UseCustomHotkey = false;
        [DataMember] public int HotkeyKey = 0x4D;
        [DataMember] public int HotkeyModifiers = 3;
        [DataMember] public int Width = 420;
        [DataMember] public int HorizontalOffset = 0;
        [DataMember] public int VerticalOffset = 0;
        [DataMember] public int MonitorIndex = 0;
        [DataMember] public string Alignment = "left";
        [DataMember] public double OffsetSeconds = 0;
        [DataMember] public MusicPlayer PreferredPlayer = MusicPlayer.None;

        [OnDeserializing]
        private void InitializeNewSettings(StreamingContext context)
        {
            // Older portable settings must get the new display defaults as well.
            ShowTranslation = true;
            LongLineScroll = true;
            LyricBrightness = 115;
            TranslationColor = "";
            TranslationActiveColor = "";
            TranslationFontFamily = "";
            ColorMode = "solid";
            GradientEndColor = "#C4A5FF";
            TranslationGradientEndColor = "#FFB8D9";
            RecentColors = new List<string>();
            AutoCheckUpdates = true;
            HideInstrumental = true;
            HideWhenPaused = true;
            InstrumentalHoldSeconds = 10;
            KaraokeEnabled = true;
            BoldLyrics = true;
            EnableHotkey = true;
            HotkeyKey = 0x4D;
            HotkeyModifiers = 3;
        }

        public void Normalize()
        {
            FontSize = Math.Max(10, Math.Min(30, FontSize));
            LyricBrightness = Math.Max(30, Math.Min(160, LyricBrightness));
            Width = Math.Max(180, Math.Min(1200, Width));
            MaxCharactersPerLine = Math.Max(0, Math.Min(200, MaxCharactersPerLine));
            HorizontalOffset = Math.Max(-20000, Math.Min(20000, HorizontalOffset));
            VerticalOffset = Math.Max(-2000, Math.Min(2000, VerticalOffset));
            OffsetSeconds = Math.Max(-120, Math.Min(120, OffsetSeconds));
            if (double.IsNaN(InstrumentalHoldSeconds) || double.IsInfinity(InstrumentalHoldSeconds)) InstrumentalHoldSeconds = 10;
            InstrumentalHoldSeconds = Math.Max(3, Math.Min(20, InstrumentalHoldSeconds));
            MonitorIndex = Math.Max(0, Math.Min(20, MonitorIndex));
            if (!Enum.IsDefined(typeof(MusicPlayer), PreferredPlayer)) PreferredPlayer = MusicPlayer.None;
            if (Alignment != "left" && Alignment != "center" && Alignment != "right") Alignment = "left";
            if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = "Microsoft YaHei UI";
            if (string.IsNullOrWhiteSpace(TranslationFontFamily)) TranslationFontFamily = "";
            if (!ValidColor(TextColor)) TextColor = "#F8FAFC";
            if (!ValidColor(ActiveColor)) ActiveColor = "#4ADE80";
            if (!ValidColor(TranslationColor)) TranslationColor = TextColor;
            if (!ValidColor(TranslationActiveColor)) TranslationActiveColor = ActiveColor;
            if (ColorMode != "gradient") ColorMode = "solid";
            if (!ValidColor(GradientEndColor)) GradientEndColor = "#C4A5FF";
            if (!ValidColor(TranslationGradientEndColor)) TranslationGradientEndColor = "#FFB8D9";
            var recent = new List<string>();
            if (RecentColors != null) foreach (string color in RecentColors)
            {
                if (!ValidColor(color)) continue;
                string normalized = color.ToUpperInvariant();
                if (!recent.Contains(normalized)) recent.Add(normalized);
                if (recent.Count == 8) break;
            }
            RecentColors = recent;
            HotkeyPreset = Math.Max(0, Math.Min(2, HotkeyPreset));
            if (!ValidHotkey(HotkeyKey, HotkeyModifiers))
            {
                UseCustomHotkey = false;
                HotkeyKey = 0x4D;
                HotkeyModifiers = 3;
            }
        }
        public int EffectiveHotkeyKey { get { return UseCustomHotkey ? HotkeyKey : 0x4D; } }
        public int EffectiveHotkeyModifiers { get { return UseCustomHotkey ? HotkeyModifiers : HotkeyPreset == 1 ? 6 : HotkeyPreset == 2 ? 5 : 3; } }
        public static bool ValidHotkey(int key, int modifiers)
        {
            // Function keys work alone; text keys still need Ctrl or Alt.
            if ((modifiers & ~7) != 0) return false;
            bool functionKey = key >= 0x70 && key <= 0x87;
            if ((modifiers & 3) == 0 && !functionKey) return false;
            return key >= 0x30 && key <= 0x39 || key >= 0x41 && key <= 0x5A ||
                key >= 0x60 && key <= 0x87 || key >= 0xBA && key <= 0xC0 ||
                key >= 0xDB && key <= 0xDF || key == 0xE2 ||
                key == 8 || key == 9 || key == 13 || key == 27 ||
                key >= 32 && key <= 40 || key == 45 || key == 46;
        }
        public static bool ValidColor(string value)
        {
            int parsed;
            return value != null && value.Length == 7 && value[0] == '#' && int.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
        }
        public Color Foreground { get { return ColorTranslator.FromHtml(TextColor); } }
        public Color Highlight { get { return ColorTranslator.FromHtml(ActiveColor); } }
        public Color HighlightEnd { get { return ColorTranslator.FromHtml(ValidColor(GradientEndColor) ? GradientEndColor : ActiveColor); } }
        public Color TranslationHighlightEnd { get { return ColorTranslator.FromHtml(ValidColor(TranslationGradientEndColor) ? TranslationGradientEndColor : TranslationActiveColor); } }
        public Color TranslationForeground { get { return ColorTranslator.FromHtml(ValidColor(TranslationColor) ? TranslationColor : TextColor); } }
        public Color TranslationHighlight { get { return ColorTranslator.FromHtml(ValidColor(TranslationActiveColor) ? TranslationActiveColor : ActiveColor); } }
        public Color Brightened(Color color)
        {
            double factor = LyricBrightness / 100.0;
            int maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            // Cap the gain as a whole, so pale custom colors retain their hue instead
            // of clipping all three channels independently to white.
            if (maximum > 0) factor = Math.Min(factor, 255.0 / maximum);
            return Color.FromArgb(color.A, (int)Math.Round(color.R * factor), (int)Math.Round(color.G * factor), (int)Math.Round(color.B * factor));
        }
    }
}
