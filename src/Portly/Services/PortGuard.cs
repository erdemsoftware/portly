using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Portly.Core;

namespace Portly.Services
{
    public class PortConflict
    {
        public Service Service;
        public string Key;          // settings key, e.g. HttpPort
        public string Label;        // HTTP, HTTPS, MySQL ...
        public int Port;
        public PortOwnerInfo Owner;
        public int Suggested;       // a free port to use instead (0 = none found)
    }

    /// <summary>
    /// Checks every port a service needs BEFORE starting it.
    ///  - leftovers of this Portly folder are closed silently,
    ///  - anything else is reported with the owning program and a free alternative port.
    /// </summary>
    public static class PortGuard
    {
        static readonly Dictionary<string, int[]> Alternatives = new Dictionary<string, int[]>
        {
            { "HttpPort", new[] { 8080, 8000, 8081, 8888, 8090 } },
            { "HttpsPort", new[] { 8443, 4443, 9443, 10443 } },
            { "MysqlPort", new[] { 3307, 3308, 3309, 3310 } },
            { "RedisPort", new[] { 6380, 6381, 6382 } },
            { "MailpitWebPort", new[] { 8026, 8027, 8126 } },
            { "MailpitSmtpPort", new[] { 1026, 1027, 2525 } },
        };

        static int Get(string key) { return Settings.Current.Raw.GetInt("services", key, 0); }

        /// <summary>(key, label, port) pairs a service needs.</summary>
        public static List<Tuple<string, string, int>> Needs(Service s)
        {
            var st = Settings.Current;
            var list = new List<Tuple<string, string, int>>();
            switch (s.Id)
            {
                case "web":
                    list.Add(Tuple.Create("HttpPort", "HTTP", st.HttpPort));
                    if (st.Ssl) list.Add(Tuple.Create("HttpsPort", "HTTPS", st.HttpsPort));
                    break;
                case "mysql": list.Add(Tuple.Create("MysqlPort", "MySQL", st.MysqlPort)); break;
                case "redis": list.Add(Tuple.Create("RedisPort", "Redis", st.RedisPort)); break;
                case "mailpit":
                    list.Add(Tuple.Create("MailpitWebPort", "Mailpit web", st.MailpitWebPort));
                    list.Add(Tuple.Create("MailpitSmtpPort", "Mailpit SMTP", st.MailpitSmtpPort));
                    break;
            }
            return list;
        }

        public static List<PortConflict> Check(IEnumerable<Service> services)
        {
            var conflicts = new List<PortConflict>();
            var used = new HashSet<int>(new[] { "HttpPort", "HttpsPort", "MysqlPort", "RedisPort", "MailpitWebPort", "MailpitSmtpPort" }.Select(Get));
            foreach (var s in services)
            {
                if (s.IsActive || !s.IsInstalled) continue;
                foreach (var need in Needs(s))
                {
                    if (!ProcessUtil.IsPortInUse(need.Item3)) continue;
                    var owner = ProcessUtil.PortOwnerInfo(need.Item3);
                    if (owner != null && owner.IsOurs)
                    {
                        // a Portly service left behind (e.g. after a crash of an old version): close it
                        Log.Warn(I18n.T("port.orphan", need.Item3, owner));
                        ProcessUtil.KillTree(owner.Pid);
                        Thread.Sleep(500);
                        if (!ProcessUtil.IsPortInUse(need.Item3)) continue;
                    }
                    var c = new PortConflict { Service = s, Key = need.Item1, Label = need.Item2, Port = need.Item3, Owner = owner ?? new PortOwnerInfo() };
                    c.Suggested = ProcessUtil.FreePort(Alternatives[need.Item1], used);
                    if (c.Suggested > 0) used.Add(c.Suggested);
                    conflicts.Add(c);
                }
            }
            return conflicts;
        }

        /// <summary>Switch every conflicting port to its suggestion. Returns a summary like "HTTP 80 -> 8080".</summary>
        public static string UseSuggestions(List<PortConflict> conflicts)
        {
            var parts = new List<string>();
            foreach (var c in conflicts.Where(x => x.Suggested > 0))
            {
                Settings.Current.Raw.Set("services", c.Key, c.Suggested);
                parts.Add(c.Label + " " + c.Port + " → " + c.Suggested);
            }
            Settings.Current.Save();
            var text = string.Join(", ", parts);
            if (text != "") Log.Info(I18n.T("port.changed", text));
            return text;
        }

        /// <summary>Close the programs holding the ports. Returns the ones that could not be closed.</summary>
        public static List<string> CloseOwners(List<PortConflict> conflicts)
        {
            var failed = new List<string>();
            foreach (var pid in conflicts.Where(c => !c.Owner.IsSystem).Select(c => c.Owner.Pid).Distinct())
            {
                Log.Info(I18n.T("port.closing", conflicts.First(c => c.Owner.Pid == pid).Owner));
                ProcessUtil.KillTree(pid);
            }
            Thread.Sleep(800);
            foreach (var c in conflicts)
                if (ProcessUtil.IsPortInUse(c.Port)) failed.Add(c.Label + " " + c.Port + " (" + c.Owner + ")");
            return failed;
        }
    }
}
