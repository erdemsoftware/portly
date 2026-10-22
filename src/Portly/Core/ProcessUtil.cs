using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Portly.Core
{
    public class RunResult
    {
        public int ExitCode = -1;
        public string Output = "";
        public bool TimedOut;
        public bool Ok { get { return ExitCode == 0 && !TimedOut; } }
    }

    public class PortOwnerInfo
    {
        public int Pid;
        public string Name = "?";
        public string Path = "";
        /// <summary>PID 4 = Windows itself (http.sys: IIS, Web Deploy, ...): cannot be closed.</summary>
        public bool IsSystem { get { return Pid <= 4; } }
        /// <summary>A leftover service of this Portly (a program from bin\, never Portly itself or anything else).</summary>
        public bool IsOurs
        {
            get
            {
                return Path != "" && Pid != Process.GetCurrentProcess().Id
                    && Path.StartsWith(Paths.Bin + "\\", StringComparison.OrdinalIgnoreCase);
            }
        }
        public override string ToString() { return Name + " (PID " + Pid + ")"; }
    }

    public static class ProcessUtil
    {
        /// <summary>Run a command hidden and wait for it. Output = stdout + stderr.</summary>
        public static RunResult Run(string exe, string args, string workDir = null, IDictionary<string, string> env = null, int timeoutMs = 60000, string stdin = null)
        {
            var res = new RunResult();
            var psi = new ProcessStartInfo(exe, args ?? "")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                WorkingDirectory = workDir ?? Path.GetDirectoryName(exe),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var sb = new StringBuilder();
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (stdin != null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
                    if (!p.WaitForExit(timeoutMs))
                    {
                        res.TimedOut = true;
                        KillTree(p.Id);
                    }
                    else
                    {
                        p.WaitForExit();
                        res.ExitCode = p.ExitCode;
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine(ex.Message); }
            lock (sb) res.Output = sb.ToString().Trim();
            return res;
        }

        public static void KillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe", "/T /F /PID " + pid) { CreateNoWindow = true, UseShellExecute = false };
                using (var p = Process.Start(psi)) p.WaitForExit(10000);
            }
            catch { }
            try { using (var p = Process.GetProcessById(pid)) p.Kill(); } catch { }
        }

        // ---------------- graceful stop with Ctrl+C ----------------
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
        static readonly object CtrlLock = new object();

        /// <summary>Send Ctrl+C to a console process (Apache, MySQL, Redis shut down cleanly on it).</summary>
        public static bool SendCtrlC(Process p, int waitMs)
        {
            lock (CtrlLock)
            {
                try
                {
                    if (p.HasExited) return true;
                    FreeConsole();
                    if (!AttachConsole((uint)p.Id)) return false;
                    SetConsoleCtrlHandler(IntPtr.Zero, true);
                    GenerateConsoleCtrlEvent(0, 0);
                    bool exited = p.WaitForExit(waitMs);
                    FreeConsole();
                    Thread.Sleep(50);
                    SetConsoleCtrlHandler(IntPtr.Zero, false);
                    return exited;
                }
                catch { try { FreeConsole(); SetConsoleCtrlHandler(IntPtr.Zero, false); } catch { } return false; }
            }
        }

        // ---------------- ports ----------------
        static List<Tuple<int, int>> _excluded;
        static DateTime _excludedAt;

        /// <summary>
        /// Port ranges Windows reserves for Hyper-V / WSL / Docker / WinNAT ("netsh int ipv4 show excludedportrange").
        /// Nothing listens there, but binding fails with "access forbidden" (OS 10013). Cached for a minute.
        /// </summary>
        public static List<Tuple<int, int>> ExcludedRanges()
        {
            if (_excluded != null && _excludedAt > DateTime.Now.AddMinutes(-1)) return _excluded;
            var list = new List<Tuple<int, int>>();
            try
            {
                var r = Run("netsh.exe", "int ipv4 show excludedportrange protocol=tcp", null, null, 10000);
                foreach (var line in r.Output.Split('\n'))
                {
                    var parts = line.Trim().Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
                    int a, b;
                    if (parts.Length >= 2 && int.TryParse(parts[0], out a) && int.TryParse(parts[1], out b) && b >= a) list.Add(Tuple.Create(a, b));
                }
            }
            catch { }
            _excluded = list;
            _excludedAt = DateTime.Now;
            return list;
        }

        public static bool IsPortExcluded(int port) { return ExcludedRanges().Any(r => port >= r.Item1 && port <= r.Item2); }

        public static bool IsPortInUse(int port)
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port);
            }
            catch { return false; }
        }

/// <summary>Which process listens on the port? Returns "name (PID n)" or "".</summary>
        public static string PortOwner(int port)
        {
            var o = PortOwnerInfo(port);
            return o == null ? "" : o.ToString();
        }

        /// <summary>Process listening on a TCP port (IPv4 or IPv6), or null.</summary>
        public static PortOwnerInfo PortOwnerInfo(int port)
        {
            try
            {
                var r = Run("netstat.exe", "-ano -p TCP", null, null, 15000);
                var r6 = Run("netstat.exe", "-ano -p TCPv6", null, null, 15000);
                foreach (var raw in (r.Output + "\n" + r6.Output).Split('\n'))
                {
                    var parts = raw.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5 || !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!parts[1].EndsWith(":" + port)) continue;
                    int pid;
                    if (!int.TryParse(parts[4], out pid)) continue;
                    var info = new PortOwnerInfo { Pid = pid, Name = pid == 4 ? "System" : "?" };
                    try { using (var p = Process.GetProcessById(pid)) info.Name = p.ProcessName + ".exe"; } catch { }
                    info.Path = ProcessPath(pid);
                    return info;
                }
            }
            catch { }
            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>Full exe path of a process (works for most processes without admin rights).</summary>
        public static string ProcessPath(int pid)
        {
            if (pid <= 4) return "";
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        /// <summary>First free TCP port from the candidates, skipping the excluded ones.</summary>
        public static int FreePort(IEnumerable<int> candidates, ICollection<int> exclude)
        {
            foreach (var p in candidates)
                if (p > 0 && p < 65536 && !exclude.Contains(p) && !IsPortInUse(p) && !IsPortExcluded(p)) return p;
            for (int p = 20000; p < 21000; p++)
                if (!exclude.Contains(p) && !IsPortInUse(p) && !IsPortExcluded(p)) return p;
            return 0;
        }

        public static string Quote(string s) { return "\"" + s + "\""; }
    }
}
