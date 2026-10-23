using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>What a portly.yml asks for (schema v1, see docs/04-kullanim.md).</summary>
    public class ProjectSpec
    {
        public string Php, Node, Root;
        public List<string> Domains = new List<string>();
        public List<string> Services = new List<string>();
        public string DbName, DbCollation;
        public List<JobDef> Jobs = new List<JobDef>();
        public Dictionary<string, string> Hooks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Ignored keys and values, shown in the apply window.</summary>
        public List<string> Warnings = new List<string>();
        public bool HasCommands { get { return Jobs.Count > 0 || Hooks.Count > 0; } }
    }

    /// <summary>One line of the apply preview.</summary>
    public class ApplyStep
    {
        public string Text;
        /// <summary>Runs a command from the file: needs the explicit "Allow commands" tick.</summary>
        public bool IsCommand;
        public bool Warning;
    }

    /// <summary>
    /// Feature "projectFile": portly.yml in the project root sets the project up for the whole team (like herd.yml).
    /// The format is platform neutral so future macOS / Linux editions can read the same file.
    /// Portly never applies it silently: the site card shows a notice, the apply window lists every change,
    /// and commands (jobs, hooks) need their own tick. The applied file's hash is kept in usr\sites.ini.
    ///
    /// Schema v1 (all keys optional):
    ///   php: "8.3" | "^8.2" | ">=8.1 &lt;8.4" | "8.3.*"     node: "22"       root: public
    ///   domains: [shop.test, "*.shop.portly"]           services: [mysql, redis, mailpit]
    ///   database: { name: shop, collation: utf8mb4_turkish_ci }   (or database: shop)
    ///   jobs: [ { name: queue, command: "php artisan queue:work", autostart: true, restart: true, schedule: false } ]
    ///   hooks: { before-start: "...", after-start: "...", before-stop: "...", after-stop: "..." }
    /// </summary>
    public static class ProjectFile
    {
        public const string FileName = "portly.yml";
        static readonly string[] Known = { "version", "php", "node", "root", "domains", "services", "database", "jobs", "hooks" };
        static readonly Regex HostRx = new Regex(@"^(\*\.)?[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$");
        static readonly Regex CollationRx = new Regex(@"^[a-z0-9]+_[a-z0-9_]+$");

        public static string Find(Site site)
        {
            if (site == null || site.Folder == null) return null;
            foreach (var n in new[] { FileName, "portly.yaml" })
            {
                var f = Path.Combine(site.Folder, n);
                if (File.Exists(f)) return f;
            }
            return null;
        }

        public static string Hash(string file)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = File.ReadAllBytes(file);
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
        }

        /// <summary>The site has a portly.yml that was not applied in this version yet.</summary>
        public static bool IsPending(Site site)
        {
            if (!Features.IsOn("projectFile") || site == null || !site.Valid) return false;
            var f = Find(site);
            if (f == null) return false;
            try { return Hash(f) != new Ini(Paths.SitesFile).Get(site.Name, "projectfile", ""); }
            catch { return false; }
        }

        public static void MarkApplied(Site site, string file)
        {
            var ini = new Ini(Paths.SitesFile);
            ini.Set(site.Name, "projectfile", Hash(file));
            ini.Save();
        }

        // =====================================================================
        // Reading
        // =====================================================================
        /// <summary>Throws YamlException (with line) for syntax errors, FormatException for wrong values.</summary>
        public static ProjectSpec Load(string file) { return FromText(File.ReadAllText(file, Encoding.UTF8)); }

        public static ProjectSpec FromText(string text)
        {
            var map = Yaml.ParseMap(text);
            var spec = new ProjectSpec();
            foreach (var k in map.Keys.Where(k => !Known.Contains(k))) spec.Warnings.Add(I18n.T("pf.unknownKey", k));
            object v;
            if (map.TryGetValue("version", out v) && v != null && Yaml.Str(v) != "1") throw new FormatException(I18n.T("pf.badVersion", Yaml.Str(v)));
            if (map.TryGetValue("php", out v) && v != null)
            {
                spec.Php = Scalar(v, "php");
                if (!Regex.IsMatch(spec.Php, @"^[0-9.*^~<>=|, x]+$")) throw new FormatException(I18n.T("pf.badValue", "php", spec.Php));
            }
            if (map.TryGetValue("node", out v) && v != null) spec.Node = Scalar(v, "node");
            if (map.TryGetValue("root", out v) && v != null)
            {
                spec.Root = Scalar(v, "root").Replace('/', '\\').Trim('\\');
                if (spec.Root.Contains("..") || Path.IsPathRooted(spec.Root) || spec.Root.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    throw new FormatException(I18n.T("pf.badValue", "root", spec.Root));
            }
            if (map.TryGetValue("domains", out v) && v != null)
                foreach (var d in Strings(v, "domains"))
                {
                    var h = d.Trim().ToLowerInvariant();
                    if (HostRx.IsMatch(h)) spec.Domains.Add(h);
                    else spec.Warnings.Add(I18n.T("pf.badDomain", d));
                }
            if (map.TryGetValue("services", out v) && v != null)
                spec.Services.AddRange(Strings(v, "services").Select(x => x.Trim().ToLowerInvariant()));
            if (map.TryGetValue("database", out v) && v != null)
            {
                var dm = v as Dictionary<string, object>;
                if (dm == null) spec.DbName = Scalar(v, "database");
                else
                {
                    object n, c;
                    if (dm.TryGetValue("name", out n)) spec.DbName = Scalar(n, "database.name");
                    if (dm.TryGetValue("collation", out c) && c != null) spec.DbCollation = Scalar(c, "database.collation").ToLowerInvariant();
                }
                if (spec.DbName != null && !Database.IsValidName(spec.DbName)) throw new FormatException(I18n.T("pf.badValue", "database.name", spec.DbName));
                if (spec.DbCollation != null && !CollationRx.IsMatch(spec.DbCollation)) throw new FormatException(I18n.T("pf.badValue", "database.collation", spec.DbCollation));
            }
            if (map.TryGetValue("jobs", out v) && v != null)
            {
                var list = v as List<object>;
                if (list == null) throw new FormatException(I18n.T("pf.expectedList", "jobs"));
                foreach (var item in list)
                {
                    var jm = item as Dictionary<string, object>;
                    if (jm == null) throw new FormatException(I18n.T("pf.badValue", "jobs", Yaml.Str(item) ?? "?"));
                    object n, c, a, r, sch;
                    var def = new JobDef { Restart = true };
                    def.Name = jm.TryGetValue("name", out n) ? (Yaml.Str(n) ?? "").ToLowerInvariant() : "";
                    def.Command = jm.TryGetValue("command", out c) ? Yaml.Str(c) ?? "" : "";
                    if (!Jobs.NameRx.IsMatch(def.Name)) throw new FormatException(I18n.T("pf.badValue", "jobs.name", def.Name));
                    if (def.Command.Trim() == "") throw new FormatException(I18n.T("pf.badValue", "jobs." + def.Name + ".command", ""));
                    if (jm.TryGetValue("autostart", out a)) def.Autostart = Bool(a, "autostart");
                    if (jm.TryGetValue("restart", out r)) def.Restart = Bool(r, "restart");
                    if (jm.TryGetValue("schedule", out sch)) def.Schedule = Bool(sch, "schedule");
                    if (spec.Jobs.Any(j => j.Name == def.Name)) throw new FormatException(I18n.T("pf.badValue", "jobs.name", def.Name));
                    spec.Jobs.Add(def);
                }
            }
            if (map.TryGetValue("hooks", out v) && v != null)
            {
                var hm = v as Dictionary<string, object>;
                if (hm == null) throw new FormatException(I18n.T("pf.expectedMap", "hooks"));
                foreach (var kv in hm)
                {
                    if (!Tools.Hooks.IsEvent(kv.Key)) { spec.Warnings.Add(I18n.T("pf.unknownKey", "hooks." + kv.Key)); continue; }
                    var cmd = Yaml.Str(kv.Value);
                    if (!string.IsNullOrWhiteSpace(cmd)) spec.Hooks[kv.Key] = cmd.Trim();
                }
            }
            return spec;
        }

        static string Scalar(object v, string key)
        {
            var s = Yaml.Str(v);
            if (s == null) throw new FormatException(I18n.T("pf.badValue", key, "?"));
            return s.Trim();
        }

        static bool Bool(object v, string key)
        {
            if (v is bool) return (bool)v;
            throw new FormatException(I18n.T("pf.badValue", key, Yaml.Str(v) ?? "?"));
        }

        static List<string> Strings(object v, string key)
        {
            var list = v as List<object>;
            if (list == null)
            {
                var one = Yaml.Str(v);
                if (one == null) throw new FormatException(I18n.T("pf.expectedList", key));
                return new List<string> { one };
            }
            return list.Select(x => Scalar(x, key)).ToList();
        }

        // =====================================================================
        // Versions
        // =====================================================================
        /// <summary>Composer-style constraint: "8.3", "8.3.*", "8", "^8.2", "~8.2", "&gt;=8.1 &lt;8.4", "8.2 || 8.3".</summary>
        public static bool Satisfies(Version v, string constraint)
        {
            if (string.IsNullOrWhiteSpace(constraint)) return true;
            foreach (var alt in constraint.Split(new[] { "||" }, StringSplitOptions.None))
            {
                var parts = alt.Replace(",", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts.All(p => One(v, p))) return true;
            }
            return false;
        }

        static bool One(Version v, string c)
        {
            string op = "";
            foreach (var o in new[] { ">=", "<=", "^", "~", ">", "<", "=" })
                if (c.StartsWith(o)) { op = o; c = c.Substring(o.Length); break; }
            var nums = c.Split('.').Where(x => x != "*" && x != "x").ToList();
            if (nums.Count == 0) return true;
            var n = new int[3];
            for (int i = 0; i < Math.Min(3, nums.Count); i++) if (!int.TryParse(nums[i], out n[i])) return false;
            var want = new Version(n[0], n[1], n[2]);
            var have = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            switch (op)
            {
                case ">=": return have >= want;
                case ">": return have > want;
                case "<=": return have <= want;
                case "<": return have < want;
                case "^": return have >= want && have.Major == want.Major;
                case "~": return have >= want && (nums.Count >= 3 ? have.Major == want.Major && have.Minor == want.Minor : have.Major == want.Major);
                default:
                    // "8", "8.3", "8.3.4": every given part must match
                    if (have.Major != n[0]) return false;
                    if (nums.Count >= 2 && have.Minor != n[1]) return false;
                    if (nums.Count >= 3 && have.Build != n[2]) return false;
                    return true;
            }
        }

        public static string PickInstalledPhp(string constraint)
        {
            return PackageTypes.Installed("php").FirstOrDefault(f => Satisfies(PackageTypes.ParseVersion(f), constraint));
        }

        public static PackageEntry PickCatalogPhp(string constraint)
        {
            return Catalog.ForType("php").FirstOrDefault(e => Satisfies(PackageTypes.ParseVersion(e.Version), constraint));
        }

        static string ServiceId(string name)
        {
            switch (name)
            {
                case "mysql": case "mariadb": case "db": return "mysql";
                case "postgres": case "postgresql": case "pgsql": return "postgresql";
                case "mongo": case "mongodb": return "mongodb";
                case "redis": return "redis";
                case "memcached": return "memcached";
                case "mailpit": case "mail": case "mailhog": case "smtp4dev": return "mailpit";
                case "apache": case "nginx": case "web": return "web";
                default: return null;
            }
        }

        static Service ServiceOf(string id)
        {
            return ServiceManager.Rows.FirstOrDefault(s => s.Id == id);
        }

        // =====================================================================
        // Preview and apply
        // =====================================================================
        public static List<ApplyStep> Plan(Site site, ProjectSpec spec)
        {
            var steps = new List<ApplyStep>();
            Action<string, bool> add = (t, w) => steps.Add(new ApplyStep { Text = t, Warning = w });
            if (spec.Php != null)
            {
                var have = PickInstalledPhp(spec.Php);
                if (have != null) add(I18n.T("pf.step.php", spec.Php, PackageTypes.Label(have)), false);
                else
                {
                    var e = PickCatalogPhp(spec.Php);
                    if (e != null) add(I18n.T("pf.step.phpInstall", spec.Php, e.Version), false);
                    else add(I18n.T("pf.step.phpMissing", spec.Php), true);
                }
            }
            if (spec.Node != null)
            {
                var active = PackageTypes.ActiveFolder("nodejs");
                bool ok = active != "" && Satisfies(PackageTypes.ParseVersion(active), spec.Node);
                add(ok ? I18n.T("pf.step.nodeOk", spec.Node, PackageTypes.Label(active)) : I18n.T("pf.step.nodeOther", spec.Node, active == "" ? "-" : PackageTypes.Label(active)), !ok);
            }
            if (spec.Root != null)
            {
                bool exists = Directory.Exists(Path.Combine(site.Folder, spec.Root));
                add(exists ? I18n.T("pf.step.root", spec.Root) : I18n.T("pf.step.rootMissing", spec.Root), !exists);
            }
            var domains = spec.Domains.Where(d => d != site.Host).ToList();
            if (domains.Count > 0)
                add(Features.IsOn("aliases") ? I18n.T("pf.step.domains", string.Join(", ", domains)) : I18n.T("pf.step.domainsOff", string.Join(", ", domains)), !Features.IsOn("aliases"));
            foreach (var name in spec.Services)
            {
                var id = ServiceId(name);
                if (id == null) { add(I18n.T("pf.step.serviceUnknown", name), true); continue; }
                var svc = ServiceOf(id);
                var title = svc != null ? svc.Title : name;
                if (svc != null && !svc.IsInstalled) add(I18n.T("pf.step.serviceMissing", title), true);
                else add(I18n.T("pf.step.service", title), false);
                if (name == "mariadb" && Settings.Current.DbEngine != "mariadb" || name == "mysql" && Settings.Current.DbEngine != "mysql")
                    add(I18n.T("pf.step.engine", name, Settings.Current.DbEngine), true);
            }
            if (spec.DbName != null)
                add(spec.DbCollation != null ? I18n.T("pf.step.dbCollation", spec.DbName, spec.DbCollation) : I18n.T("pf.step.db", spec.DbName), false);
            foreach (var j in spec.Jobs)
                steps.Add(new ApplyStep { IsCommand = true, Text = I18n.T(j.Schedule ? "pf.step.jobSchedule" : "pf.step.job", j.Name, j.Command) + (j.Autostart ? " " + I18n.T("pf.step.autostart") : "") });
            foreach (var h in spec.Hooks)
                steps.Add(new ApplyStep { IsCommand = true, Text = I18n.T("pf.step.hook", h.Key, h.Value) });
            foreach (var w in spec.Warnings) add(w, true);
            return steps;
        }

        /// <summary>Applies the file. Commands (jobs, hooks) only when allowCommands. Returns what happened, line by line.</summary>
        public static async Task<List<string>> ApplyAsync(Site site, string file, ProjectSpec spec, bool allowCommands, Action<string> status)
        {
            var done = new List<string>();
            var ini = new Ini(Paths.SitesFile);
            status = status ?? (s => { });

            if (spec.Php != null)
            {
                var folder = PickInstalledPhp(spec.Php);
                if (folder == null)
                {
                    var e = PickCatalogPhp(spec.Php);
                    if (e != null)
                    {
                        status(I18n.T("log.downloading", "PHP " + e.Version));
                        try { await Installer.InstallAsync(e, null, CancellationToken.None); folder = PickInstalledPhp(spec.Php); }
                        catch (Exception ex) { done.Add("✗ PHP " + e.Version + ": " + ex.Message); }
                    }
                }
                if (folder != null)
                {
                    Sites.SetPhp(site.Name, folder);
                    done.Add("✓ PHP " + PackageTypes.Label(folder));
                }
                else done.Add("✗ " + I18n.T("pf.step.phpMissing", spec.Php));
                ini = new Ini(Paths.SitesFile);
            }
            if (spec.Node != null) { ini.Set(site.Name, "node", spec.Node); ini.Save(); }
            if (spec.Root != null && Directory.Exists(Path.Combine(site.Folder, spec.Root)))
            {
                ini.Set(site.Name, "root", spec.Root);
                ini.Save();
                done.Add("✓ " + I18n.T("pf.step.root", spec.Root));
            }
            var domains = spec.Domains.Where(d => d != site.Host).ToList();
            if (domains.Count > 0 && Features.IsOn("aliases"))
            {
                Sites.SetAliases(site.Name, site.Aliases.Concat(domains));
                done.Add("✓ " + I18n.T("pf.step.domains", string.Join(", ", domains)));
            }
            var started = new List<Service>();
            foreach (var name in spec.Services)
            {
                var id = ServiceId(name);
                if (id == null) continue;
                Settings.Current.SetShown(id, true);
                Settings.Current.SetEnabled(id, true);
                var svc = ServiceOf(id);
                if (svc != null && svc.IsInstalled && !svc.IsActive && ServiceManager.AnyActive && id != "web") started.Add(svc);
                done.Add("✓ " + I18n.T("pf.step.service", svc != null ? svc.Title : name));
            }
            Settings.Current.Save();
            foreach (var svc in started) await ServiceManager.StartAsync(svc);
            if (spec.DbName != null)
            {
                if (ServiceManager.MySql.State != ServiceState.Running && ServiceManager.MySql.IsInstalled)
                {
                    status(I18n.T("pf.startingDb"));
                    await ServiceManager.MySql.StartAsync();
                }
                var err = await Task.Run(() => Database.CreateDatabase(spec.DbName));
                if (err == null && spec.DbCollation != null)
                {
                    var charset = spec.DbCollation.Substring(0, spec.DbCollation.IndexOf('_'));
                    var r = await Task.Run(() => Database.Sql("ALTER DATABASE `" + spec.DbName + "` CHARACTER SET " + charset + " COLLATE " + spec.DbCollation + ";"));
                    if (!r.Ok) err = r.Output;
                }
                done.Add(err == null ? "✓ " + I18n.T("pf.step.db", spec.DbName) : "✗ " + spec.DbName + ": " + err);
            }
            if (spec.HasCommands)
            {
                if (allowCommands)
                {
                    foreach (var j in spec.Jobs) { Jobs.Upsert(site.Name, j); done.Add("✓ " + I18n.T("pf.step.job", j.Name, j.Command)); }
                    if (spec.Hooks.Count > 0)
                    {
                        Tools.Hooks.SetSiteHooks(site.Name, spec.Hooks);
                        foreach (var h in spec.Hooks) done.Add("✓ " + I18n.T("pf.step.hook", h.Key, h.Value));
                    }
                    if (spec.Jobs.Count > 0 && !Features.IsOn("jobs")) done.Add("! " + I18n.T("pf.jobsOff"));
                }
                else done.Add("– " + I18n.T("pf.commandsSkipped"));
            }
            MarkApplied(site, file);
            Log.Info(I18n.T("pf.log.applied", site.Name));
            return done;
        }

        // =====================================================================
        // Writing ("Create portly.yml")
        // =====================================================================
        public static string Generate(Site site)
        {
            var sb = new StringBuilder();
            sb.Append("# portly.yml - sets this project up in Portly for everyone on the team.\n");
            sb.Append("# Portly shows every change and asks before applying it; commands (jobs, hooks) need an extra tick.\n");
            sb.Append("version: 1\n");
            if (site.PhpFolder != "") sb.Append("php: \"").Append(PackageTypes.Branch(site.PhpFolder)).Append("\"\n");
            var node = PackageTypes.ActiveFolder("nodejs");
            if (node != "") sb.Append("node: \"").Append(PackageTypes.ParseVersion(node).Major).Append("\"\n");
            if (!string.Equals(site.DocRoot, site.Folder, StringComparison.OrdinalIgnoreCase) && site.DocRoot.StartsWith(site.Folder + "\\", StringComparison.OrdinalIgnoreCase))
                sb.Append("root: ").Append(Yaml.Quote(site.DocRoot.Substring(site.Folder.Length + 1).Replace('\\', '/'))).Append('\n');
            if (site.Aliases.Count > 0) sb.Append("domains: [").Append(string.Join(", ", site.Aliases.Select(Yaml.Quote))).Append("]\n");
            var s = Settings.Current;
            var services = new List<string>();
            if (s.IsEnabled("mysql")) services.Add(s.DbEngine);
            foreach (var id in new[] { "postgresql", "mongodb", "redis", "memcached", "mailpit" }) if (s.IsEnabled(id)) services.Add(id);
            if (services.Count > 0) sb.Append("services: [").Append(string.Join(", ", services)).Append("]\n");
            var db = ProjectCreator.DbNameFor(site.ShortName);
            if (ServiceManager.MySql.State == ServiceState.Running && Database.IsValidName(db))
            {
                var r = Database.Sql("SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='" + db + "';");
                var coll = r.Ok ? r.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => CollationRx.IsMatch(l)) : null;
                if (coll != null) sb.Append("database: { name: ").Append(db).Append(", collation: ").Append(coll).Append(" }\n");
            }
            var jobs = Jobs.Load(site.Name);
            if (jobs.Count > 0)
            {
                sb.Append("jobs:\n");
                foreach (var j in jobs)
                {
                    sb.Append("  - name: ").Append(j.Name).Append('\n');
                    sb.Append("    command: ").Append(Yaml.Quote(j.Command)).Append('\n');
                    if (j.Autostart) sb.Append("    autostart: true\n");
                    if (!j.Restart) sb.Append("    restart: false\n");
                    if (j.Schedule) sb.Append("    schedule: true\n");
                }
            }
            var hooks = Tools.Hooks.SiteHooks(site.Name);
            if (hooks.Count > 0)
            {
                sb.Append("hooks:\n");
                foreach (var h in hooks) sb.Append("  ").Append(h.Key).Append(": ").Append(Yaml.Quote(h.Value)).Append('\n');
            }
            return sb.ToString().Replace("\n", "\r\n");
        }

        /// <summary>Writes portly.yml and marks it applied (it describes the current state).</summary>
        public static string Write(Site site)
        {
            var file = Path.Combine(site.Folder, FileName);
            File.WriteAllText(file, Generate(site), new UTF8Encoding(false));
            MarkApplied(site, file);
            Log.Info(I18n.T("pf.log.created", file));
            return file;
        }
    }
}
