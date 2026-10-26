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
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591); // header bytes kept as they are

        class Entry
        {
            public TcpListener Listener;
            public int Port;
            public bool Https;           // the site has HTTPS: shared with https (certificate made for the address in use)
            public volatile bool Tls;    // the site sends http to https: reach it on the local HTTPS port
            public string Password = ""; // "" = no password
            public string Token;         // cookie value after the password was given (new at every start)
        }
        static readonly Dictionary<string, Entry> Shares = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

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
            Log.Info(I18n.T("lan.stopped", host));
            ShareStore.Notify();
        }

        /// <summary>New password for a running share; devices that logged in before must log in again.</summary>
        public static void SetPassword(string host, string password)
        {
            lock (Shares)
            {
                Entry e;
                if (!Shares.TryGetValue(host, out e) || e.Password == (password ?? "")) return;
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
            lock (Shares) { list = Shares.Values.ToList(); Shares.Clear(); }
            foreach (var e in list) try { e.Listener.Stop(); } catch { }
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
        /// WebSocket upgrades (Vite) are passed through as they are.
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
                    // the address the device used (follows a new IP from the router, and the right adapter)
                    var localIp = ((IPEndPoint)client.Client.LocalEndPoint).Address;
                    if (localIp.IsIPv4MappedToIPv6) localIp = localIp.MapToIPv4();
                    var pub = localIp + ":" + entry.Port;
                    // the same port answers https and http: a TLS handshake starts with 0x16
                    var first = new byte[1];
                    if (client.Client.Receive(first, 1, SocketFlags.Peek) < 1) return;
                    bool secure = first[0] == 0x16 && entry.Https;
                    if (secure)
                    {
                        var ssl = new SslStream(cs, false);
                        await ssl.AuthenticateAsServerAsync(Certs.LanCertificate(localIp), false, SslProtocols.Tls12, false);
                        cs = ssl;
                    }
                    byte[] rest;
                    var head = ReadHead(cs, out rest);
                    if (head == null) return;
                    if (await ServeCa(cs, head)) return;
                    // a share with a password: a small login page first, then a cookie
                    if (entry.Password != "")
                    {
                        var allowed = await CheckPassword(cs, head, rest, entry, host, secure);
                        if (allowed == null) return;
                        head = allowed;
                    }
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None).ToList();
                    bool upgrade = lines.Any(l => l.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase));
                    var method = lines[0].Split(' ')[0].ToUpperInvariant();
                    var remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    long length = 0;
                    var cl = lines.FirstOrDefault(l => Is(Name(l), "Content-Length"));
                    if (cl != null) long.TryParse(cl.Substring(cl.IndexOf(':') + 1).Trim(), out length);
                    bool moreBody = upgrade || length > rest.Length || lines.Any(l => Is(Name(l), "Transfer-Encoding"));

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
                            await ss.WriteAsync(req, 0, req.Length);
                            if (rest.Length > 0) await ss.WriteAsync(rest, 0, rest.Length);
                            var up = moreBody ? cs.CopyToAsync(ss) : null;

                            byte[] body;
                            var resp = ReadHead(ss, out body);
                            if (resp == null) return;
                            // http -> https of the same site: use the local HTTPS port (only a request without a body can be sent again)
                            if (!tls && attempt == 0 && Settings.Current.Ssl && !moreBody && Regex.IsMatch(resp, @"^HTTP/\S+ 30[1278] ")
                                && Regex.IsMatch(resp, @"(?im)^Location:\s*https://" + Regex.Escape(host) + @"(:\d+)?(/|\s*$)"))
                            {
                                entry.Tls = tls = true;
                                continue;
                            }
                            await Answer(cs, ss, resp, body, upgrade, method == "HEAD", secure, host, pub);
                            if (up != null && upgrade) await up;
                            return;
                        }
                    }
                }
                catch { } // the device or the web server closed the connection
            }
        }

        /// <summary>
        /// /portly-ca.crt: the Portly CA certificate (public part only) to install on the phone, so the https address
        /// opens without a warning. Answered by Portly itself, never by the site.
        /// </summary>
        static async Task<bool> ServeCa(Stream cs, string head)
        {
            if (!Regex.IsMatch(head, @"^(GET|HEAD) /portly-ca\.crt(\?\S*)? HTTP/", RegexOptions.IgnoreCase)) return false;
            var crt = File.Exists(Certs.CaCertPath) ? File.ReadAllBytes(Certs.CaCertPath) : new byte[0];
            var h = Latin1.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/x-x509-ca-cert\r\nContent-Disposition: attachment; filename=\"portly-ca.crt\"\r\n" +
                "Content-Length: " + crt.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await cs.WriteAsync(h, 0, h.Length);
            if (!head.StartsWith("HEAD", StringComparison.OrdinalIgnoreCase)) await cs.WriteAsync(crt, 0, crt.Length);
            return true;
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

        static List<string> RequestLines(List<string> lines, bool upgrade, bool tls, bool secure, string host, string pub, string remote)
        {
            var site = (tls ? "https://" : "http://") + host;
            var outLines = new List<string> { upgrade ? lines[0] : Regex.Replace(lines[0], @"HTTP/1\.\d$", "HTTP/1.0") };
            foreach (var l in lines.Skip(1))
            {
                if (l == "") continue;
                var name = Name(l);
                // plain answers (no Accept-Encoding): the site's address in pages can be rewritten
                if (Is(name, "Host") || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                    || !upgrade && (Is(name, "Connection") || Is(name, "Keep-Alive") || Is(name, "Accept-Encoding")))
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
                var h = Latin1.GetBytes(string.Join("\r\n", outLines) + "\r\n\r\n");
                await cs.WriteAsync(h, 0, h.Length);
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
