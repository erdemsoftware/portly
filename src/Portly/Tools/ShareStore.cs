using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>One shared project: its local network port and/or its last internet address, kept after the share stops.</summary>
    public class ShareEntry
    {
        public string Host = "";
        public bool Lan;            // has a local network share (running or not)
        public int LanPort;         // the same port every time: the address stays the same
        public bool Online;         // has been shared on the internet
        public string OnlineUrl = ""; // last Cloudflare address (a new random one is given at every start)
        public string Note = "";    // the user's own note ("for the customer", "phone test" ...)
        public string LanPassword = ""; // asked on the device before the site opens ("" = none)
        public bool LanAuto;        // start the local network share again when Portly starts
        public int LanMinutes;      // stops by itself after this many minutes (0 = no limit)
        public string OnlinePassword = "";
        public int OnlineMinutes;
        public string FixedHost = ""; // internet share on the user's own Cloudflare domain (demo.example.com) instead of a random one
    }

    /// <summary>Remembered shares (usr\shares.ini): listed and managed in "Shared projects", also while they are stopped.</summary>
    public static class ShareStore
    {
        public static event Action Changed;
        static string FilePath { get { return Path.Combine(Paths.Usr, "shares.ini"); } }
        static readonly object Lock = new object();

        public static List<ShareEntry> All()
        {
            lock (Lock)
            {
                var ini = new Ini(FilePath);
                return ini.Sections().Select(h => Read(ini, h)).Where(e => e.Lan || e.Online).OrderBy(e => e.Host, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public static ShareEntry Get(string host)
        {
            lock (Lock) return Read(new Ini(FilePath), host);
        }

        static ShareEntry Read(Ini ini, string host)
        {
            return new ShareEntry
            {
                Host = host,
                Lan = ini.GetBool(host, "lan", false),
                LanPort = ini.GetInt(host, "lanPort", 0),
                Online = ini.GetBool(host, "online", false),
                OnlineUrl = ini.Get(host, "onlineUrl", ""),
                Note = ini.Get(host, "note", ""),
                LanPassword = Unprotect(ini.Get(host, "lanPassword", "")),
                LanAuto = ini.GetBool(host, "lanAuto", false),
                LanMinutes = ini.GetInt(host, "lanMinutes", 0),
                OnlinePassword = Unprotect(ini.Get(host, "onlinePassword", "")),
                OnlineMinutes = ini.GetInt(host, "onlineMinutes", 0),
                FixedHost = ini.Get(host, "fixedHost", ""),
            };
        }

        public static void Update(string host, Action<ShareEntry> change)
        {
            lock (Lock)
            {
                var ini = new Ini(FilePath);
                var e = Read(ini, host);
                change(e);
                ini.RemoveSection(host);
                if (e.Lan || e.Online)
                {
                    ini.Set(host, "lan", e.Lan);
                    if (e.LanPort > 0) ini.Set(host, "lanPort", e.LanPort);
                    ini.Set(host, "online", e.Online);
                    if (e.OnlineUrl != "") ini.Set(host, "onlineUrl", e.OnlineUrl);
                    if (e.Note != "") ini.Set(host, "note", e.Note.Replace("\r", " ").Replace("\n", " "));
                    if (e.LanPassword != "") ini.Set(host, "lanPassword", Protect(e.LanPassword));
                    if (e.LanAuto) ini.Set(host, "lanAuto", true);
                    if (e.LanMinutes > 0) ini.Set(host, "lanMinutes", e.LanMinutes);
                    if (e.OnlinePassword != "") ini.Set(host, "onlinePassword", Protect(e.OnlinePassword));
                    if (e.OnlineMinutes > 0) ini.Set(host, "onlineMinutes", e.OnlineMinutes);
                    if (e.FixedHost != "") ini.Set(host, "fixedHost", e.FixedHost);
                }
                ini.Save();
            }
            Notify();
        }

        // the share password is kept with Windows DPAPI (readable only by this Windows user)
        static string Protect(string s)
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser));
        }

        static string Unprotect(string s)
        {
            if (s == "") return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        /// <summary>A share started or stopped: the cards and the list refresh.</summary>
        public static void Notify() { var h = Changed; if (h != null) h(); }
    }
}
