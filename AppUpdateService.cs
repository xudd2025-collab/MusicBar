using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MusicBar
{
    public sealed class UpdateRelease
    {
        public string Version = "", Notes = "", FileName = "", Sha256 = "", ReleaseUrl = "";
        public List<string> DownloadUrls = new List<string>();
        public bool IsNew { get { Version parsed; return AppUpdateService.TryVersion(Version, out parsed) && parsed > AppUpdateService.CurrentVersion; } }
    }

    public sealed class AppUpdateService : IDisposable
    {
        private readonly HttpClient client;
        private readonly string cachePath, downloadDirectory;
        public readonly string Repository;
        public UpdateRelease Latest { get; private set; }
        public string Status { get; private set; }
        public static Version CurrentVersion { get { var value = Assembly.GetExecutingAssembly().GetName().Version; return new Version(value.Major, value.Minor, value.Build); } }
        public static string VersionLabel { get { return "v" + CurrentVersion; } }
        public bool Configured { get { return ValidRepository(Repository); } }

        public AppUpdateService(string baseDirectory, string dataDirectory) : this(baseDirectory, dataDirectory, null, null) { }
        internal AppUpdateService(string baseDirectory, string dataDirectory, HttpMessageHandler handler, string repository)
        {
            Repository = repository ?? ReadRepository(Path.Combine(baseDirectory, "update-source.json"));
            cachePath = Path.Combine(dataDirectory, "update-cache.json");
            downloadDirectory = Path.Combine(dataDirectory, "updates");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) : new HttpClient(handler);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicBar/" + CurrentVersion);
            Status = Configured ? "等待检查更新" : "更新来源尚未设置";
            try { if (Configured && File.Exists(cachePath)) Latest = ParseManifest(File.ReadAllText(cachePath), Repository); } catch { }
        }

        private static string ReadRepository(string path)
        {
            try { var config = Json(File.ReadAllText(path)); return Text(config, "repository"); } catch { return ""; }
        }
        public static bool ValidRepository(string repository) { return repository != null && Regex.IsMatch(repository, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z"); }
        internal static bool TryVersion(string value, out Version version)
        {
            version = null;
            string normalized = (value ?? "").Trim().TrimStart('v', 'V');
            if (!Regex.IsMatch(normalized, @"\A\d+\.\d+\.\d+(\.0)?\z") || !Version.TryParse(normalized, out version)) return false;
            version = new Version(version.Major, version.Minor, version.Build); return true;
        }
        internal static IList<string> MetadataUrls(string repository)
        {
            return new[] { "https://api.github.com/repos/" + repository + "/releases/latest", "https://raw.githubusercontent.com/" + repository + "/main/update.json", "https://cdn.jsdelivr.net/gh/" + repository + "@main/update.json", "https://fastly.jsdelivr.net/gh/" + repository + "@main/update.json" };
        }

        public async Task<bool> CheckAsync(CancellationToken token)
        {
            if (!Configured) { Status = "更新来源尚未设置"; return false; }
            Status = "正在检查更新…";
            var requests = MetadataUrls(Repository).Select(url => FetchRelease(url, token)).ToList();
            UpdateRelease newest = Latest;
            bool succeeded = false;
            while (requests.Count > 0)
            {
                Task<UpdateRelease> finished = await Task.WhenAny(requests).ConfigureAwait(false);
                requests.Remove(finished);
                var found = await finished.ConfigureAwait(false);
                if (found == null) continue;
                succeeded = true;
                Version candidate, previous;
                if (newest == null || (TryVersion(found.Version, out candidate) && TryVersion(newest.Version, out previous) && candidate > previous)) newest = found;
                else if (found.Version == newest.Version && string.Equals(found.Sha256, newest.Sha256, StringComparison.OrdinalIgnoreCase)) newest.DownloadUrls = newest.DownloadUrls.Concat(found.DownloadUrls).Distinct().ToList();
            }
            token.ThrowIfCancellationRequested();
            Latest = newest;
            if (succeeded)
            {
                Status = Latest != null && Latest.IsNew ? "发现新版本 v" + Latest.Version : "已是最新版本";
                SaveCache();
            }
            else Status = Latest != null && Latest.IsNew ? "网络暂不可用，保留已发现的更新" : "暂时无法检查更新，将自动重试";
            return succeeded;
        }
        private async Task<UpdateRelease> FetchRelease(string url, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    using (var response = await GetAsync(url, timeout.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 256 * 1024) return null;
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream())
                        {
                            byte[] bytes = new byte[8192]; int count;
                            while ((count = await stream.ReadAsync(bytes, 0, bytes.Length, timeout.Token).ConfigureAwait(false)) > 0)
                            { if (buffer.Length + count > 256 * 1024) return null; buffer.Write(bytes, 0, count); }
                            string body = Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
                            return url.StartsWith("https://api.github.com/", StringComparison.Ordinal) ? ParseGitHub(body, Repository) : ParseManifest(body, Repository);
                        }
                    }
                }
                catch (Exception error) { if (!(error is HttpRequestException || error is IOException || error is OperationCanceledException || error is ArgumentException || error is InvalidOperationException)) throw; return null; }
            }
        }
        private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken token)
        {
            for (int redirects = 0; redirects < 6; redirects++)
            {
                if (!TrustedUrl(url, Repository)) throw new InvalidOperationException("更新地址不属于已配置的发布来源。");
                HttpResponseMessage response;
                using (var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                { headersTimeout.CancelAfter(TimeSpan.FromSeconds(12)); response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false); }
                if ((int)response.StatusCode < 300 || (int)response.StatusCode > 399) return response;
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new InvalidOperationException("更新下载重定向无效。");
                url = location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(new Uri(url), location).AbsoluteUri;
            }
            throw new InvalidOperationException("更新下载重定向次数过多。");
        }
        internal static bool TrustedUrl(string value, string repository)
        {
            Uri uri;
            if (!ValidRepository(repository) || !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0) return false;
            string path = Uri.UnescapeDataString(uri.AbsolutePath), owner = repository.Split('/')[0];
            if (uri.Host == "github.com") return path.StartsWith("/" + repository + "/", StringComparison.OrdinalIgnoreCase);
            if (uri.Host == "api.github.com") return path.StartsWith("/repos/" + repository + "/", StringComparison.OrdinalIgnoreCase);
            if (uri.Host == "raw.githubusercontent.com") return path.StartsWith("/" + repository + "/", StringComparison.OrdinalIgnoreCase);
            if (uri.Host == "cdn.jsdelivr.net" || uri.Host == "fastly.jsdelivr.net") return path.StartsWith("/gh/" + repository + "@", StringComparison.OrdinalIgnoreCase);
            if (uri.Host == owner.ToLowerInvariant() + ".github.io") return path.StartsWith("/" + repository.Split('/')[1] + "/", StringComparison.OrdinalIgnoreCase);
            return uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com";
        }
        private static IDictionary<string, object> Json(string body) { return new JavaScriptSerializer { MaxJsonLength = 256 * 1024, RecursionLimit = 32 }.DeserializeObject(body) as IDictionary<string, object>; }
        private static string Text(IDictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value as string ?? "" : ""; }
        private static IEnumerable<object> Array(IDictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value as IEnumerable<object> ?? new object[0] : new object[0]; }
        internal static UpdateRelease ParseManifest(string body, string repository)
        {
            var root = Json(body); object item;
            var installer = root != null && root.TryGetValue("installer", out item) ? item as IDictionary<string, object> : null;
            var release = new UpdateRelease { Version = Text(root, "version"), Notes = Text(root, "notes"), ReleaseUrl = Text(root, "release_url"), FileName = Text(installer, "name"), Sha256 = Text(installer, "sha256") };
            foreach (var url in Array(installer, "urls")) if (url is string && TrustedUrl((string)url, repository)) release.DownloadUrls.Add((string)url);
            return Validate(release, repository) ? release : null;
        }
        internal static UpdateRelease ParseGitHub(string body, string repository)
        {
            var root = Json(body); object value;
            if (root == null || (root.TryGetValue("draft", out value) && value is bool && (bool)value) || (root.TryGetValue("prerelease", out value) && value is bool && (bool)value)) return null;
            string version = Text(root, "tag_name").TrimStart('v','V');
            foreach (var entry in Array(root, "assets"))
            {
                var asset = entry as IDictionary<string, object>;
                string digest = Text(asset, "digest");
                var release = new UpdateRelease { Version = version, Notes = Text(root, "body"), ReleaseUrl = Text(root, "html_url"), FileName = Text(asset, "name"), Sha256 = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7) : "" };
                string url = Text(asset, "browser_download_url");
                if (TrustedUrl(url, repository)) release.DownloadUrls.Add(url);
                if (Validate(release, repository)) return release;
            }
            return null;
        }
        private static bool Validate(UpdateRelease release, string repository)
        {
            Version version;
            return release != null && TryVersion(release.Version, out version) && Regex.IsMatch(release.FileName, @"\AMusicBar-Setup-\d+\.\d+\.\d+\.exe\z") && release.FileName == "MusicBar-Setup-" + version.ToString(3) + ".exe" && Regex.IsMatch(release.Sha256, @"\A[0-9a-fA-F]{64}\z") && release.ReleaseUrl.StartsWith("https://github.com/" + repository + "/releases/", StringComparison.OrdinalIgnoreCase) && release.DownloadUrls.Count > 0;
        }
        private void SaveCache()
        {
            if (Latest == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                string json = new JavaScriptSerializer().Serialize(new { version = Latest.Version, notes = Latest.Notes, release_url = Latest.ReleaseUrl, installer = new { name = Latest.FileName, sha256 = Latest.Sha256, urls = Latest.DownloadUrls } });
                File.WriteAllText(cachePath + ".tmp", json);
                if (File.Exists(cachePath)) File.Replace(cachePath + ".tmp", cachePath, null); else File.Move(cachePath + ".tmp", cachePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        public async Task<string> DownloadAsync(UpdateRelease release, IProgress<int> progress, CancellationToken token)
        {
            if (!Validate(release, Repository) || !release.IsNew) throw new InvalidOperationException("没有可安装的新版本。");
            Directory.CreateDirectory(downloadDirectory);
            string destination = Path.Combine(downloadDirectory, release.FileName);
            var urls = release.DownloadUrls.Concat(new[] { "https://raw.githubusercontent.com/" + Repository + "/updates/releases/v" + release.Version + "/" + release.FileName, "https://cdn.jsdelivr.net/gh/" + Repository + "@updates/releases/v" + release.Version + "/" + release.FileName, "https://fastly.jsdelivr.net/gh/" + Repository + "@updates/releases/v" + release.Version + "/" + release.FileName }).Distinct().ToList();
            foreach (string url in urls)
            {
                token.ThrowIfCancellationRequested();
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(3));
                    try
                    {
                        using (var response = await GetAsync(url, timeout.Token).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode) continue;
                            long? expected = response.Content.Headers.ContentLength;
                            if (expected > 100 * 1024 * 1024) continue;
                            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                            using (var hash = SHA256.Create())
                            {
                                byte[] buffer = new byte[64 * 1024]; long received = 0; int count;
                                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                                {
                                    received += count;
                                    if (received > 100 * 1024 * 1024) throw new IOException("更新文件过大。");
                                    await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                                    if (progress != null && expected > 0) progress.Report((int)Math.Min(99, received * 100 / expected.Value));
                                }
                                hash.TransformFinalBlock(new byte[0], 0, 0);
                                if (received < 2 || !string.Equals(BitConverter.ToString(hash.Hash).Replace("-", ""), release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新文件校验失败。");
                            }
                        }
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
                        if (progress != null) progress.Report(100);
                        return destination;
                    }
                    catch (Exception error) { if (!(error is HttpRequestException || error is IOException || error is OperationCanceledException || error is InvalidOperationException)) throw; }
                    finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
                }
            }
            token.ThrowIfCancellationRequested();
            throw new IOException("所有下载线路暂时不可用，请稍后重试或打开发布页。");
        }
        internal static bool VerifyFile(string path, string expected)
        {
            if (!Regex.IsMatch(expected ?? "", @"\A[0-9a-fA-F]{64}\z") || !File.Exists(path)) return false;
            using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return string.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase);
        }
        public void Dispose() { client.Dispose(); }
    }
}
