#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Packages
{
    /// <summary>What to start once an update is installed (see Updater.Restart).</summary>
    public class UpdateResult
    {
        public UpdateKind Kind;
        public string RelaunchFile;
        public string[] RelaunchArgs;
    }

    /// <summary>
    /// Self update of Portly 2 on every system. The signed app.json lists the release files ("assets"); Plan picks the one
    /// that fits how this Portly is installed, InstallUpdateAsync downloads it, checks its SHA-256 and replaces the program:
    ///   Windows      the zip: Portly.exe, lang, templates (same as 1.x)
    ///   macOS        the zip: ditto, codesign --verify, Portly.app swapped (old one kept until the next start)
    ///   AppImage     the new file next to the old one, then an atomic rename over it
    ///   tar.gz       a sibling folder, then the two folders swap places
    ///   .deb         pkexec (polkit): the file is copied to a root-owned folder, its SHA-256 checked again as root, then apt installs it
    /// The caller then stops its services, calls Updater.Restart and exits.
    /// </summary>
    public static partial class Updater
    {
        public const string MacBundleId = "net.erdemsoftware.portly";
        const string StageSuffix = ".update-new";
        const string BackupSuffix = ".update-old";

        /// <summary>Replaced in tests: downloads a file (url, target, progress, cancellation).</summary>
        internal static Func<string, string, Action<int, long>, CancellationToken, Task> Downloader = (url, file, progress, ct) => Net.DownloadFileAsync(url, file, progress, ct);
        static UpdateResult _installed;

        /// <summary>Can this Portly replace itself where it runs? When not, SelfUpdateBlockedReason says why (translated).</summary>
        public static bool CanSelfUpdate { get { var p = CurrentPlan(null); return p.Kind != UpdateKind.None && p.Reason == null; } }

        public static string SelfUpdateBlockedReason { get { return CurrentPlan(null).Reason; } }

        /// <summary>What the update u would do here. Check CanInstall: Reason says why not, and the UI then opens the releases page instead.</summary>
        public static UpdatePlan Plan(AppUpdate u) { return CurrentPlan(u); }

        static UpdatePlan CurrentPlan(AppUpdate u)
        {
            var plan = Detect(OS.Name, Paths.AppDir, Environment.ProcessPath, Environment.GetEnvironmentVariable("APPIMAGE"), Elevate.Available, Paths.Root);
            return WithAsset(plan, u, OS.Name, OS.Arch);
        }

        internal static UpdatePlan WithAsset(UpdatePlan plan, AppUpdate u, string os, string arch)
        {
            if (u == null || plan.Kind == UpdateKind.None) return plan;
            plan.Asset = UpdateAssets.Select(u, os, arch, plan.Kind);
            if (plan.Asset == null && plan.Reason == null) plan.Reason = I18n.TOr("update.noAsset", "This release has no download for {0}. Get it from the releases page.", os + "-" + arch);
            return plan;
        }

        /// <summary>How the running Portly is installed. Pure apart from checking whether folders are writable.</summary>
        internal static UpdatePlan Detect(string os, string appDir, string processPath, string appImage, bool canElevate, string dataRoot)
        {
            var p = new UpdatePlan();
            appDir = (appDir ?? "").TrimEnd('/', '\\');
            if (os == "windows")
            {
                p.Kind = UpdateKind.WindowsZip;
                p.Target = appDir;
                if (!IsWritableDir(appDir)) p.Reason = NotWritable(appDir);
            }
            else if (os == "macos")
            {
                var bundle = BundleOf(appDir);
                if (bundle == null || !IsRunningFrom(appDir, processPath)) return NotPackaged(p);
                p.Kind = UpdateKind.MacApp;
                p.Target = bundle;
                if (!IsWritableDir(Path.GetDirectoryName(bundle))) p.Reason = NotWritable(Path.GetDirectoryName(bundle));
            }
            else if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage))
            {
                p.Kind = UpdateKind.LinuxAppImage;
                p.Target = appImage;
                if (!IsWritableDir(Path.GetDirectoryName(appImage))) p.Reason = NotWritable(Path.GetDirectoryName(appImage));
            }
            else if (appDir == "/opt/portly" && IsRunningFrom(appDir, processPath))
            {
                p.Kind = UpdateKind.LinuxDeb;
                p.Target = appDir;
                if (!canElevate) p.Reason = I18n.TOr("update.needsPkexec", "This Portly was installed from a .deb package. Updating it needs administrator rights, but pkexec is not available. Download the new .deb from the releases page and install it with: sudo apt install ./<file>.deb");
            }
            else
            {
                if (!IsRunningFrom(appDir, processPath)) return NotPackaged(p);
                p.Kind = UpdateKind.LinuxTarball;
                p.Target = appDir;
                var parent = Path.GetDirectoryName(appDir);
                if (!IsWritableDir(appDir) || string.IsNullOrEmpty(parent) || !IsWritableDir(parent)) p.Reason = NotWritable(appDir);
            }
            // the folder swap would take the data with it
            if (p.Reason == null && p.Kind != UpdateKind.WindowsZip && p.Kind != UpdateKind.LinuxAppImage && !string.IsNullOrEmpty(dataRoot) && IsInside(dataRoot, p.Kind == UpdateKind.MacApp ? p.Target : appDir))
                p.Reason = I18n.TOr("update.rootInside", "Portly's data folder is inside its program folder, so an update would replace it. Move the data folder (PORTLY_HOME) elsewhere.");
            return p;
        }

        static UpdatePlan NotPackaged(UpdatePlan p)
        {
            p.Kind = UpdateKind.None;
            p.Reason = I18n.TOr("update.notInstalled", "This Portly is not a packaged install (it runs from a build folder), so it cannot update itself.");
            return p;
        }

        static string NotWritable(string dir)
        {
            return I18n.TOr("update.notWritable", "Portly cannot replace itself in {0}: no write permission (or a read-only disk image). Move Portly to a folder you own, or install the new version from the releases page.", dir);
        }

        /// <summary>The Portly.app that holds appDir (".../Portly.app/Contents/MacOS"), or null.</summary>
        internal static string BundleOf(string appDir)
        {
            for (var d = appDir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                if (d.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        /// <summary>Is this process the Portly executable of appDir (not "dotnet Portly.dll" from a build folder)?</summary>
        static bool IsRunningFrom(string appDir, string processPath)
        {
            if (string.IsNullOrEmpty(processPath)) return false;
            var dir = Path.GetDirectoryName(processPath);
            return string.Equals(dir, appDir, StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(processPath) == "Portly";
        }

        static bool IsInside(string path, string folder)
        {
            var a = Path.GetFullPath(path).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            var b = Path.GetFullPath(folder).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            return a.StartsWith(b, StringComparison.Ordinal);
        }

        internal static bool IsWritableDir(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                var f = Path.Combine(dir, ".portly-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(f, new byte[0]);
                File.Delete(f);
                return true;
            }
            catch { return false; }
        }

        // ---------------- install ----------------

        /// <summary>
        /// Download the update that fits this installation, verify it and replace the program. Throws (message for the user) on any
        /// problem; the running Portly is then unchanged. Afterwards: stop the services, Updater.Restart(...), exit.
        /// </summary>
        public static async Task<UpdateResult> InstallUpdateAsync(AppUpdate u, Action<InstallProgress> progress, CancellationToken ct)
        {
            EnsureNewer(u, RunningVersion);
            return await InstallAsync(u, CurrentPlan(u), RunningVersion, progress, ct);
        }

        internal static async Task<UpdateResult> InstallAsync(AppUpdate u, UpdatePlan plan, Version current, Action<InstallProgress> progress, CancellationToken ct)
        {
            EnsureNewer(u, current);
            if (plan == null || plan.Kind == UpdateKind.None) throw new Exception(I18n.T("err.updateManifest"));
            if (plan.Reason != null) throw new Exception(plan.Reason);
            if (!UpdateAssets.IsValid(plan.Asset)) throw new Exception(I18n.T("err.updateManifest"));

            var work = Path.Combine(Paths.Tmp, "update");
            DeleteTree(work);
            Directory.CreateDirectory(work);
            string file;
            switch (plan.Kind)
            {
                case UpdateKind.LinuxAppImage: file = StagePath(plan.Target); break; // next to the old file: the final rename must stay on one volume
                case UpdateKind.LinuxDeb: file = Path.Combine(work, "portly.deb"); break;
                case UpdateKind.LinuxTarball: file = Path.Combine(work, "portly.tar.gz"); break;
                default: file = Path.Combine(work, "portly.zip"); break;
            }

            UpdateResult result;
            try
            {
                if (File.Exists(file)) File.Delete(file);
                await Downloader(plan.Asset.Url, file, (pc, b) => { if (progress != null) progress(new InstallProgress { Stage = "download", Percent = pc, Bytes = b }); }, ct);
                if (!Net.Sha256(file).Equals(plan.Asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new Exception(I18n.T("err.checksum"));
                ct.ThrowIfCancellationRequested();
                result = await Task.Run(() => Install(plan, file, u.Version, work), ct);
            }
            catch
            {
                if (plan.Kind == UpdateKind.LinuxAppImage) TryDelete(file);
                throw;
            }
            finally { DeleteTree(work); }
            _installed = result;
            Log.Info(I18n.T("log.updated", u.Version));
            return result;
        }

        static UpdateResult Install(UpdatePlan plan, string file, Version version, string work)
        {
            switch (plan.Kind)
            {
                case UpdateKind.WindowsZip:
                    InstallZip(file, work);
                    return new UpdateResult { Kind = plan.Kind, RelaunchFile = Paths.Exe, RelaunchArgs = new string[0] };
                case UpdateKind.MacApp: return InstallMacZip(file, plan.Target, version);
                case UpdateKind.LinuxAppImage: return InstallAppImage(file, plan.Target);
                case UpdateKind.LinuxTarball: return InstallTarball(file, plan.Target);
                case UpdateKind.LinuxDeb: return InstallDeb(file, plan.Asset.Sha256);
                default: throw new Exception(I18n.T("err.updateManifest"));
            }
        }

        // ---------------- macOS ----------------

        /// <summary>Unpack the zip beside the app, verify the bundle (identifier, version, code signature) and swap it in; the old bundle stays as a hidden backup.</summary>
        internal static UpdateResult InstallMacZip(string zip, string bundle, Version expected)
        {
            var parent = Path.GetDirectoryName(bundle);
            var stage = StagePath(bundle);
            var backup = BackupPath(bundle);
            DeleteTree(stage);
            DeleteTree(backup);
            Directory.CreateDirectory(stage);
            try
            {
                var x = RunTool("ditto", "-x", "-k", zip, stage);
                if (x.Item1 != 0) throw BundleError("ditto: " + x.Item2.Trim());
                var fresh = Directory.GetDirectories(stage, "*.app").FirstOrDefault();
                if (fresh == null) throw BundleError("no .app in the zip");
                var plist = Path.Combine(fresh, "Contents", "Info.plist");
                if (!File.Exists(plist) || !File.Exists(Path.Combine(fresh, "Contents", "MacOS", "Portly"))) throw BundleError("not a Portly app");
                var text = File.ReadAllText(plist, Encoding.UTF8);
                if (PlistString(text, "CFBundleIdentifier") != MacBundleId) throw BundleError("wrong bundle identifier");
                Version v;
                if (expected != null && (!Version.TryParse(PlistString(text, "CFBundleShortVersionString"), out v) || v != expected)) throw BundleError("wrong version");
                var sig = RunTool("codesign", "--verify", "--deep", "--strict", fresh);
                if (sig.Item1 != 0) throw BundleError("codesign: " + sig.Item2.Trim());

                Directory.Move(bundle, backup);
                try { Directory.Move(fresh, bundle); }
                catch
                {
                    if (!Directory.Exists(bundle) && Directory.Exists(backup)) Directory.Move(backup, bundle);
                    throw;
                }
            }
            finally { DeleteTree(stage); }
            return new UpdateResult { Kind = UpdateKind.MacApp, RelaunchFile = "open", RelaunchArgs = new[] { "-n", bundle, "--args" } };
        }

        static string PlistString(string plist, string key)
        {
            var m = Regex.Match(plist, "<key>" + Regex.Escape(key) + @"</key>\s*<string>([^<]*)</string>");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        static Exception BundleError(string what) { return new Exception(I18n.TOr("update.bundleInvalid", "The downloaded update failed the signature or contents check ({0}) and was not installed.", what)); }

        // ---------------- Linux ----------------

        /// <summary>The downloaded file (already hashed) becomes the AppImage: ELF check, executable bit, atomic rename over the old file.</summary>
        internal static UpdateResult InstallAppImage(string staged, string target)
        {
            using (var s = File.OpenRead(staged))
            {
                var magic = new byte[4];
                if (s.Read(magic, 0, 4) != 4 || magic[0] != 0x7F || magic[1] != (byte)'E' || magic[2] != (byte)'L' || magic[3] != (byte)'F') throw BundleError("not an AppImage");
            }
            SetExecutable(staged);
            File.Move(staged, target, true);
            return new UpdateResult { Kind = UpdateKind.LinuxAppImage, RelaunchFile = target, RelaunchArgs = new string[0] };
        }

        /// <summary>Unpack the tar.gz into a sibling folder and swap it with the install folder; the old folder stays as a hidden backup.</summary>
        internal static UpdateResult InstallTarball(string tgz, string appDir)
        {
            var stage = StagePath(appDir);
            var backup = BackupPath(appDir);
            DeleteTree(stage);
            DeleteTree(backup);
            Directory.CreateDirectory(stage);
            try
            {
                using (var f = File.OpenRead(tgz))
                using (var gz = new GZipStream(f, CompressionMode.Decompress))
                    TarFile.ExtractToDirectory(gz, stage, true); // refuses entries outside the folder
                var root = stage;
                if (!File.Exists(Path.Combine(root, "Portly")))
                {
                    var subs = Directory.GetDirectories(stage);
                    root = subs.Length == 1 && File.Exists(Path.Combine(subs[0], "Portly")) ? subs[0] : null;
                }
                if (root == null) throw BundleError("no Portly in the archive");
                SetExecutable(Path.Combine(root, "Portly"));
                Directory.Move(appDir, backup);
                try { Directory.Move(root, appDir); }
                catch
                {
                    if (!Directory.Exists(appDir) && Directory.Exists(backup)) Directory.Move(backup, appDir);
                    throw;
                }
            }
            finally { DeleteTree(stage); }
            return new UpdateResult { Kind = UpdateKind.LinuxTarball, RelaunchFile = Path.Combine(appDir, "Portly"), RelaunchArgs = new string[0] };
        }

        /// <summary>The root script of the .deb install. It is a constant: file and SHA-256 arrive as $1 and $2, never as part of the script text.</summary>
        internal const string DebScript =
            "set -e\n" +
            "d=$(mktemp -d)\n" +
            "trap 'rm -rf \"$d\"' EXIT\n" +
            "install -m 644 -- \"$1\" \"$d/portly.deb\"\n" +
            "echo \"$2  $d/portly.deb\" | sha256sum -c - >/dev/null\n" +
            "chmod 755 \"$d\"\n" +
            "if command -v apt-get >/dev/null 2>&1; then DEBIAN_FRONTEND=noninteractive apt-get install -y \"$d/portly.deb\"; else dpkg -i \"$d/portly.deb\"; fi\n";

        internal static UpdateResult InstallDeb(string deb, string sha256)
        {
            using (var s = File.OpenRead(deb))
            {
                var magic = new byte[8];
                if (s.Read(magic, 0, 8) != 8 || Encoding.ASCII.GetString(magic) != "!<arch>\n") throw BundleError("not a .deb");
            }
            var code = Elevate.Run("/bin/sh", "-c", DebScript, "sh", deb, sha256.ToLowerInvariant());
            if (code == Elevate.Cancelled) throw new Exception(I18n.TOr("update.elevationRefused", "The update was cancelled: administrator rights were not granted."));
            if (code != 0) throw new Exception(I18n.TOr("update.installFailed", "The package manager could not install the update (exit code {0}).", code));
            return new UpdateResult { Kind = UpdateKind.LinuxDeb, RelaunchFile = Paths.Exe, RelaunchArgs = new string[0] };
        }

        // ---------------- restart and leftovers ----------------

        /// <summary>Start the new Portly (told to wait for this process to end). False when nothing was installed through InstallUpdateAsync.</summary>
        static bool RestartUnix(bool startServices)
        {
            var r = _installed;
            if (r == null || string.IsNullOrEmpty(r.RelaunchFile)) return false;
            var psi = new ProcessStartInfo(r.RelaunchFile) { UseShellExecute = false };
            foreach (var a in r.RelaunchArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--after-update");
            psi.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString());
            if (startServices) psi.ArgumentList.Add("--start-services");
            // the new AppImage sets these itself; the old mount must not leak into it
            foreach (var v in new[] { "APPIMAGE", "APPDIR", "ARGV0", "OWD" }) psi.Environment.Remove(v);
            Process.Start(psi);
            return true;
        }

        /// <summary>Remove what an earlier update left behind (backup of the old app, half-unpacked stage).</summary>
        static void CleanupBackups()
        {
            try
            {
                if (OS.IsWindows) return;
                var targets = new List<string>();
                var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
                if (!string.IsNullOrEmpty(appImage)) targets.Add(appImage);
                var bundle = BundleOf(Paths.AppDir);
                if (bundle != null) targets.Add(bundle);
                else if (string.IsNullOrEmpty(appImage)) targets.Add(Paths.AppDir);
                foreach (var t in targets)
                    foreach (var p in new[] { StagePath(t), BackupPath(t) })
                        if (Directory.Exists(p) || File.Exists(p)) { DeleteTree(p); TryDelete(p); }
            }
            catch { }
        }

        static string StagePath(string target) { return Path.Combine(Path.GetDirectoryName(target), "." + Path.GetFileName(target) + StageSuffix); }
        static string BackupPath(string target) { return Path.Combine(Path.GetDirectoryName(target), "." + Path.GetFileName(target) + BackupSuffix); }

        static void SetExecutable(string file)
        {
            if (OS.IsWindows) return;
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        static void DeleteTree(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) { Log.Warn("Could not remove " + dir + ": " + ex.Message); }
        }

        static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        static Tuple<int, string> RunTool(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
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
            catch (Exception ex) { return Tuple.Create(-1, file + ": " + ex.Message); }
        }
    }
}
#endif
