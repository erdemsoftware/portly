using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Portly.Core;

namespace Portly.CoreTests
{
    /// <summary>Engine checks that must pass on Windows, macOS and Linux. Exit code = number of failures.</summary>
    static partial class Program
    {
        static int _fail, _ok;

        static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _ok++; else _fail++;
            Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (detail != "" ? "  -> " + detail : ""));
        }

        static void Run(string name, Action test)
        {
            try { test(); }
            catch (Exception ex)
            {
                var e = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Check(name, false, e.GetType().Name + ": " + e.Message);
            }
        }

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var home = Environment.GetEnvironmentVariable("PORTLY_HOME");
            Console.WriteLine("Portly core tests on " + OS.Name + " " + OS.Arch + ", data root " + Paths.Root);
            if (string.IsNullOrEmpty(home)) { Console.WriteLine("Set PORTLY_HOME to an empty test folder."); return 1; }
            Paths.EnsureAll();

            Run("platform", Platform);
            Run("json", JsonChecks);
            Run("secret", SecretChecks);
            Run("ports", PortChecks);
            Run("processes", ProcessChecks);
            Run("settings", SettingsChecks);
            // more suites: any "static void Suite_<Name>()" in another Tests.*.cs file of this partial class
            var only = args.Length > 0 ? args[0] : null;
            foreach (var m in typeof(Program).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .Where(x => x.Name.StartsWith("Suite_", StringComparison.Ordinal) && x.GetParameters().Length == 0).OrderBy(x => x.Name))
            {
                var name = m.Name.Substring(6);
                if (only != null && !name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine("== " + name);
                Run(name, () => m.Invoke(null, null));
            }

            Console.WriteLine();
            Console.WriteLine("PASSED " + _ok + "  FAILED " + _fail);
            return _fail;
        }

        static void Platform()
        {
            Check("OS detected once", (OS.IsWindows ? 1 : 0) + (OS.IsLinux ? 1 : 0) + (OS.IsMac ? 1 : 0) == 1, OS.Name);
            Check("exe suffix", OS.Exe("php") == (OS.IsWindows ? "php.exe" : "php"));
            Check("data root from PORTLY_HOME", Paths.Root == Path.GetFullPath(Environment.GetEnvironmentVariable("PORTLY_HOME")).TrimEnd('\\', '/'), Paths.Root);
            Check("lang next to the program", Directory.Exists(Paths.Lang) || !File.Exists(Path.Combine(Paths.AppDir, "Portly.Core.dll")), Paths.Lang);
        }

        static void JsonChecks()
        {
            var d = Json.ParseObject("{\"a\":1,\"b\":[1,2,{\"c\":\"x\\u00e9\"}],\"d\":true,\"e\":null,\"f\":1.5,\"g\":3000000000}");
            Check("json int", d.Int("a") == 1 && d["a"] is int);
            Check("json array", d["b"] is object[] && ((object[])d["b"]).Length == 3);
            Check("json unicode", ((Dictionary<string, object>)((object[])d["b"])[2]).Str("c") == "xé");
            Check("json long / decimal", d["g"] is long && d["f"] is decimal);
            Check("json html-safe output", Json.Serialize(new Dictionary<string, object> { { "s", "<a href='x'>&</a>" } }) == "{\"s\":\"\\u003ca href=\\u0027x\\u0027\\u003e\\u0026\\u003c/a\\u003e\"}");
            bool threw = false;
            try { Json.Parse(new string('[', 200) + new string(']', 200)); } catch (FormatException) { threw = true; }
            Check("json depth limit", threw);
        }

        static void SecretChecks()
        {
            var plain = Encoding.UTF8.GetBytes("root-password-ş");
            var blob = Secret.Protect(plain, null);
            Check("secret round trip", Encoding.UTF8.GetString(Secret.Unprotect(blob, null)) == "root-password-ş");
            Check("secret is not plain", !Encoding.UTF8.GetString(blob).Contains("root-password"));
            var withEntropy = Secret.Protect(plain, Encoding.ASCII.GetBytes("Portly CA"));
            bool wrong = false;
            try { Secret.Unprotect(withEntropy, Encoding.ASCII.GetBytes("other")); } catch { wrong = true; }
            Check("secret bound to its purpose", wrong);
            if (!OS.IsWindows)
            {
                var mode = File.GetUnixFileMode(Path.Combine(Paths.Usr, "secret.key"));
                Check("secret key only for the user (0600)", mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), mode.ToString());
            }
        }

        static void PortChecks()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            Check("port in use is seen", ProcessUtil.IsPortInUse(port), port.ToString());
            var owner = ProcessUtil.PortOwnerInfo(port);
            Check("port owner is this process", owner != null && owner.Pid == Environment.ProcessId, owner == null ? "null" : owner.Pid + " " + owner.Name);
            l.Stop();
            System.Threading.Thread.Sleep(300);
            Check("free port is free", !ProcessUtil.IsPortInUse(port));
            int free = ProcessUtil.FreePort(new[] { port }, new List<int>());
            Check("FreePort picks a free one", free > 0, free.ToString());
        }

        static void ProcessChecks()
        {
            var r = OS.IsWindows ? ProcessUtil.Run("cmd.exe", "/c echo portly") : ProcessUtil.Run("/bin/sh", "-c \"echo portly\"");
            Check("run a command", r.Output.Trim() == "portly", r.Output);
            if (OS.IsWindows) return;
            var p = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false });
            ProcessUtil.KillTree(p.Id);
            Check("kill tree", p.WaitForExit(5000));
            var q = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false });
            Check("SIGINT stops a process", ProcessUtil.SendCtrlC(q, 5000));
        }

        static void SettingsChecks()
        {
            Settings.Load();
            Settings.Current.Save();
            Check("settings file written", File.Exists(Paths.SettingsFile), Paths.SettingsFile);
            I18n.Load("tr");
            Check("translations load", I18n.T("svc.running") == "Çalışıyor", I18n.T("svc.running"));
            I18n.Load("en");
        }
    }
}
