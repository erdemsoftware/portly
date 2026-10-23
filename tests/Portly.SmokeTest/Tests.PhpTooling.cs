using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Tools;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>Smoke tests of the "PhpTooling" features. Runs while Apache, PHP and MySQL are up (see Run()).</summary>
        static async Task Tests_PhpTooling(Settings s, string www)
        {
            var installed = PackageTypes.Installed("php");
            var def = PackageTypes.ActiveFolder("php");

            // ---------- composer.json constraint -> newest installed version that fits ----------
            var fake = new[] { "php-8.5.12-nts-Win32-vs17-x64", "php-8.4.27-nts-Win32-vs17-x64", "php-8.2.35-nts-Win32-vs16-x64" }.ToList();
            Check("constraint ^8.1 picks newest", VersionRules.Best(fake, "^8.1") == fake[0]);
            Check("constraint ~8.4.0 picks 8.4", VersionRules.Best(fake, "~8.4.0") == fake[1]);
            Check("constraint >=8.1 <8.4 picks 8.2", VersionRules.Best(fake, ">=8.1 <8.4") == fake[2]);
            Check("constraint ^7.4 || ^8.2 picks newest", VersionRules.Best(fake, "^7.4 || ^8.2") == fake[0]);
            Check("constraint 8.2.* picks 8.2", VersionRules.Best(fake, "8.2.*") == fake[2]);
            Check("constraint ^7.4 finds nothing", VersionRules.Best(fake, "^7.4") == null);
            Check("node engines 22.x", VersionRules.Satisfies(new Version(22, 11, 0), "22.x") && !VersionRules.Satisfies(new Version(20, 1, 0), ">=22"));

            // ---------- shims: .php-version picks the version in any folder below it ----------
            var other = installed.FirstOrDefault(f => !string.Equals(f, def, StringComparison.OrdinalIgnoreCase));
            if (!Features.IsOn("shims")) Check("shims feature on by default", false);
            else if (other == null) Console.WriteLine("    (skip shim version test: only one PHP installed)");
            else
            {
                var proj = Path.Combine(Paths.Tmp, "shimtest", "proj a");
                var sub = Path.Combine(proj, "src dir");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(proj, ".php-version"), PackageTypes.Branch(other) + "\n");
                Shims.Refresh();
                var shim = Path.Combine(Shims.Dir, "php.cmd");
                var r = ProcessUtil.Run("cmd.exe", "/d /c \"\"" + shim + "\" -r \"echo PHP_VERSION, '|', $argv[1];\" \"a b\"\"", sub, null, 30000);
                Check("shim uses .php-version", r.Output.Trim() == PackageTypes.Label(other) + "|a b", r.Output.Trim());
                var r2 = ProcessUtil.Run("cmd.exe", "/d /c \"\"" + shim + "\" -r \"exit(5);\"\"", Path.Combine(Paths.Tmp, "shimtest"), null, 30000);
                Check("shim passes the exit code", r2.ExitCode == 5, r2.ExitCode.ToString());
                var r3 = ProcessUtil.Run("cmd.exe", "/d /c \"\"" + shim + "\" -r \"echo PHP_VERSION;\"\"", Path.Combine(Paths.Tmp, "shimtest"), null, 30000);
                Check("shim falls back to the default PHP", r3.Output.Trim() == PackageTypes.Label(def), r3.Output.Trim());
                try { Directory.Delete(Path.Combine(Paths.Tmp, "shimtest"), true); } catch { }
            }

            // ---------- extension catalog ----------
            PhpExtensions.LoadLocal();
            var phpDir = PackageTypes.ActiveDir("php");
            var build = PhpExtensions.Detect(phpDir);
            Check("extension catalog parses", PhpExtensions.All.Count >= 10, PhpExtensions.All.Count + " extensions");
            Check("PHP build detected", !build.ThreadSafe && build.Compiler != "" && build.Api != "", build.ToString());
            var forBranch = PhpExtensions.All.Where(x => x.BuildFor(build) != null).Select(x => x.Id).ToList();
            Check("extensions for PHP " + build.Branch, forBranch.Contains("redis") && forBranch.Contains("apcu"), string.Join(",", forBranch));

            // ---------- install + remove a small extension (apcu, ~100 KB) ----------
            var apcu = PhpExtensions.Get("apcu");
            if (apcu == null || apcu.BuildFor(build) == null || apcu.BuildFor(build).Sha256 == "")
                Console.WriteLine("    (skip extension install: no verified apcu build for PHP " + build.Branch + ")");
            else if (PhpExtensions.IsInstalled(phpDir, "apcu") && !PhpExtensions.InstalledByPortly(phpDir).Contains("apcu"))
                Console.WriteLine("    (skip extension install: apcu already ships with this PHP)");
            else
            {
                try
                {
                    await PhpExtensions.InstallAsync(phpDir, "apcu", null, CancellationToken.None);
                    Check("apcu installed and listed by php -m", PhpExtensions.CheckLoads(phpDir, "apcu") == null && PhpExtensions.IsEnabled(phpDir, "apcu"));
                    PhpExtensions.Remove(phpDir, "apcu");
                    Check("apcu removed again", !PhpExtensions.IsInstalled(phpDir, "apcu") && !PhpExtensions.IsEnabled(phpDir, "apcu"));
                }
                catch (Exception ex) { Check("apcu install", false, ex.Message); }
            }
        }
    }
}
