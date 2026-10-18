using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MusicBar
{
    /// <summary>
    /// Shows the public author project. The caller persists dismissal only after OK.
    /// A browser launch, an error page, a sign-in page or another repository never qualifies.
    /// </summary>
    public sealed class AuthorProjectDialog : Form
    {
        public const string ProjectUrl = "https://github.com/xudd2025-collab/MusicBar";
        private const string RuntimeUrl = "https://developer.microsoft.com/microsoft-edge/webview2/#download-section";
        private readonly string dataDirectory;
        private readonly WebView2 browser;
        private readonly Panel placeholder;
        private readonly Label placeholderTitle, placeholderDescription, status;
        private readonly LinkLabel runtimeLink;
        private readonly Button enter, dismiss;
        private CoreWebView2 core;
        private bool initializing, closing, visitedSuccessfully;
        private int operation;
        private ulong latestNavigation;

        public bool VisitedSuccessfully { get { return visitedSuccessfully; } }

        public AuthorProjectDialog(string dataDirectory)
        {
            this.dataDirectory = dataDirectory;
            Text = "MusicBar · 作者与项目";
            Icon = Theme.CreateIcon();
            ClientSize = new Size(920, 680);
            MinimumSize = new Size(780, 600);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            Font = Theme.Font(9, FontStyle.Regular);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Padding = new Padding(24);
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AccessibleName = "MusicBar 作者与官方项目";

            var header = new Panel { Dock = DockStyle.Top, Height = 165, BackColor = Theme.Background };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = Theme.Background };
            var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Theme.Border };
            Controls.Add(frame); Controls.Add(footer); Controls.Add(header);

            AddLabel(header, "欢迎使用 MusicBar", 0, 0, 560, 33, 17, Theme.Text, FontStyle.Bold);
            AddLabel(header, "免费开源，谨防付费购买", 1, 38, 560, 25, 11, Theme.Accent, FontStyle.Regular);
            AddLabel(header, "作者：xudd2025-collab", 1, 72, 560, 22, 9, Theme.Muted, FontStyle.Regular);
            var project = new LinkLabel { Text = ProjectUrl, Bounds = new Rectangle(0, 98, 600, 24), LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Accent, BackColor = Theme.Background, Font = Theme.Font(9, FontStyle.Regular),
                AccessibleName = "进入 GitHub 官方项目", TabIndex = 0 };
            project.LinkClicked += delegate { BeginVisit(); };
            header.Controls.Add(project);
            AddLabel(header, "进入一次官方项目页面后，即可永久关闭作者提示和水印。", 1, 130, 850, 23, 9, Theme.Muted, FontStyle.Regular);

            enter = Theme.Button("进入 GitHub 项目", true);
            enter.SetBounds(header.Width - 180, 25, 180, 38);
            enter.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            enter.TabIndex = 1;
            enter.AccessibleName = "在当前窗口打开 GitHub 官方项目";
            enter.Click += delegate { BeginVisit(); };
            header.Controls.Add(enter);
            var noAccount = AddLabel(header, "无需登录、关注或点赞", header.Width - 180, 70, 180, 22, 8.5f, Theme.Muted, FontStyle.Regular);
            noAccount.TextAlign = ContentAlignment.MiddleCenter;
            noAccount.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            browser = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Surface, Visible = false,
                AccessibleName = "GitHub 官方项目页面", TabIndex = 2 };
            frame.Controls.Add(browser);
            placeholder = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
            frame.Controls.Add(placeholder);
            placeholderTitle = AddLabel(placeholder, "认识 MusicBar 的原作者", 24, 40, 800, 35, 15, Theme.Text, FontStyle.Regular);
            placeholderDescription = AddLabel(placeholder, "点击「进入 GitHub 项目」，官方页面将在这里打开。\r\n页面成功加载后，可选择关闭提示和水印。", 24, 86, 800, 75, 10, Theme.Muted, FontStyle.Regular);
            placeholderTitle.Anchor = placeholderDescription.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            runtimeLink = new LinkLabel { Text = "查看微软官方 WebView2 运行库", Bounds = new Rectangle(24, 176, 680, 26),
                LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Accent,
                Font = Theme.Font(9, FontStyle.Regular), BackColor = Theme.Surface, Visible = false,
                AccessibleName = "在系统浏览器查看微软官方 WebView2 运行库", TabIndex = 3 };
            runtimeLink.LinkClicked += delegate { OpenRuntimePage(); };
            placeholder.Controls.Add(runtimeLink);

            dismiss = Theme.Button("关闭提示和水印", true);
            dismiss.Bounds = new Rectangle(footer.Width - 164, 24, 164, 38);
            dismiss.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            dismiss.Enabled = false;
            dismiss.TabIndex = 5;
            dismiss.AccessibleName = "永久关闭作者提示和水印";
            dismiss.Click += delegate
            {
                if (!visitedSuccessfully || closing) return;
                DialogResult = DialogResult.OK;
                Close();
            };
            footer.Controls.Add(dismiss);
            var later = Theme.Button("稍后", false);
            later.Bounds = new Rectangle(footer.Width - 262, 24, 88, 38);
            later.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            later.DialogResult = DialogResult.Cancel;
            later.AccessibleName = "稍后再看，保留作者提示和水印";
            later.TabIndex = 4;
            footer.Controls.Add(later);
            status = AddLabel(footer, "稍后也可从设置中打开作者项目。", 0, 18, Math.Max(100, footer.Width - 286), 48, 9, Theme.Muted, FontStyle.Regular);
            status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            AcceptButton = enter;
            CancelButton = later;
            Theme.ApplyDarkControls(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyDarkWindow(Handle);
        }

        private async void BeginVisit()
        {
            if (closing || IsDisposed || initializing) return;
            int request = ++operation;
            initializing = true;
            enter.Enabled = false;
            runtimeLink.Visible = false;
            SetStatus("正在打开 GitHub 官方项目…", false);
            try
            {
                if (core == null)
                {
                    placeholderTitle.Text = "正在准备项目页面";
                    placeholderDescription.Text = "首次打开可能需要几秒钟，请稍候。\r\n无需登录 GitHub。";
                    string profilePath = Path.Combine(Path.GetFullPath(dataDirectory), "author-webview");
                    Directory.CreateDirectory(profilePath);
                    // This persistent profile belongs only to this dialog. Never touch another browser's data.
                    var environment = await CoreWebView2Environment.CreateAsync(null, profilePath, null);
                    if (!IsCurrent(request)) return;
                    await browser.EnsureCoreWebView2Async(environment, null);
                    if (!IsCurrent(request)) return;
                    var initializedCore = browser.CoreWebView2;
                    initializedCore.Settings.AreDevToolsEnabled = false;
                    initializedCore.Settings.IsStatusBarEnabled = false;
                    initializedCore.Settings.IsWebMessageEnabled = false;
                    initializedCore.Settings.AreHostObjectsAllowed = false;
                    initializedCore.Settings.IsPasswordAutosaveEnabled = false;
                    initializedCore.Settings.IsGeneralAutofillEnabled = false;
                    initializedCore.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
                    initializedCore.NavigationStarting += NavigationStarting;
                    initializedCore.NavigationCompleted += NavigationCompleted;
                    initializedCore.NewWindowRequested += NewWindowRequested;
                    core = initializedCore;
                }
                if (!IsCurrent(request)) return;
                browser.Visible = true;
                placeholder.Visible = false;
                core.Navigate(ProjectUrl);
                enter.Text = "重新加载项目";
            }
            catch (Exception error)
            {
                if (!IsCurrent(request)) return;
                ShowInitializationError(error);
            }
            finally
            {
                if (IsCurrent(request))
                {
                    initializing = false;
                    enter.Enabled = true;
                }
            }
        }

        private bool IsCurrent(int request) { return !closing && !IsDisposed && request == operation; }

        private void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (closing || IsDisposed) { e.Cancel = true; return; }
            Uri uri;
            // Keep top-level browsing on GitHub; subresource loading is unaffected.
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                SetStatus("请在这里打开 GitHub 官方项目页面。", false);
                return;
            }
            latestNavigation = e.NavigationId;
            if (!visitedSuccessfully) SetStatus("正在加载 GitHub 页面…", false);
        }

        private void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (closing || IsDisposed || core == null || e.NavigationId != latestNavigation) return;
            if (visitedSuccessfully) return;
            try
            {
                if (IsSuccessfulProjectNavigation(core.Source, e.IsSuccess, e.HttpStatusCode))
                {
                    visitedSuccessfully = true;
                    dismiss.Enabled = true;
                    AcceptButton = dismiss;
                    SetStatus("官方项目已成功打开，可永久关闭提示和水印。", true);
                    return;
                }
                if (!e.IsSuccess || e.HttpStatusCode < 200 || e.HttpStatusCode > 399)
                    SetStatus("页面尚未成功加载，请检查网络后点「重新加载项目」。", false);
                else
                    SetStatus("请点击「进入 GitHub 项目」，返回 MusicBar 官方项目首页。", false);
            }
            catch (Exception error)
            {
                if (!closing && !IsDisposed) SetStatus("暂时无法确认页面状态，请重新加载。" + ShortReason(error), false);
            }
        }

        internal static bool IsSuccessfulProjectNavigation(string source, bool isSuccess, int httpStatusCode)
        {
            if (!isSuccess || httpStatusCode < 200 || httpStatusCode > 399) return false;
            Uri uri;
            if (!Uri.TryCreate(source, UriKind.Absolute, out uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo)
                && String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                && String.IsNullOrEmpty(uri.Query) && String.IsNullOrEmpty(uri.Fragment)
                && (String.Equals(uri.AbsolutePath, "/xudd2025-collab/MusicBar", StringComparison.Ordinal)
                    || String.Equals(uri.AbsolutePath, "/xudd2025-collab/MusicBar/", StringComparison.Ordinal));
        }

        private void NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (closing || IsDisposed || !e.IsUserInitiated || core == null) return;
            Uri uri;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                core.Navigate(uri.AbsoluteUri);
        }

        private void ShowInitializationError(Exception error)
        {
            browser.Visible = false;
            placeholder.Visible = true;
            placeholder.BringToFront();
            placeholderTitle.Text = "暂时无法打开内置网页";
            placeholderDescription.Text = "可点击「重试打开项目」再次尝试。\r\n" + ShortReason(error);
            runtimeLink.Visible = true;
            enter.Text = "重试打开项目";
            SetStatus("官方页面尚未加载，提示和水印将继续保留。", false);
        }

        private void OpenRuntimePage()
        {
            if (closing || IsDisposed) return;
            try { Process.Start(new ProcessStartInfo(RuntimeUrl) { UseShellExecute = true }); }
            catch (Exception error) { SetStatus("无法打开系统浏览器。" + ShortReason(error), false); }
        }

        private static string ShortReason(Exception error)
        {
            string message = (error.Message ?? error.GetType().Name).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return message.Length > 220 ? message.Substring(0, 217) + "…" : message;
        }

        private void SetStatus(string text, bool success)
        {
            status.Text = text;
            status.ForeColor = success ? Color.FromArgb(141, 227, 199) : Theme.Muted;
            status.AccessibleDescription = text;
        }

        private static Label AddLabel(Control parent, string text, int x, int y, int width, int height, float size, Color color, FontStyle style)
        {
            var label = Theme.Label(text, size, color);
            label.Font = Theme.Font(size, style);
            label.SetBounds(x, y, width, height);
            parent.Controls.Add(label);
            return label;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK && !visitedSuccessfully) { e.Cancel = true; return; }
            if (DialogResult == DialogResult.None) DialogResult = DialogResult.Cancel;
            base.OnFormClosing(e);
            if (!e.Cancel) { closing = true; ++operation; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing = true;
                ++operation;
                if (core != null)
                {
                    try
                    {
                        core.NavigationStarting -= NavigationStarting;
                        core.NavigationCompleted -= NavigationCompleted;
                        core.NewWindowRequested -= NewWindowRequested;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.Runtime.InteropServices.COMException) { }
                    core = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
