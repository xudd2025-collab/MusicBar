using System;
using System.IO;
using Microsoft.Win32;

namespace MusicBar
{
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MusicBarTaskbarLyrics";
        internal static string Command(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable) || executable.IndexOf('"') >= 0 || !Path.IsPathRooted(executable)) throw new ArgumentException("程序路径无效。", "executable");
            return "\"" + Path.GetFullPath(executable) + "\" --autostart";
        }
        public static bool IsEnabled(string executable)
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false)) return key != null && string.Equals(key.GetValue(ValueName) as string, Command(executable), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        public static void SetEnabled(bool enabled, string executable)
        {
            string command = Command(executable);
            if (enabled && !File.Exists(executable)) throw new FileNotFoundException("找不到当前程序，请重新打开 MusicBar。", executable);
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new InvalidOperationException("无法设置开机启动。请检查当前用户权限。");
                if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }
    }
}
