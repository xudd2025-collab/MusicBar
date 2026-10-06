using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBar
{
    public sealed class MainForm : Form
    {
        private readonly LyricController controller;
        private readonly LyricOverlay overlay;
        private readonly NotifyIcon tray;
        private readonly Panel content = new Panel();
        private readonly List<Panel> pages = new List<Panel>();
        private readonly List<Button> navigation = new List<Button>();
        private Label headerStatus, footerStatus, songTitle, songArtist, liveLine, liveTranslation, connectionHint, qqStatus, neteaseStatus, lyricStatus, manualStatus;
        private Label positionLabel;
        private Button previewButton, manualButton, usePlayerButton, dragButton, neteaseLaunchButton;
        private readonly System.Windows.Forms.Timer neteaseStartTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private string pendingNetEasePath = "", neteaseRecoveryStatus = "";
        private DateTime neteaseStartDeadline;
        private bool verifyingNetEaseStart;
        private CheckBox qqSwitch, neteaseSwitch, overlaySwitch, onlineSwitch, positionLock;
        private ComboBox preferred, lyricSource, positionAlignment;
        private TextBox query;
        private ListBox results;
        private Button searchButton, fetchButton;
        private NumericUpDown manualSeconds;
        private NumericUpDown horizontalPosition, verticalPosition;
        private PictureBox previewImage;
        private CancellationTokenSource searchRequest;
        private CancellationTokenSource lyricStatusRequest;
        private string displayedControllerMessage = "", displayedStatusTrackKey = "";
        private string resultsTrackKey = "";
        private bool updating = true;
        private bool exiting;
        private bool positionPreview;
        private int lastLineIndex = -2;
        private ListBox lyricLines;
        private CheckBox startupSwitch;
        private Label startupHint;
        private readonly bool startInTray;
        private readonly AppUpdateService updateService;
        private readonly AppHotkey appHotkey = new AppHotkey();
        private Label hotkeyHint;
        private readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private RoundedButton versionButton;
        private Button checkUpdateButton, downloadUpdateButton;
        private Label updateStatus, updateVersion, updateNotes, updateProgressText;
        private DownloadProgress updateProgress;
        private bool updateBusy;
        private string downloadedUpdate = "";
        private UpdateRelease downloadedRelease;
        private DateTime nextUpdateCheck = DateTime.UtcNow.AddSeconds(5), lastUpdateCheck = DateTime.MinValue;

        public MainForm(LyricController controller, LyricOverlay overlay, bool startInTray = false)
        {
            this.controller = controller;
            this.overlay = overlay;
            this.startInTray = startInTray;
            ShowInTaskbar = !controller.Settings.HideTaskbarIcon;
            updateService = new AppUpdateService(AppDomain.CurrentDomain.BaseDirectory, controller.Store.DataDirectory);
            RememberNetEasePath();
            Text = "任务栏歌词 · MusicBar";
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Font(10, FontStyle.Regular);
            Icon = Theme.CreateIcon();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(1160, 800);
            MinimumSize = new Size(1050, 780);
            StartPosition = FormStartPosition.CenterScreen;
            BuildInterface();
            Theme.ApplyDarkControls(this);
            var menu = Theme.Menu();
            menu.Items.Add("打开插件和歌词设置", null, delegate { RestoreWindow(); });
            menu.Items.Add("显示 / 隐藏任务栏歌词", null, delegate
            {
                controller.Settings.OverlayEnabled = !controller.Settings.OverlayEnabled;
                updating = true; overlaySwitch.Checked = controller.Settings.OverlayEnabled; updating = false;
                Apply(false);
            });
            menu.Items.Add("锁定 / 解锁歌词拖动", null, delegate { TogglePositionLock(); });
            menu.Items.Add("退出", null, delegate { ExitApplication(); });
            tray = new NotifyIcon { Icon = Icon, Text = "MusicBar · 任务栏歌词", ContextMenuStrip = menu, Visible = false };
            tray.DoubleClick += delegate { RestoreWindow(); };
            overlay.OpenSettingsRequested += delegate { RestoreWindow(); };
            overlay.ExitRequested += delegate { ExitApplication(); };
            overlay.PositionChanged += delegate(object sender, OverlayPositionChangedEventArgs args)
            {
                controller.Settings.HorizontalOffset = args.HorizontalOffset;
                controller.Settings.VerticalOffset = args.VerticalOffset;
                controller.Settings.MonitorIndex = args.MonitorIndex;
                controller.Settings.Alignment = args.Alignment;
                SyncPositionControls(); controller.Store.Save(controller.Settings); controller.Notify();
            };
            controller.Changed += ControllerChanged;
            controller.PlaybackFrame += PlaybackFrameChanged;
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; tray.Visible = true; Hide(); }
            };
            Resize += delegate { if (WindowState == FormWindowState.Minimized) { tray.Visible = true; Hide(); } };
            Shown += delegate { tray.Visible = true; if (this.startInTray) BeginInvoke((MethodInvoker)delegate { ShowInTaskbar = false; Hide(); }); };
            updating = false;
            overlay.UpdateSettings(controller.Settings);
            ControllerChanged(this, EventArgs.Empty);
            RefreshUpdateView();
            updateTimer.Tick += async delegate { if (controller.Settings.AutoCheckUpdates && !updateBusy && DateTime.UtcNow >= nextUpdateCheck) await CheckForUpdates(); };
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            updateTimer.Start();
            neteaseStartTimer.Tick += CompleteNetEaseStart;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyDarkWindow(Handle);
            UpdateHotkey();
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            appHotkey.Dispose(); base.OnHandleDestroyed(e);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == AppHotkey.Id)
            { RestoreWindow(); return; }
            base.WndProc(ref message);
        }
        private void UpdateHotkey()
        {
            bool enabled = controller.Settings.EnableHotkey;
            bool success = false;
            if (enabled && IsHandleCreated) success = appHotkey.Register(Handle, controller.Settings.EffectiveHotkeyModifiers, controller.Settings.EffectiveHotkeyKey);
            else appHotkey.Dispose();
            if (hotkeyHint != null) hotkeyHint.Text = !enabled ? "快捷键已关闭，可双击托盘图标打开。" : success ? "在其他软件中也可按 " + AppHotkey.Describe(controller.Settings.EffectiveHotkeyKey, controller.Settings.EffectiveHotkeyModifiers) + " 打开 MusicBar。\n点击按键框可录入 F1 等单个功能键或组合键。" : "快捷键未注册，可能已被系统或其他软件占用；请设置另一组合。";
        }
        private bool SaveHotkey(bool custom, int key, int modifiers)
        {
            if (!AppSettings.ValidHotkey(key, modifiers)) return false;
            if (controller.Settings.EnableHotkey && IsHandleCreated && !appHotkey.Register(Handle, modifiers, key))
            {
                UpdateHotkey();
                hotkeyHint.Text = "该组合键被系统或其他软件占用，未保存。原快捷键已保留，请换一个组合。";
                return false;
            }
            controller.Settings.UseCustomHotkey = custom;
            controller.Settings.HotkeyKey = key;
            controller.Settings.HotkeyModifiers = modifiers;
            if (!custom) controller.Settings.HotkeyPreset = 0;
            UpdateHotkey();
            if (!controller.Store.Save(controller.Settings)) hotkeyHint.Text = controller.Store.LastError;
            return true;
        }

        private void BuildInterface()
        {
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Background };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var header = new Panel { Width = ClientSize.Width, Dock = DockStyle.Fill, BackColor = Theme.Navigation };
            var mark = Theme.Label("♫", 23, Theme.Accent); mark.BackColor = Theme.AccentSurface; mark.TextAlign = ContentAlignment.MiddleCenter; mark.SetBounds(24, 21, 44, 44); header.Controls.Add(mark);
            var title = Theme.Label("MusicBar", 20, Theme.Text); title.Font = Theme.Font(20, FontStyle.Bold); title.SetBounds(84, 16, 250, 34); header.Controls.Add(title);
            var subtitle = Theme.Label("让每一句，都留在任务栏", 9, Theme.Muted); subtitle.SetBounds(86, 50, 360, 23); header.Controls.Add(subtitle);
            var statusCard = Theme.Card(); statusCard.Padding = new Padding(14, 0, 14, 0); statusCard.SetBounds(715, 25, 290, 36); statusCard.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            headerStatus = Theme.Label("●  等待播放器", 9, Theme.Accent); headerStatus.AutoEllipsis = true; headerStatus.Dock = DockStyle.Fill; statusCard.Controls.Add(headerStatus); header.Controls.Add(statusCard);
            versionButton = (RoundedButton)Theme.Button(AppUpdateService.VersionLabel, false); versionButton.SetBounds(1020, 25, 110, 36); versionButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            versionButton.AccessibleName = "当前版本与软件更新"; versionButton.Click += delegate { SelectPage(4); }; header.Controls.Add(versionButton);
            shell.Controls.Add(header, 0, 0);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 0, 28, 0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var sidebar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Navigation, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 22, 16, 0), Margin = new Padding(0, 0, 24, 0) };
            var section = Theme.Label("工作台", 8, Theme.Muted); section.Size = new Size(152, 25); section.Margin = new Padding(12, 0, 0, 12); sidebar.Controls.Add(section);
            string[] names = { "音乐插件", "歌词外观", "歌词管理", "常规设置", "软件更新", "关于" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var button = Theme.Button(names[i], false); button.Width = 152; button.Height = 46; button.TextAlign = ContentAlignment.MiddleLeft; button.Padding = new Padding(18, 0, 0, 0); button.Margin = new Padding(0, 0, 0, 8); ((RoundedButton)button).NavigationButton = true;
                button.Click += delegate { SelectPage(index); }; sidebar.Controls.Add(button); navigation.Add(button);
            }
            var notice = Theme.Label("关闭窗口后在后台运行\n双击音乐托盘图标返回", 8, Theme.Muted); notice.Size = new Size(152, 58); notice.Margin = new Padding(4, 42, 0, 12); sidebar.Controls.Add(notice);
            var quit = Theme.Button("退出 MusicBar", false); quit.Width = 152; quit.Click += delegate { ExitApplication(); }; sidebar.Controls.Add(quit);
            content.Dock = DockStyle.Fill;
            body.Controls.Add(sidebar, 0, 0); body.Controls.Add(content, 1, 0); shell.Controls.Add(body, 0, 1);
            footerStatus = Theme.Label("设置自动保存 · 歌词浮层不抢占鼠标", 8, Theme.Muted); footerStatus.Dock = DockStyle.Fill; footerStatus.Padding = new Padding(28, 0, 20, 0); footerStatus.AutoEllipsis = true; shell.Controls.Add(footerStatus, 0, 2);
            Controls.Add(shell);
            BuildPlugins(); BuildSettings(); BuildLyrics(); BuildGeneral(); BuildUpdates(); BuildAbout(); SelectPage(0);
        }
        private FlowLayoutPanel Page(string title, string subtitle)
        {
            var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, BackColor = Theme.Background };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Background, Padding = new Padding(0, 14, 0, 16) };
            host.Controls.Add(flow);
            host.SizeChanged += delegate
            {
                int width = Math.Max(400, host.ClientSize.Width);
                flow.MinimumSize = new Size(width, 0); flow.MaximumSize = new Size(width, 0); flow.Width = width;
                foreach (Control child in flow.Controls)
                {
                    int childWidth = Math.Max(360, width - child.Margin.Horizontal);
                    if (child.AutoSize) child.MinimumSize = new Size(childWidth, 0);
                    child.Width = childWidth;
                }
            };
            var heading = new Panel { Height = 68, Width = 800, Margin = new Padding(0) };
            var name = Theme.Label(title, 19, Theme.Text); name.Font = Theme.Font(19, FontStyle.Bold); name.SetBounds(0, 0, 800, 36); name.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var description = Theme.Label(subtitle, 9, Theme.Muted); description.SetBounds(0, 39, 800, 25); description.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            heading.Controls.Add(name); heading.Controls.Add(description); flow.Controls.Add(heading);
            pages.Add(host); content.Controls.Add(host); return flow;
        }
        private void SelectPage(int index)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == index;
                navigation[i].BackColor = i == index ? Theme.Input : Theme.Background;
                navigation[i].ForeColor = i == index ? Theme.Accent : Theme.Muted;
                navigation[i].FlatAppearance.BorderSize = i == index ? 1 : 0;
                ((RoundedButton)navigation[i]).Selected = i == index; navigation[i].Invalidate();
            }
            pages[index].BringToFront();
            if (index == 1) UpdatePreview();
        }
        private void BuildPlugins()
        {
            var page = Page("音乐插件", "连接你的播放器，歌词随音乐自然流动。");
            var now = Theme.Card(); now.Height = 206; now.Margin = new Padding(0, 8, 0, 16);
            var label = Theme.Label("正在播放", 9, Theme.Muted); label.SetBounds(20, 14, 240, 24); now.Controls.Add(label);
            songTitle = Theme.Label("等待播放歌曲", 20, Theme.Text); songTitle.AutoEllipsis = true; songTitle.Font = Theme.Font(20, FontStyle.Bold); songTitle.SetBounds(20, 42, 710, 40); songTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; now.Controls.Add(songTitle);
            songArtist = Theme.Label("打开播放器并开始播放", 10, Theme.Muted); songArtist.AutoEllipsis = true; songArtist.SetBounds(20, 86, 710, 26); songArtist.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; now.Controls.Add(songArtist);
            liveLine = Theme.Label("歌词将在这里和任务栏同步显示", 12, Theme.Accent); liveLine.AutoEllipsis = true; liveLine.SetBounds(20, 121, 710, 30); liveLine.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; now.Controls.Add(liveLine);
            liveTranslation = Theme.Label("有译文时会在这里和任务栏第二行显示", 10, Theme.Muted); liveTranslation.AutoEllipsis = true; liveTranslation.SetBounds(20, 158, 710, 28); liveTranslation.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; now.Controls.Add(liveTranslation); page.Controls.Add(now);
            var pair = new TableLayoutPanel { Height = 156, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background, Margin = new Padding(0, 0, 0, 18) };
            pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var qqCard = PluginCard(MusicPlayer.QQMusic, out qqSwitch, out qqStatus); qqCard.Dock = DockStyle.Fill; qqCard.Margin = new Padding(0, 0, 8, 0);
            var cloudCard = PluginCard(MusicPlayer.NetEase, out neteaseSwitch, out neteaseStatus); cloudCard.Dock = DockStyle.Fill; cloudCard.Margin = new Padding(8, 0, 0, 0);
            pair.Controls.Add(qqCard, 0, 0); pair.Controls.Add(cloudCard, 1, 0); page.Controls.Add(pair);
            qqSwitch.CheckedChanged += delegate { if (!updating) { controller.Settings.QQEnabled = qqSwitch.Checked; Apply(false); } };
            neteaseSwitch.CheckedChanged += delegate { if (!updating) { controller.Settings.NetEaseEnabled = neteaseSwitch.Checked; Apply(false); } };
            var bar = new FlowLayoutPanel { Height = 45, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
            previewButton = Theme.Button("预览效果", false); previewButton.Width = 138; previewButton.Click += delegate { positionPreview = false; controller.Settings.PreviewEnabled = !controller.Settings.PreviewEnabled; Apply(false); };
            var settings = Theme.Button("歌词外观", true); settings.Width = 138; settings.Click += delegate { SelectPage(1); };
            var refresh = Theme.Button("重新获取歌词", false); refresh.Width = 138; refresh.Click += delegate { controller.Reload(); };
            dragButton = Theme.Button("解锁并拖动歌词", false); dragButton.Width = 148; dragButton.Click += delegate { TogglePositionLock(); };
            bar.Controls.Add(settings); bar.Controls.Add(previewButton); bar.Controls.Add(refresh); bar.Controls.Add(dragButton); page.Controls.Add(bar);
            var positionBar = new FlowLayoutPanel { Height = 42, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            var left = Theme.Button("← 左移", false); left.Width = 90; left.Height = 34; left.Click += delegate { MoveHorizontally(-20); };
            var right = Theme.Button("右移 →", false); right.Width = 90; right.Height = 34; right.Click += delegate { MoveHorizontally(20); };
            var moveHint = Theme.Label("解锁后按住歌词拖动；调好位置后重新锁定。", 9, Theme.Muted); moveHint.Width = 450; moveHint.Height = 34;
            positionBar.Controls.Add(left); positionBar.Controls.Add(right); positionBar.Controls.Add(moveHint); page.Controls.Add(positionBar);
            connectionHint = Theme.Label("等待连接", 9, Theme.Muted); connectionHint.Height = 68; connectionHint.TextAlign = ContentAlignment.TopLeft; connectionHint.Margin = new Padding(0); page.Controls.Add(connectionHint);
        }
        private Panel PluginCard(MusicPlayer player, out CheckBox toggle, out Label status)
        {
            var card = Theme.Card(); card.Width = 360;
            Color color = player == MusicPlayer.QQMusic ? Theme.Accent : Color.FromArgb(252, 102, 109);
            var logo = Theme.Label(player == MusicPlayer.QQMusic ? "Q" : "云", 18, color); logo.TextAlign = ContentAlignment.MiddleCenter; logo.BackColor = Theme.Input; logo.SetBounds(18, 18, 42, 42); logo.Font = Theme.Font(18, FontStyle.Bold); card.Controls.Add(logo);
            var name = Theme.Label(MusicSnapshot.PlayerName(player), 13, Theme.Text); name.SetBounds(72, 16, 240, 31); name.Font = Theme.Font(13, FontStyle.Bold); name.AutoEllipsis = true; name.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; card.Controls.Add(name);
            status = Theme.Label("●  检测中", 9, Theme.Muted); status.SetBounds(72, 49, 240, 25); status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; card.Controls.Add(status);
            toggle = Theme.Check("启用插件", player == MusicPlayer.QQMusic ? controller.Settings.QQEnabled : controller.Settings.NetEaseEnabled); toggle.Location = new Point(20, 101); card.Controls.Add(toggle);
            var open = Theme.Button(player == MusicPlayer.NetEase ? "启动网易云接入" : "打开播放器", false); open.SetBounds(180, 95, 125, 34); open.Anchor = AnchorStyles.Top | AnchorStyles.Right; open.Click += delegate { LaunchPlayer(player); }; card.Controls.Add(open);
            if (player == MusicPlayer.NetEase) neteaseLaunchButton = open;
            return card;
        }
        private void BuildSettings()
        {
            var s = controller.Settings;
            var page = Page("歌词外观", "颜色、亮度与位置，调整后立即生效。");
            var switches = new FlowLayoutPanel { Height = 48, WrapContents = false, Margin = new Padding(0, 8, 0, 8) };
            overlaySwitch = Theme.Check("显示任务栏歌词", s.OverlayEnabled); overlaySwitch.Margin = new Padding(0, 0, 24, 0);
            overlaySwitch.CheckedChanged += delegate { if (!updating) { s.OverlayEnabled = overlaySwitch.Checked; Apply(false); } };
            onlineSwitch = Theme.Check("自动获取在线歌词", s.OnlineLyrics); onlineSwitch.CheckedChanged += delegate { if (!updating) { s.OnlineLyrics = onlineSwitch.Checked; if (!s.OnlineLyrics && searchRequest != null) searchRequest.Cancel(); Apply(true); } };
            switches.Controls.Add(overlaySwitch); switches.Controls.Add(onlineSwitch); page.Controls.Add(switches);
            var previewCard = Theme.Card(); previewCard.Height = 118; previewCard.Margin = new Padding(0, 0, 0, 14);
            var previewTitle = Theme.Label("外观预览 · 演唱进度示例", 9, Theme.Muted); previewTitle.SetBounds(18, 10, 360, 28); previewCard.Controls.Add(previewTitle);
            var palette = Theme.Button("应用推荐配色", false); palette.SetBounds(570, 8, 154, 32); palette.Anchor = AnchorStyles.Top | AnchorStyles.Right; previewCard.Controls.Add(palette);
            previewImage = new PictureBox { BackColor = Theme.Surface, SizeMode = PictureBoxSizeMode.CenterImage }; previewImage.SetBounds(16, 43, 716, 66); previewImage.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; previewCard.Controls.Add(previewImage); page.Controls.Add(previewCard);
            var display = SettingsGrid("显示样式");
            var style = Theme.Combo(); style.AccessibleName = "字体风格"; style.Items.AddRange(new object[] { "清晰雅黑 · 加粗", "轻盈等线 · 常规", "自定义字体" });
            SyncFontStyle(style); AddSetting(display, "字体风格", style, "两款推荐风格可切换，保留你选择的颜色。");
            var family = Theme.Combo();
            using (var fonts = new System.Drawing.Text.InstalledFontCollection())
            {
                foreach (var font in fonts.Families) family.Items.Add(font.Name);
            }
            if (!family.Items.Contains(s.FontFamily)) family.Items.Add(s.FontFamily);
            family.SelectedItem = s.FontFamily; family.SelectedIndexChanged += delegate { if (!updating && family.SelectedItem != null) { s.FontFamily = family.SelectedItem.ToString(); SyncFontStyle(style); Apply(false); } };
            AddSetting(display, "原文字体", family, "建议使用微软雅黑，兼容中文歌词。");
            var translatedFamily = Theme.Combo(); translatedFamily.Items.Add("跟随原文字体");
            foreach (var fontName in family.Items) translatedFamily.Items.Add(fontName);
            if (s.TranslationFontFamily.Length > 0 && !translatedFamily.Items.Contains(s.TranslationFontFamily)) translatedFamily.Items.Add(s.TranslationFontFamily);
            if (s.TranslationFontFamily.Length == 0) translatedFamily.SelectedIndex = 0; else translatedFamily.SelectedItem = s.TranslationFontFamily;
            translatedFamily.SelectedIndexChanged += delegate
            {
                if (!updating && translatedFamily.SelectedIndex >= 0) { s.TranslationFontFamily = translatedFamily.SelectedIndex == 0 ? "" : translatedFamily.SelectedItem.ToString(); SyncFontStyle(style); Apply(false); }
            };
            AddSetting(display, "译文字体", translatedFamily, "可跟随原文或单独选择；缺少中文字形时使用清晰雅黑。");
            var size = Theme.Number(10, 30, (decimal)s.FontSize, 0); size.ValueChanged += delegate { if (!updating) { s.FontSize = (float)size.Value; SyncFontStyle(style); Apply(false); } }; AddSetting(display, "字号", size, "任务栏内会按可用高度适配。");
            var brightness = Theme.Number(30, 160, s.LyricBrightness, 0); brightness.Increment = 5;
            brightness.ValueChanged += delegate { if (!updating) { s.LyricBrightness = (int)brightness.Value; Apply(false); } };
            AddSetting(display, "歌词亮度（%）", brightness, "100 为所选颜色；提亮保留色相，推荐 100。");
            var colors = new FlowLayoutPanel { Height = 40, Width = 270, WrapContents = false, Margin = new Padding(0) };
            var originalBase = ColorButton("未唱颜色", delegate { return s.Foreground; }, delegate(string value) { s.TextColor = value; }); originalBase.AccessibleName = "原文未唱颜色";
            var originalSung = ColorButton("已唱颜色", delegate { return s.Highlight; }, delegate(string value) { s.ActiveColor = value; }); originalSung.AccessibleName = "原文已唱颜色";
            colors.Controls.Add(originalBase); colors.Controls.Add(originalSung);
            AddSetting(display, "原文颜色", colors, "未唱文字和已唱文字可分别设置。");
            var translatedColors = new FlowLayoutPanel { Height = 40, Width = 270, WrapContents = false, Margin = new Padding(0) };
            var translatedBase = ColorButton("未唱颜色", delegate { return s.TranslationForeground; }, delegate(string value) { s.TranslationColor = value; }); translatedBase.AccessibleName = "译文未唱颜色";
            var translatedSung = ColorButton("已唱颜色", delegate { return s.TranslationHighlight; }, delegate(string value) { s.TranslationActiveColor = value; }); translatedSung.AccessibleName = "译文已唱颜色";
            translatedColors.Controls.Add(translatedBase); translatedColors.Controls.Add(translatedSung);
            AddSetting(display, "译文颜色", translatedColors, "译文配色独立于原文。");
            palette.Click += delegate
            {
                s.TextColor = "#D5DEE9"; s.ActiveColor = "#7CCEFF"; s.TranslationColor = "#AAB8C8"; s.TranslationActiveColor = "#EAF2FA"; s.LyricBrightness = 100;
                bool previous = updating; updating = true; brightness.Value = 100; updating = previous;
                SetColorSample(originalBase, s.Foreground); SetColorSample(originalSung, s.Highlight);
                SetColorSample(translatedBase, s.TranslationForeground); SetColorSample(translatedSung, s.TranslationHighlight); Apply(false);
            };
            var karaoke = Theme.Check("随演唱进度逐字变色", s.KaraokeEnabled); karaoke.CheckedChanged += delegate { if (!updating) { s.KaraokeEnabled = karaoke.Checked; Apply(false); } }; AddSetting(display, "演唱进度", karaoke, "无逐字时间戳时按句内进度估算；暂停、拖动跟随播放器。");
            var bold = Theme.Check("加粗文字", s.BoldLyrics); bold.CheckedChanged += delegate { if (!updating) { s.BoldLyrics = bold.Checked; SyncFontStyle(style); Apply(false); } }; AddSetting(display, "字体清晰度", bold, "减轻描边，改善任务栏小字的可读性。");
            style.SelectedIndexChanged += delegate
            {
                if (updating || style.SelectedIndex < 0 || style.SelectedIndex > 1) return;
                s.FontFamily = style.SelectedIndex == 0 ? "Microsoft YaHei UI" : "等线"; s.TranslationFontFamily = ""; s.FontSize = 16; s.BoldLyrics = style.SelectedIndex == 0;
                if (!family.Items.Contains(s.FontFamily)) family.Items.Add(s.FontFamily);
                bool previous = updating; updating = true; family.SelectedItem = s.FontFamily; translatedFamily.SelectedIndex = 0; size.Value = 16; bold.Checked = s.BoldLyrics; updating = previous; Apply(false);
            };
            var lines = Theme.Check("显示下一行（双行）", s.TwoLines); lines.CheckedChanged += delegate { if (!updating) { s.TwoLines = lines.Checked; Apply(false); } }; AddSetting(display, "行数", lines, "双行会在任务栏高度内缩小字号。");
            var translation = Theme.Check("显示当前句译文", s.ShowTranslation); translation.CheckedChanged += delegate { if (!updating) { s.ShowTranslation = translation.Checked; Apply(false); } }; AddSetting(display, "歌词翻译", translation, "有译文时第二行显示译文；没有译文时按行数设置显示。");
            var scroll = Theme.Check("长歌词自动滚动", s.LongLineScroll); scroll.CheckedChanged += delegate { if (!updating) { s.LongLineScroll = scroll.Checked; Apply(false); } }; AddSetting(display, "长句显示", scroll, "自动滚动查看整句；关闭后缩小字号完整显示。");
            page.Controls.Add(display);
            var placement = SettingsGrid("任务栏位置");
            var screen = Theme.Combo();
            for (int i = 0; i < Screen.AllScreens.Length; i++) screen.Items.Add("显示器 " + (i + 1) + (Screen.AllScreens[i].Primary ? " · 主屏幕" : "") + " · " + Screen.AllScreens[i].Bounds.Width + " × " + Screen.AllScreens[i].Bounds.Height);
            screen.SelectedIndex = Math.Min(s.MonitorIndex, screen.Items.Count - 1); screen.SelectedIndexChanged += delegate { if (!updating && screen.SelectedIndex >= 0) { s.MonitorIndex = screen.SelectedIndex; Apply(false); } }; AddSetting(placement, "显示器", screen, "副屏需要开启「在所有任务栏上显示」。");
            var alignment = Theme.Combo(); alignment.Items.AddRange(new object[] { "左侧空白", "居中", "右侧空白" }); alignment.SelectedIndex = s.Alignment == "center" ? 1 : s.Alignment == "right" ? 2 : 0;
            positionAlignment = alignment;
            alignment.SelectedIndexChanged += delegate { if (!updating) { s.Alignment = alignment.SelectedIndex == 1 ? "center" : alignment.SelectedIndex == 2 ? "right" : "left"; Apply(false); } }; AddSetting(placement, "对齐位置", alignment, "工具会避让检测到的任务栏按钮。");
            var width = Theme.Number(180, 1200, s.Width, 0); width.Increment = 10; width.ValueChanged += delegate { if (!updating) { s.Width = (int)width.Value; Apply(false); } }; AddSetting(placement, "歌词宽度", width, "单位：逻辑像素。可用空间不足时自动缩小。");
            var x = Theme.Number(-20000, 20000, s.HorizontalOffset, 0); horizontalPosition = x; x.Increment = 5; x.ValueChanged += delegate { if (!updating) { s.HorizontalOffset = (int)x.Value; Apply(false); } }; AddSetting(placement, "左右偏移", x, "正值向右，负值向左；可跨任务栏空白区域移动。");
            var y = Theme.Number(-2000, 2000, s.VerticalOffset, 0); verticalPosition = y; y.Increment = 2; y.ValueChanged += delegate { if (!updating) { s.VerticalOffset = (int)y.Value; Apply(false); } }; AddSetting(placement, "上下偏移", y, "正值向下，负值向上。");
            var follow = Theme.Check("在任务栏内显示", s.FollowTaskbar); follow.CheckedChanged += delegate { if (!updating) { s.FollowTaskbar = follow.Checked; Apply(false); } }; AddSetting(placement, "显示区域", follow, "关闭后显示在任务栏上方，适合空间较少的屏幕。");
            page.Controls.Add(placement);
            var behavior = SettingsGrid("同步与行为");
            var timing = new FlowLayoutPanel { Width = 270, Height = 36, WrapContents = false, Margin = new Padding(0) };
            var offset = Theme.Number(-120, 120, (decimal)s.OffsetSeconds, 1); offset.Width = 100; offset.Increment = 0.1M;
            offset.ValueChanged += delegate { if (!updating) { s.OffsetSeconds = (double)offset.Value; Apply(false); } };
            var earlier = Theme.Button("提前", false); earlier.Width = 68; earlier.Height = 30; earlier.Click += delegate { offset.Value = Math.Min(offset.Maximum, offset.Value + 0.2M); };
            var later = Theme.Button("延后", false); later.Width = 68; later.Height = 30; later.Click += delegate { offset.Value = Math.Max(offset.Minimum, offset.Value - 0.2M); };
            timing.Controls.Add(offset); timing.Controls.Add(earlier); timing.Controls.Add(later); AddSetting(behavior, "歌词时间偏移", timing, "落后歌声时点提前，每次 0.2 秒；不改变音乐播放。");
            preferred = Theme.Combo(); preferred.Items.AddRange(new object[] { "自动选择正在播放的播放器", "优先 QQ 音乐", "优先网易云音乐" }); preferred.SelectedIndex = (int)s.PreferredPlayer;
            preferred.SelectedIndexChanged += delegate { if (!updating) { s.PreferredPlayer = (MusicPlayer)preferred.SelectedIndex; Apply(false); } }; AddSetting(behavior, "播放器选择", preferred, "多个播放器同时播放时按优先级选择。");
            var click = Theme.Check("锁定位置（鼠标穿透）", s.ClickThrough); positionLock = click; click.CheckedChanged += delegate { if (!updating && s.ClickThrough != click.Checked) TogglePositionLock(); }; AddSetting(behavior, "歌词拖动", click, "取消锁定后，可按住歌词左右拖动。");
            var pause = Theme.Check("暂停时隐藏歌词", s.HideWhenPaused); pause.CheckedChanged += delegate { if (!updating) { s.HideWhenPaused = pause.Checked; Apply(false); } }; AddSetting(behavior, "暂停播放", pause, "默认暂停时保留当前行。");
            var instrumental = Theme.Check("前奏、间奏和尾奏隐藏歌词", s.HideInstrumental); instrumental.CheckedChanged += delegate { if (!updating) { s.HideInstrumental = instrumental.Checked; Apply(false); } }; AddSetting(behavior, "伴奏片段", instrumental, "空行、间奏标记和制作信息自动隐藏；下一句开始时恢复。");
            var instrumentalHold = Theme.Number(3, 20, (decimal)s.InstrumentalHoldSeconds, 1); instrumentalHold.Increment = 0.5M;
            instrumentalHold.ValueChanged += delegate { if (!updating) { s.InstrumentalHoldSeconds = (double)instrumentalHold.Value; Apply(false); } }; AddSetting(behavior, "长间隔停留（秒）", instrumentalHold, "歌词未标注唱完时间时，长间隔中的旧句按此时间收起；拖长音可调大。");
            var missing = Theme.Check("没有歌词时显示歌名", s.ShowSongWhenMissing); missing.CheckedChanged += delegate { if (!updating) { s.ShowSongWhenMissing = missing.Checked; Apply(false); } }; AddSetting(behavior, "无歌词", missing, "未连接播放器时不会显示虚构歌词。");
            page.Controls.Add(behavior);
            var preview = Theme.Button("开启 / 关闭任务栏预览", false); preview.Height = 40; preview.Click += delegate { positionPreview = false; s.PreviewEnabled = !s.PreviewEnabled; Apply(false); }; page.Controls.Add(preview);
        }
        private void SyncFontStyle(ComboBox style)
        {
            var s = controller.Settings; int selected = 2;
            if (Math.Abs(s.FontSize - 16) < .01f && s.TranslationFontFamily.Length == 0)
            {
                if (s.FontFamily == "Microsoft YaHei UI" && s.BoldLyrics) selected = 0;
                else if (s.FontFamily == "等线" && !s.BoldLyrics) selected = 1;
            }
            bool previous = updating; updating = true; style.SelectedIndex = selected; updating = previous;
        }
        private TableLayoutPanel SettingsGrid(string title)
        {
            var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, Width = 750, ColumnCount = 3, RowCount = 1, BackColor = Theme.Surface, Padding = new Padding(18, 10, 18, 12), Margin = new Padding(0, 0, 0, 14) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 282)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var heading = Theme.Label(title, 12, Theme.Text); heading.Font = Theme.Font(12, FontStyle.Bold); heading.Height = 38; heading.Dock = DockStyle.Fill; grid.Controls.Add(heading, 0, 0); grid.SetColumnSpan(heading, 3);
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); return grid;
        }
        private void AddSetting(TableLayoutPanel grid, string name, Control input, string hint)
        {
            int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            var label = Theme.Label(name, 9, Theme.Text); label.Dock = DockStyle.Fill;
            input.Anchor = AnchorStyles.Left; input.Margin = new Padding(0, 4, 4, 4);
            var explanation = Theme.Label(hint, 8, Theme.Muted); explanation.Dock = DockStyle.Fill;
            grid.Controls.Add(label, 0, row); grid.Controls.Add(input, 1, row); grid.Controls.Add(explanation, 2, row);
        }
        private static string ColorCode(Color color) { return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2"); }
        private Button ColorButton(string label, Func<Color> read, Action<string> write)
        {
            var button = Theme.Button(label, false); button.Width = 124; SetColorSample(button, read());
            button.Click += delegate
            {
                using (var dialog = new ColorDialog { Color = read(), FullOpen = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { write(ColorCode(dialog.Color)); SetColorSample(button, dialog.Color); Apply(false); }
            };
            return button;
        }
        private static void SetColorSample(Button button, Color color)
        {
            var rounded = (RoundedButton)button; rounded.ShowColorSample = true; rounded.ColorSample = color;
            rounded.AccessibleDescription = "所选颜色 " + ColorCode(color); rounded.Invalidate();
        }

        private void BuildLyrics()
        {
            var page = Page("歌词管理", "歌词不匹配时可手动选择或导入本地 LRC。选择结果会绑定当前歌曲。");
            var search = new TableLayoutPanel { Height = 46, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 10, 0, 8) };
            search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128)); search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            lyricSource = Theme.Combo(); lyricSource.Items.AddRange(new object[] { "QQ 音乐", "网易云音乐" }); lyricSource.SelectedIndex = 0; lyricSource.Dock = DockStyle.Fill;
            query = new TextBox { Font = Theme.Font(11, FontStyle.Regular), BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Margin = new Padding(10, 0, 10, 0) };
            query.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SearchLyrics(); } };
            searchButton = Theme.Button("搜索歌词", true); searchButton.Dock = DockStyle.Top; searchButton.Click += async delegate { await SearchLyrics(); };
            search.Controls.Add(lyricSource, 0, 0); search.Controls.Add(query, 1, 0); search.Controls.Add(searchButton, 2, 0); page.Controls.Add(search);
            results = new ListBox { Height = 144, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Font(10, FontStyle.Regular), ItemHeight = 30, IntegralHeight = false, HorizontalScrollbar = true, DrawMode = DrawMode.OwnerDrawFixed, Margin = new Padding(0, 0, 0, 12) };
            results.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                using (var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Theme.Input : Theme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, results.Items[e.Index].ToString(), results.Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            results.SelectedIndexChanged += delegate { fetchButton.Enabled = results.SelectedItem is LyricSearchResult; };
            results.DoubleClick += async delegate { await UseSelectedLyric(); }; page.Controls.Add(results);
            var actions = new FlowLayoutPanel { Height = 44, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            fetchButton = Theme.Button("使用所选歌词", true); fetchButton.Width = 150; fetchButton.Enabled = false; fetchButton.Click += async delegate { await UseSelectedLyric(); };
            var import = Theme.Button("导入 .lrc 文件", false); import.Width = 148; import.Click += delegate
            {
                using (var dialog = new OpenFileDialog { Title = "导入 LRC 歌词", Filter = "LRC 歌词 (*.lrc)|*.lrc|文本歌词 (*.txt)|*.txt|所有文件 (*.*)|*.*", CheckFileExists = true })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        try { controller.Import(dialog.FileName); }
                        catch (Exception ex) { ShowError("导入歌词失败", ex.Message); }
                    }
                }
            };
            var reset = Theme.Button("恢复自动匹配", false); reset.Width = 146; reset.Click += delegate { controller.RestoreAutomatic(); };
            actions.Controls.Add(fetchButton); actions.Controls.Add(import); actions.Controls.Add(reset); page.Controls.Add(actions);
            lyricStatus = Theme.Label("搜索「歌名 歌手」，或导入带时间标记的 LRC 文件。", 9, Theme.Muted); lyricStatus.Height = 60; lyricStatus.TextAlign = ContentAlignment.TopLeft; page.Controls.Add(lyricStatus);
            var manual = Theme.Card(); manual.Height = 152; manual.Margin = new Padding(0, 0, 0, 12);
            var heading = Theme.Label("手动同步", 12, Theme.Text); heading.Font = Theme.Font(12, FontStyle.Bold); heading.SetBounds(18, 12, 200, 27); manual.Controls.Add(heading);
            manualStatus = Theme.Label("播放器未提供时间轴时，按当前歌曲位置开始计时。", 8, Theme.Muted); manualStatus.SetBounds(18, 42, 710, 34); manualStatus.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right; manual.Controls.Add(manualStatus);
            manualSeconds = Theme.Number(0, 7200, 0, 1); manualSeconds.SetBounds(18, 91, 105, 32); manual.Controls.Add(manualSeconds);
            var seek = Theme.Button("设为当前秒数", false); seek.SetBounds(134, 86, 130, 38); seek.Click += delegate { controller.SeekManual((double)manualSeconds.Value); }; manual.Controls.Add(seek);
            manualButton = Theme.Button("开始手动同步", false); manualButton.SetBounds(276, 86, 130, 38); manualButton.Click += delegate { controller.ToggleManual(); }; manual.Controls.Add(manualButton);
            usePlayerButton = Theme.Button("使用播放器时间", false); usePlayerButton.SetBounds(418, 86, 140, 38); usePlayerButton.Click += delegate { controller.UsePlayerClock(); }; manual.Controls.Add(usePlayerButton);
            positionLabel = Theme.Label("00:00", 10, Theme.Accent); positionLabel.SetBounds(570, 90, 120, 32); manual.Controls.Add(positionLabel); page.Controls.Add(manual);
            lyricLines = new ListBox { Height = 145, BackColor = Theme.Surface, ForeColor = Theme.Muted, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Font(9, FontStyle.Regular), IntegralHeight = false, HorizontalScrollbar = true, ItemHeight = 26, Margin = new Padding(0) }; page.Controls.Add(lyricLines);
        }
        private void BuildAbout()
        {
            var page = Page("关于 MusicBar", "Windows 任务栏歌词工具 · " + AppUpdateService.VersionLabel + " · MIT 开源");
            var card = Theme.Card(); card.Height = 368; card.Margin = new Padding(0, 12, 0, 16);
            var text = Theme.Label("让歌词陪伴每一次播放\n\nQQ 音乐通过 Windows 媒体会话连接。\n网易云请通过「启动网易云接入」打开，支持当前版本的真实进度。\n\n在「歌词外观」调整字体、颜色、亮度和任务栏位置。\n歌词匹配失败时，可在「歌词管理」重新选择或导入本地 LRC。\n「常规设置」可以开启随 Windows 登录启动。\n\n设置和缓存保存在工具旁边的 data 文件夹。在线歌词查询会将歌名和歌手\n发送给对应平台；关闭在线获取后可以使用本地歌词。\n\n关闭窗口后继续在托盘运行；双击音乐图标可以返回设置。\n这是独立的第三方工具，QQ 音乐、网易云音乐名称仅用于说明适配来源。", 10, Theme.Text);
            text.Dock = DockStyle.Fill; text.TextAlign = ContentAlignment.TopLeft; card.Controls.Add(text); page.Controls.Add(card);
            var source = Theme.Button("查看本工具源码", false); source.Height = 40; source.Click += delegate
            {
                if (updateService.Configured) { OpenUpdateUrl("https://github.com/" + updateService.Repository); return; }
                string sourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "source");
                if (!Directory.Exists(sourcePath)) sourcePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                try { Process.Start(new ProcessStartInfo(sourcePath) { UseShellExecute = true }); } catch (Exception ex) { ShowError("无法打开源码目录", ex.Message); }
            }; page.Controls.Add(source);
        }
        private void BuildGeneral()
        {
            var page = Page("常规设置", "启动方式与后台运行，按你的习惯设置。");
            var card = Theme.Card(); card.Height = 180; card.Margin = new Padding(0, 12, 0, 18);
            var title = Theme.Label("启动与后台运行", 13, Theme.Text); title.Font = Theme.Font(13, FontStyle.Bold); title.SetBounds(24, 18, 520, 30); card.Controls.Add(title);
            startupSwitch = Theme.Check("登录 Windows 时自动启动 MusicBar", StartupRegistration.IsEnabled(Application.ExecutablePath)); startupSwitch.Location = new Point(24, 62); card.Controls.Add(startupSwitch);
            startupHint = Theme.Label("登录后静默运行在托盘，播放音乐时显示歌词。", 9, Theme.Muted); startupHint.SetBounds(24, 108, 730, 46); startupHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; card.Controls.Add(startupHint);
            startupSwitch.CheckedChanged += delegate
            {
                if (updating) return;
                try
                {
                    StartupRegistration.SetEnabled(startupSwitch.Checked, Application.ExecutablePath);
                    startupHint.Text = startupSwitch.Checked ? "已开启：下次登录 Windows 后，MusicBar 会在托盘运行。" : "已关闭开机自动启动。你可以随时在这里重新开启。";
                }
                catch (Exception ex)
                {
                    updating = true; startupSwitch.Checked = StartupRegistration.IsEnabled(Application.ExecutablePath); updating = false;
                    startupHint.Text = "设置失败：" + ex.Message;
                }
            };
            page.Controls.Add(card);
            var access = Theme.Card(); access.Height = 228; access.Margin = new Padding(0, 0, 0, 18);
            var hiddenIcon = Theme.Check("隐藏任务栏应用图标", controller.Settings.HideTaskbarIcon); hiddenIcon.Location = new Point(24, 18); access.Controls.Add(hiddenIcon);
            hiddenIcon.CheckedChanged += delegate { if (!updating) { controller.Settings.HideTaskbarIcon = hiddenIcon.Checked; ShowInTaskbar = !hiddenIcon.Checked; controller.Store.Save(controller.Settings); } };
            var hotkeyEnabled = Theme.Check("使用快捷键打开 MusicBar", controller.Settings.EnableHotkey); hotkeyEnabled.Location = new Point(24, 66); access.Controls.Add(hotkeyEnabled);
            var hotkeyInput = new TextBox { ReadOnly = true, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Font(10, FontStyle.Regular), BorderStyle = BorderStyle.FixedSingle, AccessibleName = "打开 MusicBar 的快捷键" };
            hotkeyInput.Text = AppHotkey.Describe(controller.Settings.EffectiveHotkeyKey, controller.Settings.EffectiveHotkeyModifiers);
            hotkeyInput.SetBounds(24, 112, 270, 32); access.Controls.Add(hotkeyInput);
            var saveHotkey = Theme.Button("保存快捷键", false); saveHotkey.SetBounds(306, 109, 125, 36); saveHotkey.Enabled = false; access.Controls.Add(saveHotkey);
            var resetHotkey = Theme.Button("恢复默认", false); resetHotkey.SetBounds(443, 109, 110, 36); access.Controls.Add(resetHotkey);
            hotkeyHint = Theme.Label("", 9, Theme.Muted); hotkeyHint.SetBounds(24, 160, 700, 48); access.Controls.Add(hotkeyHint);
            int pendingKey = controller.Settings.EffectiveHotkeyKey, pendingModifiers = controller.Settings.EffectiveHotkeyModifiers;
            bool recordingHotkey = false;
            Action startRecording = delegate
            {
                recordingHotkey = true; saveHotkey.Enabled = false; appHotkey.Dispose();
                hotkeyInput.Text = "请按下快捷键…";
                hotkeyHint.Text = "可直接按 F1、F2 等功能键，或按 Ctrl / Alt 加其他键。\n录入后点击「保存快捷键」，Esc 取消。";
            };
            hotkeyInput.Enter += delegate { if (!updating) startRecording(); };
            hotkeyInput.Click += delegate { if (!recordingHotkey) startRecording(); };
            hotkeyInput.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (!recordingHotkey) return;
                e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;
                if (e.KeyCode == Keys.Escape && e.Modifiers == Keys.None)
                {
                    recordingHotkey = false; saveHotkey.Enabled = false;
                    hotkeyInput.Text = AppHotkey.Describe(controller.Settings.EffectiveHotkeyKey, controller.Settings.EffectiveHotkeyModifiers); UpdateHotkey(); return;
                }
                int modifiers = (e.Control ? 2 : 0) | (e.Alt ? 1 : 0) | (e.Shift ? 4 : 0);
                if (!AppSettings.ValidHotkey((int)e.KeyCode, modifiers))
                {
                    saveHotkey.Enabled = false; hotkeyHint.Text = "请直接按功能键，或用 Ctrl / Alt 搭配其他键。Esc 可取消。"; return;
                }
                pendingKey = (int)e.KeyCode; pendingModifiers = modifiers;
                hotkeyInput.Text = AppHotkey.Describe(pendingKey, pendingModifiers); saveHotkey.Enabled = true;
                hotkeyHint.Text = "已录入 " + hotkeyInput.Text + "，点击「保存快捷键」应用。";
            };
            hotkeyInput.Leave += delegate
            {
                recordingHotkey = false; UpdateHotkey();
                if (saveHotkey.Enabled) hotkeyHint.Text = "已录入 " + hotkeyInput.Text + "，点击「保存快捷键」应用。";
                else hotkeyInput.Text = AppHotkey.Describe(controller.Settings.EffectiveHotkeyKey, controller.Settings.EffectiveHotkeyModifiers);
            };
            Deactivate += delegate
            {
                if (!recordingHotkey) return;
                recordingHotkey = false; UpdateHotkey();
                if (!saveHotkey.Enabled) hotkeyInput.Text = AppHotkey.Describe(controller.Settings.EffectiveHotkeyKey, controller.Settings.EffectiveHotkeyModifiers);
            };
            saveHotkey.Click += delegate
            {
                if (!SaveHotkey(true, pendingKey, pendingModifiers)) return;
                recordingHotkey = false; saveHotkey.Enabled = false;
                hotkeyInput.Text = AppHotkey.Describe(controller.Settings.EffectiveHotkeyKey, controller.Settings.EffectiveHotkeyModifiers);
            };
            resetHotkey.Click += delegate
            {
                if (!SaveHotkey(false, 0x4D, 3)) return;
                recordingHotkey = false; saveHotkey.Enabled = false;
                hotkeyInput.Text = AppHotkey.Describe(controller.Settings.EffectiveHotkeyKey, controller.Settings.EffectiveHotkeyModifiers);
            };
            hotkeyEnabled.CheckedChanged += delegate { if (!updating) { controller.Settings.EnableHotkey = hotkeyEnabled.Checked; UpdateHotkey(); controller.Store.Save(controller.Settings); } };
            page.Controls.Add(access);
            var netease = Theme.Card(); netease.Height = 204; netease.Margin = new Padding(0, 0, 0, 18);
            var neteaseTitle = Theme.Label("网易云稳定接入", 13, Theme.Text); neteaseTitle.Font = Theme.Font(13, FontStyle.Bold); neteaseTitle.SetBounds(24, 18, 520, 30); netease.Controls.Add(neteaseTitle);
            var neteaseHint = Theme.Label("修复现有快捷方式，普通打开也能同步歌词。原启动方式会先备份，可随时还原。\n配置后，已运行的网易云需退出并重新打开一次。", 9, Theme.Muted); neteaseHint.SetBounds(24, 58, 730, 66); neteaseHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; netease.Controls.Add(neteaseHint);
            var configureNetEase = Theme.Button("修复网易云启动方式", false); configureNetEase.SetBounds(24, 140, 190, 36); netease.Controls.Add(configureNetEase);
            configureNetEase.Click += delegate
            {
                if (MessageBox.Show(this, "为网易云的桌面、开始菜单和已固定快捷方式加入本机进度接入参数，并新增一个桌面入口。原快捷方式会备份。本操作不重开播放器。是否修复？", "修复网易云启动方式", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                try
                {
                    RememberNetEasePath();
                    string path = ""; try { path = File.ReadAllText(Path.Combine(controller.Store.DataDirectory, "netease-player-path.txt")).Trim(); } catch { }
                    if (!NetEaseLaunchIntegration.ValidPlayer(path))
                        using (var dialog = new OpenFileDialog { Title = "选择网易云安装目录的 cloudmusic.exe", Filter = "cloudmusic.exe|cloudmusic.exe", CheckFileExists = true })
                        { if (dialog.ShowDialog(this) != DialogResult.OK) return; path = dialog.FileName; }
                    neteaseHint.Text = NetEaseLaunchIntegration.Configure(path, controller.Store.DataDirectory); SaveNetEasePath(path);
                }
                catch (Exception ex) { neteaseHint.Text = "修复失败：" + ex.Message; }
            };
            var restoreNetEase = Theme.Button("还原启动方式", false); restoreNetEase.SetBounds(232, 140, 148, 36); netease.Controls.Add(restoreNetEase);
            restoreNetEase.Click += delegate
            {
                pendingNetEasePath = ""; neteaseRecoveryStatus = ""; verifyingNetEaseStart = false; neteaseStartTimer.Stop();
                try { neteaseHint.Text = NetEaseLaunchIntegration.Restore(controller.Store.DataDirectory); }
                catch (Exception ex) { neteaseHint.Text = "还原失败：" + ex.Message; }
            };
            page.Controls.Add(netease);
            var updates = Theme.Card(); updates.Height = 115; updates.Margin = new Padding(0, 0, 0, 18);
            var automatic = Theme.Check("自动检查 MusicBar 更新", controller.Settings.AutoCheckUpdates); automatic.Location = new Point(24, 18); updates.Controls.Add(automatic);
            var updateHint = Theme.Label("启动后定期检查；网络恢复后重试。有新版本时右上角显示提示点。", 9, Theme.Muted); updateHint.SetBounds(24, 62, 730, 35); updateHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; updates.Controls.Add(updateHint);
            automatic.CheckedChanged += delegate { if (!updating) { controller.Settings.AutoCheckUpdates = automatic.Checked; controller.Store.Save(controller.Settings); if (automatic.Checked) nextUpdateCheck = DateTime.UtcNow; } }; page.Controls.Add(updates);
            var menus = Theme.Card(); menus.Height = 190; menus.Margin = new Padding(0, 0, 0, 18);
            var menuTitle = Theme.Label("深色菜单", 13, Theme.Text); menuTitle.Font = Theme.Font(13, FontStyle.Bold); menuTitle.SetBounds(24, 18, 650, 30); menus.Controls.Add(menuTitle);
            var description = Theme.Label("MusicBar 的窗口、托盘和歌词菜单使用统一深色主题。\n任务栏应用图标的系统菜单还会受到 Windows 配色影响。", 9, Theme.Muted); description.SetBounds(24, 57, 730, 54); description.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; menus.Controls.Add(description);
            var colors = Theme.Button("Windows 颜色设置", false); colors.SetBounds(24, 127, 185, 38); colors.Click += delegate { try { Process.Start(new ProcessStartInfo("ms-settings:colors") { UseShellExecute = true }); } catch (Exception ex) { ShowError("无法打开颜色设置", ex.Message); } }; menus.Controls.Add(colors); page.Controls.Add(menus);
            var note = Theme.Label("关闭主窗口后歌词继续运行。需要完全结束时，使用「退出 MusicBar」。\n移动便携文件夹后，请重新关闭并开启开机启动，以更新启动位置。", 9, Theme.Muted); note.Height = 60; note.TextAlign = ContentAlignment.TopLeft; page.Controls.Add(note);
        }
        private void BuildUpdates()
        {
            var page = Page("软件更新", "发现新版后，点击一次即可下载并打开安装向导。");
            var card = Theme.Card(); card.Height = 398; card.Margin = new Padding(0, 12, 0, 18);
            updateVersion = Theme.Label("当前版本 " + AppUpdateService.VersionLabel, 16, Theme.Text); updateVersion.Font = Theme.Font(16, FontStyle.Bold); updateVersion.SetBounds(24, 20, 730, 38); updateVersion.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; card.Controls.Add(updateVersion);
            updateStatus = Theme.Label("等待检查更新", 10, Theme.Accent); updateStatus.SetBounds(24, 67, 730, 44); updateStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; card.Controls.Add(updateStatus);
            updateNotes = Theme.Label("", 10, Theme.Muted); updateNotes.AutoEllipsis = true; updateNotes.TextAlign = ContentAlignment.TopLeft; updateNotes.SetBounds(24, 123, 730, 140); updateNotes.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; card.Controls.Add(updateNotes);
            updateProgress = new DownloadProgress { Visible = false }; updateProgress.SetBounds(24, 285, 658, 20); updateProgress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; card.Controls.Add(updateProgress);
            updateProgressText = Theme.Label("", 9, Theme.Accent); updateProgressText.Visible = false; updateProgressText.TextAlign = ContentAlignment.MiddleRight; updateProgressText.SetBounds(688, 281, 66, 28); updateProgressText.Anchor = AnchorStyles.Top | AnchorStyles.Right; card.Controls.Add(updateProgressText);
            card.SizeChanged += delegate { int width = Math.Max(1, card.ClientSize.Width - 48); updateVersion.Width = width; updateStatus.Width = width; updateNotes.Width = width; updateProgress.Width = Math.Max(1, width - 76); updateProgressText.Left = card.ClientSize.Width - 90; };
            checkUpdateButton = Theme.Button("检查更新", false); checkUpdateButton.SetBounds(24, 330, 125, 40); checkUpdateButton.Click += async delegate { await CheckForUpdates(); }; card.Controls.Add(checkUpdateButton);
            downloadUpdateButton = Theme.Button("下载并安装", true); downloadUpdateButton.SetBounds(163, 330, 164, 40); downloadUpdateButton.Click += async delegate { await DownloadUpdate(); }; card.Controls.Add(downloadUpdateButton); page.Controls.Add(card);
            var hint = Theme.Label("更新检查使用 GitHub 与 jsDelivr 备用线路，下载失败自动切换。\n所有线路暂时不可用时会稍后重试，已发现的更新提示仍会保留。\n安装期间 MusicBar 会退出，设置和缓存会保留，音乐播放器继续运行。", 9, Theme.Muted); hint.Height = 94; page.Controls.Add(hint);
            var releases = new LinkLabel { Text = "查看发布记录 ↗", AutoSize = true, LinkColor = Theme.Muted, ActiveLinkColor = Theme.Accent, VisitedLinkColor = Theme.Muted, BackColor = Theme.Background, Font = Theme.Font(9, FontStyle.Regular), LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 0, 0, 10) };
            releases.LinkClicked += delegate { var latest = updateService.Latest; OpenUpdateUrl(latest == null ? "https://github.com/" + updateService.Repository + "/releases" : latest.ReleaseUrl); }; releases.Enabled = updateService.Configured; page.Controls.Add(releases);
        }
        private void RefreshUpdateView()
        {
            var latest = updateService.Latest; bool available = latest != null && latest.IsNew;
            versionButton.NotificationDot = available; versionButton.Invalidate();
            versionButton.AccessibleDescription = available ? "发现新版本 v" + latest.Version : "点击查看软件更新";
            updateVersion.Text = "当前版本 " + AppUpdateService.VersionLabel + (available ? "    →    v" + latest.Version : "");
            updateStatus.Text = updateService.Status;
            updateNotes.Text = available ? "新版说明\n\n" + (string.IsNullOrWhiteSpace(latest.Notes) ? "此版本未提供更新说明。" : latest.Notes) : "发现新版本时，这里会显示更新内容。\n\n点击「下载并安装」后，程序会下载并校验安装包，\n随后打开安装向导并退出 MusicBar。";
            checkUpdateButton.Enabled = !updateBusy && updateService.Configured;
            downloadUpdateButton.Enabled = !updateBusy && available;
        }
        private async Task CheckForUpdates()
        {
            if (updateBusy || IsDisposed || updateLifetime.IsCancellationRequested) return;
            updateBusy = true; lastUpdateCheck = DateTime.UtcNow; RefreshUpdateView(); updateStatus.Text = "正在检查更新…";
            try { bool success = await updateService.CheckAsync(updateLifetime.Token); nextUpdateCheck = DateTime.UtcNow.AddMinutes(success ? 360 : 15); }
            catch (OperationCanceledException) { }
            catch (Exception) { nextUpdateCheck = DateTime.UtcNow.AddMinutes(15); }
            finally { updateBusy = false; if (!IsDisposed && !updateLifetime.IsCancellationRequested) RefreshUpdateView(); }
        }
        private async Task DownloadUpdate()
        {
            if (updateBusy || IsDisposed || updateLifetime.IsCancellationRequested || updateService.Latest == null || !updateService.Latest.IsNew) return;
            updateBusy = true; bool acceptingProgress = true; RefreshUpdateView();
            updateProgress.Value = 0; updateProgress.Indeterminate = true; updateProgress.Visible = updateProgressText.Visible = true;
            updateProgressText.Text = "连接中"; downloadUpdateButton.Text = "正在下载…"; updateStatus.Text = "正在下载，失败时自动切换线路…";
            try
            {
                var release = updateService.Latest;
                var progress = new Progress<int>(value =>
                {
                    if (!acceptingProgress || IsDisposed || updateLifetime.IsCancellationRequested) return;
                    updateProgress.Indeterminate = value < 0; updateProgress.Value = value;
                    updateProgressText.Text = value < 0 ? "连接中" : value + "%";
                    updateStatus.Text = value < 0 ? "正在连接下载线路…" : value >= 100 ? "下载完成，正在校验…" : "正在下载更新 · " + value + "%";
                });
                bool reusable = downloadedRelease != null && release.Version == downloadedRelease.Version && string.Equals(release.Sha256, downloadedRelease.Sha256, StringComparison.OrdinalIgnoreCase) && AppUpdateService.VerifyFile(downloadedUpdate, release.Sha256);
                if (!reusable) downloadedUpdate = await updateService.DownloadAsync(release, progress, updateLifetime.Token);
                downloadedRelease = release; acceptingProgress = false;
                if (IsDisposed || updateLifetime.IsCancellationRequested) return;
                updateProgress.Indeterminate = false; updateProgress.Value = 100; updateProgressText.Text = "100%";
                downloadUpdateButton.Text = "打开安装向导…"; updateStatus.Text = "校验通过，正在打开安装向导…";
                InstallUpdate();
            }
            catch (OperationCanceledException) { if (!IsDisposed && !updateLifetime.IsCancellationRequested) updateStatus.Text = "更新已取消，可稍后重试。"; }
            catch (Exception ex) { if (!IsDisposed && !updateLifetime.IsCancellationRequested) updateStatus.Text = "更新未完成，可重试：" + ex.Message; }
            finally
            {
                acceptingProgress = false; updateBusy = false;
                if (!IsDisposed && !updateLifetime.IsCancellationRequested)
                {
                    updateProgress.Indeterminate = false; updateProgress.Visible = updateProgressText.Visible = false; downloadUpdateButton.Text = "下载并安装";
                    string status = updateStatus.Text; RefreshUpdateView(); updateStatus.Text = status;
                }
            }
        }
        private void InstallUpdate()
        {
            if (downloadedRelease == null || !downloadedRelease.IsNew || !AppUpdateService.VerifyFile(downloadedUpdate, downloadedRelease.Sha256)) throw new IOException("安装文件校验失败，请重新下载。");
            using (var installer = Process.Start(new ProcessStartInfo(downloadedUpdate, "/DIR=\"" + AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) + "\"") { UseShellExecute = true }))
            {
                if (installer == null) throw new IOException("安装向导未能启动，请稍后重试。");
                ExitApplication();
            }
        }
        private void OpenUpdateUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { ShowError("无法打开网页", ex.Message); }
        }
        private void NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable || !IsHandleCreated || IsDisposed || updateLifetime.IsCancellationRequested) return;
            try { BeginInvoke((MethodInvoker)delegate { if (controller.Settings.AutoCheckUpdates && DateTime.UtcNow - lastUpdateCheck > TimeSpan.FromMinutes(1)) nextUpdateCheck = DateTime.UtcNow.AddSeconds(5); }); } catch (InvalidOperationException) { }
        }
        private async Task SearchLyrics()
        {
            if (!searchButton.Enabled) return;
            if (!controller.Settings.OnlineLyrics) { lyricStatus.Text = "在线获取已关闭。请开启「自动获取在线歌词」，或导入本地 LRC。"; return; }
            string text = query.Text.Trim();
            if (text.Length == 0 && controller.Snapshot.HasTrack) { text = controller.Snapshot.Title + " " + controller.Snapshot.Artist; query.Text = text; }
            if (text.Length == 0) { lyricStatus.Text = "请输入歌名和歌手。"; return; }
            if (searchRequest != null) { searchRequest.Cancel(); searchRequest.Dispose(); }
            var request = new CancellationTokenSource(); searchRequest = request;
            lyricStatusRequest = request;
            MusicPlayer player = lyricSource.SelectedIndex == 0 ? MusicPlayer.QQMusic : MusicPlayer.NetEase;
            resultsTrackKey = controller.Snapshot.TrackKey;
            results.Items.Clear(); fetchButton.Enabled = false; searchButton.Enabled = false; lyricStatus.Text = "正在搜索歌词…";
            try
            {
                var songs = await controller.Repository.SearchAsync(player, text, request.Token);
                if (IsDisposed || request.IsCancellationRequested || !controller.Settings.OnlineLyrics) return;
                foreach (var song in songs) results.Items.Add(song);
                lyricStatus.Text = songs.Count == 0 ? "没有找到结果，可换用另一歌词来源，或导入 LRC。" : "找到 " + songs.Count + " 个结果，请选择对应版本。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) lyricStatus.Text = "搜索失败，可导入本地 LRC。" + Environment.NewLine + ex.Message; }
            finally
            {
                if (lyricStatusRequest == request) lyricStatusRequest = null;
                if (!IsDisposed) searchButton.Enabled = true;
            }
        }
        private async Task UseSelectedLyric()
        {
            var song = results.SelectedItem as LyricSearchResult;
            if (song == null || !fetchButton.Enabled) return;
            if (!controller.Settings.OnlineLyrics) { lyricStatus.Text = "在线获取已关闭，可导入本地 LRC。"; return; }
            if (controller.Snapshot.TrackKey != resultsTrackKey) { lyricStatus.Text = "播放器已经切歌，请按当前歌曲重新搜索，避免绑定错误的歌词。"; return; }
            string before = controller.Snapshot.TrackKey;
            if (searchRequest != null) { searchRequest.Cancel(); searchRequest.Dispose(); }
            var request = new CancellationTokenSource(); searchRequest = request;
            lyricStatusRequest = request;
            fetchButton.Enabled = false; lyricStatus.Text = "正在获取所选歌词…";
            try
            {
                var doc = await controller.Repository.FetchAsync(song, request.Token);
                if (IsDisposed || request.IsCancellationRequested || !controller.Settings.OnlineLyrics) return;
                if (controller.Snapshot.TrackKey != before) { lyricStatus.Text = "获取过程中播放器已切歌，请重新选择歌词。"; return; }
                if (doc == null || !doc.HasTimedLyrics && string.IsNullOrWhiteSpace(doc.PlainText)) { lyricStatus.Text = "该歌曲暂时没有可用歌词，可导入 LRC。"; return; }
                controller.ApplyDocument(doc);
                lyricStatus.Text = controller.Message;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) lyricStatus.Text = "获取歌词失败：" + ex.Message; }
            finally
            {
                if (lyricStatusRequest == request) lyricStatusRequest = null;
                if (!IsDisposed) fetchButton.Enabled = results.SelectedItem is LyricSearchResult;
            }
        }
        private LyricDocument displayedDocument;
        private bool displayedTranslations;
        private string displayedTranslationLrc = "";
        private void ControllerChanged(object sender, EventArgs args)
        {
            if (IsDisposed) return;
            var snapshot = controller.Snapshot;
            var s = controller.Settings;
            if (positionPreview && (snapshot.HasTrack || controller.ManualMode) && (!s.HideWhenPaused || (controller.ManualMode ? controller.ManualPlaying : snapshot.IsPlaying)))
            {
                positionPreview = false; s.PreviewEnabled = false; controller.Store.Save(s);
            }
            headerStatus.Text = s.PreviewEnabled ? "●  正在预览任务栏效果" : snapshot.HasTrack ? "●  " + MusicSnapshot.PlayerName(snapshot.Player) + (snapshot.IsPlaying ? " · 播放中" : " · 已暂停") : "●  等待播放器";
            songTitle.Text = snapshot.HasTrack ? snapshot.Title : "等待播放歌曲";
            songArtist.Text = snapshot.HasTrack ? snapshot.Artist + "  ·  " + MusicSnapshot.PlayerName(snapshot.Player) : "打开 QQ 音乐或网易云音乐，并播放一首歌";
            if (controller.IsSongInfo && controller.Document != null && controller.Document.HasTimedLyrics)
                liveLine.Text = snapshot.HasTimeline || controller.ManualMode ? "歌词已连接，等待第一句…" : "歌词已连接，等待播放器进度";
            else if (controller.Document != null && !controller.Document.HasTimedLyrics && string.IsNullOrWhiteSpace(controller.Document.PlainText)) liveLine.Text = controller.Document.Source + "，当前显示歌名";
            else liveLine.Text = controller.NeedsNetEaseIntegration && !controller.ManualMode ? "等待网易云播放进度接入" : controller.IsSongInfo ? (controller.Searching ? "正在获取歌词…" : "当前显示歌名，歌词尚未获取成功") : !string.IsNullOrWhiteSpace(controller.Current) ? controller.Current : controller.Searching ? "正在匹配歌词…" : "歌词将在这里和任务栏同步显示";
            liveTranslation.Text = !s.ShowTranslation ? "译文显示已关闭" : !string.IsNullOrWhiteSpace(controller.CurrentTranslation) ? controller.CurrentTranslation : controller.Document == null ? "有译文时会在这里和任务栏第二行显示" : controller.Document.HasTranslation ? "当前句没有对应译文" : string.IsNullOrWhiteSpace(controller.Document.TranslationStatus) ? "该歌词源未提供译文" : controller.Document.TranslationStatus;
            if (snapshot.Player == MusicPlayer.NetEase && snapshot.HasTimeline) neteaseRecoveryStatus = "";
            connectionHint.Text = pendingNetEasePath.Length > 0 || neteaseRecoveryStatus.Length > 0 ? neteaseRecoveryStatus : controller.Message + (snapshot.HasTrack && !snapshot.HasTimeline && !controller.Message.Contains("修复并启动接入") ? Environment.NewLine
                + (snapshot.Player == MusicPlayer.NetEase ? "请点本页「修复并启动接入」。启动方式修复后，从网易云托盘退出一次，MusicBar 会自动重新接入。" : "此播放器未提供播放进度：可到「歌词管理」开始手动同步。") : "");
            neteaseLaunchButton.Text = pendingNetEasePath.Length > 0 ? "等待网易云退出" : controller.NetEaseRunning && controller.NeedsNetEaseIntegration ? "修复并启动接入" : "启动网易云接入";
            neteaseLaunchButton.Enabled = pendingNetEasePath.Length == 0;
            qqStatus.Text = !s.QQEnabled ? "●  插件已关闭" : snapshot.Player == MusicPlayer.QQMusic && snapshot.HasTrack
                ? snapshot.HasTimeline ? "●  已连接播放信息" : "●  已连接歌名，未提供进度" : controller.QQRunning ? "●  已运行，等待媒体信息" : "●  等待播放器启动";
            neteaseStatus.Text = !s.NetEaseEnabled ? "●  插件已关闭" : snapshot.Player == MusicPlayer.NetEase && snapshot.HasTrack
                ? snapshot.HasTimeline ? "●  已连接真实播放进度" : "●  缺少播放进度，请修复接入" : controller.NetEaseRunning ? "●  已运行，请启动网易云接入" : "●  等待播放器启动";
            qqStatus.ForeColor = snapshot.Player == MusicPlayer.QQMusic && snapshot.HasTimeline ? Theme.Accent : Theme.Muted;
            neteaseStatus.ForeColor = snapshot.Player == MusicPlayer.NetEase && snapshot.HasTimeline ? Theme.Accent : Theme.Muted;
            previewButton.Text = s.PreviewEnabled ? "结束任务栏预览" : "预览任务栏效果";
            dragButton.Text = s.ClickThrough ? "解锁并拖动歌词" : "锁定歌词位置";
            string translationLrc = controller.Document == null ? "" : controller.Document.Translation ?? "";
            if (controller.Document != displayedDocument || displayedTranslations != s.ShowTranslation || displayedTranslationLrc != translationLrc)
            {
                displayedDocument = controller.Document; displayedTranslations = s.ShowTranslation; displayedTranslationLrc = translationLrc; lyricLines.Items.Clear(); lastLineIndex = -2;
                if (displayedDocument != null)
                {
                    for (int i = 0; i < displayedDocument.Lines.Count; i++)
                    {
                        var line = displayedDocument.Lines[i];
                        string translated = s.ShowTranslation ? LrcParser.GetTranslationForLine(displayedDocument, i) : "";
                        lyricLines.Items.Add(Clock(line.Seconds) + "  " + line.Text + (string.IsNullOrWhiteSpace(translated) ? "" : "  /  " + translated));
                    }
                    if (!displayedDocument.HasTimedLyrics && !string.IsNullOrWhiteSpace(displayedDocument.PlainText)) foreach (string line in displayedDocument.PlainText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) lyricLines.Items.Add(line);
                }
            }
            // A failed/new-song lookup can have no document. Its status must still
            // replace the previous song's connected message, without erasing the
            // results or progress of an explicit manual search.
            if (displayedControllerMessage != controller.Message || displayedStatusTrackKey != snapshot.PlaybackKey)
            {
                displayedControllerMessage = controller.Message;
                displayedStatusTrackKey = snapshot.PlaybackKey;
                if (lyricStatusRequest == null) lyricStatus.Text = controller.Message;
            }
            double position = controller.ManualMode ? controller.ManualPosition : snapshot.CurrentPosition;
            if (displayedDocument != null && displayedDocument.HasTimedLyrics)
            {
                int index = controller.ManualMode || snapshot.HasTimeline ? displayedDocument.FindLine(position + s.OffsetSeconds) : -1;
                if (index != lastLineIndex) { lastLineIndex = index; lyricLines.SelectedIndex = index; if (index >= 0) lyricLines.TopIndex = Math.Max(0, index - 2); }
            }
            manualButton.Enabled = controller.Document != null && controller.Document.HasTimedLyrics;
            manualButton.Text = controller.ManualPlaying ? "暂停手动同步" : "开始手动同步";
            usePlayerButton.Enabled = snapshot.HasTimeline;
            manualStatus.Text = controller.ManualMode ? "正在使用手动时间轴。切歌后会恢复自动接入，需随音乐手动校准。" : snapshot.HasTimeline ? "当前已读取播放器进度，一般无需手动同步。" : "播放器未提供时间轴时，导入或选择歌词后按歌曲位置开始计时。";
            positionLabel.Text = (controller.ManualMode ? "手动 " : "") + Clock(position);
            UpdateOverlay();
            footerStatus.Text = !string.IsNullOrEmpty(controller.Store.LastError) ? controller.Store.LastError : "设置自动保存  ·  " + (s.OverlayEnabled ? overlay.LayoutStatus : "任务栏歌词已关闭") + (s.PreviewEnabled ? "  ·  预览包含示例歌词" : "");
        }
        private static string Clock(double seconds)
        {
            seconds = Math.Max(0, seconds); return ((int)seconds / 60).ToString("00") + ":" + ((int)seconds % 60).ToString("00");
        }
        private void Apply(bool reload)
        {
            if (updating) return;
            overlay.UpdateSettings(controller.Settings);
            controller.SettingsChanged(reload);
            UpdatePreview();
        }
        private void TogglePositionLock()
        {
            var settings = controller.Settings;
            settings.ClickThrough = !settings.ClickThrough;
            if (!settings.ClickThrough)
            {
                settings.OverlayEnabled = true;
                if (!controller.ShouldDisplay) { positionPreview = !settings.PreviewEnabled; settings.PreviewEnabled = true; }
            }
            else if (positionPreview) { positionPreview = false; settings.PreviewEnabled = false; }
            bool previous = updating; updating = true;
            positionLock.Checked = settings.ClickThrough; overlaySwitch.Checked = settings.OverlayEnabled;
            updating = previous;
            Apply(false);
        }
        private void MoveHorizontally(int amount)
        {
            var settings = controller.Settings;
            settings.HorizontalOffset = Math.Max(-20000, Math.Min(20000, settings.HorizontalOffset + amount));
            SyncPositionControls(); Apply(false);
        }
        private void SyncPositionControls()
        {
            bool previous = updating; updating = true;
            horizontalPosition.Value = Math.Max(horizontalPosition.Minimum, Math.Min(horizontalPosition.Maximum, controller.Settings.HorizontalOffset));
            verticalPosition.Value = Math.Max(verticalPosition.Minimum, Math.Min(verticalPosition.Maximum, controller.Settings.VerticalOffset));
            positionAlignment.SelectedIndex = controller.Settings.Alignment == "center" ? 1 : controller.Settings.Alignment == "right" ? 2 : 0;
            updating = previous;
        }
        private void UpdateOverlay()
        {
            overlay.SetPreview(controller.Settings.PreviewEnabled);
            string lineKey = controller.Settings.PreviewEnabled ? "preview" : controller.CurrentLineIndex < 0 || controller.IsSongInfo ? "" : controller.Snapshot.PlaybackKey + "|" + controller.CurrentLineIndex;
            overlay.SetLineTiming(lineKey, controller.RemainingLineSeconds > 0 ? controller.RemainingLineSeconds : double.NaN,
                controller.Settings.PreviewEnabled || (controller.ManualMode ? controller.ManualPlaying : controller.Snapshot.IsPlaying));
            overlay.SetLyrics(controller.Current, controller.Next, controller.CurrentTranslation, controller.ShouldDisplay);
            PlaybackFrameChanged(this, EventArgs.Empty);
        }
        private void PlaybackFrameChanged(object sender, EventArgs args)
        {
            if (!IsDisposed) overlay.SetKaraokeProgress(controller.OriginalKaraokeProgress, controller.TranslationKaraokeProgress);
        }
        private void UpdatePreview()
        {
            if (previewImage == null || previewImage.Width < 2) return;
            var old = previewImage.Image;
            previewImage.Image = overlay.RenderPreview(Math.Max(200, previewImage.ClientSize.Width - 24), 68);
            if (old != null) old.Dispose();
        }
        private void LaunchPlayer(MusicPlayer player)
        {
            if (player == MusicPlayer.NetEase && controller.Reader.IsPlayerRunning(player) && !NetEaseBridgeReader.OwnsListener(NetEaseBridgeReader.Port))
            {
                try
                {
                    RememberNetEasePath();
                    string path = File.ReadAllText(Path.Combine(controller.Store.DataDirectory, "netease-player-path.txt")).Trim();
                    if (!NetEaseLaunchIntegration.ValidPlayer(path)) throw new InvalidOperationException("未找到正在运行的网易云安装路径，请在常规设置选择 cloudmusic.exe。");
                    NetEaseLaunchIntegration.Configure(path, controller.Store.DataDirectory);
                    verifyingNetEaseStart = false; pendingNetEasePath = path;
                    neteaseStartDeadline = DateTime.UtcNow.AddMinutes(10);
                    neteaseRecoveryStatus = "已修复网易云启动方式。请从网易云托盘菜单选择退出；MusicBar 会自动重新打开网易云，随后播放歌曲即可同步歌词。";
                    neteaseStartTimer.Start();
                    ControllerChanged(this, EventArgs.Empty);
                }
                catch (Exception ex) { ShowError("网易云接入修复失败", ex.Message); }
                return;
            }
            string exe = player == MusicPlayer.QQMusic ? "QQMusic.exe" : "cloudmusic.exe";
            string folder = player == MusicPlayer.QQMusic ? "Tencent\\QQMusic" : "NetEase\\CloudMusic";
            var candidates = new List<string>();
            if (player == MusicPlayer.NetEase)
            {
                try { candidates.Add(File.ReadAllText(Path.Combine(controller.Store.DataDirectory, "netease-player-path.txt")).Trim()); } catch { }
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folder, exe));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), folder, exe));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder, exe));
            foreach (string name in new[] { "QQMusic", "cloudmusic" })
            {
                if (name != Path.GetFileNameWithoutExtension(exe)) continue;
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try { candidates.Insert(0, process.MainModule.FileName); } catch { }
                    finally { process.Dispose(); }
                }
            }
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                try { StartPlayer(candidate, player); return; }
                catch (Exception ex) { ShowError("无法启动播放器", ex.Message); return; }
            }
            using (var dialog = new OpenFileDialog { Title = "选择 " + MusicSnapshot.PlayerName(player) + " 的 " + exe, Filter = exe + "|" + exe, FileName = exe, CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) try { StartPlayer(dialog.FileName, player); } catch (Exception ex) { ShowError("无法启动播放器", ex.Message); }
            }
        }
        private void StartPlayer(string path, MusicPlayer player)
        {
            if (player == MusicPlayer.NetEase && !Path.GetFileName(path).Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择网易云安装目录中的 cloudmusic.exe。");
            var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) };
            if (player == MusicPlayer.NetEase)
            {
                // A successful first launch also repairs ordinary shortcuts so
                // the next session keeps the progress bridge. Backups remain reversible.
                NetEaseLaunchIntegration.Configure(path, controller.Store.DataDirectory);
                start.Arguments = NetEaseLaunchIntegration.Arguments;
                SaveNetEasePath(path);
            }
            Process.Start(start);
        }
        private void CompleteNetEaseStart(object sender, EventArgs args)
        {
            if (verifyingNetEaseStart)
            {
                if (NetEaseBridgeReader.OwnsListener(NetEaseBridgeReader.Port))
                {
                    verifyingNetEaseStart = false; neteaseStartTimer.Stop();
                    neteaseRecoveryStatus = ""; ControllerChanged(this, EventArgs.Empty);
                }
                else if (DateTime.UtcNow >= neteaseStartDeadline || !controller.Settings.NetEaseEnabled)
                {
                    verifyingNetEaseStart = false; neteaseStartTimer.Stop();
                    neteaseRecoveryStatus = "网易云重启后未开启播放进度接入。客户端更新可能丢失启动参数，请点「修复并启动接入」后从网易云托盘退出一次。";
                    ControllerChanged(this, EventArgs.Empty);
                }
                return;
            }
            if (pendingNetEasePath.Length == 0) { neteaseStartTimer.Stop(); return; }
            if (!controller.Settings.NetEaseEnabled || DateTime.UtcNow >= neteaseStartDeadline)
            {
                pendingNetEasePath = ""; neteaseStartTimer.Stop();
                neteaseRecoveryStatus = "自动接入已取消。需要时可再次点击「修复并启动接入」。";
                ControllerChanged(this, EventArgs.Empty); return;
            }
            if (controller.Reader.IsPlayerRunning(MusicPlayer.NetEase)) return;
            string path = pendingNetEasePath; pendingNetEasePath = ""; neteaseStartTimer.Stop();
            try
            {
                StartPlayer(path, MusicPlayer.NetEase);
                neteaseRecoveryStatus = "网易云正在重新打开，等待确认播放进度接入…";
                verifyingNetEaseStart = true; neteaseStartDeadline = DateTime.UtcNow.AddSeconds(12);
                neteaseStartTimer.Start();
            }
            catch (Exception ex) { neteaseRecoveryStatus = "网易云接入启动失败：" + ex.Message; }
            ControllerChanged(this, EventArgs.Empty);
        }
        private void RememberNetEasePath()
        {
            foreach (var app in Process.GetProcessesByName("cloudmusic"))
                using (app) { try { SaveNetEasePath(app.MainModule.FileName); return; } catch { } }
        }
        private void SaveNetEasePath(string path)
        {
            if (!File.Exists(path) || !Path.GetFileName(path).Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase)) return;
            try { File.WriteAllText(Path.Combine(controller.Store.DataDirectory, "netease-player-path.txt"), path); } catch { }
        }
        private void ShowError(string title, string message) { MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        public void RestoreWindow() { ShowInTaskbar = !controller.Settings.HideTaskbarIcon; Show(); WindowState = FormWindowState.Normal; Activate(); }
        public void ExitApplication() { exiting = true; tray.Visible = false; Close(); Application.Exit(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
                updateTimer.Stop(); updateTimer.Dispose(); updateLifetime.Cancel(); updateService.Dispose();
                controller.Changed -= ControllerChanged;
                controller.PlaybackFrame -= PlaybackFrameChanged;
                appHotkey.Dispose();
                neteaseStartTimer.Stop(); neteaseStartTimer.Dispose();
                if (searchRequest != null) { searchRequest.Cancel(); searchRequest.Dispose(); }
                if (previewImage != null && previewImage.Image != null) previewImage.Image.Dispose();
                if (tray != null) tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
