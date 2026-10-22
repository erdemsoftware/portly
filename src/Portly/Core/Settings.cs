using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Portly.Core
{
    /// <summary>Typed access to usr\portly.ini. Every option is visible in the Settings window.</summary>
    public class Settings
    {
        public static Settings Current { get; private set; }
        readonly Ini _ini;

        Settings() { _ini = new Ini(Paths.SettingsFile); }

        public static void Load()
        {
            Current = new Settings();
            if (!File.Exists(Paths.SettingsFile)) Current.Save();
        }

        public void Save() { _ini.Save(); }
        public Ini Raw { get { return _ini; } }

        // ---------- general ----------
        public string Language { get { return _ini.Get("general", "Language", "auto"); } set { _ini.Set("general", "Language", value); } }

        public string DocumentRoot
        {
            get
            {
                var v = _ini.Get("general", "DocumentRoot", "");
                if (string.IsNullOrWhiteSpace(v)) return DefaultDocumentRoot;
                if (!Path.IsPathRooted(v)) v = Path.GetFullPath(Path.Combine(Paths.Root, v));
                return v.TrimEnd('\\', '/');
            }
            set
            {
                // keep the path relative when it is inside the Portly folder (portable)
                var v = value.TrimEnd('\\', '/');
                if (v.StartsWith(Paths.Root + "\\", StringComparison.OrdinalIgnoreCase)) v = v.Substring(Paths.Root.Length + 1);
                _ini.Set("general", "DocumentRoot", v);
            }
        }

        /// <summary>Projects live in the www folder next to Portly.exe (C:\Portly\www with the installer).</summary>
        public static string DefaultDocumentRoot { get { return Paths.Www; } }

        /// <summary>Domain ending: shop -> shop.portly</summary>
        public const string DefaultTld = "portly";
        public string Tld { get { return _ini.Get("general", "Tld", DefaultTld).Trim('.').ToLowerInvariant(); } set { _ini.Set("general", "Tld", value.Trim('.').ToLowerInvariant()); } }
        public bool AutoVhosts { get { return _ini.GetBool("general", "AutoVirtualHosts", true); } set { _ini.Set("general", "AutoVirtualHosts", value); } }
        public bool Ssl { get { return _ini.GetBool("general", "Ssl", true); } set { _ini.Set("general", "Ssl", value); } }
        public bool RunAtStartup { get { return _ini.GetBool("general", "RunAtStartup", false); } set { _ini.Set("general", "RunAtStartup", value); } }
        public bool StartMinimized { get { return _ini.GetBool("general", "StartMinimized", false); } set { _ini.Set("general", "StartMinimized", value); } }
        public bool StartServicesOnLaunch { get { return _ini.GetBool("general", "StartServicesOnLaunch", false); } set { _ini.Set("general", "StartServicesOnLaunch", value); } }
        public bool CloseToTray { get { return _ini.GetBool("general", "CloseToTray", true); } set { _ini.Set("general", "CloseToTray", value); } }
        public bool CheckUpdates { get { return _ini.GetBool("general", "CheckUpdates", true); } set { _ini.Set("general", "CheckUpdates", value); } }
        public bool AddToPath { get { return _ini.GetBool("general", "AddToPath", false); } set { _ini.Set("general", "AddToPath", value); } }
        /// <summary>"system", "light" or "dark".</summary>
        public string Theme { get { var v = _ini.Get("general", "Theme", "system"); return v == "light" || v == "dark" ? v : "system"; } set { _ini.Set("general", "Theme", value); } }
        public string Terminal { get { return _ini.Get("general", "Terminal", "cmd"); } set { _ini.Set("general", "Terminal", value); } }
        /// <summary>Start screen on every start until "Don't show again" is ticked.</summary>
        public bool ShowWelcome { get { return _ini.GetBool("general", "ShowWelcome", true); } set { _ini.Set("general", "ShowWelcome", value); } }
        public bool FirstRunDone { get { return _ini.GetBool("general", "FirstRunDone", false); } set { _ini.Set("general", "FirstRunDone", value); } }
        public bool OsWarningShown { get { return _ini.GetBool("general", "OsWarningShown", false); } set { _ini.Set("general", "OsWarningShown", value); } }
        public string LastUpdateCheck { get { return _ini.Get("general", "LastUpdateCheck", ""); } set { _ini.Set("general", "LastUpdateCheck", value); } }
        public string SkippedVersion { get { return _ini.Get("general", "SkippedVersion", ""); } set { _ini.Set("general", "SkippedVersion", value); } }

        // ---------- services ----------
        /// <summary>"apache" or "nginx".</summary>
        public string WebServer { get { var v = _ini.Get("services", "WebServer", "apache").ToLowerInvariant(); return v == "nginx" ? "nginx" : "apache"; } set { _ini.Set("services", "WebServer", value); } }
        /// <summary>Mail catcher: "mailpit", "mailhog" or "smtp4dev".</summary>
        public string MailTool
        {
            get { var v = _ini.Get("services", "MailTool", AppInfo.IsLegacy ? "mailhog" : "mailpit").ToLowerInvariant(); return v == "mailhog" || v == "smtp4dev" ? v : "mailpit"; }
            set { _ini.Set("services", "MailTool", value); }
        }
        /// <summary>Automatic database backup interval in hours (0 = off, the default).</summary>
        public int AutoBackupHours { get { return _ini.GetInt("mysql", "AutoBackupHours", 0); } set { _ini.Set("mysql", "AutoBackupHours", value); } }
        public int AutoBackupKeep { get { return Math.Max(1, _ini.GetInt("mysql", "AutoBackupKeep", 7)); } set { _ini.Set("mysql", "AutoBackupKeep", value); } }
        public string LastAutoBackup { get { return _ini.Get("mysql", "LastAutoBackup", ""); } set { _ini.Set("mysql", "LastAutoBackup", value); } }
        public bool AllowLan { get { return _ini.GetBool("services", "AllowLan", false); } set { _ini.Set("services", "AllowLan", value); } }
        public int HttpPort { get { return _ini.GetInt("services", "HttpPort", 80); } set { _ini.Set("services", "HttpPort", value); } }
        public int HttpsPort { get { return _ini.GetInt("services", "HttpsPort", 443); } set { _ini.Set("services", "HttpsPort", value); } }
        public int MysqlPort { get { return _ini.GetInt("services", "MysqlPort", 3306); } set { _ini.Set("services", "MysqlPort", value); } }
        public int RedisPort { get { return _ini.GetInt("services", "RedisPort", 6379); } set { _ini.Set("services", "RedisPort", value); } }
        public int MailpitWebPort { get { return _ini.GetInt("services", "MailpitWebPort", 8025); } set { _ini.Set("services", "MailpitWebPort", value); } }
        public int MailpitSmtpPort { get { return _ini.GetInt("services", "MailpitSmtpPort", 1025); } set { _ini.Set("services", "MailpitSmtpPort", value); } }
        /// <summary>First FastCGI port for PHP. Every installed PHP version gets its own port from here.</summary>
        public int PhpBasePort { get { return _ini.GetInt("services", "PhpBasePort", 9100); } set { _ini.Set("services", "PhpBasePort", value); } }
        public int PhpChildren { get { return _ini.GetInt("services", "PhpChildren", 6); } set { _ini.Set("services", "PhpChildren", value); } }

        /// <summary>Is the service part of "Start all"?</summary>
        public bool IsEnabled(string service)
        {
            bool def = service == "web" || service == "mysql";
            return _ini.GetBool("enabled", service, def);
        }
        public void SetEnabled(string service, bool on) { _ini.Set("enabled", service, on); }

        // ---------- active package versions ----------
        public string GetVersion(string type) { return _ini.Get("versions", type, ""); }
        public void SetVersion(string type, string folder) { _ini.Set("versions", type, folder); }

        // ---------- MySQL root password (encrypted with Windows DPAPI, current user) ----------
        public string MysqlRootPassword
        {
            get
            {
                var v = _ini.Get("mysql", "RootPassword", "");
                if (v == "") return "";
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), null, DataProtectionScope.CurrentUser)); }
                catch { return ""; }
            }
            set
            {
                if (string.IsNullOrEmpty(value)) { _ini.Set("mysql", "RootPassword", ""); return; }
                _ini.Set("mysql", "RootPassword", Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)));
            }
        }
    }
}
