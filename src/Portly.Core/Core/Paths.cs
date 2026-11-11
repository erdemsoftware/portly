using System;
using System.IO;

namespace Portly.Core
{
    /// <summary>
    /// Folder layout. Everything lives next to Portly.exe, so the whole folder is portable.
    ///   bin\    packages (bin\php\php-8.5.12-..., bin\mysql\mysql-8.4.11-winx64, ...)
    ///   etc\    generated configuration (rewritten on every start)
    ///   usr\    user area: settings, user templates, SSL authority (never touched by updates)
    ///   data\   databases        www\  projects (default document root)
    ///   tmp\    downloads, PHP sessions        logs\ log files      backup\ database backups
    /// </summary>
    public static class Paths
    {
        /// <summary>Program files: Portly.exe, lang, templates. Read-only on macOS (.app) and Linux (AppImage).</summary>
        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

        /// <summary>
        /// Everything Portly writes. Windows: next to Portly.exe (portable).
        /// macOS / Linux: ~/Portly. The .NET 10 build honours PORTLY_HOME on every system.
        /// </summary>
        public static readonly string Root = DataRoot();

        static string DataRoot()
        {
#if !NETFRAMEWORK
            var env = Environment.GetEnvironmentVariable("PORTLY_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env).TrimEnd('\\', '/');
#endif
            if (OS.IsWindows) return AppDir;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Portly");
        }

        public static string Bin { get { return Path.Combine(Root, "bin"); } }
        public static string Etc { get { return Path.Combine(Root, "etc"); } }
        public static string Usr { get { return Path.Combine(Root, "usr"); } }
        public static string Data { get { return Path.Combine(Root, "data"); } }
        public static string Tmp { get { return Path.Combine(Root, "tmp"); } }
        public static string Logs { get { return Path.Combine(Root, "logs"); } }
        public static string Www { get { return Path.Combine(Root, "www"); } }
        public static string Backup { get { return Path.Combine(Root, "backup"); } }
        public static string Lang { get { return Path.Combine(AppDir, "lang"); } }
        public static string Templates { get { return Path.Combine(AppDir, "templates"); } }
        public static string UserTemplates { get { return Path.Combine(Usr, "templates"); } }
        public static string Cache { get { return Path.Combine(Usr, "cache"); } }
        public static string Downloads { get { return Path.Combine(Tmp, "downloads"); } }
        public static string Sessions { get { return Path.Combine(Tmp, "sessions"); } }
        /// <summary>The only folder SELECT ... INTO OUTFILE and LOAD DATA INFILE may use (secure-file-priv): outside every document root.</summary>
        public static string MysqlFiles { get { return Path.Combine(Tmp, "mysql-files"); } }
        public static string Ssl { get { return Path.Combine(Etc, "ssl"); } }
        public static string UserSsl { get { return Path.Combine(Usr, "ssl"); } }
        public static string Apps { get { return Path.Combine(Etc, "apps"); } }
        public static string SettingsFile { get { return Path.Combine(Usr, "portly.ini"); } }
        public static string SitesFile { get { return Path.Combine(Usr, "sites.ini"); } }
        public static string Exe { get { return Path.Combine(AppDir, OS.Exe("Portly")); } }

        public static string BinOf(string type) { return Path.Combine(Bin, type); }

        public static void EnsureAll()
        {
            foreach (var d in new[] { Bin, Etc, Usr, Data, Tmp, Logs, Www, Backup, Cache, Downloads, Sessions, MysqlFiles, Ssl, UserSsl, UserTemplates, Apps })
                Directory.CreateDirectory(d);
        }

        /// <summary>Forward-slash path for Apache / Nginx / PHP config files.</summary>
        public static string Fwd(string p) { return p.Replace('\\', '/'); }

        public static bool HasSpaceInRoot { get { return Root.IndexOf(' ') >= 0; } }
    }
}
