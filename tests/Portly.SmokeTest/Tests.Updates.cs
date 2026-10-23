using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Portly.Core;
using Portly.Packages;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>Smoke tests of the "Updates" features. Runs while Apache, PHP and MySQL are up (see Run()).</summary>
        static async Task Tests_Updates(Settings s, string www)
        {
            // ---------- security.json ----------
            try
            {
                SecurityInfo.Parse(SecurityInfo.Embedded());
                Check("security.json parses", SecurityInfo.Eol.Count > 30 && SecurityInfo.Advisories.Count > 0, SecurityInfo.Eol.Count + " EOL, " + SecurityInfo.Advisories.Count + " advisories");
            }
            catch (Exception ex) { Check("security.json parses", false, ex.Message); }

            var old = SecurityInfo.StatusOf("php", "php-7.4.33-nts-Win32-vc15-x64");
            var cur = SecurityInfo.StatusOf("php", "php-8.5.12-nts-Win32-vs17-x64");
            Check("EOL detected for an old branch (PHP 7.4)", old != null && old.EolSince != null && cur == null, old == null ? "" : old.Long);
            var vuln = SecurityInfo.StatusOf("php", "php-8.3.5-nts-Win32-vs16-x64");
            Check("advisory detected (PHP 8.3.5 -> CVE-2024-4577)", vuln != null && vuln.Advisories.Any(a => a.Cve == "CVE-2024-4577") && vuln.Fix != null, vuln == null ? "" : vuln.Long);
            Check("node / postgres branch = major", SecurityInfo.BranchKey("nodejs", "24.22.1") == "24" && SecurityInfo.BranchKey("postgresql", "18.6") == "18" && SecurityInfo.BranchKey("php", "php-8.4.27-nts") == "8.4");

            // ---------- php.ini hotfix whitelist ----------
            SecurityInfo.Parse("{\"eol\":[],\"advisories\":[],\"iniHotfixes\":{\"allow_url_include\":\"On\",\"auto_prepend_file\":\"C:/evil.php\",\"expose_php\":\"Off\"}}");
            Check("hotfix whitelist rejects unsafe keys / values",
                !SecurityInfo.IniHotfixes.ContainsKey("allow_url_include") && !SecurityInfo.IniHotfixes.ContainsKey("auto_prepend_file") && SecurityInfo.IniHotfixes.ContainsKey("expose_php"),
                string.Join(",", SecurityInfo.IniHotfixes.Keys));
            SecurityInfo.LoadLocal();

            // ---------- integrity: a deleted DLL in a copied package folder ----------
            var phpDir = PackageTypes.ActiveDir("php");
            var copy = Path.Combine(Paths.Tmp, "updates-test", "php", "copy");
            try
            {
                if (Directory.Exists(Path.GetDirectoryName(copy))) Directory.Delete(Path.GetDirectoryName(copy), true);
                UpdatesCopyTree(phpDir, copy);
                Integrity.Record(copy);
                var clean = Integrity.Check(copy, false);
                var dll = Directory.GetFiles(Path.Combine(copy, "ext"), "*.dll").First();
                File.Delete(dll);
                var quick = Integrity.Check(copy, true);
                Check("integrity check finds a deleted DLL", clean != null && clean.Count == 0 && quick != null && quick.Missing.Any(m => m.EndsWith(Path.GetFileName(dll))), quick == null ? "no list" : string.Join(",", quick.Missing));
            }
            catch (Exception ex) { Check("integrity check finds a deleted DLL", false, ex.Message); }
            finally { try { Directory.Delete(Path.Combine(Paths.Tmp, "updates-test"), true); } catch { } }

            // ---------- unused versions ----------
            var fakeInstalled = new Dictionary<string, List<string>>
            {
                { "php", new List<string> { "php-8.5.12", "php-8.4.27", "php-8.3.36" } },
                { "apache", new List<string> { "httpd-2.4.69" } },
                { "nodejs", new List<string> { "node-v24.22.1", "node-v22.23.4" } },
            };
            var fakeActive = new Dictionary<string, string> { { "php", "php-8.5.12" }, { "apache", "httpd-2.4.69" }, { "nodejs", "node-v24.22.1" } };
            var unused = Cleanup.Find(fakeInstalled.Keys, t => fakeInstalled[t], t => fakeActive[t], new HashSet<string> { "php-8.4.27" }, t => Path.Combine(Paths.Tmp, "nope", t));
            Check("unused versions: not active, not used by a site",
                unused.Select(u => u.Folder).OrderBy(x => x).SequenceEqual(new[] { "node-v22.23.4", "php-8.3.36" }), string.Join(", ", unused.Select(u => u.Folder)));

            // ---------- new branch / patch detection with a fake catalog ----------
            var fake = new List<PackageEntry>
            {
                new PackageEntry { Type = "php", Version = "8.6.0", Folder = "php-8.6.0-nts-Win32-vs17-x64" },
                new PackageEntry { Type = "php", Version = "8.6.1RC1", Folder = "php-8.6.1RC1-nts-Win32-vs17-x64" },
                new PackageEntry { Type = "php", Version = "8.5.13", Folder = "php-8.5.13-nts-Win32-vs17-x64", Recommended = true },
                new PackageEntry { Type = "php", Version = "8.4.27", Folder = "php-8.4.27-nts-Win32-vs17-x64" },
                new PackageEntry { Type = "mysql", Version = "26.7.0", Folder = "mysql-26.7.0-winx64", Tag = "newest" },
                new PackageEntry { Type = "mysql", Version = "9.7.4", Folder = "mysql-9.7.4-winx64" },
                new PackageEntry { Type = "mysql", Version = "8.4.11", Folder = "mysql-8.4.11-winx64", Tag = "LTS", Recommended = true },
                new PackageEntry { Type = "nginx", Version = "1.31.8", Folder = "nginx-1.31.8", Tag = "mainline" },
                new PackageEntry { Type = "nginx", Version = "1.30.6", Folder = "nginx-1.30.6", Tag = "stable", Recommended = true },
            };
            var inst = new Dictionary<string, List<string>>
            {
                { "php", new List<string> { "php-8.5.12-nts-Win32-vs17-x64" } },
                { "mysql", new List<string> { "mysql-8.4.11-winx64" } },
                { "nginx", new List<string> { "nginx-1.30.6" } },
            };
            var notices = UpdateNotices.Compute(fake, t => inst.ContainsKey(t) ? inst[t] : new List<string>(), t => inst.ContainsKey(t) ? inst[t][0] : "", (t, v) => false);
            var line = string.Join("; ", notices.Select(n => n.Type + " " + n.Entry.Version + (n.NewBranch ? " (branch)" : " (patch)")));
            Check("new branch: PHP 8.6.0 when 8.5 is installed (no RC)", notices.Any(n => n.Type == "php" && n.NewBranch && n.Entry.Version == "8.6.0"), line);
            Check("patch: PHP 8.5.12 -> 8.5.13", notices.Any(n => n.Type == "php" && !n.NewBranch && n.Entry.Version == "8.5.13" && n.OldFolder.Contains("8.5.12")), line);
            Check("MySQL: next LTS 9.7 (not the innovation 26.7), install only", notices.Any(n => n.Type == "mysql" && n.Entry.Version == "9.7.4" && n.InstallOnly) && !notices.Any(n => n.Entry.Version == "26.7.0"), line);
            Check("nginx stable: mainline is not offered", !notices.Any(n => n.Type == "nginx"), line);
            var skipped = UpdateNotices.Compute(fake, t => inst.ContainsKey(t) ? inst[t] : new List<string>(), t => "", (t, v) => v == "8.6.0");
            Check("skipped version is hidden", !skipped.Any(n => n.Entry.Version == "8.6.0"));

            // ---------- proxy "None": a direct download works ----------
            var mode = s.ProxyMode;
            try
            {
                s.ProxyMode = "none";
                var json = await Net.GetStringAsync("https://windows.php.net/downloads/releases/releases.json");
                Check("proxy None: direct download works", Proxy.Current() == null && json.Contains("\"version\""));
                var err = await Proxy.TestAsync("https://windows.php.net/downloads/releases/releases.json", null);
                Check("proxy test connection (direct)", err == null, err ?? "");
                var bad = await Proxy.TestAsync("https://does-not-exist.portly-invalid.example/", null);
                // some routers answer every name (DNS hijack): then the TLS check is what fails
                Func<string, string> head = k => I18n.T(k, "\u0001").Split('\u0001')[0];
                Check("proxy test explains an unknown host in plain words", bad != null && (bad == I18n.T("proxy.errDns") || bad.StartsWith(head("proxy.errTls")) || bad.StartsWith(head("proxy.errConnect"))), bad ?? "");
            }
            catch (Exception ex) { Check("proxy None: direct download works", false, ex.Message); }
            finally { s.ProxyMode = mode; s.Save(); }
        }

        static void UpdatesCopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) UpdatesCopyTree(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
