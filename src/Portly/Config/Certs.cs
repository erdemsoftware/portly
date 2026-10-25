using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Config
{
    /// <summary>
    /// Local HTTPS without warnings:
    ///  1. A personal root authority "Portly Local CA" is created once (usr\ssl) and trusted once by the user.
    ///  2. Every site gets its own certificate signed by that authority (etc\ssl\sites).
    /// New sites never need a new trust step. Built only on .NET Framework 4.7.2+ APIs (works on Windows 7).
    /// </summary>
    public static class Certs
    {
        static string CaCrt { get { return Path.Combine(Paths.UserSsl, "portly-ca.crt"); } }
        /// <summary>CA private key, encrypted with Windows DPAPI for the current user (useless on another PC / account).</summary>
        static string CaKey { get { return Path.Combine(Paths.UserSsl, "portly-ca.key.dpapi"); } }
        /// <summary>Plain-text key written by Portly 1.0 builds; migrated to CaKey on first use.</summary>
        static string CaKeyPlain { get { return Path.Combine(Paths.UserSsl, "portly-ca.key"); } }

        static void SaveCaKey(byte[] pkcs8)
        {
            var blob = ProtectedData.Protect(pkcs8, Encoding.ASCII.GetBytes("Portly CA"), DataProtectionScope.CurrentUser);
            File.WriteAllText(CaKey, Convert.ToBase64String(blob));
        }

        static byte[] LoadCaKey()
        {
            if (!File.Exists(CaKey) && File.Exists(CaKeyPlain))
            {
                SaveCaKey(FromPem(File.ReadAllText(CaKeyPlain)));
                File.Delete(CaKeyPlain);
            }
            return ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(CaKey)), Encoding.ASCII.GetBytes("Portly CA"), DataProtectionScope.CurrentUser);
        }

        static bool HasCaKey { get { return File.Exists(CaKey) || File.Exists(CaKeyPlain); } }
        public static string SitesDir { get { return Path.Combine(Paths.Ssl, "sites"); } }
        /// <summary>CA bundle for PHP curl/openssl: Mozilla roots + Portly CA (so PHP can call the local https:// sites too).</summary>
        public static string BundleFile { get { return Path.Combine(Paths.Ssl, "cacert.pem"); } }
        static string MozillaFile { get { return Path.Combine(Paths.Cache, "cacert-mozilla.pem"); } }
        static readonly object Sync = new object();

        public static string CaCertPath { get { return CaCrt; } }

        public static X509Certificate2 EnsureCa()
        {
            lock (Sync)
            {
                if (File.Exists(CaCrt) && HasCaKey)
                {
                    try
                    {
                        var cert = new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(CaCrt)));
                        // the key must still be readable (DPAPI fails after moving Portly to another PC or user)
                        LoadCaKey();
                        if (cert.NotAfter > DateTime.Now.AddDays(30)) return cert;
                    }
                    catch (Exception ex) { Log.Warn("CA unreadable, creating a new one: " + ex.Message); }
                }
                using (var rsa = NewRsa())
                {
                    var name = new X500DistinguishedName("CN=Portly Local CA (" + Environment.MachineName + "), O=Portly by Erdem Software, OU=Local development only");
                    var req = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
                    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
                    req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
                    var ca = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
                    File.WriteAllText(CaCrt, Pem("CERTIFICATE", ca.RawData));
                    SaveCaKey(rsa.Key.Export(CngKeyBlobFormat.Pkcs8PrivateBlob));
                    try { if (File.Exists(CaKeyPlain)) File.Delete(CaKeyPlain); } catch { }
                    Log.Info(I18n.T("log.caCreated"));
                    // old site certificates were signed by another authority
                    try { if (Directory.Exists(SitesDir)) Directory.Delete(SitesDir, true); } catch { }
                    WriteBundle();
                    return new X509Certificate2(ca.RawData);
                }
            }
        }

        static RSACng NewRsa()
        {
            var p = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowPlaintextExport, KeyUsage = CngKeyUsages.AllUsages };
            p.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            return new RSACng(CngKey.Create(CngAlgorithm.Rsa, null, p));
        }

        /// <summary>Signer for site certificates. (CopyWithPrivateKey does not work with in-memory CNG keys on .NET Framework.)</summary>
        static RSACng CaKeyRsa()
        {
            var der = LoadCaKey();
            return new RSACng(CngKey.Import(der, CngKeyBlobFormat.Pkcs8PrivateBlob));
        }

        /// <summary>authorityKeyIdentifier extension = SEQUENCE { [0] keyIdentifier } (helps chain building).</summary>
        static X509Extension AuthorityKeyId(X509Certificate2 ca)
        {
            foreach (var ext in ca.Extensions)
            {
                var ski = ext as X509SubjectKeyIdentifierExtension;
                if (ski == null) continue;
                var hex = ski.SubjectKeyIdentifier;
                var id = new byte[hex.Length / 2];
                for (int i = 0; i < id.Length; i++) id[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                var der = new byte[id.Length + 4];
                der[0] = 0x30; der[1] = (byte)(id.Length + 2); der[2] = 0x80; der[3] = (byte)id.Length;
                Buffer.BlockCopy(id, 0, der, 4, id.Length);
                return new X509Extension("2.5.29.35", der, false);
            }
            return null;
        }

        /// <summary>Make sure host.crt / host.key exist and are valid. Returns (crt, key) paths.</summary>
        public static Tuple<string, string> EnsureSite(string host) { return EnsureSite(host, null); }

        /// <summary>
        /// Same, with the full list of DNS names for the certificate (site aliases and wildcards).
        /// null = host and *.host. The names are remembered in host.names; a changed list makes a new certificate.
        /// </summary>
        public static Tuple<string, string> EnsureSite(string host, IEnumerable<string> names)
        {
            var list = (names ?? new[] { host, "*." + host }).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!list.Contains(host, StringComparer.OrdinalIgnoreCase)) list.Insert(0, host);
            var namesText = string.Join(",", list).ToLowerInvariant();
            lock (Sync)
            {
                // the CA first: making a new CA clears the site certificate folder
                var ca = EnsureCa();
                Directory.CreateDirectory(SitesDir);
                var crt = Path.Combine(SitesDir, host + ".crt");
                var key = Path.Combine(SitesDir, host + ".key");
                var namesFile = Path.Combine(SitesDir, host + ".names");
                if (File.Exists(crt) && File.Exists(key))
                {
                    try
                    {
                        // certificates made before aliases existed cover host and *.host
                        var had = File.Exists(namesFile) ? File.ReadAllText(namesFile).Trim() : (host + ",*." + host).ToLowerInvariant();
                        var existing = new X509Certificate2(Encoding.ASCII.GetBytes(FirstPem(File.ReadAllText(crt))));
                        // the issuer name alone also matches a recreated CA: check the signature too
                        if (existing.NotAfter > DateTime.Now.AddDays(30) && existing.Issuer == ca.Subject && VerifiedBy(existing, ca) && had == namesText) return Tuple.Create(crt, key);
                    }
                    catch { }
                }
                using (var caKey = CaKeyRsa())
                using (var rsa = NewRsa())
                {
                    var req = new CertificateRequest(new X500DistinguishedName("CN=" + host + ", O=Portly"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var san = new SubjectAlternativeNameBuilder();
                    foreach (var n in list) san.AddDnsName(n);
                    if (host == "localhost") { san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback); }
                    req.CertificateExtensions.Add(san.Build());
                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
                    var aki = AuthorityKeyId(ca);
                    if (aki != null) req.CertificateExtensions.Add(aki);
                    var serial = new byte[16];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(serial);
                    serial[0] &= 0x7f;
                    // 825 days: the longest validity Apple / Chrome accept for leaf certificates
                    var notAfter = DateTimeOffset.Now.AddDays(825);
                    if (notAfter > ca.NotAfter) notAfter = ca.NotAfter.AddDays(-1);
                    var signer = X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1);
                    var cert = req.Create(ca.SubjectName, signer, DateTimeOffset.Now.AddDays(-1), notAfter, serial);
                    File.WriteAllText(crt, Pem("CERTIFICATE", cert.RawData) + Pem("CERTIFICATE", ca.RawData));
                    File.WriteAllText(key, Pem("PRIVATE KEY", rsa.Key.Export(CngKeyBlobFormat.Pkcs8PrivateBlob)));
                    File.WriteAllText(namesFile, namesText);
                }
                return Tuple.Create(crt, key);
            }
        }

        static readonly Dictionary<string, X509Certificate2> LanCerts = new Dictionary<string, X509Certificate2>();

        /// <summary>
        /// Certificate (with its private key, for SslStream) for this PC's local network address, signed by the Portly CA:
        /// https://192.168.1.20:8900 for "Share on the local network". Made once per address and run.
        /// </summary>
        public static X509Certificate2 LanCertificate(IPAddress ip)
        {
            lock (Sync)
            {
                X509Certificate2 have;
                var ca = EnsureCa();
                if (LanCerts.TryGetValue(ip.ToString(), out have) && have.Issuer == ca.Subject) return have;
                using (var caKey = CaKeyRsa())
                using (var rsa = new RSACryptoServiceProvider(2048)) // a CSP key: CopyWithPrivateKey works with it on .NET Framework
                {
                    var req = new CertificateRequest(new X500DistinguishedName("CN=" + ip + ", O=Portly"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var san = new SubjectAlternativeNameBuilder();
                    san.AddIpAddress(ip);
                    req.CertificateExtensions.Add(san.Build());
                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    var aki = AuthorityKeyId(ca);
                    if (aki != null) req.CertificateExtensions.Add(aki);
                    var serial = new byte[16];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(serial);
                    serial[0] &= 0x7f;
                    var signer = X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1);
                    var notAfter = DateTimeOffset.Now.AddDays(825);
                    if (notAfter > ca.NotAfter) notAfter = ca.NotAfter.AddDays(-1);
                    using (var cert = req.Create(ca.SubjectName, signer, DateTimeOffset.Now.AddDays(-1), notAfter, serial))
                    using (var withKey = cert.CopyWithPrivateKey(rsa))
                    {
                        // through a PFX: SChannel (SslStream) needs a key it can open itself
                        var pfx = withKey.Export(X509ContentType.Pkcs12, "portly");
                        have = new X509Certificate2(pfx, "portly", X509KeyStorageFlags.UserKeySet);
                    }
                }
                LanCerts[ip.ToString()] = have;
                return have;
            }
        }

        /// <summary>
        /// Why a site's certificate would show a browser warning, or null when it is fine: "missing", "expiring"
        /// (under 30 days), "otherCa" (signed by an older Portly CA), "names" (not made for the site's current names),
        /// "broken" (unreadable). Same rules as EnsureSite uses to decide on a new certificate.
        /// </summary>
        public static string SiteProblem(string host, IEnumerable<string> names)
        {
            var list = (names ?? new[] { host, "*." + host }).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!list.Contains(host, StringComparer.OrdinalIgnoreCase)) list.Insert(0, host);
            var crt = Path.Combine(SitesDir, host + ".crt");
            var key = Path.Combine(SitesDir, host + ".key");
            var namesFile = Path.Combine(SitesDir, host + ".names");
            if (!File.Exists(crt) || !File.Exists(key)) return "missing";
            try
            {
                var ca = EnsureCa();
                var cert = new X509Certificate2(Encoding.ASCII.GetBytes(FirstPem(File.ReadAllText(crt))));
                if (cert.Issuer != ca.Subject || !VerifiedBy(cert, ca)) return "otherCa";
                if (cert.NotAfter <= DateTime.Now.AddDays(30) || cert.NotBefore > DateTime.Now) return "expiring";
                var had = File.Exists(namesFile) ? File.ReadAllText(namesFile).Trim() : (host + ",*." + host).ToLowerInvariant();
                if (had != string.Join(",", list).ToLowerInvariant()) return "names";
                return null;
            }
            catch { return "broken"; }
        }

        /// <summary>Signed by this CA's key (the subject alone matches a recreated CA too).</summary>
        static bool VerifiedBy(X509Certificate2 cert, X509Certificate2 ca)
        {
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                chain.ChainPolicy.ExtraStore.Add(ca);
                if (!chain.Build(cert)) return false;
                var root = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
                return root.Thumbprint == ca.Thumbprint;
            }
        }

        /// <summary>Throw the site's certificate away and make a new one (used by "Fix SSL").</summary>
        public static void RenewSite(string host, IEnumerable<string> names)
        {
            lock (Sync)
            {
                foreach (var ext in new[] { ".crt", ".key", ".names" })
                    try { File.Delete(Path.Combine(SitesDir, host + ext)); } catch { }
            }
            EnsureSite(host, names);
        }

        // ---------------- trust ----------------
        public static bool IsCaTrusted()
        {
            if (!File.Exists(CaCrt)) return false;
            try
            {
                var ca = new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(CaCrt)));
                foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                {
                    using (var store = new X509Store(StoreName.Root, loc))
                    {
                        store.Open(OpenFlags.ReadOnly);
                        if (store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false).Count > 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Trust the Portly CA for every browser on this computer: one administrator prompt (UAC) puts it into the
        /// machine's trusted roots. If the prompt is refused, it falls back to the current user's store
        /// (Windows then shows its own confirmation).
        /// </summary>
        public static bool TrustCa()
        {
            try
            {
                EnsureCa();
                var code = WinUtil.RunElevated(Paths.Exe, "--trust-ca \"" + CaCrt + "\"");
                if (code == 0 && IsCaTrusted()) { Log.Info(I18n.T("log.caTrusted")); Tools.FirefoxTrust.EnableAll(); return true; }
                var ca = new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(CaCrt)));
                using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(ca);
                }
                Log.Info(I18n.T("log.caTrusted"));
                // Firefox keeps its own list: let it read the Windows store too
                Tools.FirefoxTrust.EnableAll();
                return IsCaTrusted();
            }
            catch (Exception ex) { Log.Warn("Trust CA: " + ex.Message); return false; }
        }

        /// <summary>Runs in the elevated helper ("Portly.exe --trust-ca file"). Only accepts the Portly CA.</summary>
        public static int TrustCaElevated(string file)
        {
            try
            {
                var ca = new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(file)));
                if (!ca.Subject.Contains("Portly Local CA") || ca.Subject != ca.Issuer) return 3;
                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(ca);
                }
                return 0;
            }
            catch { return 2; }
        }

        public static void RegenerateSiteCertificates()
        {
            lock (Sync) { try { if (Directory.Exists(SitesDir)) Directory.Delete(SitesDir, true); } catch { } }
        }

        // ---------------- CA bundle for PHP ----------------
        /// <summary>Download Mozilla's CA list (monthly) and rebuild the bundle.</summary>
        public static async Task RefreshBundleAsync()
        {
            try
            {
                var fi = new FileInfo(MozillaFile);
                if (!fi.Exists || fi.LastWriteTime < DateTime.Now.AddDays(-30) || fi.Length < 10000)
                {
                    var pem = await Net.GetStringAsync("https://curl.se/ca/cacert.pem", 30000);
                    if (pem.Contains("BEGIN CERTIFICATE")) File.WriteAllText(MozillaFile, pem);
                }
            }
            catch (Exception ex) { Log.Warn("CA bundle download failed: " + ex.Message); }
            WriteBundle();
        }

        public static void WriteBundle()
        {
            try
            {
                var sb = new StringBuilder();
                if (File.Exists(MozillaFile)) sb.Append(File.ReadAllText(MozillaFile)).Append("\n");
                if (File.Exists(CaCrt)) sb.Append("Portly Local CA\n===============\n").Append(File.ReadAllText(CaCrt));
                Templates.WriteIfChanged(BundleFile, sb.ToString());
            }
            catch (Exception ex) { Log.Warn("CA bundle: " + ex.Message); }
        }

        // ---------------- PEM helpers ----------------
        static string Pem(string label, byte[] der)
        {
            var b64 = Convert.ToBase64String(der);
            var sb = new StringBuilder();
            sb.Append("-----BEGIN ").Append(label).Append("-----\n");
            for (int i = 0; i < b64.Length; i += 64) sb.Append(b64.Substring(i, Math.Min(64, b64.Length - i))).Append('\n');
            sb.Append("-----END ").Append(label).Append("-----\n");
            return sb.ToString();
        }

        static string FirstPem(string text)
        {
            const string end = "-----END CERTIFICATE-----";
            int i = text.IndexOf(end, StringComparison.Ordinal);
            return i < 0 ? text : text.Substring(0, i + end.Length);
        }

        static byte[] FromPem(string pem)
        {
            var sb = new StringBuilder();
            foreach (var line in pem.Split('\n'))
            {
                var l = line.Trim();
                if (l.Length == 0 || l.StartsWith("-----")) continue;
                sb.Append(l);
            }
            return Convert.FromBase64String(sb.ToString());
        }
    }
}
