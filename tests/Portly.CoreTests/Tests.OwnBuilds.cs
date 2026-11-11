using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.CoreTests
{
    /// <summary>
    /// Portly's own builds on macOS / Linux (nginx, Apache httpd, memcached, PHP with Xdebug; tools/own-builds, workflow v2-builds.yml).
    /// Always: layout, generated configuration and Xdebug switch checks with fake package folders. With PORTLY_OWN_BUILDS=&lt;folder
    /// with the tar.gz artifacts&gt;: the real packages are installed and started one at a time on high ports (PORTLY_E2E=1
    /// E2EInstall first gives the catalog's static PHP, which the web servers then run).
    /// </summary>
    static partial class Program
    {
        const string FakeApache = "httpd-0.0.1-portlytest";
        const string FakeNginx = "nginx-0.0.1-portlytest";
        const string FakeXdebugPhp = "php-0.0.2-portlytest";

        static void Suite_OwnBuilds()
        {
            OwnLayoutChecks();
            OwnConfigChecks();
            OwnXdebugChecks();
            var dir = Environment.GetEnvironmentVariable("PORTLY_OWN_BUILDS");
            if (!string.IsNullOrEmpty(dir)) OwnEndToEnd(dir);
            else Console.WriteLine("  (own builds end-to-end skipped: set PORTLY_OWN_BUILDS to the folder with the tar.gz artifacts)");
        }

        // ------------------------------------------------------------------ package layout

        static void OwnLayoutChecks()
        {
            if (OS.IsWindows)
            {
                Check("layout (Windows): nginx.exe, bin\\httpd.exe, bin\\memcached.exe",
                    PackageTypes.Get("nginx").DetectFile == "nginx.exe" && PackageTypes.Get("apache").DetectFile == @"bin\httpd.exe" && PackageTypes.Get("memcached").DetectFile == @"bin\memcached.exe");
                Check("layout (Windows): memcached program in bin", PackageTypes.Exe("memcached", "m", "memcached") == Path.Combine(Paths.BinOf("memcached"), "m", "bin", "memcached.exe"));
                return;
            }
            Check("layout: nginx at the folder root", PackageTypes.Get("nginx").DetectFile == "nginx" && PackageTypes.ExeDir("nginx") == "");
            Check("layout: Apache in bin/", PackageTypes.Get("apache").DetectFile == "bin/httpd" && PackageTypes.ExeDir("apache") == "bin");
            Check("layout: memcached at the folder root", PackageTypes.Get("memcached").DetectFile == "memcached" && PackageTypes.Exe("memcached", "m", "memcached") == Path.Combine(Paths.BinOf("memcached"), "m", "memcached"));
            Check("memcached arguments: this computer only, no UDP, 64 MB", MemcachedService.Args(11211, false, false) == "-p 11211 -U 0 -l 127.0.0.1 -m 64");
            Check("memcached arguments: LAN and root", MemcachedService.Args(11300, true, true) == "-p 11300 -U 0 -l 0.0.0.0 -m 64 -u root");
        }

        // ------------------------------------------------------------------ configuration

        static void OwnConfigChecks()
        {
            ProvideTemplates();
            Paths.EnsureAll();
            var s = Settings.Current;
            int oldHttp = s.HttpPort, oldHttps = s.HttpsPort, oldPhp = s.PhpBasePort;
            bool oldSsl = s.Ssl;
            var oldTld = s.Tld;
            var apacheDir = Path.Combine(Paths.BinOf("apache"), FakeApache);
            var nginxDir = Path.Combine(Paths.BinOf("nginx"), FakeNginx);
            var fakePhp = Path.Combine(Paths.BinOf("php"), FakePhp);
            var site = Path.Combine(Paths.Www, "ownb");
            try
            {
                s.HttpPort = 18080; s.HttpsPort = 18443; s.PhpBasePort = 19100; s.Tld = "localhost";
                Touch(Path.Combine(fakePhp, "php-cgi.exe")); Touch(Path.Combine(fakePhp, "php-fpm")); Touch(Path.Combine(fakePhp, "php"));
                Touch(Path.Combine(apacheDir, "bin", OS.Exe("httpd")));
                foreach (var m in new[] { "ssl", "proxy", "proxy_fcgi", "rewrite", "headers", "unixd", "http2" }) Touch(Path.Combine(apacheDir, "modules", "mod_" + m + ".so"));
                Touch(Path.Combine(apacheDir, "conf", "mime.types"));
                Touch(Path.Combine(nginxDir, OS.Exe("nginx"))); Touch(Path.Combine(nginxDir, "conf", "fastcgi_params")); Touch(Path.Combine(nginxDir, "conf", "mime.types"));
                Directory.CreateDirectory(site);
                File.WriteAllText(Path.Combine(site, "index.php"), "<?php echo 1;");
                var sites = Sites.Scan();
                var ownb = sites.FirstOrDefault(x => x.Valid && x.Name == "ownb");
                Check("own config: the test project is found", ownb != null);
                if (ownb == null) return;
                var tmp = Paths.Fwd(Paths.Tmp);

                // ---- Apache
                s.Ssl = false;
                WebConfig.WriteApache(apacheDir, sites, f => 19100);
                var conf = File.ReadAllText(WebConfig.ApacheConf);
                var siteConf = File.ReadAllText(Path.Combine(Paths.Etc, "apache", "sites", "auto." + ownb.Host + ".conf"));
                if (OS.IsWindows)
                {
                    Check("Apache config (Windows): the Windows template is used", conf.Contains("ThreadsPerChild") && conf.Contains("AcceptFilter") && !conf.Contains("-DFOREGROUND"));
                }
                else
                {
                    Check("Apache config: server root is the package folder, pid file in tmp", conf.Contains("Define SRVROOT \"" + Paths.Fwd(apacheDir) + "\"") && conf.Contains("PidFile \"" + tmp + "/httpd.pid\""));
                    Check("Apache config: listens on 127.0.0.1 with the HTTP port", Regex.IsMatch(conf, @"^Listen 127\.0\.0\.1:18080\s*$", RegexOptions.Multiline));
                    Check("Apache config: modules are loaded relative to the server root", Regex.IsMatch(conf, @"^LoadModule proxy_fcgi_module modules/mod_proxy_fcgi\.so\s*$", RegexOptions.Multiline) &&
                          Regex.IsMatch(conf, @"^LoadModule ssl_module modules/mod_ssl\.so\s*$", RegexOptions.Multiline) && Regex.IsMatch(conf, @"^LoadModule rewrite_module modules/mod_rewrite\.so\s*$", RegexOptions.Multiline));
                    Check("Apache config: optional modules of the package (unixd, http2) are loaded when present", conf.Contains("LoadModule unixd_module modules/mod_unixd.so") && conf.Contains("LoadModule http2_module modules/mod_http2.so") && conf.Contains("Protocols h2 http/1.1"));
                    Check("Apache config: PHP through php-fpm on the PHP port", conf.Contains("ProxyFCGIBackendType FPM") && conf.Contains("SetHandler \"proxy:fcgi://127.0.0.1:19100/\""));
                    Check("Apache config: event MPM sizes, no Windows settings", conf.Contains("mpm_event_module") && !conf.Contains("ThreadStackSize") && !conf.Contains("AcceptFilter") && !conf.Contains("ProxyFCGISetEnvIf"));
                    Check("Apache config: no Windows paths, LF only", !HasWindowsPath(conf) && !conf.Contains("\r"), WebConfig.ApacheConf);
                    Check("Apache site: document root, name and PHP handler", siteConf.Contains("DocumentRoot \"" + Paths.Fwd(ownb.DocRoot) + "\"") && siteConf.Contains("ServerName " + ownb.Host) && siteConf.Contains("proxy:fcgi://127.0.0.1:19100/"));
                    Check("Apache site: no Windows paths, LF only", !HasWindowsPath(siteConf) && !siteConf.Contains("\r"));
                    File.Delete(Path.Combine(apacheDir, "modules", "mod_http2.so"));
                    WebConfig.WriteApache(apacheDir, sites, f => 19100);
                    conf = File.ReadAllText(WebConfig.ApacheConf);
                    Check("Apache config: no mod_http2 in the package -> not loaded, no Protocols line", !conf.Contains("http2_module") && !Regex.IsMatch(conf, @"^Protocols ", RegexOptions.Multiline));
                    s.Ssl = true;
                    WebConfig.WriteApache(apacheDir, sites, f => 19100);
                    conf = File.ReadAllText(WebConfig.ApacheConf);
                    if (Regex.IsMatch(conf, @"^Listen 127\.0\.0\.1:18443\s*$", RegexOptions.Multiline))
                    {
                        siteConf = File.ReadAllText(Path.Combine(Paths.Etc, "apache", "sites", "auto." + ownb.Host + ".conf"));
                        Check("Apache config: HTTPS on its port with Portly's certificate files", conf.Contains("SSLEngine on") && Regex.IsMatch(conf, @"SSLCertificateFile ""[^""]+""") && siteConf.Contains("<VirtualHost *:18443>"));
                        Check("Apache config: SSL session cache in tmp", conf.Contains("SSLSessionCache \"shmcb:" + tmp + "/ssl_scache"));
                    }
                    else Console.WriteLine("  (no HTTPS block: Portly's certificate could not be made here)");
                }

                // ---- Nginx
                s.Ssl = false;
                WebConfig.WriteNginx(nginxDir, sites, f => 19100);
                conf = File.ReadAllText(WebConfig.NginxConf);
                siteConf = File.ReadAllText(Path.Combine(Paths.Etc, "nginx", "sites", "auto." + ownb.Host + ".conf"));
                if (OS.IsWindows)
                {
                    Check("Nginx config (Windows): the Windows template is used (no daemon line)", !conf.Contains("daemon off"));
                }
                else
                {
                    Check("Nginx config: foreground, pid in tmp, log in logs", conf.Contains("daemon off;") && conf.Contains("pid        \"" + tmp + "/nginx.pid\"") && conf.Contains(Paths.Fwd(Paths.Logs) + "/nginx-error.log"));
                    Check("Nginx config: temp files in Portly's tmp (the package folder stays clean)", conf.Contains("client_body_temp_path \"" + tmp + "/nginx/client_body\"") && conf.Contains("fastcgi_temp_path     \"" + tmp + "/nginx/fastcgi\"") && Directory.Exists(Path.Combine(Paths.Tmp, "nginx")));
                    Check("Nginx config: mime.types of the package, default server on 127.0.0.1", conf.Contains("include       \"" + Paths.Fwd(nginxDir) + "/conf/mime.types\"") && conf.Contains("listen 127.0.0.1:18080 default_server;"));
                    Check("Nginx config: PHP through php-fpm with the package's fastcgi_params", conf.Contains("fastcgi_pass 127.0.0.1:19100;") && conf.Contains("include \"" + Paths.Fwd(nginxDir) + "/conf/fastcgi_params\";"));
                    Check("Nginx config: no Windows paths, LF only", !HasWindowsPath(conf) && !conf.Contains("\r"), WebConfig.NginxConf);
                    Check("Nginx site: server name, root and PHP", siteConf.Contains("server_name " + ownb.Host) && siteConf.Contains("root \"" + Paths.Fwd(ownb.DocRoot) + "\";") && siteConf.Contains("fastcgi_pass 127.0.0.1:19100;"));
                    Check("Nginx site: no Windows paths, LF only", !HasWindowsPath(siteConf) && !siteConf.Contains("\r"));
                    s.Ssl = true;
                    WebConfig.WriteNginx(nginxDir, sites, f => 19100);
                    conf = File.ReadAllText(WebConfig.NginxConf);
                    if (conf.Contains("listen 127.0.0.1:18443 ssl default_server;"))
                        Check("Nginx config: HTTPS with Portly's certificate files", Regex.IsMatch(conf, @"ssl_certificate ""[^""]+"";") && Regex.IsMatch(conf, @"ssl_certificate_key ""[^""]+"";") && conf.Contains("ssl_protocols TLSv1.2 TLSv1.3;"));
                    else Console.WriteLine("  (no HTTPS block: Portly's certificate could not be made here)");
                }
            }
            finally
            {
                s.HttpPort = oldHttp; s.HttpsPort = oldHttps; s.PhpBasePort = oldPhp; s.Ssl = oldSsl; s.Tld = oldTld;
                CleanDir(apacheDir); CleanDir(nginxDir); CleanDir(fakePhp); CleanDir(site);
            }
        }

        // ------------------------------------------------------------------ Xdebug as a loadable extension

        static string XdebugLines(string ini)
        {
            return string.Join("|", File.ReadAllLines(ini).Where(l => Regex.IsMatch(l, @"^\s*;?\s*zend_extension\s*=.*xdebug", RegexOptions.IgnoreCase)));
        }

        static void OwnXdebugChecks()
        {
            if (OS.IsWindows) return;
            var dir = Path.Combine(Paths.BinOf("php"), FakeXdebugPhp);
            try
            {
                Touch(Path.Combine(dir, "php-fpm")); Touch(Path.Combine(dir, "php"));
                PhpConfig.Ensure(dir);
                var ini = PhpConfig.IniPath(dir);
                Check("xdebug: not available in a PHP folder without xdebug.so (static builds)", !Xdebug.IsAvailable(dir) && !Xdebug.IsEnabled(dir) && Xdebug.SoPath(dir) == null);
                bool threw = false;
                try { Xdebug.EnableAsync(dir).GetAwaiter().GetResult(); } catch (Exception ex) { threw = ex.Message.Length > 10; }
                Check("xdebug: enabling it there reports 'not available' and writes nothing", threw && XdebugLines(ini) == "");

                Touch(Path.Combine(dir, "ext", "xdebug.so"));
                var so = Paths.Fwd(Path.Combine(dir, "ext", "xdebug.so"));
                Check("xdebug: available when the PHP folder has ext/xdebug.so", Xdebug.IsAvailable(dir) && Xdebug.SoPath(dir) == Path.Combine(dir, "ext", "xdebug.so") && !Xdebug.IsEnabled(dir));
                Xdebug.EnableAsync(dir).GetAwaiter().GetResult();
                var text = File.ReadAllText(ini);
                Check("xdebug: zend_extension with the full path, mode / port / log settings", XdebugLines(ini) == "zend_extension=\"" + so + "\"" && text.Contains("xdebug.mode = debug,develop") && text.Contains("xdebug.client_port = 9003") && text.Contains("xdebug.log = "));
                Check("xdebug: switched on", Xdebug.IsEnabled(dir));
                Xdebug.EnableAsync(dir).GetAwaiter().GetResult();
                Check("xdebug: enabling twice keeps one line", XdebugLines(ini).Split('|').Length == 1);

                // Portly folder moved: PhpConfig.Ensure (every PHP start) points the line at the new place
                File.WriteAllText(ini, File.ReadAllText(ini).Replace("zend_extension=\"" + so + "\"", "zend_extension=\"/old/place/php/ext/xdebug.so\""));
                PhpConfig.Ensure(dir);
                Check("xdebug: the path follows the folder (PhpConfig.Ensure)", XdebugLines(ini) == "zend_extension=\"" + so + "\"", XdebugLines(ini));

                Xdebug.Disable(dir);
                Check("xdebug: switched off keeps the line commented", !Xdebug.IsEnabled(dir) && XdebugLines(ini) == ";zend_extension=\"" + so + "\"", XdebugLines(ini));
                PhpConfig.Ensure(dir);
                Check("xdebug: PhpConfig.Ensure does not switch it on again", !Xdebug.IsEnabled(dir) && XdebugLines(ini) == ";zend_extension=\"" + so + "\"");
                Xdebug.EnableAsync(dir).GetAwaiter().GetResult();
                Check("xdebug: switched on again from the commented line", Xdebug.IsEnabled(dir) && XdebugLines(ini) == "zend_extension=\"" + so + "\"");
            }
            finally { CleanDir(dir); }
        }

        // ------------------------------------------------------------------ real packages (PORTLY_OWN_BUILDS)

        static string OwnPackage(string dir, string name)
        {
            var pattern = name + "-*-" + OS.Name + "-" + OS.Arch + ".tar.gz";
            return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).OrderByDescending(f => PackageTypes.ParseVersion(Path.GetFileName(f))).FirstOrDefault();
        }

        /// <summary>Installs one tar.gz of the own builds like "Install from file" does. Returns the version folder, or null.</summary>
        static string OwnInstall(string dir, string name, string type)
        {
            var file = OwnPackage(dir, name);
            if (file == null) { Check("artifact " + name + " for " + OS.Name + "-" + OS.Arch + " found", false, dir); return null; }
            var folder = Path.GetFileName(file).Replace(".tar.gz", "");
            try
            {
                if (!PackageTypes.Installed(type).Contains(folder)) folder = Installer.InstallLocal(type, file);
                Check("installed " + type + " from " + Path.GetFileName(file) + " (" + (new FileInfo(file).Length / 1024 / 1024) + " MB)", PackageTypes.Installed(type).Contains(folder), folder);
                return folder;
            }
            catch (Exception ex) { Check("installed " + type + " from " + Path.GetFileName(file), false, ex.GetType().Name + ": " + ex.Message); return null; }
        }

        static void OwnEndToEnd(string artifacts)
        {
            if (OS.IsWindows) { Console.WriteLine("  (own builds end-to-end: macOS / Linux only)"); return; }
            Console.WriteLine("  -- own builds: install, start, answer, stop (high ports)");
            Net.Init();
            var s = Settings.Current;
            var nginx = OwnInstall(artifacts, "nginx", "nginx");
            var httpd = OwnInstall(artifacts, "httpd", "apache");
            var memcached = OwnInstall(artifacts, "memcached", "memcached");
            var ownPhp = OwnInstall(artifacts, "php", "php");
            var staticPhp = PackageTypes.Installed("php").FirstOrDefault(f => f.EndsWith("-static"));
            if (staticPhp == null) Console.WriteLine("  (no static-php package installed: run E2EInstall first; the web servers use the own PHP build instead)");
            var phpForWeb = staticPhp ?? ownPhp;

            // the programs themselves, run from the Portly folder
            if (nginx != null) { var r = ProcessUtil.Run(PackageTypes.Exe("nginx", nginx, "nginx"), "-v", null, null, 30000); Check("nginx runs from its Portly folder", r.Output.Contains("nginx/"), r.Output); }
            if (httpd != null) { var r = ProcessUtil.Run(PackageTypes.Exe("apache", httpd, "httpd"), "-v", null, null, 30000); Check("httpd runs from its Portly folder", r.Output.Contains("Apache/"), r.Output); }
            if (memcached != null) { var r = ProcessUtil.Run(PackageTypes.Exe("memcached", memcached, "memcached"), "-V", null, null, 30000); Check("memcached runs from its Portly folder", r.Output.StartsWith("memcached "), r.Output); }
            if (ownPhp != null) OwnPhpXdebug(ownPhp);

            Paths.EnsureAll();
            ProvideTemplates();
            int oldHttp = s.HttpPort, oldHttps = s.HttpsPort, oldPhp = s.PhpBasePort, oldMc = s.MemcachedPort;
            bool oldSsl = s.Ssl;
            var oldTld = s.Tld; var oldServer = s.WebServer; var oldPhpVersion = s.GetVersion("php");
            var before = LeftoverProcesses();
            try
            {
                s.HttpPort = 18080; s.HttpsPort = 18443; s.PhpBasePort = 19100; s.MemcachedPort = 11999;
                s.Ssl = true;
                s.Tld = "localhost";
                var site = Path.Combine(Paths.Www, "e2e");
                Directory.CreateDirectory(site);
                File.WriteAllText(Path.Combine(site, "index.php"), "<?php echo PHP_VERSION, ' xdebug=', extension_loaded('xdebug') ? 'yes' : 'no';");

                if (nginx != null && phpForWeb != null) OwnWebServer("nginx", phpForWeb, false);
                if (httpd != null && phpForWeb != null) OwnWebServer("apache", phpForWeb, false);
                // the own PHP build with Xdebug on, behind Apache then nginx: php-fpm loads xdebug.so
                if (ownPhp != null && httpd != null && Xdebug.IsAvailable(Path.Combine(Paths.BinOf("php"), ownPhp))) OwnWebServer("apache", ownPhp, true);
                if (ownPhp != null && nginx != null && Xdebug.IsAvailable(Path.Combine(Paths.BinOf("php"), ownPhp))) OwnWebServer("nginx", ownPhp, true);
                if (memcached != null) OwnMemcached();

                List<string> left = null;
                for (int i = 0; i < 25; i++)
                {
                    left = LeftoverProcesses().Where(x => !before.Contains(x)).ToList();
                    if (left.Count == 0) break;
                    Thread.Sleep(200);
                }
                Check("own builds: no child process left", left.Count == 0, string.Join(", ", left));
                Check("own builds: test ports free again", !new[] { 18080, 18443, 11999, 19100, 19101, 19102 }.Any(ProcessUtil.IsPortInUse));
            }
            finally
            {
                try { ServiceManager.StopAllAsync().GetAwaiter().GetResult(); } catch { }
                s.HttpPort = oldHttp; s.HttpsPort = oldHttps; s.PhpBasePort = oldPhp; s.MemcachedPort = oldMc; s.Ssl = oldSsl; s.Tld = oldTld; s.WebServer = oldServer;
                s.SetVersion("php", oldPhpVersion);
            }
        }

        /// <summary>The own PHP build: Xdebug is offered, written into php.ini and loads in the php command line.</summary>
        static void OwnPhpXdebug(string folder)
        {
            var dir = Path.Combine(Paths.BinOf("php"), folder);
            Check("own PHP: ships ext/xdebug.so and php.ini-development", Xdebug.SoPath(dir) != null && File.Exists(Path.Combine(dir, "php.ini-development")), dir);
            Check("own PHP: Xdebug is available", Xdebug.IsAvailable(dir));
            var php = Path.Combine(dir, "php");
            var ini = PhpConfig.IniPath(dir);
            var before = ProcessUtil.Run(php, "-c \"" + ini + "\" -r \"echo extension_loaded('xdebug') ? 'yes' : 'no';\"", dir, null, 30000);
            Check("own PHP: Xdebug is off at first", before.Output.Trim() == "no", before.Output);
            try
            {
                Xdebug.EnableAsync(dir).GetAwaiter().GetResult();
                var v = ProcessUtil.Run(php, "-c \"" + ini + "\" -v", dir, null, 30000);
                Check("own PHP: php -v names Xdebug after Xdebug.Enable", v.Output.Contains("with Xdebug v"), v.Output);
                var m = ProcessUtil.Run(php, "-c \"" + ini + "\" -r \"echo extension_loaded('xdebug') ? 'yes' : 'no', ' ', ini_get('xdebug.mode');\"", dir, null, 30000);
                Check("own PHP: xdebug.mode comes from php.ini", m.Output.Trim() == "yes debug,develop", m.Output);
            }
            catch (Exception ex) { Check("own PHP: Xdebug.Enable", false, ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>Start the web server (+ php-fpm of <paramref name="phpFolder"/>), fetch the PHP page over HTTP and HTTPS, stop everything.</summary>
        static void OwnWebServer(string server, string phpFolder, bool xdebug)
        {
            var s = Settings.Current;
            var label = server + " + PHP " + PackageTypes.Label(phpFolder) + (xdebug ? " (Xdebug)" : "");
            var phpDir = Path.Combine(Paths.BinOf("php"), phpFolder);
            if (xdebug) { try { Xdebug.EnableAsync(phpDir).GetAwaiter().GetResult(); } catch (Exception ex) { Check(label + ": Xdebug switched on", false, ex.Message); return; } }
            else if (Xdebug.IsAvailable(phpDir)) Xdebug.Disable(phpDir);
            s.WebServer = server;
            s.SetVersion("php", phpFolder);
            var web = ServiceManager.Web;
            Check(label + ": the web server of the settings is used", web.Id == "web" && web.Title == (server == "nginx" ? "Nginx" : "Apache") && web.IsInstalled, web.Title);
            try
            {
                var sites = ServiceManager.RefreshSites();
                var e2e = sites.FirstOrDefault(x => x.Valid && x.Name == "e2e");
                Check(label + ": the test project is found", e2e != null);
                if (e2e == null) return;
                var php = ProcessUtil.Run(Path.Combine(phpDir, "php"), "-n -r \"echo PHP_VERSION;\"", phpDir, null, 30000);
                var version = php.Output.Trim();
                Check(label + ": php command line answers its version", Regex.IsMatch(version, @"^\d+\.\d+\.\d+"), version);

                ServiceManager.StartWebAsync().GetAwaiter().GetResult();
                Check(label + ": PHP running", ServiceManager.Php.State == ServiceState.Running, ServiceManager.Php.LastError);
                Check(label + ": " + web.Title + " running", web.State == ServiceState.Running, web.LastError);
                if (web.State == ServiceState.Running && ServiceManager.Php.State == ServiceState.Running)
                {
                    var want = version + " xdebug=" + (xdebug ? "yes" : "no");
                    var body = FetchRetry("http://127.0.0.1:18080/", e2e.Host, b => b.Contains(want) || (!xdebug && b.Contains(version)), 20);
                    Check(label + ": http page runs PHP", body.Contains(version) && (!xdebug || body.Contains(want)), body.Length > 200 ? body.Substring(0, 200) : body);
                    if (ProcessUtil.IsPortInUse(18443))
                    {
                        body = FetchRetry("https://127.0.0.1:18443/", e2e.Host, b => b.Contains(version), 10);
                        Check(label + ": https page runs PHP", body.Contains(version) && (!xdebug || body.Contains(want)), body.Length > 200 ? body.Substring(0, 200) : body);
                    }
                    else Check(label + ": HTTPS port is open", false, "18443 closed");
                    HttpStatusCode code;
                    var dash = Fetch("http://127.0.0.1:18080/", "localhost", out code);
                    Check(label + ": the dashboard answers on http://localhost", code == HttpStatusCode.OK, ((int)code) + " " + (dash.Length > 80 ? dash.Substring(0, 80) : dash));
                }
            }
            finally
            {
                ServiceManager.StopAllAsync().GetAwaiter().GetResult();
                Check(label + ": stopped", ServiceManager.Php.State == ServiceState.Stopped && web.State == ServiceState.Stopped, web.State + " " + ServiceManager.Php.State);
                for (int i = 0; i < 25 && (ProcessUtil.IsPortInUse(18080) || ProcessUtil.IsPortInUse(18443)); i++) Thread.Sleep(200);
                Check(label + ": ports free after stop", !ProcessUtil.IsPortInUse(18080) && !ProcessUtil.IsPortInUse(18443));
            }
        }

        static void OwnMemcached()
        {
            var mc = ServiceManager.Memcached;
            mc.StartAsync().GetAwaiter().GetResult();
            Check("memcached: running", mc.State == ServiceState.Running, mc.LastError);
            if (mc.State == ServiceState.Running)
            {
                string answer = "";
                try
                {
                    using (var c = new TcpClient("127.0.0.1", 11999))
                    {
                        c.ReceiveTimeout = 5000;
                        var st = c.GetStream();
                        var cmd = Encoding.ASCII.GetBytes("set portly 0 0 2\r\nok\r\nget portly\r\nversion\r\nquit\r\n");
                        st.Write(cmd, 0, cmd.Length);
                        var sb = new StringBuilder();
                        var buf = new byte[1024]; int n;
                        while ((n = st.Read(buf, 0, buf.Length)) > 0) sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        answer = sb.ToString();
                    }
                }
                catch (Exception ex) { answer = ex.Message; }
                Check("memcached: 'version' answered over TCP", Regex.IsMatch(answer, @"^VERSION \d+\.\d+\.\d+", RegexOptions.Multiline), answer.Replace("\r\n", " | "));
                Check("memcached: set / get works", answer.Contains("STORED") && answer.Contains("VALUE portly 0 2"), answer.Replace("\r\n", " | "));
            }
            mc.StopAsync().GetAwaiter().GetResult();
            Check("memcached: stopped, port free", mc.State == ServiceState.Stopped && !ProcessUtil.IsPortInUse(11999));
        }
    }
}
