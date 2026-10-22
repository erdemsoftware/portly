using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Portly.Core
{
    /// <summary>
    /// Translations live in lang\&lt;code&gt;.json as flat "key": "text" pairs.
    /// English (en.json) is the fallback for any missing key, so a partial translation still works.
    /// "_meta" holds the language name and translators.
    /// </summary>
    public static class I18n
    {
        static Dictionary<string, string> _fallback = new Dictionary<string, string>();
        static Dictionary<string, string> _current = new Dictionary<string, string>();
        public static string Code { get; private set; }

        public class LangInfo
        {
            public string Code;
            public string Name;      // native name, e.g. "Türkçe"
            public string English;   // e.g. "Turkish"
            public override string ToString() { return Name == English ? Name : Name + " (" + English + ")"; }
        }

        public static void Load(string code)
        {
            _fallback = Read("en");
            if (string.IsNullOrEmpty(code) || code == "auto") code = Detect();
            Code = code;
            _current = code == "en" ? _fallback : Read(code);
            if (_current.Count == 0) { Code = "en"; _current = _fallback; }
        }

        /// <summary>Pick the Windows display language when a matching file exists.</summary>
        public static string Detect()
        {
            var ui = CultureInfo.CurrentUICulture;
            foreach (var candidate in new[] { ui.Name, ui.TwoLetterISOLanguageName })
            {
                if (File.Exists(Path.Combine(Paths.Lang, candidate + ".json"))) return candidate;
            }
            // e.g. "pt-PT" -> "pt-BR", "zh-TW" -> "zh-CN" when only one variant exists
            foreach (var li in Available())
                if (li.Code.StartsWith(ui.TwoLetterISOLanguageName + "-", StringComparison.OrdinalIgnoreCase)) return li.Code;
            return "en";
        }

        static Dictionary<string, string> Read(string code)
        {
            var d = new Dictionary<string, string>();
            try
            {
                var file = Path.Combine(Paths.Lang, code + ".json");
                if (!File.Exists(file)) return d;
                var o = Json.ParseObject(File.ReadAllText(file, Encoding.UTF8));
                foreach (var kv in o)
                {
                    var s = kv.Value as string;
                    if (s != null) d[kv.Key] = s;
                }
            }
            catch (Exception ex) { Log.Error("Language file " + code, ex); }
            return d;
        }

        public static List<LangInfo> Available()
        {
            var list = new List<LangInfo>();
            if (!Directory.Exists(Paths.Lang)) return list;
            foreach (var f in Directory.GetFiles(Paths.Lang, "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(f);
                var li = new LangInfo { Code = code, Name = code, English = code };
                try
                {
                    var meta = Json.ParseObject(File.ReadAllText(f, Encoding.UTF8)).Obj("_meta");
                    if (meta != null) { li.Name = meta.Str("name", code); li.English = meta.Str("english", li.Name); }
                }
                catch { }
                list.Add(li);
            }
            list.Sort((a, b) => string.Compare(a.English, b.English, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>Translate. Missing keys fall back to English, then to the key itself.</summary>
        public static string T(string key, params object[] args)
        {
            string s;
            if (!_current.TryGetValue(key, out s) && !_fallback.TryGetValue(key, out s)) s = key;
            s = s.Replace("\\n", "\n");
            if (args != null && args.Length > 0)
            {
                try { s = string.Format(s, args); } catch { }
            }
            return s;
        }
    }
}
