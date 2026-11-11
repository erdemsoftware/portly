using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Portly.Core;

namespace Portly.Packages
{
    /// <summary>One downloadable file of a release: an https address and the SHA-256 the signed manifest promises for it.</summary>
    public class UpdateAsset
    {
        public string Url;
        public string Sha256;
        public long Size;
    }

    /// <summary>How the running Portly is installed, and so how it replaces itself.</summary>
    public enum UpdateKind
    {
        None,
        WindowsZip,     // Portly.exe, lang, templates replaced next to the exe (the 1.x way)
        MacApp,         // the Portly.app bundle is swapped (the zip asset)
        LinuxAppImage,  // the .AppImage file is replaced
        LinuxTarball,   // the folder holding Portly is swapped (the tar.gz asset)
        LinuxDeb,       // the .deb asset installed with pkexec
    }

    /// <summary>What an update would do on this machine. Reason is set (and CanInstall false) when it cannot be done by Portly itself.</summary>
    public class UpdatePlan
    {
        public UpdateKind Kind;
        /// <summary>The thing that gets replaced: Portly.app, the .AppImage file, the install folder, /opt/portly or the Windows program folder.</summary>
        public string Target;
        /// <summary>The file to download; null when the release has none for this system.</summary>
        public UpdateAsset Asset;
        /// <summary>Why Portly cannot update itself here (already translated), or null.</summary>
        public string Reason;
        public bool CanInstall { get { return Reason == null && Kind != UpdateKind.None && Asset != null; } }
    }

    /// <summary>The release files of an app.json: the optional "assets" object (Portly 2). Portly 1.x reads only the top-level fields.</summary>
    public static class UpdateAssets
    {
        static readonly Regex Hex64 = new Regex("^[0-9a-fA-F]{64}$");

        /// <summary>
        /// "assets": { "windows-x64": {url, sha256}, "macos-arm64": {...}, "linux-x64": { "tarball": {...}, "appimage": {...}, "deb": {...} } }.
        /// Keys of the result: "windows-x64", "macos-arm64", "linux-x64/appimage" ... Entries without an https address or a 64-digit
        /// SHA-256 are dropped, so a half-filled manifest offers nothing instead of something unverifiable.
        /// </summary>
        public static Dictionary<string, UpdateAsset> Parse(Dictionary<string, object> manifest)
        {
            var result = new Dictionary<string, UpdateAsset>(StringComparer.OrdinalIgnoreCase);
            var assets = manifest == null ? null : manifest.Obj("assets");
            if (assets == null) return result;
            foreach (var platform in assets)
            {
                var o = platform.Value as Dictionary<string, object>;
                if (o == null) continue;
                if (o.ContainsKey("url")) Add(result, platform.Key, o);
                else
                    foreach (var kind in o)
                    {
                        var k = kind.Value as Dictionary<string, object>;
                        if (k != null) Add(result, platform.Key + "/" + kind.Key, k);
                    }
            }
            return result;
        }

        static void Add(Dictionary<string, UpdateAsset> into, string key, Dictionary<string, object> o)
        {
            var a = new UpdateAsset { Url = o.Str("url"), Sha256 = o.Str("sha256").Trim(), Size = o.ContainsKey("size") ? ParseLong(o.Str("size")) : 0 };
            if (IsValid(a)) into[key] = a;
        }

        static long ParseLong(string s) { long n; return long.TryParse(s, out n) && n > 0 ? n : 0; }

        /// <summary>https only, and a full SHA-256.</summary>
        public static bool IsValid(UpdateAsset a)
        {
            if (a == null || string.IsNullOrEmpty(a.Url) || string.IsNullOrEmpty(a.Sha256)) return false;
            Uri u;
            return Uri.TryCreate(a.Url, UriKind.Absolute, out u) && u.Scheme == Uri.UriSchemeHttps && Hex64.IsMatch(a.Sha256);
        }

        /// <summary>The asset for a system and install kind, or null. os: windows | macos | linux; arch: x64 | arm64.</summary>
        public static UpdateAsset Select(AppUpdate u, string os, string arch, UpdateKind kind)
        {
            if (u == null) return null;
            string key;
            switch (kind)
            {
                case UpdateKind.WindowsZip: key = "windows-" + arch; break;
                case UpdateKind.MacApp: key = "macos-" + arch; break;
                case UpdateKind.LinuxAppImage: key = "linux-" + arch + "/appimage"; break;
                case UpdateKind.LinuxTarball: key = "linux-" + arch + "/tarball"; break;
                case UpdateKind.LinuxDeb: key = "linux-" + arch + "/deb"; break;
                default: return null;
            }
            UpdateAsset a;
            if (u.Assets != null && u.Assets.TryGetValue(key, out a)) return a;
            // the top-level url / sha256 of app.json is the Windows zip: Portly 1.x reads it, and a 2.x Windows build without "assets" does too
            if (kind == UpdateKind.WindowsZip && os == "windows")
            {
                var top = new UpdateAsset { Url = u.Url, Sha256 = u.Sha256 };
                return IsValid(top) ? top : null;
            }
            return null;
        }
    }
}
