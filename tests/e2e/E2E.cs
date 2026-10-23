using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

// End-to-end check of a copied Portly (own folder, ports 19xxx): sites with different PHP versions over
// Apache / Nginx / Caddy, strict HTTPS validation, real Chrome (headless), Fix SSL, tools, load.
static class E2E
{
    static int ok, fail;
    static readonly List<string> Fails = new List<string>();
    static X509Certificate2 Ca;
    static Settings S { get { return Settings.Current; } }
    const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    static string Shots;

    static void Check(string name, bool good, string detail = "")
    {
        Console.WriteLine((good ? "  PASS " : "  FAIL ") + name + (detail != "" ? "  -> " + detail : ""));
        if (good) ok++; else { fail++; Fails.Add(name + " -> " + detail); }
    }

    static int Main(string[] args)
    {
        try { return Run(args).GetAwaiter().GetResult(); }
        finally { try { ServiceManager.StopAllAsync().Wait(60000); } catch { } }
    }

    // site -> PHP branch
    static readonly Dictionary<string, string> PhpOf = new Dictionary<string, string>
    {
        { "p74", "7.4" }, { "p82", "8.2" }, { "p83", "8.3" }, { "p84", "8.4" }, { "p85", "8.5" },
        { "grp/one", "8.3" }, { "grp/two", "8.5" }, { "lara", "8.4" },
    };

    static async Task<int> Run(string[] args)
    {
        Shots = args.Length > 0 ? args[0] : Path.Combine(Paths.Root, "shots");
        Directory.CreateDirectory(Shots);
        Net.Init();
        Paths.EnsureAll();
        Settings.Load();
        var s = S;
        s.HttpPort = 19080; s.HttpsPort = 19443; s.MysqlPort = 19306; s.PhpBasePort = 19100;
        s.MailpitWebPort = 19025; s.MailpitSmtpPort = 19026; s.RedisPort = 19379;
        s.Tld = "localhost"; s.Ssl = true; s.AutoVhosts = true; s.WebServer = "apache"; s.DbEngine = "mysql"; s.MailTool = "mailpit";
        s.DocumentRoot = Path.Combine(Paths.Root, "www");
        s.ShowWelcome = false; s.FirstRunDone = true;
        s.SetEnabled("web", true); s.SetEnabled("mysql", true); s.SetEnabled("mailpit", true);
        s.SetVersion("mysql", PackageTypes.Installed("mysql").FirstOrDefault() ?? "");
        Features.Set("caddy", true); Features.Set("aliases", true);
        s.Save();
        I18n.Load("en");
        Catalog.LoadLocal();
        Console.WriteLine("Root: " + Paths.Root);

        // ---------- PHP versions: install the missing branches from the catalog ----------
        Console.WriteLine("== PHP versions");
        var folders = new Dictionary<string, string>();
        foreach (var branch in PhpOf.Values.Distinct())
        {
            var have = PackageTypes.Installed("php").FirstOrDefault(f => PackageTypes.Branch(f) == branch);
            if (have == null)
            {
                var e = Catalog.ForType("php").FirstOrDefault(x => x.Branch == branch);
                if (e == null) { Check("catalog has PHP " + branch, false); continue; }
                var sw = Stopwatch.StartNew();
                await Installer.InstallAsync(e, null, CancellationToken.None);
                have = e.Folder;
                Check("install PHP " + e.Version, PackageTypes.Installed("php").Contains(e.Folder), sw.Elapsed.TotalSeconds.ToString("0") + " s");
            }
            folders[branch] = have;
        }
        s.SetVersion("php", folders["8.5"]); s.Save();

        // ---------- sites ----------
        var www = s.DocumentRoot;
        foreach (var site in PhpOf.Keys.Concat(new[] { "broken" }))
        {
            var dir = Path.Combine(www, site.Replace('/', '\\'));
            if (site == "lara") dir = Path.Combine(dir, "public");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.php"), site == "broken" ? "<?php undefined_function_e2e();" : TestPage(site.Replace('/', '_')));
            if (site == "lara") File.WriteAllText(Path.Combine(www, "lara", "artisan"), "");
        }
        Directory.CreateDirectory(Path.Combine(www, "grp"));
        foreach (var kv in PhpOf) Sites.SetPhp(kv.Key, kv.Value == "8.5" ? "" : folders[kv.Value]);
        Sites.SetAliases("p85", new[] { "extra85.localhost", "*.p85.localhost" });
        ServiceManager.RefreshSites();

        // ---------- start ----------
        Console.WriteLine("== start");
        var t0 = Stopwatch.StartNew();
        await ServiceManager.StartAllAsync();
        Check("Apache running", ServiceManager.Web.State == ServiceState.Running, ServiceManager.Web.LastError ?? "");
        Check("PHP pools running", ServiceManager.Php.State == ServiceState.Running, ServiceManager.Php.LastError ?? "");
        Check("MySQL running", ServiceManager.MySql.State == ServiceState.Running, ServiceManager.MySql.LastError ?? "");
        Console.WriteLine("    start all: " + t0.Elapsed.TotalSeconds.ToString("0.0") + " s");
        Ca = Certs.EnsureCa();
        Console.WriteLine("    local CA: " + Ca.Subject + " (trusted by Windows: " + Certs.IsCaTrusted() + ")");

        await SiteChecks("Apache", true);
        await RuntimeChecks();
        await FixSslChecks();
        await LoadCheck("Apache");

        Console.WriteLine("== Nginx");
        await ServiceManager.SwitchWebServerAsync("nginx");
        Check("Nginx running", ServiceManager.Web.State == ServiceState.Running, ServiceManager.Web.LastError ?? "");
        await SiteChecks("Nginx", false);
        await LoadCheck("Nginx");

        Console.WriteLine("== Caddy");
        await ServiceManager.SwitchWebServerAsync("caddy");
        Check("Caddy running", ServiceManager.Web.State == ServiceState.Running, ServiceManager.Web.LastError ?? "");
        await SiteChecks("Caddy", false);
        await LoadCheck("Caddy");

        await ServiceManager.SwitchWebServerAsync("apache");
        Console.WriteLine("== stop");
        await ServiceManager.StopAllAsync();
        await Task.Delay(1500);
        foreach (var port in new[] { s.HttpPort, s.HttpsPort, s.MysqlPort })
            Check("port " + port + " free after stop", !ProcessUtil.IsPortInUse(port));

        Console.WriteLine();
        Console.WriteLine("PASSED " + ok + "  FAILED " + fail);
        foreach (var f in Fails) Console.WriteLine("  ! " + f);
        return fail == 0 ? 0 : 1;
    }

    static string TestPage(string id)
    {
        return @"<?php
header('Content-Type: text/html; charset=utf-8');
if (isset($_GET['light'])) { echo '{""id"":""" + id + @""",""php"":""' . PHP_VERSION . '""}'; exit; }   // load test: no self-calls

$r = array('id' => '" + id + @"', 'php' => PHP_VERSION, 'sapi' => php_sapi_name(), 'host' => isset($_SERVER['HTTP_HOST']) ? $_SERVER['HTTP_HOST'] : '',
  'https' => !empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off',
  'upload_progress' => ini_get('session.upload_progress.enabled'), 'url_include' => ini_get('allow_url_include'), 'ext' => array());
foreach (array('mysqli','pdo_mysql','curl','openssl','mbstring','intl','gd','fileinfo','zip','sodium','Zend OPcache') as $e) $r['ext'][$e] = extension_loaded($e);
try {
  $pdo = new PDO('mysql:host=127.0.0.1;port=" + S.MysqlPort + @";charset=utf8mb4', 'root', '');
  $pdo->exec('CREATE DATABASE IF NOT EXISTS e2e_" + id + @"');
  $pdo->exec('CREATE TABLE IF NOT EXISTS e2e_" + id + @".t (id INT AUTO_INCREMENT PRIMARY KEY, v VARCHAR(50))');
  $pdo->exec(""INSERT INTO e2e_" + id + @".t (v) VALUES ('çğıöşü')"");
  $r['db'] = (int)$pdo->query('SELECT COUNT(*) FROM e2e_" + id + @".t')->fetchColumn();
  $r['dbtr'] = $pdo->query('SELECT v FROM e2e_" + id + @".t LIMIT 1')->fetchColumn();
} catch (Exception $ex) { $r['db'] = 'ERR ' . $ex->getMessage(); }
session_start(); $_SESSION['n'] = isset($_SESSION['n']) ? $_SESSION['n'] + 1 : 1; $r['session'] = $_SESSION['n'];
$c = curl_init('https://localhost:" + S.HttpsPort + @"/'); curl_setopt($c, CURLOPT_RETURNTRANSFER, true); curl_setopt($c, CURLOPT_TIMEOUT, 10);
$body = curl_exec($c); $r['curl_https'] = $body !== false ? 'ok' : ('ERR ' . curl_error($c)); curl_close($c);
$r['tmp'] = file_put_contents(sys_get_temp_dir() . '/e2e_" + id + @".txt', 'x') === 1;
echo '<h1>E2E ' . htmlspecialchars($r['id']) . '</h1><pre id=""data"">' . json_encode($r) . '</pre>';
";
    }

    static string Host(string site) { return string.Join(".", site.Split('/').Reverse()) + ".localhost"; }

    static async Task SiteChecks(string server, bool chromeShots)
    {
        Console.WriteLine("== " + server + ": sites");
        foreach (var kv in PhpOf)
        {
            var host = Host(kv.Key);
            string cert;
            var res = Fetch(true, host, "/", out cert);
            var body = res.Item2;
            Check(server + " https " + host + " certificate", cert == "ok", cert);
            Check(server + " https " + host + " 200", res.Item1 == 200, res.Item1 + " " + Short(body));
            Check(server + " " + host + " runs PHP " + kv.Value, body.Contains("\"php\":\"" + kv.Value + "."), Field(body, "php"));
            Check(server + " " + host + " sees https", body.Contains("\"https\":true"));
            Check(server + " " + host + " upload_progress off / url_include off", (body.Contains("\"upload_progress\":\"0\"") || body.Contains("\"upload_progress\":\"\"") || body.Contains("\"upload_progress\":false")) && !body.Contains("\"url_include\":\"1\""), Field(body, "upload_progress"));
            Check(server + " " + host + " extensions (mysqli pdo_mysql curl openssl mbstring intl gd opcache)", new[] { "mysqli", "pdo_mysql", "curl", "openssl", "mbstring", "intl", "gd", "Zend OPcache" }.All(e => body.Contains("\"" + e + "\":true")), Missing(body));
            Check(server + " " + host + " MySQL write/read (UTF-8)", Regex(body, "\"db\":\\d+") && body.Contains("\\u00e7\\u011f\\u0131\\u00f6\\u015f\\u00fc"), Field(body, "db"));
            Check(server + " " + host + " PHP calls https://localhost (CA bundle)", body.Contains("\"curl_https\":\"ok\""), Field(body, "curl_https"));
            Check(server + " " + host + " session + temp dir", body.Contains("\"session\":1") && body.Contains("\"tmp\":true"));
            var http = Fetch(false, host, "/", out cert);
            Check(server + " http " + host + " 200", http.Item1 == 200, http.Item1.ToString());
            if (File.Exists(Chrome))
            {
                var shot = chromeShots && (kv.Key == "p74" || kv.Key == "p85" || kv.Key == "grp/one") ? Path.Combine(Shots, server + "-" + kv.Key.Replace('/', '_') + ".png") : null;
                var dom = await ChromeAsync("https://" + host + ":" + S.HttpsPort + "/", shot);
                Check(server + " Chrome opens https://" + host, dom.Contains("E2E ") && dom.Contains(kv.Value + "."), Short(dom));
            }
        }
        // aliases (named + wildcard) of p85: same site, valid certificate for those names
        foreach (var alias in new[] { "extra85.localhost", "shop.p85.localhost" })
        {
            string cert;
            var r = Fetch(true, alias, "/", out cert);
            Check(server + " alias " + alias + " certificate + same site", cert == "ok" && r.Item2.Contains("\"id\":\"p85\""), cert + " " + Short(r.Item2));
        }
        // a PHP fatal error answers 500 quickly instead of hanging
        string c2;
        var sw = Stopwatch.StartNew();
        var broken = Fetch(true, "broken.localhost", "/", out c2);
        // display_errors is on for development: PHP shows the error (status stays 200), quickly, without hanging
        Check(server + " PHP fatal error shown, fast", broken.Item2.Contains("undefined_function_e2e") && sw.ElapsedMilliseconds < 5000, broken.Item1 + " in " + sw.ElapsedMilliseconds + " ms");
        // tools on localhost
        var pma = Fetch(true, "localhost", "/phpmyadmin/", out c2);
        Check(server + " phpMyAdmin", pma.Item1 == 200 && pma.Item2.Contains("phpMyAdmin"), pma.Item1 + " " + Short(pma.Item2));
        var adm = Fetch(true, "localhost", "/adminer/", out c2);
        Check(server + " Adminer", adm.Item1 == 200 && adm.Item2.ToLowerInvariant().Contains("adminer"), adm.Item1.ToString());
        var dash = Fetch(true, "localhost", "/", out c2);
        Check(server + " dashboard lists the sites", dash.Item1 == 200 && dash.Item2.Contains("p74.localhost") && dash.Item2.Contains("one.grp.localhost"), dash.Item1.ToString());
    }

    static async Task RuntimeChecks()
    {
        Console.WriteLine("== change a site's PHP while running");
        var f85 = PackageTypes.Installed("php").First(f => PackageTypes.Branch(f) == "8.5");
        var f84 = PackageTypes.Installed("php").First(f => PackageTypes.Branch(f) == "8.4");
        Sites.SetPhp("p84", f85);
        await ServiceManager.ReloadWebAsync();
        string c;
        Check("p84 now runs PHP 8.5", Fetch(true, "p84.localhost", "/", out c).Item2.Contains("\"php\":\"8.5."));
        Sites.SetPhp("p84", f84);
        await ServiceManager.ReloadWebAsync();
        Check("p84 back on PHP 8.4", Fetch(true, "p84.localhost", "/", out c).Item2.Contains("\"php\":\"8.4."));
    }

    static async Task FixSslChecks()
    {
        Console.WriteLine("== Fix SSL");
        foreach (var ext in new[] { ".crt", ".key", ".names" }) File.Delete(Path.Combine(Certs.SitesDir, "p82.localhost" + ext));
        File.WriteAllText(Path.Combine(Certs.SitesDir, "p83.localhost.names"), "p83.localhost");   // pretend the names changed
        var before = SslRepair.Check(ServiceManager.CurrentSites);
        var probs = before.Sites.Where(x => x.Problem != null).Select(x => x.Site.Host + "=" + x.Problem).ToList();
        Check("Fix SSL finds the two broken certificates", probs.Contains("p82.localhost=missing") && probs.Contains("p83.localhost=names"), string.Join(", ", probs));
        var after = await SslRepair.FixAsync(ServiceManager.CurrentSites, false);
        Check("Fix SSL renews them", after.Renewed >= 2, after.Renewed + " renewed");
        var again = SslRepair.Check(ServiceManager.CurrentSites);
        Check("no certificate problems left", again.Sites.All(x => x.Problem == null), string.Join(", ", again.Sites.Where(x => x.Problem != null).Select(x => x.Site.Host + "=" + x.Problem)));
        string c;
        Fetch(true, "p82.localhost", "/", out c);
        Check("p82 https valid after Fix SSL", c == "ok", c);
    }

    static async Task LoadCheck(string server)
    {
        var hosts = PhpOf.Keys.Select(Host).ToArray();
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 80).Select(i => Task.Run(() => { string c; return Fetch(i % 2 == 0, hosts[i % hosts.Length], "/?light=1", out c).Item1; })).ToArray();
        var codes = await Task.WhenAll(tasks);
        Check(server + " 80 parallel requests over 8 sites all 200", codes.All(x => x == 200), string.Join(",", codes.GroupBy(x => x).Select(g => g.Key + "x" + g.Count())) + " in " + sw.ElapsedMilliseconds + " ms");
    }

    // ---------------- HTTP(S) with strict certificate checks (chain to Portly's CA + host name) ----------------
    static Tuple<int, string> Fetch(bool tls, string host, string path, out string certResult)
    {
        certResult = tls ? "not checked" : "";
        try
        {
            using (var tcp = new TcpClient())
            {
                tcp.ReceiveTimeout = 30000; tcp.SendTimeout = 30000;
                tcp.Connect("127.0.0.1", tls ? S.HttpsPort : S.HttpPort);
                Stream st = tcp.GetStream();
                string err = null;
                if (tls)
                {
                    var ssl = new SslStream(st, false, (o, cert, chain, errors) =>
                    {
                        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) { err = "name mismatch"; return false; }
                        if (cert == null) { err = "no certificate"; return false; }
                        var c2 = new X509Certificate2(cert);
                        using (var ch = new X509Chain())
                        {
                            ch.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                            ch.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                            ch.ChainPolicy.ExtraStore.Add(Ca);
                            if (!ch.Build(c2)) { err = "chain: " + string.Join(",", ch.ChainStatus.Select(x => x.Status)); return false; }
                            var root = ch.ChainElements[ch.ChainElements.Count - 1].Certificate;
                            if (root.Thumbprint != Ca.Thumbprint) { err = "not signed by Portly's CA"; return false; }
                            var bad = ch.ChainStatus.Where(x => x.Status != X509ChainStatusFlags.UntrustedRoot && x.Status != X509ChainStatusFlags.NoError).ToList();
                            if (bad.Count > 0) { err = string.Join(",", bad.Select(x => x.Status)); return false; }
                        }
                        if (c2.NotAfter < DateTime.Now) { err = "expired"; return false; }
                        err = "ok";
                        return true;
                    });
                    ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                    certResult = err ?? "?";
                    st = ssl;
                }
                var req = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\nUser-Agent: PortlyE2E\r\n\r\n");
                st.Write(req, 0, req.Length);
                var ms = new MemoryStream();
                st.CopyTo(ms);
                var raw = ms.ToArray();
                var text = Encoding.UTF8.GetString(raw);
                int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var head = split < 0 ? text : text.Substring(0, split);
                var body = split < 0 ? "" : text.Substring(split + 4);
                if (head.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0) body = Dechunk(body);
                int code = 0;
                var first = head.Split('\n')[0].Split(' ');
                if (first.Length > 1) int.TryParse(first[1], out code);
                return Tuple.Create(code, body);
            }
        }
        catch (Exception ex)
        {
            if (tls && certResult == "not checked") certResult = "TLS failed: " + ex.Message;
            return Tuple.Create(-1, ex.Message);
        }
    }

    static string Dechunk(string s)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            int nl = s.IndexOf("\r\n", i, StringComparison.Ordinal);
            if (nl < 0) break;
            int len;
            if (!int.TryParse(s.Substring(i, nl - i).Split(';')[0].Trim(), System.Globalization.NumberStyles.HexNumber, null, out len) || len == 0) break;
            // chunk sizes are bytes; the body is mostly ASCII JSON, good enough for the checks
            int start = nl + 2;
            int take = Math.Min(len, s.Length - start);
            var piece = s.Substring(start, take);
            while (Encoding.UTF8.GetByteCount(piece) > len && piece.Length > 0) piece = piece.Substring(0, piece.Length - 1);
            sb.Append(piece);
            i = start + piece.Length + 2;
        }
        return sb.ToString();
    }

    // ---------------- Chrome (headless): opens the page like a user, Portly's CA accepted via its key ----------------
    static async Task<string> ChromeAsync(string url, string screenshot)
    {
        var profile = Path.Combine(Paths.Tmp, "chrome-e2e");
        var spki = Convert.ToBase64String(SHA256.Create().ComputeHash(Spki(Ca.RawData)));
        var common = "--headless=new --disable-gpu --no-first-run --no-default-browser-check --user-data-dir=\"" + profile + "\" " +
                     "--host-resolver-rules=\"MAP *.localhost 127.0.0.1\" --ignore-certificate-errors-spki-list=" + spki + " ";
        var dom = await Task.Run(() => ProcessUtil.Run(Chrome, common + "--dump-dom \"" + url + "\"", Paths.Tmp, null, 60000).Output);
        if (screenshot != null)
            await Task.Run(() => ProcessUtil.Run(Chrome, common + "--window-size=1100,700 --screenshot=\"" + screenshot + "\" \"" + url + "\"", Paths.Tmp, null, 60000));
        return dom;
    }

    /// <summary>SubjectPublicKeyInfo DER of a certificate (TBSCertificate field 7).</summary>
    static byte[] Spki(byte[] cert)
    {
        int p = 0;
        ReadHeader(cert, ref p);                 // Certificate SEQUENCE
        ReadHeader(cert, ref p);                 // TBSCertificate SEQUENCE
        if (cert[p] == 0xA0) Skip(cert, ref p);  // [0] version
        for (int i = 0; i < 5; i++) Skip(cert, ref p);   // serial, signature, issuer, validity, subject
        int start = p;
        Skip(cert, ref p);
        var r = new byte[p - start];
        Array.Copy(cert, start, r, 0, r.Length);
        return r;
    }

    static int ReadHeader(byte[] b, ref int p)
    {
        p++;
        int len = b[p++];
        if ((len & 0x80) != 0) { int n = len & 0x7f; len = 0; for (int i = 0; i < n; i++) len = (len << 8) | b[p++]; }
        return len;
    }

    static void Skip(byte[] b, ref int p) { int len = ReadHeader(b, ref p); p += len; }

    static string Field(string body, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + name + "\":(\"[^\"]*\"|[^,}]*)");
        return m.Success ? m.Groups[1].Value : Short(body);
    }

    static bool Regex(string body, string rx) { return System.Text.RegularExpressions.Regex.IsMatch(body, rx); }

    static string Missing(string body)
    {
        var miss = new[] { "mysqli", "pdo_mysql", "curl", "openssl", "mbstring", "intl", "gd", "Zend OPcache" }.Where(e => !body.Contains("\"" + e + "\":true"));
        return "missing: " + string.Join(" ", miss);
    }

    static string Short(string s) { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length > 160 ? s.Substring(0, 160) : s; }
}
