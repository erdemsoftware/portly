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
    /// Caddy as the web server: etc\caddy\Caddyfile is generated from templates (caddy-Caddyfile.tpl, caddy-site.tpl),
    /// then converted with "caddy adapt" to etc\caddy\caddy.json, which Caddy runs.
    /// The JSON step removes Caddy's own certificate automation: Caddy would otherwise create its own local CA for
    /// *.localhost names and try to install it into Windows' trusted roots. Portly's per-site certificates are used.
    /// The admin API is off; Portly stops Caddy by ending the process.
    /// </summary>
    public static class CaddyConfig
    {
        public static string Dir { get { return Path.Combine(Paths.Etc, "caddy"); } }
        public static string Caddyfile { get { return Path.Combine(Dir, "Caddyfile"); } }
        public static string JsonFile { get { return Path.Combine(Dir, "caddy.json"); } }

        static string Q(string path) { return "\"" + Paths.Fwd(path) + "\""; }

        public static void WriteCaddyfile(List<Site> sites, Func<string, int> phpPort)
        {
            var s = Settings.Current;
            var certs = new Dictionary<string, Tuple<string, string>>();
            bool ssl = WebConfig.PrepareSsl(sites, certs);
            var defaultPhp = PackageTypes.ActiveFolder("php");
            var v = Templates.CommonVars();
            v["DASHBOARD"] = Paths.Fwd(ToolApps.DashboardDir);
            v["DEFAULT_BIND"] = s.AllowLan ? "" : "default_bind 127.0.0.1" + (Net.LoopbackV6 ? " [::1]" : "");
            v["PHP"] = defaultPhp == "" ? "" : "php_fastcgi 127.0.0.1:" + phpPort(defaultPhp);

            // /adminer, /phpmyadmin on http://localhost only (the dashboard block answers local addresses only)
            var tools = new StringBuilder();
            foreach (var tool in ToolApps.WebTools())
            {
                tools.Append("\tredir /").Append(tool.Item1).Append(" /").Append(tool.Item1).Append("/\r\n");
                tools.Append("\thandle_path /").Append(tool.Item1).Append("/* {\r\n\t\troot * ").Append(Q(tool.Item2)).Append("\r\n");
                if (defaultPhp != "") tools.Append("\t\tphp_fastcgi 127.0.0.1:").Append(phpPort(defaultPhp)).Append("\r\n");
                tools.Append("\t\tfile_server\r\n\t}\r\n");
            }
            v["TOOLS"] = tools.ToString();
            v["DEFAULT_SSL"] = ssl ? "https://localhost:" + s.HttpsPort + ", https://127.0.0.1:" + s.HttpsPort + " {\r\n\ttls " + Q(certs["localhost"].Item1) + " " + Q(certs["localhost"].Item2) + "\r\n\timport portly_dashboard\r\n}" : "";

            var siteText = new StringBuilder();
            if (s.AutoVhosts)
            {
                foreach (var site in sites.Where(x => x.Valid))
                {
                    int proxy = WebConfig.ProxyPort(site);
                    string body;
                    if (proxy > 0) body = "\treverse_proxy 127.0.0.1:" + proxy;
                    else
                    {
                        body = "\troot * " + Q(site.DocRoot) + "\r\n\t@hidden path */.ht* */.git/*\r\n\trespond @hidden 403\r\n" +
                               (site.PhpFolder == "" ? "" : "\tphp_fastcgi 127.0.0.1:" + phpPort(site.PhpFolder) + "\r\n") +
                               "\tfile_server browse";
                    }
                    var names = site.AllNames;
                    var sv = new Dictionary<string, string>(v)
                    {
                        { "HOST", site.Host },
                        { "ID", "site_" + site.Host.Replace('.', '_').Replace('*', 'x') },
                        { "BODY", body },
                        { "HTTP_ADDRESSES", string.Join(", ", names.Select(n => "http://" + n + ":" + s.HttpPort)) },
                        { "HTTPS_BLOCK", ssl && certs.ContainsKey(site.Host)
                            ? string.Join(", ", names.Select(n => "https://" + n + ":" + s.HttpsPort)) + " {\r\n\ttls " + Q(certs[site.Host].Item1) + " " + Q(certs[site.Host].Item2) + "\r\n\timport " + "site_" + site.Host.Replace('.', '_').Replace('*', 'x') + "\r\n}"
                            : "" },
                    };
                    siteText.Append(Templates.Render("caddy-site.tpl", sv)).Append("\r\n");
                }
            }
            v["SITES"] = siteText.ToString();
            Templates.WriteIfChanged(Caddyfile, Templates.Render("caddy-Caddyfile.tpl", v));
            ToolApps.WriteDashboard(sites, ssl);
        }

        /// <summary>"caddy adapt" + remove certificate automation. Returns null or an error text.</summary>
        public static string Adapt(string caddyExe)
        {
            var r = ProcessUtil.Run(caddyExe, "adapt --config \"" + Caddyfile + "\" --adapter caddyfile --validate", Dir, null, 60000);
            Dictionary<string, object> config = null;
            var messages = new List<string>();
            foreach (var line in r.Output.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("{")))
            {
                try
                {
                    var o = Json.ParseObject(line);
                    if (o.ContainsKey("apps")) config = o;
                    else if (o.Str("level") == "error" || o.Str("level") == "warn") messages.Add(o.Str("msg"));
                }
                catch { }
            }
            if (!r.Ok || config == null) return I18n.T("err.config") + "\n" + (messages.Count > 0 ? string.Join("\n", messages) : r.Output);
            Patch(config);
            Templates.WriteIfChanged(JsonFile, Json.Serialize(config));
            return null;
        }

        /// <summary>No automatic certificates and never touch the Windows trust store (Portly's own CA is used).</summary>
        public static void Patch(Dictionary<string, object> config)
        {
            var apps = config["apps"] as Dictionary<string, object>;
            if (apps == null) return;
            object tls;
            if (apps.TryGetValue("tls", out tls) && tls is Dictionary<string, object>) ((Dictionary<string, object>)tls).Remove("automation");
            apps["pki"] = new Dictionary<string, object>
            {
                { "certificate_authorities", new Dictionary<string, object> { { "local", new Dictionary<string, object> { { "install_trust", false } } } } },
            };
        }
    }
}
