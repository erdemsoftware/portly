using System;
using System.Runtime.InteropServices;
using System.Threading;
using Portly.Core;
using Portly.Desktop;

namespace Portly.CoreTests
{
    /// <summary>Desktop app plumbing that needs no window: start-up routing, notification text, session-end handling (Windows).</summary>
    static partial class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        /// <summary>This process's window of that class: a Portly running on the same PC has one too, and must not get the test's messages.</summary>
        static IntPtr OwnWindow(string cls)
        {
            var h = IntPtr.Zero;
            while ((h = FindWindowExW(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == (uint)Environment.ProcessId) return h;
            }
            return IntPtr.Zero;
        }
        [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static void Suite_Notify()
        {
            Run("notify start-up routing", NotifyRouting);
            Run("notify osascript text", NotifyAppleScript);
            Run("notify linux arguments", NotifyLinux);
            Run("notify sink", NotifySink);
            if (OS.IsWindows) Run("notify session end", NotifySessionEnd);
        }

        static void NotifyRouting()
        {
            Check("hosts helper", StartupRoute.Helper(new[] { "--hosts-apply", "f" }, false) == "hosts-apply");
            Check("hosts helper needs its file", StartupRoute.Helper(new[] { "--hosts-apply" }, true) == null);
            Check("service on Windows", StartupRoute.Helper(new[] { "--service" }, true) == "service");
            Check("service not on other systems", StartupRoute.Helper(new[] { "--service" }, false) == null);
            Check("install service", StartupRoute.Helper(new[] { "--install-service", "S-1-5-21" }, true) == "install-service");
            Check("install service needs the sid", StartupRoute.Helper(new[] { "--install-service" }, true) == null);
            Check("uninstall service", StartupRoute.Helper(new[] { "--uninstall-service" }, true) == "uninstall-service");
            Check("firefox policy", StartupRoute.Helper(new[] { "--firefox-policy", "C:/Firefox" }, true) == "firefox-policy");
            Check("firefox policy not on other systems", StartupRoute.Helper(new[] { "--firefox-policy", "/x" }, false) == null);
            Check("normal start", StartupRoute.Helper(new[] { "--tray" }, true) == null && StartupRoute.Helper(new string[0], true) == null);
            Check("helper only as first argument", StartupRoute.Helper(new[] { "--tray", "--service" }, true) == null);
        }

        // a bare double quote or a lone backslash would end the literal: it must never appear unescaped
        static bool IsSafeLiteral(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\') { if (i + 1 >= s.Length || (s[i + 1] != '\\' && s[i + 1] != '"')) return false; i++; }
                else if (s[i] == '"') return false;
            }
            return true;
        }

        static void NotifyAppleScript()
        {
            Check("plain text unchanged", Notify.AppleScriptString("PHP 8.5 is ready") == "PHP 8.5 is ready");
            Check("quote escaped", Notify.AppleScriptString("say \"hi\"") == "say \\\"hi\\\"");
            Check("backslash escaped", Notify.AppleScriptString("C:\\x") == "C:\\\\x");
            Check("line breaks become spaces", Notify.AppleScriptString("a\r\nb\tc") == "a  b c");
            var evil = "\" & (do shell script \"touch /tmp/x\") & \"\\\"";
            var lit = Notify.AppleScriptString(evil);
            Check("injection stays inside the literal", IsSafeLiteral(lit), lit);
            var script = Notify.MacScript("Port \"80\"", "line1\nline2 \\");
            Check("script shape", script == "display notification \"line1 line2 \\\\\" with title \"Port \\\"80\\\"\"", script);
            Check("long text is cut", Notify.AppleScriptString(new string('a', 2000)).Length == 500);
        }

        static void NotifyLinux()
        {
            var a = Notify.LinuxArgs("Portly", "-rf not an option", true);
            int dash = Array.IndexOf(a, "--");
            Check("text after --", dash > 0 && a[dash + 1] == "Portly" && a[dash + 2] == "-rf not an option");
            Check("warning icon", Array.IndexOf(a, "dialog-warning") > 0 && Array.IndexOf(Notify.LinuxArgs("t", "x", false), "dialog-information") > 0);
        }

        static void NotifySink()
        {
            string t = null, x = null; bool w = false; int n = 0;
            Notify.Sink = (title, text, warning) => { t = title; x = text; w = warning; n++; };
            try
            {
                Notify.Show("Portly", "hello", true);
                Check("sink gets the call", n == 1 && t == "Portly" && x == "hello" && w);
                Notify.Show(null, "no title");
                Check("default title", t == "Portly" && !w);
                Notify.Show("Portly", "  ");
                Check("empty text is skipped", n == 2);
            }
            finally { Notify.Sink = null; }
        }

        // the hidden window of ShutdownGuard answers WM_QUERYENDSESSION with TRUE and stops the services on WM_ENDSESSION
        static void NotifySessionEnd()
        {
            int stopped = 0;
            ShutdownGuard.Start(() => Interlocked.Increment(ref stopped), () => { });
            var hwnd = OwnWindow("PortlyShutdownWindow");
            Check("hidden shutdown window exists", hwnd != IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;
            Check("query end session is allowed", SendMessageW(hwnd, 0x11, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero);
            Check("nothing stopped by the query", stopped == 0);
            SendMessageW(hwnd, 0x16, IntPtr.Zero, IntPtr.Zero); // session end cancelled
            Check("nothing stopped when the end is cancelled", stopped == 0);
            ShutdownGuard.SetBlock(true);
            ShutdownGuard.SetBlock(false);
            SendMessageW(hwnd, 0x16, new IntPtr(1), IntPtr.Zero);
            Check("services stopped at session end", stopped == 1);
        }
    }
}
