using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Portly.Core
{
    /// <summary>Small INI reader/writer that keeps comments and order.</summary>
    public class Ini
    {
        readonly List<string> _lines = new List<string>();
        public string FilePath { get; private set; }

        public Ini(string path)
        {
            FilePath = path;
            if (File.Exists(path)) _lines.AddRange(File.ReadAllLines(path, Encoding.UTF8));
        }

        public List<string> Sections()
        {
            var list = new List<string>();
            foreach (var l in _lines)
            {
                var t = l.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) list.Add(t.Substring(1, t.Length - 2).Trim());
            }
            return list;
        }

        int FindSection(string section)
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                var t = _lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]") && string.Equals(t.Substring(1, t.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        int SectionEnd(int start)
        {
            int i = start + 1;
            while (i < _lines.Count && !_lines[i].TrimStart().StartsWith("[")) i++;
            return i;
        }

        int FindKey(string section, string key, out int sectionIndex)
        {
            sectionIndex = FindSection(section);
            if (sectionIndex < 0) return -1;
            int end = SectionEnd(sectionIndex);
            for (int i = sectionIndex + 1; i < end; i++)
            {
                var t = _lines[i].TrimStart();
                if (t.StartsWith(";") || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq > 0 && string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public Dictionary<string, string> Section(string section)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int s = FindSection(section);
            if (s < 0) return d;
            int end = SectionEnd(s);
            for (int i = s + 1; i < end; i++)
            {
                var t = _lines[i].TrimStart();
                if (t.StartsWith(";") || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq > 0) d[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
            }
            return d;
        }

        public string Get(string section, string key, string def = "")
        {
            int s;
            int i = FindKey(section, key, out s);
            if (i < 0) return def;
            var t = _lines[i];
            return t.Substring(t.IndexOf('=') + 1).Trim();
        }

        public int GetInt(string section, string key, int def)
        {
            int v;
            return int.TryParse(Get(section, key, ""), out v) ? v : def;
        }

        public bool GetBool(string section, string key, bool def)
        {
            var v = Get(section, key, "").ToLowerInvariant();
            if (v == "") return def;
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        public void Set(string section, string key, string value)
        {
            int s;
            int i = FindKey(section, key, out s);
            var line = key + "=" + (value ?? "");
            if (i >= 0) { _lines[i] = line; return; }
            if (s < 0)
            {
                if (_lines.Count > 0 && _lines[_lines.Count - 1].Trim() != "") _lines.Add("");
                _lines.Add("[" + section + "]");
                _lines.Add(line);
                return;
            }
            int end = SectionEnd(s);
            while (end > s + 1 && _lines[end - 1].Trim() == "") end--;
            _lines.Insert(end, line);
        }

        public void Set(string section, string key, int value) { Set(section, key, value.ToString()); }
        public void Set(string section, string key, bool value) { Set(section, key, value ? "1" : "0"); }

        public void Remove(string section, string key)
        {
            int s;
            int i = FindKey(section, key, out s);
            if (i >= 0) _lines.RemoveAt(i);
        }

        public void RemoveSection(string section)
        {
            int s = FindSection(section);
            if (s < 0) return;
            int end = SectionEnd(s);
            _lines.RemoveRange(s, end - s);
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, _lines.ToArray(), new UTF8Encoding(false));
        }
    }
}
