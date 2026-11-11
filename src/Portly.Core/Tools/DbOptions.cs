using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Portly.Core;
using Portly.Packages;

namespace Portly.Tools
{
    /// <summary>
    /// Database "language" (default collation) and default storage engine. Only what the active engine and version
    /// supports is offered, and a saved value the server does not know falls back to utf8mb4_unicode_ci
    /// (an unknown collation-server stops mysqld from starting).
    /// </summary>
    public static class DbOptions
    {
        static readonly Regex NameRx = new Regex(@"^[a-z0-9_]{3,64}$");

        public static bool Valid(string collation) { return collation != null && NameRx.IsMatch(collation); }

        /// <summary>"utf8mb4" for "utf8mb4_turkish_ci".</summary>
        public static string CharsetOf(string collation)
        {
            var i = (collation ?? "").IndexOf('_');
            return i > 0 ? collation.Substring(0, i) : "utf8mb4";
        }

        /// <summary>Collations offered for an engine and server version, the default first.</summary>
        public static List<string> Collations(string engine, Version v)
        {
            var list = new List<string> { Settings.DefaultCollation, "utf8mb4_turkish_ci", "utf8mb4_general_ci" };
            bool maria = engine == "mariadb";
            // unicode_520 (UCA 5.2): MySQL 5.6+ and MariaDB 10.0+
            if (maria || v >= new Version(5, 6)) list.Add("utf8mb4_unicode_520_ci");
            if (!maria && v.Major >= 8) { list.Add("utf8mb4_0900_ai_ci"); list.Add("utf8mb4_tr_0900_ai_ci"); }
            if (maria && v >= new Version(10, 10)) { list.Add("utf8mb4_uca1400_ai_ci"); list.Add("utf8mb4_uca1400_turkish_ai_ci"); }
            return list;
        }

        public static List<string> StorageEngines(string engine)
        {
            var list = new List<string> { "InnoDB", "MyISAM" };
            if (engine == "mariadb") list.Add("Aria");
            return list;
        }

        static Version ActiveVersion(string engine) { return PackageTypes.ParseVersion(PackageTypes.ActiveFolder(engine)); }

        /// <summary>The saved collation when the active server supports it, otherwise the safe default.</summary>
        public static string EffectiveCollation() { return EffectiveCollation(Settings.Current.DbEngine, ActiveVersion(Settings.Current.DbEngine)); }

        public static string EffectiveCollation(string engine, Version v)
        {
            var c = Settings.Current.DbCollation;
            return Collations(engine, v).Contains(c) ? c : Settings.DefaultCollation;
        }

        public static string EffectiveStorageEngine(string engine)
        {
            var e = Settings.Current.DbStorageEngine;
            return StorageEngines(engine).FirstOrDefault(x => x.Equals(e, StringComparison.OrdinalIgnoreCase)) ?? "InnoDB";
        }
    }
}
