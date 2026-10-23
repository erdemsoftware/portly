using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Portly.Core
{
    /// <summary>Settings of the "Updates" group: package notices, skipped versions, proxy.</summary>
    public partial class Settings
    {
        // ---------- new package versions ----------
        /// <summary>Day (yyyy-MM-dd) of the last automatic package check; it runs at the first start of a day.</summary>
        public string LastPackageCheck { get { return _ini.Get("updates", "LastPackageCheck", ""); } set { _ini.Set("updates", "LastPackageCheck", value); } }

        /// <summary>Versions the user chose to skip, per package type ("8.6.0|8.6.1").</summary>
        public bool IsSkipped(string type, string version)
        {
            return _ini.Get("skipped", type, "").Split('|').Contains(version, StringComparer.OrdinalIgnoreCase);
        }

        public void Skip(string type, string version)
        {
            var list = _ini.Get("skipped", type, "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!list.Contains(version, StringComparer.OrdinalIgnoreCase)) list.Add(version);
            // only the latest few matter
            _ini.Set("skipped", type, string.Join("|", list.Skip(Math.Max(0, list.Count - 10))));
        }

        /// <summary>"Later": hidden for this Portly session only (not saved).</summary>
        public static readonly System.Collections.Generic.HashSet<string> LaterThisSession = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---------- proxy ----------
        /// <summary>"system" (Windows / Internet Options, default), "none" or "manual".</summary>
        public string ProxyMode
        {
            get { var v = _ini.Get("network", "ProxyMode", "system").ToLowerInvariant(); return v == "none" || v == "manual" ? v : "system"; }
            set { _ini.Set("network", "ProxyMode", value); }
        }
        /// <summary>host:port of the manual proxy.</summary>
        public string ProxyHost { get { return _ini.Get("network", "ProxyHost", "").Trim(); } set { _ini.Set("network", "ProxyHost", (value ?? "").Trim()); } }
        public string ProxyUser { get { return _ini.Get("network", "ProxyUser", ""); } set { _ini.Set("network", "ProxyUser", value ?? ""); } }
        /// <summary>Encrypted with Windows DPAPI (current user), like the MySQL root password.</summary>
        public string ProxyPassword
        {
            get
            {
                var v = _ini.Get("network", "ProxyPassword", "");
                if (v == "") return "";
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), null, DataProtectionScope.CurrentUser)); }
                catch { return ""; }
            }
            set
            {
                if (string.IsNullOrEmpty(value)) { _ini.Set("network", "ProxyPassword", ""); return; }
                _ini.Set("network", "ProxyPassword", Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)));
            }
        }
    }
}
