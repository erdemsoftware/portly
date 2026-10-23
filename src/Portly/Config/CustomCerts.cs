using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Portly.Core;

namespace Portly.Config
{
    /// <summary>
    /// Own (or wildcard) certificate for a site instead of the one Portly signs: usr\ssl\custom\&lt;site&gt;.crt + .key (PEM).
    /// A .pfx is converted to PEM once when it is chosen; the password is not stored.
    /// </summary>
    public static class CustomCerts
    {
        public static string Dir { get { return Path.Combine(Paths.UserSsl, "custom"); } }

        static string Safe(string siteName) { return siteName.Replace('/', '_').Replace('\\', '_'); }
        public static string CrtOf(Site site) { return Path.Combine(Dir, Safe(site.Name) + ".crt"); }
        public static string KeyOf(Site site) { return Path.Combine(Dir, Safe(site.Name) + ".key"); }

        /// <summary>(crt, key) when the feature is on and the site has its own certificate, else null.</summary>
        public static Tuple<string, string> For(Site site)
        {
            if (!Features.IsOn("customCerts")) return null;
            var crt = CrtOf(site);
            var key = KeyOf(site);
            return File.Exists(crt) && File.Exists(key) ? Tuple.Create(crt, key) : null;
        }

        public static bool Has(Site site) { return File.Exists(CrtOf(site)) && File.Exists(KeyOf(site)); }

        public static void Remove(Site site)
        {
            foreach (var f in new[] { CrtOf(site), KeyOf(site) }) if (File.Exists(f)) File.Delete(f);
        }

        /// <summary>A checked certificate ready to be installed (PEM text) plus what is wrong with it.</summary>
        public class Candidate
        {
            public string CrtPem;
            public string KeyPem;
            public X509Certificate2 Cert;
            public List<string> DnsNames = new List<string>();
            public List<string> Uncovered = new List<string>();
            public bool Expired { get { return Cert.NotAfter < DateTime.Now || Cert.NotBefore > DateTime.Now; } }
        }

        /// <summary>Read a .pfx/.p12 (with password) or a .crt/.pem plus .key. Throws with a readable message.</summary>
        public static Candidate Load(string certFile, string keyFile, string password, Site site)
        {
            var c = new Candidate();
            var ext = Path.GetExtension(certFile).ToLowerInvariant();
            if (ext == ".pfx" || ext == ".p12")
            {
                X509Certificate2 cert;
                try { cert = new X509Certificate2(File.ReadAllBytes(certFile), password ?? "", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet); }
                catch (CryptographicException ex) { throw new Exception(I18n.T("cert.pfxBad", ex.Message)); }
                if (!cert.HasPrivateKey) throw new Exception(I18n.T("cert.noKey"));
                c.KeyPem = Pem("RSA PRIVATE KEY", ExportRsaPkcs1(cert));
                c.CrtPem = Pem("CERTIFICATE", cert.RawData);
                // keep the intermediate certificates of the .pfx so browsers can build the chain
                var all = new X509Certificate2Collection();
                try { all.Import(File.ReadAllBytes(certFile), password ?? "", X509KeyStorageFlags.EphemeralKeySet); } catch { }
                foreach (var other in all)
                    if (other.Thumbprint != cert.Thumbprint && other.Subject != other.Issuer) c.CrtPem += Pem("CERTIFICATE", other.RawData);
                c.Cert = cert;
            }
            else
            {
                var text = File.ReadAllText(certFile);
                if (!text.Contains("BEGIN CERTIFICATE")) throw new Exception(I18n.T("cert.notPem"));
                if (string.IsNullOrEmpty(keyFile) || !File.Exists(keyFile)) throw new Exception(I18n.T("cert.noKey"));
                var key = File.ReadAllText(keyFile);
                if (key.Contains("ENCRYPTED")) throw new Exception(I18n.T("cert.keyEncrypted"));
                if (!key.Contains("PRIVATE KEY-----")) throw new Exception(I18n.T("cert.noKey"));
                c.CrtPem = text.Replace("\r\n", "\n");
                c.KeyPem = key.Replace("\r\n", "\n");
                c.Cert = new X509Certificate2(Encoding.ASCII.GetBytes(FirstPem(c.CrtPem)));
            }
            c.DnsNames = DnsNames(c.Cert);
            if (site != null) c.Uncovered = site.AllNames.Where(n => !Covers(c.DnsNames, n)).ToList();
            return c;
        }

        public static void Install(Site site, Candidate c)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(CrtOf(site), c.CrtPem, Encoding.ASCII);
            File.WriteAllText(KeyOf(site), c.KeyPem, Encoding.ASCII);
        }

        /// <summary>Names of the site's certificate that are no longer covered, or that it has expired (for a warning).</summary>
        public static string Problem(Site site)
        {
            try
            {
                if (!Has(site)) return null;
                var cert = new X509Certificate2(Encoding.ASCII.GetBytes(FirstPem(File.ReadAllText(CrtOf(site)))));
                if (cert.NotAfter < DateTime.Now) return I18n.T("cert.expired", cert.NotAfter.ToShortDateString());
                var miss = site.AllNames.Where(n => !Covers(DnsNames(cert), n)).ToList();
                return miss.Count > 0 ? I18n.T("cert.notCovered", string.Join(", ", miss)) : null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Does a certificate name list cover the host? "*.a.b" covers "x.a.b" (one label) and the literal "*.a.b".</summary>
        public static bool Covers(IEnumerable<string> certNames, string host)
        {
            host = host.ToLowerInvariant();
            foreach (var raw in certNames)
            {
                var n = raw.ToLowerInvariant();
                if (n == host) return true;
                if (n.StartsWith("*.") && !host.StartsWith("*."))
                {
                    var dot = host.IndexOf('.');
                    if (dot > 0 && host.Substring(dot + 1) == n.Substring(2)) return true;
                }
            }
            return false;
        }

        /// <summary>dNSName entries of the subjectAltName extension (falls back to the CN).</summary>
        public static List<string> DnsNames(X509Certificate2 cert)
        {
            var list = new List<string>();
            foreach (var ext in cert.Extensions)
            {
                if (ext.Oid == null || ext.Oid.Value != "2.5.29.17") continue;
                var d = ext.RawData;
                int i = 0;
                if (d.Length < 2 || d[i++] != 0x30) break;
                int end = ReadLen(d, ref i); end += i;
                while (i < end && i < d.Length)
                {
                    byte tag = d[i++];
                    int len = ReadLen(d, ref i);
                    if (tag == 0x82) list.Add(Encoding.ASCII.GetString(d, i, len));
                    i += len;
                }
            }
            if (list.Count == 0)
            {
                var cn = cert.GetNameInfo(X509NameType.DnsName, false);
                if (!string.IsNullOrEmpty(cn)) list.Add(cn);
            }
            return list;
        }

        static int ReadLen(byte[] d, ref int i)
        {
            int b = d[i++];
            if (b < 0x80) return b;
            int n = b & 0x7f, len = 0;
            for (int k = 0; k < n; k++) len = (len << 8) | d[i++];
            return len;
        }

        // ---------------- .pfx key -> PEM ----------------
        static byte[] ExportRsaPkcs1(X509Certificate2 cert)
        {
            var rsa = cert.GetRSAPrivateKey();
            if (rsa == null) throw new Exception(I18n.T("cert.onlyRsa"));
            RSAParameters p;
            try { p = rsa.ExportParameters(true); }
            catch (CryptographicException)
            {
                // CNG keys imported as "exportable" still refuse plain-text export until the policy allows it
                var cng = rsa as RSACng;
                if (cng == null) throw;
                cng.Key.SetProperty(new CngProperty("Export Policy", BitConverter.GetBytes((int)(CngExportPolicies.AllowExport | CngExportPolicies.AllowPlaintextExport)), CngPropertyOptions.None));
                p = cng.ExportParameters(true);
            }
            // RSAPrivateKey ::= SEQUENCE { version, n, e, d, p, q, dp, dq, qinv }
            var body = new List<byte>();
            foreach (var part in new[] { new byte[] { 0 }, p.Modulus, p.Exponent, p.D, p.P, p.Q, p.DP, p.DQ, p.InverseQ }) body.AddRange(DerInt(part));
            return Der(0x30, body.ToArray());
        }

        static byte[] DerInt(byte[] v)
        {
            int s = 0;
            while (s < v.Length - 1 && v[s] == 0) s++;
            var t = v.Skip(s).ToList();
            if ((t[0] & 0x80) != 0) t.Insert(0, 0);
            return Der(0x02, t.ToArray());
        }

        static byte[] Der(byte tag, byte[] content)
        {
            var r = new List<byte> { tag };
            int n = content.Length;
            if (n < 0x80) r.Add((byte)n);
            else if (n < 0x100) { r.Add(0x81); r.Add((byte)n); }
            else if (n < 0x10000) { r.Add(0x82); r.Add((byte)(n >> 8)); r.Add((byte)n); }
            else { r.Add(0x83); r.Add((byte)(n >> 16)); r.Add((byte)(n >> 8)); r.Add((byte)n); }
            r.AddRange(content);
            return r.ToArray();
        }

        static string Pem(string label, byte[] der)
        {
            var b64 = Convert.ToBase64String(der);
            var sb = new StringBuilder("-----BEGIN " + label + "-----\n");
            for (int i = 0; i < b64.Length; i += 64) sb.Append(b64.Substring(i, Math.Min(64, b64.Length - i))).Append('\n');
            return sb.Append("-----END " + label + "-----\n").ToString();
        }

        static string FirstPem(string text)
        {
            const string end = "-----END CERTIFICATE-----";
            int b = text.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal);
            int i = text.IndexOf(end, StringComparison.Ordinal);
            return b < 0 || i < 0 ? text : text.Substring(b, i + end.Length - b);
        }
    }
}
