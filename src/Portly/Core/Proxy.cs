using System;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace Portly.Core
{
    /// <summary>
    /// Proxy for every download (Settings > General > Proxy).
    ///   system: the Windows proxy (Internet Options / WinINet, also PAC scripts) with the logged-on user's credentials
    ///   none:   direct connection
    ///   manual: host:port, optional user / password (DPAPI protected in portly.ini)
    /// </summary>
    public static class Proxy
    {
        /// <summary>The proxy to put on a WebClient / HttpWebRequest. null means "direct".</summary>
        public static IWebProxy Current()
        {
            var s = Settings.Current;
            return Create(s == null ? "system" : s.ProxyMode, s == null ? "" : s.ProxyHost, s == null ? "" : s.ProxyUser, s == null ? "" : s.ProxyPassword);
        }

        public static IWebProxy Create(string mode, string host, string user, string password)
        {
            switch (mode)
            {
                case "none":
                    return null;
                case "manual":
                    var uri = ManualUri(host);
                    if (uri == null) return null;
                    var p = new WebProxy(uri) { BypassProxyOnLocal = true };
                    if (!string.IsNullOrEmpty(user)) p.Credentials = new NetworkCredential(user, password ?? "");
                    else p.UseDefaultCredentials = true;
                    return p;
                default:
                    var sys = WebRequest.GetSystemWebProxy();
                    sys.Credentials = CredentialCache.DefaultCredentials;
                    return sys;
            }
        }

        /// <summary>"proxy.local:8080" or "http://proxy.local:8080" -> uri; null when invalid.</summary>
        public static Uri ManualUri(string host)
        {
            host = (host ?? "").Trim();
            if (host == "") return null;
            if (!host.Contains("://")) host = "http://" + host;
            Uri u;
            return Uri.TryCreate(host, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https") ? u : null;
        }

        /// <summary>composer, npm, git, curl and pip read these variables (only for a manual proxy; "system" is read by Windows tools themselves).</summary>
        public static void ApplyEnv(StringDictionary env)
        {
            var s = Settings.Current;
            if (s == null || s.ProxyMode != "manual") return;
            var uri = ManualUri(s.ProxyHost);
            if (uri == null) return;
            var auth = s.ProxyUser == "" ? "" : Uri.EscapeDataString(s.ProxyUser) + ":" + Uri.EscapeDataString(s.ProxyPassword) + "@";
            var value = uri.Scheme + "://" + auth + uri.Authority;
            env["HTTP_PROXY"] = value;
            env["HTTPS_PROXY"] = value;
            env["http_proxy"] = value;
            env["https_proxy"] = value;
            var noProxy = "localhost,127.0.0.1,::1,." + s.Tld;
            env["NO_PROXY"] = noProxy;
            env["no_proxy"] = noProxy;
        }

        /// <summary>
        /// "Test connection": fetch a URL with the given settings and explain a failure in plain words
        /// (DNS / proxy / TLS / HTTP). Returns null on success.
        /// </summary>
        public static Task<string> TestAsync(string url, IWebProxy proxy)
        {
            return Task.Run(() =>
            {
                try
                {
                    var r = (HttpWebRequest)WebRequest.Create(url);
                    r.Proxy = proxy;
                    r.UserAgent = Net.UserAgent;
                    r.Timeout = 15000;
                    r.ReadWriteTimeout = 15000;
                    using (var resp = (HttpWebResponse)r.GetResponse())
                    using (var body = resp.GetResponseStream()) body.ReadByte();
                    return (string)null;
                }
                catch (WebException ex) { return Explain(ex, proxy); }
                catch (Exception ex) { return ex.Message; }
            });
        }

        static string Explain(WebException ex, IWebProxy proxy)
        {
            var resp = ex.Response as HttpWebResponse;
            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return I18n.T("proxy.errDns");
                case WebExceptionStatus.ProxyNameResolutionFailure:
                    return I18n.T("proxy.errProxyDns");
                case WebExceptionStatus.ConnectFailure:
                    return I18n.T(proxy == null ? "proxy.errConnect" : "proxy.errProxyConnect", Inner(ex));
                case WebExceptionStatus.Timeout:
                    return I18n.T("proxy.errTimeout");
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    return I18n.T("proxy.errTls", Inner(ex));
                case WebExceptionStatus.ProtocolError:
                    if (resp != null && (int)resp.StatusCode == 407) return I18n.T("proxy.errAuth");
                    return I18n.T("proxy.errHttp", resp == null ? "?" : ((int)resp.StatusCode + " " + resp.StatusDescription));
            }
            if (ex.InnerException is AuthenticationException) return I18n.T("proxy.errTls", Inner(ex));
            if (ex.InnerException is SocketException) return I18n.T("proxy.errConnect", Inner(ex));
            return ex.Message;
        }

        static string Inner(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
