using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;

namespace Portly.Services
{
    public class ApacheService : Service
    {
        Process _p;
        public override string Id { get { return "web"; } }
        public override string Title { get { return "Apache"; } }
        public override string PackageType { get { return "apache"; } }
        public override string PortText
        {
            get { var s = Settings.Current; return s.HttpPort + (s.Ssl ? ", " + s.HttpsPort : ""); }
        }

        /// <summary>Sites list is supplied by ServiceManager right before start.</summary>
        public List<Site> Sites = new List<Site>();
        public Func<string, int> PhpPort = f => 9100;

        public override async Task StartAsync()
        {
            var dir = PackageTypes.ActiveDir("apache");
            if (dir == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", "Apache")); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var s = Settings.Current;
            string error = await Task.Run(() =>
            {
                try { WebConfig.WriteApache(dir, Sites, PhpPort); }
                catch (Exception ex) { Log.Error("Apache config", ex); return ex.Message; }
                var exe = Path.Combine(dir, "bin", OS.Exe("httpd"));
                var test = ProcessUtil.Run(exe, "-t -f \"" + WebConfig.ApacheConf + "\"", Path.Combine(dir, "bin"), null, 30000);
                if (!test.Ok) return I18n.T("err.config") + "\n" + LastLines(test.Output, 6);
                return null;
            });
            if (error != null) { SetState(ServiceState.Error, error); return; }
            if (!EnsurePortFree(s.HttpPort)) return;
            if (s.Ssl && !EnsurePortFree(s.HttpsPort)) return;

            var httpd = Path.Combine(dir, "bin", OS.Exe("httpd"));
            long errFrom = LogSize("apache-error"), conFrom = LogSize("apache-console");
            // macOS / Linux: httpd detaches from the terminal unless told to stay in the foreground
            _p = Launch(httpd, "-f \"" + WebConfig.ApacheConf + "\"" + (OS.IsWindows ? "" : " -DFOREGROUND"), Path.Combine(dir, "bin"), null, "apache-console", OnExit);
            if (await WaitForPort(s.HttpPort, _p, 15000))
            {
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", "Apache " + VersionLabel));
            }
            else
            {
                string port = null;
                if (_p.HasExited && !OS.IsWindows)
                {
                    try { _p.WaitForExit(); } catch { }
                    port = WebPortProblem(LogSince("apache-console", conFrom) + "\n" + LogSince("apache-error", errFrom));
                }
                SetState(ServiceState.Error, port ?? (_p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "apache-error.log") : I18n.T("err.timeout")));
            }
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "apache-error.log"));
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() =>
            {
                // macOS / Linux: SIGWINCH = graceful stop (running requests finish), then SIGTERM, then the tree is killed
                if (!OS.IsWindows) { if (!StopWithSignal(p, 28, 10000) && !StopWithSignal(p, 15, 5000)) StopProcess(p, false, 0); }
                else StopProcess(p, true, 8000);
            });
            _p = null;
            SetState(ServiceState.Stopped);
            Log.Info(I18n.T("log.stopped", "Apache"));
        }

        internal static string LastLines(string text, int n)
        {
            var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - n)));
        }

        /// <summary>
        /// Apache / nginx that stopped at once on macOS / Linux because they cannot bind: "(13)Permission denied: ... could not bind
        /// to address 0.0.0.0:80" (Apache), "bind() to 0.0.0.0:80 failed (13: Permission denied)" (nginx). Null for anything else.
        /// </summary>
        internal static string WebPortProblem(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(output, @"\(13\)Permission denied.*?could not bind to address \S*?:(\d+)\s");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(output, @"bind\(\) to \S*?:(\d+) failed \(13: Permission denied\)");
            if (m.Success)
                return TOr("err.portPrivileged", "Port {0} needs administrator rights on this system (ports below 1024). Choose a port such as 8080 in Settings, or let Portly use the low ports.", m.Groups[1].Value);
            m = System.Text.RegularExpressions.Regex.Match(output, @"\(98\)Address already in use.*?could not bind to address \S*?:(\d+)\s");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(output, @"bind\(\) to \S*?:(\d+) failed \(98: Address already in use\)");
            if (m.Success)
            {
                int port = int.Parse(m.Groups[1].Value);
                var owner = ProcessUtil.PortOwner(port);
                return I18n.T("err.portInUse", port, owner == "" ? "?" : owner);
            }
            return null;
        }
    }

    public class NginxService : Service
    {
        Process _p;
        string _dir;
        public override string Id { get { return "web"; } }
        public override string Title { get { return "Nginx"; } }
        public override string PackageType { get { return "nginx"; } }
        public override string PortText
        {
            get { var s = Settings.Current; return s.HttpPort + (s.Ssl ? ", " + s.HttpsPort : ""); }
        }

        public List<Site> Sites = new List<Site>();
        public Func<string, int> PhpPort = f => 9100;

        string Args(string extra)
        {
            return "-p \"" + Paths.Fwd(_dir) + "/\" -c \"" + Paths.Fwd(WebConfig.NginxConf) + "\"" + extra;
        }

        public override async Task StartAsync()
        {
            _dir = PackageTypes.ActiveDir("nginx");
            if (_dir == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", "Nginx")); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var s = Settings.Current;
            var exe = Path.Combine(_dir, OS.Exe("nginx"));
            string error = await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(_dir, "logs"));
                    Directory.CreateDirectory(Path.Combine(_dir, "temp"));
                    WebConfig.WriteNginx(_dir, Sites, PhpPort);
                }
                catch (Exception ex) { Log.Error("Nginx config", ex); return ex.Message; }
                var test = ProcessUtil.Run(exe, Args(" -t"), _dir, null, 30000);
                if (!test.Ok) return I18n.T("err.config") + "\n" + ApacheService.LastLines(test.Output, 6);
                return null;
            });
            if (error != null) { SetState(ServiceState.Error, error); return; }
            if (!EnsurePortFree(s.HttpPort)) return;
            if (s.Ssl && !EnsurePortFree(s.HttpsPort)) return;

            long errFrom = LogSize("nginx-error"), conFrom = LogSize("nginx-console");
            _p = Launch(exe, Args(""), _dir, null, "nginx-console", OnExit);
            if (await WaitForPort(s.HttpPort, _p, 10000))
            {
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", "Nginx " + VersionLabel));
            }
            else
            {
                string port = null;
                if (_p.HasExited && !OS.IsWindows)
                {
                    try { _p.WaitForExit(); } catch { }
                    port = ApacheService.WebPortProblem(LogSince("nginx-console", conFrom) + "\n" + LogSince("nginx-error", errFrom));
                }
                SetState(ServiceState.Error, port ?? (_p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "nginx-error.log") : I18n.T("err.timeout")));
            }
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "nginx-error.log"));
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() =>
            {
                // macOS / Linux: SIGQUIT = graceful shutdown (the same as "nginx -s quit", without needing the pid file)
                if (!OS.IsWindows) { if (!StopWithSignal(p, 3, 10000) && !StopWithSignal(p, 15, 5000)) StopProcess(p, false, 0); return; }
                ProcessUtil.Run(Path.Combine(_dir, OS.Exe("nginx")), Args(" -s quit"), _dir, null, 10000);
                if (!p.WaitForExit(5000)) StopProcess(p, false, 0);
            });
            _p = null;
            SetState(ServiceState.Stopped);
            Log.Info(I18n.T("log.stopped", "Nginx"));
        }
    }
}
