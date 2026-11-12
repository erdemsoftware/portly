using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Formats.Tar;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;
using Portly.Packages;

namespace Portly.CoreTests
{
    /// <summary>Self update of Portly 2: the "assets" of app.json, choosing the file per system, refusals, and a real local update of a fake install. No network.</summary>
    static partial class Program
    {
        static string UpdSha(byte[] data)
        {
            using (var h = System.Security.Cryptography.SHA256.Create()) return string.Concat(h.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        static string UpdSha(string file) { return UpdSha(File.ReadAllBytes(file)); }

        static string UpdError(Func<Task> action)
        {
            try { action().GetAwaiter().GetResult(); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        static UpdatePlan UpdPlan(UpdateKind kind, string target, string url, string sha)
        {
            return new UpdatePlan { Kind = kind, Target = target, Asset = new UpdateAsset { Url = url, Sha256 = sha } };
        }

        static void Suite_Updates()
        {
            Run("updates manifest", UpdManifest);
            Run("updates asset selection", UpdSelection);
            Run("updates detection", UpdDetect);
            Run("updates refusals", UpdRefusals);
            Run("updates appimage", UpdAppImage);
            Run("updates tarball", UpdTarball);
            Run("updates deb", UpdDeb);
            if (OS.IsMac) Run("updates macos app", UpdMac);
        }

        const string UpdHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        static void UpdManifest()
        {
            var running = new Version(2, 0, 0);
            var old = Updater.Parse("{\"version\":\"2.0.1\",\"url\":\"https://example.com/p.zip\",\"sha256\":\"" + UpdHash + "\",\"notes\":{\"en\":\"Fix\"}}", running);
            Check("manifest without assets: parsed as before", old != null && old.Version == new Version(2, 0, 1) && old.Url == "https://example.com/p.zip" && old.Sha256 == UpdHash && old.Notes == "Fix");
            Check("manifest without assets: no assets", old.Assets != null && old.Assets.Count == 0);

            var json = "{\"version\":\"2.0.1\",\"url\":\"https://example.com/win.zip\",\"sha256\":\"" + UpdHash + "\",\"assets\":{"
                + "\"windows-x64\":{\"url\":\"https://example.com/win.zip\",\"sha256\":\"" + UpdHash + "\",\"size\":1234},"
                + "\"macos-arm64\":{\"url\":\"https://example.com/mac-arm.zip\",\"sha256\":\"" + UpdHash.ToUpperInvariant() + "\"},"
                + "\"linux-x64\":{\"tarball\":{\"url\":\"https://example.com/l.tar.gz\",\"sha256\":\"" + UpdHash + "\"},\"appimage\":{\"url\":\"https://example.com/l.AppImage\",\"sha256\":\"" + UpdHash + "\"},\"deb\":{\"url\":\"http://example.com/l.deb\",\"sha256\":\"" + UpdHash + "\"}},"
                + "\"linux-arm64\":{\"tarball\":{\"url\":\"https://example.com/a.tar.gz\",\"sha256\":\"abc\"}},"
                + "\"macos-x64\":\"oops\"}}";
            var u = Updater.Parse(json, running);
            Check("assets: a plain asset", u.Assets.ContainsKey("windows-x64") && u.Assets["windows-x64"].Size == 1234 && u.Assets["macos-arm64"].Url == "https://example.com/mac-arm.zip");
            Check("assets: several kinds under one platform", u.Assets.ContainsKey("linux-x64/tarball") && u.Assets.ContainsKey("linux-x64/appimage"));
            Check("assets: an http address is dropped", !u.Assets.ContainsKey("linux-x64/deb"));
            Check("assets: a short sha256 is dropped", !u.Assets.ContainsKey("linux-arm64/tarball"));
            Check("assets: garbage is ignored", !u.Assets.ContainsKey("macos-x64") && u.Assets.Count == 4, u.Assets.Count.ToString());
            Check("assets: the top-level fields stay (Portly 1.x reads only those)", u.Url == "https://example.com/win.zip" && u.Sha256 == UpdHash);
            Check("assets: a manifest that is not newer offers nothing", Updater.Parse(json, new Version(2, 0, 1)) == null && Updater.Parse(json, new Version(3, 0, 0)) == null);
        }

        static void UpdSelection()
        {
            Func<string, UpdateAsset> a = n => new UpdateAsset { Url = "https://example.com/" + n, Sha256 = UpdHash };
            var u = new AppUpdate
            {
                Version = new Version(2, 0, 1), Url = "https://example.com/top.zip", Sha256 = UpdHash,
                Assets = new Dictionary<string, UpdateAsset>
                {
                    { "windows-x64", a("w.zip") }, { "macos-arm64", a("m-arm.zip") }, { "macos-x64", a("m-x64.zip") },
                    { "linux-x64/tarball", a("l.tgz") }, { "linux-x64/appimage", a("l.appimage") }, { "linux-x64/deb", a("l.deb") }, { "linux-arm64/appimage", a("la.appimage") },
                }
            };
            Func<string, string, UpdateKind, string> pick = (os, arch, k) => { var s = UpdateAssets.Select(u, os, arch, k); return s == null ? null : s.Url.Substring(20); };
            Check("select: windows", pick("windows", "x64", UpdateKind.WindowsZip) == "w.zip");
            Check("select: macos per arch", pick("macos", "arm64", UpdateKind.MacApp) == "m-arm.zip" && pick("macos", "x64", UpdateKind.MacApp) == "m-x64.zip");
            Check("select: linux per kind", pick("linux", "x64", UpdateKind.LinuxTarball) == "l.tgz" && pick("linux", "x64", UpdateKind.LinuxAppImage) == "l.appimage" && pick("linux", "x64", UpdateKind.LinuxDeb) == "l.deb");
            Check("select: linux arm64 has only what is listed", pick("linux", "arm64", UpdateKind.LinuxAppImage) == "la.appimage" && pick("linux", "arm64", UpdateKind.LinuxTarball) == null && pick("linux", "arm64", UpdateKind.LinuxDeb) == null);
            Check("select: windows arm64 without its own asset uses the top-level (x64, runs emulated) zip", pick("windows", "arm64", UpdateKind.WindowsZip) == "top.zip");
            u.Assets.Remove("windows-x64");
            Check("select: windows falls back to the top-level zip (a manifest without assets)", pick("windows", "x64", UpdateKind.WindowsZip) == "top.zip");
            Check("select: the top-level zip is never used for macOS or Linux", pick("macos", "arm64", UpdateKind.MacApp) == "m-arm.zip" && pick("linux", "x64", UpdateKind.LinuxTarball) == "l.tgz" && pick("macos", "riscv", UpdateKind.MacApp) == null);
            Check("select: nothing for kind None or no update", pick("linux", "x64", UpdateKind.None) == null && UpdateAssets.Select(null, "linux", "x64", UpdateKind.LinuxTarball) == null);

            var plan = Updater.WithAsset(new UpdatePlan { Kind = UpdateKind.LinuxDeb, Target = "/opt/portly" }, new AppUpdate { Version = new Version(2, 0, 1) }, "linux", "x64");
            Check("plan: a release without a file for this system cannot be installed", !plan.CanInstall && plan.Reason != null);
            plan = Updater.WithAsset(new UpdatePlan { Kind = UpdateKind.LinuxDeb, Target = "/opt/portly" }, u, "linux", "x64");
            Check("plan: with a file it can", plan.CanInstall && plan.Asset.Url.EndsWith("l.deb"));
        }

        static void UpdDetect()
        {
            var tmp = ToolsTemp();
            try
            {
                var sep = Path.DirectorySeparatorChar;
                // macOS
                var macDir = Path.Combine(tmp, "Applications", "Portly.app", "Contents", "MacOS");
                Directory.CreateDirectory(macDir);
                var mac = Updater.Detect("macos", macDir, Path.Combine(macDir, "Portly"), null, false, Path.Combine(tmp, "data"));
                Check("detect macOS: the .app bundle", mac.Kind == UpdateKind.MacApp && mac.Target == Path.Combine(tmp, "Applications", "Portly.app") && mac.Reason == null, mac.Reason);
                var run = Updater.Detect("macos", macDir, "/usr/local/share/dotnet/dotnet", null, false, Path.Combine(tmp, "data"));
                Check("detect macOS: 'dotnet Portly.dll' is not an install", run.Kind == UpdateKind.None && run.Reason != null);
                var nope = Updater.Detect("macos", Path.Combine(tmp, "nowhere", "Portly.app", "Contents", "MacOS"), Path.Combine(tmp, "nowhere", "Portly.app", "Contents", "MacOS", "Portly"), null, false, "");
                Check("detect macOS: a folder that cannot be written is explained", nope.Kind == UpdateKind.MacApp && nope.Reason != null && nope.Reason.Contains("nowhere"));
                var inside = Updater.Detect("macos", macDir, Path.Combine(macDir, "Portly"), null, false, Path.Combine(tmp, "Applications", "Portly.app", "Contents", "data"));
                Check("detect: a data folder inside the app is refused", inside.Reason != null);

                // Linux AppImage
                var img = Path.Combine(tmp, "Portly.AppImage");
                File.WriteAllText(img, "x");
                var ai = Updater.Detect("linux", "/tmp/.mount_x/usr/bin", "/tmp/.mount_x/usr/bin/Portly", img, false, Path.Combine(tmp, "data"));
                Check("detect Linux: AppImage (APPIMAGE set)", ai.Kind == UpdateKind.LinuxAppImage && ai.Target == img && ai.Reason == null, ai.Reason);
                // tar.gz
                var tar = Path.Combine(tmp, "share", "portly");
                Directory.CreateDirectory(tar);
                var tb = Updater.Detect("linux", tar, Path.Combine(tar, "Portly"), null, false, Path.Combine(tmp, "data"));
                Check("detect Linux: tar.gz folder", tb.Kind == UpdateKind.LinuxTarball && tb.Target == tar && tb.Reason == null, tb.Reason);
                var dll = Updater.Detect("linux", tar, "/usr/bin/dotnet", null, false, "");
                Check("detect Linux: a build folder started with dotnet is not an install", dll.Kind == UpdateKind.None);
                if (!OS.IsWindows)
                {
                    var deb = Updater.Detect("linux", "/opt/portly", "/opt/portly/Portly", null, false, Path.Combine(tmp, "data"));
                    Check("detect Linux: .deb without pkexec says why", deb.Kind == UpdateKind.LinuxDeb && deb.Reason != null && deb.Reason.Contains("pkexec"));
                    var deb2 = Updater.Detect("linux", "/opt/portly", "/opt/portly/Portly", null, true, Path.Combine(tmp, "data"));
                    Check("detect Linux: .deb with pkexec", deb2.Kind == UpdateKind.LinuxDeb && deb2.Reason == null);
                }
                // Windows
                var win = Updater.Detect("windows", tmp, Path.Combine(tmp, "Portly.exe"), null, true, tmp);
                Check("detect Windows: zip install next to the exe", win.Kind == UpdateKind.WindowsZip && win.Target == tmp && win.Reason == null);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        static void UpdRefusals()
        {
            var tmp = ToolsTemp();
            var saved = Updater.Downloader;
            try
            {
                var target = Path.Combine(tmp, "Portly.AppImage");
                var good = Encoding.ASCII.GetBytes("\u007fELF-new");
                File.WriteAllText(target, "old");
                var downloads = 0;
                Updater.Downloader = (url, file, p, ct) => { downloads++; File.WriteAllBytes(file, good); return Task.CompletedTask; };
                var u = new AppUpdate { Version = new Version(2, 0, 1) };
                var running = new Version(2, 0, 0);
                Func<UpdatePlan, string> run = plan => UpdError(() => Updater.InstallAsync(u, plan, running, null, CancellationToken.None));

                var err = run(UpdPlan(UpdateKind.LinuxAppImage, target, "https://example.com/x", UpdHash));
                Check("refuse: a wrong sha256", err == I18n.T("err.checksum") && File.ReadAllText(target) == "old" && downloads == 1, err);
                Check("refuse: nothing is left next to the app after a wrong sha256", Directory.GetFileSystemEntries(tmp).Length == 1);

                downloads = 0;
                err = run(UpdPlan(UpdateKind.LinuxAppImage, target, "http://example.com/x", UpdSha(good)));
                Check("refuse: an http address", err == I18n.T("err.updateManifest") && downloads == 0, err);
                err = run(UpdPlan(UpdateKind.LinuxAppImage, target, "https://example.com/x", "abc"));
                Check("refuse: a missing or short sha256", err == I18n.T("err.updateManifest") && downloads == 0, err);
                err = run(new UpdatePlan { Kind = UpdateKind.LinuxAppImage, Target = target });
                Check("refuse: no asset", err == I18n.T("err.updateManifest") && downloads == 0, err);
                err = run(new UpdatePlan { Kind = UpdateKind.LinuxAppImage, Target = target, Reason = "no write permission", Asset = new UpdateAsset { Url = "https://example.com/x", Sha256 = UpdSha(good) } });
                Check("refuse: a plan with a reason says the reason", err == "no write permission" && downloads == 0, err);

                foreach (var v in new[] { new Version(2, 0, 0), new Version(1, 9, 9) })
                {
                    u.Version = v;
                    err = run(UpdPlan(UpdateKind.LinuxAppImage, target, "https://example.com/x", UpdSha(good)));
                    Check("refuse: downgrade / same version " + v, err == I18n.T("err.updateRollback") && downloads == 0 && File.ReadAllText(target) == "old", err);
                }
                u.Version = new Version(2, 0, 1);

                // an installed Portly 2.0.0 from a "windows-x64" ...: the real ApplyAsync stops before any download
                var lower = new AppUpdate { Version = new Version(0, 0, 1), Url = "https://127.0.0.1:1/never.zip", Sha256 = UpdHash };
                err = UpdError(() => Updater.ApplyAsync(lower, null, CancellationToken.None));
                Check("refuse: ApplyAsync with a lower version stops before downloading", err == I18n.T("err.updateRollback"), err);
            }
            finally { Updater.Downloader = saved; try { Directory.Delete(tmp, true); } catch { } }
        }

        static void UpdAppImage()
        {
            var tmp = ToolsTemp();
            var saved = Updater.Downloader;
            try
            {
                var target = Path.Combine(tmp, "Portly.AppImage");
                File.WriteAllText(target, "old");
                var newer = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3, 4 };
                var progress = new List<int>();
                Updater.Downloader = (url, file, p, ct) => { p(50, 4); p(100, 8); File.WriteAllBytes(file, newer); return Task.CompletedTask; };
                var u = new AppUpdate { Version = new Version(2, 0, 1) };
                var r = Updater.InstallAsync(u, UpdPlan(UpdateKind.LinuxAppImage, target, "https://example.com/x", UpdSha(newer)), new Version(2, 0, 0), p => progress.Add(p.Percent), CancellationToken.None).GetAwaiter().GetResult();
                Check("appimage: the file is replaced", File.ReadAllBytes(target).SequenceEqual(newer));
                Check("appimage: progress reported", progress.SequenceEqual(new[] { 50, 100 }));
                Check("appimage: nothing else left in the folder", Directory.GetFileSystemEntries(tmp).Length == 1);
                Check("appimage: starts the same file again", r.Kind == UpdateKind.LinuxAppImage && r.RelaunchFile == target);
                if (!OS.IsWindows) Check("appimage: executable", (File.GetUnixFileMode(target) & UnixFileMode.UserExecute) != 0);

                // not an AppImage (a signed zip served as an AppImage): refused, the old file stays
                var notElf = Encoding.ASCII.GetBytes("<html>error page</html>");
                Updater.Downloader = (url, file, p, ct) => { File.WriteAllBytes(file, notElf); return Task.CompletedTask; };
                u.Version = new Version(2, 0, 2);
                var err = UpdError(() => Updater.InstallAsync(u, UpdPlan(UpdateKind.LinuxAppImage, target, "https://example.com/x", UpdSha(notElf)), new Version(2, 0, 1), null, CancellationToken.None));
                Check("appimage: a file that is not an ELF program is refused", err != null && File.ReadAllBytes(target).SequenceEqual(newer) && Directory.GetFileSystemEntries(tmp).Length == 1, err);
            }
            finally { Updater.Downloader = saved; try { Directory.Delete(tmp, true); } catch { } }
        }

        static byte[] UpdMakeTarball(string dir, string version, bool withExe)
        {
            var src = Path.Combine(dir, "src-" + version);
            var top = Path.Combine(src, "Portly-" + version + "-linux-x64");
            Directory.CreateDirectory(top);
            if (withExe)
            {
                File.WriteAllText(Path.Combine(top, "Portly"), "program " + version);
                if (!OS.IsWindows) File.SetUnixFileMode(Path.Combine(top, "Portly"), (UnixFileMode)0x1ED);
            }
            File.WriteAllText(Path.Combine(top, "portly.png"), "icon");
            Directory.CreateDirectory(Path.Combine(top, "lang"));
            File.WriteAllText(Path.Combine(top, "lang", "en.json"), "{}");
            var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
                TarFile.CreateFromDirectory(src, gz, false);
            return ms.ToArray();
        }

        static void UpdTarball()
        {
            var tmp = ToolsTemp();
            var saved = Updater.Downloader;
            try
            {
                var appDir = Path.Combine(tmp, "share", "portly");
                Directory.CreateDirectory(appDir);
                File.WriteAllText(Path.Combine(appDir, "Portly"), "program old");
                File.WriteAllText(Path.Combine(appDir, "old-only.txt"), "x");
                var tgz = UpdMakeTarball(tmp, "2.0.1", true);
                Updater.Downloader = (url, file, p, ct) => { File.WriteAllBytes(file, tgz); return Task.CompletedTask; };
                var u = new AppUpdate { Version = new Version(2, 0, 1) };
                var r = Updater.InstallAsync(u, UpdPlan(UpdateKind.LinuxTarball, appDir, "https://example.com/x", UpdSha(tgz)), new Version(2, 0, 0), null, CancellationToken.None).GetAwaiter().GetResult();
                Check("tarball: the install folder holds the new version", File.ReadAllText(Path.Combine(appDir, "Portly")) == "program 2.0.1" && File.Exists(Path.Combine(appDir, "lang", "en.json")) && !File.Exists(Path.Combine(appDir, "old-only.txt")));
                Check("tarball: the old folder is kept as a hidden backup until the next start", Directory.Exists(Path.Combine(tmp, "share", ".portly.update-old")) && File.Exists(Path.Combine(tmp, "share", ".portly.update-old", "old-only.txt")));
                Check("tarball: the stage folder is gone", !Directory.Exists(Path.Combine(tmp, "share", ".portly.update-new")));
                Check("tarball: starts the new Portly", r.RelaunchFile == Path.Combine(appDir, "Portly"));
                if (!OS.IsWindows) Check("tarball: executable", (File.GetUnixFileMode(Path.Combine(appDir, "Portly")) & UnixFileMode.UserExecute) != 0);

                // a second update: the earlier backup does not block it
                u.Version = new Version(2, 0, 2);
                var tgz2 = UpdMakeTarball(tmp, "2.0.2", true);
                Updater.Downloader = (url, file, p, ct) => { File.WriteAllBytes(file, tgz2); return Task.CompletedTask; };
                Updater.InstallAsync(u, UpdPlan(UpdateKind.LinuxTarball, appDir, "https://example.com/x", UpdSha(tgz2)), new Version(2, 0, 1), null, CancellationToken.None).GetAwaiter().GetResult();
                Check("tarball: a second update works", File.ReadAllText(Path.Combine(appDir, "Portly")) == "program 2.0.2");

                // an archive without Portly: refused, the folder is untouched
                u.Version = new Version(2, 0, 3);
                var bad = UpdMakeTarball(tmp, "2.0.3", false);
                Updater.Downloader = (url, file, p, ct) => { File.WriteAllBytes(file, bad); return Task.CompletedTask; };
                var err = UpdError(() => Updater.InstallAsync(u, UpdPlan(UpdateKind.LinuxTarball, appDir, "https://example.com/x", UpdSha(bad)), new Version(2, 0, 2), null, CancellationToken.None));
                Check("tarball: an archive without the program is refused", err != null && File.ReadAllText(Path.Combine(appDir, "Portly")) == "program 2.0.2" && !Directory.Exists(Path.Combine(tmp, "share", ".portly.update-new")), err);
            }
            finally { Updater.Downloader = saved; try { Directory.Delete(tmp, true); } catch { } }
        }

        static void UpdDeb()
        {
            var tmp = ToolsTemp();
            var saved = Updater.Downloader;
            try
            {
                var notDeb = Encoding.ASCII.GetBytes("<html>not a package</html>");
                Updater.Downloader = (url, file, p, ct) => { File.WriteAllBytes(file, notDeb); return Task.CompletedTask; };
                var u = new AppUpdate { Version = new Version(2, 0, 1) };
                var err = UpdError(() => Updater.InstallAsync(u, UpdPlan(UpdateKind.LinuxDeb, "/opt/portly", "https://example.com/x", UpdSha(notDeb)), new Version(2, 0, 0), null, CancellationToken.None));
                Check("deb: a file that is not a .deb is refused before any password prompt", err != null && err.Contains("not a .deb"), err);
                Check("deb: the root script is a constant (file and hash are arguments)", Updater.DebScript.Contains("\"$1\"") && Updater.DebScript.Contains("echo \"$2 ") && !Updater.DebScript.Contains("example"));

                if (OS.IsLinux)
                {
                    // run the script for real as this user, with stand-ins for apt-get and dpkg
                    var bin = Path.Combine(tmp, "bin");
                    Directory.CreateDirectory(bin);
                    var log = Path.Combine(tmp, "apt.log");
                    File.WriteAllText(Path.Combine(bin, "apt-get"), "#!/bin/sh\necho \"$@\" >> '" + log + "'\n");
                    File.SetUnixFileMode(Path.Combine(bin, "apt-get"), (UnixFileMode)0x1ED);
                    var deb = Path.Combine(tmp, "portly.deb");
                    File.WriteAllBytes(deb, Encoding.ASCII.GetBytes("!<arch>\npayload"));
                    Func<string, int> sh = sha =>
                    {
                        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                        foreach (var a in new[] { "-c", Updater.DebScript, "sh", deb, sha }) psi.ArgumentList.Add(a);
                        psi.Environment["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH");
                        using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
                    };
                    Check("deb script: a wrong sha256 installs nothing", sh(UpdHash) != 0 && !File.Exists(log));
                    Check("deb script: the right sha256 installs the verified copy", sh(UpdSha(deb)) == 0 && File.Exists(log) && File.ReadAllText(log).Contains("install -y") && File.ReadAllText(log).Contains("portly.deb"));
                }
            }
            finally { Updater.Downloader = saved; try { Directory.Delete(tmp, true); } catch { } }
        }

        static void UpdMac()
        {
            var tmp = ToolsTemp();
            var saved = Updater.Downloader;
            try
            {
                Func<string, string, string> run = (exe, args) =>
                {
                    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                    foreach (var a in args.Split(' ')) psi.ArgumentList.Add(a);
                    using (var p = Process.Start(psi)) { var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); return p.ExitCode == 0 ? "" : exe + " " + args + ": " + o; }
                };
                // a fake Portly.app: a real signed program (copy of /usr/bin/true), the plist the package script writes, an ad-hoc signature
                Func<string, string, bool, string> makeApp = (dir, version, sign) =>
                {
                    var app = Path.Combine(dir, "Portly.app");
                    Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
                    File.Copy("/usr/bin/true", Path.Combine(app, "Contents", "MacOS", "Portly"), true);
                    File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n<dict>\n  <key>CFBundleIdentifier</key><string>net.erdemsoftware.portly</string>\n  <key>CFBundleExecutable</key><string>Portly</string>\n  <key>CFBundleShortVersionString</key><string>" + version + "</string>\n</dict>\n</plist>\n");
                    var err = run("/usr/bin/codesign", "--remove-signature " + Path.Combine(app, "Contents", "MacOS", "Portly"));
                    if (sign) err += run("/usr/bin/codesign", "--force --deep --sign - " + app);
                    if (err != "") throw new Exception(err);
                    return app;
                };
                Func<string, string, string> zipOf = (app, zip) => { var e = run("/usr/bin/ditto", "-c -k --keepParent " + app + " " + zip); if (e != "") throw new Exception(e); return zip; };

                var installDir = Path.Combine(tmp, "Applications");
                var bundle = makeApp(installDir, "2.0.0", true);
                File.WriteAllText(Path.Combine(bundle, "OLD.txt"), "old");
                var u = new AppUpdate { Version = new Version(2, 0, 1) };

                var signedZip = zipOf(makeApp(Path.Combine(tmp, "new-signed"), "2.0.1", true), Path.Combine(tmp, "signed.zip"));
                var unsignedZip = zipOf(makeApp(Path.Combine(tmp, "new-unsigned"), "2.0.1", false), Path.Combine(tmp, "unsigned.zip"));
                var wrongVersionZip = zipOf(makeApp(Path.Combine(tmp, "new-wrong"), "9.9.9", true), Path.Combine(tmp, "wrong.zip"));
                Func<string, string> install = zip =>
                {
                    Updater.Downloader = (url, file, p, ct) => { File.Copy(zip, file, true); return Task.CompletedTask; };
                    return UpdError(() => Updater.InstallAsync(u, UpdPlan(UpdateKind.MacApp, bundle, "https://example.com/x", UpdSha(zip)), new Version(2, 0, 0), null, CancellationToken.None));
                };

                var err1 = install(unsignedZip);
                Check("macOS: a bundle without a valid code signature is refused", err1 != null && File.Exists(Path.Combine(bundle, "OLD.txt")), err1);
                var err2 = install(wrongVersionZip);
                Check("macOS: a bundle with another version than the manifest is refused", err2 != null && File.Exists(Path.Combine(bundle, "OLD.txt")), err2);
                Check("macOS: refusals leave nothing next to the app", Directory.GetFileSystemEntries(installDir).Length == 1);
                var ok = install(signedZip);
                Check("macOS: the signed bundle is swapped in", ok == null && !File.Exists(Path.Combine(bundle, "OLD.txt")) && File.Exists(Path.Combine(bundle, "Contents", "MacOS", "Portly")), ok);
                Check("macOS: the old bundle is kept as a hidden backup", File.Exists(Path.Combine(installDir, ".Portly.app.update-old", "OLD.txt")));
                Check("macOS: the new bundle still verifies", run("/usr/bin/codesign", "--verify --deep --strict " + bundle) == "");
            }
            finally { Updater.Downloader = saved; try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
