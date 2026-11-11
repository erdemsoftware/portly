using System;

namespace Portly.Desktop
{
    /// <summary>Which headless helper (no window) a command line asks for. Pure, so the routing can be tested.</summary>
    static class StartupRoute
    {
        /// <summary>Helper name for the arguments, or null for a normal start. The service and Firefox helpers exist on Windows only.</summary>
        public static string Helper(string[] args, bool windows)
        {
            if (args == null || args.Length == 0) return null;
            var a = args[0];
            if (args.Length >= 2 && (a == "--hosts-apply" || a == "--hosts-write" || a == "--trust-ca" || a == "--untrust-ca")) return a.Substring(2);
            if (!windows) return null;
            if (a == "--service") return "service";
            if (args.Length >= 2 && a == "--install-service") return "install-service";
            if (a == "--uninstall-service") return "uninstall-service";
            if (args.Length >= 2 && a == "--firefox-policy") return "firefox-policy";
            return null;
        }
    }
}
