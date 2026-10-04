using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MusicBar
{
    internal static class UpdateChecks
    {
        private static int count;
        private const string Repo = "test-owner/MusicBar";
        private static readonly byte[] Installer = Encoding.UTF8.GetBytes("local fake installer bytes; never executed");
        private static string Hash(byte[] value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        private static string Manifest(string version, string hash)
        {
            return new JavaScriptSerializer().Serialize(new { version = version, notes = "测试新版", release_url = "https://github.com/" + Repo + "/releases/tag/v" + version, installer = new { name = "MusicBar-Setup-" + version + ".exe", sha256 = hash, urls = new[] { "https://github.com/" + Repo + "/releases/download/v" + version + "/MusicBar-Setup-" + version + ".exe" } } });
        }
        private sealed class FakeNetwork : HttpMessageHandler
        {
            public bool Offline, Corrupt, CancelDownload;
            public int DownloadAttempts;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                string url = request.RequestUri.AbsoluteUri;
                if (Offline) throw new HttpRequestException("offline");
                if (url.EndsWith(".exe", StringComparison.Ordinal))
                {
                    DownloadAttempts++;
                    if (CancelDownload) return Task.Delay(Timeout.Infinite, token).ContinueWith<HttpResponseMessage>(t => { token.ThrowIfCancellationRequested(); return null; }, token);
                    // The primary fails its hash; raw fallback is either valid or also corrupt.
                    var bytes = Corrupt || request.RequestUri.Host == "github.com" ? Encoding.UTF8.GetBytes("corrupt") : Installer;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
                }
                if (request.RequestUri.Host == "api.github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                string version = request.RequestUri.Host == "raw.githubusercontent.com" ? "99.1.0" : "99.0.0";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest(version, Hash(Installer))) });
            }
        }
        public static int Main()
        {
            try { Run().GetAwaiter().GetResult(); Console.WriteLine("Update checks passed: " + count + " (local fake network)"); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static async Task Run()
        {
            Version version;
            Check(AppUpdateService.TryVersion("v1.0.10.0", out version) && version == new Version(1,0,10), "zero revision is normalized");
            Check(!AppUpdateService.TryVersion("1.0.10-beta", out version), "prerelease rejected");
            Check(!AppUpdateService.ValidRepository("../other"), "repository validation");
            foreach (var url in new[] { "http://github.com/" + Repo + "/x", "https://github.com.evil.test/" + Repo + "/x", "https://github.com/other/MusicBar/x", "https://user@github.com/" + Repo + "/x", "https://github.com:444/" + Repo + "/x", "file:///test.exe" }) Check(!AppUpdateService.TrustedUrl(url, Repo), "untrusted url rejected");
            foreach (var url in AppUpdateService.MetadataUrls(Repo)) Check(AppUpdateService.TrustedUrl(url, Repo), "official metadata route allowed");
            Check(AppUpdateService.ParseManifest(Manifest("99.1.0", "bad"), Repo) == null, "bad hash metadata rejected");
            Check(AppUpdateService.ParseManifest(Manifest("99.1.0", Hash(Installer)).Replace("MusicBar-Setup-99.1.0.exe", "../evil.exe"), Repo) == null, "unsafe file name rejected");
            Check(AppUpdateService.ParseGitHub("{\"draft\":true,\"tag_name\":\"v99.1.0\"}", Repo) == null, "draft release rejected");
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "scratch", "update-checks", Guid.NewGuid().ToString("N"));
            var handler = new FakeNetwork();
            using (var service = new AppUpdateService(root, root, handler, Repo))
            {
                Check(await service.CheckAsync(CancellationToken.None), "alternate metadata works when API fails");
                Check(service.Latest.Version == "99.1.0" && service.Latest.IsNew, "stale CDN cannot replace newer metadata");
                handler.Offline = true;
                Check(!await service.CheckAsync(CancellationToken.None) && service.Latest.IsNew && service.Status.Contains("保留"), "offline keeps detected update");
                handler.Offline = false;
                string path = await service.DownloadAsync(service.Latest, null, CancellationToken.None);
                Check(handler.DownloadAttempts == 2 && AppUpdateService.VerifyFile(path, Hash(Installer)), "corrupt primary switches to verified fallback");
                Check(!Directory.GetFiles(Path.Combine(root, "updates"), "*.part").Any(), "success cleans partial files");
                handler.Corrupt = true; bool failed = false;
                try { await service.DownloadAsync(service.Latest, null, CancellationToken.None); } catch (IOException) { failed = true; }
                Check(failed && AppUpdateService.VerifyFile(path, Hash(Installer)), "all hash failures preserve earlier verified download");
                Check(!Directory.GetFiles(Path.Combine(root, "updates"), "*.part").Any(), "failures clean partial files");
                handler.CancelDownload = true;
                using (var cancel = new CancellationTokenSource(100))
                {
                    bool canceled = false; try { await service.DownloadAsync(service.Latest, null, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
                    Check(canceled, "download cancellation propagates");
                }
            }
            using (var cached = new AppUpdateService(root, root, new FakeNetwork { Offline = true }, Repo)) Check(cached.Latest != null && cached.Latest.Version == "99.1.0", "cache restores update on next launch");
            using (var other = new AppUpdateService(root, root, new FakeNetwork(), "other/MusicBar")) Check(other.Latest == null, "cache cannot change repository");
            var store = new SettingsStore(root); var settings = new AppSettings { AutoCheckUpdates = false }; store.Save(settings);
            Check(!store.Load().AutoCheckUpdates, "automatic update opt out persists");
            File.WriteAllText(Path.Combine(store.DataDirectory, "settings.json"), "{}"); Check(store.Load().AutoCheckUpdates, "old settings enable automatic check");
        }
    }
}
