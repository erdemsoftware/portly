using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;

namespace Portly.Tools
{
    /// <summary>
    /// One-click Xdebug for the active PHP: downloads the matching DLL from xdebug.org into php\ext
    /// and switches zend_extension=xdebug on or off. Mode: debug + develop, starts on request trigger
    /// (XDEBUG_TRIGGER / browser extension), client 127.0.0.1:9003 (VS Code / PhpStorm default).
    /// </summary>
    public static class Xdebug
    {
        // "xdebug", "php_xdebug.dll" or a full path ("/.../php-8.5.12/ext/xdebug.so")
        static readonly Regex On = new Regex(@"^\s*zend_extension\s*=\s*""?([^"";]*[/\\])?(php_)?xdebug(\.dll|\.so)?""?\s*$", RegexOptions.IgnoreCase);
        static readonly Regex Off = new Regex(@"^\s*;\s*zend_extension\s*=\s*""?([^"";]*[/\\])?(php_)?xdebug(\.dll|\.so)?""?\s*$", RegexOptions.IgnoreCase);

        /// <summary>Windows: always (the DLL is downloaded). macOS / Linux: see <see cref="IsAvailable"/>, it depends on the PHP package.</summary>
        public static bool Available { get { return OS.IsWindows; } }

        /// <summary>
        /// macOS / Linux: Xdebug is a loadable extension, so it needs a PHP that can load one. The static-php builds cannot; Portly's own
        /// PHP builds ship it as ext/xdebug.so (or xdebug.so at the folder root). Null when the PHP folder has none.
        /// </summary>
        public static string SoPath(string phpDir)
        {
            if (OS.IsWindows || string.IsNullOrEmpty(phpDir)) return null;
            foreach (var f in new[] { Path.Combine(phpDir, "ext", "xdebug.so"), Path.Combine(phpDir, "xdebug.so") })
                if (File.Exists(f)) return f;
            return null;
        }

        /// <summary>True when Xdebug can be switched on for this PHP folder.</summary>
        public static bool IsAvailable(string phpDir) { return OS.IsWindows || SoPath(phpDir) != null; }

        /// <summary>macOS / Linux: keeps the path of an existing xdebug zend_extension line right after Portly was moved (called by PhpConfig.Ensure).</summary>
        internal static void RefreshPath(System.Collections.Generic.List<string> lines, string phpDir)
        {
            var so = SoPath(phpDir);
            if (so == null) return;
            for (int i = 0; i < lines.Count; i++)
            {
                if (On.IsMatch(lines[i])) lines[i] = "zend_extension=\"" + Paths.Fwd(so) + "\"";
                else if (Off.IsMatch(lines[i])) lines[i] = ";zend_extension=\"" + Paths.Fwd(so) + "\"";
            }
        }

        public static bool IsEnabled(string phpDir)
        {
            if (!OS.IsWindows && SoPath(phpDir) == null) return false;
            var ini = PhpConfig.IniPath(phpDir);
            return File.Exists(ini) && File.ReadAllLines(ini).Any(l => On.IsMatch(l));
        }

        /// <summary>https://xdebug.org/files/php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll for php-8.4.x-nts-Win32-vs17-x64</summary>
        public static string DownloadUrl(string phpFolder)
        {
            if (!OS.IsWindows) return "";
            var branch = PackageTypes.Branch(phpFolder);
            var m = Regex.Match(phpFolder, @"-(vs1[5-9]|vc1[45])-", RegexOptions.IgnoreCase);
            var compiler = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "vs17";
            if (branch == "7.4") return "https://xdebug.org/files/php_xdebug-" + Catalog.Xdebug74 + "-7.4-" + compiler + "-nts-x86_64.dll";
            return "https://xdebug.org/files/php_xdebug-" + Catalog.XdebugVersion + "-" + branch + "-nts-" + compiler + "-x86_64.dll";
        }

        public static async Task EnableAsync(string phpDir)
        {
            string so = null;
            if (!OS.IsWindows)
            {
                so = SoPath(phpDir);
                if (so == null) throw new Exception(PhpExtensions.TOr("ext.xdebugStatic", "Xdebug is not available in the static PHP builds used on macOS and Linux."));
            }
            else
            {
                var dll = Path.Combine(phpDir, "ext", "php_xdebug.dll");
                if (!File.Exists(dll))
                {
                    var url = DownloadUrl(Path.GetFileName(phpDir));
                    Log.Info(I18n.T("log.downloading", "Xdebug"));
                    await Net.DownloadFileAsync(url, dll, null, CancellationToken.None);
                }
            }
            var ini = PhpConfig.IniPath(phpDir);
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            int i = lines.FindIndex(l => Off.IsMatch(l));
            var entry = so == null ? "zend_extension=xdebug" : "zend_extension=\"" + Paths.Fwd(so) + "\"";
            if (lines.Any(l => On.IsMatch(l))) { if (so != null) RefreshPath(lines, phpDir); }
            else if (i >= 0) lines[i] = entry;
            else lines.Add(entry);
            PhpConfig.Set(lines, "xdebug.mode", "debug,develop");
            PhpConfig.Set(lines, "xdebug.start_with_request", "trigger");
            PhpConfig.Set(lines, "xdebug.client_host", "127.0.0.1");
            PhpConfig.Set(lines, "xdebug.client_port", "9003");
            PhpConfig.Set(lines, "xdebug.log", "\"" + Paths.Fwd(Path.Combine(Paths.Logs, "xdebug.log")) + "\"");
            PhpConfig.Set(lines, "xdebug.log_level", "0");
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
            Log.Info(I18n.T("log.xdebugOn", PackageTypes.Label(Path.GetFileName(phpDir))));
        }

        public static void Disable(string phpDir)
        {
            if (!OS.IsWindows && SoPath(phpDir) == null) return;
            var ini = PhpConfig.IniPath(phpDir);
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            for (int i = 0; i < lines.Count; i++) if (On.IsMatch(lines[i])) lines[i] = ";" + lines[i].TrimStart();
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
            Log.Info(I18n.T("log.xdebugOff", PackageTypes.Label(Path.GetFileName(phpDir))));
        }
    }
}
