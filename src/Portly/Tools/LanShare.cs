using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>
    /// Share one site with the devices on the same network (phone, tablet, another PC): http://192.168.1.20:8901.
    /// Portly listens on a port of its own and passes each request to the local web server with the site's Host header,
    /// so the web server itself stays on 127.0.0.1 and nothing else (phpMyAdmin, other sites) is reachable.
    /// Lives until it is stopped or Portly exits.
    /// </summary>
    public static class LanShare
    {
        const int FirstPort = 8900;
        const long MaxChunkedBody = 64 * 1024 * 1024;
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591); // header bytes kept as they are

        class Entry
        {
            public TcpListener Listener;
            public int Port;
            public bool Online;          // behind a Cloudflare tunnel (listens on 127.0.0.1 only; the address comes from Host)
            public bool Https;           // the site has HTTPS: shared with https (certificate made for the address in use)
            public volatile bool Tls;    // the site sends http to https: reach it on the local HTTPS port
            public string Password = ""; // "" = no password
            public string Token;         // cookie value after the password was given (new at every start)
            public DateTime? ExpiresAt;  // stops by itself then
            public Timer Expiry;
            public readonly Dictionary<string, ShareDevice> Devices = new Dictionary<string, ShareDevice>();
        }
        static readonly Dictionary<string, Entry> Shares = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        // internet shares: cloudflared -> this proxy (127.0.0.1) -> web server, so they get the same rewriting,
        // password, device list, time limit and PHP error report as local network shares
        static readonly Dictionary<string, Entry> Tunnels = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        static Dictionary<string, Entry> Map(bool online) { return online ? Tunnels : Shares; }

        /// <summary>Starts the local proxy an internet share's tunnel points to; returns its port.</summary>
        public static int StartTunnelProxy(Site site, string password)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var entry = new Entry { Listener = listener, Port = ((IPEndPoint)listener.LocalEndpoint).Port, Online = true, Password = password ?? "", Token = Guid.NewGuid().ToString("N") };
            Entry old;
            lock (Shares)
            {
                if (Tunnels.TryGetValue(site.Host, out old)) { try { old.Listener.Stop(); } catch { } DisposeExpiry(old); }
                Tunnels[site.Host] = entry;
            }
            Task.Run(() => AcceptLoop(entry, site.Host));
            return entry.Port;
        }

        public static void StopTunnelProxy(string host)
        {
            Entry e;
            lock (Shares)
            {
                if (!Tunnels.TryGetValue(host, out e)) return;
                Tunnels.Remove(host);
            }
            try { e.Listener.Stop(); } catch { }
            DisposeExpiry(e);
        }

        /// <summary>Devices that opened the share (newest first). Local network: by IP; internet: the visitor's IP from Cloudflare.</summary>
        public static List<ShareDevice> DevicesOf(string host, bool online)
        {
            Entry e;
            lock (Shares)
            {
                if (!Map(online).TryGetValue(host, out e)) return new List<ShareDevice>();
                lock (e.Devices) return e.Devices.Values.OrderByDescending(d => d.Last).Select(d => d.Copy()).ToList();
            }
        }

        /// <summary>When the share stops by itself, or null.</summary>
        public static DateTime? ExpiresAt(string host, bool online)
        {
            Entry e;
            lock (Shares) return Map(online).TryGetValue(host, out e) ? e.ExpiresAt : null;
        }

        /// <summary>Stop the running share after <paramref name="after"/> (null = no limit). <paramref name="stop"/> stops it.</summary>
        public static void SetExpiry(string host, bool online, TimeSpan? after, Action stop)
        {
            Entry e;
            lock (Shares) if (!Map(online).TryGetValue(host, out e)) return;
            DisposeExpiry(e);
            if (after == null) { e.ExpiresAt = null; ShareStore.Notify(); return; }
            e.ExpiresAt = DateTime.Now + after.Value;
            e.Expiry = new Timer(_ =>
            {
                Log.Info(I18n.T("shares.expired", host));
                try { stop(); } catch (Exception ex) { Log.Warn(ex.Message); }
            }, null, after.Value, Timeout.InfiniteTimeSpan);
            ShareStore.Notify();
        }

        static void DisposeExpiry(Entry e)
        {
            var t = e.Expiry;
            e.Expiry = null;
            if (t != null) try { t.Dispose(); } catch { }
        }

        static LanShare()
        {
            // a new IP from the router (or another Wi-Fi): the cards and the list show the new address
            try { NetworkChange.NetworkAddressChanged += (s, e) => { if (Shares.Count > 0) ShareStore.Notify(); }; } catch { }
        }

        public static string UrlOf(Site site) { return UrlOf(site.Host); }

        /// <summary>Address of a running share with the PC's current network address, or null when it is not shared.</summary>
        public static string UrlOf(string host)
        {
            Entry e;
            lock (Shares) if (!Shares.TryGetValue(host, out e)) return null;
            var ip = LocalIp();
            return (e.Https ? "https://" : "http://") + (ip != null ? ip.ToString() : "localhost") + ":" + e.Port + "/";
        }

        /// <summary>The share is running but the web server is not: devices get a "start the web server" page.</summary>
        public static bool WebDown { get { return Services.ServiceManager.Web.State != Services.ServiceState.Running; } }

        /// <summary>IPv4 address of this PC on the local network (the adapter with a gateway, private range first), or null.</summary>
        public static IPAddress LocalIp()
        {
            var found = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    // virtual adapters (WSL, Hyper-V, VPN without a route) have no gateway: other devices cannot reach them
                    if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                    found.AddRange(props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address));
                }
            }
            catch { }
            return found.FirstOrDefault(IsPrivate) ?? found.FirstOrDefault();
        }

        static bool IsPrivate(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
        }

        /// <summary>Starts sharing and returns the address for the other devices.</summary>
        public static string Start(Site site)
        {
            var existing = UrlOf(site);
            if (existing != null) return existing;
            var ip = LocalIp();
            if (ip == null) throw new Exception(I18n.T("lan.noNetwork"));
            TcpListener listener = null;
            // the remembered port first: the address on the phone stays the same
            int port = ShareStore.Get(site.Host).LanPort;
            if (port > 0)
            {
                try { listener = new TcpListener(IPAddress.Any, port); listener.Start(); }
                catch (SocketException) { listener = null; }
            }
            if (listener == null)
            {
                var taken = new HashSet<int>(ShareStore.All().Where(x => x.Host != site.Host).Select(x => x.LanPort));
                for (port = FirstPort; port < FirstPort + 100; port++)
                {
                    if (taken.Contains(port)) continue;
                    try { listener = new TcpListener(IPAddress.Any, port); listener.Start(); break; }
                    catch (SocketException) { listener = null; }
                }
            }
            if (listener == null) throw new Exception(I18n.T("lan.noPort"));
            int chosen = port;
            ShareStore.Update(site.Host, e => { e.Lan = true; e.LanPort = chosen; });
            // a site with HTTPS is shared with HTTPS too (certificate for the address, signed by the Portly CA)
            bool https = false;
            if (Settings.Current.Ssl)
            {
                try { Certs.LanCertificate(ip); https = true; }
                catch (Exception ex) { Log.Warn("LAN certificate: " + ex.Message); }
            }
            var token = Guid.NewGuid().ToString("N");
            var entry = new Entry { Listener = listener, Port = port, Https = https, Password = ShareStore.Get(site.Host).LanPassword, Token = token };
            lock (Shares) Shares[site.Host] = entry;
            Task.Run(() => AcceptLoop(entry, site.Host));
            var minutes = ShareStore.Get(site.Host).LanMinutes;
            if (minutes > 0) SetExpiry(site.Host, false, TimeSpan.FromMinutes(minutes), () => Stop(site.Host));
            var url = UrlOf(site.Host);
            Log.Info(I18n.T("lan.started", site.Host, url));
            ShareStore.Notify();
            return url;
        }

        public static void Stop(Site site) { Stop(site.Host); }

        /// <summary>Stops sharing; the entry (port, note) stays in "Shared projects".</summary>
        public static void Stop(string host)
        {
            Entry e;
            lock (Shares)
            {
                if (!Shares.TryGetValue(host, out e)) return;
                Shares.Remove(host);
            }
            try { e.Listener.Stop(); } catch { }
            DisposeExpiry(e);
            Log.Info(I18n.T("lan.stopped", host));
            ShareStore.Notify();
        }

        /// <summary>New password for a running share; devices that logged in before must log in again.</summary>
        public static void SetPassword(string host, string password, bool online = false)
        {
            lock (Shares)
            {
                Entry e;
                if (!Map(online).TryGetValue(host, out e) || e.Password == (password ?? "")) return;
                e.Password = password ?? "";
                e.Token = Guid.NewGuid().ToString("N");
            }
        }

        public static bool IsRunning(string host) { lock (Shares) return Shares.ContainsKey(host); }

        /// <summary>The address a port has on this network (also for a stopped share), or "" without a network.</summary>
        public static string AddressFor(int port)
        {
            var ip = LocalIp();
            return ip == null || port <= 0 ? "" : (Settings.Current.Ssl ? "https://" : "http://") + ip + ":" + port + "/";
        }

        /// <summary>Is the port free to listen on (not used by another program or another share)?</summary>
        public static bool PortFree(int port, string forHost)
        {
            if (port < 1024 || port > 65535) return false;
            if (ShareStore.All().Any(x => x.Host != forHost && x.LanPort == port)) return false;
            lock (Shares) if (Shares.Any(x => x.Key != forHost && x.Value.Port == port)) return false;
            if (IsRunning(forHost) && ShareStore.Get(forHost).LanPort == port) return true;
            try { var l = new TcpListener(IPAddress.Any, port); l.Start(); l.Stop(); return true; }
            catch (SocketException) { return false; }
        }

        public static void StopAll()
        {
            List<Entry> list;
            lock (Shares) { list = Shares.Values.Concat(Tunnels.Values).ToList(); Shares.Clear(); Tunnels.Clear(); }
            foreach (var e in list) { try { e.Listener.Stop(); } catch { } DisposeExpiry(e); }
        }

        static async Task AcceptLoop(Entry entry, string host)
        {
            while (true)
            {
                TcpClient client;
                try { client = await entry.Listener.AcceptTcpClientAsync(); }
                catch { return; } // stopped
                var _ = Task.Run(() => Serve(client, entry, host));
            }
        }

        /// <summary>
        /// One connection carries one request ("Connection: close", HTTP/1.0 so the answer is not chunked), so each request
        /// can be rewritten: Host = the site, X-Forwarded-Host = the address the device used. In the answer the site's own
        /// address (https://shop.test) becomes the shared one: redirects, links in pages, cookies. A site that sends every
        /// http request to https is reached on the local HTTPS port from then on (it sees HTTPS=on and stops redirecting).
        /// A WebSocket upgrade (Vite) goes out as it came, and only after the web server answered 101 does the connection
        /// become a raw tunnel: until then every byte is the request Portly checked, or nothing could stop a second request
        /// reaching the web server from 127.0.0.1 with its own Host.
        /// </summary>
        static async Task Serve(TcpClient client, Entry entry, string host)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 30000;   // a device that connects and sends nothing
                    Stream cs = client.GetStream();
                    string pub;
                    bool secure;
                    byte[] rest;
                    string head;
                    if (entry.Online)
                    {
                        // from cloudflared: plain http on 127.0.0.1, the public name (xyz.trycloudflare.com / demo.example.com) in Host
                        secure = true;
                        head = ReadHead(cs, out rest);
                        if (head == null) return;
                        if (!WellFormed(head)) { await Refuse(cs, "400 Bad Request"); return; }
                        var hostLine = head.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault(l => Is(Name(l), "Host"));
                        pub = hostLine == null ? "" : hostLine.Substring(hostLine.IndexOf(':') + 1).Trim();
                        if (!Regex.IsMatch(pub, @"^[A-Za-z0-9.-]+(:\d+)?$")) return;
                    }
                    else
                    {
                        // the address the device used (follows a new IP from the router, and the right adapter)
                        var localIp = ((IPEndPoint)client.Client.LocalEndPoint).Address;
                        if (localIp.IsIPv4MappedToIPv6) localIp = localIp.MapToIPv4();
                        pub = localIp + ":" + entry.Port;
                        // the same port answers https and http: a TLS handshake starts with 0x16
                        var first = new byte[1];
                        if (client.Client.Receive(first, 1, SocketFlags.Peek) < 1) return;
                        secure = first[0] == 0x16 && entry.Https;
                        if (secure)
                        {
                            var ssl = new SslStream(cs, false);
                            await ssl.AuthenticateAsServerAsync(Certs.LanCertificate(localIp), false, SslProtocols.Tls12, false);
                            cs = ssl;
                        }
                        head = ReadHead(cs, out rest);
                        if (head == null) return;
                        if (!WellFormed(head)) { await Refuse(cs, "400 Bad Request"); return; }
                        if (await ServeCa(cs, head, pub, entry.Https)) return;
                    }
                    // who is looking: local network = the device's IP, internet = the visitor's IP that Cloudflare passes on
                    var headLines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    if (entry.Online) remote = HeaderValue(headLines, "CF-Connecting-IP") ?? remote;
                    Seen(entry, host, remote, HeaderValue(headLines, "User-Agent"), entry.Online ? HeaderValue(headLines, "CF-IPCountry") : null);
                    // a share with a password: a small login page first, then a cookie
                    if (entry.Password != "")
                    {
                        var allowed = await CheckPassword(cs, head, rest, entry, host, secure);
                        if (allowed == null) return;
                        head = allowed;
                    }
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None).ToList();
                    var method = lines[0].Split(' ')[0].ToUpperInvariant();
                    long length = 0;
                    var cl = HeaderValue(lines, "Content-Length");
                    if (cl != null) length = long.Parse(cl); // WellFormed checked it
                    bool chunked = HeaderValue(lines, "Transfer-Encoding") != null;
                    bool upgrade = !chunked && length == 0 && AsksUpgrade(lines);
                    if (chunked)
                    {
                        // the request goes out as HTTP/1.0: the web server gets the plain body with its length
                        var refused = ReadChunked(cs, ref rest);
                        if (refused != null) { await Refuse(cs, refused); return; }
                        length = rest.Length;
                        lines.RemoveAll(l => Is(Name(l), "Transfer-Encoding"));
                        lines.Add("Content-Length: " + length);
                    }
                    // what the device sent past the head: for an upgrade it waits for the 101, otherwise only the
                    // declared body goes on, and anything after that is dropped (it would be a request of its own)
                    var tunnel = new byte[0];
                    if (upgrade) { tunnel = rest; rest = new byte[0]; }
                    else if (rest.Length > length) rest = rest.Take((int)length).ToArray();
                    long moreBody = length - rest.Length;

                    bool tls = entry.Tls;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        using (var server = new TcpClient())
                        {
                            try { await server.ConnectAsync(IPAddress.Loopback, tls ? Settings.Current.HttpsPort : Settings.Current.HttpPort); }
                            catch (SocketException)
                            {
                                // the web server is stopped: tell the device what to do instead of a blank error
                                await SendPage(cs, "503 Service Unavailable", I18n.T("lan.webDownTitle"), I18n.T("lan.webDownText"));
                                return;
                            }
                            server.NoDelay = true;
                            server.ReceiveTimeout = 300000;  // a slow PHP page
                            Stream ss = server.GetStream();
                            if (tls)
                            {
                                // Portly's own local certificate: nothing to verify on 127.0.0.1
                                var ssl = new SslStream(ss, false, (o, c, ch, e) => true);
                                await ssl.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12, false);
                                ss = ssl;
                            }
                            var req = Latin1.GetBytes(string.Join("\r\n", RequestLines(lines, upgrade, tls, secure, host, pub, remote)) + "\r\n\r\n");
                            // PHP errors while this request runs: into Portly's log with the device and the page
                            long phpLogBefore = upgrade ? -1 : PhpLogLength();
                            await ss.WriteAsync(req, 0, req.Length);
                            if (rest.Length > 0) await ss.WriteAsync(rest, 0, rest.Length);
                            if (moreBody > 0) { var _ = CopyBytes(cs, ss, moreBody); }

                            byte[] body;
                            var resp = ReadHead(ss, out body);
                            if (resp == null) return;
                            // http -> https of the same site: use the local HTTPS port (only a request without a body can be sent again)
                            if (!tls && attempt == 0 && Settings.Current.Ssl && moreBody == 0 && Regex.IsMatch(resp, @"^HTTP/\S+ 30[1278] ")
                                && Regex.IsMatch(resp, @"(?im)^Location:\s*https://" + Regex.Escape(host) + @"(:\d+)?(/|\s*$)"))
                            {
                                entry.Tls = tls = true;
                                continue;
                            }
                            if (upgrade && Switched(resp))
                            {
                                if (tunnel.Length > 0) await ss.WriteAsync(tunnel, 0, tunnel.Length);
                                // whichever side closes first ends the tunnel
                                await Task.WhenAny(cs.CopyToAsync(ss), Answer(cs, ss, resp, body, true, false, secure, host, pub));
                                return;
                            }
                            await Answer(cs, ss, resp, body, upgrade, method == "HEAD", secure, host, pub);
                            if (!upgrade) ReportErrors(entry, host, remote, lines[0], resp, phpLogBefore);
                            return;
                        }
                    }
                }
                catch { } // the device or the web server closed the connection
            }
        }

        /// <summary>
        /// Answered by Portly itself, never by the site (also before the password):
        /// /portly-ca = step-by-step guide for iPhone and Android, /portly-ca.crt = the Portly CA certificate
        /// (public part only, DER: what iOS and Android install most reliably).
        /// </summary>
        static async Task<bool> ServeCa(Stream cs, string head, string pub, bool https)
        {
            var m = Regex.Match(head, @"^(GET|HEAD) /portly-ca(\.crt|/)?(\?\S*)? HTTP/", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            bool headOnly = m.Groups[1].Value.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            if (m.Groups[2].Value != ".crt")
            {
                var bytes = Encoding.UTF8.GetBytes(GuidePage(pub, https));
                var g = Latin1.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bytes.Length +
                    "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await cs.WriteAsync(g, 0, g.Length);
                if (!headOnly) await cs.WriteAsync(bytes, 0, bytes.Length);
                return true;
            }
            byte[] crt;
            try { crt = Convert.FromBase64String(Regex.Replace(File.ReadAllText(Certs.CaCertPath), @"-----[^-]+-----|\s", "")); }
            catch { crt = new byte[0]; }
            var h = Latin1.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/x-x509-ca-cert\r\nContent-Disposition: attachment; filename=\"portly-ca.crt\"\r\n" +
                "Content-Length: " + crt.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await cs.WriteAsync(h, 0, h.Length);
            if (!headOnly) await cs.WriteAsync(crt, 0, crt.Length);
            return true;
        }

        /// <summary>The certificate guide for the phone: iPhone and Android tabs (picked from the browser), colored steps.</summary>
        static string GuidePage(string pub, bool https)
        {
            Func<string, string> T = k => Html(I18n.T(k));
            Func<string, string, string[], string> tab = (id, color, steps) =>
            {
                var sb = new StringBuilder("<section id=\"" + id + "\" class=\"tab\" style=\"--c:" + color + "\"><ol>");
                for (int i = 0; i < steps.Length; i++)
                {
                    bool key = steps[i].StartsWith("!");
                    var k = key ? steps[i].Substring(1) : steps[i];
                    sb.Append("<li" + (key ? " class=\"key\"" : "") + "><span class=\"n\">" + (i + 1) + "</span><div>" +
                        (key ? "<b class=\"tag\">" + T("guide.important") + "</b>" : "") + T(k) + "</div></li>");
                    if (i == 0) sb.Append("<li class=\"dl\"><a class=\"btn\" href=\"/portly-ca.crt\">⬇ " + T("guide.download") + "</a></li>");
                }
                return sb.Append("</ol></section>").ToString();
            };
            var site = (https ? "https://" : "http://") + pub + "/";
            return "<!doctype html><html lang=\"" + Html(I18n.Code) + "\"" + (I18n.IsRtl ? " dir=\"rtl\"" : "") + "><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + T("guide.title") + "</title><style>" +
                ":root{--bg:#f3f4fa;--card:#fff;--text:#1f2330;--muted:#5d6475;--line:#e3e6ef}" +
                "@media(prefers-color-scheme:dark){:root{--bg:#121420;--card:#1d2030;--text:#e9ebf3;--muted:#a3a9bb;--line:#30354a}}" +
                "*{box-sizing:border-box}body{margin:0;font-family:system-ui,-apple-system,'Segoe UI',sans-serif;background:var(--bg);color:var(--text);line-height:1.5}" +
                ".hero{background:linear-gradient(135deg,#5b6cff,#a24bff 55%,#ff5f8f);color:#fff;padding:28px 20px 56px;text-align:center}" +
                ".logo{display:inline-block;background:rgba(255,255,255,.2);border-radius:12px;padding:4px 12px;font-weight:800;letter-spacing:.5px}" +
                ".hero h1{font-size:23px;margin:14px 0 6px}.hero p{margin:0 auto;max-width:460px;opacity:.95}" +
                ".wrap{max-width:560px;margin:-36px auto 30px;padding:0 14px}" +
                ".tabs{display:flex;gap:8px;background:var(--card);padding:6px;border-radius:16px;box-shadow:0 6px 24px rgba(20,20,60,.12)}" +
                ".tabs button{flex:1;border:0;border-radius:12px;padding:12px;font-size:16px;font-weight:700;background:transparent;color:var(--muted)}" +
                ".tabs button.on[data-t=ios]{background:linear-gradient(135deg,#0a84ff,#5e5ce6);color:#fff}" +
                ".tabs button.on[data-t=android]{background:linear-gradient(135deg,#1db954,#0f9d58);color:#fff}" +
                ".tab{display:none}.tab.on{display:block}ol{list-style:none;margin:16px 0 0;padding:0}" +
                "li{display:flex;gap:14px;background:var(--card);border-radius:16px;padding:16px;margin-bottom:12px;border:1px solid var(--line)}" +
                ".n{flex:none;width:34px;height:34px;border-radius:50%;background:var(--c);color:#fff;font-weight:800;display:flex;align-items:center;justify-content:center}" +
                "li.key{border:2px solid #ff9f0a;background:linear-gradient(0deg,rgba(255,159,10,.08),rgba(255,159,10,.08)),var(--card)}" +
                ".tag{display:inline-block;background:#ff9f0a;color:#fff;border-radius:8px;padding:0 8px;margin-inline-end:6px;font-size:13px}" +
                "li.dl{background:none;border:0;padding:0 0 4px}" +
                ".btn{display:block;width:100%;text-align:center;text-decoration:none;color:#fff;font-weight:800;font-size:17px;padding:15px;border-radius:14px;background:var(--c);box-shadow:0 6px 18px rgba(0,0,0,.18)}" +
                ".test{background:var(--card);border-radius:16px;padding:16px;border:1px solid var(--line);margin-top:6px}" +
                ".test .btn{--c:linear-gradient(135deg,#5b6cff,#a24bff);margin-top:10px}.note{color:var(--muted);font-size:14px;margin:16px 4px}" +
                "</style></head><body><div class=\"hero\"><span class=\"logo\">Portly</span><h1>🔒 " + T("guide.title") + "</h1><p>" + T("guide.intro") + "</p></div>" +
                "<div class=\"wrap\"><div class=\"tabs\"><button data-t=\"ios\">📱 " + T("guide.iphone") + "</button><button data-t=\"android\">🤖 " + T("guide.android") + "</button></div>" +
                tab("ios", "#0a84ff", new[] { "guide.ios1", "guide.ios2", "guide.ios3", "guide.ios4", "!guide.ios5" }) +
                tab("android", "#1db954", new[] { "guide.android1", "guide.android2", "guide.android3", "guide.android4", "!guide.android5" }) +
                "<div class=\"test\">✅ " + T("guide.test") + (https ? "<a class=\"btn\" href=\"" + Html(site) + "\">" + T("guide.open") + "</a>" : "") + "</div>" +
                "<p class=\"note\">ℹ️ " + T("guide.note") + "</p></div><script>" +
                "var b=document.querySelectorAll('.tabs button');function show(t){b.forEach(function(x){x.classList.toggle('on',x.dataset.t==t)});" +
                "document.querySelectorAll('.tab').forEach(function(x){x.classList.toggle('on',x.id==t)})}" +
                "b.forEach(function(x){x.onclick=function(){show(x.dataset.t)}});show(/android/i.test(navigator.userAgent)?'android':'ios');" +
                "</script></body></html>";
        }

        static string HeaderValue(IEnumerable<string> lines, string name)
        {
            var l = lines.Skip(1).FirstOrDefault(x => Is(Name(x), name));
            return l == null ? null : l.Substring(l.IndexOf(':') + 1).Trim(' ', '\t');
        }

        static void Seen(Entry e, string host, string ip, string userAgent, string country)
        {
            bool added = false;
            string label;
            lock (e.Devices)
            {
                ShareDevice d;
                if (!e.Devices.TryGetValue(ip, out d))
                {
                    if (e.Devices.Count >= 200) return; // an internet share seen by many: enough for the list
                    e.Devices[ip] = d = new ShareDevice { Ip = ip, First = DateTime.Now };
                    added = true;
                }
                d.Last = DateTime.Now;
                d.Requests++;
                if (!string.IsNullOrEmpty(userAgent)) d.Name = ShareDevice.Describe(userAgent);
                if (!string.IsNullOrEmpty(country) && country != "XX") d.Country = country;
                label = d.Label;
            }
            if (added)
            {
                Log.Info(I18n.T("shares.deviceJoined", host, label));
                ShareStore.Notify();
            }
        }

        static string PhpLogFile { get { return Path.Combine(Paths.Logs, "php_errors.log"); } }

        static long PhpLogLength()
        {
            try { var f = new FileInfo(PhpLogFile); return f.Exists ? f.Length : 0; }
            catch { return -1; }
        }

        /// <summary>
        /// A request from a shared device ended with a server error or wrote to the PHP error log: put it into Portly's log
        /// with the device and the page, so errors seen on the phone show up on the computer. (Requests running at the same
        /// time share the PHP log, so a line may belong to another page of the same moment.)
        /// </summary>
        static void ReportErrors(Entry e, string host, string ip, string requestLine, string resp, long phpLogBefore)
        {
            try
            {
                int status = 0;
                var sm = Regex.Match(resp, @"^HTTP/\S+ (\d{3})");
                if (sm.Success) status = int.Parse(sm.Groups[1].Value);
                var parts = requestLine.Split(' ');
                var page = parts.Length > 1 ? parts[0] + " " + parts[1] : requestLine;
                if (page.Length > 160) page = page.Substring(0, 160) + "...";
                var messages = new List<string>();
                if (phpLogBefore >= 0)
                {
                    var f = new FileInfo(PhpLogFile);
                    if (f.Exists && f.Length > phpLogBefore)
                    {
                        using (var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            s.Position = Math.Max(phpLogBefore, f.Length - 8192);
                            var text = new StreamReader(s, Encoding.UTF8).ReadToEnd();
                            // "[26-Oct-2026 20:31:05 Europe/Istanbul] PHP Warning:  Undefined variable $x in ... on line 3"
                            messages.AddRange(text.Split('\n').Select(l => Regex.Replace(l.Trim(), @"^\[[^\]]+\]\s*", "")).Where(l => l.StartsWith("PHP ")).Take(5));
                        }
                    }
                }
                if (messages.Count == 0 && status < 500) return;
                string label;
                lock (e.Devices) { ShareDevice d; label = e.Devices.TryGetValue(ip, out d) ? d.Label : ip; if (d != null) d.Errors += Math.Max(1, messages.Count); }
                if (messages.Count == 0) messages.Add("HTTP " + status);
                foreach (var m in messages) Log.Warn(I18n.T("shares.phpError", host, label, page, m));
                ShareStore.Notify();
            }
            catch { }
        }

        static string CookieName(Entry e) { return "portly_share_" + e.Port; }

        /// <summary>
        /// Password check for a protected share. Returns the request head to pass on (without Portly's own cookie),
        /// or null when Portly already answered (login page, wrong password, redirect after the right one).
        /// </summary>
        static async Task<string> CheckPassword(Stream cs, string head, byte[] rest, Entry entry, string host, bool secure)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None).ToList();
            var parts = lines[0].Split(' ');
            var path = parts.Length > 1 ? parts[1] : "/";
            var cookie = CookieName(entry) + "=" + entry.Token;
            if (parts[0].Equals("POST", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/__portly/login"))
            {
                // the form: password=...&next=...
                long len = 0;
                var cl = lines.FirstOrDefault(l => Is(Name(l), "Content-Length"));
                if (cl != null) long.TryParse(cl.Substring(cl.IndexOf(':') + 1).Trim(), out len);
                var body = new MemoryStream();
                body.Write(rest, 0, rest.Length);
                var buf = new byte[1024];
                while (body.Length < Math.Min(len, 4096))
                {
                    int n = cs.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    body.Write(buf, 0, n);
                }
                var form = ParseForm(Encoding.UTF8.GetString(body.ToArray()));
                string next, given;
                if (!form.TryGetValue("next", out next)) next = "/";
                form.TryGetValue("password", out given);
                if (!next.StartsWith("/") || next.StartsWith("//")) next = "/";
                if (given == entry.Password)
                {
                    var h = Latin1.GetBytes("HTTP/1.1 303 See Other\r\nLocation: " + next + "\r\nSet-Cookie: " + cookie + "; Path=/; HttpOnly; SameSite=Lax" + (secure ? "; Secure" : "") +
                        "\r\nContent-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    await cs.WriteAsync(h, 0, h.Length);
                    return null;
                }
                await Task.Delay(800); // slows down guessing
                await SendLogin(cs, host, next, true);
                return null;
            }
            var cookieLine = lines.FirstOrDefault(l => Is(Name(l), "Cookie"));
            if (cookieLine == null || !Regex.IsMatch(cookieLine, @"(^|[;:\s])" + Regex.Escape(cookie) + @"(;|\s|$)"))
            {
                await SendLogin(cs, host, path, false);
                return null;
            }
            // the site never sees Portly's cookie
            var others = Regex.Replace(cookieLine.Substring(cookieLine.IndexOf(':') + 1), @"\s*" + Regex.Escape(cookie) + @";?", "").Trim().TrimEnd(';');
            var i = lines.IndexOf(cookieLine);
            if (others == "") lines.RemoveAt(i); else lines[i] = "Cookie: " + others;
            return string.Join("\r\n", lines);
        }

        static Task SendLogin(Stream cs, string host, string next, bool wrong)
        {
            var form = "<form method=\"post\" action=\"/__portly/login\"><input type=\"hidden\" name=\"next\" value=\"" + Html(next) + "\">" +
                "<input type=\"password\" name=\"password\" placeholder=\"" + Html(I18n.T("lan.loginPassword")) + "\" autofocus required>" +
                (wrong ? "<p class=\"err\">" + Html(I18n.T("lan.loginWrong")) + "</p>" : "") +
                "<button type=\"submit\">" + Html(I18n.T("lan.loginButton")) + "</button></form>";
            return SendPage(cs, wrong ? "401 Unauthorized" : "200 OK", I18n.T("lan.loginTitle", host), null, form);
        }

        /// <summary>Small page answered by Portly itself (login, web server stopped), readable on a phone.</summary>
        static async Task SendPage(Stream cs, string status, string title, string text, string formHtml = null)
        {
            var html = "<!doctype html><html lang=\"" + Html(I18n.Code) + "\"" + (I18n.IsRtl ? " dir=\"rtl\"" : "") + "><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + Html(title) + "</title><style>" +
                "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;font-family:system-ui,-apple-system,Segoe UI,sans-serif;background:#f3f4f8;color:#1f2330}" +
                "@media(prefers-color-scheme:dark){body{background:#15171e;color:#e7e9f0}.c{background:#1f2230!important}input{background:#15171e;color:#e7e9f0;border-color:#3a3f52!important}}" +
                ".c{background:#fff;border-radius:16px;padding:28px 24px;max-width:360px;width:calc(100% - 32px);box-shadow:0 8px 30px rgba(0,0,0,.08)}" +
                ".b{display:inline-block;background:#4f6bff;color:#fff;border-radius:10px;padding:4px 10px;font-weight:700;font-size:13px}" +
                "h1{font-size:19px;margin:14px 0 8px}p{margin:0 0 12px;line-height:1.5;opacity:.85}" +
                "input{box-sizing:border-box;width:100%;font-size:17px;padding:12px;border:1px solid #d7dbe7;border-radius:10px;margin:8px 0}" +
                "button{width:100%;font-size:17px;padding:12px;border:0;border-radius:10px;background:#4f6bff;color:#fff;font-weight:600}" +
                ".err{color:#e5484d;font-size:14px}</style></head><body><div class=\"c\"><span class=\"b\">Portly</span><h1>" + Html(title) + "</h1>" +
                (text != null ? "<p>" + Html(text) + "</p>" : "") + (formHtml ?? "") + "</div></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            var h = Latin1.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bytes.Length +
                "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await cs.WriteAsync(h, 0, h.Length);
            await cs.WriteAsync(bytes, 0, bytes.Length);
        }

        static Dictionary<string, string> ParseForm(string body)
        {
            var d = new Dictionary<string, string>();
            foreach (var pair in body.Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2) d[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
            }
            return d;
        }

        static string Html(string s) { return System.Net.WebUtility.HtmlEncode(s ?? ""); }

        static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        static string Name(string headerLine) { var i = headerLine.IndexOf(':'); return i < 0 ? "" : headerLine.Substring(0, i).Trim(); }

        static readonly Regex RequestLine = new Regex(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+ (/[^\x00-\x20\x7f]*|\*) HTTP/1\.[01]\z");
        static readonly Regex HeaderLine = new Regex(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+:[\t\x20-\x7e\x80-\xff]*\z");

        /// <summary>
        /// Only a head that this proxy and the web server cannot read differently goes on. Refused: bare CR/LF and other control
        /// characters, folded lines, spaces around a header name, a repeated Host / Content-Length / Transfer-Encoding / Upgrade,
        /// a body framed two ways, and an address in the request line ("GET http://localhost/ ..." would override the Host).
        /// </summary>
        static bool WellFormed(string head)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (!RequestLine.IsMatch(lines[0]) || !lines.Skip(1).All(l => HeaderLine.IsMatch(l))) return false;
            foreach (var single in new[] { "Host", "Content-Length", "Transfer-Encoding", "Upgrade" })
                if (lines.Skip(1).Count(l => Is(Name(l), single)) > 1) return false;
            var length = HeaderValue(lines, "Content-Length");
            var encoding = HeaderValue(lines, "Transfer-Encoding");
            if (length != null && encoding != null) return false;
            if (encoding != null && !Is(encoding, "chunked")) return false;
            long parsed;
            return length == null || Regex.IsMatch(length, @"^[0-9]+\z") && long.TryParse(length, out parsed);
        }

        /// <summary>
        /// A real WebSocket handshake. Any other upgrade stays a plain request: "h2c" would turn the tunnel into HTTP/2 requests
        /// of the device's own making, with the same effect as a request smuggled past the checks here.
        /// </summary>
        static bool AsksUpgrade(List<string> lines)
        {
            return lines[0].StartsWith("GET ", StringComparison.Ordinal) && lines[0].EndsWith(" HTTP/1.1", StringComparison.Ordinal)
                && Is(HeaderValue(lines, "Upgrade"), "websocket")
                && lines.Skip(1).Where(l => Is(Name(l), "Connection"))
                    .Any(l => l.Substring(l.IndexOf(':') + 1).Split(',').Any(t => Is(t.Trim(' ', '\t'), "upgrade")));
        }

        static bool Switched(string resp) { return Regex.IsMatch(resp, @"^HTTP/1\.1 101(\s|$)"); }

        /// <summary>Answer of Portly itself to a request that does not go on.</summary>
        static async Task Refuse(Stream cs, string status)
        {
            var h = Latin1.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await cs.WriteAsync(h, 0, h.Length);
        }

        static List<string> RequestLines(List<string> lines, bool upgrade, bool tls, bool secure, string host, string pub, string remote)
        {
            var site = (tls ? "https://" : "http://") + host;
            var outLines = new List<string> { upgrade ? lines[0] : Regex.Replace(lines[0], @"HTTP/1\.\d$", "HTTP/1.0") };
            foreach (var l in lines.Skip(1))
            {
                if (l == "") continue;
                var name = Name(l);
                // plain answers (no Accept-Encoding): the site's address in pages can be rewritten
                // (PHP reads "_" in a header name as "-")
                if (Is(name, "Host") || name.Replace('_', '-').StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                    || !upgrade && (Is(name, "Connection") || Is(name, "Keep-Alive") || Is(name, "Accept-Encoding") || Is(name, "Upgrade"))
                    || upgrade && Is(name, "Expect"))
                    continue;
                // for the site, a form sent from the device comes from its own address (CSRF / referer checks)
                if (Is(name, "Origin") || Is(name, "Referer"))
                {
                    outLines.Add(Regex.Replace(l, @"https?://" + Regex.Escape(pub), site, RegexOptions.IgnoreCase));
                    continue;
                }
                outLines.Add(l);
            }
            outLines.Insert(1, "Host: " + host);
            outLines.Add("X-Forwarded-Host: " + pub);
            outLines.Add("X-Forwarded-Proto: " + (secure ? "https" : "http"));
            outLines.Add("X-Forwarded-For: " + remote);
            if (!upgrade) outLines.Add("Connection: close");
            return outLines;
        }

        static readonly Regex TextType = new Regex(@"(?im)^Content-Type:\s*(text/|application/(javascript|x-javascript|json|ld\+json|manifest\+json|xml|xhtml\+xml|rss\+xml|atom\+xml))");

        static async Task Answer(Stream cs, Stream ss, string resp, byte[] body, bool upgrade, bool headOnly, bool secure, string host, string pub)
        {
            var scheme = secure ? "https:" : "http:";
            var mine = scheme + "//" + pub;
            var siteRx = @"https?://" + Regex.Escape(host) + @"(:\d+)?";
            var outLines = new List<string>();
            foreach (var l in resp.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                var name = Name(l);
                if (Is(name, "Strict-Transport-Security")) continue; // the share is plain http
                if (Is(name, "Location") || Is(name, "Content-Location") || Is(name, "Refresh") || Is(name, "Link"))
                    outLines.Add(Regex.Replace(l, siteRx, mine, RegexOptions.IgnoreCase));
                else if (Is(name, "Set-Cookie"))
                    // a cookie for "shop.test", or one that needs https, would be dropped by the device
                    outLines.Add(Regex.Replace(l, secure ? @";\s*Domain=[^;]*(?=;|$)" : @";\s*(Domain=[^;]*|Secure|SameSite=None)(?=;|$)", "", RegexOptions.IgnoreCase));
                else outLines.Add(l);
            }

            long declared = -1;
            var cl = outLines.FirstOrDefault(l => Is(Name(l), "Content-Length"));
            if (cl != null) long.TryParse(cl.Substring(cl.IndexOf(':') + 1).Trim(), out declared);
            bool rewrite = !upgrade && !headOnly && TextType.IsMatch(resp) && declared < 20 * 1024 * 1024
                && !Regex.IsMatch(resp, @"(?im)^(Content-Encoding:\s*(?!identity)\S|Transfer-Encoding:)");
            if (!rewrite)
            {
                // an upgrade the web server refused: its connection stays open (HTTP/1.1), this answer is the last one
                bool refused = upgrade && !Switched(resp);
                if (refused)
                {
                    outLines.RemoveAll(l => Is(Name(l), "Connection") || Is(Name(l), "Keep-Alive"));
                    outLines.Add("Connection: close");
                }
                var h = Latin1.GetBytes(string.Join("\r\n", outLines) + "\r\n\r\n");
                await cs.WriteAsync(h, 0, h.Length);
                if (refused) { CopyAnswer(ss, body, resp, cs); return; }
                if (body.Length > 0) await cs.WriteAsync(body, 0, body.Length);
                await ss.CopyToAsync(cs);
                return;
            }
            // pages, CSS, JS, JSON: links to the site's address point to the shared one
            var all = new MemoryStream();
            all.Write(body, 0, body.Length);
            await ss.CopyToAsync(all);
            var bytes = all.ToArray();
            foreach (var pair in new[]
            {
                Tuple.Create("https://" + host, mine), Tuple.Create("http://" + host, mine),
                Tuple.Create("https:\\/\\/" + host, scheme + "\\/\\/" + pub), Tuple.Create("http:\\/\\/" + host, scheme + "\\/\\/" + pub),
                Tuple.Create("//" + host, "//" + pub),
            })
                bytes = Replace(bytes, Latin1.GetBytes(pair.Item1), Latin1.GetBytes(pair.Item2));
            outLines.RemoveAll(l => Is(Name(l), "Content-Length"));
            outLines.Add("Content-Length: " + bytes.Length);
            var head = Latin1.GetBytes(string.Join("\r\n", outLines) + "\r\n\r\n");
            await cs.WriteAsync(head, 0, head.Length);
            await cs.WriteAsync(bytes, 0, bytes.Length);
        }

        static byte[] Replace(byte[] data, byte[] find, byte[] with)
        {
            int i = IndexOf(data, find, 0);
            if (i < 0) return data;
            var o = new MemoryStream(data.Length + 256);
            int from = 0;
            while (i >= 0)
            {
                o.Write(data, from, i - from);
                o.Write(with, 0, with.Length);
                from = i + find.Length;
                i = IndexOf(data, find, from);
            }
            o.Write(data, from, data.Length - from);
            return o.ToArray();
        }

        /// <summary>Exactly <paramref name="count"/> bytes, the rest of a request body; less when the device stops sending.</summary>
        static async Task CopyBytes(Stream from, Stream to, long count)
        {
            var buf = new byte[81920];
            while (count > 0)
            {
                int n = await from.ReadAsync(buf, 0, (int)Math.Min(buf.Length, count));
                if (n <= 0) return;
                await to.WriteAsync(buf, 0, n);
                count -= n;
            }
        }

        /// <summary>
        /// The rest of an answer on a connection that stays open: up to where the answer says it ends, not to the end of the
        /// connection. Blocking reads, so the socket's receive timeout counts.
        /// </summary>
        static void CopyAnswer(Stream ss, byte[] body, string resp, Stream cs)
        {
            var lines = resp.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var status = Regex.Match(lines[0], @"^HTTP/\S+ (\d{3})").Groups[1].Value;
            if (status.StartsWith("1", StringComparison.Ordinal) || status == "204" || status == "304") return;
            var from = new Source(ss, body);
            var encoding = HeaderValue(lines, "Transfer-Encoding");
            long length;
            if (encoding != null && encoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) CopyChunked(from, cs, null, long.MaxValue);
            else if (long.TryParse(HeaderValue(lines, "Content-Length"), out length)) from.Copy(length, cs, null);
            else from.CopyToEnd(cs);
        }

        /// <summary>Reads a chunked request body from the device into <paramref name="rest"/>. Returns the status to refuse it with, or null.</summary>
        static string ReadChunked(Stream cs, ref byte[] rest)
        {
            var decoded = new MemoryStream();
            try { CopyChunked(new Source(cs, rest), null, decoded, MaxChunkedBody); }
            catch (InvalidDataException) { return "400 Bad Request"; }
            catch (OverflowException) { return "413 Payload Too Large"; }
            rest = decoded.ToArray();
            return null;
        }

        /// <summary>One chunked body and nothing after it: <paramref name="raw"/> gets it as it is, <paramref name="data"/> without the framing.</summary>
        static void CopyChunked(Source from, Stream raw, Stream data, long limit)
        {
            Func<string> line = () =>
            {
                var l = from.Line();
                if (raw != null) { var b = Latin1.GetBytes(l + "\r\n"); raw.Write(b, 0, b.Length); }
                return l;
            };
            while (true)
            {
                var m = Regex.Match(line(), @"^([0-9A-Fa-f]{1,8})(;[\t\x20-\x7e\x80-\xff]*)?\z");
                if (!m.Success) throw new InvalidDataException();
                long size = Convert.ToInt64(m.Groups[1].Value, 16);
                if (size == 0) break;
                if (size > limit) throw new OverflowException();
                limit -= size;
                from.Copy(size, raw, data);
                if (line() != "") throw new InvalidDataException();
            }
            while (line() != "") { } // trailers
        }

        /// <summary>Blocking reads from a stream, the bytes already taken from it first.</summary>
        class Source
        {
            readonly Stream _s;
            byte[] _buf;
            int _pos, _end;

            public Source(Stream s, byte[] first) { _s = s; _buf = first; _end = first.Length; }

            bool Fill()
            {
                if (_pos < _end) return true;
                if (_buf.Length < 8192) _buf = new byte[8192];
                _pos = 0;
                _end = Math.Max(0, _s.Read(_buf, 0, _buf.Length));
                return _end > 0;
            }

            /// <summary>A line ending in CRLF, without it.</summary>
            public string Line()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    if (!Fill()) throw new EndOfStreamException();
                    var c = (char)_buf[_pos++];
                    if (c == '\n')
                    {
                        if (sb.Length == 0 || sb[sb.Length - 1] != '\r') throw new InvalidDataException();
                        return sb.ToString(0, sb.Length - 1);
                    }
                    if (sb.Length >= 8192) throw new InvalidDataException();
                    sb.Append(c);
                }
            }

            public void Copy(long count, Stream raw, Stream data)
            {
                while (count > 0)
                {
                    if (!Fill()) throw new EndOfStreamException();
                    int n = (int)Math.Min(_end - _pos, count);
                    if (raw != null) raw.Write(_buf, _pos, n);
                    if (data != null) data.Write(_buf, _pos, n);
                    _pos += n;
                    count -= n;
                }
            }

            public void CopyToEnd(Stream to)
            {
                while (Fill())
                {
                    to.Write(_buf, _pos, _end - _pos);
                    _pos = _end;
                }
            }
        }

        /// <summary>Reads up to the empty line after the headers (max 64 KB); what came after it goes to <paramref name="rest"/>.</summary>
        static string ReadHead(Stream s, out byte[] rest)
        {
            var buf = new MemoryStream();
            var one = new byte[8192];
            rest = new byte[0];
            while (buf.Length < 64 * 1024)
            {
                int n;
                try { n = s.Read(one, 0, one.Length); } catch { return null; }
                if (n <= 0) return null;
                buf.Write(one, 0, n);
                var all = buf.ToArray();
                int end = IndexOf(all, new byte[] { 13, 10, 13, 10 });
                if (end >= 0)
                {
                    rest = all.Skip(end + 4).ToArray();
                    return Latin1.GetString(all, 0, end);
                }
            }
            return null;
        }

        static int IndexOf(byte[] a, byte[] p, int start = 0)
        {
            for (int i = start; i + p.Length <= a.Length; i++)
            {
                int j = 0;
                while (j < p.Length && a[i + j] == p[j]) j++;
                if (j == p.Length) return i;
            }
            return -1;
        }
    }
}

namespace Portly.Tools
{
    /// <summary>A device (or internet visitor) that opened a shared project.</summary>
    public class ShareDevice
    {
        public string Ip = "";
        public string Name = "";      // "iPhone · Safari", "Android (Pixel 9) · Chrome"
        public string Country = "";   // internet shares: two-letter code from Cloudflare
        public DateTime First, Last;
        public int Requests, Errors;

        public string Label { get { return (Name != "" ? Name + " - " : "") + Ip + (Country != "" ? " (" + Country + ")" : ""); } }

        public ShareDevice Copy() { return (ShareDevice)MemberwiseClone(); }

        /// <summary>Phone / computer and browser from the User-Agent, in a few words.</summary>
        public static string Describe(string ua)
        {
            string os =
                ua.Contains("iPhone") ? "iPhone" :
                ua.Contains("iPad") || ua.Contains("Macintosh") && ua.Contains("Mobile") ? "iPad" :
                ua.Contains("Android") ? "Android" :
                ua.Contains("Windows") ? "Windows" :
                ua.Contains("Mac OS X") ? "Mac" :
                ua.Contains("CrOS") ? "Chromebook" :
                ua.Contains("Linux") ? "Linux" : "";
            if (os == "Android")
            {
                // "Android 15; Pixel 9 Build/..." -> the model, when the browser sends it
                var m = System.Text.RegularExpressions.Regex.Match(ua, @"Android [\d.]+; ([^;)]+?)(?: Build/[^;)]*)?[;)]");
                if (m.Success && m.Groups[1].Value != "K") os += " (" + m.Groups[1].Value.Trim() + ")";
            }
            string browser =
                ua.Contains("SamsungBrowser") ? "Samsung Internet" :
                ua.Contains("Edg/") || ua.Contains("EdgA/") || ua.Contains("EdgiOS/") ? "Edge" :
                ua.Contains("OPR/") ? "Opera" :
                ua.Contains("Firefox/") || ua.Contains("FxiOS/") ? "Firefox" :
                ua.Contains("CriOS/") || ua.Contains("Chrome/") ? "Chrome" :
                ua.Contains("Safari/") ? "Safari" :
                ua.Contains("curl/") ? "curl" : "";
            return os != "" && browser != "" ? os + " · " + browser : os + browser;
        }
    }
}
