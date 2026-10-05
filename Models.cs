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

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember] public bool QQEnabled = true;
        [DataMember] public bool NetEaseEnabled = true;
        [DataMember] public bool OverlayEnabled = true;
        [DataMember] public bool OnlineLyrics = true;
        [DataMember] public bool AutoCheckUpdates = true;
        [DataMember] public bool HideWhenPaused = false;
        [DataMember] public bool HideInstrumental = true;
        [DataMember] public double InstrumentalHoldSeconds = 10;
        [DataMember] public bool TwoLines = false;
        [DataMember] public bool ShowTranslation = true;
        [DataMember] public bool LongLineScroll = true;
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
        [DataMember] public bool KaraokeEnabled = true;
        [DataMember] public bool BoldLyrics = true;
        [DataMember] public bool HideTaskbarIcon = false;
        [DataMember] public bool EnableHotkey = true;
        [DataMember] public int HotkeyPreset = 0;
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
            AutoCheckUpdates = true;
            HideInstrumental = true;
            InstrumentalHoldSeconds = 10;
            KaraokeEnabled = true;
            BoldLyrics = true;
            EnableHotkey = true;
        }

        public void Normalize()
        {
            FontSize = Math.Max(10, Math.Min(30, FontSize));
            LyricBrightness = Math.Max(30, Math.Min(160, LyricBrightness));
            Width = Math.Max(180, Math.Min(1200, Width));
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
            HotkeyPreset = Math.Max(0, Math.Min(2, HotkeyPreset));
        }
        public static bool ValidColor(string value)
        {
            int parsed;
            return value != null && value.Length == 7 && value[0] == '#' && int.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
        }
        public Color Foreground { get { return ColorTranslator.FromHtml(TextColor); } }
        public Color Highlight { get { return ColorTranslator.FromHtml(ActiveColor); } }
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
