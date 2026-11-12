using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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
            if (!HasHash(e) && !e.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new Exception(I18n.T("err.noHashHttp", e.TypeName + " " + e.Version));
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

            await DownloadVerifiedAsync(e, e.Url, e.Sha256, download, progress, ct);
            // more archives of the same version (macOS / Linux: php-cli next to php-fpm)
            var partFiles = new System.Collections.Generic.List<string>();
            for (int i = 0; i < e.Parts.Count; i++)
            {
                var pf = Path.Combine(Paths.Downloads, e.Type + "-" + e.Folder + "-part" + (i + 1) + "-" + UrlFileName(e.Parts[i].Url));
                await DownloadVerifiedAsync(e, e.Parts[i].Url, e.Parts[i].Sha256, pf, progress, ct);
                partFiles.Add(pf);
            }
            ct.ThrowIfCancellationRequested();

            if (e.Kind == "file")
            {
                Directory.CreateDirectory(target);
                File.Copy(download, Path.Combine(target, fileName), true);
                if (!OS.IsWindows && !IsScriptFile(fileName)) MakeExecutable(Path.Combine(target, fileName));
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
                await Task.Run(() => Extract(e.Kind, download, extract, p => Report(progress, "extract", p, 0), ct), ct);
                var root = FindRoot(extract, e.Subdir);
                MoveDir(root, target);
                TryDeleteDir(extract);
                for (int i = 0; i < partFiles.Count; i++)
                {
                    var part = e.Parts[i];
                    var pextract = Path.Combine(Paths.Tmp, "extract", Guid.NewGuid().ToString("N").Substring(0, 8));
                    var pfile = partFiles[i];
                    await Task.Run(() => Extract(part.Kind, pfile, pextract, p => { }, ct), ct);
                    MergeDir(FindRoot(pextract, part.Subdir), target);
                    TryDeleteDir(pextract);
                }
            }
            TryDelete(download);
            foreach (var pf in partFiles) TryDelete(pf);
            if (!OS.IsWindows) MakeExecutables(target, e.Type);

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

        /// <summary>Download one file (or take it from the download cache) and check its SHA-256 before anything uses it.</summary>
        static async Task DownloadVerifiedAsync(PackageEntry e, string url, string sha256, string file, Action<InstallProgress> progress, CancellationToken ct)
        {
            var one = new PackageEntry { Type = e.Type, Version = e.Version, Folder = e.Folder, Url = url, Sha256 = sha256 };
            bool cached = File.Exists(file) && (!HasHash(one) || HashMatches(one, file));
            if (!cached)
            {
                string lastError = null;
                foreach (var u in CandidateUrls(one))
                {
                    try
                    {
                        await Net.DownloadFileAsync(u, file, (p, b) => Report(progress, "download", p, b), ct);
                        lastError = null;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { lastError = ex.Message; Log.Warn(u + " -> " + ex.Message); }
                }
                if (lastError != null) throw new Exception(I18n.T("err.download", lastError));
            }
            VerifyDownload(one, file);
        }

        static bool IsTarName(string file)
        {
            var n = file.ToLowerInvariant();
            return n.EndsWith(".tar.gz") || n.EndsWith(".tgz") || n.EndsWith(".tar.xz") || n.EndsWith(".txz") || n.EndsWith(".tar");
        }

        static bool IsTar(string kind) { return kind != null && kind.StartsWith("tar", StringComparison.OrdinalIgnoreCase); }

        /// <summary>Extract an archive of the given kind (zip, tar.gz, tar.xz, zip+tar.xz) into an empty folder.</summary>
        static void Extract(string kind, string archive, string dest, Action<int> progress, CancellationToken ct)
        {
            if (kind == "zip+tar.xz") { ExtractZipWithTar(archive, dest, progress, ct); return; }
            if (!IsTar(kind)) { ExtractZip(archive, dest, progress, ct); return; }
            ExtractTar(archive, dest);
            progress(100);
        }

        /// <summary>A zip (a Maven .jar) that carries one tar.xz: the programs are inside that (Zonky's PostgreSQL builds for Linux).</summary>
        static void ExtractZipWithTar(string archive, string dest, Action<int> progress, CancellationToken ct)
        {
            var outer = dest + "-outer";
            try
            {
                ExtractZip(archive, outer, p => progress(p / 2), ct);
                var inner = Directory.GetFiles(outer, "*", SearchOption.AllDirectories).FirstOrDefault(f => IsTarName(f));
                if (inner == null) throw new Exception("no tar archive inside " + Path.GetFileName(archive));
                ExtractTar(inner, dest);
                progress(100);
            }
            finally { TryDeleteDir(outer); }
        }

        /// <summary>
        /// tar.gz / tar.xz with the system tar (macOS bsdtar, GNU tar, and tar.exe of Windows 10+): it reads both
        /// compressions, keeps the executable bit and refuses paths that leave the folder.
        /// </summary>
        public static void ExtractTar(string archive, string dest)
        {
            Directory.CreateDirectory(dest);
            var r = ProcessUtil.Run(TarExe(), "-xf " + ProcessUtil.Quote(archive) + " -C " + ProcessUtil.Quote(dest), dest, null, 15 * 60 * 1000);
            if (!r.Ok) throw new Exception(r.Output != "" ? r.Output : "tar failed");
        }

        /// <summary>The system tar: /usr/bin/tar, or tar.exe of Windows (a Git for Windows tar would read "C:" as a remote host).</summary>
        public static string TarExe()
        {
            if (!OS.IsWindows) return File.Exists("/usr/bin/tar") ? "/usr/bin/tar" : "tar";
            var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            return File.Exists(win) ? win : "tar";
        }

        static bool IsScriptFile(string name)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".php" || ext == ".phar" || ext == ".json" || ext == ".txt" || ext == ".md";
        }

        /// <summary>chmod 755 (macOS / Linux). A no-op on Windows and on .NET Framework.</summary>
        public static void MakeExecutable(string file)
        {
#if !NETFRAMEWORK
            if (OS.IsWindows) return;
            try
            {
                File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                    | UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            catch (Exception ex) { Log.Warn("chmod " + file + ": " + ex.Message); }
#endif
        }

        /// <summary>zip files carry no reliable mode: everything in bin/ and the type's own programs at the folder root become executable.</summary>
        static void MakeExecutables(string dir, string type)
        {
#if !NETFRAMEWORK
            if (OS.IsWindows) return;
            var bin = Path.Combine(dir, "bin");
            if (Directory.Exists(bin))
                foreach (var f in Directory.GetFiles(bin)) MakeExecutable(f);
            var detect = PackageTypes.Get(type).DetectFile ?? "";
            if (detect == "" || IsScriptFile(detect)) return;
            var exeDir = Path.Combine(dir, PackageTypes.ExeDir(type));
            foreach (var name in ExecutableNames(type))
            {
                var f = Path.Combine(exeDir, name);
                if (File.Exists(f)) MakeExecutable(f);
            }
#endif
        }

        /// <summary>Programs a type ships besides its detect file (macOS / Linux names).</summary>
        static string[] ExecutableNames(string type)
        {
            switch (type)
            {
                case "php": return new[] { "php-fpm", "php", "php-cgi", "phpdbg" };
                case "mysql": return new[] { "mysqld", "mysql", "mysqldump", "mysqladmin" };
                default: return new[] { Path.GetFileName(PackageTypes.Get(type).DetectFile ?? "") };
            }
        }

        /// <summary>Copy the files of a folder into another one, replacing files with the same name.</summary>
        static void MergeDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) MergeDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        static bool HasHash(PackageEntry e) { return !string.IsNullOrWhiteSpace(e.Sha256); }

        static bool HashMatches(PackageEntry e, string file) { return Net.Sha256(file).Equals(e.Sha256.Trim(), StringComparison.OrdinalIgnoreCase); }

        /// <summary>
        /// Nothing is extracted or run before this. A wrong SHA-256 deletes the file and refuses the install. A package
        /// without one (a moving "latest" URL, or one tools\fill-package-hashes.ps1 has not reached yet) still installs
        /// over HTTPS, with a warning in the log.
        /// </summary>
        public static void VerifyDownload(PackageEntry e, string file)
        {
            if (!HasHash(e)) { Log.Warn(I18n.T("log.noPackageHash", e.TypeName + " " + e.Version)); return; }
            if (HashMatches(e, file)) return;
            TryDelete(file);
            throw new Exception(I18n.T("err.checksum"));
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
            else if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || IsTarName(source))
            {
                tmp = Path.Combine(Paths.Tmp, "extract", Guid.NewGuid().ToString("N").Substring(0, 8));
                if (IsTarName(source)) ExtractTar(source, tmp); else ExtractZip(source, tmp, p => { }, CancellationToken.None);
                root = FindRoot(tmp, type == "apache" ? "Apache24" : "");
                name = Path.GetFileNameWithoutExtension(source);
                if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
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
            if (!OS.IsWindows) MakeExecutables(target, type);

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
                        if (!ExtractUnixLink(entry, path, destFull)) entry.ExtractToFile(path, true);
                        ApplyUnixMode(entry, path);
                    }
                    done++;
                    int pct = total == 0 ? 100 : done * 100 / total;
                    if (pct != lastPct) { lastPct = pct; progress(pct); }
                }
            }
        }

        const int UnixTypeMask = 0xF000, UnixSymlink = 0xA000;

        /// <summary>macOS / Linux: a zip entry made on Unix can be a symbolic link (the EDB PostgreSQL libraries are: libicudata.77.dylib -> libicudata.77.1.dylib).</summary>
        static bool ExtractUnixLink(ZipArchiveEntry entry, string path, string destFull)
        {
#if !NETFRAMEWORK
            if (OS.IsWindows) return false;
            if (((entry.ExternalAttributes >> 16) & UnixTypeMask) != UnixSymlink) return false;
            string target;
            using (var r = new StreamReader(entry.Open(), Encoding.UTF8)) target = r.ReadToEnd().Trim();
            if (target == "" || Path.IsPathRooted(target)) return false;
            // a link may not leave the extraction folder
            if (!Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), target)).StartsWith(destFull, StringComparison.Ordinal)) return false;
            if (File.Exists(path) || Directory.Exists(path)) File.Delete(path);
            File.CreateSymbolicLink(path, target);
            return true;
#else
            return false;
#endif
        }

        /// <summary>macOS / Linux: keep the permission bits a Unix zip carries (the executable bit), never more than 755.</summary>
        static void ApplyUnixMode(ZipArchiveEntry entry, string path)
        {
#if !NETFRAMEWORK
            if (OS.IsWindows) return;
            try
            {
                int mode = (entry.ExternalAttributes >> 16) & 0x1FF;
                if ((((entry.ExternalAttributes >> 16) & UnixTypeMask) == UnixSymlink) || mode == 0 || (mode & 0x40) == 0) return;
                if (new FileInfo(path).LinkTarget != null) return;
                File.SetUnixFileMode(path, (UnixFileMode)(mode & 0x1ED | 0x1A4)); // rwxr-xr-x
            }
            catch { }
#endif
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
