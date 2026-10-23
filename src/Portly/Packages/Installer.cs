using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;

namespace Portly.Packages
{
    public class InstallProgress
    {
        public string Stage;   // download | extract | configure | done
        public int Percent;    // -1 = unknown
        public long Bytes;
    }

    public static class Installer
    {
        /// <summary>Download, verify, extract and configure a package. Safe to call for an already installed one.</summary>
        public static async Task InstallAsync(PackageEntry e, Action<InstallProgress> progress, CancellationToken ct, string copySettingsFrom = null)
        {
            var target = e.InstallDir;
            if (Directory.Exists(target)) { Log.Info(e.TypeName + " " + e.Version + " is already installed"); return; }
            // a database without a pinned version follows the newest folder: installing a newer one would switch
            // to an empty data dir silently ("my databases vanished"). Pin the current one; switching stays explicit.
            if ((e.Type == "mysql" || e.Type == "mariadb" || e.Type == "postgresql" || e.Type == "mongodb") && Settings.Current.GetVersion(e.Type) == "")
            {
                var current = PackageTypes.ActiveFolder(e.Type);
                if (current != "") { Settings.Current.SetVersion(e.Type, current); Settings.Current.Save(); }
            }
            Directory.CreateDirectory(Paths.BinOf(e.Type));

            var fileName = e.Kind == "file" ? (e.FileName != "" ? e.FileName : UrlFileName(e.Url)) : e.Folder + ".zip";
            var download = Path.Combine(Paths.Downloads, e.Type + "-" + e.Folder + "-" + UrlFileName(e.Url));
            Report(progress, "download", 0, 0);
            Log.Info(I18n.T("log.downloading", e.TypeName + " " + e.Version));

            bool cached = File.Exists(download) && (e.Sha256 == "" || Net.Sha256(download).Equals(e.Sha256, StringComparison.OrdinalIgnoreCase));
            if (!cached)
            {
                string lastError = null;
                foreach (var url in CandidateUrls(e))
                {
                    try
                    {
                        await Net.DownloadFileAsync(url, download, (p, b) => Report(progress, "download", p, b), ct);
                        lastError = null;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { lastError = ex.Message; Log.Warn(url + " -> " + ex.Message); }
                }
                if (lastError != null) throw new Exception(I18n.T("err.download", lastError));
                if (e.Sha256 != "")
                {
                    var actual = Net.Sha256(download);
                    if (!actual.Equals(e.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(download);
                        throw new Exception(I18n.T("err.checksum"));
                    }
                }
            }
            ct.ThrowIfCancellationRequested();

            if (e.Kind == "file")
            {
                Directory.CreateDirectory(target);
                File.Copy(download, Path.Combine(target, fileName), true);
            }
            else if (e.Kind == "sfx")
            {
                // 7-Zip self-extracting archive (PortableGit): "-y -o<dir>" extracts silently
                Report(progress, "extract", -1, 0);
                var extract = Path.Combine(Paths.Tmp, "extract", Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(extract);
                var r = await Task.Run(() => ProcessUtil.Run(download, "-y -o\"" + extract + "\"", Path.GetDirectoryName(download), null, 15 * 60 * 1000), ct);
                var root = FindRoot(extract, e.Subdir);
                if (!r.Ok && !Directory.EnumerateFileSystemEntries(root).Any()) throw new Exception(r.Output);
                MoveDir(root, target);
                TryDeleteDir(extract);
            }
            else
            {
                Report(progress, "extract", 0, 0);
                var extract = Path.Combine(Paths.Tmp, "extract", Guid.NewGuid().ToString("N").Substring(0, 8));
                await Task.Run(() => ExtractZip(download, extract, p => Report(progress, "extract", p, 0), ct), ct);
                var root = FindRoot(extract, e.Subdir);
                MoveDir(root, target);
                TryDeleteDir(extract);
            }
            TryDelete(download);

            Report(progress, "configure", -1, 0);
            await Task.Run(() => PostInstall(e, copySettingsFrom));
            await Task.Run(() => Integrity.Record(target)); // file list for the antivirus quarantine check

            if (PackageTypes.ActiveFolder(e.Type) == "" || Settings.Current.GetVersion(e.Type) == "")
            {
                Settings.Current.SetVersion(e.Type, e.Folder);
                Settings.Current.Save();
            }
            Log.Info(I18n.T("log.installed", e.TypeName + " " + e.Version));
            Report(progress, "done", 100, 0);
        }

        /// <summary>
        /// Manual install of a version that is not in the catalog: a .zip, an extracted folder, or a single file
        /// (composer.phar, adminer.php). The result lands in bin\&lt;type&gt;\&lt;name&gt; like any other version.
        /// Returns the folder name.
        /// </summary>
        public static string InstallLocal(string type, string source)
        {
            var t = PackageTypes.Get(type);
            string name, root, tmp = null;
            bool isDir = Directory.Exists(source);
            if (!isDir && !File.Exists(source)) throw new FileNotFoundException(source);

            if (isDir)
            {
                root = FindRoot(source, type == "apache" ? "Apache24" : "");
                name = Path.GetFileName(source.TrimEnd('\\', '/'));
            }
            else if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                tmp = Path.Combine(Paths.Tmp, "extract", Guid.NewGuid().ToString("N").Substring(0, 8));
                ExtractZip(source, tmp, p => { }, CancellationToken.None);
                root = FindRoot(tmp, type == "apache" ? "Apache24" : "");
                name = Path.GetFileNameWithoutExtension(source);
            }
            else
            {
                // single-file tools: composer.phar, adminer-x.y.php ...
                name = Path.GetFileNameWithoutExtension(source);
                if (type == "composer" && !name.StartsWith("composer", StringComparison.OrdinalIgnoreCase)) name = "composer-" + name;
                tmp = Path.Combine(Paths.Tmp, "extract", Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tmp);
                File.Copy(source, Path.Combine(tmp, t.DetectFile));
                root = tmp;
            }

            if (t.DetectFile != "" && !File.Exists(Path.Combine(root, t.DetectFile)))
            {
                if (tmp != null) TryDeleteDir(tmp);
                throw new Exception(I18n.T("err.manualInvalid", t.Name, t.DetectFile));
            }
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            var target = Path.Combine(Paths.BinOf(type), name);
            if (Directory.Exists(target))
            {
                if (tmp != null) TryDeleteDir(tmp);
                throw new Exception(I18n.T("err.manualExists", name));
            }
            Directory.CreateDirectory(Paths.BinOf(type));
            if (tmp != null) { MoveDir(root, target); TryDeleteDir(tmp); }
            else CopyDir(root, target);

            PostInstall(new PackageEntry { Type = type, Folder = name, Version = PackageTypes.Label(name) }, null);
            Integrity.Record(target);
            if (Settings.Current.GetVersion(type) == "") { Settings.Current.SetVersion(type, name); Settings.Current.Save(); }
            Log.Info(I18n.T("log.installed", t.Name + " " + PackageTypes.Label(name)));
            return name;
        }

        /// <summary>
        /// Move a freshly extracted folder. Antivirus scanners (Windows Defender) often hold files of a new
        /// folder for a moment, which makes Directory.Move fail with "access denied": retry, then copy.
        /// </summary>
        public static void MoveDir(string from, string to)
        {
            for (int i = 0; i < 6; i++)
            {
                try { Directory.Move(from, to); return; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                System.Threading.Thread.Sleep(700);
            }
            CopyDir(from, to);
            TryDeleteDir(from);
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        static System.Collections.Generic.IEnumerable<string> CandidateUrls(PackageEntry e)
        {
            yield return e.Url;
            // windows.php.net moves superseded builds to /archives/
            if (e.Url.Contains("windows.php.net/downloads/releases/") && !e.Url.Contains("/archives/"))
                yield return e.Url.Replace("/downloads/releases/", "/downloads/releases/archives/");
            // the Apache CDN keeps only the newest releases; archive.apache.org keeps all of them
            if (e.Url.StartsWith("https://dlcdn.apache.org/", StringComparison.OrdinalIgnoreCase))
                yield return "https://archive.apache.org/dist/" + e.Url.Substring("https://dlcdn.apache.org/".Length);
        }

        static string UrlFileName(string url)
        {
            var u = url.Split('?')[0];
            var n = u.Substring(u.LastIndexOf('/') + 1);
            return n == "" ? "download.bin" : n;
        }

        static void ExtractZip(string zip, string dest, Action<int> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(dest);
            var destFull = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zip))
            {
                int total = archive.Entries.Count, done = 0, lastPct = -1;
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = Path.GetFullPath(Path.Combine(dest, entry.FullName));
                    if (!path.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip protection
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) Directory.CreateDirectory(path);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        entry.ExtractToFile(path, true);
                    }
                    done++;
                    int pct = total == 0 ? 100 : done * 100 / total;
                    if (pct != lastPct) { lastPct = pct; progress(pct); }
                }
            }
        }

        /// <summary>Zips often wrap everything in one folder (node-v24.../, Apache24/): use the real root.</summary>
        static string FindRoot(string extract, string subdir)
        {
            if (subdir != "")
            {
                var s = Path.Combine(extract, subdir);
                if (Directory.Exists(s)) return s;
            }
            var dir = extract;
            for (int depth = 0; depth < 3; depth++)
            {
                var dirs = Directory.GetDirectories(dir);
                var files = Directory.GetFiles(dir).Where(f => !IsReadme(f)).ToArray();
                if (dirs.Length == 1 && files.Length == 0) dir = dirs[0];
                else break;
            }
            return dir;
        }

        static bool IsReadme(string f)
        {
            var n = Path.GetFileName(f).ToLowerInvariant();
            return n.EndsWith(".txt") || n.EndsWith(".md") || n.EndsWith(".html") || n.StartsWith("--") || n.StartsWith("readme") || n.StartsWith("license");
        }

        static void PostInstall(PackageEntry e, string copySettingsFrom)
        {
            var dir = e.InstallDir;
            switch (e.Type)
            {
                case "php":
                    if (copySettingsFrom != null)
                    {
                        var oldIni = Path.Combine(Paths.BinOf("php"), copySettingsFrom, "php.ini");
                        if (File.Exists(oldIni) && PackageTypes.Branch(copySettingsFrom) == e.Branch)
                            File.Copy(oldIni, Path.Combine(dir, "php.ini"), true);
                    }
                    PhpConfig.Ensure(dir);
                    break;
                case "composer":
                    File.WriteAllText(Path.Combine(dir, "composer.bat"), "@echo off\r\nphp \"%~dp0composer.phar\" %*\r\n");
                    break;
                case "wpcli":
                    File.WriteAllText(Path.Combine(dir, "wp.bat"), "@echo off\r\nphp \"%~dp0wp-cli.phar\" %*\r\n");
                    break;
                case "heidisql":
                    // settings next to the exe instead of the registry (portable mode)
                    File.WriteAllText(Path.Combine(dir, "portable.lock"), "");
                    break;
                case "git":
                    // PortableGit finishes its setup on first start; run it once now, hidden
                    if (File.Exists(Path.Combine(dir, "post-install.bat")))
                        ProcessUtil.Run(Path.Combine(dir, "git-bash.exe"), "--no-needs-console --hide --no-cd --command=post-install.bat", dir, null, 120000);
                    break;
                case "phpmyadmin":
                    ToolApps.ConfigurePhpMyAdmin(dir);
                    break;
            }
        }

        /// <summary>Install a newer build of the same branch and switch to it when the old one was active.</summary>
        public static async Task UpdateAsync(string oldFolder, PackageEntry e, Action<InstallProgress> progress, CancellationToken ct)
        {
            await InstallAsync(e, progress, ct, oldFolder);
            if (string.Equals(PackageTypes.ActiveFolder(e.Type), oldFolder, StringComparison.OrdinalIgnoreCase))
            {
                Settings.Current.SetVersion(e.Type, e.Folder);
                Settings.Current.Save();
            }
        }

        public static void Remove(string type, string folder)
        {
            var dir = Path.Combine(Paths.BinOf(type), folder);
            if (!Directory.Exists(dir)) return;
            Directory.Delete(dir, true);
            if (string.Equals(Settings.Current.GetVersion(type), folder, StringComparison.OrdinalIgnoreCase))
            {
                Settings.Current.SetVersion(type, PackageTypes.ActiveFolder(type));
                Settings.Current.Save();
            }
            Log.Info(I18n.T("log.removed", PackageTypes.Get(type).Name + " " + PackageTypes.Label(folder)));
        }

        static void Report(Action<InstallProgress> p, string stage, int pct, long bytes)
        {
            if (p != null) p(new InstallProgress { Stage = stage, Percent = pct, Bytes = bytes });
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
    }
}
