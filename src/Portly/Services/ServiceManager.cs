using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;

namespace Portly.Services
{
    public static class ServiceManager
    {
        public static readonly PhpService Php = new PhpService();
        public static readonly ApacheService Apache = new ApacheService();
        public static readonly NginxService Nginx = new NginxService();
        public static readonly MySqlService MySql = new MySqlService();
        public static readonly RedisService Redis = new RedisService();
        public static readonly MailpitService Mailpit = new MailpitService();
        public static readonly PostgresService Postgres = new PostgresService();
        public static readonly MongoService Mongo = new MongoService();
        public static readonly MemcachedService Memcached = new MemcachedService();
        public static readonly CaddyService Caddy = new CaddyService();
        public static readonly FrankenPhpService FrankenPhp = new FrankenPhpService();
        public static readonly TomcatService Tomcat = new TomcatService();

        static Service[] AllServices { get { return new Service[] { Php, Apache, Nginx, Caddy, FrankenPhp, MySql, Postgres, Mongo, Redis, Memcached, Mailpit, Tomcat }; } }

        public static event Action<Service> Changed;
        public static event Action SitesChanged;
        public static List<Site> CurrentSites = new List<Site>();
        static string _refusedHostsKey;

        static ServiceManager()
        {
            foreach (var s in AllServices)
                s.Changed += x => { var h = Changed; if (h != null) h(x); };
            WebConfig.ProxyPort = FrankenPhp.PortOf;
        }

        public static Service Web { get { var w = Settings.Current.WebServer; return w == "nginx" ? (Service)Nginx : w == "caddy" ? (Service)Caddy : Apache; } }

        /// <summary>Rows shown in the main window.</summary>
        public static List<Service> Rows
        {
            get
            {
                var rows = new List<Service> { Web, Php, MySql, Postgres, Mongo, Redis, Memcached, Mailpit };
                if (TomcatService.Enabled) rows.Add(Tomcat); // optional feature
                return rows;
            }
        }

        public static bool AnyActive
        {
            get { return AllServices.Any(s => s.State != ServiceState.Stopped && s.State != ServiceState.Error); }
        }

        /// <summary>Services on the main window with their "Bulk" switch on (what Start all / Stop all act on).</summary>
        public static List<Service> BulkRows
        {
            get { var s = Settings.Current; return Rows.Where(x => x.Id != "php" && s.IsEnabled(x.Id)).ToList(); }
        }

        /// <summary>Is any service of the bulk group running? Decides between "Start all" and "Stop all".</summary>
        public static bool AnyBulkActive
        {
            get { return BulkRows.Any(x => x.State != ServiceState.Stopped && x.State != ServiceState.Error); }
        }

        /// <summary>
        /// Stop all: only the services in the bulk group; ones the user started by hand with "Bulk" off keep running.
        /// (Closing Portly or Windows shutting down still stops everything through StopAllAsync.)
        /// </summary>
        public static Task StopBulkAsync()
        {
            if (ServiceMode.IsController) return ServiceMode.StopAllAsync();
            return Task.Run(async () =>
            {
                var list = BulkRows.Where(x => x.State != ServiceState.Stopped && x.State != ServiceState.Error).ToList();
                if (list.Count == 0) return;
                await Tools.Hooks.RunAsync("before-stop");
                var tasks = new List<Task>();
                foreach (var svc in list)
                {
                    // the web server takes its PHP pools and the site jobs with it
                    if (svc.Id == "web") { await Tools.Jobs.StopAllAsync(); tasks.Add(StopWebAsync()); }
                    else tasks.Add(svc.StopAsync());
                }
                await Task.WhenAll(tasks);
                await Tools.Hooks.RunAsync("after-stop");
            });
        }

        public static List<Site> RefreshSites()
        {
            CurrentSites = Sites.Scan();
            var h = SitesChanged; if (h != null) h();
            return CurrentSites;
        }

        public static async Task StartWebAsync()
        {
            if (ServiceMode.IsController) { await ServiceMode.CommandAsync("start", "web"); return; }
            var sites = await Task.Run(() => RefreshSites());
            var needed = Sites.NeededPhpFolders(sites);
            if (needed.Count > 0)
            {
                // stop PHP versions that are no longer used
                if (Php.RunningFolders.Any(f => !needed.Contains(f, StringComparer.OrdinalIgnoreCase)))
                    await Php.StopAsync();
                await Php.StartAsync(needed);
            }
            Apache.Sites = sites; Apache.PhpPort = Php.PortFor;
            Nginx.Sites = sites; Nginx.PhpPort = Php.PortFor;
            Caddy.Sites = sites; Caddy.PhpPort = Php.PortFor;
            // FrankenPHP sites first: the web server config forwards to their ports
            await FrankenPhp.StartAsync(sites);
            await Web.StartAsync();
            if (Web.State == ServiceState.Running) SyncHostsInBackground(sites);
        }

        public static async Task StopWebAsync()
        {
            if (ServiceMode.IsController) { await ServiceMode.CommandAsync("stop", "web"); return; }
            await Web.StopAsync();
            await Php.StopAsync();
            await FrankenPhp.StopAsync();
        }

        /// <summary>Re-generate configuration and restart the web server if it runs (new site, PHP switch ...).</summary>
        public static async Task ReloadWebAsync()
        {
            if (ServiceMode.IsController)
            {
                await Task.Run(() => RefreshSites());
                await ServiceMode.CommandAsync("reloadweb");
                return;
            }
            if (Web.IsActive || Php.IsActive)
            {
                await StopWebAsync();
                await StartWebAsync();
            }
            else await Task.Run(() => RefreshSites());
        }

        public static void SyncHostsInBackground(List<Site> sites)
        {
            var s = Settings.Current;
            if (!s.AutoVhosts) return;
            // site names and aliases unless they end in .localhost (browsers resolve those); wildcards cannot go into the
            // hosts file at all. A real domain (example.com) set to "live" is left out, so this PC opens the live site.
            var hosts = sites.Where(x => x.Valid && !x.ShowLive).Select(x => x.Host).Where(HostsFile.NeedsEntry).ToList();
            hosts.AddRange(sites.Where(x => x.Valid && !x.ShowLive).SelectMany(x => x.Aliases).Where(HostsFile.NeedsEntry));
            // the local name of a real domain (example.com.portly) stays, also in Live mode: the local copy keeps working
            hosts.AddRange(sites.Where(x => x.Valid && x.LocalHost != "").Select(x => x.LocalHost).Where(HostsFile.NeedsEntry));
            if (s.Tld == "localhost" && hosts.Count == 0 && !HostsFile.HasPortlyBlock()) return;
            var key = string.Join(",", hosts);
            if (key == _refusedHostsKey) return;
            Task.Run(() =>
            {
                if (!HostsFile.Sync(hosts)) _refusedHostsKey = key;
            });
        }

        public static Task StartAsync(Service s)
        {
            // service mode: the Windows service runs the programs, the GUI only asks it
            if (ServiceMode.IsController) return ServiceMode.CommandAsync("start", s.Id);
            if (s.Id == "web" || s.Id == "php") return StartWebAsync();
            return s.StartAsync();
        }

        public static Task StopAsync(Service s)
        {
            if (ServiceMode.IsController) return ServiceMode.CommandAsync("stop", s.Id);
            if (s.Id == "web" || s.Id == "php") return StopWebAsync();
            return s.StopAsync();
        }

        /// <summary>Restart one service (version switch); in service mode the Windows service does it.</summary>
        public static Task RestartAsync(Service s)
        {
            if (ServiceMode.IsController) return ServiceMode.CommandAsync("restart", s.Id);
            return s.RestartAsync();
        }

        public static async Task StartAllAsync()
        {
            if (ServiceMode.IsController) { await ServiceMode.StartAllAsync(); return; }
            await Tools.Hooks.RunAsync("before-start");
            var s = Settings.Current;
            var tasks = new List<Task>();
            if (s.IsEnabled("web")) tasks.Add(StartWebAsync());
            if (s.IsEnabled("mysql")) tasks.Add(MySql.StartAsync());
            if (s.IsEnabled("redis")) tasks.Add(Redis.StartAsync());
            if (s.IsEnabled("mailpit")) tasks.Add(Mailpit.StartAsync());
            if (s.IsEnabled("postgresql")) tasks.Add(Postgres.StartAsync());
            if (s.IsEnabled("mongodb")) tasks.Add(Mongo.StartAsync());
            if (s.IsEnabled("memcached")) tasks.Add(Memcached.StartAsync());
            if (TomcatService.Enabled && s.IsEnabled("tomcat")) tasks.Add(Tomcat.StartAsync());
            await Task.WhenAll(tasks);
            // the database is up now: phpMyAdmin can count the accounts (auto login only with root alone and no password)
            Config.ToolApps.RefreshPhpMyAdmin();
            await Tools.Hooks.RunAsync("after-start");
            Tools.Jobs.StartAutostart(CurrentSites);
        }

        public static Task StopAllAsync()
        {
            if (ServiceMode.IsController) return ServiceMode.StopAllAsync();
            // on a thread-pool thread: Windows shutdown waits on this with .Wait() from the UI thread
            return Task.Run(async () =>
            {
                bool any = AnyActive;
                if (any) await Tools.Hooks.RunAsync("before-stop");
                await Tools.Jobs.StopAllAsync();
                await Task.WhenAll(StopWebAsync(), Nginx.StopAsync(), Apache.StopAsync(), Caddy.StopAsync(), MySql.StopAsync(), Redis.StopAsync(), Mailpit.StopAsync(),
                    Postgres.StopAsync(), Mongo.StopAsync(), Memcached.StopAsync(), Tomcat.StopAsync());
                if (any) await Tools.Hooks.RunAsync("after-stop");
            });
        }

        public static async Task SwitchWebServerAsync(string server)
        {
            if (server == Settings.Current.WebServer) return;
            if (ServiceMode.IsController)
            {
                Settings.Current.WebServer = server;
                Settings.Current.Save();
                await ServiceMode.CommandAsync("reload");
                return;
            }
            bool wasRunning = Web.IsActive;
            await StopWebAsync();
            Settings.Current.WebServer = server;
            Settings.Current.Save();
            if (wasRunning) await StartWebAsync();
        }
    }
}
