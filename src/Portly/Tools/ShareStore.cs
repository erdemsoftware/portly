using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                }
                ini.Save();
            }
            Notify();
        }

        /// <summary>A share started or stopped: the cards and the list refresh.</summary>
        public static void Notify() { var h = Changed; if (h != null) h(); }
    }
}
