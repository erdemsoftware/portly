using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;

namespace Portly.Tools
{
    public class ProfileFunction
    {
        public string Name;
        public double InclusiveMs, SelfMs;
        public long Calls;
    }

    public class ProfileFile
    {
        public string File, Script, Request;
        public DateTime Time;
        public long Bytes;
    }

    public class ProfileSummary
    {
        public string Script = "";
        public double TotalMs;
        public List<ProfileFunction> Functions = new List<ProfileFunction>();
    }

    /// <summary>
    /// Feature "dumps": a request profiler on top of the existing Xdebug integration.
    /// xdebug.mode gets "profile" with start_with_request=trigger, so only requests with XDEBUG_TRIGGER
    /// (query string, cookie or the browser extension) are profiled; the cachegrind files go to logs\profiles
    /// and Portly shows the slowest functions (inclusive / self time) without an extra program.
    /// </summary>
    public static class Profiler
    {
        public static string Dir { get { return Path.Combine(Paths.Logs, "profiles"); } }
        const string ModeOn = "debug,develop,profile";
        const string ModeOff = "debug,develop";

        public static bool IsOn(string phpDir)
        {
            return phpDir != null && Xdebug.IsEnabled(phpDir) && PhpConfig.Get(phpDir, "xdebug.mode").Contains("profile");
        }

        public static async Task EnableAsync(string phpDir)
        {
            var folder = Path.GetFileName(phpDir);
            if (!Xdebug.IsEnabled(phpDir))
            {
                await Xdebug.EnableAsync(phpDir);
                var by = Settings.Current.ProfilerXdebugBy.Split('|').Where(x => x != "").ToList();
                if (!by.Contains(folder, StringComparer.OrdinalIgnoreCase)) by.Add(folder);
                Settings.Current.ProfilerXdebugBy = string.Join("|", by);
                Settings.Current.Save();
            }
            Directory.CreateDirectory(Dir);
            var ini = PhpConfig.IniPath(phpDir);
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            PhpConfig.Set(lines, "xdebug.mode", ModeOn);
            PhpConfig.Set(lines, "xdebug.start_with_request", "trigger");
            PhpConfig.Set(lines, "xdebug.output_dir", "\"" + Paths.Fwd(Dir) + "\"");
            PhpConfig.Set(lines, "xdebug.profiler_output_name", "cachegrind.out.%t.%R");
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
            Log.Info(I18n.T("profiler.log.on", PackageTypes.Label(folder)));
        }

        public static void Disable(string phpDir)
        {
            var folder = Path.GetFileName(phpDir);
            var ini = PhpConfig.IniPath(phpDir);
            var lines = File.ReadAllLines(ini, Encoding.UTF8).ToList();
            PhpConfig.Set(lines, "xdebug.mode", ModeOff);
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
            // Xdebug slows every request: switch it off again when only the profiler had switched it on
            var by = Settings.Current.ProfilerXdebugBy.Split('|').Where(x => x != "").ToList();
            if (by.RemoveAll(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Xdebug.Disable(phpDir);
                Settings.Current.ProfilerXdebugBy = string.Join("|", by);
                Settings.Current.Save();
            }
            Log.Info(I18n.T("profiler.log.off", PackageTypes.Label(folder)));
        }

        /// <summary>Profiles of a site (by the script path in the file header), newest first.</summary>
        public static List<ProfileFile> List(Site site)
        {
            var list = new List<ProfileFile>();
            if (!Directory.Exists(Dir)) return list;
            var prefix = site == null ? null : site.Folder.TrimEnd('\\') + "\\";
            foreach (var fi in new DirectoryInfo(Dir).GetFiles("cachegrind.out.*").OrderByDescending(f => f.LastWriteTimeUtc).Take(500))
            {
                var script = HeaderCmd(fi.FullName);
                if (prefix != null && !script.Replace('/', '\\').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new ProfileFile { File = fi.FullName, Script = script, Request = RequestFromName(fi.Name), Time = fi.LastWriteTime, Bytes = fi.Length });
            }
            return list;
        }

        /// <summary>"cachegrind.out.1761200000.123._shop_index_php" -> "/shop/index.php" (Xdebug writes %R with _ for / and .).</summary>
        static string RequestFromName(string name)
        {
            var parts = name.Split(new[] { '.' }, 4);
            if (parts.Length < 4) return "";
            var r = parts[3];
            int dot = r.IndexOf('.');
            r = dot >= 0 && r.Substring(0, dot).All(char.IsDigit) ? r.Substring(dot + 1) : r;
            return r.Replace('_', '/');
        }

        static string HeaderCmd(string file)
        {
            try
            {
                using (var r = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                {
                    for (int i = 0; i < 12; i++)
                    {
                        var l = r.ReadLine();
                        if (l == null) break;
                        if (l.StartsWith("cmd:")) return l.Substring(4).Trim();
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Reads a cachegrind file. Cost lines ("line time memory") right after "calls=" are the inclusive cost of
        /// that call and are added to the caller's inclusive time; all other cost lines are the function's own time.
        /// Names may be compressed: "fn=(12) App\Foo::bar" defines id 12, later "fn=(12)" refers to it.
        /// </summary>
        public static ProfileSummary Parse(string file, int top = 40)
        {
            var sum = new ProfileSummary();
            var names = new Dictionary<string, string>();
            var funcs = new Dictionary<string, ProfileFunction>();
            double unitMs = 0.001;           // Xdebug 2: microseconds
            string cur = null, callee = null;
            bool afterCalls = false;
            long callCount = 0;
            double summary = -1;
            using (var r = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            {
                string l;
                while ((l = r.ReadLine()) != null)
                {
                    if (l.Length == 0) continue;
                    char c = l[0];
                    if (char.IsDigit(c) || c == '+' || c == '-' || c == '*')
                    {
                        if (cur == null) continue;
                        var parts = l.Split(' ');
                        double cost;
                        if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out cost)) { afterCalls = false; continue; }
                        var f = Fn(funcs, cur);
                        if (afterCalls)
                        {
                            f.InclusiveMs += cost * unitMs;
                            if (callee != null) Fn(funcs, callee).Calls += callCount;
                            afterCalls = false;
                        }
                        else { f.SelfMs += cost * unitMs; f.InclusiveMs += cost * unitMs; }
                        continue;
                    }
                    afterCalls = false;
                    if (l.StartsWith("fn=")) cur = Name(names, l.Substring(3));
                    else if (l.StartsWith("cfn=")) callee = Name(names, l.Substring(4));
                    else if (l.StartsWith("calls="))
                    {
                        afterCalls = true;
                        var p = l.Substring(6).Split(' ');
                        if (!long.TryParse(p[0], out callCount)) callCount = 1;
                    }
                    else if (l.StartsWith("events:")) unitMs = l.Contains("10ns") ? 0.00001 : 0.001;
                    else if (l.StartsWith("summary:"))
                    {
                        double v;
                        if (double.TryParse(l.Substring(8).Trim().Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) summary = v;
                    }
                    else if (l.StartsWith("cmd:")) sum.Script = l.Substring(4).Trim();
                }
            }
            foreach (var f in funcs.Values) if (f.Calls == 0) f.Calls = 1;
            var main = funcs.Values.FirstOrDefault(f => f.Name == "{main}");
            sum.TotalMs = summary >= 0 ? summary * unitMs : main != null ? main.InclusiveMs : funcs.Values.Select(f => f.SelfMs).Sum();
            sum.Functions = funcs.Values.OrderByDescending(f => f.InclusiveMs).ThenByDescending(f => f.SelfMs).Take(top).ToList();
            return sum;
        }

        static ProfileFunction Fn(Dictionary<string, ProfileFunction> d, string name)
        {
            ProfileFunction f;
            if (!d.TryGetValue(name, out f)) d[name] = f = new ProfileFunction { Name = name };
            return f;
        }

        static string Name(Dictionary<string, string> names, string v)
        {
            v = v.Trim();
            if (!v.StartsWith("(")) return v;
            int close = v.IndexOf(')');
            if (close < 0) return v;
            var id = v.Substring(0, close + 1);
            var rest = v.Substring(close + 1).Trim();
            if (rest != "") { names[id] = rest; return rest; }
            string known;
            return names.TryGetValue(id, out known) ? known : id;
        }

        public static void DeleteAll(Site site)
        {
            foreach (var p in List(site)) try { File.Delete(p.File); } catch { }
        }
    }
}
