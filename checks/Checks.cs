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
                Check(defaults.HideWhenPaused, "new settings hide lyrics while paused");
                Check(defaults.KaraokeEnabled && defaults.BoldLyrics && defaults.EnableHotkey && !defaults.HideTaskbarIcon, "usable karaoke, readability and hotkey defaults");
                defaults.TranslationColor = "#12AB34"; defaults.TranslationActiveColor = "#FE8721"; defaults.TranslationFontFamily = "等线"; defaults.HotkeyPreset = 2; defaults.HideTaskbarIcon = true;
                store.Save(defaults); var colorSettings = store.Load();
                Check(colorSettings.TranslationColor == "#12AB34" && colorSettings.TranslationActiveColor == "#FE8721" && colorSettings.HotkeyPreset == 2 && colorSettings.HideTaskbarIcon, "translation palette and application access settings survive restart");
                Check(colorSettings.TranslationFontFamily == "等线", "independent translation font survives restart");
                Check(colorSettings.EffectiveHotkeyKey == 0x4D && colorSettings.EffectiveHotkeyModifiers == 5, "existing Alt Shift M preset stays effective after upgrade");
                var customSettings = new AppSettings { UseCustomHotkey = true, HotkeyKey = 0x4B, HotkeyModifiers = 3 };
                Check(store.Save(customSettings), "custom hotkey settings saved");
                var customRestored = store.Load();
                Check(customRestored.UseCustomHotkey && customRestored.EffectiveHotkeyKey == 0x4B && customRestored.EffectiveHotkeyModifiers == 3, "Ctrl Alt K survives restart without becoming a preset");
                Check(AppSettings.ValidHotkey(0x77, 6) && AppSettings.ValidHotkey(0x35, 1) && AppSettings.ValidHotkey(0x4A, 7), "function keys, digits and all three modifiers are supported");
                Check(!AppSettings.ValidHotkey(0x41, 0) && !AppSettings.ValidHotkey(0x41, 4), "global hotkeys never capture plain or Shift-only typing");
                for (int functionKey = 0x70; functionKey <= 0x87; functionKey++)
                    Check(AppSettings.ValidHotkey(functionKey, 0) && AppSettings.ValidHotkey(functionKey, 4), "standalone and Shift function keys: " + functionKey);
                var singleKeySettings = new AppSettings { UseCustomHotkey = true, HotkeyKey = 0x70, HotkeyModifiers = 0 };
                Check(store.Save(singleKeySettings), "standalone F1 settings saved");
                var singleKeyRestored = store.Load();
                Check(singleKeyRestored.UseCustomHotkey && singleKeyRestored.EffectiveHotkeyKey == 0x70 && singleKeyRestored.EffectiveHotkeyModifiers == 0, "standalone F1 survives normalization and restart");
                Check(!AppSettings.ValidHotkey(0x11, 3) && !AppSettings.ValidHotkey(0x01, 3) && !AppSettings.ValidHotkey(0x10041, 3), "modifier-only, mouse and invalid key values rejected");
                Check(!AppSettings.ValidHotkey(0x41, 8) && !AppSettings.ValidHotkey(0x41, 0x4003), "unsupported persisted modifier flags rejected");
                customRestored.HotkeyKey = 0x11; customRestored.Normalize();
                Check(!customRestored.UseCustomHotkey && customRestored.EffectiveHotkeyKey == 0x4D && customRestored.EffectiveHotkeyModifiers == 3, "invalid custom settings restore the default shortcut");
                Check(defaults.QQEnabled && defaults.NetEaseEnabled && defaults.ClickThrough, "usable defaults");
                Check(defaults.LyricBrightness == 100, "new lyric palettes display at their selected brightness");
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
                Check(upgraded.KaraokeEnabled && upgraded.BoldLyrics && upgraded.EnableHotkey && upgraded.TranslationColor == upgraded.TextColor && upgraded.TranslationActiveColor == upgraded.ActiveColor, "old settings acquire karaoke and translation palette without replacing colors");
                Check(upgraded.TranslationFontFamily == "", "old settings follow the original font for translated text");
                Check(!upgraded.UseCustomHotkey && upgraded.EffectiveHotkeyKey == 0x4D && upgraded.EffectiveHotkeyModifiers == 3, "older settings without custom fields retain Ctrl Alt M");
                Check(upgraded.LyricBrightness == 115 && upgraded.ActiveColor == "#DAD74E", "old settings brighten without replacing the selected lyric color");
                var brightColor = upgraded.Brightened(upgraded.Highlight);
                Check(brightColor.R > upgraded.Highlight.R && brightColor.G > upgraded.Highlight.G && brightColor.B > upgraded.Highlight.B && brightColor.A == upgraded.Highlight.A, "brightness raises all amber color channels");
                upgraded.LyricBrightness = 145;
                var pale = System.Drawing.ColorTranslator.FromHtml("#F8FCF8");
                var paleBright = upgraded.Brightened(pale);
                Check(paleBright.G == 255 && paleBright.R < 255 && paleBright.B < 255, "high brightness preserves a pale custom hue instead of flattening it to white");
                var blue = System.Drawing.ColorTranslator.FromHtml("#7CCEFF");
                Check(upgraded.Brightened(blue).ToArgb() == blue.ToArgb(), "a full-bright blue keeps its selected hue");
                upgraded.LyricBrightness = 100;
                Check(upgraded.Brightened(pale).ToArgb() == pale.ToArgb(), "100 percent renders exactly the selected color");
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
                Check(upgraded.HideInstrumental && upgraded.InstrumentalHoldSeconds == 10, "older settings enable instrumental hiding with a conservative hold");
                upgraded.HideInstrumental = false; upgraded.InstrumentalHoldSeconds = 14; store.Save(upgraded);
                Check(!store.Load().HideInstrumental && store.Load().InstrumentalHoldSeconds == 14, "instrumental preference and long-note hold persist");
                upgraded.ShowTranslation = false; upgraded.LongLineScroll = false; store.Save(upgraded);
                var choices = store.Load();
                Check(!choices.ShowTranslation && !choices.LongLineScroll, "explicit translation and scrolling choices survive restart");
                var document = LrcParser.Parse("[00:01.00]第一行\n[00:03.50]第二行\n[00:05.00]\n[00:06.00]尾声");
                var creditsOnly = LrcParser.Parse("[00:00.00-1] 作词 : Test writer\n[00:00.00-1] 作曲 : Test composer");
                Check(!creditsOnly.HasTimedLyrics && string.IsNullOrWhiteSpace(creditsOnly.PlainText), "untimed platform production credits are not reported as missing lyric timestamps");
                var mixedCredits = LrcParser.Parse("[00:00.00-1] 作词 : Test writer\n[00:02]真实歌词\n[00:05]下一句");
                Check(mixedCredits.Lines.Count == 2 && mixedCredits.Lines[0].Text == "真实歌词" && !mixedCredits.PlainText.Contains("Test writer"), "credit cleanup preserves actual lyric text and time");
                Check(LrcParser.IsNonVocalCue("纯音乐，请欣赏") && LrcParser.IsNonVocalCue("此歌曲为没有填词的纯音乐，请欣赏。") && !LrcParser.IsNonVocalCue("我听着纯音乐想起你"), "instrumental platform placeholders are hidden without hiding real vocal sentences");
                var enhanced = LrcParser.Parse("[00:01]<00:01>你<00:02>好<00:04>\n[00:06]下一句");
                Check(enhanced.Lines[0].Text == "你好" && enhanced.Lines[0].Words.Count == 2 && enhanced.Lines[0].EndSeconds == 4, "enhanced LRC keeps real word times without painting timestamp tags");
                Check(Math.Abs(LrcParser.KaraokeProgress(enhanced.Lines[0], 1.5, 6) - .25) < .001, "native word timing overrides uniform line estimation");
                Check(Math.Abs(LrcParser.KaraokeProgress(enhanced.Lines[0], 3, 6) - .75) < .001, "second word has its own duration");
                Check(LrcParser.KaraokeProgress(enhanced.Lines[0], 4, 6) == 1 && LrcParser.KaraokeProgress(enhanced.Lines[0], 0, 6) == 0, "karaoke clamps before and after vocals");
                var plain = LrcParser.Parse("[00:01]你好\n[00:05]下一句");
                Check(Math.Abs(LrcParser.KaraokeProgress(plain.Lines[0], 3, 5) - .5) < .001, "plain LRC provides bounded sentence progress");
                string yrc = "[1000,3000](1000,1000,0)你(2000,2000,0)好";
                Check(LrcParser.ApplyWordTiming(plain, yrc, false) == 1 && Math.Abs(LrcParser.KaraokeProgress(plain.Lines[0], 1.5, 5) - .25) < .001, "NetEase YRC maps only identical lyric text and timing");
                var qrc = LrcParser.Parse("[00:01]你好");
                Check(LrcParser.ApplyWordTiming(qrc, "<QrcInfos><Lyric_1 LyricContent=\"[1000,3000]你(1000,1000)好(2000,2000)\" /></QrcInfos>", false) == 1, "QQ plaintext QRC uses the same word timing model");
                Check(LrcParser.ApplyWordTiming(qrc, "[1000,3000](1000,1000,0)错(2000,2000,0)词", false) == 0, "word timing from a different caption is rejected");
                qrc.Translation = "[00:01]<00:01>你<00:02>们<00:04>"; LrcParser.EnsureTranslation(qrc);
                Check(qrc.TranslationLines[0].Words.Count == 2 && Math.Abs(LrcParser.KaraokeProgress(qrc.TranslationLines[0], 3, 5) - .75) < .001, "translation has independent real word times");
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
                    Check(!controller.ShouldDisplay, "paused lyrics are retained internally and hidden with the default setting");
                    settings.HideWhenPaused = false;
                    Check(controller.ShouldDisplay, "users can keep the current lyric visible while paused");
                    settings.HideWhenPaused = true;
                    controller.Snapshot.IsPlaying = true;
                    Check(controller.ShouldDisplay, "resuming playback restores the retained lyric without another lookup");
                    controller.Snapshot.IsPlaying = false;
                    Check(!controller.IsSongInfo && controller.CurrentLineIndex == 0, "timed lyrics distinguished from song fallback");
                    Check(controller.CurrentTranslation == "第一句译文" && Math.Abs(controller.RemainingLineSeconds - 1.5) < 0.001, "same-line translation and actual line duration available for scrolling");
                    double pausedProgress = controller.OriginalKaraokeProgress;
                    Check(Math.Abs(pausedProgress - .4) < .001 && Math.Abs(controller.TranslationKaraokeProgress - .4) < .001, "both languages use the player's current sentence progress");
                    controller.UsePlayerClock();
                    Check(controller.OriginalKaraokeProgress == pausedProgress, "paused karaoke does not advance on repeated display updates");
                    controller.Snapshot.PositionSeconds = 2.8; controller.UsePlayerClock();
                    Check(controller.OriginalKaraokeProgress > pausedProgress && Math.Abs(controller.OriginalKaraokeProgress - .72) < .001, "seeking repositions karaoke within the same sentence");
                    controller.Snapshot.PositionSeconds = 2; controller.UsePlayerClock();
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
                using (var fallback = new LyricController(new AppSettings { HideInstrumental = false }, store))
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
                using (var instrumental = new LyricController(new AppSettings { TwoLines = true }, store))
                {
                    instrumental.Snapshot = new MusicSnapshot { Player = MusicPlayer.QQMusic, Title = "伴奏测试", HasTimeline = true, IsPlaying = true, InterpolateTimeline = false, DurationSeconds = 100 };
                    var rest = LrcParser.Parse("[00:01]作曲：测试\n[00:03]第一句\n[00:06]\n[00:09]第二句\n[00:13]（间奏）\n[00:16]长间隔前的歌词\n[00:46]再次开始演唱\n[00:54]尾声歌词");
                    rest.Translation = "[00:03]First\n[00:09]Second\n[00:16]Before the break\n[00:46]Singing again\n[00:54]Last line";
                    instrumental.ApplyDocument(rest);
                    Check(!instrumental.ShouldDisplay && instrumental.Current == "", "intro before the first lyric hides instead of showing the title");
                    instrumental.Snapshot.PositionSeconds = 2; instrumental.UsePlayerClock();
                    Check(!instrumental.ShouldDisplay && instrumental.Current == "" && instrumental.Next == "", "credits do not show upcoming vocals during intro");
                    instrumental.Snapshot.PositionSeconds = 4; instrumental.UsePlayerClock();
                    Check(instrumental.ShouldDisplay && instrumental.Current == "第一句" && instrumental.CurrentTranslation == "First", "vocals show both languages after intro");
                    instrumental.Snapshot.Title = "第一句"; instrumental.UsePlayerClock();
                    Check(instrumental.ShouldDisplay && instrumental.Current == "第一句", "a sung line identical to the song title remains visible");
                    instrumental.Snapshot.Title = "伴奏测试";
                    instrumental.Snapshot.PositionSeconds = 7; instrumental.UsePlayerClock();
                    Check(!instrumental.ShouldDisplay && instrumental.CurrentTranslation == "" && instrumental.Next == "", "explicit empty rest hides original, translation and second row");
                    instrumental.Snapshot.PositionSeconds = 14; instrumental.UsePlayerClock();
                    Check(!instrumental.ShouldDisplay && instrumental.Next == "", "bracketed interlude cue hides without displaying the next lyric");
                    instrumental.Snapshot.PositionSeconds = 25.99; instrumental.UsePlayerClock();
                    Check(instrumental.ShouldDisplay && instrumental.Current == "长间隔前的歌词", "conservative hold keeps the line until the selected deadline");
                    instrumental.Snapshot.PositionSeconds = 26; instrumental.UsePlayerClock();
                    Check(!instrumental.ShouldDisplay && instrumental.Current == "" && instrumental.CurrentTranslation == "" && instrumental.RemainingLineSeconds == 0, "unmarked long gap clears both languages at its display deadline");
                    instrumental.Settings.InstrumentalHoldSeconds = 15; instrumental.UsePlayerClock();
                    Check(instrumental.ShouldDisplay && instrumental.Current == "长间隔前的歌词", "long-note setting can extend a line that was hidden");
                    instrumental.Snapshot.PositionSeconds = 46; instrumental.UsePlayerClock();
                    Check(instrumental.ShouldDisplay && instrumental.Current == "再次开始演唱" && instrumental.CurrentTranslation == "Singing again", "next timestamp restores both languages without restarting playback");
                    instrumental.Snapshot.PositionSeconds = 80; instrumental.UsePlayerClock();
                    Check(!instrumental.ShouldDisplay, "long outro does not hold the final lyric until song end");
                    instrumental.Settings.HideInstrumental = false; instrumental.UsePlayerClock();
                    Check(instrumental.ShouldDisplay && instrumental.Current == "尾声歌词", "turning instrumental hiding off restores the original display policy");
                    Check(!LrcParser.IsNonVocalCue("Music is my life") && !LrcParser.IsNonVocalCue("这首歌的间奏很美"), "ordinary lyrics containing cue words remain vocals");
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
