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
                var exe = Path.Combine(dir, "bin", "httpd.exe");
                var test = ProcessUtil.Run(exe, "-t -f \"" + WebConfig.ApacheConf + "\"", Path.Combine(dir, "bin"), null, 30000);
                if (!test.Ok) return I18n.T("err.config") + "\n" + LastLines(test.Output, 6);
                return null;
            });
            if (error != null) { SetState(ServiceState.Error, error); return; }
            if (!EnsurePortFree(s.HttpPort)) return;
            if (s.Ssl && !EnsurePortFree(s.HttpsPort)) return;

            var httpd = Path.Combine(dir, "bin", "httpd.exe");
            _p = Launch(httpd, "-f \"" + WebConfig.ApacheConf + "\"", Path.Combine(dir, "bin"), null, "apache-console", OnExit);
            if (await WaitForPort(s.HttpPort, _p, 15000))
            {
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", "Apache " + VersionLabel));
            }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "apache-error.log") : I18n.T("err.timeout"));
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
            await Task.Run(() => StopProcess(p, true, 8000));
            _p = null;
            SetState(ServiceState.Stopped);
            Log.Info(I18n.T("log.stopped", "Apache"));
        }

        internal static string LastLines(string text, int n)
        {
            var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - n)));
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
            var exe = Path.Combine(_dir, "nginx.exe");
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

            _p = Launch(exe, Args(""), _dir, null, "nginx-console", OnExit);
            if (await WaitForPort(s.HttpPort, _p, 10000))
            {
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", "Nginx " + VersionLabel));
            }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "nginx-error.log") : I18n.T("err.timeout"));
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
                ProcessUtil.Run(Path.Combine(_dir, "nginx.exe"), Args(" -s quit"), _dir, null, 10000);
                if (!p.WaitForExit(5000)) StopProcess(p, false, 0);
            });
            _p = null;
            SetState(ServiceState.Stopped);
            Log.Info(I18n.T("log.stopped", "Nginx"));
        }
    }
}
