using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Portly.Config;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>One log a site has: its file and, for logs shared by all sites, which entries belong to the site.</summary>
    public class LogSource
    {
        public string Key, Title;
        /// <summary>Finds the file now (Laravel's daily files change at midnight).</summary>
        public Func<string> Resolve;
        /// <summary>Shared log (PHP errors, web server): only entries matching this are shown. Null = whole file.</summary>
        public Func<string, bool> Match;
        public bool Shared { get { return Match != null; } }
    }

    /// <summary>
    /// Feature "logs": the logs that exist for a site. PHP and the web server write one shared log for all sites
    /// (php-cgi has no per-pool error_log, and a per-site .user.ini would put a Portly file into the project),
    /// so those are filtered by the site's folder and host name. Laravel and WordPress logs are the site's own.
    /// </summary>
    public static class SiteLogs
    {
        public const long TailBytes = 2 * 1024 * 1024;

        public static string PhpErrorLog { get { return Path.Combine(Paths.Logs, "php_errors.log"); } }
        public static string WebErrorLog { get { return Path.Combine(Paths.Logs, Settings.Current.WebServer + "-error.log"); } }

        public static List<LogSource> For(Site site)
        {
            var list = new List<LogSource>();
            var match = SiteMatcher(site);
            list.Add(new LogSource { Key = "php", Title = I18n.T("logs.php"), Resolve = () => PhpErrorLog, Match = match });
            list.Add(new LogSource { Key = "web", Title = I18n.T("logs.web", Settings.Current.WebServer == "nginx" ? "Nginx" : "Apache"), Resolve = () => WebErrorLog, Match = match });
            if (Directory.Exists(Path.Combine(site.Folder, "storage", "logs")) || File.Exists(Path.Combine(site.Folder, "artisan")))
                list.Add(new LogSource { Key = "laravel", Title = "Laravel", Resolve = () => LaravelLog(site) });
            var wp = WordPressLog(site);
            if (wp != null) list.Add(new LogSource { Key = "wordpress", Title = "WordPress", Resolve = () => WordPressLog(site) ?? wp });
            foreach (var def in Jobs.Load(site.Name))
            {
                var d = def;
                list.Add(new LogSource { Key = "job-" + d.Name, Title = I18n.T("logs.job", d.Name), Resolve = () => Jobs.LogFileFor(site.Name, d.Name) });
            }
            return list;
        }

        /// <summary>Newest storage\logs\laravel*.log (single or daily channel).</summary>
        public static string LaravelLog(Site site)
        {
            var dir = Path.Combine(site.Folder, "storage", "logs");
            try
            {
                var newest = new DirectoryInfo(dir).GetFiles("laravel*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (newest != null) return newest.FullName;
            }
            catch { }
            return Path.Combine(dir, "laravel.log");
        }

        public static string WordPressLog(Site site)
        {
            foreach (var root in new[] { site.Folder, site.DocRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (root == null) continue;
                if (Directory.Exists(Path.Combine(root, "wp-content")) || File.Exists(Path.Combine(root, "wp-config.php")))
                    return Path.Combine(root, "wp-content", "debug.log");
            }
            return null;
        }

        /// <summary>Entry belongs to the site: its folder (\ or /) or its host name appears in it.</summary>
        public static Func<string, bool> SiteMatcher(Site site)
        {
            var back = site.Folder.TrimEnd('\\') + "\\";
            var fwd = Paths.Fwd(back);
            var host = new Regex(@"(?<![a-z0-9.-])" + Regex.Escape(site.Host) + @"(?![a-z0-9-])", RegexOptions.IgnoreCase);
            return e => e.IndexOf(back, StringComparison.OrdinalIgnoreCase) >= 0 || e.IndexOf(fwd, StringComparison.OrdinalIgnoreCase) >= 0 || host.IsMatch(e);
        }

        /// <summary>Last bytes of a file (shared read; works while PHP / Apache write to it). Returns "" when missing.</summary>
        public static string ReadTail(string file, long maxBytes, out long end)
        {
            end = 0;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    end = fs.Length;
                    long start = Math.Max(0, end - maxBytes);
                    fs.Position = start;
                    var buf = new byte[end - start];
                    int read = 0;
                    while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
                    end = start + read;
                    var text = Decode(buf, read);
                    // started in the middle of a line: drop the partial first line
                    if (start > 0) { int nl = text.IndexOf('\n'); text = nl >= 0 ? text.Substring(nl + 1) : ""; }
                    return text;
                }
            }
            catch { return ""; }
        }

        /// <summary>New bytes since a position. Returns null when the file shrank (cleared / rotated): read again from the start.</summary>
        public static string ReadFrom(string file, long pos, out long end)
        {
            end = pos;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < pos) return null;
                    if (fs.Length == pos) return "";
                    long len = Math.Min(fs.Length - pos, TailBytes);
                    fs.Position = fs.Length - len;
                    var buf = new byte[len];
                    int read = 0;
                    while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
                    end = fs.Length - len + read;
                    return Decode(buf, read);
                }
            }
            catch { return File.Exists(file) ? "" : null; }
        }

        static string Decode(byte[] buf, int len)
        {
            var text = new UTF8Encoding(false, false).GetString(buf, 0, len);
            return text.Replace("\r\n", "\n");
        }

        /// <summary>Empty the file (keeps it, the writer keeps its handle).</summary>
        public static void Truncate(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) fs.SetLength(0);
        }
    }

    /// <summary>
    /// Keeps whole entries of a shared log that belong to a site. An entry starts with "[" (PHP: "[23-Oct-2026 ...]",
    /// Apache: "[Thu Oct 23 ...]", Nginx: "2026/10/23 ...") and continues on the following lines (stack traces).
    /// Text may arrive in pieces; a line is only judged once it is complete.
    /// </summary>
    public class LogFilter
    {
        readonly Func<string, bool> _match;
        readonly StringBuilder _entry = new StringBuilder();
        string _partial = "";
        static readonly Regex EntryStart = new Regex(@"^(\[|\d{4}[/-]\d\d[/-]\d\d[ T])");

        public LogFilter(Func<string, bool> match) { _match = match; }

        /// <summary>Feed new text; returns the complete entries that match (or everything when there is no matcher).</summary>
        public string Feed(string text, bool final = false)
        {
            text = _partial + text;
            int last = text.LastIndexOf('\n');
            if (last < 0 && !final) { _partial = text; return ""; }
            var complete = last < 0 ? text : text.Substring(0, last + 1);
            _partial = last < 0 ? "" : text.Substring(last + 1);
            if (final && _partial != "") { complete += _partial + "\n"; _partial = ""; }
            if (_match == null) return complete;
            var output = new StringBuilder();
            foreach (var line in complete.Split('\n'))
            {
                if (line == "") continue;
                if (EntryStart.IsMatch(line) && _entry.Length > 0) Flush(output);
                _entry.Append(line).Append('\n');
            }
            // an entry is finished when the next one starts; at the end of a read keep the last one open
            // only if it looks like a stack trace may follow
            if (_entry.Length > 0 && (final || !_entry.ToString().Contains("Stack trace"))) Flush(output);
            return output.ToString();
        }

        void Flush(StringBuilder output)
        {
            var e = _entry.ToString();
            _entry.Clear();
            if (_match(e)) output.Append(e);
        }
    }
}
