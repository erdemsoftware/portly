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
            "openssl", "pdo_mysql", "pdo_pgsql", "pdo_sqlite", "pgsql", "sodium", "sqlite3", "soap", "sockets", "xsl", "zip"
        };

        public static string IniPath(string phpDir) { return Path.Combine(phpDir, "php.ini"); }

        public static void Ensure(string phpDir)
        {
            var ini = IniPath(phpDir);
            bool fresh = !File.Exists(ini);
            if (fresh)
            {
                var src = new[] { "php.ini-development", "php.ini-production" }.Select(f => Path.Combine(phpDir, f)).FirstOrDefault(File.Exists);
                if (src == null) File.WriteAllText(ini, OS.IsWindows ? "[PHP]\r\n" : "[PHP]\n");
                else File.Copy(src, ini);
            }
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            var before = string.Join("\n", lines);

            // ---- paths (always, keeps the folder portable) ----
            // macOS / Linux PHP is a static build: every extension is compiled in, there is no ext\ folder
            if (OS.IsWindows) Set(lines, "extension_dir", Q(Paths.Fwd(Path.Combine(phpDir, "ext"))));
            Set(lines, "error_log", Q(Paths.Fwd(Path.Combine(Paths.Logs, "php_errors.log"))));
            Set(lines, "session.save_path", Q(Paths.Fwd(Paths.Sessions)));
            Set(lines, "upload_tmp_dir", Q(Paths.Fwd(Paths.Tmp)));
            if (OS.IsWindows)
            {
                Set(lines, "curl.cainfo", Q(Paths.Fwd(Certs.BundleFile)));
                Set(lines, "openssl.cafile", Q(Paths.Fwd(Certs.BundleFile)));
                // mail(): PHP's built-in SMTP client on Windows talks straight to Mailpit (no helper process).
                Set(lines, "SMTP", "127.0.0.1");
                Set(lines, "smtp_port", Settings.Current.MailpitSmtpPort.ToString());
            }
            else
            {
                Set(lines, "sys_temp_dir", Q(Paths.Fwd(Paths.Tmp)));
                // a static PHP has no CA bundle of its own it can rely on: use Portly's (system roots + Portly's authority)
                if (File.Exists(Certs.BundleFile))
                {
                    Set(lines, "curl.cainfo", Q(Paths.Fwd(Certs.BundleFile)));
                    Set(lines, "openssl.cafile", Q(Paths.Fwd(Certs.BundleFile)));
                }
                // mail(): there is no built-in SMTP client outside Windows; Mailpit's sendmail command hands the message over
                var mailpit = Settings.Current.MailTool == "mailpit" ? PackageTypes.ActiveDir("mailpit") : null;
                if (mailpit != null)
                    Set(lines, "sendmail_path", Q(Paths.Fwd(Path.Combine(mailpit, "mailpit")) + " sendmail --smtp-addr 127.0.0.1:" + Settings.Current.MailpitSmtpPort));
            }
            if (Get(lines, "sendmail_from") == "") Set(lines, "sendmail_from", "\"portly@localhost\"");
            // macOS / Linux PHP with Xdebug (Portly's own build): the zend_extension path follows the folder when Portly is moved
            if (!OS.IsWindows) Tools.Xdebug.RefreshPath(lines, phpDir);
            // a sendmail_path written by older Portly / Laragon (mailpit.exe sendmail) would override SMTP
            if (OS.IsWindows)
                for (int i = 0; i < lines.Count; i++)
                    if (Regex.IsMatch(lines[i], @"^\s*sendmail_path\s*=.*mailpit", RegexOptions.IgnoreCase)) lines[i] = ";" + lines[i];

            // ---- OPcache on Windows: several php-cgi workers cannot share memory at the same address (ASLR).
            //      A file cache lets every worker fall back safely (PHP 8.5 refuses to start otherwise).
            //      php-fpm workers on macOS / Linux fork from one parent and share the cache normally. ----
            if (OS.IsWindows)
            {
                var opcacheDir = Path.Combine(Paths.Tmp, "opcache");
                Directory.CreateDirectory(opcacheDir);
                Set(lines, "opcache.file_cache", Q(Paths.Fwd(opcacheDir)));
                Set(lines, "opcache.file_cache_fallback", "1");
            }

            // ---- security (always) ----
            // session.upload_progress + a dynamic include can lead to remote code execution: keep it off.
            Set(lines, "session.upload_progress.enabled", "Off");
            Set(lines, "allow_url_include", "Off");
            Set(lines, "expose_php", "Off");
            // hotfixes from manifest/security.json (only whitelisted, stricter-only values)
            SecurityInfo.ApplyIniHotfixes((k, v) => Set(lines, k, v));

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
                if (OS.IsWindows)
                {
                    foreach (var ext in Extensions)
                        if (File.Exists(Path.Combine(phpDir, "ext", "php_" + ext + ".dll"))) EnableExtension(lines, ext);
                    if (File.Exists(Path.Combine(phpDir, "ext", "php_opcache.dll"))) EnableZend(lines, "opcache");
                }
                Set(lines, "opcache.enable", "1");
                Set(lines, "opcache.enable_cli", "0");
                Set(lines, "opcache.validate_timestamps", "1");
                Set(lines, "opcache.revalidate_freq", "0");
            }

            if (string.Join("\n", lines) != before)
                File.WriteAllLines(ini, lines, new UTF8Encoding(false));
        }

        /// <summary>macOS / Linux: etc/php-fpm/&lt;php folder&gt;.conf, the php-fpm configuration of one PHP version (Windows has no php-fpm).</summary>
        public static string FpmConf(string folder) { return Path.Combine(Paths.Etc, "php-fpm", folder + ".conf"); }

        public static string FpmPid(string folder) { return Path.Combine(Paths.Tmp, "php-fpm-" + folder + ".pid"); }

        /// <summary>
        /// php-fpm in the foreground (daemonize = no, so Portly owns the process) with one static pool on 127.0.0.1:port.
        /// No user / group lines: the pool runs as the user who started Portly (php-fpm -R when that is root).
        /// </summary>
        public static string WriteFpmConf(string folder, int port)
        {
            var s = Settings.Current;
            Directory.CreateDirectory(Path.Combine(Paths.Etc, "php-fpm"));
            Directory.CreateDirectory(Paths.Logs);
            Directory.CreateDirectory(Paths.Tmp);
            var v = Templates.CommonVars();
            v["PHP_FPM_PID"] = Paths.Fwd(FpmPid(folder));
            v["PHP_FPM_LOG"] = Paths.Fwd(Path.Combine(Paths.Logs, "php-fpm-" + PackageTypes.Label(folder) + ".log"));
            v["PHP_PORT"] = port.ToString();
            v["PHP_CHILDREN"] = Math.Max(1, s.PhpChildren).ToString();
            v["PHP_MAX_REQUESTS"] = s.PhpMaxRequests.ToString();
            var file = FpmConf(folder);
            Templates.WriteIfChanged(file, Templates.Render("php-fpm.conf.tpl", v));
            return file;
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
            if (!OS.IsWindows) return BuiltInExtensions(phpDir);
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

        static readonly Dictionary<string, List<KeyValuePair<string, bool>>> BuiltIn = new Dictionary<string, List<KeyValuePair<string, bool>>>();

        /// <summary>macOS / Linux: every extension of the static PHP build is compiled in and always on ("php -m").</summary>
        static List<KeyValuePair<string, bool>> BuiltInExtensions(string phpDir)
        {
            lock (BuiltIn)
            {
                List<KeyValuePair<string, bool>> cached;
                if (BuiltIn.TryGetValue(phpDir, out cached)) return new List<KeyValuePair<string, bool>>(cached);
            }
            var list = new List<KeyValuePair<string, bool>>();
            var exe = Path.Combine(phpDir, OS.Exe("php"));
            if (File.Exists(exe))
            {
                var r = ProcessUtil.Run(exe, "-n -m", phpDir, null, 15000);
                foreach (var raw in r.Output.Split('\n'))
                {
                    var name = raw.Trim().ToLowerInvariant();
                    if (name == "" || name.StartsWith("[") || name.Contains(" ")) continue;
                    if (name == "core" || name == "date" || name == "opcache" || list.Any(kv => kv.Key == name)) continue;
                    list.Add(new KeyValuePair<string, bool>(name, true));
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                if (list.Count > 0) lock (BuiltIn) BuiltIn[phpDir] = list;
            }
            return new List<KeyValuePair<string, bool>>(list);
        }

        public static void ToggleExtension(string phpDir, string name, bool enable)
        {
            if (!OS.IsWindows) return;   // nothing to switch: built in
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
                // macOS / Linux already name zones the IANA way ("Europe/Istanbul")
                if (!OS.IsWindows && TimeZoneInfo.Local.Id.Contains("/")) return TimeZoneInfo.Local.Id;
                return Map.TryGetValue(TimeZoneInfo.Local.Id, out v) ? v : "UTC";
            }
            catch { return "UTC"; }
        }
    }
}
