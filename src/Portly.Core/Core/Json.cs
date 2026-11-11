using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Portly.Core
{
    /// <summary>
    /// Small JSON reader / writer with no dependency, the same on .NET Framework 4.8 and .NET 10.
    /// Results match the old JavaScriptSerializer: objects are Dictionary&lt;string, object&gt;, arrays object[],
    /// numbers int / long / decimal / double; the writer escapes &lt; &gt; &amp; ' so the output is safe inside HTML.
    /// </summary>
    public static class Json
    {
        const int MaxDepth = 64;

        public static Dictionary<string, object> ParseObject(string json)
        {
            var o = Parse(json) as Dictionary<string, object>;
            if (o == null) throw new FormatException("JSON object expected");
            return o;
        }

        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException("json");
            var r = new Reader(json);
            r.SkipSpace();
            var v = r.Value(0);
            r.SkipSpace();
            if (r.Pos != json.Length) throw r.Error("unexpected text after the value");
            return v;
        }

        public static string Serialize(object o)
        {
            var sb = new StringBuilder();
            Write(sb, o, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON nesting too deep");
            if (o == null || o is DBNull) { sb.Append("null"); return; }
            var s = o as string;
            if (s != null) { WriteString(sb, s); return; }
            if (o is char) { WriteString(sb, o.ToString()); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is Enum) { sb.Append(Convert.ToInt64(o, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is double) { sb.Append(((double)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is float) { sb.Append(((float)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is decimal || o is int || o is long || o is short || o is byte || o is sbyte || o is uint || o is ulong || o is ushort)
            {
                sb.Append(((IFormattable)o).ToString(null, CultureInfo.InvariantCulture));
                return;
            }
            var dict = o as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, e.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            var list = o as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': case '>': case '&': case '\'':
                        sb.Append("\\u").Append(((int)c).ToString("x4")); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Reader
        {
            readonly string _s;
            public int Pos;

            public Reader(string s) { _s = s; }

            public FormatException Error(string what) { return new FormatException("Invalid JSON at " + Pos + ": " + what); }

            public void SkipSpace()
            {
                while (Pos < _s.Length && (_s[Pos] == ' ' || _s[Pos] == '\t' || _s[Pos] == '\n' || _s[Pos] == '\r' || _s[Pos] == '﻿')) Pos++;
            }

            public object Value(int depth)
            {
                if (depth > MaxDepth) throw Error("nesting too deep");
                if (Pos >= _s.Length) throw Error("unexpected end");
                char c = _s[Pos];
                switch (c)
                {
                    case '{': return Object(depth);
                    case '[': return Array(depth);
                    case '"': return String();
                    case 't': Word("true"); return true;
                    case 'f': Word("false"); return false;
                    case 'n': Word("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Number();
                        throw Error("unexpected character '" + c + "'");
                }
            }

            void Word(string w)
            {
                if (string.CompareOrdinal(_s, Pos, w, 0, w.Length) != 0) throw Error("expected " + w);
                Pos += w.Length;
            }

            Dictionary<string, object> Object(int depth)
            {
                var d = new Dictionary<string, object>();
                Pos++;
                SkipSpace();
                if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    SkipSpace();
                    if (Pos >= _s.Length || _s[Pos] != '"') throw Error("expected a property name");
                    var key = String();
                    SkipSpace();
                    if (Pos >= _s.Length || _s[Pos] != ':') throw Error("expected ':'");
                    Pos++;
                    SkipSpace();
                    d[key] = Value(depth + 1);
                    SkipSpace();
                    if (Pos >= _s.Length) throw Error("unexpected end");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return d; }
                    throw Error("expected ',' or '}'");
                }
            }

            object[] Array(int depth)
            {
                var list = new List<object>();
                Pos++;
                SkipSpace();
                if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return list.ToArray(); }
                while (true)
                {
                    SkipSpace();
                    list.Add(Value(depth + 1));
                    SkipSpace();
                    if (Pos >= _s.Length) throw Error("unexpected end");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return list.ToArray(); }
                    throw Error("expected ',' or ']'");
                }
            }

            string String()
            {
                Pos++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= _s.Length) throw Error("unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (Pos >= _s.Length) throw Error("unterminated string");
                    char e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            int code;
                            if (Pos + 4 > _s.Length || !int.TryParse(_s.Substring(Pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                throw Error("bad \\u escape");
                            sb.Append((char)code);
                            Pos += 4;
                            break;
                        default: throw Error("bad escape");
                    }
                }
            }

            object Number()
            {
                int start = Pos;
                if (_s[Pos] == '-') Pos++;
                while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++;
                bool real = false, exp = false;
                if (Pos < _s.Length && _s[Pos] == '.') { real = true; Pos++; while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++; }
                if (Pos < _s.Length && (_s[Pos] == 'e' || _s[Pos] == 'E'))
                {
                    real = exp = true;
                    Pos++;
                    if (Pos < _s.Length && (_s[Pos] == '+' || _s[Pos] == '-')) Pos++;
                    while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++;
                }
                var t = _s.Substring(start, Pos - start);
                if (t == "-" || t.EndsWith(".", StringComparison.Ordinal)) throw Error("bad number");
                var inv = CultureInfo.InvariantCulture;
                if (!real)
                {
                    int i; long l;
                    if (int.TryParse(t, NumberStyles.AllowLeadingSign, inv, out i)) return i;
                    if (long.TryParse(t, NumberStyles.AllowLeadingSign, inv, out l)) return l;
                }
                decimal m;
                if (!exp && decimal.TryParse(t, NumberStyles.Float, inv, out m)) return m;
                double d;
                if (double.TryParse(t, NumberStyles.Float, inv, out d)) return d;
                throw Error("bad number");
            }
        }

        public static string Str(this Dictionary<string, object> d, string key, string def = "")
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int Int(this Dictionary<string, object> d, string key, int def = 0)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static bool Bool(this Dictionary<string, object> d, string key, bool def = false)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            var s = v.ToString().ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes";
        }

        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<Dictionary<string, object>> Arr(this Dictionary<string, object> d, string key)
        {
            var list = new List<Dictionary<string, object>>();
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return list;
            var e = v as IEnumerable;
            if (e == null || v is string) return list;
            foreach (var item in e)
            {
                var o = item as Dictionary<string, object>;
                if (o != null) list.Add(o);
            }
            return list;
        }
    }
}
