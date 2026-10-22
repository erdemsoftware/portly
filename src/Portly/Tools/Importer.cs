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
        public string Name { get { return (Kind == "xampp" ? "XAMPP" : "Laragon") + " (" + Root + ")"; } }
        public override string ToString() { return Name; }
    }

    /// <summary>
    /// Move from Laragon or XAMPP: projects (use the folder in place, or copy it) and databases
    /// (dumped with the old installation's own mysqldump and loaded into Portly's MySQL).
    /// </summary>
    public static class Importer
    {
        static readonly string[] SystemDbs = { "information_schema", "mysql", "performance_schema", "sys", "phpmyadmin" };

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
            }
            return list;
        }

        static ImportSource Laragon(string root)
        {
            var src = new ImportSource { Kind = "laragon", Root = root };
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
            var src = new ImportSource { Kind = "xampp", Root = root, ProjectsDir = Path.Combine(root, "htdocs") };
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
            var targetMajor = PackageTypes.ParseVersion(PackageTypes.ActiveFolder("mysql")).Major;
            var errors = await Task.Run(() => Load(dump, targetMajor), ct);
            try { File.Delete(dump); } catch { }
            Log.Info(I18n.T("import.dbDone", names.Count, errors));
            return I18n.T("import.dbDone", names.Count, errors);
        }

        /// <summary>Stream the dump into mysql.exe, fixing MariaDB-only syntax on the way. Returns the error count.</summary>
        static int Load(string dump, int targetMajor)
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
                    using (var reader = new StreamReader(dump, new UTF8Encoding(false)))
                    using (var writer = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false), 1 << 16))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.StartsWith("/*M!999999")) continue; // MariaDB "sandbox mode" header, unknown to MySQL
                            line = line.Replace("utf8mb4_uca1400_ai_ci", "utf8mb4_unicode_ci").Replace("utf8mb3_uca1400_ai_ci", "utf8mb3_general_ci")
                                       .Replace("utf8mb4_uca1400_as_cs", "utf8mb4_bin");
                            if (targetMajor < 8) line = line.Replace("utf8mb4_0900_ai_ci", "utf8mb4_unicode_ci");
                            writer.Write(line);
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
}
