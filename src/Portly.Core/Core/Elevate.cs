using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Portly.Core
{
    /// <summary>
    /// Runs Portly itself (or another program) with administrator rights.
    ///   Windows: "runas" (UAC), exactly what WinUtil.RunElevated does.
    ///   Linux:   pkexec (polkit); without it the call fails with -1 and a log line.
    ///   macOS:   osascript "do shell script ... with administrator privileges".
    /// Return value: the exit code of the program, -2 when the user refused / closed the prompt, -1 on any other
    /// failure (also "no elevation tool installed"). A process that is already root runs the command directly.
    /// Arguments are never joined into one shell string unescaped: every argument is quoted for the target.
    /// </summary>
    public static class Elevate
    {
        public const int Cancelled = -2;

        /// <summary>Run this program (Paths.Exe) elevated with the given arguments, e.g. RunSelf("--hosts-apply", file).</summary>
        public static int RunSelf(params string[] args) { return Run(Paths.Exe, args); }

        /// <summary>Run a program elevated and wait for it.</summary>
        public static int Run(string exe, params string[] args)
        {
            args = args ?? new string[0];
            if (OS.IsWindows) return WinUtil.RunElevated(exe, WindowsArgs(args));
#if NETFRAMEWORK
            return -1;
#else
            try
            {
                foreach (var a in args.Concat(new[] { exe })) if (!IsSafe(a)) { Log.Warn("Elevation refused: control character in an argument"); return -1; }
                if (Environment.IsPrivilegedProcess) return RunDirect(exe, args);
                if (OS.IsMac)
                {
                    var r = RunProcess("osascript", new[] { "-e", MacScript(exe, args) });
                    // "User canceled." = AppleScript error -128
                    if (r.Item1 != 0 && (r.Item2.Contains("-128") || r.Item2.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0)) return Cancelled;
                    return r.Item1;
                }
                var pk = Which("pkexec");
                if (pk == null) { Log.Warn("pkexec is not installed: cannot ask for administrator rights"); return -1; }
                var code = RunProcess(pk, PkexecArgs(Which(exe) ?? exe, args)).Item1;
                // pkexec: 126 = dialog dismissed, 127 = not authorized
                return code == 126 || code == 127 ? Cancelled : code;
            }
            catch (Exception ex) { Log.Error("Elevation", ex); return -1; }
#endif
        }

        /// <summary>Can this system show an elevation prompt at all (pkexec on Linux, osascript on macOS)?</summary>
        public static bool Available
        {
            get
            {
                if (OS.IsWindows) return true;
#if !NETFRAMEWORK
                if (Environment.IsPrivilegedProcess) return true;
#endif
                return OS.IsMac ? Which("osascript") != null : Which("pkexec") != null;
            }
        }

        // ---------------- command lines (pure, tested) ----------------

        /// <summary>Windows command line: an argument with a space, tab or quote is wrapped in quotes (CommandLineToArgv rules).</summary>
        public static string WindowsArgs(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(WindowsQuote));
        }

        public static string WindowsQuote(string a)
        {
            if (a == null) a = "";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (var c in a)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); }
                else sb.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        /// <summary>POSIX shell: 'text' with every ' written as '\''. Nothing inside single quotes is interpreted.</summary>
        public static string ShQuote(string s)
        {
            return "'" + (s ?? "").Replace("'", "'\\''") + "'";
        }

        /// <summary>AppleScript string literal: "..." with \ and " escaped.</summary>
        public static string AppleScriptString(string s)
        {
            return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>The shell command for macOS: every argument single-quoted.</summary>
        public static string ShellCommand(string exe, IEnumerable<string> args)
        {
            return string.Join(" ", new[] { exe }.Concat(args).Select(ShQuote));
        }

        /// <summary>The AppleScript passed to "osascript -e".</summary>
        public static string MacScript(string exe, IEnumerable<string> args)
        {
            return "do shell script " + AppleScriptString(ShellCommand(exe, args)) + " with administrator privileges";
        }

        /// <summary>Arguments of pkexec (no shell involved: one array element per argument).</summary>
        public static List<string> PkexecArgs(string exe, IEnumerable<string> args)
        {
            var l = new List<string> { exe };
            l.AddRange(args);
            return l;
        }

        static bool IsSafe(string a)
        {
            if (a == null) return true;
            foreach (var c in a) if (c == '\0' || c == '\n' || c == '\r') return false;
            return true;
        }

#if !NETFRAMEWORK
        static int RunDirect(string exe, IEnumerable<string> args) { return RunProcess(exe, args).Item1; }

        static Tuple<int, string> RunProcess(string file, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                var sb = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                lock (sb) return Tuple.Create(p.ExitCode, sb.ToString());
            }
        }
#endif

        /// <summary>First match of a program name on PATH (plus the usual sbin folders), or null.</summary>
        public static string Which(string name)
        {
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return File.Exists(name) ? name : null;
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
            if (!OS.IsWindows) dirs.AddRange(new[] { "/usr/local/sbin", "/usr/sbin", "/sbin", "/usr/local/bin", "/usr/bin", "/bin" });
            foreach (var d in dirs)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                try
                {
                    var f = Path.Combine(d.Trim().Trim('"'), OS.IsWindows ? name + ".exe" : name);
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
            return null;
        }
    }
}
