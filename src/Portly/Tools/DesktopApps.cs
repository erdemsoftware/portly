using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>Desktop programs installed as packages (database GUIs, editors, Git Bash).</summary>
    public static class DesktopApps
    {
        public static readonly string[] DatabaseGuis = { "heidisql", "dbeaver", "beekeeper", "dbgate", "sqlitebrowser" };
        public static readonly string[] Editors = { "vscode", "notepadpp" };

        /// <summary>Where the usual Windows installers put these programs (per user and for all users).</summary>
        static readonly Dictionary<string, string[]> SystemCandidates = new Dictionary<string, string[]>
        {
            { "vscode",        new[] { @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe", @"%ProgramFiles%\Microsoft VS Code\Code.exe" } },
            { "notepadpp",     new[] { @"%ProgramFiles%\Notepad++\notepad++.exe", @"%ProgramFiles(x86)%\Notepad++\notepad++.exe" } },
            { "git",           new[] { @"%ProgramFiles%\Git\cmd\git.exe", @"%LOCALAPPDATA%\Programs\Git\cmd\git.exe" } },
            { "heidisql",      new[] { @"%ProgramFiles%\HeidiSQL\heidisql.exe", @"%ProgramFiles(x86)%\HeidiSQL\heidisql.exe" } },
            { "dbeaver",       new[] { @"%ProgramFiles%\DBeaver\dbeaver.exe", @"%LOCALAPPDATA%\DBeaver\dbeaver.exe", @"%LOCALAPPDATA%\Programs\DBeaver\dbeaver.exe" } },
            { "beekeeper",     new[] { @"%LOCALAPPDATA%\Programs\beekeeper-studio\Beekeeper Studio.exe", @"%ProgramFiles%\Beekeeper Studio\Beekeeper Studio.exe" } },
            { "dbgate",        new[] { @"%LOCALAPPDATA%\Programs\dbgate\DbGate.exe", @"%ProgramFiles%\DbGate\DbGate.exe" } },
            { "sqlitebrowser", new[] { @"%ProgramFiles%\DB Browser for SQLite\DB Browser for SQLite.exe" } },
        };

        static readonly Dictionary<string, string> SystemCache = new Dictionary<string, string>();

        /// <summary>
        /// The program installed on Windows the normal way (not as a Portly package), or null.
        /// Checked once per run: common install folders, then the "App Paths" registry entries.
        /// </summary>
        public static string SystemExe(string type)
        {
            lock (SystemCache)
            {
                string found;
                if (SystemCache.TryGetValue(type, out found)) return found;
                string[] list;
                if (SystemCandidates.TryGetValue(type, out list))
                {
                    found = list.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);
                    if (found == null) found = AppPath(Path.GetFileName(list[0]));
                }
                SystemCache[type] = found;
                return found;
            }
        }

        static string AppPath(string exe)
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                    {
                        var v = k == null ? null : (k.GetValue("") as string ?? "").Trim('"');
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>Portly's own package first, otherwise the copy installed on Windows.</summary>
        static string ExeOf(string type)
        {
            var dir = PackageTypes.ActiveDir(type);
            return dir != null ? Path.Combine(dir, PackageTypes.Get(type).DetectFile) : SystemExe(type);
        }

        public static bool IsInstalled(string type) { return ExeOf(type) != null; }

        /// <summary>pgAdmin 4 ships inside the PostgreSQL package.</summary>
        public static string PgAdmin
        {
            get
            {
                var dir = PackageTypes.ActiveDir("postgresql");
                if (dir == null) return null;
                var exe = Path.Combine(dir, "pgAdmin 4", "runtime", "pgAdmin4.exe");
                return File.Exists(exe) ? exe : null;
            }
        }

        public static string GitBash
        {
            get
            {
                var git = ExeOf("git");   // ...\Git\cmd\git.exe
                if (git == null) return null;
                var exe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(git)), "git-bash.exe");
                return File.Exists(exe) ? exe : null;
            }
        }

        /// <summary>Open a database GUI already pointed at Portly's MySQL / MariaDB when it supports that.</summary>
        public static void OpenDatabaseGui(string type)
        {
            var exe = ExeOf(type);
            if (exe == null) return;
            var s = Settings.Current;
            var pass = s.MysqlRootPassword;
            string args = "";
            switch (type)
            {
                case "heidisql":
                    // -n=0: MySQL/MariaDB over TCP
                    args = "-n=0 -h=127.0.0.1 -P=" + s.MysqlPort + " -u=root" + (pass != "" ? " -p=\"" + pass.Replace("\"", "") + "\"" : "");
                    break;
                case "dbeaver":
                    var driver = MySqlService.IsMariaDb ? "mariadb" : "mysql";
                    args = "-con \"driver=" + driver + "|host=127.0.0.1|port=" + s.MysqlPort + "|user=root|password=" + pass.Replace("|", "").Replace("\"", "") +
                           "|name=Portly " + PackageTypes.Get(s.DbEngine).Name + "|save=true|connect=true\"";
                    break;
            }
            Start(exe, args);
        }

        public static void Start(string exe, string args = "", string workDir = null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, args ?? "") { UseShellExecute = false, WorkingDirectory = workDir ?? Path.GetDirectoryName(exe) });
            }
            catch (Exception ex) { Log.Error(Path.GetFileName(exe), ex); }
        }

        /// <summary>Open a folder in VS Code or Notepad++ (falls back to Explorer).</summary>
        public static void OpenInEditor(string type, string folder)
        {
            var exe = ExeOf(type);
            if (exe == null) { WinUtil.OpenFolder(folder); return; }
            Start(exe, type == "notepadpp" ? "-openFoldersAsWorkspace \"" + folder + "\"" : "\"" + folder + "\"", folder);
        }
    }
}
