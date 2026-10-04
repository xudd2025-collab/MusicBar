using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBar
{
    public sealed class LyricController : IDisposable
    {
        public event EventHandler Changed;
        public readonly AppSettings Settings;
        public readonly SettingsStore Store;
        public readonly MusicSessionReader Reader;
        public readonly LyricRepository Repository;
        public MusicSnapshot Snapshot = new MusicSnapshot();
        public LyricDocument Document;
        public string Current = "";
        public string Next = "";
        public string CurrentTranslation = "";
        public double RemainingLineSeconds;
        public bool IsSongInfo;
        public int CurrentLineIndex = -1;
        public string Message = "打开 QQ 音乐或网易云音乐，并播放一首歌。";
        public bool ManualMode;
        public bool ManualPlaying;
        public bool Searching;
        public bool QQRunning;
        public bool NetEaseRunning;
        private double manualPosition;
        private DateTime manualAt = DateTime.UtcNow;
        private readonly System.Windows.Forms.Timer ticker;
        private DateTime nextPoll = DateTime.MinValue;
        private bool polling;
        private bool disposed;
        private CancellationTokenSource lyricRequest;
        private int revision;
        private string trackKey = "";
        private DateTime nextLyricRetryUtc = DateTime.MaxValue;
        private int automaticAttempts;
        private LyricDocument translatedDocument;
        private string observedTranslation = "";

        public LyricController(AppSettings settings, SettingsStore store)
        {
            Settings = settings;
            Store = store;
            Reader = new MusicSessionReader();
            Repository = new LyricRepository(store.DataDirectory);
            ticker = new System.Windows.Forms.Timer { Interval = 40 };
            ticker.Tick += async delegate
            {
                if (disposed) return;
                UpdateLine();
                if (!polling && DateTime.UtcNow >= nextPoll)
                {
                    polling = true;
                    try { await PollAsync(); }
                    catch (Exception ex) { Message = "读取播放器失败：" + ex.Message; Notify(); }
                    finally { polling = false; nextPoll = DateTime.UtcNow.AddMilliseconds(350); }
                }
            };
        }
        public void Start() { ticker.Start(); }
        public void Notify() { if (!disposed && Changed != null) Changed(this, EventArgs.Empty); }
        public void SettingsChanged(bool reloadLyrics)
        {
            Settings.Normalize();
            Store.Save(Settings);
            nextPoll = DateTime.MinValue;
            bool disabledCurrent = Snapshot.Player == MusicPlayer.QQMusic && !Settings.QQEnabled || Snapshot.Player == MusicPlayer.NetEase && !Settings.NetEaseEnabled;
            if (disabledCurrent)
            {
                CancelRequest();
                Snapshot = new MusicSnapshot();
                trackKey = "";
                Document = null;
                ManualMode = false;
                ManualPlaying = false;
                Current = Next = CurrentTranslation = ""; RemainingLineSeconds = 0;
                Message = "当前播放器插件已关闭。";
            }
            else if (reloadLyrics && Snapshot.HasTrack) Reload();
            UpdateLine();
            Notify();
        }
        private async Task PollAsync()
        {
            MusicSnapshot latest = await Reader.ReadAsync(Settings.QQEnabled, Settings.NetEaseEnabled, Settings.PreferredPlayer);
            if (disposed) return;
            if (latest != null && (latest.Player == MusicPlayer.QQMusic && !Settings.QQEnabled || latest.Player == MusicPlayer.NetEase && !Settings.NetEaseEnabled))
                latest = new MusicSnapshot { Status = "当前播放器插件已关闭，等待已启用的播放器。" };
            QQRunning = Reader.IsPlayerRunning(MusicPlayer.QQMusic);
            NetEaseRunning = Reader.IsPlayerRunning(MusicPlayer.NetEase);
            MusicSnapshot previous = Snapshot;
            Snapshot = latest ?? new MusicSnapshot();
            if (Snapshot.HasTrack)
            {
                if (trackKey != Snapshot.TrackKey)
                {
                    trackKey = Snapshot.TrackKey;
                    ManualMode = false;
                    ManualPlaying = false;
                    Document = null;
                    Current = Next = CurrentTranslation = ""; RemainingLineSeconds = 0;
                    automaticAttempts = 0;
                    nextLyricRetryUtc = DateTime.MaxValue;
                    BeginLoad();
                }
                else if (Document == null && !Searching && !ManualMode && Settings.OnlineLyrics && automaticAttempts < 3 && DateTime.UtcNow >= nextLyricRetryUtc)
                {
                    BeginLoad();
                }
                else if (!previous.HasTimeline && Snapshot.HasTimeline && Document == null && !Searching && !ManualMode && Settings.OnlineLyrics && automaticAttempts < 3)
                {
                    // Metadata and the fresh timeline can arrive on different media events.
                    // Retry with a reliable duration instead of retaining an inconclusive match.
                    BeginLoad();
                }
            }
            else if (trackKey.Length > 0)
            {
                trackKey = "";
                CancelRequest();
                Document = null;
                ManualMode = false;
                ManualPlaying = false;
                Current = Next = CurrentTranslation = ""; RemainingLineSeconds = 0;
                Message = Snapshot.Status;
                nextLyricRetryUtc = DateTime.MaxValue;
            }
            else if (!ManualMode && !Searching) Message = Snapshot.Status;
            UpdateLine();
            Notify();
        }
        private void CancelRequest()
        {
            revision++;
            if (lyricRequest != null) { lyricRequest.Cancel(); lyricRequest.Dispose(); lyricRequest = null; }
            Searching = false;
        }
        private async void BeginLoad()
        {
            CancelRequest();
            nextLyricRetryUtc = DateTime.MaxValue;
            var saved = Store.LoadOverride(trackKey);
            if (saved != null)
            {
                LrcParser.EnsureTranslation(saved);
                Document = saved;
                Message = "使用这首歌的本地歌词 · " + saved.Source;
                UpdateLine(); Notify(); return;
            }
            if (!Settings.OnlineLyrics)
            {
                Message = "在线歌词已关闭，可在「歌词管理」导入 LRC。"; Notify(); return;
            }
            var requestedTrack = Snapshot;
            automaticAttempts++;
            int requestedRevision = revision;
            var request = new CancellationTokenSource();
            lyricRequest = request;
            Searching = true;
            Message = "正在匹配歌词…";
            Notify();
            try
            {
                LyricDocument document = await Repository.FindAsync(requestedTrack, request.Token);
                if (disposed || request.IsCancellationRequested || requestedRevision != revision || requestedTrack.TrackKey != trackKey) return;
                if (document != null) LrcParser.EnsureTranslation(document);
                Document = document;
                Message = document == null ? "未找到可靠匹配，请搜索选择歌词或导入 LRC。" : document.HasTimedLyrics ? "歌词已连接 · " + document.Source : string.IsNullOrWhiteSpace(document.PlainText) ? document.Source + "，可导入本地 LRC。" : "这首歌没有逐行时间标记，可导入带时间轴的 LRC。";
                if (document == null) ScheduleRetry();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!disposed && requestedRevision == revision)
                {
                    Message = (automaticAttempts < 3 ? "在线歌词获取失败，正在准备重试；也可搜索或导入 LRC。" : "在线歌词获取失败，可重新获取、搜索或导入 LRC。") + Environment.NewLine + ex.Message;
                    ScheduleRetry();
                }
            }
            finally
            {
                if (!disposed && requestedRevision == revision) { Searching = false; UpdateLine(); Notify(); }
            }
        }
        public void Reload()
        {
            if (!Snapshot.HasTrack) { Message = "请先播放歌曲，或手动搜索 / 导入歌词。"; Notify(); return; }
            Document = null;
            Current = Next = CurrentTranslation = ""; RemainingLineSeconds = 0;
            automaticAttempts = 0;
            BeginLoad();
        }
        private void ScheduleRetry()
        {
            if (automaticAttempts < 3) nextLyricRetryUtc = DateTime.UtcNow.AddSeconds(automaticAttempts == 1 ? 8 : 24);
        }
        public void RestoreAutomatic()
        {
            try { if (Snapshot.HasTrack) Store.RemoveOverride(Snapshot.TrackKey); }
            catch (Exception ex) { Message = "清除本地歌词失败：" + ex.Message; Notify(); return; }
            ManualMode = false; ManualPlaying = false;
            Document = null;
            Current = Next = CurrentTranslation = ""; RemainingLineSeconds = 0;
            Reload();
        }
        public void ApplyDocument(LyricDocument document)
        {
            CancelRequest();
            LrcParser.EnsureTranslation(document);
            Document = document;
            ManualPlaying = false;
            manualPosition = 0;
            manualAt = DateTime.UtcNow;
            ManualMode = !Snapshot.HasTimeline;
            Message = "已使用歌词 · " + document.Source;
            if (!document.HasTimedLyrics) Message += " · 没有时间轴，无法逐行同步";
            if (Snapshot.HasTrack && !Store.SaveOverride(Snapshot.TrackKey, document)) Message += Environment.NewLine + Store.LastError;
            UpdateLine(); Notify();
        }
        public void Import(string file)
        {
            if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidOperationException("歌词文件超过 2 MB，请选择普通 LRC 文件。");
            string text;
            byte[] bytes = File.ReadAllBytes(file);
            try { text = new System.Text.UTF8Encoding(false, true).GetString(bytes); }
            catch (System.Text.DecoderFallbackException) { text = System.Text.Encoding.GetEncoding(936).GetString(bytes); }
            var document = LrcParser.Parse(text);
            document.Source = "本地 LRC · " + Path.GetFileName(file);
            if (document.Lines.Count == 0 && string.IsNullOrWhiteSpace(document.PlainText)) throw new InvalidOperationException("文件没有可读取的歌词。");
            ApplyDocument(document);
        }
        public double ManualPosition
        {
            get { return Math.Max(0, manualPosition + (ManualPlaying ? (DateTime.UtcNow - manualAt).TotalSeconds : 0)); }
        }
        public void SeekManual(double seconds)
        {
            manualPosition = Math.Max(0, Math.Min(7200, seconds));
            manualAt = DateTime.UtcNow;
            ManualMode = true;
            UpdateLine(); Notify();
        }
        public void ToggleManual()
        {
            manualPosition = ManualPosition;
            manualAt = DateTime.UtcNow;
            ManualMode = true;
            ManualPlaying = !ManualPlaying;
            UpdateLine(); Notify();
        }
        public void UsePlayerClock() { ManualMode = false; ManualPlaying = false; UpdateLine(); Notify(); }
        private void UpdateLine()
        {
            if (Document != translatedDocument || (Document != null && observedTranslation != (Document.Translation ?? "")))
            {
                if (Document != null) LrcParser.EnsureTranslation(Document);
                translatedDocument = Document;
                observedTranslation = Document == null ? "" : Document.Translation ?? "";
            }
            string current = "", next = "", translation = "";
            string songInfo = Snapshot.Title + (string.IsNullOrWhiteSpace(Snapshot.Artist) ? "" : " · " + Snapshot.Artist);
            bool showingSongInfo = false;
            int lineIndex = -1;
            RemainingLineSeconds = 0;
            if (Document != null && Document.HasTimedLyrics)
            {
                if (Snapshot.HasTimeline || ManualMode)
                {
                    double position = (ManualMode ? ManualPosition : Snapshot.CurrentPosition) + Settings.OffsetSeconds;
                    int index = Document.FindLine(position);
                    lineIndex = index;
                    if (index >= 0) current = Document.Lines[index].Text;
                    else if (Settings.ShowSongWhenMissing && Snapshot.HasTrack) { current = songInfo; showingSongInfo = true; }
                    if (index + 1 >= 0 && index + 1 < Document.Lines.Count) next = Document.Lines[index + 1].Text;
                    if (index >= 0)
                    {
                        if (Settings.ShowTranslation) translation = LrcParser.GetTranslationForLine(Document, index);
                        double end = index + 1 < Document.Lines.Count ? Document.Lines[index + 1].Seconds : Snapshot.DurationSeconds;
                        if (end > position) RemainingLineSeconds = (end - position) / (ManualMode || Snapshot.PlaybackRate <= 0 ? 1 : Snapshot.PlaybackRate);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(Snapshot.LiveLyric)) current = Snapshot.LiveLyric;
                else if (Settings.ShowSongWhenMissing && Snapshot.HasTrack) { current = songInfo; showingSongInfo = true; }
            }
            else if (!string.IsNullOrWhiteSpace(Snapshot.LiveLyric)) current = Snapshot.LiveLyric;
            else if (Settings.ShowSongWhenMissing && Snapshot.HasTrack) { current = songInfo; showingSongInfo = true; }
            bool changed = Current != current || Next != next || CurrentTranslation != translation || IsSongInfo != showingSongInfo || CurrentLineIndex != lineIndex;
            Current = current; Next = next; CurrentTranslation = translation; IsSongInfo = showingSongInfo; CurrentLineIndex = lineIndex;
            if (changed) Notify();
        }
        public bool ShouldDisplay
        {
            get
            {
                if (Settings.PreviewEnabled) return true;
                if (!Settings.OverlayEnabled || string.IsNullOrWhiteSpace(Current)) return false;
                return !Settings.HideWhenPaused || (ManualMode ? ManualPlaying : Snapshot.IsPlaying);
            }
        }
        public void Dispose()
        {
            disposed = true;
            ticker.Stop(); ticker.Dispose();
            CancelRequest(); Reader.Dispose(); Repository.Dispose();
        }
    }
}
