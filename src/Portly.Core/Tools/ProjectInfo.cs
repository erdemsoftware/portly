using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Portly.Config;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>What a project is built with and which database it uses, read from its files (never any password).</summary>
    public class ProjectInfo
    {
        public string Tech = "";       // "Laravel 12", "WordPress 6.9", "Symfony", "PHP", "HTML", "Next.js" ...
        public string DbEngine = "";   // "MySQL", "PostgreSQL", "SQLite" or ""
        public string DbName = "";     // database name only

        public string DbText { get { return DbEngine == "" ? "" : DbName == "" ? DbEngine : DbEngine + ": " + DbName; } }

        static readonly Dictionary<string, Tuple<DateTime, ProjectInfo>> Cache = new Dictionary<string, Tuple<DateTime, ProjectInfo>>(StringComparer.OrdinalIgnoreCase);
        static readonly string[] StampFiles = { "composer.json", "composer.lock", ".env", "wp-config.php", "package.json", "config.php" };

        /// <summary>Cached until one of the project's key files changes.</summary>
        public static ProjectInfo For(Site site)
        {
            DateTime stamp = DateTime.MinValue;
            foreach (var f in StampFiles)
            {
                try { var p = Path.Combine(site.Folder, f); if (File.Exists(p)) { var t = File.GetLastWriteTimeUtc(p); if (t > stamp) stamp = t; } }
                catch { }
            }
            lock (Cache)
            {
                Tuple<DateTime, ProjectInfo> hit;
                if (Cache.TryGetValue(site.Folder, out hit) && hit.Item1 == stamp) return hit.Item2;
            }
            ProjectInfo info;
            try { info = Detect(site); } catch { info = new ProjectInfo(); }
            lock (Cache) Cache[site.Folder] = Tuple.Create(stamp, info);
            return info;
        }

        static ProjectInfo Detect(Site site)
        {
            var dir = site.Folder;
            var info = new ProjectInfo();
            var composer = Read(Path.Combine(dir, "composer.json"));
            var env = ReadEnv(Path.Combine(dir, ".env"));

            if (File.Exists(Path.Combine(dir, "wp-config.php")) || File.Exists(Path.Combine(dir, "wp-includes", "version.php")))
            {
                var v = Regex.Match(Read(Path.Combine(dir, "wp-includes", "version.php")), @"\$wp_version\s*=\s*'(\d+\.\d+)");
                info.Tech = "WordPress" + (v.Success ? " " + v.Groups[1].Value : "");
                var db = Regex.Match(Read(Path.Combine(dir, "wp-config.php")), @"define\(\s*['""]DB_NAME['""]\s*,\s*['""]([^'""]+)['""]");
                info.DbEngine = "MySQL";
                if (db.Success) info.DbName = db.Groups[1].Value;
                return info;
            }

            if (composer != "")
            {
                if (composer.Contains("\"laravel/framework\"")) info.Tech = "Laravel" + LockMajor(dir, "laravel/framework");
                else if (composer.Contains("\"symfony/framework-bundle\"")) info.Tech = "Symfony" + LockMajor(dir, "symfony/framework-bundle");
                else if (composer.Contains("\"codeigniter4/framework\"")) info.Tech = "CodeIgniter 4";
                else if (composer.Contains("\"cakephp/cakephp\"")) info.Tech = "CakePHP" + LockMajor(dir, "cakephp/cakephp");
                else if (composer.Contains("\"drupal/core")) info.Tech = "Drupal" + LockMajor(dir, "drupal/core");
                else if (composer.Contains("\"yiisoft/yii2\"")) info.Tech = "Yii 2";
                else if (composer.Contains("\"slim/slim\"")) info.Tech = "Slim";
                else if (composer.Contains("\"filament/filament\"")) info.Tech = "Filament";
            }
            if (info.Tech == "" && Sites.IsNodeProject(site)) info.Tech = NodeTech(dir);
            if (info.Tech == "") info.Tech = HasPhp(site) ? "PHP" : HasHtml(site) ? "HTML" : "";

            // database: .env (Laravel, Symfony, many others) first, then the usual PHP config files
            string conn;
            if (env.TryGetValue("DB_CONNECTION", out conn))
            {
                info.DbEngine = EngineName(conn);
                string name;
                if (env.TryGetValue("DB_DATABASE", out name))
                    info.DbName = info.DbEngine == "SQLite" ? Path.GetFileName(name.Replace('/', '\\')) : name;
            }
            else if (env.TryGetValue("DATABASE_URL", out conn))
            {
                // mysql://user:pass@host:3306/dbname?serverVersion=8 -> engine + name; the password is never kept
                var m = Regex.Match(conn, @"^(\w+)://[^/]*/([^?""\s]+)");
                if (m.Success) { info.DbEngine = EngineName(m.Groups[1].Value); info.DbName = m.Groups[2].Value; }
            }
            if (info.DbEngine == "") FromConfigFiles(dir, info);
            return info;
        }

        static string LockMajor(string dir, string package)
        {
            var m = Regex.Match(Read(Path.Combine(dir, "composer.lock")),
                "\"name\":\\s*\"" + Regex.Escape(package) + "\"\\s*,\\s*\"version\":\\s*\"v?(\\d+)");
            return m.Success ? " " + m.Groups[1].Value : "";
        }

        static string NodeTech(string dir)
        {
            var p = Read(Path.Combine(dir, "package.json"));
            if (p.Contains("\"next\"")) return "Next.js";
            if (p.Contains("\"nuxt\"")) return "Nuxt";
            if (p.Contains("\"@angular/core\"")) return "Angular";
            if (p.Contains("\"astro\"")) return "Astro";
            if (p.Contains("\"svelte\"")) return "Svelte";
            if (p.Contains("\"vue\"")) return "Vue";
            if (p.Contains("\"react\"")) return "React";
            return "Node.js";
        }

        static string EngineName(string conn)
        {
            conn = (conn ?? "").ToLowerInvariant();
            if (conn.StartsWith("pg") || conn.Contains("postgres")) return "PostgreSQL";
            if (conn.Contains("sqlite")) return "SQLite";
            if (conn.Contains("sqlsrv")) return "SQL Server";
            if (conn.Contains("mongo")) return "MongoDB";
            return "MySQL";
        }

        /// <summary>Plain PHP projects: a PDO DSN, define('DB_NAME', ...), 'database' => '...' or $dbname = '...'.</summary>
        static void FromConfigFiles(string dir, ProjectInfo info)
        {
            var files = new List<string>();
            foreach (var sub in new[] { "", "config", "app", "app\\Config", "includes", "inc", "core", "src" })
            {
                var d = Path.Combine(dir, sub);
                if (!Directory.Exists(d)) continue;
                try
                {
                    files.AddRange(Directory.EnumerateFiles(d, "*.php")
                        .Where(f => Regex.IsMatch(Path.GetFileName(f), "config|db|database|settings|ayar|baglanti|connect", RegexOptions.IgnoreCase))
                        .Take(20));
                }
                catch { }
            }
            foreach (var f in files.Distinct())
            {
                string text;
                try { if (new FileInfo(f).Length > 200 * 1024) continue; text = File.ReadAllText(f); } catch { continue; }
                var dsn = Regex.Match(text, @"(mysql|pgsql|sqlite)\s*:[^'""]*?dbname\s*=\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
                if (dsn.Success) { info.DbEngine = EngineName(dsn.Groups[1].Value); info.DbName = dsn.Groups[2].Value; return; }
                if (Regex.IsMatch(text, @"['""]sqlite:", RegexOptions.IgnoreCase)) { info.DbEngine = "SQLite"; return; }
                foreach (var rx in new[]
                {
                    @"define\(\s*['""]DB_(?:NAME|DATABASE)['""]\s*,\s*['""]([^'""]+)['""]",
                    @"['""](?:database|db_?name|dbname|DB_DATABASE)['""]\s*=>\s*['""]([^'""]+)['""]",
                    @"\$(?:db_?name|database)\s*=\s*['""]([^'""]+)['""]",
                })
                {
                    var m = Regex.Match(text, rx, RegexOptions.IgnoreCase);
                    if (m.Success) { info.DbEngine = "MySQL"; info.DbName = m.Groups[1].Value; return; }
                }
            }
            // a SQLite file next to the code
            foreach (var sub in new[] { "", "database", "storage", "data", "db" })
            {
                try
                {
                    var d = Path.Combine(dir, sub);
                    if (Directory.Exists(d) && Directory.EnumerateFiles(d).Any(f => f.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase)))
                    { info.DbEngine = "SQLite"; return; }
                }
                catch { }
            }
        }

        static bool HasPhp(Site site)
        {
            try { return Directory.EnumerateFiles(site.DocRoot ?? site.Folder, "*.php").Any() || Directory.EnumerateFiles(site.Folder, "*.php").Any(); }
            catch { return false; }
        }

        static bool HasHtml(Site site)
        {
            try { return File.Exists(Path.Combine(site.DocRoot ?? site.Folder, "index.html")) || File.Exists(Path.Combine(site.DocRoot ?? site.Folder, "index.htm")); }
            catch { return false; }
        }

        static string Read(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length < 2 * 1024 * 1024 ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        /// <summary>KEY=value lines; only the keys this class needs are kept (no secrets are stored).</summary>
        static Dictionary<string, string> ReadEnv(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Read(path).Split('\n'))
            {
                var m = Regex.Match(line, @"^\s*(DB_CONNECTION|DB_DATABASE|DATABASE_URL)\s*=\s*""?([^""#\r]*)""?");
                if (m.Success) d[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
            return d;
        }
    }
}
