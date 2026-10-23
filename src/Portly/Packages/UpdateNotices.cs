using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Portly.Core;

namespace Portly.Packages
{
    /// <summary>One "new version" message on the main window.</summary>
    public class UpdateNotice
    {
        public string Type;
        /// <summary>true: a newer branch than any installed one (PHP 8.6 while 8.5 is the newest); false: a newer patch of an installed branch.</summary>
        public bool NewBranch;
        public PackageEntry Entry;
        /// <summary>Patch only: the installed folder that gets replaced.</summary>
        public string OldFolder;

        public string Key { get { return Type + "/" + Entry.Version; } }
        public string TypeName { get { return PackageTypes.Get(Type).Name; } }
        /// <summary>Databases keep their data in a format of their branch: a new branch is only installed, the switch stays manual.</summary>
        public bool InstallOnly { get { return NewBranch && UpdateNotices.DataTypes.Contains(Type); } }
    }

    /// <summary>
    /// New package versions for the types the user has installed (modern edition only).
    /// Computed from the local catalog after every refresh; the refresh runs at the first start of a day
    /// and whenever the user clicks "Check for updates".
    /// </summary>
    public static class UpdateNotices
    {
        public static List<UpdateNotice> Current = new List<UpdateNotice>();
        /// <summary>Types whose data folder needs a migration between branches.</summary>
        public static readonly string[] DataTypes = { "mysql", "mariadb", "postgresql", "mongodb" };
        /// <summary>Tags of builds that are not meant as the next stable step.</summary>
        static readonly string[] NotStableTags = { "mainline", "current", "newest", "preview", "rc", "beta", "alpha", "old", "eol", "dev" };
        static readonly Regex PreRelease = new Regex(@"(alpha|beta|rc\d*|preview|dev|snapshot)", RegexOptions.IgnoreCase);
        // composer "2 (latest)" and the Redis builds use their own numbering (same as Catalog.UpdateFor)
        static readonly string[] NoPatchTypes = { "composer", "redis" };

        /// <summary>Is the daily automatic check due?</summary>
        public static bool DailyCheckDue { get { return Settings.Current.LastPackageCheck != DateTime.Today.ToString("yyyy-MM-dd"); } }

        public static void MarkChecked()
        {
            Settings.Current.LastPackageCheck = DateTime.Today.ToString("yyyy-MM-dd");
            Settings.Current.Save();
        }

        /// <summary>Rebuild Current from the local catalog and the installed folders.</summary>
        public static List<UpdateNotice> Recompute()
        {
            if (!AppInfo.UpdatesSupported) { Current = new List<UpdateNotice>(); return Current; }
            try
            {
                var s = Settings.Current;
                Current = Compute(Catalog.Available().ToList(), PackageTypes.Installed, PackageTypes.ActiveFolder,
                    (t, v) => s.IsSkipped(t, v) || Settings.LaterThisSession.Contains(t + "/" + v));
            }
            catch (Exception ex) { Log.Warn("Update notices: " + ex.Message); }
            return Current;
        }

        /// <summary>
        /// The pure logic (tested with a fake catalog): newer patches of installed branches, and for every type
        /// the newest stable branch above the newest installed one.
        /// </summary>
        public static List<UpdateNotice> Compute(List<PackageEntry> catalog, Func<string, List<string>> installedOf, Func<string, string> activeOf, Func<string, string, bool> hidden)
        {
            var list = new List<UpdateNotice>();
            foreach (var type in catalog.Select(e => e.Type).Distinct())
            {
                var installed = installedOf(type).Where(f => SecurityInfo.ParseLoose(f) != null).ToList();
                if (installed.Count == 0) continue;
                var entries = catalog.Where(e => e.Type == type && SecurityInfo.ParseLoose(e.Version) != null && !IsPreRelease(e)).ToList();
                Func<PackageEntry, bool> isInstalled = e => installed.Contains(e.Folder, StringComparer.OrdinalIgnoreCase)
                    || installed.Any(f => SecurityInfo.Compare(PackageTypes.Label(f), e.Version) == 0);
                var active = activeOf(type) ?? "";

                // (a) newer patch of an installed branch
                if (!NoPatchTypes.Contains(type))
                {
                    foreach (var branch in installed.GroupBy(f => SecurityInfo.BranchKey(type, PackageTypes.Label(f))))
                    {
                        var newestHere = branch.OrderByDescending(f => PackageTypes.Label(f), Comparer<string>.Create(SecurityInfo.Compare)).First();
                        var old = branch.FirstOrDefault(f => string.Equals(f, active, StringComparison.OrdinalIgnoreCase)) ?? newestHere;
                        var best = entries.Where(e => SecurityInfo.BranchKey(type, e.Version) == branch.Key
                                                      && SecurityInfo.Compare(e.Version, PackageTypes.Label(newestHere)) > 0)
                                          .OrderByDescending(e => e.Version, Comparer<string>.Create(SecurityInfo.Compare)).FirstOrDefault();
                        if (best == null || isInstalled(best) || hidden(type, best.Version)) continue;
                        if (list.Any(n => n.Entry == best)) continue;
                        list.Add(new UpdateNotice { Type = type, Entry = best, OldFolder = old });
                    }
                }

                // (b) a newer branch than any installed one
                var top = installed.Select(f => PackageTypes.Label(f)).OrderByDescending(v => v, Comparer<string>.Create(SecurityInfo.Compare)).First();
                var topBranch = SecurityInfo.BranchKey(type, top);
                bool hasLts = entries.Any(e => string.Equals(e.Tag, "LTS", StringComparison.OrdinalIgnoreCase)) || SecurityInfo.Eol.Any(x => x.Type == type && x.Lts);
                var candidate = entries
                    .Where(e => SecurityInfo.Compare(SecurityInfo.BranchKey(type, e.Version), topBranch) > 0)
                    .Where(e => !isInstalled(e))
                    .Where(e => !IsEolBranch(type, e.Version))
                    .Where(e => hasLts
                        ? string.Equals(e.Tag, "LTS", StringComparison.OrdinalIgnoreCase) || SecurityInfo.IsLtsBranch(type, e.Version) || e.Recommended
                        : e.Recommended || !NotStableTags.Contains((e.Tag ?? "").ToLowerInvariant()))
                    .OrderByDescending(e => e.Version, Comparer<string>.Create(SecurityInfo.Compare))
                    .FirstOrDefault();
                if (candidate != null && !hidden(type, candidate.Version))
                    list.Add(new UpdateNotice { Type = type, Entry = candidate, NewBranch = true });
            }
            // new branches first (they are the news), then patches in catalog type order
            return list.OrderBy(n => n.NewBranch ? 0 : 1).ToList();
        }

        static bool IsPreRelease(PackageEntry e)
        {
            return PreRelease.IsMatch(e.Version ?? "") || PreRelease.IsMatch(e.Tag ?? "") && !string.Equals(e.Tag, "LTS", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsEolBranch(string type, string version)
        {
            var e = SecurityInfo.EolOf(type, version);
            return e != null && e.Eol < DateTime.Today;
        }
    }
}
