using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Portly.Core;
using Portly.Packages;

namespace Portly.Config
{
    public class Site
    {
        public string Name;
        public string Host;
        public string Folder;
        public string DocRoot;
        public bool Valid;
        public string Problem = "";
        /// <summary>PHP version folder chosen for this site ("" = default).</summary>
        public string PhpOverride = "";

        public string PhpFolder
        {
            get
            {
                if (PhpOverride != "" && PackageTypes.Installed("php").Contains(PhpOverride, StringComparer.OrdinalIgnoreCase)) return PhpOverride;
                return PackageTypes.ActiveFolder("php");
            }
        }

        public string Url(bool https)
        {
            var s = Settings.Current;
            if (https) return "https://" + Host + (s.HttpsPort == 443 ? "" : ":" + s.HttpsPort) + "/";
            return "http://" + Host + (s.HttpPort == 80 ? "" : ":" + s.HttpPort) + "/";
        }

        public string PreferredUrl { get { return Url(Settings.Current.Ssl); } }
    }

    /// <summary>
    /// Every folder in the document root is a site: E:\projects\shop -> http(s)://shop.test
    /// Laravel / Symfony style projects (public\index.php) are served from their public folder.
    /// Per-site options are kept in usr\sites.ini.
    /// </summary>
    public static class Sites
    {
        static readonly Regex NameRx = new Regex(@"^[a-z0-9]([a-z0-9_-]{0,61}[a-z0-9])?$", RegexOptions.IgnoreCase);

        public static bool IsValidName(string name) { return name != null && NameRx.IsMatch(name); }

        public static List<Site> Scan()
        {
            var list = new List<Site>();
            var root = Settings.Current.DocumentRoot;
            if (!Directory.Exists(root)) return list;
            var ini = new Ini(Paths.SitesFile);
            var tld = Settings.Current.Tld;
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                try
                {
                    var attr = File.GetAttributes(dir);
                    if ((attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0) continue;
                }
                catch { continue; }
                if (name.StartsWith(".") || name.StartsWith("$")) continue;

                var site = new Site { Name = name, Folder = dir, Host = name.ToLowerInvariant() + "." + tld };
                site.Valid = IsValidName(name);
                if (!site.Valid) site.Problem = I18n.T("sites.invalidName");

                var customRoot = ini.Get(name, "root", "");
                if (customRoot != "" && !customRoot.Contains("..") && Directory.Exists(Path.Combine(dir, customRoot)))
                    site.DocRoot = Path.Combine(dir, customRoot);
                else if (File.Exists(Path.Combine(dir, "public", "index.php")) || File.Exists(Path.Combine(dir, "public", "index.html")))
                    site.DocRoot = Path.Combine(dir, "public");
                else
                    site.DocRoot = dir;

                site.PhpOverride = ini.Get(name, "php", "");
                if (ini.GetBool(name, "disabled", false)) { site.Valid = false; site.Problem = I18n.T("sites.disabled"); }
                list.Add(site);
            }
            return list;
        }

        public static void SetPhp(string siteName, string phpFolder)
        {
            var ini = new Ini(Paths.SitesFile);
            if (string.IsNullOrEmpty(phpFolder)) ini.Remove(siteName, "php");
            else ini.Set(siteName, "php", phpFolder);
            ini.Save();
        }

        /// <summary>PHP versions needed by the current sites (default first).</summary>
        public static List<string> NeededPhpFolders(List<Site> sites)
        {
            var list = new List<string>();
            var def = PackageTypes.ActiveFolder("php");
            if (def != "") list.Add(def);
            foreach (var s in sites.Where(x => x.Valid))
            {
                var f = s.PhpFolder;
                if (f != "" && !list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
            }
            return list;
        }
    }
}
