#if !LEGACY
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>
    /// Access from WSL (feature "wsl", Modern edition). In WSL2's default NAT mode code inside Linux cannot reach
    /// Windows' 127.0.0.1, it reaches the host through the "vEthernet (WSL)" adapter. Portly then also listens on that
    /// adapter's address, adds a database user for the WSL subnet and a firewall rule limited to it.
    /// With networkingMode=mirrored localhost already works and nothing is changed.
    /// </summary>
    public static class WslAccess
    {
        public const string User = "portly_wsl";
        const string RuleName = "Portly MySQL (WSL)";

        public class Adapter
        {
            public IPAddress Ip;
            public IPAddress Mask;
            public string Name;
            public string Subnet { get { return Network(Ip, Mask) + "/" + PrefixLength(Mask); } }
            /// <summary>MySQL host pattern for the subnet: 172.29.% (first two octets).</summary>
            public string UserHost { get { var b = Ip.GetAddressBytes(); return b[0] + "." + b[1] + ".%"; } }
        }

        public static bool Supported { get { return OsInfo.IsWin10OrLater; } }

        /// <summary>networkingMode=mirrored in %UserProfile%\.wslconfig.</summary>
        public static bool IsMirrored()
        {
            try
            {
                var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");
                return File.Exists(f) && new Ini(f).Get("wsl2", "networkingMode", "").Trim().Equals("mirrored", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>The WSL virtual adapter (exists after WSL was started once since boot), or null.</summary>
        public static Adapter Find()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name.IndexOf("WSL", StringComparison.OrdinalIgnoreCase) < 0 || ni.OperationalStatus != OperationalStatus.Up) continue;
                    var ua = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (ua != null) return new Adapter { Ip = ua.Address, Mask = ua.IPv4Mask, Name = ni.Name };
                }
            }
            catch { }
            return null;
        }

        /// <summary>Server version accepts several bind addresses (MySQL 8.0.13+, MariaDB 10.11+).</summary>
        public static bool MultiBind()
        {
            var engine = Settings.Current.DbEngine;
            var v = PackageTypes.ParseVersion(PackageTypes.ActiveFolder(engine));
            return engine == "mariadb" ? v >= new Version(10, 11) : v >= new Version(8, 0, 13);
        }

        /// <summary>Extra bind address for my.ini, or null (feature off, not set up, mirrored, adapter missing).</summary>
        public static string BindAddress()
        {
            var s = Settings.Current;
            if (!Supported || !Features.IsOn("wsl") || !s.WslAccess || s.AllowLan) return null;
            var a = Find();
            // a missing address would stop mysqld from starting: only bind what exists now
            return a == null ? null : a.Ip.ToString();
        }

        /// <summary>Set up access. Returns the text with the connection details; throws with the error.</summary>
        public static string Enable()
        {
            var s = Settings.Current;
            if (ServiceManager.MySql.State != ServiceState.Running) throw new Exception(I18n.T("err.mysqlNotRunning"));
            var a = Find();
            if (a == null) throw new Exception(I18n.T("wsl.noAdapter"));
            if (!s.AllowLan && !MultiBind()) throw new Exception(I18n.T("wsl.oldServer"));
            var pass = s.WslPassword;
            if (pass == "") pass = Database.RandomPassword(20);
            // old user of another subnet first
            if (s.WslUserHost != "" && s.WslUserHost != a.UserHost) DropUser(s.WslUserHost);
            var u = Database.SqlString(User) + "@" + Database.SqlString(a.UserHost);
            var r = Database.Sql("CREATE USER IF NOT EXISTS " + u + " IDENTIFIED BY " + Database.SqlString(pass) + ";\n" +
                                 "ALTER USER " + u + " IDENTIFIED BY " + Database.SqlString(pass) + ";\n" +
                                 "GRANT ALL PRIVILEGES ON *.* TO " + u + " WITH GRANT OPTION;\nFLUSH PRIVILEGES;\n");
            if (!r.Ok) throw new Exception(Database.Clean(r));
            int code = SetFirewall(a.Subnet);
            if (code == -2) { DropUser(a.UserHost); throw new Exception(I18n.T("wsl.uacCancelled")); }
            s.WslPassword = pass;
            s.WslUserHost = a.UserHost;
            s.WslSubnet = a.Subnet;
            s.WslAccess = true;
            s.Save();
            Log.Info(I18n.T("wsl.logOn", a.Subnet));
            return ConnectionText(a, pass);
        }

        public static string ConnectionText(Adapter a, string pass)
        {
            var port = Settings.Current.MysqlPort;
            return I18n.T("wsl.connection", a.Ip, port, User, pass) + "\n\n" +
                   "mysql -h " + a.Ip + " -P " + port + " -u " + User + " -p'" + pass + "'\n" +
                   "mysql://" + User + ":" + pass + "@" + a.Ip + ":" + port + "/\n\n" +
                   I18n.T("wsl.hostHint");
        }

        /// <summary>Remove user, firewall rule and bind address. Returns false when the user cancelled UAC.</summary>
        public static bool Disable()
        {
            var s = Settings.Current;
            if (s.WslUserHost != "" && ServiceManager.MySql.State == ServiceState.Running) DropUser(s.WslUserHost);
            if (WinUtil.RunElevated("netsh.exe", "advfirewall firewall delete rule name=\"" + RuleName + "\"") == -2) return false;
            s.WslAccess = false;
            s.WslUserHost = "";
            s.WslSubnet = "";
            s.WslPassword = "";
            s.Save();
            Log.Info(I18n.T("wsl.logOff"));
            return true;
        }

        static void DropUser(string host)
        {
            Database.Sql("DROP USER IF EXISTS " + Database.SqlString(User) + "@" + Database.SqlString(host) + ";");
        }

        /// <summary>Inbound rule only for the WSL subnet and the database port (UAC). -2 = cancelled.</summary>
        static int SetFirewall(string subnet)
        {
            var del = "advfirewall firewall delete rule name=\"" + RuleName + "\"";
            return WinUtil.RunElevated("cmd.exe", "/c netsh " + del + " & netsh advfirewall firewall add rule name=\"" + RuleName +
                "\" dir=in action=allow protocol=TCP localport=" + Settings.Current.MysqlPort + " remoteip=" + subnet);
        }

        /// <summary>Exact commands the confirmation shows before UAC.</summary>
        public static string FirewallCommand(Adapter a)
        {
            return "netsh advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow protocol=TCP localport=" + Settings.Current.MysqlPort + " remoteip=" + a.Subnet;
        }

        static IPAddress Network(IPAddress ip, IPAddress mask)
        {
            var b = ip.GetAddressBytes(); var m = mask.GetAddressBytes();
            for (int i = 0; i < 4; i++) b[i] &= m[i];
            return new IPAddress(b);
        }

        static int PrefixLength(IPAddress mask)
        {
            int n = 0;
            foreach (var x in mask.GetAddressBytes()) for (int i = 7; i >= 0; i--) if ((x >> i & 1) == 1) n++;
            return n;
        }
    }
}
#endif
