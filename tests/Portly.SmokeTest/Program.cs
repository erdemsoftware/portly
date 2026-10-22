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
    static partial class Program
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
            s.PostgresPort = 5477; s.MongoPort = 27077; s.MemcachedPort = 11277;
            s.DbEngine = "mysql"; s.MailTool = "mailpit";
            s.PhpMaxRequests = 5; s.PhpChildren = 2;
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
            Check("sites scanned", sites.Count(x => x.Valid && x.Group == "" && !x.IsGroup && x.Name != "wpdemo") == 3, string.Join(", ", sites.Select(x => x.Name + (x.Valid ? "" : "(invalid)"))));
            Check("Laravel-style public folder detected", sites.First(x => x.Name == "legacyapp").DocRoot.EndsWith("public"));
            var php84 = PackageTypes.Installed("php").First(f => PackageTypes.Branch(f) == "8.4");
            Sites.SetPhp("legacyapp", php84);

            // older test php.ini files predate pgsql in the default extension list
            PhpConfig.ToggleExtension(phpDir, "pdo_pgsql", true);
            PhpConfig.ToggleExtension(phpDir, "pgsql", true);

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

            // a run that stopped half-way may have left a root password behind: reset it first
            if (!Database.Sql("SELECT 1;").Ok)
            {
                var resetErr = await ServiceManager.MySql.ResetRootPasswordAsync();
                Console.WriteLine("    leftover root password reset: " + (resetErr ?? "ok"));
            }

            Check("PHP moved to a free FastCGI port when its port was taken", ServiceManager.Php.PortFor(php.Folder) != ((IPEndPoint)phpBlocker.LocalEndpoint).Port, "port " + ServiceManager.Php.PortFor(php.Folder));
            await WebChecks("Apache", php, php84);
            phpBlocker.Stop();

            // ---------- PHP workers are recycled after PhpMaxRequests (5): 40 requests must all succeed ----------
            int okCount = 0;
            for (int i = 0; i < 40; i++) if (Get("http://127.0.0.1:" + s.HttpPort + "/", "legacyapp.localhost").Contains("LEGACY")) okCount++;
            Check("PHP worker recycling without errors (40 requests, recycle every 5)", okCount == 40, okCount + "/40");

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

            // ---------- forgotten root password ----------
            try
            {
                var alter = Database.Sql("ALTER USER 'root'@'localhost' IDENTIFIED BY 'Gizli-123';");
                if (!alter.Ok) Console.WriteLine("    ALTER USER failed: " + alter.Output.Trim());
                s.MysqlRootPassword = "wrong"; s.Save();
                var before = Database.Sql("SELECT 1;");
                var err = await ServiceManager.MySql.ResetRootPasswordAsync();
                var after = Database.Sql("SELECT 1;");
                Check("reset forgotten root password", !before.Ok && err == null && after.Ok, err ?? "");
            }
            catch (Exception ex) { Check("reset forgotten root password", false, ex.Message); }

            // ---------- MariaDB as the database engine ----------
            try
            {
                var maria = Catalog.Recommended("mariadb");
                await Installer.InstallAsync(maria, null, CancellationToken.None);
                await ServiceManager.MySql.StopAsync();
                s.DbEngine = "mariadb"; s.Save();
                await ServiceManager.MySql.StartAsync();
                Check("MariaDB " + maria.Version + " runs", ServiceManager.MySql.State == ServiceState.Running, ServiceManager.MySql.LastError ?? "");
                Check("MariaDB: create database", Database.CreateDatabase("maria_db") == null && Database.ListDatabases().Contains("maria_db"));
                await ServiceManager.MySql.StopAsync();
            }
            catch (Exception ex) { Check("MariaDB runs", false, ex.Message); }
            s.DbEngine = "mysql"; s.Save();
            await ServiceManager.MySql.StartAsync();

            // ---------- PostgreSQL, MongoDB, Memcached ----------
            foreach (var pair in new[] { Tuple.Create("postgresql", (Service)ServiceManager.Postgres), Tuple.Create("mongodb", (Service)ServiceManager.Mongo), Tuple.Create("memcached", (Service)ServiceManager.Memcached) })
            {
                try
                {
                    var entry = Catalog.Recommended(pair.Item1);
                    await Installer.InstallAsync(entry, null, CancellationToken.None);
                    await pair.Item2.StartAsync();
                    Check(entry.TypeName + " " + entry.Version + " runs", pair.Item2.State == ServiceState.Running, pair.Item2.LastError ?? "");
                }
                catch (Exception ex) { Check(pair.Item1 + " runs", false, ex.Message); }
            }
            try
            {
                var pgBin = Path.Combine(PackageTypes.ActiveDir("postgresql"), "bin");
                var q = ProcessUtil.Run(Path.Combine(pgBin, "psql.exe"), "-h 127.0.0.1 -p " + s.PostgresPort + " -U postgres -t -c \"select 40+2\"", pgBin, null, 30000);
                Check("PostgreSQL answers SQL", q.Output.Contains("42"), q.Output.Trim());
                var pg = Get("http://127.0.0.1:" + s.HttpPort + "/", "demo.localhost");
                File.WriteAllText(Path.Combine(www, "demo", "pg.php"), "<?php $c = new PDO('pgsql:host=127.0.0.1;port=" + s.PostgresPort + ";dbname=postgres', 'postgres', ''); echo 'PG=' . $c->query('select 7*6')->fetchColumn();");
                var pgPage = Get("http://127.0.0.1:" + s.HttpPort + "/pg.php", "demo.localhost");
                Check("PHP -> PostgreSQL (pdo_pgsql)", pgPage.Contains("PG=42"), pgPage.Length > 120 ? pgPage.Substring(0, 120) : pgPage);
            }
            catch (Exception ex) { Check("PostgreSQL answers SQL", false, ex.Message); }
            await ServiceManager.Postgres.StopAsync();
            await ServiceManager.Mongo.StopAsync();
            await ServiceManager.Memcached.StopAsync();

            // ---------- one-click WordPress (download, database, wp-config, admin account) ----------
            try
            {
                if (Directory.Exists(Path.Combine(www, "wpdemo"))) Directory.Delete(Path.Combine(www, "wpdemo"), true);
                var msg = await ProjectCreator.CreateAsync("wpdemo", "wordpress", true, true, t => Console.WriteLine("    | " + t), CancellationToken.None);
                await ServiceManager.ReloadWebAsync();
                var login = Get("https://127.0.0.1:" + s.HttpsPort + "/wp-login.php", "wpdemo.localhost");
                Check("WordPress installed in one click", msg.Contains("admin") && login.Contains("user_login"), msg.Replace("\n", " | ") + " || login: " + (login.Length > 200 ? login.Substring(0, 200) : login));
            }
            catch (Exception ex) { Check("WordPress installed in one click", false, ex.Message); }

            // ---------- tools: Git (self-extracting), HeidiSQL, SQLite, Bun, Deno ----------
            foreach (var tool in new[] { "git", "heidisql", "sqlite", "bun", "deno" })
            {
                try
                {
                    var entry = Catalog.Recommended(tool);
                    await Installer.InstallAsync(entry, null, CancellationToken.None);
                    Check("install " + entry.TypeName + " " + entry.Version, PackageTypes.ActiveDir(tool) != null);
                }
                catch (Exception ex) { Check("install " + tool, false, ex.Message); }
            }
            var gitV = ProcessUtil.Run(Path.Combine(PackageTypes.ActiveDir("git") ?? Paths.Root, "cmd", "git.exe"), "--version", null, null, 20000);
            Check("git runs", gitV.Output.Contains("git version"), gitV.Output);

            // ---------- share on the internet (Cloudflare quick tunnel) ----------
            try
            {
                var demoSite = Sites.Scan().First(x => x.Name == "demo");
                var publicUrl = await Share.StartAsync(demoSite, t => Console.WriteLine("    | " + t));
                string page = "";
                for (int i = 0; i < 24 && !page.Contains("DEMO-OK"); i++) { await Task.Delay(5000); page = Get(publicUrl, null); }
                Check("site shared on the internet", page.Contains("DEMO-OK"), publicUrl);
                Share.Stop(demoSite);
                var wpSite = Sites.Scan().FirstOrDefault(x => x.Name == "wpdemo");
                if (wpSite != null)
                {
                    var wpUrl = await Share.StartAsync(wpSite, t => { });
                    string wpPage = "";
                    for (int i = 0; i < 24 && !wpPage.Contains("trycloudflare"); i++) { await Task.Delay(5000); wpPage = Get(wpUrl + "/", null); }
                    Check("shared WordPress keeps the public address (no redirect to .localhost)", wpPage.Contains(new Uri(wpUrl).Host), wpUrl + " " + (wpPage.Length > 100 ? wpPage.Substring(0, 100) : wpPage));
                    Share.Stop(wpSite);
                }
            }
            catch (Exception ex) { Check("site shared on the internet", false, ex.Message); }

            // ---------- feature groups (one file each: Tests.<Group>.cs) ----------
            foreach (var group in new Func<Settings, string, Task>[] { Tests_PhpTooling, Tests_Database, Tests_Updates, Tests_SitesWeb, Tests_DevWorkflow, Tests_Platform })
            {
                try { await group(s, www); }
                catch (Exception ex) { Check(group.Method.Name, false, ex.GetType().Name + ": " + ex.Message); }
                // a group may stop or reconfigure services: leave the stack as the next group expects it
                if (ServiceManager.Web.State != ServiceState.Running) await ServiceManager.StartWebAsync();
                if (ServiceManager.MySql.State != ServiceState.Running) await ServiceManager.MySql.StartAsync();
            }

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

            // ---------- move the setup to another computer: export, change, import ----------
            try
            {
                var zip = Path.Combine(Paths.Tmp, "transfer-test.zip");
                var themeBefore = s.Theme;
                SettingsTransfer.Export(zip);
                string[] entries;
                using (var z = System.IO.Compression.ZipFile.OpenRead(zip)) entries = z.Entries.Select(e => e.FullName).ToArray();
                var exportedIni = "";
                using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                using (var r = new StreamReader(z.GetEntry("portly.ini").Open())) exportedIni = r.ReadToEnd();
                Check("settings export has settings and package list, no password", entries.Contains("portly.ini") && entries.Contains("packages.ini") && !exportedIni.Contains("RootPassword"), string.Join(", ", entries));
                s.Theme = themeBefore == "dark" ? "light" : "dark"; s.Save();
                var imported = SettingsTransfer.Import(zip);
                Check("settings import restores settings, nothing missing", Settings.Current.Theme == themeBefore && imported.Missing.Count == 0, "missing " + imported.Missing.Count + ", theme " + Settings.Current.Theme);
                File.Delete(zip);
            }
            catch (Exception ex) { Check("settings export / import", false, ex.Message); }

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
                req.Proxy = null; // never send local test traffic to a system proxy
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
