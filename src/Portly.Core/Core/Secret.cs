using System;
using System.IO;
using System.Security.Cryptography;

namespace Portly.Core
{
    /// <summary>
    /// Encrypts saved passwords and keys. Windows: DPAPI (bound to the Windows user, or the machine for the service).
    /// macOS / Linux: AES-256-GCM with a random key in usr/secret.key that only the user can read (0600).
    /// </summary>
    public static class Secret
    {
        public static byte[] Protect(byte[] data, byte[] entropy, bool machine = false)
        {
#if !NETFRAMEWORK
            if (!OS.IsWindows) return UnixProtect(data, entropy);
#endif
            return ProtectedData.Protect(data, entropy, machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
        }

        public static byte[] Unprotect(byte[] blob, byte[] entropy, bool machine = false)
        {
#if !NETFRAMEWORK
            if (!OS.IsWindows) return UnixUnprotect(blob, entropy);
#endif
            return ProtectedData.Unprotect(blob, entropy, machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
        }

#if !NETFRAMEWORK
        const byte Format = 1;
        static readonly object KeyLock = new object();
        static byte[] _key;

        static string KeyFile { get { return Path.Combine(Paths.Usr, "secret.key"); } }

        static byte[] Key()
        {
            lock (KeyLock)
            {
                if (_key != null) return _key;
                if (File.Exists(KeyFile))
                {
                    var k = File.ReadAllBytes(KeyFile);
                    if (k.Length != 32) throw new CryptographicException("usr/secret.key is damaged");
                    return _key = k;
                }
                Directory.CreateDirectory(Paths.Usr);
                var key = RandomNumberGenerator.GetBytes(32);
                var tmp = KeyFile + ".tmp";
                using (var fs = new FileStream(tmp, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
                    fs.Write(key, 0, key.Length);
                File.Move(tmp, KeyFile);
                return _key = key;
            }
        }

        // blob: format(1) | nonce(12) | tag(16) | ciphertext
        static byte[] UnixProtect(byte[] data, byte[] entropy)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[data.Length];
            using (var aes = new AesGcm(Key(), 16)) aes.Encrypt(nonce, data, cipher, tag, entropy);
            var blob = new byte[1 + 12 + 16 + cipher.Length];
            blob[0] = Format;
            Buffer.BlockCopy(nonce, 0, blob, 1, 12);
            Buffer.BlockCopy(tag, 0, blob, 13, 16);
            Buffer.BlockCopy(cipher, 0, blob, 29, cipher.Length);
            return blob;
        }

        static byte[] UnixUnprotect(byte[] blob, byte[] entropy)
        {
            if (blob == null || blob.Length < 29 || blob[0] != Format) throw new CryptographicException("Unknown secret format");
            var plain = new byte[blob.Length - 29];
            using (var aes = new AesGcm(Key(), 16))
                aes.Decrypt(new ReadOnlySpan<byte>(blob, 1, 12), new ReadOnlySpan<byte>(blob, 29, plain.Length), new ReadOnlySpan<byte>(blob, 13, 16), plain, entropy);
            return plain;
        }
#endif
    }
}
