using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>
    /// Fixed internet address with the user's own Cloudflare account (demo.example.com instead of a random
    /// xyz.trycloudflare.com): one named tunnel for this PC, a DNS record per address, one cloudflared process that
    /// sends each address to the project's local share proxy. Everything lives in usr\cloudflared.
    /// </summary>
    public static class CloudflareTunnel
    {
        static string Dir { get { return Path.Combine(Paths.Usr, "cloudflared"); } }
        static string CertFile { get { return Path.Combine(Dir, "cert.pem"); } }
        static string CredFile { get { return Path.Combine(Dir, "tunnel.json"); } }
        static string IdFile { get { return Path.Combine(Dir, "tunnel.id"); } }
        static string DnsFile { get { return Path.Combine(Dir, "dns.txt"); } }
        static string ConfigFile { get { return Path.Combine(Dir, "config.yml"); } }
        /// <summary>Where "cloudflared tunnel login" saves the account certificate.</summary>
        static string LoginCert { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared", "cert.pem"); } }

        static readonly object Sync = new object();
        static readonly Dictionary<string, int> Routes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // address -> proxy port
        static Process _proc;
        static readonly Regex UuidRx = new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);

        /// <summary>The Cloudflare account is connected (fixed addresses can be used).</summary>
        public static bool IsConnected { get { return File.Exists(CertFile); } }

        /// <summary>demo.example.com: letters, digits, dashes, at least one dot, no scheme or path.</summary>
        public static bool IsValidHost(string host)
        {
            return !string.IsNullOrEmpty(host) && host.Length <= 253 &&
                Regex.IsMatch(host, @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Connect the Cloudflare account: "cloudflared tunnel login" opens the browser, the user picks the domain
        /// (e.g. example.com) and the certificate is copied into Portly. An existing login of this Windows user is reused.
        /// </summary>
        public static async Task ConnectAsync(string exe, Action<string> status, CancellationToken ct)
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(LoginCert))
            {
                status(I18n.T("cf.loginOpening"));
                var psi = new ProcessStartInfo(exe, "tunnel --no-autoupdate login")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                };
                using (var p = new Process { StartInfo = psi })
                {
                    DataReceivedEventHandler h = (s, e) =>
                    {
                        if (e.Data == null) return;
                        Log.WriteFileOnly("cloudflared login: " + e.Data);
                        // the browser normally opens by itself; the link is also shown in case it did not
                        var m = Regex.Match(e.Data, @"https://dash\.cloudflare\.com/\S+");
                        if (m.Success) status(I18n.T("cf.loginLink", m.Value));
                    };
                    p.OutputDataReceived += h;
                    p.ErrorDataReceived += h;
                    p.Start();
                    JobObject.Add(p);
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    // up to 10 minutes to log in and pick the domain
                    for (int i = 0; i < 600 && !File.Exists(LoginCert); i++)
                    {
                        if (ct.IsCancellationRequested) { try { ProcessUtil.KillTree(p.Id); } catch { } throw new OperationCanceledException(); }
                        await Task.Delay(1000);
                    }
                    try { if (!p.HasExited) ProcessUtil.KillTree(p.Id); } catch { }
                }
                if (!File.Exists(LoginCert)) throw new Exception(I18n.T("cf.loginTimeout"));
            }
            File.Copy(LoginCert, CertFile, true);
            // a new account (or domain): the old tunnel and DNS records belong to the old one
            foreach (var f in new[] { IdFile, CredFile, DnsFile }) try { File.Delete(f); } catch { }
            Log.Info(I18n.T("cf.connected"));
        }

        /// <summary>Forget the account in Portly (the tunnel and DNS records stay in Cloudflare; they can be deleted there).</summary>
        public static void Disconnect()
        {
            StopProcess();
            lock (Sync) Routes.Clear();
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
        }

        static RunResult Cf(string exe, string args)
        {
            var r = ProcessUtil.Run(exe, "tunnel --no-autoupdate --origincert \"" + CertFile + "\" " + args, Dir, null, 120000);
            Log.WriteFileOnly("cloudflared " + args + ": " + r.Output);
            return r;
        }

        /// <summary>The named tunnel of this PC (created once: "portly-PCNAME-xxxx").</summary>
        static string EnsureTunnel(string exe)
        {
            if (File.Exists(IdFile) && File.Exists(CredFile)) return File.ReadAllText(IdFile).Trim();
            var name = "portly-" + Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9-]", "") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            var r = Cf(exe, "create --credentials-file \"" + CredFile + "\" " + name);
            var id = UuidRx.Match(r.Output);
            if (!r.Ok || !id.Success || !File.Exists(CredFile)) throw new Exception(I18n.T("cf.createFailed") + " " + LastLine(r.Output));
            File.WriteAllText(IdFile, id.Value);
            return id.Value;
        }

        static string LastLine(string output)
        {
            return (output ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l != "") ?? "";
        }

        /// <summary>Send https://<paramref name="host"/> to the local proxy on <paramref name="port"/>; returns the address.</summary>
        public static async Task<string> AddAsync(string exe, string host, int port, Action<string> status)
        {
            if (!IsConnected) throw new Exception(I18n.T("cf.notConnected"));
            host = host.Trim().ToLowerInvariant();
            await Task.Run(() =>
            {
                status(I18n.T("cf.preparing", host));
                var id = EnsureTunnel(exe);
                // the DNS record (CNAME to the tunnel) once per address; --overwrite-dns replaces an old record of that name
                var done = File.Exists(DnsFile) ? File.ReadAllLines(DnsFile).ToList() : new List<string>();
                if (!done.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    var r = Cf(exe, "route dns --overwrite-dns " + id + " " + host);
                    if (!r.Ok) throw new Exception(I18n.T("cf.dnsFailed", host) + " " + LastLine(r.Output));
                    done.Add(host);
                    File.WriteAllLines(DnsFile, done);
                }
                lock (Sync) Routes[host] = port;
                Restart(exe, id);
            });
            return "https://" + host + "/";
        }

        public static void Remove(string host)
        {
            bool empty;
            lock (Sync) { Routes.Remove(host ?? ""); empty = Routes.Count == 0; }
            if (empty) { StopProcess(); return; }
            var exe = Exe();
            if (exe == null || !File.Exists(IdFile)) return;
            var id = File.ReadAllText(IdFile).Trim();
            // the other addresses keep running: restart in the background (it waits for the connection)
            Task.Run(() => { try { Restart(exe, id); } catch (Exception ex) { Log.Warn(ex.Message); } });
        }

        static string Exe()
        {
            var dir = Packages.PackageTypes.ActiveDir("cloudflared");
            return dir == null ? null : Path.Combine(dir, "cloudflared.exe");
        }

        /// <summary>Write config.yml with every address and (re)start the one cloudflared process; waits for the connection.</summary>
        static void Restart(string exe, string id)
        {
            lock (Sync)
            {
                var yml = new StringBuilder();
                yml.AppendLine("# Written by Portly (fixed addresses of shared projects)");
                yml.AppendLine("tunnel: " + id);
                yml.AppendLine("credentials-file: " + CredFile.Replace('\\', '/'));
                yml.AppendLine("ingress:");
                foreach (var r in Routes)
                {
                    yml.AppendLine("  - hostname: " + r.Key);
                    yml.AppendLine("    service: http://127.0.0.1:" + r.Value);
                }
                yml.AppendLine("  - service: http_status:404");
                File.WriteAllText(ConfigFile, yml.ToString(), new UTF8Encoding(false));

                StopProcess();
                var psi = new ProcessStartInfo(exe, "tunnel --no-autoupdate --config \"" + ConfigFile + "\" run " + id)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Dir,
                };
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var ready = new ManualResetEventSlim(false);
                var logFile = Path.Combine(Paths.Logs, "cloudflared.log");
                DataReceivedEventHandler h = (s, e) =>
                {
                    if (e.Data == null) return;
                    try { lock (logFile) File.AppendAllText(logFile, e.Data + Environment.NewLine); } catch { }
                    if (e.Data.Contains("Registered tunnel connection")) ready.Set();
                };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.Exited += (s, e) => ready.Set();
                p.Start();
                JobObject.Add(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _proc = p;
                ready.Wait(30000);
                if (p.HasExited) throw new Exception(I18n.T("share.failed") + " " + I18n.T("err.seeLog", "cloudflared.log"));
            }
        }

        public static void StopProcess()
        {
            Process p;
            lock (Sync) { p = _proc; _proc = null; }
            if (p != null) try { if (!p.HasExited) ProcessUtil.KillTree(p.Id); } catch { }
        }
    }
}
