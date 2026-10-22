using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.SmokeTest
{
    /// <summary>
    /// Runs Portly's engine without the UI on non-standard ports (so it can run next to Laragon/XAMPP).
    /// Optional argument: a folder with existing MySQL installs to link instead of downloading 280 MB.
    /// </summary>
    static class Program
    {
        static int _fail, _ok;

        static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _ok++; else _fail++;
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "  PASS " : "  FAIL ");
            Console.ResetColor();
            Console.WriteLine(name + (detail != "" ? "  -> " + detail : ""));
        }

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try { return Run(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine(ex); return 2; }
            finally { try { ServiceManager.StopAllAsync().Wait(30000); } catch { } }
        }

        static async Task<int> Run(string[] args)
        {
            Net.Init();
            Paths.EnsureAll();
            Settings.Load();
            var s = Settings.Current;
            s.HttpPort = 8781; s.HttpsPort = 8744; s.MysqlPort = 3377; s.RedisPort = 6377;
            s.MailpitWebPort = 8727; s.MailpitSmtpPort = 1727; s.PhpBasePort = 9277;
            s.Tld = "localhost"; s.Ssl = true; s.AutoVhosts = true; s.WebServer = "apache";
            s.DocumentRoot = Path.Combine(Paths.Root, "www");
            s.Save();
            I18n.Load("tr");
            Log.Written += l => Console.WriteLine("    | " + l);
            Console.WriteLine("Root: " + Paths.Root);

            // ---------- catalog ----------
            Catalog.LoadLocal();
            Check("embedded catalog loads", Catalog.Entries.Count > 10, Catalog.Entries.Count + " entries");
            await Catalog.RefreshAsync();
            var php = Catalog.Recommended("php");
            Check("live PHP list merged", php != null && php.Sha256 != "", php == null ? "" : php.Folder);

            // ---------- MySQL: link an existing install when given (saves a 280 MB download) ----------
            var mysqlEntry = Catalog.Recommended("mysql");
            if (args.Length > 0 && Directory.Exists(Path.Combine(args[0], mysqlEntry.Folder)) && !mysqlEntry.IsInstalled)
            {
                Directory.CreateDirectory(Paths.BinOf("mysql"));
                var r = ProcessUtil.Run("cmd.exe", "/c mklink /J \"" + mysqlEntry.InstallDir + "\" \"" + Path.Combine(args[0], mysqlEntry.Folder) + "\"", Paths.Root);
                Check("link MySQL " + mysqlEntry.Version, Directory.Exists(mysqlEntry.InstallDir), r.Output);
                s.SetVersion("mysql", mysqlEntry.Folder); s.Save();
            }

            // ---------- install packages (real downloads) ----------
            var wanted = new List<PackageEntry>
            {
                Catalog.Recommended("apache"), Catalog.Recommended("nginx"), php,
                Catalog.ForType("php").First(e => e.Branch == "8.4"),
                Catalog.Recommended("redis"), Catalog.Recommended("mailpit"),
                Catalog.Recommended("adminer"), Catalog.Recommended("composer"), Catalog.Recommended("phpmyadmin"),
            };
            if (!mysqlEntry.IsInstalled) wanted.Add(mysqlEntry);
            foreach (var e in wanted)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await Installer.InstallAsync(e, null, CancellationToken.None);
                    Check("install " + e.TypeName + " " + e.Version, e.IsInstalled && PackageTypes.Installed(e.Type).Contains(e.Folder), sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
                }
                catch (Exception ex) { Check("install " + e.TypeName + " " + e.Version, false, ex.Message); }
            }
            s.SetVersion("php", php.Folder); s.Save();
            var phpDir = PackageTypes.ActiveDir("php");
            Check("php.ini security: upload_progress Off", PhpConfig.Get(phpDir, "session.upload_progress.enabled") == "Off");
            Check("php.ini extension_dir points to Portly", PhpConfig.Get(phpDir, "extension_dir").StartsWith(Paths.Fwd(Paths.Root)));
            Check("composer.bat created", File.Exists(Path.Combine(PackageTypes.ActiveDir("composer"), "composer.bat")));

            // ---------- sites ----------
            var www = s.DocumentRoot;
            Directory.CreateDirectory(Path.Combine(www, "demo"));
            File.WriteAllText(Path.Combine(www, "demo", "index.php"),
                "<?php echo 'DEMO-OK php=' . PHP_VERSION . ' ';\n" +
                "$m = @new mysqli('127.0.0.1', 'root', '', '', " + s.MysqlPort + ");\n" +
                "echo $m->connect_errno ? 'db=fail' : 'db=' . $m->server_info;\n" +
                "echo ' mail=' . (mail('test@example.com', 'Portly smoke', 'hello') ? 'sent' : 'fail');\n" +
                "echo ' upload_progress=' . ini_get('session.upload_progress.enabled');\n");
            Directory.CreateDirectory(Path.Combine(www, "legacyapp", "public"));
            File.WriteAllText(Path.Combine(www, "legacyapp", "public", "index.php"), "<?php echo 'LEGACY php=' . PHP_VERSION;");
            Directory.CreateDirectory(Path.Combine(www, "Özel Proje"));
            // a group folder: clientscme -> acme.clients.localhost
            Directory.CreateDirectory(Path.Combine(www, "clients", "acme"));
            File.WriteAllText(Path.Combine(www, "clients", "acme", "index.php"), "<?php echo 'ACME-OK';");
            Directory.CreateDirectory(Path.Combine(www, "cakeapp", "webroot"));
            File.WriteAllText(Path.Combine(www, "cakeapp", "webroot", "index.php"), "<?php echo 'CAKE';");
            var sites = Sites.Scan();
            Check("CakePHP-style webroot folder detected", sites.First(x => x.Name == "cakeapp").DocRoot.EndsWith("webroot"));
            Check("group folder detected", sites.Any(x => x.Name == "clients" && x.IsGroup) && sites.Any(x => x.Host == "acme.clients.localhost" && x.Valid),
                string.Join(", ", sites.Where(x => x.Group != "" || x.IsGroup).Select(x => x.Host)));
            Check("sites scanned", sites.Count(x => x.Valid && x.Group == "" && !x.IsGroup) == 3, string.Join(", ", sites.Select(x => x.Name + (x.Valid ? "" : "(invalid)"))));
            Check("Laravel-style public folder detected", sites.First(x => x.Name == "legacyapp").DocRoot.EndsWith("public"));
            var php84 = PackageTypes.Installed("php").First(f => PackageTypes.Branch(f) == "8.4");
            Sites.SetPhp("legacyapp", php84);

            // ---------- port guard: occupy the FastCGI port of the default PHP, it must move to a free one ----------
            var phpBlocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, ServiceManager.Php.PortFor(php.Folder));
            phpBlocker.Start();

            // ---------- start ----------
            var sw2 = Stopwatch.StartNew();
            s.SetEnabled("redis", true); s.SetEnabled("mailpit", true); s.Save();
            await ServiceManager.StartAllAsync();
            Console.WriteLine("    start all took " + sw2.Elapsed.TotalSeconds.ToString("0.0") + " s");
            foreach (var svc in new Service[] { ServiceManager.Apache, ServiceManager.Php, ServiceManager.MySql, ServiceManager.Redis, ServiceManager.Mailpit })
                Check(svc.Title + " running", svc.State == ServiceState.Running, svc.LastError ?? "");

            Check("PHP moved to a free FastCGI port when its port was taken", ServiceManager.Php.PortFor(php.Folder) != ((IPEndPoint)phpBlocker.LocalEndpoint).Port, "port " + ServiceManager.Php.PortFor(php.Folder));
            await WebChecks("Apache", php, php84);
            phpBlocker.Stop();

            // ---------- database tools ----------
            Check("create database", Database.CreateDatabase("smoke_db") == null);
            Check("database listed", Database.ListDatabases().Contains("smoke_db"));
            try { var f = Database.BackupAll(); Check("backup all databases", new FileInfo(f).Length > 1000, f); }
            catch (Exception ex) { Check("backup all databases", false, ex.Message); }

            // ---------- automatic backup: rotation keeps only the newest N auto-*.sql ----------
            s.AutoBackupHours = 1; s.AutoBackupKeep = 1; s.LastAutoBackup = ""; s.Save();
            var auto1 = Database.AutoBackupIfDue();
            s.LastAutoBackup = ""; s.Save();
            System.Threading.Thread.Sleep(1100);
            var auto2 = Database.AutoBackupIfDue();
            var autoFiles = Directory.GetFiles(Path.Combine(Paths.Backup, "mysql"), "auto-*.sql");
            Check("automatic backup + rotation", auto1 != null && auto2 != null && autoFiles.Length == 1 && Database.AutoBackupIfDue() == null, autoFiles.Length + " auto file(s)");
            s.AutoBackupHours = 0; s.Save();

            // ---------- Xdebug toggle on PHP 8.4 ----------
            var php84Dir = Path.Combine(Paths.BinOf("php"), php84);
            try
            {
                await Xdebug.EnableAsync(php84Dir);
                var mods = ProcessUtil.Run(Path.Combine(php84Dir, "php.exe"), "-m", php84Dir, null, 30000);
                Check("Xdebug enabled (php -m)", Xdebug.IsEnabled(php84Dir) && mods.Output.Contains("Xdebug"), Xdebug.DownloadUrl(php84));
                Xdebug.Disable(php84Dir);
                mods = ProcessUtil.Run(Path.Combine(php84Dir, "php.exe"), "-m", php84Dir, null, 30000);
                Check("Xdebug disabled again", !Xdebug.IsEnabled(php84Dir) && !mods.Output.Contains("Xdebug"));
            }
            catch (Exception ex) { Check("Xdebug enabled (php -m)", false, ex.Message); }

            // ---------- import from Laragon (read only): the smallest database ----------
            var laragon = Importer.Detect().FirstOrDefault(x => x.Kind == "laragon" && x.MysqlBin != null);
            if (laragon != null)
            {
                try
                {
                    var dbs = await Importer.ListDatabasesAsync(laragon, "", t => Console.WriteLine("    | " + t));
                    var small = dbs.OrderBy(d => d.Bytes).FirstOrDefault(d => d.Bytes > 0);
                    Check("Laragon detected and databases listed", dbs.Count > 0, laragon.Name + ": " + dbs.Count + " dbs, MySQL " + laragon.MysqlVersion);
                    if (small != null)
                    {
                        var msg = await Importer.ImportDatabasesAsync(laragon, "", new List<string> { small.Name }, t => Console.WriteLine("    | " + t), CancellationToken.None);
                        Check("Laragon database imported (" + small.Name + ", " + small.Bytes / 1024 + " KB)", Database.ListDatabases().Contains(small.Name), msg);
                    }
                }
                catch (Exception ex) { Check("Laragon import", false, ex.Message); }
            }
            else Console.WriteLine("  (skip Laragon import: not installed)");

            // ---------- mail caught by Mailpit ----------
            var mails = Get("http://127.0.0.1:" + s.MailpitWebPort + "/api/v1/messages", null);
            Check("Mailpit caught PHP mail()", mails.Contains("Portly smoke"));

            // ---------- other mail catchers: MailHog, smtp4dev (SMTP in, HTTP API out) ----------
            foreach (var tool in new[] { "mailhog", "smtp4dev" })
            {
                try
                {
                    var entry = Catalog.Recommended(tool);
                    await Installer.InstallAsync(entry, null, CancellationToken.None);
                    await ServiceManager.Mailpit.StopAsync();
                    s.MailTool = tool; s.Save();
                    await ServiceManager.Mailpit.StartAsync();
                    var started = ServiceManager.Mailpit.State == ServiceState.Running;
                    using (var smtp = new System.Net.Mail.SmtpClient("127.0.0.1", s.MailpitSmtpPort))
                        smtp.Send("portly@localhost", "test@example.com", "Portly " + tool, "hello");
                    await Task.Delay(1500);
                    var api = tool == "mailhog" ? "/api/v2/messages" : "/api/messages";
                    var got = Get("http://127.0.0.1:" + s.MailpitWebPort + api, null);
                    Check(tool + " catches mail", started && got.Contains("Portly " + tool), started ? got.Substring(0, Math.Min(80, got.Length)) : ServiceManager.Mailpit.LastError);
                }
                catch (Exception ex) { Check(tool + " catches mail", false, ex.Message); }
            }
            await ServiceManager.Mailpit.StopAsync();
            s.MailTool = "mailpit"; s.Save();
            await ServiceManager.Mailpit.StartAsync();

            // ---------- Redis 8 (redis-windows build) ----------
            try
            {
                var r8 = Catalog.Recommended("redis");
                await Installer.InstallAsync(r8, null, CancellationToken.None);
                await ServiceManager.Redis.StopAsync();
                s.SetVersion("redis", r8.Folder); s.Save();
                await ServiceManager.Redis.StartAsync();
                Check("Redis " + r8.Version + " runs", ServiceManager.Redis.State == ServiceState.Running, ServiceManager.Redis.LastError ?? "");
            }
            catch (Exception ex) { Check("Redis 8 runs", false, ex.Message); }

            // ---------- share on the internet (Cloudflare quick tunnel) ----------
            try
            {
                var demoSite = Sites.Scan().First(x => x.Name == "demo");
                var publicUrl = await Share.StartAsync(demoSite, t => Console.WriteLine("    | " + t));
                string page = "";
                for (int i = 0; i < 12 && !page.Contains("DEMO-OK"); i++) { await Task.Delay(5000); page = Get(publicUrl, null); }
                Check("site shared on the internet", page.Contains("DEMO-OK"), publicUrl);
                Share.Stop(demoSite);
            }
            catch (Exception ex) { Check("site shared on the internet", false, ex.Message); }

            // ---------- switch to Nginx ----------
            await ServiceManager.SwitchWebServerAsync("nginx");
            Check("Nginx running", ServiceManager.Nginx.State == ServiceState.Running, ServiceManager.Nginx.LastError ?? "");
            Check("Apache stopped", ServiceManager.Apache.State == ServiceState.Stopped);
            await WebChecks("Nginx", php, php84);
            await ServiceManager.SwitchWebServerAsync("apache");

            // ---------- stop ----------
            await ServiceManager.StopAllAsync();
            await Task.Delay(1000);
            foreach (var port in new[] { s.HttpPort, s.HttpsPort, s.MysqlPort, s.RedisPort, s.MailpitWebPort, s.PhpBasePort })
                Check("port " + port + " released", !ProcessUtil.IsPortInUse(port));

            // ---------- port guard: a taken port is reported with its owner and a free suggestion ----------
            var redisBlocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, s.RedisPort);
            redisBlocker.Start();
            var conflicts = PortGuard.Check(new Service[] { ServiceManager.Redis });
            var me = Process.GetCurrentProcess().Id;
            Check("port conflict detected with owner", conflicts.Count == 1 && conflicts[0].Owner.Pid == me, conflicts.Count == 1 ? conflicts[0].Owner + " " + conflicts[0].Owner.Path : "none");
            Check("free port suggested", conflicts.Count == 1 && conflicts[0].Suggested > 0 && !ProcessUtil.IsPortInUse(conflicts[0].Suggested), conflicts.Count == 1 ? conflicts[0].Suggested.ToString() : "");
            var oldRedis = s.RedisPort;
            PortGuard.UseSuggestions(conflicts);
            Check("suggested port applied to settings", Settings.Current.RedisPort == (conflicts.Count == 1 ? conflicts[0].Suggested : -1));
            await ServiceManager.Redis.StartAsync();
            Check("Redis starts on the new port", ServiceManager.Redis.State == ServiceState.Running, ServiceManager.Redis.PortText);
            await ServiceManager.Redis.StopAsync();
            redisBlocker.Stop();
            s.RedisPort = oldRedis; s.Save();

            // ---------- port guard: a leftover service from bin\ (e.g. after a crash) is closed silently ----------
            var orphan = Process.Start(new ProcessStartInfo(Path.Combine(PackageTypes.ActiveDir("redis"), "redis-server.exe"), "--port " + s.RedisPort) { UseShellExecute = false, CreateNoWindow = true });
            await Task.Delay(1500);
            var orphanConflicts = PortGuard.Check(new Service[] { ServiceManager.Redis });
            Check("leftover Portly service closed automatically", orphanConflicts.Count == 0 && orphan.WaitForExit(3000) && !ProcessUtil.IsPortInUse(s.RedisPort));

            // ---------- self update: apply the release zip from dist\ ----------
            var releaseZip = Directory.GetFiles(Path.Combine(Paths.Root, "..", "..", "dist"), "Portly-*-win10.zip").FirstOrDefault();
            if (releaseZip != null)
            {
                var before = File.GetLastWriteTimeUtc(Path.Combine(Paths.Root, "Portly.exe"));
                Updater.InstallZip(releaseZip, Path.Combine(Paths.Tmp, "update-test"));
                Check("self update replaces Portly.exe (old one kept as .old)", File.Exists(Path.Combine(Paths.Root, "Portly.exe.old")) && File.Exists(Path.Combine(Paths.Root, "Portly.exe")));
                Check("self update keeps user data", File.Exists(Paths.SettingsFile) && Directory.Exists(Path.Combine(Paths.Root, "www", "demo")) && PackageTypes.Installed("php").Count >= 2);
                Check("self update brings languages", File.Exists(Path.Combine(Paths.Lang, "de.json")));
            }
            else Console.WriteLine("  (skip self update: run build.ps1 first)");

            Console.WriteLine();
            Console.WriteLine("PASSED " + _ok + "  FAILED " + _fail);
            return _fail == 0 ? 0 : 1;
        }

        static async Task WebChecks(string server, PackageEntry php, string php84)
        {
            var s = Settings.Current;
            await Task.Delay(300);
            var demo = Get("http://127.0.0.1:" + s.HttpPort + "/", "demo.localhost");
            Check(server + ": http demo site + PHP " + php.Version, demo.Contains("DEMO-OK php=" + php.Version), demo);
            Check(server + ": PHP -> MySQL", demo.Contains("db=" + PackageTypes.Label(PackageTypes.ActiveFolder("mysql")).Split('.')[0]), demo);
            Check(server + ": upload_progress off at runtime", System.Text.RegularExpressions.Regex.IsMatch(demo, "upload_progress=0?$"), demo);
            var acme = Get("https://127.0.0.1:" + s.HttpsPort + "/", "acme.clients.localhost");
            Check(server + ": project inside a group folder (acme.clients)", acme.Contains("ACME-OK"), acme.Length > 80 ? acme.Substring(0, 80) : acme);
            var legacy = Get("http://127.0.0.1:" + s.HttpPort + "/", "legacyapp.localhost");
            Check(server + ": per-site PHP version (" + PackageTypes.Label(php84) + ")", legacy.Contains("LEGACY php=" + PackageTypes.Label(php84)), legacy);
            var https = Get("https://127.0.0.1:" + s.HttpsPort + "/", "demo.localhost");
            Check(server + ": https with Portly CA certificate", https.Contains("DEMO-OK") && _lastIssuer.Contains("Portly Local CA"), _lastIssuer);
            var dash = Get("http://127.0.0.1:" + s.HttpPort + "/", "localhost");
            Check(server + ": dashboard lists sites", dash.Contains("demo.localhost") && dash.Contains("legacyapp"), dash.Length + " bytes");
            var adminer = Get("http://127.0.0.1:" + s.HttpPort + "/adminer/", "localhost");
            Check(server + ": /adminer works", adminer.Contains("Adminer"), adminer.Length + " bytes");
            var pma = Get("http://127.0.0.1:" + s.HttpPort + "/phpmyadmin/", "localhost");
            Check(server + ": /phpmyadmin works (PHP " + PackageTypes.Label(PackageTypes.ActiveFolder("php")) + ")", pma.Contains("phpMyAdmin") && !pma.Contains("Fatal error"), pma.Length + " bytes");
        }

        static string _lastIssuer = "";

        static string Get(string url, string host)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                if (host != null) req.Host = host;
                req.KeepAlive = false; // one connection per request, like a browser per site (avoids 421 on reuse)
                req.Timeout = 30000;
                req.ServerCertificateValidationCallback = (o, cert, chain, errors) =>
                {
                    _lastIssuer = cert == null ? "" : cert.Issuer;
                    return true;
                };
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                    using (var r = new StreamReader(ex.Response.GetResponseStream())) return "HTTP ERROR " + ((HttpWebResponse)ex.Response).StatusCode + ": " + r.ReadToEnd();
                return "ERROR " + ex.Message;
            }
        }
    }
}
