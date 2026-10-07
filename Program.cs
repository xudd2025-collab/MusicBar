using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MusicBar
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] arguments)
        {
            try
            {
                Theme.EnableNativeDarkMode();
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                bool isNew;
                bool automatic = Array.IndexOf(arguments, "--autostart") >= 0;
                using (var mutex = new Mutex(true, "Local\\MusicBarTaskbarLyrics", out isNew))
                {
                    if (!isNew)
                    {
                        if (!automatic)
                        {
                            try { using (var signal = EventWaitHandle.OpenExisting("Local\\MusicBarOpenSettings")) signal.Set(); }
                            catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("MusicBar 正在启动，请稍后再次打开，或使用已设置的快捷键。", "MusicBar", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                        }
                        return;
                    }
                    var store = new SettingsStore(AppDomain.CurrentDomain.BaseDirectory);
                    using (var controller = new LyricController(store.Load(), store))
                    using (var overlay = new LyricOverlay())
                    using (var form = new MainForm(controller, overlay, automatic))
                    using (var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\MusicBarOpenSettings"))
                    {
                        form.Shown += delegate { controller.Start(); };
                        var registration = ThreadPool.RegisterWaitForSingleObject(openSettings, delegate
                        {
                            try { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke((MethodInvoker)delegate { if (!form.IsDisposed) form.RestoreWindow(); }); }
                            catch (InvalidOperationException) { }
                        }, null, Timeout.Infinite, false);
                        try { Application.Run(form); }
                        finally { registration.Unregister(null); }
                    }
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception ex)
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt");
                try { File.WriteAllText(path, ex.ToString()); } catch { }
                MessageBox.Show("启动失败：" + ex.Message + Environment.NewLine + "请检查 Windows 10 1809 / Windows 11 和 .NET Framework 4.8。", "MusicBar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
