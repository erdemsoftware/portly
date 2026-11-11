using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;

namespace Portly.Services
{
    /// <summary>Caddy as the web server (feature "caddy"). Same role as Apache / Nginx: one process for all sites.</summary>
    public class CaddyService : Service
    {
        Process _p;
        public override string Id { get { return "web"; } }
        public override string Title { get { return "Caddy"; } }
        public override string PackageType { get { return "caddy"; } }
        public override string PortText
        {
            get { var s = Settings.Current; return s.HttpPort + (s.Ssl ? ", " + s.HttpsPort : ""); }
        }

        public List<Site> Sites = new List<Site>();
        public Func<string, int> PhpPort = f => 9100;

        public override async Task StartAsync()
        {
            var dir = PackageTypes.ActiveDir("caddy");
            if (dir == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", "Caddy")); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var s = Settings.Current;
            var exe = Path.Combine(dir, "caddy.exe");
            string error = await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(CaddyConfig.Dir);
                    CaddyConfig.WriteCaddyfile(Sites, PhpPort);
                    return CaddyConfig.Adapt(exe);
                }
                catch (Exception ex) { Log.Error("Caddy config", ex); return ex.Message; }
            });
            if (error != null) { SetState(ServiceState.Error, error); return; }
            if (!EnsurePortFree(s.HttpPort)) return;
            if (s.Ssl && !EnsurePortFree(s.HttpsPort)) return;

            // Caddy keeps its data under %APPDATA% unless told otherwise: keep everything in the Portly folder
            var env = new Dictionary<string, string> { { "XDG_DATA_HOME", Path.Combine(Paths.Tmp, "caddy") }, { "XDG_CONFIG_HOME", Path.Combine(Paths.Tmp, "caddy") } };
            _p = Launch(exe, "run --config \"" + CaddyConfig.JsonFile + "\"", dir, env, "caddy-console", OnExit);
            if (await WaitForPort(s.HttpPort, _p, 15000))
            {
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", "Caddy " + VersionLabel));
            }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "caddy-console.log") : I18n.T("err.timeout"));
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "caddy-console.log"));
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() => StopProcess(p, true, 5000));
            _p = null;
            SetState(ServiceState.Stopped);
            Log.Info(I18n.T("log.stopped", "Caddy"));
        }
    }

    /// <summary>
    /// FrankenPHP runtime (feature "frankenphp"): every site set to FrankenPHP runs as its own frankenphp.exe on a
    /// free local port; Apache / Nginx / Caddy forward the site's names to it, so HTTPS, hosts and aliases stay the same.
    /// php.ini: the FrankenPHP folder gets its own php.ini through PhpConfig (upload_progress off etc.), found via PHPRC.
    /// Not a row on the main window: it starts and stops together with the web server.
    /// </summary>
    public class FrankenPhpService : Service
    {
        public override string Id { get { return "frankenphp"; } }
        public override string Title { get { return "FrankenPHP"; } }
        public override string PackageType { get { return "frankenphp"; } }

        readonly Dictionary<string, Process> _procs = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> _ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public override string PortText { get { lock (Sync) return string.Join(", ", _ports.Values); } }

        static string ConfDir { get { return Path.Combine(Paths.Etc, "frankenphp"); } }

        /// <summary>Port of the site's FrankenPHP server, 0 when the site is not (or no longer) running on it.</summary>
        public int PortOf(Site site)
        {
            lock (Sync)
            {
                Process p; int port;
                if (_procs.TryGetValue(site.Name, out p) && !p.HasExited && _ports.TryGetValue(site.Name, out port)) return port;
            }
            return 0;
        }

        public override Task StartAsync() { return StartAsync(ServiceManager.CurrentSites); }

        public async Task StartAsync(List<Site> sites)
        {
            var wanted = sites.Where(x => x.Valid && x.Runtime != "").ToList();
            if (wanted.Count == 0) return;
            var dir = PackageTypes.ActiveDir("frankenphp");
            if (dir == null) { Log.Error(I18n.T("franken.notInstalled", string.Join(", ", wanted.Select(x => x.Name)))); return; }
            StopRequested = false;
            try { PhpConfig.Ensure(dir); } catch (Exception ex) { Log.Warn("FrankenPHP php.ini: " + ex.Message); }
            foreach (var site in wanted)
            {
                if (PortOf(site) > 0) continue;
                var err = await StartOne(dir, site);
                // the site falls back to classic PHP so it still opens
                if (err != null) Log.Error("FrankenPHP " + site.Name + ": " + err);
            }
            SetState(ServiceState.Running);
        }

        /// <summary>The worker script Laravel Octane publishes ("php artisan octane:install --server=frankenphp").</summary>
        public static string WorkerFile(Site site) { return Path.Combine(site.DocRoot, "frankenphp-worker.php"); }

        public static bool HasOctane(Site site)
        {
            try
            {
                var f = Path.Combine(site.Folder, "composer.json");
                return File.Exists(f) && File.ReadAllText(f).Contains("\"laravel/octane\"");
            }
            catch { return false; }
        }

        async Task<string> StartOne(string dir, Site site)
        {
            bool octane = site.Runtime == "octane";
            if (octane && (!HasOctane(site) || !File.Exists(WorkerFile(site)))) return I18n.T("franken.octaneMissing");
            int port;
            lock (Sync)
            {
                var taken = new HashSet<int>(_ports.Values);
                var b = Settings.Current.PhpBasePort + 100;
                port = ProcessUtil.FreePort(Enumerable.Range(b, 100), taken);
                _ports[site.Name] = port;
            }
            if (port == 0) return I18n.T("err.portInUse", "-", "?");
            var id = site.Name.Replace('/', '_');
            var conf = Path.Combine(ConfDir, id + ".Caddyfile");
            Directory.CreateDirectory(ConfDir);
            Templates.WriteIfChanged(conf, Caddyfile(site, port, octane));
            var env = new Dictionary<string, string>
            {
                { "PHPRC", dir },
                { "PATH", dir + ";" + Environment.GetEnvironmentVariable("PATH") },
                { "XDG_DATA_HOME", Path.Combine(Paths.Tmp, "frankenphp") },
                { "XDG_CONFIG_HOME", Path.Combine(Paths.Tmp, "frankenphp") },
            };
            var p = Launch(Path.Combine(dir, "frankenphp.exe"), "run --config \"" + conf + "\" --adapter caddyfile", site.Folder, env, "frankenphp-" + id, proc => OnExit(site.Name, proc));
            lock (Sync) _procs[site.Name] = p;
            if (!await WaitForPort(port, p, 20000))
            {
                lock (Sync) { _procs.Remove(site.Name); _ports.Remove(site.Name); }
                if (!p.HasExited) StopProcess(p, false, 0);
                return (p.HasExited ? ExitReason(p) : I18n.T("err.timeout")) + " " + I18n.T("err.seeLog", "frankenphp-" + id + ".log");
            }
            Log.Info(I18n.T("log.started", "FrankenPHP " + site.Name + " (127.0.0.1:" + port + (octane ? ", worker" : "") + ")"));
            return null;
        }

        static string Caddyfile(Site site, int port, bool octane)
        {
            var root = Paths.Fwd(site.DocRoot);
            var sb = new StringBuilder();
            sb.Append("# Generated by Portly for ").Append(site.Name).Append(" - changes are overwritten.\r\n");
            sb.Append("{\r\n\tadmin off\r\n\tauto_https off\r\n\tpersist_config off\r\n\tskip_install_trust\r\n");
            sb.Append("\tstorage file_system \"").Append(Paths.Fwd(Path.Combine(Paths.Tmp, "frankenphp"))).Append("\"\r\n");
            // requests come from the local web server: trust its X-Forwarded-* headers
            sb.Append("\tservers {\r\n\t\ttrusted_proxies static 127.0.0.1/32 ::1/128\r\n\t}\r\n}\r\n\r\n");
            sb.Append("http://:").Append(port).Append(" {\r\n\tbind 127.0.0.1\r\n\troot * \"").Append(root).Append("\"\r\n");
            if (octane)
            {
                sb.Append("\tphp_server {\r\n\t\tindex frankenphp-worker.php\r\n\t\ttry_files {path} frankenphp-worker.php\r\n");
                sb.Append("\t\tenv APP_BASE_PATH \"").Append(Paths.Fwd(site.Folder)).Append("\"\r\n");
                sb.Append("\t\tenv APP_PUBLIC_PATH \"").Append(root).Append("\"\r\n");
                sb.Append("\t\tenv LARAVEL_OCTANE \"1\"\r\n");
                sb.Append("\t\tworker {\r\n\t\t\tfile \"").Append(Paths.Fwd(WorkerFile(site))).Append("\"\r\n\t\t}\r\n\t}\r\n");
            }
            else sb.Append("\tphp_server\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        void OnExit(string name, Process p)
        {
            lock (Sync)
            {
                Process cur;
                if (!_procs.TryGetValue(name, out cur) || cur != p) return;
                _procs.Remove(name);
                _ports.Remove(name);
            }
            if (!StopRequested) Log.Error("FrankenPHP " + name + ": " + ExitReason(p) + " " + I18n.T("err.seeLog", "frankenphp-" + name.Replace('/', '_') + ".log"));
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            List<Process> list;
            lock (Sync) { list = _procs.Values.ToList(); _procs.Clear(); _ports.Clear(); }
            if (list.Count == 0) { if (State != ServiceState.Stopped) SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() => { foreach (var p in list) StopProcess(p, true, 3000); });
            SetState(ServiceState.Stopped);
        }

        /// <summary>"FrankenPHP 1.13.2 PHP 8.5.12 Caddy v2.11.7" for the installed version (cached in the folder).</summary>
        public static string VersionText(string dir)
        {
            if (dir == null) return "";
            var cache = Path.Combine(dir, ".portly-version.txt");
            try
            {
                if (File.Exists(cache)) return File.ReadAllText(cache).Trim();
                var r = ProcessUtil.Run(Path.Combine(dir, "frankenphp.exe"), "version", dir, null, 15000);
                var line = r.Output.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("FrankenPHP"));
                if (line == null) return "";
                var h = line.IndexOf(" h1:", StringComparison.Ordinal);
                if (h > 0) line = line.Substring(0, h);
                File.WriteAllText(cache, line);
                return line;
            }
            catch { return ""; }
        }

        /// <summary>"8.5.12" from the version text.</summary>
        public static string PhpVersion(string dir)
        {
            var m = System.Text.RegularExpressions.Regex.Match(VersionText(dir), @"PHP (\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
