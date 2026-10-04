using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;

namespace MusicBar
{
    /// <summary>
    /// Reads public QQ Music / NetEase search and lyric endpoints without a login.
    /// Only positive lyric responses are cached. API restrictions and network errors
    /// propagate to the caller; empty documents mean the platform has no lyrics.
    /// Automatic matching returns null when inconclusive; no lyrics are generated.
    /// </summary>
    public sealed class LyricRepository : IDisposable
    {
        private const int MaximumResponseBytes = 2 * 1024 * 1024;
        private readonly HttpClient client;
        private readonly string cacheDirectory;
        private readonly string matchDirectory;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly bool completeTranslations;
        private volatile bool disposed;
        private static readonly Regex HighlightMarkup = new Regex(
            @"</?(?:em|b|strong|font|span)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ArtistSeparator = new Regex(
            @"\s*(?:/|&|、|,|，|;|；|\b(?:feat|ft|featuring)\b\.?|\band\b)\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public LyricRepository(string dataDirectory)
            : this(dataDirectory, new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }, true) { }

        internal LyricRepository(string dataDirectory, HttpMessageHandler handler)
            : this(dataDirectory, handler, false) { }

        internal LyricRepository(string dataDirectory, HttpMessageHandler handler, bool backgroundTranslations)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("缺少歌词缓存目录。", "dataDirectory");
            cacheDirectory = Path.GetFullPath(Path.Combine(dataDirectory, "lyric-cache-v1"));
            matchDirectory = Path.GetFullPath(Path.Combine(dataDirectory, "lyric-match-v1"));
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            client = new HttpClient(handler, true);
            client.Timeout = TimeSpan.FromSeconds(18);
            client.MaxResponseContentBufferSize = MaximumResponseBytes;
            completeTranslations = backgroundTranslations;
        }

        public async Task<List<LyricSearchResult>> SearchAsync(MusicPlayer player, string query, CancellationToken token)
        {
            CheckDisposed();
            token.ThrowIfCancellationRequested();
            if (player != MusicPlayer.QQMusic && player != MusicPlayer.NetEase)
                throw new ArgumentException("请选择 QQ 音乐或网易云音乐。", "player");
            query = (query ?? "").Trim();
            if (query.Length == 0) return new List<LyricSearchResult>();
            if (query.Length > 200) throw new ArgumentException("搜索文字过长。", "query");
            List<LyricSearchResult> songs;
            if (player == MusicPlayer.QQMusic)
            {
                songs = await SearchQQAsync(query, token).ConfigureAwait(false);
            }
            else
            {
                string url = "https://music.163.com/api/search/get/web?s=" + Uri.EscapeDataString(query) +
                    "&type=1&limit=30&offset=0";
                IDictionary<string, object> root = await GetJsonAsync(player, url, token).ConfigureAwait(false);
                RequireCode(root, 200, player);
                songs = ParseNetEaseSongs(Child(root, "result"));
            }
            List<LyricSearchResult> unique = new List<LyricSearchResult>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (LyricSearchResult song in songs)
                if (!string.IsNullOrWhiteSpace(song.Id) && !string.IsNullOrWhiteSpace(song.Title) && seen.Add(song.Id))
                    unique.Add(song);
            return unique;
        }

        private async Task<List<LyricSearchResult>> SearchQQAsync(string query, CancellationToken token)
        {
            // The older c.y.qq.com search can fail with HTTP 500. Desktop
            // comm.ct=24/cv=0 also silently returns empty successful responses.
            // This public web client version currently returns full song records.
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "comm", new Dictionary<string, object> { { "ct", 19 }, { "cv", 1859 }, { "uin", "0" } } },
                { "request", new Dictionary<string, object>
                {
                    { "module", "music.search.SearchCgiService" },
                    { "method", "DoSearchForQQMusicDesktop" },
                    { "param", new Dictionary<string, object>
                        { { "search_type", 0 }, { "query", query }, { "num_per_page", 30 }, { "page_num", 1 } }
                    }
                } }
            };
            string data = new JavaScriptSerializer().Serialize(payload);
            string modernUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg?format=json&data=" + Uri.EscapeDataString(data);
            bool desktopReturnedEmpty = false;
            try
            {
                IDictionary<string, object> modern = await GetJsonAsync(MusicPlayer.QQMusic, modernUrl, token).ConfigureAwait(false);
                RequireCode(modern, 0, MusicPlayer.QQMusic);
                IDictionary<string, object> request = Child(modern, "request");
                RequireCode(request, 0, MusicPlayer.QQMusic);
                IDictionary<string, object> body = Child(Child(request, "data"), "body");
                IDictionary<string, object> song = Child(body, "song");
                if (song == null) throw new InvalidOperationException("QQ 音乐搜索接口暂不可用，请稍后重试或导入本地 LRC。");
                List<LyricSearchResult> desktopSongs = ParseQQSongs(song, "list");
                if (desktopSongs.Count > 0) return desktopSongs;
                desktopReturnedEmpty = true;
            }
            catch (InvalidOperationException)
            {
                token.ThrowIfCancellationRequested();
            }
            string legacyUrl = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&new_json=1&p=1&n=30&w=" +
                Uri.EscapeDataString(query);
            try
            {
                IDictionary<string, object> root = await GetJsonAsync(MusicPlayer.QQMusic, legacyUrl, token).ConfigureAwait(false);
                RequireCode(root, 0, MusicPlayer.QQMusic);
                IDictionary<string, object> songs = Child(Child(root, "data"), "song");
                if (songs == null) throw new InvalidOperationException("QQ 音乐搜索返回的数据格式已变化。");
                return ParseQQSongs(songs, "list");
            }
            catch (InvalidOperationException)
            {
                if (desktopReturnedEmpty) return new List<LyricSearchResult>();
                throw;
            }
        }

        public async Task<LyricDocument> FetchAsync(LyricSearchResult song, CancellationToken token)
        {
            CheckDisposed();
            token.ThrowIfCancellationRequested();
            ValidateSong(song);
            string cachedLrc;
            bool translationRecentlyChecked;
            LyricDocument cached = TryReadCache(song, out cachedLrc, out translationRecentlyChecked);
            if (cached != null)
            {
                if (!translationRecentlyChecked) StartTranslationCompletion(song, cached, cachedLrc, token);
                return cached;
            }
            string lrc = "", translation = "";
            string translationError = "";
            string source = MusicSnapshot.PlayerName(song.Player);
            if (song.Player == MusicPlayer.QQMusic)
            {
                string url = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid=" +
                    Uri.EscapeDataString(song.Id) +
                    "&g_tk=5381&loginUin=0&hostUin=0&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";
                IDictionary<string, object> root = await GetJsonAsync(song.Player, url, token).ConfigureAwait(false);
                RequireCode(root, 0, song.Player);
                if (root.ContainsKey("retcode") && Number(root, "retcode", 0) != 0)
                    throw new InvalidOperationException("QQ 音乐暂未开放这首歌的歌词，请稍后重试或导入本地 LRC。");
                lrc = DecodeQQ(StringValue(root, "lyric"));
                try { translation = DecodeQQ(StringValue(root, "trans")); }
                catch (InvalidOperationException) { translationError = "译文编码暂时无法读取，原文可正常显示"; }
            }
            else
            {
                string url = "https://music.163.com/api/song/lyric?id=" + Uri.EscapeDataString(song.Id) + "&lv=-1&kv=-1&tv=-1";
                IDictionary<string, object> root = await GetJsonAsync(song.Player, url, token).ConfigureAwait(false);
                RequireCode(root, 200, song.Player);
                if (Boolean(root, "nolyric")) source += " · 纯音乐";
                else if (Boolean(root, "uncollected")) source += " · 平台暂未收录歌词";
                lrc = StringValue(Child(root, "lrc"), "lyric");
                translation = StringValue(Child(root, "tlyric"), "lyric");
                if (string.IsNullOrWhiteSpace(translation)) translation = StringValue(Child(root, "ytlrc"), "lyric");
            }
            token.ThrowIfCancellationRequested();
            LyricDocument document = CreateDocument(lrc, translation, source);
            if (translationError.Length > 0) document.TranslationStatus = translationError;
            if (!HasUsefulLyrics(document) && source == MusicSnapshot.PlayerName(song.Player))
                document.Source += " · 平台暂未提供歌词";
            if (HasUsefulLyrics(document)) TryWriteCache(song, lrc, document, document.HasTranslation);
            StartTranslationCompletion(song, document, lrc, token);
            return document;
        }

        private void StartTranslationCompletion(LyricSearchResult song, LyricDocument document, string rawLrc, CancellationToken token)
        {
            if (!completeTranslations || disposed || token.IsCancellationRequested ||
                song.Player != MusicPlayer.QQMusic || !document.HasTimedLyrics || !HasUsefulLyrics(document) || document.HasTranslation) return;
            document.TranslationStatus = "正在获取平台译文，原文已显示";
            // A modern QQ lyric request is necessary for trans=1: the older
            // endpoint often returns an empty trans field for a translated song.
            // Run it after returning the original document so playback stays live.
            Task.Run(async delegate
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
                {
                    linked.CancelAfter(TimeSpan.FromSeconds(12));
                    try
                    {
                        Dictionary<string, object> payload = new Dictionary<string, object>
                        {
                            { "comm", new Dictionary<string, object> { { "ct", 19 }, { "cv", 1859 }, { "uin", "0" } } },
                            { "lyrics", new Dictionary<string, object>
                            {
                                { "module", "music.musichallSong.PlayLyricInfo" },
                                { "method", "GetPlayLyricInfo" },
                                { "param", new Dictionary<string, object>
                                    { { "songMID", song.Id }, { "songID", 0 }, { "trans", 1 }, { "roma", 0 }, { "qrc", 0 }, { "crypt", 0 }, { "type", 0 } }
                                }
                            } }
                        };
                        string data = new JavaScriptSerializer().Serialize(payload);
                        string url = "https://u.y.qq.com/cgi-bin/musicu.fcg?format=json&data=" + Uri.EscapeDataString(data);
                        IDictionary<string, object> response = await GetJsonAsync(MusicPlayer.QQMusic, url, linked.Token).ConfigureAwait(false);
                        RequireCode(response, 0, MusicPlayer.QQMusic);
                        IDictionary<string, object> lyrics = Child(response, "lyrics");
                        RequireCode(lyrics, 0, MusicPlayer.QQMusic);
                        IDictionary<string, object> body = Child(lyrics, "data");
                        if (body == null) throw new InvalidOperationException("QQ 音乐译文接口的数据格式已变化。");
                        string translation = DecodeQQ(StringValue(body, "trans"));
                        LyricDocument prepared = CreateDocument(rawLrc, translation, document.Source);
                        prepared.TranslationSource = string.IsNullOrWhiteSpace(translation) ? "" : "QQ 音乐 · 平台译文";
                        linked.Token.ThrowIfCancellationRequested();
                        // Prepare an independent mapping even when the target
                        // already contains correctly sized blank translation rows.
                        // The controller observes the raw field, so publish it last.
                        document.TranslationLines = prepared.TranslationLines;
                        document.TranslationSource = prepared.TranslationSource;
                        document.TranslationStatus = prepared.TranslationStatus;
                        Volatile.Write(ref document.Translation, prepared.Translation);
                        TryWriteCache(song, rawLrc, prepared, true);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!disposed && !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
                            document.TranslationStatus = "获取译文超时，原文可正常显示";
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException)
                    {
                        if (!disposed && !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
                            document.TranslationStatus = "平台译文暂时无法读取，原文可正常显示";
                    }
                }
            });
        }

        public async Task<LyricDocument> FindAsync(MusicSnapshot song, CancellationToken token)
        {
            CheckDisposed();
            token.ThrowIfCancellationRequested();
            if (song == null || !song.HasTrack) return null;
            if (string.IsNullOrWhiteSpace(song.Artist) &&
                (string.IsNullOrWhiteSpace(song.Album) || song.DurationSeconds <= 0))
                return null;
            LyricDocument matchedCache = TryReadMatchedCache(song, token);
            if (matchedCache != null) return matchedCache;
            string query = song.Title + (string.IsNullOrWhiteSpace(song.Artist) ? "" : " " + song.Artist);
            LyricDocument primaryDocument = null;
            InvalidOperationException primaryFailure = null;
            try
            {
                List<LyricSearchResult> results = await SearchAsync(song.Player, query, token).ConfigureAwait(false);
                LyricSearchResult chosen = SelectAutomaticMatch(song, results);
                if (chosen != null) primaryDocument = await FetchAsync(chosen, token).ConfigureAwait(false);
                if (primaryDocument != null && primaryDocument.HasTimedLyrics)
                {
                    TryWriteMatch(song, chosen);
                    return primaryDocument;
                }
            }
            catch (InvalidOperationException error) { primaryFailure = error; }

            // A QQ song can use NetEase's public lyric catalog when the full
            // title/artist, album and observed duration identify the same recording.
            // Keep version words (including Explicit, Live and DJ); no first-result
            // selection or relaxed title match is allowed across providers.
            if (song.Player == MusicPlayer.QQMusic && !string.IsNullOrWhiteSpace(song.Artist) &&
                !string.IsNullOrWhiteSpace(song.Album) && song.DurationSeconds > 0)
            {
                try
                {
                    MusicSnapshot fallbackTrack = new MusicSnapshot
                    {
                        Player = MusicPlayer.NetEase, Title = song.Title, Artist = song.Artist,
                        Album = song.Album, DurationSeconds = song.DurationSeconds
                    };
                    List<LyricSearchResult> alternative = await SearchAsync(MusicPlayer.NetEase, query, token).ConfigureAwait(false);
                    string exactAlbum = Normalize(song.Album);
                    alternative = alternative.FindAll(delegate(LyricSearchResult candidate)
                    {
                        return Normalize(candidate.Album) == exactAlbum;
                    });
                    LyricSearchResult chosen = SelectAutomaticMatch(fallbackTrack, alternative);
                    if (chosen != null)
                    {
                        LyricDocument document = await FetchAsync(chosen, token).ConfigureAwait(false);
                        if (document.HasTimedLyrics)
                        {
                            document.Source += " · QQ 音乐备用歌词";
                            TryWriteMatch(song, chosen);
                            return document;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // A failed alternative must not replace valid plain-text
                    // primary lyrics or change an inconclusive identity match.
                    token.ThrowIfCancellationRequested();
                }
            }
            if (primaryDocument != null) return primaryDocument;
            if (primaryFailure != null) throw primaryFailure;
            return null;
        }

        // Version words remain significant: Live, remix and cover names must match.
        // Exact artist identity is preferred. QQ's media session can publish only
        // the first artist; that case needs the exact album, duration and one candidate.
        // Search order never decides an automatic match.
        internal static LyricSearchResult SelectAutomaticMatch(MusicSnapshot song, IEnumerable<LyricSearchResult> results)
        {
            if (song == null || results == null || string.IsNullOrWhiteSpace(song.Title)) return null;
            string title = Normalize(song.Title), artist = ArtistSignature(song.Artist), album = Normalize(song.Album);
            bool haveArtist = artist.Length > 0, haveDuration = song.DurationSeconds > 0;
            if (!haveArtist && (!haveDuration || album.Length == 0)) return null;
            List<LyricSearchResult> matching = new List<LyricSearchResult>();
            List<LyricSearchResult> leadingArtistMatches = new List<LyricSearchResult>();
            bool allowLeadingArtist = song.Player == MusicPlayer.QQMusic && haveArtist && artist.IndexOf('|') < 0
                && album.Length > 0 && haveDuration && !double.IsInfinity(song.DurationSeconds);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (LyricSearchResult candidate in results)
            {
                if (candidate == null || candidate.Player != song.Player || string.IsNullOrWhiteSpace(candidate.Id) ||
                    Normalize(candidate.Title) != title || !seen.Add(candidate.Id)) continue;
                if (!haveArtist && Normalize(candidate.Album) != album) continue;
                if (haveDuration)
                {
                    // Durations may be rounded to whole seconds by QQ or the media session.
                    double tolerance = Math.Min(4.0, Math.Max(2.0, song.DurationSeconds * 0.01));
                    if (candidate.DurationSeconds <= 0 || Math.Abs(candidate.DurationSeconds - song.DurationSeconds) > tolerance) continue;
                }
                if (haveArtist && ArtistSignature(candidate.Artist) != artist)
                {
                    if (allowLeadingArtist && Normalize(candidate.Album) == album)
                    {
                        string[] artists = ArtistSeparator.Split(Clean(candidate.Artist));
                        if (artists.Length > 1 && Normalize(artists[0]) == artist) leadingArtistMatches.Add(candidate);
                    }
                    continue;
                }
                matching.Add(candidate);
            }
            if (matching.Count == 0) return leadingArtistMatches.Count == 1 ? leadingArtistMatches[0] : null;
            if (album.Length > 0)
            {
                List<LyricSearchResult> sameAlbum = matching.FindAll(delegate(LyricSearchResult candidate) { return Normalize(candidate.Album) == album; });
                if (sameAlbum.Count > 0) matching = sameAlbum;
            }
            if (matching.Count == 1) return matching[0];
            if (haveDuration)
            {
                matching.Sort(delegate(LyricSearchResult left, LyricSearchResult right)
                {
                    return Math.Abs(left.DurationSeconds - song.DurationSeconds).CompareTo(Math.Abs(right.DurationSeconds - song.DurationSeconds));
                });
                double best = Math.Abs(matching[0].DurationSeconds - song.DurationSeconds);
                double next = Math.Abs(matching[1].DurationSeconds - song.DurationSeconds);
                if (next - best >= 1.5) return matching[0];
            }
            return null;
        }

        private async Task<IDictionary<string, object>> GetJsonAsync(MusicPlayer player, string url, CancellationToken token)
        {
            try
            {
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                    request.Headers.Referrer = new Uri(player == MusicPlayer.QQMusic ? "https://y.qq.com/" : "https://music.163.com/");
                    request.Headers.Accept.ParseAdd("application/json");
                    using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException(MusicSnapshot.PlayerName(player) + "接口返回 HTTP " + (int)response.StatusCode + "，请稍后重试。");
                        string body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).TrimStart('\uFEFF').Trim();
                        token.ThrowIfCancellationRequested();
                        // Some QQ web endpoints still wrap JSON in a JSONP callback.
                        if (!body.StartsWith("{", StringComparison.Ordinal))
                        {
                            int start = body.IndexOf('{'), end = body.LastIndexOf('}');
                            if (start >= 0 && end >= start) body = body.Substring(start, end - start + 1);
                        }
                        JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaximumResponseBytes, RecursionLimit = 64 };
                        IDictionary<string, object> root = serializer.DeserializeObject(body) as IDictionary<string, object>;
                        if (root == null) throw new InvalidOperationException(MusicSnapshot.PlayerName(player) + "返回了无法识别的数据。");
                        return root;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                throw new InvalidOperationException(MusicSnapshot.PlayerName(player) + "请求超时，请稍后重试。");
            }
            catch (HttpRequestException error)
            {
                throw new InvalidOperationException(MusicSnapshot.PlayerName(player) + "连接失败，请检查网络后重试。", error);
            }
            catch (ArgumentException error)
            {
                throw new InvalidOperationException(MusicSnapshot.PlayerName(player) + "歌词接口的数据格式已变化，请稍后重试或导入本地 LRC。", error);
            }
        }

        private static List<LyricSearchResult> ParseQQSongs(IDictionary<string, object> container, string key)
        {
            List<LyricSearchResult> results = new List<LyricSearchResult>();
            foreach (IDictionary<string, object> item in Children(container, key))
            {
                // QQ's name often omits meaningful Live/DJ/Explicit labels that
                // are present in title and in the Windows media session title.
                string title = StringValue(item, "title");
                if (title.Length == 0) title = StringValue(item, "name");
                if (title.Length == 0) title = StringValue(item, "songname");
                string id = StringValue(item, "mid");
                if (id.Length == 0) id = StringValue(item, "songmid");
                IDictionary<string, object> album = Child(item, "album");
                string albumTitle = StringValue(album, "name");
                if (albumTitle.Length == 0) albumTitle = StringValue(item, "albumname");
                results.Add(new LyricSearchResult
                {
                    Player = MusicPlayer.QQMusic, Id = id, Title = Clean(title),
                    Artist = JoinArtists(item, "singer"), Album = Clean(albumTitle),
                    DurationSeconds = Math.Max(0, Number(item, "interval", 0))
                });
            }
            return results;
        }

        private static List<LyricSearchResult> ParseNetEaseSongs(IDictionary<string, object> container)
        {
            List<LyricSearchResult> results = new List<LyricSearchResult>();
            if (container == null) throw new InvalidOperationException("网易云音乐搜索返回的数据格式已变化。");
            foreach (IDictionary<string, object> item in Children(container, "songs"))
            {
                IDictionary<string, object> album = Child(item, "album") ?? Child(item, "al");
                string artist = JoinArtists(item, "artists");
                if (artist.Length == 0) artist = JoinArtists(item, "ar");
                results.Add(new LyricSearchResult
                {
                    Player = MusicPlayer.NetEase, Id = StringValue(item, "id"), Title = Clean(StringValue(item, "name")),
                    Artist = artist, Album = Clean(StringValue(album, "name")),
                    DurationSeconds = Math.Max(0, Number(item, "duration", Number(item, "dt", 0)) / 1000.0)
                });
            }
            return results;
        }

        private static string JoinArtists(IDictionary<string, object> song, string key)
        {
            List<string> artists = new List<string>();
            foreach (IDictionary<string, object> artist in Children(song, key))
            {
                string name = Clean(StringValue(artist, "name"));
                if (name.Length > 0) artists.Add(name);
            }
            return string.Join(" / ", artists);
        }

        private static IEnumerable<IDictionary<string, object>> Children(IDictionary<string, object> value, string key)
        {
            object found;
            if (value == null || !value.TryGetValue(key, out found)) yield break;
            IEnumerable list = found as IEnumerable;
            if (list == null || found is string) yield break;
            foreach (object item in list)
            {
                IDictionary<string, object> child = item as IDictionary<string, object>;
                if (child != null) yield return child;
            }
        }
        private static IDictionary<string, object> Child(IDictionary<string, object> value, string key)
        {
            object found;
            return value != null && value.TryGetValue(key, out found) ? found as IDictionary<string, object> : null;
        }
        private static string StringValue(IDictionary<string, object> value, string key)
        {
            object found;
            return value != null && value.TryGetValue(key, out found) && found != null ? Convert.ToString(found, CultureInfo.InvariantCulture) : "";
        }
        private static double Number(IDictionary<string, object> value, string key, double fallback)
        {
            double parsed;
            return double.TryParse(StringValue(value, key), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
                !double.IsInfinity(parsed) && !double.IsNaN(parsed) ? parsed : fallback;
        }
        private static bool Boolean(IDictionary<string, object> value, string key)
        {
            bool result;
            return bool.TryParse(StringValue(value, key), out result) && result;
        }
        private static void RequireCode(IDictionary<string, object> value, int success, MusicPlayer player)
        {
            if (value == null || !value.ContainsKey("code"))
                throw new InvalidOperationException(MusicSnapshot.PlayerName(player) + "返回了无法识别的数据。");
            double code = Number(value, "code", -1);
            if (code != success)
                throw new InvalidOperationException(MusicSnapshot.PlayerName(player) + "接口暂不可用（代码 " + code.ToString(CultureInfo.InvariantCulture) + "），请稍后重试或导入本地 LRC。");
        }
        private static string DecodeQQ(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string decoded = WebUtility.HtmlDecode(value).TrimStart('\uFEFF');
            if (decoded.TrimStart().StartsWith("[", StringComparison.Ordinal)) return value.TrimStart('\uFEFF');
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(decoded)); }
            catch (FormatException error) { throw new InvalidOperationException("QQ 音乐返回的歌词编码无法识别，请稍后重试或导入本地 LRC。", error); }
        }
        private static string Clean(string text)
        { return WebUtility.HtmlDecode(HighlightMarkup.Replace(text ?? "", "")).Trim(); }
        private static string Normalize(string text)
        {
            string value = Clean(text).Normalize(NormalizationForm.FormKC).ToUpperInvariant();
            value = value.Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201C', '"').Replace('\u201D', '"');
            value = value.Replace('\u2013', '-').Replace('\u2014', '-');
            value = Regex.Replace(value, @"\s+", " ").Trim();
            // Keep significant punctuation: "C++" must not match "C", and
            // "A B" must not match "AB". Only bracket spacing is cosmetic.
            return Regex.Replace(value, @"\s*([\(\)\[\]\{\}])\s*", "$1");
        }
        private static string ArtistSignature(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            List<string> artists = new List<string>();
            foreach (string piece in ArtistSeparator.Split(Clean(text)))
            {
                string artist = Normalize(piece);
                if (artist.Length > 0 && !artists.Contains(artist)) artists.Add(artist);
            }
            artists.Sort(StringComparer.Ordinal);
            return string.Join("|", artists);
        }
        private static void ValidateSong(LyricSearchResult song)
        {
            if (song == null) throw new ArgumentNullException("song");
            if (song.Player == MusicPlayer.QQMusic)
            {
                if (!Regex.IsMatch(song.Id ?? "", @"\A[A-Za-z0-9]{1,64}\z"))
                    throw new ArgumentException("QQ 音乐歌曲编号无效。", "song");
            }
            else if (song.Player == MusicPlayer.NetEase)
            {
                if (!Regex.IsMatch(song.Id ?? "", @"\A[0-9]{1,20}\z"))
                    throw new ArgumentException("网易云音乐歌曲编号无效。", "song");
            }
            else throw new ArgumentException("请选择 QQ 音乐或网易云音乐。", "song");
        }

        private static LyricDocument CreateDocument(string lrc, string translation, string source)
        {
            LyricDocument document = LrcParser.Parse(lrc);
            document.Source = source;
            // Keep translated LRC timestamps so the caller can sync a second language.
            document.Translation = translation ?? "";
            if (!string.IsNullOrWhiteSpace(translation)) document.TranslationSource = source;
            LrcParser.EnsureTranslation(document);
            return document;
        }
        private static bool HasUsefulLyrics(LyricDocument document)
        { return !string.IsNullOrWhiteSpace(document.PlainText); }

        private static bool BindingIdentityMatches(MusicSnapshot observed, LyricSearchResult chosen)
        {
            // Bindings are created only for the full recording identity. They
            // never infer a match by scanning old lyric-cache titles alone.
            if (observed == null || chosen == null || string.IsNullOrWhiteSpace(observed.Artist) ||
                string.IsNullOrWhiteSpace(observed.Album) || observed.DurationSeconds <= 0 ||
                double.IsNaN(observed.DurationSeconds) || double.IsInfinity(observed.DurationSeconds) ||
                Normalize(observed.Album) != Normalize(chosen.Album)) return false;
            if (chosen.Player != observed.Player && !(observed.Player == MusicPlayer.QQMusic && chosen.Player == MusicPlayer.NetEase)) return false;
            MusicSnapshot recording = new MusicSnapshot
            {
                Player = chosen.Player, Title = observed.Title, Artist = observed.Artist,
                Album = observed.Album, DurationSeconds = observed.DurationSeconds
            };
            return SelectAutomaticMatch(recording, new[] { chosen }) == chosen;
        }

        private string MatchPath(MusicSnapshot observed)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(observed.TrackKey));
                return Path.Combine(matchDirectory, BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant() + ".xml");
            }
        }

        private LyricDocument TryReadMatchedCache(MusicSnapshot observed, CancellationToken token)
        {
            try
            {
                FileInfo file = new FileInfo(MatchPath(observed));
                if (!file.Exists || file.Length > 65536 || DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromDays(30) ||
                    file.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(5)) return null;
                XmlDocument xml = new XmlDocument { XmlResolver = null };
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 };
                using (XmlReader reader = XmlReader.Create(file.FullName, settings)) xml.Load(reader);
                XmlElement root = xml.DocumentElement;
                if (root == null || root.Name != "match" || root.GetAttribute("version") != "1" ||
                    root.GetAttribute("trackKey") != observed.TrackKey) return null;
                double duration;
                if (!double.TryParse(root.GetAttribute("observedDuration"), NumberStyles.Float, CultureInfo.InvariantCulture, out duration) ||
                    double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0 ||
                    double.IsNaN(observed.DurationSeconds) || double.IsInfinity(observed.DurationSeconds) ||
                    Math.Abs(duration - observed.DurationSeconds) > .25) return null;
                MusicPlayer player;
                if (!Enum.TryParse(root.GetAttribute("player"), false, out player)) return null;
                double recordingDuration;
                if (!double.TryParse(root.GetAttribute("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out recordingDuration) ||
                    double.IsNaN(recordingDuration) || double.IsInfinity(recordingDuration) || recordingDuration <= 0) return null;
                LyricSearchResult chosen = new LyricSearchResult
                {
                    Player = player, Id = root.GetAttribute("id"), Title = root.GetAttribute("title"),
                    Artist = root.GetAttribute("artist"), Album = root.GetAttribute("album"), DurationSeconds = recordingDuration
                };
                ValidateSong(chosen);
                if (!BindingIdentityMatches(observed, chosen)) return null;
                string rawLrc;
                bool translationRecentlyChecked;
                LyricDocument cached = TryReadCache(chosen, out rawLrc, out translationRecentlyChecked);
                if (cached == null || !cached.HasTimedLyrics) return null;
                token.ThrowIfCancellationRequested();
                if (chosen.Player != observed.Player) cached.Source += " · QQ 音乐备用歌词";
                if (!translationRecentlyChecked) StartTranslationCompletion(chosen, cached, rawLrc, token);
                return cached;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (XmlException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private void TryWriteMatch(MusicSnapshot observed, LyricSearchResult chosen)
        {
            if (!BindingIdentityMatches(observed, chosen)) return;
            string temporary = "";
            try
            {
                Directory.CreateDirectory(matchDirectory);
                string path = MatchPath(observed);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CheckCharacters = true };
                using (XmlWriter writer = XmlWriter.Create(temporary, settings))
                {
                    writer.WriteStartElement("match");
                    writer.WriteAttributeString("version", "1");
                    writer.WriteAttributeString("trackKey", observed.TrackKey);
                    writer.WriteAttributeString("observedDuration", observed.DurationSeconds.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("player", chosen.Player.ToString());
                    writer.WriteAttributeString("id", chosen.Id);
                    writer.WriteAttributeString("title", chosen.Title);
                    writer.WriteAttributeString("artist", chosen.Artist);
                    writer.WriteAttributeString("album", chosen.Album);
                    writer.WriteAttributeString("duration", chosen.DurationSeconds.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            finally
            {
                if (temporary.Length > 0)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private string CachePath(LyricSearchResult song)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(song.Player + "|" + song.Id));
                return Path.Combine(cacheDirectory, BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant() + ".xml");
            }
        }
        private LyricDocument TryReadCache(LyricSearchResult song, out string rawLrc, out bool translationRecentlyChecked)
        {
            rawLrc = "";
            translationRecentlyChecked = false;
            try
            {
                string path = CachePath(song);
                FileInfo file = new FileInfo(path);
                if (!file.Exists || file.Length > MaximumResponseBytes * 2 || DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromDays(30)) return null;
                XmlDocument xml = new XmlDocument { XmlResolver = null };
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumResponseBytes * 2 };
                using (XmlReader reader = XmlReader.Create(path, settings)) xml.Load(reader);
                XmlElement root = xml.DocumentElement;
                if (root == null || root.Name != "lyrics" || root.GetAttribute("version") != "1" ||
                    root.GetAttribute("player") != song.Player.ToString() || root.GetAttribute("id") != song.Id) return null;
                XmlNode lrc = root.SelectSingleNode("lrc"), translation = root.SelectSingleNode("translation");
                rawLrc = lrc == null ? "" : lrc.InnerText;
                LyricDocument document = CreateDocument(lrc == null ? "" : lrc.InnerText,
                    translation == null ? "" : translation.InnerText, MusicSnapshot.PlayerName(song.Player) + " · 缓存");
                XmlNode translationSource = root.SelectSingleNode("translationSource");
                if (translationSource != null) document.TranslationSource = translationSource.InnerText;
                XmlNodeList mapping = root.SelectNodes("translationAligned/line");
                if (mapping.Count == document.Lines.Count && mapping.Count > 0 && !string.IsNullOrWhiteSpace(document.Translation))
                {
                    List<LyricLine> lines = new List<LyricLine>();
                    for (int i = 0; i < mapping.Count; i++)
                    {
                        double seconds;
                        XmlElement row = mapping[i] as XmlElement;
                        if (row == null || !double.TryParse(row.GetAttribute("seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                            double.IsNaN(seconds) || double.IsInfinity(seconds) || Math.Abs(seconds - document.Lines[i].Seconds) > .000001) { lines.Clear(); break; }
                        lines.Add(new LyricLine(seconds, mapping[i].InnerText));
                    }
                    if (lines.Count == document.Lines.Count) document.TranslationLines = lines;
                }
                LrcParser.EnsureTranslation(document);
                DateTime checkedUtc;
                translationRecentlyChecked = DateTime.TryParse(root.GetAttribute("translationCheckedUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out checkedUtc) && DateTime.UtcNow - checkedUtc.ToUniversalTime() < TimeSpan.FromDays(1);
                return HasUsefulLyrics(document) ? document : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (XmlException) { return null; }
        }
        private void TryWriteCache(LyricSearchResult song, string lrc, LyricDocument document, bool translationChecked)
        {
            string temporary = "";
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                string path = CachePath(song);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CheckCharacters = true };
                using (XmlWriter writer = XmlWriter.Create(temporary, settings))
                {
                    writer.WriteStartElement("lyrics");
                    writer.WriteAttributeString("version", "1");
                    writer.WriteAttributeString("player", song.Player.ToString());
                    writer.WriteAttributeString("id", song.Id);
                    writer.WriteAttributeString("title", song.Title ?? "");
                    writer.WriteAttributeString("artist", song.Artist ?? "");
                    if (translationChecked) writer.WriteAttributeString("translationCheckedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    writer.WriteElementString("lrc", lrc ?? "");
                    writer.WriteElementString("translation", document.Translation ?? "");
                    writer.WriteElementString("translationSource", document.TranslationSource ?? "");
                    if (document.HasTranslation)
                    {
                        writer.WriteStartElement("translationAligned");
                        foreach (LyricLine line in document.TranslationLines)
                        {
                            writer.WriteStartElement("line");
                            writer.WriteAttributeString("seconds", line.Seconds.ToString("R", CultureInfo.InvariantCulture));
                            writer.WriteString(line.Text ?? ""); writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            finally
            {
                if (temporary.Length > 0)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        private void CheckDisposed()
        { if (disposed) throw new ObjectDisposedException("LyricRepository"); }
        public void Dispose()
        { if (!disposed) { disposed = true; lifetime.Cancel(); client.Dispose(); } }
    }
}
