using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using Portly.Config;
using Portly.Core;
using Portly.Packages;

namespace Portly.Tools
{
    /// <summary>
    /// Command prompt / PowerShell with php, mysql, composer, node ... of the active versions on PATH.
    /// macOS: Terminal.app; Linux: the first of x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal, kitty,
    /// alacritty, xterm. Both start a small generated script that sets Portly's PATH and then hands over to the user's shell.
    /// </summary>
    public static class Terminal
    {
        static readonly string[] Types = { "php", "composer", "wpcli", "mysql", "mariadb", "postgresql", "mongodb", "nodejs", "bun", "deno", "python", "go", "java", "git", "sqlite", "vscode", "apache", "nginx", "redis" };

        public static List<string> PathDirs()
        {
            var dirs = new List<string>();
            foreach (var type in Types)
            {
                var d = PackageTypes.ActiveDir(type);
                if (d == null) continue;
                foreach (var rel in PackageTypes.Get(type).PathDirs) dirs.Add(rel == "" ? d : Path.Combine(d, rel));
            }
            if (OS.IsWindows)
                foreach (var extra in new[] { @"bin\git\cmd", @"bin\git\usr\bin", @"usr\bin" })
                    dirs.Add(Path.Combine(Paths.Root, extra));
            else dirs.Add(Path.Combine(Paths.Root, "usr", "bin"));
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
            // shims first: php / composer / node follow the project folder even after a version switch
            var sep = Path.PathSeparator.ToString();
            var path = string.Join(sep, Shims.PathDirs().Concat(PathDirs())) + sep + Environment.GetEnvironmentVariable("PATH");
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
                if (!OS.IsWindows) { OpenUnix(workDir, null); return; }
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
            if (!OS.IsWindows) { OpenUnix(workDir, command); return; }
            try { Process.Start(Prepare("cmd.exe", "/k title Portly && " + command, workDir)); }
            catch (Exception ex) { Log.Error("Terminal", ex); }
        }

        // ---------------- macOS / Linux ----------------
        static readonly string[] LinuxTerminals = { "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "kitty", "alacritty", "xterm" };

        /// <summary>The variables Portly adds or changes for a terminal (PATH, certificate files, proxy), without the rest of the environment.</summary>
        static Dictionary<string, string> EnvChanges(ProcessStartInfo psi)
        {
            var changes = new Dictionary<string, string>();
            foreach (System.Collections.DictionaryEntry e in psi.EnvironmentVariables)
            {
                var k = (string)e.Key;
                var v = (string)e.Value;
                if (v != Environment.GetEnvironmentVariable(k)) changes[k] = v;
            }
            return changes;
        }

        /// <summary>
        /// The start script of a terminal window: go to the folder, set the variables, run the command (when given) and
        /// stay in the user's own shell. Every value is single-quoted for sh; the command is the caller's own text.
        /// </summary>
        public static string UnixScript(string workDir, IDictionary<string, string> env, string command)
        {
            var sb = new StringBuilder();
            sb.Append("#!/bin/sh\n");
            sb.Append("# Portly terminal (generated, deleted after use)\n");
            sb.Append("rm -f \"$0\" 2>/dev/null\n");
            sb.Append("cd " + Elevate.ShQuote(workDir) + " || exit 1\n");
            foreach (var kv in env)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(kv.Key, "^[A-Za-z_][A-Za-z0-9_]*$")) continue;
                sb.Append("export " + kv.Key + "=" + Elevate.ShQuote(kv.Value) + "\n");
            }
            sb.Append("printf '\\033]0;Portly\\007'\n");
            if (!string.IsNullOrEmpty(command)) sb.Append(command + "\n");
            sb.Append("exec \"${SHELL:-/bin/sh}\"\n");
            return sb.ToString();
        }

        /// <summary>Arguments that make a Linux terminal emulator run "sh script".</summary>
        public static string[] LinuxTerminalArgs(string terminal, string script)
        {
            switch (Path.GetFileName(terminal))
            {
                case "gnome-terminal": return new[] { "--", "sh", script };
                case "xfce4-terminal": return new[] { "-x", "sh", script };
                case "kitty": return new[] { "sh", script };
                default: return new[] { "-e", "sh", script };   // x-terminal-emulator, konsole, alacritty, xterm
            }
        }

        /// <summary>The terminal to use: the one named in the setting when it exists, else the first of the usual list.</summary>
        static string FindLinuxTerminal()
        {
            var wanted = Settings.Current.Terminal;
            if (!string.IsNullOrEmpty(wanted) && LinuxTerminals.Contains(wanted)) { var w = Elevate.Which(wanted); if (w != null) return w; }
            foreach (var t in LinuxTerminals) { var f = Elevate.Which(t); if (f != null) return f; }
            return null;
        }

        static void OpenUnix(string workDir, string command)
        {
            try
            {
                var psi = Prepare("sh", "", workDir);
                var dir = psi.WorkingDirectory;
                var scripts = Path.Combine(Paths.Tmp, "terminal");
                Directory.CreateDirectory(scripts);
                foreach (var old in Directory.GetFiles(scripts)) { try { if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-1)) File.Delete(old); } catch { } }
                var file = Path.Combine(scripts, "portly-" + Guid.NewGuid().ToString("N") + (OS.IsMac ? ".command" : ".sh"));
                File.WriteAllText(file, UnixScript(dir, EnvChanges(psi), command), new UTF8Encoding(false));
#if !NETFRAMEWORK
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#endif
                if (OS.IsMac)
                {
                    // Terminal.app opens a .command file in a new window and runs it
                    if (!UnixIntegration.Start("open", "-a", "Terminal", file)) Log.Warn("Terminal: could not start Terminal.app");
                    return;
                }
                var term = FindLinuxTerminal();
                if (term == null) { Log.Warn("Terminal: no terminal program found (x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal, kitty, alacritty, xterm)"); return; }
                if (!UnixIntegration.Start(term, LinuxTerminalArgs(term, file))) Log.Warn("Terminal: could not start " + term);
            }
            catch (Exception ex) { Log.Error("Terminal", ex); }
        }

        /// <summary>Keep the user PATH in sync with the active versions (only when enabled in Settings).</summary>
        public static void ApplyUserPath()
        {
            // also the moment to rewrite the shim maps: versions, sites or settings just changed
            Shims.Hook();
            Shims.Refresh();
            // the shim folder goes first; the package folders stay behind it for programs that start php.exe / node.exe directly
            if (Settings.Current.AddToPath) WinUtil.SetUserPath(Shims.PathDirs().Concat(PathDirs()).ToList());
            else WinUtil.SetUserPath(new string[0]);
        }
    }
}
