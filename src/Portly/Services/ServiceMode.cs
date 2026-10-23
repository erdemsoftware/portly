using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Portly.Core;

namespace Portly.Services
{
    /// <summary>
    /// Service mode (feature "serviceMode"): "Portly.exe --service" runs ServiceManager headless as the Windows
    /// service "Portly" (LocalSystem, automatic start, restarts after a crash), so sites and databases are up
    /// before anyone signs in. The GUI then only controls it: Start / Stop all start / stop the Windows service,
    /// single services and reloads go over a named pipe, and the states shown are the ones the service reports.
    /// </summary>
    public static class ServiceMode
    {
        public const string Name = "Portly";

        /// <summary>True inside "Portly.exe --service".</summary>
        public static bool IsServiceProcess { get; private set; }

        public static string PipeName { get { return "portly-service-" + Fnv(Paths.Root.ToLowerInvariant()); } }

        public static bool IsSupported { get { return Features.IsOn("serviceMode"); } }

        static bool _installedCache;
        static DateTime _installedAt;

        /// <summary>The "Portly" service exists and runs this Portly.exe.</summary>
        public static bool IsInstalled
        {
            get
            {
                if (_installedAt > DateTime.Now.AddSeconds(-5)) return _installedCache;
                _installedCache = ImagePath().IndexOf(Paths.Exe, StringComparison.OrdinalIgnoreCase) >= 0;
                _installedAt = DateTime.Now;
                return _installedCache;
            }
        }

        /// <summary>Command line of the installed "Portly" service, "" when there is none.</summary>
        public static string ImagePath()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name))
                    return k == null ? "" : (k.GetValue("ImagePath") as string ?? "");
            }
            catch { return ""; }
        }

        /// <summary>The GUI controls the Windows service instead of running the programs itself.</summary>
        public static bool IsController
        {
            get { return !IsServiceProcess && IsSupported && Settings.Current.RunAsService && IsInstalled; }
        }

        // ---------------- install / uninstall (unit-tested command lines) ----------------
        public static string ServiceCommandLine(string exe) { return "\"" + exe + "\" --service"; }

        /// <summary>
        /// Service security: the default (SY, BA, read-only for interactive users) plus start / stop / query for
        /// the user who switched service mode on, so the GUI needs no UAC prompt for Start / Stop all.
        /// </summary>
        public static string Sddl(string sid)
        {
            return "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;CCLCSWRPWPDTLOCRRC;;;" + sid + ")";
        }

        public static List<string> InstallCommands(string exe, string sid, string root)
        {
            return new List<string>
            {
                "create " + Name + " binPath= " + Tools.Deploy.Arg(ServiceCommandLine(exe)) + " start= auto obj= LocalSystem DisplayName= " + Name,
                "description " + Name + " " + Tools.Deploy.Arg("Portly: web server, PHP and databases of " + root),
                "failure " + Name + " reset= 86400 actions= restart/5000/restart/15000/restart/60000",
                "sdset " + Name + " " + Sddl(sid),
            };
        }

        /// <summary>Files the service (LocalSystem) creates stay writable for the user: inheritable Modify on the Portly folder.</summary>
        public static string IcaclsArgs(string root, string sid) { return Tools.Deploy.Arg(root) + " /grant *" + sid + ":(OI)(CI)M /Q"; }

        static bool ValidSid(string sid)
        {
            try { new SecurityIdentifier(sid); return sid.StartsWith("S-1-5-", StringComparison.Ordinal); } catch { return false; }
        }

        /// <summary>Elevated helper "--install-service &lt;sid&gt;". Returns 0 on success.</summary>
        public static int InstallElevated(string sid)
        {
            if (!ValidSid(sid)) return 87;
            var existing = ImagePath();
            if (existing != "" && existing.IndexOf(Paths.Exe, StringComparison.OrdinalIgnoreCase) < 0) return 1073; // another Portly owns the name
            if (existing != "") Sc("stop " + Name);
            if (existing != "") Sc("delete " + Name);
            foreach (var cmd in InstallCommands(Paths.Exe, sid, Paths.Root))
            {
                var r = Sc(cmd);
                if (!r.Ok) { Log.Error("sc " + cmd.Split(' ')[0] + ": " + r.Output); return r.ExitCode == 0 ? 1 : r.ExitCode; }
            }
            var acl = ProcessUtil.Run(Path.Combine(Environment.SystemDirectory, "icacls.exe"), IcaclsArgs(Paths.Root, sid), Paths.Root, null, 600000);
            if (!acl.Ok) Log.Warn("icacls: " + acl.Output);
            return 0;
        }

        /// <summary>Elevated helper "--uninstall-service".</summary>
        public static int UninstallElevated()
        {
            if (ImagePath() == "") return 0;
            if (ImagePath().IndexOf(Paths.Exe, StringComparison.OrdinalIgnoreCase) < 0) return 1073;
            Sc("stop " + Name);
            for (int i = 0; i < 60 && Status() != ServiceControllerStatus.Stopped && Status() != null; i++) Thread.Sleep(500);
            var r = Sc("delete " + Name);
            return r.Ok ? 0 : r.ExitCode;
        }

        static RunResult Sc(string args) { return ProcessUtil.Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), args, Environment.SystemDirectory, null, 60000); }

        /// <summary>GUI: install with one UAC prompt. Returns 0, -2 (cancelled) or an error code.</summary>
        public static int Install()
        {
            var s = Settings.Current;
            var sid = WindowsIdentity.GetCurrent().User.Value;
            s.ServiceControllerSid = sid;
            s.MachineRootPassword = s.MysqlRootPassword;
            s.Save();
            int code = WinUtil.RunElevated(Paths.Exe, "--install-service " + sid);
            _installedAt = DateTime.MinValue;
            return code;
        }

        public static int Uninstall()
        {
            StopPolling();
            int code = WinUtil.RunElevated(Paths.Exe, "--uninstall-service");
            _installedAt = DateTime.MinValue;
            if (code == 0)
            {
                var s = Settings.Current;
                s.MachineRootPassword = "";
                s.Save();
                foreach (var svc in ServiceManager.Rows) svc.Mirror(ServiceState.Stopped, null);
            }
            return code;
        }

        // ---------------- GUI side: control and state ----------------
        public static ServiceControllerStatus? Status()
        {
            try { using (var sc = new ServiceController(Name)) return sc.Status; }
            catch { return null; }
        }

        /// <summary>The root password may have changed in the GUI: keep the service's copy current.</summary>
        static void SyncPassword()
        {
            var s = Settings.Current;
            if (s.MachineRootPassword == s.MysqlRootPassword) return;
            s.MachineRootPassword = s.MysqlRootPassword;
            s.Save();
        }

        static async Task StartWindowsServiceAsync(params string[] args)
        {
            SyncPassword();
            await Task.Run(() =>
            {
                using (var sc = new ServiceController(Name))
                {
                    if (sc.Status == ServiceControllerStatus.Running) return;
                    if (sc.Status != ServiceControllerStatus.StartPending) sc.Start(args);
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(90));
                }
            });
        }

        public static async Task StartAllAsync()
        {
            try
            {
                if (Status() == ServiceControllerStatus.Running) await CommandAsync("startall");
                else await StartWindowsServiceAsync();
                await WaitForStatesAsync();
            }
            catch (Exception ex) { Log.Error(I18n.T("service.failed", ex.Message)); }
        }

        public static async Task StopAllAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    using (var sc = new ServiceController(Name))
                    {
                        if (sc.Status == ServiceControllerStatus.Stopped) return;
                        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(90));
                    }
                });
            }
            catch (Exception ex) { Log.Error(I18n.T("service.failed", ex.Message)); }
            Refresh();
        }

        /// <summary>start / stop / restart &lt;id&gt;, startall, reload, reloadweb.</summary>
        public static async Task CommandAsync(string command, string id = null)
        {
            try
            {
                if (Status() != ServiceControllerStatus.Running)
                {
                    // only a start makes sense while the Windows service is stopped: start it with just this program
                    if (command == "start") await StartWindowsServiceAsync("--only", id);
                    else if (command == "startall") await StartWindowsServiceAsync();
                    Refresh();
                    return;
                }
                SyncPassword();
                await Task.Run(() => Send(command + (id != null ? " " + id : ""), 180000));
                await WaitForStatesAsync();
            }
            catch (Exception ex) { Log.Error(I18n.T("service.failed", ex.Message)); }
        }

        static async Task WaitForStatesAsync()
        {
            Refresh();
            for (int i = 0; i < 20 && ServiceManager.Rows.Any(s => s.State == ServiceState.Starting || s.State == ServiceState.Stopping); i++)
            {
                await Task.Delay(500);
                Refresh();
            }
        }

        static string Send(string line, int timeoutMs)
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
            {
                pipe.Connect(3000);
                pipe.ReadMode = PipeTransmissionMode.Byte;
                var data = Encoding.UTF8.GetBytes(line + "\n");
                pipe.Write(data, 0, data.Length);
                pipe.Flush();
                var task = new StreamReader(pipe, Encoding.UTF8).ReadToEndAsync();
                if (!task.Wait(timeoutMs)) throw new System.TimeoutException();
                return task.Result;
            }
        }

        static readonly string Sep = ((char)0x2028).ToString(); // keeps a multi-line error on one line

        public static string FormatStates(IEnumerable<Service> services)
        {
            return string.Join("\n", services.Select(s => s.Id + "\t" + s.State + "\t" + (s.LastError ?? "").Replace("\t", " ").Replace("\r", "").Replace("\n", Sep)));
        }

        public static Dictionary<string, Tuple<ServiceState, string>> ParseStates(string text)
        {
            var d = new Dictionary<string, Tuple<ServiceState, string>>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                var parts = line.Split('\t');
                ServiceState st;
                if (parts.Length >= 2 && Enum.TryParse(parts[1].Trim(), out st))
                    d[parts[0]] = Tuple.Create(st, parts.Length > 2 ? parts[2].Replace(Sep, "\n").Trim() : "");
            }
            return d;
        }

        /// <summary>Ask the service for its states and show them on the GUI's service objects.</summary>
        public static void Refresh()
        {
            Dictionary<string, Tuple<ServiceState, string>> states = null;
            var st = Status();
            if (st == ServiceControllerStatus.Running)
            {
                try { states = ParseStates(Send("status", 5000)); } catch { }
            }
            foreach (var svc in ServiceManager.Rows)
            {
                Tuple<ServiceState, string> t;
                if (states != null && states.TryGetValue(svc.Id, out t)) svc.Mirror(t.Item1, t.Item2);
                else if (st == ServiceControllerStatus.StartPending) svc.Mirror(Settings.Current.IsEnabled(svc.Id) ? ServiceState.Starting : ServiceState.Stopped, null);
                else if (st == ServiceControllerStatus.StopPending) svc.Mirror(svc.State == ServiceState.Stopped ? ServiceState.Stopped : ServiceState.Stopping, null);
                else svc.Mirror(ServiceState.Stopped, null);
            }
        }

        static Timer _poll;

        public static void StartPolling()
        {
            if (_poll != null) return;
            int busy = 0;
            _poll = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref busy, 1) == 1) return;
                try { if (IsController) Refresh(); } catch { }
                finally { busy = 0; }
            }, null, 0, 2000);
        }

        public static void StopPolling()
        {
            if (_poll != null) { _poll.Dispose(); _poll = null; }
        }

        // ---------------- service side ----------------
        public static int RunService()
        {
            IsServiceProcess = true;
            Settings.ReadOnly = true;
            ServiceBase.Run(new PortlyWindowsService());
            return 0;
        }

        internal static void LoadSettings()
        {
            Settings.Load();
            var pw = Settings.Current.MachineRootPassword;
            // in memory only (Settings.ReadOnly): mysqladmin shutdown and backups need the root password
            if (pw != "") Settings.Current.MysqlRootPassword = pw;
            I18n.Load(Settings.Current.Language);
        }

        internal static async Task StartInServiceAsync(string[] only)
        {
            if (only == null || only.Length == 0) await ServiceManager.StartAllAsync();
            else foreach (var id in only) { var s = Find(id); if (s != null) await ServiceManager.StartAsync(s); }
        }

        static Service Find(string id) { return ServiceManager.Rows.FirstOrDefault(s => s.Id == id); }

        internal static void PipeLoop(CancellationToken ct)
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            var sid = Settings.Current.ServiceControllerSid;
            if (ValidSid(sid)) sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 65536, sec))
                    {
                        var wait = pipe.WaitForConnectionAsync(ct);
                        wait.Wait(ct);
                        var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                        var line = (reader.ReadLine() ?? "").Trim();
                        var answer = Handle(line);
                        var data = Encoding.UTF8.GetBytes(answer);
                        pipe.Write(data, 0, data.Length);
                        pipe.Flush();
                        pipe.WaitForPipeDrain();
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Warn("Service pipe: " + ex.Message); Thread.Sleep(500); }
            }
        }

        static readonly SemaphoreSlim Busy = new SemaphoreSlim(1, 1);

        internal static string Handle(string line)
        {
            var parts = line.Split(new[] { ' ' }, 2);
            var cmd = parts[0];
            var arg = parts.Length > 1 ? parts[1].Trim() : "";
            if (cmd == "status") return FormatStates(ServiceManager.Rows);
            Busy.Wait();
            try
            {
                Log.Info(I18n.T("service.command", line));
                switch (cmd)
                {
                    case "start": { var s = Find(arg); if (s != null) ServiceManager.StartAsync(s).Wait(); break; }
                    case "stop": { var s = Find(arg); if (s != null) ServiceManager.StopAsync(s).Wait(); break; }
                    case "restart": { var s = Find(arg); if (s != null) { ServiceManager.StopAsync(s).Wait(); ServiceManager.StartAsync(s).Wait(); } break; }
                    case "startall": ServiceManager.StartAllAsync().Wait(); break;
                    case "reloadweb":
                        LoadSettings();
                        ServiceManager.ReloadWebAsync().Wait();
                        break;
                    case "reload":
                        {
                            // settings changed in the GUI: restart what runs with the new configuration
                            var running = ServiceManager.Rows.Where(s => s.IsActive && s.Id != "php").Select(s => s.Id).ToList();
                            ServiceManager.StopAllAsync().Wait();
                            LoadSettings();
                            foreach (var id in running) { var s = Find(id); if (s != null) ServiceManager.StartAsync(s).Wait(); }
                            break;
                        }
                    default: return "unknown command";
                }
                return FormatStates(ServiceManager.Rows);
            }
            catch (Exception ex) { Log.Error("Service command " + line, ex); return "error"; }
            finally { Busy.Release(); }
        }

        static string Fnv(string s)
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }

    /// <summary>The Windows service host: ServiceManager without any window.</summary>
    public class PortlyWindowsService : ServiceBase
    {
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        Thread _pipe;

        public PortlyWindowsService()
        {
            ServiceName = ServiceMode.Name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            Net.Init();
            Paths.EnsureAll();
            ServiceMode.LoadSettings();
            var only = new List<string>();
            int i = Array.IndexOf(args ?? new string[0], "--only");
            if (i >= 0 && i + 1 < args.Length) only.Add(args[i + 1]);
            Log.Info(I18n.T("service.started", Paths.Root));
            _pipe = new Thread(() => ServiceMode.PipeLoop(_cts.Token)) { IsBackground = true, Name = "Portly service pipe" };
            _pipe.Start();
            // start in the background: Windows waits at most 30 s for OnStart, MySQL may need longer after a crash
            Task.Run(async () =>
            {
                try { await ServiceMode.StartInServiceAsync(only.ToArray()); }
                catch (Exception ex) { Log.Error("Service start", ex); }
            });
        }

        protected override void OnStop() { StopAll(); }
        protected override void OnShutdown() { StopAll(); }

        void StopAll()
        {
            RequestAdditionalTime(60000);
            _cts.Cancel();
            try { ServiceManager.StopAllAsync().Wait(55000); } catch { }
            Log.Info(I18n.T("service.stopped"));
        }
    }
}
