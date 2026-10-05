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
            bool desktopRequiresLogin = false;
            try
            {
                IDictionary<string, object> modern = await GetJsonAsync(MusicPlayer.QQMusic, modernUrl, token).ConfigureAwait(false);
                RequireCode(modern, 0, MusicPlayer.QQMusic);
                IDictionary<string, object> request = Child(modern, "request");
                desktopRequiresLogin = Number(request, "code", -1) == 2001;
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
                // Respect the service's access restriction and promptly allow the
                // caller to try its strictly matched alternate lyric provider.
                if (desktopRequiresLogin) throw;
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

        public Task<LyricDocument> FetchAsync(LyricSearchResult song, CancellationToken token)
        { return FetchAsync(song, token, token); }

        private async Task<LyricDocument> FetchAsync(LyricSearchResult song, CancellationToken token, CancellationToken translationToken)
        {
            CheckDisposed();
            token.ThrowIfCancellationRequested();
            ValidateSong(song);
            string cachedLrc;
            bool translationRecentlyChecked;
            LyricDocument cached = TryReadCache(song, out cachedLrc, out translationRecentlyChecked);
            if (cached != null)
            {
                if (!translationRecentlyChecked || !cached.WordTimingChecked) StartTranslationCompletion(song, cached, cachedLrc, translationToken);
                return cached;
            }
            string lrc = "", translation = "", wordTiming = "", translatedWordTiming = "";
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
                string url = "https://music.163.com/api/song/lyric?id=" + Uri.EscapeDataString(song.Id) + "&lv=-1&kv=-1&tv=-1&yv=-1&ytv=-1";
                IDictionary<string, object> root = await GetJsonAsync(song.Player, url, token).ConfigureAwait(false);
                RequireCode(root, 200, song.Player);
                if (Boolean(root, "nolyric")) source += " · 纯音乐";
                else if (Boolean(root, "uncollected")) source += " · 平台暂未收录歌词";
                lrc = StringValue(Child(root, "lrc"), "lyric");
                translation = StringValue(Child(root, "tlyric"), "lyric");
                if (string.IsNullOrWhiteSpace(translation)) translation = StringValue(Child(root, "ytlrc"), "lyric");
                wordTiming = StringValue(Child(root, "yrc"), "lyric");
                if (wordTiming.Length == 0) wordTiming = StringValue(Child(root, "klyric"), "lyric");
                translatedWordTiming = StringValue(Child(root, "ytlrc"), "lyric");
            }
            token.ThrowIfCancellationRequested();
            LyricDocument document = CreateDocument(lrc, translation, source);
            document.WordTiming = wordTiming; document.TranslationWordTiming = translatedWordTiming;
            document.WordTimingChecked = song.Player == MusicPlayer.NetEase;
            LrcParser.ApplyWordTiming(document, wordTiming, false);
            LrcParser.ApplyWordTiming(document, translatedWordTiming, true);
            if (translationError.Length > 0) document.TranslationStatus = translationError;
            if (!HasUsefulLyrics(document) && source == MusicSnapshot.PlayerName(song.Player))
                document.Source += " · 平台暂未提供歌词";
            if (HasUsefulLyrics(document)) TryWriteCache(song, lrc, document, document.HasTranslation);
            StartTranslationCompletion(song, document, lrc, translationToken);
            return document;
        }

        private void StartTranslationCompletion(LyricSearchResult song, LyricDocument document, string rawLrc, CancellationToken token)
        {
            if (completeTranslations && !disposed && !token.IsCancellationRequested && song.Player == MusicPlayer.NetEase &&
                document.HasTimedLyrics && !document.WordTimingChecked)
            { StartNetEaseWordCompletion(song, document, rawLrc, token); return; }
            if (!completeTranslations || disposed || token.IsCancellationRequested ||
                song.Player != MusicPlayer.QQMusic || !document.HasTimedLyrics || !HasUsefulLyrics(document) || document.HasTranslation && document.WordTimingChecked) return;
            if (!document.HasTranslation) document.TranslationStatus = "正在获取平台译文，原文已显示";
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
                                    { { "songMID", song.Id }, { "songID", 0 }, { "trans", 1 }, { "roma", 0 }, { "qrc", 1 }, { "crypt", 0 }, { "type", 0 } }
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
                        if (document.HasTranslation) translation = document.Translation;
                        LyricDocument prepared = CreateDocument(rawLrc, translation, document.Source);
                        prepared.TranslationSource = document.HasTranslation ? document.TranslationSource : string.IsNullOrWhiteSpace(translation) ? "" : "QQ 音乐 · 平台译文";
                        prepared.WordTiming = DecodeQQ(StringValue(body, "lyric"));
                        prepared.TranslationWordTiming = translation;
                        prepared.WordTimingChecked = true;
                        LrcParser.ApplyWordTiming(prepared, prepared.WordTiming, false);
                        LrcParser.ApplyWordTiming(prepared, prepared.TranslationWordTiming, true);
                        linked.Token.ThrowIfCancellationRequested();
                        // Prepare an independent mapping even when the target
                        // already contains correctly sized blank translation rows.
                        // The controller observes the raw field, so publish it last.
                        document.TranslationLines = prepared.TranslationLines;
                        document.TranslationSource = prepared.TranslationSource;
                        document.TranslationStatus = prepared.TranslationStatus;
                        document.WordTiming = prepared.WordTiming; document.TranslationWordTiming = prepared.TranslationWordTiming; document.WordTimingChecked = true;
                        for (int i = 0; i < document.Lines.Count && i < prepared.Lines.Count; i++)
                        { document.Lines[i].EndSeconds = prepared.Lines[i].EndSeconds; document.Lines[i].Words = prepared.Lines[i].Words; }
                        Volatile.Write(ref document.Translation, prepared.Translation);
                        TryWriteCache(song, rawLrc, prepared, true);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!disposed && !document.HasTranslation && !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
                            document.TranslationStatus = "获取译文超时，原文可正常显示";
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException)
                    {
                        if (!disposed && !document.HasTranslation && !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
                            document.TranslationStatus = "平台译文暂时无法读取，原文可正常显示";
                    }
                }
            });
        }

        private void StartNetEaseWordCompletion(LyricSearchResult song, LyricDocument document, string rawLrc, CancellationToken token)
        {
            Task.Run(async delegate
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
                {
                    linked.CancelAfter(TimeSpan.FromSeconds(6));
                    try
                    {
                        var response = await GetJsonAsync(MusicPlayer.NetEase, "https://music.163.com/api/song/lyric?id=" + Uri.EscapeDataString(song.Id) + "&lv=-1&kv=-1&tv=-1&yv=-1&ytv=-1", linked.Token).ConfigureAwait(false);
                        RequireCode(response, 200, MusicPlayer.NetEase);
                        string words = StringValue(Child(response, "yrc"), "lyric");
                        if (words.Length == 0) words = StringValue(Child(response, "klyric"), "lyric");
                        string translatedWords = StringValue(Child(response, "ytlrc"), "lyric");
                        linked.Token.ThrowIfCancellationRequested();
                        LrcParser.ApplyWordTiming(document, words, false);
                        LrcParser.ApplyWordTiming(document, translatedWords, true);
                        document.WordTiming = words; document.TranslationWordTiming = translatedWords; document.WordTimingChecked = true;
                        TryWriteCache(song, rawLrc, document, document.HasTranslation);
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }
            });
        }

        public async Task<LyricDocument> FindAsync(MusicSnapshot song, CancellationToken token)
        {
            CheckDisposed();
            token.ThrowIfCancellationRequested();
            if (song == null || !song.HasTrack) return null;
            // The local player supplies the exact recording ID. Fetch that ID (and its
            // cache) immediately instead of searching titles or using an older match.
            if (song.Player == MusicPlayer.NetEase && MusicSnapshot.ValidNetEaseTrackId(song.PlatformTrackId))
                return await FetchAsync(new LyricSearchResult
                {
                    Player = song.Player, Id = song.PlatformTrackId, Title = song.Title,
                    Artist = song.Artist, Album = song.Album, DurationSeconds = song.DurationSeconds
                }, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(song.Artist) &&
                (string.IsNullOrWhiteSpace(song.Album) || song.DurationSeconds <= 0))
                return null;
            LyricDocument matchedCache = TryReadMatchedCache(song, token);
            if (matchedCache != null) return matchedCache;
            string queryTitle = song.Player == MusicPlayer.QQMusic ? QQSearchTitle(song.Title) : song.Title;
            string queryArtist = song.Player == MusicPlayer.QQMusic ? TrimQQArtistDisplayNames(song.Artist) : song.Artist;
            string query = queryTitle + (string.IsNullOrWhiteSpace(queryArtist) ? "" : " " + queryArtist);
            bool canUseAlternative = song.Player == MusicPlayer.QQMusic && !string.IsNullOrWhiteSpace(song.Artist) &&
                !string.IsNullOrWhiteSpace(song.Album) && song.DurationSeconds > 0;
            if (!canUseAlternative)
            {
                using (CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    if (song.Player == MusicPlayer.QQMusic) bounded.CancelAfter(TimeSpan.FromSeconds(6));
                    return FinishLookup(song, await LookupAsync(song, query, false, bounded.Token, token).ConfigureAwait(false));
                }
            }

            // Give the primary a short head start. A slow search must not prevent
            // a verified recording from the alternative catalog loading promptly.
            using (CancellationTokenSource primaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (CancellationTokenSource alternateCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                primaryCancellation.CancelAfter(TimeSpan.FromSeconds(6));
                alternateCancellation.CancelAfter(TimeSpan.FromSeconds(6.5));
                Task<LookupResult> primary = LookupAsync(song, query, false, primaryCancellation.Token, token);
                Task<LookupResult> alternative = null;
                LookupResult primaryResult = null, alternateResult = null;
                try
                {
                    await Task.WhenAny(primary, Task.Delay(500, token)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (primary.IsCompleted)
                    {
                        primaryResult = await primary.ConfigureAwait(false);
                        if (primaryResult.HasTimedLyrics) return FinishLookup(song, primaryResult);
                    }
                    alternative = LookupAsync(song, query, true, alternateCancellation.Token, token);
                    while (primaryResult == null || alternateResult == null)
                    {
                        if (primaryResult == null && alternateResult == null)
                            await Task.WhenAny(primary, alternative).ConfigureAwait(false);
                        else if (primaryResult == null) await primary.ConfigureAwait(false);
                        else await alternative.ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        // Prefer the primary when both verified documents are ready.
                        if (primaryResult == null && primary.IsCompleted)
                        {
                            primaryResult = await primary.ConfigureAwait(false);
                            if (primaryResult.HasTimedLyrics) return FinishLookup(song, primaryResult);
                        }
                        if (alternateResult == null && alternative.IsCompleted)
                        {
                            alternateResult = await alternative.ConfigureAwait(false);
                            if (alternateResult.HasTimedLyrics) return FinishLookup(song, alternateResult);
                        }
                    }
                    if (primaryResult.Document != null) return primaryResult.Document;
                    if (primaryResult.Failure != null)
                    {
                        string alternateStatus = alternateResult.Failure != null ? "备用来源也暂时无法读取：" + alternateResult.Failure.Message :
                            alternateResult.Document != null ? "备用来源已匹配到同一歌曲，但平台未提供带时间轴的歌词。" :
                            "备用来源未找到歌名、歌手、专辑和时长一致的歌词。";
                        throw new InvalidOperationException(primaryResult.Failure.Message + Environment.NewLine + alternateStatus, primaryResult.Failure);
                    }
                    if (alternateResult.Failure != null) throw alternateResult.Failure;
                    return null;
                }
                finally
                {
                    primaryCancellation.Cancel();
                    alternateCancellation.Cancel();
                    ObserveLookup(primary);
                    if (alternative != null) ObserveLookup(alternative);
                }
            }
        }

        private sealed class LookupResult
        {
            internal LyricDocument Document;
            internal LyricSearchResult Song;
            internal InvalidOperationException Failure;
            internal bool HasTimedLyrics { get { return Document != null && Document.HasTimedLyrics; } }
        }

        private async Task<LookupResult> LookupAsync(MusicSnapshot song, string query, bool alternative,
            CancellationToken requestToken, CancellationToken translationToken)
        {
            LookupResult result = new LookupResult();
            try
            {
                MusicSnapshot recording = alternative ? new MusicSnapshot
                {
                    Player = MusicPlayer.NetEase, Title = song.Title, Artist = song.Artist,
                    Album = song.Album, DurationSeconds = song.DurationSeconds
                } : song;
                List<LyricSearchResult> results = await SearchAsync(recording.Player, query, requestToken).ConfigureAwait(false);
                if (alternative)
                {
                    // Cross-provider selection still requires the exact recording
                    // identity, album and duration. Version words stay significant.
                    results = results.FindAll(delegate(LyricSearchResult candidate)
                    {
                        return DisplayAlbumMatches(song.Album, candidate, true);
                    });
                }
                result.Song = SelectAutomaticMatch(recording, results, alternative);
                if (result.Song != null) result.Document = await FetchAsync(result.Song, requestToken, translationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException error) { result.Failure = error; }
            catch (OperationCanceledException)
            {
                translationToken.ThrowIfCancellationRequested();
                result.Failure = new InvalidOperationException(MusicSnapshot.PlayerName(alternative ? MusicPlayer.NetEase : song.Player) + "歌词请求超时，将自动重试。");
            }
            return result;
        }

        private LyricDocument FinishLookup(MusicSnapshot observed, LookupResult result)
        {
            if (result.HasTimedLyrics)
            {
                if (result.Song.Player != observed.Player) result.Document.Source += " · QQ 音乐备用歌词";
                TryWriteMatch(observed, result.Song);
            }
            if (result.Document == null && result.Failure != null) throw result.Failure;
            return result.Document;
        }

        private static void ObserveLookup(Task<LookupResult> task)
        {
            task.ContinueWith(delegate(Task<LookupResult> completed) { var ignored = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // Version words remain significant: Live, remix and cover names must match.
        // Exact artist identity is preferred. QQ's media session can publish only
        // the first artist; that case needs the exact album, duration and one candidate.
        // Search order never decides an automatic match.
        internal static LyricSearchResult SelectAutomaticMatch(MusicSnapshot song, IEnumerable<LyricSearchResult> results, bool qqDisplayNames = false)
        {
            if (song == null || results == null || string.IsNullOrWhiteSpace(song.Title)) return null;
            string title = Normalize(song.Title), artist = ArtistSignature(song.Artist), album = Normalize(song.Album);
            bool haveArtist = artist.Length > 0, haveDuration = song.DurationSeconds > 0;
            bool displayNames = qqDisplayNames || song.Player == MusicPlayer.QQMusic;
            bool fullIdentity = haveArtist && album.Length > 0 && haveDuration &&
                !double.IsNaN(song.DurationSeconds) && !double.IsInfinity(song.DurationSeconds);
            if (!haveArtist && (!haveDuration || album.Length == 0)) return null;
            List<LyricSearchResult> matching = new List<LyricSearchResult>();
            HashSet<string> displayIdentityMatches = new HashSet<string>(StringComparer.Ordinal);
            List<LyricSearchResult> leadingArtistMatches = new List<LyricSearchResult>();
            List<LyricSearchResult> displayArtistMatches = new List<LyricSearchResult>();
            bool allowLeadingArtist = song.Player == MusicPlayer.QQMusic && haveArtist && artist.IndexOf('|') < 0
                && album.Length > 0 && haveDuration && !double.IsInfinity(song.DurationSeconds);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (LyricSearchResult candidate in results)
            {
                if (candidate == null || candidate.Player != song.Player || string.IsNullOrWhiteSpace(candidate.Id) || !seen.Add(candidate.Id)) continue;
                bool albumMatches = DisplayAlbumMatches(song.Album, candidate, displayNames && fullIdentity);
                bool exactTitle = Normalize(candidate.Title) == title;
                if (!exactTitle && !(displayNames && fullIdentity && albumMatches &&
                    DisplayNameMatches(song.Title, candidate.Title, candidate.TitleAliases))) continue;
                if (!haveArtist && Normalize(candidate.Album) != album) continue;
                if (haveDuration)
                {
                    // Durations may be rounded to whole seconds by QQ or the media session.
                    double tolerance = Math.Min(4.0, Math.Max(2.0, song.DurationSeconds * 0.01));
                    if (candidate.DurationSeconds <= 0 || double.IsNaN(candidate.DurationSeconds) || double.IsInfinity(candidate.DurationSeconds) ||
                        Math.Abs(candidate.DurationSeconds - song.DurationSeconds) > tolerance) continue;
                }
                if (haveArtist && ArtistSignature(candidate.Artist) != artist)
                {
                    if (displayNames && fullIdentity && albumMatches &&
                        ArtistSignature(TrimQQArtistDisplayNames(song.Artist)) == ArtistSignature(TrimQQArtistDisplayNames(candidate.Artist)))
                    {
                        displayArtistMatches.Add(candidate);
                        continue;
                    }
                    if (allowLeadingArtist && albumMatches)
                    {
                        string[] artists = ArtistSeparator.Split(Clean(candidate.Artist));
                        if (artists.Length > 1 && Normalize(artists[0]) == artist) leadingArtistMatches.Add(candidate);
                    }
                    continue;
                }
                matching.Add(candidate);
                if (!exactTitle || albumMatches && Normalize(candidate.Album) != album) displayIdentityMatches.Add(candidate.Id);
            }
            if (matching.Count == 0)
            {
                if (displayArtistMatches.Count > 0)
                    return displayArtistMatches.Count == 1 && leadingArtistMatches.Count == 0 ? displayArtistMatches[0] : null;
                return leadingArtistMatches.Count == 1 ? leadingArtistMatches[0] : null;
            }
            if (album.Length > 0)
            {
                List<LyricSearchResult> sameAlbum = matching.FindAll(delegate(LyricSearchResult candidate) { return DisplayAlbumMatches(song.Album, candidate, displayNames && fullIdentity); });
                if (sameAlbum.Count > 0) matching = sameAlbum;
            }
            if (matching.Count == 1) return matching[0];
            // Display compensation requires one recording; a slightly closer
            // duration cannot resolve several translated names or album aliases.
            if (matching.Exists(delegate(LyricSearchResult candidate) { return displayIdentityMatches.Contains(candidate.Id); })) return null;
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
                    TitleAliases = ReadDisplayAliases(item), AlbumAliases = ReadDisplayAliases(album),
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
                    TitleAliases = ReadDisplayAliases(item), AlbumAliases = ReadDisplayAliases(album),
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
        private static List<string> ReadDisplayAliases(IDictionary<string, object> value)
        {
            List<string> aliases = new List<string>();
            if (value == null) return aliases;
            foreach (string key in new[] { "transNames", "tns", "alias", "alia" })
            {
                object raw;
                if (!value.TryGetValue(key, out raw) || raw is string) continue;
                IEnumerable entries = raw as IEnumerable;
                if (entries == null) continue;
                foreach (object entry in entries)
                {
                    string alias = entry as string;
                    if (alias == null) continue;
                    alias = Clean(alias);
                    if (alias.Length > 0 && alias.Length <= 200 && !aliases.Contains(alias)) aliases.Add(alias);
                    if (aliases.Count >= 16) return aliases;
                }
            }
            return aliases;
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
            value = value.Replace('\u301C', '~');
            value = Regex.Replace(value, @"\s+", " ").Trim();
            // Keep significant punctuation: "C++" must not match "C", and
            // "A B" must not match "AB". Only bracket spacing is cosmetic.
            return Regex.Replace(value, @"\s*([\(\)\[\]\{\}~])\s*", "$1");
        }
        internal static string TrimTranslatedDisplaySuffix(string title)
        {
            string text = Clean(title).Normalize(NormalizationForm.FormKC);
            Match suffix = Regex.Match(text, @"\A(?<name>.+)\s*\((?<translation>[^()]+)\)\z");
            if (!suffix.Success) return text;
            string name = suffix.Groups["name"].Value.Trim(), translation = suffix.Groups["translation"].Value.Trim();
            // QQ appends Chinese translations to Japanese display names. Only this
            // narrow display convention is eligible; recording/version labels stay.
            if (!Regex.IsMatch(name, @"[\u3040-\u30ff]") || !Regex.IsMatch(translation, @"[\u3400-\u9fff]") ||
                Regex.IsMatch(translation, @"[A-Za-z\u3040-\u30ff]") ||
                Regex.IsMatch(translation, @"版|现场|翻唱|伴奏|清唱|纯音乐|纯享|混音|重制|加长|录音|演唱会|原声|主题曲|片头曲|片尾曲")) return text;
            return name;
        }
        private static bool DisplayAlbumMatches(string observed, LyricSearchResult candidate, bool qqDisplayNames)
        {
            return Normalize(observed) == Normalize(candidate.Album) ||
                qqDisplayNames && DisplayNameMatches(observed, candidate.Album, candidate.AlbumAliases);
        }
        private static bool HasRecordingQualifier(string text)
        {
            return Regex.IsMatch(text ?? "", @"\b(?:live|dj|cover|remix|mix|version|instrumental|acoustic|explicit|clean|edit|radio|short|extended|remaster)\b|版|现场|翻唱|伴奏|清唱|纯音乐|纯享|混音|重制|加长|录音|演唱会|原声|主题曲|片头曲|片尾曲", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }
        private static bool DisplayNameMatches(string observed, string canonical, List<string> aliases)
        {
            string name = Normalize(observed);
            if (name == Normalize(canonical) || Normalize(TrimTranslatedDisplaySuffix(observed)) == Normalize(canonical)) return true;
            if (aliases == null || HasRecordingQualifier(canonical)) return false;
            foreach (string alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || HasRecordingQualifier(alias)) continue;
                if (name == Normalize(alias) || name == Normalize(canonical + " (" + alias + ")")) return true;
            }
            return false;
        }
        internal static string QQSearchTitle(string title)
        {
            string text = Clean(title).Normalize(NormalizationForm.FormKC);
            Match suffix = Regex.Match(text, @"\A(?<name>.+)\s*\((?<translation>[^()]+)\)\z", RegexOptions.CultureInvariant);
            if (!suffix.Success) return text;
            string translation = suffix.Groups["translation"].Value.Trim();
            // Query simplification never changes the recording match. English and
            // kanji-only display translations require catalog aliases for selection.
            return Regex.IsMatch(translation, @"[\u3400-\u9fff]") && !Regex.IsMatch(translation, @"[A-Za-z\u3040-\u30ff]") &&
                !HasRecordingQualifier(translation) ? suffix.Groups["name"].Value.Trim() : text;
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
        internal static string TrimQQArtistDisplayNames(string text)
        {
            string original = Clean(text).Normalize(NormalizationForm.FormKC);
            string[] names = ArtistSeparator.Split(original);
            bool changed = false;
            for (int i = 0; i < names.Length; i++)
            {
                Match suffix = Regex.Match(names[i], @"\A(?<name>[^()]+?)\s*\((?<alias>[^()]+)\)\s*\z", RegexOptions.CultureInvariant);
                if (!suffix.Success) continue;
                string name = suffix.Groups["name"].Value.Trim(), alias = suffix.Groups["alias"].Value.Trim();
                // QQ decorates artist names with another written name.
                // Keep collaboration, performance and version labels significant.
                if (name.Length == 0 || alias.Length == 0 || alias.Length > 100 ||
                    !Regex.IsMatch(alias, @"\A[\p{L}\p{M}\p{Nd}\s.'’·・-]+\z", RegexOptions.CultureInvariant) ||
                    !Regex.IsMatch(alias, @"\p{L}", RegexOptions.CultureInvariant) ||
                    Regex.IsMatch(alias, @"\b(?:live|dj|cover|feat|ft|featuring|with|guest|version|remix|mix|solo|band|acoustic|instrumental|original|from)\b|版|现场|翻唱|伴奏|角色|成员|演唱|合唱|客串", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)) continue;
                names[i] = name;
                changed = true;
            }
            return changed ? string.Join(" / ", names) : original;
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
                !DisplayAlbumMatches(observed.Album, chosen, observed.Player == MusicPlayer.QQMusic)) return false;
            if (chosen.Player != observed.Player && !(observed.Player == MusicPlayer.QQMusic && chosen.Player == MusicPlayer.NetEase)) return false;
            MusicSnapshot recording = new MusicSnapshot
            {
                Player = chosen.Player, Title = observed.Title, Artist = observed.Artist,
                Album = observed.Album, DurationSeconds = observed.DurationSeconds
            };
            return SelectAutomaticMatch(recording, new[] { chosen }, observed.Player == MusicPlayer.QQMusic) == chosen;
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
                chosen.TitleAliases = ReadCachedAliases(root, "titleAliases/alias");
                chosen.AlbumAliases = ReadCachedAliases(root, "albumAliases/alias");
                ValidateSong(chosen);
                if (!BindingIdentityMatches(observed, chosen)) return null;
                string rawLrc;
                bool translationRecentlyChecked;
                LyricDocument cached = TryReadCache(chosen, out rawLrc, out translationRecentlyChecked);
                if (cached == null || !cached.HasTimedLyrics) return null;
                token.ThrowIfCancellationRequested();
                if (chosen.Player != observed.Player) cached.Source += " · QQ 音乐备用歌词";
                if (!translationRecentlyChecked || !cached.WordTimingChecked) StartTranslationCompletion(chosen, cached, rawLrc, token);
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
                    WriteCachedAliases(writer, "titleAliases", chosen.TitleAliases);
                    WriteCachedAliases(writer, "albumAliases", chosen.AlbumAliases);
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
        private static List<string> ReadCachedAliases(XmlElement root, string path)
        {
            List<string> aliases = new List<string>();
            foreach (XmlNode node in root.SelectNodes(path))
            {
                string value = node.InnerText;
                if (value.Length > 0 && value.Length <= 200 && !aliases.Contains(value)) aliases.Add(value);
                if (aliases.Count >= 16) break;
            }
            return aliases;
        }
        private static void WriteCachedAliases(XmlWriter writer, string name, List<string> aliases)
        {
            if (aliases == null || aliases.Count == 0) return;
            writer.WriteStartElement(name);
            int count = 0;
            foreach (string alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || alias.Length > 200) continue;
                writer.WriteElementString("alias", alias);
                if (++count >= 16) break;
            }
            writer.WriteEndElement();
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
                XmlNode wordTiming = root.SelectSingleNode("wordTiming"), translatedWords = root.SelectSingleNode("translationWordTiming");
                document.WordTiming = wordTiming == null ? "" : wordTiming.InnerText;
                document.TranslationWordTiming = translatedWords == null ? "" : translatedWords.InnerText;
                XmlNode wordsChecked = root.SelectSingleNode("wordTimingChecked");
                document.WordTimingChecked = wordsChecked != null && wordsChecked.InnerText == "true";
                LrcParser.ApplyWordTiming(document, document.WordTiming, false);
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
                    writer.WriteElementString("wordTiming", document.WordTiming ?? "");
                    writer.WriteElementString("translationWordTiming", document.TranslationWordTiming ?? "");
                    writer.WriteElementString("wordTimingChecked", document.WordTimingChecked ? "true" : "false");
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
