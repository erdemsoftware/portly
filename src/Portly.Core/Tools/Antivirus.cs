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
    public class DefenderDetection
    {
        public string Time, Threat, Path;
    }

    /// <summary>
    /// Smart App Control, Windows Defender history / scan, and plain-language reasons why a service could not
    /// load one of its DLLs. Everything here is best effort: failures are logged and ignored.
    /// </summary>
    public static class Antivirus
    {
        /// <summary>Smart App Control: 0 off, 1 on, 2 evaluation, -1 unknown / not available (Windows 11 22H2+ only).</summary>
        public static int SmartAppControlState()
        {
            if (!OsInfo.IsWin10OrLater) return -1;
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy"))
                {
                    var v = k == null ? null : k.GetValue("VerifiedAndReputablePolicyState");
                    return v == null ? -1 : Convert.ToInt32(v);
                }
            }
            catch { return -1; }
        }

        static readonly Regex DllRx = new Regex(@"[A-Za-z]:\\[^""'<>|\r\n]*?\.(?:dll|exe)|[\w\-.]+\.(?:dll|so)\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// After a service failed to start: look at the end of its fresh log files for "module could not be found",
        /// "blocked" or code integrity messages, and at the Code Integrity event log. Returns a sentence naming the
        /// DLL, or "" when nothing points to a blocked file.
        /// </summary>
        public static string ExplainStartFailure(params string[] logFiles)
        {
            try
            {
                foreach (var log in logFiles)
                {
                    var f = Path.IsPathRooted(log) ? log : Path.Combine(Paths.Logs, log);
                    if (!File.Exists(f) || File.GetLastWriteTime(f) < DateTime.Now.AddMinutes(-3)) continue;
                    foreach (var line in Tail(f, 40).AsEnumerable().Reverse())
                    {
                        var l = line.ToLowerInvariant();
                        bool blocked = l.Contains("blocked") || l.Contains("code integrity") || l.Contains("application control") || l.Contains("0x800711c7");
                        bool missing = l.Contains("could not be found") || l.Contains("unable to load dynamic library") || l.Contains("cannot load");
                        if (!blocked && !missing) continue;
                        var m = DllRx.Match(line);
                        var dll = m.Success ? m.Value : "?";
                        return blocked || SmartAppControlState() >= 1 ? I18n.T("av.dllBlocked", dll) : I18n.T("av.dllMissing", dll);
                    }
                }
                var ci = CodeIntegrityBlocks(TimeSpan.FromMinutes(5)).FirstOrDefault();
                if (ci != null) return I18n.T("av.dllBlocked", ci);
            }
            catch (Exception ex) { Log.Warn("Start failure check: " + ex.Message); }
            return "";
        }

        static List<string> Tail(string file, int lines)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(Math.Max(0, fs.Length - 16384), SeekOrigin.Begin);
                using (var r = new StreamReader(fs, Encoding.UTF8))
                {
                    var all = r.ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Trim() != "").ToList();
                    return all.Skip(Math.Max(0, all.Count - lines)).ToList();
                }
            }
        }

        /// <summary>Files under the Portly folder that Code Integrity (Smart App Control, WDAC) blocked recently.</summary>
        public static List<string> CodeIntegrityBlocks(TimeSpan within)
        {
            var list = new List<string>();
            if (!OsInfo.IsWin10OrLater) return list;
            try
            {
                var ms = (long)within.TotalMilliseconds;
                var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("Microsoft-Windows-CodeIntegrity/Operational",
                    System.Diagnostics.Eventing.Reader.PathType.LogName,
                    "*[System[(EventID=3033 or EventID=3077 or EventID=3089) and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]");
                // device paths look like \Device\HarddiskVolume3\Portly\bin\php\...; compare without the drive
                var rootNoDrive = Paths.Root.Substring(Path.GetPathRoot(Paths.Root).Length - 1);
                using (var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(q))
                {
                    for (var e = reader.ReadEvent(); e != null && list.Count < 10; e = reader.ReadEvent())
                        using (e)
                            foreach (var p in e.Properties)
                            {
                                var s = p.Value as string;
                                if (s == null || s.IndexOf(rootNoDrive + "\\", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                if (!s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                                var at = s.IndexOf(rootNoDrive + "\\", StringComparison.OrdinalIgnoreCase);
                                var local = Path.GetPathRoot(Paths.Root).TrimEnd('\\') + s.Substring(at);
                                if (!list.Contains(local, StringComparer.OrdinalIgnoreCase)) list.Add(local);
                            }
                }
            }
            catch (Exception ex) { Log.Warn("Code Integrity log: " + ex.Message); }
            return list;
        }

        /// <summary>Windows Defender detections of files under a folder (Get-MpThreatDetection; empty when not available).</summary>
        public static List<DefenderDetection> DefenderHistory(string folder)
        {
            var list = new List<DefenderDetection>();
            if (!OsInfo.IsWin10OrLater) return list;
            var script = "$ErrorActionPreference='SilentlyContinue'; $t=@{}; Get-MpThreat | % { $t[[string]$_.ThreatID]=$_.ThreatName }; " +
                         "Get-MpThreatDetection | % { $d=$_; foreach($r in $d.Resources){ '{0}|{1}|{2}' -f $d.InitialDetectionTime.ToString('yyyy-MM-dd HH:mm'), $t[[string]$d.ThreatID], $r } }";
            var r = ProcessUtil.Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"", Paths.Root, null, 45000);
            foreach (var line in (r.Output ?? "").Split('\n'))
            {
                var parts = line.Trim().Split(new[] { '|' }, 3);
                if (parts.Length < 3) continue;
                // resources look like "file:_C:\Portly\bin\php\...\x.dll" or "containerfile:_..."
                var path = parts[2];
                var i = path.IndexOf(":_", StringComparison.Ordinal);
                if (i >= 0) path = path.Substring(i + 2);
                if (path.IndexOf(folder, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(new DefenderDetection { Time = parts[0], Threat = parts[1], Path = path });
            }
            return list;
        }

        public static string MpCmdRun
        {
            get
            {
                var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
                return File.Exists(f) ? f : null;
            }
        }

        /// <summary>Custom Defender scan of a folder. Returns (exit code, output): 0 = clean, 2 = threats found.</summary>
        public static RunResult ScanFolder(string folder)
        {
            var exe = MpCmdRun;
            if (exe == null) return new RunResult { ExitCode = -1, Output = I18n.T("av.noDefender") };
            return ProcessUtil.Run(exe, "-Scan -ScanType 3 -File \"" + folder.TrimEnd('\\') + "\"", Path.GetDirectoryName(exe), null, 60 * 60 * 1000);
        }
    }
}
