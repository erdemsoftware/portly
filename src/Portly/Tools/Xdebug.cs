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
        static readonly Regex On = new Regex(@"^\s*zend_extension\s*=\s*""?(php_)?xdebug(\.dll)?""?\s*$", RegexOptions.IgnoreCase);
        static readonly Regex Off = new Regex(@"^\s*;\s*zend_extension\s*=\s*""?(php_)?xdebug(\.dll)?""?\s*$", RegexOptions.IgnoreCase);

        public static bool IsEnabled(string phpDir)
        {
            var ini = PhpConfig.IniPath(phpDir);
            return File.Exists(ini) && File.ReadAllLines(ini).Any(l => On.IsMatch(l));
        }

        /// <summary>https://xdebug.org/files/php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll for php-8.4.x-nts-Win32-vs17-x64</summary>
        public static string DownloadUrl(string phpFolder)
        {
            var branch = PackageTypes.Branch(phpFolder);
            var m = Regex.Match(phpFolder, @"-(vs1[5-9]|vc1[45])-", RegexOptions.IgnoreCase);
            var compiler = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "vs17";
            if (branch == "7.4") return "https://xdebug.org/files/php_xdebug-" + Catalog.Xdebug74 + "-7.4-" + compiler + "-nts-x86_64.dll";
            return "https://xdebug.org/files/php_xdebug-" + Catalog.XdebugVersion + "-" + branch + "-nts-" + compiler + "-x86_64.dll";
        }

        public static async Task EnableAsync(string phpDir)
        {
            var dll = Path.Combine(phpDir, "ext", "php_xdebug.dll");
            if (!File.Exists(dll))
            {
                var url = DownloadUrl(Path.GetFileName(phpDir));
                Log.Info(I18n.T("log.downloading", "Xdebug"));
                await Net.DownloadFileAsync(url, dll, null, CancellationToken.None);
            }
            var ini = PhpConfig.IniPath(phpDir);
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            int i = lines.FindIndex(l => Off.IsMatch(l));
            if (lines.Any(l => On.IsMatch(l))) { }
            else if (i >= 0) lines[i] = "zend_extension=xdebug";
            else lines.Add("zend_extension=xdebug");
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
            var ini = PhpConfig.IniPath(phpDir);
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            for (int i = 0; i < lines.Count; i++) if (On.IsMatch(lines[i])) lines[i] = ";" + lines[i].TrimStart();
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
            Log.Info(I18n.T("log.xdebugOff", PackageTypes.Label(Path.GetFileName(phpDir))));
        }
    }
}
