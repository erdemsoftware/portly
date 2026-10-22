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
        /// <summary>MySQL or MariaDB (Settings > Services > Database engine).</summary>
        public override string Title { get { return PackageTypes.Get(PackageType).Name; } }
        public override string PackageType { get { return Settings.Current.DbEngine; } }
        public static bool IsMariaDb { get { return Settings.Current.DbEngine == "mariadb"; } }
        public override string PortText { get { return Settings.Current.MysqlPort.ToString(); } }

        public static string ConfFile { get { return Path.Combine(Paths.Etc, "mysql", "my.ini"); } }

        public static string DataDirFor(string folder)
        {
            return Path.Combine(Paths.Data, Settings.Current.DbEngine + "-" + PackageTypes.Branch(folder));
        }

        public static string BinDir { get { var d = PackageTypes.ActiveDir(Settings.Current.DbEngine); return d == null ? null : Path.Combine(d, "bin"); } }

        void WriteConfig(string dir, string data)
        {
            var v = Templates.CommonVars();
            v["MYSQL_DIR"] = Paths.Fwd(dir);
            v["MYSQL_DATA"] = Paths.Fwd(data);
            v["MYSQL_PORT"] = Settings.Current.MysqlPort.ToString();
            // "localhost" resolves to ::1 first; MySQL 8.0.13+ and MariaDB 10.11+ accept several addresses
            var ver = PackageTypes.ParseVersion(dir);
            bool multi = IsMariaDb ? ver >= new Version(10, 11) : ver >= new Version(8, 0, 13);
            if (!Settings.Current.AllowLan && multi && Net.LoopbackV6) v["BIND"] = "127.0.0.1,::1";
            v["SQL_MODE"] = Settings.Current.LegacySqlMode ? "sql_mode=NO_ENGINE_SUBSTITUTION" : "";
            // MySQL 8+: the X protocol would open port 33060 on all interfaces; not needed locally
            v["MYSQL_EXTRA"] = !IsMariaDb && PackageTypes.ParseVersion(dir).Major >= 8 ? "mysqlx=OFF" : "";
            Templates.WriteIfChanged(ConfFile, Templates.Render("mysql.ini.tpl", v));
        }

        public override async Task StartAsync()
        {
            var engine = PackageType;
            var folder = PackageTypes.ActiveFolder(engine);
            if (folder == "") { SetState(ServiceState.Error, I18n.T("err.notInstalled", Title)); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var dir = Path.Combine(Paths.BinOf(engine), folder);
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
                    // MySQL: --initialize-insecure; MariaDB: its own mysql_install_db tool (root without password)
                    var r = engine == "mariadb"
                        ? ProcessUtil.Run(Path.Combine(dir, "bin", "mysql_install_db.exe"), "--datadir=\"" + data + "\"", Path.Combine(dir, "bin"), null, 300000)
                        : ProcessUtil.Run(mysqld, "--defaults-file=\"" + ConfFile + "\" --initialize-insecure --console", Path.Combine(dir, "bin"), null, 300000);
                    if (!Directory.Exists(data) || !Directory.EnumerateFileSystemEntries(data).Any())
                        return I18n.T("err.mysqlInit") + "\n" + ApacheService.LastLines(r.Output, 6);
                }
                return null;
            });
            if (error != null) { SetState(ServiceState.Error, error); return; }
            if (!EnsurePortFree(port)) return;

            _p = Launch(mysqld, "--defaults-file=\"" + ConfFile + "\"" + (_extraArgs != null ? " " + _extraArgs : ""), Path.Combine(dir, "bin"), null, "mysql-console", OnExit);
            if (await WaitForPort(port, _p, 60000))
            {
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", Title + " " + VersionLabel));
            }
            else SetState(ServiceState.Error, (_p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "mysql-error.log") : I18n.T("err.timeout")) + RecoveryHint());
        }

        string _extraArgs;

        /// <summary>After a crash / power loss InnoDB or Aria may refuse to start: point the user to recovery mode.</summary>
        static string RecoveryHint()
        {
            try
            {
                var log = Path.Combine(Paths.Logs, "mysql-error.log");
                if (!File.Exists(log)) return "";
                var tail = string.Join("\n", File.ReadAllLines(log).Reverse().Take(60));
                foreach (var word in new[] { "corrupt", "Corrupt", "crash recovery", "init function returned error", "Aria recovery failed", "Database page corruption", "redo log", "ib_logfile" })
                    if (tail.Contains(word)) return "\n" + I18n.T("err.mysqlRecover");
            }
            catch { }
            return "";
        }

        async Task StartWithArgsAsync(string extra)
        {
            _extraArgs = extra;
            try { await StartAsync(); }
            finally { _extraArgs = null; }
        }

        /// <summary>Forgotten root password: restart once with --init-file that clears it.</summary>
        public async Task<string> ResetRootPasswordAsync()
        {
            await StopAsync();
            var f = Path.Combine(Paths.Tmp, "reset-root.sql");
            File.WriteAllText(f, "ALTER USER 'root'@'localhost' IDENTIFIED BY '';\nFLUSH PRIVILEGES;\n");
            try { await StartWithArgsAsync("--init-file=\"" + Paths.Fwd(f) + "\""); }
            finally { try { File.Delete(f); } catch { } }
            if (State != ServiceState.Running) return LastError ?? I18n.T("err.timeout");
            Settings.Current.MysqlRootPassword = "";
            Settings.Current.Save();
            Log.Info(I18n.T("log.rootPassword"));
            return null;
        }

        /// <summary>Start with innodb_force_recovery=1 so the data can be backed up after a crash.</summary>
        public async Task StartRecoveryAsync()
        {
            await StopAsync();
            Log.Warn(I18n.T("db.recoveryStarted"));
            await StartWithArgsAsync("--innodb-force-recovery=1");
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
                var r = Tools.Database.Admin("shutdown", 20000);
                // a wrong root password makes "shutdown" fail at once: do not wait the full 30 s then
                if (!p.WaitForExit(r.Ok ? 30000 : 3000)) StopProcess(p, false, 0);
                p.WaitForExit(10000);
                // the next start (reset root, recovery, engine switch) needs the port really free
                for (int i = 0; i < 50 && ProcessUtil.IsPortInUse(Settings.Current.MysqlPort); i++) System.Threading.Thread.Sleep(200);
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
            // Redis 7/8 Windows builds are Cygwin / MSYS2 programs: they only understand /cygdrive/e/... paths
            bool posix = File.Exists(Path.Combine(dir, "msys-2.0.dll")) || File.Exists(Path.Combine(dir, "cygwin1.dll"));
            Func<string, string> pathFor = x => posix ? Posix(x) : Paths.Fwd(x);
            v["REDIS_PORT"] = port.ToString();
            v["REDIS_DATA"] = pathFor(data);
            v["LOGS"] = pathFor(Paths.Logs);
            Templates.WriteIfChanged(conf, Templates.Render("redis.conf.tpl", v));
            if (!EnsurePortFree(port)) return;
            _p = Launch(Path.Combine(dir, "redis-server.exe"), "\"" + pathFor(conf) + "\"", dir, null, "redis-console", OnExit);
            if (await WaitForPort(port, _p, 10000)) { SetState(ServiceState.Running); Log.Info(I18n.T("log.started", "Redis " + VersionLabel)); }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) : I18n.T("err.timeout"));
        }

        /// <summary>E:\www\x -> /cygdrive/e/www/x</summary>
        static string Posix(string path)
        {
            var full = Path.GetFullPath(path);
            return "/cygdrive/" + char.ToLowerInvariant(full[0]) + Paths.Fwd(full.Substring(2));
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

    /// <summary>
    /// Mail catcher: Mailpit, MailHog or smtp4dev (Settings > Mail catcher). PHP talks SMTP to it,
    /// so switching the tool needs no PHP change. Id stays "mailpit" for settings compatibility.
    /// </summary>
    public class MailpitService : Service
    {
        Process _p;
        string _runningTool;
        public static readonly string[] Tools = { "mailpit", "mailhog", "smtp4dev" };

        public override string Id { get { return "mailpit"; } }
        public override string Title { get { return PackageTypes.Get(PackageType).Name; } }
        public override string PackageType { get { return Settings.Current.MailTool; } }
        public override string PortText { get { var s = Settings.Current; return s.MailpitWebPort + ", " + s.MailpitSmtpPort; } }
        string LogName { get { return _runningTool ?? PackageType; } }

        public override async Task StartAsync()
        {
            var tool = PackageType;
            var dir = PackageTypes.ActiveDir(tool);
            if (dir == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", Title)); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var s = Settings.Current;
            if (!EnsurePortFree(s.MailpitWebPort) || !EnsurePortFree(s.MailpitSmtpPort)) return;
            var data = Path.Combine(Paths.Data, tool);
            Directory.CreateDirectory(data);
            string exe, args;
            switch (tool)
            {
                case "mailhog":
                    exe = Path.Combine(dir, "MailHog.exe");
                    args = "-api-bind-addr 127.0.0.1:" + s.MailpitWebPort + " -ui-bind-addr 127.0.0.1:" + s.MailpitWebPort + " -smtp-bind-addr 127.0.0.1:" + s.MailpitSmtpPort;
                    break;
                case "smtp4dev":
                    exe = Path.Combine(dir, "Rnwood.Smtp4dev.exe");
                    // local only, no IMAP / POP3 ports, everything stored in data\smtp4dev
                    args = "--urls=http://127.0.0.1:" + s.MailpitWebPort + " --smtpport=" + s.MailpitSmtpPort +
                           " --allowremoteconnections- --imapport=\"\" --pop3port=\"\" --nousersettings" +
                           " --baseappdatapath=\"" + data + "\" --db=\"" + Path.Combine(data, "database.db") + "\"";
                    break;
                default:
                    exe = Path.Combine(dir, "mailpit.exe");
                    args = "--listen 127.0.0.1:" + s.MailpitWebPort + " --smtp 127.0.0.1:" + s.MailpitSmtpPort + " --database \"" + Path.Combine(data, "mailpit.db") + "\"";
                    break;
            }
            _runningTool = tool;
            _p = Launch(exe, args, dir, null, tool, OnExit);
            if (await WaitForPort(s.MailpitWebPort, _p, 30000) && await WaitForPort(s.MailpitSmtpPort, _p, 10000))
            {
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", Title + " " + VersionLabel));
            }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", tool + ".log") : I18n.T("err.timeout"));
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", LogName + ".log"));
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            await Task.Run(() => StopProcess(p, false, 0));
            _p = null;
            _runningTool = null;
            SetState(ServiceState.Stopped);
        }
    }
}
