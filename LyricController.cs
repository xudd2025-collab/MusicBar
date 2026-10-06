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
        public event EventHandler PlaybackFrame;
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
        public double OriginalKaraokeProgress, TranslationKaraokeProgress;
        public bool IsSongInfo;
        public int CurrentLineIndex = -1;
        public string Message = "打开 QQ 音乐或网易云音乐，并播放一首歌。";
        public bool ManualMode;
        public bool ManualPlaying;
        public bool Searching;
        public bool QQRunning;
        public bool NetEaseRunning;
        public bool NeedsNetEaseIntegration
        {
            get { return Snapshot.Player == MusicPlayer.NetEase && Snapshot.HasTrack && !Snapshot.HasTimeline; }
        }
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
        private int consecutiveFailures;
        private int inconclusiveAttempts;
        internal DateTime NextLyricRetryUtc { get { return nextLyricRetryUtc; } }
        private LyricDocument translatedDocument;
        private string observedTranslation = "";
        private string documentPlaybackKey = "";
        private bool automaticDocument;
        private bool lookupHasReliableDuration;
        private double lookupDuration;
        private double pendingDuration;
        private DateTime pendingDurationSinceUtc;

        public LyricController(AppSettings settings, SettingsStore store)
            : this(settings, store, new LyricRepository(store.DataDirectory)) { }

        internal LyricController(AppSettings settings, SettingsStore store, LyricRepository repository)
        {
            Settings = settings;
            Store = store;
            Reader = new MusicSessionReader();
            Repository = repository;
            ticker = new System.Windows.Forms.Timer { Interval = 40 };
            ticker.Tick += async delegate
            {
                if (disposed) return;
                UpdateLine();
                if (PlaybackFrame != null) PlaybackFrame(this, EventArgs.Empty);
                if (!polling && DateTime.UtcNow >= nextPoll)
                {
                    polling = true;
                    try { await PollAsync(); }
                    catch (Exception ex) { Message = "读取播放器失败：" + ex.Message; Notify(); }
                    finally { polling = false; nextPoll = DateTime.UtcNow.AddMilliseconds(Snapshot.Player == MusicPlayer.NetEase ? 100 : 250); }
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
            QQRunning = Reader.IsPlayerRunning(MusicPlayer.QQMusic);
            NetEaseRunning = Reader.IsPlayerRunning(MusicPlayer.NetEase);
            ApplySnapshot(latest, DateTime.UtcNow);
        }
        internal void ApplySnapshot(MusicSnapshot latest, DateTime utcNow)
        {
            if (disposed) return;
            if (latest != null && (latest.Player == MusicPlayer.QQMusic && !Settings.QQEnabled || latest.Player == MusicPlayer.NetEase && !Settings.NetEaseEnabled))
                latest = new MusicSnapshot { Status = "当前播放器插件已关闭，等待已启用的播放器。" };
            Snapshot = latest ?? new MusicSnapshot();
            if (Snapshot.HasTrack)
            {
                if (trackKey != Snapshot.PlaybackKey)
                {
                    trackKey = Snapshot.PlaybackKey;
                    ManualMode = false;
                    ManualPlaying = false;
                    Document = null;
                    Current = Next = CurrentTranslation = ""; RemainingLineSeconds = 0;
                    consecutiveFailures = inconclusiveAttempts = 0;
                    nextLyricRetryUtc = DateTime.MaxValue;
                    BeginLoad();
                }
                else if ((Document == null || automaticDocument) && !ManualMode && Settings.OnlineLyrics && Snapshot.IsPlaying && LookupInformationImproved(utcNow))
                {
                    // A late/corrected duration gets a fresh matching budget, even
                    // after the earlier incomplete search exhausted its attempts.
                    consecutiveFailures = inconclusiveAttempts = 0;
                    // The earlier document may have been selected using the old
                    // recording duration. Validate again instead of showing it
                    // against the corrected recording. Manual bindings are kept.
                    Document = null;
                    Current = Next = CurrentTranslation = ""; RemainingLineSeconds = 0;
                    BeginLoad();
                }
                else if (Document == null && !Searching && !ManualMode && Settings.OnlineLyrics && Snapshot.IsPlaying && utcNow >= nextLyricRetryUtc)
                {
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
        private bool LookupInformationImproved(DateTime utcNow)
        {
            double duration = Snapshot.DurationSeconds;
            bool reliable = Snapshot.HasTimeline && duration > 0 && !double.IsNaN(duration) && !double.IsInfinity(duration);
            if (!reliable || (lookupHasReliableDuration && Math.Abs(duration - lookupDuration) <= .25))
            { pendingDurationSinceUtc = DateTime.MinValue; return false; }
            if (!lookupHasReliableDuration) return true;
            // A corrected duration must settle before replacing an in-flight
            // lookup. Small native rounding differences never restart requests.
            if (pendingDurationSinceUtc == DateTime.MinValue || Math.Abs(duration - pendingDuration) > .25)
            { pendingDuration = duration; pendingDurationSinceUtc = utcNow; return false; }
            return utcNow >= pendingDurationSinceUtc.AddMilliseconds(500);
        }
        private async void BeginLoad()
        {
            CancelRequest();
            nextLyricRetryUtc = DateTime.MaxValue;
            var requestedTrack = Snapshot.Copy();
            lookupDuration = requestedTrack.DurationSeconds;
            lookupHasReliableDuration = requestedTrack.HasTimeline && lookupDuration > 0 && !double.IsNaN(lookupDuration) && !double.IsInfinity(lookupDuration);
            pendingDurationSinceUtc = DateTime.MinValue;
            var saved = Store.LoadOverride(Snapshot.TrackKey);
            if (saved != null)
            {
                LrcParser.EnsureTranslation(saved);
                Document = saved;
                automaticDocument = false;
                documentPlaybackKey = Snapshot.PlaybackKey;
                Message = "使用这首歌的本地歌词 · " + saved.Source;
                UpdateLine(); Notify(); return;
            }
            if (!Settings.OnlineLyrics)
            {
                Message = "在线歌词已关闭，可在「歌词管理」导入 LRC。"; Notify(); return;
            }
            if (NeedsNetEaseIntegration && !MusicSnapshot.ValidNetEaseTrackId(Snapshot.PlatformTrackId))
            {
                Message = "网易云当前只提供歌名，尚未接入播放进度。请点「修复并启动接入」；退出网易云后，MusicBar 会自动重新接入。";
                UpdateLine(); Notify(); return;
            }
            int requestedRevision = revision;
            var request = new CancellationTokenSource();
            lyricRequest = request;
            Searching = true;
            Message = "正在匹配歌词…";
            Notify();
            try
            {
                LyricDocument document = await Repository.FindAsync(requestedTrack, request.Token);
                if (disposed || request.IsCancellationRequested || requestedRevision != revision || requestedTrack.PlaybackKey != trackKey) return;
                if (document != null) LrcParser.EnsureTranslation(document);
                bool keepExisting = Document != null && Document.HasTimedLyrics && documentPlaybackKey == requestedTrack.PlaybackKey &&
                    (document == null || !document.HasTimedLyrics);
                if (!keepExisting) { Document = document; documentPlaybackKey = requestedTrack.PlaybackKey; automaticDocument = true; }
                Message = keepExisting ? "继续使用已连接的歌词 · " + Document.Source : document == null ? "未找到可靠匹配，请搜索选择歌词或导入 LRC。" : document.HasTimedLyrics ? "歌词已连接 · " + document.Source : string.IsNullOrWhiteSpace(document.PlainText) ? document.Source + "，可导入本地 LRC。" : "这首歌没有逐行时间标记，可导入带时间轴的 LRC。";
                consecutiveFailures = 0;
                if (Document == null) { inconclusiveAttempts++; ScheduleRetry(false); }
            }
            catch (OperationCanceledException)
            {
                // Only a superseded/explicitly cancelled request is silent.
                // HttpClient timeouts can also surface as cancellation without
                // cancelling our token; leaving MaxValue would strand this song.
                if (!disposed && requestedRevision == revision && !request.IsCancellationRequested)
                    RecordServiceFailure("歌词请求超时。");
            }
            catch (Exception ex)
            {
                if (!disposed && requestedRevision == revision)
                {
                    RecordServiceFailure(ex.Message);
                }
            }
            finally
            {
                if (!disposed && requestedRevision == revision) { Searching = false; UpdateLine(); Notify(); }
            }
        }
        private void RecordServiceFailure(string detail)
        {
            consecutiveFailures = Math.Min(5, consecutiveFailures + 1);
            ScheduleRetry(true);
            Message = (Document != null && Document.HasTimedLyrics ? "刷新暂未完成，继续显示已连接的歌词。" : "在线歌词接口暂时不可用，将在播放时自动重试；也可搜索或导入 LRC。") + Environment.NewLine + detail;
        }
        public void Reload()
        {
            if (!Snapshot.HasTrack) { Message = "请先播放歌曲，或手动搜索 / 导入歌词。"; Notify(); return; }
            // Keep the connected same-song document visible while refreshing.
            // Repeated clicks must not restart an in-flight request indefinitely.
            if (Searching) return;
            if (documentPlaybackKey != Snapshot.PlaybackKey)
            {
                Document = null;
                Current = Next = CurrentTranslation = ""; RemainingLineSeconds = 0;
            }
            consecutiveFailures = inconclusiveAttempts = 0;
            BeginLoad();
        }
        private void ScheduleRetry(bool serviceFailure)
        {
            // An unavailable service can recover later in the same song. Back off
            // without permanently disabling recovery after three failed requests.
            // A successful search with no safe match still has a bounded budget.
            if (serviceFailure)
            {
                double[] delays = { 2, 8, 24, 60, 120 };
                nextLyricRetryUtc = DateTime.UtcNow.AddSeconds(delays[Math.Max(0, consecutiveFailures - 1)]);
            }
            else if (inconclusiveAttempts < 3)
                nextLyricRetryUtc = DateTime.UtcNow.AddSeconds(inconclusiveAttempts == 1 ? 8 : 24);
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
            automaticDocument = false;
            documentPlaybackKey = Snapshot.PlaybackKey;
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
            OriginalKaraokeProgress = TranslationKaraokeProgress = 0;
            if (Document != null && Document.HasTimedLyrics)
            {
                if (Snapshot.HasTimeline || ManualMode)
                {
                    double position = (ManualMode ? ManualPosition : Snapshot.CurrentPosition) + Settings.OffsetSeconds;
                    int index = Document.FindLine(position);
                    lineIndex = index;
                    if (index >= 0) current = Document.Lines[index].Text;
                    else if (!Settings.HideInstrumental && Settings.ShowSongWhenMissing && Snapshot.HasTrack) { current = songInfo; showingSongInfo = true; }
                    if (index + 1 >= 0 && index + 1 < Document.Lines.Count) next = Document.Lines[index + 1].Text;
                    if (index >= 0)
                    {
                        if (Settings.ShowTranslation) translation = LrcParser.GetTranslationForLine(Document, index);
                        double end = index + 1 < Document.Lines.Count ? Document.Lines[index + 1].Seconds : Snapshot.DurationSeconds;
                        double singingEnd = end;
                        if (Settings.HideInstrumental && end - Document.Lines[index].Seconds >= Math.Max(20, Settings.InstrumentalHoldSeconds + 4))
                            singingEnd = Math.Min(end, Document.Lines[index].Seconds + Settings.InstrumentalHoldSeconds);
                        if (Settings.KaraokeEnabled)
                        {
                            OriginalKaraokeProgress = LrcParser.KaraokeProgress(Document.Lines[index], position, singingEnd);
                            if (!string.IsNullOrWhiteSpace(translation) && index < Document.TranslationLines.Count)
                                TranslationKaraokeProgress = LrcParser.KaraokeProgress(Document.TranslationLines[index], position, singingEnd);
                        }
                        if (end > position) RemainingLineSeconds = (end - position) / (ManualMode || Snapshot.PlaybackRate <= 0 ? 1 : Snapshot.PlaybackRate);
                        if (Settings.HideInstrumental)
                        {
                            bool nonVocal = LrcParser.IsNonVocalCue(current) ||
                                (index == 0 && Document.Lines[index].Seconds <= 5 && IsTrackHeading(current));
                            // LRC gives starts only. Infer just long gaps, never ordinary
                            // short sentences; the user can extend the hold for long notes.
                            double hold = Document.Lines[index].EndSeconds > Document.Lines[index].Seconds && Document.Lines[index].Words.Count > 0
                                ? Math.Max(0, Document.Lines[index].EndSeconds - Document.Lines[index].Seconds) : Settings.InstrumentalHoldSeconds;
                            double gap = end - Document.Lines[index].Seconds;
                            bool longGap = gap >= Math.Max(20, hold + 4);
                            if (nonVocal || (longGap && position >= Document.Lines[index].Seconds + hold))
                            {
                                current = next = translation = "";
                                RemainingLineSeconds = 0;
                            }
                            else if (longGap && RemainingLineSeconds > 0)
                                RemainingLineSeconds = Math.Min(RemainingLineSeconds, (Document.Lines[index].Seconds + hold - position) / (ManualMode || Snapshot.PlaybackRate <= 0 ? 1 : Snapshot.PlaybackRate));
                        }
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
        private bool IsTrackHeading(string text)
        {
            string title = (Snapshot.Title ?? "").Trim();
            string line = (text ?? "").Trim();
            if (title.Length == 0) return false;
            return line.StartsWith(title + " - ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(title + " · ", StringComparison.OrdinalIgnoreCase);
        }
        public void Dispose()
        {
            disposed = true;
            ticker.Stop(); ticker.Dispose();
            CancelRequest(); Reader.Dispose(); Repository.Dispose();
        }
    }
}
