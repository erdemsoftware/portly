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

        static string ExeOf(string type)
        {
            var dir = PackageTypes.ActiveDir(type);
            return dir == null ? null : Path.Combine(dir, PackageTypes.Get(type).DetectFile);
        }

        public static bool IsInstalled(string type) { return PackageTypes.ActiveDir(type) != null; }

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
                var dir = PackageTypes.ActiveDir("git");
                if (dir == null) return null;
                var exe = Path.Combine(dir, "git-bash.exe");
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
