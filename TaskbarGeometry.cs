using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MusicBar
{
    internal sealed class TaskbarSnapshot
    {
        public IntPtr Handle;
        public Rectangle Bounds;
        public Rectangle MonitorBounds;
        public Rectangle WorkingArea;
        public float Scale = 1;
        public bool Horizontal;
        public int Edge;
        public bool AutoHide;
        public readonly List<Rectangle> NativeOccupied = new List<Rectangle>();
    }

    internal sealed class OverlayLayout
    {
        public Rectangle Bounds;
        public bool Rotate;
        public bool RotateClockwise;
        public float Scale = 1;
        public bool PositionClamped;
        public bool CrossedButtons;
        public int ResolvedHorizontalOffset;
    }

    internal static class TaskbarGeometry
    {
        private struct Interval
        {
            public int Start;
            public int End;
            public Interval(int start, int end) { Start = start; End = end; }
            public int Length { get { return End - Start; } }
        }

        internal static bool TryFind(int monitorIndex, out TaskbarSnapshot snapshot)
        {
            snapshot = null;
            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0) return false;
            Screen screen = monitorIndex >= 0 && monitorIndex < screens.Length ? screens[monitorIndex] : Screen.PrimaryScreen;
            if (screen == null) screen = screens[0];
            Rectangle monitorBounds = screen.Bounds;
            OverlayNative.Point center = new OverlayNative.Point(monitorBounds.Left + monitorBounds.Width / 2, monitorBounds.Top + monitorBounds.Height / 2);
            IntPtr selectedMonitor = OverlayNative.MonitorFromPoint(center, 2);
            IntPtr found = IntPtr.Zero;
            OverlayNative.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                string name = OverlayNative.ClassName(window);
                if ((name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") && OverlayNative.IsWindowVisible(window) && OverlayNative.MonitorFromWindow(window, 2) == selectedMonitor)
                {
                    found = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero || !OverlayNative.IsWindowVisible(found)) return false;

            OverlayNative.Rect nativeRect;
            if (!OverlayNative.GetWindowRect(found, out nativeRect)) return false;
            Rectangle entireBar = nativeRect.ToRectangle();
            Rectangle visibleBar = Rectangle.Intersect(entireBar, monitorBounds);
            bool horizontal = entireBar.Width >= entireBar.Height;
            int thickness = horizontal ? entireBar.Height : entireBar.Width;
            int visibleThickness = horizontal ? visibleBar.Height : visibleBar.Width;
            float scale = OverlayNative.ScaleForWindow(found, selectedMonitor);
            // An auto-hidden or sliding taskbar is usually still WS_VISIBLE. Its rectangle must
            // actually be inside the display before a separate floating window can follow it.
            if (thickness <= 0 || visibleThickness < thickness - Math.Max(2, (int)Math.Round(scale)) || visibleThickness < 16 * scale) return false;
            if (visibleBar.Width <= 0 || visibleBar.Height <= 0) return false;

            TaskbarSnapshot result = new TaskbarSnapshot();
            result.Handle = found;
            result.Bounds = visibleBar;
            result.MonitorBounds = monitorBounds;
            result.WorkingArea = screen.WorkingArea;
            result.Horizontal = horizontal;
            result.Scale = scale;
            result.Edge = horizontal ? (visibleBar.Top + visibleBar.Height / 2 < monitorBounds.Top + monitorBounds.Height / 2 ? 1 : 3)
                : (visibleBar.Left + visibleBar.Width / 2 < monitorBounds.Left + monitorBounds.Width / 2 ? 0 : 2);
            result.AutoHide = OverlayNative.IsAutoHiddenTaskbar(found, result.Edge, monitorBounds);
            OverlayNative.EnumChildWindows(found, delegate(IntPtr child, IntPtr parameter)
            {
                string name = OverlayNative.ClassName(child);
                if (name == "TrayNotifyWnd" || name == "Start" || name == "StartButton" || name == "TrayClockWClass")
                {
                    OverlayNative.Rect childRect;
                    if (OverlayNative.IsWindowVisible(child) && OverlayNative.GetWindowRect(child, out childRect))
                    {
                        Rectangle occupied = Rectangle.Intersect(childRect.ToRectangle(), visibleBar);
                        if (!occupied.IsEmpty) result.NativeOccupied.Add(occupied);
                    }
                }
                return true;
            }, IntPtr.Zero);
            snapshot = result;
            return true;
        }

        // A borderless full-screen app can hide or move the taskbar off-screen. The caller
        // must first confirm that the foreground window is actually full-screen.
        internal static bool TryCreateFullscreenSnapshot(int monitorIndex, IntPtr overlay, out TaskbarSnapshot snapshot)
        {
            return TryCreateFullscreenSnapshot(monitorIndex, overlay, null, null, out snapshot);
        }

        internal static bool TryCreateFullscreenSnapshot(int monitorIndex, IntPtr overlay, TaskbarSnapshot reference,
            AppSettings settings, out TaskbarSnapshot snapshot)
        {
            snapshot = null;
            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0) return false;
            Screen screen = monitorIndex >= 0 && monitorIndex < screens.Length ? screens[monitorIndex] : Screen.PrimaryScreen;
            if (screen == null) screen = screens[0];
            Rectangle monitor = screen.Bounds;
            if (!ForegroundIsFullscreen(monitor, overlay)) return false;

            float scale = OverlayNative.ScaleForMonitor(monitor, overlay);
            if (scale <= 0 && reference != null && reference.MonitorBounds == monitor && reference.Scale > 0) scale = reference.Scale;
            if (scale <= 0) scale = 1f;

            int edge = -1;
            int thickness = 0;
            Rectangle taskbar;
            if (OverlayNative.TryGetTaskbarBounds(monitor, out taskbar))
            {
                edge = EdgeForRectangle(taskbar, monitor);
                int observedThickness = edge == 0 || edge == 2 ? taskbar.Width : taskbar.Height;
                // An auto-hidden taskbar can leave only a 1–2 px reveal strip on screen.
                // Keep its edge, but do not mistake the reveal strip for the taskbar's
                // actual thickness or it will produce a nearly invisible lyric surface.
                thickness = ReliableTaskbarThickness(observedThickness, scale);
            }
            if (edge < 0)
            {
                int workAreaThickness;
                edge = EdgeFromWorkArea(monitor, screen.WorkingArea, out workAreaThickness);
                if (edge >= 0) thickness = workAreaThickness;
            }

            // AppBar auto-hide can leave the work area equal to the whole monitor. Ask
            // Explorer for its actual edge before falling back to the last observed edge.
            if (edge < 0) edge = OverlayNative.AutoHideTaskbarEdge(monitor);
            if (edge < 0 && reference != null) edge = reference.Edge;
            if (edge < 0 || edge > 3) edge = 3; // Windows' default taskbar anchor.

            if (thickness <= 0 && reference != null && reference.Scale > 0)
            {
                int referenceThickness = reference.Horizontal ? reference.Bounds.Height : reference.Bounds.Width;
                thickness = (int)Math.Round(referenceThickness / reference.Scale * scale);
            }
            int lyricThickness = FullscreenFallbackThickness(settings, scale);
            snapshot = CreateFullscreenSnapshot(monitor, screen.WorkingArea, scale, edge, thickness, lyricThickness);
            snapshot.Handle = IntPtr.Zero;
            return true;
        }

        // Pure geometry helper shared with checks: taskbar thickness is taken from the
        // work-area inset or the last observed taskbar. Only when neither exists do we
        // size the temporary edge surface to the current lyric typography.
        internal static TaskbarSnapshot CreateFullscreenSnapshot(Rectangle monitor, Rectangle workingArea,
            float scale, int edge, int preferredThickness, int fallbackThickness)
        {
            if (monitor.Width <= 0 || monitor.Height <= 0) return new TaskbarSnapshot();
            if (scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale)) scale = 1f;
            if (edge < 0 || edge > 3)
            {
                int ignored;
                edge = EdgeFromWorkArea(monitor, workingArea, out ignored);
                if (edge < 0) edge = 3;
            }

            int workThickness;
            int workEdge = EdgeFromWorkArea(monitor, workingArea, out workThickness);
            if (workEdge == edge && workThickness > 0) preferredThickness = workThickness;
            int maxThickness = edge == 0 || edge == 2 ? monitor.Width : monitor.Height;
            int thickness = preferredThickness > 0 ? preferredThickness : fallbackThickness;
            thickness = Math.Max(1, Math.Min(maxThickness, thickness));

            Rectangle bounds;
            if (edge == 0) bounds = new Rectangle(monitor.Left, monitor.Top, thickness, monitor.Height);
            else if (edge == 1) bounds = new Rectangle(monitor.Left, monitor.Top, monitor.Width, thickness);
            else if (edge == 2) bounds = new Rectangle(monitor.Right - thickness, monitor.Top, thickness, monitor.Height);
            else bounds = new Rectangle(monitor.Left, monitor.Bottom - thickness, monitor.Width, thickness);

            return new TaskbarSnapshot
            {
                Bounds = bounds, MonitorBounds = monitor, WorkingArea = workingArea,
                Scale = scale, Horizontal = edge == 1 || edge == 3, Edge = edge, AutoHide = false
            };
        }

        internal static List<Rectangle> MapOccupied(TaskbarSnapshot source, TaskbarSnapshot target, IList<Rectangle> occupied)
        {
            List<Rectangle> mapped = new List<Rectangle>();
            if (source == null || target == null || occupied == null || source.MonitorBounds.Width <= 0 || source.MonitorBounds.Height <= 0) return mapped;
            Rectangle from = source.MonitorBounds, to = target.MonitorBounds;
            foreach (Rectangle rectangle in occupied)
            {
                Rectangle clipped = Rectangle.Intersect(rectangle, from);
                if (clipped.Width <= 0 || clipped.Height <= 0) continue;
                int left = to.Left + (int)Math.Round((clipped.Left - from.Left) * (double)to.Width / from.Width);
                int top = to.Top + (int)Math.Round((clipped.Top - from.Top) * (double)to.Height / from.Height);
                int right = to.Left + (int)Math.Round((clipped.Right - from.Left) * (double)to.Width / from.Width);
                int bottom = to.Top + (int)Math.Round((clipped.Bottom - from.Top) * (double)to.Height / from.Height);
                Rectangle mappedRectangle = Rectangle.Intersect(Rectangle.FromLTRB(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom)), target.Bounds);
                if (mappedRectangle.Width > 0 && mappedRectangle.Height > 0) mapped.Add(mappedRectangle);
            }
            return mapped;
        }

        internal static int PositionAtRatio(TaskbarSnapshot bar, OverlayLayout layout, double ratio)
        {
            if (bar == null || layout == null || layout.Bounds.IsEmpty) return 0;
            ratio = double.IsNaN(ratio) || double.IsInfinity(ratio) ? .5 : Math.Max(0, Math.Min(1, ratio));
            int start = bar.Horizontal ? bar.Bounds.Left : bar.Bounds.Top;
            int barLength = bar.Horizontal ? bar.Bounds.Width : bar.Bounds.Height;
            int lyricLength = bar.Horizontal ? layout.Bounds.Width : layout.Bounds.Height;
            int travel = Math.Max(0, barLength - lyricLength);
            return start + (int)Math.Round(travel * ratio);
        }

        internal static void ApplyPositionRatio(TaskbarSnapshot bar, OverlayLayout layout, double ratio)
        {
            if (bar == null || layout == null || layout.Bounds.IsEmpty) return;
            int position = PositionAtRatio(bar, layout, ratio);
            Rectangle bounds = layout.Bounds;
            if (bar.Horizontal)
            {
                int maxLeft = bar.Bounds.Right - bounds.Width;
                bounds.X = Math.Max(bar.Bounds.Left, Math.Min(maxLeft, position));
            }
            else
            {
                int maxTop = bar.Bounds.Bottom - bounds.Height;
                bounds.Y = Math.Max(bar.Bounds.Top, Math.Min(maxTop, position));
            }
            layout.Bounds = bounds;
        }

        internal static double PositionRatio(TaskbarSnapshot bar, OverlayLayout layout)
        {
            if (bar == null || layout == null || layout.Bounds.IsEmpty) return .5;
            int start = bar.Horizontal ? bar.Bounds.Left : bar.Bounds.Top;
            int barLength = bar.Horizontal ? bar.Bounds.Width : bar.Bounds.Height;
            int lyricLength = bar.Horizontal ? layout.Bounds.Width : layout.Bounds.Height;
            int travel = barLength - lyricLength;
            if (travel <= 0) return .5;
            int position = bar.Horizontal ? layout.Bounds.Left : layout.Bounds.Top;
            return Math.Max(0, Math.Min(1, (position - start) / (double)travel));
        }

        // A fullscreen player may hide the taskbar or cause Explorer to report a different
        // edge surface. Reuse the last visible lyric coordinates on the same display. When
        // the display geometry changes, map both axes proportionally across the monitor.
        internal static void ApplyRememberedLocation(Rectangle sourceMonitor, OverlayLayout sourceLayout,
            Rectangle targetMonitor, OverlayLayout targetLayout)
        {
            if (sourceLayout == null || targetLayout == null || sourceLayout.Bounds.IsEmpty || targetLayout.Bounds.IsEmpty
                || sourceMonitor.Width <= 0 || sourceMonitor.Height <= 0 || targetMonitor.Width <= 0 || targetMonitor.Height <= 0)
                return;

            Rectangle bounds = targetLayout.Bounds;
            if (sourceMonitor == targetMonitor)
            {
                bounds.X = sourceLayout.Bounds.X;
                bounds.Y = sourceLayout.Bounds.Y;
            }
            else
            {
                double xRatio = PositionRatioOnAxis(sourceMonitor.Left, sourceMonitor.Width, sourceLayout.Bounds.Left, sourceLayout.Bounds.Width);
                double yRatio = PositionRatioOnAxis(sourceMonitor.Top, sourceMonitor.Height, sourceLayout.Bounds.Top, sourceLayout.Bounds.Height);
                bounds.X = targetMonitor.Left + (int)Math.Round(Math.Max(0, targetMonitor.Width - bounds.Width) * xRatio);
                bounds.Y = targetMonitor.Top + (int)Math.Round(Math.Max(0, targetMonitor.Height - bounds.Height) * yRatio);
            }

            // If a setting changed the lyric size while fullscreen, keep its top-left anchor
            // but ensure the resized surface remains on the selected display.
            bounds.X = Math.Max(targetMonitor.Left, Math.Min(targetMonitor.Right - bounds.Width, bounds.X));
            bounds.Y = Math.Max(targetMonitor.Top, Math.Min(targetMonitor.Bottom - bounds.Height, bounds.Y));
            targetLayout.Bounds = bounds;
        }

        private static double PositionRatioOnAxis(int monitorStart, int monitorLength, int position, int extent)
        {
            int travel = Math.Max(0, monitorLength - extent);
            if (travel == 0) return .5;
            return Math.Max(0, Math.Min(1, (position - monitorStart) / (double)travel));
        }

        private static int FullscreenFallbackThickness(AppSettings settings, float scale)
        {
            int lines = settings != null && settings.TwoLines ? 2 : 1;
            float fontSize = settings != null && settings.FontSize >= 10 && settings.FontSize <= 30 ? settings.FontSize : 16f;
            int logicalHeight = Math.Max(32, (int)Math.Ceiling(fontSize * (96f / 72f) * 1.35f * lines + 8));
            return Math.Max(1, (int)Math.Ceiling(logicalHeight * scale));
        }

        internal static int ReliableTaskbarThickness(int observedThickness, float scale)
        {
            if (scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale)) scale = 1f;
            int minimum = Math.Max(16, (int)Math.Ceiling(16 * scale));
            return observedThickness >= minimum ? observedThickness : 0;
        }

        private static int EdgeForRectangle(Rectangle rectangle, Rectangle monitor)
        {
            int tolerance = Math.Max(2, (int)Math.Round(Math.Max(monitor.Width, monitor.Height) * .002));
            int[] distances = { Math.Abs(rectangle.Left - monitor.Left), Math.Abs(rectangle.Top - monitor.Top),
                Math.Abs(monitor.Right - rectangle.Right), Math.Abs(monitor.Bottom - rectangle.Bottom) };
            int edge = 0;
            for (int i = 1; i < distances.Length; i++) if (distances[i] < distances[edge]) edge = i;
            return distances[edge] <= tolerance ? edge : -1;
        }

        private static int EdgeFromWorkArea(Rectangle monitor, Rectangle workingArea, out int thickness)
        {
            thickness = 0;
            Rectangle work = Rectangle.Intersect(monitor, workingArea);
            if (work.Width <= 0 || work.Height <= 0) return -1;
            int[] insets = { work.Left - monitor.Left, work.Top - monitor.Top, monitor.Right - work.Right, monitor.Bottom - work.Bottom };
            int edge = 0;
            for (int i = 1; i < insets.Length; i++) if (insets[i] > insets[edge]) edge = i;
            if (insets[edge] <= 0) return -1;
            thickness = insets[edge];
            return edge;
        }

        internal static OverlayLayout Calculate(TaskbarSnapshot bar, AppSettings settings, IList<Rectangle> occupied)
        {
            OverlayLayout result = new OverlayLayout();
            result.Scale = bar.Scale;
            if (!settings.FollowTaskbar)
            {
                result.Bounds = AboveTaskbar(bar, settings);
                AppSettings anchor = new AppSettings { FollowTaskbar = false, FontSize = settings.FontSize, TwoLines = settings.TwoLines, Width = settings.Width, Alignment = settings.Alignment, VerticalOffset = settings.VerticalOffset };
                int displacement = result.Bounds.X - AboveTaskbar(bar, anchor).X;
                result.ResolvedHorizontalOffset = (int)Math.Round(displacement / bar.Scale);
                result.PositionClamped = displacement != (int)Math.Round(settings.HorizontalOffset * bar.Scale);
                return result;
            }

            bool horizontal = bar.Horizontal;
            List<Interval> free = FreeIntervals(bar, occupied);
            int minLength = (int)Math.Ceiling(80 * bar.Scale);
            if (free.Count == 0) return result;
            int baseGap;
            int baseRank = BaseRank(bar, settings, free, out baseGap);
            int count = TotalPositions(free, minLength);
            long wanted = baseRank + (long)Math.Round(settings.HorizontalOffset * bar.Scale);
            int rank = (int)Math.Max(0L, Math.Min(count - 1L, wanted));
            result.ResolvedHorizontalOffset = (int)Math.Round((rank - baseRank) / bar.Scale);
            int chosenIndex = 0;
            while (rank >= free[chosenIndex].Length - minLength + 1)
            {
                rank -= free[chosenIndex].Length - minLength + 1;
                chosenIndex++;
            }
            Interval chosen = free[chosenIndex];
            int position = chosen.Start + rank;
            int requested = Math.Max(minLength, (int)Math.Round(settings.Width * bar.Scale));
            int length = Math.Min(requested, chosen.End - position);
            result.PositionClamped = wanted < 0 || wanted >= count;
            result.CrossedButtons = chosenIndex != baseGap;
            result.Rotate = !horizontal;
            result.RotateClockwise = bar.Edge == 2;
            result.Bounds = horizontal ? new Rectangle(position, bar.Bounds.Top, length, bar.Bounds.Height)
                : new Rectangle(bar.Bounds.Left, position, bar.Bounds.Width, length);
            return result;
        }

        // Offsets walk through all safe start positions, skipping occupied button regions.
        // This keeps a positive nudge moving right even when a lyric no longer fits one gap.
        private static List<Interval> FreeIntervals(TaskbarSnapshot bar, IList<Rectangle> occupied)
        {
            bool horizontal = bar.Horizontal;
            int begin = horizontal ? bar.Bounds.Left : bar.Bounds.Top;
            int end = horizontal ? bar.Bounds.Right : bar.Bounds.Bottom;
            int padding = Math.Max(4, (int)Math.Round(8 * bar.Scale));
            List<Interval> reserved = new List<Interval>();
            if (occupied != null) foreach (Rectangle rectangle in occupied)
            {
                Rectangle clipped = Rectangle.Intersect(rectangle, bar.Bounds);
                if (clipped.Width <= 0 || clipped.Height <= 0) continue;
                int start = horizontal ? clipped.Left : clipped.Top;
                int finish = horizontal ? clipped.Right : clipped.Bottom;
                reserved.Add(new Interval(Math.Max(begin, start - padding), Math.Min(end, finish + padding)));
            }
            reserved.Sort(delegate(Interval left, Interval right) { return left.Start.CompareTo(right.Start); });
            List<Interval> free = new List<Interval>();
            int cursor = begin + padding;
            foreach (Interval blocked in reserved)
            {
                if (blocked.Start > cursor) free.Add(new Interval(cursor, Math.Min(blocked.Start, end - padding)));
                cursor = Math.Max(cursor, blocked.End);
            }
            if (cursor < end - padding) free.Add(new Interval(cursor, end - padding));
            int minimum = (int)Math.Ceiling(80 * bar.Scale);
            free.RemoveAll(delegate(Interval gap) { return gap.Length < minimum; });
            return free;
        }

        private static int TotalPositions(List<Interval> gaps, int minimum)
        {
            int count = 0;
            foreach (Interval gap in gaps) count += gap.Length - minimum + 1;
            return count;
        }

        private static int BaseRank(TaskbarSnapshot bar, AppSettings settings, List<Interval> gaps, out int chosenIndex)
        {
            int requested = (int)Math.Round(settings.Width * bar.Scale);
            chosenIndex = settings.Alignment == "right" ? gaps.Count - 1 : 0;
            if (settings.Alignment == "center")
            {
                int begin = bar.Horizontal ? bar.Bounds.Left : bar.Bounds.Top;
                int end = bar.Horizontal ? bar.Bounds.Right : bar.Bounds.Bottom;
                int target = begin + (end - begin) / 2;
                long best = long.MaxValue;
                for (int i = 0; i < gaps.Count; i++)
                {
                    Interval gap = gaps[i];
                    long score = Math.Abs(gap.Start + gap.Length / 2 - target) + Math.Max(0, requested - gap.Length) * 4L;
                    if (score < best) { best = score; chosenIndex = i; }
                }
            }
            Interval chosen = gaps[chosenIndex];
            int length = Math.Min(requested, chosen.Length);
            int position = chosen.Start;
            if (settings.Alignment == "center") position += (chosen.Length - length) / 2;
            if (settings.Alignment == "right") position = chosen.End - length;
            int minimum = (int)Math.Ceiling(80 * bar.Scale);
            int rank = position - chosen.Start;
            for (int i = 0; i < chosenIndex; i++) rank += gaps[i].Length - minimum + 1;
            return rank;
        }

        internal static int NudgeHorizontalOffset(TaskbarSnapshot bar, AppSettings settings, IList<Rectangle> occupied, int amount)
        {
            OverlayLayout current = Calculate(bar, settings, occupied);
            int offset = current.Bounds.IsEmpty ? settings.HorizontalOffset : current.ResolvedHorizontalOffset;
            return (int)Math.Max(-20000L, Math.Min(20000L, (long)offset + amount));
        }

        internal static int OffsetForPosition(TaskbarSnapshot bar, AppSettings settings, IList<Rectangle> occupied, int desiredPosition, int direction)
        {
            if (!settings.FollowTaskbar)
            {
                AppSettings anchor = new AppSettings { FollowTaskbar = false, FontSize = settings.FontSize, TwoLines = settings.TwoLines, Width = settings.Width, Alignment = settings.Alignment, VerticalOffset = settings.VerticalOffset };
                int start = AboveTaskbar(bar, anchor).X;
                return (int)Math.Round((desiredPosition - start) / bar.Scale);
            }
            List<Interval> gaps = FreeIntervals(bar, occupied);
            if (gaps.Count == 0) return settings.HorizontalOffset;
            int minimum = (int)Math.Ceiling(80 * bar.Scale);
            int baseGap;
            int originRank = BaseRank(bar, settings, gaps, out baseGap);
            int preceding = 0;
            int pickedRank = 0;
            for (int i = 0; i < gaps.Count; i++)
            {
                Interval gap = gaps[i];
                int last = gap.End - minimum;
                int positionCount = gap.Length - minimum + 1;
                if (desiredPosition < gap.Start)
                {
                    if (i == 0 || direction > 0) pickedRank = preceding;
                    else if (direction < 0) pickedRank = preceding - 1;
                    else pickedRank = desiredPosition - (gaps[i - 1].End - minimum) <= gap.Start - desiredPosition ? preceding - 1 : preceding;
                    break;
                }
                if (desiredPosition <= last) { pickedRank = preceding + desiredPosition - gap.Start; break; }
                preceding += positionCount;
                pickedRank = preceding - 1;
            }
            return (int)Math.Round((pickedRank - originRank) / bar.Scale);
        }

        private static Rectangle AboveTaskbar(TaskbarSnapshot bar, AppSettings settings)
        {
            Rectangle monitor = bar.MonitorBounds;
            int margin = Math.Max(4, (int)Math.Round(8 * bar.Scale));
            int width = Math.Min((int)Math.Round(settings.Width * bar.Scale), Math.Max(16, monitor.Width - margin * 2));
            int lines = settings.TwoLines ? 2 : 1;
            int height = Math.Min(monitor.Height, Math.Max((int)Math.Round(32 * bar.Scale), (int)Math.Ceiling(settings.FontSize * (96f / 72f) * bar.Scale * 1.35f * lines + 8 * bar.Scale)));
            int x = monitor.Left + margin;
            if (settings.Alignment == "center") x = monitor.Left + (monitor.Width - width) / 2;
            if (settings.Alignment == "right") x = monitor.Right - margin - width;
            x += (int)Math.Round(settings.HorizontalOffset * bar.Scale);
            int y;
            if (bar.Edge == 1) y = bar.Bounds.Bottom + margin;
            else if (bar.Edge == 0 || bar.Edge == 2)
            {
                x = bar.Edge == 0 ? bar.Bounds.Right + margin : bar.Bounds.Left - margin - width;
                x += (int)Math.Round(settings.HorizontalOffset * bar.Scale);
                y = monitor.Top + (monitor.Height - height) / 2;
            }
            else y = bar.Bounds.Top - height - margin;
            y += (int)Math.Round(settings.VerticalOffset * bar.Scale);
            x = Math.Max(monitor.Left, Math.Min(monitor.Right - width, x));
            y = Math.Max(monitor.Top, Math.Min(monitor.Bottom - height, y));
            // Keep the floating alternative on the desktop side of a horizontal taskbar.
            if (bar.Edge == 3) y = Math.Min(y, Math.Max(monitor.Top, bar.Bounds.Top - height));
            if (bar.Edge == 1) y = Math.Max(y, Math.Min(monitor.Bottom - height, bar.Bounds.Bottom));
            return new Rectangle(x, y, width, height);
        }

        internal static bool ForegroundIsFullscreen(Rectangle monitor, IntPtr overlay)
        {
            IntPtr foreground = OverlayNative.GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == overlay || OverlayNative.IsIconic(foreground)) return false;
            string name = OverlayNative.ClassName(foreground);
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") return false;
            OverlayNative.Rect rectangle;
            if (!OverlayNative.GetWindowRect(foreground, out rectangle)) return false;
            try
            {
                OverlayNative.Rect frame;
                if (OverlayNative.DwmGetWindowAttribute(foreground, 9, out frame, Marshal.SizeOf(typeof(OverlayNative.Rect))) == 0) rectangle = frame;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Rectangle bounds = rectangle.ToRectangle();
            return bounds.Left <= monitor.Left + 2 && bounds.Top <= monitor.Top + 2 && bounds.Right >= monitor.Right - 2 && bounds.Bottom >= monitor.Bottom - 2;
        }
    }

    internal static class OverlayNative
    {
        internal delegate bool EnumWindowProcedure(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct Point
        {
            public int X; public int Y;
            public Point(int x, int y) { X = x; Y = y; }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Size
        {
            public int Width; public int Height;
            public Size(int width, int height) { Width = width; Height = height; }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        {
            public int Left; public int Top; public int Right; public int Bottom;
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BlendFunction
        {
            public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat;
        }
        [StructLayout(LayoutKind.Sequential)] private struct AppBarData
        {
            public uint Size; public IntPtr Window; public uint Callback; public uint Edge; public Rect Rectangle; public IntPtr Parameter;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OsVersionInfo
        {
            public uint Size; public uint Major; public uint Minor; public uint Build; public uint Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        }

        internal static readonly bool Windows11 = ReadWindowsBuild() >= 22000;

        internal static string ClassName(IntPtr window)
        {
            StringBuilder text = new StringBuilder(256);
            GetClassName(window, text, text.Capacity);
            return text.ToString();
        }
        private static uint ReadWindowsBuild()
        {
            try
            {
                OsVersionInfo version = new OsVersionInfo();
                version.Size = (uint)Marshal.SizeOf(typeof(OsVersionInfo));
                if (RtlGetVersion(ref version) == 0) return version.Build;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return (uint)Environment.OSVersion.Version.Build;
        }
        internal static float ScaleForWindow(IntPtr window, IntPtr monitor)
        {
            try
            {
                uint dpi = GetDpiForWindow(window);
                if (dpi >= 48 && dpi <= 768) return dpi / 96f;
            }
            catch (EntryPointNotFoundException) { }
            try
            {
                uint x, y;
                if (GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x >= 48 && x <= 768) return x / 96f;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 1;
        }

        internal static float ScaleForMonitor(Rectangle monitor, IntPtr referenceWindow)
        {
            IntPtr monitorHandle = IntPtr.Zero;
            try
            {
                Point center = new Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2);
                monitorHandle = MonitorFromPoint(center, 2);
            }
            catch (EntryPointNotFoundException) { }

            bool referenceIsOnTarget = false;
            float windowScale = 0;
            if (referenceWindow != IntPtr.Zero && monitorHandle != IntPtr.Zero)
            {
                try { referenceIsOnTarget = MonitorFromWindow(referenceWindow, 2) == monitorHandle; }
                catch (EntryPointNotFoundException) { }
                if (referenceIsOnTarget) windowScale = ScaleForWindow(referenceWindow, monitorHandle);
            }

            float monitorScale = 0;
            if (monitorHandle != IntPtr.Zero)
            {
                try
                {
                    uint x, y;
                    if (GetDpiForMonitor(monitorHandle, 0, out x, out y) == 0 && x >= 48 && x <= 768)
                        monitorScale = x / 96f;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
            return SelectMonitorScale(monitorScale, windowScale, referenceIsOnTarget);
        }

        internal static float SelectMonitorScale(float monitorScale, float windowScale, bool referenceIsOnTarget)
        {
            bool validMonitor = monitorScale >= .5f && monitorScale <= 8f && !float.IsNaN(monitorScale) && !float.IsInfinity(monitorScale);
            bool validWindow = windowScale >= .5f && windowScale <= 8f && !float.IsNaN(windowScale) && !float.IsInfinity(windowScale);
            // A window's DPI is authoritative only while that window is on the monitor being
            // measured. When screens have mixed scaling, prefer the target monitor's own DPI.
            if (referenceIsOnTarget && validWindow) return windowScale;
            if (validMonitor) return monitorScale;
            return referenceIsOnTarget && validWindow ? windowScale : 0f;
        }

        internal static bool TryGetTaskbarBounds(Rectangle monitor, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            Point center = new Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2);
            IntPtr selectedMonitor = MonitorFromPoint(center, 2);
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                string name = ClassName(window);
                if ((name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") && IsWindowVisible(window) && MonitorFromWindow(window, 2) == selectedMonitor)
                {
                    found = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return false;
            Rect native;
            if (!GetWindowRect(found, out native)) return false;
            Rectangle clipped = Rectangle.Intersect(native.ToRectangle(), monitor);
            if (clipped.Width <= 0 || clipped.Height <= 0) return false;
            bounds = clipped;
            return true;
        }

        internal static int AutoHideTaskbarEdge(Rectangle monitor)
        {
            AppBarData data = new AppBarData();
            data.Size = (uint)Marshal.SizeOf(typeof(AppBarData));
            data.Rectangle = new Rect { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom };
            for (int edge = 0; edge <= 3; edge++)
            {
                data.Edge = (uint)edge;
                if (SHAppBarMessage(11, ref data).ToUInt64() != 0) return edge;
            }
            return -1;
        }
        internal static bool IsAutoHiddenTaskbar(IntPtr window, int edge, Rectangle monitor)
        {
            AppBarData data = new AppBarData();
            data.Size = (uint)Marshal.SizeOf(typeof(AppBarData));
            data.Window = window;
            if ((SHAppBarMessage(4, ref data).ToUInt64() & 1UL) != 0) return true;
            data.Edge = (uint)edge;
            data.Rectangle = new Rect { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom };
            return SHAppBarMessage(11, ref data).ToUInt64() != 0;
        }
        internal static bool CursorIn(Rectangle rectangle)
        {
            Point point;
            return GetCursorPos(out point) && rectangle.Contains(point.X, point.Y);
        }
        internal static bool TryCursorPosition(out System.Drawing.Point point)
        {
            Point native;
            bool result = GetCursorPos(out native);
            point = new System.Drawing.Point(native.X, native.Y);
            return result;
        }
        internal static bool LeftMouseDown { get { return (GetAsyncKeyState(1) & 0x8000) != 0; } }
        internal static IntPtr GetExtendedStyle(IntPtr window)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(window, -20) : new IntPtr(GetWindowLong32(window, -20));
        }
        internal static void SetExtendedStyle(IntPtr window, IntPtr style)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(window, -20, style);
            else SetWindowLong32(window, -20, style.ToInt32());
        }

        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProcedure callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr window, EnumWindowProcedure callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref OsVersionInfo version);
        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern int SetWindowLong32(IntPtr window, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref Point destination, ref Size size, IntPtr sourceDc, ref Point source, uint key, ref BlendFunction blend, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern uint GetGlyphIndices(IntPtr dc, string text, int count, [Out] ushort[] glyphs, uint flags);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr handle);
    }
}
