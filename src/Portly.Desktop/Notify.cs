using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Desktop
{
    /// <summary>
    /// System notification from the tray app: a tray balloon on Windows, "display notification" on macOS, notify-send on Linux
    /// (skipped when it is not installed). Never throws and never blocks the caller.
    /// </summary>
    public static class Notify
    {
        /// <summary>Tests: receives (title, text, warning) instead of showing anything.</summary>
        public static Action<string, string, bool> Sink;

        public static void Show(string title, string text, bool warning = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (string.IsNullOrWhiteSpace(title)) title = "Portly";
            var sink = Sink;
            if (sink != null) { sink(title, text, warning); return; }
            Task.Run(() =>
            {
                try
                {
                    if (OS.IsWindows) WinBalloon.Show(title, text, warning);
                    else if (OS.IsMac) Run("osascript", new[] { "-e", MacScript(title, text) });
                    else if (OS.IsLinux && OnPath("notify-send")) Run("notify-send", LinuxArgs(title, text, warning));
                }
                catch { }
            });
        }

        // ---------------- macOS ----------------
        /// <summary>Text for an AppleScript string literal: only a backslash and a double quote are special; line breaks and other control characters become spaces.</summary>
        internal static string AppleScriptString(string s)
        {
            if (s == null) s = "";
            if (s.Length > 500) s = s.Substring(0, 500);
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (char.IsControl(c)) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        internal static string MacScript(string title, string text)
        {
            return "display notification \"" + AppleScriptString(text) + "\" with title \"" + AppleScriptString(title) + "\"";
        }

        // ---------------- Linux ----------------
        internal static string[] LinuxArgs(string title, string text, bool warning)
        {
            if (text != null && text.Length > 500) text = text.Substring(0, 500);
            return new[] { "-a", "Portly", "-i", warning ? "dialog-warning" : "dialog-information", "--", title, text };
        }

        static bool OnPath(string name)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { if (dir != "" && File.Exists(Path.Combine(dir, name))) return true; }
                catch { }
            }
            return false;
        }

        static void Run(string exe, string[] args)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                if (p == null) return;
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } }
            }
        }

        // ---------------- Windows ----------------
        /// <summary>
        /// A balloon needs a notification icon, and Avalonia's own tray icon cannot be reached for it, so each balloon uses its own
        /// short-lived icon (a hidden icon shows no balloon): added with the balloon, removed after ten seconds.
        /// </summary>
        static class WinBalloon
        {
            const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
            const uint NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
            const uint NIIF_INFO = 0x1, NIIF_WARNING = 0x2;
            static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
            static int _id;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct NOTIFYICONDATA
            {
                public uint cbSize;
                public IntPtr hWnd;
                public uint uID;
                public uint uFlags;
                public uint uCallbackMessage;
                public IntPtr hIcon;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
                public uint dwState;
                public uint dwStateMask;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
                public uint uTimeout;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
                public uint dwInfoFlags;
                public Guid guidItem;
                public IntPtr hBalloonIcon;
            }

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);
            [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ExtractIconW(IntPtr instance, string file, uint index);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
            [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
            [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
            [DllImport("user32.dll")] static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

            static string Cut(string s, int max)
            {
                s = (s ?? "").Replace('\r', ' ');
                return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
            }

            public static void Show(string title, string text, bool warning)
            {
                var worker = new Thread(() => Balloon(title, text, warning)) { IsBackground = true, Name = "Portly notification" };
                worker.Start();
            }

            static void Balloon(string title, string text, bool warning)
            {
                IntPtr hwnd = CreateWindowExW(0, "STATIC", "Portly notification", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) return;
                IntPtr icon = IntPtr.Zero;
                bool added = false, ownIcon = false;
                var nid = new NOTIFYICONDATA();
                try
                {
                    try { icon = ExtractIconW(IntPtr.Zero, Process.GetCurrentProcess().MainModule.FileName, 0); } catch { }
                    ownIcon = icon.ToInt64() > 1;
                    if (!ownIcon) icon = LoadIconW(IntPtr.Zero, new IntPtr(32512));
                    nid.cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONDATA));
                    nid.hWnd = hwnd;
                    nid.uID = (uint)Interlocked.Increment(ref _id);
                    nid.uFlags = NIF_ICON | NIF_TIP;
                    nid.hIcon = icon;
                    nid.szTip = "Portly";
                    added = Shell_NotifyIconW(NIM_ADD, ref nid);
                    if (!added) return;
                    nid.uFlags = NIF_INFO;
                    nid.szInfoTitle = Cut(title, 63);
                    nid.szInfo = Cut(text, 255);
                    nid.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
                    Shell_NotifyIconW(NIM_MODIFY, ref nid);
                    Thread.Sleep(10000);
                }
                finally
                {
                    if (added) Shell_NotifyIconW(NIM_DELETE, ref nid);
                    DestroyWindow(hwnd);
                    if (ownIcon) DestroyIcon(icon);
                }
            }
        }
    }
}
