using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.UI;

namespace Portly
{
    static class Program
    {
        static Mutex _mutex;

        [STAThread]
        static int Main(string[] args)
        {
            // elevated helper mode: only rewrites the Portly block of the hosts file
            if (args.Length >= 2 && args[0] == "--hosts-apply") return HostsFile.ApplyElevated(args[1]);
            if (args.Length >= 2 && args[0] == "--trust-ca") return Certs.TrustCaElevated(args[1]);
            if (args.Length >= 2 && args[0] == "--firefox-policy") return Tools.FirefoxTrust.ApplyPolicyElevated(args[1]);

            int i = Array.IndexOf(args, "--after-update");
            if (i >= 0 && i + 1 < args.Length)
            {
                int pid;
                if (int.TryParse(args[i + 1], out pid))
                {
                    try { using (var old = Process.GetProcessById(pid)) old.WaitForExit(30000); } catch { }
                }
            }

            // one Portly per folder; a second start just shows the first window
            var key = "Portly-" + Fnv(Paths.Root.ToLowerInvariant());
            bool created;
            _mutex = new Mutex(true, @"Local\" + key, out created);
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + "-show");
            if (!created) { signal.Set(); return 0; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);

            Net.Init();
            // be asked early at Windows shutdown (before most apps) so databases get time to close cleanly
            try { SetProcessShutdownParameters(0x3FF, 0); } catch { }
            Paths.EnsureAll();
            Settings.Load();
            I18n.Load(Settings.Current.Language);
            Theme.Load(Settings.Current.Theme);
            Updater.CleanupOldFiles();
            Catalog.LoadLocal();

            bool autostart = args.Contains("--autostart");
            bool hidden = args.Contains("--tray") || (autostart && Settings.Current.StartMinimized);
            bool startServices = args.Contains("--start-services");

            var form = new MainForm(hidden, startServices);
            var listener = new Thread(() =>
            {
                while (true)
                {
                    signal.WaitOne();
                    form.BringToFrontFromOtherInstance();
                }
            }) { IsBackground = true };
            listener.Start();

            Application.Run(form);
            GC.KeepAlive(_mutex);
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetProcessShutdownParameters(uint level, uint flags);

        static void Crash(Exception ex)
        {
            if (ex == null) return;
            Log.Error("Unexpected error", ex);
            try { MessageBox.Show(I18n.T("err.unexpected") + "\n\n" + ex.Message, "Portly", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        }

        static string Fnv(string s)
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }
}
