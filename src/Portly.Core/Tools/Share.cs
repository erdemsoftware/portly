using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;

namespace Portly.Tools
{
    /// <summary>
    /// Share a local site on the internet with Cloudflare: a free "quick tunnel" (random https://xxx.trycloudflare.com,
    /// no account needed) or, with the user's Cloudflare account, a fixed address (CloudflareTunnel).
    /// cloudflared talks to the project's local share proxy (LanShare.StartTunnelProxy), which rewrites addresses and
    /// adds the password, device list, time limit and PHP error report. Runs until it is stopped or Portly exits.
    /// </summary>
    public static class Share
    {
        static readonly Dictionary<string, Process> Quick = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> Fixed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // site -> fixed address
        static readonly Dictionary<string, string> Urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex UrlRx = new Regex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);

        public static bool IsAvailable { get { return PackageTypes.ActiveDir("cloudflared") != null || Catalog.Recommended("cloudflared") != null; } }

        public static string UrlOf(Site site) { return UrlOf(site.Host); }

        public static string UrlOf(string host)
        {
            lock (Urls) { string u; return Urls.TryGetValue(host, out u) ? u : null; }
        }

        public static bool IsRunning(string host) { lock (Urls) return Urls.ContainsKey(host); }

        /// <summary>cloudflared.exe, installed first when needed.</summary>
        public static async Task<string> EnsureCloudflaredAsync(Action<string> status)
        {
            if (PackageTypes.ActiveDir("cloudflared") == null)
            {
                var entry = Catalog.Recommended("cloudflared");
                if (entry == null) throw new Exception(I18n.T("err.notInstalled", "cloudflared"));
                status(I18n.T("log.downloading", "cloudflared"));
                await Installer.InstallAsync(entry, null, CancellationToken.None);
            }
            return Path.Combine(PackageTypes.ActiveDir("cloudflared"), "cloudflared.exe");
        }

        /// <summary>Starts the tunnel and returns the public URL (fixed address when one is set and the account is connected).</summary>
        public static async Task<string> StartAsync(Site site, Action<string> status)
        {
            var existing = UrlOf(site);
            if (existing != null) return existing;
            var exe = await EnsureCloudflaredAsync(status);
            var stored = ShareStore.Get(site.Host);
            int port = LanShare.StartTunnelProxy(site, stored.OnlinePassword);
            string url;
            bool fixedHost = stored.FixedHost != "" && CloudflareTunnel.IsConnected;
            try
            {
                if (fixedHost)
                {
                    url = await CloudflareTunnel.AddAsync(exe, stored.FixedHost, port, status);
                    lock (Urls) Fixed[site.Host] = stored.FixedHost;
                }
                else url = await StartQuick(exe, site.Host, port, status);
            }
            catch
            {
                LanShare.StopTunnelProxy(site.Host);
                throw;
            }
            lock (Urls) Urls[site.Host] = url;
            // remembered for "Shared projects" (a quick tunnel gets a new random address at every start)
            ShareStore.Update(site.Host, e => { e.Online = true; e.OnlineUrl = url; });
            if (stored.OnlineMinutes > 0)
                LanShare.SetExpiry(site.Host, true, TimeSpan.FromMinutes(stored.OnlineMinutes), () => Stop(site.Host));
            Log.Info(I18n.T("share.started", site.Host, url));
            return url;
        }

        static async Task<string> StartQuick(string exe, string host, int port, Action<string> status)
        {
            var psi = new ProcessStartInfo(exe, "tunnel --no-autoupdate --url http://127.0.0.1:" + port)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var found = new TaskCompletionSource<string>();
            var logFile = Path.Combine(Paths.Logs, "cloudflared.log");
            DataReceivedEventHandler h = (s, e) =>
            {
                if (e.Data == null) return;
                try { lock (logFile) File.AppendAllText(logFile, e.Data + Environment.NewLine); } catch { }
                var m = UrlRx.Match(e.Data);
                if (m.Success) found.TrySetResult(m.Value);
            };
            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;
            p.Exited += (s, e) =>
            {
                found.TrySetResult(null);
                bool was;
                lock (Urls) { Process q; was = Quick.TryGetValue(host, out q) && q == p; if (was) { Quick.Remove(host); Urls.Remove(host); } }
                if (was) { LanShare.StopTunnelProxy(host); ShareStore.Notify(); } // the tunnel ended by itself
            };
            status(I18n.T("share.starting"));
            p.Start();
            JobObject.Add(p);
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            var done = await Task.WhenAny(found.Task, Task.Delay(45000));
            var url = done == found.Task ? found.Task.Result : null;
            if (url == null)
            {
                try { if (!p.HasExited) ProcessUtil.KillTree(p.Id); } catch { }
                throw new Exception(I18n.T("share.failed") + " " + I18n.T("err.seeLog", "cloudflared.log"));
            }
            lock (Urls) Quick[host] = p;
            return url;
        }

        public static void Stop(Site site) { Stop(site.Host); }

        public static void Stop(string host)
        {
            Process p = null;
            string fixedHost = null;
            lock (Urls)
            {
                if (!Urls.Remove(host)) return;
                if (Quick.TryGetValue(host, out p)) Quick.Remove(host);
                if (Fixed.TryGetValue(host, out fixedHost)) Fixed.Remove(host);
            }
            if (p != null) try { if (!p.HasExited) ProcessUtil.KillTree(p.Id); } catch { }
            if (fixedHost != null) CloudflareTunnel.Remove(fixedHost);
            LanShare.StopTunnelProxy(host);
            Log.Info(I18n.T("share.stopped", host));
            ShareStore.Notify();
        }

        public static void StopAll()
        {
            List<Process> list;
            lock (Urls) { list = Quick.Values.ToList(); Quick.Clear(); Fixed.Clear(); Urls.Clear(); }
            foreach (var p in list) try { if (!p.HasExited) ProcessUtil.KillTree(p.Id); } catch { }
            CloudflareTunnel.StopProcess();
        }
    }
}
