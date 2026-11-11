using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Portly.Core;
using Portly.Packages;

namespace Portly.CoreTests
{
    static partial class Program
    {
        static string EmbeddedCatalog(string name)
        {
            using (var s = typeof(Catalog).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        static void Suite_Packages()
        {
            // ---- the catalog of this OS ----
            Catalog.LoadLocal();
            Check("catalog: loaded for " + OS.Name, Catalog.Entries.Count > 0, Catalog.ManifestName + " " + Catalog.Entries.Count);
            Check("catalog: file name", Catalog.ManifestName == (OS.IsWindows ? "packages.json" : OS.IsMac ? "packages-macos.json" : "packages-linux.json"), Catalog.ManifestName);
            if (OS.IsWindows)
            {
                // the Windows catalog is read exactly as before: no arch, no parts, zip / file / sfx
                Check("catalog (Windows): no arch or parts", Catalog.Entries.All(e => e.Arch == "" && e.Parts.Count == 0));
                Check("catalog (Windows): zip / file / sfx only", Catalog.Entries.All(e => e.Kind == "zip" || e.Kind == "file" || e.Kind == "sfx"));
                var win = Json.ParseObject(EmbeddedCatalog(AppInfo.IsLegacy ? "catalog.legacy.json" : "catalog.modern.json"));
                int listed = win.Arr("packages").Count(p => p.Str("type") != "" && p.Str("folder") != "" && p.Str("url") != "");
                Check("catalog (Windows): all entries of packages.json", Catalog.Entries.Count == listed, Catalog.Entries.Count + " vs " + listed);
                Check("catalog (Windows): apache + php + mysql present", new[] { "apache", "php", "mysql" }.All(t => Catalog.Entries.Any(e => e.Type == t)));
            }
            else
            {
                Check("catalog: entries for " + OS.Arch + " only", Catalog.Entries.All(e => e.Arch == "" || e.Arch == OS.Arch));
                foreach (var t in new[] { "php", "caddy", "mysql", "mailpit", "nodejs", "cloudflared", "phpmyadmin", "adminer" })
                    Check("catalog: " + t + " offered", Catalog.Entries.Any(e => e.Type == t));
                Check("catalog: php has a php-cli part", Catalog.Entries.Where(e => e.Type == "php").All(e => e.Parts.Count == 1 && e.Parts[0].Sha256.Length == 64));
                Check("catalog: every entry has a sha256", Catalog.Entries.All(e => Regex.IsMatch(e.Sha256, "^[0-9a-f]{64}$")), string.Join(",", Catalog.Entries.Where(e => !Regex.IsMatch(e.Sha256, "^[0-9a-f]{64}$")).Select(e => e.Folder)));
                Check("catalog: recommended php", Catalog.Recommended("php") != null && Catalog.Recommended("php").Version.StartsWith("8.5"));
            }

            // ---- both macOS / Linux catalogs are valid on every OS (read straight from the embedded files) ----
            foreach (var os in new[] { "linux", "macos" })
            {
                var text = EmbeddedCatalog("catalog." + os + ".json");
                Check(os + " catalog: embedded", text != null);
                if (text == null) continue;
                var doc = Json.ParseObject(text);
                var list = doc.Arr("packages");
                Check(os + " catalog: edition", doc.Str("edition") == os, doc.Str("edition"));
                bool allHashed = true, kindsOk = true, archOk = true, https = true, folders = true;
                var seen = new HashSet<string>();
                foreach (var p in list)
                {
                    var hex = new Regex("^[0-9a-f]{64}$");
                    if (!hex.IsMatch(p.Str("sha256"))) allHashed = false;
                    if (!new[] { "tar.gz", "tar.xz", "zip", "zip+tar.xz", "file" }.Contains(p.Str("kind", "zip"))) kindsOk = false;
                    if (!new[] { "", "x64", "arm64" }.Contains(p.Str("arch"))) archOk = false;
                    if (!p.Str("url").StartsWith("https://")) https = false;
                    if (!seen.Add(p.Str("type") + "|" + p.Str("folder") + "|" + p.Str("arch"))) folders = false;
                    foreach (var q in p.Arr("parts"))
                    {
                        if (!hex.IsMatch(q.Str("sha256"))) allHashed = false;
                        if (!q.Str("url").StartsWith("https://")) https = false;
                        if (!new[] { "tar.gz", "tar.xz", "zip" }.Contains(q.Str("kind", "tar.gz"))) kindsOk = false;
                    }
                }
                Check(os + " catalog: every url has a sha256", allHashed);
                Check(os + " catalog: kinds", kindsOk);
                Check(os + " catalog: arch values", archOk);
                Check(os + " catalog: https urls", https);
                Check(os + " catalog: no duplicate type/folder/arch", folders);
                foreach (var arch in new[] { "x64", "arm64" })
                    foreach (var t in new[] { "php", "caddy", "mysql", "mailpit", "nodejs", "cloudflared" })
                        Check(os + " catalog: " + t + " for " + arch, list.Any(p => p.Str("type") == t && (p.Str("arch") == "" || p.Str("arch") == arch)));
            }

            // ---- package type layout ----
            if (OS.IsWindows)
            {
                Check("types (Windows): php detects php-cgi.exe", PackageTypes.Get("php").DetectFile == "php-cgi.exe");
                Check("types (Windows): mysql detects bin\\mysqld.exe", PackageTypes.Get("mysql").DetectFile == @"bin\mysqld.exe");
                Check("types (Windows): apache detects bin\\httpd.exe", PackageTypes.Get("apache").DetectFile == @"bin\httpd.exe");
                Check("types (Windows): nodejs detects node.exe", PackageTypes.Get("nodejs").DetectFile == "node.exe");
                Check("types (Windows): Exe()", PackageTypes.Exe("mysql", "m", "mysqld") == Path.Combine(Paths.BinOf("mysql"), "m", "bin", "mysqld.exe")
                    && PackageTypes.Exe("php", "p", "php-cgi") == Path.Combine(Paths.BinOf("php"), "p", "php-cgi.exe"));
            }
            else
            {
                Check("types: php detects php-fpm", PackageTypes.Get("php").DetectFile == "php-fpm");
                Check("types: caddy / frankenphp / mailpit / cloudflared at the folder root",
                    new[] { "caddy", "frankenphp", "mailpit", "cloudflared" }.All(t => PackageTypes.Get(t).DetectFile == t));
                Check("types: mysql detects bin/mysqld", PackageTypes.Get("mysql").DetectFile == "bin/mysqld");
                Check("types: nodejs detects bin/node", PackageTypes.Get("nodejs").DetectFile == "bin/node");
                Check("types: Exe()", PackageTypes.Exe("php", "p", "php-fpm") == Path.Combine(Paths.BinOf("php"), "p", "php-fpm")
                    && PackageTypes.Exe("mysql", "m", "mysqld") == Path.Combine(Paths.BinOf("mysql"), "m", "bin", "mysqld")
                    && PackageTypes.Exe("nodejs", "n", "node") == Path.Combine(Paths.BinOf("nodejs"), "n", "bin", "node"));
            }

            Run("installer", InstallerChecks);
        }

        /// <summary>
        /// Offline install: the archives are put into the download cache with their real hashes, so no network is used.
        /// A tar.gz as the main archive (with a part merged into the same folder) and a tar.xz with a subdir.
        /// </summary>
        static void InstallerChecks()
        {
            var work = Path.Combine(Paths.Tmp, "pkgtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);
            var type = new PackageType { Id = "faketool", Name = "Fake tool", Category = "dev", DetectFile = OS.IsWindows ? "faketool.exe" : "faketool" };
            PackageTypes.All.Add(type);
            try
            {
                var tar = Installer.TarExe();
                // main archive: faketool + a data file; part: second program + a nested file
                var main = Path.Combine(work, "main"); Directory.CreateDirectory(main);
                File.WriteAllText(Path.Combine(main, type.DetectFile), "#!/bin/sh\necho main\n");
                File.WriteAllText(Path.Combine(main, "README.txt"), "hello");
                var part = Path.Combine(work, "part"); Directory.CreateDirectory(Path.Combine(part, "lib"));
                File.WriteAllText(Path.Combine(part, "faketool-cli"), "#!/bin/sh\necho cli\n");
                File.WriteAllText(Path.Combine(part, "lib", "x.dat"), "data");
                // the xz one has a top folder (subdir)
                var wrapped = Path.Combine(work, "wrapped", "faketool-2.0"); Directory.CreateDirectory(Path.Combine(wrapped, "bin"));
                File.WriteAllText(Path.Combine(wrapped, "bin", "tool"), "#!/bin/sh\necho 2\n");
                if (!OS.IsWindows)
                {
                    Installer.MakeExecutable(Path.Combine(main, type.DetectFile));
                    Installer.MakeExecutable(Path.Combine(part, "faketool-cli"));
                    Installer.MakeExecutable(Path.Combine(wrapped, "bin", "tool"));
                }
                var a1 = Path.Combine(work, "main.tar.gz");
                var a2 = Path.Combine(work, "part.tar.gz");
                var a3 = Path.Combine(work, "wrapped.tar.xz");
                bool ok1 = Tar(tar, "-czf", a1, main), ok2 = Tar(tar, "-czf", a2, part);
                Check("installer: test archives built", ok1 && ok2, tar);
                if (!ok1 || !ok2) return;
                bool hasXz = Tar(tar, "-cJf", a3, Path.Combine(work, "wrapped"));

                var entry = new PackageEntry
                {
                    Type = "faketool", Version = "1.0.0", Folder = "faketool-1.0.0", Kind = "tar.gz",
                    Url = "https://packages.invalid/faketool-1.0.0.tar.gz", Sha256 = Net.Sha256(a1),
                    Parts = { new PackagePart { Url = "https://packages.invalid/faketool-cli-1.0.0.tar.gz", Sha256 = Net.Sha256(a2), Kind = "tar.gz" } },
                };
                File.Copy(a1, Path.Combine(Paths.Downloads, "faketool-faketool-1.0.0-faketool-1.0.0.tar.gz"), true);
                File.Copy(a2, Path.Combine(Paths.Downloads, "faketool-faketool-1.0.0-part1-faketool-cli-1.0.0.tar.gz"), true);
                Installer.InstallAsync(entry, null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();

                var dir = entry.InstallDir;
                Check("installer: detect file in place", File.Exists(Path.Combine(dir, type.DetectFile)), dir);
                Check("installer: part merged into the same folder", File.Exists(Path.Combine(dir, "faketool-cli")) && File.Exists(Path.Combine(dir, "lib", "x.dat")));
                Check("installer: type is listed as installed", PackageTypes.Installed("faketool").Contains("faketool-1.0.0"));
                Check("installer: download cache cleaned", !Directory.EnumerateFiles(Paths.Downloads, "faketool-*").Any());
                Check("installer: file list written", Integrity.HasList(dir));
                var problem = Integrity.Check(dir, false);
                Check("installer: integrity check is clean", problem != null && problem.Count == 0, problem == null ? "no list" : string.Join(",", problem.Missing.Concat(problem.Changed)));
                if (!OS.IsWindows)
                {
                    Check("installer: main program is executable", IsExecutable(Path.Combine(dir, type.DetectFile)));
                    Check("installer: part program is executable", IsExecutable(Path.Combine(dir, "faketool-cli")));
                    Check("installer: data file is not executable", !IsExecutable(Path.Combine(dir, "lib", "x.dat")));
                    // a missing program is found by the quick check
                    File.Delete(Path.Combine(dir, "faketool"));
                    var lost = Integrity.Check(dir, true);
                    Check("installer: integrity sees a deleted program", lost != null && lost.Missing.Contains("faketool"));
                }

                // a wrong hash is refused (and the file deleted) before anything is extracted
                var tmp = Path.Combine(work, "bad.tar.gz");
                File.Copy(a1, tmp, true);
                bool refused = false;
                try { Installer.VerifyDownload(new PackageEntry { Type = "faketool", Version = "9.9.9", Folder = "x", Url = "https://packages.invalid/bad", Sha256 = new string('0', 64) }, tmp); }
                catch (Exception) { refused = true; }
                Check("installer: wrong sha256 refused", refused && !File.Exists(tmp));

                if (hasXz)
                {
                    var x = new PackageEntry
                    {
                        Type = "faketool", Version = "2.0.0", Folder = "faketool-2.0.0", Kind = "tar.xz", Subdir = "faketool-2.0",
                        Url = "https://packages.invalid/wrapped.tar.xz", Sha256 = Net.Sha256(a3),
                    };
                    File.Copy(a3, Path.Combine(Paths.Downloads, "faketool-faketool-2.0.0-wrapped.tar.xz"), true);
                    Installer.InstallAsync(x, null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    Check("installer: tar.xz with subdir", File.Exists(Path.Combine(x.InstallDir, "bin", "tool")), x.InstallDir);
                    if (!OS.IsWindows) Check("installer: tar.xz keeps the exec bit", IsExecutable(Path.Combine(x.InstallDir, "bin", "tool")));
                }
                else Console.WriteLine("  (skipped: this tar cannot write xz)");

                if (!OS.IsWindows)
                {
                    // kind=file: a single binary is made executable, a .php file is not
                    var bin = Path.Combine(work, "single");
                    File.WriteAllText(bin, "#!/bin/sh\necho single\n");
                    var f = new PackageEntry { Type = "faketool", Version = "3.0.0", Folder = "faketool-3.0.0", Kind = "file", FileName = "faketool", Url = "https://packages.invalid/single", Sha256 = Net.Sha256(bin) };
                    File.Copy(bin, Path.Combine(Paths.Downloads, "faketool-faketool-3.0.0-single"), true);
                    Installer.InstallAsync(f, null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    Check("installer: kind=file is executable", IsExecutable(Path.Combine(f.InstallDir, "faketool")));
                }
            }
            finally
            {
                PackageTypes.All.Remove(type);
                try { Directory.Delete(work, true); } catch { }
                try { Directory.Delete(Paths.BinOf("faketool"), true); } catch { }
            }
        }

        static bool Tar(string tar, string mode, string archive, string folder)
        {
            // contents of the folder, not the folder itself
            var r = ProcessUtil.Run(tar, mode + " " + ProcessUtil.Quote(archive) + " -C " + ProcessUtil.Quote(folder) + " .", folder, null, 60000);
            return r.Ok && File.Exists(archive);
        }

        static bool IsExecutable(string file)
        {
#if NETFRAMEWORK
            return true;
#else
            return OS.IsWindows || (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;
#endif
        }
    }
}
