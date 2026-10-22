using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>
    /// Check and repair: XAMPP's classic "MySQL shutdown unexpectedly" comes from damaged Aria / MyISAM tables,
    /// often the mysql.* system tables. mysqlcheck finds them; MyISAM / Aria / CSV / ARCHIVE tables are repaired,
    /// InnoDB tables cannot be repaired this way (recovery mode + backup is the way there).
    /// </summary>
    public static class DbRepair
    {
        public class Result
        {
            public int Checked;
            public List<string> Repaired = new List<string>();
            public List<string> NeedRecovery = new List<string>();
            public List<string> Failed = new List<string>();
            public string Summary
            {
                get
                {
                    var s = I18n.T("repair.summary", Checked, Repaired.Count, NeedRecovery.Count);
                    if (Repaired.Count > 0) s += "\n\n" + I18n.T("repair.repairedList", string.Join(", ", Repaired.Take(20)));
                    if (NeedRecovery.Count > 0) s += "\n\n" + I18n.T("repair.recoveryList", string.Join(", ", NeedRecovery.Take(20)));
                    if (Failed.Count > 0) s += "\n\n" + I18n.T("repair.failedList", string.Join(", ", Failed.Take(20)));
                    return s;
                }
            }
        }

        static readonly string[] Repairable = { "MyISAM", "Aria", "CSV", "ARCHIVE", "MRG_MYISAM" };

        static string CheckTool()
        {
            var bin = MySqlService.BinDir;
            return bin != null && File.Exists(Path.Combine(bin, "mariadb-check.exe")) ? "mariadb-check.exe" : "mysqlcheck.exe";
        }

        /// <summary>
        /// Parses mysqlcheck output: "db.table   OK" or "db.table" followed by "error : ..." / "warning : ..." lines.
        /// Returns table -> problem text ("" when OK).
        /// </summary>
        public static Dictionary<string, string> Parse(string output)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string current = null;
            var msg = new Regex(@"^(error|warning|note|info|status)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
            foreach (var raw in (output ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim() == "" || line.Contains("Using a password")) continue;
                var m = msg.Match(line.Trim());
                if (m.Success && current != null)
                {
                    // notes like "The storage engine for the table doesn't support check" are not problems
                    if (m.Groups[1].Value.Equals("error", StringComparison.OrdinalIgnoreCase))
                        d[current] = (d[current] + " " + m.Groups[2].Value).Trim();
                    else if (m.Groups[1].Value.Equals("warning", StringComparison.OrdinalIgnoreCase) && d[current] == "" && m.Groups[2].Value.IndexOf("crashed", StringComparison.OrdinalIgnoreCase) >= 0)
                        d[current] = m.Groups[2].Value;
                    continue;
                }
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !parts[0].Contains(".")) continue;
                current = parts[0];
                var rest = string.Join(" ", parts.Skip(1));
                d[current] = rest == "" || rest.Equals("OK", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("Table is already up to date", StringComparison.OrdinalIgnoreCase) ? "" : rest;
            }
            return d;
        }

        /// <summary>Check the system tables first, then all databases; repair what can be repaired. Server must run.</summary>
        public static Result CheckAndRepair(Action<string> status)
        {
            if (ServiceManager.MySql.State != ServiceState.Running) throw new Exception(I18n.T("err.mysqlNotRunning"));
            var res = new Result();
            var tool = CheckTool();
            var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            status(I18n.T("repair.checkingSystem"));
            var r = Database.Tool(tool, "--check --databases mysql", 3600000);
            foreach (var kv in Parse(r.Output)) all[kv.Key] = kv.Value;
            var users = Database.ListDatabases();
            if (users.Count > 0)
            {
                status(I18n.T("repair.checkingAll", users.Count));
                r = Database.Tool(tool, "--check --databases " + string.Join(" ", users.Select(n => "\"" + n + "\"")), 6 * 3600000);
                foreach (var kv in Parse(r.Output)) all[kv.Key] = kv.Value;
            }
            res.Checked = all.Count;
            var bad = all.Where(kv => kv.Value != "").Select(kv => kv.Key).ToList();
            if (bad.Count == 0) return res;

            var engines = EnginesOf(bad);
            foreach (var table in bad)
            {
                string engine;
                engines.TryGetValue(table, out engine);
                if (engine != null && !Repairable.Contains(engine, StringComparer.OrdinalIgnoreCase)) { res.NeedRecovery.Add(table); continue; }
                status(I18n.T("repair.repairing", table));
                var dot = table.IndexOf('.');
                var rr = Database.Tool(tool, "--repair \"" + table.Substring(0, dot) + "\" \"" + table.Substring(dot + 1) + "\"", 3600000);
                var after = Parse(rr.Output);
                string problem;
                if (rr.Ok && after.TryGetValue(table, out problem) && problem == "") res.Repaired.Add(table);
                else if (rr.Output.IndexOf("doesn't support repair", StringComparison.OrdinalIgnoreCase) >= 0) res.NeedRecovery.Add(table);
                else res.Failed.Add(table);
            }
            Log.Info(I18n.T("repair.summary", res.Checked, res.Repaired.Count, res.NeedRecovery.Count));
            return res;
        }

        static Dictionary<string, string> EnginesOf(List<string> tables)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cond = string.Join(" OR ", tables.Select(t =>
            {
                var i = t.IndexOf('.');
                return "(TABLE_SCHEMA = " + Database.SqlString(t.Substring(0, i)) + " AND TABLE_NAME = " + Database.SqlString(t.Substring(i + 1)) + ")";
            }));
            var r = Database.Sql("SELECT CONCAT(TABLE_SCHEMA, '.', TABLE_NAME), ENGINE FROM information_schema.TABLES WHERE " + cond + ";", 60000);
            if (!r.Ok) return d;
            foreach (var line in r.Output.Split('\n'))
            {
                var p = line.Trim().Split('\t');
                if (p.Length == 2 && p[1] != "NULL") d[p[0]] = p[1];
            }
            return d;
        }

        // =====================================================================
        // MariaDB offline fix (server does not start because of Aria logs)
        // =====================================================================
        /// <summary>The error log says the MariaDB server failed because of Aria (control file / log / system tables).</summary>
        public static bool AriaProblemInLog()
        {
            if (Settings.Current.DbEngine != "mariadb") return false;
            try
            {
                var log = Path.Combine(Paths.Logs, "mysql-error.log");
                if (!File.Exists(log)) return false;
                var tail = File.ReadLines(log).Reverse().Take(80).ToList();
                return tail.Any(l => l.IndexOf("Aria", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (l.Contains("recovery failed") || l.Contains("init function returned error") || l.Contains("corrupt") || l.Contains("aria_log") || l.Contains("crashed")));
            }
            catch { return false; }
        }

        /// <summary>
        /// Server stopped: back up the Aria log files and the mysql.* Aria tables, repair those tables with aria_chk -r,
        /// then remove the Aria logs (the backup copy stays in backup\mysql). Returns the backup folder.
        /// </summary>
        public static string OfflineAriaFix(Action<string> status)
        {
            if (ServiceManager.MySql.IsActive) throw new Exception(I18n.T("repair.stopFirst"));
            var folder = PackageTypes.ActiveFolder("mariadb");
            var data = MySqlService.DataDirFor(folder);
            var bin = MySqlService.BinDir;
            var ariaChk = bin == null ? null : Path.Combine(bin, "aria_chk.exe");
            if (ariaChk == null || !File.Exists(ariaChk)) throw new Exception(I18n.T("err.notInstalled", "aria_chk"));
            var backup = Path.Combine(Paths.Backup, "mysql", "aria-" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            Directory.CreateDirectory(Path.Combine(backup, "mysql"));

            status(I18n.T("repair.ariaBackup", backup));
            var logs = Directory.GetFiles(data, "aria_log*").ToList();
            var tables = Directory.Exists(Path.Combine(data, "mysql")) ? Directory.GetFiles(Path.Combine(data, "mysql"), "*.MA?") : new string[0];
            foreach (var f in logs) Copy(f, Path.Combine(backup, Path.GetFileName(f)));
            foreach (var f in tables) Copy(f, Path.Combine(backup, "mysql", Path.GetFileName(f)));

            foreach (var mai in tables.Where(f => f.EndsWith(".MAI", StringComparison.OrdinalIgnoreCase)))
            {
                status(I18n.T("repair.repairing", "mysql." + Path.GetFileNameWithoutExtension(mai)));
                var r = ProcessUtil.Run(ariaChk, "--datadir=\"" + data + "\" -r \"" + mai + "\"", bin, null, 600000);
                if (!r.Ok) Log.Warn("aria_chk " + Path.GetFileName(mai) + ": " + ApacheService.LastLines(r.Output, 4));
            }
            // only now, with a verified copy in the backup folder, the logs are moved aside
            foreach (var f in logs)
            {
                var copy = Path.Combine(backup, Path.GetFileName(f));
                if (File.Exists(copy) && new FileInfo(copy).Length == new FileInfo(f).Length) File.Delete(f);
            }
            Log.Info(I18n.T("repair.ariaDone", backup));
            return backup;
        }

        static void Copy(string from, string to)
        {
            File.Copy(from, to, true);
            if (new FileInfo(from).Length != new FileInfo(to).Length) throw new IOException(I18n.T("repair.copyFailed", from));
        }
    }
}
