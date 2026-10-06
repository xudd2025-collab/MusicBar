using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MusicBar
{
    internal sealed class AppHotkey : IDisposable
    {
        internal const int Id = 0x4D42;
        private IntPtr window;
        internal bool Registered { get; private set; }
        internal bool Register(IntPtr handle, int modifiers, int key)
        {
            if (!AppSettings.ValidHotkey(key, modifiers)) return false;
            Dispose();
            Registered = RegisterHotKey(handle, Id, (uint)modifiers | 0x4000u, (uint)key);
            if (Registered) window = handle;
            return Registered;
        }
        internal static string Describe(int key, int modifiers)
        {
            string name = key >= 0x30 && key <= 0x39 ? ((char)key).ToString() : ((Keys)key).ToString();
            switch (key)
            {
                case 0xBA: name = ";"; break; case 0xBB: name = "+"; break;
                case 0xBC: name = ","; break; case 0xBD: name = "-"; break;
                case 0xBE: name = "."; break; case 0xBF: name = "/"; break;
                case 0xC0: name = "`"; break; case 0xDB: name = "["; break;
                case 0xDC: name = "\\"; break; case 0xDD: name = "]"; break;
                case 0xDE: name = "'"; break;
            }
            return ((modifiers & 2) != 0 ? "Ctrl + " : "") +
                ((modifiers & 1) != 0 ? "Alt + " : "") +
                ((modifiers & 4) != 0 ? "Shift + " : "") + name;
        }
        public void Dispose()
        {
            if (window != IntPtr.Zero) UnregisterHotKey(window, Id);
            window = IntPtr.Zero; Registered = false;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
