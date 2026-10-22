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
    /// <summary>
    /// PHP FastCGI pool: one php-cgi.exe (with child workers) per PHP version that is in use.
    /// Ports: PhpBasePort + index of the version among the installed ones (stable while nothing is installed/removed).
    /// </summary>
    public class PhpService : Service
    {
        public override string Id { get { return "php"; } }
        public override string Title { get { return "PHP"; } }
        public override string PackageType { get { return "php"; } }

        readonly Dictionary<string, Process> _procs = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<DateTime>> _crashes = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);

        public override string PortText
        {
            get
            {
                lock (Sync) return string.Join(", ", _procs.Keys.Select(PortFor));
            }
        }

        public int PortFor(string folder)
        {
            var installed = PackageTypes.Installed("php");
            installed.Sort(StringComparer.OrdinalIgnoreCase);
            int i = installed.FindIndex(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            return Settings.Current.PhpBasePort + Math.Max(0, i);
        }

        public List<string> RunningFolders { get { lock (Sync) return _procs.Keys.ToList(); } }

        public override Task StartAsync() { return StartAsync(Sites.NeededPhpFolders(Sites.Scan())); }

        public async Task StartAsync(List<string> folders)
        {
            if (folders.Count == 0) { SetState(ServiceState.Error, I18n.T("err.notInstalled", "PHP")); return; }
            StopRequested = false;
            lock (Sync) _crashes.Clear();
            SetState(ServiceState.Starting);
            var errors = new List<string>();
            foreach (var folder in folders)
            {
                lock (Sync) if (_procs.ContainsKey(folder) && !_procs[folder].HasExited) continue;
                var err = await StartOne(folder);
                if (err != null) errors.Add(PackageTypes.Label(folder) + ": " + err);
            }
            if (errors.Count > 0 && RunningFolders.Count == 0) SetState(ServiceState.Error, string.Join("\n", errors));
            else
            {
                foreach (var e in errors) Log.Error("PHP " + e);
                SetState(ServiceState.Running);
            }
        }

        async Task<string> StartOne(string folder)
        {
            var dir = Path.Combine(Paths.BinOf("php"), folder);
            var exe = Path.Combine(dir, "php-cgi.exe");
            if (!File.Exists(exe)) return I18n.T("err.notInstalled", "php-cgi.exe");
            try { PhpConfig.Ensure(dir); } catch (Exception ex) { Log.Warn("php.ini: " + ex.Message); }
            int port = PortFor(folder);
            if (ProcessUtil.IsPortInUse(port))
            {
                var owner = ProcessUtil.PortOwner(port);
                return I18n.T("err.portInUse", port, owner == "" ? "?" : owner);
            }
            var env = new Dictionary<string, string>
            {
                { "PHP_FCGI_MAX_REQUESTS", "0" },
                { "PHP_FCGI_CHILDREN", Math.Max(1, Settings.Current.PhpChildren).ToString() },
                { "PHPRC", dir },
                { "PATH", dir + ";" + Environment.GetEnvironmentVariable("PATH") },
            };
            Process p = null;
            p = Launch(exe, "-b 127.0.0.1:" + port + " -c \"" + PhpConfig.IniPath(dir) + "\"", dir, env, "php-" + PackageTypes.Label(folder), proc => OnExit(folder, proc));
            lock (Sync) _procs[folder] = p;
            if (!await WaitForPort(port, p, 10000))
            {
                lock (Sync) _procs.Remove(folder);
                return p.HasExited ? ExitReason(p) : I18n.T("err.timeout");
            }
            Log.Info(I18n.T("log.started", "PHP " + PackageTypes.Label(folder) + " (127.0.0.1:" + port + ")"));
            return null;
        }

        void OnExit(string folder, Process p)
        {
            lock (Sync)
            {
                Process cur;
                if (!_procs.TryGetValue(folder, out cur) || cur != p) return;
                _procs.Remove(folder);
            }
            if (StopRequested) return;
            // restart a crashed PHP, but give up when it crashes again and again (broken php.ini, missing DLL ...)
            List<DateTime> times;
            lock (Sync)
            {
                if (!_crashes.TryGetValue(folder, out times)) _crashes[folder] = times = new List<DateTime>();
                times.RemoveAll(t => t < DateTime.Now.AddMinutes(-1));
                times.Add(DateTime.Now);
            }
            if (times.Count > 3)
            {
                SetState(ServiceState.Error, "PHP " + PackageTypes.Label(folder) + " " + ExitReason(p) + " " + I18n.T("err.seeLog", "php-" + PackageTypes.Label(folder) + ".log"));
                return;
            }
            Log.Warn("PHP " + PackageTypes.Label(folder) + " " + ExitReason(p) + " - " + I18n.T("log.restarting"));
            Task.Run(async () =>
            {
                await Task.Delay(1000);
                if (StopRequested) return;
                var err = await StartOne(folder);
                if (err != null) SetState(ServiceState.Error, err);
            });
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            List<Process> list;
            lock (Sync) { list = _procs.Values.ToList(); _procs.Clear(); }
            if (list.Count == 0) { SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() => { foreach (var p in list) StopProcess(p, false, 0); });
            SetState(ServiceState.Stopped);
        }
    }
}
