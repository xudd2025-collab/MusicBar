using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace MusicBar
{
    internal static class OverlayChecks
    {
        private static int _checks;

        [STAThread]
        public static int Main(string[] arguments)
        {
            try
            {
                bool renderOnly = Array.IndexOf(arguments, "--render-only") >= 0;
                Run(!renderOnly);
                Console.WriteLine("Overlay geometry / alpha rendering: " + _checks + " checks passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 1;
            }
        }

        internal static void Run(bool includeMouseChecks = true)
        {
            AppSettings settings = new AppSettings();
            TaskbarSnapshot bar = BottomBar(1920, 1080, 48, 1);
            List<Rectangle> controls = new List<Rectangle>
            {
                new Rectangle(0, 1032, 180, 48),
                new Rectangle(700, 1032, 550, 48),
                new Rectangle(1600, 1032, 320, 48)
            };
            OverlayLayout left = TaskbarGeometry.Calculate(bar, settings, controls);
            Check(left.Bounds.X == 188 && left.Bounds.Width == 420, "Left lyrics must begin after weather and keep the requested width.");
            CheckSafe(bar, left, controls);
            settings.Alignment = "right";
            OverlayLayout right = TaskbarGeometry.Calculate(bar, settings, controls);
            Check(right.Bounds.X == 1258 && right.Bounds.Width == 334, "Right lyrics must use the right free gap and shrink before the tray.");
            CheckSafe(bar, right, controls);
            settings.Alignment = "center";
            CheckSafe(bar, TaskbarGeometry.Calculate(bar, settings, controls), controls);
            settings.Alignment = "left";
            settings.HorizontalOffset = 4000;
            OverlayLayout shifted = TaskbarGeometry.Calculate(bar, settings, controls);
            Check(shifted.Bounds.X == 1512 && shifted.Bounds.Width == 80 && shifted.PositionClamped, "A large offset must reach the last safe gap rather than remain locked in the first.");
            CheckSafe(bar, shifted, controls);
            VerifyDirectionalOffsets(bar, controls);

            settings.HorizontalOffset = 0;
            TaskbarSnapshot narrow = BottomBar(1280, 720, 48, 1);
            List<Rectangle> crowded = new List<Rectangle>
            {
                new Rectangle(0, 672, 180, 48),
                new Rectangle(420, 672, 600, 48),
                new Rectangle(1050, 672, 230, 48)
            };
            OverlayLayout small = TaskbarGeometry.Calculate(narrow, settings, crowded);
            Check(small.Bounds.Width == 224, "A narrow screen should shorten lyrics within the remaining gap.");
            CheckSafe(narrow, small, crowded);
            Check(TaskbarGeometry.Calculate(narrow, settings, new List<Rectangle> { narrow.Bounds }).Bounds.IsEmpty, "A full taskbar must hide the lyrics.");

            TaskbarSnapshot scaled = BottomBar(2880, 1620, 72, 1.5f);
            List<Rectangle> scaledControls = new List<Rectangle>
            {
                new Rectangle(0, 1548, 270, 72),
                new Rectangle(1050, 1548, 825, 72),
                new Rectangle(2400, 1548, 480, 72)
            };
            OverlayLayout dpi = TaskbarGeometry.Calculate(scaled, settings, scaledControls);
            Check(dpi.Bounds.X == 282 && dpi.Bounds.Width == 630 && dpi.Bounds.Height == 72, "150% DPI should scale dimensions in physical pixels.");
            CheckSafe(scaled, dpi, scaledControls);

            TaskbarSnapshot secondary = BottomBar(1920, 1080, 48, 1);
            secondary.Bounds.Offset(-1920, -300);
            secondary.MonitorBounds.Offset(-1920, -300);
            secondary.WorkingArea.Offset(-1920, -300);
            List<Rectangle> secondaryControls = new List<Rectangle>();
            foreach (Rectangle rectangle in controls) { Rectangle moved = rectangle; moved.Offset(-1920, -300); secondaryControls.Add(moved); }
            CheckSafe(secondary, TaskbarGeometry.Calculate(secondary, settings, secondaryControls), secondaryControls);

            TaskbarSnapshot vertical = new TaskbarSnapshot { Bounds = new Rectangle(0, 0, 48, 1080), MonitorBounds = new Rectangle(0, 0, 1920, 1080), WorkingArea = new Rectangle(48, 0, 1872, 1080), Scale = 1, Horizontal = false, Edge = 0 };
            List<Rectangle> verticalControls = new List<Rectangle> { new Rectangle(0, 0, 48, 96), new Rectangle(0, 850, 48, 230) };
            OverlayLayout turned = TaskbarGeometry.Calculate(vertical, settings, verticalControls);
            Check(turned.Rotate && turned.Bounds.Width == 48 && turned.Bounds.Height == 420, "A vertical taskbar must rotate text within its width.");
            CheckSafe(vertical, turned, verticalControls);

            settings.FollowTaskbar = false;
            settings.VerticalOffset = 2000;
            OverlayLayout floating = TaskbarGeometry.Calculate(bar, settings, controls);
            Check(floating.Bounds.Bottom <= bar.Bounds.Top && bar.MonitorBounds.Contains(floating.Bounds), "The explicit floating mode must remain above a bottom taskbar.");
            VerifyPreview();
            VerifyScrollState();
            VerifyLongLineRendering();
            if (includeMouseChecks)
            {
                VerifyAnimationScheduler();
                VerifyMouseGestures();
                VerifyNativeCursorPolling();
            }
            else Console.WriteLine("Native mouse checks omitted by --render-only.");
        }

        private static void VerifyScrollState()
        {
            LyricScrollState state = new LyricScrollState();
            state.Reset(0);
            state.Configure(200, 40, 0);
            state.Advance(.49, double.NaN);
            Check(state.Offset == 0 && !state.IsMoving, "A long line must hold its beginning for half a second.");
            state.Advance(3, double.NaN);
            Check(Math.Abs(state.Offset - 100) < .01 && state.IsMoving, "Unknown-duration lyrics must move smoothly at the default speed.");
            double paused = state.Offset;
            state.Advance(3, double.NaN);
            Check(state.Offset == paused, "Frozen playback time must freeze the scroll position.");
            state.Advance(5.5, double.NaN);
            Check(Math.Abs(state.Offset - 200) < .01 && !state.IsMoving, "The first traversal must reveal the complete tail.");
            state.Configure(200, 40, 5.5);
            state.Advance(6.5, double.NaN);
            Check(state.Offset == 200, "Reapplying identical geometry must not restart the final hold.");
            state.Advance(6.71, double.NaN);
            Check(state.Offset == 200 && !state.IsMoving && double.IsPositiveInfinity(state.NextWakeSeconds),
                "A completed line must stay at its ending without scheduling another traversal.");
            state.Advance(3600, double.NaN);
            Check(state.Offset == 200 && !state.IsMoving, "Even a long delayed tick must not repeat a completed line.");
            state.Reset(7);
            Check(state.Offset == 0 && state.LastTime == 7, "A new lyric must reset to its beginning.");
            state.Configure(400, 40, 7);
            state.Advance(9.8, 11);
            Check(Math.Abs(state.Offset - 400) < .01, "Timed lyrics must reach the tail early enough for the final 1.2-second hold.");
            state.Advance(10.9, 11);
            Check(state.Offset == 400, "The accelerated traversal must still keep the full final hold.");
            state.Configure(500, 40, 10.9);
            Check(state.Offset == 500, "Resizing at the tail must keep the final words visible.");
            state.Configure(0, 40, 10.9);
            Check(state.Offset == 0 && double.IsInfinity(state.NextWakeSeconds), "Short lines must require no animation wakeups.");
            state.Reset(0);
            state.Configure(200, 40, 0);
            state.Advance(1, 10);
            Check(Math.Abs(state.Offset - 20) < .01, "An ordinary line must begin scrolling at its chosen speed.");
            state.Advance(1.5, 5);
            Check(Math.Abs(state.Offset - 40) < .01, "A corrected player deadline must not accelerate a sentence already moving.");
            state.Advance(2, 3);
            Check(Math.Abs(state.Offset - 60) < .01, "Repeated deadline corrections must not accumulate faster scrolling.");
            state.Reset(0);
            state.Configure(1000, 40, 0);
            for (double tick = .033; tick < .67; tick += .033) state.Advance(tick, 1);
            Check(Math.Abs(state.Offset - 1000) < .01, "Very short timed lyrics must reach the tail rather than asymptotically slow down before it.");
            state.Reset(0);
            state.Configure(1000, 40, 0);
            state.UpdateDeadline(.2);
            Check(state.NextWakeSeconds < .1, "Late-arriving lyrics must shorten their initial hold before the first timer wakeup.");
            for (double tick = .033; tick < .14; tick += .033) state.Advance(tick, .2);
            Check(Math.Abs(state.Offset - 1000) < .01, "A lyric with less than half a second remaining must still reveal its complete tail in time.");
        }

        private static void VerifyAnimationScheduler()
        {
            IntPtr foreground = OverlayNative.GetForegroundWindow();
            using (LyricOverlay overlay = new LyricOverlay())
            {
                AppSettings settings = new AppSettings { OverlayEnabled = false, FontSize = 18 };
                overlay.UpdateSettings(settings);
                overlay.SetLineTiming("timer|0", 1.6, true);
                const string lyric = "真实计时滚动 · 这是一句足够长的歌词，开头、中间和最后的文字都需要显示出来 · 完整结尾";
                overlay.SetLyrics(lyric, "", true);
                TaskbarSnapshot bar = OffscreenBar(true);
                List<Rectangle> controls = OffscreenControls(bar);
                typeof(LyricOverlay).GetMethod("ApplyLayout", InstancePrivate).Invoke(overlay,
                    new object[] { bar, controls, TaskbarGeometry.Calculate(bar, settings, controls) });
                ((Timer)PrivateField(overlay, "_layoutTimer")).Stop();
                Timer timer = (Timer)PrivateField(overlay, "_animationTimer");
                int ticks = 0;
                timer.Tick += delegate { ticks++; };
                Check(timer.Enabled && timer.Interval >= 400, "A visible long lyric must schedule its initial hold without 30fps repainting.");
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                double nextNotification = 0;
                while (clock.Elapsed.TotalSeconds < 1.05)
                {
                    if (clock.Elapsed.TotalSeconds >= nextNotification)
                    {
                        overlay.SetLineTiming("timer|0", 1.6 - clock.Elapsed.TotalSeconds, true);
                        overlay.SetLyrics(lyric, "", true);
                        overlay.SetPreview(false);
                        nextNotification += .1;
                    }
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(5);
                }
                LyricScrollState state = ScrollFor(overlay, "_currentLine");
                double painted = (double)PrivateField(overlay, "_currentLine").GetType().GetField("PaintedOffset", InstancePrivate).GetValue(PrivateField(overlay, "_currentLine"));
                Check(ticks > 2 && Math.Abs(state.Offset - state.MaximumOffset) < .01 && Math.Abs(painted - state.Offset) < .01,
                    "The real WinForms timer must paint the complete tail despite periodic player notifications.");
                Check(!timer.Enabled, "A completed lyric must stop animation wakeups until the next line.");
                overlay.SetLineTiming("timer|0", double.NaN, true);
                overlay.SetLyrics(lyric, "", true);
                Check(!timer.Enabled && state.Offset == state.MaximumOffset,
                    "Periodic notifications after the line deadline must not restart completed scrolling.");
                overlay.SetLineTiming("timer|0", double.NaN, false);
                Check(!timer.Enabled, "Pausing must stop animation wakeups.");
                overlay.SetLineTiming("timer|0", double.NaN, true);
                Check(!timer.Enabled && state.Offset == state.MaximumOffset, "Resuming a completed lyric must keep its ending.");
                const string translation = "TRANSLATION · Every word of this long translated sentence remains visible until its complete ending · FINAL WORDS";
                overlay.SetLyrics(lyric, "", translation, true);
                typeof(LyricOverlay).GetMethod("ApplyLayout", InstancePrivate).Invoke(overlay,
                    new object[] { bar, controls, TaskbarGeometry.Calculate(bar, settings, controls) });
                ((Timer)PrivateField(overlay, "_layoutTimer")).Stop();
                LyricScrollState second = ScrollFor(overlay, "_secondLine");
                Check(state.Offset == state.MaximumOffset && second.MaximumOffset > 0 && timer.Enabled,
                    "An unfinished translation must keep the shared timer running after the original finishes.");
                second.Advance(second.LastTime + .5 + second.MaximumOffset / 40, double.NaN);
                typeof(LyricOverlay).GetMethod("AnimateOverlay", InstancePrivate).Invoke(overlay, null);
                Check(second.Offset == second.MaximumOffset && !timer.Enabled,
                    "The shared timer must stop once both the original and translation reach their endings.");
                overlay.SetLineTiming("timer|1", 1.6, true);
                Check(state.Offset == 0 && second.Offset == 0, "A new timed occurrence must restart both rows even with identical text.");
                Check(OverlayNative.GetForegroundWindow() == foreground, "Animated drawing must preserve native foreground focus.");
            }
        }

        private static BindingFlags InstancePrivate { get { return BindingFlags.Instance | BindingFlags.NonPublic; } }

        private static object PrivateField(object target, string name)
        {
            return target.GetType().GetField(name, InstancePrivate).GetValue(target);
        }

        private static LyricScrollState ScrollFor(LyricOverlay overlay, string name)
        {
            object drawing = PrivateField(overlay, name);
            return (LyricScrollState)drawing.GetType().GetField("Scroll", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(drawing);
        }

        private static Bitmap CurrentBitmap(LyricOverlay overlay, int width, int height)
        {
            return (Bitmap)typeof(LyricOverlay).GetMethod("DrawLyrics", InstancePrivate).Invoke(overlay, new object[] { width, height, 1f, false, false });
        }

        private static int PixelDifference(Bitmap first, Bitmap second, Rectangle area)
        {
            int changed = 0;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                    if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) changed++;
            return changed;
        }

        private static void CheckMargins(Bitmap bitmap)
        {
            bool clear = true;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if ((x < 4 || x >= bitmap.Width - 4 || y < 2 || y >= bitmap.Height - 2) && bitmap.GetPixel(x, y).A != 0) clear = false;
            Check(clear, "Scrolling glyphs and outlines must stay inside the clipped safe window.");
        }

        private static void SaveDark(Bitmap bitmap, string path)
        {
            using (Bitmap dark = new Bitmap(bitmap.Width, bitmap.Height))
            using (Graphics graphics = Graphics.FromImage(dark))
            {
                graphics.Clear(Color.FromArgb(17, 24, 39));
                graphics.DrawImageUnscaled(bitmap, 0, 0);
                dark.Save(path, ImageFormat.Png);
            }
        }

        private static void VerifyLongLineRendering()
        {
            const string original = "开头 · 风吹过的地方，所有漫长的路都会抵达我们曾经约定的城市，继续听见这首歌最后的声音 · 完整结尾";
            const string translation = "START · When the wind crosses this long road, every word stays with you until the complete ending · FINAL WORDS";
            using (LyricOverlay overlay = new LyricOverlay())
            {
                AppSettings settings = new AppSettings { OverlayEnabled = false, TwoLines = false, ShowTranslation = true, LongLineScroll = true, FontSize = 18 };
                overlay.UpdateSettings(settings);
                overlay.SetLineTiming("track|0", 8, true);
                overlay.SetLyrics(original, "下一句不应代替译文", translation, true);
                using (Bitmap start = CurrentBitmap(overlay, 300, 60))
                {
                    LyricScrollState first = ScrollFor(overlay, "_currentLine"), second = ScrollFor(overlay, "_secondLine");
                    Check(first.MaximumOffset > 200 && second.MaximumOffset > 200, "Long original and translated lyrics must retain their natural width.");
                    Check(first.Offset == 0 && second.Offset == 0, "Both lyric rows must begin at the first word.");
                    Check((string)PrivateField(PrivateField(overlay, "_secondLine"), "_text") == translation, "Translation must display even when next-line display is off.");
                    CheckMargins(start);
                    SaveDark(start, "overlay-scroll-start.png");
                    object path = PrivateField(PrivateField(overlay, "_currentLine"), "_path");
                    double firstStart = first.LastTime, secondStart = second.LastTime;
                    first.Advance(firstStart + .5 + first.MaximumOffset / 80, double.NaN);
                    using (Bitmap middle = CurrentBitmap(overlay, 300, 60))
                    {
                        Check(PixelDifference(start, middle, new Rectangle(0, 0, 300, 30)) > 500, "Advancing a long line must reveal a different middle section.");
                        Check(PixelDifference(start, middle, new Rectangle(0, 30, 300, 30)) == 0, "Original and translated rows must scroll independently.");
                        Check(object.ReferenceEquals(path, PrivateField(PrivateField(overlay, "_currentLine"), "_path")), "Animation frames must reuse the complete glyph path.");
                        CheckMargins(middle);
                        SaveDark(middle, "overlay-scroll-middle.png");
                    }
                    first.Advance(firstStart + .5 + first.MaximumOffset / 40, double.NaN);
                    second.Advance(secondStart + .5 + second.MaximumOffset / 40, double.NaN);
                    using (Bitmap tail = CurrentBitmap(overlay, 300, 60))
                    {
                        Check(Math.Abs(first.Offset - first.MaximumOffset) < .01 && Math.Abs(second.Offset - second.MaximumOffset) < .01, "Both rows must be able to display their complete endings.");
                        Check(PixelDifference(start, tail, new Rectangle(0, 0, 300, 60)) > 1000, "The complete tail must produce a distinct rendered view.");
                        CheckMargins(tail);
                        SaveDark(tail, "overlay-scroll-end.png");
                    }
                    double before = first.Offset, beforeSecond = second.Offset;
                    typeof(LyricOverlay).GetField("_dirty", InstancePrivate).SetValue(overlay, false);
                    overlay.SetLyrics(original, "下一句不应代替译文", translation, true);
                    overlay.SetLineTiming("track|0", 4, true);
                    overlay.SetPreview(false);
                    overlay.UpdateSettings(settings);
                    Check(first.Offset == before && second.Offset == beforeSecond, "Repeated player and settings notifications must preserve scrolling.");
                    Check(!(bool)PrivateField(overlay, "_dirty"), "Unchanged lyrics, preview, and settings must avoid unnecessary repainting.");
                    overlay.SetLineTiming("track|0", 4, false);
                    overlay.SetLyrics(original, "下一句不应代替译文", translation, false);
                    Check(first.Offset == before && second.Offset == beforeSecond, "Hiding paused lyrics must retain each row's completed scroll.");
                    overlay.SetLineTiming("track|0", 4, true);
                    overlay.SetLyrics(original, "下一句不应代替译文", translation, true);
                    Check(first.Offset == before && second.Offset == beforeSecond, "Showing the same lyric after resume must not repeat its traversal.");
                    using (Bitmap preview = overlay.RenderPreview(420, 80)) { }
                    Check(first.Offset == before && second.Offset == beforeSecond, "A settings preview must not change live lyric animation.");
                    settings.HorizontalOffset = 20;
                    using (Bitmap originalBrightness = CurrentBitmap(overlay, 300, 60))
                    {
                        settings.LyricBrightness = 160;
                        overlay.UpdateSettings(settings);
                        Check((bool)PrivateField(overlay, "_dirty"), "A brightness change schedules an immediate repaint.");
                        Check(first.Offset == before && second.Offset == beforeSecond, "A brightness change preserves completed lyric scrolling.");
                        using (Bitmap increasedBrightness = CurrentBitmap(overlay, 300, 60))
                            Check(PixelDifference(originalBrightness, increasedBrightness, new Rectangle(0, 0, 300, 60)) > 100, "Increased brightness changes visible glyph pixels.");
                    }
                    overlay.UpdateSettings(settings);
                    Check(first.Offset == before, "Position changes must not restart a long lyric.");
                    using (Bitmap narrower = CurrentBitmap(overlay, 180, 60))
                        Check(first.Offset > 0, "Shrinking the safe gap during a drag must retain lyric progress.");
                    overlay.SetLineTiming("track|1", 8, true);
                    Check(first.Offset == 0 && second.Offset == 0, "Identical repeated lyric text must reset when its timed line key changes.");
                }

                overlay.SetLyrics(original, "下一句歌词", "", true);
                using (Bitmap oneRow = CurrentBitmap(overlay, 300, 60)) { }
                LyricScrollState current = ScrollFor(overlay, "_currentLine");
                current.Advance(current.LastTime + 1.5, double.NaN);
                overlay.SetLyrics(original, "下一句歌词", translation, true);
                using (Bitmap arrivingTranslation = CurrentBitmap(overlay, 300, 60))
                    Check(current.Offset > 0 && ScrollFor(overlay, "_secondLine").Offset == 0, "A late translation must reset only its row, preserving the original's progress.");
                settings.TwoLines = true;
                overlay.UpdateSettings(settings);
                Check((string)typeof(LyricOverlay).GetMethod("SecondText", InstancePrivate).Invoke(overlay, new object[] { false }) == translation, "Translation must take precedence over next-line display.");
                settings.ShowTranslation = false;
                overlay.UpdateSettings(settings);
                Check((string)typeof(LyricOverlay).GetMethod("SecondText", InstancePrivate).Invoke(overlay, new object[] { false }) == "下一句歌词", "Turning translation off must restore the next line.");
                settings.ShowTranslation = true;
                settings.TwoLines = false;
                settings.FollowTaskbar = false;
                overlay.UpdateSettings(settings);
                AppSettings effective = (AppSettings)typeof(LyricOverlay).GetMethod("GeometrySettings", InstancePrivate).Invoke(overlay, null);
                TaskbarSnapshot bar = BottomBar(1920, 1080, 48, 1);
                Check(effective.TwoLines && TaskbarGeometry.Calculate(bar, effective, new List<Rectangle>()).Bounds.Height > TaskbarGeometry.Calculate(bar, settings, new List<Rectangle>()).Bounds.Height,
                    "Above-taskbar translation must get room for two full-height rows.");

                settings.FollowTaskbar = true;
                settings.LongLineScroll = false;
                overlay.UpdateSettings(settings);
                string veryLong = new string('A', 750) + " COMPLETE END";
                overlay.SetLyrics(veryLong, "", "", true);
                using (Bitmap fitted = CurrentBitmap(overlay, 300, 60))
                {
                    Check((string)PrivateField(overlay, "_current") == veryLong, "Long lyric input must remain complete beyond 700 characters.");
                    Check((string)PrivateField(PrivateField(overlay, "_currentLine"), "_text") == veryLong, "Disabling scrolling must retain the full text without an ellipsis.");
                    SizeF glyphs = (SizeF)PrivateField(PrivateField(overlay, "_currentLine"), "_glyphSize");
                    Check(glyphs.Width < 292 && ScrollFor(overlay, "_currentLine").MaximumOffset == 0, "Static long lines must fit completely inside the available width.");
                    CheckMargins(fitted);
                }
                Check(!((Timer)PrivateField(overlay, "_animationTimer")).Enabled, "Hidden or static lyrics must not keep an animation timer running.");

                settings.LongLineScroll = true;
                overlay.UpdateSettings(settings);
                overlay.SetLineTiming("pause|0", double.NaN, false);
                double frozenTime = (double)typeof(LyricOverlay).GetProperty("AnimationTime", InstancePrivate).GetValue(overlay, null);
                System.Threading.Thread.Sleep(25);
                double laterTime = (double)typeof(LyricOverlay).GetProperty("AnimationTime", InstancePrivate).GetValue(overlay, null);
                Check(frozenTime == laterTime, "Pausing playback must freeze the monotonic animation clock.");
                overlay.SetLineTiming("pause|0", double.NaN, true);
                double resumedTime = (double)typeof(LyricOverlay).GetProperty("AnimationTime", InstancePrivate).GetValue(overlay, null);
                Check(resumedTime - frozenTime < .02, "Resuming must exclude paused wall-clock time from lyric movement.");
            }
        }

        private static void VerifyDirectionalOffsets(TaskbarSnapshot bar, List<Rectangle> controls)
        {
            AppSettings settings = new AppSettings();
            int previous = int.MinValue;
            foreach (int offset in new int[] { 0, 5, 50, 100, 200, 420, 424, 425, 430, 500, 679 })
            {
                settings.HorizontalOffset = offset;
                OverlayLayout layout = TaskbarGeometry.Calculate(bar, settings, controls);
                Check(layout.Bounds.X > previous, "Every positive nudge with remaining space must actually move right.");
                CheckSafe(bar, layout, controls);
                previous = layout.Bounds.X;
            }
            foreach (int offset in new int[] { 678, 650, 500, 430, 425, 424, 420, 300, 100, 5, 0 })
            {
                settings.HorizontalOffset = offset;
                OverlayLayout layout = TaskbarGeometry.Calculate(bar, settings, controls);
                Check(layout.Bounds.X < previous, "Every negative nudge with remaining space must actually move left.");
                CheckSafe(bar, layout, controls);
                previous = layout.Bounds.X;
            }
            settings.HorizontalOffset = 20000;
            OverlayLayout boundary = TaskbarGeometry.Calculate(bar, settings, controls);
            settings.HorizontalOffset = boundary.ResolvedHorizontalOffset - 5;
            Check(TaskbarGeometry.Calculate(bar, settings, controls).Bounds.X < boundary.Bounds.X, "Normalizing an extreme value must allow the next left nudge to move immediately.");
            settings.HorizontalOffset = 0;
            settings.HorizontalOffset = TaskbarGeometry.OffsetForPosition(bar, settings, controls, 613, 1);
            Check(TaskbarGeometry.Calculate(bar, settings, controls).Bounds.X == 1258, "Dragging right out of the first safe gap must skip the button block.");
            settings.HorizontalOffset = TaskbarGeometry.OffsetForPosition(bar, settings, controls, 1257, -1);
            Check(TaskbarGeometry.Calculate(bar, settings, controls).Bounds.X == 612, "Dragging left out of the right safe gap must return before the button block.");
        }

        private static TaskbarSnapshot OffscreenBar(bool onLeft)
        {
            Rectangle desktop = SystemInformation.VirtualScreen;
            int x = onLeft ? desktop.Left - 10000 : desktop.Right + 10000;
            TaskbarSnapshot bar = BottomBar(1920, 1080, 48, 1);
            bar.Bounds.Offset(x, 0);
            bar.MonitorBounds.Offset(x, 0);
            bar.WorkingArea.Offset(x, 0);
            return bar;
        }

        private static List<Rectangle> OffscreenControls(TaskbarSnapshot bar)
        {
            int x = bar.Bounds.Left;
            return new List<Rectangle> { new Rectangle(x, 1032, 180, 48), new Rectangle(x + 700, 1032, 550, 48), new Rectangle(x + 1600, 1032, 320, 48) };
        }

        private static void PrepareWindow(LyricOverlay overlay, TaskbarSnapshot bar, List<Rectangle> controls, AppSettings settings)
        {
            overlay.UpdateSettings(settings);
            overlay.SetLyrics("左右拖动检查", "", true);
            MethodInfo apply = typeof(LyricOverlay).GetMethod("ApplyLayout", BindingFlags.Instance | BindingFlags.NonPublic);
            apply.Invoke(overlay, new object[] { bar, controls, TaskbarGeometry.Calculate(bar, settings, controls) });
        }

        private static void Mouse(LyricOverlay overlay, string method, MouseButtons button, Point point)
        {
            MethodInfo handler = typeof(LyricOverlay).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            handler.Invoke(overlay, new object[] { new MouseEventArgs(button, 1, point.X, point.Y, 0) });
        }

        private static bool IsDragging(LyricOverlay overlay)
        {
            return (bool)typeof(LyricOverlay).GetField("_dragging", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
        }

        private static Rectangle WindowBounds(LyricOverlay overlay)
        {
            OverlayNative.Rect rectangle;
            if (!OverlayNative.GetWindowRect(overlay.Handle, out rectangle)) throw new InvalidOperationException("The native overlay window no longer exists.");
            return rectangle.ToRectangle();
        }

        private static void VerifyMouseGestures()
        {
            IntPtr foreground = OverlayNative.GetForegroundWindow();
            using (LyricOverlay overlay = new LyricOverlay())
            {
                TaskbarSnapshot bar = OffscreenBar(true);
                List<Rectangle> controls = OffscreenControls(bar);
                AppSettings settings = new AppSettings { OverlayEnabled = false, ClickThrough = false };
                int changed = 0, completed = 0;
                OverlayPositionChangedEventArgs last = null;
                overlay.PositionChanged += delegate(object sender, OverlayPositionChangedEventArgs args) { changed++; last = args; };
                overlay.DragCompleted += delegate { completed++; };
                PrepareWindow(overlay, bar, controls, settings);
                long style = OverlayNative.GetExtendedStyle(overlay.Handle).ToInt64();
                Check((style & 0x08000000) != 0 && (style & 0x80) != 0 && (style & 0x20) == 0, "An unlocked strip must retain NOACTIVATE / TOOLWINDOW while accepting mouse input.");
                int start = WindowBounds(overlay).Left;
                Point down = overlay.PointToScreen(new Point(22, 24));
                Mouse(overlay, "OnMouseDown", MouseButtons.Left, new Point(22, 24));
                Check(IsDragging(overlay), "An unlocked left mouse-down must begin a drag.");
                Point target = new Point(down.X + 300, down.Y);
                Mouse(overlay, "OnMouseMove", MouseButtons.Left, overlay.PointToClient(target));
                Console.WriteLine("Native mouse right: " + start + " -> " + WindowBounds(overlay).Left);
                Check(WindowBounds(overlay).Left > start && changed == 0 && completed == 0, "A right drag must move immediately without saving on every mouse-move.");
                Mouse(overlay, "OnMouseUp", MouseButtons.Left, overlay.PointToClient(target));
                Check(!IsDragging(overlay) && changed == 1 && completed == 1 && last.HorizontalOffset == 300, "Releasing a right drag must publish one final position.");
                CheckSafe(bar, new OverlayLayout { Bounds = WindowBounds(overlay) }, controls);

                start = WindowBounds(overlay).Left;
                down = overlay.PointToScreen(new Point(22, 24));
                Mouse(overlay, "OnMouseDown", MouseButtons.Left, new Point(22, 24));
                target = new Point(down.X - 200, down.Y);
                Mouse(overlay, "OnMouseMove", MouseButtons.Left, overlay.PointToClient(target));
                Console.WriteLine("Native mouse left: " + start + " -> " + WindowBounds(overlay).Left);
                Check(WindowBounds(overlay).Left < start && changed == 1, "A left drag must move left without intermediate saves.");
                Mouse(overlay, "OnMouseUp", MouseButtons.Left, overlay.PointToClient(target));
                Check(changed == 2 && completed == 2 && last.HorizontalOffset == 100, "Releasing a left drag must publish its new offset exactly once.");
                CheckSafe(bar, new OverlayLayout { Bounds = WindowBounds(overlay) }, controls);
                Check(OverlayNative.GetForegroundWindow() == foreground, "Dragging must not change the native foreground window.");

                settings.ClickThrough = true;
                PrepareWindow(overlay, bar, controls, settings);
                Mouse(overlay, "OnMouseDown", MouseButtons.Left, new Point(22, 24));
                Check(!IsDragging(overlay) && changed == 2 && (OverlayNative.GetExtendedStyle(overlay.Handle).ToInt64() & 0x20) != 0, "Locking the strip must restore native click-through and reject drags.");
            }
        }

        private static void VerifyNativeCursorPolling()
        {
            if (OverlayNative.LeftMouseDown) { Console.WriteLine("Native cursor polling checks skipped: a real mouse button is currently down."); return; }
            IntPtr foreground = OverlayNative.GetForegroundWindow();
            foreach (bool right in new bool[] { true, false })
            using (LyricOverlay overlay = new LyricOverlay())
            {
                TaskbarSnapshot bar = OffscreenBar(right);
                List<Rectangle> controls = OffscreenControls(bar);
                AppSettings settings = new AppSettings { OverlayEnabled = false, ClickThrough = false, HorizontalOffset = 300 };
                int saved = 0;
                overlay.PositionChanged += delegate { saved++; };
                PrepareWindow(overlay, bar, controls, settings);
                int origin = WindowBounds(overlay).Left;
                Mouse(overlay, "OnMouseDown", MouseButtons.Left, new Point(22, 24));
                Timer timer = (Timer)typeof(LyricOverlay).GetField("_dragTimer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
                typeof(Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(timer, new object[] { EventArgs.Empty });
                Console.WriteLine("Native cursor polling " + (right ? "right: " : "left: ") + origin + " -> " + WindowBounds(overlay).Left);
                Check(right ? WindowBounds(overlay).Left > origin : WindowBounds(overlay).Left < origin, "The native cursor-polling path must track movement outside a nonactivated window in both directions.");
                Check(!IsDragging(overlay) && saved == 1, "Native released-button polling must finish and save the drag once.");
                CheckSafe(bar, new OverlayLayout { Bounds = WindowBounds(overlay) }, controls);
                Check(OverlayNative.GetForegroundWindow() == foreground, "Cursor polling must preserve foreground focus.");
            }
        }

        private static void VerifyPreview()
        {
            using (LyricOverlay overlay = new LyricOverlay())
            {
                overlay.UpdateSettings(new AppSettings { OverlayEnabled = false, FontFamily = "細明體-ExtB", FontSize = 19, ShowTranslation = true });
                overlay.SetLyrics("Burn it all", "", "焚尽旧章", true);
                using (Bitmap taskbarCaption = CurrentBitmap(overlay, 410, 48))
                {
                    SizeF originalSize = (SizeF)PrivateField(PrivateField(overlay, "_currentLine"), "_glyphSize");
                    SizeF translatedSize = (SizeF)PrivateField(PrivateField(overlay, "_secondLine"), "_glyphSize");
                    Check(originalSize.Height >= 16 && translatedSize.Height >= 16, "A 48px taskbar must not shrink a 19pt bilingual caption before fitting its actual glyphs.");
                    CheckMargins(taskbarCaption);
                    SaveDark(taskbarCaption, "overlay-bilingual-taskbar.png");
                }
            }
            using (LyricOverlay overlay = new LyricOverlay())
            {
                AppSettings settings = new AppSettings { OverlayEnabled = false, ActiveColor = "#FF4433", TextColor = "#5484D1", TwoLines = true, FontSize = 18 };
                overlay.UpdateSettings(settings);
                using (Bitmap preview = overlay.RenderPreview(420, 60))
                {
                    int transparent = 0, colored = 0, active = 0, normal = 0;
                    for (int y = 0; y < preview.Height; y++)
                        for (int x = 0; x < preview.Width; x++)
                        {
                            Color pixel = preview.GetPixel(x, y);
                            if (pixel.A == 0) transparent++;
                            else colored++;
                            if (pixel.A > 200 && pixel.R > 180 && pixel.G < 120) active++;
                            if (pixel.A > 150 && pixel.B > 140 && pixel.R < 130) normal++;
                        }
                    Check(colored > 500 && transparent > preview.Width * preview.Height / 4, "A preview must contain visible glyphs on a genuinely transparent background.");
                    Check(active > 100 && normal > 100, "Two-line rendering must use both configured lyric colors.");
                    Check(preview.GetPixel(0, 0).A == 0 && preview.GetPixel(preview.Width - 1, preview.Height - 1).A == 0, "The overlay must not paint a rectangular background.");
                    preview.Save("overlay-preview.png", ImageFormat.Png);
                    using (Bitmap backdrop = new Bitmap(420, 60))
                    using (Graphics graphics = Graphics.FromImage(backdrop))
                    {
                        graphics.Clear(Color.FromArgb(17, 24, 39));
                        graphics.DrawImageUnscaled(preview, 0, 0);
                        backdrop.Save("overlay-preview-dark.png", ImageFormat.Png);
                    }
                }
                settings.ClickThrough = false;
                overlay.UpdateSettings(settings);
                using (Bitmap unlocked = overlay.RenderPreview(420, 60))
                {
                    Check(unlocked.GetPixel(200, 1).A > 0, "Unlocking must paint a continuous draggable strip, including empty pixels.");
                    unlocked.Save("overlay-preview-unlocked.png", ImageFormat.Png);
                }
            }
        }

        private static TaskbarSnapshot BottomBar(int width, int height, int thickness, float scale)
        {
            return new TaskbarSnapshot { Bounds = new Rectangle(0, height - thickness, width, thickness), MonitorBounds = new Rectangle(0, 0, width, height), WorkingArea = new Rectangle(0, 0, width, height - thickness), Scale = scale, Horizontal = true, Edge = 3 };
        }
        private static void CheckSafe(TaskbarSnapshot bar, OverlayLayout layout, IList<Rectangle> controls)
        {
            Check(bar.Bounds.Contains(layout.Bounds), "Lyrics must remain entirely within the visible taskbar.");
            foreach (Rectangle control in controls) Check(!layout.Bounds.IntersectsWith(control), "Lyrics must not overlap a reserved taskbar control.");
        }
        private static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            _checks++;
        }
    }
}
