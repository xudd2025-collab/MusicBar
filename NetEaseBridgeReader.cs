using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MusicBar
{
    // Read the live range input and only the client's two playback records.
    // Never invoke playback controls, inspect arbitrary storage, or install code in it.
    internal sealed class NetEaseBridgeReader : IDisposable
    {
        internal const int Port = 9223;
        internal const string ReadExpression = "(async()=>{if(!window.channel||!window.channel.deData)return null;const raw=localStorage.getItem('playingInfo'),last=localStorage.getItem('lastPlaying');if(!raw||!last)return null;const p=JSON.parse(await window.channel.deData(raw)),q=JSON.parse(await window.channel.deData(last));const b=document.querySelector('[aria-label=\"播放进度调节\"]'),i=b&&b.querySelector('input[type=\"range\"]');if(!i||raw!==localStorage.getItem('playingInfo')||String(p.resourceTrackId)!==String(q.trackId))return null;const position=Number(i.value),duration=Number(i.max);if(Math.abs(duration-Number(p.resourceDuration))>.001||Math.abs(duration-Number(q.resourceDuration))>.001)return null;const state=({'2':'Playing','1':'Pause','0':'Stop','-1':'End'})[String(p.playingState)];const track=p.curTrack||p.curVoice||{};return {id:String(p.resourceTrackId||''),title:p.resourceName,artist:(p.resourceArtists||[]).map(x=>typeof x==='string'?x:x.name).join(' / '),album:track.album&&track.album.name||'',position,duration,state};})()";
        private readonly HttpClient http;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 262144 };
        private readonly NetEaseProgressClock progressClock = new NetEaseProgressClock();
        private ClientWebSocket socket;
        private int commandId;
        private DateTime retryUtc;
        private volatile bool disposed;

        internal NetEaseBridgeReader()
        {
            http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.Timeout = TimeSpan.FromMilliseconds(900);
        }

        internal async Task<MusicSnapshot> ReadAsync(CancellationToken lifetime)
        {
            if (disposed || DateTime.UtcNow < retryUtc) return null;
            try
            {
                // Refuse a port occupied by a different application.
                if (!OwnsListener(Port)) { Reset(); retryUtc = DateTime.UtcNow.AddSeconds(2); return null; }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
                {
                    timeout.CancelAfter(1400);
                    if (socket != null && socket.State == WebSocketState.Open)
                    {
                        var sample = await ReadSocketAsync(timeout.Token).ConfigureAwait(false);
                        if (sample != null) return sample;
                        Reset();
                    }
                    string response = await http.GetStringAsync("http://127.0.0.1:" + Port + "/json/list").ConfigureAwait(false);
                    object[] pages = json.DeserializeObject(response) as object[];
                    if (pages == null) return null;
                    int tried = 0;
                    foreach (object page in pages)
                    {
                        var entry = page as IDictionary<string, object>;
                        if (entry == null || Text(entry, "type") != "page" || !IsPlayerPage(Text(entry, "url"))) continue;
                        Uri uri;
                        if (!TrySocketUri(Text(entry, "webSocketDebuggerUrl"), out uri)) continue;
                        if (++tried > 3) break;
                        socket = new ClientWebSocket();
                        await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
                        var sample = await ReadSocketAsync(timeout.Token).ConfigureAwait(false);
                        if (sample != null) return sample;
                        Reset();
                    }
                }
            }
            catch (OperationCanceledException) { if (lifetime.IsCancellationRequested) throw; }
            catch { /* A player can exit, reload its page or stop its local endpoint. */ }
            Reset();
            retryUtc = DateTime.UtcNow.AddSeconds(1);
            return null;
        }

        private async Task<MusicSnapshot> ReadSocketAsync(CancellationToken token)
        {
            int id = ++commandId;
            byte[] command = Encoding.UTF8.GetBytes(json.Serialize(new { id, method = "Runtime.evaluate", @params = new { expression = ReadExpression, returnByValue = true, awaitPromise = true } }));
            var started = Stopwatch.StartNew();
            await socket.SendAsync(new ArraySegment<byte>(command), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            byte[] buffer = new byte[8192];
            while (!token.IsCancellationRequested)
            {
                var bytes = new System.IO.MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (part.MessageType != WebSocketMessageType.Text) return null;
                    bytes.Write(buffer, 0, part.Count);
                    if (bytes.Length > 262144) return null;
                } while (!part.EndOfMessage);
                var message = json.DeserializeObject(Encoding.UTF8.GetString(bytes.ToArray())) as IDictionary<string, object>;
                bytes.Dispose();
                if (message == null || Number(message, "id") != id) continue;
                // Reject delayed samples: they must never get a fresh timestamp.
                if (started.ElapsedMilliseconds > 600) return null;
                var result = Child(message, "result");
                if (result == null || result.ContainsKey("exceptionDetails")) return null;
                var remote = Child(result, "result");
                return progressClock.Observe(ParseSample(remote == null ? null : Child(remote, "value"), DateTime.UtcNow));
            }
            return null;
        }

        internal static MusicSnapshot ParseSample(IDictionary<string, object> sample, DateTime capturedUtc)
        {
            if (sample == null) return null;
            string title = Text(sample, "title").Trim(), state = Text(sample, "state"), trackId = Text(sample, "id");
            double position = Number(sample, "position"), duration = Number(sample, "duration");
            if (title.Length == 0 || !MusicSnapshot.ValidNetEaseTrackId(trackId) || !Finite(position) || !Finite(duration) ||
                position < 0 || duration <= 0 || position > duration + 1 || duration > 86400 ||
                (state != "Playing" && state != "Pause" && state != "Stop" && state != "End")) return null;
            return new MusicSnapshot
            {
                Player = MusicPlayer.NetEase, Title = title, Artist = Text(sample, "artist").Trim(), Album = Text(sample, "album").Trim(),
                PlatformTrackId = trackId,
                PositionSeconds = Math.Min(position, duration), DurationSeconds = duration,
                IsPlaying = state == "Playing", HasTimeline = true, InterpolateTimeline = false,
                TimestampUtc = capturedUtc, Status = (state == "Playing" ? "播放中" : "已暂停") + " · 网易云音乐 · 本机真实进度"
            };
        }

        internal static bool IsPlayerPage(string url)
        {
            Uri page;
            return Uri.TryCreate(url, UriKind.Absolute, out page) && page.Scheme == "orpheus" && page.Host == "orpheus" &&
                (page.AbsolutePath == "/pub/app.html" || page.AbsolutePath == "/pub/index.html" ||
                page.AbsolutePath.StartsWith("/pub/hybrid/", StringComparison.Ordinal));
        }
        internal static bool TrySocketUri(string value, out Uri uri)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "ws" &&
                (uri.Host == "127.0.0.1" || uri.Host == "localhost") && uri.Port == Port &&
                uri.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal) && uri.UserInfo.Length == 0;
        }
        private static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
        private static IDictionary<string, object> Child(IDictionary<string, object> map, string key)
        { object v; return map != null && map.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        private static string Text(IDictionary<string, object> map, string key)
        { object v; return map.TryGetValue(key, out v) && v is string ? (string)v : ""; }
        private static double Number(IDictionary<string, object> map, string key)
        {
            object v; double n;
            return map.TryGetValue(key, out v) && v != null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? n : double.NaN;
        }

        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
        internal static bool OwnsListener(int port)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
            if (size < 4 || size > 1024 * 1024) return false;
            IntPtr data = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(data, ref size, false, 2, 3, 0) != 0) return false;
                int count = Marshal.ReadInt32(data);
                if (count < 0 || count > (size - 4) / 24) return false;
                for (int i = 0; i < count; i++)
                {
                    int offset = 4 + i * 24;
                    int nativePort = Marshal.ReadInt32(data, offset + 8);
                    int localPort = ((nativePort & 255) << 8) | ((nativePort >> 8) & 255);
                    if (localPort != port || Marshal.ReadInt32(data, offset) != 2) continue;
                    using (var app = Process.GetProcessById(Marshal.ReadInt32(data, offset + 20)))
                        if (app.ProcessName.Equals("cloudmusic", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(data); }
            return false;
        }
        private void Reset() { progressClock.Reset(); var old = socket; socket = null; if (old != null) old.Dispose(); }
        public void Dispose() { disposed = true; Reset(); http.Dispose(); }
    }

    // The UI publishes roughly one native progress value per second. Smooth only
    // after observing a normal advancing pair, and cap prediction to one sample.
    // Duplicate polls retain the original anchor; no time is added cumulatively.
    internal sealed class NetEaseProgressClock
    {
        private MusicSnapshot anchor;
        private bool smoothing;
        private double limit;
        internal void Reset() { anchor = null; smoothing = false; }

        internal MusicSnapshot Observe(MusicSnapshot sample)
        {
            if (sample == null) { Reset(); return null; }
            bool compatible = anchor != null && anchor.PlaybackKey == sample.PlaybackKey &&
                Math.Abs(anchor.DurationSeconds - sample.DurationSeconds) < .001 &&
                anchor.IsPlaying && sample.IsPlaying;
            if (!compatible)
            {
                anchor = sample;
                smoothing = false;
                return sample;
            }
            if (Math.Abs(sample.PositionSeconds - anchor.PositionSeconds) > .001)
            {
                double step = sample.PositionSeconds - anchor.PositionSeconds;
                double elapsed = (sample.TimestampUtc - anchor.TimestampUtc).TotalSeconds;
                smoothing = step >= .35 && step <= 1.6 && elapsed >= .25 && elapsed <= 1.8 && Math.Abs(step - elapsed) <= .45;
                limit = Math.Min(1.2, Math.Max(.5, step + .15));
                anchor = sample;
            }
            if (smoothing)
            {
                sample.TimestampUtc = anchor.TimestampUtc;
                sample.InterpolateTimeline = true;
                sample.MaximumInterpolationSeconds = limit;
            }
            return sample;
        }
    }
}
