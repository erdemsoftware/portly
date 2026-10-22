using System;
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
    /// MySQL. Each major.minor line keeps its own data folder (data\mysql-8.4, data\mysql-9.7 ...),
    /// so switching versions never damages data. Patch updates (8.4.10 -> 8.4.11) reuse the same data.
    /// </summary>
    public class MySqlService : Service
    {
        Process _p;
        public override string Id { get { return "mysql"; } }
        public override string Title { get { return "MySQL"; } }
        public override string PackageType { get { return "mysql"; } }
        public override string PortText { get { return Settings.Current.MysqlPort.ToString(); } }

        public static string ConfFile { get { return Path.Combine(Paths.Etc, "mysql", "my.ini"); } }

        public static string DataDirFor(string folder)
        {
            return Path.Combine(Paths.Data, "mysql-" + PackageTypes.Branch(folder));
        }

        public static string BinDir { get { var d = PackageTypes.ActiveDir("mysql"); return d == null ? null : Path.Combine(d, "bin"); } }

        void WriteConfig(string dir, string data)
        {
            var v = Templates.CommonVars();
            v["MYSQL_DIR"] = Paths.Fwd(dir);
            v["MYSQL_DATA"] = Paths.Fwd(data);
            v["MYSQL_PORT"] = Settings.Current.MysqlPort.ToString();
            // MySQL 8+: the X protocol would open port 33060 on all interfaces; not needed locally
            v["MYSQL_EXTRA"] = PackageTypes.ParseVersion(dir).Major >= 8 ? "mysqlx=OFF" : "";
            Templates.WriteIfChanged(ConfFile, Templates.Render("mysql.ini.tpl", v));
        }

        public override async Task StartAsync()
        {
            var folder = PackageTypes.ActiveFolder("mysql");
            if (folder == "") { SetState(ServiceState.Error, I18n.T("err.notInstalled", "MySQL")); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var dir = Path.Combine(Paths.BinOf("mysql"), folder);
            var data = DataDirFor(folder);
            var mysqld = Path.Combine(dir, "bin", "mysqld.exe");
            var port = Settings.Current.MysqlPort;

            string error = await Task.Run(() =>
            {
                try { WriteConfig(dir, data); } catch (Exception ex) { return ex.Message; }
                if (!Directory.Exists(data) || !Directory.EnumerateFileSystemEntries(data).Any())
                {
                    Log.Info(I18n.T("log.mysqlInit", data));
                    try { if (Directory.Exists(data)) Directory.Delete(data); } catch { }
                    var r = ProcessUtil.Run(mysqld, "--defaults-file=\"" + ConfFile + "\" --initialize-insecure --console", Path.Combine(dir, "bin"), null, 300000);
                    if (!Directory.Exists(data) || !Directory.EnumerateFileSystemEntries(data).Any())
                        return I18n.T("err.mysqlInit") + "\n" + ApacheService.LastLines(r.Output, 6);
                }
                return null;
            });
            if (error != null) { SetState(ServiceState.Error, error); return; }
            if (!EnsurePortFree(port)) return;

            _p = Launch(mysqld, "--defaults-file=\"" + ConfFile + "\"", Path.Combine(dir, "bin"), null, "mysql-console", OnExit);
            if (await WaitForPort(port, _p, 60000))
            {
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", "MySQL " + VersionLabel));
            }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "mysql-error.log") : I18n.T("err.timeout"));
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "mysql-error.log"));
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() =>
            {
                // clean shutdown keeps InnoDB fast to start next time
                Tools.Database.Admin("shutdown", 20000);
                if (!p.WaitForExit(30000)) StopProcess(p, false, 0);
            });
            _p = null;
            SetState(ServiceState.Stopped);
            Log.Info(I18n.T("log.stopped", "MySQL"));
        }
    }

    public class RedisService : Service
    {
        Process _p;
        public override string Id { get { return "redis"; } }
        public override string Title { get { return "Redis"; } }
        public override string PackageType { get { return "redis"; } }
        public override string PortText { get { return Settings.Current.RedisPort.ToString(); } }

        public override async Task StartAsync()
        {
            var dir = PackageTypes.ActiveDir("redis");
            if (dir == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", "Redis")); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var port = Settings.Current.RedisPort;
            var data = Path.Combine(Paths.Data, "redis");
            Directory.CreateDirectory(data);
            var conf = Path.Combine(Paths.Etc, "redis", "redis.conf");
            var v = Templates.CommonVars();
            v["REDIS_PORT"] = port.ToString();
            v["REDIS_DATA"] = Paths.Fwd(data);
            Templates.WriteIfChanged(conf, Templates.Render("redis.conf.tpl", v));
            if (!EnsurePortFree(port)) return;
            _p = Launch(Path.Combine(dir, "redis-server.exe"), "\"" + conf + "\"", dir, null, "redis-console", OnExit);
            if (await WaitForPort(port, _p, 10000)) { SetState(ServiceState.Running); Log.Info(I18n.T("log.started", "Redis " + VersionLabel)); }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) : I18n.T("err.timeout"));
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "redis.log"));
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
        }
    }

    public class MailpitService : Service
    {
        Process _p;
        public override string Id { get { return "mailpit"; } }
        public override string Title { get { return "Mailpit"; } }
        public override string PackageType { get { return "mailpit"; } }
        public override string PortText { get { var s = Settings.Current; return s.MailpitWebPort + ", " + s.MailpitSmtpPort; } }

        public override async Task StartAsync()
        {
            var dir = PackageTypes.ActiveDir("mailpit");
            if (dir == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", "Mailpit")); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var s = Settings.Current;
            if (!EnsurePortFree(s.MailpitWebPort) || !EnsurePortFree(s.MailpitSmtpPort)) return;
            var data = Path.Combine(Paths.Data, "mailpit");
            Directory.CreateDirectory(data);
            var args = "--listen 127.0.0.1:" + s.MailpitWebPort + " --smtp 127.0.0.1:" + s.MailpitSmtpPort + " --database \"" + Path.Combine(data, "mailpit.db") + "\"";
            _p = Launch(Path.Combine(dir, "mailpit.exe"), args, dir, null, "mailpit", OnExit);
            if (await WaitForPort(s.MailpitWebPort, _p, 10000)) { SetState(ServiceState.Running); Log.Info(I18n.T("log.started", "Mailpit " + VersionLabel)); }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) : I18n.T("err.timeout"));
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "mailpit.log"));
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
