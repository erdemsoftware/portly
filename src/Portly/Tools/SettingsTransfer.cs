using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Portly.Core;
using Portly.Packages;

namespace Portly.Tools
{
    /// <summary>
    /// Move a Portly setup to another computer: one .zip with the settings, per-site options, own templates and
    /// the list of installed packages. Projects and databases are not inside (use the folder / database backup).
    /// Machine-bound secrets (the DPAPI-protected MySQL password, the local CA key) are never exported.
    ///
    /// The format is meant to stay readable by future macOS / Linux editions: plain UTF-8 ini files, "/" in the
    /// zip, packages described by type + version (folder names are Windows builds), the projects folder also kept
    /// relative to the user's home ("~/..."), and a manifest that says which platform wrote the file.
    /// </summary>
    public static class SettingsTransfer
    {
        const int Format = 1;
        const string ManifestEntry = "manifest.ini";
        const string SettingsEntry = "portly.ini";
        const string SitesEntry = "sites.ini";
        const string PackagesEntry = "packages.ini";
        const string TemplatesDir = "templates/";
        const string Platform = "windows";

        /// <summary>Keys that only make sense on this computer.</summary>
        static readonly string[][] Skip =
        {
            new[] { "mysql", "RootPassword" }, new[] { "mysql", "LastAutoBackup" },
            new[] { "general", "LastUpdateCheck" }, new[] { "general", "HealthShown" }, new[] { "general", "SkippedVersion" },
            new[] { "general", "DocumentRootHome" },
        };

        /// <summary>Keys that only make sense on the platform that wrote them (Windows terminal, PATH, autostart ...).</summary>
        static readonly string[][] PlatformOnly =
        {
            new[] { "general", "Terminal" }, new[] { "general", "AddToPath" }, new[] { "general", "RunAtStartup" },
            new[] { "general", "DocumentRoot" },
        };

        static bool In(string[][] list, string section, string key)
        {
            return list.Any(s => s[0].Equals(section, StringComparison.OrdinalIgnoreCase) && s[1].Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }

        /// <summary>C:\Users\ali\Sites\www -> ~/Sites/www (null when the folder is not under the home folder).</summary>
        static string ToHome(string path)
        {
            var home = Home.TrimEnd('\\');
            if (!path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            return "~/" + path.Substring(home.Length + 1).Replace('\\', '/');
        }

        static string FromHome(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("~/")) return null;
            return Path.Combine(Home, value.Substring(2).Replace('/', '\\'));
        }

        public static void Export(string zipPath)
        {
            Settings.Current.Save();
            var tmp = Path.Combine(Paths.Tmp, "export-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                var manifest = new Ini(Path.Combine(tmp, ManifestEntry));
                manifest.Set("portly", "format", Format);
                manifest.Set("portly", "platform", Platform);
                manifest.Set("portly", "app", AppInfo.VersionText);
                manifest.Set("portly", "created", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                manifest.Save();

                // settings without the machine-bound keys
                var ini = new Ini(Path.Combine(tmp, SettingsEntry));
                var src = Settings.Current.Raw;
                foreach (var section in src.Sections())
                    foreach (var kv in src.Section(section))
                        if (!In(Skip, section, kv.Key)) ini.Set(section, kv.Key, kv.Value);
                var home = ToHome(Settings.Current.DocumentRoot);
                if (home != null) ini.Set("general", "DocumentRootHome", home);
                ini.Save();

                // installed packages: folder names (exact match on Windows) and versions (any platform), same order
                var pk = new Ini(Path.Combine(tmp, PackagesEntry));
                foreach (var t in PackageTypes.All)
                {
                    var installed = PackageTypes.Installed(t.Id);
                    if (installed.Count == 0) continue;
                    var active = PackageTypes.ActiveFolder(t.Id);
                    pk.Set(t.Id, "installed", string.Join(",", installed));
                    pk.Set(t.Id, "versions", string.Join(",", installed.Select(f => VersionOf(t.Id, f))));
                    pk.Set(t.Id, "active", active);
                    pk.Set(t.Id, "activeVersion", VersionOf(t.Id, active));
                }
                pk.Save();

                if (File.Exists(zipPath)) File.Delete(zipPath);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.UTF8))
                {
                    foreach (var name in new[] { ManifestEntry, SettingsEntry, PackagesEntry })
                        zip.CreateEntryFromFile(Path.Combine(tmp, name), name);
                    if (File.Exists(Paths.SitesFile)) zip.CreateEntryFromFile(Paths.SitesFile, SitesEntry);
                    if (Directory.Exists(Paths.UserTemplates))
                        foreach (var f in Directory.GetFiles(Paths.UserTemplates, "*", SearchOption.AllDirectories))
                            zip.CreateEntryFromFile(f, TemplatesDir + f.Substring(Paths.UserTemplates.Length + 1).Replace('\\', '/'));
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        static string VersionOf(string type, string folder)
        {
            var e = Catalog.FindByFolder(type, folder);
            return e != null ? e.Version : PackageTypes.Label(folder);
        }

        public class ImportResult
        {
            /// <summary>Catalog entries that were installed on the old computer but are missing here.</summary>
            public List<PackageEntry> Missing = new List<PackageEntry>();
            /// <summary>Packages that were added by hand there (not in the catalog): name + version.</summary>
            public List<string> Manual = new List<string>();
            public bool DocumentRootKept;
        }

        public static ImportResult Import(string zipPath)
        {
            var result = new ImportResult();
            var tmp = Path.Combine(Paths.Tmp, "import-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    if (zip.GetEntry(SettingsEntry) == null) throw new Exception(I18n.T("transfer.notPortly"));
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.EndsWith("/")) continue;
                        // only the known files; never write outside the temp folder
                        bool known = e.FullName == ManifestEntry || e.FullName == SettingsEntry || e.FullName == SitesEntry || e.FullName == PackagesEntry ||
                                     (e.FullName.StartsWith(TemplatesDir) && !e.FullName.Contains("..") && !e.FullName.Contains(":"));
                        if (!known) continue;
                        var dest = Path.GetFullPath(Path.Combine(tmp, e.FullName.Replace('/', '\\')));
                        if (!dest.StartsWith(tmp + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
                }

                // files from the first builds have no manifest: they were written on Windows
                var manifest = new Ini(Path.Combine(tmp, ManifestEntry));
                bool samePlatform = manifest.Get("portly", "platform", Platform) == Platform;

                // settings: copy everything except machine-bound keys (and other-platform keys)
                var ini = new Ini(Path.Combine(tmp, SettingsEntry));
                var dst = Settings.Current.Raw;
                foreach (var section in ini.Sections())
                    foreach (var kv in ini.Section(section))
                    {
                        if (In(Skip, section, kv.Key)) continue;
                        if (!samePlatform && In(PlatformOnly, section, kv.Key)) continue;
                        if (section.Equals("general", StringComparison.OrdinalIgnoreCase) && kv.Key.Equals("DocumentRoot", StringComparison.OrdinalIgnoreCase)) continue;
                        dst.Set(section, kv.Key, kv.Value);
                    }
                // the projects folder: the same path, else the same place under this user's home, else keep ours
                var docRoot = samePlatform ? ini.Get("general", "DocumentRoot") : "";
                if (docRoot == "" || !Directory.Exists(docRoot)) docRoot = FromHome(ini.Get("general", "DocumentRootHome"));
                if (docRoot != null && Directory.Exists(docRoot)) dst.Set("general", "DocumentRoot", docRoot);
                else result.DocumentRootKept = true;
                Settings.Current.Save();

                var sites = Path.Combine(tmp, SitesEntry);
                if (File.Exists(sites)) File.Copy(sites, Paths.SitesFile, true);
                var templates = Path.Combine(tmp, "templates");
                if (Directory.Exists(templates))
                    foreach (var f in Directory.GetFiles(templates, "*", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(Paths.UserTemplates, f.Substring(templates.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(f, dest, true);
                    }

                // packages: what is missing here (by folder, else by version, else a newer build of the branch)
                var pkFile = Path.Combine(tmp, PackagesEntry);
                if (File.Exists(pkFile))
                {
                    var pk = new Ini(pkFile);
                    foreach (var type in pk.Sections())
                    {
                        if (PackageTypes.All.All(t => t.Id != type)) continue;
                        var have = PackageTypes.Installed(type);
                        var folders = pk.Get(type, "installed").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        var versions = pk.Get(type, "versions").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < Math.Max(folders.Length, versions.Length); i++)
                        {
                            var folder = i < folders.Length ? folders[i] : "";
                            var version = i < versions.Length ? versions[i] : PackageTypes.Label(folder);
                            if (folder != "" && have.Contains(folder, StringComparer.OrdinalIgnoreCase)) continue;
                            var entry = (samePlatform && folder != "" ? Catalog.FindByFolder(type, folder) : null)
                                        ?? Catalog.ForType(type).FirstOrDefault(e => e.Version == version)
                                        ?? (folder != "" ? Catalog.UpdateFor(type, folder) : null);
                            if (entry != null && have.Contains(entry.Folder, StringComparer.OrdinalIgnoreCase)) continue;
                            if (entry != null) { if (!result.Missing.Contains(entry)) result.Missing.Add(entry); }
                            else result.Manual.Add(PackageTypes.Get(type).Name + " " + version);
                        }
                        // the version that was active there becomes active here once it is installed
                        var activeVersion = pk.Get(type, "activeVersion");
                        var activeEntry = Catalog.ForType(type).FirstOrDefault(e => e.Version == activeVersion);
                        if (activeEntry != null) Settings.Current.SetVersion(type, activeEntry.Folder);
                    }
                    Settings.Current.Save();
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
            return result;
        }
    }
}
