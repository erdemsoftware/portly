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

        static string Clean(RunResult r)
        {
            return string.Join("\n", r.Output.Split('\n').Where(l => !l.Contains("[Warning]") && !l.Contains("Using a password")).Select(l => l.Trim()).Where(l => l != ""));
        }

        /// <summary>Returns null on success, otherwise the error text.</summary>
        public static string CreateDatabase(string name)
        {
            if (!IsValidName(name)) return I18n.T("err.dbName");
            if (ServiceManager.MySql.State != ServiceState.Running) return I18n.T("err.mysqlNotRunning");
            var r = Sql("CREATE DATABASE IF NOT EXISTS `" + name + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
            if (!r.Ok) return Clean(r);
            Log.Info(I18n.T("log.dbCreated", name));
            return null;
        }

        public static List<string> ListDatabases()
        {
            var r = Sql("SHOW DATABASES;");
            if (!r.Ok) return new List<string>();
            var system = new[] { "information_schema", "mysql", "performance_schema", "sys" };
            return r.Output.Split('\n').Select(l => l.Trim()).Where(l => l != "" && !system.Contains(l) && !l.Contains("[Warning]")).ToList();
        }

        static string SqlString(string s) { return "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'"; }

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
        public static string BackupAll()
        {
            if (ServiceManager.MySql.State != ServiceState.Running) throw new Exception(I18n.T("err.mysqlNotRunning"));
            var dir = Path.Combine(Paths.Backup, "mysql");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "mysql-" + PackageTypes.Branch(PackageTypes.ActiveFolder("mysql")) + "-" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".sql");
            var r = Client("mysqldump.exe", "--all-databases --routines --events --triggers --single-transaction --default-character-set=utf8mb4 --result-file=\"" + file + "\"", null, 3600000);
            if (!r.Ok || !File.Exists(file)) throw new Exception(Clean(r));
            Log.Info(I18n.T("log.backup", file));
            return file;
        }
    }
}
