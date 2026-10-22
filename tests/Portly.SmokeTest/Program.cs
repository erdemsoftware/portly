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
                Catalog.Recommended("adminer"), Catalog.Recommended("composer"),
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
            var sites = Sites.Scan();
            Check("sites scanned", sites.Count(x => x.Valid) == 2, string.Join(", ", sites.Select(x => x.Name + (x.Valid ? "" : "(invalid)"))));
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

            // ---------- mail caught by Mailpit ----------
            var mails = Get("http://127.0.0.1:" + s.MailpitWebPort + "/api/v1/messages", null);
            Check("Mailpit caught PHP mail()", mails.Contains("Portly smoke"));

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
            var legacy = Get("http://127.0.0.1:" + s.HttpPort + "/", "legacyapp.localhost");
            Check(server + ": per-site PHP version (" + PackageTypes.Label(php84) + ")", legacy.Contains("LEGACY php=" + PackageTypes.Label(php84)), legacy);
            var https = Get("https://127.0.0.1:" + s.HttpsPort + "/", "demo.localhost");
            Check(server + ": https with Portly CA certificate", https.Contains("DEMO-OK") && _lastIssuer.Contains("Portly Local CA"), _lastIssuer);
            var dash = Get("http://127.0.0.1:" + s.HttpPort + "/", "localhost");
            Check(server + ": dashboard lists sites", dash.Contains("demo.localhost") && dash.Contains("legacyapp"), dash.Length + " bytes");
            var adminer = Get("http://127.0.0.1:" + s.HttpPort + "/adminer/", "localhost");
            Check(server + ": /adminer works", adminer.Contains("Adminer"), adminer.Length + " bytes");
        }

        static string _lastIssuer = "";

        static string Get(string url, string host)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                if (host != null) req.Host = host;
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
