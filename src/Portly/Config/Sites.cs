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
        /// <summary>Extra host names (sites.ini "aliases=", comma separated): "shop.test", "*.shop.portly" ...</summary>
        public List<string> Aliases = new List<string>();
        /// <summary>sites.ini "archived=1": hidden, no vhost / hosts entry / certificate; files untouched.</summary>
        public bool Archived;
        /// <summary>A real domain (example.com) set to open the live site on this PC: no hosts entry while this is on.</summary>
        public bool ShowLive;
        /// <summary>
        /// A real domain also answers to a local name of its own (example.com.portly), in the hosts file even in Live
        /// mode: the local project stays reachable next to the live site. "" for other sites.
        /// </summary>
        public string LocalHost = "";
        /// <summary>The name Portly opens: the local name while the real domain shows the live site, otherwise Host.</summary>
        public string OpenHost { get { return ShowLive && LocalHost != "" ? LocalHost : Host; } }
        /// <summary>"" = PHP FastCGI, "frankenphp" or "octane" (FrankenPHP worker for Laravel Octane).</summary>
        public string Runtime = "";

        /// <summary>Every name the site answers to: host, *.host (not for groups) and the aliases.</summary>
        public List<string> AllNames
        {
            get
            {
                var list = new List<string> { Host };
                if (LocalHost != "") list.Add(LocalHost);
                if (!IsGroup) list.Add("*." + Host);
                foreach (var a in Aliases) if (!list.Contains(a)) list.Add(a);
                return list;
            }
        }

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
            if (https) return "https://" + OpenHost + (s.HttpsPort == 443 ? "" : ":" + s.HttpsPort) + "/";
            return "http://" + OpenHost + (s.HttpPort == 80 ? "" : ":" + s.HttpPort) + "/";
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
        // package.json alone is not a marker: Node scripts and app sources (Next, Nuxt ...) are not served by the
        // web server. Node projects Portly creates are marked in sites.ini instead (MarkNodeProject).
        static readonly string[] ProjectMarkers =
        {
            "index.php", "index.html", "index.htm", "composer.json", "artisan", "wp-config.php", "wp-config-sample.php",
            @"public\index.php", @"public\index.html", @"web\index.php", @"webroot\index.php", @"public_html\index.php",
        };

        /// <summary>Apps of other platforms (Python, Flutter, Go, Rust, Java): not web sites, and not groups either.</summary>
        static readonly string[] OtherAppMarkers =
        {
            "app.py", "manage.py", "requirements.txt", "pyproject.toml", "Pipfile", "pubspec.yaml", "go.mod", "Cargo.toml", "pom.xml", "build.gradle",
        };

        /// <summary>Parts of an app that sit next to the real projects in a group folder; never sub-projects themselves.</summary>
        static readonly HashSet<string> NotProjectFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "templates", "views", "partials", "layouts", "components", "includes", "assets", "static", "css", "js", "img", "images", "fonts",
        };

        /// <summary>An app of another platform with no PHP entry point (a Flask app, a Flutter app ...).</summary>
        public static bool IsOtherApp(string dir)
        {
            try
            {
                if (!OtherAppMarkers.Any(m => File.Exists(Path.Combine(dir, m)))) return false;
                return !File.Exists(Path.Combine(dir, "index.php")) && !File.Exists(Path.Combine(dir, "public", "index.php"));
            }
            catch { return false; }
        }

        /// <summary>A project created inside another project's folder (New project > sub-project).</summary>
        public static void MarkSubProject(string parent, string name)
        {
            var ini = new Ini(Paths.SitesFile);
            ini.Set(parent + "/" + name, "sub", "1");
            ini.Save();
        }

        /// <summary>A Node project: made by Portly as one, or a package.json without a PHP entry point.</summary>
        public static bool IsNodeProject(Site site)
        {
            try
            {
                if (new Ini(Paths.SitesFile).Get(site.Name, "kind", "") == "node") return true;
                return File.Exists(Path.Combine(site.Folder, "package.json")) && !File.Exists(Path.Combine(site.Folder, "index.php"))
                    && !File.Exists(Path.Combine(site.Folder, "composer.json"));
            }
            catch { return false; }
        }

        /// <summary>A Node project made by Portly (no index file to recognise it by).</summary>
        public static void MarkNodeProject(string siteName)
        {
            var ini = new Ini(Paths.SitesFile);
            ini.Set(siteName, "kind", "node");
            ini.Save();
        }

        public static bool IsValidName(string name) { return name != null && (NameRx.IsMatch(name) || IsDomainName(name)); }

        // example.com, www.example.com.tr: a folder named like a domain is served under that very name (like Laragon's "{name}")
        static readonly Regex DomainRx = new Regex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.IgnoreCase);
        public static bool IsDomainName(string name) { return name != null && DomainRx.IsMatch(name); }

        /// <summary>
        /// Address of a top-level project folder: a domain-like name as it is, otherwise the address template
        /// (Settings > address format, "{name}.{tld}" by default: shop -> shop.portly).
        /// </summary>
        public static string HostFor(string folderName)
        {
            var n = (folderName ?? "").ToLowerInvariant();
            if (IsDomainName(n)) return n;
            var s = Settings.Current;
            return s.HostTemplate.Replace("{name}", n).Replace("{tld}", s.Tld).ToLowerInvariant();
        }

        /// <summary>Address of a project inside a group / main project: blog.shop.portly, or its own domain name.</summary>
        public static string ChildHost(string folderName, string parentHost)
        {
            var n = (folderName ?? "").ToLowerInvariant();
            return IsDomainName(n) ? n : n + "." + parentHost;
        }

        // endings that never exist on the internet: such addresses cannot hide a live site
        static readonly string[] LocalEndings = { "localhost", "test", "local", "example", "invalid", "internal", "lan", "home.arpa" };

        /// <summary>
        /// Is this a real internet name (example.com)? While Portly serves it, this PC opens the local copy instead of the live site.
        /// </summary>
        public static bool IsPublicName(string host)
        {
            if (string.IsNullOrEmpty(host) || host.IndexOf('.') < 0) return false;
            var h = host.ToLowerInvariant();
            var tld = Settings.Current.Tld;
            if (h == tld || h.EndsWith("." + tld)) return false;
            return !LocalEndings.Any(e => h == e || h.EndsWith("." + e));
        }

        /// <summary>Open the live site (true) or the local project (false) on this PC for a real-domain project.</summary>
        public static void SetLive(string siteName, bool live)
        {
            var ini = new Ini(Paths.SitesFile);
            if (live) ini.Set(siteName, "live", true); else ini.Remove(siteName, "live");
            ini.Save();
        }

        /// <summary>Does the folder look like a web project by itself?</summary>
        /// <summary>
        /// A folder that can be served as a website: an index (php/html/htm) in the folder or in a front-controller
        /// subfolder (public / web / webroot / public_html), or a framework file (composer.json, package.json,
        /// artisan, wp-config.php). Folders with only tools, .exe files, scripts or notes are not sites.
        /// </summary>
        public static bool LooksLikeProject(string dir)
        {
            try
            {
                if (IsOtherApp(dir)) return false;
                if (ProjectMarkers.Any(m => File.Exists(Path.Combine(dir, m)))) return true;
                foreach (var sub in new[] { "public", "web", "webroot", "public_html" })
                {
                    var p = Path.Combine(dir, sub);
                    if (Directory.Exists(p) && (File.Exists(Path.Combine(p, "index.php"))
                        || File.Exists(Path.Combine(p, "index.html")) || File.Exists(Path.Combine(p, "index.htm")))) return true;
                }
                return false;
            }
            catch { return false; }
        }

        static IEnumerable<string> Folders(string dir)
        {
            string[] all;
            try { all = Directory.GetDirectories(dir); } catch { yield break; }
            var excluded = ExcludedPatterns();
            foreach (var d in all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith(".") || name.StartsWith("$") || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || name.Equals("vendor", StringComparison.OrdinalIgnoreCase)) continue;
                if (excluded.Any(rx => rx.IsMatch(name))) continue;
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
            if (LooksLikeProject(dir) || IsOtherApp(dir)) return false;
            return Folders(dir).Any(d => !NotProjectFolders.Contains(Path.GetFileName(d)) && LooksLikeProject(d));
        }

        /// <summary>Settings > excluded folders ("_*", "backup*", "*.bak" ...) as regexes; only with the "archive" feature.</summary>
        static List<Regex> ExcludedPatterns()
        {
            if (!Features.IsOn("archive")) return new List<Regex>();
            return Settings.Current.ExcludedFolderPatterns
                .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase))
                .ToList();
        }

        /// <summary>Sites that are served (archived ones are left out).</summary>
        public static List<Site> Scan()
        {
            return ScanAll().Where(x => !x.Archived).ToList();
        }

        /// <summary>Archived projects (for the "Archived (N)" list on the Sites page).</summary>
        public static List<Site> ArchivedSites()
        {
            return ScanAll().Where(x => x.Archived).ToList();
        }

        /// <summary>Every project folder, archived ones included (Site.Archived set).</summary>
        public static List<Site> ScanAll()
        {
            var list = new List<Site>();
            var root = Settings.Current.DocumentRoot;
            if (!Directory.Exists(root)) return list;
            var ini = new Ini(Paths.SitesFile);
            foreach (var dir in Folders(root))
            {
                var name = Path.GetFileName(dir);
                bool group = IsGroupFolder(dir, ini, name);
                // only real web projects and groups become sites; a folder with just tools / .exe / notes is skipped
                // (a sites.ini entry forces it to show, so a manual override always wins)
                if (!group && !LooksLikeProject(dir) && ini.Section(name).Count == 0) continue;
                var site = Make(name, dir, HostFor(name), ini);
                list.Add(site);
                // an archived group hides the projects inside it too
                if (site.Archived || !site.Valid) continue;
                // a project can also hold sub-projects made with "New project > sub-project" (sites.ini "sub=1")
                var declared = Folders(dir).Where(d => ini.GetBool(name + "/" + Path.GetFileName(d), "sub", false)).ToList();
                if (!group && declared.Count == 0) continue;
                site.IsGroup = true;
                foreach (var sub in group ? Folders(dir) : declared)
                {
                    var subName = Path.GetFileName(sub);
                    var childName = name + "/" + subName;
                    // inside a group, list only the web projects, not the tool folders / app parts next to them
                    bool configured = ini.Section(childName).Count > 0;
                    if (!configured && (NotProjectFolders.Contains(subName) || !LooksLikeProject(sub))) continue;
                    var child = Make(childName, sub, ChildHost(subName, site.Host), ini);
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
            site.ShowLive = IsPublicName(host) && ini.GetBool(name, "live", false);
            if (IsPublicName(host)) site.LocalHost = host + "." + Settings.Current.Tld;

            var customRoot = ini.Get(name, "root", "");
            if (customRoot != "" && !customRoot.Contains("..") && Directory.Exists(Path.Combine(dir, customRoot)))
                site.DocRoot = Path.Combine(dir, customRoot);
            else
            {
                // front-controller folders: public (Laravel, Symfony, CodeIgniter), web (Drupal, Yii), webroot (CakePHP)
                site.DocRoot = dir;
                if (!File.Exists(Path.Combine(dir, "index.php")) && !File.Exists(Path.Combine(dir, "index.html")))
                    foreach (var sub in new[] { "public", "web", "webroot", "public_html" })
                        if (File.Exists(Path.Combine(dir, sub, "index.php")) || File.Exists(Path.Combine(dir, sub, "index.html")))
                        {
                            site.DocRoot = Path.Combine(dir, sub);
                            break;
                        }
            }

            site.PhpOverride = ini.Get(name, "php", "");
            // switched-off features leave the stored values alone but do not use them
            if (Features.IsOn("aliases"))
                site.Aliases = ini.Get(name, "aliases", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(IsValidAlias).Distinct().ToList();
            site.Archived = Features.IsOn("archive") && ini.GetBool(name, "archived", false);
            if (Features.IsOn("frankenphp") && !AppInfo.IsLegacy)
            {
                var rt = ini.Get(name, "runtime", "").ToLowerInvariant();
                site.Runtime = rt == "frankenphp" || rt == "octane" ? rt : "";
            }
            if (ini.GetBool(name, "disabled", false)) { site.Valid = false; site.Problem = I18n.T("sites.disabled"); }
            return site;
        }

        static readonly Regex AliasRx = new Regex(@"^(\*\.)?[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$");

        /// <summary>"shop.test", "*.shop.test": letters, digits, dashes and dots, at least two labels, one leading "*." allowed.</summary>
        public static bool IsValidAlias(string name)
        {
            return name != null && name.Length <= 253 && AliasRx.IsMatch(name);
        }

        public static void SetArchived(string siteName, bool archived)
        {
            var ini = new Ini(Paths.SitesFile);
            if (archived) ini.Set(siteName, "archived", true);
            else ini.Remove(siteName, "archived");
            ini.Save();
        }

        /// <summary>"" (PHP FastCGI), "frankenphp" or "octane".</summary>
        public static void SetRuntime(string siteName, string runtime)
        {
            var ini = new Ini(Paths.SitesFile);
            if (string.IsNullOrEmpty(runtime)) ini.Remove(siteName, "runtime");
            else ini.Set(siteName, "runtime", runtime);
            ini.Save();
        }

        public static void SetPhp(string siteName, string phpFolder)
        {
            var ini = new Ini(Paths.SitesFile);
            if (string.IsNullOrEmpty(phpFolder)) ini.Remove(siteName, "php");
            else ini.Set(siteName, "php", phpFolder);
            ini.Save();
        }

        public static void SetAliases(string siteName, IEnumerable<string> aliases)
        {
            var ini = new Ini(Paths.SitesFile);
            var list = (aliases ?? new string[0]).Select(x => x.Trim().ToLowerInvariant()).Where(x => x != "").Distinct().ToList();
            if (list.Count == 0) ini.Remove(siteName, "aliases");
            else ini.Set(siteName, "aliases", string.Join(",", list));
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
