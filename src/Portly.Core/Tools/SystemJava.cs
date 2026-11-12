using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>
    /// Java and Tomcat installed on the system the normal way (not Portly packages): JAVA_HOME, the JavaSoft registry keys
    /// and the usual vendor folders (Oracle, Adoptium/Temurin, Microsoft, Zulu, Corretto, Liberica). The newest Java wins.
    /// macOS: /Library/Java/JavaVirtualMachines and Homebrew. Linux: /usr/lib/jvm, /opt, SDKMAN.
    /// </summary>
    public static class SystemJava
    {
        static readonly string[] VendorDirs = { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft", "Semeru" };

        /// <summary>Every Java home found, newest first.</summary>
        public static List<string> Homes()
        {
            var homes = new List<string>();
            Action<string> add = h =>
            {
                if (string.IsNullOrEmpty(h)) return;
                h = h.Trim().Trim('"').TrimEnd('\\', '/');
                if (File.Exists(Path.Combine(h, "bin", OS.Exe("java"))) && !homes.Contains(h, StringComparer.OrdinalIgnoreCase)) homes.Add(h);
            };
            add(Environment.GetEnvironmentVariable("JAVA_HOME"));
            if (!OS.IsWindows)
            {
                foreach (var h in UnixHomes()) add(h);
                return homes.OrderByDescending(VersionOf).ToList();
            }
            foreach (var key in new[] { @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\JRE", @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\Java Development Kit" })
            {
                try
                {
                    using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(key))
                    {
                        if (k == null) continue;
                        foreach (var v in k.GetSubKeyNames())
                            using (var sub = k.OpenSubKey(v)) add(sub == null ? null : sub.GetValue("JavaHome") as string);
                    }
                }
                catch { }
            }
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") }.Where(x => !string.IsNullOrEmpty(x)).Distinct())
                foreach (var vendor in VendorDirs)
                {
                    var dir = Path.Combine(pf, vendor);
                    if (!Directory.Exists(dir)) continue;
                    try { foreach (var d in Directory.GetDirectories(dir)) add(d); } catch { }
                }
            return homes.OrderByDescending(VersionOf).ToList();
        }

        /// <summary>JDK folders of macOS and Linux (each holds bin/java).</summary>
        static IEnumerable<string> UnixHomes()
        {
            var roots = new List<string>();
            if (OS.IsMac)
            {
                foreach (var d in Safe("/Library/Java/JavaVirtualMachines")) roots.Add(Path.Combine(d, "Contents", "Home"));
                foreach (var d in Safe("/opt/homebrew/opt")) if (Path.GetFileName(d).StartsWith("openjdk")) roots.Add(Path.Combine(d, "libexec", "openjdk.jdk", "Contents", "Home"));
                foreach (var d in Safe("/usr/local/opt")) if (Path.GetFileName(d).StartsWith("openjdk")) roots.Add(Path.Combine(d, "libexec", "openjdk.jdk", "Contents", "Home"));
            }
            else
            {
                foreach (var parent in new[] { "/usr/lib/jvm", "/usr/java", "/opt/java", "/opt/jdk", "/usr/lib64/jvm" })
                    foreach (var d in Safe(parent)) roots.Add(d);
            }
            foreach (var d in Safe(Path.Combine(UnixIntegration.Home, ".sdkman", "candidates", "java"))) roots.Add(d);
            return roots;
        }

        static IEnumerable<string> Safe(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : new string[0]; } catch { return new string[0]; }
        }

        /// <summary>The newest Java home on this system, or null.</summary>
        public static string Newest() { return Homes().FirstOrDefault(); }

        /// <summary>"21.0.4" / "1.8.0_491" -> comparable version (8.0.491 for the old scheme).</summary>
        public static Version VersionOf(string home)
        {
            try
            {
                var f = Path.Combine(home, "release");
                var text = File.Exists(f) ? File.ReadAllText(f) : Path.GetFileName(home);
                var m = Regex.Match(text, "JAVA_VERSION=\"?([\\d._]+)");
                var s = m.Success ? m.Groups[1].Value : Regex.Match(text, "\\d+(\\.\\d+)*(_\\d+)?").Value;
                var parts = s.Replace('_', '.').Split('.').Select(p => { int n; return int.TryParse(p, out n) ? n : 0; }).ToList();
                if (parts.Count > 1 && parts[0] == 1) parts.RemoveAt(0);     // 1.8.0_491 -> 8.0.491
                while (parts.Count < 3) parts.Add(0);
                return new Version(parts[0], parts[1], parts[2]);
            }
            catch { return new Version(0, 0); }
        }

        /// <summary>A Tomcat installed with its own Windows installer (it runs as its own Windows service), or null.</summary>
        public static string Tomcat()
        {
            if (!OS.IsWindows)
            {
                foreach (var pattern in new[] { "/opt/tomcat*", "/usr/share/tomcat*", "/usr/local/tomcat*", "/opt/homebrew/opt/tomcat*/libexec", "/usr/local/opt/tomcat*/libexec" })
                {
                    try
                    {
                        var parent = Path.GetDirectoryName(pattern);
                        var prefix = Path.GetFileName(pattern).TrimEnd('*');
                        // "/opt/homebrew/opt/tomcat*/libexec": the wildcard sits in the middle folder
                        var hit = pattern.EndsWith("/libexec")
                            ? Safe(Path.GetDirectoryName(parent)).Where(d => Path.GetFileName(d).StartsWith(Path.GetFileName(parent).TrimEnd('*'))).Select(d => Path.Combine(d, "libexec"))
                            : Safe(parent).Where(d => Path.GetFileName(d).StartsWith(prefix));
                        var found = hit.Where(d => File.Exists(Path.Combine(d, "bin", "bootstrap.jar"))).OrderByDescending(d => d).FirstOrDefault();
                        if (found != null) return found;
                    }
                    catch { }
                }
                return null;
            }
            var dir = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Apache Software Foundation");
            if (!Directory.Exists(dir)) return null;
            try
            {
                return Directory.GetDirectories(dir, "Tomcat*")
                    .Where(d => File.Exists(Path.Combine(d, "bin", "bootstrap.jar")))
                    .OrderByDescending(d => d).FirstOrDefault();
            }
            catch { return null; }
        }
    }
}
