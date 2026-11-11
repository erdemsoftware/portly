using System;
using System.IO;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Portly.Core;
using Portly.Packages;

namespace Portly.Desktop
{
    static class Program
    {
        static System.Threading.Mutex _mutex;

        [STAThread]
        static int Main(string[] args)
        {
            // --screenshot <file.png> [width] [height]: render the main window without showing it
            int i = Array.IndexOf(args, "--screenshot");
            if (i >= 0 && i + 1 < args.Length)
            {
                Init();
                return Screenshot(args, i);
            }

            // one Portly per data folder
            bool created;
            _mutex = new System.Threading.Mutex(true, "Portly-" + Fnv(Paths.Root), out created);
            if (!created) return 0;

            // processes left by a crashed Portly; safe only while we hold the folder lock
            Children.CloseAll();
            Init();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            GC.KeepAlive(_mutex);
            return 0;
        }

        static string Fnv(string s)
        {
            if (OS.IsWindows) s = s.ToLowerInvariant();
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
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

        static int Screenshot(string[] args, int i)
        {
            var file = Path.GetFullPath(args[i + 1]);
            int w = args.Length > i + 2 ? int.Parse(args[i + 2]) : 1100;
            int h = args.Length > i + 3 ? int.Parse(args[i + 3]) : 720;
            AppBuilder.Configure<App>().UseSkia().WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            var win = new MainWindow { Width = w, Height = h };
            win.Show();
            for (int n = 0; n < 5; n++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            var frame = win.CaptureRenderedFrame();
            if (frame == null) { Console.Error.WriteLine("no frame"); return 1; }
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            frame.Save(file);
            Console.WriteLine(file);
            return 0;
        }
    }
}
