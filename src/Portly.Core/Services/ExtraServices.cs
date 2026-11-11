using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Portly.Core;
using Portly.Packages;

namespace Portly.Services
{
    /// <summary>
    /// PostgreSQL: data\postgresql-&lt;major&gt;, created with initdb (user "postgres", trust auth: local only).
    /// Runs postgres.exe directly so it lives in Portly's job object.
    /// </summary>
    public class PostgresService : Service
    {
        Process _p;
        string _data, _bin;
        public override string Id { get { return "postgresql"; } }
        public override string Title { get { return "PostgreSQL"; } }
        public override string PackageType { get { return "postgresql"; } }
        public override string PortText { get { return Settings.Current.PostgresPort.ToString(); } }

        public static string DataDirFor(string folder)
        {
            return Path.Combine(Paths.Data, "postgresql-" + PackageTypes.ParseVersion(folder).Major);
        }

        public override async Task StartAsync()
        {
            var dir = PackageTypes.ActiveDir("postgresql");
            if (dir == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", Title)); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            _bin = Path.Combine(dir, "bin");
            _data = DataDirFor(Path.GetFileName(dir));
            var port = Settings.Current.PostgresPort;
            string error = await Task.Run(() =>
            {
                if (Directory.Exists(_data) && File.Exists(Path.Combine(_data, "PG_VERSION"))) return null;
                Log.Info(I18n.T("log.mysqlInit", _data));
                try { if (Directory.Exists(_data) && !Directory.EnumerateFileSystemEntries(_data).Any()) Directory.Delete(_data); } catch { }
                var r = ProcessUtil.Run(Path.Combine(_bin, "initdb.exe"), "-D \"" + _data + "\" -U postgres -A trust -E UTF8 --no-locale", _bin, null, 300000);
                return File.Exists(Path.Combine(_data, "PG_VERSION")) ? null : I18n.T("err.mysqlInit") + "\n" + ApacheService.LastLines(r.Output, 6);
            });
            if (error != null) { SetState(ServiceState.Error, error); return; }
            if (!EnsurePortFree(port)) return;
            // also ::1: "localhost" clients try it first and Windows needs ~2 s to refuse a connection
            var listen = Settings.Current.AllowLan ? "*" : Net.LoopbackV6 ? "127.0.0.1,::1" : "127.0.0.1";
            _p = Launch(Path.Combine(_bin, "postgres.exe"), "-D \"" + _data + "\" -p " + port + " -h " + listen, _bin, null, "postgresql", OnExit);
            if (await WaitForPort(port, _p, 30000)) { SetState(ServiceState.Running); Log.Info(I18n.T("log.started", Title + " " + VersionLabel)); }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "postgresql.log") : I18n.T("err.timeout"));
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "postgresql.log"));
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() =>
            {
                // fast shutdown: active transactions are rolled back, data stays consistent
                ProcessUtil.Run(Path.Combine(_bin, "pg_ctl.exe"), "stop -D \"" + _data + "\" -m fast -w -t 20", _bin, null, 30000);
                if (!p.WaitForExit(10000)) StopProcess(p, false, 0);
            });
            _p = null;
            SetState(ServiceState.Stopped);
            Log.Info(I18n.T("log.stopped", Title));
        }
    }

    /// <summary>MongoDB: data\mongodb-&lt;major.minor&gt;, no authentication, 127.0.0.1 only.</summary>
    public class MongoService : Service
    {
        Process _p;
        public override string Id { get { return "mongodb"; } }
        public override string Title { get { return "MongoDB"; } }
        public override string PackageType { get { return "mongodb"; } }
        public override string PortText { get { return Settings.Current.MongoPort.ToString(); } }

        public override async Task StartAsync()
        {
            var dir = PackageTypes.ActiveDir("mongodb");
            if (dir == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", Title)); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var port = Settings.Current.MongoPort;
            var data = Path.Combine(Paths.Data, "mongodb-" + PackageTypes.Branch(Path.GetFileName(dir)));
            Directory.CreateDirectory(data);
            if (!EnsurePortFree(port)) return;
            // also ::1: "localhost" clients try it first and Windows needs ~2 s to refuse a connection
            var bind = Settings.Current.AllowLan ? "--bind_ip_all --ipv6" : Net.LoopbackV6 ? "--bind_ip 127.0.0.1,::1 --ipv6" : "--bind_ip 127.0.0.1";
            var args = "--dbpath \"" + data + "\" --port " + port + " " + bind + " --logpath \"" + Path.Combine(Paths.Logs, "mongodb.log") + "\" --logappend";
            _p = Launch(Path.Combine(dir, "bin", "mongod.exe"), args, Path.Combine(dir, "bin"), null, "mongodb-console", OnExit);
            if (await WaitForPort(port, _p, 60000)) { SetState(ServiceState.Running); Log.Info(I18n.T("log.started", Title + " " + VersionLabel)); }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "mongodb.log") : I18n.T("err.timeout"));
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "mongodb.log"));
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() => StopProcess(p, true, 20000)); // Ctrl+C = clean shutdown
            _p = null;
            SetState(ServiceState.Stopped);
            Log.Info(I18n.T("log.stopped", Title));
        }
    }

    /// <summary>Memcached: in-memory cache, 64 MB, 127.0.0.1 only.</summary>
    public class MemcachedService : Service
    {
        Process _p;
        public override string Id { get { return "memcached"; } }
        public override string Title { get { return "Memcached"; } }
        public override string PackageType { get { return "memcached"; } }
        public override string PortText { get { return Settings.Current.MemcachedPort.ToString(); } }

        public override async Task StartAsync()
        {
            var dir = PackageTypes.ActiveDir("memcached");
            if (dir == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", Title)); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var port = Settings.Current.MemcachedPort;
            if (!EnsurePortFree(port)) return;
            var bind = Settings.Current.AllowLan ? "0.0.0.0" : "127.0.0.1";
            _p = Launch(Path.Combine(dir, "bin", "memcached.exe"), "-p " + port + " -U 0 -l " + bind + " -m 64", Path.Combine(dir, "bin"), null, "memcached", OnExit);
            if (await WaitForPort(port, _p, 10000)) { SetState(ServiceState.Running); Log.Info(I18n.T("log.started", Title + " " + VersionLabel)); }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) : I18n.T("err.timeout"));
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "memcached.log"));
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() => StopProcess(p, false, 0));
            _p = null;
            SetState(ServiceState.Stopped);
        }
    }
}
