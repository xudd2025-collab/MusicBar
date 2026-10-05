using System;
using System.Runtime.InteropServices;

namespace MusicBar
{
    internal sealed class AppHotkey : IDisposable
    {
        internal const int Id = 0x4D42;
        private IntPtr window;
        internal bool Registered { get; private set; }
        internal bool Register(IntPtr handle, int preset)
        {
            Dispose();
            uint modifiers = preset == 1 ? 2u | 4u : preset == 2 ? 1u | 4u : 1u | 2u;
            Registered = RegisterHotKey(handle, Id, modifiers | 0x4000u, 0x4Du);
            if (Registered) window = handle;
            return Registered;
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
