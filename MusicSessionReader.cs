using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;

namespace MusicBar
{
    // Windows media sessions plus the current NetEase client's local read-only bridge.
    public sealed class MusicSessionReader : IDisposable
    {
        private const int ReadTimeoutMilliseconds = 3200;
        private const double MaximumTrustedTimelineAgeSeconds = 5;
        private readonly object _sync = new object();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly Dictionary<string, TimelineAnchor> _anchors = new Dictionary<string, TimelineAnchor>(StringComparer.OrdinalIgnoreCase);
        private GlobalSystemMediaTransportControlsSessionManager _manager;
        private Task<MusicSnapshot> _readTask;
        private DateTime _managerRetryUtc = DateTime.MinValue;
        private string _lastManagerError = "";
        private volatile bool _disposed;
        private readonly NetEaseBridgeReader _netease = new NetEaseBridgeReader();
        private MusicSnapshot _lastNetEase;
        private DateTime _lastNetEaseUtc;

        public async Task<MusicSnapshot> ReadAsync(bool qqEnabled, bool neteaseEnabled, MusicPlayer preferredPlayer)
        {
            if (_disposed) return Empty("媒体读取器已关闭");
            if (!qqEnabled && !neteaseEnabled) return Empty("请先在插件页启用 QQ 音乐或网易云音乐");
            Task<MusicSnapshot> work;
            lock (_sync)
            {
                // Reuse a slow native call instead of creating another worker every polling tick.
                if (_readTask == null || _readTask.IsCompleted)
                    _readTask = Task.Run(() => ReadCoreAsync(qqEnabled, neteaseEnabled, preferredPlayer));
                work = _readTask;
            }
            var completed = await Task.WhenAny(work, Task.Delay(ReadTimeoutMilliseconds)).ConfigureAwait(false);
            if (completed != work) return Empty("Windows 媒体信息读取超时；请稍后重试");
            try
            {
                var result = await work.ConfigureAwait(false);
                // A plugin can be disabled while an earlier read is still completing.
                if (result.Player != MusicPlayer.None && !Enabled(result.Player, qqEnabled, neteaseEnabled))
                    return Empty("等待已启用播放器的媒体信息");
                return result;
            }
            catch (OperationCanceledException) { return Empty("媒体信息读取已取消"); }
            catch (Exception ex) { return Empty("媒体信息读取失败：" + ShortMessage(ex)); }
        }

        public bool IsPlayerRunning(MusicPlayer player)
        {
            var names = player == MusicPlayer.QQMusic ? new[] { "QQMusic" } :
                player == MusicPlayer.NetEase ? new[] { "cloudmusic", "NeteaseCloudMusic" } : new string[0];
            foreach (var name in names)
            {
                Process[] processes = null;
                try
                {
                    processes = Process.GetProcessesByName(name);
                    if (processes.Length > 0) return true;
                }
                catch { }
                finally
                {
                    if (processes != null) foreach (var process in processes) process.Dispose();
                }
            }
            return false;
        }

        private async Task<MusicSnapshot> ReadCoreAsync(bool qqEnabled, bool neteaseEnabled, MusicPlayer preferredPlayer)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            MusicSnapshot netease = neteaseEnabled ? await _netease.ReadAsync(_lifetime.Token).ConfigureAwait(false) : null;
            if (netease != null) { _lastNetEase = netease; _lastNetEaseUtc = DateTime.UtcNow; }
            else if (!neteaseEnabled) _lastNetEase = null;
            if (_manager == null && DateTime.UtcNow >= _managerRetryUtc)
            {
                try
                {
                    _manager = await WaitOperation(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 1600, _lifetime.Token).ConfigureAwait(false);
                    _lastManagerError = "";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _lastManagerError = ShortMessage(ex);
                    _managerRetryUtc = DateTime.UtcNow.AddSeconds(5);
                }
            }
            if (_manager == null) return netease ?? Waiting(qqEnabled, neteaseEnabled, _lastManagerError);

            try
            {
                var sessions = _manager.GetSessions();
                var current = _manager.GetCurrentSession();
                var candidates = new List<SessionCandidate>();
                foreach (var session in sessions)
                {
                    try
                    {
                        var source = session.SourceAppUserModelId ?? "";
                        var player = IdentifyPlayer(source);
                        if (!Enabled(player, qqEnabled, neteaseEnabled)) continue;
                        var playback = session.GetPlaybackInfo();
                        bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        if (player == MusicPlayer.NetEase && netease != null) playing = netease.IsPlaying;
                        double rate = playback.PlaybackRate.HasValue ? playback.PlaybackRate.Value : 1;
                        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0) rate = 1;
                        if (rate == 0) playing = false;
                        int score = (playing ? 100 : 0) + (player == preferredPlayer ? 20 : 0) + (object.Equals(session, current) ? 10 : 0);
                        candidates.Add(new SessionCandidate { Session = session, Source = source, Player = player, Playing = playing, Rate = rate, Score = score });
                    }
                    catch { /* A session can disappear while Windows enumerates it. */ }
                }
                candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (candidates.Count == 0) return netease ?? Waiting(qqEnabled, neteaseEnabled, "");
                if (netease != null && !candidates.Exists(c => c.Player == MusicPlayer.NetEase))
                {
                    int bridgeScore = (netease.IsPlaying ? 100 : 0) + (preferredPlayer == MusicPlayer.NetEase ? 20 : 0);
                    if (bridgeScore > candidates[0].Score) return netease;
                }

                string readError = "";
                var started = Stopwatch.StartNew();
                MusicSnapshot blank = null;
                foreach (var candidate in candidates)
                {
                    if (candidate.Player == MusicPlayer.NetEase && netease != null) return netease;
                    if (started.ElapsedMilliseconds > 2200) break;
                    try
                    {
                        var media = await WaitOperation(candidate.Session.TryGetMediaPropertiesAsync(), 800, _lifetime.Token).ConfigureAwait(false);
                        var latestPlayback = candidate.Session.GetPlaybackInfo();
                        candidate.Playing = latestPlayback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        candidate.Rate = latestPlayback.PlaybackRate.HasValue ? latestPlayback.PlaybackRate.Value : 1;
                        if (double.IsNaN(candidate.Rate) || double.IsInfinity(candidate.Rate) || candidate.Rate < 0) candidate.Rate = 1;
                        if (candidate.Rate == 0) candidate.Playing = false;
                        var snapshot = new MusicSnapshot
                        {
                            Player = candidate.Player,
                            Title = Clean(media.Title),
                            Artist = Clean(media.Artist),
                            Album = Clean(media.AlbumTitle),
                            IsPlaying = candidate.Playing,
                            PlaybackRate = candidate.Rate,
                            TimestampUtc = DateTime.UtcNow
                        };
                        if (!snapshot.HasTrack)
                        {
                            snapshot.Status = "已连接 " + MusicSnapshot.PlayerName(snapshot.Player) + "，请播放歌曲";
                            if (blank == null) blank = snapshot;
                            continue;
                        }
                        try
                        {
                            TimelineObservation observation;
                            bool coherent = TryReadTimelineObservation(
                                () => ReadPlaybackClock(candidate.Session),
                                () => ReadRawTimeline(candidate.Session),
                                () => DateTime.UtcNow,
                                out observation);
                            snapshot.IsPlaying = observation.Playback.IsPlaying;
                            snapshot.PlaybackRate = observation.Playback.Rate;
                            // The timestamp belongs to the completed native sample, not to the
                            // earlier metadata response. LastUpdatedTime supplies its real age.
                            snapshot.TimestampUtc = observation.CapturedUtc;
                            if (coherent)
                                ApplyTimeline(snapshot, candidate.Source, observation.Timeline.Position,
                                    observation.Timeline.Duration, observation.Timeline.UpdatedUtc, observation.Playback.Rate);
                            else snapshot.HasTimeline = false;
                        }
                        catch { snapshot.HasTimeline = false; }
                        snapshot.Status = (snapshot.IsPlaying ? "播放中 · " : "已暂停 · ") + MusicSnapshot.PlayerName(snapshot.Player);
                        if (!snapshot.HasTimeline) snapshot.Status += " · 播放器未提供歌曲进度";
                        if (snapshot.Player == MusicPlayer.NetEase)
                        {
                            snapshot = RetainNetEaseIdentity(snapshot, _lastNetEase, _lastNetEaseUtc, DateTime.UtcNow);
                            if (!snapshot.HasTimeline) snapshot.Status += " · 请在常规设置修复网易云启动方式";
                        }
                        return snapshot;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { readError = ShortMessage(ex); }
                }
                return netease ?? blank ?? Waiting(qqEnabled, neteaseEnabled, readError);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Explorer or its media service can restart; request a fresh manager next time.
                _manager = null;
                _managerRetryUtc = DateTime.UtcNow.AddSeconds(2);
                _lastManagerError = ShortMessage(ex);
                return netease ?? Waiting(qqEnabled, neteaseEnabled, _lastManagerError);
            }
        }

        internal static MusicSnapshot RetainNetEaseIdentity(MusicSnapshot observed, MusicSnapshot cached, DateTime receivedUtc, DateTime now)
        {
            // Brief bridge interruptions must not replace the recording ID/album
            // with incomplete SMTC metadata and cancel an otherwise valid lookup.
            // Freeze the last position, respect pause, and discard it after 2s or
            // as soon as the public metadata identifies another recording.
            if (observed == null || observed.Player != MusicPlayer.NetEase || observed.HasTimeline || cached == null ||
                cached.Player != MusicPlayer.NetEase || !cached.HasTimeline || now < receivedUtc || now - receivedUtc > TimeSpan.FromSeconds(2) ||
                !string.Equals(observed.Title, cached.Title, StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(observed.Artist) && !string.Equals(observed.Artist, cached.Artist, StringComparison.Ordinal) && !cached.Artist.StartsWith(observed.Artist + " / ", StringComparison.Ordinal)) ||
                (!string.IsNullOrEmpty(observed.Album) && observed.Album != cached.Album) ||
                (!string.IsNullOrEmpty(observed.PlatformTrackId) && observed.PlatformTrackId != cached.PlatformTrackId)) return observed;
            MusicSnapshot retained = cached.Copy();
            retained.PositionSeconds = cached.PositionSeconds;
            retained.InterpolateTimeline = false;
            retained.IsPlaying = observed.IsPlaying;
            retained.Status = "网易云接入暂时重连 · 等待新的真实进度";
            return retained;
        }

        private static PlaybackClock ReadPlaybackClock(GlobalSystemMediaTransportControlsSession session)
        {
            var playback = session.GetPlaybackInfo();
            double rate = playback.PlaybackRate.HasValue ? playback.PlaybackRate.Value : 1;
            if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0) rate = 1;
            return new PlaybackClock { Status = playback.PlaybackStatus, Rate = rate };
        }

        private static RawTimeline ReadRawTimeline(GlobalSystemMediaTransportControlsSession session)
        {
            var timeline = session.GetTimelineProperties();
            // Read all native fields before assigning the measurement's timestamp.
            return new RawTimeline
            {
                Position = timeline.Position.TotalSeconds - timeline.StartTime.TotalSeconds,
                Duration = timeline.EndTime.TotalSeconds - timeline.StartTime.TotalSeconds,
                UpdatedUtc = timeline.LastUpdatedTime.UtcDateTime
            };
        }

        internal static bool TryReadTimelineObservation(Func<PlaybackClock> readPlayback, Func<RawTimeline> readTimeline,
            Func<DateTime> readUtc, out TimelineObservation observation)
        {
            observation = new TimelineObservation();
            // A pause/resume or rate change can occur between native API calls. Retry once,
            // then wait for the next poll rather than combine fields from different states.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                PlaybackClock before = readPlayback();
                RawTimeline timeline = readTimeline();
                PlaybackClock after = readPlayback();
                observation = new TimelineObservation { Timeline = timeline, Playback = after, CapturedUtc = readUtc() };
                if (before.Status == after.Status && Math.Abs(before.Rate - after.Rate) < 0.000001) return true;
            }
            return false;
        }

        internal void ApplyTimeline(MusicSnapshot snapshot, string source, double rawPosition, double duration, DateTime updatedUtc, double rate)
        {
            DateTime now = snapshot.TimestampUtc;
            string track = snapshot.TrackKey + "|" + snapshot.Album;
            TimelineAnchor previous;
            _anchors.TryGetValue(source, out previous);
            bool sameSample = previous != null && Math.Abs(previous.RawPosition - rawPosition) < 0.001 &&
                previous.UpdatedUtc == updatedUtc && Math.Abs(previous.Duration - duration) < 0.001;
            bool changedTrack = previous != null && previous.Track != track;
            bool staleNewTrack = (changedTrack || (previous != null && previous.WaitingForTrackTimeline)) && sameSample;
            bool compatible = previous != null && previous.Track == track && previous.Valid;
            bool rawChanged = previous != null && Math.Abs(previous.RawPosition - rawPosition) >= 0.001;
            bool observationClock = compatible && previous.UsesObservationClock;
            // Some publishers advance Position while retaining an old LastUpdatedTime.
            // Their Position is already current; ageing every new value advances twice.
            if (compatible && rawChanged && (updatedUtc <= previous.UpdatedUtc ||
                (now - updatedUtc).TotalSeconds > MaximumTrustedTimelineAgeSeconds)) observationClock = true;
            bool valid = !double.IsNaN(rawPosition) && !double.IsInfinity(rawPosition) &&
                !double.IsNaN(duration) && !double.IsInfinity(duration) && duration > 0 &&
                updatedUtc.Year >= 2000 && updatedUtc <= now.AddSeconds(5) && !staleNewTrack;
            double position = Math.Max(0, rawPosition);
            if (valid)
            {
                if (compatible && sameSample && (snapshot.IsPlaying || !observationClock))
                {
                    // Windows commonly returns the same timeline until the next seek or pause.
                    // Keep the existing clock through repeated reads and playback state changes.
                    position = previous.PositionAt(now);
                }
                else if (snapshot.IsPlaying && !observationClock)
                {
                    double age = Math.Max(0, (now - updatedUtc).TotalSeconds);
                    bool clockTransition = compatible && (!previous.Playing || Math.Abs(previous.Rate - rate) > 0.000001);
                    // A timestamp from before the most recent paused observation cannot
                    // measure time played after resume. Keep the native position instead.
                    if (clockTransition && updatedUtc < previous.TimestampUtc) age = 0;
                    if (age <= MaximumTrustedTimelineAgeSeconds) position += age * rate;
                }
                position = Math.Min(duration, Math.Max(0, position));
            }
            snapshot.HasTimeline = valid;
            snapshot.PositionSeconds = valid ? position : 0;
            snapshot.DurationSeconds = valid ? duration : 0;
            _anchors[source] = new TimelineAnchor
            {
                Track = track, RawPosition = rawPosition, Duration = duration, UpdatedUtc = updatedUtc,
                Position = snapshot.PositionSeconds, TimestampUtc = now, Playing = snapshot.IsPlaying,
                Rate = rate, Valid = valid, WaitingForTrackTimeline = staleNewTrack, UsesObservationClock = observationClock
            };
            if (_anchors.Count > 64) _anchors.Clear();
        }

        internal static MusicPlayer IdentifyPlayer(string source)
        {
            string value = (source ?? "").ToLowerInvariant();
            if (value.Contains("qqmusic") || value.Contains("qq.music")) return MusicPlayer.QQMusic;
            if (value.Contains("cloudmusic") || value.Contains("neteasemusic")) return MusicPlayer.NetEase;
            return MusicPlayer.None;
        }

        private MusicSnapshot Waiting(bool qqEnabled, bool neteaseEnabled, string error)
        {
            var detected = new List<string>();
            if (qqEnabled && IsPlayerRunning(MusicPlayer.QQMusic)) detected.Add("QQ 音乐");
            if (neteaseEnabled && IsPlayerRunning(MusicPlayer.NetEase)) detected.Add("网易云音乐");
            string status = detected.Count > 0 ? "已检测到 " + string.Join("、", detected.ToArray()) + "；等待媒体信息，请播放歌曲并开启系统媒体控制" :
                "请启动 " + (qqEnabled && neteaseEnabled ? "QQ 音乐或网易云音乐" : qqEnabled ? "QQ 音乐" : "网易云音乐") + "并播放歌曲";
            if (detected.Contains("网易云音乐")) status += "。网易云：在「设置→系统」开启 SMTC 后继续播放";
            if (!string.IsNullOrEmpty(error)) status += "。Windows 媒体会话：" + error;
            return Empty(status);
        }

        private static async Task<T> WaitOperation<T>(IAsyncOperation<T> operation, int timeoutMilliseconds, CancellationToken token)
        {
            var info = (IAsyncInfo)operation;
            var clock = Stopwatch.StartNew();
            try
            {
                while (info.Status == AsyncStatus.Started)
                {
                    token.ThrowIfCancellationRequested();
                    if (clock.ElapsedMilliseconds >= timeoutMilliseconds)
                    {
                        try { info.Cancel(); } catch { }
                        throw new TimeoutException("读取 Windows 媒体信息超时");
                    }
                    await Task.Delay(20, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                // GetResults preserves the original Windows failure instead of inventing data.
                return operation.GetResults();
            }
            catch (OperationCanceledException)
            {
                try { info.Cancel(); } catch { }
                throw;
            }
            finally
            {
                try { if (info.Status != AsyncStatus.Started) info.Close(); } catch { }
            }
        }

        private static bool Enabled(MusicPlayer player, bool qqEnabled, bool neteaseEnabled)
        {
            return (player == MusicPlayer.QQMusic && qqEnabled) || (player == MusicPlayer.NetEase && neteaseEnabled);
        }
        private static string Clean(string value) { return (value ?? "").Trim(); }
        private static MusicSnapshot Empty(string status) { return new MusicSnapshot { Status = status }; }
        private static string ShortMessage(Exception ex)
        {
            string message = (ex.Message ?? ex.GetType().Name).Replace("\r", " ").Replace("\n", " ").Trim();
            while (message.Contains("  ")) message = message.Replace("  ", " ");
            return message.Length > 160 ? message.Substring(0, 160) : message;
        }
        public void Dispose()
        {
            _disposed = true;
            _lifetime.Cancel();
            _netease.Dispose();
        }

        private sealed class SessionCandidate
        {
            public GlobalSystemMediaTransportControlsSession Session;
            public string Source;
            public MusicPlayer Player;
            public bool Playing;
            public double Rate;
            public int Score;
        }
        internal struct PlaybackClock
        {
            public GlobalSystemMediaTransportControlsSessionPlaybackStatus Status;
            public double Rate;
            public bool IsPlaying { get { return Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && Rate > 0; } }
        }
        internal struct RawTimeline
        {
            public double Position, Duration;
            public DateTime UpdatedUtc;
        }
        internal struct TimelineObservation
        {
            public RawTimeline Timeline;
            public PlaybackClock Playback;
            public DateTime CapturedUtc;
        }
        private sealed class TimelineAnchor
        {
            public string Track;
            public double RawPosition, Duration, Position, Rate;
            public DateTime UpdatedUtc, TimestampUtc;
            public bool Playing, Valid, WaitingForTrackTimeline, UsesObservationClock;
            public double PositionAt(DateTime now)
            {
                return Math.Min(Duration, Position + (Playing ? Math.Max(0, (now - TimestampUtc).TotalSeconds) * Rate : 0));
            }
        }
    }
}
