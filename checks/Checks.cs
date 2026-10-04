using System;
using System.IO;

namespace MusicBar
{
    public static class Checks
    {
        private static int passed;
        [STAThread]
        public static int Main()
        {
            try
            {
                string checkDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "scratch", "root-checks", Guid.NewGuid().ToString("N")));
                var store = new SettingsStore(checkDirectory);
                var defaults = store.Load();
                Check(defaults.QQEnabled && defaults.NetEaseEnabled && defaults.ClickThrough, "usable defaults");
                Check(defaults.LyricBrightness == 115, "lyrics default to a brighter display");
                defaults.Width = 700; defaults.TextColor = "#ABCDEF"; defaults.ActiveColor = "#29E89D";
                defaults.QQEnabled = false; defaults.OffsetSeconds = -1.5; defaults.PreviewEnabled = true;
                Check(store.Save(defaults), "settings saved atomically");
                var restored = store.Load();
                Check(restored.Width == 700 && restored.TextColor == "#ABCDEF" && !restored.QQEnabled && restored.OffsetSeconds == -1.5, "settings persist");
                Check(!restored.PreviewEnabled, "preview does not fake playback after restart");
                defaults.LyricBrightness = 145; store.Save(defaults);
                Check(store.Load().LyricBrightness == 145, "brightness persists across restart");
                File.WriteAllText(Path.Combine(store.DataDirectory, "settings.json"), "{\"Width\":410,\"FontSize\":19,\"HorizontalOffset\":410,\"ClickThrough\":true,\"QQEnabled\":true,\"OverlayEnabled\":true,\"OnlineLyrics\":true,\"FontFamily\":\"Microsoft YaHei UI\",\"TextColor\":\"#F8FAFC\",\"ActiveColor\":\"#DAD74E\",\"Alignment\":\"left\"}");
                var upgraded = store.Load();
                Check(upgraded.LyricBrightness == 115 && upgraded.ActiveColor == "#DAD74E", "old settings brighten without replacing the selected lyric color");
                var brightColor = upgraded.Brightened(upgraded.Highlight);
                Check(brightColor.R > upgraded.Highlight.R && brightColor.G > upgraded.Highlight.G && brightColor.B > upgraded.Highlight.B && brightColor.A == upgraded.Highlight.A, "brightness raises all amber color channels");
                upgraded.LyricBrightness = 999; upgraded.Normalize(); Check(upgraded.LyricBrightness == 160, "brightness maximum is bounded");
                upgraded.LyricBrightness = -1; upgraded.Normalize(); Check(upgraded.LyricBrightness == 30, "brightness minimum is bounded");
                string startupPath = Path.GetFullPath(Path.Combine(checkDirectory, "歌词工具", "MusicBar.exe"));
                Check(StartupRegistration.Command(startupPath) == "\"" + startupPath + "\" --autostart", "startup quotes paths with spaces and Chinese characters");
                foreach (string invalid in new[] { "", "MusicBar.exe", "C:\\bad\"path.exe" })
                {
                    bool rejected = false; try { StartupRegistration.Command(invalid); } catch (ArgumentException) { rejected = true; }
                    Check(rejected, "invalid startup path rejected");
                }
                Check(upgraded.ShowTranslation && upgraded.LongLineScroll && upgraded.Width == 410 && upgraded.FontSize == 19 && upgraded.HorizontalOffset == 410, "old portable settings enable new defaults without losing existing display choices");
                upgraded.ShowTranslation = false; upgraded.LongLineScroll = false; store.Save(upgraded);
                var choices = store.Load();
                Check(!choices.ShowTranslation && !choices.LongLineScroll, "explicit translation and scrolling choices survive restart");
                var document = LrcParser.Parse("[00:01.00]第一行\n[00:03.50]第二行\n[00:05.00]\n[00:06.00]尾声");
                document.Source = "检查用本地歌词";
                document.Translation = "[00:01.012]第一句译文\n[00:03.512]第二句译文\n[00:06.012]末句译文";
                Check(document.FindLine(0) == -1 && document.FindLine(3.49) == 0 && document.FindLine(3.5) == 1, "lyric boundary lookup");
                Check(document.Lines[document.FindLine(5.2)].Text == "", "instrumental clears line");
                string trackKey = "QQMusic|测试歌曲|测试歌手";
                var bilingual = LrcParser.Parse("[01:26.376]Burn it all");
                bilingual.Translation = "[01:26.376]Burn it all\n[01:26.376]焚尽旧章";
                LrcParser.EnsureTranslation(bilingual);
                Check(LrcParser.GetTranslationForLine(bilingual, 0) == "焚尽旧章", "bilingual provider captions exclude the original");
                bilingual.TranslationLines[0].Text = "Burn it all / 焚尽旧章";
                LrcParser.EnsureTranslation(bilingual);
                Check(bilingual.TranslationLines[0].Text == "焚尽旧章", "old cached bilingual captions are cleaned locally");
                bilingual.Translation = "[01:26.376]Burn it all"; bilingual.TranslationLines.Clear();
                LrcParser.EnsureTranslation(bilingual);
                Check(!bilingual.HasTranslation, "an original-only caption is not a translation");
                bilingual.Translation = "[01:26.376]请唱出 Burn it all / 焚尽旧章";
                LrcParser.EnsureTranslation(bilingual);
                Check(LrcParser.GetTranslationForLine(bilingual, 0) == "请唱出 Burn it all / 焚尽旧章", "a phrase within a real translation is preserved");
                Check(store.SaveOverride(trackKey, document), "manual song binding saved");
                var loaded = store.LoadOverride(trackKey);
                Check(loaded != null && loaded.Lines.Count == 4 && loaded.Lines[1].Text == "第二行", "manual song binding restored");
                Check(store.LoadOverride("NetEase|其他歌|其他歌手") == null, "binding isolated by track and player");
                var settings = new AppSettings { ShowSongWhenMissing = false };
                using (var controller = new LyricController(settings, store))
                {
                    controller.Snapshot = new MusicSnapshot { Player = MusicPlayer.QQMusic, Title = "测试歌曲", Artist = "测试歌手", PositionSeconds = 2, DurationSeconds = 100, HasTimeline = true, IsPlaying = false };
                    controller.ApplyDocument(document);
                    Check(controller.Current == "第一行" && !controller.ManualMode, "paused player uses actual position");
                    Check(!controller.IsSongInfo && controller.CurrentLineIndex == 0, "timed lyrics distinguished from song fallback");
                    Check(controller.CurrentTranslation == "第一句译文" && Math.Abs(controller.RemainingLineSeconds - 1.5) < 0.001, "same-line translation and actual line duration available for scrolling");
                    settings.OffsetSeconds = 2;
                    controller.SettingsChanged(false);
                    Check(controller.Current == "第二行", "positive display offset advances lyrics");
                    Check(controller.CurrentTranslation == "第二句译文", "translation follows the same calibrated player clock");
                    settings.ShowTranslation = false; controller.SettingsChanged(false);
                    Check(controller.CurrentTranslation == "" && controller.Current == "第二行" && controller.Next == "", "turning translation off clears only translated display state");
                    settings.ShowTranslation = true; controller.SettingsChanged(false);
                    controller.SeekManual(3.2);
                    Check(controller.Current == "" && controller.CurrentTranslation == "", "instrumental blank clears both original and translation");
                    controller.UsePlayerClock();
                    settings.HideWhenPaused = true;
                    Check(!controller.ShouldDisplay, "pause hiding applied");
                    controller.SeekManual(1.6);
                    Check(controller.ManualMode && controller.Current == "第二行", "manual clock and offset");
                    controller.ToggleManual();
                    Check(controller.ManualPlaying && controller.ShouldDisplay, "manual playing controls visibility");
                    controller.ToggleManual();
                    Check(!controller.ManualPlaying, "manual pause");
                    controller.UsePlayerClock();
                    Check(!controller.ManualMode, "return to player clock");
                    settings.QQEnabled = false;
                    controller.SettingsChanged(false);
                    Check(!controller.Snapshot.HasTrack && controller.Document == null && controller.Current == "", "disabled plugin clears stale lyrics");
                    Check(!controller.IsSongInfo && controller.CurrentLineIndex == -1, "disabled state clears lyric role and index");
                    Check(controller.CurrentTranslation == "" && controller.RemainingLineSeconds == 0, "disabled player clears translation and animation clock");
                }
                using (var fallback = new LyricController(new AppSettings(), store))
                {
                    fallback.Snapshot = new MusicSnapshot { Player = MusicPlayer.QQMusic, Title = "没有歌词的歌", Artist = "歌手", HasTimeline = true, PositionSeconds = 10 };
                    fallback.SettingsChanged(false);
                    Check(fallback.IsSongInfo && fallback.CurrentLineIndex == -1, "song-only display explicitly marked");
                    fallback.ApplyDocument(LrcParser.Parse("[00:00]真实时间轴歌词\n[00:20]下一行"));
                    Check(!fallback.IsSongInfo && fallback.Current == "真实时间轴歌词", "successful lyrics replace song info");
                    fallback.Document.Translation = "[00:00.012]迟到的对应译文\n[00:20.012]下一句译文";
                    fallback.SettingsChanged(false);
                    Check(fallback.CurrentTranslation == "迟到的对应译文", "late translation replaces an already built empty mapping");
                }
                File.WriteAllText(Path.Combine(store.DataDirectory, "settings.json"), "invalid-json");
                var recovered = store.Load();
                Check(recovered.QQEnabled && recovered.NetEaseEnabled && !string.IsNullOrEmpty(store.LastError), "damaged settings recover visibly");
                Console.WriteLine("MusicBar state/settings checks passed: " + passed);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Failed: " + name);
            passed++;
        }
    }
}
