using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Portly.Tools
{
    /// <summary>
    /// Java and Tomcat installed on Windows the normal way (not Portly packages): JAVA_HOME, the JavaSoft registry keys
    /// and the usual vendor folders (Oracle, Adoptium/Temurin, Microsoft, Zulu, Corretto, Liberica). The newest Java wins.
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
                h = h.Trim().Trim('"').TrimEnd('\\');
                if (File.Exists(Path.Combine(h, "bin", "java.exe")) && !homes.Contains(h, StringComparer.OrdinalIgnoreCase)) homes.Add(h);
            };
            add(Environment.GetEnvironmentVariable("JAVA_HOME"));
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

        /// <summary>The newest Java home on Windows, or null.</summary>
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
