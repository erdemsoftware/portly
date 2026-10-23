using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Portly.Core;
using Portly.Packages;

namespace Portly.Config
{
    /// <summary>
    /// Generates the whole Apache or Nginx configuration under etc\ from templates.
    /// The original files shipped with Apache/Nginx are never modified.
    /// Files in etc\apache\sites (or etc\nginx\sites) whose name starts with "auto." belong to Portly;
    /// any other .conf file you put there is yours and is kept.
    /// </summary>
    public static class WebConfig
    {
        public static string ApacheConf { get { return Path.Combine(Paths.Etc, "apache", "httpd.conf"); } }
        public static string NginxConf { get { return Path.Combine(Paths.Etc, "nginx", "nginx.conf"); } }
        static string ApacheSites { get { return Path.Combine(Paths.Etc, "apache", "sites"); } }
        static string ApacheAlias { get { return Path.Combine(Paths.Etc, "apache", "alias"); } }
        static string NginxSites { get { return Path.Combine(Paths.Etc, "nginx", "sites"); } }
        static string NginxAlias { get { return Path.Combine(Paths.Etc, "nginx", "alias"); } }

        /// <summary>Returns false when SSL could not be prepared (then HTTPS is skipped this time).</summary>
        internal static bool PrepareSsl(List<Site> sites, Dictionary<string, Tuple<string, string>> certs)
        {
            if (!Settings.Current.Ssl) return false;
            try
            {
                certs["localhost"] = Certs.EnsureSite("localhost");
                foreach (var s in sites.Where(x => x.Valid))
                {
                    // the site's own certificate (Site menu > Certificate...) wins over the one Portly signs
                    var own = CustomCerts.For(s);
                    if (own != null)
                    {
                        var problem = CustomCerts.Problem(s);
                        if (problem != null) Log.Warn(s.Host + ": " + problem);
                        certs[s.Host] = own;
                    }
                    else certs[s.Host] = Certs.EnsureSite(s.Host, s.AllNames);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(I18n.T("err.ssl"), ex);
                return false;
            }
        }

        static void CleanAuto(string dir, IEnumerable<string> keep)
        {
            Directory.CreateDirectory(dir);
            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(dir, "auto.*.conf"))
                if (!keepSet.Contains(Path.GetFileName(f))) File.Delete(f);
        }

        // =====================================================================
        // Apache
        // =====================================================================
        public static void WriteApache(string apacheDir, List<Site> sites, Func<string, int> phpPort)
        {
            var s = Settings.Current;
            var certs = new Dictionary<string, Tuple<string, string>>();
            bool ssl = PrepareSsl(sites, certs);
            var defaultPhp = PackageTypes.ActiveFolder("php");
            var v = Templates.CommonVars();
            v["APACHE_DIR"] = Paths.Fwd(apacheDir);
            v["DASHBOARD"] = Paths.Fwd(ToolApps.DashboardDir);
            string ip = s.AllowLan ? "" : "127.0.0.1:";
            v["LISTEN"] = "Listen " + ip + s.HttpPort + (ssl ? "\r\nListen " + ip + s.HttpsPort : "");
            // apps that connect to "localhost" try ::1 first: answer there too instead of a slow fallback
            if (!s.AllowLan && Net.LoopbackV6)
                v["LISTEN"] += "\r\nListen [::1]:" + s.HttpPort + (ssl ? "\r\nListen [::1]:" + s.HttpsPort : "");
            v["PHP_HANDLER"] = defaultPhp == "" ? "# PHP is not installed" : ApachePhp(phpPort(defaultPhp));
            v["DEFAULT_SSL"] = ssl ? Templates.Render("apache-default-ssl.conf.tpl", Merge(v, new Dictionary<string, string>
            {
                { "CRT", Paths.Fwd(certs["localhost"].Item1) }, { "KEY", Paths.Fwd(certs["localhost"].Item2) }
            })) : "";
            Templates.WriteIfChanged(ApacheConf, Templates.Render("apache-httpd.conf.tpl", v));

            // tools (/adminer, /phpmyadmin) on http://localhost only
            Directory.CreateDirectory(ApacheAlias);
            var aliasText = new StringBuilder("# " + v["GENERATED"] + "\r\n");
            foreach (var tool in ToolApps.WebTools())
            {
                aliasText.Append("Alias /").Append(tool.Item1).Append(" \"").Append(Paths.Fwd(tool.Item2)).Append("\"\r\n");
                aliasText.Append("<Directory \"").Append(Paths.Fwd(tool.Item2)).Append("\">\r\n    Options -Indexes\r\n    AllowOverride None\r\n    DirectoryIndex index.php\r\n    Require local\r\n</Directory>\r\n");
            }
            Templates.WriteIfChanged(Path.Combine(ApacheAlias, "auto.tools.conf"), aliasText.ToString());

            var written = new List<string>();
            if (s.AutoVhosts)
            {
                foreach (var site in sites.Where(x => x.Valid))
                {
                    // a FrankenPHP site runs as its own server: Apache only forwards to it
                    int proxy = ProxyPort(site);
                    // a group folder must not swallow the addresses of the projects inside it (AllNames leaves *.group out)
                    var extra = site.AllNames.Where(n => n != site.Host).ToList();
                    var sv = Merge(v, new Dictionary<string, string>
                    {
                        { "HOST", site.Host },
                        { "ALIAS", extra.Count == 0 ? "" : "ServerAlias " + string.Join(" ", extra) },
                        { "SITE_ROOT", Paths.Fwd(site.DocRoot) },
                        { "PHP_HANDLER", proxy > 0 ? ApacheProxy(proxy, false) : site.PhpFolder == "" ? "" : ApachePhp(phpPort(site.PhpFolder)) },
                    });
                    var text = Templates.Render("apache-site.conf.tpl", sv);
                    if (ssl && certs.ContainsKey(site.Host))
                    {
                        if (proxy > 0) sv["PHP_HANDLER"] = ApacheProxy(proxy, true);
                        sv["CRT"] = Paths.Fwd(certs[site.Host].Item1);
                        sv["KEY"] = Paths.Fwd(certs[site.Host].Item2);
                        text += "\r\n" + Templates.Render("apache-site-ssl.conf.tpl", sv);
                    }
                    var name = "auto." + site.Host + ".conf";
                    Templates.WriteIfChanged(Path.Combine(ApacheSites, name), text);
                    written.Add(name);
                }
            }
            CleanAuto(ApacheSites, written);
            ToolApps.WriteDashboard(sites, ssl);
        }

        /// <summary>Port of the FrankenPHP server of a site (0 = served by the PHP FastCGI pool). Set by ServiceManager.</summary>
        public static Func<Site, int> ProxyPort = site => 0;

        static string ApacheProxy(int port, bool https)
        {
            return "ProxyPreserveHost On\r\n    ProxyPass / \"http://127.0.0.1:" + port + "/\"\r\n    ProxyPassReverse / \"http://127.0.0.1:" + port + "/\"" +
                   (https ? "\r\n    RequestHeader set X-Forwarded-Proto \"https\"" : "\r\n    RequestHeader set X-Forwarded-Proto \"http\"");
        }

        static string ApachePhp(int port)
        {
            return "<FilesMatch \"\\.php$\">\r\n        CGIPassAuth On\r\n        SetHandler \"proxy:fcgi://127.0.0.1:" + port + "/\"\r\n    </FilesMatch>";
        }

        // =====================================================================
        // Nginx
        // =====================================================================
        public static void WriteNginx(string nginxDir, List<Site> sites, Func<string, int> phpPort)
        {
            var s = Settings.Current;
            var certs = new Dictionary<string, Tuple<string, string>>();
            bool ssl = PrepareSsl(sites, certs);
            var defaultPhp = PackageTypes.ActiveFolder("php");
            var v = Templates.CommonVars();
            v["NGINX_DIR"] = Paths.Fwd(nginxDir);
            v["DASHBOARD"] = Paths.Fwd(ToolApps.DashboardDir);
            string ip = s.AllowLan ? "" : "127.0.0.1:";
            v["LISTEN_HTTP"] = ip + s.HttpPort;
            v["LISTEN_HTTPS"] = ssl ? "listen " + ip + s.HttpsPort + " ssl default_server;" : "";
            bool v6 = !s.AllowLan && Net.LoopbackV6;
            v["LISTEN_V6"] = v6 ? "listen [::1]:" + s.HttpPort + " default_server;" + (ssl ? " listen [::1]:" + s.HttpsPort + " ssl default_server;" : "") : "";
            v["SSL_CERT"] = ssl ? NginxCert(certs["localhost"]) : "";
            v["PHP_LOCATION"] = defaultPhp == "" ? "" : NginxPhp(nginxDir, phpPort(defaultPhp));
            Templates.WriteIfChanged(NginxConf, Templates.Render("nginx.conf.tpl", v));

            Directory.CreateDirectory(NginxAlias);
            var alias = new StringBuilder("# " + v["GENERATED"] + "\r\n");
            int toolPort = defaultPhp == "" ? 0 : phpPort(defaultPhp);
            foreach (var tool in ToolApps.WebTools())
            {
                var p = Paths.Fwd(tool.Item2).TrimEnd('/') + "/";
                alias.Append("location = /").Append(tool.Item1).Append(" { return 301 /").Append(tool.Item1).Append("/; }\r\n");
                alias.Append("location ^~ /").Append(tool.Item1).Append("/ {\r\n    alias \"").Append(p).Append("\";\r\n    index index.php;\r\n");
                if (toolPort > 0)
                    alias.Append("    location ~ \\.php$ {\r\n        include \"").Append(Paths.Fwd(nginxDir)).Append("/conf/fastcgi_params\";\r\n        fastcgi_param SCRIPT_FILENAME $request_filename;\r\n        fastcgi_pass 127.0.0.1:").Append(toolPort).Append(";\r\n    }\r\n");
                alias.Append("}\r\n");
            }
            Templates.WriteIfChanged(Path.Combine(NginxAlias, "auto.tools.conf"), alias.ToString());

            var written = new List<string>();
            if (s.AutoVhosts)
            {
                foreach (var site in sites.Where(x => x.Valid))
                {
                    bool siteSsl = ssl && certs.ContainsKey(site.Host);
                    int proxy = ProxyPort(site);
                    var sv = Merge(v, new Dictionary<string, string>
                    {
                        { "HOST", site.Host },
                        { "SERVER_NAMES", string.Join(" ", site.AllNames) },
                        { "SITE_ROOT", Paths.Fwd(site.DocRoot) },
                        { "LISTEN_HTTPS", siteSsl ? "listen " + ip + s.HttpsPort + " ssl;" : "" },
                        { "LISTEN_V6", v6 ? "listen [::1]:" + s.HttpPort + ";" + (siteSsl ? " listen [::1]:" + s.HttpsPort + " ssl;" : "") : "" },
                        { "SSL_CERT", siteSsl ? NginxCert(certs[site.Host]) : "" },
                        { "PHP_LOCATION", site.PhpFolder == "" ? "" : NginxPhp(nginxDir, phpPort(site.PhpFolder)) },
                        { "PROXY_PORT", proxy.ToString() },
                    });
                    var name = "auto." + site.Host + ".conf";
                    Templates.WriteIfChanged(Path.Combine(NginxSites, name), Templates.Render(proxy > 0 ? "nginx-site-proxy.conf.tpl" : "nginx-site.conf.tpl", sv));
                    written.Add(name);
                }
            }
            CleanAuto(NginxSites, written);
            ToolApps.WriteDashboard(sites, ssl);
        }

        static string NginxCert(Tuple<string, string> c)
        {
            return "ssl_certificate \"" + Paths.Fwd(c.Item1) + "\";\r\n        ssl_certificate_key \"" + Paths.Fwd(c.Item2) + "\";\r\n        ssl_protocols TLSv1.2 TLSv1.3;";
        }

        static string NginxPhp(string nginxDir, int port)
        {
            return "location ~ [^/]\\.php(/|$) {\r\n" +
                   "            fastcgi_split_path_info ^(.+?\\.php)(/.*)$;\r\n" +
                   "            if (!-f $document_root$fastcgi_script_name) { return 404; }\r\n" +
                   "            include \"" + Paths.Fwd(nginxDir) + "/conf/fastcgi_params\";\r\n" +
                   "            fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;\r\n" +
                   "            fastcgi_param PATH_INFO $fastcgi_path_info;\r\n" +
                   "            fastcgi_pass 127.0.0.1:" + port + ";\r\n" +
                   "        }";
        }

        static Dictionary<string, string> Merge(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            var d = new Dictionary<string, string>(a);
            foreach (var kv in b) d[kv.Key] = kv.Value;
            return d;
        }
    }
}
