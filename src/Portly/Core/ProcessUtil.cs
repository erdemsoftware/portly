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
            try
            {
                var r = Run("netstat.exe", "-ano -p TCP", null, null, 15000);
                foreach (var raw in r.Output.Split('\n'))
                {
                    var parts = raw.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5 || !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!parts[1].EndsWith(":" + port)) continue;
                    int pid;
                    if (!int.TryParse(parts[4], out pid)) continue;
                    string name = "?";
                    try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName + ".exe"; } catch { }
                    return name + " (PID " + pid + ")";
                }
            }
            catch { }
            return "";
        }

        public static string Quote(string s) { return "\"" + s + "\""; }
    }
}
