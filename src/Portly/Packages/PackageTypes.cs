using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Portly.Core;

namespace Portly.Packages
{
    public class PackageType
    {
        public string Id;
        public string Name;
        /// <summary>Group in the packages window: web, php, db, dbtool, cache, mail, runtime, dev, share.</summary>
        public string Category = "";
        /// <summary>File that proves a version folder is a valid installation (relative to the folder).</summary>
        public string DetectFile;
        /// <summary>Folders added to PATH for the terminal (relative to the version folder).</summary>
        public string[] PathDirs = new[] { "" };
    }

    /// <summary>Package kinds Portly knows how to install and use.</summary>
    public static class PackageTypes
    {
        /// <summary>Category order in the packages window (title key: "pkgcat." + id).</summary>
        public static readonly string[] Categories = { "web", "php", "db", "dbtool", "cache", "mail", "runtime", "dev", "share" };

        /// <summary>Types grouped by category, in display order.</summary>
        public static IEnumerable<PackageType> Ordered
        {
            get { return Categories.SelectMany(c => All.Where(t => t.Category == c)).Concat(All.Where(t => !Categories.Contains(t.Category))); }
        }

        public static readonly List<PackageType> All = new List<PackageType>
        {
            new PackageType { Id = "apache", Category = "web",     Name = "Apache",     DetectFile = @"bin\httpd.exe",   PathDirs = new[] { "bin" } },
            new PackageType { Id = "nginx", Category = "web",      Name = "Nginx",      DetectFile = "nginx.exe" },
            new PackageType { Id = "php", Category = "php",        Name = "PHP",        DetectFile = "php-cgi.exe" },
            new PackageType { Id = "mysql", Category = "db",      Name = "MySQL",      DetectFile = @"bin\mysqld.exe",  PathDirs = new[] { "bin" } },
            new PackageType { Id = "nodejs", Category = "runtime",     Name = "Node.js",    DetectFile = "node.exe" },
            new PackageType { Id = "redis", Category = "cache",      Name = "Redis",      DetectFile = "redis-server.exe" },
            new PackageType { Id = "mailpit", Category = "mail",    Name = "Mailpit",    DetectFile = "mailpit.exe" },
            new PackageType { Id = "mailhog", Category = "mail",    Name = "MailHog",    DetectFile = "MailHog.exe",      PathDirs = new string[0] },
            new PackageType { Id = "smtp4dev", Category = "mail",   Name = "smtp4dev",   DetectFile = "Rnwood.Smtp4dev.exe", PathDirs = new string[0] },
            new PackageType { Id = "cloudflared", Category = "share", Name = "Cloudflare Tunnel", DetectFile = "cloudflared.exe", PathDirs = new string[0] },
            // more databases and caches
            new PackageType { Id = "mariadb", Category = "db",    Name = "MariaDB",    DetectFile = @"bin\mysqld.exe",  PathDirs = new[] { "bin" } },
            new PackageType { Id = "postgresql", Category = "db", Name = "PostgreSQL", DetectFile = @"bin\postgres.exe", PathDirs = new[] { "bin" } },
            new PackageType { Id = "mongodb", Category = "db",    Name = "MongoDB",    DetectFile = @"bin\mongod.exe",  PathDirs = new[] { "bin" } },
            new PackageType { Id = "memcached", Category = "cache",  Name = "Memcached",  DetectFile = @"bin\memcached.exe", PathDirs = new string[0] },
            // database GUIs
            new PackageType { Id = "phpmyadmin", Category = "dbtool", Name = "phpMyAdmin", DetectFile = "index.php",        PathDirs = new string[0] },
            new PackageType { Id = "adminer", Category = "dbtool",    Name = "Adminer",    DetectFile = "index.php",        PathDirs = new string[0] },
            new PackageType { Id = "heidisql", Category = "dbtool",   Name = "HeidiSQL",   DetectFile = "heidisql.exe",     PathDirs = new string[0] },
            new PackageType { Id = "dbeaver", Category = "dbtool",    Name = "DBeaver",    DetectFile = "dbeaver.exe",      PathDirs = new string[0] },
            new PackageType { Id = "beekeeper", Category = "dbtool",  Name = "Beekeeper Studio", DetectFile = "Beekeeper-Studio.exe", PathDirs = new string[0] },
            new PackageType { Id = "dbgate", Category = "dbtool",     Name = "DbGate",     DetectFile = "DbGate.exe",       PathDirs = new string[0] },
            new PackageType { Id = "sqlite", Category = "db",     Name = "SQLite tools", DetectFile = "sqlite3.exe" },
            new PackageType { Id = "sqlitebrowser", Category = "dbtool", Name = "DB Browser for SQLite", DetectFile = "DB Browser for SQLite.exe", PathDirs = new string[0] },
            // developer tools and runtimes
            new PackageType { Id = "git", Category = "dev",        Name = "Git",        DetectFile = @"cmd\git.exe",     PathDirs = new[] { "cmd" } },
            new PackageType { Id = "vscode", Category = "dev",     Name = "VS Code",    DetectFile = "Code.exe",         PathDirs = new[] { "bin" } },
            new PackageType { Id = "notepadpp", Category = "dev",  Name = "Notepad++",  DetectFile = "notepad++.exe",    PathDirs = new string[0] },
            new PackageType { Id = "python", Category = "runtime",     Name = "Python",     DetectFile = "python.exe" },
            new PackageType { Id = "go", Category = "runtime",         Name = "Go",         DetectFile = @"bin\go.exe",      PathDirs = new[] { "bin" } },
            new PackageType { Id = "bun", Category = "runtime",        Name = "Bun",        DetectFile = "bun.exe" },
            new PackageType { Id = "deno", Category = "runtime",       Name = "Deno",       DetectFile = "deno.exe" },
            new PackageType { Id = "wpcli", Category = "php",      Name = "WP-CLI",     DetectFile = "wp-cli.phar" },
            new PackageType { Id = "composer", Category = "php",   Name = "Composer",   DetectFile = "composer.phar" },
        };

        public static PackageType Get(string id)
        {
            return All.FirstOrDefault(t => t.Id == id) ?? new PackageType { Id = id, Name = id, DetectFile = "" };
        }

        /// <summary>Installed version folders of a type, newest first.</summary>
        public static List<string> Installed(string type)
        {
            var dir = Paths.BinOf(type);
            var t = Get(type);
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .Where(f => !f.StartsWith(".") && (t.DetectFile == "" || File.Exists(Path.Combine(dir, f, t.DetectFile))))
                .OrderByDescending(ParseVersion)
                .ThenByDescending(f => f)
                .ToList();
        }

        /// <summary>The active version folder name ("" when nothing is installed).</summary>
        public static string ActiveFolder(string type)
        {
            var installed = Installed(type);
            var chosen = Settings.Current.GetVersion(type);
            if (chosen != "" && installed.Contains(chosen, StringComparer.OrdinalIgnoreCase)) return chosen;
            return installed.FirstOrDefault() ?? "";
        }

        /// <summary>Full path of the active version, or null.</summary>
        public static string ActiveDir(string type)
        {
            var f = ActiveFolder(type);
            return f == "" ? null : Path.Combine(Paths.BinOf(type), f);
        }

        static readonly Regex VersionRx = new Regex(@"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");

        public static Version ParseVersion(string text)
        {
            var m = VersionRx.Match(text ?? "");
            if (!m.Success) return new Version(0, 0);
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            int c = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            int d = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
            return new Version(a, b, c, d);
        }

        /// <summary>"8.4" for "php-8.4.27-nts-Win32-vs17-x64".</summary>
        public static string Branch(string text)
        {
            var v = ParseVersion(text);
            return v.Major + "." + v.Minor;
        }

        /// <summary>Short label for the UI, e.g. "8.5.12" or "8.4.11".</summary>
        public static string Label(string folder)
        {
            var m = VersionRx.Match(folder ?? "");
            return m.Success ? m.Value : folder;
        }
    }
}
