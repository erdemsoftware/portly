using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Portly.Core
{
    public class DonateMethod
    {
        public string Kind;   // link | copy
        public string Name;
        public string Value;  // url or address / IBAN
        public string Note;
    }

    /// <summary>
    /// Donation options from manifest/donate.json: embedded in the exe, refreshed from GitHub (Windows 10/11 edition)
    /// so addresses and links can change without a new Portly release.
    /// </summary>
    public static class Donate
    {
        static string CacheFile { get { return Path.Combine(Paths.Cache, "donate.json"); } }

        public static List<DonateMethod> Load()
        {
            string json = null;
            try { if (File.Exists(CacheFile)) json = File.ReadAllText(CacheFile, Encoding.UTF8); } catch { }
            var list = Parse(json);
            if (list == null)
            {
                using (var s = typeof(Donate).Assembly.GetManifestResourceStream("donate.json"))
                using (var r = new StreamReader(s, Encoding.UTF8)) list = Parse(r.ReadToEnd()) ?? new List<DonateMethod>();
            }
            return list;
        }

        public static async Task RefreshAsync()
        {
            if (!AppInfo.UpdatesSupported) return;
            try
            {
                var json = await Net.GetStringAsync(AppInfo.ManifestBase + "donate.json");
                if (Parse(json) != null) File.WriteAllText(CacheFile, json, new UTF8Encoding(false));
            }
            catch { }
        }

        static List<DonateMethod> Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var list = new List<DonateMethod>();
                foreach (var m in Json.ParseObject(json).Arr("methods"))
                {
                    var kind = m.Str("kind", "link");
                    var value = kind == "link" ? m.Str("url") : m.Str("value");
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (kind == "link" && !value.StartsWith("https://")) continue;
                    var note = m.Obj("note");
                    list.Add(new DonateMethod
                    {
                        Kind = kind,
                        Name = m.Str("name"),
                        Value = value.Trim(),
                        Note = note == null ? "" : note.Str(I18n.Code, note.Str("en")),
                    });
                }
                return list;
            }
            catch { return null; }
        }
    }
}
