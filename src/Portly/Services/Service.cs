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
            if (code == -1073741515 || code == -1073741701 || code == -1073741511)
                return I18n.T("err.vcRuntime");
            return I18n.T("err.exited", code);
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
            var until = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < until)
            {
                if (p.HasExited) return false;
                if (ProcessUtil.IsPortInUse(port)) return true;
                await Task.Delay(150);
            }
            return ProcessUtil.IsPortInUse(port);
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
