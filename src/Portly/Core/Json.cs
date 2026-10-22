using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace Portly.Core
{
    /// <summary>Tiny helpers around the built-in JavaScriptSerializer (no external dependency).</summary>
    public static class Json
    {
        static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 }; }

        public static Dictionary<string, object> ParseObject(string json)
        {
            var o = Ser().DeserializeObject(json) as Dictionary<string, object>;
            if (o == null) throw new FormatException("JSON object expected");
            return o;
        }

        public static string Serialize(object o) { return Ser().Serialize(o); }

        public static string Str(this Dictionary<string, object> d, string key, string def = "")
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static int Int(this Dictionary<string, object> d, string key, int def = 0)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return def; }
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
            if (e == null) return list;
            foreach (var item in e)
            {
                var o = item as Dictionary<string, object>;
                if (o != null) list.Add(o);
            }
            return list;
        }
    }
}
