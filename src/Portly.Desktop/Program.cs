using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
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

        /// <summary>
        /// Portly --screenshot out.png [width height] [--page services|sites|log] [--dialog TypeName]
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
