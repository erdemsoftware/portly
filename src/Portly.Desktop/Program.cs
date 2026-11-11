using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Portly.Config;
using Portly.Core;
using Portly.Packages;

namespace Portly.Desktop
{
    static class Program
    {
        static Mutex _mutex;

        /// <summary>--screenshot: the window is only rendered; no start-up work, tray, watchers or services.</summary>
        internal static bool IsScreenshot;
        /// <summary>--tray, or --autostart with "start minimized": the window stays hidden.</summary>
        internal static bool StartHidden;
        /// <summary>--start-services (also passed by the updater after a restart).</summary>
        internal static bool StartServices;

        [STAThread]
        static int Main(string[] args)
        {
            // elevated helper mode: only rewrites the Portly block of the hosts file / trusts the CA
            if (args.Length >= 2 && args[0] == "--hosts-apply") return HostsFile.ApplyElevated(args[1]);
            if (args.Length >= 2 && args[0] == "--hosts-write") return HostsFile.WriteElevated(args[1]);
            if (args.Length >= 2 && args[0] == "--trust-ca") return Certs.TrustCaElevated(args[1]);
            if (args.Length >= 2 && args[0] == "--untrust-ca") return Certs.UntrustCaElevated(args[1]);

            // --screenshot <file.png> [width] [height]: render the main window without showing it
            int i = Array.IndexOf(args, "--screenshot");
            if (i >= 0 && i + 1 < args.Length)
            {
                IsScreenshot = true;
                Init();
                return Screenshot(args, i);
            }

            // "portly status", "portly start" ...: print into the calling console, talk to the running Portly (Windows only)
            Tools.Cli.InstanceKey = "Portly-" + Fnv(Paths.Root);
            if (Tools.Cli.IsCommand(args)) return Tools.Cli.RunClient(args);

            // the updater starts the new Portly while the old one is still closing
            i = Array.IndexOf(args, "--after-update");
            if (i >= 0 && i + 1 < args.Length)
            {
                int pid;
                if (int.TryParse(args[i + 1], out pid))
                {
                    try { using (var old = Process.GetProcessById(pid)) old.WaitForExit(30000); } catch { }
                }
            }

            // one Portly per data folder; a second start just shows the first window
            bool created;
            _mutex = new Mutex(true, "Portly-" + Fnv(Paths.Root), out created);
            if (!created) { SignalRunningInstance(); return 0; }
            StartShowListener();

            AppDomain.CurrentDomain.UnhandledException += (s, e) => { var ex = e.ExceptionObject as Exception; if (ex != null) Log.Error("Unexpected error", ex); };
            // be asked early at Windows shutdown (before most apps) so databases get time to close cleanly
            if (OS.IsWindows) { try { SetProcessShutdownParameters(0x3FF, 0); } catch { } }

            // processes left by a crashed Portly; safe only while we hold the folder lock
            Children.CloseAll();
            Init();
            Settings.RootPasswordChanged += ToolApps.RefreshPhpMyAdmin;
            Updater.CleanupOldFiles();

            bool autostart = args.Contains("--autostart");
            StartHidden = args.Contains("--tray") || (autostart && Settings.Current.StartMinimized);
            StartServices = args.Contains("--start-services");

            Tools.Cli.StartServer();
            if (Features.IsOn("cli"))
            {
                if (OS.IsWindows) Tools.Cli.EnsureCmdFile(Paths.Root);
                else Tools.Cli.EnsureShellFile(Paths.Root);
            }

            var code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            GC.KeepAlive(_mutex);
            return code;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetProcessShutdownParameters(uint level, uint flags);

        static string Fnv(string s)
        {
            if (OS.IsWindows) s = s.ToLowerInvariant();
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }

        // ---------------- single instance: the second Portly asks the first one to show its window ----------------
        // a named pipe (a Unix socket on macOS and Linux) named from the same hash as the lock
        static string ShowPipeName { get { return "Portly-" + Fnv(Paths.Root) + "-show"; } }

        static void SignalRunningInstance()
        {
            // the first Portly may still be starting: try for a few seconds
            for (int n = 0; n < 10; n++)
            {
                try
                {
                    using (var c = new NamedPipeClientStream(".", ShowPipeName, PipeDirection.Out))
                    {
                        c.Connect(500);
                        c.WriteByte(1);
                        c.Flush();
                        return;
                    }
                }
                catch { Thread.Sleep(300); }
            }
        }

        static void StartShowListener()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(ShowPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                        {
                            await s.WaitForConnectionAsync();
                            s.ReadByte();
                        }
                        var main = MainWindow.Instance;
                        if (main != null) main.BringToFrontFromOtherInstance();
                    }
                    catch { await Task.Delay(1000); }
                }
            });
        }

        static void Init()
        {
            Net.Init();
            Paths.EnsureAll();
            Settings.Load();
            I18n.Load(Settings.Current.Language);
            Catalog.LoadLocal();
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
        }

        /// <summary>
        /// Portly --screenshot out.png [width height] [--page services|sites|log] [--menu] [--update] [--dialog TypeName]
        /// --dialog renders TypeName.Preview() (a public static method returning a Window) instead of the main window.
        /// </summary>
        static int Screenshot(string[] args, int i)
        {
            var file = Path.GetFullPath(args[i + 1]);
            int w, h;
            if (!(args.Length > i + 2 && int.TryParse(args[i + 2], out w))) w = 1100;
            if (!(args.Length > i + 3 && int.TryParse(args[i + 3], out h))) h = 740;
            int p = Array.IndexOf(args, "--page");
            int d = Array.IndexOf(args, "--dialog");
            AppBuilder.Configure<App>().UseSkia().WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            var main = new MainWindow { Width = w, Height = h };
            main.Show();
            if (p >= 0 && p + 1 < args.Length) main.ShowPage(args[p + 1]);
            if (p >= 0 && p + 1 < args.Length && args[p + 1] == "log")
            {
                Log.Info("Portly 2.0.0 (Windows 11)");
                Log.Info("Apache 2.4.69 started on 80, 443");
                Log.Warn("Port 3306 is used by another program");
                Log.Error("MySQL: could not start");
            }
            if (args.Contains("--menu")) main.PreviewMenu();
            if (args.Contains("--update")) { Updater.Available = new AppUpdate { Version = new Version(2, 1, 0) }; main.UpdateBanner(); }
            Window win = main;
            if (d >= 0 && d + 1 < args.Length)
            {
                var type = typeof(Program).Assembly.GetTypes().FirstOrDefault(t => t.Name == args[d + 1]);
                var preview = type == null ? null : type.GetMethod("Preview", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                if (preview == null) { Console.Error.WriteLine("no public static Preview() on " + args[d + 1]); return 1; }
                win = (Window)preview.Invoke(null, null);
                win.Show(main);
            }
            for (int n = 0; n < 8; n++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            var frame = win.CaptureRenderedFrame();
            if (frame == null) { Console.Error.WriteLine("no frame"); return 1; }
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            frame.Save(file, new PngBitmapEncoderOptions());
            Console.WriteLine(file);
            return 0;
        }
    }
}
