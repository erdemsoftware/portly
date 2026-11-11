using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Portly.Core
{
    /// <summary>
    /// Linux: shared libraries a downloaded program needs but the system does not have under that name.
    /// Ubuntu 24.04 renamed libaio.so.1 to libaio.so.1t64 (the 64-bit time_t move), which the official MySQL builds
    /// still ask for. A link with the old name in the package's own lib folder (in its rpath) fixes it without
    /// touching the system.
    /// </summary>
    public static class LinuxLibs
    {
        static readonly string[] SystemDirs =
        {
            "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/lib/x86_64-linux-gnu", "/lib/aarch64-linux-gnu",
            "/usr/lib64", "/lib64", "/usr/lib", "/lib",
        };

        /// <summary>Libraries ldd reports as "not found" for the program.</summary>
        public static List<string> Missing(string exe)
        {
            var list = new List<string>();
            if (!OS.IsLinux || !File.Exists(exe)) return list;
            var r = ProcessUtil.Run("ldd", ProcessUtil.Quote(exe), null, null, 20000);
            foreach (Match m in Regex.Matches(r.Output ?? "", @"^\s*(\S+)\s*=>\s*not found", RegexOptions.Multiline))
                list.Add(m.Groups[1].Value);
            return list.Distinct().ToList();
        }

        /// <summary>
        /// Link renamed system libraries (name + "t64") into libDir under the old name.
        /// Returns what is still missing afterwards (empty = the program can start).
        /// </summary>
        public static List<string> Fix(string exe, string libDir)
        {
            var missing = Missing(exe);
            if (missing.Count == 0) return missing;
            foreach (var name in missing)
            {
                var renamed = SystemDirs.Select(d => Path.Combine(d, name + "t64")).FirstOrDefault(File.Exists);
                if (renamed == null) continue;
                try
                {
                    Directory.CreateDirectory(libDir);
                    var link = Path.Combine(libDir, name);
                    if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
#if !NETFRAMEWORK
                    File.CreateSymbolicLink(link, renamed);
#endif
                    Log.Info("Linked " + link + " -> " + renamed);
                }
                catch (Exception ex) { Log.Warn("Library link " + name + ": " + ex.Message); }
            }
            return Missing(exe);
        }

        /// <summary>Package names to install for missing libraries (shown to the user).</summary>
        public static string InstallHint(IEnumerable<string> libs)
        {
            var names = libs.Select(l => l.StartsWith("libaio") ? "libaio1t64 (Debian/Ubuntu) / libaio (Fedora, Arch)"
                : l.StartsWith("libnuma") ? "libnuma1 / numactl-libs"
                : l.StartsWith("libncurses") || l.StartsWith("libtinfo") ? "libncurses6 / ncurses-libs"
                : l).Distinct();
            return string.Join(", ", names);
        }
    }
}
