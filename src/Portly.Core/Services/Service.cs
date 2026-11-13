using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Portly.Core;
using Portly.Packages;

namespace Portly.Services
{
    public enum ServiceState { Stopped, Starting, Running, Stopping, Error }

    /// <summary>
    /// A background program (Apache, MySQL ...). Portly does not install Windows services:
    /// programs run as hidden child processes and stop when Portly exits (like Laragon, but crash-safe).
    /// </summary>
    public abstract class Service
    {
        public abstract string Id { get; }
        public abstract string Title { get; }
        public abstract string PackageType { get; }
        public virtual string PortText { get { return ""; } }

        public ServiceState State { get; private set; }
        public string LastError { get; protected set; }
        public event Action<Service> Changed;

        protected bool StopRequested;
        protected readonly object Sync = new object();

        public bool IsInstalled { get { return PackageTypes.ActiveDir(PackageType) != null; } }
        public bool IsActive { get { return State == ServiceState.Running || State == ServiceState.Starting; } }
        public string VersionLabel { get { return PackageTypes.Label(PackageTypes.ActiveFolder(PackageType)); } }

        protected void SetState(ServiceState s, string error = null)
        {
            State = s;
            LastError = error;
            if (error != null) Log.Error(Title + ": " + error);
            var h = Changed;
            if (h != null) { try { h(this); } catch { } }
        }

        /// <summary>Service mode: show the state reported by the Portly Windows service (the GUI runs nothing itself).</summary>
        public void Mirror(ServiceState s, string error)
        {
            if (s != State || (error ?? "") != (LastError ?? "")) SetState(s, string.IsNullOrEmpty(error) ? null : error);
        }

        public abstract Task StartAsync();
        public abstract Task StopAsync();

        public async Task RestartAsync()
        {
            await StopAsync();
            await StartAsync();
        }

        /// <summary>Start a hidden process, log its output to logs\&lt;logName&gt;.log, watch for crashes.</summary>
        protected Process Launch(string exe, string args, string workDir, IDictionary<string, string> env, string logName, Action<Process> onExit)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = workDir,
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var logFile = Path.Combine(Paths.Logs, logName + ".log");
            DataReceivedEventHandler h = (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                try { lock (logFile) File.AppendAllText(logFile, e.Data + Environment.NewLine, Encoding.UTF8); } catch { }
            };
            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;
            p.Exited += (s, e) => { try { onExit(p); } catch { } };
            p.Start();
            JobObject.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        /// <summary>Explain an unexpected exit (missing Visual C++ runtime is the most common cause).</summary>
        protected string ExitReason(Process p)
        {
            int code = 0;
            try { code = p.ExitCode; } catch { }
            // a DLL blocked by Smart App Control / removed by an antivirus program: name it
            var blocked = Tools.Antivirus.ExplainStartFailure(LogsFor(p));
            if (blocked != "") return blocked;
            if (code == -1073741515 || code == -1073741701 || code == -1073741511)
                return I18n.T("err.vcRuntime");
            return I18n.T("err.exited", code);
        }

        static string[] LogsFor(Process p)
        {
            string exe = "";
            try { exe = Path.GetFileNameWithoutExtension(p.StartInfo.FileName).ToLowerInvariant(); } catch { }
            if (exe == "httpd") return new[] { "apache-error.log", "apache-console.log" };
            if (exe == "php-fpm") return new[] { "php_errors.log", "php-" + PackageTypes.Label(Path.GetFileName(Path.GetDirectoryName(p.StartInfo.FileName))) + ".log" };
            if (exe == "nginx") return new[] { "nginx-error.log", "nginx-console.log" };
            if (exe == "php-cgi") return new[] { "php_errors.log", "php-" + PackageTypes.Label(Path.GetFileName(Path.GetDirectoryName(p.StartInfo.FileName))) + ".log" };
            return new[] { exe + ".log" };
        }

        /// <summary>Returns false (and sets the error) when another program already uses the port.</summary>
        protected bool EnsurePortFree(int port)
        {
            if (!ProcessUtil.IsPortInUse(port)) return true;
            var owner = ProcessUtil.PortOwner(port);
            SetState(ServiceState.Error, I18n.T("err.portInUse", port, owner == "" ? "?" : owner));
            return false;
        }

        protected static async Task<bool> WaitForPort(int port, Process p, int timeoutMs)
        {
            var start = DateTime.Now;
            var until = start.AddMilliseconds(timeoutMs);
            var nextConnect = start.AddSeconds(2);
            while (DateTime.Now < until)
            {
                if (p.HasExited) return false;
                if (ProcessUtil.IsPortInUse(port)) return true;
                // On some Windows PCs the listener table does not show a port that is already open (issue #3: PHP ran and
                // served pages while Portly waited 30 s and gave up). After 2 s a local connect, once a second, decides.
                if (DateTime.Now >= nextConnect)
                {
                    if (ProcessUtil.CanConnect(port))
                    {
                        Log.Info("Port " + port + ": open (local connection) after " + (int)(DateTime.Now - start).TotalSeconds + " s, although the listener table does not list it yet.");
                        return true;
                    }
                    nextConnect = DateTime.Now.AddSeconds(1);
                }
                await Task.Delay(150);
            }
            return ProcessUtil.IsPortInUse(port) || ProcessUtil.CanConnect(port);
        }

        /// <summary>
        /// macOS / Linux: send a signal (SIGTERM 15, SIGQUIT 3 ...) and wait for the process to end.
        /// Returns true when it ended. Always false on Windows, where nothing is sent.
        /// </summary>
        protected static bool StopWithSignal(Process p, int signal, int waitMs)
        {
#if !NETFRAMEWORK
            if (!OS.IsWindows)
            {
                try
                {
                    if (p.HasExited) return true;
                    if (!ProcessUtil.Signal(p.Id, signal)) return false;
                    return p.WaitForExit(waitMs);
                }
                catch { return false; }
            }
#endif
            return false;
        }

        /// <summary>Translated text, or <paramref name="fallback"/> (English) while the language files have no such key yet.</summary>
        protected static string TOr(string key, string fallback, params object[] args)
        {
            var s = I18n.T(key, args);
            if (s != key) return s;
            try { return string.Format(fallback, args); } catch { return fallback; }
        }

        /// <summary>Size of logs\&lt;name&gt;.log now, so that only what a new process wrote can be read back later.</summary>
        protected static long LogSize(string logName)
        {
            try { var f = new FileInfo(Path.Combine(Paths.Logs, logName + ".log")); return f.Exists ? f.Length : 0; }
            catch { return 0; }
        }

        /// <summary>What the process wrote to logs\&lt;name&gt;.log after position <paramref name="from"/>.</summary>
        protected static string LogSince(string logName, long from)
        {
            try
            {
                var file = Path.Combine(Paths.Logs, logName + ".log");
                if (!File.Exists(file)) return "";
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < from) from = 0;
                    fs.Seek(from, SeekOrigin.Begin);
                    using (var r = new StreamReader(fs, Encoding.UTF8)) return r.ReadToEnd();
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// A web server that stopped at once on macOS / Linux: "bind: permission denied" is a port below 1024 without
        /// the right to use it, "address already in use" a port someone else holds. Null when the output says neither.
        /// </summary>
        protected static string PortProblemFrom(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(output, @":(\d+): bind: permission denied");
            if (m.Success)
                return TOr("err.portPrivileged", "Port {0} needs administrator rights on this system (ports below 1024). Choose a port such as 8080 in Settings, or let Portly use the low ports.", m.Groups[1].Value);
            m = System.Text.RegularExpressions.Regex.Match(output, @":(\d+): bind: address already in use");
            if (m.Success)
            {
                int port = int.Parse(m.Groups[1].Value);
                var owner = ProcessUtil.PortOwner(port);
                return I18n.T("err.portInUse", port, owner == "" ? "?" : owner);
            }
            return null;
        }

        protected static void StopProcess(Process p, bool ctrlC, int waitMs)
        {
            if (p == null) return;
            try
            {
                if (p.HasExited) return;
                if (ctrlC && ProcessUtil.SendCtrlC(p, waitMs)) return;
                ProcessUtil.KillTree(p.Id);
                p.WaitForExit(5000);
            }
            catch { }
        }
    }
}
