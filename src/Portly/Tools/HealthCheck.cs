using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Portly.Core;

namespace Portly.Tools
{
    public class HealthIssue
    {
        public string Key;     // stable id, used to show each warning only once
        public string Text;
        public string FixLabel;
        public Action Fix;     // null = information only
    }

    /// <summary>
    /// Environment problems that commonly break local stacks (see docs/05-yaygin-sorunlar.md):
    /// OneDrive folders, non-ASCII paths, long paths off, another PHP on PATH, a global PHPRC.
    /// </summary>
    public static class HealthCheck
    {
        static bool IsAscii(string s) { return s.All(c => c < 128); }

        public static List<HealthIssue> Run()
        {
            var list = new List<HealthIssue>();
            var root = Paths.Root;
            var docs = Settings.Current.DocumentRoot;

            // 1. OneDrive: Files On-Demand + sync locks break composer/npm and corrupt databases
            var od = Environment.GetEnvironmentVariable("OneDrive") ?? "";
            foreach (var p in new[] { root, docs })
            {
                if ((od != "" && p.StartsWith(od, StringComparison.OrdinalIgnoreCase)) || p.IndexOf("\\OneDrive", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    list.Add(new HealthIssue { Key = "onedrive", Text = I18n.T("health.onedrive", p) });
                    break;
                }
            }

            // 2. non-ASCII characters (Turkish İ ş ğ, Cyrillic ...) in paths that tools pass around
            var temp = Path.GetTempPath();
            var bad = new[] { root, docs, temp }.Where(p => !IsAscii(p)).ToList();
            if (bad.Count > 0) list.Add(new HealthIssue { Key = "nonascii", Text = I18n.T("health.nonAscii", string.Join(", ", bad)) });

            // 3. long paths (node_modules, vendor) beyond 260 characters
            if (OsInfo.IsWin10OrLater && !LongPathsEnabled())
                list.Add(new HealthIssue
                {
                    Key = "longpaths",
                    Text = I18n.T("health.longPaths"),
                    FixLabel = I18n.T("health.enable"),
                    Fix = () => WinUtil.RunElevated("reg.exe", @"add HKLM\SYSTEM\CurrentControlSet\Control\FileSystem /v LongPathsEnabled /t REG_DWORD /d 1 /f"),
                });

            // 4. a global PHPRC makes every php.exe read another php.ini
            var phprc = Environment.GetEnvironmentVariable("PHPRC", EnvironmentVariableTarget.Machine) ?? Environment.GetEnvironmentVariable("PHPRC", EnvironmentVariableTarget.User);
            if (!string.IsNullOrEmpty(phprc) && !phprc.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                list.Add(new HealthIssue { Key = "phprc", Text = I18n.T("health.phprc", phprc) });

            // 5. another php.exe earlier on PATH (XAMPP, old installs): editors and terminals pick the wrong PHP
            var other = FirstOnPath("php.exe");
            if (other != null && !other.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                list.Add(new HealthIssue { Key = "otherphp:" + other, Text = I18n.T("health.otherPhp", other) });

            return list;
        }

        static bool LongPathsEnabled()
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem"))
                    return k != null && Convert.ToInt32(k.GetValue("LongPathsEnabled", 0)) == 1;
            }
            catch { return true; }
        }

        static string FirstOnPath(string exe)
        {
            var path = (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "") + ";" +
                       (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "");
            foreach (var dir in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var f = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), exe);
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Optional: exclude the Portly folder from Windows Defender real-time scanning (npm / composer / MySQL get
        /// much faster). Needs administrator rights; the user is told about the security trade-off first.
        /// </summary>
        public static int AddDefenderExclusion()
        {
            var paths = new[] { Paths.Root, Settings.Current.DocumentRoot }.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => "'" + p.Replace("'", "''") + "'");
            var cmd = "-NoProfile -ExecutionPolicy Bypass -Command \"Add-MpPreference -ExclusionPath " + string.Join(",", paths) + "\"";
            return WinUtil.RunElevated("powershell.exe", cmd);
        }

        /// <summary>LAN access: inbound firewall rule for the web ports (removed again when LAN access is turned off).</summary>
        public static int SetFirewallRule(bool allow)
        {
            var s = Settings.Current;
            var del = "advfirewall firewall delete rule name=\"Portly web\"";
            if (!allow) return WinUtil.RunElevated("netsh.exe", del);
            var ports = s.HttpPort + "," + s.HttpsPort;
            return WinUtil.RunElevated("cmd.exe", "/c netsh " + del + " & netsh advfirewall firewall add rule name=\"Portly web\" dir=in action=allow protocol=TCP localport=" + ports);
        }
    }
}
