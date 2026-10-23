using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Portly.Core
{
    public static class Net
    {
        public static string UserAgent { get { return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Portly/" + AppInfo.VersionText + " (+" + AppInfo.GitHubUrl + ")"; } }

        public static void Init()
        {
            // Windows 7 / 8 do not enable TLS 1.2 by default for .NET; ask for it explicitly.
            // Windows 10+ lets the OS choose (TLS 1.2 / 1.3).
            try
            {
                if (OsInfo.IsWin10OrLater) ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
                else ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            }
            catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            ServicePointManager.DefaultConnectionLimit = 8;
            ServicePointManager.Expect100Continue = false;
        }

        class TimeoutClient : WebClient
        {
            public int Timeout = 30000;
            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address);
                r.Timeout = Timeout;
                var h = r as HttpWebRequest;
                if (h != null)
                {
                    h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    h.ReadWriteTimeout = Timeout;
                }
                return r;
            }
        }

        static TimeoutClient NewClient(int timeout)
        {
            var c = new TimeoutClient { Timeout = timeout, Encoding = Encoding.UTF8 };
            c.Headers[HttpRequestHeader.UserAgent] = UserAgent;
            c.Proxy = Proxy.Current(); // Settings > General > Proxy (null = direct)
            return c;
        }

        public static Task<string> GetStringAsync(string url, int timeoutMs = 20000)
        {
            return Task.Run(() =>
            {
                using (var c = NewClient(timeoutMs))
                {
                    // avoid stale CDN copies of manifests
                    var u = url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
                    if (url.Contains("windows.php.net")) u = url;
                    return c.DownloadString(u);
                }
            });
        }

        /// <summary>Download with progress (0..100, -1 when the size is unknown) and cancellation.</summary>
        public static async Task DownloadFileAsync(string url, string target, Action<int, long> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var part = target + ".part";
            using (var c = NewClient(60000))
            {
                c.DownloadProgressChanged += (s, e) =>
                {
                    if (progress != null) progress(e.TotalBytesToReceive > 0 ? e.ProgressPercentage : -1, e.BytesReceived);
                };
                using (ct.Register(c.CancelAsync))
                {
                    try
                    {
                        await c.DownloadFileTaskAsync(new Uri(url), part).ConfigureAwait(false);
                    }
                    catch (WebException ex) when (ex.Status == WebExceptionStatus.RequestCanceled)
                    {
                        TryDelete(part);
                        throw new OperationCanceledException(ct);
                    }
                    catch
                    {
                        TryDelete(part);
                        throw;
                    }
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
        }

        /// <summary>Returns true when the URL answers (used to fall back to archive URLs).</summary>
        public static bool Exists(string url)
        {
            try
            {
                var r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "HEAD";
                r.UserAgent = UserAgent;
                r.Proxy = Proxy.Current();
                r.Timeout = 15000;
                r.AllowAutoRedirect = true;
                using (var resp = (HttpWebResponse)r.GetResponse()) return (int)resp.StatusCode < 400;
            }
            catch { return false; }
        }

        static bool? _v6;
        /// <summary>Can we bind to [::1]? (false when IPv6 is disabled; then we only listen on 127.0.0.1)</summary>
        public static bool LoopbackV6
        {
            get
            {
                if (_v6.HasValue) return _v6.Value;
                try
                {
                    var l = new System.Net.Sockets.TcpListener(IPAddress.IPv6Loopback, 0);
                    l.Start();
                    l.Stop();
                    _v6 = true;
                }
                catch { _v6 = false; }
                return _v6.Value;
            }
        }

        public static string Sha256(string file)
        {
            using (var s = File.OpenRead(file))
            using (var h = SHA256.Create())
            {
                var b = h.ComputeHash(s);
                var sb = new StringBuilder(64);
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }
}
