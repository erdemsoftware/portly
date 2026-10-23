using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>
    /// Scripts that run around "Start all" / "Stop all":
    ///   global:   usr\hooks\before-start.cmd, after-start.cmd, before-stop.cmd, after-stop.cmd (feature "cli")
    ///   per site: sites.ini "hook.after-start=..." written by an applied portly.yml (feature "projectFile";
    ///             only when the user ticked "Allow commands from this file")
    /// Each runs hidden with a timeout; output goes to the Portly log. Env: PORTLY_ROOT, PORTLY_EVENT (+ PORTLY_SITE ...).
    /// </summary>
    public static class Hooks
    {
        public static readonly string[] Events = { "before-start", "after-start", "before-stop", "after-stop" };

        public static string Dir { get { return Path.Combine(Paths.Usr, "hooks"); } }

        public static string GlobalFile(string ev) { return Path.Combine(Dir, ev + ".cmd"); }

        public static bool IsEvent(string ev) { return Events.Contains(ev); }

        /// <summary>Per-site hook commands from usr\sites.ini.</summary>
        public static Dictionary<string, string> SiteHooks(string site)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in new Ini(Paths.SitesFile).Section(site))
                if (kv.Key.StartsWith("hook.", StringComparison.OrdinalIgnoreCase) && IsEvent(kv.Key.Substring(5).ToLowerInvariant()) && kv.Value.Trim() != "")
                    d[kv.Key.Substring(5).ToLowerInvariant()] = kv.Value.Trim();
            return d;
        }

        public static void SetSiteHooks(string site, IDictionary<string, string> hooks)
        {
            var ini = new Ini(Paths.SitesFile);
            foreach (var k in ini.Section(site).Keys.Where(k => k.StartsWith("hook.", StringComparison.OrdinalIgnoreCase)).ToList()) ini.Remove(site, k);
            foreach (var kv in hooks)
                if (IsEvent(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    ini.Set(site, "hook." + kv.Key, kv.Value.Replace("\r", " ").Replace("\n", " ").Trim());
            ini.Save();
        }

        /// <summary>Is there anything to run for this event? (cheap: no process is started when not)</summary>
        static List<Tuple<Site, string>> Collect(string ev, IEnumerable<Site> sites)
        {
            var list = new List<Tuple<Site, string>>();
            if (Features.IsOn("cli") && File.Exists(GlobalFile(ev))) list.Add(Tuple.Create((Site)null, "\"" + GlobalFile(ev) + "\""));
            if (Features.IsOn("projectFile"))
                foreach (var s in sites.Where(x => x.Valid))
                {
                    string cmd;
                    if (SiteHooks(s.Name).TryGetValue(ev, out cmd)) list.Add(Tuple.Create(s, cmd));
                }
            return list;
        }

        /// <summary>Runs the hooks of an event one after another. Completes synchronously when there is none.</summary>
        public static Task RunAsync(string ev)
        {
            var todo = Collect(ev, ServiceManager.CurrentSites);
            if (todo.Count == 0) return Task.FromResult(0);
            return Task.Run(() => { foreach (var t in todo) Run(ev, t.Item1, t.Item2); });
        }

        /// <summary>Run one hook command; returns its result (also used by the tests).</summary>
        public static RunResult Run(string ev, Site site, string command)
        {
            var env = Jobs.EnvFor(site);
            env["PORTLY_EVENT"] = ev;
            var who = site == null ? "usr\\hooks\\" + ev + ".cmd" : site.Name + " (portly.yml)";
            Log.Info(I18n.T("hooks.running", ev, who));
            var cmd = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(cmd) || !File.Exists(cmd)) cmd = "cmd.exe";
            var dir = site != null && Directory.Exists(site.Folder) ? site.Folder : Paths.Root;
            var r = ProcessUtil.Run(cmd, "/d /s /c \"" + command + "\"", dir, env, Settings.Current.HookTimeoutSeconds * 1000);
            foreach (var line in r.Output.Split('\n').Select(l => l.TrimEnd()).Where(l => l != "").Take(200))
                Log.Info("  [" + ev + "] " + line);
            if (r.TimedOut) Log.Warn(I18n.T("hooks.timeout", who, Settings.Current.HookTimeoutSeconds));
            else if (r.ExitCode != 0) Log.Warn(I18n.T("hooks.failed", who, r.ExitCode));
            return r;
        }
    }
}
