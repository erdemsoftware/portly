using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    /// <summary>One download of an extension for one PHP branch (NTS x64).</summary>
    public class ExtBuild
    {
        public string Php;        // "8.4"
        public string Version;    // extension version
        public string Vs;         // vc15 | vs16 | vs17
        public string Kind = "zip";
        public string Url;
        public string Sha256 = "";
        public string Dll;        // extension DLL (path inside the zip, or file name for kind=dll)
        public List<string> Deps = new List<string>();  // extra DLLs that must sit next to php.exe
    }

    public class ExtInfo
    {
        public string Id;
        public string Name;
        public string Description = "";
        public string Note = "";
        public bool Zend;
        public List<string> Requires = new List<string>();
        public List<ExtBuild> Builds = new List<ExtBuild>();
        public Dictionary<string, string> Missing = new Dictionary<string, string>();

        public ExtBuild BuildFor(PhpBuild b)
        {
            return Builds.FirstOrDefault(x => x.Php == b.Branch && (b.Compiler == "" || string.Equals(x.Vs, b.Compiler, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>What a PHP installation is: an extension DLL must match all of it or PHP refuses to start.</summary>
    public class PhpBuild
    {
        public string Folder;
        public string Branch = "";      // 8.4
        public string Version = "";     // 8.4.12
        public bool ThreadSafe;
        public string Compiler = "";    // vs17
        public string Arch = "x64";
        public string Api = "";         // API20240924
        public override string ToString()
        {
            return "PHP " + Version + " " + (ThreadSafe ? "TS" : "NTS") + " " + Compiler.ToUpperInvariant() + " " + Arch + (Api != "" ? " (" + Api + ")" : "");
        }
    }

    /// <summary>
    /// PHP extension installer. PECL no longer offers a Windows download page that is easy to use, and a DLL built for
    /// another PHP branch / compiler / thread mode stops PHP from starting. manifest/extensions.json lists verified
    /// NTS x64 builds per PHP branch; this class picks the matching one, installs it, checks that PHP still starts
    /// (php -m) and rolls back otherwise. Files installed for an extension are recorded in &lt;php&gt;\portly-extensions.ini
    /// so they can be removed again.
    /// </summary>
    public static class PhpExtensions
    {
        public static List<ExtInfo> All = new List<ExtInfo>();
        public static string Updated = "";
        static bool _loaded, _refreshed;

        static string CacheFile { get { return Path.Combine(Paths.Cache, "extensions.json"); } }
        static string RecordFile(string phpDir) { return Path.Combine(phpDir, "portly-extensions.ini"); }

        // ---------------- catalog ----------------
        public static void LoadLocal()
        {
            if (_loaded) return;
            _loaded = true;
            if (!AppInfo.IsLegacy && File.Exists(CacheFile))
            {
                try { Parse(File.ReadAllText(CacheFile, Encoding.UTF8)); return; }
                catch (Exception ex) { Log.Warn("Cached extension list is invalid: " + ex.Message); }
            }
            try
            {
                using (var s = typeof(PhpExtensions).Assembly.GetManifestResourceStream("extensions.json"))
                using (var r = new StreamReader(s, Encoding.UTF8)) Parse(r.ReadToEnd());
            }
            catch (Exception ex) { Log.Warn("Extension list: " + ex.Message); }
        }

        /// <summary>Fetch the newest list from the same place as the package catalog (modern edition, once per run).</summary>
        public static async Task RefreshAsync()
        {
            LoadLocal();
            if (AppInfo.IsLegacy || _refreshed) return;
            _refreshed = true;
            try
            {
                var json = await Net.GetStringAsync(AppInfo.ManifestBase + "extensions.json");
                Parse(json); // throws when invalid -> cache is not overwritten
                File.WriteAllText(CacheFile, json, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("Extension list refresh failed: " + ex.Message); }
        }

        public static void Parse(string json)
        {
            var root = Json.ParseObject(json);
            var list = new List<ExtInfo>();
            foreach (var e in root.Arr("extensions"))
            {
                var x = new ExtInfo
                {
                    Id = e.Str("id"), Name = e.Str("name"), Description = e.Str("description"), Note = e.Str("note"), Zend = e.Bool("zend"),
                };
                // the id ends up in php.ini and in file names: only plain names
                if (!Regex.IsMatch(x.Id, "^[a-z0-9_]+$")) continue;
                if (x.Name == "") x.Name = x.Id;
                x.Requires = Strings(e, "requires").Where(r => Regex.IsMatch(r, "^[a-z0-9_]+$")).ToList();
                foreach (var b in e.Arr("builds"))
                {
                    var build = new ExtBuild
                    {
                        Php = b.Str("php"), Version = b.Str("version"), Vs = b.Str("vs").ToLowerInvariant(), Kind = b.Str("kind", "zip"),
                        Url = b.Str("url"), Sha256 = b.Str("sha256"), Dll = b.Str("dll", "php_" + x.Id + ".dll"),
                        Deps = Strings(b, "deps").Where(SafeRelative).ToList(),
                    };
                    if (!build.Url.StartsWith("https://") || build.Php == "" || !SafeRelative(build.Dll)) continue;
                    x.Builds.Add(build);
                }
                var missing = e.Obj("missing");
                if (missing != null) foreach (var kv in missing) x.Missing[kv.Key] = Convert.ToString(kv.Value);
                list.Add(x);
            }
            if (list.Count == 0) throw new FormatException("empty extension list");
            All = list;
            Updated = root.Str("updated");
        }

        static List<string> Strings(Dictionary<string, object> d, string key)
        {
            object v;
            var list = new List<string>();
            if (d.TryGetValue(key, out v) && v is System.Collections.IEnumerable && !(v is string))
                foreach (var o in (System.Collections.IEnumerable)v) if (o != null) list.Add(o.ToString());
            return list;
        }

        static bool SafeRelative(string p)
        {
            return p != "" && !p.Contains("..") && !Path.IsPathRooted(p) && p.IndexOf(':') < 0;
        }

        public static ExtInfo Get(string id) { LoadLocal(); return All.FirstOrDefault(e => e.Id == id); }

        // ---------------- PHP build detection ----------------
        static readonly Dictionary<string, PhpBuild> _builds = new Dictionary<string, PhpBuild>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Thread safety, compiler, architecture and API version of a PHP folder (php -i, else the folder name).</summary>
        public static PhpBuild Detect(string phpDir)
        {
            lock (_builds)
            {
                PhpBuild cached;
                if (_builds.TryGetValue(phpDir, out cached)) return cached;
            }
            var folder = Path.GetFileName(phpDir);
            var b = new PhpBuild { Folder = folder, Branch = PackageTypes.Branch(folder), Version = PackageTypes.Label(folder) };
            var f = folder.ToLowerInvariant();
            b.ThreadSafe = !f.Contains("-nts-") && f.Contains("-win32-");
            var m = Regex.Match(f, @"-(vs1[5-9]|vc1[45])-");
            if (m.Success) b.Compiler = m.Groups[1].Value;
            if (f.EndsWith("-x86")) b.Arch = "x86";
            var exe = Path.Combine(phpDir, "php.exe");
            if (File.Exists(exe))
            {
                // -n: no php.ini, so a broken extension line cannot hide the build information
                var r = ProcessUtil.Run(exe, "-n -i", phpDir, null, 15000);
                foreach (var line in r.Output.Split('\n'))
                {
                    var parts = line.Split(new[] { "=>" }, StringSplitOptions.None);
                    if (parts.Length < 2) continue;
                    var key = parts[0].Trim();
                    var val = parts[1].Trim();
                    if (key == "PHP Version") { b.Version = val; b.Branch = PackageTypes.Branch(val); }
                    else if (key == "Thread Safety") b.ThreadSafe = val.StartsWith("enabled", StringComparison.OrdinalIgnoreCase);
                    else if (key == "Architecture") b.Arch = val;
                    else if (key == "PHP Extension Build")
                    {
                        // API20240924,NTS,VS17
                        var p = val.Split(',');
                        b.Api = p[0];
                        if (p.Length > 2) b.Compiler = p[2].Trim().ToLowerInvariant();
                    }
                }
            }
            lock (_builds) _builds[phpDir] = b;
            return b;
        }

        /// <summary>Why no download fits this PHP, or null when one does.</summary>
        public static string Unavailable(ExtInfo x, PhpBuild b)
        {
            if (b.ThreadSafe) return I18n.T("ext.onlyNts");
            if (!string.Equals(b.Arch, "x64", StringComparison.OrdinalIgnoreCase)) return I18n.T("ext.onlyX64");
            if (x.BuildFor(b) != null) return null;
            string why;
            if (x.Missing.TryGetValue(b.Branch, out why) && why != "") return I18n.T("ext.noBuildWhy", b.Branch, why);
            return I18n.T("ext.noBuild", b.Branch);
        }

        // ---------------- state ----------------
        public static bool IsInstalled(string phpDir, string id)
        {
            return File.Exists(Path.Combine(phpDir, "ext", "php_" + id + ".dll"));
        }

        /// <summary>Extensions Portly installed into this PHP (from the record file).</summary>
        public static List<string> InstalledByPortly(string phpDir)
        {
            var file = RecordFile(phpDir);
            if (!File.Exists(file)) return new List<string>();
            return new Ini(file).Sections().Where(s => IsInstalled(phpDir, s)).ToList();
        }

        public static string InstalledVersion(string phpDir, string id)
        {
            var file = RecordFile(phpDir);
            return File.Exists(file) ? new Ini(file).Get(id, "version", "") : "";
        }

        // ---------------- install / remove ----------------
        /// <summary>
        /// Download, verify, copy the DLLs, enable in php.ini and check with "php -m" that PHP still starts and loads it.
        /// On failure every change is undone and the exception explains why. Does not restart the web server.
        /// </summary>
        public static async Task InstallAsync(string phpDir, string id, Action<string> status, CancellationToken ct)
        {
            LoadLocal();
            var x = Get(id);
            if (x == null) throw new Exception(I18n.T("ext.unknown", id));
            var build = Detect(phpDir);
            var why = Unavailable(x, build);
            if (why != null) throw new Exception(why);
            foreach (var req in x.Requires)
            {
                if (!IsInstalled(phpDir, req) && Get(req) != null) await InstallAsync(phpDir, req, status, ct);
                else if (IsInstalled(phpDir, req) && !IsEnabled(phpDir, req)) PhpConfig.ToggleExtension(phpDir, req, true);
            }

            var b = x.BuildFor(build);
            var download = Path.Combine(Paths.Downloads, "ext-" + id + "-" + b.Version + "-" + b.Php + "-" + b.Vs + (b.Kind == "zip" ? ".zip" : ".dll"));
            if (status != null) status(I18n.T("log.downloading", x.Name + " " + b.Version));
            bool cached = File.Exists(download) && b.Sha256 != "" && Net.Sha256(download).Equals(b.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!cached)
            {
                await Net.DownloadFileAsync(b.Url, download, null, ct);
                if (b.Sha256 != "" && !Net.Sha256(download).Equals(b.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(download);
                    throw new Exception(I18n.T("err.checksum"));
                }
            }
            ct.ThrowIfCancellationRequested();
            if (status != null) status(I18n.T("ext.installing", x.Name));

            var ini = PhpConfig.IniPath(phpDir);
            PhpConfig.Ensure(phpDir);
            var iniBackup = File.ReadAllBytes(ini);
            var written = new List<string>();      // files we created (removed again on rollback)
            var target = Path.Combine(phpDir, "ext", "php_" + id + ".dll");
            try
            {
                await Task.Run(() =>
                {
                    if (b.Kind == "dll") { CopyNew(download, target, true, written); return; }
                    using (var zip = ZipFile.OpenRead(download))
                    {
                        var dll = Find(zip, b.Dll);
                        if (dll == null) throw new Exception(I18n.T("ext.dllMissing", b.Dll));
                        Extract(dll, target, true, written);
                        foreach (var dep in b.Deps)
                        {
                            var e = Find(zip, dep);
                            // never replace a DLL that PHP itself ships (libssl, libcrypto ...)
                            if (e != null) Extract(e, Path.Combine(phpDir, Path.GetFileName(dep)), false, written);
                        }
                    }
                });
                if (x.Zend) EnableZend(ini, id);
                else PhpConfig.ToggleExtension(phpDir, id, true);

                var check = await Task.Run(() => CheckLoads(phpDir, id));
                if (check != null) throw new Exception(I18n.T("ext.startFailed", x.Name, check));

                var rec = new Ini(RecordFile(phpDir));
                rec.Set(id, "version", b.Version);
                rec.Set(id, "files", string.Join("|", written.Select(f => f.Substring(phpDir.Length + 1))));
                rec.Save();
                TryDelete(download);
                Log.Info(I18n.T("ext.installed", x.Name + " " + b.Version, PackageTypes.Label(Path.GetFileName(phpDir))));
            }
            catch
            {
                // roll back: php.ini as it was, and only the files this install created
                try { File.WriteAllBytes(ini, iniBackup); } catch { }
                foreach (var f in written) TryDelete(f);
                throw;
            }
        }

        /// <summary>Disable in php.ini and delete the files Portly installed for it (shared DLLs stay while still used).</summary>
        public static void Remove(string phpDir, string id)
        {
            var x = Get(id);
            if (x != null && x.Zend) DisableZend(PhpConfig.IniPath(phpDir), id);
            else if (File.Exists(PhpConfig.IniPath(phpDir))) PhpConfig.ToggleExtension(phpDir, id, false);
            var recFile = RecordFile(phpDir);
            var rec = new Ini(recFile);
            var files = rec.Get(id, "files", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (files.Count == 0) files.Add(Path.Combine("ext", "php_" + id + ".dll"));
            var stillUsed = new HashSet<string>(rec.Sections().Where(s => s != id)
                .SelectMany(s => rec.Get(s, "files", "").Split('|')), StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (stillUsed.Contains(f) || !SafeRelative(f)) continue;
                TryDelete(Path.Combine(phpDir, f));
            }
            rec.RemoveSection(id);
            rec.Save();
            Log.Info(I18n.T("ext.removed", x != null ? x.Name : id, PackageTypes.Label(Path.GetFileName(phpDir))));
        }

        public static bool IsEnabled(string phpDir, string id)
        {
            return PhpConfig.ListExtensions(phpDir).Any(kv => kv.Key.Equals(id, StringComparison.OrdinalIgnoreCase) && kv.Value);
        }

        /// <summary>null when "php -m" runs cleanly and lists the module; otherwise the reason (startup warning).</summary>
        public static string CheckLoads(string phpDir, string id)
        {
            var exe = Path.Combine(phpDir, "php.exe");
            var env = new Dictionary<string, string> { { "PATH", phpDir + ";" + Environment.GetEnvironmentVariable("PATH") }, { "PHPRC", phpDir } };
            var r = ProcessUtil.Run(exe, "-c \"" + PhpConfig.IniPath(phpDir) + "\" -m", phpDir, env, 30000);
            if (r.TimedOut) return I18n.T("err.timeout");
            var lines = r.Output.Split('\n').Select(l => l.Trim()).ToList();
            var warning = lines.FirstOrDefault(l => l.IndexOf("PHP Startup", StringComparison.OrdinalIgnoreCase) >= 0
                && l.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
            if (warning != null) return warning;
            if (r.ExitCode != 0) return r.Output.Trim().Split('\n').FirstOrDefault() ?? ("exit " + r.ExitCode);
            if (!lines.Any(l => l.Equals(id, StringComparison.OrdinalIgnoreCase) || l.Replace(" ", "_").Equals(id, StringComparison.OrdinalIgnoreCase)))
                return I18n.T("ext.notListed", id);
            return null;
        }

        /// <summary>Downloadable extensions installed in one PHP that the other one lacks: (available, not available + reason).</summary>
        public static Tuple<List<ExtInfo>, List<string>> PlanCopy(string fromDir, string toDir)
        {
            LoadLocal();
            var ok = new List<ExtInfo>();
            var no = new List<string>();
            var target = Detect(toDir);
            foreach (var id in InstalledByPortly(fromDir))
            {
                if (IsInstalled(toDir, id)) continue;
                var x = Get(id);
                if (x == null) continue;
                var why = Unavailable(x, target);
                if (why == null) ok.Add(x); else no.Add(x.Name + ": " + why);
            }
            return Tuple.Create(ok, no);
        }

        // ---------------- helpers ----------------
        static ZipArchiveEntry Find(ZipArchive zip, string rel)
        {
            var want = rel.Replace('\\', '/');
            return zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(want, StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e => e.Name.Equals(Path.GetFileName(want), StringComparison.OrdinalIgnoreCase));
        }

        static void Extract(ZipArchiveEntry e, string target, bool overwrite, List<string> written)
        {
            if (File.Exists(target) && !overwrite) return;
            bool existed = File.Exists(target);
            e.ExtractToFile(target, true);
            if (!existed) written.Add(target);
        }

        static void CopyNew(string src, string target, bool overwrite, List<string> written)
        {
            if (File.Exists(target) && !overwrite) return;
            bool existed = File.Exists(target);
            File.Copy(src, target, true);
            if (!existed) written.Add(target);
        }

        static void EnableZend(string ini, string id)
        {
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            var on = new Regex(@"^\s*zend_extension\s*=\s*""?(php_)?" + Regex.Escape(id) + @"(\.dll)?""?\s*$", RegexOptions.IgnoreCase);
            if (lines.Any(l => on.IsMatch(l))) return;
            lines.Add("zend_extension=" + id);
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
        }

        static void DisableZend(string ini, string id)
        {
            if (!File.Exists(ini)) return;
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            var on = new Regex(@"^\s*zend_extension\s*=\s*""?(php_)?" + Regex.Escape(id) + @"(\.dll)?""?\s*$", RegexOptions.IgnoreCase);
            for (int i = 0; i < lines.Count; i++) if (on.IsMatch(lines[i])) lines[i] = ";" + lines[i].TrimStart();
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }
}
