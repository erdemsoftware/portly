using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>How the databases get to another MySQL / MariaDB version.</summary>
    public enum DbMove
    {
        /// <summary>Same data folder (same engine and major.minor line): nothing to do.</summary>
        None,
        /// <summary>Same engine, newer line: copy the data folder, the new server upgrades it.</summary>
        Copy,
        /// <summary>Engine change, downgrade or an unsupported jump: dump from the old server, load into the new one.</summary>
        Dump,
    }

    /// <summary>
    /// Safe database version change. Each engine + line has its own data folder (data\mysql-8.4, data\mariadb-11.8),
    /// so after switching to a newer version the databases seemed to vanish. When the new folder does not exist yet
    /// and the old one holds databases, Portly offers to bring them along; the old folder is never changed.
    /// </summary>
    public static class DbVersionChange
    {
        /// <summary>Written into the data folder after every successful start: "mysql 8.4.11".</summary>
        public const string MarkerFile = "portly-server.txt";
        static readonly string[] SystemDirs = { "mysql", "performance_schema", "sys", "information_schema" };

        // =====================================================================
        // Decision (pure, unit tested)
        // =====================================================================
        public static DbMove Decide(string fromEngine, Version from, string toEngine, Version to)
        {
            if (fromEngine != toEngine) return DbMove.Dump;
            var fromLine = new Version(from.Major, from.Minor);
            var toLine = new Version(to.Major, to.Minor);
            if (toLine == fromLine) return DbMove.None;
            if (toLine < fromLine) return DbMove.Dump;          // downgrades are never done on the files
            if (fromEngine == "mariadb") return DbMove.Copy;    // mariadb-upgrade handles any newer line
            // MySQL upgrades in place only one series at a time: 5.7 -> 8.0 -> 8.4 LTS -> 9.x ...
            if (fromLine < new Version(5, 7)) return DbMove.Dump;
            if (fromLine == new Version(5, 7)) return toLine == new Version(8, 0) ? DbMove.Copy : DbMove.Dump;
            if (fromLine == new Version(8, 0)) return toLine <= new Version(8, 4) ? DbMove.Copy : DbMove.Dump;
            return DbMove.Copy;
        }

        // =====================================================================
        // Data folder state
        // =====================================================================
        public static string DataDir(string engine, string folder)
        {
            return Path.Combine(Paths.Data, engine + "-" + PackageTypes.Branch(folder));
        }

        public static bool IsEmptyDir(string dir)
        {
            try { return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any(); } catch { return false; }
        }

        /// <summary>User databases in a data folder (sub folders that are not system schemas), without a server.</summary>
        public static List<string> DatabasesIn(string dataDir)
        {
            var list = new List<string>();
            if (!Directory.Exists(dataDir)) return list;
            foreach (var d in Directory.GetDirectories(dataDir))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith("#") || SystemDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                // MariaDB creates an empty "test" database
                if (name == "test" && !Directory.EnumerateFiles(d).Any(f => !f.EndsWith("db.opt", StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(name.Replace("@002d", "-"));
            }
            return list;
        }

        /// <summary>Server version that last used a data folder, or null when unknown.</summary>
        public static Version WrittenBy(string dataDir)
        {
            try
            {
                var marker = Path.Combine(dataDir, MarkerFile);
                if (File.Exists(marker)) return PackageTypes.ParseVersion(File.ReadAllText(marker));
                // MySQL 8.0.36+ keeps an upgrade history, MariaDB a mysql_upgrade_info file
                var hist = Path.Combine(dataDir, "mysql_upgrade_history");
                if (File.Exists(hist))
                {
                    var m = Regex.Matches(File.ReadAllText(hist), "\"version\"\\s*:\\s*\"([\\d.]+)\"");
                    if (m.Count > 0) return PackageTypes.ParseVersion(m[m.Count - 1].Groups[1].Value);
                }
                var info = Path.Combine(dataDir, "mysql_upgrade_info");
                if (File.Exists(info)) return PackageTypes.ParseVersion(File.ReadAllText(info));
            }
            catch { }
            return null;
        }

        public static void WriteMarker(string dataDir, string engine, string folder)
        {
            try { File.WriteAllText(Path.Combine(dataDir, MarkerFile), engine + " " + PackageTypes.Label(folder) + "\r\n"); } catch { }
        }

        /// <summary>
        /// Error text when an older server would open a data folder a newer one has written (that damages it:
        /// e.g. MySQL 8.0.x patch downgrades), otherwise null.
        /// </summary>
        public static string BlockOlder(string dataDir, string engine, string folder)
        {
            var written = WrittenBy(dataDir);
            if (written == null || written.Major == 0) return null;
            var mine = PackageTypes.ParseVersion(folder);
            var w = new Version(written.Major, written.Minor, Math.Max(0, written.Build));
            var m = new Version(mine.Major, mine.Minor, Math.Max(0, mine.Build));
            if (new Version(w.Major, w.Minor) > new Version(m.Major, m.Minor)
                // MySQL 8.0 had no patch downgrade support; 8.4 LTS and MariaDB lines do
                || engine == "mysql" && w.Major == 8 && w.Minor == 0 && m.Major == 8 && m.Minor == 0 && w > m)
                return I18n.T("dbv.blocked", PackageTypes.Get(engine).Name + " " + PackageTypes.Label(folder), w.ToString(), dataDir);
            return null;
        }

        // =====================================================================
        // Plan
        // =====================================================================
        public class Plan
        {
            public string FromEngine, FromFolder, ToEngine, ToFolder;
            public string FromData, ToData;
            public List<string> Databases = new List<string>();
            public DbMove Move;
            public string FromLabel { get { return PackageTypes.Get(FromEngine).Name + " " + PackageTypes.Label(FromFolder); } }
            public string ToLabel { get { return PackageTypes.Get(ToEngine).Name + " " + PackageTypes.Label(ToFolder); } }
        }

        /// <summary>
        /// A plan when switching from the active database server to <paramref name="toEngine"/> / <paramref name="toFolder"/>
        /// should offer to bring the databases along, otherwise null (switch as usual).
        /// </summary>
        public static Plan PlanFor(string toEngine, string toFolder)
        {
            var fromEngine = Settings.Current.DbEngine;
            var fromFolder = PackageTypes.ActiveFolder(fromEngine);
            if (fromFolder == "" || string.IsNullOrEmpty(toFolder)) return null;
            var p = new Plan
            {
                FromEngine = fromEngine, FromFolder = fromFolder, ToEngine = toEngine, ToFolder = toFolder,
                FromData = DataDir(fromEngine, fromFolder), ToData = DataDir(toEngine, toFolder),
            };
            if (string.Equals(p.FromData, p.ToData, StringComparison.OrdinalIgnoreCase)) return null;
            if (!IsEmptyDir(p.ToData)) return null;   // the target line already has its own databases
            p.Databases = DatabasesIn(p.FromData);
            if (p.Databases.Count == 0) return null;
            p.Move = Decide(fromEngine, PackageTypes.ParseVersion(fromFolder), toEngine, PackageTypes.ParseVersion(toFolder));
            return p.Move == DbMove.None ? null : p;
        }

        // =====================================================================
        // Move
        // =====================================================================
        /// <summary>
        /// Bring the databases to the new version and make it the active one. Returns the summary text; throws with the
        /// error (the old version is active again then). The old data folder is never changed.
        /// </summary>
        public static async Task<string> MoveAsync(Plan p, Action<string> status, CancellationToken ct)
        {
            var mysql = ServiceManager.MySql;
            bool wasActive = mysql.IsActive;
            var s = Settings.Current;
            var rootPassword = s.MysqlRootPassword;

            // 1. the old server must run: a start after an unclean stop does its crash recovery now
            status(I18n.T("dbv.startingOld", p.FromLabel));
            if (mysql.State != ServiceState.Running) await mysql.StartAsync();
            if (mysql.State != ServiceState.Running) throw new Exception(I18n.T("dbv.oldFailed", p.FromLabel) + "\n\n" + mysql.LastError);

            // 2. automatic backup of everything
            status(I18n.T("db.backingUp"));
            var backup = await Task.Run(() => Database.BackupAll("pre-upgrade-"));
            ct.ThrowIfCancellationRequested();

            var fromVer = PackageTypes.ParseVersion(p.FromFolder);
            var toVer = PackageTypes.ParseVersion(p.ToFolder);
            string dump = null, notes = "";
            if (p.Move == DbMove.Dump)
            {
                status(I18n.T("import.dumping", p.Databases.Count));
                dump = await Task.Run(() => DumpUserDatabases(), ct);
            }
            else if (p.FromEngine == "mysql" && fromVer < new Version(8, 0) && toVer >= new Version(8, 0))
            {
                // 5.7 -> 8.0: the checks the old server can do itself (old temporal types, invalid table definitions)
                status(I18n.T("dbv.checking"));
                var r = await Task.Run(() => Database.Tool("mysqlcheck.exe", "--all-databases --check-upgrade", 3600000));
                var problems = Database.Clean(r).Split('\n').Where(l => l.Contains("error") || l.Contains("upgrade")).ToList();
                if (!r.Ok || problems.Count > 0) notes += "\n" + I18n.T("dbv.checkNotes", string.Join("\n", problems.Take(10)));
            }

            // 3. clean stop of the old server
            status(I18n.T("dbv.stoppingOld", p.FromLabel));
            await mysql.StopAsync();
            if (!mysql.LastStopClean)
            {
                if (wasActive) await mysql.StartAsync();
                throw new Exception(I18n.T("dbv.notClean", p.FromLabel));
            }

            // 4. switch and bring the data over
            string failedCopy = null;
            try
            {
                if (p.Move == DbMove.Copy)
                {
                    status(I18n.T("dbv.copying", p.FromData, p.ToData));
                    await Task.Run(() => CopyDir(p.FromData, p.ToData), ct);
                    failedCopy = p.ToData;
                }
                Activate(p.ToEngine, p.ToFolder);
                status(I18n.T("dbv.startingNew", p.ToLabel));
                if (p.Move == DbMove.Dump) s.MysqlRootPassword = "";   // a fresh data folder: root has no password yet
                await mysql.StartAsync();
                if (mysql.State != ServiceState.Running) throw new Exception(I18n.T("dbv.newFailed", p.ToLabel) + "\n\n" + mysql.LastError);
                failedCopy = null;

                if (p.Move == DbMove.Copy && p.ToEngine == "mariadb")
                {
                    status(I18n.T("dbv.upgrading"));
                    var exe = File.Exists(Path.Combine(MySqlService.BinDir, "mariadb-upgrade.exe")) ? "mariadb-upgrade.exe" : "mysql_upgrade.exe";
                    var r = await Task.Run(() => Database.Tool(exe, "--force", 3600000));
                    if (!r.Ok) notes += "\n" + I18n.T("dbv.upgradeWarn", ApacheService.LastLines(r.Output, 6));
                }
                else if (p.Move == DbMove.Copy && toVer < new Version(8, 0, 16) && File.Exists(Path.Combine(MySqlService.BinDir, "mysql_upgrade.exe")))
                {
                    // before 8.0.16 MySQL did not upgrade the system tables at start
                    status(I18n.T("dbv.upgrading"));
                    var r = await Task.Run(() => Database.Tool("mysql_upgrade.exe", "--force", 3600000));
                    if (!r.Ok) notes += "\n" + I18n.T("dbv.upgradeWarn", ApacheService.LastLines(r.Output, 6));
                }

                if (p.Move == DbMove.Dump)
                {
                    if (!string.IsNullOrEmpty(rootPassword))
                    {
                        var err = Database.ChangeRootPassword(rootPassword);
                        if (err != null) notes += "\n" + err;
                    }
                    status(I18n.T("import.loading"));
                    var result = await Task.Run(() => SqlImport.Run(dump, null, true, null, ct));
                    notes += "\n" + I18n.T("dbv.loaded", p.Databases.Count, result.Errors);
                    notes += "\n" + I18n.T("dbv.usersNote");
                }
            }
            catch
            {
                // back to the old version; a half-made copy is renamed so the next try starts clean
                try { if (mysql.IsActive) await mysql.StopAsync(); } catch { }
                if (failedCopy != null && Directory.Exists(failedCopy))
                    try { Directory.Move(failedCopy, failedCopy + ".failed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }
                if (p.Move == DbMove.Dump && !string.IsNullOrEmpty(rootPassword)) s.MysqlRootPassword = rootPassword;
                Activate(p.FromEngine, p.FromFolder);
                if (wasActive) await mysql.StartAsync();
                throw;
            }
            finally
            {
                if (dump != null) try { File.Delete(dump); } catch { }
            }
            if (!wasActive) await mysql.StopAsync();
            Log.Info(I18n.T("dbv.done", p.FromLabel, p.ToLabel));
            return I18n.T("dbv.doneText", p.ToLabel, backup, p.FromData) + notes;
        }

        static void Activate(string engine, string folder)
        {
            var s = Settings.Current;
            s.DbEngine = engine;
            s.SetVersion(engine, folder);
            s.Save();
            Log.Info(I18n.T("log.switched", PackageTypes.Get(engine).Name, PackageTypes.Label(folder)));
        }

        /// <summary>User databases of the running server (with routines, events, triggers) into one file.</summary>
        static string DumpUserDatabases()
        {
            var names = Database.ListDatabases();
            var dir = Path.Combine(Paths.Tmp, "import");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "move-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sql");
            if (names.Count == 0) { File.WriteAllText(file, ""); return file; }
            var r = Database.Tool("mysqldump.exe", "--databases " + string.Join(" ", names.Select(n => "\"" + n + "\"")) +
                " --routines --events --triggers --single-transaction --default-character-set=utf8mb4 --result-file=\"" + file + "\"", 6 * 3600 * 1000);
            if (!r.Ok || !File.Exists(file)) throw new Exception(Database.Clean(r));
            return file;
        }

        /// <summary>Copy (never move) a data folder; robocopy keeps large files fast and handles long paths.</summary>
        static void CopyDir(string from, string to)
        {
            if (Directory.Exists(to)) Directory.Delete(to, true);   // only empty folders get here (see PlanFor)
            var r = ProcessUtil.Run("robocopy.exe", "\"" + from + "\" \"" + to + "\" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP", null, null, 24 * 3600 * 1000);
            if (r.ExitCode >= 8 || r.TimedOut) throw new Exception("robocopy: " + r.Output);
            // the marker belongs to the old server; the new one writes its own after the first start
            try { File.Delete(Path.Combine(to, MarkerFile)); } catch { }
        }
    }
}
