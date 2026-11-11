using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.CoreTests
{
    /// <summary>
    /// Services: generated configuration checks (every OS, no packages needed) and, only with PORTLY_E2E=1 and the
    /// packages installed, a real start / answer / stop of PHP, the web server, MySQL and Mailpit on high ports.
    /// </summary>
    static partial class Program
    {
        const string FakePhp = "php-0.0.1-portlytest";
        const string FakeMysql = "mysql-8.4.0-portlytest";

        static void Suite_Services()
        {
            ConfigChecks();
            NotInstalledChecks();
            if (Environment.GetEnvironmentVariable("PORTLY_E2E") == "1") EndToEnd();
            else Console.WriteLine("  (end-to-end start/stop skipped: set PORTLY_E2E=1 with the packages installed)");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Templates ship next to the program; the test build does not copy them, so take them from the source tree.</summary>
        static void ProvideTemplates()
        {
            if (File.Exists(Path.Combine(Paths.Templates, "caddy-Caddyfile.tpl"))) return;
            var dir = new DirectoryInfo(Paths.AppDir);
            string src = null;
            while (dir != null && src == null)
            {
                var c = Path.Combine(dir.FullName, "src", "Portly", "templates");
                if (File.Exists(Path.Combine(c, "caddy-Caddyfile.tpl"))) src = c;
                dir = dir.Parent;
            }
            if (src == null) throw new Exception("templates folder not found from " + Paths.AppDir);
            Directory.CreateDirectory(Paths.UserTemplates);
            foreach (var f in Directory.GetFiles(src, "*.tpl"))
            {
                var target = Path.Combine(Paths.UserTemplates, Path.GetFileName(f));
                if (!File.Exists(target)) File.Copy(f, target);
            }
        }

        static void Touch(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            if (!File.Exists(file)) File.WriteAllText(file, "");
        }

        static void CleanDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        static bool HasWindowsPath(string text)
        {
            return text.Contains("\\") || Regex.IsMatch(text, @"(^|[\s=""'])[A-Za-z]:[/\\]", RegexOptions.Multiline);
        }

        // ------------------------------------------------------------------ configuration (every OS)

        static void ConfigChecks()
        {
            ProvideTemplates();
            Paths.EnsureAll();
            var s = Settings.Current;
            int oldHttp = s.HttpPort, oldHttps = s.HttpsPort, oldPhp = s.PhpBasePort, oldChildren = s.PhpChildren, oldMysql = s.MysqlPort;
            bool oldSsl = s.Ssl;
            var fakePhp = Path.Combine(Paths.BinOf("php"), FakePhp);
            var fakeMysql = Path.Combine(Paths.BinOf("mysql"), FakeMysql);
            try
            {
                s.HttpPort = 18080; s.HttpsPort = 18443; s.PhpBasePort = 19100; s.PhpChildren = 4; s.MysqlPort = 13306;

                // ---- php-fpm pool (the file is plain text, written on every OS)
                var conf = PhpConfig.WriteFpmConf(FakePhp, 19100);
                var fpm = File.ReadAllText(conf);
                Check("php-fpm: stays in the foreground", fpm.Contains("daemonize = no"));
                Check("php-fpm: one pool on the PHP port", fpm.Contains("listen = 127.0.0.1:19100"));
                Check("php-fpm: static pool of PhpChildren workers", fpm.Contains("pm = static") && fpm.Contains("pm.max_children = 4"));
                Check("php-fpm: environment kept, worker output caught", fpm.Contains("clear_env = no") && fpm.Contains("catch_workers_output = yes"));
                Check("php-fpm: no user / group lines", !Regex.IsMatch(fpm, @"^\s*(user|group)\s*=", RegexOptions.Multiline));
                Check("php-fpm: error log in logs, pid in tmp", fpm.Contains(Paths.Fwd(Paths.Logs) + "/php-fpm-") && fpm.Contains("pid = " + Paths.Fwd(Paths.Tmp) + "/"));
                if (!OS.IsWindows) Check("php-fpm: no Windows paths, LF only", !HasWindowsPath(fpm) && !fpm.Contains("\r"), conf);
                else Check("php-fpm: forward slashes only", !fpm.Split((char)10).Where(l => !l.StartsWith(";")).Any(l => l.Contains("\\")));

                // ---- php.ini
                Touch(Path.Combine(fakePhp, "php-cgi.exe")); Touch(Path.Combine(fakePhp, "php-fpm")); Touch(Path.Combine(fakePhp, "php"));
                PhpConfig.Ensure(fakePhp);
                var ini = File.ReadAllText(PhpConfig.IniPath(fakePhp));
                Check("php.ini: upload_progress off", Regex.IsMatch(ini, @"^session\.upload_progress\.enabled = Off\s*$", RegexOptions.Multiline));
                Check("php.ini: allow_url_include off", Regex.IsMatch(ini, @"^allow_url_include = Off\s*$", RegexOptions.Multiline));
                Check("php.ini: sessions and upload folder in Portly's tmp", ini.Contains("session.save_path = \"" + Paths.Fwd(Paths.Sessions) + "\"") && ini.Contains("upload_tmp_dir = \"" + Paths.Fwd(Paths.Tmp) + "\""));
                Check("php.ini: error_log in logs", ini.Contains("error_log = \"" + Paths.Fwd(Path.Combine(Paths.Logs, "php_errors.log")) + "\""));
                if (!OS.IsWindows)
                {
                    Check("php.ini (static PHP): no extension_dir", !Regex.IsMatch(ini, @"^\s*extension_dir\s*=", RegexOptions.Multiline));
                    Check("php.ini (static PHP): no extension= / zend_extension= lines", !Regex.IsMatch(ini, @"^\s*(zend_)?extension\s*=", RegexOptions.Multiline));
                    Check("php.ini (static PHP): sys_temp_dir set", ini.Contains("sys_temp_dir = \"" + Paths.Fwd(Paths.Tmp) + "\""));
                    Check("php.ini: no Windows paths", !HasWindowsPath(ini), PhpConfig.IniPath(fakePhp));
                    Check("php.ini: no SMTP lines (mail() uses sendmail_path)", !Regex.IsMatch(ini, @"^\s*SMTP\s*=", RegexOptions.Multiline));
                }
                else Check("php.ini (Windows): extension_dir kept", Regex.IsMatch(ini, @"^extension_dir = ""[^""]*/ext""\s*$", RegexOptions.Multiline));

                // ---- Caddyfile: dashboard block with php_fastcgi to the php-fpm port
                s.Ssl = false;
                CaddyConfig.WriteCaddyfile(new List<Site>(), f => 19100);
                var caddy = File.ReadAllText(CaddyConfig.Caddyfile);
                Check("Caddyfile: php_fastcgi to the PHP port", caddy.Contains("php_fastcgi 127.0.0.1:19100"));
                Check("Caddyfile: HTTP port, admin off, logs in logs", caddy.Contains("http://:18080") && caddy.Contains("admin off") && caddy.Contains(Paths.Fwd(Paths.Logs) + "/caddy-error.log"));
                if (!OS.IsWindows) Check("Caddyfile: no Windows paths, LF only", !Regex.IsMatch(caddy, @"(^|[\s=""'])[A-Za-z]:[/\\]", RegexOptions.Multiline) &&!caddy.Contains("\r"), CaddyConfig.Caddyfile);
                // HTTPS with Portly's own certificate (skipped quietly when this system could not make one)
                s.Ssl = true;
                CaddyConfig.WriteCaddyfile(new List<Site>(), f => 19100);
                caddy = File.ReadAllText(CaddyConfig.Caddyfile);
                if (caddy.Contains("https://:18443"))
                    Check("Caddyfile: HTTPS block uses Portly's certificate files", Regex.IsMatch(caddy, @"tls ""[^""]+/localhost[^""]*"" ""[^""]+"""), "tls line");
                else Console.WriteLine("  (no HTTPS block: Portly's certificate could not be made here)");

                // ---- MySQL option file
                Touch(Path.Combine(fakeMysql, "bin", "mysqld.exe")); Touch(Path.Combine(fakeMysql, "bin", "mysqld"));
                var data = Path.Combine(Paths.Data, "mysql-8.4");
                MySqlService.WriteConfig(fakeMysql, data);
                var cnf = File.ReadAllText(MySqlService.ConfFile);
                Check("my.cnf: data folder and port", cnf.Contains("datadir=\"" + Paths.Fwd(data) + "\"") && cnf.Contains("port=13306"));
                Check("my.cnf: secure-file-priv and error log inside Portly", cnf.Contains("secure-file-priv=\"" + Paths.Fwd(Paths.MysqlFiles) + "\"") && cnf.Contains("log-error=\"" + Paths.Fwd(Paths.Logs) + "/mysql-error.log\""));
                if (!OS.IsWindows)
                {
                    Check("my.cnf: socket line", Regex.IsMatch(cnf, @"^socket=""[^""]+mysql\.sock""\s*$", RegexOptions.Multiline), MySqlService.ConfFile);
                    Check("my.cnf: socket also for clients", Regex.Matches(cnf, @"^socket=", RegexOptions.Multiline).Count == 2);
                    Check("my.cnf: pid-file, tmpdir and X socket", cnf.Contains("pid-file=") && cnf.Contains("tmpdir=") && cnf.Contains("mysqlx-socket="));
                    Check("my.cnf: no Windows paths, LF only", !HasWindowsPath(cnf) && !cnf.Contains("\r"));
                    Check("my.cnf: socket path short enough", MySqlService.SocketFile.Length < 104, MySqlService.SocketFile);
                }
                else Check("my.ini (Windows): written as before", MySqlService.ConfFile.EndsWith("my.ini") && !cnf.Contains("socket="));
            }
            finally
            {
                s.HttpPort = oldHttp; s.HttpsPort = oldHttps; s.PhpBasePort = oldPhp; s.PhpChildren = oldChildren; s.MysqlPort = oldMysql; s.Ssl = oldSsl;
                CleanDir(fakePhp);
                CleanDir(fakeMysql);
            }
        }

        // ------------------------------------------------------------------ services without a package

        static void NotInstalledChecks()
        {
            if (OS.IsWindows) return;
            // macOS / Linux have no Apache, Nginx, Redis ... packages yet: no crash, a clear "not installed" state
            foreach (var svc in new Service[] { ServiceManager.Apache, ServiceManager.Nginx, ServiceManager.Redis, ServiceManager.Postgres, ServiceManager.Mongo, ServiceManager.Memcached })
            {
                if (svc.IsInstalled) continue;
                svc.StartAsync().GetAwaiter().GetResult();
                Check(svc.Title + ": not installed, no crash", svc.State == ServiceState.Error && !string.IsNullOrEmpty(svc.LastError), svc.LastError);
                svc.StopAsync().GetAwaiter().GetResult();
                Check(svc.Title + ": stop after a failed start", svc.State == ServiceState.Stopped);
            }
            var oldServer = Settings.Current.WebServer;
            try
            {
                Settings.Current.WebServer = "apache";
                Check("web server: apache chosen but not installed -> Caddy", ServiceManager.Apache.IsInstalled || ServiceManager.Web == ServiceManager.Caddy);
                Settings.Current.WebServer = "nginx";
                Check("web server: nginx chosen but not installed -> Caddy", ServiceManager.Nginx.IsInstalled || ServiceManager.Web == ServiceManager.Caddy);
            }
            finally { Settings.Current.WebServer = oldServer; }
        }

        // ------------------------------------------------------------------ real start / stop (PORTLY_E2E=1)

        static List<string> LeftoverProcesses()
        {
            var found = new List<string>();
            var bin = Paths.Bin + Path.DirectorySeparatorChar;
            var cmp = OS.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.HasExited) continue;
                    var path = ProcessUtil.ProcessPath(p.Id);
                    if (path != "" && path.StartsWith(bin, cmp)) found.Add(p.ProcessName + " (" + p.Id + ")");
                }
                catch { }
                finally { p.Dispose(); }
            }
            return found;
        }

        static string Fetch(string url, string host, out HttpStatusCode code)
        {
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => true };
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) })
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (host != null) req.Headers.Host = host;
                try
                {
                    using (var res = client.SendAsync(req).GetAwaiter().GetResult())
                    {
                        code = res.StatusCode;
                        return res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex) { code = 0; return ex.GetType().Name + ": " + ex.Message; }
            }
        }

        static string FetchRetry(string url, string host, Func<string, bool> good, int seconds)
        {
            var until = DateTime.Now.AddSeconds(seconds);
            string body; HttpStatusCode code;
            do
            {
                body = Fetch(url, host, out code);
                if (code == HttpStatusCode.OK && good(body)) return body;
                Thread.Sleep(500);
            } while (DateTime.Now < until);
            return "[" + (int)code + "] " + body;
        }

        static void EndToEnd()
        {
            Console.WriteLine("  -- end-to-end start / stop (high ports)");
            var s = Settings.Current;
            var phpDir = PackageTypes.ActiveDir("php");
            // Caddy is the web server on macOS / Linux; on Windows ask for it as well so that the same test runs there
            var oldServer = s.WebServer;
            var oldFeature = s.Raw.GetBool("features", "caddy", false);
            s.Raw.Set("features", "caddy", true);
            s.WebServer = "caddy";
            var web = ServiceManager.Web;
            if (phpDir == null || !web.IsInstalled || !ServiceManager.MySql.IsInstalled || !ServiceManager.Mailpit.IsInstalled)
            {
                s.WebServer = oldServer; s.Raw.Set("features", "caddy", oldFeature);
                Console.WriteLine("  SKIP end-to-end: packages missing (php " + (phpDir != null) + ", " + web.Title + " " + web.IsInstalled +
                                  ", mysql " + ServiceManager.MySql.IsInstalled + ", mailpit " + ServiceManager.Mailpit.IsInstalled + ")");
                return;
            }
            Paths.EnsureAll();
            ProvideTemplates();
            int oldHttp = s.HttpPort, oldHttps = s.HttpsPort, oldMysql = s.MysqlPort, oldWeb = s.MailpitWebPort, oldSmtp = s.MailpitSmtpPort, oldPhp = s.PhpBasePort;
            bool oldSsl = s.Ssl;
            var oldTld = s.Tld;
            var before = LeftoverProcesses();
            try
            {
                s.HttpPort = 18080; s.HttpsPort = 18443; s.MysqlPort = 13306; s.MailpitWebPort = 18025; s.MailpitSmtpPort = 11025; s.PhpBasePort = 19100;
                s.Ssl = true;
                s.Tld = "localhost";   // *.localhost needs no hosts file entry (the test must not ask for administrator rights)
                var site = Path.Combine(Paths.Www, "e2e");
                Directory.CreateDirectory(site);
                File.WriteAllText(Path.Combine(site, "index.php"), "<?php echo PHP_VERSION;");

                var sites = ServiceManager.RefreshSites();
                var e2e = sites.FirstOrDefault(x => x.Valid && x.Name == "e2e");
                Check("e2e: the test project is found", e2e != null);
                if (e2e == null) return;

                // expected version: what the installed PHP says itself
                var php = ProcessUtil.Run(Path.Combine(phpDir, OS.Exe("php")), "-n -r \"echo PHP_VERSION;\"", phpDir, null, 30000);
                var version = php.Output.Trim();
                Check("e2e: php CLI answers its version", Regex.IsMatch(version, @"^\d+\.\d+\.\d+"), version);

                ServiceManager.StartWebAsync().GetAwaiter().GetResult();
                Check("e2e: PHP running", ServiceManager.Php.State == ServiceState.Running, ServiceManager.Php.LastError);
                Check("e2e: " + web.Title + " running", web.State == ServiceState.Running, web.LastError);
                ServiceManager.MySql.StartAsync().GetAwaiter().GetResult();
                Check("e2e: MySQL running", ServiceManager.MySql.State == ServiceState.Running, ServiceManager.MySql.LastError);
                ServiceManager.Mailpit.StartAsync().GetAwaiter().GetResult();
                Check("e2e: Mailpit running", ServiceManager.Mailpit.State == ServiceState.Running, ServiceManager.Mailpit.LastError);

                if (web.State == ServiceState.Running && ServiceManager.Php.State == ServiceState.Running)
                {
                    var body = FetchRetry("http://127.0.0.1:18080/", e2e.Host, b => b.Contains(version), 20);
                    Check("e2e: http site runs PHP " + version, body.Contains(version) && version != "", body.Length > 200 ? body.Substring(0, 200) : body);
                    if (s.Ssl && ProcessUtil.IsPortInUse(18443))
                    {
                        body = FetchRetry("https://127.0.0.1:18443/", e2e.Host, b => b.Contains(version), 10);
                        Check("e2e: https site runs PHP " + version, body.Contains(version) && version != "", body.Length > 200 ? body.Substring(0, 200) : body);
                    }
                }

                if (ServiceManager.MySql.State == ServiceState.Running)
                {
                    var mysql = Path.Combine(PackageTypes.ActiveDir("mysql"), "bin", OS.Exe("mysql"));
                    var r = ProcessUtil.Run(mysql, "--protocol=TCP -h127.0.0.1 -P13306 -uroot -N -B -e \"select 1\"", null, null, 60000);
                    Check("e2e: mysql client runs select 1", r.Ok && r.Output.Split('\n').Any(l => l.Trim() == "1"), r.Output);
                }
                if (ServiceManager.Mailpit.State == ServiceState.Running)
                {
                    var info = FetchRetry("http://127.0.0.1:18025/api/v1/info", null, b => b.Length > 0, 10);
                    Check("e2e: Mailpit web answers", !info.StartsWith("["), info.Length > 120 ? info.Substring(0, 120) : info);
                }

                ServiceManager.StopAllAsync().GetAwaiter().GetResult();
                Check("e2e: services stopped", ServiceManager.Php.State == ServiceState.Stopped && web.State == ServiceState.Stopped &&
                      ServiceManager.MySql.State == ServiceState.Stopped && ServiceManager.Mailpit.State == ServiceState.Stopped);
                // children end a moment after their parent
                List<string> left = null;
                for (int i = 0; i < 25; i++)
                {
                    left = LeftoverProcesses().Where(x => !before.Contains(x)).ToList();
                    if (left.Count == 0) break;
                    Thread.Sleep(200);
                }
                Check("e2e: no child process left", left.Count == 0, string.Join(", ", left));
                Check("e2e: test ports free again", !new[] { 18080, 18443, 13306, 18025, 11025, 19100 }.Any(ProcessUtil.IsPortInUse));
            }
            finally
            {
                try { ServiceManager.StopAllAsync().GetAwaiter().GetResult(); } catch { }
                s.WebServer = oldServer; s.Raw.Set("features", "caddy", oldFeature);
                s.HttpPort = oldHttp; s.HttpsPort = oldHttps; s.MysqlPort = oldMysql; s.MailpitWebPort = oldWeb; s.MailpitSmtpPort = oldSmtp; s.PhpBasePort = oldPhp; s.Ssl = oldSsl; s.Tld = oldTld;
            }
        }
    }
}
