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

        /// <summary>Temporary option file with the root credentials (never put passwords on the command line).</summary>
        static string ClientFile()
        {
            var pass = Settings.Current.MysqlRootPassword.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var f = Path.Combine(Paths.Tmp, "my-client-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cnf");
            File.WriteAllText(f, "[client]\r\nuser=root\r\npassword=\"" + pass + "\"\r\nhost=127.0.0.1\r\nport=" + Settings.Current.MysqlPort + "\r\n", new UTF8Encoding(false));
            return f;
        }

        /// <summary>Run one of the bundled client tools (mysqlcheck, mysqldump, mariadb-upgrade ...) as root.</summary>
        public static RunResult Tool(string exe, string args, int timeout) { return Client(exe, args, null, timeout); }

        /// <summary>
        /// Start a client tool with redirected input / output for streaming (SQL import).
        /// The caller deletes <paramref name="cnf"/> after the process ended.
        /// </summary>
        public static System.Diagnostics.Process StartClient(string exe, string args, out string cnf)
        {
            cnf = null;
            var bin = MySqlService.BinDir;
            if (bin == null) throw new Exception(I18n.T("err.notInstalled", "MySQL"));
            cnf = ClientFile();
            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(bin, exe), "--defaults-extra-file=\"" + cnf + "\" " + args)
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
            var bin = MySqlService.BinDir;
            if (bin == null) return new RunResult { Output = I18n.T("err.notInstalled", "MySQL") };
            var cnf = ClientFile();
            try
            {
                return ProcessUtil.Run(Path.Combine(bin, exe), "--defaults-extra-file=\"" + cnf + "\" " + args, bin, null, timeout, stdin);
            }
            finally { try { File.Delete(cnf); } catch { } }
        }

        public static RunResult Admin(string command, int timeout) { return Client("mysqladmin.exe", command, null, timeout); }

        public static RunResult Sql(string sql, int timeout = 30000)
        {
            return Client("mysql.exe", "--default-character-set=utf8mb4 --batch --skip-column-names", sql, timeout);
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
            return null;
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

        public static string ChangeRootPassword(string newPassword)
        {
            if (ServiceManager.MySql.State != ServiceState.Running) return I18n.T("err.mysqlNotRunning");
            var r = Sql("ALTER USER 'root'@'localhost' IDENTIFIED BY " + SqlString(newPassword ?? "") + "; FLUSH PRIVILEGES;");
            if (!r.Ok) return Clean(r);
            Settings.Current.MysqlRootPassword = newPassword;
            Settings.Current.Save();
            Log.Info(I18n.T("log.rootPassword"));
            return null;
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
            var r = Client("mysqldump.exe", "--all-databases --routines --events --triggers --single-transaction --default-character-set=utf8mb4 --result-file=\"" + file + "\"", null, 3600000);
            if (!r.Ok || !File.Exists(file)) throw new Exception(Clean(r));
            Log.Info(I18n.T("log.backup", file));
            return file;
        }
    }
}
