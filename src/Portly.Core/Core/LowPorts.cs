using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Portly.Core
{
    /// <summary>
    /// Linux only: ports below 1024 need privileges. One administrator prompt (once) lowers the limit for ordinary
    /// users to 80 (net.ipv4.ip_unprivileged_port_start), after which Portly binds 80 and 443 as a normal user.
    /// macOS lets any user listen on every port; Windows has no such rule. Both are a no-op here.
    /// UI: call NeedsSetup(port) before the web server starts on its HTTP / HTTPS port; when true, explain it in one
    /// sentence and call Setup() (it shows the system password dialog).
    /// </summary>
    public static class LowPorts
    {
        public const string ConfFile = "/etc/sysctl.d/50-portly-ports.conf";
        const string Proc = "/proc/sys/net/ipv4/ip_unprivileged_port_start";
        public const string ConfLine = "net.ipv4.ip_unprivileged_port_start=80";

        /// <summary>Linux limit for unprivileged ports (1024 by default), -1 when unknown / not Linux.</summary>
        public static int UnprivilegedStart()
        {
            if (!OS.IsLinux) return -1;
            try
            {
                int n;
                return int.TryParse(File.ReadAllText(Proc).Trim(), out n) ? n : -1;
            }
            catch { return -1; }
        }

        /// <summary>Must Setup() run before a normal user can listen on this port?</summary>
        public static bool NeedsSetup(int port)
        {
            if (!OS.IsLinux || port <= 0 || port >= 1024) return false;
            int start = UnprivilegedStart();
            return start < 0 ? true : port < start;
        }

        /// <summary>Same decision from a known limit (testable).</summary>
        public static bool NeedsSetup(int port, int unprivilegedStart)
        {
            return port > 0 && port < 1024 && port < unprivilegedStart;
        }

        /// <summary>The fixed script run as root (no outside text in it): write the sysctl file, then apply the value.</summary>
        public static string RootScript
        {
            get
            {
                return "printf '%s\\n' '# Portly: let ordinary users listen on ports 80 and 443' '" + ConfLine + "' > " + ConfFile
                    + " && (sysctl -w " + ConfLine + " || sysctl --system)";
            }
        }

        /// <summary>Ask for administrator rights once and lower the limit. Returns true when ports 80 and 443 are usable afterwards.</summary>
        public static bool Setup()
        {
            if (!OS.IsLinux) return true;
            if (!NeedsSetup(80) && !NeedsSetup(443)) return true;
            // /bin/sh -c with a constant script: no new helper argument is needed in the desktop app
            var code = Elevate.Run("/bin/sh", "-c", RootScript);
            if (code != 0) Log.Warn("Low ports setup failed (" + code + ")");
            return !NeedsSetup(80) && !NeedsSetup(443);
        }
    }
}
