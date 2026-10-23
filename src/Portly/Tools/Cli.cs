using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    public class CliResult
    {
        public int Code;
        public string Output = "";
        public static CliResult Ok(string text) { return new CliResult { Code = 0, Output = text }; }
        public static CliResult Fail(string text) { return new CliResult { Code = 1, Output = text }; }
    }

    /// <summary>
    /// Feature "cli": the "portly" command. Portly.exe is a GUI program, so the command side attaches to the
    /// parent console to print, and sends the command to the running Portly over a named pipe (one per Portly
    /// folder, like the single-instance mutex). When Portly is not running it is started in the tray first.
    /// portly.cmd in the Portly folder makes cmd / PowerShell wait for the answer and pass the exit code on.
    /// </summary>
    public static class Cli
    {
        public static readonly string[] Commands = { "status", "start", "stop", "restart", "sites", "open", "share", "use", "db", "version", "help" };

        /// <summary>Set by Program: "Portly-&lt;hash of the folder&gt;".</summary>
        public static string InstanceKey = "Portly";
        public static string PipeName { get { return InstanceKey + "-cli"; } }

        public static bool IsCommand(string[] args)
        {
            return args.Length > 0 && Commands.Contains(args[0].ToLowerInvariant());
        }

        // =====================================================================
        // Client (the "portly ..." process)
        // =====================================================================
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll")] static extern int GetFileType(IntPtr h);

        static TextWriter _out;

        static void PrepareConsole()
        {
            // output redirected to a file / pipe: use it as it is; otherwise print into the console we were started from
            var h = GetStdHandle(-11);
            bool redirected = h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) != 0 && GetFileType(h) != 2;
            if (!redirected) AttachConsole(-1);
            try
            {
                var stream = Console.OpenStandardOutput();
                _out = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            }
            catch { _out = TextWriter.Null; }
        }

        static void Print(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _out.Write(text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            if (!text.EndsWith("\n")) _out.WriteLine();
        }

        public static int RunClient(string[] args)
        {
            PrepareConsole();
            try
            {
                Paths.EnsureAll();
                Settings.Load();
                I18n.Load(Settings.Current.Language);
                var verb = args[0].ToLowerInvariant();
                if (verb == "help") { Print(Help()); return 0; }
                if (verb == "version") { Print("Portly " + AppInfo.VersionText + " (" + AppInfo.EditionName + ")"); return 0; }
                if (!Features.IsOn("cli")) { Print(I18n.T("cli.off")); return 1; }

                var pipe = Connect(1500);
                if (pipe == null)
                {
                    bool guiRunning = GuiRunning();
                    if (!guiRunning && verb == "stop") { Print(I18n.T("cli.notRunning")); return 0; }
                    if (!guiRunning)
                    {
                        Print(I18n.T("cli.starting"));
                        Process.Start(new ProcessStartInfo(Paths.Exe, "--tray") { UseShellExecute = false, WorkingDirectory = Paths.Root });
                    }
                    pipe = Connect(guiRunning ? 10000 : 45000);
                    if (pipe == null) { Print(I18n.T("cli.noAnswer")); return 1; }
                }
                using (pipe)
                {
                    var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                    w.WriteLine(args.Length);
                    foreach (var a in args) w.WriteLine(a.Replace("\r", " ").Replace("\n", " "));
                    var r = new StreamReader(pipe, Encoding.UTF8);
                    var first = r.ReadLine();
                    int code;
                    if (first == null || !int.TryParse(first, out code)) { Print(I18n.T("cli.noAnswer")); return 1; }
                    Print(r.ReadToEnd());
                    return code;
                }
            }
            catch (Exception ex) { Print(ex.Message); return 1; }
            finally { try { _out.Flush(); } catch { } }
        }

        static NamedPipeClientStream Connect(int timeoutMs)
        {
            var until = DateTime.Now.AddMilliseconds(timeoutMs);
            while (true)
            {
                var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                try
                {
                    c.Connect(Math.Max(100, Math.Min(1000, (int)(until - DateTime.Now).TotalMilliseconds)));
                    return c;
                }
                catch (TimeoutException) { c.Dispose(); }
                catch (IOException) { c.Dispose(); Thread.Sleep(200); }
                if (DateTime.Now >= until) return null;
            }
        }

        static bool GuiRunning()
        {
            Mutex m;
            if (Mutex.TryOpenExisting(@"Local\" + InstanceKey, out m)) { m.Dispose(); return true; }
            return false;
        }

        public static string Help()
        {
            return "Portly " + AppInfo.VersionText + "\n\n" + I18n.T("cli.help") + "\n\n" +
                "  portly status\n" +
                "  portly start [service]        (web, php, mysql, redis, mailpit, postgresql, mongodb, memcached)\n" +
                "  portly stop [service]\n" +
                "  portly restart [service]\n" +
                "  portly sites\n" +
                "  portly open <site>\n" +
                "  portly share <site> --yes\n" +
                "  portly use php <version> [--site <name>]\n" +
                "  portly db backup\n" +
                "  portly version\n" +
                "  portly help\n";
        }

        /// <summary>portly.cmd next to Portly.exe (or in another folder, e.g. a shims folder on PATH).</summary>
        public static void EnsureCmdFile(string dir)
        {
            try
            {
                var exe = Paths.Exe;
                string rel = exe.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? "%~dp0" + exe.Substring(dir.TrimEnd('\\').Length + 1) : RelativeFrom(dir, exe);
                // a batch file waits for the GUI exe to finish, so the prompt returns after the answer is printed
                var text = "@echo off\r\nrem Portly command line - see \"portly help\"\r\n\"" + rel + "\" %*\r\nexit /b %errorlevel%\r\n";
                var file = Path.Combine(dir, "portly.cmd");
                if (File.Exists(file) && File.ReadAllText(file) == text) return;
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, text, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("portly.cmd: " + ex.Message); }
        }

        static string RelativeFrom(string dir, string file)
        {
            var from = new Uri(dir.TrimEnd('\\') + "\\");
            var to = new Uri(file);
            if (!string.Equals(from.GetLeftPart(UriPartial.Authority) + Path.GetPathRoot(dir), to.GetLeftPart(UriPartial.Authority) + Path.GetPathRoot(file), StringComparison.OrdinalIgnoreCase)) return file;
            return "%~dp0" + Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString()).Replace('/', '\\');
        }

        // =====================================================================
        // Server (inside the running Portly)
        // =====================================================================
        static Thread _server;

        public static void StartServer()
        {
            if (_server != null || !Features.IsOn("cli")) return;
            _server = new Thread(ServerLoop) { IsBackground = true, Name = "Portly CLI" };
            _server.Start();
        }

        static PipeSecurity Security()
        {
            // only this Windows user, and never over the network
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return ps;
        }

        static void ServerLoop()
        {
            while (true)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security());
                    server.WaitForConnection();
                    var s = server;
                    server = null;
                    Task.Run(() => Serve(s));
                }
                catch (Exception ex)
                {
                    Log.Warn("CLI: " + ex.Message);
                    if (server != null) server.Dispose();
                    Thread.Sleep(2000);
                }
            }
        }

        static async Task Serve(NamedPipeServerStream s)
        {
            using (s)
            {
                try
                {
                    var r = new StreamReader(s, Encoding.UTF8);
                    int n;
                    if (!int.TryParse(r.ReadLine(), out n) || n < 1 || n > 50) return;
                    var args = new string[n];
                    for (int i = 0; i < n; i++) args[i] = r.ReadLine() ?? "";
                    var result = await Handle(args).ConfigureAwait(false);
                    var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true };
                    w.WriteLine(result.Code);
                    w.Write(result.Output ?? "");
                    w.Flush();
                    s.WaitForPipeDrain();
                }
                catch (Exception ex) { Log.Warn("CLI: " + ex.Message); }
            }
        }

        // =====================================================================
        // Commands (also called directly by the tests)
        // =====================================================================
        public static async Task<CliResult> Handle(string[] args)
        {
            if (args.Length == 0) return CliResult.Ok(Help());
            var verb = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToList();
            try
            {
                switch (verb)
                {
                    case "help": return CliResult.Ok(Help());
                    case "version": return CliResult.Ok("Portly " + AppInfo.VersionText + " (" + AppInfo.EditionName + ")");
                    case "status": return CliResult.Ok(Status());
                    case "sites": return CliResult.Ok(SitesText());
                    case "start": return await Start(rest.FirstOrDefault()).ConfigureAwait(false);
                    case "stop": return await Stop(rest.FirstOrDefault()).ConfigureAwait(false);
                    case "restart":
                        {
                            var stop = await Stop(rest.FirstOrDefault()).ConfigureAwait(false);
                            if (stop.Code != 0) return stop;
                            return await Start(rest.FirstOrDefault()).ConfigureAwait(false);
                        }
                    case "open":
                        {
                            var site = FindSite(rest.FirstOrDefault());
                            if (site == null) return CliResult.Fail(I18n.T("cli.noSite", rest.FirstOrDefault() ?? ""));
                            if (!site.Valid) return CliResult.Fail(site.Problem);
                            WinUtil.Open(site.PreferredUrl);
                            return CliResult.Ok(site.PreferredUrl);
                        }
                    case "share": return await ShareSite(rest).ConfigureAwait(false);
                    case "use": return await Use(rest).ConfigureAwait(false);
                    case "db":
                        {
                            if (rest.FirstOrDefault() != "backup") return CliResult.Fail(Help());
                            var file = await Task.Run(() => Database.BackupAll()).ConfigureAwait(false);
                            return CliResult.Ok(I18n.T("db.backedUp", file));
                        }
                    default: return CliResult.Fail(I18n.T("cli.unknown", verb) + "\n\n" + Help());
                }
            }
            catch (Exception ex) { return CliResult.Fail(ex.Message); }
        }

        static string Status()
        {
            var sb = new StringBuilder();
            foreach (var svc in ServiceManager.Rows)
            {
                if (!svc.IsInstalled) continue;
                var state = svc.State.ToString().ToLowerInvariant();
                sb.Append(Pad(svc.Title, 12)).Append(Pad(svc.VersionLabel, 10)).Append(Pad(state, 10));
                if (svc.State == ServiceState.Running && svc.PortText != "") sb.Append(I18n.T("cli.port", svc.PortText));
                if (svc.State == ServiceState.Error && !string.IsNullOrEmpty(svc.LastError)) sb.Append(svc.LastError.Split('\n')[0]);
                sb.Append('\n');
            }
            if (sb.Length == 0) sb.Append(I18n.T("cli.nothingInstalled")).Append('\n');
            var jobs = Jobs.Active();
            if (jobs.Count > 0)
            {
                sb.Append('\n').Append(I18n.T("cli.jobs")).Append('\n');
                foreach (var j in jobs) sb.Append("  ").Append(Pad(j.Site + " / " + j.Def.Name, 30)).Append(j.State.ToString().ToLowerInvariant()).Append('\n');
            }
            return sb.ToString();
        }

        static string SitesText()
        {
            var sites = ServiceManager.CurrentSites.Count > 0 ? ServiceManager.CurrentSites : Sites.Scan();
            if (sites.Count == 0) return I18n.T("sites.none");
            var sb = new StringBuilder();
            foreach (var s in sites)
            {
                sb.Append(Pad(s.Name, 22));
                if (s.Valid) sb.Append(Pad(s.PreferredUrl, 36)).Append(Pad("PHP " + PackageTypes.Label(s.PhpFolder), 12));
                else sb.Append(Pad("(" + s.Problem + ")", 48));
                sb.Append(s.Folder).Append('\n');
            }
            return sb.ToString();
        }

        static string Pad(string s, int n) { s = s ?? ""; return s.Length >= n ? s + " " : s.PadRight(n); }

        static Site FindSite(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var sites = ServiceManager.CurrentSites.Count > 0 ? ServiceManager.CurrentSites : Sites.Scan();
            name = name.Trim().ToLowerInvariant().TrimEnd('/');
            return sites.FirstOrDefault(s => s.Name.ToLowerInvariant() == name)
                ?? sites.FirstOrDefault(s => s.Host == name)
                ?? sites.FirstOrDefault(s => s.ShortName.ToLowerInvariant() == name);
        }

        static Service FindService(string name)
        {
            if (name == null) return null;
            name = name.ToLowerInvariant();
            if (name == "apache" || name == "nginx") name = "web";
            if (name == "mariadb" || name == "db") name = "mysql";
            if (name == "postgres" || name == "pgsql") name = "postgresql";
            if (name == "mongo") name = "mongodb";
            if (name == "mail" || name == "mailhog" || name == "smtp4dev") name = "mailpit";
            return ServiceManager.Rows.FirstOrDefault(s => s.Id == name);
        }

        static async Task<CliResult> Start(string name)
        {
            if (name == null)
            {
                await ServiceManager.StartAllAsync().ConfigureAwait(false);
                return Summary();
            }
            var svc = FindService(name);
            if (svc == null) return CliResult.Fail(I18n.T("cli.noService", name));
            if (!svc.IsInstalled) return CliResult.Fail(I18n.T("err.notInstalled", svc.Title));
            await ServiceManager.StartAsync(svc).ConfigureAwait(false);
            return svc.State == ServiceState.Running ? CliResult.Ok(Status()) : CliResult.Fail(svc.Title + ": " + (svc.LastError ?? svc.State.ToString()));
        }

        static async Task<CliResult> Stop(string name)
        {
            if (name == null)
            {
                await ServiceManager.StopAllAsync().ConfigureAwait(false);
                return CliResult.Ok(I18n.T("main.allStopped"));
            }
            var svc = FindService(name);
            if (svc == null) return CliResult.Fail(I18n.T("cli.noService", name));
            await ServiceManager.StopAsync(svc).ConfigureAwait(false);
            return CliResult.Ok(Status());
        }

        /// <summary>Exit code 1 when an enabled, installed service did not start.</summary>
        static CliResult Summary()
        {
            var s = Settings.Current;
            bool failed = ServiceManager.Rows.Any(x => x.Id != "php" && x.IsInstalled && s.IsEnabled(x.Id) && x.State != ServiceState.Running);
            return new CliResult { Code = failed ? 1 : 0, Output = Status() };
        }

        static async Task<CliResult> ShareSite(List<string> rest)
        {
            var site = FindSite(rest.FirstOrDefault(a => !a.StartsWith("-")));
            if (site == null) return CliResult.Fail(I18n.T("cli.noSite", rest.FirstOrDefault() ?? ""));
            if (!site.Valid) return CliResult.Fail(site.Problem);
            var existing = Share.UrlOf(site);
            if (existing != null) return CliResult.Ok(existing);
            // the site becomes public on the internet: say so and require an explicit --yes
            if (!rest.Contains("--yes") && !rest.Contains("-y"))
                return CliResult.Fail(I18n.T("share.confirm", site.Host) + "\n\n" + I18n.T("cli.shareYes", site.Name));
            if (ServiceManager.Web.State != ServiceState.Running) return CliResult.Fail(I18n.T("main.webNotRunning"));
            var url = await Share.StartAsync(site, t => { }).ConfigureAwait(false);
            return CliResult.Ok(url);
        }

        static async Task<CliResult> Use(List<string> rest)
        {
            if (rest.Count < 2 || rest[0].ToLowerInvariant() != "php") return CliResult.Fail(Help());
            var version = rest[1];
            string siteName = null;
            int i = rest.IndexOf("--site");
            if (i >= 0)
            {
                if (i + 1 >= rest.Count) return CliResult.Fail(Help());
                siteName = rest[i + 1];
            }
            var folder = ProjectFile.PickInstalledPhp(version);
            if (folder == null)
                return CliResult.Fail(I18n.T("cli.phpMissing", version, string.Join(", ", PackageTypes.Installed("php").Select(PackageTypes.Label))));
            if (siteName != null)
            {
                var site = FindSite(siteName);
                if (site == null) return CliResult.Fail(I18n.T("cli.noSite", siteName));
                Sites.SetPhp(site.Name, folder);
                Log.Info(I18n.T("log.sitePhp", site.Name, PackageTypes.Label(folder)));
                await ServiceManager.ReloadWebAsync().ConfigureAwait(false);
                return CliResult.Ok(I18n.T("log.sitePhp", site.Name, PackageTypes.Label(folder)));
            }
            Settings.Current.SetVersion("php", folder);
            Settings.Current.Save();
            Terminal.ApplyUserPath();
            await ServiceManager.ReloadWebAsync().ConfigureAwait(false);
            return CliResult.Ok(I18n.T("cli.phpDefault", PackageTypes.Label(folder)));
        }
    }
}
