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

        static Service[] AllServices { get { return new Service[] { Php, Apache, Nginx, MySql, Postgres, Mongo, Redis, Memcached, Mailpit }; } }

        public static event Action<Service> Changed;
        public static event Action SitesChanged;
        public static List<Site> CurrentSites = new List<Site>();
        static string _refusedHostsKey;

        static ServiceManager()
        {
            foreach (var s in AllServices)
                s.Changed += x => { var h = Changed; if (h != null) h(x); };
        }

        public static Service Web { get { return Settings.Current.WebServer == "nginx" ? (Service)Nginx : Apache; } }

        /// <summary>Rows shown in the main window.</summary>
        public static List<Service> Rows { get { return new List<Service> { Web, Php, MySql, Postgres, Mongo, Redis, Memcached, Mailpit }; } }

        public static bool AnyActive
        {
            get { return AllServices.Any(s => s.State != ServiceState.Stopped && s.State != ServiceState.Error); }
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
            await Web.StartAsync();
            if (Web.State == ServiceState.Running) SyncHostsInBackground(sites);
        }

        public static async Task StopWebAsync()
        {
            if (ServiceMode.IsController) { await ServiceMode.CommandAsync("stop", "web"); return; }
            await Web.StopAsync();
            await Php.StopAsync();
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
            if (!s.AutoVhosts || s.Tld == "localhost") return;
            var hosts = sites.Where(x => x.Valid).Select(x => x.Host).ToList();
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

        public static Task StartAllAsync()
        {
            if (ServiceMode.IsController) return ServiceMode.StartAllAsync();
            var s = Settings.Current;
            var tasks = new List<Task>();
            if (s.IsEnabled("web")) tasks.Add(StartWebAsync());
            if (s.IsEnabled("mysql")) tasks.Add(MySql.StartAsync());
            if (s.IsEnabled("redis")) tasks.Add(Redis.StartAsync());
            if (s.IsEnabled("mailpit")) tasks.Add(Mailpit.StartAsync());
            if (s.IsEnabled("postgresql")) tasks.Add(Postgres.StartAsync());
            if (s.IsEnabled("mongodb")) tasks.Add(Mongo.StartAsync());
            if (s.IsEnabled("memcached")) tasks.Add(Memcached.StartAsync());
            return Task.WhenAll(tasks);
        }

        public static Task StopAllAsync()
        {
            if (ServiceMode.IsController) return ServiceMode.StopAllAsync();
            return Task.WhenAll(StopWebAsync(), Nginx.StopAsync(), Apache.StopAsync(), MySql.StopAsync(), Redis.StopAsync(), Mailpit.StopAsync(),
                Postgres.StopAsync(), Mongo.StopAsync(), Memcached.StopAsync());
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
