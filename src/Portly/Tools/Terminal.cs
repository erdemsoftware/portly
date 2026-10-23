using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Portly.Config;
using Portly.Core;
using Portly.Packages;

namespace Portly.Tools
{
    /// <summary>Command prompt / PowerShell with php, mysql, composer, node ... of the active versions on PATH.</summary>
    public static class Terminal
    {
        static readonly string[] Types = { "php", "composer", "wpcli", "mysql", "mariadb", "postgresql", "mongodb", "nodejs", "bun", "deno", "python", "go", "git", "sqlite", "vscode", "apache", "nginx", "redis" };

        public static List<string> PathDirs()
        {
            var dirs = new List<string>();
            foreach (var type in Types)
            {
                var d = PackageTypes.ActiveDir(type);
                if (d == null) continue;
                foreach (var rel in PackageTypes.Get(type).PathDirs) dirs.Add(rel == "" ? d : Path.Combine(d, rel));
            }
            foreach (var extra in new[] { @"bin\git\cmd", @"bin\git\usr\bin", @"usr\bin" })
                dirs.Add(Path.Combine(Paths.Root, extra));
            return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static ProcessStartInfo Prepare(string exe, string args, string workDir)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Directory.Exists(workDir ?? "") ? workDir : Settings.Current.DocumentRoot,
            };
            var path = string.Join(";", PathDirs()) + ";" + Environment.GetEnvironmentVariable("PATH");
            psi.EnvironmentVariables["PATH"] = path;
            psi.EnvironmentVariables["PORTLY_ROOT"] = Paths.Root;
            // let Node, Python, curl and Git trust https://*.portly (Mozilla roots + Portly CA)
            if (File.Exists(Certs.BundleFile))
            {
                psi.EnvironmentVariables["REQUESTS_CA_BUNDLE"] = Certs.BundleFile;
                psi.EnvironmentVariables["CURL_CA_BUNDLE"] = Certs.BundleFile;
                psi.EnvironmentVariables["GIT_SSL_CAINFO"] = Certs.BundleFile;
            }
            if (File.Exists(Certs.CaCertPath)) psi.EnvironmentVariables["NODE_EXTRA_CA_CERTS"] = Certs.CaCertPath;
            Proxy.ApplyEnv(psi.EnvironmentVariables); // composer / npm / git behind a manual proxy
            return psi;
        }

        public static void Open(string workDir = null)
        {
            try
            {
                if (Settings.Current.Terminal == "powershell")
                    Process.Start(Prepare("powershell.exe", "-NoExit -NoLogo -Command \"$host.UI.RawUI.WindowTitle='Portly'\"", workDir));
                else
                    Process.Start(Prepare("cmd.exe", "/k title Portly", workDir));
            }
            catch (Exception ex) { Log.Error("Terminal", ex); }
        }

        /// <summary>Run a command in a visible window (used for composer create-project ...).</summary>
        public static void Run(string workDir, string command)
        {
            try { Process.Start(Prepare("cmd.exe", "/k title Portly && " + command, workDir)); }
            catch (Exception ex) { Log.Error("Terminal", ex); }
        }

        /// <summary>Keep the user PATH in sync with the active versions (only when enabled in Settings).</summary>
        public static void ApplyUserPath()
        {
            if (Settings.Current.AddToPath) WinUtil.SetUserPath(PathDirs());
            else WinUtil.SetUserPath(new string[0]);
        }
    }
}
