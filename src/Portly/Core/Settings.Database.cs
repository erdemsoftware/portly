using System;
using System.Security.Cryptography;
using System.Text;

namespace Portly.Core
{
    /// <summary>Database settings: default collation / storage engine and WSL access.</summary>
    public partial class Settings
    {
        public const string DefaultCollation = "utf8mb4_unicode_ci";

        /// <summary>Server default collation (collation-server) and the one new databases get.</summary>
        public string DbCollation
        {
            get { var v = _ini.Get("mysql", "Collation", DefaultCollation).Trim(); return v == "" ? DefaultCollation : v; }
            set { _ini.Set("mysql", "Collation", value); }
        }

        /// <summary>default-storage-engine: InnoDB (default), MyISAM, Aria (MariaDB only).</summary>
        public string DbStorageEngine
        {
            get { var v = _ini.Get("mysql", "StorageEngine", "InnoDB").Trim(); return v == "" ? "InnoDB" : v; }
            set { _ini.Set("mysql", "StorageEngine", value); }
        }

        // ---------- access from WSL (feature "wsl") ----------
        /// <summary>Access from WSL was set up (user, bind address, firewall rule).</summary>
        public bool WslAccess { get { return _ini.GetBool("wsl", "Enabled", false); } set { _ini.Set("wsl", "Enabled", value); } }
        /// <summary>WSL subnet the user and the firewall rule were made for, e.g. 172.29.144.0/20.</summary>
        public string WslSubnet { get { return _ini.Get("wsl", "Subnet", ""); } set { _ini.Set("wsl", "Subnet", value); } }
        /// <summary>Host part of the database user, e.g. 172.29.%.</summary>
        public string WslUserHost { get { return _ini.Get("wsl", "UserHost", ""); } set { _ini.Set("wsl", "UserHost", value); } }

        /// <summary>Password of the portly_wsl database user (DPAPI, current Windows user).</summary>
        public string WslPassword
        {
            get
            {
                var v = _ini.Get("wsl", "Password", "");
                if (v == "") return "";
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), null, DataProtectionScope.CurrentUser)); }
                catch { return ""; }
            }
            set
            {
                if (string.IsNullOrEmpty(value)) { _ini.Set("wsl", "Password", ""); return; }
                _ini.Set("wsl", "Password", Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)));
            }
        }
    }
}
