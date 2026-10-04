using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace MusicBar
{
    // Each timed line scrolls once, then keeps its ending visible until the next line.
    // Monotonic playback time preserves progress across pauses and delayed UI ticks.
    internal sealed class LyricScrollState
    {
        internal const double StartHoldSeconds = .5;
        internal const double EndHoldSeconds = 1.2;
        private int _phase;
        private double _phaseElapsed;
        private bool _firstPass = true;
        private double _speed = 40;
        private double _firstPassSpeed;
        private double _startedAt;
        private double _initialHold = StartHoldSeconds;
        internal double Offset { get; private set; }
        internal double MaximumOffset { get; private set; }
        internal double LastTime { get; private set; }
        internal bool IsMoving { get { return MaximumOffset > .1 && _phase == 1; } }

        internal void Reset(double now)
        {
            Offset = 0;
            _phase = 0;
            _phaseElapsed = 0;
            _firstPass = true;
            _firstPassSpeed = 0;
            _startedAt = now;
            _initialHold = StartHoldSeconds;
            LastTime = now;
        }

        internal void Configure(double maximumOffset, double pixelsPerSecond, double now)
        {
            maximumOffset = Math.Max(0, maximumOffset);
            bool extentChanged = Math.Abs(MaximumOffset - maximumOffset) > .01;
            _speed = Math.Max(1, pixelsPerSecond);
            if (MaximumOffset <= .1 && maximumOffset > .1) Reset(now);
            MaximumOffset = maximumOffset;
            if (MaximumOffset <= .1) Offset = 0;
            else if (extentChanged && _phase == 2) Offset = MaximumOffset;
            else if (extentChanged && Offset >= MaximumOffset)
            {
                Offset = MaximumOffset;
                _phase = 2;
                _phaseElapsed = 0;
                _firstPass = false;
            }
        }

        internal void ScaleOffset(double factor)
        {
            if (_phase == 1)
            {
                Offset = Math.Max(0, Offset * factor);
                _firstPassSpeed *= factor;
            }
        }

        internal void Advance(double now, double deadline)
        {
            UpdateDeadline(deadline);
            if (now <= LastTime) return;
            double elapsed = now - LastTime;
            double at = LastTime;
            LastTime = now;
            if (MaximumOffset <= .1) return;
            while (_firstPass && elapsed > .000001)
            {
                if (_phase == 0)
                {
                    double used = Math.Min(elapsed, Math.Max(0, _initialHold - _phaseElapsed));
                    _phaseElapsed += used;
                    elapsed -= used;
                    at += used;
                    if (_phaseElapsed + .000001 < _initialHold) return;
                    _phase = 1;
                    _phaseElapsed = 0;
                }
                else
                {
                    double distance = Math.Max(0, MaximumOffset - Offset);
                    if (_firstPassSpeed <= 0)
                    {
                        _firstPassSpeed = _speed;
                        if (!double.IsNaN(deadline) && !double.IsInfinity(deadline))
                        {
                            // Choose this line's traversal speed once. Player-clock corrections
                            // must not make an in-progress sentence accelerate every frame.
                            double tailHold = Math.Min(EndHoldSeconds, Math.Max(0, deadline - _startedAt) * .4);
                            double minimumTravel = Math.Min(.12, Math.Max(.001, (deadline - at) * .35));
                            _firstPassSpeed = Math.Max(_speed, distance / Math.Max(minimumTravel, deadline - at - tailHold));
                        }
                    }
                    double speed = _firstPassSpeed;
                    double duration = distance / speed;
                    double used = Math.Min(elapsed, duration);
                    Offset = Math.Min(MaximumOffset, Offset + used * speed);
                    elapsed -= used;
                    at += used;
                    if (Offset + .001 < MaximumOffset) return;
                    Offset = MaximumOffset;
                    _phase = 2;
                    _phaseElapsed = 0;
                    _firstPass = false;
                }
            }
        }

        internal void UpdateDeadline(double deadline)
        {
            if (!_firstPass || _phase != 0) return;
            double duration = deadline - _startedAt;
            _initialHold = !double.IsNaN(duration) && !double.IsInfinity(duration) && duration < .75
                ? Math.Max(0, duration * .15) : StartHoldSeconds;
        }

        internal double NextWakeSeconds
        {
            get
            {
                if (MaximumOffset <= .1 || !_firstPass) return double.PositiveInfinity;
                if (_phase == 1) return .033;
                return Math.Max(.033, _initialHold - _phaseElapsed);
            }
        }
    }

    public sealed class OverlayPositionChangedEventArgs : EventArgs
    {
        public int HorizontalOffset { get; private set; }
        public int VerticalOffset { get; private set; }
        public int MonitorIndex { get; private set; }
        public string Alignment { get; private set; }
        internal OverlayPositionChangedEventArgs(AppSettings settings)
        {
            HorizontalOffset = settings.HorizontalOffset;
            VerticalOffset = settings.VerticalOffset;
            MonitorIndex = settings.MonitorIndex;
            Alignment = settings.Alignment;
        }
    }

    // A separate, per-pixel alpha window. It does not attach to Explorer or either player.
    public sealed class LyricOverlay : Form
    {
        private sealed class LineDrawing : IDisposable
        {
            internal readonly LyricScrollState Scroll = new LyricScrollState();
            internal double PaintedOffset;
            private GraphicsPath _path;
            private string _text;
            private string _font;
            private float _pixels;
            private float _height;
            private float _width;
            private float _scale;
            private bool _scrolling;
            private SizeF _glyphSize;

            internal void Prepare(string text, AppSettings settings, float pixels, RectangleF row, float scale, double now)
            {
                bool reset = _text != text || _font != settings.FontFamily || _scrolling != settings.LongLineScroll;
                bool rebuild = reset || Math.Abs(_pixels - pixels) > .01f || Math.Abs(_height - row.Height) > .01f
                    || Math.Abs(_scale - scale) > .01f || (!settings.LongLineScroll && Math.Abs(_width - row.Width) > .01f);
                if (rebuild)
                {
                    float previousGlyphWidth = _glyphSize.Width;
                    if (_path != null) { _path.Dispose(); _path = null; }
                    _text = text;
                    _font = settings.FontFamily;
                    _pixels = pixels;
                    _height = row.Height;
                    _width = row.Width;
                    _scale = scale;
                    _scrolling = settings.LongLineScroll;
                    _glyphSize = SizeF.Empty;
                    if (!string.IsNullOrEmpty(text)) BuildPath(text, settings.FontFamily, pixels, row, scale, settings.LongLineScroll);
                    if (reset) Scroll.Reset(now);
                    else if (previousGlyphWidth > 0) Scroll.ScaleOffset(_glyphSize.Width / previousGlyphWidth);
                }
                float padding = Math.Max(1.5f, 2.8f * scale) / 2 + .5f;
                double overflow = settings.LongLineScroll ? Math.Max(0, _glyphSize.Width - Math.Max(1, row.Width - padding * 2)) : 0;
                Scroll.Configure(overflow, 40 * scale, now);
            }

            private void BuildPath(string text, string fontName, float pixels, RectangleF row, float scale, bool scrolling)
            {
                FontFamily family;
                try { family = new FontFamily(fontName); }
                catch (ArgumentException) { family = new FontFamily("Segoe UI"); }
                using (family)
                using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    FontStyle style = family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular
                        : family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold
                        : family.IsStyleAvailable(FontStyle.Italic) ? FontStyle.Italic : FontStyle.Bold | FontStyle.Italic;
                    format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                    GraphicsPath path = new GraphicsPath();
                    try
                    {
                        path.AddString(text, family, (int)style, pixels, PointF.Empty, format);
                        RectangleF glyphs = path.GetBounds();
                        if (glyphs.Width <= 0 || glyphs.Height <= 0) return;
                        float padding = Math.Max(1.5f, 2.8f * scale) / 2 + .5f;
                        float fit = Math.Min(1, Math.Max(1, row.Height - padding * 2) / glyphs.Height);
                        if (!scrolling) fit = Math.Min(fit, Math.Max(1, row.Width - padding * 2) / glyphs.Width);
                        using (Matrix normalize = new Matrix(fit, 0, 0, fit, -glyphs.Left * fit, -glyphs.Top * fit)) path.Transform(normalize);
                        _glyphSize = path.GetBounds().Size;
                        _path = path;
                        path = null;
                    }
                    finally { if (path != null) path.Dispose(); }
                }
            }

            internal void Draw(Graphics graphics, Color color, RectangleF row, float scale, AppSettings settings, bool stationary)
            {
                if (_path == null) return;
                float outlineWidth = Math.Max(1.5f, 2.8f * scale);
                float padding = outlineWidth / 2 + .5f;
                float y = row.Top + (row.Height - _glyphSize.Height) / 2;
                if (settings.FollowTaskbar)
                    y = Math.Max(row.Top + padding, Math.Min(row.Bottom - padding - _glyphSize.Height, y + settings.VerticalOffset * scale));
                float offset = stationary ? 0 : (float)Scroll.Offset;
                GraphicsState saved = graphics.Save();
                try
                {
                    // The clip stays inside the chosen safe taskbar gap, including the grip.
                    graphics.SetClip(row, CombineMode.Intersect);
                    graphics.TranslateTransform(row.Left + padding - offset, y);
                    using (Pen outline = new Pen(Color.FromArgb(220, 10, 15, 24), outlineWidth))
                    using (SolidBrush fill = new SolidBrush(color))
                    {
                        outline.LineJoin = LineJoin.Round;
                        graphics.DrawPath(outline, _path);
                        graphics.FillPath(fill, _path);
                    }
                }
                finally { graphics.Restore(saved); }
                PaintedOffset = offset;
            }

            public void Dispose()
            {
                if (_path != null) { _path.Dispose(); _path = null; }
            }
        }

        private sealed class OccupancyCache
        {
            public IntPtr Handle;
            public Rectangle Bounds;
            public DateTime Timestamp;
            public bool HasControls;
            public List<Rectangle> Rectangles = new List<Rectangle>();
        }

        private readonly Timer _layoutTimer;
        private readonly Timer _dragTimer;
        private readonly Timer _animationTimer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly LineDrawing _currentLine = new LineDrawing();
        private readonly LineDrawing _secondLine = new LineDrawing();
        private readonly LineDrawing _previewCurrentLine = new LineDrawing();
        private readonly LineDrawing _previewSecondLine = new LineDrawing();
        private readonly ContextMenuStrip _menu;
        private readonly object _cacheLock = new object();
        private AppSettings _settings = new AppSettings();
        private string _current = "";
        private string _next = "";
        private string _translation = "";
        private string _lineKey = "";
        private double _lineDeadline = double.NaN;
        private bool _playing = true;
        private double _pauseStarted;
        private double _pausedSeconds;
        private bool _hasContent;
        private bool _preview;
        private bool _dirty = true;
        private volatile bool _scanBusy;
        private volatile bool _disposed;
        private OccupancyCache _cache;
        private IntPtr _lastScanHandle;
        private Rectangle _lastScanBounds;
        private DateTime _nextScanUtc;
        private Rectangle _paintedBounds;
        private bool _paintedRotate;
        private float _paintedScale;
        private TaskbarSnapshot _lastBar;
        private List<Rectangle> _lastOccupied = new List<Rectangle>();
        private OverlayLayout _lastLayout;
        private bool _dragging;
        private Point _dragCursorStart;
        private Point _dragPreviousCursor;
        private int _dragWindowStart;
        private int _dragOriginalOffset;
        private int _dragOriginalVertical;
        private string _dragOriginalAlignment;
        private bool _publishingPosition;

        public event EventHandler OpenSettingsRequested;
        public event EventHandler ExitRequested;
        public event EventHandler<OverlayPositionChangedEventArgs> PositionChanged;
        public event EventHandler<OverlayPositionChangedEventArgs> DragCompleted;
        public string LayoutStatus { get; private set; }

        public LyricOverlay()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "MusicBar 任务栏歌词";
            Size = new Size(420, 48);
            _menu = Theme.Menu();
            _menu.Items.Add("歌词设置", null, delegate { Raise(OpenSettingsRequested); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出歌词工具", null, delegate { Raise(ExitRequested); });
            ContextMenuStrip = _menu;
            _layoutTimer = new Timer();
            _layoutTimer.Interval = 250;
            _layoutTimer.Tick += delegate { RefreshOverlay(); };
            _layoutTimer.Start();
            _dragTimer = new Timer { Interval = 20 };
            _dragTimer.Tick += delegate
            {
                if (!_dragging) return;
                Point cursor;
                if (OverlayNative.TryCursorPosition(out cursor)) MoveDrag(cursor);
                if (!OverlayNative.LeftMouseDown) FinishDrag();
            };
            _animationTimer = new Timer { Interval = 33 };
            _animationTimer.Tick += delegate { AnimateOverlay(); };
            LayoutStatus = "等待歌词";
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; // LAYERED, TOOLWINDOW, NOACTIVATE
                if (_settings.ClickThrough) parameters.ExStyle |= 0x00000020;
                return parameters;
            }
        }

        public void UpdateSettings(AppSettings settings)
        {
            if (settings == null) return;
            AppSettings copy = new AppSettings();
            copy.OverlayEnabled = settings.OverlayEnabled;
            copy.TwoLines = settings.TwoLines;
            copy.ShowTranslation = settings.ShowTranslation;
            copy.LongLineScroll = settings.LongLineScroll;
            copy.ClickThrough = settings.ClickThrough;
            copy.FollowTaskbar = settings.FollowTaskbar;
            copy.PreviewEnabled = settings.PreviewEnabled;
            copy.FontFamily = settings.FontFamily;
            copy.FontSize = float.IsNaN(settings.FontSize) || float.IsInfinity(settings.FontSize) ? 14 : settings.FontSize;
            copy.LyricBrightness = settings.LyricBrightness;
            copy.TextColor = settings.TextColor;
            copy.ActiveColor = settings.ActiveColor;
            copy.Width = settings.Width;
            copy.HorizontalOffset = settings.HorizontalOffset;
            copy.VerticalOffset = settings.VerticalOffset;
            copy.MonitorIndex = settings.MonitorIndex;
            copy.Alignment = settings.Alignment;
            copy.Normalize();
            OnUi(delegate
            {
                if (_dragging)
                {
                    bool sameRegion = copy.MonitorIndex == _settings.MonitorIndex && copy.FollowTaskbar == _settings.FollowTaskbar;
                    if (!sameRegion || copy.ClickThrough) FinishDrag();
                    if (sameRegion)
                    {
                        // Controller updates during playback must not reset an in-progress drag.
                        copy.HorizontalOffset = _settings.HorizontalOffset;
                        copy.VerticalOffset = _settings.VerticalOffset;
                        copy.Alignment = _settings.Alignment;
                    }
                }
                if (SameSettings(_settings, copy)) return;
                bool reset = _settings.TwoLines != copy.TwoLines || _settings.ShowTranslation != copy.ShowTranslation
                    || _settings.LongLineScroll != copy.LongLineScroll || _settings.FontFamily != copy.FontFamily
                    || Math.Abs(_settings.FontSize - copy.FontSize) > .01f;
                _settings = copy;
                bool previewChanged = _preview != copy.PreviewEnabled;
                _preview = copy.PreviewEnabled;
                if (reset || previewChanged) ResetScrolling();
                _dirty = true;
                Cursor = copy.ClickThrough ? Cursors.Default : Cursors.SizeWE;
                if (IsHandleCreated) ApplyMouseMode();
                RefreshOverlay();
            });
        }

        public void SetLyrics(string current, string next, bool hasContent)
        {
            SetLyrics(current, next, "", hasContent, false);
        }

        public void SetLyrics(string current, string next, string translation, bool hasContent, bool resetScroll = false)
        {
            string cleanCurrent = Clean(current);
            string cleanNext = Clean(next);
            string cleanTranslation = Clean(translation);
            OnUi(delegate
            {
                if (!resetScroll && _current == cleanCurrent && _next == cleanNext && _translation == cleanTranslation && _hasContent == hasContent) return;
                bool currentChanged = _current != cleanCurrent;
                string previousSecond = SecondText(false);
                _current = cleanCurrent;
                _next = cleanNext;
                _translation = cleanTranslation;
                _hasContent = hasContent;
                if (currentChanged || resetScroll)
                {
                    _currentLine.Scroll.Reset(AnimationTime);
                    _secondLine.Scroll.Reset(AnimationTime);
                }
                else if (previousSecond != SecondText(false)) _secondLine.Scroll.Reset(AnimationTime);
                _dirty = true;
                RefreshOverlay();
            });
        }

        // remainingSeconds is wall-clock time until the next timed lyric. NaN means unknown.
        // Repeated notifications for the same key update the target without restarting a line.
        public void SetLineTiming(string lineKey, double remainingSeconds, bool playing)
        {
            string key = lineKey ?? "";
            OnUi(delegate
            {
                if (_playing != playing)
                {
                    double previousTime = AnimationTime;
                    _currentLine.Scroll.Advance(previousTime, _lineDeadline);
                    _secondLine.Scroll.Advance(previousTime, _lineDeadline);
                    if (playing) _pausedSeconds += _clock.Elapsed.TotalSeconds - _pauseStarted;
                    else _pauseStarted = _clock.Elapsed.TotalSeconds;
                    _playing = playing;
                }
                double now = AnimationTime;
                _lineDeadline = remainingSeconds > 0 && !double.IsNaN(remainingSeconds) && !double.IsInfinity(remainingSeconds)
                    ? now + remainingSeconds : double.NaN;
                if (_lineKey != key)
                {
                    _lineKey = key;
                    _currentLine.Scroll.Reset(now);
                    _secondLine.Scroll.Reset(now);
                    _currentLine.Scroll.UpdateDeadline(_lineDeadline);
                    _secondLine.Scroll.UpdateDeadline(_lineDeadline);
                    _dirty = true;
                    RefreshOverlay();
                }
                else
                {
                    _currentLine.Scroll.UpdateDeadline(_lineDeadline);
                    _secondLine.Scroll.UpdateDeadline(_lineDeadline);
                    UpdateAnimationTimer();
                }
            });
        }

        public void ResetScroll()
        {
            OnUi(delegate { ResetScrolling(); _dirty = true; RefreshOverlay(); });
        }

        public void SetPreview(bool enabled)
        {
            OnUi(delegate
            {
                if (_preview == enabled) return;
                _preview = enabled;
                ResetScrolling();
                _dirty = true;
                RefreshOverlay();
            });
        }

        // The caller owns this transparent bitmap and should dispose it when replacing it.
        // This preview always contains an explicit label and does not depend on a running player.
        public Bitmap RenderPreview(int width, int height)
        {
            width = Math.Max(16, Math.Min(4096, width));
            height = Math.Max(16, Math.Min(512, height));
            return DrawLyrics(width, height, 1, true, true);
        }

        private void RefreshOverlay()
        {
            if (_disposed || IsDisposed || Disposing) return;
            bool hasText = _hasContent && (!string.IsNullOrWhiteSpace(_current) || !string.IsNullOrWhiteSpace(SecondText(false)));
            if (!_settings.OverlayEnabled || (!_preview && !hasText))
            {
                HideOverlay(_settings.OverlayEnabled ? "等待歌词" : "歌词显示已关闭");
                return;
            }
            TaskbarSnapshot bar;
            if (!TaskbarGeometry.TryFind(_settings.MonitorIndex, out bar))
            {
                HideOverlay("任务栏未显示或尚未找到，歌词已隐藏");
                return;
            }
            _layoutTimer.Interval = bar.AutoHide ? 100 : 250;
            // Hover is the reliable reveal signal with an auto-hidden bar. Hiding as soon as the
            // pointer leaves prevents a separate topmost window remaining during its collapse.
            if (bar.AutoHide && !OverlayNative.CursorIn(bar.Bounds))
            {
                HideOverlay("任务栏自动隐藏：鼠标移到任务栏后显示歌词");
                return;
            }
            if (TaskbarGeometry.ForegroundIsFullscreen(bar.MonitorBounds, Handle))
            {
                HideOverlay("当前显示器正在全屏，歌词已隐藏");
                return;
            }

            List<Rectangle> occupied = new List<Rectangle>(bar.NativeOccupied);
            if (_settings.FollowTaskbar)
            {
                RequestOccupancyScan(bar);
                OccupancyCache cached;
                lock (_cacheLock) cached = _cache;
                if (cached == null || !cached.HasControls || cached.Handle != bar.Handle || cached.Bounds != bar.Bounds || (DateTime.UtcNow - cached.Timestamp).TotalSeconds > 20)
                {
                    HideOverlay(cached != null && cached.Handle == bar.Handle && !cached.HasControls
                        ? "无法识别任务栏按钮，歌词已隐藏；可选择任务栏上方模式"
                        : "正在识别任务栏空位；也可选择任务栏上方模式");
                    return;
                }
                occupied.AddRange(cached.Rectangles);
                // Windows 11's weather/widgets surface can omit a useful UIA rectangle. Reserve
                // its usual left region as well as all actual button rectangles from the scan.
                if (OverlayNative.Windows11 && bar.Horizontal)
                {
                    int weather = Math.Min(bar.Bounds.Width, (int)Math.Round(180 * bar.Scale));
                    occupied.Add(new Rectangle(bar.Bounds.Left, bar.Bounds.Top, weather, bar.Bounds.Height));
                }
                // Include the clock and unlabelled tray surfaces when the native tray container
                // is absent. The real tray buttons still add their own larger reserved area.
                bool nativeTray = false;
                foreach (Rectangle rectangle in bar.NativeOccupied)
                    if (bar.Horizontal ? rectangle.Right >= bar.Bounds.Right - 2 : rectangle.Bottom >= bar.Bounds.Bottom - 2) nativeTray = true;
                if (!nativeTray)
                {
                    int tray = (int)Math.Round(240 * bar.Scale);
                    if (bar.Horizontal) occupied.Add(new Rectangle(Math.Max(bar.Bounds.Left, bar.Bounds.Right - tray), bar.Bounds.Top, Math.Min(tray, bar.Bounds.Width), bar.Bounds.Height));
                    else occupied.Add(new Rectangle(bar.Bounds.Left, Math.Max(bar.Bounds.Top, bar.Bounds.Bottom - tray), bar.Bounds.Width, Math.Min(tray, bar.Bounds.Height)));
                }
            }

            OverlayLayout layout = TaskbarGeometry.Calculate(bar, GeometrySettings(), occupied);
            ApplyLayout(bar, occupied, layout);
        }

        private void ApplyLayout(TaskbarSnapshot bar, IList<Rectangle> occupied, OverlayLayout layout)
        {
            _lastBar = bar;
            _lastOccupied = new List<Rectangle>(occupied);
            _lastLayout = layout;
            if (layout.Bounds.Width < 1 || layout.Bounds.Height < 1)
            {
                HideOverlay("任务栏空位不足，歌词已隐藏；可缩小宽度或选择任务栏上方模式");
                return;
            }
            bool needsPaint = _dirty || _paintedBounds.Size != layout.Bounds.Size || _paintedRotate != layout.Rotate || Math.Abs(_paintedScale - layout.Scale) > .01f;
            if (needsPaint && !PaintLayout(layout)) return;
            if (!Visible) Show();
            // Native positioning explicitly forbids activation, including after Explorer restarts.
            OverlayNative.SetWindowPos(Handle, new IntPtr(-1), layout.Bounds.X, layout.Bounds.Y, layout.Bounds.Width, layout.Bounds.Height, 0x0010 | 0x0200);
            _paintedBounds = layout.Bounds;
            if (!_settings.ClickThrough) Cursor = layout.Rotate ? Cursors.SizeNS : Cursors.SizeWE;
            LayoutStatus = _dragging ? "正在拖动歌词，松开鼠标保存位置"
                : layout.PositionClamped ? (_settings.HorizontalOffset < 0 ? "已到左侧可用边界" : "已到右侧可用边界")
                : _preview ? "预览歌词" : (_settings.FollowTaskbar ? "任务栏歌词已显示" : "歌词显示在任务栏上方");
            if (!_dragging && layout.PositionClamped && _settings.HorizontalOffset != layout.ResolvedHorizontalOffset)
            {
                _settings.HorizontalOffset = layout.ResolvedHorizontalOffset;
                PublishPosition(false);
            }
            UpdateAnimationTimer();
        }

        private bool PaintLayout(OverlayLayout layout)
        {
            int canvasWidth = layout.Rotate ? layout.Bounds.Height : layout.Bounds.Width;
            int canvasHeight = layout.Rotate ? layout.Bounds.Width : layout.Bounds.Height;
            using (Bitmap bitmap = DrawLyrics(canvasWidth, canvasHeight, layout.Scale, _preview, false))
            {
                if (layout.Rotate) bitmap.RotateFlip(layout.RotateClockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
                if (!Present(bitmap, layout.Bounds))
                {
                    HideOverlay("歌词窗口暂时无法绘制");
                    _dirty = true;
                    return false;
                }
            }
            _paintedBounds = layout.Bounds;
            _paintedRotate = layout.Rotate;
            _paintedScale = layout.Scale;
            _dirty = false;
            return true;
        }

        private double AnimationTime
        {
            get { return (_playing ? _clock.Elapsed.TotalSeconds : _pauseStarted) - _pausedSeconds; }
        }

        private string SecondText(bool preview)
        {
            if (preview)
                return _settings.ShowTranslation ? "译文预览 · 你的歌正在播放"
                    : _settings.TwoLines ? "下一句歌词 · QQ 音乐 / 网易云音乐" : "";
            if (_settings.ShowTranslation && !string.IsNullOrWhiteSpace(_translation)) return _translation;
            return _settings.TwoLines ? _next : "";
        }

        private AppSettings GeometrySettings()
        {
            bool twoLines = !string.IsNullOrWhiteSpace(SecondText(_preview));
            if (_settings.FollowTaskbar || _settings.TwoLines == twoLines) return _settings;
            // The above-taskbar mode needs room for a translation even with next-line display off.
            return new AppSettings
            {
                FollowTaskbar = false, TwoLines = twoLines, FontSize = _settings.FontSize,
                Width = _settings.Width, Alignment = _settings.Alignment, HorizontalOffset = _settings.HorizontalOffset,
                VerticalOffset = _settings.VerticalOffset, MonitorIndex = _settings.MonitorIndex
            };
        }

        private static bool SameSettings(AppSettings left, AppSettings right)
        {
            return left.OverlayEnabled == right.OverlayEnabled && left.TwoLines == right.TwoLines
                && left.ShowTranslation == right.ShowTranslation && left.LongLineScroll == right.LongLineScroll
                && left.ClickThrough == right.ClickThrough && left.FollowTaskbar == right.FollowTaskbar
                && left.PreviewEnabled == right.PreviewEnabled && left.FontFamily == right.FontFamily
                && left.FontSize == right.FontSize && left.LyricBrightness == right.LyricBrightness && left.TextColor == right.TextColor && left.ActiveColor == right.ActiveColor
                && left.Width == right.Width && left.HorizontalOffset == right.HorizontalOffset
                && left.VerticalOffset == right.VerticalOffset && left.MonitorIndex == right.MonitorIndex && left.Alignment == right.Alignment;
        }

        private void ResetScrolling()
        {
            _currentLine.Scroll.Reset(AnimationTime);
            _secondLine.Scroll.Reset(AnimationTime);
            _previewCurrentLine.Scroll.Reset(_clock.Elapsed.TotalSeconds);
            _previewSecondLine.Scroll.Reset(_clock.Elapsed.TotalSeconds);
        }

        private void AnimateOverlay()
        {
            if (_disposed || !Visible || _lastLayout == null || !_settings.LongLineScroll || (!_preview && !_playing))
            {
                _animationTimer.Stop();
                return;
            }
            LineDrawing first = _preview ? _previewCurrentLine : _currentLine;
            LineDrawing second = _preview ? _previewSecondLine : _secondLine;
            double now = _preview ? _clock.Elapsed.TotalSeconds : AnimationTime;
            double deadline = _preview ? double.NaN : _lineDeadline;
            first.Scroll.Advance(now, deadline);
            bool twoLines = !string.IsNullOrWhiteSpace(SecondText(_preview));
            if (twoLines) second.Scroll.Advance(now, deadline);
            bool moved = Math.Abs(first.Scroll.Offset - first.PaintedOffset) > .05
                || (twoLines && Math.Abs(second.Scroll.Offset - second.PaintedOffset) > .05);
            if (moved && !PaintLayout(_lastLayout)) return;
            UpdateAnimationTimer();
        }

        private void UpdateAnimationTimer()
        {
            if (_disposed || !Visible || !_settings.LongLineScroll || (!_preview && !_playing))
            {
                _animationTimer.Stop();
                return;
            }
            LineDrawing first = _preview ? _previewCurrentLine : _currentLine;
            LineDrawing second = _preview ? _previewSecondLine : _secondLine;
            double next = first.Scroll.NextWakeSeconds;
            if (!string.IsNullOrWhiteSpace(SecondText(_preview))) next = Math.Min(next, second.Scroll.NextWakeSeconds);
            if (double.IsInfinity(next)) { _animationTimer.Stop(); return; }
            // Holds need one wakeup; only visible movement is painted at 30 frames per second.
            _animationTimer.Interval = Math.Max(33, Math.Min(1200, (int)Math.Ceiling(next * 1000)));
            _animationTimer.Start();
        }

        private void RequestOccupancyScan(TaskbarSnapshot bar)
        {
            DateTime now = DateTime.UtcNow;
            bool changed = _lastScanHandle != bar.Handle || _lastScanBounds != bar.Bounds;
            if (_scanBusy || (!changed && now < _nextScanUtc)) return;
            _lastScanHandle = bar.Handle;
            _lastScanBounds = bar.Bounds;
            _nextScanUtc = now.AddSeconds(5);
            _scanBusy = true;
            // UIA providers belong to Explorer and can take an unbounded time to respond. A
            // single background job is allowed; the UI never waits for it or queues more jobs.
            Task.Factory.StartNew(delegate
            {
                OccupancyCache result = new OccupancyCache();
                result.Handle = bar.Handle;
                result.Bounds = bar.Bounds;
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(bar.Handle);
                    if (root != null)
                    {
                        Condition condition = new OrCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                        CacheRequest request = new CacheRequest();
                        request.Add(AutomationElement.BoundingRectangleProperty);
                        request.Add(AutomationElement.IsOffscreenProperty);
                        request.AutomationElementMode = AutomationElementMode.None;
                        request.TreeScope = TreeScope.Element;
                        AutomationElementCollection controls;
                        using (request.Activate()) controls = root.FindAll(TreeScope.Descendants, condition);
                        int count = Math.Min(250, controls.Count);
                        for (int i = 0; i < count; i++)
                        {
                            AutomationElement control = controls[i];
                            if ((bool)control.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty)) continue;
                            System.Windows.Rect rectangle = (System.Windows.Rect)control.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                            if (rectangle.IsEmpty || rectangle.Width < 1 || rectangle.Height < 1 || double.IsInfinity(rectangle.Left) || double.IsNaN(rectangle.Left)) continue;
                            Rectangle rounded = Rectangle.FromLTRB((int)Math.Floor(rectangle.Left), (int)Math.Floor(rectangle.Top), (int)Math.Ceiling(rectangle.Right), (int)Math.Ceiling(rectangle.Bottom));
                            Rectangle clipped = Rectangle.Intersect(bar.Bounds, rounded);
                            int length = bar.Horizontal ? clipped.Width : clipped.Height;
                            int barLength = bar.Horizontal ? bar.Bounds.Width : bar.Bounds.Height;
                            int cross = bar.Horizontal ? clipped.Height : clipped.Width;
                            int barCross = bar.Horizontal ? bar.Bounds.Height : bar.Bounds.Width;
                            if (length <= 0 || cross < barCross / 4 || length > barLength * .65) continue;
                            result.Rectangles.Add(clipped);
                            int controlStart = bar.Horizontal ? clipped.Left - bar.Bounds.Left : clipped.Top - bar.Bounds.Top;
                            if (controlStart < barLength * .75) result.HasControls = true;
                        }
                    }
                }
                catch (Exception) { result.HasControls = false; }
                finally
                {
                    result.Timestamp = DateTime.UtcNow;
                    if (!_disposed) lock (_cacheLock) _cache = result;
                    _scanBusy = false;
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        private Bitmap DrawLyrics(int width, int height, float scale, bool preview, bool stationary)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            LineDrawing first = stationary ? new LineDrawing() : preview ? _previewCurrentLine : _currentLine;
            LineDrawing second = stationary ? new LineDrawing() : preview ? _previewSecondLine : _secondLine;
            double now = preview ? _clock.Elapsed.TotalSeconds : AnimationTime;
            double deadline = preview ? double.NaN : _lineDeadline;
            try
            {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                bool unlocked = !_settings.ClickThrough;
                if (unlocked)
                {
                    // Nonzero alpha makes the whole unlocked strip hit-testable, rather than
                    // requiring the user to grab an individual painted glyph.
                    using (SolidBrush panel = new SolidBrush(Color.FromArgb(48, 17, 24, 39))) graphics.FillRectangle(panel, 0, 0, width, height);
                    using (Pen border = new Pen(Color.FromArgb(105, _settings.Highlight), Math.Max(1, scale))) graphics.DrawRectangle(border, .5f * scale, .5f * scale, Math.Max(1, width - scale), Math.Max(1, height - scale));
                    using (SolidBrush grip = new SolidBrush(Color.FromArgb(185, _settings.Highlight)))
                        for (int i = -1; i <= 1; i++) graphics.FillEllipse(grip, 7 * scale, height / 2f + i * 5 * scale - scale, 2 * scale, 2 * scale);
                }
                string current = preview ? "预览 · 风吹过的地方，你的歌正在播放" : _current;
                string next = SecondText(preview);
                int lines = !string.IsNullOrWhiteSpace(next) ? 2 : 1;
                float margin = Math.Max(2, 4 * scale);
                float verticalMargin = Math.Max(2, 2 * scale);
                float rowGap = lines == 2 ? Math.Max(1, scale) : 0;
                float leftMargin = unlocked ? Math.Max(margin, 18 * scale) : margin;
                float rowHeight = Math.Max(1, (height - verticalMargin * 2 - rowGap) / lines);
                // Fit the actual glyph bounds in BuildPath, avoiding a second reduction of
                // the font's em size before measuring narrow Latin or CJK glyphs.
                float pixels = _settings.FontSize * (96f / 72f) * scale;
                pixels = Math.Max(5, pixels);
                RectangleF firstRow = new RectangleF(leftMargin, verticalMargin, Math.Max(1, width - leftMargin - margin), rowHeight);
                first.Prepare(current, _settings, pixels, firstRow, scale, now);
                if (!stationary) first.Scroll.Advance(now, deadline);
                first.Draw(graphics, _settings.Brightened(_settings.Highlight), firstRow, scale, _settings, stationary);
                if (lines == 2)
                {
                    RectangleF secondRow = new RectangleF(leftMargin, verticalMargin + rowHeight + rowGap, Math.Max(1, width - leftMargin - margin), rowHeight);
                    second.Prepare(next, _settings, pixels, secondRow, scale, now);
                    if (!stationary) second.Scroll.Advance(now, deadline);
                    second.Draw(graphics, Color.FromArgb(235, _settings.Brightened(_settings.Foreground)), secondRow, scale, _settings, stationary);
                }
            }
            }
            catch { bitmap.Dispose(); throw; }
            finally
            {
                if (stationary) { first.Dispose(); second.Dispose(); }
            }
            return bitmap;
        }

        private bool Present(Bitmap bitmap, Rectangle bounds)
        {
            IntPtr screenDc = OverlayNative.GetDC(IntPtr.Zero);
            IntPtr memoryDc = IntPtr.Zero, nativeBitmap = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                memoryDc = OverlayNative.CreateCompatibleDC(screenDc);
                nativeBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                previous = OverlayNative.SelectObject(memoryDc, nativeBitmap);
                OverlayNative.Point destination = new OverlayNative.Point(bounds.X, bounds.Y);
                OverlayNative.Point source = new OverlayNative.Point(0, 0);
                OverlayNative.Size size = new OverlayNative.Size(bitmap.Width, bitmap.Height);
                OverlayNative.BlendFunction blend = new OverlayNative.BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                return OverlayNative.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, 2);
            }
            finally
            {
                if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) OverlayNative.SelectObject(memoryDc, previous);
                if (nativeBitmap != IntPtr.Zero) OverlayNative.DeleteObject(nativeBitmap);
                if (memoryDc != IntPtr.Zero) OverlayNative.DeleteDC(memoryDc);
                if (screenDc != IntPtr.Zero) OverlayNative.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void ApplyMouseMode()
        {
            long style = OverlayNative.GetExtendedStyle(Handle).ToInt64();
            if (_settings.ClickThrough) style |= 0x00000020;
            else style &= ~0x00000020;
            style |= 0x00080000 | 0x00000080 | 0x08000000;
            OverlayNative.SetExtendedStyle(Handle, new IntPtr(style));
        }

        private void HideOverlay(string reason)
        {
            _animationTimer.Stop();
            if (_dragging) FinishDrag();
            LayoutStatus = reason;
            if (Visible) Hide();
        }

        private void OnUi(Action action)
        {
            if (_disposed || IsDisposed) return;
            if (IsHandleCreated && InvokeRequired)
            {
                try { BeginInvoke(action); }
                catch (InvalidOperationException) { }
                return;
            }
            action();
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }
        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return value;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!_settings.ClickThrough && e.Button == MouseButtons.Left) Raise(OpenSettingsRequested);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _settings.ClickThrough || !Visible || _lastBar == null || _lastLayout == null || _lastLayout.Bounds.IsEmpty) return;
            _dragging = true;
            _dragCursorStart = PointToScreen(e.Location);
            _dragPreviousCursor = _dragCursorStart;
            _dragWindowStart = _settings.FollowTaskbar && !_lastBar.Horizontal ? _lastLayout.Bounds.Top : _lastLayout.Bounds.Left;
            _dragOriginalOffset = _settings.HorizontalOffset;
            _dragOriginalVertical = _settings.VerticalOffset;
            _dragOriginalAlignment = _settings.Alignment;
            Capture = true;
            _dragTimer.Start();
            LayoutStatus = "正在拖动歌词，松开鼠标保存位置";
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) MoveDrag(PointToScreen(e.Location));
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging && e.Button == MouseButtons.Left)
            {
                MoveDrag(PointToScreen(e.Location));
                FinishDrag();
            }
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_dragging && !Capture) FinishDrag();
        }
        private void MoveDrag(Point cursor)
        {
            if (!_dragging || _lastBar == null) return;
            bool vertical = _settings.FollowTaskbar && !_lastBar.Horizontal;
            int current = vertical ? cursor.Y : cursor.X;
            int previous = vertical ? _dragPreviousCursor.Y : _dragPreviousCursor.X;
            int direction = current.CompareTo(previous);
            if (direction == 0) return;
            int origin = vertical ? _dragCursorStart.Y : _dragCursorStart.X;
            _dragPreviousCursor = cursor;
            int desired = _dragWindowStart + current - origin;
            _settings.HorizontalOffset = TaskbarGeometry.OffsetForPosition(_lastBar, GeometrySettings(), _lastOccupied, desired, direction);
            OverlayLayout layout = TaskbarGeometry.Calculate(_lastBar, GeometrySettings(), _lastOccupied);
            ApplyLayout(_lastBar, _lastOccupied, layout);
        }
        private void FinishDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            _dragTimer.Stop();
            Capture = false;
            bool changed = _settings.HorizontalOffset != _dragOriginalOffset || _settings.VerticalOffset != _dragOriginalVertical || _settings.Alignment != _dragOriginalAlignment;
            LayoutStatus = changed ? "歌词位置已调整" : "歌词位置未改变";
            if (changed && !_disposed) PublishPosition(true);
        }
        private void PublishPosition(bool dragCompleted)
        {
            if (_publishingPosition || _disposed) return;
            _publishingPosition = true;
            try
            {
                OverlayPositionChangedEventArgs args = new OverlayPositionChangedEventArgs(_settings);
                if (PositionChanged != null) PositionChanged(this, args);
                if (dragCompleted && DragCompleted != null) DragCompleted(this, args);
            }
            finally { _publishingPosition = false; }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            if (message.Msg == 0x0084 && _settings.ClickThrough) { message.Result = new IntPtr(-1); return; }
            base.WndProc(ref message);
            if (message.Msg == 0x007E || message.Msg == 0x001A || message.Msg == 0x02E0) _dirty = true;
        }
        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            if (disposing)
            {
                _layoutTimer.Stop();
                _layoutTimer.Dispose();
                _dragTimer.Stop();
                _dragTimer.Dispose();
                _animationTimer.Stop();
                _animationTimer.Dispose();
                _clock.Stop();
                _currentLine.Dispose();
                _secondLine.Dispose();
                _previewCurrentLine.Dispose();
                _previewSecondLine.Dispose();
                _menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
