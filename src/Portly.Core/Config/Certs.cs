using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Config
{
    /// <summary>
    /// Local HTTPS without warnings:
    ///  1. A personal root authority "Portly Local CA" is created once (usr\ssl) and trusted once by the user.
    ///  2. Every site gets its own certificate signed by that authority (etc\ssl\sites).
    /// New sites never need a new trust step. Built only on .NET Framework 4.7.2+ APIs (works on Windows 7).
    ///
    /// A new authority carries X.509 name constraints: if its key ever leaks, it can only forge local names and private
    /// addresses, not google.com. Sites may use any name (real domains as folders, aliases, a free ending), so the list
    /// is made from the names in use when the authority is created; a later name outside it is reported by "Fix SSL",
    /// which can replace the authority (SslRepair.UpgradeCaAsync). An older unrestricted authority keeps working until
    /// the user chooses that step: a trusted authority is never replaced silently.
    /// </summary>
    public static partial class Certs
    {
        static string CaCrt { get { return Path.Combine(Paths.UserSsl, "portly-ca.crt"); } }
        /// <summary>CA private key, encrypted with Windows DPAPI for the current user (useless on another PC / account).</summary>
        static string CaKey { get { return Path.Combine(Paths.UserSsl, "portly-ca.key.dpapi"); } }
        /// <summary>Plain-text key written by Portly 1.0 builds; migrated to CaKey on first use.</summary>
        static string CaKeyPlain { get { return Path.Combine(Paths.UserSsl, "portly-ca.key"); } }

        /// <summary>Authority made by PrepareNewCa: waits here until Windows trusts it.</summary>
        static string NewCaCrt { get { return Path.Combine(Paths.UserSsl, "portly-ca.new.crt"); } }
        static string NewCaKey { get { return Path.Combine(Paths.UserSsl, "portly-ca.new.key.dpapi"); } }

        static void SaveCaKey(byte[] pkcs8) { SaveCaKey(pkcs8, CaKey); }

        static void SaveCaKey(byte[] pkcs8, string file)
        {
            var blob = Secret.Protect(pkcs8, Encoding.ASCII.GetBytes("Portly CA"));
            File.WriteAllText(file, Convert.ToBase64String(blob));
        }

        static byte[] LoadCaKey()
        {
            if (!File.Exists(CaKey) && File.Exists(CaKeyPlain))
            {
                SaveCaKey(FromPem(File.ReadAllText(CaKeyPlain)));
                File.Delete(CaKeyPlain);
            }
            return Secret.Unprotect(Convert.FromBase64String(File.ReadAllText(CaKey)), Encoding.ASCII.GetBytes("Portly CA"));
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
                byte[] key;
                var ca = CreateCa(out key);
                File.WriteAllText(CaCrt, Pem("CERTIFICATE", ca.RawData));
                SaveCaKey(key);
                try { if (File.Exists(CaKeyPlain)) File.Delete(CaKeyPlain); } catch { }
                Log.Info(I18n.T("log.caCreated"));
                // old site certificates were signed by another authority
                try { if (Directory.Exists(SitesDir)) Directory.Delete(SitesDir, true); } catch { }
                WriteBundle();
                return ca;
            }
        }

        /// <summary>A new authority and its key as PKCS#8, restricted to the names Portly uses now.</summary>
        static X509Certificate2 CreateCa(out byte[] pkcs8)
        {
            using (var rsa = NewRsa())
            {
                var name = new X500DistinguishedName("CN=Portly Local CA (" + Environment.MachineName + "), O=Portly by Erdem Software, OU=Local development only");
                var req = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
                req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
                req.CertificateExtensions.Add(new X509Extension(NameConstraintsOid, NameConstraintsDer(PermittedNames()), true));
                // web servers only: name constraints say nothing about code signing or e-mail, a leaked key could still forge those
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                var ca = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
                pkcs8 = ExportPkcs8(rsa);
                return new X509Certificate2(ca.RawData);
            }
        }

        // ---------------- name constraints ----------------
        const string NameConstraintsOid = "2.5.29.30";

        /// <summary>Loopback, private networks, link-local and CGNAT (Tailscale): every address "Share on the local network" normally has.</summary>
        public static readonly string[] PermittedNetworks =
        {
            "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10",
            "::1/128", "fc00::/7", "fe80::/10",
        };

        // no leading dot: Java refuses that form, and without one every library also matches the subdomains
        static readonly Regex DnsNameRx = new Regex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$");

        /// <summary>
        /// DNS names a new authority may sign: endings that never exist on the internet, the ending and address format
        /// from Settings, plus every name the projects use now (real domains, aliases).
        /// </summary>
        public static List<string> PermittedNames()
        {
            var names = new List<string>(Sites.LocalEndings) { Settings.DefaultTld };
            var s = Settings.Current;
            if (s != null)
            {
                names.Add(s.Tld);
                // "{name}.{tld}.corp" -> every future project ends with portly.corp
                var t = s.HostTemplate;
                var rest = t.Substring(t.IndexOf("{name}", StringComparison.Ordinal) + 6).TrimStart('.').Replace("{tld}", s.Tld);
                if (rest != "") names.Add(rest);
                try { foreach (var site in Sites.ScanAll()) names.AddRange(site.AllNames); }
                catch (Exception ex) { Log.Warn("Certificate names: " + ex.Message); }
            }
            return ReduceNames(names);
        }

        /// <summary>Lower case, without "*.", and without names another entry already covers ("a.portly" under "portly").</summary>
        public static List<string> ReduceNames(IEnumerable<string> names)
        {
            var clean = names.Select(n => (n ?? "").Trim().TrimEnd('.').ToLowerInvariant()).Select(n => n.StartsWith("*.") ? n.Substring(2) : n)
                             .Where(n => DnsNameRx.IsMatch(n)).Distinct().ToList();
            return clean.Where(n => !clean.Any(o => n.EndsWith("." + o))).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        /// <summary>NameConstraints ::= SEQUENCE { permittedSubtrees [0] SEQUENCE OF SEQUENCE { base GeneralName } } (RFC 5280).</summary>
        public static byte[] NameConstraintsDer(IEnumerable<string> dnsNames)
        {
            var subtrees = new List<byte>();
            foreach (var n in dnsNames) subtrees.AddRange(CustomCerts.Der(0x30, CustomCerts.Der(0x82, Encoding.ASCII.GetBytes(n))));   // dNSName
            foreach (var net in PermittedNetworks) subtrees.AddRange(CustomCerts.Der(0x30, CustomCerts.Der(0x87, NetworkBytes(net))));  // iPAddress
            return CustomCerts.Der(0x30, CustomCerts.Der(0xA0, subtrees.ToArray()));
        }

        /// <summary>"10.0.0.0/8" -> address followed by its mask, as the iPAddress form of a constraint wants it.</summary>
        static byte[] NetworkBytes(string cidr)
        {
            var parts = cidr.Split('/');
            var ip = IPAddress.Parse(parts[0]).GetAddressBytes();
            int bits = int.Parse(parts[1]);
            var mask = new byte[ip.Length];
            for (int i = 0; i < mask.Length; i++, bits -= 8) mask[i] = (byte)(bits >= 8 ? 0xFF : bits <= 0 ? 0 : 0xFF << (8 - bits));
            return ip.Concat(mask).ToArray();
        }

        public static bool IsRestricted(X509Certificate2 ca) { return ca.Extensions[NameConstraintsOid] != null; }

        /// <summary>dNSName entries of an authority's permitted subtrees.</summary>
        public static List<string> PermittedDns(X509Certificate2 ca)
        {
            var list = new List<string>();
            var ext = ca.Extensions[NameConstraintsOid];
            if (ext == null) return list;
            var d = ext.RawData;
            int i = 1;
            CustomCerts.ReadLen(d, ref i);    // SEQUENCE
            i++;
            int end = CustomCerts.ReadLen(d, ref i);    // [0]
            end += i;
            while (i < end)
            {
                i++;
                int len = CustomCerts.ReadLen(d, ref i);    // GeneralSubtree
                int next = i + len;
                byte tag = d[i++];
                int nameLen = CustomCerts.ReadLen(d, ref i);
                if (tag == 0x82) list.Add(Encoding.ASCII.GetString(d, i, nameLen));
                i = next;
            }
            return list;
        }

        /// <summary>The names this authority cannot sign: browsers refuse such a certificate whatever Portly issues.</summary>
        public static List<string> OutsideCa(X509Certificate2 ca, IEnumerable<string> names)
        {
            if (!IsRestricted(ca)) return new List<string>();
            var permitted = PermittedDns(ca);
            return names.Where(n =>
            {
                var h = n.StartsWith("*.") ? n.Substring(2) : n;
                return !permitted.Any(p => h.Equals(p, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + p, StringComparison.OrdinalIgnoreCase));
            }).ToList();
        }

        /// <summary>An authority exists and it has no name constraints.</summary>
        public static bool CaIsUnrestricted()
        {
            try { return File.Exists(CaCrt) && !IsRestricted(new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(CaCrt)))); }
            catch { return false; }
        }

        /// <summary>The key as PKCS#8. Windows keys are CNG (what the .NET Framework build needs); macOS / Linux use the portable RSA API.</summary>
        static byte[] ExportPkcs8(RSA rsa)
        {
            var cng = rsa as RSACng;
            if (cng != null) return cng.Key.Export(CngKeyBlobFormat.Pkcs8PrivateBlob);
#if !NETFRAMEWORK
            return rsa.ExportPkcs8PrivateKey();
#else
            throw new NotSupportedException();
#endif
        }

        static RSA NewRsa()
        {
#if !NETFRAMEWORK
            if (!OS.IsWindows) return RSA.Create(2048);
#endif
            var p = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowPlaintextExport, KeyUsage = CngKeyUsages.AllUsages };
            p.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            return new RSACng(CngKey.Create(CngAlgorithm.Rsa, null, p));
        }

        /// <summary>Signer for site certificates. (CopyWithPrivateKey does not work with in-memory CNG keys on .NET Framework.)</summary>
        static RSA CaKeyRsa()
        {
            var der = LoadCaKey();
#if !NETFRAMEWORK
            if (!OS.IsWindows)
            {
                var r = RSA.Create();
                int read;
                r.ImportPkcs8PrivateKey(der, out read);
                return r;
            }
#endif
            return new RSACng(CngKey.Import(der, CngKeyBlobFormat.Pkcs8PrivateBlob));
        }

        /// <summary>Key of a LAN certificate: a CSP key on Windows (CopyWithPrivateKey works with it on .NET Framework).</summary>
        static RSA NewLanRsa()
        {
#if !NETFRAMEWORK
            if (!OS.IsWindows) return RSA.Create(2048);
#endif
            return new RSACryptoServiceProvider(2048);
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
                var outside = OutsideCa(ca, list);
                if (outside.Count > 0 && WarnedOutside.Add(namesText)) Log.Warn(I18n.T("sslfix.outsideLog", string.Join(", ", outside)));
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
                    File.WriteAllText(key, Pem("PRIVATE KEY", ExportPkcs8(rsa)));
                    File.WriteAllText(namesFile, namesText);
                }
                return Tuple.Create(crt, key);
            }
        }

        static readonly HashSet<string> WarnedOutside = new HashSet<string>();
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
                using (var rsa = NewLanRsa())
                {
                    // no CN: Windows and OpenSSL check a CN against the DNS name constraints when the SAN has no dNSName
                    var req = new CertificateRequest(new X500DistinguishedName("O=Portly, OU=LAN share"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
                        have = new X509Certificate2(pfx, "portly", OS.IsWindows ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet);
                    }
                }
                LanCerts[ip.ToString()] = have;
                return have;
            }
        }

        /// <summary>
        /// Why a site's certificate would show a browser warning, or null when it is fine: "missing", "expiring"
        /// (under 30 days), "otherCa" (signed by an older Portly CA), "names" (not made for the site's current names),
        /// "constraint" (a name the authority may not sign: a new certificate would not help), "broken" (unreadable).
        /// Same rules as EnsureSite uses to decide on a new certificate.
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
                if (OutsideCa(ca, list).Count > 0) return "constraint";
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
                // only the signature counts here: a name outside the constraints is reported as such
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority | X509VerificationFlags.IgnoreInvalidName;
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
            if (!OS.IsWindows) return IsTrustedUnix(CaCrt);
            try
            {
                return InRootStore(new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(CaCrt))).Thumbprint, StoreLocation.CurrentUser, StoreLocation.LocalMachine);
            }
            catch { }
            return false;
        }

        static bool InRootStore(string thumbprint, params StoreLocation[] locations)
        {
            foreach (var loc in locations)
                using (var store = new X509Store(StoreName.Root, loc))
                {
                    store.Open(OpenFlags.ReadOnly);
                    if (store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0) return true;
                }
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
                return TrustFile(CaCrt);
            }
            catch (Exception ex) { Log.Warn("Trust CA: " + ex.Message); return false; }
        }

        /// <summary>The steps of TrustCa for a certificate file (the current authority or the one that replaces it).</summary>
        public static bool TrustFile(string file)
        {
            if (!OS.IsWindows) return TrustFileUnix(file);
            try
            {
                var ca = new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(file)));
                var code = Elevate.RunSelf("--trust-ca", file);
                if (code == 0 && InRootStore(ca.Thumbprint, StoreLocation.CurrentUser, StoreLocation.LocalMachine)) { Log.Info(I18n.T("log.caTrusted")); Tools.FirefoxTrust.EnableAll(); return true; }
                using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(ca);
                }
                Log.Info(I18n.T("log.caTrusted"));
                // Firefox keeps its own list: let it read the Windows store too
                Tools.FirefoxTrust.EnableAll();
                return InRootStore(ca.Thumbprint, StoreLocation.CurrentUser, StoreLocation.LocalMachine);
            }
            catch (Exception ex) { Log.Warn("Trust CA: " + ex.Message); return false; }
        }

        /// <summary>Runs in the elevated helper ("Portly.exe --trust-ca file"). Only accepts the Portly CA.</summary>
        public static int TrustCaElevated(string file)
        {
            if (!OS.IsWindows) return TrustCaElevatedUnix(file);
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

        // ---------------- replacing the authority (SslRepair.UpgradeCaAsync) ----------------
        const string OldCaKey = "OldCaThumbprint";

        /// <summary>
        /// Thumbprints of replaced authorities still to be removed from the trust store. Only these: another Portly
        /// folder on this PC has its own authority under the same name.
        /// </summary>
        public static List<string> OldCas
        {
            get { return Settings.Current.Raw.Get("ssl", OldCaKey, "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(); }
        }

        static void SetOldCas(IEnumerable<string> list)
        {
            Settings.Current.Raw.Set("ssl", OldCaKey, string.Join(",", list));
            Settings.Current.Save();
        }

        /// <summary>Make the replacement next to the current authority, which stays in use. Returns its certificate file.</summary>
        public static string PrepareNewCa()
        {
            lock (Sync)
            {
                byte[] key;
                var ca = CreateCa(out key);
                File.WriteAllText(NewCaCrt, Pem("CERTIFICATE", ca.RawData));
                SaveCaKey(key, NewCaKey);
                return NewCaCrt;
            }
        }

        public static void DiscardNewCa()
        {
            lock (Sync) foreach (var f in new[] { NewCaCrt, NewCaKey }) try { File.Delete(f); } catch { }
        }

        /// <summary>The prepared authority becomes the current one. The old one is remembered for RemoveOldCas.</summary>
        public static void ActivateNewCa()
        {
            lock (Sync)
            {
                var old = File.Exists(CaCrt) ? new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(CaCrt))).Thumbprint : null;
                File.Copy(NewCaKey, CaKey, true);
                File.Copy(NewCaCrt, CaCrt, true);
                DiscardNewCa();
                if (old != null) SetOldCas(OldCas.Concat(new[] { old }).Distinct());
                LanCerts.Clear();
                try { if (Directory.Exists(SitesDir)) Directory.Delete(SitesDir, true); } catch { }
                WriteBundle();
            }
        }

        /// <summary>Remove one replaced authority from the user's and the machine's trusted roots (UAC for the machine's).</summary>
        public static bool UntrustCa(string thumbprint)
        {
            if (!OS.IsWindows) return UntrustUnix(thumbprint);
            try
            {
                using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false)) store.Remove(c);
                }
                if (InRootStore(thumbprint, StoreLocation.LocalMachine)) Elevate.RunSelf("--untrust-ca", thumbprint);
                return !InRootStore(thumbprint, StoreLocation.CurrentUser, StoreLocation.LocalMachine);
            }
            catch (Exception ex) { Log.Warn("Untrust CA: " + ex.Message); return false; }
        }

        /// <summary>Runs in the elevated helper ("Portly.exe --untrust-ca thumbprint"). Only removes a Portly CA.</summary>
        public static int UntrustCaElevated(string thumbprint)
        {
            if (!OS.IsWindows) return UntrustCaElevatedUnix(thumbprint);
            try
            {
                if (!Regex.IsMatch(thumbprint, "^[0-9A-Fa-f]{40}$")) return 3;
                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false))
                    {
                        if (!c.Subject.Contains("Portly Local CA") || c.Subject != c.Issuer) return 3;
                        store.Remove(c);
                    }
                }
                return 0;
            }
            catch { return 2; }
        }

        /// <summary>Try the removal of every replaced authority; those that stay are tried again by the next "Fix SSL".</summary>
        public static bool RemoveOldCas(Func<string, bool> untrust)
        {
            var left = OldCas.Where(t => !untrust(t)).ToList();
            SetOldCas(left);
            return left.Count == 0;
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
