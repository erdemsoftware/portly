using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Portly.Core;

namespace Portly.Packages
{
    public class UnusedVersion
    {
        public string Type, Folder, Dir;
        public long Size;
        public string Title { get { return PackageTypes.Get(Type).Name + " " + PackageTypes.Label(Folder); } }
    }

    /// <summary>
    /// "Remove unused...": installed versions that are not active, not chosen by any site (sites.ini php=)
    /// and not running. Removing them frees disk space; data folders (data\) are never touched.
    /// </summary>
    public static class Cleanup
    {
        /// <summary>All unused versions of the real bin folder, with sizes.</summary>
        public static List<UnusedVersion> Find()
        {
            var types = PackageTypes.All.Select(t => t.Id).ToList();
            return Find(types, PackageTypes.Installed, PackageTypes.ActiveFolder, SitePhpFolders(), Paths.BinOf);
        }

        /// <summary>The pure rule (tested with a fake bin folder).</summary>
        public static List<UnusedVersion> Find(IEnumerable<string> types, Func<string, List<string>> installedOf, Func<string, string> activeOf,
                                               ICollection<string> sitePhp, Func<string, string> binOf)
        {
            var list = new List<UnusedVersion>();
            foreach (var type in types)
            {
                var active = activeOf(type) ?? "";
                foreach (var f in installedOf(type))
                {
                    if (string.Equals(f, active, StringComparison.OrdinalIgnoreCase)) continue;
                    if (type == "php" && sitePhp.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                    var dir = Path.Combine(binOf(type), f);
                    list.Add(new UnusedVersion { Type = type, Folder = f, Dir = dir, Size = FolderSize(dir) });
                }
            }
            return list;
        }

        /// <summary>PHP folders named in usr\sites.ini (also of disabled or hidden sites).</summary>
        public static HashSet<string> SitePhpFolders()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(Paths.SitesFile)) return set;
                var ini = new Ini(Paths.SitesFile);
                foreach (var sec in ini.Sections())
                {
                    var v = ini.Get(sec, "php", "");
                    if (v != "") set.Add(v);
                }
            }
            catch (Exception ex) { Log.Warn("sites.ini: " + ex.Message); }
            return set;
        }

        public static long FolderSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return total;
        }

        /// <summary>Is a program from this folder running (also a PHP pool of a site's own version)?</summary>
        public static bool IsInUse(string dir)
        {
            var prefix = dir.TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var path = ProcessUtil.ProcessPath(p.Id);
                    if (!string.IsNullOrEmpty(path) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }
    }
}
