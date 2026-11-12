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
    /// <summary>The tools of the engine (Tools/, Sites, Yaml ...) on every platform. Nothing here runs ssh, composer or touches real projects.</summary>
    static partial class Program
    {
        static string ToolsTemp()
        {
            var d = Path.Combine(Path.GetTempPath(), "portly-tools-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Touch(string file, string text = "x")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, text);
        }

        static void Suite_Tools()
        {
            Run("tools yaml", ToolsYaml);
            Run("tools project file", ToolsProjectFile);
            Run("tools path rules", ToolsPathRules);
            Run("tools sites", ToolsSites);
            Run("tools project info", ToolsProjectInfo);
            Run("tools command lines", ToolsCommandLines);
            Run("tools shell", ToolsShell);
            Run("tools mysql client", ToolsMysqlClient);
            Run("tools trash", ToolsTrash);
            Run("tools platform guards", ToolsPlatformGuards);
            Run("tools importer", ToolsImporter);
            Run("tools site logs", ToolsSiteLogs);
        }

        // ------------------------------------------------------------------
        static void ToolsYaml()
        {
            var map = Yaml.ParseMap("# comment\nversion: 1\nphp: \"8.3\"\ndomains: [a.test, \"b.test\"]\nhooks:\n  after-start: echo hi   # trailing\njobs:\n  - name: queue\n    command: php artisan queue:work\n    autostart: true\n");
            Check("yaml scalar", Yaml.Str(map["version"]) == "1" && Yaml.Str(map["php"]) == "8.3");
            var domains = map["domains"] as List<object>;
            Check("yaml flow list", domains != null && domains.Count == 2 && Yaml.Str(domains[1]) == "b.test");
            var hooks = map["hooks"] as Dictionary<string, object>;
            Check("yaml nested map and trailing comment", hooks != null && Yaml.Str(hooks["after-start"]) == "echo hi");
            var jobs = map["jobs"] as List<object>;
            var job = jobs == null || jobs.Count != 1 ? null : jobs[0] as Dictionary<string, object>;
            Check("yaml list of maps", job != null && Yaml.Str(job["command"]) == "php artisan queue:work" && (bool)job["autostart"]);
            Check("yaml quote round trip", Yaml.Str(Yaml.ParseMap("k: " + Yaml.Quote("a: b #c \"d\" \\ e"))["k"]) == "a: b #c \"d\" \\ e");
            bool threw = false;
            try { Yaml.ParseMap("a:\n\tb: 1\n"); } catch (YamlException) { threw = true; }
            Check("yaml refuses tabs", threw);
            Check("yaml CRLF text", Yaml.Str(Yaml.ParseMap("a: 1\r\nb: 2\r\n")["b"]) == "2");
        }

        static void ToolsProjectFile()
        {
            var spec = ProjectFile.FromText("version: 1\nphp: \"8.3\"\nroot: public/sub\ndomains: [shop.test]\nservices: [mysql, redis]\ndatabase: { name: shop_db, collation: utf8mb4_unicode_ci }\njobs:\n  - name: queue\n    command: php artisan queue:work\nhooks:\n  after-start: echo started\n");
            Check("portly.yml php and domains", spec.Php == "8.3" && spec.Domains.Contains("shop.test"), spec.Php);
            Check("portly.yml root uses the OS separator", spec.Root == "public" + Path.DirectorySeparatorChar + "sub", spec.Root);
            Check("portly.yml database", spec.DbName == "shop_db" && spec.DbCollation == "utf8mb4_unicode_ci");
            Check("portly.yml services", spec.Services.SequenceEqual(new[] { "mysql", "redis" }));
            Check("portly.yml job and hook", spec.Jobs.Count == 1 && spec.Jobs[0].Name == "queue" && spec.Jobs[0].Restart && spec.Hooks["after-start"] == "echo started");
            var winRoot = ProjectFile.FromText("version: 1\nroot: web\\app\n");
            Check("portly.yml root written with a backslash", winRoot.Root == "web" + Path.DirectorySeparatorChar + "app", winRoot.Root);
            bool bad = false;
            try { ProjectFile.FromText("version: 1\nroot: ../outside\n"); } catch (FormatException) { bad = true; }
            Check("portly.yml root cannot leave the project", bad);
            bool badDb = false;
            try { ProjectFile.FromText("version: 1\ndatabase: \"a b;drop\"\n"); } catch (FormatException) { badDb = true; }
            Check("portly.yml database name is checked", badDb);
            Check("version rules", VersionRules.Satisfies(new Version(8, 3, 1), "^8.1") && !VersionRules.Satisfies(new Version(7, 4), "^8.1") && VersionRules.Satisfies(new Version(8, 2), "~8.2"));
        }

        static void ToolsPathRules()
        {
            var tmp = ToolsTemp();
            try
            {
                var www = Path.Combine(tmp, "www");
                var inside = Path.Combine(www, "shop", "index.php");
                Check("path inside folder", PathRules.IsUnder(inside, www));
                Check("folder is under itself", PathRules.IsUnder(www, www) && PathRules.IsUnder(www + Path.DirectorySeparatorChar, www));
                Check("sibling with the same prefix is not inside", !PathRules.IsUnder(Path.Combine(tmp, "www2", "x"), www));
                Check("same path ignores a trailing separator", PathRules.Same(www, www + Path.DirectorySeparatorChar));
                Check("different paths differ", !PathRules.Same(www, Path.Combine(tmp, "www2")));
                // Windows and macOS file systems ignore case by default, Linux does not
                var upper = www.ToUpperInvariant();
                Check("case rule of this OS", PathRules.Same(www, upper) == !OS.IsLinux && PathRules.IsUnder(inside, upper) == !OS.IsLinux, OS.Name);
                Check("comparison follows the OS", PathRules.Comparison == (OS.IsLinux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
                Check("relative path uses /", PathRules.RelativeTo(inside, www) == "shop/index.php", PathRules.RelativeTo(inside, www));
                Check("Deploy exclude rules", Deploy.IsExcluded("node_modules/a/b.js", Deploy.DefaultExcludes) && Deploy.IsExcluded("app/storage/logs/x", Deploy.DefaultExcludes)
                    && !Deploy.IsExcluded("src/index.php", Deploy.DefaultExcludes) && Deploy.IsExcluded("a/debug.log", Deploy.DefaultExcludes));
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        static void ToolsSites()
        {
            var tmp = ToolsTemp();
            var old = Settings.Current.DocumentRoot;
            try
            {
                var www = Path.Combine(tmp, "www");
                Touch(Path.Combine(www, "plain", "index.php"));
                Touch(Path.Combine(www, "lara", "artisan"));
                Touch(Path.Combine(www, "lara", "public", "index.php"));
                Touch(Path.Combine(www, "drup", "web", "index.php"));
                Touch(Path.Combine(www, "cake", "webroot", "index.php"));
                Touch(Path.Combine(www, "blog", "wp-config.php"));
                Touch(Path.Combine(www, "blog", "index.php"));
                Touch(Path.Combine(www, "stat", "index.html"));
                Touch(Path.Combine(www, "pyapp", "app.py"));
                Touch(Path.Combine(www, "pyapp", "requirements.txt"));
                Touch(Path.Combine(www, "notes", "readme.txt"));
                Touch(Path.Combine(www, ".hidden", "index.php"));
                Touch(Path.Combine(www, "clients", "acme", "index.php"));
                Touch(Path.Combine(www, "clients", "beta", "public", "index.php"));
                Settings.Current.DocumentRoot = www;
                var all = Sites.ScanAll();
                var names = all.Select(s => s.Name).ToList();
                foreach (var expected in new[] { "plain", "lara", "drup", "cake", "blog", "stat", "clients", "clients/acme", "clients/beta" })
                    Check("site found: " + expected, names.Contains(expected), string.Join(",", names));
                Check("not a site: no markers / other platform / hidden", !names.Contains("notes") && !names.Contains("pyapp") && !names.Contains(".hidden"));
                Func<string, Site> get = n => all.First(s => s.Name == n);
                Check("document root: index.php in the folder", get("plain").DocRoot == get("plain").Folder);
                Check("document root: public/", get("lara").DocRoot == Path.Combine(get("lara").Folder, "public"), get("lara").DocRoot);
                Check("document root: web/", get("drup").DocRoot == Path.Combine(get("drup").Folder, "web"), get("drup").DocRoot);
                Check("document root: webroot/", get("cake").DocRoot == Path.Combine(get("cake").Folder, "webroot"));
                Check("document root: WordPress in the folder", get("blog").DocRoot == get("blog").Folder && Deploy.IsWordPress(get("blog")) && !Deploy.IsWordPress(get("plain")));
                Check("group and its projects", get("clients").IsGroup && get("clients/acme").Group == "clients" && get("clients/beta").DocRoot == Path.Combine(get("clients/beta").Folder, "public"));
                Check("host names", get("plain").Host == Sites.HostFor("plain") && get("clients/acme").Host == "acme." + get("clients").Host, get("clients/acme").Host);
                Check("all valid", all.All(s => s.Valid));
                // a root saved in sites.ini on Windows ("public\sub") still works elsewhere
                Touch(Path.Combine(www, "plain", "pub", "sub", "index.php"));
                var ini = new Ini(Paths.SitesFile);
                ini.Set("plain", "root", "pub\\sub");
                ini.Save();
                var rooted = Sites.ScanAll().First(s => s.Name == "plain");
                Check("custom root from sites.ini", rooted.DocRoot == Path.Combine(www, "plain", "pub", "sub"), rooted.DocRoot);
                ini.Remove("plain", "root");
                ini.Save();
                Check("name rules", Sites.IsValidName("shop") && Sites.IsValidName("example.com") && !Sites.IsValidName("a b") && !Sites.IsValidName("-x"));
            }
            finally
            {
                Settings.Current.DocumentRoot = old;
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        static void ToolsProjectInfo()
        {
            var tmp = ToolsTemp();
            try
            {
                Touch(Path.Combine(tmp, "composer.json"), "{\"require\":{\"laravel/framework\":\"^12.0\"}}");
                Touch(Path.Combine(tmp, ".env"), "DB_CONNECTION=sqlite\nDB_DATABASE=database/app.sqlite\n");
                var info = ProjectInfo.For(new Site { Name = "x", Folder = tmp, DocRoot = tmp });
                Check("project info: Laravel + SQLite file name", info.Tech.StartsWith("Laravel") && info.DbEngine == "SQLite" && info.DbName == "app.sqlite", info.Tech + " / " + info.DbText);
                var tmp2 = ToolsTemp();
                try
                {
                    Touch(Path.Combine(tmp2, "app", "Config", "Database.php"), "<?php\n$cfg = ['database' => 'shop_db'];\n");
                    Touch(Path.Combine(tmp2, "index.php"));
                    var i2 = ProjectInfo.For(new Site { Name = "y", Folder = tmp2, DocRoot = tmp2 });
                    Check("project info: database name from app/Config", i2.DbName == "shop_db", i2.DbText);
                }
                finally { try { Directory.Delete(tmp2, true); } catch { } }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        static void ToolsCommandLines()
        {
            var p = new DeployProfile { Host = "example.com", User = "me", Port = 2222, KeyFile = Path.Combine("a b", "key") };
            var args = Deploy.SshArgs(p, "echo hi");
            Check("ssh args", args.Contains("-p 2222") && args.Contains("me@example.com") && args.Contains("BatchMode=yes") && args.Contains("-i \"a b" + Path.DirectorySeparatorChar + "key\""), args);
            Check("ssh known_hosts in usr/ssh", args.Contains("UserKnownHostsFile=") || args.Contains("known_hosts"));
            Check("remote command is base64", Deploy.RemoteCommand("echo 1\r\necho 2").StartsWith("sh -c \"$(echo ") && !Deploy.RemoteCommand("rm -rf $HOME").Contains("rm -rf"));
            Check("tar excludes", Deploy.LocalExcludes(new[] { ".git", "a b" }) == "--exclude .git --exclude \"a b\"", Deploy.LocalExcludes(new[] { ".git", "a b" }));
            Check("remote excludes quoted for sh", Deploy.RemoteExcludes(new[] { "it's" }) == "--exclude='it'\\''s'", Deploy.RemoteExcludes(new[] { "it's" }));
            if (OS.IsWindows)
            {
                Check("Windows tools: ssh.exe / tar.exe", Deploy.SshExe.EndsWith("ssh.exe") && Deploy.SshKeygenExe.EndsWith("ssh-keygen.exe") && Deploy.TarExe.EndsWith("tar.exe"));
            }
            else
            {
                Check("Unix tools: ssh, ssh-keygen, tar without .exe", Path.GetFileName(Deploy.SshExe) == "ssh" && Path.GetFileName(Deploy.SshKeygenExe) == "ssh-keygen" && Path.GetFileName(Deploy.TarExe) == "tar", Deploy.SshExe);
                Check("tar is found on this system", File.Exists(Deploy.TarExe), Deploy.TarExe);
                Check("Deploy.IsAvailable needs the feature and ssh", Deploy.IsAvailable == (Features.IsOn("deploy") && File.Exists(Deploy.SshExe) && File.Exists(Deploy.TarExe)));
            }
            Check("PHP CLI name", Path.GetFileName(ProjectCreator.PhpCli(Path.Combine("x", "php-8.5"))) == OS.Exe("php"));
            Check("echo after a terminal template", OS.IsWindows ? ProjectCreator.EchoAfter("a b").StartsWith(" && echo. && echo a b") : ProjectCreator.EchoAfter("it's").Contains("'it'\\''s'"));
        }

        static void ToolsShell()
        {
            string exe, args;
            Jobs.ShellCommand("echo portly-job", out exe, out args);
            if (OS.IsWindows) Check("shell is cmd.exe /d /s /c", exe.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) && args == "/d /s /c \"echo portly-job\"", exe + " " + args);
            else Check("shell is /bin/sh -c", exe.EndsWith("sh") && args == "-c \"echo portly-job\"", exe + " " + args);
            var r = ProcessUtil.Run(exe, args);
            Check("the shell runs a command line", r.Output.Trim() == "portly-job", r.Output);
            Jobs.ShellCommand("echo \"two words\" && echo 'x'", out exe, out args);
            if (!OS.IsWindows)
            {
                r = ProcessUtil.Run(exe, args);
                Check("quotes survive the shell", r.Output.Replace("\r", "").Trim() == "two words\nx", r.Output);
            }
            var env = Jobs.EnvFor(null);
            Check("job PATH uses the OS separator", env["PATH"].Contains(Path.PathSeparator.ToString()) || !env["PATH"].Contains(OS.IsWindows ? ":" : ";"));
            Check("hook file extension", Hooks.GlobalFile("after-start").EndsWith(OS.IsWindows ? ".cmd" : ".sh"));
            var hookDir = Hooks.Dir;
            Directory.CreateDirectory(hookDir);
            var file = Hooks.GlobalFile("after-stop");
            try
            {
                File.WriteAllText(file, OS.IsWindows ? "@echo off\r\necho hook-%PORTLY_EVENT%\r\n" : "echo hook-$PORTLY_EVENT\n");
                var hr = Hooks.Run("after-stop", null, OS.IsWindows ? "\"" + file + "\"" : "sh \"" + file + "\"");
                Check("hook runs through the shell with its environment", hr.Output.Contains("hook-after-stop") && hr.ExitCode == 0, hr.Output);
            }
            finally { try { File.Delete(file); } catch { } }
            Check("Cli.IsCommand", Cli.IsCommand(new[] { "status" }) && !Cli.IsCommand(new[] { "nope" }) && !Cli.IsCommand(new string[0]));
            var ver = Cli.Handle(new[] { "version" }).Result;
            Check("portly version", ver.Code == 0 && ver.Output.StartsWith("Portly "), ver.Output);
            var text = Cli.ShellFileText("/opt/it's here/Portly");
            Check("portly sh script text", text.StartsWith("#!/bin/sh\n") && text.Contains("exec '/opt/it'\\''s here/Portly' \"$@\""), text);
            if (!OS.IsWindows)
            {
                var d = ToolsTemp();
                try
                {
                    Cli.EnsureShellFile(d);
                    var f = Path.Combine(d, "portly");
                    var mode = File.GetUnixFileMode(f);
                    Check("portly sh script is executable", File.Exists(f) && (mode & UnixFileMode.UserExecute) != 0, mode.ToString());
                }
                finally { try { Directory.Delete(d, true); } catch { } }
            }
        }

        static void ToolsMysqlClient()
        {
            Check("client names", Database.ClientExe("mysql") == OS.Exe("mysql") && Database.ClientExe("mysqldump.exe") == OS.Exe("mysqldump") && Database.ClientExe("mariadb-upgrade") == OS.Exe("mariadb-upgrade"));
            Check("client file line ends", Database.Nl == (OS.IsWindows ? "\r\n" : "\n"));
            var d = ToolsTemp();
            try
            {
                var secret = Path.Combine(d, "my.cnf");
                Database.WriteSecretFile(secret, "[client]\npassword=\"x\"\n");
                Check("option file written", File.ReadAllText(secret).Contains("password"));
                if (!OS.IsWindows)
                {
                    var mode = File.GetUnixFileMode(secret);
                    Check("option file only for this user (0600)", mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), mode.ToString());
                }
                var from = Path.Combine(d, "src");
                Touch(Path.Combine(from, "a.txt"), "A");
                Touch(Path.Combine(from, "sub", "b.txt"), "B");
                var to = Path.Combine(d, "dst");
                Database.CopyDirectory(from, to);
                Check("copy a data folder", File.ReadAllText(Path.Combine(to, "a.txt")) == "A" && File.ReadAllText(Path.Combine(to, "sub", "b.txt")) == "B" && File.Exists(Path.Combine(from, "a.txt")));
                Check("Database.IsValidName", Database.IsValidName("shop_1") && !Database.IsValidName("a-b") && !Database.IsValidName("a`b"));
                Check("SqlString escapes", Database.SqlString("a'b\\c") == "'a''b\\\\c'");
            }
            finally { try { Directory.Delete(d, true); } catch { } }
        }

        static void ToolsTrash()
        {
            Check("AppleScript path escaping", ProjectCreator.MacTrashScript("/Users/a b/\"x\"\\y") == "tell application \"Finder\" to delete POSIX file \"/Users/a b/\\\"x\\\"\\\\y\"", ProjectCreator.MacTrashScript("/Users/a b/\"x\"\\y"));
            Check("trashinfo path encoding", ProjectCreator.TrashPathEncode("/home/a b/ş/x") == "/home/a%20b/%C5%9F/x", ProjectCreator.TrashPathEncode("/home/a b/ş/x"));
            // the FreeDesktop fallback is plain file work: it runs on every OS, into a temporary trash
            var tmp = ToolsTemp();
            try
            {
                var trash = Path.Combine(tmp, "Trash");
                var proj = Path.Combine(tmp, "proj");
                Touch(Path.Combine(proj, "index.php"), "<?php");
                var moved = ProjectCreator.FreeDesktopTrash(proj, trash);
                var info = Path.Combine(trash, "info", "proj.trashinfo");
                Check("fallback trash moves the folder", !Directory.Exists(proj) && File.Exists(Path.Combine(moved, "index.php")) && moved == Path.Combine(trash, "files", "proj"), moved);
                var text = File.Exists(info) ? File.ReadAllText(info) : "";
                Check("fallback trash writes .trashinfo", text.StartsWith("[Trash Info]\n") && text.Contains("\nPath=") && text.Contains("\nDeletionDate=") && text.Contains("\nPath=" + ProjectCreator.TrashPathEncode(Path.GetFullPath(proj)) + "\n"), text);
                var proj2 = Path.Combine(tmp, "proj");
                Touch(Path.Combine(proj2, "index.php"), "second");
                var moved2 = ProjectCreator.FreeDesktopTrash(proj2, trash);
                Check("second folder with the same name gets a free name", moved2 == Path.Combine(trash, "files", "proj.2") && File.Exists(Path.Combine(trash, "info", "proj.2.trashinfo")), moved2);
                Check("default trash folder", ProjectCreator.DefaultTrashDir().EndsWith("Trash"));
                bool missing = false;
                try { ProjectCreator.MoveFolderToTrash(Path.Combine(tmp, "nope")); } catch (DirectoryNotFoundException) { missing = true; }
                Check("trash of a missing folder fails cleanly", missing);

                // the real thing, only where a trash program exists and never on a real project
                if (OS.IsWindows) { Console.WriteLine("  SKIP real trash (Windows Recycle Bin is not used by the tests)"); return; }
                var haveTool = OS.IsMac
                    ? Directory.Exists("/System/Library/CoreServices/Finder.app") && Environment.GetEnvironmentVariable("CI") == null
                    : File.Exists(Deploy.UnixTool("gio")) && Deploy.UnixTool("gio").EndsWith("gio");
                if (!haveTool) { Console.WriteLine("  SKIP real trash (" + (OS.IsMac ? "Finder not usable here" : "gio not installed") + ")"); return; }
                var real = Path.Combine(tmp, "portly-trash-test-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Touch(Path.Combine(real, "index.php"));
                ProjectCreator.MoveFolderToTrash(real);
                Check("real trash removed the folder", !Directory.Exists(real));
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        static void ToolsPlatformGuards()
        {
            Check("Features: WSL and Windows services only on Windows", Features.IsAvailable("wsl") == OS.IsWindows && Features.IsAvailable("serviceMode") == OS.IsWindows && Features.IsAvailable("cli"));
            if (!OS.IsWindows)
            {
                Settings.Current.Raw.Set("features", "wsl", true);
                Check("a Windows-only feature is never on elsewhere", !Features.IsOn("wsl"));
                Settings.Current.Raw.Set("features", "wsl", false);
                Check("WSL not supported", !WslAccess.Supported && WslAccess.BindAddress() == null && WslAccess.Disable());
                bool threw = false;
                try { WslAccess.Enable(); } catch (NotSupportedException) { threw = true; }
                Check("WSL setup refuses cleanly", threw);
                ShareFirewall.Open(8900, false);   // nothing to do: devices on the network just reach the bound port
                ShareFirewall.Close(8900);
                ShareFirewall.Prepare(new[] { 8900 });
                Check("firewall calls are no-ops", ShareFirewall.Note(8900) == null);
            }
            Check("edition name", AppInfo.EditionName != "" && (OS.IsWindows || AppInfo.EditionName == (OS.IsMac ? "macOS" : "Linux")), AppInfo.EditionName);
            Check("user agent names the OS", Net.UserAgent.Contains(OS.IsWindows ? "Windows NT" : OS.IsMac ? "Macintosh" : "Linux") && Net.UserAgent.Contains("Portly/"), Net.UserAgent);
        }

        static void ToolsImporter()
        {
            var found = Importer.Detect();
            Check("importer detection runs", found != null);
            if (OS.IsWindows) return;
            var kinds = Importer.ProjectOnlySources().Select(x => x[0]).ToList();
            Check("importer offers Herd and Local everywhere", kinds.Contains("Laravel Herd") && kinds.Contains("Local (Flywheel)"));
            Check("importer offers only sources of this OS", OS.IsMac ? kinds.Contains("MAMP") && !kinds.Contains("XAMPP") : kinds.Contains("XAMPP") && !kinds.Contains("MAMP"), string.Join(",", kinds));
            Check("no Windows stack is detected", !found.Any(s => s.Kind == "laragon" || s.Product == "WampServer" || s.MysqlBin != null), string.Join(",", found.Select(s => s.Product)));
        }

        static void ToolsSiteLogs()
        {
            var tmp = ToolsTemp();
            try
            {
                var folder = Path.Combine(tmp, "Shop");
                var site = new Site { Name = "shop", Host = "shop.portly", Folder = folder, DocRoot = folder };
                var m = SiteLogs.SiteMatcher(site);
                var sep = Path.DirectorySeparatorChar;
                Check("log entry with the site folder", m("[11-Nov-2026] PHP Warning: x in " + folder + sep + "index.php on line 3"));
                Check("log entry with the host", m("client 1.2.3.4 requested shop.portly/x"));
                Check("other site's entry is not shown", !m("PHP Warning: in " + Path.Combine(tmp, "Shop2") + sep + "index.php") && !m("other.portly"));
                var lower = "PHP Warning: in " + folder.ToLowerInvariant() + sep + "index.php";
                Check("folder match follows the OS case rule", m(lower) == !OS.IsLinux || folder.ToLowerInvariant() == folder, OS.Name);
                Touch(Path.Combine(folder, "wp-content", "x.txt"));
                Touch(Path.Combine(folder, "wp-config.php"));
                Check("WordPress log path", SiteLogs.WordPressLog(site) == Path.Combine(folder, "wp-content", "debug.log"));
                Touch(Path.Combine(folder, "storage", "logs", "laravel-2026-11-11.log"), "x");
                Check("Laravel log path", SiteLogs.LaravelLog(site).EndsWith("laravel-2026-11-11.log"));
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
