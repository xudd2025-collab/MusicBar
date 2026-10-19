using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;

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
                PersistentCacheChecks().GetAwaiter().GetResult();
                AutomaticRetryChecks().GetAwaiter().GetResult();
                PlatformIsolationChecks().GetAwaiter().GetResult();
                DisplayAnnotationChecks();
                string checkDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "scratch", "root-checks", Guid.NewGuid().ToString("N")));
                var store = new SettingsStore(checkDirectory);
                var defaults = store.Load();
                Check(defaults.MaxCharactersPerLine == 0, "existing lyrics remain unlimited until a character limit is selected");
                defaults.MaxCharactersPerLine = 12; store.Save(defaults);
                Check(store.Load().MaxCharactersPerLine == 12, "per-line character limit survives restart");
                defaults.MaxCharactersPerLine = -1; defaults.Normalize();
                Check(defaults.MaxCharactersPerLine == 0, "invalid negative character limit returns to unlimited");
                defaults.MaxCharactersPerLine = 500; defaults.Normalize();
                Check(defaults.MaxCharactersPerLine == 200, "oversized character limit is bounded");
                defaults.MaxCharactersPerLine = 0;
                string segmentText = "甲😀e\u0301乙丁戊己庚";
                int[] segmentBoundaries = System.Globalization.StringInfo.ParseCombiningCharacters(segmentText);
                foreach (int limit in new[] { 1, 3, 7 })
                {
                    string restoredSegments = "";
                    for (int first = 0; first < segmentBoundaries.Length; first += limit)
                    {
                        var segment = LyricTextSegment.Select(segmentText, limit, (double)segmentBoundaries[first] / segmentText.Length);
                        Check(System.Globalization.StringInfo.ParseCombiningCharacters(segment.Text).Length <= limit, "character segments never exceed the chosen limit or split a Unicode text element");
                        Check(Math.Abs(segment.Progress) < .000001, "segment highlighting starts at the correct lyric offset");
                        restoredSegments += segment.Text;
                    }
                    Check(restoredSegments == segmentText, "all character segments preserve every original character in order");
                }
                var middleSegment = LyricTextSegment.Select("一二三四五六七八九十", 3, .45);
                Check(middleSegment.Text == "四五六" && Math.Abs(middleSegment.Progress - .5) < .000001, "segment highlighting follows the actual position within its own text");
                Check(LyricTextSegment.Select("一二三四五六七八九十", 3, 1).Text == "十", "finished lyrics retain their final segment");
                Check(LyricTextSegment.Select("一二三四五六七八九十", 3, .05).Text == "一二三", "seeking backward returns to the earlier segment");
                Check(LyricTextSegment.Select(segmentText, 0, .5).Text == segmentText, "zero limit leaves the complete lyric untouched");
                Check(LyricTextSegment.Select("一二三四", 2, double.NaN).Text == "一二", "invalid playback progress starts safely from the first segment");
                LyricTextSegment pairOriginal, pairTranslated;
                LyricTextSegment.SelectPair("一二三四五六七八九十", "甲乙丙丁戊己庚辛", 3, .45, .65, out pairOriginal, out pairTranslated);
                Check(pairOriginal.Text == "三四五" && pairTranslated.Text == "丙丁", "different-length lyric and translation share the same page despite different highlighting progress");
                LyricTextSegment.SelectPair("一二三四五六七八九十", "甲乙丙丁戊己庚辛", 3, .45, .05, out pairOriginal, out pairTranslated);
                Check(pairTranslated.Text == "丙丁", "translated word progress cannot switch the paired translated page");
                LyricTextSegment.SelectPair("一二三四五六七八九十", "甲乙丙丁戊己庚辛", 3, .05, .8, out pairOriginal, out pairTranslated);
                Check(pairOriginal.Text == "一二" && pairTranslated.Text == "甲乙", "seeking backward repositions both lyric rows together");
                LyricTextSegment.SelectPair("一二三四五六七八九十", "甲乙丙丁戊己庚辛", 3, 1, 1, out pairOriginal, out pairTranslated);
                Check(pairOriginal.Text == "八九十" && pairTranslated.Text == "庚辛", "both paired rows retain their final page at completion");
                foreach (int limit in new[] { 1, 3, 12 })
                    foreach (double progress in new[] { 0, .1, .49, .8, 1 })
                    {
                        LyricTextSegment.SelectPair(segmentText, "甲乙丙丁戊己庚辛壬癸甲乙丙丁戊己庚辛壬癸", limit, progress, .8, out pairOriginal, out pairTranslated);
                        Check(System.Globalization.StringInfo.ParseCombiningCharacters(pairOriginal.Text).Length <= limit &&
                            System.Globalization.StringInfo.ParseCombiningCharacters(pairTranslated.Text).Length <= limit &&
                            pairOriginal.Text.Length > 0 && pairTranslated.Text.Length > 0,
                            "paired Unicode rows stay nonempty and within the limit when the translation is longer");
                    }
                LyricTextSegment.SelectPair(segmentText, "译文", 0, .5, .8, out pairOriginal, out pairTranslated);
                Check(pairOriginal.Text == segmentText && pairTranslated.Text == "译文", "unlimited paired rows preserve the complete sentence");
                LyricTextSegment.SelectPair(new string('甲', 70000) + "乙", new string('丙', 70000) + "丁", 1, 1, 1, out pairOriginal, out pairTranslated);
                Check(pairOriginal.Text == "乙" && pairTranslated.Text == "丁", "long paired text cannot overflow page indexing at a one-character limit");
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
                defaults.ColorMode = "gradient"; defaults.GradientEndColor = "#C4A5FF"; defaults.TranslationGradientEndColor = "#FFB8D9";
                defaults.RecentColors = new System.Collections.Generic.List<string> { "#abcdef", "invalid", "#ABCDEF", "#123456" };
                defaults.AuthorNoticeShown = true; defaults.AuthorWatermarkDismissed = true;
                Check(store.Save(defaults), "gradient and author visit settings save atomically");
                var paletteRestored = store.Load();
                Check(paletteRestored.ColorMode == "gradient" && paletteRestored.GradientEndColor == "#C4A5FF" && paletteRestored.TranslationGradientEndColor == "#FFB8D9", "both gradient endpoints and selected mode survive restart");
                Check(paletteRestored.RecentColors.Count == 2 && paletteRestored.RecentColors[0] == "#ABCDEF" && paletteRestored.RecentColors[1] == "#123456", "recent colors normalize valid values without duplicates and preserve recency");
                Check(paletteRestored.AuthorNoticeShown && paletteRestored.AuthorWatermarkDismissed, "completed author visit keeps notice and watermark dismissed on restart");
                paletteRestored.ColorMode = "unknown"; paletteRestored.GradientEndColor = null; paletteRestored.TranslationGradientEndColor = "bad"; paletteRestored.RecentColors = null; paletteRestored.Normalize();
                Check(paletteRestored.ColorMode == "solid" && AppSettings.ValidColor(paletteRestored.GradientEndColor) && AppSettings.ValidColor(paletteRestored.TranslationGradientEndColor) && paletteRestored.RecentColors.Count == 0, "invalid new palette settings recover safely");
                File.WriteAllText(Path.Combine(store.DataDirectory, "settings.json"), "{\"Width\":410,\"FontSize\":19,\"HorizontalOffset\":410,\"ClickThrough\":true,\"QQEnabled\":true,\"OverlayEnabled\":true,\"OnlineLyrics\":true,\"FontFamily\":\"Microsoft YaHei UI\",\"TextColor\":\"#F8FAFC\",\"ActiveColor\":\"#DAD74E\",\"Alignment\":\"left\"}");
                var upgraded = store.Load();
                Check(upgraded.ColorMode == "solid" && upgraded.RecentColors.Count == 0 && !upgraded.AuthorNoticeShown && !upgraded.AuthorWatermarkDismissed, "old settings preserve solid colors and receive the initial author notice");
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
                string zeroDurationQrc = "[1000,3000]甲(1000,1000) (2000,0)乙(2000,2000)\n[5000,1000]丙(5000,1000)";
                var zeroDuration = LrcParser.ParseWordTiming(zeroDurationQrc);
                Check(zeroDuration.Lines.Count == 2 && zeroDuration.Lines[0].Text == "甲 乙" && zeroDuration.Lines[1].Text == "丙",
                    "a zero-duration QQ separator preserves its entire lyric line and the following line");
                Check(zeroDuration.Lines[0].Words.Count == 3 && zeroDuration.Lines[0].Words[1].StartSeconds == 2 && zeroDuration.Lines[0].Words[1].EndSeconds == 2,
                    "zero-duration words retain provider timestamps without inventing a duration");
                Check(zeroDuration.Lines[0].EndSeconds == 4 && zeroDuration.FindLine(3) == 0,
                    "a retained QQ sentence remains available during its supplied line interval");
                double previousZeroProgress = 0;
                foreach (double position in new[] { 1.0, 1.5, 2.0, 2.1, 3.0, 4.0 })
                {
                    double progress = LrcParser.KaraokeProgress(zeroDuration.Lines[0], position, 5);
                    Check(progress >= previousZeroProgress && progress <= 1, "zero-duration separators never reverse or overflow karaoke progress");
                    previousZeroProgress = progress;
                }
                var zeroDurationLrc = LrcParser.Parse("[00:01]甲 乙");
                Check(LrcParser.ApplyWordTiming(zeroDurationLrc, zeroDurationQrc, false) == 1 && zeroDurationLrc.Lines[0].Words.Count == 3,
                    "cached ordinary lyrics accept the same zero-duration native word map");
                var zeroDurationYrc = LrcParser.ParseWordTiming("[1000,3000](1000,1000,0)甲(2000,0,0) (2000,2000,0)乙");
                Check(zeroDurationYrc.Lines.Count == 1 && zeroDurationYrc.Lines[0].Text == "甲 乙",
                    "the shared native parser preserves zero-duration YRC text too");
                foreach (string malformed in new[] { "[1000,3000]甲(999,0)", "[1000,3000]甲(4400,0)",
                    "[1000,3000]甲(2000,500)乙(1500,0)", "[1000,3000]甲(1000,1000)unparsed", "[1000,0]甲(1000,0)" })
                    Check(!LrcParser.ParseWordTiming(malformed).HasTimedLyrics, "zero-duration support does not accept malformed or out-of-range native rows");
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
                    settings.KaraokeEnabled = false; controller.SettingsChanged(false);
                    Check(controller.OriginalKaraokeProgress == 0 && Math.Abs(controller.OriginalLineProgress - .4) < .001, "character segments keep playback progress when karaoke coloring is disabled");
                    settings.KaraokeEnabled = true; controller.SettingsChanged(false);
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

        private static void DisplayAnnotationChecks()
        {
            const string displayed = "なんでもないや (没什么大不了) (Movie ver.)";
            const string canonical = "なんでもないや (Movie ver.)";
            const string album = "君の名は。 (《你的名字。》动画电影原声带)";
            Check(LyricRepository.QQSearchTitle(displayed) == canonical, "search removes a display translation before a preserved movie version");
            Check(LyricRepository.TrimTranslatedDisplaySuffix(displayed) == canonical, "Japanese display matching handles a translation before the version suffix");
            Check(LyricRepository.QQSearchTitle("なんでもないや (Movie ver.) (没什么大不了)") == canonical, "search also handles a translation after the movie version");
            Check(LyricRepository.QQSearchTitle("ねえ (再见) 君へ") == "ねえ (再见) 君へ", "parentheses inside the actual song title remain significant");
            Check(LyricRepository.QQSearchTitle("(无题)") == "(无题)", "a parenthesized title never becomes an empty search");
            Check(LyricRepository.QQCatalogDiscoveryTitle(displayed) == "なんでもないや", "catalog discovery accepts a bare smartbox name for a versioned song");
            Check(LyricRepository.QQCatalogDiscoveryTitle("なんでもないや (Movie edit.)") == "なんでもないや", "alternate versions can locate catalog details but cannot authorize a recording");
            Check(LyricRepository.QQCatalogDiscoveryTitle("ねえ (再见) 君へ") == "ねえ (再见) 君へ", "catalog discovery preserves internal title parentheses");
            Check(LyricRepository.QQCatalogDiscoveryTitle("(无题)") == "(无题)", "catalog discovery never produces an empty title from parentheses");
            Check(LyricRepository.TrimTranslatedDisplaySuffix("Small Song (小小的歌) (Movie ver.)") == "Small Song (小小的歌) (Movie ver.)",
                "English display translations still need explicit catalog aliases");
            foreach (string version in new[] { "Movie ver.", "Movie edit.", "Live", "TV size", "Instrumental", "Acoustic", "钢琴", "交响", "不插电", "重新录制", "再録" })
            {
                string title = "なんでもないや (" + version + ")";
                Check(LyricRepository.QQSearchTitle(title) == title, "search preserves recording qualifier " + version);
                Check(LyricRepository.TrimTranslatedDisplaySuffix(title) == title, "display matching preserves recording qualifier " + version);
            }
            var observed = new MusicSnapshot { Player = MusicPlayer.QQMusic, Title = displayed, Artist = "RADWIMPS", Album = album, DurationSeconds = 341.891 };
            var candidate = new LyricSearchResult { Player = MusicPlayer.QQMusic, Id = "displayfixture", Title = canonical, Artist = "RADWIMPS", Album = album, DurationSeconds = 341 };
            Check(LyricRepository.SelectAutomaticMatch(observed, new[] { candidate }) == candidate, "a full recording identity accepts the middle display translation");
            candidate.Title = "なんでもないや (Movie edit.)";
            Check(LyricRepository.SelectAutomaticMatch(observed, new[] { candidate }) == null, "the movie edit cannot replace the movie version");
            candidate.Title = "なんでもないや";
            Check(LyricRepository.SelectAutomaticMatch(observed, new[] { candidate }) == null, "a bare catalog discovery title cannot replace the full recording title");
            candidate.Title = canonical; candidate.Artist = "Another artist";
            Check(LyricRepository.SelectAutomaticMatch(observed, new[] { candidate }) == null, "display annotations do not weaken artist identity");
            candidate.Artist = "RADWIMPS"; candidate.Album = "Another album";
            Check(LyricRepository.SelectAutomaticMatch(observed, new[] { candidate }) == null, "a unique result from another album is not enough");
            candidate.Album = album; candidate.DurationSeconds = 300;
            Check(LyricRepository.SelectAutomaticMatch(observed, new[] { candidate }) == null, "display annotations do not weaken duration checks");
            candidate.DurationSeconds = 341;
            var second = new LyricSearchResult { Player = candidate.Player, Id = "secondfixture", Title = canonical, Artist = candidate.Artist, Album = album, DurationSeconds = 340 };
            Check(LyricRepository.SelectAutomaticMatch(observed, new[] { candidate, second }) == null, "ambiguous display variants are never selected by search order or a closer duration");
            observed.DurationSeconds = 0;
            Check(LyricRepository.SelectAutomaticMatch(observed, new[] { candidate }) == null, "display compensation requires a real duration");
            observed.DurationSeconds = 341.891; observed.Title = canonical;
            candidate.Title = "なんでもないや (钢琴) (Movie ver.)";
            Check(LyricRepository.SelectAutomaticMatch(observed, new[] { candidate }) == null, "candidate performance annotations cannot be stripped to force a match");
            var myAll = new MusicSnapshot { Player = MusicPlayer.QQMusic, Title = "MY ALL", Artist = "滨崎步", Album = "GUILTY", DurationSeconds = 327.453 };
            var catalog = new LyricSearchResult { Player = MusicPlayer.QQMusic, Id = "album-alias-fixture", Title = "MY ALL", Artist = "滨崎步", Album = "GUILTY (原罪)", DurationSeconds = 327 };
            catalog.AlbumAliases.Add("GUILTY");
            Check(LyricRepository.SelectAutomaticMatch(myAll, new[] { catalog }) == catalog, "a provider-declared album name resolves its translated album title");
            catalog.AlbumAliases.Clear();
            Check(LyricRepository.SelectAutomaticMatch(myAll, new[] { catalog }) == null, "an album translation without a provider-declared alias cannot authorize a recording");
            catalog.Album = "GUILTY (Deluxe Edition)"; catalog.AlbumAliases.Add("GUILTY");
            Check(LyricRepository.SelectAutomaticMatch(myAll, new[] { catalog }) == null, "an album alias cannot hide a deluxe recording qualifier");
            catalog.Album = "GUILTY (Movie ver.)";
            Check(LyricRepository.SelectAutomaticMatch(myAll, new[] { catalog }) == null, "an album alias cannot hide a movie version qualifier");
        }

        private enum RetryReply { NoMatch, ServiceFailure, EmptyLyrics, PlainLyrics, TimedLyrics }

        // These controller checks use only an in-memory NetEase endpoint. No
        // provider races, public requests or long retry delays are involved.
        private sealed class RetryHandler : HttpMessageHandler
        {
            internal RetryReply Reply;
            internal int Calls, SearchCalls, LyricCalls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref Calls);
                if (request.RequestUri.Host != "music.163.com")
                    throw new InvalidOperationException("Retry fixture attempted an unexpected provider.");
                string body;
                if (request.RequestUri.AbsolutePath == "/api/search/get/web")
                {
                    Interlocked.Increment(ref SearchCalls);
                    body = Reply == RetryReply.NoMatch ? "{\"code\":200,\"result\":{\"songs\":[]}}" :
                        "{\"code\":200,\"result\":{\"songs\":[{\"id\":9876501,\"name\":\"Retry fixture\",\"duration\":10000,\"album\":{\"name\":\"Retry album\"},\"artists\":[{\"name\":\"Retry artist\"}]}]}}";
                }
                else if (request.RequestUri.AbsolutePath == "/api/song/lyric")
                {
                    Interlocked.Increment(ref LyricCalls);
                    string lyric = Reply == RetryReply.EmptyLyrics ? "" : Reply == RetryReply.PlainLyrics ? "Waiting for timeline" :
                        "[00:01]Recovered first\n[00:05]Recovered next";
                    body = new JavaScriptSerializer().Serialize(new { code = 200, lrc = new { lyric = lyric } });
                }
                else throw new InvalidOperationException("Unexpected retry fixture route: " + request.RequestUri.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(Reply == RetryReply.ServiceFailure ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }

        private static MusicSnapshot RetryTrack()
        {
            return new MusicSnapshot { Player = MusicPlayer.NetEase, Title = "Retry fixture", Artist = "Retry artist",
                Album = "Retry album", DurationSeconds = 10, HasTimeline = true, IsPlaying = true,
                PositionSeconds = 2, InterpolateTimeline = false };
        }

        private static async Task RetryIdle(LyricController controller)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (controller.Searching && DateTime.UtcNow < deadline) await Task.Delay(10).ConfigureAwait(false);
            Check(!controller.Searching, "the local retry fixture finishes without a real retry wait");
        }

        private static async Task RetryWhenDue(LyricController controller, MusicSnapshot track)
        {
            DateTime due = controller.NextLyricRetryUtc;
            Check(due != DateTime.MaxValue, "an unfinished automatic song retains a future recovery attempt");
            controller.ApplySnapshot(track.Copy(), due.AddMilliseconds(1));
            await RetryIdle(controller).ConfigureAwait(false);
        }

        private static async Task AutomaticRetryChecks()
        {
            string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            // Leave room for the cache's atomic-write suffix under legacy
            // .NET Framework MAX_PATH behavior on deeply nested checkouts.
            string directory = Path.Combine(root, "r-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (RetryReply failure in new[] { RetryReply.NoMatch, RetryReply.ServiceFailure })
                {
                    var store = new SettingsStore(Path.Combine(directory, failure.ToString()));
                    var handler = new RetryHandler { Reply = failure };
                    using (var repository = new LyricRepository(store.DataDirectory, handler))
                    using (var controller = new LyricController(new AppSettings(), store, repository))
                    {
                        var track = RetryTrack();
                        controller.ApplySnapshot(track, DateTime.UtcNow);
                        await RetryIdle(controller).ConfigureAwait(false);
                        int attempts = failure == RetryReply.NoMatch ? 5 : 6;
                        for (int i = 1; i < attempts; i++) await RetryWhenDue(controller, track).ConfigureAwait(false);
                        Check(handler.SearchCalls == attempts && controller.Document == null,
                            "the same song keeps searching after repeated " + failure + " results");
                        int before = handler.Calls;
                        controller.ApplySnapshot(track.Copy(), controller.NextLyricRetryUtc.AddMilliseconds(-1));
                        Check(handler.Calls == before, "retry backoff prevents immediate request loops after " + failure);
                        handler.Reply = RetryReply.TimedLyrics;
                        await RetryWhenDue(controller, track).ConfigureAwait(false);
                        Check(controller.Document != null && controller.Document.HasTimedLyrics && controller.Current == "Recovered first",
                            "the same song displays recovered timed lyrics after more than three " + failure + " results");
                        before = handler.Calls;
                        controller.ApplySnapshot(track.Copy(), DateTime.UtcNow.AddDays(1));
                        Check(handler.Calls == before, "a recovered timed song stops automatic retry requests");
                    }
                }

                foreach (RetryReply initial in new[] { RetryReply.EmptyLyrics, RetryReply.PlainLyrics })
                {
                    var store = new SettingsStore(Path.Combine(directory, initial.ToString()));
                    var handler = new RetryHandler { Reply = initial };
                    using (var repository = new LyricRepository(store.DataDirectory, handler))
                    using (var controller = new LyricController(new AppSettings(), store, repository))
                    {
                        var track = RetryTrack();
                        controller.ApplySnapshot(track, DateTime.UtcNow);
                        await RetryIdle(controller).ConfigureAwait(false);
                        Check(controller.Document != null && !controller.Document.HasTimedLyrics,
                            "an automatic " + initial + " response is retained as an unfinished document");
                        if (initial == RetryReply.PlainLyrics)
                            Check(repository.GetCacheInfo().LyricCount == 1, "untimed provider text is present in the persistent cache");
                        int before = handler.LyricCalls;
                        handler.Reply = RetryReply.TimedLyrics;
                        await RetryWhenDue(controller, track).ConfigureAwait(false);
                        Check(handler.LyricCalls > before && controller.Document.HasTimedLyrics && controller.Current == "Recovered first",
                            "an automatic " + initial + " document does not block a fresh remote timeline");
                    }
                }

                // Seed a real persistent untimed cache, then restart both the
                // repository and controller before the platform supplies a timeline.
                var cachedStore = new SettingsStore(Path.Combine(directory, "restart"));
                using (var seed = new LyricRepository(cachedStore.DataDirectory, new RetryHandler { Reply = RetryReply.PlainLyrics }))
                    await seed.FetchAsync(new LyricSearchResult { Player = MusicPlayer.NetEase, Id = "9876501",
                        Title = "Retry fixture", Artist = "Retry artist", Album = "Retry album", DurationSeconds = 10 }, CancellationToken.None).ConfigureAwait(false);
                var restarted = new RetryHandler { Reply = RetryReply.TimedLyrics };
                using (var repository = new LyricRepository(cachedStore.DataDirectory, restarted))
                using (var controller = new LyricController(new AppSettings(), cachedStore, repository))
                {
                    var track = RetryTrack(); track.PlatformTrackId = "9876501";
                    controller.ApplySnapshot(track, DateTime.UtcNow);
                    await RetryIdle(controller).ConfigureAwait(false);
                    Check(restarted.LyricCalls == 1 && controller.Document.HasTimedLyrics && controller.Current == "Recovered first",
                        "a persistent untimed native-ID cache is refreshed after application restart");
                }

                var gatedStore = new SettingsStore(Path.Combine(directory, "gates"));
                var gatedHandler = new RetryHandler { Reply = RetryReply.NoMatch };
                var settings = new AppSettings();
                using (var repository = new LyricRepository(gatedStore.DataDirectory, gatedHandler))
                using (var controller = new LyricController(settings, gatedStore, repository))
                {
                    var track = RetryTrack();
                    controller.ApplySnapshot(track, DateTime.UtcNow);
                    await RetryIdle(controller).ConfigureAwait(false);
                    DateTime due = controller.NextLyricRetryUtc.AddSeconds(1);
                    int before = gatedHandler.Calls;
                    var paused = track.Copy(); paused.IsPlaying = false;
                    controller.ApplySnapshot(paused, due);
                    Check(gatedHandler.Calls == before, "an overdue retry sends no request while playback is paused");
                    settings.OnlineLyrics = false;
                    controller.SettingsChanged(false);
                    controller.ApplySnapshot(track.Copy(), due);
                    Check(gatedHandler.Calls == before, "an overdue retry sends no request while online lyrics are disabled");
                    settings.OnlineLyrics = true;
                    gatedHandler.Reply = RetryReply.TimedLyrics;
                    controller.ApplySnapshot(track.Copy(), due);
                    await RetryIdle(controller).ConfigureAwait(false);
                    Check(gatedHandler.Calls > before && controller.Document.HasTimedLyrics,
                        "resuming enabled online playback recovers the unfinished same song");
                    var manual = LrcParser.Parse("User-selected untimed words"); manual.Source = "Manual fixture";
                    controller.ApplyDocument(manual);
                    before = gatedHandler.Calls;
                    controller.ApplySnapshot(track.Copy(), DateTime.UtcNow.AddDays(1));
                    Check(gatedHandler.Calls == before && ReferenceEquals(controller.Document, manual),
                        "a user-selected untimed document is never overwritten by automatic retries");
                }
                var manualHandler = new RetryHandler { Reply = RetryReply.TimedLyrics };
                using (var repository = new LyricRepository(gatedStore.DataDirectory, manualHandler))
                using (var controller = new LyricController(new AppSettings(), gatedStore, repository))
                {
                    controller.ApplySnapshot(RetryTrack(), DateTime.UtcNow);
                    controller.ApplySnapshot(RetryTrack(), DateTime.UtcNow.AddDays(1));
                    Check(manualHandler.Calls == 0 && controller.Document != null && controller.Document.PlainText == "User-selected untimed words",
                        "a saved manual untimed binding remains authoritative after restart");
                }

                var clearStore = new SettingsStore(Path.Combine(directory, "clear"));
                var clearHandler = new RetryHandler { Reply = RetryReply.EmptyLyrics };
                using (var repository = new LyricRepository(clearStore.DataDirectory, clearHandler))
                using (var controller = new LyricController(new AppSettings(), clearStore, repository))
                {
                    var track = RetryTrack();
                    controller.ApplySnapshot(track, DateTime.UtcNow);
                    await RetryIdle(controller).ConfigureAwait(false);
                    controller.ClearLyricCache();
                    clearHandler.Reply = RetryReply.TimedLyrics;
                    await RetryWhenDue(controller, track).ConfigureAwait(false);
                    Check(controller.Document.HasTimedLyrics && controller.Current == "Recovered first",
                        "clearing cache while lyrics are unfinished does not strand the same song");
                }
            }
            finally
            {
                string target = Path.GetFullPath(directory);
                if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Invalid retry check cleanup path.");
                if (Directory.Exists(target)) Directory.Delete(target, true);
            }
        }

        // Small public-entry fixtures for provider isolation. Every response is
        // produced in memory; a request to the other platform is counted even if
        // the repository catches its error or returns a useful document later.
        private sealed class PlatformHandler : HttpMessageHandler
        {
            internal const string Title = "独立样例 (副标题)", Artist = "Fixture artist", Album = "Fixture album";
            internal int QQCalls, NetEaseCalls, FullTitleQueries, BareTitleQueries, DetailCalls, NativeCalls;
            internal bool NoQQMatch, WrongQQAlbum, RejectRequests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                bool netEase = request.RequestUri.Host == "music.163.com";
                if (netEase) Interlocked.Increment(ref NetEaseCalls);
                else if (request.RequestUri.Host == "c.y.qq.com" || request.RequestUri.Host == "u.y.qq.com") Interlocked.Increment(ref QQCalls);
                else throw new InvalidOperationException("Unexpected isolation fixture host.");
                if (RejectRequests) throw new InvalidOperationException("Offline isolation fixture received a request.");
                string path = request.RequestUri.AbsolutePath, query = Uri.UnescapeDataString(request.RequestUri.Query), body;
                var json = new JavaScriptSerializer();
                if (netEase && path == "/api/search/get/web")
                    body = json.Serialize(new { code = 200, result = new { songs = new[] { new { id = 7000123, name = Title,
                        duration = 20000, album = new { name = Album }, artists = new[] { new { name = Artist } } } } } });
                else if (netEase && path == "/api/song/lyric")
                    body = json.Serialize(new { code = 200, lrc = new { lyric = "[00:01]NetEase original\n[00:05]NetEase next" },
                        tlyric = new { lyric = "[00:01]网易译文\n[00:05]网易下一行" } });
                else if (!netEase && query.Contains("DoSearchForQQMusicDesktop"))
                    body = "{\"code\":0,\"request\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":[]}}}}}";
                else if (!netEase && path == "/soso/fcgi-bin/client_search_cp")
                    body = "{\"code\":0,\"data\":{\"song\":{\"list\":[]}}}";
                else if (!netEase && path == "/splcloud/fcgi-bin/smartbox_new.fcg")
                {
                    string key = query.Substring(query.IndexOf("key=", StringComparison.Ordinal) + 4);
                    bool complete = key == Title;
                    if (complete) Interlocked.Increment(ref FullTitleQueries);
                    else Interlocked.Increment(ref BareTitleQueries);
                    object[] hints = NoQQMatch ? new object[0] : new object[] { new {
                        name = complete ? Title : "独立样例", singer = complete ? Artist : "Different artist", mid = "PlatformFixture" } };
                    body = json.Serialize(new { code = 0, data = new { song = new { itemlist = hints } } });
                }
                else if (!netEase && query.Contains("get_song_detail_yqq"))
                {
                    Interlocked.Increment(ref DetailCalls);
                    body = json.Serialize(new { code = 0, catalog = new { code = 0, data = new { track_info = new {
                        mid = "PlatformFixture", title = Title, interval = 20,
                        album = new { title = WrongQQAlbum ? "Different album" : Album },
                        singer = new[] { new { name = Artist, mid = "PlatformSinger" } } } } } });
                }
                else if (!netEase && query.Contains("GetAlbumList"))
                    body = "{\"code\":0,\"catalog\":{\"code\":0,\"data\":{\"albumList\":[]}}}";
                else if (!netEase && path == "/lyric/fcgi-bin/fcg_query_lyric_new.fcg")
                    body = "{\"code\":0,\"lyric\":\"\",\"trans\":\"\"}";
                else if (!netEase && query.Contains("GetPlayLyricInfo"))
                {
                    Interlocked.Increment(ref NativeCalls);
                    body = json.Serialize(new { code = 0, lyrics = new { code = 0, data = new {
                        lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]QQ original\n[00:05]QQ next")),
                        trans = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]QQ译文\n[00:05]QQ下一行")) } } });
                }
                else throw new InvalidOperationException("Unexpected isolation fixture route: " + path);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }

        private static MusicSnapshot PlatformTrack(MusicPlayer player)
        {
            return new MusicSnapshot { Player = player, Title = PlatformHandler.Title, Artist = PlatformHandler.Artist,
                Album = PlatformHandler.Album, DurationSeconds = 20, HasTimeline = true, IsPlaying = true,
                PositionSeconds = 2, InterpolateTimeline = false };
        }

        private static async Task PlatformIsolationChecks()
        {
            string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            string directory = Path.Combine(root, "p-" + Guid.NewGuid().ToString("N"));
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                CancellationToken token = cancellation.Token;
                try
                {
                    var qqTrack = PlatformTrack(MusicPlayer.QQMusic);
                    string qqDirectory = Path.Combine(directory, "qq");
                    var qqHandler = new PlatformHandler();
                    using (var repository = new LyricRepository(qqDirectory, qqHandler, true))
                    {
                        LyricDocument document = await repository.FindAsync(qqTrack, token).ConfigureAwait(false);
                        Check(document != null && document.HasTranslation && document.Lines[0].Text == "QQ original" &&
                            LrcParser.GetTranslationForLine(document, 0) == "QQ译文", "QQ native recording returns its original and translation together");
                        Check(qqHandler.FullTitleQueries == 1 && qqHandler.BareTitleQueries == 0 && qqHandler.DetailCalls == 1,
                            "complete parenthesized title discovers and verifies its own native record before bare suggestions");
                        Check(qqHandler.NativeCalls == 1 && qqHandler.NetEaseCalls == 0,
                            "QQ automatic lookup uses only QQ search, catalog and lyrics");
                    }
                    var offline = new PlatformHandler { RejectRequests = true };
                    using (var repository = new LyricRepository(qqDirectory, offline, true))
                    {
                        LyricDocument cached = repository.FindCached(qqTrack);
                        LyricDocument onlineCache = await repository.FindAsync(qqTrack, token).ConfigureAwait(false);
                        Check(cached != null && onlineCache != null && onlineCache.HasTranslation &&
                            onlineCache.Lines[0].Text == "QQ original" && LrcParser.GetTranslationForLine(onlineCache, 0) == "QQ译文" &&
                            offline.QQCalls == 0 && offline.NetEaseCalls == 0, "QQ original, translation and native binding survive an offline restart");
                    }
                    var netEaseHandler = new PlatformHandler();
                    using (var repository = new LyricRepository(Path.Combine(directory, "netease"), netEaseHandler, true))
                    {
                        LyricDocument document = await repository.FindAsync(PlatformTrack(MusicPlayer.NetEase), token).ConfigureAwait(false);
                        Check(document != null && document.Lines[0].Text == "NetEase original" && document.HasTranslation &&
                            netEaseHandler.NetEaseCalls == 2 && netEaseHandler.QQCalls == 0, "NetEase automatic lookup uses only NetEase search and lyrics");
                    }
                    var wrongAlbum = new PlatformHandler { WrongQQAlbum = true };
                    using (var repository = new LyricRepository(Path.Combine(directory, "wrong"), wrongAlbum, true))
                    {
                        LyricDocument document = await repository.FindAsync(qqTrack, token).ConfigureAwait(false);
                        Check(document == null && wrongAlbum.DetailCalls == 1 && wrongAlbum.NativeCalls == 0 && wrongAlbum.NetEaseCalls == 0,
                            "a same-title QQ record on another album cannot authorize lyric retrieval or another provider");
                    }
                    // Represent an old release's cross-provider binding on disk.
                    // Seed the lyric through its own provider, then write only the
                    // historical match format so the production reader must reject it.
                    string legacyDirectory = Path.Combine(directory, "legacy");
                    using (var seed = new LyricRepository(legacyDirectory, new PlatformHandler()))
                        await seed.FetchAsync(new LyricSearchResult { Player = MusicPlayer.NetEase, Id = "7000123",
                            Title = PlatformHandler.Title, Artist = PlatformHandler.Artist, Album = PlatformHandler.Album,
                            DurationSeconds = 20 }, token).ConfigureAwait(false);
                    string matchDirectory = Path.Combine(legacyDirectory, "lyric-match-v1");
                    Directory.CreateDirectory(matchDirectory);
                    string matchName;
                    using (var hash = System.Security.Cryptography.SHA256.Create())
                        matchName = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(qqTrack.TrackKey))).Replace("-", "").ToLowerInvariant() + ".xml";
                    using (var writer = XmlWriter.Create(Path.Combine(matchDirectory, matchName), new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                    {
                        writer.WriteStartElement("match"); writer.WriteAttributeString("version", "1"); writer.WriteAttributeString("trackKey", qqTrack.TrackKey);
                        writer.WriteAttributeString("observedDuration", "20"); writer.WriteAttributeString("player", "NetEase"); writer.WriteAttributeString("id", "7000123");
                        writer.WriteAttributeString("title", PlatformHandler.Title); writer.WriteAttributeString("artist", PlatformHandler.Artist);
                        writer.WriteAttributeString("album", PlatformHandler.Album); writer.WriteAttributeString("duration", "20"); writer.WriteEndElement();
                    }
                    var noMatch = new PlatformHandler { NoQQMatch = true };
                    using (var repository = new LyricRepository(legacyDirectory, noMatch, true))
                    {
                        Check(repository.FindCached(qqTrack) == null, "an old QQ-to-NetEase cache binding is ignored offline");
                        LyricDocument document = await repository.FindAsync(qqTrack, token).ConfigureAwait(false);
                        Check(document == null && noMatch.QQCalls > 0 && noMatch.NetEaseCalls == 0 && noMatch.NativeCalls == 0,
                            "QQ with no matching native record keeps searching QQ without using an old cross-provider cache or NetEase requests");
                    }
                }
                finally
                {
                    cancellation.Cancel();
                    string target = Path.GetFullPath(directory);
                    if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Invalid isolation fixture cleanup path.");
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                }
            }
        }

        private sealed class CacheHandler : HttpMessageHandler
        {
            internal int Calls;
            internal bool RejectRequests, BlockLyrics;
            internal readonly TaskCompletionSource<bool> LyricsStarted = new TaskCompletionSource<bool>();
            internal readonly TaskCompletionSource<bool> ReleaseLyrics = new TaskCompletionSource<bool>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Interlocked.Increment(ref Calls);
                if (RejectRequests) throw new InvalidOperationException("Offline cache attempted a network request.");
                string query = Uri.UnescapeDataString(request.RequestUri.Query), body;
                if (query.Contains("DoSearchForQQMusicDesktop"))
                    body = "{\"code\":0,\"request\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":[{\"mid\":\"PersistentFixture\",\"title\":\"Cache fixture\",\"interval\":10,\"album\":{\"title\":\"Cache album\"},\"singer\":[{\"name\":\"Cache artist\"}]}]}}}}}";
                else if (request.RequestUri.Host == "music.163.com" && request.RequestUri.AbsolutePath == "/api/search/get/web")
                    body = "{\"code\":200,\"result\":{\"songs\":[{\"id\":123455,\"name\":\"Cache fixture\",\"duration\":10000,\"album\":{\"name\":\"Cache album\"},\"artists\":[{\"name\":\"Cache artist\"}]}]}}";
                else
                {
                    bool netEaseLyric = request.RequestUri.Host == "music.163.com" && request.RequestUri.AbsolutePath == "/api/song/lyric";
                    bool qqOrdinaryLyric = request.RequestUri.Host == "c.y.qq.com" && request.RequestUri.AbsolutePath == "/lyric/fcgi-bin/fcg_query_lyric_new.fcg";
                    bool qqWordLyric = request.RequestUri.Host == "u.y.qq.com" && query.Contains("GetPlayLyricInfo");
                    if (!netEaseLyric && !qqOrdinaryLyric && !qqWordLyric)
                        throw new InvalidOperationException("Unexpected cache fixture request: " + request.RequestUri.AbsolutePath);
                    if (BlockLyrics)
                    {
                        LyricsStarted.TrySetResult(true);
                        var cancelled = new TaskCompletionSource<bool>();
                        using (token.Register(delegate { cancelled.TrySetCanceled(); }))
                        {
                            await Task.WhenAny(ReleaseLyrics.Task, cancelled.Task).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                        }
                    }
                    if (netEaseLyric)
                        body = "{\"code\":200,\"lrc\":{\"lyric\":\"[00:01]First line\\n[00:05]Next line\"},\"tlyric\":{\"lyric\":\"[00:01]第一行\\n[00:05]下一行\"}}";
                    else if (qqWordLyric)
                        body = new JavaScriptSerializer().Serialize(new { code = 0, lyrics = new { code = 0, data = new {
                            lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes("[1000,3000]First line(1000,3000)\n[5000,3000]Next line(5000,3000)")),
                            trans = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]第一行\n[00:05]下一行")) } } });
                    else body = new JavaScriptSerializer().Serialize(new { code = 0,
                        lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]First line\n[00:05]Next line")),
                        trans = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]第一行\n[00:05]下一行")) });
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }

        private static MusicSnapshot CacheTrack()
        {
            // Cache persistence is independent of QQ's short provider-race deadline.
            // Use the unbounded ordinary match path with an entirely local handler.
            return new MusicSnapshot { Player = MusicPlayer.NetEase, Title = "Cache fixture", Artist = "Cache artist",
                Album = "Cache album", DurationSeconds = 10, HasTimeline = true, IsPlaying = true,
                PositionSeconds = 2, InterpolateTimeline = false };
        }

        private static async Task PersistentCacheChecks()
        {
            string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            // Leave room for atomic-write suffixes under Framework MAX_PATH
            // even when a release checkout has a longer directory name.
            string directory = Path.Combine(root, "p-" + Guid.NewGuid().ToString("N"));
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            CancellationToken token = cancellation.Token;
            try
            {
                var store = new SettingsStore(directory); var track = CacheTrack();
                var seed = new CacheHandler();
                using (var repository = new LyricRepository(store.DataDirectory, seed))
                {
                    var document = await repository.FindAsync(track, token).ConfigureAwait(false);
                    Check(document.HasTimedLyrics && document.HasTranslation, "successful original and translation are stored before exit");
                    await repository.FetchAsync(new LyricSearchResult { Player = MusicPlayer.NetEase, Id = "123456",
                        Title = "Exact cache", Artist = "Cache artist", Album = "Cache album", DurationSeconds = 10 }, token).ConfigureAwait(false);
                }
                // Keep QQ persistence coverage without involving automatic lookup's
                // production wall-clock budget in this filesystem/cache check.
                var qqTrack = new LyricSearchResult { Player = MusicPlayer.QQMusic, Id = "PersistentFixture",
                    Title = "Cache fixture", Artist = "Cache artist", Album = "Cache album", DurationSeconds = 10 };
                string qqDirectory = Path.Combine(directory, "qq");
                using (var repository = new LyricRepository(qqDirectory, new CacheHandler()))
                {
                    var document = await repository.FetchAsync(qqTrack, token).ConfigureAwait(false);
                    Check(document.HasTimedLyrics && document.HasTranslation, "QQ cache is seeded through its explicit lyric endpoint");
                }
                var qqOffline = new CacheHandler { RejectRequests = true };
                using (var repository = new LyricRepository(qqDirectory, qqOffline))
                {
                    var document = await repository.FetchAsync(qqTrack, token).ConfigureAwait(false);
                    Check(document.HasTimedLyrics && document.HasTranslation && document.Source.Contains("缓存") && qqOffline.Calls == 0,
                        "QQ original and translation survive restart without a network request");
                }
                foreach (string folder in new[] { "lyric-cache-v1", "lyric-match-v1" })
                    foreach (string file in Directory.GetFiles(Path.Combine(store.DataDirectory, folder), "*.xml"))
                    {
                        if (folder == "lyric-cache-v1")
                        {
                            var xml = new XmlDocument { XmlResolver = null }; xml.Load(file);
                            xml.DocumentElement.SetAttribute("translationCheckedUtc", "2000-01-01T00:00:00Z");
                            xml.DocumentElement.SelectSingleNode("wordTimingChecked").InnerText = "false"; xml.Save(file);
                        }
                        File.SetLastWriteTimeUtc(file, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                    }
                var offline = new CacheHandler { RejectRequests = true };
                using (var repository = new LyricRepository(store.DataDirectory, offline, true))
                {
                    var cached = repository.FindCached(track);
                    Check(cached != null && cached.HasTimedLyrics && cached.HasTranslation && cached.Source.Contains("缓存") && offline.Calls == 0,
                        "cache and recording binding survive restart and age without any enrichment request");
                    var exact = repository.FindCached(new MusicSnapshot { Player = MusicPlayer.NetEase, Title = "Exact cache", PlatformTrackId = "123456" });
                    Check(exact != null && exact.HasTimedLyrics && offline.Calls == 0, "native NetEase ID reads persistent lyrics entirely offline");
                    foreach (int mismatch in new[] { 0, 1, 2 })
                    {
                        var changed = CacheTrack();
                        if (mismatch == 0) changed.DurationSeconds = 30;
                        else if (mismatch == 1) changed.Album = "Different album";
                        else changed.Artist = "Different artist";
                        Check(repository.FindCached(changed) == null, "persistent cache still rejects a different recording identity " + mismatch);
                    }
                    var settings = new AppSettings { OnlineLyrics = false, HideInstrumental = false };
                    using (var controller = new LyricController(settings, store, repository))
                    {
                        var incomplete = CacheTrack(); incomplete.DurationSeconds = 0; incomplete.HasTimeline = false;
                        controller.ApplySnapshot(incomplete, DateTime.UtcNow);
                        Check(controller.Document == null && offline.Calls == 0, "incomplete offline metadata does not guess a cached recording");
                        controller.ApplySnapshot(track, DateTime.UtcNow);
                        Check(controller.Current == "First line" && controller.CurrentTranslation == "第一行" && offline.Calls == 0,
                            "late reliable timeline enables cached original and translation while online lyrics are disabled");
                        store.Save(settings); store.SaveOverride("preserved-manual", LrcParser.Parse("[00:01]Imported lyric"));
                        string lyricDirectory = Path.Combine(store.DataDirectory, "lyric-cache-v1");
                        File.WriteAllText(Path.Combine(lyricDirectory, "unfinished.tmp"), "pending");
                        File.WriteAllText(Path.Combine(lyricDirectory, "keep.txt"), "unrelated");
                        Directory.CreateDirectory(Path.Combine(lyricDirectory, "nested"));
                        File.WriteAllText(Path.Combine(lyricDirectory, "nested", "keep.xml"), "nested unrelated file");
                        var info = repository.GetCacheInfo();
                        Check(info.LyricCount == 2 && info.MatchCount == 1 && info.TotalBytes > 0, "cache stats count stored lyrics, recording bindings and byte size");
                        var visible = controller.Document; string current = controller.Current;
                        var cleared = controller.ClearLyricCache();
                        Check(cleared.DeletedFiles == 4 && cleared.FailedFiles == 0 && repository.GetCacheInfo().LyricCount == 0 && repository.GetCacheInfo().MatchCount == 0,
                            "manual clear removes only cached XML and unfinished cache files");
                        Check(ReferenceEquals(controller.Document, visible) && controller.Current == current && controller.CurrentTranslation == "第一行",
                            "clearing disk cache preserves the current on-screen original and translation");
                        Check(store.LoadOverride("preserved-manual") != null && File.Exists(Path.Combine(store.DataDirectory, "settings.json")) &&
                            File.Exists(Path.Combine(lyricDirectory, "keep.txt")) && File.Exists(Path.Combine(lyricDirectory, "nested", "keep.xml")),
                            "clear preserves settings, manually saved/imported lyrics, unrelated files and nested folders");
                        var manual = LrcParser.Parse("[00:01]Manual first\n[00:05]Manual next");
                        controller.ApplyDocument(manual); controller.SeekManual(2); controller.ToggleManual();
                        controller.ClearLyricCache();
                        Check(controller.Document == manual && controller.ManualMode && controller.ManualPlaying && controller.Current == "Manual first",
                            "cache clear preserves a manually chosen document and its playback clock");
                    }
                }
                var blocked = new CacheHandler { BlockLyrics = true };
                using (var repository = new LyricRepository(Path.Combine(directory, "inflight"), blocked))
                {
                    var pending = repository.FindAsync(CacheTrack(), token);
                    Check(await Task.WhenAny(blocked.LyricsStarted.Task, Task.Delay(10000, token)).ConfigureAwait(false) == blocked.LyricsStarted.Task,
                        "in-flight cache fixture reaches the lyric request");
                    repository.ClearCache(); blocked.ReleaseLyrics.TrySetResult(true);
                    var document = await pending.ConfigureAwait(false);
                    Check(document.HasTimedLyrics && repository.GetCacheInfo().LyricCount == 0 && repository.GetCacheInfo().MatchCount == 0,
                        "a fetch begun before manual clear cannot recreate either lyric files or recording bindings");
                    blocked.BlockLyrics = false;
                    document = await repository.FindAsync(CacheTrack(), token).ConfigureAwait(false);
                    Check(document.HasTimedLyrics && repository.GetCacheInfo().LyricCount == 1 && repository.GetCacheInfo().MatchCount == 1,
                        "new online requests can cache normally after a clear");
                    string locked = Path.Combine(directory, "inflight", "lyric-cache-v1", "locked.xml");
                    File.WriteAllText(locked, "locked");
                    using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        var result = repository.ClearCache();
                        Check(result.FailedFiles == 1 && result.DeletedFiles == 2, "clear reports locked files while removing other cache entries");
                    }
                }
            }
            finally
            {
                cancellation.Cancel(); cancellation.Dispose();
                string target = Path.GetFullPath(directory);
                if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Invalid cache test cleanup path.");
                if (Directory.Exists(target)) Directory.Delete(target, true);
            }
        }
    }
}
