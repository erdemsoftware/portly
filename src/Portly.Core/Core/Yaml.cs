using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Portly.Core
{
    public class YamlException : Exception
    {
        public int Line { get; private set; }
        public YamlException(int line, string message) : base(message) { Line = line; }
    }

    /// <summary>
    /// A small, strict YAML subset for portly.yml (no NuGet). Supported: block maps and lists by indentation,
    /// "- key: value" list items, flow lists [a, b] and flow maps { a: 1 } on one line, plain / "double" / 'single'
    /// quoted strings, integers, decimals, true / false, null / ~, # comments and a leading "---".
    /// Anchors, aliases, tags, multi-line strings (| >), tabs for indentation and several documents are rejected
    /// with the line number, so a typo never turns silently into a different setting.
    /// Result: Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / long / decimal / bool / null.
    /// Decimals keep their text form ("8.10" stays 8.10), so versions survive a round trip.
    /// </summary>
    public static class Yaml
    {
        class Ln { public int No, Indent; public string Text; }

        public static object Parse(string text)
        {
            var lines = Prepare(text ?? "");
            if (lines.Count == 0) return new Dictionary<string, object>(StringComparer.Ordinal);
            int i = 0;
            if (lines[0].Indent != 0) throw new YamlException(lines[0].No, "the first line must not be indented");
            var result = Block(lines, ref i, 0);
            if (i < lines.Count) throw new YamlException(lines[i].No, "unexpected indentation");
            return result;
        }

        /// <summary>Top level must be a map (portly.yml style); returns it.</summary>
        public static Dictionary<string, object> ParseMap(string text)
        {
            var o = Parse(text);
            var d = o as Dictionary<string, object>;
            if (d == null) throw new YamlException(1, "the file must start with 'key: value' lines");
            return d;
        }

        // ---------------- lines ----------------
        static List<Ln> Prepare(string text)
        {
            var list = new List<Ln>();
            var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int n = 0; n < raw.Length; n++)
            {
                var line = raw[n];
                if (n == 0 && line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1);
                int indent = 0;
                while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
                {
                    if (line[indent] == '\t') throw new YamlException(n + 1, "use spaces, not tabs, for indentation");
                    indent++;
                }
                var body = StripComment(line.Substring(indent), n + 1).TrimEnd();
                if (body == "") continue;
                if (body == "---" && list.Count == 0) continue;
                if (body == "---" || body == "...") throw new YamlException(n + 1, "only one document is allowed");
                list.Add(new Ln { No = n + 1, Indent = indent, Text = body });
            }
            return list;
        }

        static string StripComment(string s, int no)
        {
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (quote == '"' && c == '\\') { i++; continue; }
                    if (c == quote)
                    {
                        if (quote == '\'' && i + 1 < s.Length && s[i + 1] == '\'') { i++; continue; }
                        quote = '\0';
                    }
                    continue;
                }
                if ((c == '"' || c == '\'') && (i == 0 || " \t[{,:-".IndexOf(s[i - 1]) >= 0)) { quote = c; continue; }
                if (c == '#' && (i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t')) return s.Substring(0, i);
            }
            return s;
        }

        // ---------------- block structure ----------------
        static bool IsListItem(string t) { return t == "-" || t.StartsWith("- "); }

        static object Block(List<Ln> lines, ref int i, int indent)
        {
            return IsListItem(lines[i].Text) ? (object)List(lines, ref i, indent) : Map(lines, ref i, indent);
        }

        static Dictionary<string, object> Map(List<Ln> lines, ref int i, int indent)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            while (i < lines.Count && lines[i].Indent == indent && !IsListItem(lines[i].Text))
            {
                var ln = lines[i];
                string rest;
                var key = SplitKey(ln.Text, ln.No, out rest);
                if (key == null) throw new YamlException(ln.No, "expected 'key: value'");
                if (map.ContainsKey(key)) throw new YamlException(ln.No, "duplicate key '" + key + "'");
                i++;
                object value;
                if (rest != "") value = Inline(rest, ln.No);
                else if (i < lines.Count && lines[i].Indent > indent) value = Block(lines, ref i, lines[i].Indent);
                // "key:" followed by "- item" lines at the same indentation is valid YAML too
                else if (i < lines.Count && lines[i].Indent == indent && IsListItem(lines[i].Text)) value = List(lines, ref i, indent);
                else value = null;
                map[key] = value;
            }
            if (i < lines.Count && lines[i].Indent > indent) throw new YamlException(lines[i].No, "unexpected indentation");
            return map;
        }

        static List<object> List(List<Ln> lines, ref int i, int indent)
        {
            var list = new List<object>();
            while (i < lines.Count && lines[i].Indent == indent && IsListItem(lines[i].Text))
            {
                var ln = lines[i];
                var content = ln.Text.Length > 1 ? ln.Text.Substring(2).TrimStart() : "";
                if (content == "")
                {
                    i++;
                    if (i < lines.Count && lines[i].Indent > indent) list.Add(Block(lines, ref i, lines[i].Indent));
                    else list.Add(null);
                    continue;
                }
                string rest;
                if (!content.StartsWith("[") && !content.StartsWith("{") && SplitKey(content, ln.No, out rest) != null)
                {
                    // "- name: queue" starts a map whose other keys line up with "name"
                    int inner = ln.Indent + (ln.Text.Length - content.Length);
                    lines[i] = new Ln { No = ln.No, Indent = inner, Text = content };
                    list.Add(Map(lines, ref i, inner));
                    continue;
                }
                if (IsListItem(content)) throw new YamlException(ln.No, "nested '- -' lists are not supported; put the inner list on its own lines");
                list.Add(Inline(content, ln.No));
                i++;
            }
            if (i < lines.Count && lines[i].Indent > indent) throw new YamlException(lines[i].No, "unexpected indentation");
            return list;
        }

        /// <summary>"key: rest" -> key (unquoted) and rest; null when the line is not a map entry.</summary>
        static string SplitKey(string t, int no, out string rest)
        {
            rest = "";
            string key;
            int after;
            if (t.StartsWith("\"") || t.StartsWith("'"))
            {
                int p = 0;
                key = Quoted(t, ref p, no);
                after = p;
                while (after < t.Length && t[after] == ' ') after++;
                if (after >= t.Length || t[after] != ':') return null;
            }
            else
            {
                after = -1;
                for (int k = 0; k < t.Length; k++)
                    if (t[k] == ':' && (k + 1 == t.Length || t[k + 1] == ' ')) { after = k; break; }
                if (after <= 0) return null;
                key = t.Substring(0, after).Trim();
                if (key.IndexOfAny(new[] { '[', ']', '{', '}', ',', '&', '*', '!', '|', '>', '%', '@', '`', '?' }) >= 0)
                    throw new YamlException(no, "unsupported character in key '" + key + "'");
            }
            if (after + 1 < t.Length && t[after + 1] != ' ') return null;
            rest = after + 1 < t.Length ? t.Substring(after + 1).Trim() : "";
            return key;
        }

        // ---------------- values on one line ----------------
        static object Inline(string s, int no)
        {
            int p = 0;
            var v = Value(s, ref p, no, false);
            while (p < s.Length && s[p] == ' ') p++;
            if (p < s.Length) throw new YamlException(no, "unexpected text after the value: '" + s.Substring(p) + "'");
            return v;
        }

        static object Value(string s, ref int p, int no, bool inFlow)
        {
            while (p < s.Length && s[p] == ' ') p++;
            if (p >= s.Length) return null;
            char c = s[p];
            if (c == '[') return FlowList(s, ref p, no);
            if (c == '{') return FlowMap(s, ref p, no);
            if (c == '"' || c == '\'') return Quoted(s, ref p, no);
            if ("&*!|>%@`".IndexOf(c) >= 0)
                throw new YamlException(no, "'" + c + "' (anchors, aliases, tags, multi-line text) is not supported; put the value in quotes");
            int start = p;
            if (inFlow)
            {
                while (p < s.Length && s[p] != ',' && s[p] != ']' && s[p] != '}' && !(s[p] == ':' && p + 1 < s.Length && s[p + 1] == ' ')) p++;
            }
            else p = s.Length;
            var plain = s.Substring(start, p - start).Trim();
            if (!inFlow && plain.Contains(": ")) throw new YamlException(no, "a value containing ': ' must be in quotes");
            return Scalar(plain);
        }

        static object Scalar(string t)
        {
            if (t == "" || t == "~" || t == "null" || t == "Null" || t == "NULL") return null;
            if (t == "true" || t == "True" || t == "TRUE") return true;
            if (t == "false" || t == "False" || t == "FALSE") return false;
            long l;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[-+]?[0-9]+$") && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            decimal d;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[-+]?[0-9]*\.[0-9]+$") && decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out d)) return d;
            return t;
        }

        static string Quoted(string s, ref int p, int no)
        {
            char q = s[p++];
            var sb = new StringBuilder();
            while (true)
            {
                if (p >= s.Length) throw new YamlException(no, "missing closing " + q);
                char c = s[p++];
                if (q == '\'' && c == '\'')
                {
                    if (p < s.Length && s[p] == '\'') { sb.Append('\''); p++; continue; }
                    return sb.ToString();
                }
                if (q == '"' && c == '"') return sb.ToString();
                if (q == '"' && c == '\\')
                {
                    if (p >= s.Length) throw new YamlException(no, "missing closing \"");
                    char e = s[p++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '0': sb.Append('\0'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        default: throw new YamlException(no, "unknown escape \\" + e + " (write \\\\ for a backslash)");
                    }
                    continue;
                }
                sb.Append(c);
            }
        }

        static List<object> FlowList(string s, ref int p, int no)
        {
            p++; // [
            var list = new List<object>();
            while (true)
            {
                while (p < s.Length && s[p] == ' ') p++;
                if (p >= s.Length) throw new YamlException(no, "missing ']' (a [list] must end on the same line)");
                if (s[p] == ']') { p++; return list; }
                list.Add(Value(s, ref p, no, true));
                while (p < s.Length && s[p] == ' ') p++;
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == ']') { p++; return list; }
                throw new YamlException(no, "expected ',' or ']' in the list");
            }
        }

        static Dictionary<string, object> FlowMap(string s, ref int p, int no)
        {
            p++; // {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            while (true)
            {
                while (p < s.Length && s[p] == ' ') p++;
                if (p >= s.Length) throw new YamlException(no, "missing '}' (a { map } must end on the same line)");
                if (s[p] == '}') { p++; return map; }
                string key;
                if (s[p] == '"' || s[p] == '\'') key = Quoted(s, ref p, no);
                else
                {
                    int start = p;
                    while (p < s.Length && s[p] != ':' && s[p] != ',' && s[p] != '}') p++;
                    key = s.Substring(start, p - start).Trim();
                }
                while (p < s.Length && s[p] == ' ') p++;
                if (key == "" || p >= s.Length || s[p] != ':') throw new YamlException(no, "expected 'key: value' inside { }");
                p++;
                if (map.ContainsKey(key)) throw new YamlException(no, "duplicate key '" + key + "'");
                map[key] = Value(s, ref p, no, true);
                while (p < s.Length && s[p] == ' ') p++;
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == '}') { p++; return map; }
                throw new YamlException(no, "expected ',' or '}' in the map");
            }
        }

        // ---------------- helpers for readers ----------------
        /// <summary>Scalar as text ("8.3", "22", "true"); null for maps, lists and null.</summary>
        public static string Str(object v)
        {
            if (v == null || v is Dictionary<string, object> || v is List<object>) return null;
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is decimal) return ((decimal)v).ToString(CultureInfo.InvariantCulture);
            if (v is long) return ((long)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString();
        }

        /// <summary>Quote a string for writing when it would not read back as the same plain text.</summary>
        public static string Quote(string s)
        {
            if (s == null) return "null";
            bool plainOk = s != "" && s.Trim() == s && Scalar(s) is string && "[{\"'&*!|>%@`#-?,".IndexOf(s[0]) < 0
                && !s.Contains(": ") && !s.Contains(" #") && s.IndexOfAny(new[] { ',', ']', '}', '\n', '\r', '\t' }) < 0 && !s.EndsWith(":");
            if (plainOk) return s;
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r") + "\"";
        }
    }
}
