using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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

        public static string ConfFile { get { return Path.Combine(Paths.Etc, "mysql", OS.IsWindows ? "my.ini" : "my.cnf"); } }

        /// <summary>
        /// macOS / Linux: the Unix socket "localhost" clients use. A socket path may hold only about 100 characters,
        /// so a deep Portly folder falls back to a short name in the system temp folder.
        /// </summary>
        public static string SocketFile
        {
            get
            {
                var f = Path.Combine(Paths.Tmp, "mysql.sock");
                if (f.Length <= 96) return f;
                using (var sha = SHA1.Create())
                {
                    var h = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Paths.Root))).Replace("-", "").Substring(0, 10).ToLowerInvariant();
                    return "/tmp/portly-" + h + "-mysql.sock";
                }
            }
        }

        public static string DataDirFor(string folder)
        {
            return Path.Combine(Paths.Data, Settings.Current.DbEngine + "-" + PackageTypes.Branch(folder));
        }

        public static string BinDir { get { var d = PackageTypes.ActiveDir(Settings.Current.DbEngine); return d == null ? null : Path.Combine(d, "bin"); } }

        internal static void WriteConfig(string dir, string data)
        {
            // secure-file-priv pointing at a missing folder stops mysqld from starting (tmp\ can be emptied by hand)
            Directory.CreateDirectory(Paths.MysqlFiles);
            var v = Templates.CommonVars();
            v["MYSQL_DIR"] = Paths.Fwd(dir);
            v["MYSQL_DATA"] = Paths.Fwd(data);
            v["MYSQL_PORT"] = Settings.Current.MysqlPort.ToString();
            if (!OS.IsWindows)
            {
                var tmp = Path.Combine(Paths.Tmp, "mysql");
                Directory.CreateDirectory(tmp);
                v["MYSQL_TMP"] = Paths.Fwd(tmp);
                v["MYSQL_SOCKET"] = Paths.Fwd(SocketFile);
                v["MYSQL_XSOCKET"] = Paths.Fwd(SocketFile).Replace("mysql.sock", "mysqlx.sock");
                v["MYSQL_PID"] = Paths.Fwd(Path.Combine(Paths.Tmp, "mysql.pid"));
            }
            // "localhost" resolves to ::1 first; MySQL 8.0.13+ and MariaDB 10.11+ accept several addresses
            var ver = PackageTypes.ParseVersion(dir);
            bool multi = IsMariaDb ? ver >= new Version(10, 11) : ver >= new Version(8, 0, 13);
            // Windows takes ~2 s to refuse a connection: a server missing on ::1 made every "localhost" page that slow
            if (Settings.Current.AllowLan) v["BIND"] = IsMariaDb ? "::" : "*";   // all IPv4 and IPv6 addresses (a list may not hold wildcards)
            else if (multi && Net.LoopbackV6) v["BIND"] = "127.0.0.1,::1";
#if !LEGACY
            // access from WSL: also listen on the WSL adapter (its address changes after a reboot)
            var wsl = OS.IsWindows && multi && !Settings.Current.AllowLan ? Tools.WslAccess.BindAddress() : null;
            if (wsl != null) v["BIND"] = v["BIND"] + "," + wsl;
#endif
            var collation = Tools.DbOptions.EffectiveCollation(Settings.Current.DbEngine, ver);
            v["CHARSET"] = Tools.DbOptions.CharsetOf(collation);
            v["COLLATION"] = collation;
            v["STORAGE_ENGINE"] = Tools.DbOptions.EffectiveStorageEngine(Settings.Current.DbEngine);
            v["SQL_MODE"] = Settings.Current.LegacySqlMode ? "sql_mode=NO_ENGINE_SUBSTITUTION" : "";
            // MySQL 8+: the X protocol would open port 33060 on all interfaces; not needed locally
            v["MYSQL_EXTRA"] = !IsMariaDb && PackageTypes.ParseVersion(dir).Major >= 8 ? "mysqlx=OFF" : "";
            Templates.WriteIfChanged(ConfFile, Templates.Render(OS.IsWindows ? "mysql.ini.tpl" : "mysql.cnf.tpl", v));
        }

        public override async Task StartAsync()
        {
            var engine = PackageType;
            var folder = PackageTypes.ActiveFolder(engine);
            if (folder == "") { SetState(ServiceState.Error, I18n.T("err.notInstalled", Title)); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            if (_extraArgs == null) { try { File.Delete(ResetFile); } catch { } } // left by a root password reset
            var dir = Path.Combine(Paths.BinOf(engine), folder);
            var data = DataDirFor(folder);
            var mysqld = Path.Combine(dir, "bin", OS.Exe("mysqld"));
            // MariaDB 11 warns about the old program name: use its own on macOS / Linux (the bintar has both)
            if (engine == "mariadb" && !OS.IsWindows && File.Exists(Path.Combine(dir, "bin", "mariadbd"))) mysqld = Path.Combine(dir, "bin", "mariadbd");
            var port = Settings.Current.MysqlPort;

            string error = await Task.Run(() =>
            {
                try { WriteConfig(dir, data); } catch (Exception ex) { return ex.Message; }
                if (OS.IsLinux)
                {
                    // MySQL builds look in lib/private first ($ORIGIN/../lib/private in their rpath); MariaDB ones link system libraries only
                    var missing = LinuxLibs.Fix(mysqld, Path.Combine(dir, "lib", engine == "mariadb" ? "" : "private"));
                    if (missing.Count > 0) return I18n.T("err.linuxLibs", Title, string.Join(", ", missing), LinuxLibs.InstallHint(missing));
                }
                if (!Directory.Exists(data) || !Directory.EnumerateFileSystemEntries(data).Any())
                {
                    Log.Info(I18n.T("log.mysqlInit", data));
                    try { if (Directory.Exists(data)) Directory.Delete(data); } catch { }
                    // MySQL: --initialize-insecure; MariaDB: its own mysql_install_db tool (root without password)
                    var r = engine == "mariadb"
                        ? MariaDbInstallDb(dir, data)
                        : ProcessUtil.Run(mysqld, "--defaults-file=\"" + ConfFile + "\"" + AsRoot + " --initialize-insecure --console", Path.Combine(dir, "bin"), null, 300000);
                    if (!Directory.Exists(data) || !Directory.EnumerateFileSystemEntries(data).Any())
                        return I18n.T("err.mysqlInit") + "\n" + ApacheService.LastLines(r.Output, 6);
                }
                return null;
            });
            if (error == null) error = Tools.DbVersionChange.BlockOlder(data, engine, folder);
            if (error != null) { SetState(ServiceState.Error, error); return; }
            if (!EnsurePortFree(port)) return;

            _p = Launch(mysqld, "--defaults-file=\"" + ConfFile + "\"" + AsRoot + (_extraArgs != null ? " " + _extraArgs : ""), Path.Combine(dir, "bin"), null, "mysql-console", OnExit);
            if (await WaitForPort(port, _p, 60000))
            {
                SetState(ServiceState.Running);
                Tools.DbVersionChange.WriteMarker(data, engine, folder);
                Log.Info(I18n.T("log.started", Title + " " + VersionLabel));
                // each engine / version line has its own data folder, and so its own root account
                if (_extraArgs == null) await Task.Run(() => Tools.Database.ReconcileRootPassword());
            }
            else SetState(ServiceState.Error, (_p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", "mysql-error.log") : I18n.T("err.timeout")) + RecoveryHint());
        }

        string _extraArgs;

        /// <summary>MariaDB: its own install tool; root without a password (like MySQL --initialize-insecure).</summary>
        static RunResult MariaDbInstallDb(string dir, string data)
        {
            if (OS.IsWindows)
                return ProcessUtil.Run(Path.Combine(dir, "bin", OS.Exe("mysql_install_db")), "--datadir=\"" + data + "\"", Path.Combine(dir, "bin"), null, 300000);
            var script = Path.Combine(dir, "scripts", "mariadb-install-db");
            if (!File.Exists(script)) script = Path.Combine(dir, "scripts", "mysql_install_db");
            Installer.MakeExecutable(script);
            return ProcessUtil.Run(script, "--defaults-file=\"" + ConfFile + "\" --basedir=\"" + dir + "\" --datadir=\"" + data + "\" --auth-root-authentication-method=normal" + AsRoot, dir, null, 300000);
        }

        /// <summary>macOS / Linux: mysqld refuses to run as root unless told to (a container, or sudo).</summary>
        static string AsRoot { get { return !OS.IsWindows && string.Equals(Environment.UserName, "root", StringComparison.Ordinal) ? " --user=root" : ""; } }

        /// <summary>After a crash / power loss InnoDB or Aria may refuse to start: point the user to recovery mode.</summary>
        static string RecoveryHint()
        {
            try
            {
                var log = Path.Combine(Paths.Logs, "mysql-error.log");
                if (!File.Exists(log)) return "";
                var tail = string.Join("\n", File.ReadAllLines(log).Reverse().Take(60));
                foreach (var word in new[] { "corrupt", "Corrupt", "crashed", "crash recovery", "init function returned error", "Aria recovery failed", "aria_log", "Database page corruption", "redo log", "ib_logfile" })
                    if (tail.Contains(word)) return "\n" + I18n.T("repair.hint");
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

        static string ResetFile { get { return Path.Combine(Paths.Tmp, "reset-root.sql"); } }

        /// <summary>Forgotten root password: restart once with --init-file that clears it.</summary>
        public async Task<string> ResetRootPasswordAsync()
        {
            await StopAsync();
            var f = ResetFile;
            File.WriteAllText(f, Tools.Database.ResetRootSql());
            var old = Settings.Current.MysqlRootPassword;
            // mysqld reads the init file some time after it opens the port, so the file is not deleted here
            // (the next normal start removes it); success = the empty password works and the server stays up
            await StartWithArgsAsync("--init-file=\"" + Paths.Fwd(f) + "\"");
            Settings.Current.MysqlRootPassword = "";
            bool ok = false;
            for (int i = 0; i < 40 && State == ServiceState.Running && !ok; i++)
            {
                await Task.Delay(500);
                ok = await Task.Run(() => Tools.Database.Sql("SELECT 1;", 5000).Ok);
            }
            if (ok) { await Task.Delay(1500); ok = State == ServiceState.Running; }
            if (!ok)
            {
                Settings.Current.MysqlRootPassword = old;
                return (State != ServiceState.Running ? LastError : null) ?? I18n.T("err.timeout");
            }
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
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", "mysql-error.log") + RecoveryHint());
        }

        /// <summary>The last stop was a clean shutdown (needed before copying the data folder to a new version).</summary>
        public bool LastStopClean { get; private set; }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            LastStopClean = false;
            await Task.Run(() =>
            {
                // clean shutdown keeps InnoDB fast to start next time
                if (!OS.IsWindows)
                {
                    // SIGTERM is mysqld's clean shutdown (no root password needed); kill only when it does not end
                    if (StopWithSignal(p, 15, 60000)) LastStopClean = true;
                    else StopProcess(p, false, 0);
                }
                else
                {
                    var r = Tools.Database.Admin("shutdown", 20000);
                    // a wrong root password makes "shutdown" fail at once: do not wait the full 30 s then
                    if (p.WaitForExit(r.Ok ? 30000 : 3000)) LastStopClean = r.Ok;
                    else StopProcess(p, false, 0);
                }
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
            // "localhost" clients try ::1 first (Windows needs ~2 s to refuse it); "-" = skip the address if it is missing (Redis 6.2+)
            if (PackageTypes.ParseVersion(dir) >= new Version(6, 2))
                v["BIND"] = Settings.Current.AllowLan ? "* -::*" : "127.0.0.1 -::1";
            v["LOGS"] = pathFor(Paths.Logs);
            Templates.WriteIfChanged(conf, Templates.Render("redis.conf.tpl", v));
            if (!EnsurePortFree(port)) return;
            var exe = Path.Combine(dir, PackageTypes.ExeDir("redis"), OS.Exe("redis-server"));
            _p = Launch(exe, "\"" + pathFor(conf) + "\"", Path.GetDirectoryName(exe), null, "redis-console", OnExit);
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
        HostGuard _guard;
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
            Dictionary<string, string> env = null;
            int web = s.MailpitWebPort;     // where the tool itself listens
            string secret = null, authFile = Path.Combine(data, "portly-ui-auth");
            switch (tool)
            {
                case "mailhog":
                    // no Host check and a WebSocket open to every site: only reachable through HostGuard
                    exe = Path.Combine(dir, OS.Exe("MailHog"));
                    web = PrivatePort();
                    secret = Guid.NewGuid().ToString("N");
                    var hash = ProcessUtil.Run(exe, "bcrypt " + secret, dir, null, 15000).Output.Trim();
                    File.WriteAllText(authFile, GuardUser + ":" + hash + "\n");
                    args = "-api-bind-addr 127.0.0.1:" + web + " -ui-bind-addr 127.0.0.1:" + web + " -smtp-bind-addr 127.0.0.1:" + s.MailpitSmtpPort + " -auth-file \"" + authFile + "\"";
                    break;
                case "smtp4dev":
                    exe = Path.Combine(dir, OS.Exe("Rnwood.Smtp4dev"));
                    // local only, no IMAP / POP3 ports, everything stored in data\smtp4dev
                    args = "--urls=http://127.0.0.1:" + s.MailpitWebPort + " --smtpport=" + s.MailpitSmtpPort +
                           " --allowremoteconnections- --imapport=\"\" --pop3port=\"\" --nousersettings" +
                           " --baseappdatapath=\"" + data + "\" --db=\"" + Path.Combine(data, "database.db") + "\"";
                    env = new Dictionary<string, string> { { "AllowedHosts", "localhost;127.0.0.1;[::1]" } }; // ASP.NET's Host filter
                    break;
                default:
                    exe = Path.Combine(dir, OS.Exe("mailpit"));
                    string hostCheck;
                    if (KnowsAllowedHosts(exe)) hostCheck = " --allowed-hosts localhost,127.0.0.1,[::1]";
                    else
                    {
                        // before 1.31.1: same answer as MailHog
                        web = PrivatePort();
                        secret = Guid.NewGuid().ToString("N");
                        using (var sha = SHA1.Create())
                            File.WriteAllText(authFile, GuardUser + ":{SHA}" + Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret))) + "\n");
                        hostCheck = " --ui-auth-file \"" + authFile + "\"";
                    }
                    args = "--listen 127.0.0.1:" + web + " --smtp 127.0.0.1:" + s.MailpitSmtpPort + " --database \"" + Path.Combine(data, "mailpit.db") + "\"" + hostCheck;
                    break;
            }
            _runningTool = tool;
            _p = Launch(exe, args, dir, env, tool, OnExit);
            bool up = await WaitForPort(web, _p, 30000) && await WaitForPort(s.MailpitSmtpPort, _p, 10000);
            try { File.Delete(authFile); } catch { } // the tool read it at its start
            if (up)
            {
                if (secret != null && !StartGuard(web, secret)) return;
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", Title + " " + VersionLabel));
            }
            else SetState(ServiceState.Error, _p.HasExited ? ExitReason(_p) + " " + I18n.T("err.seeLog", tool + ".log") : I18n.T("err.timeout"));
        }

        void OnExit(Process p)
        {
            if (p != _p) return;
            StopGuard();
            if (StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", LogName + ".log"));
        }

        const string GuardUser = "portly";
        static readonly Dictionary<string, bool> AllowedHostsOption = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Only for the smoke test: treat the installed Mailpit like one that has no --allowed-hosts.</summary>
        public static bool IgnoreAllowedHosts;

        /// <summary>--allowed-hosts came with Mailpit 1.31.1 (Host and WebSocket Origin); older ones stop at an unknown flag.</summary>
        static bool KnowsAllowedHosts(string exe)
        {
            if (IgnoreAllowedHosts) return false;
            bool known;
            lock (AllowedHostsOption)
            {
                if (!AllowedHostsOption.TryGetValue(exe, out known))
                    AllowedHostsOption[exe] = known = ProcessUtil.Run(exe, "--help", null, null, 15000).Output.Contains("--allowed-hosts");
            }
            return known;
        }

        static int PrivatePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>The configured web port opens only now, in front of the running tool. The tool does not stay up without it.</summary>
        bool StartGuard(int web, string secret)
        {
            var port = Settings.Current.MailpitWebPort;
            var auth = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(GuardUser + ":" + secret));
            try { _guard = HostGuard.Start(port, web, auth); }
            catch (SocketException)
            {
                StopRequested = true;
                StopProcess(_p, false, 0);
                SetState(ServiceState.Error, I18n.T("err.portInUse", port, ProcessUtil.PortOwner(port)));
                return false;
            }
            if (_p.HasExited) StopGuard(); // crashed right then: OnExit has already looked
            return true;
        }

        void StopGuard()
        {
            var g = _guard;
            _guard = null;
            if (g != null) g.Stop();
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            StopGuard();
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
