using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Portly.Config;
using Portly.Core;
using Portly.Desktop;
using Portly.Packages;
using Portly.Services;

namespace Portly.UiTests
{
    /// <summary>
    /// UI flow tests: the real MainWindow runs headless (Skia) and is driven like a user, with real packages and real services.
    /// PORTLY_HOME must point to a scratch folder. Exit code = number of failures. Screenshots go to PORTLY_UI_SHOTS (default ./ui-shots).
    /// </summary>
    static partial class Program
    {
        static int _ok, _fail, _skip;
        static string _flow = "";
        static readonly List<string> Notes = new List<string>();

        static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _ok++; else { _fail++; Shot("FAIL-" + Slug(name)); }
            Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (!ok && detail != "" ? "  -> " + detail : ""));
        }

        static void Skip(string name, string why)
        {
            _skip++;
            Console.WriteLine("  SKIP " + name + "  -> " + why);
        }

        static void Note(string text)
        {
            Console.WriteLine("  note: " + text);
            Notes.Add(text);
        }

        static string Slug(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            return sb.ToString().Trim('-');
        }

        static void Flow(string name, Action body)
        {
            _flow = name;
            Console.WriteLine("== " + name);
            int errorsBefore = UiErrors.Count, logErrorsBefore = LogErrorCount();
            try { body(); }
            catch (Exception ex)
            {
                var e = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                _fail++;
                Console.WriteLine("  FAIL " + name + " threw  -> " + e.GetType().Name + ": " + e.Message);
                Console.WriteLine("       " + (e.StackTrace ?? "").Replace("\n", "\n       "));
                Shot("FAIL-" + Slug(name) + "-exception");
            }
            finally
            {
                try { DismissPopups(); CloseAllDialogs(); } catch { }
            }
            for (int i = errorsBefore; i < UiErrors.Count; i++) { _fail++; Console.WriteLine("  FAIL " + name + ": " + UiErrors[i]); }
            lock (LogErrors)
                for (int i = logErrorsBefore; i < LogErrors.Count; i++) { _fail++; Console.WriteLine("  FAIL " + name + ": error in the Portly log  -> " + LogErrors[i]); }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PORTLY_HOME"))) { Console.WriteLine("Set PORTLY_HOME to a scratch folder."); return 1; }
            Console.WriteLine("Portly UI tests on " + OS.Name + " " + OS.Arch + ", data root " + Paths.Root);
            ShotDir = Path.GetFullPath(Environment.GetEnvironmentVariable("PORTLY_UI_SHOTS") ?? "ui-shots");
            var only = args.Length > 0 ? args : null;

            var started = DateTime.Now;
            try
            {
                if (!Setup()) return 1;
                StartWindow();
                RunFlows(only);
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL runner  -> " + ex);
            }
            finally
            {
                try { ServiceManager.StopAllAsync().GetAwaiter().GetResult(); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("PASSED " + _ok + "  FAILED " + _fail + "  SKIPPED " + _skip + "  (" + (int)(DateTime.Now - started).TotalSeconds + " s)");
            Environment.Exit(_fail);
            return _fail;
        }

        static void RunFlows(string[] only)
        {
            var flows = new List<Tuple<string, Action>>
            {
                Tuple.Create<string, Action>("start", Flow_Start),
                Tuple.Create<string, Action>("services", Flow_Services),
                Tuple.Create<string, Action>("navigation", Flow_Navigation),
                Tuple.Create<string, Action>("projects", Flow_Projects),
                Tuple.Create<string, Action>("dialogs", Flow_Dialogs),
                Tuple.Create<string, Action>("stop", Flow_Stop),
                Tuple.Create<string, Action>("packages", Flow_Packages),
            };
            foreach (var f in flows)
            {
                if (only != null && !only.Contains(f.Item1, StringComparer.OrdinalIgnoreCase) && f.Item1 != "start" && f.Item1 != "stop") continue;
                Flow(f.Item1, f.Item2);
            }
        }

        // ------------------------------------------------------------------ setup (engine only, before the window exists)

        static bool _webReady;
        static string _phpVersion = "";

        static bool Setup()
        {
            Console.WriteLine("== setup");
            Net.Init();
            Paths.EnsureAll();
            Settings.Load();
            var s = Settings.Current;
            s.Language = "en";
            I18n.Load("en");
            Catalog.LoadLocal();

            // Caddy is the web server on macOS / Linux; on Windows ask for it as well so that the same test runs there
            var types = new[] { "php", "caddy", "mysql", "mailpit" };
            foreach (var type in types)
            {
                var e = Catalog.Recommended(type) ?? Catalog.ForType(type).FirstOrDefault();
                if (e == null) { Check("catalog offers " + type, false, OS.Name + " " + OS.Arch); continue; }
                if (PackageTypes.Installed(type).Contains(e.Folder)) { Check("installed " + type, true, "already there"); continue; }
                var t0 = DateTime.Now;
                try
                {
                    Installer.InstallAsync(e, p => { }, CancellationToken.None).GetAwaiter().GetResult();
                    var dir = PackageTypes.ActiveDir(type);
                    Check("installed " + type + " " + e.Version, dir != null, (dir ?? "not detected")); Note(type + " installed in " + (int)(DateTime.Now - t0).TotalSeconds + " s");
                }
                catch (Exception ex) { Check("installed " + type + " " + e.Version, false, ex.GetType().Name + ": " + ex.Message); }
            }

            s.Raw.Set("features", "caddy", true);
            s.Raw.Set("features", "jobs", true);
            s.WebServer = "caddy";
            s.HttpPort = 18080; s.HttpsPort = 18443; s.MysqlPort = 13306; s.MailpitWebPort = 18025; s.MailpitSmtpPort = 11025; s.PhpBasePort = 19100;
            s.Ssl = true;
            s.Tld = "localhost";   // *.localhost needs no hosts file entry: the test never writes the hosts file
            s.ShowWelcome = false;
            s.FirstRunDone = true;
            s.CheckUpdates = false;
            s.CloseToTray = false;
            s.Save();

            var site = Path.Combine(Paths.Www, "e2e");
            Directory.CreateDirectory(site);
            File.WriteAllText(Path.Combine(site, "index.php"), "<?php echo PHP_VERSION;");

            var phpDir = PackageTypes.ActiveDir("php");
            _webReady = phpDir != null && ServiceManager.Web.IsInstalled && ServiceManager.MySql.IsInstalled && ServiceManager.Mailpit.IsInstalled;
            if (phpDir != null)
            {
                var r = ProcessUtil.Run(Path.Combine(phpDir, OS.Exe("php")), "-n -r \"echo PHP_VERSION;\"", phpDir, null, 30000);
                _phpVersion = r.Output.Trim();
            }
            Check("setup: php, " + ServiceManager.Web.Title + ", MySQL and Mailpit are installed", _webReady, "php " + _phpVersion);
            ServiceManager.RefreshSites();
            return true;
        }

        static void StartWindow()
        {
            // the same headless setup as "Portly --screenshot"; the shell skips start-up dialogs and the tray, the tests open those themselves
            var program = typeof(MainWindow).Assembly.GetType("Portly.Desktop.Program");
            program.GetField("IsScreenshot", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, true);
            AppBuilder.Configure<App>().UseSkia().WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            Dispatcher.UIThread.UnhandledException += (s, e) =>
            {
                UiErrors.Add("unhandled UI exception: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
                e.Handled = true;
            };
            Log.Written += line =>
            {
                lock (AllLog) AllLog.Add(line);
                if (line.Contains("ERROR")) lock (LogErrors) LogErrors.Add(line);
            };
            Win = new MainWindow { Width = 1100, Height = 900 };
            Win.Show();
            Pump(300);
            Console.WriteLine("  main window up");
        }

        static readonly List<string> LogErrors = new List<string>();

        // ------------------------------------------------------------------ helpers shared by the flows

        static List<string> LeftoverProcesses()
        {
            var found = new List<string>();
            var bin = Paths.Bin + Path.DirectorySeparatorChar;
            var cmp = OS.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.HasExited) continue;
                    var path = ProcessUtil.ProcessPath(p.Id);
                    if (path != "" && path.StartsWith(bin, cmp)) found.Add(p.ProcessName + " (" + p.Id + ")");
                }
                catch { }
                finally { p.Dispose(); }
            }
            return found;
        }

        static string Fetch(string url, string host, out System.Net.HttpStatusCode code)
        {
            var handler = new System.Net.Http.HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => true };
            using (var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) })
            {
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                if (host != null) req.Headers.Host = host;
                try
                {
                    using (var res = client.SendAsync(req).GetAwaiter().GetResult())
                    {
                        code = res.StatusCode;
                        return res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex) { code = 0; return ex.GetType().Name + ": " + ex.Message; }
            }
        }

        /// <summary>Asks the web server on its test port (host header = the project's address); retries while it settles.</summary>
        static string FetchRetry(string host, Func<string, bool> good, int seconds, string path = "/")
        {
            var until = DateTime.Now.AddSeconds(seconds);
            string body = "";
            System.Net.HttpStatusCode code = 0;
            do
            {
                var r = System.Threading.Tasks.Task.Run(() => { System.Net.HttpStatusCode c; var b = Fetch("http://127.0.0.1:18080" + path, host, out c); return Tuple.Create(c, b); }).GetAwaiter().GetResult();
                code = r.Item1; body = r.Item2;
                if (code == System.Net.HttpStatusCode.OK && good(body)) return body;
                Pump(400);
            } while (DateTime.Now < until);
            return "[" + (int)code + "] " + body;
        }
    }
}
