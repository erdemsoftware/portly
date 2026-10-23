using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>Smoke tests of the "SitesWeb" features. Runs while Apache, PHP and MySQL are up (see Run()).</summary>
        static async Task Tests_SitesWeb(Settings s, string www)
        {
            var http = "http://127.0.0.1:" + s.HttpPort + "/";
            var https = "https://127.0.0.1:" + s.HttpsPort + "/";

            // ---------- Fix SSL: certificate checks ----------
            var sslNames = new[] { "sslcheck.portly", "*.sslcheck.portly" };
            Certs.RenewSite("sslcheck.portly", sslNames);
            Check("fix ssl: a fresh certificate has no problem", Certs.SiteProblem("sslcheck.portly", sslNames) == null);
            Check("fix ssl: changed names are found", Certs.SiteProblem("sslcheck.portly", new[] { "sslcheck.portly", "x.test" }) == "names");
            Check("fix ssl: a missing certificate is found", Certs.SiteProblem("nocert.portly", null) == "missing");
            var sslCheck = SslRepair.Check(ServiceManager.CurrentSites);
            Check("fix ssl: every valid site is checked", sslCheck.Sites.Count == ServiceManager.CurrentSites.Count(x => x.Valid), sslCheck.Sites.Count.ToString());
            foreach (var ext in new[] { ".crt", ".key", ".names" }) try { File.Delete(Path.Combine(Certs.SitesDir, "sslcheck.portly" + ext)); } catch { }

            // ---------- extra domains: same site via Host header, names in the certificate ----------
            Features.Set("aliases", true);
            Sites.SetAliases("demo", new[] { "demo-alias.localhost", "*.wild.localhost" });
            await ServiceManager.ReloadWebAsync();
            var viaAlias = Get(http, "demo-alias.localhost");
            var viaWild = Get(http, "x.wild.localhost");
            Check("SitesWeb: alias reaches the same site (Apache)", viaAlias.Contains("DEMO-OK") && viaWild.Contains("DEMO-OK"), Short(viaAlias) + " | " + Short(viaWild));
            var page = Get(https, "demo-alias.localhost");
            // the certificate the server sends for the alias name (SNI)
            var served = ServerCert(s.HttpsPort, "demo-alias.localhost");
            var names = served == null ? new System.Collections.Generic.List<string>() : CustomCerts.DnsNames(served);
            var apacheConf = File.ReadAllText(Path.Combine(Paths.Etc, "apache", "sites", "auto.demo.localhost.conf"));
            Check("SitesWeb: aliases in the site certificate (SAN) and ServerAlias", page.Contains("DEMO-OK") && names.Contains("demo-alias.localhost") && names.Contains("*.wild.localhost")
                && apacheConf.Contains("demo-alias.localhost") && apacheConf.Contains("*.wild.localhost"), string.Join(", ", names));
            Check("SitesWeb: dashboard shows aliases", Get(http, "localhost").Contains("demo-alias.localhost"));
            Check("SitesWeb: alias validation", Sites.IsValidAlias("*.shop.test") && Sites.IsValidAlias("a-b.c") && !Sites.IsValidAlias("*.*.x.y") && !Sites.IsValidAlias("bad name.test") && !Sites.IsValidAlias("single"));
            Check("SitesWeb: hosts file skips wildcards and .localhost", HostsFile.NeedsEntry("shop.test") && !HostsFile.NeedsEntry("*.shop.test") && !HostsFile.NeedsEntry("a.localhost"));

            // ---------- own certificate (.pfx) ----------
            Features.Set("customCerts", true);
            try
            {
                var pfx = Path.Combine(Paths.Tmp, "sitesweb-test.pfx");
                string thumb;
                using (var rsa = RSA.Create(2048))
                {
                    var req = new CertificateRequest("CN=demo.localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var san = new SubjectAlternativeNameBuilder();
                    san.AddDnsName("demo.localhost"); san.AddDnsName("*.demo.localhost");
                    req.CertificateExtensions.Add(san.Build());
                    using (var self = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30)))
                    {
                        File.WriteAllBytes(pfx, self.Export(X509ContentType.Pfx, "Gizli-1"));
                        thumb = self.Thumbprint;
                    }
                }
                var demo = Sites.Scan().First(x => x.Name == "demo");
                var cand = CustomCerts.Load(pfx, null, "Gizli-1", demo);
                Check("SitesWeb: .pfx read, missing names reported", cand.KeyPem.Contains("RSA PRIVATE KEY") && cand.Uncovered.Contains("demo-alias.localhost") && !cand.Uncovered.Contains("demo.localhost"), string.Join(", ", cand.Uncovered));
                CustomCerts.Install(demo, cand);
                await ServiceManager.ReloadWebAsync();
                var conf = File.ReadAllText(Path.Combine(Paths.Etc, "apache", "sites", "auto.demo.localhost.conf"));
                var installed = new X509Certificate2(File.ReadAllBytes(CustomCerts.CrtOf(demo)));
                var own = ServerCert(s.HttpsPort, "demo.localhost");
                Check("SitesWeb: own certificate served by Apache", conf.Contains(Paths.Fwd(CustomCerts.CrtOf(demo))) && installed.Thumbprint == thumb && own != null && own.Thumbprint == thumb
                    && Get(https, "demo.localhost").Contains("DEMO-OK"), ServiceManager.Apache.LastError ?? (own == null ? "no certificate" : own.Subject));
                CustomCerts.Remove(demo);
                File.Delete(pfx);
            }
            catch (Exception ex) { Check("SitesWeb: own certificate", false, ex.Message); }
            Features.Set("customCerts", false);
            Sites.SetAliases("demo", null);

            // ---------- archive ----------
            Features.Set("archive", true);
            Sites.SetArchived("legacyapp", true);
            await ServiceManager.ReloadWebAsync();
            var archived = Get(http, "legacyapp.localhost");
            Check("SitesWeb: archived site not served", !archived.Contains("LEGACY") && Sites.ArchivedSites().Any(x => x.Name == "legacyapp") && !ServiceManager.CurrentSites.Any(x => x.Name == "legacyapp"), Short(archived));
            Sites.SetArchived("legacyapp", false);
            await ServiceManager.ReloadWebAsync();
            Check("SitesWeb: restored site served again", Get(http, "legacyapp.localhost").Contains("LEGACY"));

            // ---------- excluded folders ----------
            var ex1 = Path.Combine(www, "_scratch");
            var ex2 = Path.Combine(www, "backup-2024");
            foreach (var d in new[] { ex1, ex2 }) { Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "index.php"), "<?php echo 1;"); }
            var scanned = Sites.Scan();
            Check("SitesWeb: excluded folders are not sites", !scanned.Any(x => x.Name == "_scratch" || x.Name == "backup-2024") && scanned.Any(x => x.Name == "demo"));
            foreach (var d in new[] { ex1, ex2 }) Directory.Delete(d, true);

            // ---------- Firefox user.js on a fake profile (idempotent, keeps other lines) ----------
            var fakeAppData = Path.Combine(Paths.Tmp, "fake-appdata");
            if (Directory.Exists(fakeAppData)) Directory.Delete(fakeAppData, true);
            var profile = Path.Combine(fakeAppData, "Mozilla", "Firefox", "Profiles", "abc.default-release");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "user.js"), "user_pref(\"browser.startup.page\", 3);");
            int first = FirefoxTrust.EnableAll(fakeAppData), second = FirefoxTrust.EnableAll(fakeAppData);
            var userJs = File.ReadAllText(Path.Combine(profile, "user.js"));
            Check("SitesWeb: Firefox user.js helper (idempotent)", first == 1 && second == 0 && userJs.Contains("browser.startup.page")
                && userJs.Split('\n').Count(l => l.Contains("security.enterprise_roots.enabled")) == 1, "first " + first + ", second " + second);
            Directory.Delete(fakeAppData, true);

            // ---------- Caddy and FrankenPHP (downloads ~80 MB): only with PORTLY_SLOW_TESTS=1 ----------
            if (Environment.GetEnvironmentVariable("PORTLY_SLOW_TESTS") != "1") { Console.WriteLine("  (skip Caddy / FrankenPHP: set PORTLY_SLOW_TESTS=1)"); return; }
            try
            {
                Features.Set("caddy", true);
                await Installer.InstallAsync(Catalog.Recommended("caddy") ?? Fallback("caddy"), null, CancellationToken.None);
                await ServiceManager.SwitchWebServerAsync("caddy");
                var c1 = Get(http, "demo.localhost");
                var c2 = Get(https, "demo.localhost");
                Check("SitesWeb: Caddy serves the demo site (http + https)", ServiceManager.Caddy.State == ServiceState.Running && c1.Contains("DEMO-OK") && c2.Contains("DEMO-OK") && _lastIssuer.Contains("Portly Local CA"), ServiceManager.Caddy.LastError ?? Short(c1));
            }
            catch (Exception ex) { Check("SitesWeb: Caddy serves the demo site", false, ex.Message); }
            await ServiceManager.SwitchWebServerAsync("apache");
            Features.Set("caddy", false);

            try
            {
                Features.Set("frankenphp", true);
                await Installer.InstallAsync(Catalog.Recommended("frankenphp") ?? Fallback("frankenphp"), null, CancellationToken.None);
                File.WriteAllText(Path.Combine(www, "demo", "sapi.php"), "<?php echo 'SAPI=' . php_sapi_name() . ' up=' . ini_get('session.upload_progress.enabled');");
                Sites.SetRuntime("demo", "frankenphp");
                await ServiceManager.ReloadWebAsync();
                var f1 = Get(http + "sapi.php", "demo.localhost");
                var f2 = Get(https, "demo.localhost");
                Check("SitesWeb: FrankenPHP serves the demo site behind Apache", f1.Contains("SAPI=frankenphp") && System.Text.RegularExpressions.Regex.IsMatch(f1, "up=0?$") && f2.Contains("DEMO-OK"), Short(f1) + " | " + Short(f2));
                Check("SitesWeb: FrankenPHP bundled PHP version shown", FrankenPhpService.PhpVersion(PackageTypes.ActiveDir("frankenphp")) != "", FrankenPhpService.VersionText(PackageTypes.ActiveDir("frankenphp")));
            }
            catch (Exception ex) { Check("SitesWeb: FrankenPHP serves the demo site", false, ex.Message); }
            Sites.SetRuntime("demo", "");
            Features.Set("frankenphp", false);
            await ServiceManager.ReloadWebAsync();
        }

        /// <summary>Same values as manifest/packages.json, for runs before the remote catalog has the new types.</summary>
        static PackageEntry Fallback(string type)
        {
            if (type == "caddy")
                return new PackageEntry { Type = "caddy", Version = "2.11.7", Folder = "caddy-2.11.7", Url = "https://github.com/caddyserver/caddy/releases/download/v2.11.7/caddy_2.11.7_windows_amd64.zip", Sha256 = "0a1edc0b799512051c57071ce0e798d3f2cf67dc98171366f1d2b326072e3b06", MinOs = 10 };
            return new PackageEntry { Type = "frankenphp", Version = "1.13.2", Folder = "frankenphp-1.13.2-php-8.5.12", Url = "https://github.com/php/frankenphp/releases/download/v1.13.2/frankenphp-windows-x86_64.zip", Sha256 = "a2ecfc278493f2cf8b5eb4a5f7c47f6eaa03d282e9c643afa933afb696ea407c", Vc = 30, MinOs = 10 };
        }

        static string Short(string t) { return t.Length > 120 ? t.Substring(0, 120) : t; }

        /// <summary>The certificate a local HTTPS port sends for a name (TLS SNI), or null.</summary>
        static X509Certificate2 ServerCert(int port, string sni)
        {
            try
            {
                using (var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port))
                using (var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (a, b, c, d) => true))
                {
                    ssl.AuthenticateAsClient(sni);
                    return new X509Certificate2(ssl.RemoteCertificate);
                }
            }
            catch { return null; }
        }
    }
}
