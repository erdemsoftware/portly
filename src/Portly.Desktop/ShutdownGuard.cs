using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Desktop
{
    /// <summary>
    /// Clean stop of the services when the operating system ends the session.
    /// Windows: a hidden window of our own (there may be no main window at all while Portly sits in the tray) gets WM_ENDSESSION,
    /// stops the services before it returns, and while services run it holds a shutdown block reason ("Portly is stopping ...").
    /// macOS / Linux: SIGTERM, SIGHUP and SIGINT (logout, "kill", Ctrl+C in the terminal) close Portly the normal way.
    /// </summary>
    static class ShutdownGuard
    {
        const uint WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16, WM_APP_BLOCK = 0x8001;
        const uint WS_POPUP = 0x80000000;

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASS
        {
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASS cls);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);
        [DllImport("user32.dll")] static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

        static WndProcDelegate _proc;
        static IntPtr _hwnd;
        static Action _stop;
        static bool _blocked;
        static readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
        static object _signals;
        static int _signalled;

        /// <summary>stop = stops the services (and shares) and returns when done; it runs on a background thread.</summary>
        public static void Start(Action stop, Action requestExit)
        {
            _stop = stop;
            if (OS.IsWindows) StartWindow();
            else StartSignals(requestExit);
        }

        /// <summary>Windows: services are running, so Windows should wait for Portly (and say why) when it shuts down.</summary>
        public static void SetBlock(bool on)
        {
            if (!OS.IsWindows || _hwnd == IntPtr.Zero || on == _blocked) return;
            _blocked = on;
            PostMessageW(_hwnd, WM_APP_BLOCK, on ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
        }

        // ---------------- Windows ----------------
        static void StartWindow()
        {
            var t = new Thread(WindowThread) { IsBackground = true, Name = "Portly shutdown" };
            t.Start();
            Ready.Wait(3000);
        }

        static void WindowThread()
        {
            try
            {
                _proc = WndProc;
                var cls = new WNDCLASS { lpfnWndProc = _proc, lpszClassName = "PortlyShutdownWindow" };
                if (RegisterClassW(ref cls) == 0) return;
                // a real top-level window that is never shown: message-only windows get no shutdown broadcast
                _hwnd = CreateWindowExW(0x80, cls.lpszClassName, "Portly", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            finally { Ready.Set(); }
            if (_hwnd == IntPtr.Zero) return;
            MSG m;
            while (GetMessageW(out m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }
        }

        static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_QUERYENDSESSION) return new IntPtr(1);
                if (msg == WM_ENDSESSION)
                {
                    if (wParam != IntPtr.Zero)
                    {
                        var stop = _stop;
                        if (stop != null) try { stop(); } catch { }
                        if (_blocked) { _blocked = false; ShutdownBlockReasonDestroy(hWnd); }
                    }
                    return IntPtr.Zero;
                }
                if (msg == WM_APP_BLOCK)
                {
                    if (wParam != IntPtr.Zero) ShutdownBlockReasonCreate(hWnd, I18n.T("main.stopping"));
                    else ShutdownBlockReasonDestroy(hWnd);
                    return IntPtr.Zero;
                }
            }
            catch { }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // ---------------- macOS / Linux ----------------
        static void StartSignals(Action requestExit)
        {
            try
            {
                var regs = new[]
                {
                    PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => OnSignal(c, requestExit)),
                    PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => OnSignal(c, requestExit)),
                    PosixSignalRegistration.Create(PosixSignal.SIGINT, c => OnSignal(c, requestExit)),
                };
                _signals = regs;
            }
            catch (Exception ex) { Log.Warn("Signals: " + ex.Message); }
        }

        static void OnSignal(PosixSignalContext context, Action requestExit)
        {
            // we close by ourselves, the normal way (services first); the default handler would end the process at once
            context.Cancel = true;
            if (Interlocked.Exchange(ref _signalled, 1) == 1) return;
            requestExit();
            // if the window thread is stuck, the signal still has to end Portly
            Task.Run(async () =>
            {
                await Task.Delay(45000);
                var stop = _stop;
                if (stop != null) try { stop(); } catch { }
                Environment.Exit(0);
            });
        }
    }
}
