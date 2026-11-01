using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Portly.Core;
using Portly.Tools;

namespace Portly.Services
{
    /// <summary>
    /// Stands in front of a local web UI that has no Host check of its own (MailHog, Mailpit before 1.31.1): Portly listens on
    /// the UI's port, the tool on a private loopback port. With DNS rebinding a website reaches 127.0.0.1 under its own name,
    /// and "local client only" rules do not help because the browser is local. So a request goes on only with one of this
    /// computer's names in Host, and in Origin when it has one (a page of another site calling the API or the WebSocket).
    /// One connection carries one request, as in the share proxy, and the WebSocket becomes a raw tunnel only after the 101.
    /// The tool itself is behind a random password that only this forwarder sends, so its private port is of no use to a
    /// page that finds it by trying ports. Lives until Stop(); listens on 127.0.0.1 only.
    /// </summary>
    public sealed class HostGuard
    {
        const int HeadTimeoutMs = 30000;
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        static readonly Regex LocalHostName = new Regex(@"^(localhost|127\.0\.0\.1|\[::1\])(:\d{1,5})?\z", RegexOptions.IgnoreCase);
        static readonly Regex LocalOriginName = new Regex(@"^https?://(localhost|127\.0\.0\.1|\[::1\])(:\d{1,5})?\z", RegexOptions.IgnoreCase);

        readonly TcpListener _listener;
        readonly int _target;
        readonly string _authorization;
        readonly HashSet<TcpClient> _open = new HashSet<TcpClient>();
        readonly HashSet<string> _reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool _stopped;

        HostGuard(TcpListener listener, int target, string authorization)
        {
            _listener = listener;
            _target = target;
            _authorization = authorization;
        }

        /// <summary>Listens on 127.0.0.1:<paramref name="port"/>. <paramref name="authorization"/> (or null) replaces the Authorization header. Throws SocketException when the port is taken.</summary>
        public static HostGuard Start(int port, int target, string authorization)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true; // nobody else can bind the same port next to this one
            listener.Start();
            var guard = new HostGuard(listener, target, authorization);
            Task.Run(() => guard.AcceptLoop());
            return guard;
        }

        /// <summary>Closes the port and every connection, live WebSockets too.</summary>
        public void Stop()
        {
            List<TcpClient> open;
            lock (_open)
            {
                _stopped = true;
                open = _open.ToList();
                _open.Clear();
            }
            try { _listener.Stop(); } catch { }
            foreach (var c in open) { try { c.Close(); } catch { } }
        }

        public static bool IsLocalHost(string host) { return host != null && LocalHostName.IsMatch(host); }
        public static bool IsLocalOrigin(string origin) { return origin != null && LocalOriginName.IsMatch(origin); }

        bool Track(TcpClient c)
        {
            lock (_open)
            {
                if (_stopped) return false;
                _open.Add(c);
                return true;
            }
        }

        void Untrack(TcpClient c) { lock (_open) _open.Remove(c); }

        async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { return; } // stopped
                var _ = Task.Run(() => Serve(client));
            }
        }

        async Task Serve(TcpClient client)
        {
            using (client)
            {
                if (!Track(client)) return;
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = HeadTimeoutMs;
                    Stream cs = client.GetStream();
                    byte[] rest;
                    var head = LanShare.ReadHead(cs, out rest);
                    if (head == null) return;
                    if (!LanShare.WellFormed(head)) { await Refuse(cs, "400 Bad Request"); return; }
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None).ToList();

                    var host = LanShare.HeaderValue(lines, "Host");
                    var origin = LanShare.HeaderValue(lines, "Origin");
                    if (!IsLocalHost(host) || origin != null && !IsLocalOrigin(origin))
                    {
                        Report(IsLocalHost(host) ? origin : host);
                        await Refuse(cs, "403 Forbidden");
                        return;
                    }
                    // nothing here needs a chunked request, and its end is what a smuggled request would hide behind
                    if (LanShare.HeaderValue(lines, "Transfer-Encoding") != null) { await Refuse(cs, "411 Length Required"); return; }

                    long length = 0;
                    var cl = LanShare.HeaderValue(lines, "Content-Length");
                    if (cl != null) length = long.Parse(cl); // WellFormed checked it
                    bool upgrade = length == 0 && LanShare.AsksUpgrade(lines);
                    var tunnel = new byte[0];
                    if (upgrade) { tunnel = rest; rest = new byte[0]; }
                    else if (rest.Length > length) rest = rest.Take((int)length).ToArray();

                    using (var server = new TcpClient())
                    {
                        if (!Track(server)) return;
                        try { await Pass(cs, server, lines, upgrade, rest, length, tunnel); }
                        finally { Untrack(server); }
                    }
                }
                catch { } // either side closed the connection
                finally { Untrack(client); }
            }
        }

        async Task Pass(Stream cs, TcpClient server, List<string> lines, bool upgrade, byte[] rest, long length, byte[] tunnel)
        {
            try { await server.ConnectAsync(IPAddress.Loopback, _target); }
            catch (SocketException) { await Refuse(cs, "502 Bad Gateway"); return; }
            server.NoDelay = true;
            server.ReceiveTimeout = HeadTimeoutMs;
            Stream ss = server.GetStream();
            var req = Latin1.GetBytes(string.Join("\r\n", RequestLines(lines, upgrade)) + "\r\n\r\n");
            await ss.WriteAsync(req, 0, req.Length);
            if (rest.Length > 0) await ss.WriteAsync(rest, 0, rest.Length);
            long more = length - rest.Length;
            if (more > 0) { var _ = LanShare.CopyBytes(cs, ss, more); }

            if (!upgrade)
            {
                // also a stream of events: "Connection: close" lets the tool end the connection when it is done
                await ss.CopyToAsync(cs);
                return;
            }
            byte[] body;
            var resp = LanShare.ReadHead(ss, out body);
            if (resp == null) return;
            var h = Latin1.GetBytes(resp + "\r\n\r\n");
            await cs.WriteAsync(h, 0, h.Length);
            if (!LanShare.Switched(resp))
            {
                LanShare.CopyAnswer(ss, body, resp, cs);
                return;
            }
            if (body.Length > 0) await cs.WriteAsync(body, 0, body.Length);
            if (tunnel.Length > 0) await ss.WriteAsync(tunnel, 0, tunnel.Length);
            await Task.WhenAny(cs.CopyToAsync(ss), ss.CopyToAsync(cs)); // whichever side closes first ends it
        }

        List<string> RequestLines(List<string> lines, bool upgrade)
        {
            var outLines = new List<string> { lines[0] };
            foreach (var l in lines.Skip(1))
            {
                var name = LanShare.Name(l);
                if (LanShare.Is(name, "Authorization") || LanShare.Is(name, "Keep-Alive") || LanShare.Is(name, "Proxy-Connection")) continue;
                if (!upgrade && (LanShare.Is(name, "Connection") || LanShare.Is(name, "Upgrade"))) continue;
                outLines.Add(l);
            }
            if (_authorization != null) outLines.Add("Authorization: " + _authorization);
            if (!upgrade) outLines.Add("Connection: close");
            return outLines;
        }

        /// <summary>Into the log once per name: this is how someone who uses another name for the tool finds out why it stopped working.</summary>
        void Report(string name)
        {
            lock (_reported)
            {
                if (_reported.Count >= 50 || !_reported.Add(name ?? "")) return;
            }
            Log.Warn(I18n.T("mail.hostRefused", name ?? ""));
        }

        static async Task Refuse(Stream cs, string status)
        {
            var body = Latin1.GetBytes("Portly: " + status + "\n");
            var h = Latin1.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            await cs.WriteAsync(h, 0, h.Length);
            await cs.WriteAsync(body, 0, body.Length);
        }
    }
}
