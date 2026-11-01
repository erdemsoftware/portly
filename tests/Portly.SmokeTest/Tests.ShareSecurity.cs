using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Tools;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        const string ShareHost = "shop.portly";
        const string PublicHost = "demo.example.com";
        const string Ok200 = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok";
        const string Upgrade101 = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n";
        static readonly string WsHead = "GET /hmr HTTP/1.1\r\nHost: " + PublicHost + "\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Key: abc\r\nSec-WebSocket-Version: 13\r\n\r\n";
        static readonly string SecondRequest = "GET /phpmyadmin/ HTTP/1.1\r\nHost: localhost\r\n\r\n";

        /// <summary>Runs only this group, without services or downloads: Portly.SmokeTest.exe --share-security</summary>
        static int ShareSecurityOnly()
        {
            Paths.EnsureAll();
            Settings.Load();
            I18n.Load("en");
            try { Tests_ShareSecurity(Settings.Current, null).GetAwaiter().GetResult(); }
            catch (Exception ex) { Check("share security", false, ex.ToString()); }
            Console.WriteLine(_ok + " passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>The share proxy passes one checked request, and nothing a device can use to speak to the web server directly.</summary>
        static Task Tests_ShareSecurity(Settings s, string www)
        {
            int httpPort = s.HttpPort, httpsPort = s.HttpsPort;
            bool ssl = s.Ssl;
            s.Ssl = false;
            try
            {
                ShareSmuggling(s);
                ShareTunnel(s);
                ShareMalformed(s);
                ShareNormal(s);
                ShareHttpsRetry(s);
            }
            finally
            {
                s.HttpPort = httpPort; s.HttpsPort = httpsPort; s.Ssl = ssl;
                LanShare.StopTunnelProxy(ShareHost);
            }
            return Task.FromResult(0);
        }

        static int ShareProxy(Settings s, FakeWeb web, string password = "")
        {
            s.HttpPort = web.Port;
            return LanShare.StartTunnelProxy(new Site { Name = "shop", Host = ShareHost }, password);
        }

        static void ShareSmuggling(Settings s)
        {
            // the reported attack: not an upgrade at all, a keep-alive connection reused for a second request
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("GET / HTTP/1.1\r\nHost: " + PublicHost + "\r\nUpgrade: anything\r\nConnection: keep-alive\r\n\r\n");
                c.ReadUntil("ok");
                c.Send(SecondRequest);
                Thread.Sleep(300);
                Check("share: Upgrade without the Connection token is a plain request", web.Seen.StartsWith("GET / HTTP/1.0\r\n") && !web.Seen.Contains("Upgrade:"), Short(web.Seen));
                Check("share: second request after an Upgrade header does not reach the web server", !web.Seen.Contains("phpmyadmin"), Short(web.Seen));
            }
            // a real upgrade head, the server says no
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send(WsHead);
                c.ReadUntil("ok");
                c.Send(SecondRequest);
                Thread.Sleep(300);
                Check("share: second request after a refused upgrade does not reach the web server", !web.Seen.Contains("phpmyadmin"), Short(web.Seen));
            }
            // in one packet: "rest" of the head read
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send(WsHead + SecondRequest);
                var got = c.ReadToClose(3000);
                Check("share: pipelined request after an upgrade head does not reach the web server", got.Contains("ok") && !web.Seen.Contains("phpmyadmin"), Short(web.Seen));
            }
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("GET / HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n" + SecondRequest);
                c.ReadToClose(3000);
                Check("share: pipelined request after a plain GET does not reach the web server", !web.Seen.Contains("phpmyadmin"), Short(web.Seen));
            }
            // a body shorter than what is sent
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("POST /a HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Length: 4\r\n\r\nabcd" + SecondRequest);
                c.ReadToClose(3000);
                Check("share: bytes after Content-Length in the same packet are dropped", web.Seen.EndsWith("abcd"), Short(web.Seen));
            }
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("POST /a HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Length: 4\r\n\r\nab");
                Thread.Sleep(100);
                c.Send("cd" + SecondRequest);
                c.ReadToClose(3000);
                Check("share: bytes after Content-Length in a later packet are dropped", web.Seen.EndsWith("abcd"), Short(web.Seen));
            }
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("POST /a HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n" + SecondRequest);
                c.ReadToClose(3000);
                Check("share: bytes after a request without a body are dropped", !web.Seen.Contains("phpmyadmin"), Short(web.Seen));
            }
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("POST /a HTTP/1.1\r\nHost: " + PublicHost + "\r\nTransfer-Encoding: chunked\r\n\r\n4\r\nabcd\r\n0\r\n\r\n" + SecondRequest);
                var got = c.ReadToClose(3000);
                Check("share: chunked body reaches the web server with a length, nothing after it",
                    got.Contains("ok") && web.Seen.Contains("Content-Length: 4\r\n") && !web.Seen.Contains("Transfer-Encoding") && web.Seen.EndsWith("abcd"), Short(web.Seen));
            }
            // a password share: nothing at all without the cookie
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web, "secret")))
            {
                c.Send(WsHead + SecondRequest);
                var got = c.ReadToClose(3000);
                Check("share: password is checked before any forwarding", got.Contains("password") && web.Connections == 0, Short(got));
            }
            // h2c: a 101 here would make the rest of the connection HTTP/2 requests of the device's own making
            using (var web = new FakeWeb(h => h.Contains("Upgrade:") ? Upgrade101 : Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("GET / HTTP/1.1\r\nHost: " + PublicHost + "\r\nUpgrade: h2c\r\nConnection: Upgrade, HTTP2-Settings\r\nHTTP2-Settings: AAMAAABkAAQAAP__\r\n\r\n");
                var got = c.ReadToClose(3000);
                Check("share: h2c upgrade is a plain request", got.Contains("ok") && web.Seen.StartsWith("GET / HTTP/1.0\r\n") && !web.Seen.Contains("Upgrade:"), Short(web.Seen));
            }
        }

        static void ShareTunnel(Settings s)
        {
            using (var web = new FakeWeb(h => Upgrade101 + "srv-hello") { Echo = true })
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send(WsHead);
                var got = c.ReadUntil("srv-hello");
                Check("share: 101 reaches the device", got.StartsWith("HTTP/1.1 101 ") && got.Contains("srv-hello"), Short(got));
                c.Send("ping");
                got = c.ReadUntil("echo:ping");
                Check("share: after 101 bytes pass both ways", got.Contains("echo:ping"), Short(got));
                Check("share: the upgrade head reaches the web server unchanged but for the Host",
                    web.Seen.StartsWith("GET /hmr HTTP/1.1\r\nHost: " + ShareHost + "\r\n") && web.Seen.Contains("Upgrade: websocket") && web.Seen.Contains("Connection: keep-alive, Upgrade"), Short(web.Seen));
            }
            // bytes sent with the head wait for the 101
            using (var web = new FakeWeb(h => Upgrade101) { Echo = true })
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send(WsHead + "early");
                var got = c.ReadUntil("echo:early");
                Check("share: bytes sent with the head follow the 101", got.Contains("echo:early"), Short(got));
            }
            // either side closing ends the tunnel
            using (var web = new FakeWeb(h => Upgrade101) { Echo = true })
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send(WsHead);
                c.ReadUntil("101");
                web.CloseAll();
                c.ReadToClose(3000);
                Check("share: tunnel ends when the web server closes", c.Closed, "");
            }
            using (var web = new FakeWeb(h => Upgrade101) { Echo = true })
            {
                var c = new ShareClient(ShareProxy(s, web));
                c.Send(WsHead);
                c.ReadUntil("101");
                c.Dispose();
                bool ended = false;
                for (int i = 0; i < 30 && !ended; i++) { Thread.Sleep(100); ended = web.Closed > 0; }
                Check("share: tunnel ends when the device closes", ended, "");
            }

            foreach (var refused in new[]
            {
                Tuple.Create("Content-Length", "HTTP/1.1 403 Forbidden\r\nContent-Length: 6\r\n\r\nnoway!", "noway!"),
                Tuple.Create("chunked", "HTTP/1.1 400 Bad Request\r\nTransfer-Encoding: chunked\r\n\r\n6\r\nnoway!\r\n0\r\n\r\n", "noway!"),
                Tuple.Create("no body", "HTTP/1.1 304 Not Modified\r\nETag: \"x\"\r\n\r\n", "304"),
            })
                using (var web = new FakeWeb(h => refused.Item2))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    var clock = Stopwatch.StartNew();
                    c.Send(WsHead);
                    var got = c.ReadToClose(5000);
                    Check("share: refused upgrade (" + refused.Item1 + ") reaches the device and closes at once",
                        got.Contains(refused.Item3) && c.Closed && clock.ElapsedMilliseconds < 2000, clock.ElapsedMilliseconds + " ms, " + Short(got));
                    Check("share: refused upgrade (" + refused.Item1 + ") tells the device the connection closes", got.Contains("Connection: close"), Short(got));
                }
            // an answer without an end is read to the end of the connection, as HTTP says
            using (var web = new FakeWeb(h => "HTTP/1.1 500 Oops\r\n\r\nstill-here") { CloseAfterAnswer = true })
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send(WsHead);
                var got = c.ReadToClose(3000);
                Check("share: refused upgrade without a length reaches the device", got.Contains("still-here") && c.Closed, Short(got));
            }
        }

        static void ShareMalformed(Settings s)
        {
            var h = "Host: " + PublicHost + "\r\n";
            foreach (var bad in new[]
            {
                Tuple.Create("both Content-Length and Transfer-Encoding", "POST /a HTTP/1.1\r\n" + h + "Content-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\nabcd"),
                Tuple.Create("Transfer-Encoding before Content-Length", "POST /a HTTP/1.1\r\n" + h + "Transfer-Encoding: chunked\r\nContent-Length: 4\r\n\r\nabcd"),
                Tuple.Create("two Content-Lengths", "POST /a HTTP/1.1\r\n" + h + "Content-Length: 4\r\nContent-Length: 5\r\n\r\nabcd"),
                Tuple.Create("Content-Length list", "POST /a HTTP/1.1\r\n" + h + "Content-Length: 4, 5\r\n\r\nabcd"),
                Tuple.Create("signed Content-Length", "POST /a HTTP/1.1\r\n" + h + "Content-Length: +4\r\n\r\nabcd"),
                Tuple.Create("huge Content-Length", "POST /a HTTP/1.1\r\n" + h + "Content-Length: 99999999999999999999999\r\n\r\nabcd"),
                Tuple.Create("empty Content-Length", "POST /a HTTP/1.1\r\n" + h + "Content-Length:\r\n\r\nabcd"),
                Tuple.Create("Transfer-Encoding other than chunked", "POST /a HTTP/1.1\r\n" + h + "Transfer-Encoding: gzip, chunked\r\n\r\n0\r\n\r\n"),
                Tuple.Create("two Transfer-Encodings", "POST /a HTTP/1.1\r\n" + h + "Transfer-Encoding: chunked\r\nTransfer-Encoding: identity\r\n\r\n0\r\n\r\n"),
                Tuple.Create("bad chunk size", "POST /a HTTP/1.1\r\n" + h + "Transfer-Encoding: chunked\r\n\r\nzz\r\nabcd\r\n0\r\n\r\n"),
                Tuple.Create("chunk size with a bare LF", "POST /a HTTP/1.1\r\n" + h + "Transfer-Encoding: chunked\r\n\r\n4\nabcd\r\n0\r\n\r\n"),
                Tuple.Create("chunk without its CRLF", "POST /a HTTP/1.1\r\n" + h + "Transfer-Encoding: chunked\r\n\r\n4\r\nabcdXX\r\n0\r\n\r\n"),
                Tuple.Create("two Hosts", "GET / HTTP/1.1\r\n" + h + "Host: localhost\r\n\r\n"),
                Tuple.Create("space before the colon", "GET / HTTP/1.1\r\n" + h + "Host : localhost\r\n\r\n"),
                Tuple.Create("tab before the colon", "GET / HTTP/1.1\r\n" + h + "Transfer-Encoding\t: chunked\r\n\r\n"),
                Tuple.Create("folded header", "GET / HTTP/1.1\r\n" + h + "X-A: b\r\n Host: localhost\r\n\r\n"),
                Tuple.Create("bare LF in a value", "GET / HTTP/1.1\r\n" + h + "X-A: b\nHost: localhost\r\n\r\n"),
                Tuple.Create("bare CR in a value", "GET / HTTP/1.1\r\n" + h + "X-A: b\rHost: localhost\r\n\r\n"),
                Tuple.Create("NUL in a value", "GET / HTTP/1.1\r\n" + h + "X-A: b\0c\r\n\r\n"),
                Tuple.Create("line without a colon", "GET / HTTP/1.1\r\n" + h + "nonsense\r\n\r\n"),
                Tuple.Create("empty header name", "GET / HTTP/1.1\r\n" + h + ": x\r\n\r\n"),
                Tuple.Create("address in the request line", "GET http://localhost/phpmyadmin/ HTTP/1.1\r\n" + h + "\r\n"),
                Tuple.Create("CONNECT", "CONNECT localhost:80 HTTP/1.1\r\n" + h + "\r\n"),
                Tuple.Create("HTTP/2 preface style request line", "GET / HTTP/2.0\r\n" + h + "\r\n"),
                Tuple.Create("no version", "GET /\r\n" + h + "\r\n"),
                Tuple.Create("two spaces in the request line", "GET  / HTTP/1.1\r\n" + h + "\r\n"),
                Tuple.Create("two Upgrades", "GET / HTTP/1.1\r\n" + h + "Upgrade: websocket\r\nUpgrade: h2c\r\nConnection: Upgrade\r\n\r\n"),
            })
                using (var web = new FakeWeb(x => Ok200))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    c.Send(bad.Item2);
                    var got = c.ReadToClose(3000);
                    Check("share: 400 for " + bad.Item1, got.StartsWith("HTTP/1.1 400 ") && !web.Seen.Contains("/a HTTP") && !web.Seen.Contains("localhost"), Short(got) + " | " + Short(web.Seen));
                }
        }

        static void ShareNormal(Settings s)
        {
            using (var web = new FakeWeb(h => "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 40\r\n\r\n<a href=\"http://" + ShareHost + "/x\">link</a>        "))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("GET /page?a=1 HTTP/1.1\r\nHost: " + PublicHost + "\r\nConnection: keep-alive\r\nAccept-Encoding: gzip\r\nX-Forwarded-For: 1.2.3.4\r\nX_Forwarded_Host: evil\r\nCookie: a=b\r\n\r\n");
                var got = c.ReadToClose(3000);
                Check("share: plain GET goes out as HTTP/1.0 with the site's Host", web.Seen.StartsWith("GET /page?a=1 HTTP/1.0\r\nHost: " + ShareHost + "\r\n"), Short(web.Seen));
                Check("share: GET closes the connection upstream", web.Seen.Contains("Connection: close\r\n") && !web.Seen.Contains("keep-alive"), Short(web.Seen));
                Check("share: forwarded headers come from Portly only", web.Seen.Contains("X-Forwarded-Host: " + PublicHost + "\r\n") && !web.Seen.Contains("1.2.3.4") && !web.Seen.Contains("evil"), Short(web.Seen));
                Check("share: other headers pass", web.Seen.Contains("Cookie: a=b\r\n"), Short(web.Seen));
                Check("share: links in the page are rewritten", got.Contains("href=\"https://" + PublicHost + "/x\""), Short(got));
            }
            using (var web = new FakeWeb(h => "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100\r\n\r\n") { CloseAfterAnswer = true })
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("HEAD / HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                var got = c.ReadToClose(3000);
                Check("share: HEAD", web.Seen.StartsWith("HEAD / HTTP/1.0\r\n") && got.Contains("Content-Length: 100") && c.Closed, Short(got));
            }
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("POST /upload HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Type: text/plain\r\nContent-Length: 11\r\n\r\nhello");
                Thread.Sleep(100);
                c.Send(" world");
                var got = c.ReadToClose(3000);
                Check("share: POST with the body in two packets", got.Contains("ok") && web.Seen.EndsWith("Content-Length: 11\r\nX-Forwarded-Host: " + PublicHost + "\r\nX-Forwarded-Proto: https\r\nX-Forwarded-For: 127.0.0.1\r\nConnection: close\r\n\r\nhello world"), Short(web.Seen));
            }
            // larger than one read of the proxy
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                var big = new string('x', 300000);
                c.Send("POST /upload HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Length: " + big.Length + "\r\n\r\n" + big);
                var got = c.ReadToClose(5000);
                Check("share: 300 KB POST arrives whole", got.Contains("ok") && web.Seen.EndsWith("\r\n\r\n" + big), web.Seen.Length.ToString());
            }
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("POST /u HTTP/1.1\r\nHost: " + PublicHost + "\r\nTransfer-Encoding: Chunked\r\n\r\n3;x=1\r\nabc\r\n");
                Thread.Sleep(100);
                c.Send("2\r\nde\r\n0\r\nX-Trailer: 1\r\n\r\n");
                var got = c.ReadToClose(3000);
                Check("share: chunked POST in two packets, extension and trailer", got.Contains("ok") && web.Seen.Contains("Content-Length: 5\r\n") && web.Seen.EndsWith("abcde"), Short(web.Seen));
            }
            // the password flow
            using (var web = new FakeWeb(h => Ok200))
            {
                int port = ShareProxy(s, web, "secret");
                using (var c = new ShareClient(port))
                {
                    var form = "password=secret&next=%2Fhome";
                    c.Send("POST /__portly/login HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Length: " + form.Length + "\r\n\r\n" + form);
                    var got = c.ReadToClose(3000);
                    var cookie = System.Text.RegularExpressions.Regex.Match(got, @"Set-Cookie: ([^;]+);").Groups[1].Value;
                    Check("share: right password gives a cookie", got.StartsWith("HTTP/1.1 303 ") && cookie != "" && web.Connections == 0, Short(got));
                    using (var d = new ShareClient(port))
                    {
                        d.Send("GET /home HTTP/1.1\r\nHost: " + PublicHost + "\r\nCookie: " + cookie + "; a=b\r\n\r\n");
                        var page = d.ReadToClose(3000);
                        Check("share: the cookie opens the site and stays away from it", page.Contains("ok") && web.Seen.Contains("Cookie: a=b\r\n") && !web.Seen.Contains("portly_share"), Short(web.Seen));
                    }
                }
            }
        }

        /// <summary>http -> https of the same site: answered from the local HTTPS port, with the body sent again.</summary>
        static void ShareHttpsRetry(Settings s)
        {
            X509Certificate2 cert;
            using (var rsa = RSA.Create(2048))
            using (var made = new CertificateRequest("CN=" + ShareHost, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)))
                cert = new X509Certificate2(made.Export(X509ContentType.Pfx));
            using (var web = new FakeWeb(h => "HTTP/1.1 301 Moved\r\nLocation: https://" + ShareHost + "/a\r\nContent-Length: 0\r\n\r\n"))
            using (var tls = new FakeWeb(h => Ok200) { Certificate = cert })
            {
                s.Ssl = true;
                s.HttpsPort = tls.Port;
                try
                {
                    using (var c = new ShareClient(ShareProxy(s, web)))
                    {
                        c.Send("POST /a HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Length: 4\r\n\r\nabcd" + SecondRequest);
                        var got = c.ReadToClose(5000);
                        Check("share: http -> https redirect is retried on the HTTPS port with the body", got.Contains("ok") && !got.Contains("301") && tls.Seen.EndsWith("abcd"), Short(got) + " | " + Short(tls.Seen));
                    }
                }
                finally { s.Ssl = false; }
            }
        }

        /// <summary>A device talking to the proxy.</summary>
        class ShareClient : IDisposable
        {
            readonly TcpClient _c = new TcpClient();
            readonly NetworkStream _s;
            public bool Closed;

            public ShareClient(int port)
            {
                _c.Connect(IPAddress.Loopback, port);
                _c.NoDelay = true;
                _s = _c.GetStream();
            }

            public void Send(string text)
            {
                try { var b = Encoding.GetEncoding(28591).GetBytes(text); _s.Write(b, 0, b.Length); }
                catch (IOException) { } // the proxy already closed: what reached the web server is what counts
            }

            string _got = "";

            public string ReadUntil(string text, int ms = 3000)
            {
                var clock = Stopwatch.StartNew();
                while (!_got.Contains(text) && !Closed && clock.ElapsedMilliseconds < ms) ReadSome(ms);
                return _got;
            }

            public string ReadToClose(int ms)
            {
                var clock = Stopwatch.StartNew();
                while (!Closed && clock.ElapsedMilliseconds < ms) ReadSome(ms);
                return _got;
            }

            void ReadSome(int ms)
            {
                var buf = new byte[65536];
                _c.ReceiveTimeout = ms;
                try
                {
                    int n = _s.Read(buf, 0, buf.Length);
                    if (n <= 0) Closed = true;
                    else _got += Encoding.GetEncoding(28591).GetString(buf, 0, n);
                }
                catch (IOException ex)
                {
                    var se = ex.InnerException as SocketException;
                    Closed = se == null || se.SocketErrorCode != SocketError.TimedOut;
                }
            }

            public void Dispose() { _c.Close(); }
        }

        /// <summary>
        /// The web server behind the proxy. Records every byte that arrives; like Apache it answers each complete request and
        /// keeps an HTTP/1.1 connection open for the next one. Bytes after an HTTP/1.0 request are still recorded for a moment.
        /// </summary>
        class FakeWeb : IDisposable
        {
            readonly TcpListener _l = new TcpListener(IPAddress.Loopback, 0);
            readonly Func<string, string> _answer;
            readonly StringBuilder _seen = new StringBuilder();
            readonly List<TcpClient> _open = new List<TcpClient>();
            int _connections, _closed;

            public readonly int Port;
            public bool Echo;               // after a 101: "echo:" + every piece of data
            public bool CloseAfterAnswer;   // an answer that ends with the connection
            public X509Certificate2 Certificate;

            public int Connections { get { return Volatile.Read(ref _connections); } }
            public int Closed { get { return Volatile.Read(ref _closed); } }
            public string Seen { get { lock (_seen) return _seen.ToString(); } }

            public FakeWeb(Func<string, string> answer)
            {
                _answer = answer;
                _l.Start();
                Port = ((IPEndPoint)_l.LocalEndpoint).Port;
                Task.Run(() => Loop());
            }

            async Task Loop()
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync(); }
                    catch { return; }
                    Interlocked.Increment(ref _connections);
                    lock (_open) _open.Add(c);
                    var _ = Task.Run(() => Connection(c));
                }
            }

            void Connection(TcpClient c)
            {
                var latin = Encoding.GetEncoding(28591);
                try
                {
                    using (c)
                    {
                        Stream s = c.GetStream();
                        if (Certificate != null)
                        {
                            var ssl = new SslStream(s);
                            ssl.AuthenticateAsServer(Certificate, false, SslProtocols.Tls12, false);
                            s = ssl;
                        }
                        var pending = "";
                        var buf = new byte[65536];
                        bool tunnel = false, ending = false;
                        c.ReceiveTimeout = 10000;
                        while (true)
                        {
                            int n;
                            try { n = s.Read(buf, 0, buf.Length); }
                            catch (IOException) { return; }
                            if (n <= 0) return;
                            var text = latin.GetString(buf, 0, n);
                            lock (_seen) _seen.Append(text);
                            if (tunnel)
                            {
                                if (Echo) Write(s, "echo:" + text);
                                continue;
                            }
                            if (ending) continue;
                            pending += text;
                            while (true)
                            {
                                int end = pending.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                if (end < 0) break;
                                var head = pending.Substring(0, end);
                                var m = System.Text.RegularExpressions.Regex.Match(head, @"(?im)^Content-Length:\s*(\d+)");
                                int length = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                                if (pending.Length < end + 4 + length) break;
                                pending = pending.Substring(end + 4 + length);
                                var answer = _answer(head);
                                Write(s, answer);
                                if (CloseAfterAnswer) return;
                                if (answer.StartsWith("HTTP/1.1 101 "))
                                {
                                    tunnel = true;
                                    if (Echo && pending != "") Write(s, "echo:" + pending);
                                    break;
                                }
                                if (head.StartsWith("HEAD ") || head.Contains(" HTTP/1.0\r\n"))
                                {
                                    // closes shortly after, while bytes sent behind the request are still seen
                                    ending = true;
                                    c.ReceiveTimeout = 200;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
                finally { Interlocked.Increment(ref _closed); }
            }

            static void Write(Stream s, string text)
            {
                var b = Encoding.GetEncoding(28591).GetBytes(text);
                s.Write(b, 0, b.Length);
            }

            public void CloseAll()
            {
                lock (_open) foreach (var c in _open) try { c.Close(); } catch { }
            }

            public void Dispose()
            {
                _l.Stop();
                CloseAll();
            }
        }
    }
}
