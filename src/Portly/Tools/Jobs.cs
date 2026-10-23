using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Services;

namespace Portly.Tools
{
    public enum JobState { Stopped, Running, Waiting, Failed }

    /// <summary>A background job of a site, as saved in usr\sites.ini: "job.queue=autostart,restart|php artisan queue:work".</summary>
    public class JobDef
    {
        public string Name = "";
        public string Command = "";
        /// <summary>Start with "Start all" (and "portly start").</summary>
        public bool Autostart;
        /// <summary>Start again when it exits (back-off, at most 5 restarts a minute).</summary>
        public bool Restart = true;
        /// <summary>Not a long-running process: Portly runs the command once a minute (Laravel scheduler).</summary>
        public bool Schedule;

        public JobDef Clone() { return (JobDef)MemberwiseClone(); }

        public string Flags
        {
            get
            {
                var f = new List<string>();
                if (Autostart) f.Add("autostart");
                if (Restart) f.Add("restart");
                if (Schedule) f.Add("schedule");
                return string.Join(",", f);
            }
        }
    }

    public class JobPreset
    {
        public string Key, Name, Command;
        public bool Schedule;
        public string TitleKey { get { return "jobs.preset." + Key; } }
    }

    /// <summary>Runtime state of a job.</summary>
    public class Job
    {
        public string Site;
        public JobDef Def;
        public JobState State;
        /// <summary>Last exit / error text for the list.</summary>
        public string Info = "";
        public DateTime Since;
        internal Process Proc;
        internal Timer Timer;
        internal bool StopRequested;
        internal int Generation;
        internal readonly List<DateTime> Exits = new List<DateTime>();
        internal readonly object LogLock = new object();
        internal int LinesSinceCheck;
        public string LogFile { get { return Jobs.LogFileFor(Site, Def.Name); } }
        public bool IsActive { get { return State == JobState.Running || State == JobState.Waiting; } }
    }

    /// <summary>
    /// Background jobs per site (feature "jobs"): queue workers, the Laravel scheduler, Vite, Reverb, any command.
    /// Windows has no supervisor for these; Portly runs them as hidden child processes inside its job object
    /// (so they die with Portly), with the site's PHP version first on PATH, restarts them with back-off
    /// and writes their output to logs\jobs\&lt;site&gt;-&lt;job&gt;.log. Stop = Ctrl+C first, then kill the tree.
    /// </summary>
    public static class Jobs
    {
        static readonly Dictionary<string, Job> All = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        public static event Action Changed;
        public static readonly Regex NameRx = new Regex(@"^[a-z0-9][a-z0-9_-]{0,39}$");
        public const int MaxRestartsPerMinute = 5;

        public static readonly List<JobPreset> Presets = new List<JobPreset>
        {
            new JobPreset { Key = "queue",    Name = "queue",    Command = "php artisan queue:work --tries=3" },
            new JobPreset { Key = "schedule", Name = "schedule", Command = "php artisan schedule:run", Schedule = true },
            new JobPreset { Key = "vite",     Name = "vite",     Command = "npm run dev" },
            new JobPreset { Key = "reverb",   Name = "reverb",   Command = "php artisan reverb:start" },
            new JobPreset { Key = "custom",   Name = "",         Command = "" },
        };

        public static string LogDir { get { return Path.Combine(Paths.Logs, "jobs"); } }

        public static string LogFileFor(string site, string job)
        {
            return Path.Combine(LogDir, site.Replace('/', '_') + "-" + job + ".log");
        }

        static string Key(string site, string job) { return site + "|" + job; }

        static void Fire()
        {
            var h = Changed;
            if (h != null) { try { h(); } catch { } }
        }

        // =====================================================================
        // Saved definitions (usr\sites.ini)
        // =====================================================================
        public static List<JobDef> Load(string site)
        {
            var list = new List<JobDef>();
            var ini = new Ini(Paths.SitesFile);
            foreach (var kv in ini.Section(site))
            {
                if (!kv.Key.StartsWith("job.", StringComparison.OrdinalIgnoreCase)) continue;
                var name = kv.Key.Substring(4).ToLowerInvariant();
                if (!NameRx.IsMatch(name)) continue;
                var v = kv.Value;
                int bar = v.IndexOf('|');
                var flags = bar < 0 ? "" : v.Substring(0, bar);
                var cmd = bar < 0 ? v : v.Substring(bar + 1);
                var f = flags.Split(',').Select(x => x.Trim().ToLowerInvariant()).ToList();
                list.Add(new JobDef { Name = name, Command = cmd.Trim(), Autostart = f.Contains("autostart"), Restart = f.Contains("restart"), Schedule = f.Contains("schedule") });
            }
            return list;
        }

        public static void Save(string site, IEnumerable<JobDef> defs)
        {
            var ini = new Ini(Paths.SitesFile);
            foreach (var k in ini.Section(site).Keys.Where(k => k.StartsWith("job.", StringComparison.OrdinalIgnoreCase)).ToList()) ini.Remove(site, k);
            foreach (var d in defs)
            {
                if (!NameRx.IsMatch(d.Name) || string.IsNullOrWhiteSpace(d.Command)) continue;
                ini.Set(site, "job." + d.Name, d.Flags + "|" + d.Command.Replace("\r", " ").Replace("\n", " ").Trim());
            }
            ini.Save();
        }

        /// <summary>Add or replace one job definition.</summary>
        public static void Upsert(string site, JobDef def)
        {
            var list = Load(site);
            list.RemoveAll(d => d.Name == def.Name);
            list.Add(def);
            Save(site, list);
        }

        // =====================================================================
        // Runtime
        // =====================================================================
        public static Job Get(string site, string name)
        {
            lock (All) { Job j; return All.TryGetValue(Key(site, name), out j) ? j : null; }
        }

        public static JobState StateOf(string site, string name)
        {
            var j = Get(site, name);
            return j == null ? JobState.Stopped : j.State;
        }

        public static int ActiveCount(string site)
        {
            lock (All) return All.Values.Count(j => j.Site == site && j.IsActive);
        }

        public static int ActiveCountAll()
        {
            lock (All) return All.Values.Count(j => j.IsActive);
        }

        public static List<Job> Active()
        {
            lock (All) return All.Values.Where(j => j.IsActive).ToList();
        }

        /// <summary>Environment for jobs and hooks: the site's PHP first on PATH, then Portly's tools (Node, Composer ...).</summary>
        public static Dictionary<string, string> EnvFor(Site site)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new List<string>();
            if (site != null && site.PhpFolder != "") dirs.Add(Path.Combine(Paths.BinOf("php"), site.PhpFolder));
            dirs.AddRange(Terminal.PathDirs());
            dirs.Add(Environment.GetEnvironmentVariable("PATH") ?? "");
            env["PATH"] = string.Join(";", dirs.Where(d => d != "").Distinct(StringComparer.OrdinalIgnoreCase));
            env["PORTLY_ROOT"] = Paths.Root;
            if (site != null)
            {
                env["PORTLY_SITE"] = site.Name;
                env["PORTLY_SITE_DIR"] = site.Folder;
                env["PORTLY_SITE_URL"] = site.PreferredUrl;
            }
            if (File.Exists(Certs.BundleFile))
            {
                env["CURL_CA_BUNDLE"] = Certs.BundleFile;
                env["REQUESTS_CA_BUNDLE"] = Certs.BundleFile;
            }
            if (File.Exists(Certs.CaCertPath)) env["NODE_EXTRA_CA_CERTS"] = Certs.CaCertPath;
            return env;
        }

        public static void Start(Site site, JobDef def)
        {
            if (site == null || def == null || string.IsNullOrWhiteSpace(def.Command)) return;
            Job job;
            lock (All)
            {
                if (!All.TryGetValue(Key(site.Name, def.Name), out job)) All[Key(site.Name, def.Name)] = job = new Job { Site = site.Name };
                if (job.IsActive) return;
                job.Def = def.Clone();
                job.StopRequested = false;
                job.Generation++;
                job.Exits.Clear();
                job.Info = "";
                job.Since = DateTime.Now;
            }
            Directory.CreateDirectory(LogDir);
            if (def.Schedule)
            {
                job.State = JobState.Running;
                Marker(job, I18n.T("jobs.log.scheduleOn", def.Command));
                // run at the start of every minute, like cron
                var due = 60000 - (int)(DateTime.Now.TimeOfDay.TotalMilliseconds % 60000) + 200;
                int gen = job.Generation;
                job.Timer = new Timer(_ => ScheduleTick(job, site, gen), null, due, 60000);
                Log.Info(I18n.T("jobs.log.started", site.Name, def.Name));
                Fire();
                return;
            }
            Launch(job, site);
            Log.Info(I18n.T("jobs.log.started", site.Name, def.Name));
        }

        static void Launch(Job job, Site site)
        {
            Process p;
            try { p = Spawn(job, site, job.Def.Command, proc => OnExit(job, site, proc)); }
            catch (Exception ex)
            {
                job.State = JobState.Failed;
                job.Info = ex.Message;
                Marker(job, ex.Message);
                Fire();
                return;
            }
            lock (All) job.Proc = p;
            job.State = JobState.Running;
            Fire();
        }

        static Process Spawn(Job job, Site site, string command, Action<Process> onExit)
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(cmd) || !File.Exists(cmd)) cmd = "cmd.exe";
            var psi = new ProcessStartInfo(cmd, "/d /s /c \"" + command + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,   // own hidden console, so Ctrl+C reaches the whole job and nothing else
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = Directory.Exists(site.Folder) ? site.Folder : Paths.Root,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var kv in EnvFor(site)) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler h = (s, e) => { if (e.Data != null) Write(job, e.Data); };
            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;
            p.Exited += (s, e) => { try { onExit(p); } catch { } };
            Marker(job, "> " + command);
            p.Start();
            JobObject.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        static void OnExit(Job job, Site site, Process p)
        {
            int gen;
            lock (All)
            {
                if (job.Proc != p) return;
                job.Proc = null;
                gen = job.Generation;
            }
            try { p.WaitForExit(); } catch { }   // let the output readers finish
            int code = 0;
            try { code = p.ExitCode; } catch { }
            Marker(job, I18n.T("jobs.log.exited", code));
            if (job.StopRequested) { job.State = JobState.Stopped; Fire(); return; }
            if (!job.Def.Restart)
            {
                job.State = code == 0 ? JobState.Stopped : JobState.Failed;
                job.Info = I18n.T("err.exited", code);
                Fire();
                return;
            }
            int count;
            lock (All)
            {
                job.Exits.RemoveAll(t => t < DateTime.Now.AddMinutes(-1));
                job.Exits.Add(DateTime.Now);
                count = job.Exits.Count;
            }
            if (count > MaxRestartsPerMinute)
            {
                job.State = JobState.Failed;
                job.Info = I18n.T("jobs.gaveUp", MaxRestartsPerMinute);
                Marker(job, job.Info);
                Log.Warn(site.Name + " / " + job.Def.Name + ": " + job.Info);
                Fire();
                return;
            }
            // back-off: 1, 2, 4, 8, 16 s
            var delay = Math.Min(30, 1 << (count - 1));
            job.State = JobState.Waiting;
            job.Info = I18n.T("jobs.restartingIn", delay, code);
            Fire();
            Task.Delay(delay * 1000).ContinueWith(_ =>
            {
                if (job.StopRequested || job.Generation != gen) return;
                Launch(job, site);
            });
        }

        static void ScheduleTick(Job job, Site site, int gen)
        {
            if (job.StopRequested || job.Generation != gen) return;
            lock (All)
            {
                if (job.Proc != null)
                {
                    bool busy = true;
                    try { busy = !job.Proc.HasExited; } catch { }
                    if (busy) { Marker(job, I18n.T("jobs.log.stillRunning")); return; }
                }
            }
            try
            {
                var p = Spawn(job, site, job.Def.Command, proc =>
                {
                    lock (All) if (job.Proc == proc) job.Proc = null;
                    try { proc.WaitForExit(); Marker(job, I18n.T("jobs.log.exited", proc.ExitCode)); } catch { }
                });
                lock (All) job.Proc = p;
            }
            catch (Exception ex) { Marker(job, ex.Message); }
        }

        public static Task StopAsync(string site, string name)
        {
            var job = Get(site, name);
            return job == null ? Task.FromResult(0) : StopAsync(job);
        }

        static async Task StopAsync(Job job)
        {
            Process p;
            lock (All)
            {
                job.StopRequested = true;
                job.Generation++;
                if (job.Timer != null) { job.Timer.Dispose(); job.Timer = null; }
                p = job.Proc;
            }
            if (p != null) await Task.Run(() => StopProcess(p)).ConfigureAwait(false);
            lock (All) job.Proc = null;
            if (job.State != JobState.Stopped)
            {
                job.State = JobState.Stopped;
                job.Info = "";
                Marker(job, I18n.T("jobs.log.stopped"));
                Log.Info(I18n.T("jobs.log.stoppedLog", job.Site, job.Def.Name));
            }
            Fire();
        }

        /// <summary>Ctrl+C first (queue:work finishes the current job, Vite closes cleanly), then kill the tree.</summary>
        static void StopProcess(Process p)
        {
            try
            {
                if (p.HasExited) return;
                if (ProcessUtil.SendCtrlC(p, 5000)) return;
                ProcessUtil.KillTree(p.Id);
                p.WaitForExit(5000);
            }
            catch { }
        }

        public static async Task RestartAsync(Site site, JobDef def)
        {
            await StopAsync(site.Name, def.Name).ConfigureAwait(false);
            Start(site, def);
        }

        public static Task StopAllAsync()
        {
            List<Job> list;
            lock (All) list = All.Values.Where(j => j.IsActive || j.Proc != null || j.Timer != null).ToList();
            if (list.Count == 0) return Task.FromResult(0);
            return Task.WhenAll(list.Select(StopAsync));
        }

        /// <summary>"Start all": start the jobs marked autostart of every valid site.</summary>
        public static void StartAutostart(IEnumerable<Site> sites)
        {
            if (!Features.IsOn("jobs")) return;
            foreach (var site in sites.Where(s => s.Valid))
                foreach (var def in Load(site.Name).Where(d => d.Autostart))
                    if (!StateOf(site.Name, def.Name).Equals(JobState.Running)) Start(site, def);
        }

        /// <summary>Forget a removed job (stops it first).</summary>
        public static async Task RemoveAsync(string site, string name)
        {
            await StopAsync(site, name).ConfigureAwait(false);
            lock (All) All.Remove(Key(site, name));
            Fire();
        }

        // ---------------- log ----------------
        static void Marker(Job job, string text)
        {
            Write(job, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] portly: " + text);
        }

        static void Write(Job job, string line)
        {
            lock (job.LogLock)
            {
                try
                {
                    var file = job.LogFile;
                    if (++job.LinesSinceCheck >= 200 || job.LinesSinceCheck == 1)
                    {
                        job.LinesSinceCheck = 1;
                        var fi = new FileInfo(file);
                        if (fi.Exists && fi.Length > Settings.Current.JobLogMaxMb * 1024L * 1024L)
                        {
                            var old = file + ".1";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(file, old);
                        }
                    }
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
