using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>
        /// The dashboard and the web tools answer only to this computer's names (DNS rebinding) and the database can
        /// write files only into its own folder. Runs while Apache, PHP and MySQL are up (see Run()).
        /// </summary>
        static async Task Tests_LocalToolsSecurity(Settings s, string www)
        {
            await LocalToolsConfigChecks(s);
            await MysqlFilesChecks(s, www);

            if (ServiceManager.Web.State == ServiceState.Running) LocalToolsRequests("Apache", s);
            await ServiceManager.SwitchWebServerAsync("nginx");
            if (ServiceManager.Nginx.State == ServiceState.Running) LocalToolsRequests("Nginx", s);
            else Check("LocalTools: Nginx runs with the Host check", false, ServiceManager.Nginx.LastError ?? "");
            await ServiceManager.SwitchWebServerAsync("apache");

            // Caddy (downloads ~40 MB): only with PORTLY_SLOW_TESTS=1, Tests_SitesWeb installs it
            if (Environment.GetEnvironmentVariable("PORTLY_SLOW_TESTS") != "1" || PackageTypes.ActiveDir("caddy") == null) return;
            Features.Set("caddy", true);
            await ServiceManager.SwitchWebServerAsync("caddy");
            if (ServiceManager.Caddy.State == ServiceState.Running) LocalToolsRequests("Caddy", s);
            else Check("LocalTools: Caddy runs with the Host check", false, ServiceManager.Caddy.LastError ?? "");
            await ServiceManager.SwitchWebServerAsync("apache");
            Features.Set("caddy", false);
        }

        /// <summary>The Host rule of the generated configuration of all three web servers, also with LAN access on.</summary>
        static async Task LocalToolsConfigChecks(Settings s)
        {
            var sites = ServiceManager.CurrentSites;
            Func<string, int> phpPort = ServiceManager.Php.PortFor;
            var nginxDir = PackageTypes.ActiveDir("nginx") ?? Paths.Root;
            var apacheDir = PackageTypes.ActiveDir("apache") ?? Paths.Root;
            bool lan = s.AllowLan;
            try
            {
                foreach (var allowLan in new[] { false, true })
                {
                    s.AllowLan = allowLan;
                    var mode = allowLan ? " (LAN access on)" : "";
                    WebConfig.WriteApache(apacheDir, sites, phpPort);
                    WebConfig.WriteNginx(nginxDir, sites, phpPort);
                    CaddyConfig.WriteCaddyfile(sites, phpPort);

                    var httpd = File.ReadAllText(WebConfig.ApacheConf);
                    var apacheAlias = File.ReadAllText(Path.Combine(Paths.Etc, "apache", "alias", "auto.tools.conf"));
                    var nginx = File.ReadAllText(WebConfig.NginxConf);
                    var nginxAlias = File.ReadAllText(Path.Combine(Paths.Etc, "nginx", "alias", "auto.tools.conf"));
                    var caddy = File.ReadAllText(CaddyConfig.Caddyfile);

                    Check("LocalTools: LAN setting decides what the web servers bind to" + mode,
                        allowLan == !Regex.IsMatch(httpd, @"(?m)^Listen 127\.0\.0\.1:" + s.HttpPort + @"\s*$")
                        && allowLan == !nginx.Contains("listen 127.0.0.1:" + s.HttpPort)
                        && allowLan == !caddy.Contains("default_bind 127.0.0.1"));
                    Check("LocalTools: Apache needs a local client and a local name" + mode,
                        Regex.IsMatch(apacheAlias, @"<Location ""/"">\s+AuthMerging And\s+<RequireAll>\s+Require local\s+Require expr ""%\{HTTP_HOST\} =~ m#[^#]+#i""\s+Require expr ""-z %\{req:X-Forwarded-Host\}""\s+</RequireAll>\s+</Location>")
                        && Regex.Matches(httpd, @"apache/alias/\*\.conf").Count == (s.Ssl ? 2 : 1) && httpd.Contains("Require local"), apacheAlias.Length + " bytes");
                    Check("LocalTools: Nginx needs a local client and a local name" + mode,
                        Regex.IsMatch(nginxAlias, @"if \(\$http_host !~\* ""[^""]+""\) \{\s+return 403;\s+\}") && nginx.Contains("nginx/alias/*.conf")
                        && nginx.Contains("allow 127.0.0.1;") && nginx.Contains("deny all;"), nginxAlias.Length + " bytes");
                    Check("LocalTools: Caddy refuses first, and on every name of the HTTPS port" + mode,
                        Regex.IsMatch(caddy, @"route \{\s+@refused not \{\s+remote_ip 127\.0\.0\.1 ::1\s+header_regexp Host ""\(\?i\)[^""]+""\s+\}\s+respond @refused 403\s+#[^@]+@shared header X-Forwarded-Host \*\s+respond @shared 403\s")
                        && caddy.IndexOf("respond @refused 403", StringComparison.Ordinal) < caddy.IndexOf("handle {", StringComparison.Ordinal)
                        && (!s.Ssl || caddy.Contains("https://:" + s.HttpsPort + " {")), caddy.Length + " bytes");

                    foreach (var rule in new[]
                    {
                        Tuple.Create("Apache", Regex.Match(apacheAlias, "m#([^#]+)#i").Groups[1].Value),
                        Tuple.Create("Nginx", Regex.Match(nginxAlias, @"!~\* ""([^""]+)""").Groups[1].Value),
                        Tuple.Create("Caddy", Regex.Match(caddy, @"Host ""\(\?i\)([^""]+)""").Groups[1].Value),
                    })
                    {
                        var rx = new Regex(rule.Item2, RegexOptions.IgnoreCase);
                        var wrong = LocalNames(s).Where(h => !rx.IsMatch(h)).Concat(ForeignNames(s).Where(h => rx.IsMatch(h))).ToList();
                        Check("LocalTools: " + rule.Item1 + " rule tells local names from foreign ones" + mode, rule.Item2 != "" && wrong.Count == 0, rule.Item2 + " wrong: " + string.Join(", ", wrong));
                    }
                }
            }
            finally
            {
                s.AllowLan = lan;
                await ServiceManager.ReloadWebAsync();   // the files as the running server expects them
            }
        }

        static string[] LocalNames(Settings s)
        {
            return new[]
            {
                "localhost", "LOCALHOST", "LocalHost:" + s.HttpPort, "localhost:" + s.HttpsPort, "127.0.0.1", "127.0.0.1:" + s.HttpPort, "127.0.0.1:" + s.HttpsPort,
                "[::1]", "[::1]:" + s.HttpPort, "[::1]:" + s.HttpsPort,
            };
        }

        static string[] ForeignNames(Settings s)
        {
            return new[]
            {
                "evil.example", "EVIL.EXAMPLE:" + s.HttpPort, "localhost.evil.example", "evillocalhost", "127.0.0.1.nip.io", "127.0.0.1.evil.example:" + s.HttpPort,
                "[::1].evil.example", "0.0.0.0", "192.168.1.20", "localhost:1", "127.0.0.1:1",
            };
        }

        /// <summary>Real requests against the running web server, over HTTP and HTTPS.</summary>
        static void LocalToolsRequests(string server, Settings s)
        {
            var paths = new[] { "/" }.Concat(ToolApps.WebTools().Select(t => "/" + t.Item1 + "/")).ToList();
            foreach (var tls in s.Ssl ? new[] { false, true } : new[] { false })
            {
                var port = tls ? s.HttpsPort : s.HttpPort;
                var name = "LocalTools: " + server + (tls ? " https" : " http");
                // [::1] with a port is what a browser sends for http://[::1]:8080/
                var allowed = (from h in LocalNames(s) from p in paths let c = RawStatus(port, tls, h, p) where c < 200 || c >= 400 select h + p + "=" + c).ToList();
                Check(name + ": dashboard and tools answer to this computer's names", allowed.Count == 0, string.Join(", ", allowed));

                var more = paths.Concat(new[] { "/?phpinfo=1", "/index.php", "/data.json", "/missing.php" }).Concat(paths.Where(p => p != "/").Select(p => p.TrimEnd('/'))).ToList();
                var refused = (from h in ForeignNames(s) from p in more let c = RawStatus(port, tls, h, p) where !Refused(c) select h + p + "=" + c).ToList();
                Check(name + ": every other name is refused, on the dashboard and on the tools", refused.Count == 0, string.Join(", ", refused.Take(8)));
                var bare = RawStatus(port, tls, null, "/");
                Check(name + ": a request without a Host header is refused", Refused(bare), bare.ToString());

                // LAN and internet shares connect from 127.0.0.1 and replace Host: a device must not get the tools through them (Apache may keep the site of the TLS name: 404)
                var viaShare = paths.Where(p => p != "/").Select(p => RawStatus(port, tls, "demo.localhost", "http://localhost" + p, null, "X-Forwarded-Host: 192.168.1.20:8901\r\n")).ToList();
                Check(name + ": a request forwarded by a share with a local address in its first line is refused", viaShare.All(c => Refused(c) || c == 404), string.Join(", ", viaShare));

                var page = Get((tls ? "https" : "http") + "://127.0.0.1:" + port + "/phpmyadmin/", "demo.localhost");
                Check(name + ": sites answer as before, without the tools", RawStatus(port, tls, "demo.localhost", "/") == 200 && !page.Contains("phpMyAdmin"), Short(page));
                // Apache's Require local takes all of 127.0.0.0/8; the others name 127.0.0.1 and ::1, so this is a client from elsewhere
                if (server != "Apache")
                {
                    var other = RawStatus(port, tls, "localhost", "/", IPAddress.Parse("127.0.0.3"));
                    Check(name + ": a local name from another address is refused (LAN access)", other == 403, other.ToString());
                }
            }
        }

        /// <summary>400 is the server refusing a Host header that is not a valid name.</summary>
        static bool Refused(int code) { return code == 403 || code == 421 || code == 400; }

        /// <summary>Status code of a hand-made request: full control of the Host line (null = none, then HTTP/1.0).</summary>
        static int RawStatus(int port, bool tls, string host, string path, IPAddress from = null, string headers = "")
        {
            try
            {
                using (var c = from == null ? new TcpClient() : new TcpClient(new IPEndPoint(from, 0)))
                {
                    c.Connect(IPAddress.Loopback, port);
                    c.ReceiveTimeout = 30000;
                    Stream st = c.GetStream();
                    if (tls)
                    {
                        var ssl = new SslStream(st, false, (o, cert, chain, errors) => true);
                        ssl.AuthenticateAsClient(host != null && host.EndsWith(".localhost") ? host : "localhost", null, SslProtocols.Tls12, false);
                        st = ssl;
                    }
                    var head = "GET " + path + (host == null ? " HTTP/1.0\r\n" : " HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n") + headers + "\r\n";
                    var bytes = Encoding.ASCII.GetBytes(head);
                    st.Write(bytes, 0, bytes.Length);
                    var buffer = new byte[64];
                    int got = 0, n;
                    while (got < 12 && (n = st.Read(buffer, got, buffer.Length - got)) > 0) got += n;
                    var m = Regex.Match(Encoding.ASCII.GetString(buffer, 0, got), @"^HTTP/\d\.\d (\d{3})");
                    return m.Success ? int.Parse(m.Groups[1].Value) : -1;
                }
            }
            catch { return -2; }
        }

        /// <summary>SELECT ... INTO OUTFILE only into tmp\mysql-files; the folder comes back by itself; on MySQL and MariaDB.</summary>
        static async Task MysqlFilesChecks(Settings s, string www)
        {
            var dir = Paths.Fwd(Paths.MysqlFiles);
            var rendered = Templates.Render("mysql.ini.tpl", Templates.CommonVars());
            Check("LocalTools: my.ini template has the folder, not an open secure-file-priv", rendered.Contains("secure-file-priv=\"" + dir + "\"") && !rendered.Contains("secure-file-priv=\"\""));
            Check("LocalTools: the file folder is outside the document root", !Paths.MysqlFiles.StartsWith(s.DocumentRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase), dir);

            var engine = s.DbEngine;
            foreach (var e in new[] { "mysql", "mariadb" })
            {
                if (PackageTypes.ActiveDir(e) == null) continue;
                await ServiceManager.MySql.StopAsync();
                s.DbEngine = e; s.Save();
                // an install from before this change: old my.ini on disk, folder missing
                var ini = MySqlService.ConfFile;
                if (File.Exists(ini)) File.WriteAllText(ini, Regex.Replace(File.ReadAllText(ini), "secure-file-priv=.*", "secure-file-priv=\"\""));
                if (Directory.Exists(Paths.MysqlFiles)) Directory.Delete(Paths.MysqlFiles, true);
                await ServiceManager.MySql.StartAsync();
                Check("LocalTools: " + e + " starts with the folder missing and an old my.ini", ServiceManager.MySql.State == ServiceState.Running && Directory.Exists(Paths.MysqlFiles)
                    && File.ReadAllText(ini).Contains("secure-file-priv=\"" + dir + "\""), ServiceManager.MySql.LastError ?? "");

                var inWww = Paths.Fwd(Path.Combine(www, "outfile-test.php"));
                var denied = Database.Sql("SELECT '<?php echo 1;' INTO OUTFILE " + Database.SqlString(inWww) + ";");
                Check("LocalTools: " + e + " refuses to write a file into the web root", !denied.Ok && denied.Output.Contains("secure-file-priv") && !File.Exists(inWww.Replace('/', '\\')), Short(denied.Output));

                var name = dir + "/smoke-" + e + ".txt";
                var written = Database.Sql("SELECT 'portly' INTO OUTFILE " + Database.SqlString(name) + ";");
                Check("LocalTools: " + e + " writes into its own folder", written.Ok && File.Exists(name.Replace('/', '\\')), Short(written.Output));
                try { File.Delete(name.Replace('/', '\\')); } catch { }
            }
            await ServiceManager.MySql.StopAsync();
            s.DbEngine = engine; s.Save();
            await ServiceManager.MySql.StartAsync();
        }
    }
}
