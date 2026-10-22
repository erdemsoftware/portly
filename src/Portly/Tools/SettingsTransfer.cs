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
    /// </summary>
    public static class SettingsTransfer
    {
        const string SettingsEntry = "portly.ini";
        const string SitesEntry = "sites.ini";
        const string PackagesEntry = "packages.ini";
        const string TemplatesDir = "templates/";

        /// <summary>Keys that only make sense on this computer.</summary>
        static readonly string[][] Skip =
        {
            new[] { "mysql", "RootPassword" }, new[] { "mysql", "LastAutoBackup" },
            new[] { "general", "LastUpdateCheck" }, new[] { "general", "HealthShown" }, new[] { "general", "SkippedVersion" },
        };

        static bool Skipped(string section, string key)
        {
            return Skip.Any(s => s[0].Equals(section, StringComparison.OrdinalIgnoreCase) && s[1].Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        public static void Export(string zipPath)
        {
            Settings.Current.Save();
            var tmp = Path.Combine(Paths.Tmp, "export-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                // settings without the machine-bound keys
                var ini = new Ini(Path.Combine(tmp, SettingsEntry));
                var src = Settings.Current.Raw;
                foreach (var section in src.Sections())
                    foreach (var kv in src.Section(section))
                        if (!Skipped(section, kv.Key)) ini.Set(section, kv.Key, kv.Value);
                ini.Save();

                // installed package versions and the active one of each type
                var pk = new Ini(Path.Combine(tmp, PackagesEntry));
                foreach (var t in PackageTypes.All)
                {
                    var installed = PackageTypes.Installed(t.Id);
                    if (installed.Count == 0) continue;
                    pk.Set(t.Id, "installed", string.Join(",", installed));
                    pk.Set(t.Id, "active", PackageTypes.ActiveFolder(t.Id));
                }
                pk.Save();

                if (File.Exists(zipPath)) File.Delete(zipPath);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.UTF8))
                {
                    zip.CreateEntryFromFile(Path.Combine(tmp, SettingsEntry), SettingsEntry);
                    zip.CreateEntryFromFile(Path.Combine(tmp, PackagesEntry), PackagesEntry);
                    if (File.Exists(Paths.SitesFile)) zip.CreateEntryFromFile(Paths.SitesFile, SitesEntry);
                    if (Directory.Exists(Paths.UserTemplates))
                        foreach (var f in Directory.GetFiles(Paths.UserTemplates, "*", SearchOption.AllDirectories))
                            zip.CreateEntryFromFile(f, TemplatesDir + f.Substring(Paths.UserTemplates.Length + 1).Replace('\\', '/'));
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
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
                        bool known = e.FullName == SettingsEntry || e.FullName == SitesEntry || e.FullName == PackagesEntry ||
                                     (e.FullName.StartsWith(TemplatesDir) && !e.FullName.Contains("..") && !e.FullName.Contains(":"));
                        if (!known) continue;
                        var dest = Path.GetFullPath(Path.Combine(tmp, e.FullName.Replace('/', '\\')));
                        if (!dest.StartsWith(tmp + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
                }

                // settings: copy everything except machine-bound keys; keep this computer's projects folder
                // when the old one does not exist here
                var ini = new Ini(Path.Combine(tmp, SettingsEntry));
                var dst = Settings.Current.Raw;
                foreach (var section in ini.Sections())
                    foreach (var kv in ini.Section(section))
                    {
                        if (Skipped(section, kv.Key)) continue;
                        if (section.Equals("general", StringComparison.OrdinalIgnoreCase) && kv.Key.Equals("DocumentRoot", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(kv.Value))
                        {
                            result.DocumentRootKept = true;
                            continue;
                        }
                        dst.Set(section, kv.Key, kv.Value);
                    }
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

                // packages: what is missing here
                var pkFile = Path.Combine(tmp, PackagesEntry);
                if (File.Exists(pkFile))
                {
                    var pk = new Ini(pkFile);
                    foreach (var type in pk.Sections())
                    {
                        if (PackageTypes.All.All(t => t.Id != type)) continue;
                        var have = PackageTypes.Installed(type);
                        foreach (var folder in pk.Get(type, "installed").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (have.Contains(folder, StringComparer.OrdinalIgnoreCase)) continue;
                            // the same build, or a newer build of the same branch when the catalog moved on
                            var entry = Catalog.FindByFolder(type, folder) ?? Catalog.UpdateFor(type, folder);
                            if (entry != null) result.Missing.Add(entry);
                            else result.Manual.Add(PackageTypes.Get(type).Name + " " + PackageTypes.Label(folder));
                        }
                    }
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
            return result;
        }
    }
}
