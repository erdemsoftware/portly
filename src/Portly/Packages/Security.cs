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
    public class EolEntry
    {
        public string Type, Branch, Note = "";
        public DateTime Eol;
        public bool Lts;
    }

    public class Advisory
    {
        public string Type, Branch, FixedIn, Cve, Severity, Url, Note = "";
    }

    /// <summary>Security state of one installed version (what the card pill and the packages list show).</summary>
    public class SecurityStatus
    {
        public string Type, Folder;
        public DateTime? EolSince;           // set when the branch is past its end of life
        public List<Advisory> Advisories = new List<Advisory>();
        public PackageEntry Fix;             // newer build of the same branch that fixes the advisories
        public bool IsProblem { get { return EolSince != null || Advisories.Count > 0; } }
        /// <summary>Short text for a pill: "End of life" / "Security fix".</summary>
        public string Short { get { return Advisories.Count > 0 ? I18n.T("sec.pillFix") : I18n.T("sec.pillEol"); } }
        /// <summary>Full sentence for the packages list and tooltips.</summary>
        public string Long
        {
            get
            {
                var parts = new List<string>();
                if (Advisories.Count > 0)
                {
                    var cves = string.Join(", ", Advisories.Select(a => a.Cve).Where(c => c != "").Distinct());
                    parts.Add(Fix != null ? I18n.T("sec.fixAvailable", Fix.Version, cves) : I18n.T("sec.vulnerable", cves));
                }
                if (EolSince != null) parts.Add(I18n.T("sec.eolSince", EolSince.Value.ToString("d", CultureInfo.CurrentCulture)));
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// manifest/security.json: end-of-life dates per branch, security advisories and php.ini hotfixes.
    /// Modern edition: refreshed together with the catalog (cached in usr\cache). Legacy: the embedded copy only.
    /// </summary>
    public static class SecurityInfo
    {
        public static List<EolEntry> Eol = new List<EolEntry>();
        public static List<Advisory> Advisories = new List<Advisory>();
        /// <summary>Validated hotfixes (only whitelisted key/value pairs survive parsing).</summary>
        public static Dictionary<string, string> IniHotfixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string Updated = "";

        /// <summary>
        /// The only php.ini changes a remote file may ask for: each one makes PHP stricter, never looser.
        /// Anything else in "iniHotfixes" is ignored, so a tampered manifest cannot turn into code execution.
        /// </summary>
        public static readonly Dictionary<string, string[]> IniWhitelist = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "session.upload_progress.enabled", new[] { "Off", "0" } },
            { "allow_url_include", new[] { "Off", "0" } },
            { "expose_php", new[] { "Off", "0" } },
            { "enable_dl", new[] { "Off", "0" } },
            { "session.use_strict_mode", new[] { "1", "On" } },
            { "session.use_only_cookies", new[] { "1", "On" } },
            { "session.cookie_httponly", new[] { "1", "On" } },
            { "cgi.force_redirect", new[] { "1" } },
        };

        static string CacheFile { get { return Path.Combine(Paths.Cache, "security.json"); } }

        public static void LoadLocal()
        {
            if (!AppInfo.IsLegacy && File.Exists(CacheFile))
            {
                try { Parse(File.ReadAllText(CacheFile, Encoding.UTF8)); return; }
                catch (Exception ex) { Log.Warn("Cached security.json is invalid: " + ex.Message); }
            }
            try { Parse(Embedded()); } catch (Exception ex) { Log.Warn("security.json: " + ex.Message); }
        }

        public static string Embedded()
        {
            using (var s = typeof(SecurityInfo).Assembly.GetManifestResourceStream("security.json"))
            {
                if (s == null) return "{}";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        /// <summary>Download the latest file (modern edition only). The cache is replaced only by a valid file.</summary>
        public static async Task<bool> RefreshAsync()
        {
            if (AppInfo.IsLegacy) return false;
            try
            {
                var json = await Net.GetStringAsync(AppInfo.ManifestBase + "security.json");
                Parse(json);
                Directory.CreateDirectory(Paths.Cache);
                File.WriteAllText(CacheFile, json, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("security.json refresh failed: " + ex.Message);
                if (Eol.Count == 0) LoadLocal();
                return false;
            }
        }

        /// <summary>Parse a security.json text. Throws on a broken file (then the previous data stays).</summary>
        public static void Parse(string json)
        {
            var root = Json.ParseObject(json);
            var eol = new List<EolEntry>();
            foreach (var o in root.Arr("eol"))
            {
                DateTime d;
                if (!DateTime.TryParseExact(o.Str("eol"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
                if (o.Str("type") == "" || o.Str("branch") == "") continue;
                eol.Add(new EolEntry { Type = o.Str("type"), Branch = o.Str("branch"), Eol = d, Lts = o.Bool("lts"), Note = o.Str("note") });
            }
            var adv = new List<Advisory>();
            foreach (var o in root.Arr("advisories"))
            {
                var a = new Advisory { Type = o.Str("type"), Branch = o.Str("branch"), FixedIn = o.Str("fixedIn"), Cve = o.Str("cve"), Severity = o.Str("severity"), Url = o.Str("url"), Note = o.Str("note") };
                if (a.Type == "" || a.Branch == "" || a.FixedIn == "") continue;
                if (a.Url != "" && !a.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) a.Url = "";
                adv.Add(a);
            }
            var hot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var h = root.Obj("iniHotfixes");
            if (h != null)
                foreach (var kv in h)
                {
                    var v = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? "";
                    if (IsAllowedHotfix(kv.Key, v)) hot[kv.Key] = v;
                    else Log.Warn("security.json: ignored php.ini hotfix " + kv.Key);
                }
            if (!root.ContainsKey("eol") && !root.ContainsKey("advisories")) throw new FormatException("no security data");
            Eol = eol;
            Advisories = adv;
            IniHotfixes = hot;
            Updated = root.Str("updated");
        }

        public static bool IsAllowedHotfix(string key, string value)
        {
            string[] allowed;
            return key != null && IniWhitelist.TryGetValue(key.Trim(), out allowed)
                && allowed.Contains((value ?? "").Trim(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Branch key used by security.json and the "new branch" check: major for Node.js and PostgreSQL
        /// (24.x, 18.x), major.minor for everything else (8.4, 1.28, 11.8).
        /// </summary>
        public static string BranchKey(string type, string version)
        {
            var v = ParseLoose(version);
            if (v == null) return "";
            return type == "nodejs" || type == "postgresql" ? v[0].ToString() : v[0] + "." + (v.Length > 1 ? v[1] : 0);
        }

        /// <summary>"18.6" -> {18, 6}; "php-8.4.27-nts" -> {8, 4, 27}; null when no number.</summary>
        public static int[] ParseLoose(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            var list = new List<int>();
            for (int i = 1; i <= 4; i++) if (m.Groups[i].Success) list.Add(int.Parse(m.Groups[i].Value));
            return list.ToArray();
        }

        /// <summary>Compare two loose versions ("8.3.8" vs "8.3.31").</summary>
        public static int Compare(string a, string b)
        {
            var x = ParseLoose(a) ?? new int[0];
            var y = ParseLoose(b) ?? new int[0];
            for (int i = 0; i < 4; i++)
            {
                int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p.CompareTo(q);
            }
            return 0;
        }

        public static EolEntry EolOf(string type, string version)
        {
            var key = BranchKey(type, version);
            return Eol.FirstOrDefault(e => e.Type == type && e.Branch == key);
        }

        public static bool IsLtsBranch(string type, string version)
        {
            var e = EolOf(type, version);
            return e != null && e.Lts;
        }

        /// <summary>Advisories that apply to the version (same branch, older than the fix).</summary>
        public static List<Advisory> AdvisoriesFor(string type, string version)
        {
            var key = BranchKey(type, version);
            return Advisories.Where(a => a.Type == type && a.Branch == key && Compare(version, a.FixedIn) < 0).ToList();
        }

        /// <summary>EOL / advisory state of an installed version folder; null when there is nothing to report.</summary>
        public static SecurityStatus StatusOf(string type, string folder, DateTime? today = null)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            var version = PackageTypes.Label(folder);
            var st = new SecurityStatus { Type = type, Folder = folder };
            var eol = EolOf(type, version);
            var now = (today ?? DateTime.Today).Date;
            if (eol != null && eol.Eol < now) st.EolSince = eol.Eol;
            st.Advisories = AdvisoriesFor(type, version);
            if (st.Advisories.Count > 0 && AppInfo.UpdatesSupported)
            {
                // the newest build of the same branch that is at least the highest fixedIn
                var need = st.Advisories.Select(a => a.FixedIn).OrderByDescending(v => v, Comparer<string>.Create(Compare)).First();
                st.Fix = Catalog.ForType(type).FirstOrDefault(e => BranchKey(type, e.Version) == BranchKey(type, version) && Compare(e.Version, need) >= 0);
            }
            return st.IsProblem ? st : null;
        }

        /// <summary>Called by PhpConfig.Ensure: apply the remote hotfixes (whitelist checked again here).</summary>
        public static void ApplyIniHotfixes(Action<string, string> set)
        {
            foreach (var kv in IniHotfixes.ToList())
                if (IsAllowedHotfix(kv.Key, kv.Value)) set(kv.Key, kv.Value);
        }
    }
}
