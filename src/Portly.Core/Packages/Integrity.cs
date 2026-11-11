using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Packages
{
    /// <summary>Files of one installed version that are gone or changed (antivirus quarantine, manual deletes).</summary>
    public class IntegrityProblem
    {
        public string Type, Folder, Dir;
        public List<string> Missing = new List<string>();
        public List<string> Changed = new List<string>();
        public int Count { get { return Missing.Count + Changed.Count; } }
        public string Title { get { return PackageTypes.Get(Type).Name + " " + PackageTypes.Label(Folder); } }
    }

    /// <summary>
    /// Antivirus quarantine detection. At install time Portly writes &lt;version folder&gt;\.portly-files:
    /// one line per file "relative path|size|sha256" (the hash only for .exe/.dll, the files a scanner removes).
    /// Start-up: a fast check of the active versions (existence + size of .exe/.dll only).
    /// Tools > Verify packages: the full check (every file exists, .exe/.dll hashes) of all installed versions.
    /// </summary>
    public static class Integrity
    {
        public const string ListFile = ".portly-files";

        static bool IsBinary(string rel)
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            return ext == ".exe" || ext == ".dll";
        }

        /// <summary>Write the file list of a freshly installed folder.</summary>
        public static void Record(string dir)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Portly file list: path|size|sha256 (.exe/.dll). Used to detect files removed by antivirus software.");
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(dir.Length).TrimStart('\\');
                    if (rel.Equals(ListFile, StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(f);
                    var hash = IsBinary(rel) ? Net.Sha256(f) : "";
                    sb.Append(rel).Append('|').Append(info.Length).Append('|').Append(hash).Append("\r\n");
                }
                var target = Path.Combine(dir, ListFile);
                File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));
                try { File.SetAttributes(target, FileAttributes.Hidden); } catch { }
            }
            catch (Exception ex) { Log.Warn("File list " + dir + ": " + ex.Message); }
        }

        public static bool HasList(string dir) { return File.Exists(Path.Combine(dir, ListFile)); }

        /// <summary>
        /// Compare a folder with its list. quick = only .exe/.dll, existence + size (milliseconds);
        /// full = every file must exist, .exe/.dll hashes must match. Returns null when there is no list.
        /// </summary>
        public static IntegrityProblem Check(string dir, bool quick, CancellationToken ct = default(CancellationToken))
        {
            var list = Path.Combine(dir, ListFile);
            if (!File.Exists(list)) return null;
            var p = new IntegrityProblem { Dir = dir, Folder = Path.GetFileName(dir), Type = Path.GetFileName(Path.GetDirectoryName(dir)) };
            foreach (var line in File.ReadAllLines(list, Encoding.UTF8))
            {
                ct.ThrowIfCancellationRequested();
                if (line.StartsWith("#") || line.Trim() == "") continue;
                var parts = line.Split('|');
                if (parts.Length < 3) continue;
                var rel = parts[0];
                // never follow a path out of the folder, whatever the list says
                if (rel.Contains("..") || Path.IsPathRooted(rel)) continue;
                bool bin = IsBinary(rel);
                if (quick && !bin) continue;
                var f = Path.Combine(dir, rel);
                var info = new FileInfo(f);
                if (!info.Exists) { p.Missing.Add(rel); continue; }
                if (!bin) continue;
                long size;
                if (long.TryParse(parts[1], out size) && info.Length != size) { p.Changed.Add(rel); continue; }
                if (!quick && parts[2] != "")
                {
                    try { if (!Net.Sha256(f).Equals(parts[2], StringComparison.OrdinalIgnoreCase)) p.Changed.Add(rel); }
                    catch (IOException) { } // locked by a running program: it is there
                    catch (UnauthorizedAccessException) { p.Changed.Add(rel); }
                }
            }
            return p;
        }

        /// <summary>Start-up check of the active version of every installed type.</summary>
        public static List<IntegrityProblem> QuickCheckActive()
        {
            var list = new List<IntegrityProblem>();
            foreach (var t in PackageTypes.All)
            {
                // the chosen folder even when its main exe is gone (then it no longer counts as installed)
                var chosen = Settings.Current.GetVersion(t.Id);
                var dir = chosen != "" && Directory.Exists(Path.Combine(Paths.BinOf(t.Id), chosen)) ? Path.Combine(Paths.BinOf(t.Id), chosen) : PackageTypes.ActiveDir(t.Id);
                if (dir == null) continue;
                try
                {
                    var p = Check(dir, true);
                    if (p != null && p.Count > 0) list.Add(p);
                }
                catch (Exception ex) { Log.Warn("Integrity " + dir + ": " + ex.Message); }
            }
            return list;
        }

        /// <summary>Full check of every installed version. withoutList counts versions installed before this check existed.</summary>
        public static List<IntegrityProblem> FullCheck(Action<string> progress, CancellationToken ct, out int checkedCount, out int withoutList)
        {
            var list = new List<IntegrityProblem>();
            checkedCount = 0; withoutList = 0;
            foreach (var t in PackageTypes.All)
                foreach (var f in VersionFolders(t.Id))
                {
                    ct.ThrowIfCancellationRequested();
                    var dir = Path.Combine(Paths.BinOf(t.Id), f);
                    if (progress != null) progress(t.Name + " " + PackageTypes.Label(f));
                    var p = Check(dir, false, ct);
                    if (p == null) { withoutList++; continue; }
                    checkedCount++;
                    if (p.Count > 0) list.Add(p);
                }
            return list;
        }

        /// <summary>Every version folder of a type, also one whose main exe was removed (Installed() hides those).</summary>
        public static List<string> VersionFolders(string type)
        {
            var dir = Paths.BinOf(type);
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetDirectories(dir).Select(Path.GetFileName).Where(f => !f.StartsWith(".")).ToList();
        }

        /// <summary>
        /// Put back the missing / changed files: download the same version again (or use the download cache),
        /// extract it next to the folder and copy only the affected files, so php.ini and other user changes stay.
        /// </summary>
        public static async Task RepairAsync(IntegrityProblem p, Action<InstallProgress> progress, CancellationToken ct)
        {
            var entry = Catalog.FindByFolder(p.Type, p.Folder);
            if (entry == null) throw new Exception(I18n.T("verify.noCatalogEntry", p.Title));
            // a hidden sibling folder (names starting with "." are never listed as versions)
            var tmpEntry = new PackageEntry
            {
                Type = entry.Type, Version = entry.Version, Folder = ".repair-" + entry.Folder, Url = entry.Url, Sha256 = entry.Sha256,
                Subdir = entry.Subdir, Kind = entry.Kind, FileName = entry.FileName, MinOs = entry.MinOs, Vc = entry.Vc,
            };
            var tmpDir = tmpEntry.InstallDir;
            var chosen = Settings.Current.GetVersion(p.Type);
            try
            {
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                // the download is cached under the real folder name; InstallAsync looks it up by type + folder + file name
                await Installer.InstallAsync(tmpEntry, progress, ct);
                foreach (var rel in p.Missing.Concat(p.Changed))
                {
                    var src = Path.Combine(tmpDir, rel);
                    if (!File.Exists(src)) continue;
                    var dst = Path.Combine(p.Dir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                }
                Log.Info(I18n.T("verify.repaired", p.Title, p.Count));
            }
            finally
            {
                // InstallAsync may have picked the temporary folder as the active one
                if (Settings.Current.GetVersion(p.Type) != chosen) { Settings.Current.SetVersion(p.Type, chosen); Settings.Current.Save(); }
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        }
    }
}
