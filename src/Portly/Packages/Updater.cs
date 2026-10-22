using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;

namespace Portly.Packages
{
    public class AppUpdate
    {
        public Version Version;
        public string Url;
        public string Sha256;
        public string Notes;
    }

    /// <summary>
    /// Self update (modern edition only).
    /// manifest/app.json on GitHub describes the latest release; the zip must match its SHA-256.
    /// Only program files are replaced (Portly.exe, lang\, templates\); bin, data, www, usr, etc are never touched.
    /// The running exe is renamed to Portly.exe.old (Windows allows renaming a running exe) and removed on next start.
    /// </summary>
    public static class Updater
    {
        public static AppUpdate Available;
        static readonly string[] Protected = { "bin", "data", "www", "usr", "etc", "tmp", "logs", "backup" };

        public static async Task<AppUpdate> CheckAsync()
        {
            if (!AppInfo.UpdatesSupported) return null;
            var json = await Net.GetStringAsync(AppInfo.ManifestBase + "app.json");
            var o = Json.ParseObject(json);
            var donate = o.Str("donateUrl");
            if (donate.StartsWith("https://")) AppInfo.DonateUrl = donate;
            Version v;
            if (!Version.TryParse(o.Str("version"), out v)) return null;
            var current = new Version(AppInfo.Version.Major, AppInfo.Version.Minor, AppInfo.Version.Build);
            Settings.Current.LastUpdateCheck = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Settings.Current.Save();
            if (v <= current) { Available = null; return null; }
            var notes = o.Obj("notes");
            Available = new AppUpdate
            {
                Version = v,
                Url = o.Str("url"),
                Sha256 = o.Str("sha256"),
                Notes = notes == null ? "" : notes.Str(I18n.Code, notes.Str("en")),
            };
            Log.Info(I18n.T("log.updateAvailable", v));
            return Available;
        }

        public static async Task ApplyAsync(AppUpdate u, Action<InstallProgress> progress, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(u.Sha256) || !u.Url.StartsWith("https://")) throw new Exception(I18n.T("err.updateManifest"));
            var work = Path.Combine(Paths.Tmp, "update");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            var zip = Path.Combine(work, "portly.zip");
            await Net.DownloadFileAsync(u.Url, zip, (p, b) => { if (progress != null) progress(new InstallProgress { Stage = "download", Percent = p, Bytes = b }); }, ct);
            if (!Net.Sha256(zip).Equals(u.Sha256, StringComparison.OrdinalIgnoreCase)) throw new Exception(I18n.T("err.checksum"));
            await Task.Run(() => InstallZip(zip, work), ct);
            Log.Info(I18n.T("log.updated", u.Version));
        }

        /// <summary>Replace the program files with the ones in a verified release zip.</summary>
        public static void InstallZip(string zip, string work)
        {
            var extracted = Path.Combine(work, "new");
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            ZipFile.ExtractToDirectory(zip, extracted);
            var root = extracted;
            if (!File.Exists(Path.Combine(root, "Portly.exe")))
            {
                var sub = Directory.GetDirectories(extracted).FirstOrDefault(d => File.Exists(Path.Combine(d, "Portly.exe")));
                if (sub == null) throw new Exception(I18n.T("err.updateManifest"));
                root = sub;
            }

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(root.Length + 1);
                var first = rel.Split('\\')[0];
                if (Protected.Contains(first, StringComparer.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(Paths.Root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target))
                {
                    var old = target + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                }
                File.Copy(file, target);
            }
        }

        /// <summary>Start the new Portly and let it wait until this process has exited.</summary>
        public static void Restart(bool startServices)
        {
            var args = "--after-update " + Process.GetCurrentProcess().Id + (startServices ? " --start-services" : "");
            Process.Start(new ProcessStartInfo(Paths.Exe, args) { UseShellExecute = false });
        }

        public static void CleanupOldFiles()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Paths.Root, "*.old"))
                    try { File.Delete(f); } catch { }
                foreach (var d in new[] { Paths.Lang, Paths.Templates })
                    if (Directory.Exists(d))
                        foreach (var f in Directory.GetFiles(d, "*.old", SearchOption.AllDirectories))
                            try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
