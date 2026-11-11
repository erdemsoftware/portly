using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Portly.Config;
using Portly.Core;
using Portly.Tools;

namespace Portly.CoreTests
{
    /// <summary>System integration (hosts, elevation, CA trust commands, autostart, shims, language). Never touches the real system.</summary>
    static partial class Program
    {
        static string SysTemp(string name)
        {
            var d = Path.Combine(Paths.Tmp, "systest-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Suite_System()
        {
            Run("hosts block", SysHosts);
            Run("elevate quoting", SysElevate);
            Run("default tld", SysTld);
            Run("autostart", SysAutostart);
            Run("shell profile", SysProfile);
            Run("shims", SysShims);
            Run("ca trust commands", SysTrustCommands);
            Run("language detect", SysLanguage);
            Run("low ports", SysLowPorts);
            Run("terminal script", SysTerminal);
            Run("os info", SysOsInfo);
        }

        static void SysHosts()
        {
            var dir = SysTemp("hosts");
            var file = Path.Combine(dir, "hosts");
            var nl = OS.IsWindows ? "\r\n" : "\n";
            var original = "127.0.0.1" + nl + "localhost" + nl + nl + "# my own line" + nl + "10.0.0.5\tbuild.lan" + nl;
            File.WriteAllText(file, original);

            var added = HostsFile.ApplyBlock(File.ReadAllText(file), new[] { "shop.test", "api.shop.test", "shop.test", "bad host" });
            File.WriteAllText(file, added);
            var text = File.ReadAllText(file);
            Check("hosts: block added", text.Contains("# >>> Portly") && text.Contains("127.0.0.1\tshop.test") && text.Contains("127.0.0.1\tapi.shop.test"));
            Check("hosts: duplicate and invalid names left out", text.Split(new[] { "127.0.0.1\tshop.test" }, StringSplitOptions.None).Length == 2 && !text.Contains("bad host"));
            Check("hosts: own lines kept", text.Contains("# my own line") && text.Contains("10.0.0.5\tbuild.lan"));
            Check("hosts: line breaks of this OS", OS.IsWindows ? !text.Replace("\r\n", "").Contains("\n") : !text.Contains("\r"));

            var again = HostsFile.ApplyBlock(text, new[] { "shop.test", "api.shop.test" });
            Check("hosts: same names change nothing", again == text);
            var changed = HostsFile.ApplyBlock(text, new[] { "blog.test" });
            Check("hosts: block replaced, not doubled", changed.Contains("blog.test") && !changed.Contains("shop.test") && changed.Split(new[] { "# >>> Portly" }, StringSplitOptions.None).Length == 2);

            var removed = HostsFile.RemoveBlock(text);
            Check("hosts: block removed, rest intact", !removed.Contains("Portly") && removed.Contains("# my own line") && removed.Contains("10.0.0.5\tbuild.lan"));
            Check("hosts: empty list leaves no block", !HostsFile.ApplyBlock(text, new string[0]).Contains("127.0.0.1\tshop.test"));
            Check("hosts: *.localhost needs no entry", !HostsFile.NeedsEntry("shop.localhost") && !HostsFile.NeedsEntry("localhost") && HostsFile.NeedsEntry("shop.test"));
            Check("hosts: file path of this OS", OS.IsWindows ? HostsFile.FilePath.EndsWith("hosts") && HostsFile.FilePath.Contains("drivers") : HostsFile.FilePath == "/etc/hosts", HostsFile.FilePath);
            try { Directory.Delete(dir, true); } catch { }
        }

        static void SysElevate()
        {
            Check("windows args: plain stay plain", Elevate.WindowsArgs(new[] { "--trust-ca", "C:\\x\\ca.crt" }) == "--trust-ca C:\\x\\ca.crt");
            Check("windows args: space quoted", Elevate.WindowsArgs(new[] { "--hosts-apply", "C:\\My Files\\h.txt" }) == "--hosts-apply \"C:\\My Files\\h.txt\"");
            Check("windows args: quote escaped", Elevate.WindowsQuote("a\"b c") == "\"a\\\"b c\"");
            Check("windows args: trailing backslash doubled", Elevate.WindowsQuote("C:\\dir with space\\") == "\"C:\\dir with space\\\\\"");
            Check("windows args: empty argument kept", Elevate.WindowsArgs(new[] { "a", "" }) == "a \"\"");

            Check("sh quote: plain", Elevate.ShQuote("abc") == "'abc'");
            Check("sh quote: single quote", Elevate.ShQuote("it's") == "'it'\\''s'");
            var evil = "x'; rm -rf / #$(id)`id`";
            var cmd = Elevate.ShellCommand("/opt/Portly App/Portly", new[] { "--hosts-apply", evil });
            Check("sh command: every part quoted", cmd == "'/opt/Portly App/Portly' '--hosts-apply' '" + evil.Replace("'", "'\\''") + "'", cmd);

            Check("applescript string: escapes", Elevate.AppleScriptString("a\"b\\c") == "\"a\\\"b\\\\c\"");
            var script = Elevate.MacScript("/Applications/Portly.app/Contents/MacOS/Portly", new[] { "--trust-ca", "/Users/me/My \"CA\".crt" });
            Check("mac script: administrator privileges", script.StartsWith("do shell script \"") && script.EndsWith(" with administrator privileges"), script);
            Check("mac script: quotes survive both layers", script.Contains("'/Users/me/My \\\"CA\\\".crt'"), script);

            var pk = Elevate.PkexecArgs("/usr/bin/Portly", new[] { "--hosts-write", "/tmp/a b" });
            Check("pkexec args: one element per argument", pk.Count == 3 && pk[0] == "/usr/bin/Portly" && pk[2] == "/tmp/a b");
        }

        static void SysTld()
        {
            Check("default ending per OS", Settings.DefaultTld == (OS.IsWindows ? "portly" : "localhost"), Settings.DefaultTld);
            Check("localhost ending needs no hosts entry", OS.IsWindows || !HostsFile.NeedsEntry("shop." + Settings.DefaultTld));
        }

        /// <summary>Run an action with HOME pointing to a temporary folder (Unix only; elsewhere the action runs without the change).</summary>
        static void WithHome(string home, Action act)
        {
            var old = Environment.GetEnvironmentVariable("HOME");
            if (!OS.IsWindows) Environment.SetEnvironmentVariable("HOME", home);
            try { act(); }
            finally { if (!OS.IsWindows) Environment.SetEnvironmentVariable("HOME", old); }
        }

        static void SysAutostart()
        {
            var plist = UnixIntegration.LaunchAgentPlist("/Applications/Portly & Co.app/Contents/MacOS/Portly");
            Check("autostart plist: label, run at load", plist.Contains("<string>net.portly.app</string>") && plist.Contains("<key>RunAtLoad</key>") && plist.Contains("<true/>"));
            Check("autostart plist: arguments", plist.Contains("<string>/Applications/Portly &amp; Co.app/Contents/MacOS/Portly</string>") && plist.Contains("<string>--autostart</string>"));
            var desk = UnixIntegration.AutostartDesktop("/home/me/Portly App/Portly");
            Check("autostart desktop entry", desk.StartsWith("[Desktop Entry]\n") && desk.Contains("Type=Application") && desk.Contains("Exec=\"/home/me/Portly App/Portly\" --autostart\n"), desk);
            Check("desktop exec: plain path unquoted", UnixIntegration.DesktopExecArg("/opt/portly/Portly") == "/opt/portly/Portly");
            Check("desktop exec: quotes and dollar escaped", UnixIntegration.DesktopExecArg("/a b/$x") == "\"/a b/\\\\$x\"", UnixIntegration.DesktopExecArg("/a b/$x"));

            if (OS.IsWindows) return;   // Windows keeps the registry key: not touched by tests
            var home = SysTemp("home");
            WithHome(home, () =>
            {
                Check("autostart: on writes the file", UnixIntegration.SetAutostart(true) && UnixIntegration.AutostartEnabled);
                var file = OS.IsMac ? UnixIntegration.LaunchAgentFile : UnixIntegration.AutostartFile;
                Check("autostart: file inside the temp HOME", file.StartsWith(home), file);
                var text = File.ReadAllText(file);
                Check("autostart: file content", text.Contains(Paths.Exe) && text.Contains("--autostart"));
                UnixIntegration.SetAutostart(false);
                Check("autostart: off removes it", !File.Exists(file));
            });
            try { Directory.Delete(home, true); } catch { }
        }

        static void SysProfile()
        {
            var dirs = new[] { "/home/me/Portly/bin/shims", "/home/me/My \"Tools\"/bin" };
            var block = UnixIntegration.ProfileBlock(dirs);
            Check("profile block: marked and exported", block.StartsWith("# >>> Portly >>>") && block.TrimEnd().EndsWith("# <<< Portly <<<") && block.Contains("export PATH=\"/home/me/Portly/bin/shims:"), block);
            Check("profile block: quotes escaped", block.Contains("/home/me/My \\\"Tools\\\"/bin:$PATH"));
            Check("profile block: no folders, no block", UnixIntegration.ProfileBlock(new string[0]) == "");

            var original = "export FOO=1\nalias ll='ls -l'\n";
            var withBlock = UnixIntegration.ApplyProfile(original, dirs);
            Check("profile: block appended, own lines kept", withBlock.StartsWith(original.TrimEnd('\n')) && withBlock.Contains("# >>> Portly >>>"));
            Check("profile: applying twice is the same", UnixIntegration.ApplyProfile(withBlock, dirs) == withBlock);
            var changed = UnixIntegration.ApplyProfile(withBlock, new[] { "/other/shims" });
            Check("profile: block replaced", changed.Contains("/other/shims") && !changed.Contains("/home/me/Portly") && changed.Split(new[] { "# >>> Portly >>>" }, StringSplitOptions.None).Length == 2);
            Check("profile: removed with an empty list", UnixIntegration.ApplyProfile(withBlock, new string[0]).TrimEnd('\n') == original.TrimEnd('\n'));
            Check("profile: removing from a file without a block changes nothing", UnixIntegration.ApplyProfile(original, new string[0]) == original);

            var home = SysTemp("profile");
            File.WriteAllText(Path.Combine(home, ".bashrc"), "# mine\n");
            var files = UnixIntegration.ProfileFiles(home, "/bin/zsh");
            Check("profile files: existing ones and the shell's default", files.Contains(Path.Combine(home, ".bashrc")) && files.Contains(Path.Combine(home, ".zshrc")));
            try { Directory.Delete(home, true); } catch { }
        }

        static void SysShims()
        {
            var scripts = Shims.UnixScripts("composer-2.8.1", "wpcli-2.11");
            Check("shim scripts: set", new[] { "php", "composer", "wp", "node", "npm", "npx", "lib/resolve.sh" }.All(scripts.ContainsKey));
            Check("shim scripts: sh, LF only", scripts.Where(k => k.Key != "README.txt").All(k => !k.Value.Contains("\r")) && scripts["php"].StartsWith("#!/bin/sh\n"));
            Check("shim php: resolves and executes", scripts["php"].Contains("portly_resolve php php php .php-version") && scripts["php"].Contains("exec \"$PORTLY_DIR/php\" \"$@\""));
            Check("shim composer: phar path quoted", scripts["composer"].Contains("'composer-2.8.1'") && scripts["composer"].Contains("composer.phar"));
            Check("shim composer: no shell injection from a folder name", Shims.UnixScripts("a'; id; '", "w")["composer"].Contains("'a'\\''; id; '\\'''"));
            Check("shim node: npm prefix and nvmrc", scripts["node"].Contains(".nvmrc .node-version") && scripts["node"].Contains("NPM_CONFIG_PREFIX"));
            Check("shim resolver: reads maps with awk", scripts["lib/resolve.sh"].Contains("awk -F'|'") && scripts["lib/resolve.sh"].Contains("-sites.sh.txt"));

            var dir = SysTemp("shims");
            Shims.WriteScriptsUnixTo(dir, "composer-2.8.1", "wpcli-2.11");
            Check("shim files written", File.Exists(Path.Combine(dir, "php")) && File.Exists(Path.Combine(dir, "lib", "resolve.sh")));
            Check("shim file has no CR", !File.ReadAllText(Path.Combine(dir, "php")).Contains("\r"));
#if !NETFRAMEWORK
            if (!OS.IsWindows)
            {
                var mode = File.GetUnixFileMode(Path.Combine(dir, "php"));
                Check("shim file mode 0755", mode == (UnixFileMode)0x1ED, Convert.ToString((int)mode, 8));
                Check("shim composer is executable too", (File.GetUnixFileMode(Path.Combine(dir, "composer")) & UnixFileMode.UserExecute) != 0);
                Check("shim resolver not executable", (File.GetUnixFileMode(Path.Combine(dir, "lib", "resolve.sh")) & UnixFileMode.UserExecute) == 0);
                SysShimRun(dir);
            }
#endif
            try { Directory.Delete(dir, true); } catch { }
        }

        /// <summary>Unix only: a fake PHP package and the map files, then the shim must run the right one.</summary>
        static void SysShimRun(string shims)
        {
            // layout: <root>/bin/shims (the scripts), <root>/bin/php/php-8.4.1/bin/php
            var root = SysTemp("shimrun");
            var bin = Path.Combine(root, "bin");
            var sh = Path.Combine(bin, "shims");
            Directory.CreateDirectory(Path.Combine(sh, "lib"));
            Directory.CreateDirectory(Path.Combine(sh, "map"));
            foreach (var f in new[] { "php", "lib/resolve.sh" }) File.Copy(Path.Combine(shims, f), Path.Combine(sh, f), true);
#if !NETFRAMEWORK
            File.SetUnixFileMode(Path.Combine(sh, "php"), (UnixFileMode)0x1ED);
            foreach (var v in new[] { "php-8.3.9", "php-8.4.1" })
            {
                var pd = Path.Combine(bin, "php", v, "bin");
                Directory.CreateDirectory(pd);
                File.WriteAllText(Path.Combine(pd, "php"), "#!/bin/sh\necho \"" + v + " $*\"\n");
                File.SetUnixFileMode(Path.Combine(pd, "php"), (UnixFileMode)0x1ED);
            }
#endif
            File.WriteAllText(Path.Combine(sh, "map", "php-default.txt"), "php-8.4.1");
            File.WriteAllText(Path.Combine(sh, "map", "php-versions.txt"), "php-8.3.9|php-8.3.9\n8.3|php-8.3.9\nphp-8.4.1|php-8.4.1\n8.4|php-8.4.1\n");
            var project = Path.Combine(root, "www", "old");
            Directory.CreateDirectory(project);
            var r1 = ProcessUtil.Run(Path.Combine(sh, "php"), "-v", project, null, 20000);
            Check("shim run: default PHP", r1.Ok && r1.Output == "php-8.4.1 -v", r1.Output);
            File.WriteAllText(Path.Combine(project, ".php-version"), "8.3\n");
            var r2 = ProcessUtil.Run(Path.Combine(sh, "php"), "-v", project, null, 20000);
            Check("shim run: .php-version wins", r2.Ok && r2.Output == "php-8.3.9 -v", r2.Output);
            File.Delete(Path.Combine(project, ".php-version"));
            File.WriteAllText(Path.Combine(sh, "map", "php-sites.sh.txt"), project + "/|php-8.3.9\n");
            var r3 = ProcessUtil.Run(Path.Combine(sh, "php"), "-v", project, null, 20000);
            Check("shim run: site map", r3.Ok && r3.Output == "php-8.3.9 -v", r3.Output);
            File.WriteAllText(Path.Combine(project, ".php-version"), "5.6\n");
            var r4 = ProcessUtil.Run(Path.Combine(sh, "php"), "-v", project, null, 20000);
            Check("shim run: unknown version is an error", !r4.Ok && r4.Output.Contains("not installed"), r4.Output);
            try { Directory.Delete(root, true); } catch { }
        }

        static void SysTrustCommands()
        {
            var thumb = "AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12";
            var mac = Certs.MacTrustArgs("/tmp/portly ca.crt");
            Check("mac trust: user keychain, trustRoot", mac[0] == "add-trusted-cert" && mac.Contains("trustRoot") && mac.Contains("-k") && mac.Contains(Certs.MacKeychain) && mac.Last() == "/tmp/portly ca.crt"
                && Certs.MacKeychain.EndsWith("login.keychain-db") && Certs.MacKeychain.Contains("Library"), string.Join(" ", mac));
            var unm = Certs.MacUntrustArgs(thumb.ToLowerInvariant());
            Check("mac untrust: by hash, with trust settings", unm[0] == "delete-certificate" && unm[1] == "-Z" && unm[2] == thumb && unm.Contains("-t"));
            Check("mac verify", Certs.MacVerifyArgs("/x.crt")[0] == "verify-cert" && Certs.MacVerifyArgs("/x.crt").Contains("/x.crt"));

            Check("nss nickname holds the thumbprint", Certs.NssNickname(thumb) == "Portly Local CA AB12CD34EF56", Certs.NssNickname(thumb));
            var add = Certs.NssAddArgs("Portly Local CA X", "/tmp/a.crt", "/home/me/.pki/nssdb");
            Check("nss add: C,, trust and sql: database", add.SequenceEqual(new[] { "-A", "-n", "Portly Local CA X", "-t", "C,,", "-i", "/tmp/a.crt", "-d", "sql:/home/me/.pki/nssdb" }), string.Join(" ", add));
            Check("nss delete / find", Certs.NssDeleteArgs("n", "/d")[0] == "-D" && Certs.NssFindArgs("n", "/d")[0] == "-L");

            var debian = Certs.LinuxPlan(d => d == "/usr/local/share/ca-certificates", n => n == "update-ca-certificates" ? "/usr/sbin/update-ca-certificates" : null);
            Check("linux store: Debian / Ubuntu", debian != null && debian.AnchorDir == "/usr/local/share/ca-certificates" && debian.UpdateTool.EndsWith("update-ca-certificates"));
            Check("linux store: file name from the thumbprint", debian.AnchorFile(thumb) == Path.Combine("/usr/local/share/ca-certificates", "portly-" + thumb.ToLowerInvariant() + ".crt"));
            var fedora = Certs.LinuxPlan(d => d == "/etc/pki/ca-trust/source/anchors", n => n == "update-ca-trust" ? "/usr/bin/update-ca-trust" : null);
            Check("linux store: Fedora / RHEL", fedora != null && fedora.AnchorDir == "/etc/pki/ca-trust/source/anchors" && fedora.UpdateTool.EndsWith("update-ca-trust"));
            var arch = Certs.LinuxPlan(d => d == "/etc/ca-certificates/trust-source/anchors", n => n == "trust" ? "/usr/bin/trust" : null);
            Check("linux store: Arch", arch != null && arch.AnchorDir == "/etc/ca-certificates/trust-source/anchors" && arch.UpdateArgs.SequenceEqual(new[] { "extract-compat" }));
            Check("linux store: none known", Certs.LinuxPlan(d => false, n => null) == null);

            var chromium = Certs.ChromiumNssDirs("/home/me");
            Check("chromium nss folders", chromium[0] == Path.Combine("/home/me", ".pki", "nssdb") && chromium.Count >= 3);
            Check("elevated trust refuses a foreign certificate file", OS.IsWindows || Certs.TrustCaElevated(Path.Combine(Paths.Tmp, "does-not-exist.crt")) != 0);
            Check("elevated untrust refuses a bad thumbprint", Certs.UntrustCaElevated("not-a-thumbprint") == 3);
        }

        static void SysLanguage()
        {
            if (!Directory.Exists(Paths.Lang)) { Check("language files present", true, "skipped: no lang folder next to the tests"); return; }
            Check("LANG tr_TR.UTF-8 -> tr", I18n.DetectUnix(null, null, "tr_TR.UTF-8", null) == "tr");
            Check("LANG de_DE@euro -> de", I18n.DetectUnix(null, null, "de_DE@euro", null) == "de");
            Check("LC_ALL beats LANG", I18n.DetectUnix("fr_FR.UTF-8", null, "tr_TR.UTF-8", null) == "fr");
            Check("LC_MESSAGES beats LANG", I18n.DetectUnix(null, "es_ES.UTF-8", "tr_TR.UTF-8", null) == "es");
            Check("pt_BR -> pt-BR file", I18n.DetectUnix(null, null, "pt_BR.UTF-8", null) == "pt-BR");
            Check("pt_PT falls back to pt-BR", I18n.DetectUnix(null, null, "pt_PT.UTF-8", null) == "pt-BR");
            Check("zh_TW falls back to zh-CN", I18n.DetectUnix(null, null, "zh_TW.UTF-8", null) == "zh-CN");
            Check("en_US -> en", I18n.DetectUnix(null, null, "en_US.UTF-8", null) == "en");
            Check("LANGUAGE list: first available", I18n.DetectUnix(null, null, "en_US.UTF-8", "xx:it:de") == "it");
            Check("C locale names no language", I18n.DetectUnix(null, null, "C", null) == null && I18n.DetectUnix(null, null, "POSIX", null) == null && I18n.DetectUnix(null, null, "C.UTF-8", null) == null);
            Check("unset names no language", I18n.DetectUnix(null, null, null, null) == null);
            Check("unknown language names none", I18n.DetectUnix(null, null, "xx_XX.UTF-8", null) == null);
            Check("Detect() always finds a language file", File.Exists(Path.Combine(Paths.Lang, I18n.Detect() + ".json")));
        }

        static void SysLowPorts()
        {
            Check("low ports: 80 needs setup under the default limit", LowPorts.NeedsSetup(80, 1024) && LowPorts.NeedsSetup(443, 1024));
            Check("low ports: limit 80 is enough", !LowPorts.NeedsSetup(80, 80) && !LowPorts.NeedsSetup(443, 80));
            Check("low ports: high ports never need it", !LowPorts.NeedsSetup(8080, 1024) && !LowPorts.NeedsSetup(1024, 2000));
            Check("low ports: only Linux asks", OS.IsLinux || (!LowPorts.NeedsSetup(80) && !LowPorts.NeedsSetup(443)));
            Check("low ports: script writes the sysctl file", LowPorts.RootScript.Contains("/etc/sysctl.d/50-portly-ports.conf") && LowPorts.RootScript.Contains("net.ipv4.ip_unprivileged_port_start=80") && LowPorts.RootScript.Contains("sysctl"));
            Check("low ports: Setup is a no-op outside Linux", OS.IsLinux || LowPorts.Setup());
        }

        static void SysTerminal()
        {
            var env = new Dictionary<string, string> { { "PATH", "/a b:/c'd:/usr/bin" }, { "BAD NAME", "x" }, { "NODE_EXTRA_CA_CERTS", "/x/ca.crt" } };
            var s = Terminal.UnixScript("/home/me/My Project", env, "composer install");
            Check("terminal script: folder, env, command, shell", s.Contains("cd '/home/me/My Project' || exit 1") && s.Contains("export PATH='/a b:/c'\\''d:/usr/bin'")
                && s.Contains("composer install\n") && s.TrimEnd().EndsWith("exec \"${SHELL:-/bin/sh}\""), s);
            Check("terminal script: invalid variable names skipped", !s.Contains("BAD NAME"));
            Check("terminal script: LF only", !s.Contains("\r"));
            Check("terminal args: gnome-terminal", Terminal.LinuxTerminalArgs("/usr/bin/gnome-terminal", "/t/s.sh").SequenceEqual(new[] { "--", "sh", "/t/s.sh" }));
            Check("terminal args: xfce4 / konsole / kitty", Terminal.LinuxTerminalArgs("xfce4-terminal", "s")[0] == "-x" && Terminal.LinuxTerminalArgs("konsole", "s")[0] == "-e" && Terminal.LinuxTerminalArgs("kitty", "s")[0] == "sh");
        }

        static void SysOsInfo()
        {
            Check("os-release: pretty name", OsInfo.ParseOsRelease("NAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n") == "Ubuntu 24.04.1 LTS");
            Check("os-release: name and version", OsInfo.ParseOsRelease("NAME=Fedora\nVERSION_ID=41\n") == "Fedora 41");
            Check("os-release: empty", OsInfo.ParseOsRelease("") == "");
            Check("os display name", OsInfo.DisplayName != "", OsInfo.DisplayName);
            Check("os: macOS / Linux count as a modern system", OS.IsWindows || (OsInfo.IsWin10OrLater && OsInfo.Number >= 10.0));
            Check("os: no Windows runtime outside Windows", OS.IsWindows || WinUtil.VcRuntimeMinor() == int.MaxValue);
        }
    }
}
