using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Portly.Core;
using Portly.Packages;

namespace Portly.Config
{
    /// <summary>
    /// Creates and maintains php.ini for every PHP version.
    /// Paths are rewritten on every start (so the Portly folder can be moved),
    /// security settings are always enforced, everything else is set once and then left to the user.
    /// </summary>
    public static class PhpConfig
    {
        static readonly string[] Extensions =
        {
            "bz2", "curl", "fileinfo", "gd", "gd2", "gettext", "intl", "mbstring", "exif", "mysqli",
            "openssl", "pdo_mysql", "pdo_sqlite", "sodium", "sqlite3", "soap", "sockets", "xsl", "zip"
        };

        public static string IniPath(string phpDir) { return Path.Combine(phpDir, "php.ini"); }

        public static void Ensure(string phpDir)
        {
            var ini = IniPath(phpDir);
            bool fresh = !File.Exists(ini);
            if (fresh)
            {
                var src = new[] { "php.ini-development", "php.ini-production" }.Select(f => Path.Combine(phpDir, f)).FirstOrDefault(File.Exists);
                if (src == null) File.WriteAllText(ini, "[PHP]\r\n");
                else File.Copy(src, ini);
            }
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            var before = string.Join("\n", lines);

            // ---- paths (always, keeps the folder portable) ----
            Set(lines, "extension_dir", Q(Paths.Fwd(Path.Combine(phpDir, "ext"))));
            Set(lines, "error_log", Q(Paths.Fwd(Path.Combine(Paths.Logs, "php_errors.log"))));
            Set(lines, "session.save_path", Q(Paths.Fwd(Paths.Sessions)));
            Set(lines, "upload_tmp_dir", Q(Paths.Fwd(Paths.Tmp)));
            Set(lines, "curl.cainfo", Q(Paths.Fwd(Certs.BundleFile)));
            Set(lines, "openssl.cafile", Q(Paths.Fwd(Certs.BundleFile)));
            // mail(): PHP's built-in SMTP client on Windows talks straight to Mailpit (no helper process).
            Set(lines, "SMTP", "127.0.0.1");
            Set(lines, "smtp_port", Settings.Current.MailpitSmtpPort.ToString());
            if (Get(lines, "sendmail_from") == "") Set(lines, "sendmail_from", "\"portly@localhost\"");
            // a sendmail_path written by older Portly / Laragon (mailpit.exe sendmail) would override SMTP
            for (int i = 0; i < lines.Count; i++)
                if (Regex.IsMatch(lines[i], @"^\s*sendmail_path\s*=.*mailpit", RegexOptions.IgnoreCase)) lines[i] = ";" + lines[i];

            // ---- OPcache on Windows: several php-cgi workers cannot share memory at the same address (ASLR).
            //      A file cache lets every worker fall back safely (PHP 8.5 refuses to start otherwise). ----
            var opcacheDir = Path.Combine(Paths.Tmp, "opcache");
            Directory.CreateDirectory(opcacheDir);
            Set(lines, "opcache.file_cache", Q(Paths.Fwd(opcacheDir)));
            Set(lines, "opcache.file_cache_fallback", "1");

            // ---- security (always) ----
            // session.upload_progress + a dynamic include can lead to remote code execution: keep it off.
            Set(lines, "session.upload_progress.enabled", "Off");
            Set(lines, "allow_url_include", "Off");
            Set(lines, "expose_php", "Off");

            // ---- comfortable defaults (only for a brand new php.ini) ----
            if (fresh)
            {
                Set(lines, "memory_limit", "512M");
                Set(lines, "upload_max_filesize", "512M");
                Set(lines, "post_max_size", "512M");
                Set(lines, "max_execution_time", "300");
                Set(lines, "max_input_time", "300");
                Set(lines, "max_input_vars", "5000");
                Set(lines, "date.timezone", TimeZones.CurrentIana());
                Set(lines, "display_errors", "On");
                Set(lines, "error_reporting", "E_ALL");
                Set(lines, "log_errors", "On");
                foreach (var ext in Extensions)
                    if (File.Exists(Path.Combine(phpDir, "ext", "php_" + ext + ".dll"))) EnableExtension(lines, ext);
                if (File.Exists(Path.Combine(phpDir, "ext", "php_opcache.dll"))) EnableZend(lines, "opcache");
                Set(lines, "opcache.enable", "1");
                Set(lines, "opcache.enable_cli", "0");
                Set(lines, "opcache.validate_timestamps", "1");
                Set(lines, "opcache.revalidate_freq", "0");
            }

            if (string.Join("\n", lines) != before)
                File.WriteAllLines(ini, lines, new UTF8Encoding(false));
        }

        static string Q(string v) { return "\"" + v + "\""; }

        /// <summary>Set "key = value": replaces the active line, else the first commented one, else appends.</summary>
        public static void Set(List<string> lines, string key, string value)
        {
            var active = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=", RegexOptions.IgnoreCase);
            var commented = new Regex(@"^\s*;\s*" + Regex.Escape(key) + @"\s*=", RegexOptions.IgnoreCase);
            var line = key + " = " + value;
            int i = lines.FindIndex(l => active.IsMatch(l));
            if (i < 0) i = lines.FindIndex(l => commented.IsMatch(l));
            if (i >= 0) lines[i] = line;
            else lines.Add(line);
        }

        public static string Get(string phpDir, string key)
        {
            var ini = IniPath(phpDir);
            return File.Exists(ini) ? Get(File.ReadAllLines(ini).ToList(), key) : "";
        }

        static string Get(List<string> lines, string key)
        {
            var active = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=\s*(.*)$", RegexOptions.IgnoreCase);
            foreach (var l in lines)
            {
                var m = active.Match(l);
                if (m.Success) return m.Groups[1].Value.Trim().Trim('"');
            }
            return "";
        }

        static void EnableExtension(List<string> lines, string ext)
        {
            var on = new Regex(@"^\s*extension\s*=\s*""?(php_)?" + Regex.Escape(ext) + @"(\.dll)?""?\s*$", RegexOptions.IgnoreCase);
            if (lines.Any(l => on.IsMatch(l))) return;
            var off = new Regex(@"^\s*;\s*extension\s*=\s*""?(php_)?" + Regex.Escape(ext) + @"(\.dll)?""?\s*$", RegexOptions.IgnoreCase);
            int i = lines.FindIndex(l => off.IsMatch(l));
            if (i >= 0) lines[i] = "extension=" + ext;
            else lines.Add("extension=" + ext);
        }

        static void EnableZend(List<string> lines, string ext)
        {
            var on = new Regex(@"^\s*zend_extension\s*=\s*""?(php_)?" + Regex.Escape(ext), RegexOptions.IgnoreCase);
            if (lines.Any(l => on.IsMatch(l))) return;
            var off = new Regex(@"^\s*;\s*zend_extension\s*=\s*""?(php_)?" + Regex.Escape(ext), RegexOptions.IgnoreCase);
            int i = lines.FindIndex(l => off.IsMatch(l));
            if (i >= 0) lines[i] = "zend_extension=" + ext;
            else lines.Add("zend_extension=" + ext);
        }

        /// <summary>Extensions listed in php.ini, with their state (for the extension menu).</summary>
        public static List<KeyValuePair<string, bool>> ListExtensions(string phpDir)
        {
            var result = new List<KeyValuePair<string, bool>>();
            var extDir = Path.Combine(phpDir, "ext");
            if (!Directory.Exists(extDir)) return result;
            var ini = File.Exists(IniPath(phpDir)) ? File.ReadAllLines(IniPath(phpDir)) : new string[0];
            foreach (var dll in Directory.GetFiles(extDir, "php_*.dll").OrderBy(f => f))
            {
                var name = Path.GetFileNameWithoutExtension(dll).Substring(4);
                if (name == "opcache") continue;
                var on = new Regex(@"^\s*extension\s*=\s*""?(php_)?" + Regex.Escape(name) + @"(\.dll)?""?\s*$", RegexOptions.IgnoreCase);
                result.Add(new KeyValuePair<string, bool>(name, ini.Any(l => on.IsMatch(l))));
            }
            return result;
        }

        public static void ToggleExtension(string phpDir, string name, bool enable)
        {
            var ini = IniPath(phpDir);
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            if (enable) EnableExtension(lines, name);
            else
            {
                var on = new Regex(@"^\s*extension\s*=\s*""?(php_)?" + Regex.Escape(name) + @"(\.dll)?""?\s*$", RegexOptions.IgnoreCase);
                for (int i = 0; i < lines.Count; i++) if (on.IsMatch(lines[i])) lines[i] = ";" + lines[i].TrimStart();
            }
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
        }
    }

    /// <summary>Windows time zone id -> IANA name for php.ini (the most common zones; UTC otherwise).</summary>
    public static class TimeZones
    {
        static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"Turkey Standard Time","Europe/Istanbul"},{"GTB Standard Time","Europe/Bucharest"},{"GMT Standard Time","Europe/London"},
            {"W. Europe Standard Time","Europe/Berlin"},{"Central Europe Standard Time","Europe/Budapest"},{"Romance Standard Time","Europe/Paris"},
            {"Central European Standard Time","Europe/Warsaw"},{"E. Europe Standard Time","Europe/Chisinau"},{"FLE Standard Time","Europe/Kiev"},
            {"Russian Standard Time","Europe/Moscow"},{"Belarus Standard Time","Europe/Minsk"},{"Azerbaijan Standard Time","Asia/Baku"},
            {"Georgian Standard Time","Asia/Tbilisi"},{"Caucasus Standard Time","Asia/Yerevan"},{"Arabian Standard Time","Asia/Dubai"},
            {"Arab Standard Time","Asia/Riyadh"},{"Arabic Standard Time","Asia/Baghdad"},{"Iran Standard Time","Asia/Tehran"},
            {"Egypt Standard Time","Africa/Cairo"},{"Israel Standard Time","Asia/Jerusalem"},{"Jordan Standard Time","Asia/Amman"},
            {"Syria Standard Time","Asia/Damascus"},{"Middle East Standard Time","Asia/Beirut"},{"Pakistan Standard Time","Asia/Karachi"},
            {"India Standard Time","Asia/Kolkata"},{"Bangladesh Standard Time","Asia/Dhaka"},{"SE Asia Standard Time","Asia/Bangkok"},
            {"China Standard Time","Asia/Shanghai"},{"Taipei Standard Time","Asia/Taipei"},{"Singapore Standard Time","Asia/Singapore"},
            {"Tokyo Standard Time","Asia/Tokyo"},{"Korea Standard Time","Asia/Seoul"},{"AUS Eastern Standard Time","Australia/Sydney"},
            {"New Zealand Standard Time","Pacific/Auckland"},{"Eastern Standard Time","America/New_York"},{"Central Standard Time","America/Chicago"},
            {"Mountain Standard Time","America/Denver"},{"Pacific Standard Time","America/Los_Angeles"},{"E. South America Standard Time","America/Sao_Paulo"},
            {"Argentina Standard Time","America/Argentina/Buenos_Aires"},{"Central Standard Time (Mexico)","America/Mexico_City"},
            {"SA Pacific Standard Time","America/Bogota"},{"South Africa Standard Time","Africa/Johannesburg"},{"Morocco Standard Time","Africa/Casablanca"},
            {"W. Central Africa Standard Time","Africa/Lagos"},{"E. Africa Standard Time","Africa/Nairobi"},{"Ekaterinburg Standard Time","Asia/Yekaterinburg"},
            {"Central Asia Standard Time","Asia/Almaty"},{"West Asia Standard Time","Asia/Tashkent"},{"N. Central Asia Standard Time","Asia/Novosibirsk"},
            {"Afghanistan Standard Time","Asia/Kabul"},{"Nepal Standard Time","Asia/Kathmandu"},{"Myanmar Standard Time","Asia/Yangon"},
            {"North Asia East Standard Time","Asia/Irkutsk"},{"Greenwich Standard Time","Atlantic/Reykjavik"},{"UTC","UTC"},
        };

        public static string CurrentIana()
        {
            try
            {
                string v;
                return Map.TryGetValue(TimeZoneInfo.Local.Id, out v) ? v : "UTC";
            }
            catch { return "UTC"; }
        }
    }
}
