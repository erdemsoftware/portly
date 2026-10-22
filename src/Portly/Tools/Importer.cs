using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public class ImportSource
    {
        public string Kind;          // laragon | xampp
        public string Root;
        public string ProjectsDir;
        public int ProjectCount;
        public string MysqlBin;      // folder with mysqld.exe, mysql.exe, mysqldump.exe (null = no database)
        public string MysqlIni;      // their my.ini (may be null)
        public string MysqlData;
        public int MysqlPort = 3306;
        public string MysqlVersion = "";
        public string Product = "";   // display name: Laragon, XAMPP, WampServer ...
        public string Name { get { return (Product != "" ? Product : Kind) + " (" + Root + ")"; } }
        public override string ToString() { return Name; }
    }

    /// <summary>
    /// Move from Laragon, XAMPP, WampServer, AMPPS, UwAmp, Uniform Server, Open Server, EasyPHP, MAMP
    /// (and projects from Laravel Herd / Local): projects (use the folder in place, or copy it) and databases
    /// (dumped with the old installation's own mysqldump and loaded into Portly's MySQL).
    /// </summary>
    public static class Importer
    {
        static readonly string[] SystemDbs = { "information_schema", "mysql", "performance_schema", "sys", "phpmyadmin" };

        /// <summary>Known stacks: product name, folder names to look for on every drive, project folders inside.</summary>
        static readonly string[][] Stacks =
        {
            // product, folder patterns (on each drive / Program Files), project folders
            new[] { "WampServer", "wamp64|wamp", "www" },
            new[] { "AMPPS", @"Ampps|Program Files\Ampps|Program Files (x86)\Ampps", "www" },
            new[] { "UwAmp", "UwAmp", "www" },
            new[] { "Uniform Server", "UniServerZ|UniServer", "www" },
            new[] { "Open Server", "OSPanel|OpenServer", "home|domains" },
            new[] { "EasyPHP", "EasyPHP*", "eds-www|www" },
            new[] { "MAMP", "MAMP", "htdocs" },
        };

        public static List<ImportSource> Detect()
        {
            var list = new List<ImportSource>();
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
            foreach (var drive in drives)
            {
                var lr = Path.Combine(drive, "laragon");
                if (File.Exists(Path.Combine(lr, "laragon.exe"))) { var s = Laragon(lr); if (s != null) list.Add(s); }
                var xr = Path.Combine(drive, "xampp");
                if (Directory.Exists(Path.Combine(xr, "htdocs"))) { var s = Xampp(xr); if (s != null) list.Add(s); }
                foreach (var st in Stacks)
                    foreach (var pattern in st[1].Split('|'))
                        foreach (var root in Match(drive, pattern))
                        {
                            if (list.Any(x => string.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase))) continue;
                            var s = Generic(st[0], root, st[2].Split('|'));
                            if (s != null) list.Add(s);
                        }
            }
            // projects only: these keep their databases inside their own app
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var pair in new[] { new[] { "Laravel Herd", Path.Combine(home, "Herd") }, new[] { "Local (Flywheel)", Path.Combine(home, "Local Sites") } })
                if (Directory.Exists(pair[1]) && Count(pair[1]) > 0)
                    list.Add(new ImportSource { Kind = "projects", Product = pair[0], Root = pair[1], ProjectsDir = pair[1], ProjectCount = Count(pair[1]) });
            return list;
        }

        static IEnumerable<string> Match(string drive, string pattern)
        {
            if (!pattern.Contains("*")) { var d = Path.Combine(drive, pattern); if (Directory.Exists(d)) yield return d; yield break; }
            string[] found;
            try { found = Directory.GetDirectories(drive, pattern, SearchOption.TopDirectoryOnly); } catch { yield break; }
            foreach (var d in found) yield return d;
        }

        /// <summary>Any other AMP stack: find the projects folder and the newest mysqld.exe below the root.</summary>
        static ImportSource Generic(string product, string root, string[] projectDirs)
        {
            var projects = projectDirs.Select(d => Path.Combine(root, d)).FirstOrDefault(Directory.Exists);
            if (projects == null) return null;
            var src = new ImportSource { Kind = "generic", Product = product, Root = root, ProjectsDir = projects, ProjectCount = Count(projects) };
            var mysqld = FindFiles(root, "mysqld.exe", 6).OrderByDescending(f => PackageTypes.ParseVersion(f)).FirstOrDefault();
            if (mysqld != null)
            {
                src.MysqlBin = Path.GetDirectoryName(mysqld);
                var home = Path.GetDirectoryName(src.MysqlBin);
                src.MysqlIni = new[] { Path.Combine(src.MysqlBin, "my.ini"), Path.Combine(home, "my.ini"), Path.Combine(root, "my.ini") }.FirstOrDefault(File.Exists);
                ReadMyIni(src);
                if (src.MysqlData == null || !Directory.Exists(src.MysqlData))
                    src.MysqlData = new[] { Path.Combine(home, "data"), Path.Combine(root, @"mysql\data"), Path.Combine(root, "data") }
                        .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "mysql")));
                if (src.MysqlData == null) src.MysqlBin = null;
                else
                {
                    var r = ProcessUtil.Run(mysqld, "--version", src.MysqlBin, null, 15000);
                    var m = Regex.Match(r.Output, @"Ver\s+([\d.]+)(-MariaDB)?", RegexOptions.IgnoreCase);
                    src.MysqlVersion = m.Success ? m.Groups[1].Value + (m.Groups[2].Success ? " MariaDB" : "") : PackageTypes.Label(home);
                }
            }
            return src;
        }

        static IEnumerable<string> FindFiles(string dir, string name, int depth)
        {
            var result = new List<string>();
            try
            {
                var f = Path.Combine(dir, name);
                if (File.Exists(f)) result.Add(f);
                if (depth <= 0) return result;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var n = Path.GetFileName(sub).ToLowerInvariant();
                    if (n == "www" || n == "htdocs" || n == "home" || n == "domains" || n == "node_modules" || n == "data" || n == "tmp" || n == "logs") continue;
                    result.AddRange(FindFiles(sub, name, depth - 1));
                }
            }
            catch { }
            return result;
        }

        static ImportSource Laragon(string root)
        {
            var src = new ImportSource { Kind = "laragon", Product = "Laragon", Root = root };
            var ini = new Ini(Path.Combine(root, @"usr\laragon.ini"));
            var docRoot = ini.Get("apache", "DocumentRoot", "");
            if (docRoot == "") docRoot = ini.Get("nginx", "DocumentRoot", "");
            src.ProjectsDir = Directory.Exists(docRoot) ? docRoot : Path.Combine(root, "www");
            // active MySQL version: usr\profile\default.ini (new Laragon) or laragon.ini (old)
            var ver = new Ini(Path.Combine(root, @"usr\profile\default.ini")).Get("mysql", "Version", "");
            if (ver == "") ver = ini.Get("mysql", "Version", "");
            var mysqlRoot = Path.Combine(root, @"bin\mysql");
            string dir = ver != "" && Directory.Exists(Path.Combine(mysqlRoot, ver)) ? Path.Combine(mysqlRoot, ver)
                : Directory.Exists(mysqlRoot) ? Directory.GetDirectories(mysqlRoot).Where(d => File.Exists(Path.Combine(d, @"bin\mysqld.exe"))).OrderByDescending(d => PackageTypes.ParseVersion(d)).FirstOrDefault() : null;
            if (dir != null)
            {
                src.MysqlBin = Path.Combine(dir, "bin");
                src.MysqlIni = File.Exists(Path.Combine(dir, "my.ini")) ? Path.Combine(dir, "my.ini") : null;
                src.MysqlVersion = PackageTypes.Label(Path.GetFileName(dir));
                ReadMyIni(src);
                if (src.MysqlData == null)
                {
                    var dataRoot = ini.Get("preferences", "DataDir", Path.Combine(root, "data"));
                    var v = PackageTypes.ParseVersion(dir);
                    var guess = Path.Combine(dataRoot, "mysql-" + v.Major + "." + v.Minor);
                    src.MysqlData = Directory.Exists(guess) ? guess : Path.Combine(dataRoot, "mysql-" + v.Major);
                }
            }
            src.ProjectCount = Count(src.ProjectsDir);
            return src;
        }

        static ImportSource Xampp(string root)
        {
            var src = new ImportSource { Kind = "xampp", Product = "XAMPP", Root = root, ProjectsDir = Path.Combine(root, "htdocs") };
            var bin = Path.Combine(root, @"mysql\bin");
            if (File.Exists(Path.Combine(bin, "mysqld.exe")))
            {
                src.MysqlBin = bin;
                src.MysqlIni = File.Exists(Path.Combine(bin, "my.ini")) ? Path.Combine(bin, "my.ini") : null;
                src.MysqlData = Path.Combine(root, @"mysql\data");
                ReadMyIni(src);
                var r = ProcessUtil.Run(Path.Combine(bin, "mysqld.exe"), "--version", bin, null, 15000);
                var m = Regex.Match(r.Output, @"Ver\s+([\d.]+)(-MariaDB)?", RegexOptions.IgnoreCase);
                if (m.Success) src.MysqlVersion = m.Groups[1].Value + (m.Groups[2].Success ? " MariaDB" : "");
            }
            src.ProjectCount = Count(src.ProjectsDir);
            return src;
        }

        static void ReadMyIni(ImportSource src)
        {
            if (src.MysqlIni == null) return;
            var ini = new Ini(src.MysqlIni);
            var data = ini.Get("mysqld", "datadir", "").Trim('"');
            if (data != "") src.MysqlData = Path.GetFullPath(Path.IsPathRooted(data) ? data : Path.Combine(Path.GetDirectoryName(src.MysqlIni), data));
            int port;
            if (int.TryParse(ini.Get("mysqld", "port", ""), out port)) src.MysqlPort = port;
        }

        static int Count(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir).Length : 0; } catch { return 0; }
        }

        // =====================================================================
        // Projects
        // =====================================================================
        /// <summary>Use the old projects folder as Portly's projects folder (nothing is copied).</summary>
        public static void UseProjectsInPlace(ImportSource src)
        {
            Settings.Current.DocumentRoot = src.ProjectsDir;
            Settings.Current.Save();
            Log.Info(I18n.T("import.projectsLinked", src.ProjectsDir));
        }

        /// <summary>Copy every project folder into Portly's projects folder (existing folders are skipped).</summary>
        public static int CopyProjects(ImportSource src, Action<string> status, CancellationToken ct)
        {
            var target = Settings.Current.DocumentRoot;
            if (string.Equals(Path.GetFullPath(target).TrimEnd('\\'), Path.GetFullPath(src.ProjectsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 0;
            int copied = 0;
            foreach (var dir in Directory.GetDirectories(src.ProjectsDir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                var dest = Path.Combine(target, name);
                if (Directory.Exists(dest)) { Log.Warn(I18n.T("import.skipExists", name)); continue; }
                status(I18n.T("import.copying", name));
                // robocopy is on every Windows since Vista and keeps attributes and long paths
                var r = ProcessUtil.Run("robocopy.exe", "\"" + dir + "\" \"" + dest + "\" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP", null, null, 24 * 3600 * 1000);
                if (r.ExitCode >= 8) Log.Warn("robocopy " + name + ": " + r.Output);
                else copied++;
            }
            Log.Info(I18n.T("import.projectsCopied", copied, target));
            return copied;
        }

        // =====================================================================
        // Databases
        // =====================================================================
        class Source : IDisposable
        {
            public int Port;
            public Process Temp;
            public string Cnf;
            public void Dispose()
            {
                try { if (Cnf != null) File.Delete(Cnf); } catch { }
                if (Temp != null)
                {
                    try { if (!Temp.HasExited) { ProcessUtil.KillTree(Temp.Id); Temp.WaitForExit(10000); } } catch { }
                }
            }
        }

        static string WriteCnf(string password, int port)
        {
            var f = Path.Combine(Paths.Tmp, "import-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cnf");
            File.WriteAllText(f, "[client]\r\nuser=root\r\npassword=\"" + (password ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\r\nhost=127.0.0.1\r\nport=" + port + "\r\n", new UTF8Encoding(false));
            return f;
        }

        /// <summary>Connect to the old server, or start it temporarily on a free port when it is not running.</summary>
        static async Task<Source> OpenSource(ImportSource src, string password, Action<string> status)
        {
            var s = new Source();
            var owner = ProcessUtil.IsPortInUse(src.MysqlPort) ? ProcessUtil.PortOwnerInfo(src.MysqlPort) : null;
            if (owner != null && owner.Path.StartsWith(src.Root, StringComparison.OrdinalIgnoreCase))
            {
                s.Port = src.MysqlPort;
            }
            else
            {
                status(I18n.T("import.startingSource", src.MysqlVersion));
                s.Port = ProcessUtil.FreePort(Enumerable.Range(3390, 40), new HashSet<int>());
                var args = (src.MysqlIni != null ? "--defaults-file=\"" + src.MysqlIni + "\"" : "--no-defaults") +
                           " --datadir=\"" + src.MysqlData + "\" --port=" + s.Port + " --bind-address=127.0.0.1 --console";
                var psi = new ProcessStartInfo(Path.Combine(src.MysqlBin, "mysqld.exe"), args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                    WorkingDirectory = src.MysqlBin,
                };
                s.Temp = Process.Start(psi);
                JobObject.Add(s.Temp);
                s.Temp.BeginErrorReadLine();
                s.Temp.BeginOutputReadLine();
                var until = DateTime.Now.AddSeconds(60);
                while (DateTime.Now < until && !s.Temp.HasExited && !ProcessUtil.IsPortInUse(s.Port)) await Task.Delay(300);
                if (!ProcessUtil.IsPortInUse(s.Port)) { s.Dispose(); throw new Exception(I18n.T("import.sourceFailed")); }
            }
            s.Cnf = WriteCnf(password, s.Port);
            return s;
        }

        public class DbInfo { public string Name; public long Bytes; public override string ToString() { return Name; } }

        /// <summary>User databases of the old server with their size. Throws "access denied" when the password is wrong.</summary>
        public static async Task<List<DbInfo>> ListDatabasesAsync(ImportSource src, string password, Action<string> status)
        {
            using (var s = await OpenSource(src, password, status))
            {
                var sql = "SELECT s.schema_name, COALESCE(SUM(t.data_length + t.index_length), 0) FROM information_schema.schemata s " +
                          "LEFT JOIN information_schema.tables t ON t.table_schema = s.schema_name GROUP BY s.schema_name;";
                var r = ProcessUtil.Run(Path.Combine(src.MysqlBin, "mysql.exe"), "--defaults-extra-file=\"" + s.Cnf + "\" --batch --skip-column-names", src.MysqlBin, null, 60000, sql);
                if (!r.Ok) throw new Exception(Clean(r.Output));
                var list = new List<DbInfo>();
                foreach (var line in r.Output.Split('\n'))
                {
                    var parts = line.Trim().Split('\t');
                    if (parts.Length < 2 || SystemDbs.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) continue;
                    long b; long.TryParse(parts[1], out b);
                    list.Add(new DbInfo { Name = parts[0], Bytes = b });
                }
                return list.OrderBy(d => d.Name).ToList();
            }
        }

        /// <summary>Dump the chosen databases from the old server and load them into Portly's MySQL.</summary>
        public static async Task<string> ImportDatabasesAsync(ImportSource src, string password, List<string> names, Action<string> status, CancellationToken ct)
        {
            if (names.Count == 0) return "";
            var dumpDir = Path.Combine(Paths.Tmp, "import");
            Directory.CreateDirectory(dumpDir);
            var dump = Path.Combine(dumpDir, "dump-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sql");
            using (var s = await OpenSource(src, password, status))
            {
                status(I18n.T("import.dumping", names.Count));
                var args = "--defaults-extra-file=\"" + s.Cnf + "\" --databases " + string.Join(" ", names.Select(n => "\"" + n + "\"")) +
                           " --routines --events --triggers --single-transaction --default-character-set=utf8mb4 --result-file=\"" + dump + "\"";
                var r = await Task.Run(() => ProcessUtil.Run(Path.Combine(src.MysqlBin, "mysqldump.exe"), args, src.MysqlBin, null, 6 * 3600 * 1000), ct);
                if (!r.Ok || !File.Exists(dump)) throw new Exception(Clean(r.Output));
            }

            if (ServiceManager.MySql.State != ServiceState.Running)
            {
                status(I18n.T("import.startingPortly"));
                await ServiceManager.MySql.StartAsync();
                if (ServiceManager.MySql.State != ServiceState.Running) throw new Exception(I18n.T("err.mysqlNotRunning"));
            }
            status(I18n.T("import.loading"));
            var errors = await Task.Run(() => Load(dump), ct);
            try { File.Delete(dump); } catch { }
            Log.Info(I18n.T("import.dbDone", names.Count, errors));
            return I18n.T("import.dbDone", names.Count, errors);
        }

        /// <summary>Stream the dump into mysql.exe, fixing MariaDB-only syntax on the way. Returns the error count.</summary>
        static int Load(string dump)
        {
            var bin = MySqlService.BinDir;
            var cnf = WriteCnf(Settings.Current.MysqlRootPassword, Settings.Current.MysqlPort);
            int errors = 0;
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(bin, "mysql.exe"), "--defaults-extra-file=\"" + cnf + "\" --default-character-set=utf8mb4 --force")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
                    WorkingDirectory = bin,
                };
                using (var p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (o, e) => { if (!string.IsNullOrEmpty(e.Data) && e.Data.StartsWith("ERROR")) { errors++; Log.Warn("import: " + e.Data); } };
                    p.OutputDataReceived += (o, e) => { };
                    p.BeginErrorReadLine();
                    p.BeginOutputReadLine();
                    var filter = DumpFilter.ForActive();
                    using (var reader = new StreamReader(dump, new UTF8Encoding(false)))
                    using (var writer = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false), 1 << 16))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            writer.Write(filter.Apply(line));
                            writer.Write('\n');
                        }
                    }
                    p.WaitForExit();
                }
            }
            finally { try { File.Delete(cnf); } catch { } }
            return errors;
        }

        static string Clean(string output)
        {
            return string.Join("\n", (output ?? "").Split('\n').Select(l => l.Trim()).Where(l => l != "" && !l.Contains("[Warning]") && !l.Contains("Using a password")));
        }
    }

    /// <summary>
    /// Line filter for loading a dump into the active server: fixes MariaDB-only syntax for MySQL and MySQL-only syntax
    /// for MariaDB. Skipped lines become empty lines so mysql's "ERROR ... at line N" still points into the original file.
    /// </summary>
    public class DumpFilter
    {
        readonly bool _toMaria;
        readonly Version _ver;
        bool _skipping;
        static readonly Regex MySql8Comment = new Regex(@"/\*!80\d{3}\s[^*]*\*/");

        public DumpFilter(bool targetMariaDb, Version targetVersion) { _toMaria = targetMariaDb; _ver = targetVersion; }

        public static DumpFilter ForActive()
        {
            var engine = Settings.Current.DbEngine;
            return new DumpFilter(engine == "mariadb", PackageTypes.ParseVersion(PackageTypes.ActiveFolder(engine)));
        }

        public string Apply(string line)
        {
            if (_skipping)
            {
                if (line.TrimEnd().EndsWith(";")) _skipping = false;
                return "";
            }
            if (line.StartsWith("/*M!999999")) return "";   // MariaDB "sandbox mode" header, unknown to MySQL and old clients
            // GTID sets of the source server fail on a local server without GTID mode (may span lines)
            if (line.StartsWith("SET @@GLOBAL.GTID_PURGED", StringComparison.OrdinalIgnoreCase))
            {
                _skipping = !line.TrimEnd().EndsWith(";");
                return "";
            }
            bool uca1400 = _toMaria && _ver >= new Version(10, 10);
            if (!uca1400 && line.Contains("uca1400"))
                line = line.Replace("utf8mb4_uca1400_turkish_ai_ci", "utf8mb4_turkish_ci").Replace("utf8mb4_uca1400_ai_ci", "utf8mb4_unicode_ci")
                           .Replace("utf8mb3_uca1400_ai_ci", "utf8mb3_general_ci").Replace("utf8mb4_uca1400_as_cs", "utf8mb4_bin");
            // MariaDB and MySQL 5.x do not know the MySQL 8 collations
            if ((_toMaria || _ver.Major < 8) && line.Contains("_0900_"))
                line = line.Replace("utf8mb4_tr_0900_ai_ci", "utf8mb4_turkish_ci").Replace("utf8mb4_0900_ai_ci", "utf8mb4_unicode_ci")
                           .Replace("utf8mb4_0900_as_cs", "utf8mb4_bin").Replace("utf8mb4_0900_bin", "utf8mb4_bin");
            // MariaDB runs /*!80016 ... */ comments (its version number is higher) and fails on DEFAULT ENCRYPTION etc.
            if (_toMaria && line.Contains("/*!80")) line = MySql8Comment.Replace(line, "");
            return line;
        }
    }
}
