using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>One inbound Windows Firewall rule, as far as a share cares.</summary>
    public class FirewallRule
    {
        public string Name = "";
        public bool Allow, Enabled = true;
        public int Profiles;
        public int Protocol = 6;      // 6 = TCP, 256 = any
        public string Ports = "*";
        public string App = "";       // "" = any program
    }

    /// <summary>The firewall as this computer has it now.</summary>
    public class FirewallView
    {
        public int Active;            // profiles of the connected networks
        public int Off;               // profiles with the firewall switched off
        public List<FirewallRule> Rules = new List<FirewallRule>();   // only rules named for a share and rules of Portly.exe
    }

    public enum FirewallNeed
    {
        None,          // devices get through already, or nothing can be told
        AddRule,
        NotPrivate,    // a rule for private networks would do nothing on this network
        Blocked,       // a block rule for Portly.exe wins over any allow rule
    }

    /// <summary>
    /// The way in for local network shares. The listener is on all addresses, and without a rule Windows asks once per program
    /// path whether to allow it: "Allow" means every port of that Portly.exe, and moving a portable Portly asks again.
    /// So a share gets a rule of its own instead: its TCP port, private networks, this subnet ("Portly share 8900", any location
    /// of Portly.exe). The rule goes when the share is stopped. Reading rules needs no rights, only changing them does, so:
    /// a rule that exists costs no UAC; refused ports are remembered and not asked again by automatic starts; one UAC prompt
    /// also takes the rules left behind (Portly closed while sharing, UAC refused at stop). Shares marked to start with Portly
    /// keep their rule when Portly exits, or every start would ask.
    /// </summary>
    public static class ShareFirewall
    {
        public const int Domain = 1, Private = 2, Public = 4;
        const string Prefix = "Portly share ";

        // swapped by the tests, which must not raise UAC or depend on the machine
        public static Func<string, string, int> Elevate = (exe, args) => WinUtil.RunElevated(exe, args);
        public static Func<FirewallView> Read = () => ReadWindows(Paths.Exe);

        static readonly object Busy = new object();
        static readonly Dictionary<int, string> Notes = new Dictionary<int, string>();

        static string DeclinedFile { get { return Path.Combine(Paths.Usr, "share-firewall-declined.txt"); } }

        static string RuleName(int port) { return Prefix + port; }

        public static string DeleteCommand(int port) { return "netsh advfirewall firewall delete rule name=\"" + RuleName(port) + "\""; }

        public static string AddCommand(int port)
        {
            return "netsh advfirewall firewall add rule name=\"" + RuleName(port) + "\" dir=in action=allow protocol=TCP localport=" + port + " profile=private remoteip=localsubnet";
        }

        /// <summary>What the port needs now. Nothing here needs rights.</summary>
        public static FirewallNeed Decide(FirewallView view, int port, string exe)
        {
            if (view == null) return FirewallNeed.None;  // firewall service off or replaced: nothing a rule could do
            int guarded = view.Active & ~view.Off;
            if (guarded == 0) return FirewallNeed.None;
            var program = view.Rules.Where(r => r.Enabled && Same(r.App, exe) && (r.Protocol == 6 || r.Protocol == 256) && HasPort(r.Ports, port) && (r.Profiles & guarded) != 0).ToList();
            if (program.Any(r => !r.Allow)) return FirewallNeed.Blocked;
            if (program.Any(r => r.Allow)) return FirewallNeed.None;
            bool own = view.Rules.Any(r => r.Name == RuleName(port) && r.Allow && r.Enabled && (r.Profiles & Private) != 0);
            if ((guarded & Private) == 0) return FirewallNeed.NotPrivate;
            return own ? FirewallNeed.None : FirewallNeed.AddRule;
        }

        static bool Same(string app, string exe)
        {
            if (string.IsNullOrEmpty(app)) return false;
            try { return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(app)), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static bool HasPort(string ports, int port)
        {
            if (string.IsNullOrEmpty(ports) || ports == "*") return true;
            foreach (var part in ports.Split(','))
            {
                var ends = part.Trim().Split('-');
                int low, high;
                if (int.TryParse(ends[0], out low) && int.TryParse(ends[ends.Length - 1], out high) && port >= low && port <= high) return true;
            }
            return false;
        }

        /// <summary>Ports that have a rule of a share.</summary>
        static List<int> RulePorts(FirewallView view)
        {
            var found = new List<int>();
            if (view == null) return found;
            foreach (var r in view.Rules)
            {
                int port;
                if (r.Name.StartsWith(Prefix, StringComparison.Ordinal) && int.TryParse(r.Name.Substring(Prefix.Length), out port) && !found.Contains(port)) found.Add(port);
            }
            return found;
        }

        /// <summary>Why devices may not reach the share on this port, for the window with the address; null when all is well.</summary>
        public static string Note(int port)
        {
            lock (Notes) { string n; return Notes.TryGetValue(port, out n) ? n : null; }
        }

        static void Say(int port, string text)
        {
            lock (Notes) { if (text == null) Notes.Remove(port); else Notes[port] = text; }
            if (text != null) Log.Warn(text);
        }

        /// <summary>
        /// Before a share listens on <paramref name="port"/>. Never throws and never stops the share: a refused UAC only means
        /// a message. <paramref name="automatic"/> starts do not ask again after a refusal.
        /// </summary>
        public static void Open(int port, bool automatic)
        {
            lock (Busy)
            {
                Say(port, null);
                var view = Read();
                switch (Decide(view, port, Paths.Exe))
                {
                    case FirewallNeed.Blocked: Say(port, I18n.T("lan.firewallBlocked", Paths.Exe)); return;
                    case FirewallNeed.NotPrivate: Say(port, I18n.T("lan.firewallNotPrivate", port)); return;
                    case FirewallNeed.None: return;
                }
                var declined = Declined();
                if (automatic && declined.Contains(port)) { Say(port, I18n.T("lan.firewallDeclined", port)); return; }
                var stale = RulePorts(view).Except(LanShare.PortsInUse()).Where(p => p != port).ToList();
                int code = Run(stale, new[] { port });
                if (code == -2)
                {
                    declined.Add(port);
                    Save(declined);
                    Say(port, I18n.T("lan.firewallDeclined", port));
                    return;
                }
                declined.Remove(port);
                declined.ExceptWith(stale);
                Save(declined);
                if (Decide(Read(), port, Paths.Exe) == FirewallNeed.None) Log.Info(I18n.T("lan.firewallAdded", port));
                else Say(port, I18n.T("lan.firewallFailed", port));
            }
        }

        /// <summary>After a share stopped: its rule goes, with anything else left behind, in one UAC prompt. Waits for that prompt.</summary>
        public static void Close(int port)
        {
            lock (Busy)
            {
                lock (Notes) Notes.Remove(port);
                var view = Read();
                var rules = RulePorts(view);
                // started again in the meantime: the rule is needed after all
                if (!rules.Contains(port) || LanShare.IsPortShared(port)) return;
                Remove(rules.Except(LanShare.PortsInUse()).Union(new[] { port }).ToList());
            }
        }

        /// <summary>
        /// When Portly starts, before the automatic shares: one UAC prompt at most, for rules they lack and rules nothing uses.
        /// Whatever the user refused stays out of it, or that prompt would come at every start.
        /// </summary>
        public static void Prepare(IEnumerable<int> automatic)
        {
            lock (Busy)
            {
                var view = Read();
                if (view == null) return;
                var keep = automatic.ToList();
                var declined = Declined();
                var add = keep.Where(p => !declined.Contains(p) && Decide(view, p, Paths.Exe) == FirewallNeed.AddRule).ToList();
                var drop = RulePorts(view).Except(keep).Except(declined).ToList();
                if (add.Count == 0 && drop.Count == 0) return;
                int code = Run(drop, add);
                if (code == -2)
                {
                    declined.UnionWith(add);
                    declined.UnionWith(drop);
                    Save(declined);
                    return;
                }
                foreach (var p in drop) Log.Info(I18n.T("lan.firewallRemoved", p));
            }
        }

        static void Remove(List<int> ports)
        {
            var declined = Declined();
            if (Run(ports, new int[0]) == -2)
            {
                declined.UnionWith(ports);
                Save(declined);
                foreach (var p in ports) Log.Warn(I18n.T("lan.firewallRemoveDeclined", p));
                return;
            }
            declined.ExceptWith(ports);
            Save(declined);
            foreach (var p in ports) Log.Info(I18n.T("lan.firewallRemoved", p));
        }

        /// <summary>One elevated command for everything: one UAC prompt. -2 = refused.</summary>
        static int Run(IList<int> drop, IList<int> add)
        {
            var parts = drop.Union(add).Select(DeleteCommand).Concat(add.Select(AddCommand));
            return Elevate("cmd.exe", "/c " + string.Join(" & ", parts));
        }

        static HashSet<int> Declined()
        {
            var set = new HashSet<int>();
            try
            {
                if (File.Exists(DeclinedFile))
                    foreach (var line in File.ReadAllLines(DeclinedFile))
                    {
                        int port;
                        if (int.TryParse(line.Trim(), out port)) set.Add(port);
                    }
            }
            catch { }
            return set;
        }

        static void Save(HashSet<int> ports)
        {
            try
            {
                if (ports.Count > 0) File.WriteAllLines(DeclinedFile, ports.Select(p => p.ToString()));
                else if (File.Exists(DeclinedFile)) File.Delete(DeclinedFile);
            }
            catch { }
        }

        static object Get(object com, string name) { return com.GetType().InvokeMember(name, BindingFlags.GetProperty, null, com, null); }

        /// <summary>The Windows firewall API needs no rights to read and has no language, unlike the output of netsh. Null when it cannot be read.</summary>
        public static FirewallView ReadWindows(string exe)
        {
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (type == null) return null;
                var policy = Activator.CreateInstance(type);
                var view = new FirewallView { Active = (int)Get(policy, "CurrentProfileTypes") };
                foreach (var profile in new[] { Domain, Private, Public })
                    if (!(bool)type.InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy, new object[] { profile })) view.Off |= profile;
                foreach (var rule in (IEnumerable)Get(policy, "Rules"))
                {
                    if ((int)Get(rule, "Direction") != 1) continue; // 1 = inbound
                    var name = Get(rule, "Name") as string ?? "";
                    var app = Get(rule, "ApplicationName") as string ?? "";
                    if (!name.StartsWith(Prefix, StringComparison.Ordinal) && !Same(app, exe)) continue;
                    view.Rules.Add(new FirewallRule
                    {
                        Name = name,
                        App = app,
                        Allow = (int)Get(rule, "Action") == 1,
                        Enabled = (bool)Get(rule, "Enabled"),
                        Profiles = (int)Get(rule, "Profiles"),
                        Protocol = (int)Get(rule, "Protocol"),
                        Ports = Get(rule, "LocalPorts") as string ?? "*",
                    });
                }
                return view;
            }
            catch (Exception ex)
            {
                Log.Info("Windows Firewall: " + ex.Message);
                return null;
            }
        }
    }
}
