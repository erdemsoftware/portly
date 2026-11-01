using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Packages
{
    /// <summary>A manifest was refused because its signature is missing or wrong. The message is meant for the user.</summary>
    public class ManifestSecurityException : Exception
    {
        public ManifestSecurityException(string message) : base(message) { }
    }

    /// <summary>
    /// Detached RSA signatures: PKCS#1 v1.5 with SHA-256, built into .NET Framework and Windows 7 (Ed25519 is not).
    /// A .sig file holds one base64 signature per line, so a file can carry two keys during a key rotation.
    /// The padding is deterministic: signing unchanged bytes gives the same .sig, so re-signing creates no commit.
    /// </summary>
    public static class ManifestSignature
    {
        const int MinKeyBits = 3072;
        const int MaxLines = 8;

        /// <summary>True when one signature line verifies under one key (default: ManifestKeys.Trusted). Never throws.</summary>
        public static bool Verify(byte[] data, string signature, string[] keys = null)
        {
            if (data == null || string.IsNullOrWhiteSpace(signature)) return false;
            foreach (var line in signature.Split('\r', '\n').Where(l => l.Trim() != "").Take(MaxLines))
            {
                byte[] sig;
                try { sig = Convert.FromBase64String(line.Trim()); }
                catch (FormatException) { continue; }
                foreach (var key in keys ?? ManifestKeys.Trusted)
                    if (VerifyOne(data, sig, key)) return true;
            }
            return false;
        }

        static bool VerifyOne(byte[] data, byte[] sig, string keyXml)
        {
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(keyXml);
                    return rsa.KeySize >= MinKeyBits && rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception) { return false; } // the placeholder, a broken key, a signature of the wrong size: not verified
        }
    }

    /// <summary>
    /// A manifest (manifest/*.json) whose exact bytes passed the signature check. There is no unsigned path:
    /// downloads are refused without a valid .sig, and a cached copy is used only with its own valid .sig.
    /// Copies embedded in Portly.exe are not checked; they are part of the signed release.
    /// </summary>
    public class SignedManifest
    {
        public const string SigExtension = ".sig";

        public byte[] Data;
        public string Signature;
        public string Text;

        /// <summary>
        /// Download a manifest and its signature. Throws ManifestSecurityException when the signature is missing or wrong;
        /// network errors stay as they are. get and keys are for the tests.
        /// </summary>
        public static async Task<SignedManifest> DownloadAsync(string name, Func<string, Task<byte[]>> get = null, string[] keys = null)
        {
            get = get ?? (u => Net.GetBytesAsync(u));
            var url = AppInfo.ManifestBase + name;
            var data = await get(url);
            string signature;
            try { signature = Encoding.ASCII.GetString(await get(url + SigExtension)); }
            catch (WebException ex) when (IsNotFound(ex)) { throw new ManifestSecurityException(I18n.T("err.manifestUnsigned", name)); }
            if (!ManifestSignature.Verify(data, signature, keys)) throw new ManifestSecurityException(I18n.T("err.manifestBadSignature", name));
            return new SignedManifest { Data = data, Signature = signature, Text = Decode(data) };
        }

        /// <summary>Store the verified bytes with their signature, so the copy can be checked again when it is read.</summary>
        public void SaveTo(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, Data);
            File.WriteAllText(file + SigExtension, Signature, Encoding.ASCII);
        }

        /// <summary>
        /// The text of a cached manifest, or null when there is none or it does not verify. A copy that fails (also one
        /// saved by a Portly without signatures) is removed: the caller falls back to the embedded copy until the next refresh.
        /// </summary>
        public static string ReadCache(string file, string[] keys = null)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var data = File.ReadAllBytes(file);
                var sig = file + SigExtension;
                if (File.Exists(sig) && ManifestSignature.Verify(data, File.ReadAllText(sig, Encoding.ASCII), keys)) return Decode(data);
                Log.Warn("Ignoring " + Path.GetFileName(file) + " in the cache: it has no valid signature");
                File.Delete(file);
                if (File.Exists(sig)) File.Delete(sig);
            }
            catch (IOException ex) { Log.Warn("Cached " + Path.GetFileName(file) + ": " + ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Warn("Cached " + Path.GetFileName(file) + ": " + ex.Message); }
            return null;
        }

        static string Decode(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data);
            return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
        }

        static bool IsNotFound(WebException ex)
        {
            var r = ex.Response as HttpWebResponse;
            return r != null && r.StatusCode == HttpStatusCode.NotFound;
        }
    }
}
