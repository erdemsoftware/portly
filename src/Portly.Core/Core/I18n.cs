using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        /// <summary>Right-to-left language (Arabic, Persian, Urdu ...): "_meta": { "rtl": true }.</summary>
        public static bool IsRtl { get; private set; }

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
            IsRtl = false;
            try
            {
                var file = Path.Combine(Paths.Lang, Code + ".json");
                if (File.Exists(file)) IsRtl = Json.ParseObject(File.ReadAllText(file, Encoding.UTF8)).Obj("_meta").Bool("rtl");
            }
            catch { }
        }

        /// <summary>
        /// Pick the system display language when a matching file exists. Windows: the UI culture. macOS / Linux: the
        /// locale variables (LC_ALL, LC_MESSAGES, LANG, LANGUAGE), then the UI culture (which macOS fills from the
        /// user's language list even when a program started from Finder has no LANG).
        /// </summary>
        public static string Detect()
        {
            if (!OS.IsWindows)
            {
                var fromEnv = DetectUnix(Environment.GetEnvironmentVariable("LC_ALL"), Environment.GetEnvironmentVariable("LC_MESSAGES"),
                    Environment.GetEnvironmentVariable("LANG"), Environment.GetEnvironmentVariable("LANGUAGE"));
                if (fromEnv != null) return fromEnv;
            }
            var ui = CultureInfo.CurrentUICulture;
            return Pick(ui.Name, ui.TwoLetterISOLanguageName);
        }

        /// <summary>
        /// The language file for the POSIX locale variables, or null when they name no language ("C", "POSIX", unset).
        /// Precedence as in gettext: LC_ALL, then LANGUAGE (a colon list), LC_MESSAGES, LANG.
        /// </summary>
        public static string DetectUnix(string lcAll, string lcMessages, string lang, string language)
        {
            var list = new List<string>();
            var main = !string.IsNullOrWhiteSpace(lcAll) ? lcAll : !string.IsNullOrWhiteSpace(lcMessages) ? lcMessages : lang;
            bool plain = string.IsNullOrWhiteSpace(main) || main == "C" || main == "POSIX" || main.StartsWith("C.", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(lcAll) && !plain && !string.IsNullOrWhiteSpace(language)) list.AddRange(language.Split(':'));
            if (!plain) list.Add(main);
            foreach (var raw in list)
            {
                var loc = raw.Trim();
                int cut = loc.IndexOfAny(new[] { '.', '@' });
                if (cut >= 0) loc = loc.Substring(0, cut);
                loc = loc.Replace('_', '-');
                if (loc == "" || loc == "C" || loc == "POSIX") continue;
                var two = loc.Split('-')[0].ToLowerInvariant();
                var code = Pick(loc, two, false);
                if (code != null) return code;
            }
            return null;
        }

        /// <summary>The language file for a culture name ("pt-BR") and its two letter code ("pt"); "en" when none fits.</summary>
        static string Pick(string name, string two) { return Pick(name, two, true); }

        static string Pick(string name, string two, bool fallbackEn)
        {
            foreach (var candidate in new[] { name, two })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                var hit = FindFile(candidate);
                if (hit != null) return hit;
            }
            // e.g. "pt-PT" -> "pt-BR", "zh-TW" -> "zh-CN" when only one variant exists
            foreach (var li in Available())
                if (two != "" && li.Code.StartsWith(two + "-", StringComparison.OrdinalIgnoreCase)) return li.Code;
            return fallbackEn ? "en" : null;
        }

        /// <summary>The code with the file's own spelling (case differs between locale names and file names).</summary>
        static string FindFile(string code)
        {
            if (File.Exists(Path.Combine(Paths.Lang, code + ".json"))) return code;
            foreach (var li in Available()) if (li.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) return li.Code;
            return null;
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

        static List<LangInfo> _available;
        static string _availableKey;

        /// <summary>The installed languages. Remembered while the language files stay the same (31 files of ~80 KB each).</summary>
        public static List<LangInfo> Available()
        {
            string key = "";
            try { if (Directory.Exists(Paths.Lang)) key = string.Join("|", Directory.GetFiles(Paths.Lang, "*.json").Select(f => f + ":" + File.GetLastWriteTimeUtc(f).Ticks)); }
            catch { }
            if (_available != null && key == _availableKey) return new List<LangInfo>(_available);
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
            _available = list;
            _availableKey = key;
            return new List<LangInfo>(list);
        }

        /// <summary>Translate. Missing keys fall back to English, then to the key itself.</summary>
        /// <summary>T(key), or the English fallback while the key is not in the language files yet.</summary>
        public static string TOr(string key, string fallback, params object[] args)
        {
            var s = T(key, args);
            if (s != key) return s;
            if (args != null && args.Length > 0) { try { return string.Format(fallback, args); } catch { } }
            return fallback;
        }

        public static string T(string key, params object[] args)
        {
            string s;
            // macOS / Linux wording ("Same as the system" instead of "Same as Windows") lives in "<key>@unix"
            if (!OS.IsWindows && (_current.TryGetValue(key + "@unix", out s) || _fallback.TryGetValue(key + "@unix", out s))) { }
            else if (!_current.TryGetValue(key, out s) && !_fallback.TryGetValue(key, out s)) s = key;
            s = s.Replace("\\n", "\n");
            if (args != null && args.Length > 0)
            {
                try { s = string.Format(s, args); } catch { }
            }
            return s;
        }
    }
}
