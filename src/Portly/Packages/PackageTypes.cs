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
        /// <summary>File that proves a version folder is a valid installation (relative to the folder).</summary>
        public string DetectFile;
        /// <summary>Folders added to PATH for the terminal (relative to the version folder).</summary>
        public string[] PathDirs = new[] { "" };
    }

    /// <summary>Package kinds Portly knows how to install and use.</summary>
    public static class PackageTypes
    {
        public static readonly List<PackageType> All = new List<PackageType>
        {
            new PackageType { Id = "apache",     Name = "Apache",     DetectFile = @"bin\httpd.exe",   PathDirs = new[] { "bin" } },
            new PackageType { Id = "nginx",      Name = "Nginx",      DetectFile = "nginx.exe" },
            new PackageType { Id = "php",        Name = "PHP",        DetectFile = "php-cgi.exe" },
            new PackageType { Id = "mysql",      Name = "MySQL",      DetectFile = @"bin\mysqld.exe",  PathDirs = new[] { "bin" } },
            new PackageType { Id = "nodejs",     Name = "Node.js",    DetectFile = "node.exe" },
            new PackageType { Id = "redis",      Name = "Redis",      DetectFile = "redis-server.exe" },
            new PackageType { Id = "mailpit",    Name = "Mailpit",    DetectFile = "mailpit.exe" },
            new PackageType { Id = "mailhog",    Name = "MailHog",    DetectFile = "MailHog.exe",      PathDirs = new string[0] },
            new PackageType { Id = "smtp4dev",   Name = "smtp4dev",   DetectFile = "Rnwood.Smtp4dev.exe", PathDirs = new string[0] },
            new PackageType { Id = "cloudflared", Name = "Cloudflare Tunnel", DetectFile = "cloudflared.exe", PathDirs = new string[0] },
            new PackageType { Id = "composer",   Name = "Composer",   DetectFile = "composer.phar" },
            new PackageType { Id = "adminer",    Name = "Adminer",    DetectFile = "index.php",        PathDirs = new string[0] },
            new PackageType { Id = "phpmyadmin", Name = "phpMyAdmin", DetectFile = "index.php",        PathDirs = new string[0] },
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
