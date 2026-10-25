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

        class Entry { public TcpListener Listener; public string Url; public string Public; public volatile bool Tls; public X509Certificate2 Cert; }
        static readonly Dictionary<string, Entry> Shares = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static string UrlOf(Site site) { return UrlOf(site.Host); }

        public static string UrlOf(string host)
        {
            lock (Shares) { Entry e; return Shares.TryGetValue(host, out e) ? e.Url : null; }
        }

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
            var pub = ip + ":" + port;
            // a site with HTTPS is shared with HTTPS too (certificate for this address, signed by the Portly CA)
            X509Certificate2 cert = null;
            if (Settings.Current.Ssl)
            {
                try { cert = Certs.LanCertificate(ip); }
                catch (Exception ex) { Log.Warn("LAN certificate: " + ex.Message); }
            }
            var entry = new Entry { Listener = listener, Public = pub, Cert = cert, Url = (cert != null ? "https://" : "http://") + pub + "/" };
            lock (Shares) Shares[site.Host] = entry;
            Task.Run(() => AcceptLoop(entry, site.Host));
            Log.Info(I18n.T("lan.started", site.Host, entry.Url));
            ShareStore.Notify();
            return entry.Url;
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
            lock (Shares) if (Shares.Any(x => x.Key != forHost && ((IPEndPoint)x.Value.Listener.LocalEndpoint).Port == port)) return false;
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
                    // the same port answers https and http: a TLS handshake starts with 0x16
                    var first = new byte[1];
                    if (client.Client.Receive(first, 1, SocketFlags.Peek) < 1) return;
                    bool secure = first[0] == 0x16 && entry.Cert != null;
                    if (secure)
                    {
                        var ssl = new SslStream(cs, false);
                        await ssl.AuthenticateAsServerAsync(entry.Cert, false, SslProtocols.Tls12, false);
                        cs = ssl;
                    }
                    byte[] rest;
                    var head = ReadHead(cs, out rest);
                    if (head == null) return;
                    if (await ServeCa(cs, head)) return;
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
                            await server.ConnectAsync(IPAddress.Loopback, tls ? Settings.Current.HttpsPort : Settings.Current.HttpPort);
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
                            var req = Latin1.GetBytes(string.Join("\r\n", RequestLines(lines, upgrade, tls, secure, host, entry.Public, remote)) + "\r\n\r\n");
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
                            await Answer(cs, ss, resp, body, upgrade, method == "HEAD", secure, host, entry.Public);
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
