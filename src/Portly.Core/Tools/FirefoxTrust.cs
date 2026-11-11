using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>
    /// Firefox does not use the Windows certificate store by default on every version, so the trusted Portly CA
    /// may still give warnings there. Two ways to fix it:
    ///  1. per profile (no admin): user.js gets  user_pref("security.enterprise_roots.enabled", true);
    ///  2. per machine (admin, explicit action only): distribution\policies.json  Certificates.ImportEnterpriseRoots = true
    /// </summary>
    public static class FirefoxTrust
    {
        public const string PrefLine = "user_pref(\"security.enterprise_roots.enabled\", true);";
        static readonly Regex PrefRx = new Regex(@"^\s*user_pref\(\s*""security\.enterprise_roots\.enabled""\s*,\s*(true|false)\s*\)", RegexOptions.IgnoreCase);

        /// <summary>Firefox profile folders of the current user (classic install and Microsoft Store version).</summary>
        public static List<string> ProfileDirs(string appData = null)
        {
            var roots = new List<string>();
            appData = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            roots.Add(Path.Combine(appData, "Mozilla", "Firefox"));
            if (appData == Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            {
                try
                {
                    var pkgs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                    if (Directory.Exists(pkgs))
                        foreach (var p in Directory.GetDirectories(pkgs, "Mozilla.Firefox*"))
                            roots.Add(Path.Combine(p, "LocalCache", "Roaming", "Mozilla", "Firefox"));
                }
                catch { }
            }
            var dirs = new List<string>();
            foreach (var root in roots.Where(Directory.Exists))
            {
                // profiles.ini can point anywhere (IsRelative=0)
                var ini = Path.Combine(root, "profiles.ini");
                if (File.Exists(ini))
                {
                    try
                    {
                        bool relative = true;
                        string path = null;
                        foreach (var raw in File.ReadAllLines(ini).Concat(new[] { "[end]" }))
                        {
                            var l = raw.Trim();
                            if (l.StartsWith("["))
                            {
                                if (path != null) dirs.Add(relative ? Path.Combine(root, path.Replace('/', '\\')) : path);
                                path = null; relative = true;
                            }
                            else if (l.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) path = l.Substring(5);
                            else if (l.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase)) relative = l.Substring(11) != "0";
                        }
                    }
                    catch { }
                }
                var profiles = Path.Combine(root, "Profiles");
                try { if (Directory.Exists(profiles)) dirs.AddRange(Directory.GetDirectories(profiles)); } catch { }
            }
            return dirs.Select(d => { try { return Path.GetFullPath(d).TrimEnd('\\'); } catch { return null; } })
                .Where(d => d != null && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool HasProfiles { get { return ProfileDirs().Count > 0; } }

        /// <summary>
        /// Add the preference to user.js of one profile. Creates the file or appends one line; never removes or
        /// duplicates lines. Returns true when the file was changed.
        /// </summary>
        public static bool EnableIn(string profileDir)
        {
            var file = Path.Combine(profileDir, "user.js");
            var text = File.Exists(file) ? File.ReadAllText(file) : "";
            // the last setting wins in user.js: an earlier "false" is overridden by our appended "true"
            string last = null;
            foreach (var l in text.Split('\n'))
            {
                var m = PrefRx.Match(l);
                if (m.Success) last = m.Groups[1].Value.ToLowerInvariant();
            }
            if (last == "true") return false;
            var sb = new StringBuilder();
            if (text.Length > 0 && !text.EndsWith("\n")) sb.Append(text.Contains("\r\n") ? "\r\n" : "\n");
            sb.Append("// Portly: trust the certificates in the Windows store (Portly Local CA)\r\n").Append(PrefLine).Append("\r\n");
            File.AppendAllText(file, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        /// <summary>All profiles of the current user. Returns the number of profiles changed (0 = nothing to do).</summary>
        public static int EnableAll(string appData = null)
        {
            int changed = 0;
            foreach (var dir in ProfileDirs(appData))
            {
                try { if (EnableIn(dir)) changed++; }
                catch (Exception ex) { Log.Warn("Firefox user.js (" + dir + "): " + ex.Message); }
            }
            if (changed > 0) Log.Info(I18n.T("firefox.done", changed));
            return changed;
        }

        // ---------------- per machine: policies.json (needs admin) ----------------
        /// <summary>Install folder of a per-machine Firefox (Program Files), or null.</summary>
        public static string InstallDir()
        {
            try
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = hk.OpenSubKey(@"SOFTWARE\Mozilla\Mozilla Firefox"))
                    {
                        if (k == null) continue;
                        foreach (var ver in k.GetSubKeyNames())
                            using (var main = k.OpenSubKey(ver + @"\Main"))
                            {
                                var dir = main == null ? null : main.GetValue("Install Directory") as string;
                                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "firefox.exe"))) return dir.TrimEnd('\\');
                            }
                    }
            }
            catch { }
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                var dir = Path.Combine(pf, "Mozilla Firefox");
                if (File.Exists(Path.Combine(dir, "firefox.exe"))) return dir;
            }
            return null;
        }

        public static string PolicyFile(string installDir) { return Path.Combine(installDir, "distribution", "policies.json"); }

        public static bool PolicySet(string installDir)
        {
            try { var f = PolicyFile(installDir); return File.Exists(f) && File.ReadAllText(f).Contains("ImportEnterpriseRoots"); }
            catch { return false; }
        }

        /// <summary>Runs elevated ("Portly.exe --firefox-policy dir"). Adds ImportEnterpriseRoots, keeps other policies.</summary>
        public static int ApplyPolicyElevated(string installDir)
        {
            try
            {
                if (!File.Exists(Path.Combine(installDir, "firefox.exe"))) return 3;
                var file = PolicyFile(installDir);
                Dictionary<string, object> root = new Dictionary<string, object>();
                if (File.Exists(file))
                {
                    var text = File.ReadAllText(file);
                    if (text.Trim() != "") root = Json.ParseObject(text);
                }
                var policies = root.ContainsKey("policies") ? root["policies"] as Dictionary<string, object> : null;
                if (policies == null) { policies = new Dictionary<string, object>(); root["policies"] = policies; }
                var certs = policies.ContainsKey("Certificates") ? policies["Certificates"] as Dictionary<string, object> : null;
                if (certs == null) { certs = new Dictionary<string, object>(); policies["Certificates"] = certs; }
                certs["ImportEnterpriseRoots"] = true;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, Json.Serialize(root), new UTF8Encoding(false));
                return 0;
            }
            catch { return 2; }
        }
    }
}
