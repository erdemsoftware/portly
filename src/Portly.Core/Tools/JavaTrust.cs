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

        /// <summary>Java home that has keytool (keytool.exe on Windows), or null.</summary>
        public static string FindHome()
        {
            var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(jh) && File.Exists(Path.Combine(jh.Trim('"'), "bin", OS.Exe("keytool")))) return jh.Trim('"').TrimEnd('\\', '/');
            if (OS.IsMac)
            {
                // /usr/libexec/java_home prints the home of the Java macOS considers current
                var r = ProcessUtil.Run("/usr/libexec/java_home", "", "/", null, 10000);
                var home = r.Ok ? r.Output.Trim() : "";
                if (home != "" && File.Exists(Path.Combine(home, "bin", "keytool"))) return home;
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var d = dir.Trim().Trim('"');
                    if (d == "" || !File.Exists(Path.Combine(d, OS.Exe("java")))) continue;
                    if (!OS.IsWindows) { var h = HomeOfLauncher(Path.Combine(d, "java")); if (h != null) return h; continue; }
                    // Oracle's "javapath" folder only holds launchers: keytool lives next to the real java.exe
                    if (File.Exists(Path.Combine(d, "keytool.exe"))) return Path.GetDirectoryName(d.TrimEnd('\\'));
                }
                catch { }
            }
            return null;
        }

        /// <summary>macOS / Linux: /usr/bin/java is usually a link into the real JDK; the home is two folders above the real file.</summary>
        static string HomeOfLauncher(string java)
        {
#if !NETFRAMEWORK
            var target = new FileInfo(java).ResolveLinkTarget(true);
            var real = target != null ? target.FullName : java;
#else
            var real = java;
#endif
            var home = Path.GetDirectoryName(Path.GetDirectoryName(real));
            return home != null && File.Exists(Path.Combine(home, "bin", "keytool")) ? home : null;
        }

        public static string Keytool(string home) { return Path.Combine(home, "bin", OS.Exe("keytool")); }

        /// <summary>lib/security/cacerts (JDK 9+) or jre/lib/security/cacerts (JDK 8).</summary>
        public static string Cacerts(string home)
        {
            foreach (var p in new[] { Path.Combine("lib", "security", "cacerts"), Path.Combine("jre", "lib", "security", "cacerts") })
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

        /// <summary>The store's copy is the current authority (SHA-256 fingerprint: hex looks the same in every language).</summary>
        static bool HoldsCurrentCa(string home)
        {
            var cacerts = Cacerts(home);
            if (cacerts == null) return false;
            var r = ProcessUtil.Run(Keytool(home), "-list -noprompt " + StoreArgs(cacerts) + " -alias " + Alias, null, null, 30000);
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var ca = new System.Security.Cryptography.X509Certificates.X509Certificate2(Certs.CaCertPath))
                return r.Ok && r.Output.Contains(BitConverter.ToString(sha.ComputeHash(ca.RawData)).Replace("-", ":"));
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
            // keytool refuses an alias that exists: a replaced authority goes first
            var remove = IsImported(home) ? "-delete -noprompt " + StoreArgs(cacerts) + " -alias " + Alias : null;
            if (CanWrite(cacerts))
            {
                if (remove != null) ProcessUtil.Run(Keytool(home), remove, null, null, 60000);
                var r = ProcessUtil.Run(Keytool(home), args, null, null, 60000);
                if (!r.Ok && !IsImported(home)) return r.Output.Trim();
            }
            else
            {
                if (!OS.IsWindows)
                {
                    // one prompt for both steps; every part is quoted for the shell, nothing from outside is interpreted
                    var kt = Keytool(home);
                    var del = new[] { "-delete", "-noprompt", "-keystore", cacerts, "-storepass", "changeit", "-alias", Alias };
                    var add = new[] { "-importcert", "-noprompt", "-trustcacerts", "-keystore", cacerts, "-storepass", "changeit", "-alias", Alias, "-file", Certs.CaCertPath };
                    var script = (remove != null ? Elevate.ShellCommand(kt, del) + " ; " : "") + Elevate.ShellCommand(kt, add);
                    var ucode = Elevate.Run("/bin/sh", "-c", script);
                    if (ucode == Elevate.Cancelled) return I18n.T("java.cancelled");
                    if (!HoldsCurrentCa(home)) return I18n.T("java.failed");
                    Log.Info(I18n.T("java.done", home));
                    return null;
                }
                var tool = "\"" + Keytool(home) + "\" ";
                var code = remove != null ? WinUtil.RunElevated("cmd.exe", "/c \"" + tool + remove + " & " + tool + args + "\"") : WinUtil.RunElevated(Keytool(home), args);
                if (code == -2) return I18n.T("java.cancelled");
            }
            if (!HoldsCurrentCa(home)) return I18n.T("java.failed");
            Log.Info(I18n.T("java.done", home));
            return null;
        }
    }
}
