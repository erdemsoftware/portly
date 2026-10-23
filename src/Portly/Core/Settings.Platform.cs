using System;
using System.Security.Cryptography;
using System.Text;

namespace Portly.Core
{
    /// <summary>Settings of the "Platform" group: Windows service mode.</summary>
    public partial class Settings
    {
        /// <summary>
        /// The Windows service process never writes portly.ini (the GUI owns it); it only reads it again on "reload".
        /// It also keeps a decrypted root password in memory that must not reach the file.
        /// </summary>
        public static bool ReadOnly;

        /// <summary>Web server, PHP and databases run in the "Portly" Windows service (Settings > General, feature "serviceMode").</summary>
        public bool RunAsService { get { return _ini.GetBool("service", "Enabled", false); } set { _ini.Set("service", "Enabled", value); } }

        /// <summary>SID of the Windows user who may control the service (start / stop, named pipe).</summary>
        public string ServiceControllerSid { get { return _ini.Get("service", "ControllerSid", ""); } set { _ini.Set("service", "ControllerSid", value); } }

        /// <summary>
        /// Copy of the MySQL root password for the service, which runs as LocalSystem and cannot read the
        /// per-user DPAPI value. DPAPI LocalMachine scope: any process on this PC that can read portly.ini can
        /// decrypt it, so it is only written while service mode is on and cleared when it is switched off.
        /// </summary>
        public string MachineRootPassword
        {
            get
            {
                var v = _ini.Get("service", "RootPassword", "");
                if (v == "") return "";
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), Entropy, DataProtectionScope.LocalMachine)); }
                catch { return ""; }
            }
            set
            {
                if (string.IsNullOrEmpty(value)) { _ini.Set("service", "RootPassword", ""); return; }
                _ini.Set("service", "RootPassword", Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine)));
            }
        }

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Portly service mode");
    }
}
