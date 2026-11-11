using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>MySQL helpers using the bundled mysql / mysqladmin / mysqldump clients.</summary>
    public static class Database
    {
        static readonly Regex DbName = new Regex(@"^[A-Za-z0-9_]{1,64}$");

        public static bool IsValidName(string name) { return name != null && DbName.IsMatch(name); }

        /// <summary>"mysql", "mysql.exe", "mysqldump"... as the file name on this system (".exe" only on Windows).</summary>
        public static string ClientExe(string name)
        {
            name = name ?? "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return OS.Exe(name);
        }

        /// <summary>Temporary option file with the root credentials (never put passwords on the command line).</summary>
        static string ClientFile() { return ClientFile(Settings.Current.MysqlRootPassword); }

        /// <summary>Option file text: CRLF on Windows, LF elsewhere.</summary>
        internal static string Nl { get { return OS.IsWindows ? "\r\n" : "\n"; } }

        /// <summary>Writes a file that holds a secret; on Unix it is created readable by the current user only.</summary>
        internal static void WriteSecretFile(string path, string text)
        {
#if !NETFRAMEWORK
            if (!OS.IsWindows)
            {
                File.WriteAllText(path, "");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
#endif
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        static string ClientFile(string password)
        {
            var pass = password.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var f = Path.Combine(Paths.Tmp, "my-client-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cnf");
            WriteSecretFile(f, "[client]" + Nl + "user=root" + Nl + "password=\"" + pass + "\"" + Nl + "host=127.0.0.1" + Nl + "port=" + Settings.Current.MysqlPort + Nl);
            return f;
        }

        /// <summary>
        /// Copy a folder tree into <paramref name="to"/> (which must not exist or be empty): robocopy on Windows, cp -a on
        /// macOS / Linux (keeps modes and times). Throws with the tool's output when it fails.
        /// </summary>
        public static void CopyDirectory(string from, string to)
        {
            if (OS.IsWindows)
            {
                var r = ProcessUtil.Run("robocopy.exe", "\"" + from + "\" \"" + to + "\" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP", null, null, 24 * 3600 * 1000);
                if (r.ExitCode >= 8 || r.TimedOut) throw new Exception("robocopy: " + r.Output);
                return;
            }
            Directory.CreateDirectory(to);
            var c = ProcessUtil.Run("cp", "-a \"" + from.TrimEnd('/') + "/.\" \"" + to + "\"", null, null, 24 * 3600 * 1000);
            if (!c.Ok) throw new Exception("cp: " + c.Output);
        }

        /// <summary>Run one of the bundled client tools (mysqlcheck, mysqldump, mariadb-upgrade ...) as root.</summary>
        public static RunResult Tool(string exe, string args, int timeout) { return Client(exe, args, null, timeout); }

        /// <summary>
        /// Start a client tool with redirected input / output for streaming (SQL import).
        /// The caller deletes <paramref name="cnf"/> after the process ended.
        /// </summary>
        public static System.Diagnostics.Process StartClient(string exe, string args, out string cnf)
        {
            return StartClient(exe, args, out cnf, Settings.Current.MysqlRootPassword);
        }

        static System.Diagnostics.Process StartClient(string exe, string args, out string cnf, string password)
        {
            cnf = null;
            var bin = MySqlService.BinDir;
            if (bin == null) throw new Exception(I18n.T("err.notInstalled", "MySQL"));
            cnf = ClientFile(password);
            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(bin, ClientExe(exe)), "--defaults-extra-file=\"" + cnf + "\" " + args)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = bin,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            var p = System.Diagnostics.Process.Start(psi);
            JobObject.Add(p);
            return p;
        }

        static RunResult Client(string exe, string args, string stdin, int timeout)
        {
            return Client(exe, args, stdin, timeout, Settings.Current.MysqlRootPassword);
        }

        static RunResult Client(string exe, string args, string stdin, int timeout, string password)
        {
            var bin = MySqlService.BinDir;
            if (bin == null) return new RunResult { Output = I18n.T("err.notInstalled", "MySQL") };
            var cnf = ClientFile(password);
            try
            {
                return ProcessUtil.Run(Path.Combine(bin, ClientExe(exe)), "--defaults-extra-file=\"" + cnf + "\" " + args, bin, null, timeout, stdin);
            }
            finally { try { File.Delete(cnf); } catch { } }
        }

        public static RunResult Admin(string command, int timeout) { return Client("mysqladmin", command, null, timeout); }

        public static RunResult Sql(string sql, int timeout = 30000)
        {
            return Client("mysql", "--default-character-set=utf8mb4 --batch --skip-column-names", sql, timeout);
        }

        public static string Clean(RunResult r)
        {
            return string.Join("\n", r.Output.Split('\n').Where(l => !l.Contains("[Warning]") && !l.Contains("Using a password")).Select(l => l.Trim()).Where(l => l != ""));
        }

        /// <summary>Returns null on success, otherwise the error text. Uses the default collation (Settings > MySQL).</summary>
        public static string CreateDatabase(string name) { return CreateDatabase(name, null); }

        public static string CreateDatabase(string name, string collation)
        {
            if (!IsValidName(name)) return I18n.T("err.dbName");
            if (ServiceManager.MySql.State != ServiceState.Running) return I18n.T("err.mysqlNotRunning");
            collation = DbOptions.Valid(collation) ? collation : DbOptions.EffectiveCollation();
            var r = Sql("CREATE DATABASE IF NOT EXISTS `" + name + "` CHARACTER SET " + DbOptions.CharsetOf(collation) + " COLLATE " + collation + ";");
            if (!r.Ok) return Clean(r);
            Log.Info(I18n.T("log.dbCreated", name));
            return null;
        }

        /// <summary>Collation of an existing database, or null.</summary>
        public static string CollationOf(string db)
        {
            if (!IsValidName(db)) return null;
            var r = Sql("SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = " + SqlString(db) + ";");
            var v = r.Ok ? Clean(r).Trim() : "";
            return v == "" ? null : v;
        }

        /// <summary>
        /// A user that may only use one database ('user'@'localhost' and 'user'@'127.0.0.1').
        /// Returns null on success, otherwise the error text.
        /// </summary>
        public static string CreateUser(string db, string user, string password)
        {
            if (!IsValidName(db) || !IsValidName(user) || user.Length > 32) return I18n.T("db.badUser");
            if (ServiceManager.MySql.State != ServiceState.Running) return I18n.T("err.mysqlNotRunning");
            var sb = new StringBuilder();
            foreach (var host in new[] { "localhost", "127.0.0.1", "::1" })
            {
                var u = SqlString(user) + "@" + SqlString(host);
                sb.Append("CREATE USER IF NOT EXISTS " + u + " IDENTIFIED BY " + SqlString(password ?? "") + ";\n");
                sb.Append("ALTER USER " + u + " IDENTIFIED BY " + SqlString(password ?? "") + ";\n");
                sb.Append("GRANT ALL PRIVILEGES ON `" + db + "`.* TO " + u + ";\n");
            }
            sb.Append("FLUSH PRIVILEGES;\n");
            var r = Sql(sb.ToString());
            if (!r.Ok) return Clean(r);
            Log.Info(I18n.T("log.dbUserCreated", user, db));
            Config.ToolApps.RefreshPhpMyAdmin();   // more than root now: phpMyAdmin shows its login page
            return null;
        }

        /// <summary>
        /// MySQL / MariaDB accounts besides root and the built-in system ones (mysql.sys, mariadb.sys ...).
        /// -1 when the server cannot be asked (not running).
        /// </summary>
        public static int OtherUserCount()
        {
            if (ServiceManager.MySql.State != ServiceState.Running) return -1;
            var r = Sql("SELECT COUNT(DISTINCT User) FROM mysql.user WHERE User NOT IN " +
                        "('root','mysql.sys','mysql.session','mysql.infoschema','mariadb.sys','PUBLIC','');", 10000);
            int n;
            return r.Ok && int.TryParse(Clean(r).Trim(), out n) ? n : -1;
        }

        /// <summary>Random password from letters and digits (no quoting trouble in connection strings).</summary>
        public static string RandomPassword(int length)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            var bytes = new byte[length];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        public static List<string> ListDatabases()
        {
            var r = Sql("SHOW DATABASES;");
            if (!r.Ok) return new List<string>();
            var system = new[] { "information_schema", "mysql", "performance_schema", "sys" };
            return r.Output.Split('\n').Select(l => l.Trim()).Where(l => l != "" && !system.Contains(l) && !l.Contains("[Warning]")).ToList();
        }

        public static string SqlString(string s) { return "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'"; }

        // ---------------- root password ----------------
        static readonly Regex RootAccountRx = new Regex(@"^'root'@'[^']*'$");

        /// <summary>
        /// Root has no password while the server listens beyond this computer (LAN access, or the WSL adapter).
        /// The default root@localhost is still not reachable from there, but a project or tool on that network is.
        /// </summary>
        public static bool RootPasswordExposed
        {
            get
            {
                var s = Settings.Current;
                return !s.HasRootPassword && (s.AllowLan || (s.WslAccess && Features.IsOn("wsl")));
            }
        }

        /// <summary>A new connection as root with this password.</summary>
        public static bool LoginWorks(string password)
        {
            return Client("mysql", "--batch --skip-column-names", "SELECT 1;", 15000, password ?? "").Ok;
        }

        /// <summary>
        /// 'root'@'host' of every root account: one on MySQL; on MariaDB also 127.0.0.1, ::1 and the computer's name.
        /// null when this password does not log in.
        /// </summary>
        static List<string> RootAccounts(string password)
        {
            var r = Client("mysql", "--batch --skip-column-names", "SELECT CONCAT(QUOTE(User), '@', QUOTE(Host)) FROM mysql.user WHERE User = 'root';", 15000, password);
            if (!r.Ok) return null;
            return r.Output.Split('\n').Select(l => l.Trim()).Where(l => RootAccountRx.IsMatch(l)).ToList();
        }

        /// <summary>One statement: atomic on MySQL 8+, on the others the first error stops it and the caller puts things back.</summary>
        static string AlterRootSql(IEnumerable<string> accounts, string password)
        {
            return "ALTER USER " + string.Join(", ", accounts.Select(a => a + " IDENTIFIED BY " + SqlString(password))) + ";";
        }

        /// <summary>
        /// Script for --init-file, which runs before anyone could ask for the accounts, so SQL finds them itself. The first
        /// line alone covers the accounts that decide whether Portly gets in.
        /// </summary>
        public static string ResetRootSql()
        {
            return "ALTER USER IF EXISTS 'root'@'localhost' IDENTIFIED BY '', 'root'@'127.0.0.1' IDENTIFIED BY '', 'root'@'::1' IDENTIFIED BY '';\n" +
                   "SET @portly = (SELECT GROUP_CONCAT(CONCAT(QUOTE(User), '@', QUOTE(Host), ' IDENTIFIED BY ''''') SEPARATOR ', ') FROM mysql.user WHERE User = 'root');\n" +
                   "SET @portly = CONCAT('ALTER USER ', @portly);\n" +
                   "PREPARE portly FROM @portly;\nEXECUTE portly;\nDEALLOCATE PREPARE portly;\nFLUSH PRIVILEGES;\n";
        }

        /// <summary>
        /// A mysql.exe connection that stays open: after ALTER USER only this connection is sure to still be logged in,
        /// so a change that turns out wrong can be taken back through it.
        /// </summary>
        sealed class SqlSession : IDisposable
        {
            readonly System.Diagnostics.Process _p;
            readonly string _cnf;
            readonly StringBuilder _errors = new StringBuilder();
            readonly System.Threading.ManualResetEventSlim _done = new System.Threading.ManualResetEventSlim(false);
            string _marker;
            int _failed = -1;

            public SqlSession(string password)
            {
                // --force: an error does not end the connection; --unbuffered: answers arrive while stdin stays open
                _p = StartClient("mysql", "--default-character-set=utf8mb4 --batch --skip-column-names --unbuffered --force", out _cnf, password);
                _p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (_errors) _errors.AppendLine(e.Data); };
                _p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) { _done.Set(); return; }
                    var m = _marker;
                    if (m == null || !e.Data.StartsWith(m + ":")) return;
                    int.TryParse(e.Data.Substring(m.Length + 1).Trim(), out _failed);
                    _done.Set();
                };
                _p.BeginOutputReadLine();
                _p.BeginErrorReadLine();
            }

            /// <summary>True when the statement ran without an error; otherwise <paramref name="error"/> says why.</summary>
            public bool Run(string sql, out string error)
            {
                error = null;
                _done.Reset();
                _failed = -1;
                _marker = "portly-" + Guid.NewGuid().ToString("N");
                try
                {
                    var bytes = new UTF8Encoding(false).GetBytes(sql + "\nSELECT CONCAT('" + _marker + ":', @@error_count);\n");
                    _p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    _p.StandardInput.BaseStream.Flush();
                }
                catch (Exception ex) { error = ex.Message; return false; }
                bool answered = _done.Wait(30000);
                if (answered && _failed == 0) return true;
                System.Threading.Thread.Sleep(100);   // stderr has its own reader
                lock (_errors) error = Clean(new RunResult { Output = _errors.ToString() });
                if (error == "") error = I18n.T("err.timeout");
                return false;
            }

            public void Dispose()
            {
                try { _p.StandardInput.Close(); if (!_p.WaitForExit(5000)) ProcessUtil.KillTree(_p.Id); } catch { }
                try { File.Delete(_cnf); } catch { }
                _p.Dispose();
            }
        }

        /// <summary>
        /// Change every root account, then prove it with a new connection before anything is stored: the new password
        /// must log in and the old one must no longer do so. Otherwise the open connection puts the old password back.
        /// Returns null on success, otherwise the error text.
        /// </summary>
        static string ApplyRootPassword(string current, string next, Func<string, bool> verify, Action save)
        {
            var accounts = RootAccounts(current);
            if (accounts == null) return I18n.T("db.rootLoginFailed");
            if (accounts.Count == 0) return I18n.T("db.rootNoAccounts");
            Func<string, string> hide = t => next == "" ? t : t.Replace(next, "***");
            using (var session = new SqlSession(current))
            {
                string error;
                bool ok = session.Run(AlterRootSql(accounts, next), out error);
                if (ok && !(verify(next) && (next == current || !verify(current))))
                {
                    ok = false;
                    error = I18n.T("db.rootNotVerified");
                }
                if (ok && save != null)
                {
                    try { save(); }
                    catch (Exception ex) { ok = false; error = ex.Message; }
                }
                if (ok) return null;
                string undo;
                if (!session.Run(AlterRootSql(accounts, current), out undo)) return I18n.T("db.rootRollbackFailed", hide(error), hide(undo));
                return I18n.T("db.rootChangeFailed", hide(error));
            }
        }

        /// <summary>Set, change or (with "") remove the root password. Returns null on success, otherwise the error text.</summary>
        public static string ChangeRootPassword(string newPassword) { return ChangeRootPassword(newPassword, null); }

        /// <summary><paramref name="verify"/> replaces the login check (the tests make it fail on purpose).</summary>
        public static string ChangeRootPassword(string newPassword, Func<string, bool> verify)
        {
            newPassword = newPassword ?? "";
            if (ServiceManager.MySql.State != ServiceState.Running) return I18n.T("err.mysqlNotRunning");
            if (newPassword.Any(char.IsControl)) return I18n.T("db.rootBadChars");
            var s = Settings.Current;
            var err = ApplyRootPassword(s.MysqlRootPassword, newPassword, verify ?? LoginWorks, () =>
            {
                s.MysqlRootPassword = newPassword;
                s.Save();
            });
            if (err != null) return err;
            if (s.RunAsService) ServiceMode.PasswordChanged();
            Log.Info(I18n.T("log.rootPassword"));
            return null;
        }

        /// <summary>
        /// After every start. Each engine / version line has its own data folder, and a new one starts with an empty
        /// root: the stored password would lock Portly out of it, so the same password is set there too.
        /// </summary>
        public static void ReconcileRootPassword()
        {
            var stored = Settings.Current.MysqlRootPassword;
            if (LoginWorks(stored)) return;
            if (stored != "" && LoginWorks(""))
            {
                var err = ApplyRootPassword("", stored, LoginWorks, null);
                if (err == null) Log.Info(I18n.T("db.rootApplied"));
                else Log.Warn(err);
                return;
            }
            Log.Warn(I18n.T("db.rootLoginFailed"));
        }

        /// <summary>Dump all databases to backup\mysql. Returns the file, or throws with the error.</summary>
        public static string BackupAll() { return BackupAll(""); }

        /// <summary>
        /// Automatic backup (Settings > MySQL): runs when the interval has passed and MySQL is running.
        /// Only "auto-*.sql" files are rotated; manual backups are never deleted. Returns the file or null.
        /// </summary>
        public static string AutoBackupIfDue()
        {
            var s = Settings.Current;
            if (s.AutoBackupHours <= 0 || ServiceManager.MySql.State != ServiceState.Running) return null;
            if (System.Threading.Interlocked.Exchange(ref _autoRunning, 1) == 1) return null;
            try { return AutoBackupNow(s); }
            finally { _autoRunning = 0; }
        }

        static int _autoRunning;

        static string AutoBackupNow(Settings s)
        {
            DateTime last;
            if (DateTime.TryParse(s.LastAutoBackup, out last) && last > DateTime.Now.AddHours(-s.AutoBackupHours)) return null;
            var file = BackupAll("auto-");
            s.LastAutoBackup = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            s.Save();
            var dir = Path.GetDirectoryName(file);
            foreach (var old in new DirectoryInfo(dir).GetFiles("auto-*.sql").OrderByDescending(f => f.LastWriteTime).Skip(s.AutoBackupKeep))
                try { old.Delete(); } catch { }
            return file;
        }

        public static string BackupAll(string prefix)
        {
            if (ServiceManager.MySql.State != ServiceState.Running) throw new Exception(I18n.T("err.mysqlNotRunning"));
            var dir = Path.Combine(Paths.Backup, "mysql");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, prefix + Settings.Current.DbEngine + "-" + PackageTypes.Branch(PackageTypes.ActiveFolder(Settings.Current.DbEngine)) + "-" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".sql");
            var r = Client("mysqldump", "--all-databases --routines --events --triggers --single-transaction --default-character-set=utf8mb4 --result-file=\"" + file + "\"", null, 3600000);
            if (!r.Ok || !File.Exists(file)) throw new Exception(Clean(r));
            Log.Info(I18n.T("log.backup", file));
            return file;
        }
    }
}
