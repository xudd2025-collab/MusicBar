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
        public double PositionSeconds;
        public double DurationSeconds;
        public bool IsPlaying;
        public bool HasTimeline;
        public bool InterpolateTimeline = true;
        public double PlaybackRate = 1;
        public DateTime TimestampUtc = DateTime.UtcNow;
        public string Status = "";
        public string LiveLyric = "";
        public string TrackKey { get { return Player.ToString() + "|" + Title.Trim() + "|" + Artist.Trim() + "|" + Album.Trim(); } }
        public bool HasTrack { get { return Player != MusicPlayer.None && !string.IsNullOrWhiteSpace(Title); } }
        public double CurrentPosition
        {
            get
            {
                double elapsed = IsPlaying && HasTimeline && InterpolateTimeline ? Math.Max(0, (DateTime.UtcNow - TimestampUtc).TotalSeconds) : 0;
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
        public LyricLine() { Text = ""; }
        public LyricLine(double seconds, string text) { Seconds = seconds; Text = text ?? ""; }
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
        [DataMember] public bool TwoLines = false;
        [DataMember] public bool ShowTranslation = true;
        [DataMember] public bool LongLineScroll = true;
        [DataMember] public bool ClickThrough = true;
        [DataMember] public bool ShowSongWhenMissing = true;
        [DataMember] public bool FollowTaskbar = true;
        [DataMember] public bool PreviewEnabled = false;
        [DataMember] public string FontFamily = "Microsoft YaHei UI";
        [DataMember] public float FontSize = 14;
        [DataMember] public int LyricBrightness = 115;
        [DataMember] public string TextColor = "#F8FAFC";
        [DataMember] public string ActiveColor = "#4ADE80";
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
            AutoCheckUpdates = true;
        }

        public void Normalize()
        {
            FontSize = Math.Max(10, Math.Min(30, FontSize));
            LyricBrightness = Math.Max(30, Math.Min(160, LyricBrightness));
            Width = Math.Max(180, Math.Min(1200, Width));
            HorizontalOffset = Math.Max(-20000, Math.Min(20000, HorizontalOffset));
            VerticalOffset = Math.Max(-2000, Math.Min(2000, VerticalOffset));
            OffsetSeconds = Math.Max(-120, Math.Min(120, OffsetSeconds));
            MonitorIndex = Math.Max(0, Math.Min(20, MonitorIndex));
            if (!Enum.IsDefined(typeof(MusicPlayer), PreferredPlayer)) PreferredPlayer = MusicPlayer.None;
            if (Alignment != "left" && Alignment != "center" && Alignment != "right") Alignment = "left";
            if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = "Microsoft YaHei UI";
            if (!ValidColor(TextColor)) TextColor = "#F8FAFC";
            if (!ValidColor(ActiveColor)) ActiveColor = "#4ADE80";
        }
        public static bool ValidColor(string value)
        {
            int parsed;
            return value != null && value.Length == 7 && value[0] == '#' && int.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
        }
        public Color Foreground { get { return ColorTranslator.FromHtml(TextColor); } }
        public Color Highlight { get { return ColorTranslator.FromHtml(ActiveColor); } }
        public Color Brightened(Color color)
        {
            double factor = LyricBrightness / 100.0;
            return Color.FromArgb(color.A, Math.Min(255, (int)Math.Round(color.R * factor)), Math.Min(255, (int)Math.Round(color.G * factor)), Math.Min(255, (int)Math.Round(color.B * factor)));
        }
    }
}
