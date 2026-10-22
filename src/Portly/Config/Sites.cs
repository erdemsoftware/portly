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
        /// <summary>Key and display name: "shop", or "clients/acme" for a project inside a group folder.</summary>
        public string Name;
        public string Host;
        public string Folder;
        public string DocRoot;
        public bool Valid;
        public string Problem = "";
        /// <summary>Group folder name when the project sits in a group (clients/acme -> "clients").</summary>
        public string Group = "";
        /// <summary>This folder only holds other projects (it keeps its own address but without *.wildcard).</summary>
        public bool IsGroup;
        /// <summary>PHP version folder chosen for this site ("" = default).</summary>
        public string PhpOverride = "";

        public string ShortName { get { var i = Name.LastIndexOf('/'); return i < 0 ? Name : Name.Substring(i + 1); } }

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
    /// Every folder in the projects folder is a site:  C:\www\shop -> https://shop.portly
    /// A folder that is not a project itself but contains projects is a group:
    ///   C:\www\clients\acme  -> https://acme.clients.portly
    ///   C:\www\clients\beta  -> https://beta.clients.portly
    /// Laravel / Symfony style projects (public\index.php) are served from their public folder.
    /// Per-site options are kept in usr\sites.ini ([shop] php=..., root=..., disabled=1, [clients] group=0|1).
    /// </summary>
    public static class Sites
    {
        static readonly Regex NameRx = new Regex(@"^[a-z0-9]([a-z0-9_-]{0,61}[a-z0-9])?$", RegexOptions.IgnoreCase);
        static readonly string[] ProjectMarkers =
        {
            "index.php", "index.html", "index.htm", "composer.json", "package.json", "artisan", "wp-config.php", "wp-config-sample.php",
            @"public\index.php", @"public\index.html", @"web\index.php", @"public_html\index.php",
        };

        public static bool IsValidName(string name) { return name != null && NameRx.IsMatch(name); }

        /// <summary>Does the folder look like a web project by itself?</summary>
        public static bool LooksLikeProject(string dir)
        {
            try { return ProjectMarkers.Any(m => File.Exists(Path.Combine(dir, m))); }
            catch { return false; }
        }

        static IEnumerable<string> Folders(string dir)
        {
            string[] all;
            try { all = Directory.GetDirectories(dir); } catch { yield break; }
            foreach (var d in all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith(".") || name.StartsWith("$") || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || name.Equals("vendor", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var attr = File.GetAttributes(d);
                    if ((attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0) continue;
                }
                catch { continue; }
                yield return d;
            }
        }

        /// <summary>Is this top-level folder a group of projects? (sites.ini "group=1/0" overrides the guess)</summary>
        static bool IsGroupFolder(string dir, Ini ini, string name)
        {
            var forced = ini.Get(name, "group", "");
            if (forced == "1") return true;
            if (forced == "0") return false;
            if (LooksLikeProject(dir)) return false;
            return Folders(dir).Any(LooksLikeProject);
        }

        public static List<Site> Scan()
        {
            var list = new List<Site>();
            var root = Settings.Current.DocumentRoot;
            if (!Directory.Exists(root)) return list;
            var ini = new Ini(Paths.SitesFile);
            var tld = Settings.Current.Tld;
            foreach (var dir in Folders(root))
            {
                var name = Path.GetFileName(dir);
                var site = Make(name, dir, name.ToLowerInvariant() + "." + tld, ini);
                list.Add(site);
                if (!site.Valid || !IsGroupFolder(dir, ini, name)) continue;
                site.IsGroup = true;
                foreach (var sub in Folders(dir))
                {
                    var subName = Path.GetFileName(sub);
                    var child = Make(name + "/" + subName, sub, subName.ToLowerInvariant() + "." + site.Host, ini);
                    child.Group = name;
                    if (!IsValidName(subName)) { child.Valid = false; child.Problem = I18n.T("sites.invalidName"); }
                    list.Add(child);
                }
            }
            return list;
        }

        static Site Make(string name, string dir, string host, Ini ini)
        {
            var site = new Site { Name = name, Folder = dir, Host = host };
            site.Valid = IsValidName(Path.GetFileName(dir));
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
            return site;
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
