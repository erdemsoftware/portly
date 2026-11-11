using System;
using System.IO;
using System.Linq;
using System.Threading;
using Portly.Core;
using Portly.Packages;

namespace Portly.CoreTests
{
    static partial class Program
    {
        /// <summary>
        /// PORTLY_E2E=1: download and install the recommended PHP, web server, MySQL and Mailpit for this system from
        /// the real catalog (CI on macOS / Linux). Runs before Suite_Services, which then starts them.
        /// </summary>
        static void Suite_E2EInstall()
        {
            if (Environment.GetEnvironmentVariable("PORTLY_E2E") != "1") return;
            Net.Init();
            Catalog.LoadLocal();
            var types = OS.IsWindows ? new[] { "php", "apache", "mysql", "mailpit" } : new[] { "php", "caddy", "mysql", "mailpit" };
            foreach (var type in types)
            {
                var e = Catalog.Recommended(type) ?? Catalog.ForType(type).FirstOrDefault();
                if (e == null) { Check("catalog offers " + type, false, OS.Name + " " + OS.Arch); continue; }
                if (PackageTypes.Installed(type).Contains(e.Folder)) { Check("installed " + type, true, "already there"); continue; }
                var started = DateTime.Now;
                try
                {
                    Installer.InstallAsync(e, p => { }, CancellationToken.None).GetAwaiter().GetResult();
                    var dir = PackageTypes.ActiveDir(type);
                    Check("installed " + type + " " + e.Version, dir != null, (dir ?? "not detected") + " in " + (int)(DateTime.Now - started).TotalSeconds + " s");
                }
                catch (Exception ex) { Check("installed " + type + " " + e.Version, false, ex.GetType().Name + ": " + ex.Message); }
            }
            if (!OS.IsWindows)
            {
                var php = PackageTypes.ActiveExe("php", "php");
                var r = php == null ? null : ProcessUtil.Run(php, "-r \"echo PHP_VERSION, ' ', extension_loaded('pdo_mysql') ? 'pdo_mysql' : 'no-pdo';\"");
                Check("php cli runs", r != null && r.Output.Contains("pdo_mysql"), r == null ? "no php" : r.Output.Trim());
            }
        }
    }
}
