using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Portly.Packages;

namespace Portly.Tools
{
    /// <summary>
    /// Version constraints as written in composer.json ("require": {"php": "^8.1"}) and package.json
    /// ("engines": {"node": ">=18 &lt;23"}): ^ ~ &gt;= &gt; &lt;= &lt; = != , wildcards (8.2.*, 22.x), hyphen ranges
    /// (1.2 - 2.0), AND (space / comma) and OR (|| or |). Good enough to pick an installed version, not a full solver.
    /// </summary>
    public static class VersionRules
    {
        /// <summary>The newest folder whose version satisfies the constraint, or null.</summary>
        public static string Best(IEnumerable<string> folders, string constraint)
        {
            return folders.Where(f => Satisfies(PackageTypes.ParseVersion(f), constraint))
                          .OrderByDescending(PackageTypes.ParseVersion).FirstOrDefault();
        }

        public static bool Satisfies(Version v, string constraint)
        {
            if (string.IsNullOrWhiteSpace(constraint)) return true;
            v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            foreach (var alt in Regex.Split(constraint, @"\|\|?"))
            {
                var a = alt.Trim();
                if (a == "") continue;
                var hy = Regex.Match(a, @"^(\S+)\s+-\s+(\S+)$");
                if (hy.Success)
                {
                    if (Compare(v, ">=", hy.Groups[1].Value) && Compare(v, "<=", hy.Groups[2].Value)) return true;
                    continue;
                }
                // ">= 8.1" -> ">=8.1", then AND over the terms
                a = Regex.Replace(a, @"(>=|<=|!=|==|>|<|=|\^|~)\s+", "$1");
                var terms = a.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (terms.Length > 0 && terms.All(t => Term(v, t))) return true;
            }
            return false;
        }

        static bool Term(Version v, string t)
        {
            t = Regex.Replace(t, @"@\w+$", "");           // composer stability flags (@dev)
            if (t == "*" || t == "x" || t == "X") return true;
            var m = Regex.Match(t, @"^(>=|<=|!=|==|>|<|=|\^|~)?v?(.*)$");
            var op = m.Groups[1].Value;
            var ver = m.Groups[2].Value;
            if (op == "^")
            {
                var p = Parts(ver);
                if (p.Count == 0) return false;
                var low = Low(p);
                Version high = p[0] > 0 ? new Version(p[0] + 1, 0, 0)
                    : p.Count > 1 && p[1] > 0 ? new Version(0, p[1] + 1, 0) : new Version(0, 0, (p.Count > 2 ? p[2] : 0) + 1);
                return v >= low && v < high;
            }
            if (op == "~")
            {
                var p = Parts(ver);
                if (p.Count == 0) return false;
                var low = Low(p);
                // composer: ~8.2 = >=8.2 <9.0, ~8.2.1 = >=8.2.1 <8.3; npm: ~8 = 8.x
                Version high = p.Count == 1 ? new Version(p[0] + 1, 0, 0)
                    : p.Count == 2 ? new Version(p[0] + 1, 0, 0) : new Version(p[0], p[1] + 1, 0);
                return v >= low && v < high;
            }
            if (op == "" || op == "=" || op == "==")
            {
                // 8, 8.2, 8.2.*, 22.x -> prefix match; 8.2.3 -> exact
                var p = Parts(ver);
                if (p.Count == 0) return Regex.IsMatch(ver, @"^[*xX]?$");
                if (p.Count >= 1 && v.Major != p[0]) return false;
                if (p.Count >= 2 && v.Minor != p[1]) return false;
                if (p.Count >= 3 && v.Build != p[2]) return false;
                return true;
            }
            return Compare(v, op, ver);
        }

        static bool Compare(Version v, string op, string ver)
        {
            var p = Parts(ver.TrimStart('v'));
            if (p.Count == 0) return false;
            var x = Low(p);
            switch (op)
            {
                case ">=": return v >= x;
                case ">": return p.Count < 3 ? v >= Next(p) : v > x;      // >8.1 means 8.2+ (8.1.x is not "more")
                case "<": return v < x;
                case "<=": return p.Count < 3 ? v < Next(p) : v <= x;     // <=8.2 includes 8.2.x
                case "!=": return !(v.Major == p[0] && (p.Count < 2 || v.Minor == p[1]) && (p.Count < 3 || v.Build == p[2]));
            }
            return false;
        }

        /// <summary>"8.2.*" -> [8, 2]; "22.x" -> [22]; "8.2.3-RC1" -> [8, 2, 3].</summary>
        static List<int> Parts(string ver)
        {
            var list = new List<int>();
            foreach (var s in ver.Split('.'))
            {
                var m = Regex.Match(s, @"^\d+");
                if (!m.Success) break;
                list.Add(int.Parse(m.Value));
                if (list.Count == 3) break;
            }
            return list;
        }

        static Version Low(List<int> p) { return new Version(p[0], p.Count > 1 ? p[1] : 0, p.Count > 2 ? p[2] : 0); }
        static Version Next(List<int> p) { return p.Count == 1 ? new Version(p[0] + 1, 0, 0) : new Version(p[0], p[1] + 1, 0); }
    }
}
