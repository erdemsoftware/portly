using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Portly.Core
{
    /// <summary>
    /// macOS / Linux counterparts of what WinUtil does with Explorer, Notepad, the Run registry key and the user PATH:
    /// open a folder or file with the system opener, start Portly at login (LaunchAgent / autostart .desktop file) and
    /// put the shim folder on PATH through a clearly marked block in the user's shell profile.
    /// The builders are pure functions so tests can check them without touching the real home folder.
    /// </summary>
    public static class UnixIntegration
    {
        /// <summary>Home folder (HOME wins, so tests can point it elsewhere).</summary>
        public static string Home
        {
            get
            {
                var h = Environment.GetEnvironmentVariable("HOME");
                return !string.IsNullOrEmpty(h) ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        // ---------------- open / edit ----------------
        /// <summary>The program and arguments that open a folder, file or address: "open" on macOS, "xdg-open" on Linux.</summary>
        public static Tuple<string, string[]> OpenCommand(string target)
        {
            return Tuple.Create(OS.IsMac ? "open" : "xdg-open", new[] { target });
        }

        /// <summary>Open a file in a text editor: macOS "open -t" (the default text editor), Linux xdg-open.</summary>
        public static Tuple<string, string[]> EditCommand(string file)
        {
            return OS.IsMac ? Tuple.Create("open", new[] { "-t", file }) : Tuple.Create("xdg-open", new[] { file });
        }

        /// <summary>Start a program and do not wait for it. False when it could not be started (tool missing).</summary>
        public static bool Start(string tool, params string[] args)
        {
#if NETFRAMEWORK
            return false;
#else
            try
            {
                var psi = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                var p = Process.Start(psi);
                if (p == null) return false;
                // never wait, but drain the pipes so the child cannot block on a full buffer
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex) { Log.Warn(tool + ": " + ex.Message); return false; }
#endif
        }

        /// <summary>Run a program with an argument array (no shell, no quoting problems) and wait. Output = stdout + stderr.</summary>
        public static RunResult Run(string tool, IEnumerable<string> args, int timeoutMs = 60000)
        {
            var res = new RunResult();
#if NETFRAMEWORK
            res.Output = "not supported";
            return res;
#else
            var sb = new StringBuilder();
            try
            {
                var psi = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { res.TimedOut = true; ProcessUtil.KillTree(p.Id); }
                    else { p.WaitForExit(); res.ExitCode = p.ExitCode; }
                }
            }
            catch (Exception ex) { sb.AppendLine(ex.Message); }
            lock (sb) res.Output = sb.ToString().Trim();
            return res;
#endif
        }

        // ---------------- start at login ----------------
        public static string LaunchAgentFile { get { return Path.Combine(Home, "Library", "LaunchAgents", "net.portly.app.plist"); } }
        public static string AutostartFile { get { return Path.Combine(Home, ".config", "autostart", "portly.desktop"); } }

        static string XmlEscape(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        /// <summary>The macOS LaunchAgent: runs "Portly --autostart" at login.</summary>
        public static string LaunchAgentPlist(string exe)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
                + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
                + "<plist version=\"1.0\">\n"
                + "<dict>\n"
                + "    <key>Label</key>\n    <string>net.portly.app</string>\n"
                + "    <key>ProgramArguments</key>\n    <array>\n"
                + "        <string>" + XmlEscape(exe) + "</string>\n"
                + "        <string>--autostart</string>\n"
                + "    </array>\n"
                + "    <key>RunAtLoad</key>\n    <true/>\n"
                + "</dict>\n"
                + "</plist>\n";
        }

        /// <summary>One argument of an Exec= line (desktop entry specification): quoted when needed, specials escaped.</summary>
        public static string DesktopExecArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`' }) < 0)
                return a.Replace("%", "%%");
            var q = new StringBuilder("\"");
            foreach (var c in a)
            {
                if (c == '"' || c == '`' || c == '$' || c == '\\') q.Append('\\');
                q.Append(c);
            }
            // a backslash in the value of a key file is written twice
            return q.Append('"').ToString().Replace("\\", "\\\\").Replace("%", "%%");
        }

        /// <summary>The Linux autostart entry (~/.config/autostart/portly.desktop).</summary>
        public static string AutostartDesktop(string exe)
        {
            return "[Desktop Entry]\n"
                + "Type=Application\n"
                + "Name=Portly\n"
                + "Comment=Local web development environment\n"
                + "Exec=" + DesktopExecArg(exe) + " --autostart\n"
                + "Terminal=false\n"
                + "X-GNOME-Autostart-enabled=true\n";
        }

        /// <summary>Start Portly at login on macOS (LaunchAgent) or Linux (autostart file); off removes the file. Returns false on a file error.</summary>
        public static bool SetAutostart(bool on)
        {
            try
            {
                var file = OS.IsMac ? LaunchAgentFile : AutostartFile;
                if (on)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, OS.IsMac ? LaunchAgentPlist(Paths.Exe) : AutostartDesktop(Paths.Exe), new UTF8Encoding(false));
                }
                else if (File.Exists(file)) File.Delete(file);
                return true;
            }
            catch (Exception ex) { Log.Error("Autostart", ex); return false; }
        }

        public static bool AutostartEnabled { get { return File.Exists(OS.IsMac ? LaunchAgentFile : AutostartFile); } }

        // ---------------- PATH through the shell profile ----------------
        public const string ProfileBegin = "# >>> Portly >>> (managed by Portly: turn off \"Add to PATH\" in Portly's settings, or delete this block)";
        public const string ProfileEnd = "# <<< Portly <<<";

        /// <summary>The block for a profile file; empty text for an empty list.</summary>
        public static string ProfileBlock(IEnumerable<string> dirs)
        {
            var list = dirs.Where(d => !string.IsNullOrEmpty(d)).Select(DoubleQuoteEscape).ToList();
            if (list.Count == 0) return "";
            return ProfileBegin + "\nexport PATH=\"" + string.Join(":", list) + ":$PATH\"\n" + ProfileEnd + "\n";
        }

        static string DoubleQuoteEscape(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (c == '"' || c == '$' || c == '`' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>A profile text with Portly's block replaced by the one for these folders (none = removed). Other lines stay.</summary>
        public static string ApplyProfile(string text, IEnumerable<string> dirs)
        {
            text = (text ?? "").Replace("\r\n", "\n");
            int b = text.IndexOf("# >>> Portly >>>", StringComparison.Ordinal);
            if (b >= 0)
            {
                int e = text.IndexOf(ProfileEnd, b, StringComparison.Ordinal);
                if (e >= 0)
                {
                    e += ProfileEnd.Length;
                    while (e < text.Length && text[e] == '\n') e++;
                    text = text.Substring(0, b).TrimEnd('\n') + (b > 0 ? "\n" : "") + text.Substring(e);
                    if (text.Trim() == "") text = "";
                }
            }
            var block = ProfileBlock(dirs);
            if (block == "") return text;
            var rest = text.TrimEnd('\n');
            return (rest == "" ? "" : rest + "\n\n") + block;
        }

        static readonly string[] ProfileNames = { ".zshrc", ".bashrc", ".bash_profile", ".profile" };

        /// <summary>Profile files to update: those that exist, and the default one of the login shell when none does.</summary>
        public static List<string> ProfileFiles(string home = null, string shell = null)
        {
            home = home ?? Home;
            shell = shell ?? Environment.GetEnvironmentVariable("SHELL") ?? "";
            var files = ProfileNames.Select(n => Path.Combine(home, n)).Where(File.Exists).ToList();
            var def = shell.EndsWith("zsh", StringComparison.Ordinal) ? ".zshrc"
                : shell.EndsWith("bash", StringComparison.Ordinal) ? (OS.IsMac ? ".bash_profile" : ".bashrc")
                : OS.IsMac ? ".zshrc" : ".profile";
            var defFile = Path.Combine(home, def);
            if (!files.Contains(defFile)) files.Add(defFile);
            return files;
        }

        /// <summary>Put the folders at the front of PATH for new terminals (an empty list removes Portly's block everywhere).</summary>
        public static void SetShellPath(IEnumerable<string> dirs)
        {
            var list = dirs.ToList();
            try
            {
                foreach (var f in ProfileFiles())
                {
                    var exists = File.Exists(f);
                    if (!exists && list.Count == 0) continue;
                    var cur = exists ? File.ReadAllText(f) : "";
                    var next = ApplyProfile(cur, list);
                    if (next == cur.Replace("\r\n", "\n")) continue;
                    if (!exists && next == "") continue;
                    File.WriteAllText(f, next, new UTF8Encoding(false));
                }
                Log.Info("Shell profile PATH updated");
            }
            catch (Exception ex) { Log.Error("PATH", ex); }
        }
    }
}
