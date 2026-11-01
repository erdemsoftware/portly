using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;
using Portly.Packages;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>Runs only this group, offline: Portly.SmokeTest.exe --updates-security. Keys are made here and thrown away.</summary>
        static int UpdatesSecurityOnly()
        {
            Paths.EnsureAll();
            Settings.Load();
            I18n.Load("en");
            try { Tests_UpdatesSecurity().GetAwaiter().GetResult(); }
            catch (Exception ex) { Check("updates security", false, ex.ToString()); }
            Console.WriteLine(_ok + " passed, " + _fail + " failed");
            return _fail == 0 && _ok > 0 ? 0 : 1;
        }

        static async Task Tests_UpdatesSecurity()
        {
            using (RSA key = NewKey(3072), other = NewKey(3072), weak = NewKey(2048))
            {
                string pub = key.ToXmlString(false), otherPub = other.ToXmlString(false);
                var one = new[] { pub };
                var data = Encoding.UTF8.GetBytes("{\r\n  \"version\": \"9.9.9\"\n}\n");
                var sig = Sign(key, data);

                // ---------- signature ----------
                Check("good signature verifies", ManifestSignature.Verify(data, sig, one));
                var tampered = (byte[])data.Clone();
                tampered[tampered.Length / 2] ^= 1;
                Check("one changed byte is refused", !ManifestSignature.Verify(tampered, sig, one));
                Check("line endings changed by git are refused", !ManifestSignature.Verify(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("\r\n", "\n")), sig, one));
                Check("empty signature is refused", !ManifestSignature.Verify(data, "", one) && !ManifestSignature.Verify(data, null, one));
                Check("garbage signature is refused", !ManifestSignature.Verify(data, "not base64 !!!", one) && !ManifestSignature.Verify(data, Convert.ToBase64String(new byte[384]), one) && !ManifestSignature.Verify(data, "AAAA", one));
                Check("signature by another key is refused", !ManifestSignature.Verify(data, Sign(other, data), one));
                Check("a key below 3072 bits is refused", !ManifestSignature.Verify(data, Sign(weak, data), new[] { weak.ToXmlString(false) }));
                Check("a signature over other data is refused", !ManifestSignature.Verify(data, Sign(key, tampered), one));
                Check("no keys verifies nothing", !ManifestSignature.Verify(data, sig, new string[0]));
                Check("the placeholder is no key", !ManifestSignature.Verify(data, sig, new[] { ManifestKeys.Placeholder }));
                Check("the signature of the file is trusted with the keys in the app only", !ManifestSignature.Verify(data, sig));
                Check("Trusted holds the app's key(s)", ManifestKeys.Trusted.Length > 0 && ManifestKeys.Trusted.All(k => k != "" && !k.Contains("<D>")), ManifestKeys.Trusted.Length + " key(s)");

                // ---------- rotation ----------
                Check("rotation: either trusted key verifies", ManifestSignature.Verify(data, sig, new[] { otherPub, pub }) && ManifestSignature.Verify(data, Sign(other, data), new[] { pub, otherPub }));
                Check("rotation: a .sig with two lines works for a client with one key", ManifestSignature.Verify(data, Sign(other, data) + "\n" + sig + "\n", one));

                // ---------- download: fails closed ----------
                var served = new Dictionary<string, byte[]>();
                Func<string, Task<byte[]>> get = u =>
                {
                    served[u] = null;
                    var name = u.Substring(u.LastIndexOf('/') + 1);
                    if (name == "ok.json" || name == "nosig.json" || name == "badsig.json" || name == "tampered.json") return Task.FromResult(name == "tampered.json" ? tampered : data);
                    if (name == "ok.json.sig" || name == "tampered.json.sig") return Task.FromResult(Encoding.ASCII.GetBytes(sig));
                    if (name == "badsig.json.sig") return Task.FromResult(Encoding.ASCII.GetBytes(Sign(other, data)));
                    throw NotFound();
                };
                var good = await SignedManifest.DownloadAsync("ok.json", get, one);
                Check("download: a signed manifest is accepted, with the exact bytes", good.Data.SequenceEqual(data) && good.Text == Encoding.UTF8.GetString(data) && served.Keys.Any(k => k.EndsWith("/ok.json.sig")));
                Check("download: a stripped .sig is refused, no fallback", await Refused("nosig.json", get, one));
                Check("download: a bad .sig is refused", await Refused("badsig.json", get, one));
                Check("download: a changed manifest is refused", await Refused("tampered.json", get, one));
                Check("download: the app's own keys refuse a file signed by another key", await Refused("ok.json", get, null));
                var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(data).ToArray();
                var bomSig = Sign(key, bom);
                var withBom = await SignedManifest.DownloadAsync("x.json", u => Task.FromResult(u.EndsWith(".sig") ? Encoding.ASCII.GetBytes(bomSig) : bom), one);
                Check("download: a BOM is signed but not part of the text", withBom.Text[0] == '{');

                // ---------- cached copies ----------
                var dir = Path.Combine(Paths.Tmp, "updates-security");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "ok.json");
                good.SaveTo(file);
                Check("cache: stored bytes are the signed bytes", File.ReadAllBytes(file).SequenceEqual(data));
                Check("cache: a good copy is read", SignedManifest.ReadCache(file, one) == good.Text);
                File.WriteAllBytes(file, tampered);
                Check("cache: a changed copy is refused and removed", SignedManifest.ReadCache(file, one) == null && !File.Exists(file) && !File.Exists(file + ".sig"));
                good.SaveTo(file);
                File.Delete(file + ".sig");
                Check("cache: a copy without .sig (older Portly) is refused", SignedManifest.ReadCache(file, one) == null);
                good.SaveTo(file);
                Check("cache: a copy signed by another key is refused", SignedManifest.ReadCache(file, new[] { otherPub }) == null);
                Check("cache: no file, no text", SignedManifest.ReadCache(Path.Combine(dir, "none.json"), one) == null);
                CatalogCache();

                // ---------- rollback ----------
                Func<string, string> app = v => "{\"version\":\"" + v + "\",\"url\":\"https://example.com/p.zip\",\"sha256\":\"ab\"}";
                var running = new Version(1, 2, 0);
                Check("rollback: a lower version offers nothing", Updater.Parse(app("1.1.9"), running) == null && Updater.Parse(app("0.9.0"), running) == null);
                Check("rollback: the same version offers nothing", Updater.Parse(app("1.2.0"), running) == null);
                var newer = Updater.Parse(app("1.2.1"), running);
                Check("a newer version is offered", newer != null && newer.Version == new Version(1, 2, 1) && newer.Sha256 == "ab");
                Check("a broken version offers nothing", Updater.Parse(app("x"), running) == null);
                Check("rollback: apply refuses a lower or equal version", Throws(() => Updater.EnsureNewer(new AppUpdate { Version = new Version(1, 1, 9) }, running)) && Throws(() => Updater.EnsureNewer(new AppUpdate { Version = running }, running)) && Throws(() => Updater.EnsureNewer(new AppUpdate(), running)) && !Throws(() => Updater.EnsureNewer(newer, running)));
                var lower = new AppUpdate { Version = new Version(0, 0, 1), Url = "https://127.0.0.1:1/never.zip", Sha256 = "ab" };
                string applied = null;
                try { await Updater.ApplyAsync(lower, null, CancellationToken.None); applied = "no error"; }
                catch (Exception ex) { applied = ex.Message; }
                Check("rollback: ApplyAsync stops before downloading", applied == I18n.T("err.updateRollback"), applied);

                // ---------- package hashes ----------
                await PackageHashes(dir);

                // ---------- the signing tool against the app ----------
                SignTool(dir, key, pub, other);
                try { Directory.Delete(dir, true); } catch { }
            }
            RepoRules();
        }

        static RSA NewKey(int bits)
        {
            return new RSACryptoServiceProvider(bits) { PersistKeyInCsp = false };
        }

        static string Sign(RSA key, byte[] data)
        {
            return Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        static WebException NotFound()
        {
            // HttpWebResponse has no public constructor: a real 404 from a local server
            using (var web = new FakeWeb(req => "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"))
            {
                try { new WebClient { Proxy = null }.DownloadData("http://127.0.0.1:" + web.Port + "/x.sig"); }
                catch (WebException ex) { return ex; }
            }
            throw new InvalidOperationException("no 404");
        }

        static async Task<bool> Refused(string name, Func<string, Task<byte[]>> get, string[] keys)
        {
            try { await SignedManifest.DownloadAsync(name, get, keys); return false; }
            catch (ManifestSecurityException) { return true; }
        }

        static bool Throws(Action a)
        {
            try { a(); return false; }
            catch { return true; }
        }

        /// <summary>The real catalog cache: an unsigned or wrongly signed copy must not replace the embedded catalog.</summary>
        static void CatalogCache()
        {
            var cache = Path.Combine(Paths.Cache, "packages.json");
            const string evil = "{\"packages\":[{\"type\":\"php\",\"version\":\"6.6.6\",\"folder\":\"evil\",\"url\":\"https://evil.example/x.zip\"}]}";
            foreach (var withSig in new[] { false, true })
            {
                File.WriteAllText(cache, evil);
                if (withSig) File.WriteAllText(cache + ".sig", Convert.ToBase64String(new byte[384]));
                Catalog.LoadLocal();
                Check("catalog: a cached copy " + (withSig ? "with a bad .sig" : "without .sig") + " is not used; the embedded catalog is",
                    !Catalog.Entries.Any(e => e.Folder == "evil") && Catalog.Entries.Count > 10, Catalog.Entries.Count + " entries");
                try { File.Delete(cache); File.Delete(cache + ".sig"); } catch { }
            }
            Catalog.LoadLocal();
            Check("offline: the embedded catalog works without any signature", Catalog.Entries.Count > 10 && Catalog.FindByFolder("php", "evil") == null);
        }

        static async Task PackageHashes(string dir)
        {
            var file = Path.Combine(dir, "package.zip");
            var bytes = Encoding.ASCII.GetBytes("package bytes");
            File.WriteAllBytes(file, bytes);
            var right = Net.Sha256(file);
            var wrong = new string('0', 64);
            var e = new PackageEntry { Type = "nginx", Version = "0.0.0-test", Folder = ".updates-security-test", Url = "https://example.com/package.zip", Sha256 = wrong };

            Check("hash: a wrong SHA-256 is refused and the file removed", Throws(() => Installer.VerifyDownload(e, file)) && !File.Exists(file));
            File.WriteAllBytes(file, bytes);
            e.Sha256 = "abc";
            Check("hash: a broken SHA-256 in a manifest is refused", Throws(() => Installer.VerifyDownload(e, file)));
            File.WriteAllBytes(file, bytes);
            e.Sha256 = right.ToUpperInvariant();
            Check("hash: the right SHA-256 passes", !Throws(() => Installer.VerifyDownload(e, file)) && File.Exists(file));
            e.Sha256 = "";
            var warned = "";
            Action<string> listen = l => warned += l;
            Log.Written += listen;
            Check("hash: no SHA-256 still installs", !Throws(() => Installer.VerifyDownload(e, file)) && File.Exists(file));
            Log.Written -= listen;
            Check("hash: no SHA-256 logs a warning", warned.Contains("WARN"), warned);

            // the whole install path, against a local server handing out other bytes than the catalog promises
            using (var web = new FakeWeb(req => "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nevil"))
            {
                var mode = Settings.Current.ProxyMode;
                Settings.Current.ProxyMode = "none";
                try
                {
                    e.Url = "http://127.0.0.1:" + web.Port + "/package.zip";
                    e.Sha256 = wrong;
                    string error = null;
                    try { await Installer.InstallAsync(e, null, CancellationToken.None); }
                    catch (Exception ex) { error = ex.Message; }
                    Check("install: bytes that do not match are refused before extraction", error == I18n.T("err.checksum") && !Directory.Exists(e.InstallDir), error ?? "installed");
                    e.Sha256 = "";
                    error = null;
                    try { await Installer.InstallAsync(e, null, CancellationToken.None); }
                    catch (Exception ex) { error = ex.Message; }
                    Check("install: no SHA-256 and no HTTPS is refused", error != null && !error.Contains("evil") && error == I18n.T("err.noHashHttp", e.TypeName + " " + e.Version) && !Directory.Exists(e.InstallDir), error ?? "installed");
                }
                finally
                {
                    Settings.Current.ProxyMode = mode;
                    try { if (Directory.Exists(e.InstallDir)) Directory.Delete(e.InstallDir, true); } catch { }
                }
            }
        }

        /// <summary>tools\sign-manifests.ps1 must produce what the app accepts. Only where the repository is around (CI, a clone).</summary>
        static void SignTool(string dir, RSA key, string pub, RSA other)
        {
            var script = RepoFile(@"tools\sign-manifests.ps1");
            if (script == null) { Console.WriteLine("  SKIP sign-manifests.ps1: not in a clone"); return; }
            var folder = Path.Combine(dir, "sign");
            Directory.CreateDirectory(folder);
            var manifest = Path.Combine(folder, "app.json");
            var bytes = Encoding.UTF8.GetBytes("{\n  \"note\": \"ç\"\r\n}\n");
            File.WriteAllBytes(manifest, bytes);

            var keyXml = key.ToXmlString(true);
            var signed = RunSignTool(script, "-Dir \"" + folder + "\" -NoKeyCheck", keyXml);
            var sig = File.Exists(manifest + ".sig") ? File.ReadAllText(manifest + ".sig") : "";
            Check("sign tool: the app accepts its signature", signed.Item1 == 0 && ManifestSignature.Verify(bytes, sig, new[] { pub }), "exit " + signed.Item1);
            Check("sign tool: the same bytes give the same .sig", RunSignTool(script, "-Dir \"" + folder + "\" -NoKeyCheck", keyXml).Item1 == 0 && File.ReadAllText(manifest + ".sig") == sig);
            Check("sign tool: the key never reaches the output", !signed.Item2.Contains(keyXml.Substring(keyXml.IndexOf("<D>") + 3, 40)));

            File.Delete(manifest + ".sig");
            var none = RunSignTool(script, "-Dir \"" + folder + "\" -NoKeyCheck", "");
            Check("sign tool: no secret fails, nothing is written", none.Item1 != 0 && !File.Exists(manifest + ".sig"), "exit " + none.Item1);
            var publicOnly = RunSignTool(script, "-Dir \"" + folder + "\" -NoKeyCheck", pub);
            Check("sign tool: a public key cannot sign", publicOnly.Item1 != 0 && !File.Exists(manifest + ".sig"), "exit " + publicOnly.Item1);
            var noMatch = RunSignTool(script, "-CheckKey", other.ToXmlString(true));
            Check("sign tool: a key that is not in ManifestKeys.cs fails the check", noMatch.Item1 != 0, "exit " + noMatch.Item1);
        }

        static Tuple<int, string> RunSignTool(string script, string args, string key)
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " + args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.EnvironmentVariables["PORTLY_MANIFEST_KEY"] = key;
            psi.EnvironmentVariables.Remove("PORTLY_MANIFEST_KEY_2");
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return Tuple.Create(p.ExitCode, output + err.Result);
            }
        }

        /// <summary>Rules that keep the signatures valid once the repository is on GitHub.</summary>
        static void RepoRules()
        {
            var attributes = RepoFile(".gitattributes");
            if (attributes == null) { Console.WriteLine("  SKIP repository rules: not in a clone"); return; }
            var lines = File.ReadAllLines(attributes);
            Check("git must not change line endings of manifests or signatures", lines.Contains("manifest/*.json -text") && lines.Contains("manifest/*.json.sig -text"));
        }

        static string RepoFile(string relative)
        {
            for (var d = new DirectoryInfo(Paths.Root); d != null; d = d.Parent)
            {
                var f = Path.Combine(d.FullName, relative);
                if (Directory.Exists(Path.Combine(d.FullName, "manifest")) && File.Exists(f)) return f;
            }
            return null;
        }
    }
}
