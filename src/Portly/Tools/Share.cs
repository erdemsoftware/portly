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
    /// Share a local site on the internet with a free Cloudflare "quick tunnel" (https://xxx.trycloudflare.com).
    /// No Cloudflare account is needed. The tunnel lives until it is stopped or Portly exits.
    /// </summary>
    public static class Share
    {
        static readonly Dictionary<string, Process> Tunnels = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> Urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex UrlRx = new Regex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);

        public static bool IsAvailable { get { return PackageTypes.ActiveDir("cloudflared") != null || Catalog.Recommended("cloudflared") != null; } }

        public static string UrlOf(Site site)
        {
            lock (Tunnels) { string u; return Urls.TryGetValue(site.Host, out u) ? u : null; }
        }

        /// <summary>Starts the tunnel (installs cloudflared first when needed) and returns the public URL.</summary>
        public static async Task<string> StartAsync(Site site, Action<string> status)
        {
            var existing = UrlOf(site);
            if (existing != null) return existing;
            if (PackageTypes.ActiveDir("cloudflared") == null)
            {
                var entry = Catalog.Recommended("cloudflared");
                if (entry == null) throw new Exception(I18n.T("err.notInstalled", "cloudflared"));
                status(I18n.T("log.downloading", "cloudflared"));
                await Installer.InstallAsync(entry, null, CancellationToken.None);
            }
            var exe = Path.Combine(PackageTypes.ActiveDir("cloudflared"), "cloudflared.exe");
            var port = Settings.Current.HttpPort;
            // plain http to the local web server; the Host header picks the right site
            var psi = new ProcessStartInfo(exe, "tunnel --no-autoupdate --url http://127.0.0.1:" + port + " --http-host-header " + site.Host)
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
                lock (Tunnels) { Tunnels.Remove(site.Host); Urls.Remove(site.Host); }
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
            lock (Tunnels) { Tunnels[site.Host] = p; Urls[site.Host] = url; }
            Log.Info(I18n.T("share.started", site.Host, url));
            return url;
        }

        public static void Stop(Site site)
        {
            Process p;
            lock (Tunnels)
            {
                if (!Tunnels.TryGetValue(site.Host, out p)) return;
                Tunnels.Remove(site.Host);
                Urls.Remove(site.Host);
            }
            try { if (!p.HasExited) ProcessUtil.KillTree(p.Id); } catch { }
            Log.Info(I18n.T("share.stopped", site.Host));
        }

        public static void StopAll()
        {
            List<Process> list;
            lock (Tunnels) { list = Tunnels.Values.ToList(); Tunnels.Clear(); Urls.Clear(); }
            foreach (var p in list) try { if (!p.HasExited) ProcessUtil.KillTree(p.Id); } catch { }
        }
    }
}
