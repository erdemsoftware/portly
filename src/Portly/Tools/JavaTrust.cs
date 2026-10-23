using System;
using System.IO;
using System.Linq;
using Portly.Config;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>
    /// Java ignores the Windows certificate store by default. When a JDK/JRE is found (JAVA_HOME or java on PATH),
    /// the Portly CA can be imported into its "cacerts" truststore with keytool (only on request; admin when the
    /// JDK sits in Program Files).
    /// </summary>
    public static class JavaTrust
    {
        public const string Alias = "portly-local-ca";

        /// <summary>Java home that has keytool.exe, or null.</summary>
        public static string FindHome()
        {
            var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(jh) && File.Exists(Path.Combine(jh.Trim('"'), "bin", "keytool.exe"))) return jh.Trim('"').TrimEnd('\\');
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var d = dir.Trim().Trim('"');
                    if (d == "" || !File.Exists(Path.Combine(d, "java.exe"))) continue;
                    // Oracle's "javapath" folder only holds launchers: keytool lives next to the real java.exe
                    if (File.Exists(Path.Combine(d, "keytool.exe"))) return Path.GetDirectoryName(d.TrimEnd('\\'));
                }
                catch { }
            }
            return null;
        }

        public static string Keytool(string home) { return Path.Combine(home, "bin", "keytool.exe"); }

        /// <summary>lib\security\cacerts (JDK 9+) or jre\lib\security\cacerts (JDK 8).</summary>
        public static string Cacerts(string home)
        {
            foreach (var p in new[] { @"lib\security\cacerts", @"jre\lib\security\cacerts" })
                if (File.Exists(Path.Combine(home, p))) return Path.Combine(home, p);
            return null;
        }

        static string StoreArgs(string cacerts) { return "-keystore \"" + cacerts + "\" -storepass changeit"; }

        public static bool IsImported(string home)
        {
            var cacerts = Cacerts(home);
            if (cacerts == null) return false;
            var r = ProcessUtil.Run(Keytool(home), "-list -noprompt " + StoreArgs(cacerts) + " -alias " + Alias, null, null, 30000);
            return r.Ok;
        }

        /// <summary>The exact command shown in the confirmation before it runs.</summary>
        public static string ImportCommand(string home)
        {
            return "\"" + Keytool(home) + "\" -importcert -noprompt -trustcacerts " + StoreArgs(Cacerts(home)) + " -alias " + Alias + " -file \"" + Certs.CaCertPath + "\"";
        }

        static bool CanWrite(string file)
        {
            try { using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) return true; }
            catch { return false; }
        }

        /// <summary>Returns null on success, else an error text.</summary>
        public static string Import(string home)
        {
            var cacerts = Cacerts(home);
            if (cacerts == null) return I18n.T("java.noCacerts", home);
            Certs.EnsureCa();
            var args = "-importcert -noprompt -trustcacerts " + StoreArgs(cacerts) + " -alias " + Alias + " -file \"" + Certs.CaCertPath + "\"";
            if (CanWrite(cacerts))
            {
                var r = ProcessUtil.Run(Keytool(home), args, null, null, 60000);
                if (!r.Ok && !IsImported(home)) return r.Output.Trim();
            }
            else
            {
                var code = WinUtil.RunElevated(Keytool(home), args);
                if (code == -2) return I18n.T("java.cancelled");
            }
            if (!IsImported(home)) return I18n.T("java.failed");
            Log.Info(I18n.T("java.done", home));
            return null;
        }
    }
}
