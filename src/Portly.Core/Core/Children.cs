using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Portly.Core
{
    /// <summary>
    /// macOS / Linux have no job object: service processes are listed in tmp/children.pids, closed when Portly exits,
    /// and anything still running from a crashed Portly is closed on the next start.
    /// Only processes started from Portly's own bin folder with the recorded start time are touched.
    /// </summary>
    public static class Children
    {
        static readonly object Sync = new object();
        static bool _hooked;

        static string File_ { get { return Path.Combine(Paths.Tmp, "children.pids"); } }

        public static void Add(Process p)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Paths.Tmp);
                    File.AppendAllText(File_, p.Id + " " + p.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "\n");
                }
                catch { }
                if (!_hooked)
                {
                    _hooked = true;
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => CloseAll();
                }
            }
        }

        /// <summary>Close every recorded process that is still ours. Call at start (crash leftovers) and at exit.</summary>
        public static void CloseAll()
        {
            if (OS.IsWindows) return;
            lock (Sync)
            {
                List<string> lines;
                try { lines = File.Exists(File_) ? File.ReadAllLines(File_).ToList() : new List<string>(); }
                catch { return; }
                foreach (var line in lines)
                {
                    var parts = line.Split(' ');
                    int pid;
                    long ticks;
                    if (parts.Length != 2 || !int.TryParse(parts[0], out pid) || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                    try
                    {
                        using (var p = Process.GetProcessById(pid))
                        {
                            if (p.HasExited || p.StartTime.ToUniversalTime().Ticks != ticks) continue;
                            var path = ProcessUtil.ProcessPath(pid);
                            if (path == "" || !path.StartsWith(Paths.Bin + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                            ProcessUtil.KillTree(pid);
                        }
                    }
                    catch { }
                }
                try { File.Delete(File_); } catch { }
            }
        }
    }
}
