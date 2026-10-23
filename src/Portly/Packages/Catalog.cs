using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Packages
{
    public class PackageEntry
    {
        public string Type;
        public string Version;
        public string Folder;
        public string Url;
        public string Sha256 = "";
        public string Subdir = "";
        public string Kind = "zip";        // zip | file
        public string FileName = "";       // for kind=file: name inside the version folder
        public string Tag = "";            // LTS, latest, ...
        public string Note = "";
        public bool Recommended;
        public double MinOs;               // 6.1 = Win7, 10.0 = Win10
        public int Vc;                     // required VC++ 14.x runtime minor version (0 = none)

        public string TypeName { get { return PackageTypes.Get(Type).Name; } }
        public string InstallDir { get { return Path.Combine(Paths.BinOf(Type), Folder); } }
        public bool IsInstalled { get { return Directory.Exists(InstallDir); } }
        public string Branch { get { return PackageTypes.Branch(Version); } }
    }

    /// <summary>
    /// Package catalog.
    ///  Modern edition: manifest/packages.json from GitHub (cached in usr\cache), plus live PHP versions
    ///                  from windows.php.net, so new PHP releases appear without a Portly update.
    ///  Legacy edition: the catalog embedded in the exe (frozen, Windows 7 compatible packages).
    /// </summary>
    public static class Catalog
    {
        public static List<PackageEntry> Entries = new List<PackageEntry>();
        public static string VcRedistUrl = "https://aka.ms/vc14/vc_redist.x64.exe";
        public static int VcRedistMinor = 50;
        public static string Updated = "";
        /// <summary>Xdebug build to download for PHP 8.x, and the last one for PHP 7.4.</summary>
        public static string XdebugVersion = "3.5.3";
        public static string Xdebug74 = "3.1.6";
        public static event Action Changed;

        static string CacheFile { get { return Path.Combine(Paths.Cache, "packages.json"); } }
        static string PhpCacheFile { get { return Path.Combine(Paths.Cache, "php-releases.json"); } }

        public static void LoadLocal()
        {
            string json = null;
            if (!AppInfo.IsLegacy && File.Exists(CacheFile))
            {
                try { json = File.ReadAllText(CacheFile, Encoding.UTF8); Parse(json); }
                catch (Exception ex) { Log.Warn("Cached catalog is invalid: " + ex.Message); json = null; }
            }
            if (json == null) Parse(Embedded());
            if (!AppInfo.IsLegacy && File.Exists(PhpCacheFile))
            {
                try { MergePhpReleases(File.ReadAllText(PhpCacheFile, Encoding.UTF8)); } catch { }
            }
            SecurityInfo.LoadLocal();
        }

        static string Embedded()
        {
            var name = AppInfo.IsLegacy ? "catalog.legacy.json" : "catalog.modern.json";
            using (var s = typeof(Catalog).Assembly.GetManifestResourceStream(name))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        /// <summary>Download the latest catalog (modern edition only).</summary>
        public static async Task<bool> RefreshAsync()
        {
            if (AppInfo.IsLegacy) return false;
            bool ok = false;
            try
            {
                var json = await Net.GetStringAsync(AppInfo.ManifestBase + "packages.json");
                Parse(json); // throws when invalid -> cache is not overwritten
                File.WriteAllText(CacheFile, json, new UTF8Encoding(false));
                ok = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Catalog refresh failed: " + ex.Message);
                LoadLocal();
            }
            try
            {
                var php = await Net.GetStringAsync("https://windows.php.net/downloads/releases/releases.json");
                MergePhpReleases(php);
                File.WriteAllText(PhpCacheFile, php, new UTF8Encoding(false));
                ok = true;
            }
            catch (Exception ex) { Log.Warn("PHP release list failed: " + ex.Message); }
            await SecurityInfo.RefreshAsync(); // end-of-life dates and advisories travel with the catalog
            var h = Changed; if (h != null) h();
            return ok;
        }

        static void Parse(string json)
        {
            var root = Json.ParseObject(json);
            var list = new List<PackageEntry>();
            foreach (var p in root.Arr("packages"))
            {
                var e = new PackageEntry
                {
                    Type = p.Str("type"),
                    Version = p.Str("version"),
                    Folder = p.Str("folder"),
                    Url = p.Str("url"),
                    Sha256 = p.Str("sha256"),
                    Subdir = p.Str("subdir"),
                    Kind = p.Str("kind", "zip"),
                    FileName = p.Str("fileName"),
                    Tag = p.Str("tag"),
                    Note = p.Str("note"),
                    Recommended = p.Bool("recommended"),
                    Vc = p.Int("vc"),
                };
                double minOs;
                e.MinOs = double.TryParse(p.Str("minOs", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out minOs) ? minOs : 0;
                if (e.Type == "" || e.Folder == "" || e.Url == "") continue;
                if (e.Folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || e.Folder.Contains("..")) continue;
                list.Add(e);
            }
            if (list.Count == 0) throw new FormatException("empty catalog");
            Entries = list;
            Updated = root.Str("updated");
            var vc = root.Obj("vcredist");
            if (vc != null) { VcRedistUrl = vc.Str("url", VcRedistUrl); VcRedistMinor = vc.Int("minor", VcRedistMinor); }
            var xd = root.Obj("xdebug");
            if (xd != null) { XdebugVersion = xd.Str("version", XdebugVersion); Xdebug74 = xd.Str("php74", Xdebug74); }
            var donate = root.Str("donateUrl");
            if (donate.StartsWith("https://")) AppInfo.DonateUrl = donate;
        }

        /// <summary>Add the newest PHP build of every branch listed by windows.php.net.</summary>
        static void MergePhpReleases(string json)
        {
            var root = Json.ParseObject(json);
            var catalogPhp = Entries.Where(e => e.Type == "php").ToList();
            if (catalogPhp.Count == 0) return;
            var maxBranch = catalogPhp.Max(e => PackageTypes.ParseVersion(e.Version));
            foreach (var kv in root)
            {
                var branch = kv.Value as Dictionary<string, object>;
                if (branch == null) continue;
                var version = branch.Str("version");
                var v = PackageTypes.ParseVersion(version);
                // update known branches, and add brand new branches (e.g. PHP 8.6) automatically
                bool known = catalogPhp.Any(e => e.Branch == v.Major + "." + v.Minor);
                if (!known && new Version(v.Major, v.Minor) <= new Version(maxBranch.Major, maxBranch.Minor)) continue;
                var build = branch.Keys.Where(k => k.StartsWith("nts-") && k.EndsWith("-x64")).Select(k => branch.Obj(k)).FirstOrDefault();
                var zip = build == null ? null : build.Obj("zip");
                if (zip == null) continue;
                var file = zip.Str("path");
                if (!file.EndsWith(".zip")) continue;
                var folder = file.Substring(0, file.Length - 4);
                var sameBranch = catalogPhp.Where(e => e.Branch == v.Major + "." + v.Minor).ToList();
                if (sameBranch.Any(e => PackageTypes.ParseVersion(e.Version) >= v)) continue;
                bool recommended = sameBranch.Any(e => e.Recommended);
                foreach (var old in sameBranch) Entries.Remove(old);
                var vc = folder.Contains("-vs17-") ? 30 : folder.Contains("-vs16-") ? 20 : folder.Contains("-vs18-") ? 50 : 0;
                Entries.Add(new PackageEntry
                {
                    Type = "php", Version = version, Folder = folder,
                    Url = "https://windows.php.net/downloads/releases/" + file,
                    Sha256 = zip.Str("sha256"), Recommended = recommended, Vc = vc,
                    MinOs = sameBranch.Select(e => e.MinOs).DefaultIfEmpty(10.0).Max(),
                });
            }
        }

        /// <summary>Entries usable on this Windows version.</summary>
        public static IEnumerable<PackageEntry> Available()
        {
            return Entries.Where(e => e.MinOs <= OsInfo.Number + 0.001);
        }

        public static IEnumerable<PackageEntry> ForType(string type)
        {
            return Available().Where(e => e.Type == type).OrderByDescending(e => PackageTypes.ParseVersion(e.Version));
        }

        public static PackageEntry Recommended(string type)
        {
            return ForType(type).FirstOrDefault(e => e.Recommended) ?? ForType(type).FirstOrDefault();
        }

        /// <summary>Newer build of the same branch (8.4.x -> 8.4.y) for an installed folder.</summary>
        public static PackageEntry UpdateFor(string type, string installedFolder)
        {
            var cur = PackageTypes.ParseVersion(installedFolder);
            var branch = PackageTypes.Branch(installedFolder);
            if (type == "composer" || type == "redis") return null;
            return ForType(type).FirstOrDefault(e => e.Branch == branch && PackageTypes.ParseVersion(e.Version) > cur && !e.IsInstalled);
        }

        /// <summary>All installed packages that have an update in the catalog.</summary>
        public static List<Tuple<string, PackageEntry>> PendingUpdates()
        {
            var list = new List<Tuple<string, PackageEntry>>();
            foreach (var t in PackageTypes.All)
                foreach (var folder in PackageTypes.Installed(t.Id))
                {
                    var u = UpdateFor(t.Id, folder);
                    if (u != null && !list.Any(x => x.Item2 == u)) list.Add(Tuple.Create(folder, u));
                }
            return list;
        }

        public static PackageEntry FindByFolder(string type, string folder)
        {
            return Entries.FirstOrDefault(e => e.Type == type && string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase));
        }
    }
}
