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
            return _fail == 0 && _ok > 0 ? 0 : 1;
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
                ShareHeaders(s);
                ShareSlowClients(s);
                ShareConnectionLimits(s);
                SharePasswordGate();
                SharePasswordFlow(s);
                ShareLanSide(s);
                ShareFirewallRules();
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
            // what is connected goes with the right to be connected
            using (var web = new FakeWeb(h => Upgrade101) { Echo = true })
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send(WsHead);
                c.ReadUntil("101");
                LanShare.SetPassword(ShareHost, "changed", true);
                c.ReadToClose(3000);
                Check("share: a new password ends open tunnels", c.Closed, "");
            }
            using (var web = new FakeWeb(h => Upgrade101) { Echo = true })
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send(WsHead);
                c.ReadUntil("101");
                LanShare.StopTunnelProxy(ShareHost);
                c.ReadToClose(3000);
                Check("share: stopping the share ends open tunnels", c.Closed, "");
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
                Check("share: POST with the body in two packets", got.Contains("ok") && web.Seen.EndsWith("Content-Length: 11\r\nX-Forwarded-Host: " + PublicHost + "\r\nX-Forwarded-Proto: https\r\nX-Forwarded-For: 127.0.0.1\r\nX-Real-IP: 127.0.0.1\r\nConnection: close\r\n\r\nhello world"), Short(web.Seen));
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

        /// <summary>Only Portly tells the site who is asking; the address for the device list still comes from Cloudflare.</summary>
        static void ShareHeaders(Settings s)
        {
            var spoofed = new[]
            {
                "Forwarded", "forwarded", "X-Real-IP", "X_Real_IP", "x-real-ip", "True-Client-IP", "True_Client_IP", "X-Client-IP", "X_Client_IP", "Client-IP",
                "CF-Connecting-IP", "CF_Connecting_IP", "cf-connecting-ip", "CF-Connecting-IPv6", "CF-IPCountry", "X-Cluster-Client-IP", "X_Cluster_Client_IP",
                "X-Originating-IP", "X-Remote-IP", "X-Remote-Addr", "X-Forwarded-For", "X_Forwarded_For", "X-Forwarded-Server", "X-Host", "X-Original-URL", "X-Rewrite-URL", "Proxy",
            };
            foreach (var request in new[] { "GET /a HTTP/1.1\r\nHost: " + PublicHost + "\r\n", WsHead.Substring(0, WsHead.Length - 2) })
            {
                bool upgrade = request == WsHead.Substring(0, WsHead.Length - 2);
                foreach (var name in spoofed)
                    using (var web = new FakeWeb(h => Ok200))
                    using (var c = new ShareClient(ShareProxy(s, web)))
                    {
                        c.Send(request + name + ": 6.6.6.6\r\n\r\n");
                        c.ReadToClose(3000);
                        // CF-Connecting-IP itself is who Portly believes on this kind of share: it comes out as Portly's own two headers only
                        var passed = name.StartsWith("CF-C", StringComparison.OrdinalIgnoreCase) ? web.Seen.Replace("X-Forwarded-For: 6.6.6.6\r\n", "").Replace("X-Real-IP: 6.6.6.6\r\n", "") : web.Seen;
                        Check("share: client " + name + " does not reach the site" + (upgrade ? " (upgrade)" : ""), web.Seen.StartsWith("GET ") && !passed.Contains("6.6.6.6"), Short(web.Seen));
                    }
            }
            using (var web = new FakeWeb(h => Ok200))
            {
                int port = ShareProxy(s, web);
                using (var c = new ShareClient(port))
                {
                    c.Send("GET / HTTP/1.1\r\nHost: " + PublicHost + "\r\nCF-Connecting-IP: 203.0.113.9\r\nCF-IPCountry: TR\r\nX-Real-IP: 6.6.6.6\r\nX-Forwarded-For: 6.6.6.6\r\n\r\n");
                    c.ReadToClose(3000);
                }
                Check("share: the site gets the visitor's address from Portly only", web.Seen.Contains("X-Forwarded-For: 203.0.113.9\r\n") && web.Seen.Contains("X-Real-IP: 203.0.113.9\r\n") && !web.Seen.Contains("6.6.6.6") && !web.Seen.Contains("CF-Connecting-IP"), Short(web.Seen));
                var device = LanShare.DevicesOf(ShareHost, true).FirstOrDefault(d => d.Ip == "203.0.113.9");
                Check("share: the device list still reads CF-Connecting-IP", device != null && device.Country == "TR", string.Join(",", LanShare.DevicesOf(ShareHost, true).Select(d => d.Label)));
            }
            using (var web = new FakeWeb(h => Ok200))
            using (var c = new ShareClient(ShareProxy(s, web)))
            {
                c.Send("GET / HTTP/1.1\r\nHost: " + PublicHost + "\r\nCF-Connecting-IP: not an address, 1.2.3.4\r\n\r\n");
                c.ReadToClose(3000);
                Check("share: a CF-Connecting-IP that is no address is not believed", web.Seen.Contains("X-Forwarded-For: 127.0.0.1\r\n") && !web.Seen.Contains("not an address"), Short(web.Seen));
            }
        }

        static void Limits(int head, int window, int minBytes, int stall, Action test)
        {
            int h = ShareLimits.HeadMs, w = ShareLimits.BodyWindowMs, m = ShareLimits.BodyMinBytes, t = ShareLimits.StallMs;
            ShareLimits.HeadMs = head; ShareLimits.BodyWindowMs = window; ShareLimits.BodyMinBytes = minBytes; ShareLimits.StallMs = stall;
            try { test(); }
            finally { ShareLimits.HeadMs = h; ShareLimits.BodyWindowMs = w; ShareLimits.BodyMinBytes = m; ShareLimits.StallMs = t; }
        }

        /// <summary>Sends one small piece every 100 ms until the proxy hangs up, at most for <paramref name="ms"/>. Returns how long it stayed open, in ms.</summary>
        static long Trickle(ShareClient c, int ms, string piece = "x")
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms && !c.HungUp(100)) c.Send(piece);
            return clock.ElapsedMilliseconds;
        }

        static void ShareSlowClients(Settings s)
        {
            Limits(800, 600, 1000, 700, () =>
            {
                using (var web = new FakeWeb(h => Ok200))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    var clock = Stopwatch.StartNew();
                    c.ReadToClose(4000);
                    Check("share: a connection that sends nothing is closed when the head time is over", c.Closed && clock.ElapsedMilliseconds < 2500 && web.Connections == 0, clock.ElapsedMilliseconds + " ms");
                }
                using (var web = new FakeWeb(h => Ok200))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    c.Send("GET / HTTP/1.1\r\nHost: " + PublicHost + "\r\n");
                    var open = Trickle(c, 5000, "X-A: b\r\n");
                    Check("share: a head sent byte by byte does not extend its time", open < 2500 && web.Connections == 0, open + " ms");
                }
                using (var web = new FakeWeb(h => Ok200))
                using (var c = new ShareClient(ShareProxy(s, web, "secret")))
                {
                    c.Send("POST /__portly/login HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Length: 4000\r\n\r\n");
                    var open = Trickle(c, 5000);
                    Check("share: a login form sent byte by byte runs out of the same time", open < 2500, open + " ms");
                }
                using (var web = new FakeWeb(h => Ok200))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    c.Send("POST /a HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Length: 100000\r\n\r\n");
                    var open = Trickle(c, 6000);
                    bool ended = false;
                    for (int i = 0; i < 30 && !ended; i++) { Thread.Sleep(50); ended = web.Closed > 0; }
                    Check("share: a body that trickles is closed, the web server side too", open < 3000 && ended, open + " ms, web closed " + web.Closed);
                }
                using (var web = new FakeWeb(h => Ok200))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    c.Send("POST /a HTTP/1.1\r\nHost: " + PublicHost + "\r\nTransfer-Encoding: chunked\r\n\r\n");
                    var open = Trickle(c, 6000, "1\r\nx\r\n");
                    Check("share: a chunked body that trickles is closed", open < 3000 && web.Connections == 0, open + " ms");
                }
                // about 3 KB/s for 4 seconds: over several windows, none of them empty
                using (var web = new FakeWeb(h => Ok200))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    var piece = new string('u', 1000);
                    c.Send("POST /upload HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Length: " + 12 * piece.Length + "\r\n\r\n");
                    for (int i = 0; i < 12; i++) { Thread.Sleep(300); c.Send(piece); }
                    var got = c.ReadToClose(3000);
                    Check("share: a slow but steady upload arrives whole", got.Contains("ok") && web.Seen.EndsWith(string.Concat(Enumerable.Repeat(piece, 12))), web.Seen.Length.ToString());
                }
                // the time PHP takes is not the device's
                using (var web = new FakeWeb(h => { Thread.Sleep(2000); return Ok200; }))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    c.Send("GET / HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                    var got = c.ReadToClose(5000);
                    Check("share: a slow page is left alone", got.Contains("ok"), Short(got));
                }
                int waitServer = ShareLimits.ServerMs;
                ShareLimits.ServerMs = 700;
                using (var web = new FakeWeb(h => { Thread.Sleep(4000); return Ok200; }))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    var clock = Stopwatch.StartNew();
                    c.Send("GET / HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                    c.ReadToClose(3000);
                    Check("share: a web server that never answers does not hold the connection for ever", c.Closed && clock.ElapsedMilliseconds < 2500, clock.ElapsedMilliseconds + " ms");
                }
                ShareLimits.ServerMs = waitServer;
                using (var web = new FakeWeb(h => { Thread.Sleep(1500); return Ok200; }))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    c.Send("POST /a HTTP/1.1\r\nHost: " + PublicHost + "\r\nContent-Length: 4\r\n\r\nabcd");
                    var got = c.ReadToClose(5000);
                    Check("share: a POST with a slow answer is left alone", got.Contains("ok"), Short(got));
                }
                using (var web = new FakeWeb(h => Upgrade101) { Echo = true })
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    c.Send(WsHead);
                    c.ReadUntil("101");
                    Thread.Sleep(1800);
                    c.Send("ping");
                    var got = c.ReadUntil("echo:ping");
                    Check("share: a quiet tunnel stays open past every deadline", got.Contains("echo:ping"), Short(got));
                }
                // a device that stops reading: Windows takes in a lot for it first, then the proxy's writes wait
                using (var web = new FakeWeb(h => "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 2147483648\r\n\r\n") { FloodMb = 2048 })
                using (var c = new ShareClient(ShareProxy(s, web), 4096))
                {
                    c.Send("GET /big HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                    bool ended = false;
                    for (int i = 0; i < 150 && !ended; i++) { Thread.Sleep(100); ended = web.Closed > 0; }
                    Check("share: a device that stops reading is closed", ended && !web.Flooded, "web closed " + web.Closed);
                }
                // reading steadily, a large rewritten page is one big write for the proxy
                var page = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 4194304\r\n\r\n" + new string('p', 4 * 1024 * 1024);
                using (var web = new FakeWeb(h => page))
                using (var c = new ShareClient(ShareProxy(s, web)))
                {
                    c.Send("GET /big HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                    var got = c.ReadToClose(8000);
                    Check("share: a device that keeps reading gets a large page whole", got.EndsWith(new string('p', 1000)) && got.Length > 4 * 1024 * 1024, got.Length.ToString());
                }
            });
        }

        static void ShareConnectionLimits(Settings s)
        {
            int conns = ShareLimits.MaxConnections, tunnels = ShareLimits.MaxTunnels, refusing = ShareLimits.MaxRefusing;
            try
            {
                using (var web = new FakeWeb(h => Ok200))
                {
                    int port = ShareProxy(s, web);
                    var clients = Enumerable.Range(0, 60).Select(i => new ShareClient(port)).ToList();
                    foreach (var c in clients) c.Send("GET /asset" + " HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                    var answers = clients.Select(c => c.ReadToClose(5000)).ToList();
                    int fine = answers.Count(a => a.Contains("ok"));
                    clients.ForEach(c => c.Dispose());
                    Check("share: 60 assets at once are all served with the normal limit", fine == 60, fine + ": " + Short(answers.FirstOrDefault(a => !a.Contains("ok")) ?? ""));
                }
                ShareLimits.MaxConnections = 3;
                using (var web = new FakeWeb(h => Ok200))
                {
                    int port = ShareProxy(s, web);
                    var waiting = Enumerable.Range(0, 3).Select(i => new ShareClient(port)).ToList();
                    Thread.Sleep(200);
                    using (var c = new ShareClient(port))
                    {
                        c.Send("GET /one-too-many HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                        var got = c.ReadToClose(3000);
                        Check("share: past the limit a device gets a clean 503", got.StartsWith("HTTP/1.1 503 ") && got.Contains("Retry-After:") && c.Closed && !web.Seen.Contains("one-too-many"), Short(got));
                    }
                    waiting.ForEach(c => c.Dispose());
                    Thread.Sleep(300);
                    using (var c = new ShareClient(port))
                    {
                        c.Send("GET /again HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                        var got = c.ReadToClose(3000);
                        Check("share: places free up when connections end", got.Contains("ok"), Short(got));
                    }
                }
                ShareLimits.MaxConnections = 2;
                ShareLimits.MaxTunnels = 2;
                using (var web = new FakeWeb(h => h.Contains("Upgrade:") ? Upgrade101 : Ok200) { Echo = true })
                {
                    int port = ShareProxy(s, web);
                    var open = Enumerable.Range(0, 2).Select(i => new ShareClient(port)).ToList();
                    foreach (var c in open) { c.Send(WsHead); c.ReadUntil("101"); }
                    Thread.Sleep(100);
                    using (var c = new ShareClient(port))
                    {
                        c.Send(WsHead.Replace("/hmr", "/third"));
                        var got = c.ReadToClose(3000);
                        Check("share: past the tunnel limit an upgrade gets 503 and never reaches the site", got.StartsWith("HTTP/1.1 503 ") && !web.Seen.Contains("/third"), Short(got));
                    }
                    using (var c = new ShareClient(port))
                    using (var d = new ShareClient(port))
                    {
                        c.Send("GET /plain HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                        d.Send("GET /plain HTTP/1.1\r\nHost: " + PublicHost + "\r\n\r\n");
                        Check("share: open tunnels do not use up the places of normal requests", c.ReadToClose(3000).StartsWith("HTTP/1.1 200 ") && d.ReadToClose(3000).StartsWith("HTTP/1.1 200 "), Short(c.ReadToClose(1)));
                    }
                    open[0].Dispose();
                    Thread.Sleep(300);
                    using (var c = new ShareClient(port))
                    {
                        c.Send(WsHead);
                        Check("share: a closed tunnel frees its place", c.ReadUntil("101").StartsWith("HTTP/1.1 101"), Short(c.ReadUntil("101")));
                    }
                    open[1].Dispose();
                }
                ShareLimits.MaxConnections = 0;
                ShareLimits.MaxRefusing = 1;
                using (var web = new FakeWeb(h => Ok200))
                {
                    int port = ShareProxy(s, web);
                    using (var a = new ShareClient(port))
                    {
                        Thread.Sleep(100);
                        using (var b = new ShareClient(port))
                        {
                            var clock = Stopwatch.StartNew();
                            b.ReadToClose(3000);
                            Check("share: past the refusing limit a connection is just closed", b.Closed && clock.ElapsedMilliseconds < 1000, clock.ElapsedMilliseconds + " ms");
                        }
                    }
                }
            }
            finally { ShareLimits.MaxConnections = conns; ShareLimits.MaxTunnels = tunnels; ShareLimits.MaxRefusing = refusing; }
        }

        static void SharePasswordGate()
        {
            var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var gate = new PasswordGate();
            GateVerdict v = null;
            for (int i = 0; i < 5; i++) v = gate.Attempt("10.0.0.1", () => false, t);
            Check("password gate: the fifth wrong answer locks for a minute", v.LockStarted && !v.Everyone && v.Wait == TimeSpan.FromMinutes(1), v.Wait.ToString());
            bool asked = false;
            v = gate.Attempt("10.0.0.1", () => { asked = true; return true; }, t.AddSeconds(59));
            Check("password gate: while locked the password is not looked at", !asked && !v.Right && !v.Tested && v.Wait == TimeSpan.FromSeconds(1) && !v.LockStarted, v.Wait.ToString());
            Check("password gate: another address is not locked", gate.Attempt("10.0.0.2", () => true, t).Right, "");
            var locks = new List<double>();
            for (int i = 0; i < 7; i++)
            {
                t = t.AddMinutes(16);
                locks.Add(gate.Attempt("10.0.0.1", () => false, t).Wait.TotalMinutes);
            }
            Check("password gate: the lock grows up to 15 minutes", locks.SequenceEqual(new double[] { 2, 4, 8, 15, 15, 15, 15 }), string.Join(",", locks));
            t = t.AddMinutes(16);
            Check("password gate: the right password after the lock opens and clears", gate.Attempt("10.0.0.1", () => true, t).Right, "");
            for (int i = 0; i < 4; i++) v = gate.Attempt("10.0.0.1", () => false, t);
            Check("password gate: a success starts the count again", v.Wait == TimeSpan.Zero, v.Wait.ToString());
            gate.Reset();
            Check("password gate: Reset forgets everything", gate.Attempt("10.0.0.1", () => false, t).Wait == TimeSpan.Zero && gate.Tracked == 1, "");

            gate = new PasswordGate();
            for (int i = 0; i < 19; i++) v = gate.Attempt("10.1.0." + i, () => false, t);
            Check("password gate: wrong answers from many addresses are allowed up to the share's limit", v.Wait == TimeSpan.Zero, v.Wait.ToString());
            v = gate.Attempt("10.1.0.100", () => false, t);
            Check("password gate: the share-wide limit locks", v.LockStarted && v.Everyone && v.Wait == TimeSpan.FromMinutes(1), v.Wait.ToString());
            asked = false;
            v = gate.Attempt("10.1.0.200", () => { asked = true; return true; }, t.AddSeconds(30));
            Check("password gate: a new address cannot test the password during the share-wide lock", !asked && !v.Right && v.Wait > TimeSpan.Zero, v.Wait.ToString());
            int tested = 0;
            var u = t;
            for (int i = 0; i < 2000; i++)
            {
                u = u.AddSeconds(1);
                if (gate.Attempt("10.2." + i / 200 + "." + i % 200, () => false, u).Tested) tested++;
            }
            Check("password gate: rotating addresses get about 100 guesses an hour", tested <= 110 && tested > 0, tested.ToString());

            gate = new PasswordGate();
            for (int i = 0; i < 5; i++) v = gate.Attempt("2001:db8:1:2::" + (i + 1).ToString("x"), () => false, t);
            Check("password gate: an IPv6 network counts as one address", v.LockStarted, v.Wait.ToString());
            Check("password gate: another IPv6 network does not", gate.Attempt("2001:db8:1:3::1", () => true, t).Right, "");

            gate = new PasswordGate();
            for (int i = 0; i < 3000; i++) gate.Attempt("10." + i / 250 + "." + i % 250 + ".1", () => false, t.AddMinutes(10 * i));
            Check("password gate: the list of addresses stays bounded", gate.Tracked <= 1024, gate.Tracked.ToString());

            Check("share: password comparison", LanShare.SamePassword("kite-1234-moon", "kite-1234-moon") && LanShare.SamePassword("şifré", "şifré")
                && !LanShare.SamePassword("kite", "kite-1234-moon") && !LanShare.SamePassword("kitf", "kite") && !LanShare.SamePassword("", "kite"), "");
        }

        static string Login(int port, string password, string address)
        {
            using (var c = new ShareClient(port))
            {
                var form = "password=" + password + "&next=%2F";
                c.Send("POST /__portly/login HTTP/1.1\r\nHost: " + PublicHost + "\r\nCF-Connecting-IP: " + address + "\r\nContent-Length: " + form.Length + "\r\n\r\n" + form);
                return c.ReadToClose(5000);
            }
        }

        static void SharePasswordFlow(Settings s)
        {
            using (var web = new FakeWeb(h => Ok200))
            {
                int port = ShareProxy(s, web, "secret");
                var bad = "";
                for (int i = 0; i < 4; i++) bad = Login(port, "guess" + i, "198.51.100.1");
                Check("share: wrong password below the limit is 401", bad.StartsWith("HTTP/1.1 401 "), Short(bad));
                bad = Login(port, "guess5", "198.51.100.1");
                Check("share: the fifth wrong password is 429 and says when to try again", bad.StartsWith("HTTP/1.1 429 ") && bad.Contains("Retry-After: 60\r\n"), Short(bad));
                var clock = Stopwatch.StartNew();
                var wrong = Login(port, "guess6", "198.51.100.1");
                var right = Login(port, "secret", "198.51.100.1");
                Check("share: locked out, the right password gets nothing", right.StartsWith("HTTP/1.1 429 ") && !right.Contains("Set-Cookie"), Short(right));
                Func<string, string> body = x => System.Text.RegularExpressions.Regex.Replace(x, @"Retry-After: \d+", "");
                Check("share: locked out, right and wrong passwords get the same answer", body(wrong) == body(right), Short(wrong) + " | " + Short(right));
                Check("share: locked out, the answer is immediate", clock.ElapsedMilliseconds < 500, clock.ElapsedMilliseconds + " ms");
                var other = Login(port, "secret", "198.51.100.2");
                Check("share: another address can still log in", other.StartsWith("HTTP/1.1 303 "), Short(other));
                var cookie = System.Text.RegularExpressions.Regex.Match(other, @"Set-Cookie: ([^;]+);").Groups[1].Value;
                using (var c = new ShareClient(port))
                {
                    c.Send("GET / HTTP/1.1\r\nHost: " + PublicHost + "\r\nCF-Connecting-IP: 198.51.100.1\r\nCookie: " + cookie + "\r\n\r\n");
                    Check("share: a device with the cookie stays in during a lockout", c.ReadToClose(3000).Contains("ok"), "");
                }
                using (var c = new ShareClient(port))
                {
                    var form = "password=secret";
                    c.Send("POST /__portly/login HTTP/1.1\r\nHost: " + PublicHost + "\r\nCF-Connecting-IP: 198.51.100.1\r\nX-Forwarded-For: 198.51.100.77\r\nX-Real-IP: 198.51.100.77\r\nContent-Length: " + form.Length + "\r\n\r\n" + form);
                    Check("share: a forged address header does not get around the lockout", c.ReadToClose(5000).StartsWith("HTTP/1.1 429 "), "");
                }
                LanShare.SetPassword(ShareHost, "changed", true);
                var after = Login(port, "changed", "198.51.100.1");
                Check("share: a new password ends the lockout", after.StartsWith("HTTP/1.1 303 "), Short(after));
            }
            // side by side: the count happens with the comparison
            using (var web = new FakeWeb(h => Ok200))
            {
                int port = ShareProxy(s, web, "secret");
                var answers = Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() => Login(port, "guess" + i, "198.51.100.3")))).GetAwaiter().GetResult();
                int unlocked = answers.Count(a => a.StartsWith("HTTP/1.1 401 "));
                Check("share: guesses sent side by side cannot pass the limit", unlocked == 4 && answers.Count(a => a.StartsWith("HTTP/1.1 429 ")) == 20, unlocked.ToString());
            }
        }

        /// <summary>
        /// The local network branch of the proxy, on 127.0.0.1: a real share listens on all addresses, which would put the
        /// Windows firewall question on the screen of whoever runs this.
        /// </summary>
        static TcpListener LanProxy(bool https, string password = "")
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var type = typeof(LanShare).GetNestedType("Entry", System.Reflection.BindingFlags.NonPublic);
            var entry = Activator.CreateInstance(type);
            type.GetField("Listener").SetValue(entry, listener);
            type.GetField("Port").SetValue(entry, ((IPEndPoint)listener.LocalEndpoint).Port);
            type.GetField("Https").SetValue(entry, https);
            type.GetField("Password").SetValue(entry, password);
            type.GetField("Token").SetValue(entry, Guid.NewGuid().ToString("N"));
            typeof(LanShare).GetMethod("AcceptLoop", flags).Invoke(null, new[] { entry, ShareHost });
            return listener;
        }

        static void ShareLanSide(Settings s)
        {
            Limits(800, 600, 1000, 700, () =>
            {
                using (var web = new FakeWeb(h => Ok200))
                {
                    s.HttpPort = web.Port;
                    var listener = LanProxy(true);
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    try
                    {
                        using (var c = new ShareClient(port))
                        {
                            c.Send("GET / HTTP/1.1\r\nHost: x\r\nCF-Connecting-IP: 6.6.6.6\r\n\r\n");
                            var got = c.ReadToClose(3000);
                            Check("share (LAN): plain http on the shared port, no address believed from a header", got.Contains("ok") && web.Seen.Contains("X-Forwarded-For: 127.0.0.1\r\n") && web.Seen.Contains("X-Forwarded-Proto: http\r\n") && !web.Seen.Contains("6.6.6.6"), Short(web.Seen));
                        }
                        using (var c = new ShareClient(port))
                        {
                            var clock = Stopwatch.StartNew();
                            c.ReadToClose(4000);
                            Check("share (LAN): a connection that sends nothing is closed in time", c.Closed && clock.ElapsedMilliseconds < 2500, clock.ElapsedMilliseconds + " ms");
                        }
                        using (var c = new ShareClient(port))
                        {
                            c.Send("\x16\x03\x01");
                            var open = Trickle(c, 5000, "\x00");
                            Check("share (LAN): a TLS handshake that stalls is closed in time", open < 2500, open + " ms");
                        }
                        using (var raw = new TcpClient())
                        {
                            raw.Connect(IPAddress.Loopback, port);
                            using (var tls = new SslStream(raw.GetStream(), false, (o, c, ch, e) => true))
                            {
                                tls.AuthenticateAsClient("127.0.0.1", null, SslProtocols.Tls12, false);
                                var request = Encoding.ASCII.GetBytes("GET /over-tls HTTP/1.1\r\nHost: x\r\n\r\n");
                                tls.Write(request, 0, request.Length);
                                var got = new StreamReader(tls, Encoding.ASCII).ReadToEnd();
                                Check("share (LAN): a request over TLS gets its answer", got.Contains("ok") && web.Seen.Contains("GET /over-tls HTTP/1.0\r\n") && web.Seen.Contains("X-Forwarded-Proto: https\r\n"), Short(got));
                            }
                        }
                    }
                    finally { listener.Stop(); }
                }
            });
            using (var web = new FakeWeb(h => Ok200))
            {
                s.HttpPort = web.Port;
                var listener = LanProxy(false, "secret");
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                try
                {
                    var last = "";
                    for (int i = 0; i < 5; i++) last = Login(port, "guess", "198.51.100." + (10 + i));
                    Check("share (LAN): a forged address header cannot rotate past the lockout", last.StartsWith("HTTP/1.1 429 "), Short(last));
                }
                finally { listener.Stop(); }
            }
        }

        /// <summary>The firewall rule of a local network share, with the elevated command and the firewall itself swapped out: no UAC here.</summary>
        static void ShareFirewallRules()
        {
            var exe = Paths.Exe;
            File.Delete(Path.Combine(Paths.Usr, "share-firewall-declined.txt"));
            Func<int, FirewallView> net = active => new FirewallView { Active = active };
            Func<int, FirewallRule> own = port => new FirewallRule { Name = "Portly share " + port, Allow = true, Profiles = ShareFirewall.Private, Ports = port.ToString() };
            Check("firewall: the rule is the port, private networks and this subnet",
                ShareFirewall.AddCommand(8900) == "netsh advfirewall firewall add rule name=\"Portly share 8900\" dir=in action=allow protocol=TCP localport=8900 profile=private remoteip=localsubnet", ShareFirewall.AddCommand(8900));
            Check("firewall: no rule on a private network means add it", ShareFirewall.Decide(net(ShareFirewall.Private), 8900, exe) == FirewallNeed.AddRule, "");
            var v = net(ShareFirewall.Private);
            v.Rules.Add(own(8900));
            Check("firewall: the share's rule is enough", ShareFirewall.Decide(v, 8900, exe) == FirewallNeed.None, "");
            Check("firewall: a rule for another port is not", ShareFirewall.Decide(v, 8901, exe) == FirewallNeed.AddRule, "");
            v.Rules[0].Enabled = false;
            Check("firewall: a disabled rule does not count", ShareFirewall.Decide(v, 8900, exe) == FirewallNeed.AddRule, "");
            v = net(ShareFirewall.Private);
            v.Rules.Add(new FirewallRule { Name = "Portly", App = exe.ToUpperInvariant(), Allow = true, Profiles = ShareFirewall.Private });
            Check("firewall: Windows' own allow for this Portly.exe needs no rule", ShareFirewall.Decide(v, 8900, exe) == FirewallNeed.None, "");
            v = net(ShareFirewall.Private);
            v.Rules.Add(new FirewallRule { Name = "Portly", App = @"D:\other\Portly.exe", Allow = true, Profiles = ShareFirewall.Private });
            Check("firewall: an allow for a Portly.exe somewhere else does not help", ShareFirewall.Decide(v, 8900, exe) == FirewallNeed.AddRule, "");
            v = net(ShareFirewall.Private);
            v.Rules.Add(new FirewallRule { Name = "Portly", App = exe, Allow = false, Profiles = ShareFirewall.Private });
            Check("firewall: a block rule for Portly.exe is reported, an allow rule could not beat it", ShareFirewall.Decide(v, 8900, exe) == FirewallNeed.Blocked, "");
            Check("firewall: on a public network a private rule would do nothing", ShareFirewall.Decide(net(ShareFirewall.Public), 8900, exe) == FirewallNeed.NotPrivate, "");
            v = net(ShareFirewall.Public);
            v.Off = ShareFirewall.Public;
            Check("firewall: switched off, nothing is needed", ShareFirewall.Decide(v, 8900, exe) == FirewallNeed.None, "");
            Check("firewall: no firewall to read, nothing is asked", ShareFirewall.Decide(null, 8900, exe) == FirewallNeed.None, "");

            var read = ShareFirewall.Read;
            var elevate = ShareFirewall.Elevate;
            var machine = read();
            Check("firewall: the real firewall can be read without rights", machine != null, "");
            var rules = new FirewallView { Active = ShareFirewall.Private };
            var commands = new List<string>();
            int answer = 0;
            ShareFirewall.Read = () => rules;
            ShareFirewall.Elevate = (file, args) =>
            {
                commands.Add(file + " " + args);
                if (answer != 0) return answer;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(args, @"delete rule name=""Portly share (\d+)"""))
                    rules.Rules.RemoveAll(r => r.Name == "Portly share " + m.Groups[1].Value);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(args, @"add rule name=""Portly share (\d+)"""))
                    rules.Rules.Add(own(int.Parse(m.Groups[1].Value)));
                return 0;
            };
            try
            {
                const int P = 48900, Q = 48901, R = 48902;
                ShareFirewall.Open(P, false);
                Check("firewall: a start adds the rule once", commands.Count == 1 && commands[0].StartsWith("cmd.exe /c ") && commands[0].EndsWith(ShareFirewall.AddCommand(P)) && ShareFirewall.Note(P) == null, string.Join(" | ", commands));
                ShareFirewall.Open(P, false);
                ShareFirewall.Open(P, true);
                Check("firewall: a rule that exists costs no UAC", commands.Count == 1, string.Join(" | ", commands));
                ShareFirewall.Close(P);
                Check("firewall: stopping removes the rule", commands.Count == 2 && commands[1].EndsWith(ShareFirewall.DeleteCommand(P)) && rules.Rules.Count == 0, string.Join(" | ", commands));
                ShareFirewall.Close(P);
                Check("firewall: stopping without a rule costs no UAC", commands.Count == 2, "");

                commands.Clear();
                answer = -2;
                ShareFirewall.Open(P, false);
                Check("firewall: refused UAC leaves a message and no exception", commands.Count == 1 && ShareFirewall.Note(P) != null, "");
                ShareFirewall.Open(P, true);
                ShareFirewall.Open(P, true);
                ShareFirewall.Prepare(new[] { P });
                Check("firewall: after a refusal automatic starts do not ask again", commands.Count == 1 && ShareFirewall.Note(P) != null, string.Join(" | ", commands));
                ShareFirewall.Open(P, false);
                Check("firewall: a start by the user asks again", commands.Count == 2, "");
                answer = 0;
                ShareFirewall.Open(P, false);
                ShareFirewall.Close(P);
                commands.Clear();
                ShareFirewall.Prepare(new[] { P });
                Check("firewall: once granted the refusal is forgotten", commands.Count == 1, string.Join(" | ", commands));
                ShareFirewall.Close(P);

                // left behind by a Portly that was closed while sharing
                commands.Clear();
                rules.Rules.Add(own(Q));
                rules.Rules.Add(own(R));
                ShareFirewall.Prepare(new[] { R, P });
                Check("firewall: at start one UAC prompt adds what is missing and removes what is left over",
                    commands.Count == 1 && commands[0].Contains(ShareFirewall.DeleteCommand(Q)) && commands[0].Contains(ShareFirewall.AddCommand(P)) && !commands[0].Contains(ShareFirewall.DeleteCommand(R)), string.Join(" | ", commands));
                ShareFirewall.Prepare(new[] { R, P });
                Check("firewall: nothing to do at the next start, no UAC", commands.Count == 1, string.Join(" | ", commands));
                commands.Clear();
                answer = -2;
                rules.Rules.Add(own(Q));
                ShareFirewall.Prepare(new[] { R, P });
                ShareFirewall.Prepare(new[] { R, P });
                Check("firewall: a refused clean-up is not asked at every start", commands.Count == 1, string.Join(" | ", commands));
                commands.Clear();
                answer = 0;
                rules.Rules.RemoveAll(r => r.Name.EndsWith(P.ToString()));
                ShareFirewall.Open(P, false);
                Check("firewall: the next prompt takes the left-over rule along", commands.Count == 1 && commands[0].Contains(ShareFirewall.DeleteCommand(Q)) && commands[0].Contains(ShareFirewall.AddCommand(P)), string.Join(" | ", commands));

                rules.Active = ShareFirewall.Public;
                rules.Rules.Clear();
                commands.Clear();
                ShareFirewall.Open(P, false);
                Check("firewall: on a public network no UAC, a message instead", commands.Count == 0 && ShareFirewall.Note(P) != null, "");
                ShareFirewall.Close(P);
            }
            finally
            {
                ShareFirewall.Read = read;
                ShareFirewall.Elevate = elevate;
            }
        }

        /// <summary>A device talking to the proxy.</summary>
        class ShareClient : IDisposable
        {
            readonly TcpClient _c = new TcpClient();
            readonly NetworkStream _s;
            public bool Closed;

            public ShareClient(int port, int receiveBuffer = 0)
            {
                if (receiveBuffer > 0) _c.ReceiveBufferSize = receiveBuffer;
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

            /// <summary>Did the proxy close the connection? Waits for it up to <paramref name="ms"/>.</summary>
            public bool HungUp(int ms)
            {
                if (Closed) return true;
                if (_c.Client.Poll(ms * 1000, SelectMode.SelectRead)) ReadSome(ms);
                return Closed;
            }

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
            public int FloodMb;             // after the answer: this many more MB, sent for as long as they are taken
            public volatile bool Flooded;   // all of them went out

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
                                for (int mb = 0; mb < FloodMb; mb++) Write(s, new string('f', 1024 * 1024));
                                Flooded = FloodMb > 0;
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
