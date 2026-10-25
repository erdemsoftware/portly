using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
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

        class Entry { public TcpListener Listener; public string Url; public string Public; }
        static readonly Dictionary<string, Entry> Shares = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static string UrlOf(Site site)
        {
            lock (Shares) { Entry e; return Shares.TryGetValue(site.Host, out e) ? e.Url : null; }
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
            int port = FirstPort;
            for (; port < FirstPort + 100; port++)
            {
                lock (Shares) if (Shares.Values.Any(x => ((IPEndPoint)x.Listener.LocalEndpoint).Port == port)) continue;
                try { listener = new TcpListener(IPAddress.Any, port); listener.Start(); break; }
                catch (SocketException) { listener = null; }
            }
            if (listener == null) throw new Exception(I18n.T("lan.noPort"));
            var pub = ip + ":" + port;
            var entry = new Entry { Listener = listener, Public = pub, Url = "http://" + pub + "/" };
            lock (Shares) Shares[site.Host] = entry;
            Task.Run(() => AcceptLoop(entry, site.Host));
            Log.Info(I18n.T("lan.started", site.Host, entry.Url));
            return entry.Url;
        }

        public static void Stop(Site site)
        {
            Entry e;
            lock (Shares)
            {
                if (!Shares.TryGetValue(site.Host, out e)) return;
                Shares.Remove(site.Host);
            }
            try { e.Listener.Stop(); } catch { }
            Log.Info(I18n.T("lan.stopped", site.Host));
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
        /// One connection carries one request ("Connection: close"), so each request head can be rewritten:
        /// Host = the site, X-Forwarded-Host = the address the device used. Redirects to the site's local name are
        /// pointed back to the shared address. WebSocket upgrades (Vite) are passed through as they are.
        /// </summary>
        static async Task Serve(TcpClient client, Entry entry, string host)
        {
            using (client)
            using (var server = new TcpClient())
            {
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 30000;   // a device that connects and sends nothing
                    var cs = client.GetStream();
                    byte[] rest;
                    var head = ReadHead(cs, out rest);
                    if (head == null) return;
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None).ToList();
                    bool upgrade = lines.Any(l => l.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase));
                    var remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    var outLines = new List<string> { lines[0] };
                    foreach (var l in lines.Skip(1))
                    {
                        if (l == "") continue;
                        var name = l.Split(':')[0].Trim();
                        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                            || !upgrade && (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        outLines.Add(l);
                    }
                    outLines.Insert(1, "Host: " + host);
                    outLines.Add("X-Forwarded-Host: " + entry.Public);
                    outLines.Add("X-Forwarded-Proto: http");
                    outLines.Add("X-Forwarded-For: " + remote);
                    if (!upgrade) outLines.Add("Connection: close");

                    await server.ConnectAsync(IPAddress.Loopback, Settings.Current.HttpPort);
                    server.NoDelay = true;
                    server.ReceiveTimeout = 300000;  // a slow PHP page
                    var ss = server.GetStream();
                    var reqHead = Latin1.GetBytes(string.Join("\r\n", outLines) + "\r\n\r\n");
                    await ss.WriteAsync(reqHead, 0, reqHead.Length);
                    if (rest.Length > 0) await ss.WriteAsync(rest, 0, rest.Length);
                    var up = cs.CopyToAsync(ss).ContinueWith(t => { try { server.Client.Shutdown(SocketShutdown.Send); } catch { } });

                    // response head: send redirects to the site's local address back to the shared one
                    byte[] body;
                    var resp = ReadHead(ss, out body);
                    if (resp == null) return;
                    resp = Regex.Replace(resp, @"(?im)^(Location|Content-Location|Refresh):(.*)$", m =>
                        m.Groups[1].Value + ":" + Regex.Replace(m.Groups[2].Value, @"https?://" + Regex.Escape(host) + @"(:\d+)?", "http://" + entry.Public, RegexOptions.IgnoreCase));
                    var respHead = Latin1.GetBytes(resp + "\r\n\r\n");
                    await cs.WriteAsync(respHead, 0, respHead.Length);
                    if (body.Length > 0) await cs.WriteAsync(body, 0, body.Length);
                    await ss.CopyToAsync(cs);
                    await Task.WhenAny(up, Task.Delay(upgrade ? 0 : 2000));
                }
                catch { } // the device or the web server closed the connection
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

        static int IndexOf(byte[] a, byte[] p)
        {
            for (int i = 0; i + p.Length <= a.Length; i++)
            {
                int j = 0;
                while (j < p.Length && a[i + j] == p[j]) j++;
                if (j == p.Length) return i;
            }
            return -1;
        }
    }
}
