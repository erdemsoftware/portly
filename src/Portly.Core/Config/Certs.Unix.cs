using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Portly.Core;

namespace Portly.Config
{
    /// <summary>
    /// Trusting the local CA on macOS and Linux.
    ///   macOS: the login keychain (macOS asks the user for confirmation itself), no administrator rights.
    ///   Linux: the system store through the elevated helper ("--trust-ca"), plus the NSS databases of Chrome / Chromium
    ///          (~/.pki/nssdb) and of every Firefox profile with certutil, as the user.
    /// Windows keeps its own code in Certs.cs. Nothing here runs in tests except the pure command builders.
    /// </summary>
    public static partial class Certs
    {
        /// <summary>What the last trust attempt did, step by step (for the UI to show when something was skipped).</summary>
        public static string LastTrustReport { get; private set; }

        public static string MacKeychain { get { return Path.Combine(UnixIntegration.Home, "Library", "Keychains", "login.keychain-db"); } }

        // ---------------- command builders (pure) ----------------
        public static string[] MacTrustArgs(string crt) { return new[] { "add-trusted-cert", "-r", "trustRoot", "-k", MacKeychain, crt }; }
        /// <summary>Removes the certificate and its trust settings from the login keychain.</summary>
        public static string[] MacUntrustArgs(string thumbprint) { return new[] { "delete-certificate", "-Z", thumbprint.ToUpperInvariant(), "-t", MacKeychain }; }
        public static string[] MacVerifyArgs(string crt) { return new[] { "verify-cert", "-c", crt, "-L", "-q" }; }

        /// <summary>Name of the certificate in an NSS database (the thumbprint keeps replaced authorities apart).</summary>
        public static string NssNickname(string thumbprint) { return "Portly Local CA " + thumbprint.ToUpperInvariant().Substring(0, 12); }
        public static string[] NssAddArgs(string nick, string crt, string dir) { return new[] { "-A", "-n", nick, "-t", "C,,", "-i", crt, "-d", "sql:" + dir }; }
        public static string[] NssFindArgs(string nick, string dir) { return new[] { "-L", "-n", nick, "-d", "sql:" + dir }; }
        public static string[] NssDeleteArgs(string nick, string dir) { return new[] { "-D", "-n", nick, "-d", "sql:" + dir }; }

        /// <summary>How the Linux system store is fed: a folder for certificates and the tool that rebuilds the bundle.</summary>
        public class LinuxTrustPlan
        {
            public string AnchorDir;
            public string UpdateTool;
            public string[] UpdateArgs = new string[0];
            public string AnchorFile(string thumbprint) { return Path.Combine(AnchorDir, "portly-" + thumbprint.ToLowerInvariant() + ".crt"); }
        }

        /// <summary>The store of this distribution: Fedora / RHEL, Arch, openSUSE, Debian / Ubuntu (in that order), or null.</summary>
        public static LinuxTrustPlan LinuxPlan(Func<string, bool> dirExists = null, Func<string, string> which = null)
        {
            dirExists = dirExists ?? Directory.Exists;
            which = which ?? Elevate.Which;
            if (dirExists("/etc/pki/ca-trust/source/anchors") && which("update-ca-trust") != null)
                return new LinuxTrustPlan { AnchorDir = "/etc/pki/ca-trust/source/anchors", UpdateTool = which("update-ca-trust"), UpdateArgs = new string[0] };
            if (dirExists("/etc/ca-certificates/trust-source/anchors") && which("update-ca-trust") != null)
                return new LinuxTrustPlan { AnchorDir = "/etc/ca-certificates/trust-source/anchors", UpdateTool = which("update-ca-trust"), UpdateArgs = new string[0] };
            if (dirExists("/etc/ca-certificates/trust-source/anchors") && which("trust") != null)
                return new LinuxTrustPlan { AnchorDir = "/etc/ca-certificates/trust-source/anchors", UpdateTool = which("trust"), UpdateArgs = new[] { "extract-compat" } };
            if (dirExists("/etc/pki/trust/anchors") && which("update-ca-certificates") != null)
                return new LinuxTrustPlan { AnchorDir = "/etc/pki/trust/anchors", UpdateTool = which("update-ca-certificates"), UpdateArgs = new string[0] };
            if (which("update-ca-certificates") != null || dirExists("/usr/local/share/ca-certificates"))
                return new LinuxTrustPlan { AnchorDir = "/usr/local/share/ca-certificates", UpdateTool = which("update-ca-certificates") ?? "update-ca-certificates", UpdateArgs = new string[0] };
            return null;
        }

        /// <summary>Every folder where an earlier run may have put the authority (to find and remove it).</summary>
        static readonly string[] AllAnchorDirs =
        {
            "/usr/local/share/ca-certificates", "/etc/pki/ca-trust/source/anchors", "/etc/ca-certificates/trust-source/anchors", "/etc/pki/trust/anchors",
        };

        /// <summary>NSS databases of Chrome / Chromium (~/.pki/nssdb, snap, Flatpak) as folders, existing or not.</summary>
        public static List<string> ChromiumNssDirs(string home = null)
        {
            home = home ?? UnixIntegration.Home;
            return new List<string>
            {
                Path.Combine(home, ".pki", "nssdb"),
                Path.Combine(home, "snap", "chromium", "current", ".pki", "nssdb"),
                Path.Combine(home, ".var", "app", "org.chromium.Chromium", ".pki", "nssdb"),
                Path.Combine(home, ".var", "app", "com.google.Chrome", ".pki", "nssdb"),
            };
        }

        /// <summary>The NSS database folders to update: the Chromium ones that exist (the main one is created) and Firefox profiles with a cert9.db.</summary>
        public static List<string> NssDirs(string home = null)
        {
            home = home ?? UnixIntegration.Home;
            var dirs = new List<string>();
            var chromium = ChromiumNssDirs(home);
            foreach (var d in chromium)
                if (File.Exists(Path.Combine(d, "cert9.db")) || d == chromium[0]) dirs.Add(d);
            foreach (var p in Tools.FirefoxTrust.ProfileDirs())
                if (File.Exists(Path.Combine(p, "cert9.db"))) dirs.Add(p);
            return dirs.Distinct().ToList();
        }

        static X509Certificate2 LoadCert(string file) { return new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(file))); }

        // ---------------- trust ----------------
        static bool IsTrustedUnix(string crt)
        {
            try
            {
                if (OS.IsMac) return UnixIntegration.Run("security", MacVerifyArgs(crt), 30000).Ok;
                var thumb = LoadCert(crt).Thumbprint;
                var plan = LinuxPlan();
                bool system = AllAnchorDirs.Any(d => File.Exists(Path.Combine(d, "portly-" + thumb.ToLowerInvariant() + ".crt")));
                if (system) return true;
                var certutil = Elevate.Which("certutil");
                if (certutil == null) return false;
                var nss = NssDirs().Where(d => Directory.Exists(d) && File.Exists(Path.Combine(d, "cert9.db"))).ToList();
                return nss.Count > 0 && nss.All(d => UnixIntegration.Run(certutil, NssFindArgs(NssNickname(thumb), d), 15000).Ok);
            }
            catch { return false; }
        }

        static bool TrustFileUnix(string file)
        {
            var report = new List<string>();
            bool ok;
            try
            {
                var ca = LoadCert(file);
                var thumb = ca.Thumbprint;
                if (OS.IsMac)
                {
                    // macOS shows its own confirmation (password / Touch ID) for the keychain change
                    var r = UnixIntegration.Run("security", MacTrustArgs(file), 180000);
                    ok = r.Ok && UnixIntegration.Run("security", MacVerifyArgs(file), 30000).Ok;
                    report.Add(ok ? "login keychain: trusted" : "login keychain: not trusted (" + (r.TimedOut ? "no answer" : r.Output) + ")");
                    // Firefox reads the keychain only when told to
                    if (ok) Tools.FirefoxTrust.EnableAll();
                }
                else
                {
                    bool system = false;
                    var plan = LinuxPlan();
                    if (plan == null) report.Add("system store: no known tool (update-ca-certificates / update-ca-trust) found");
                    else if (!Elevate.Available) report.Add("system store: pkexec is not installed, no administrator prompt possible");
                    else
                    {
                        var code = LinuxTrustElevated(ca, plan);
                        system = code == 0 && File.Exists(plan.AnchorFile(thumb));
                        report.Add(system ? "system store: trusted" : code == Elevate.Cancelled ? "system store: administrator prompt refused" : "system store: failed (" + code + ")");
                    }
                    bool nss = TrustNss(file, thumb, report);
                    ok = system || nss;
                }
            }
            catch (Exception ex) { report.Add("error: " + ex.Message); ok = false; }
            LastTrustReport = string.Join("; ", report);
            if (ok) Log.Info(I18n.T("log.caTrusted")); else Log.Warn("Trust CA: " + LastTrustReport);
            return ok;
        }

        /// <summary>Add the CA to the NSS databases. True when at least one database has it afterwards.</summary>
        static bool TrustNss(string file, string thumb, List<string> report)
        {
            var certutil = Elevate.Which("certutil");
            if (certutil == null)
            {
                report.Add("browsers: certutil not found (install libnss3-tools / nss-tools); Chrome and Firefox were not set up");
                return false;
            }
            var nick = NssNickname(thumb);
            int done = 0, failed = 0;
            foreach (var dir in NssDirs())
            {
                try
                {
                    if (!File.Exists(Path.Combine(dir, "cert9.db")))
                    {
                        Directory.CreateDirectory(dir);
                        UnixIntegration.Run(certutil, new[] { "-N", "-d", "sql:" + dir, "--empty-password" }, 30000);
                    }
                    // a changed authority under the same name cannot happen (the nickname holds the thumbprint): add only when missing
                    if (UnixIntegration.Run(certutil, NssFindArgs(nick, dir), 15000).Ok) { done++; continue; }
                    if (UnixIntegration.Run(certutil, NssAddArgs(nick, file, dir), 30000).Ok) done++; else failed++;
                }
                catch { failed++; }
            }
            report.Add("browsers: " + done + " database(s) updated" + (failed > 0 ? ", " + failed + " failed" : ""));
            return done > 0;
        }

        /// <summary>Runs in the elevated helper (Linux: "Portly --trust-ca file"). Only accepts the Portly CA.</summary>
        static int TrustCaElevatedUnix(string file)
        {
            try
            {
                var ca = LoadCert(file);
                if (!ca.Subject.Contains("Portly Local CA") || ca.Subject != ca.Issuer) return 3;
                if (OS.IsMac) return 3;   // macOS uses the user's keychain, never this helper
                var plan = LinuxPlan();
                if (plan == null) return 4;
                Directory.CreateDirectory(plan.AnchorDir);
                var dest = plan.AnchorFile(ca.Thumbprint);
                // rewritten from the parsed certificate: nothing else from the user's file reaches the system store
                File.WriteAllText(dest, Pem("CERTIFICATE", ca.RawData), new UTF8Encoding(false));
#if !NETFRAMEWORK
                try { File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead); } catch { }
#endif
                var r = UnixIntegration.Run(plan.UpdateTool, plan.UpdateArgs, 120000);
                return r.Ok ? 0 : 2;
            }
            catch { return 2; }
        }

        // Linux runs a fixed /bin/sh script as root instead of Portly itself: an AppImage lives on the user's FUSE mount,
        // which root cannot open. The certificate is checked and re-encoded here; root only gets its PEM text (argv cannot
        // change after the prompt) and fixed system paths built from the hex thumbprint.
        public const string LinuxTrustScript = "mkdir -p \"$1\" && printf '%s\n' \"$3\" > \"$2\" && chmod 644 \"$2\" && shift 3 && exec \"$@\"";
        public const string LinuxUntrustScript = "n=\"$1\"; shift; for d in /usr/local/share/ca-certificates /etc/pki/ca-trust/source/anchors /etc/ca-certificates/trust-source/anchors /etc/pki/trust/anchors; do rm -f \"$d/$n\"; done; exec \"$@\"";

        public static List<string> LinuxTrustArgs(X509Certificate2 ca, LinuxTrustPlan plan)
        {
            if (!ca.Subject.Contains("Portly Local CA") || ca.Subject != ca.Issuer) throw new InvalidOperationException("not a Portly CA");
            var a = new List<string> { "-c", LinuxTrustScript, "sh", plan.AnchorDir, plan.AnchorFile(ca.Thumbprint), Pem("CERTIFICATE", ca.RawData).Trim(), plan.UpdateTool };
            a.AddRange(plan.UpdateArgs);
            return a;
        }

        public static List<string> LinuxUntrustArgs(string thumbprint, LinuxTrustPlan plan)
        {
            if (!Regex.IsMatch(thumbprint ?? "", "^[0-9A-Fa-f]{40}$")) throw new ArgumentException("thumbprint");
            var a = new List<string> { "-c", LinuxUntrustScript, "sh", "portly-" + thumbprint.ToLowerInvariant() + ".crt", plan.UpdateTool };
            a.AddRange(plan.UpdateTool.EndsWith("update-ca-certificates", StringComparison.Ordinal) ? new[] { "--fresh" } : plan.UpdateArgs);
            return a;
        }

        static int LinuxTrustElevated(X509Certificate2 ca, LinuxTrustPlan plan) { return Elevate.Run("/bin/sh", LinuxTrustArgs(ca, plan).ToArray()); }

        static int LinuxUntrustElevated(string thumbprint)
        {
            var plan = LinuxPlan();
            return plan == null ? 4 : Elevate.Run("/bin/sh", LinuxUntrustArgs(thumbprint, plan).ToArray());
        }

        // ---------------- untrust ----------------
        static bool UntrustUnix(string thumbprint)
        {
            try
            {
                if (!Regex.IsMatch(thumbprint ?? "", "^[0-9A-Fa-f]{40}$")) return false;
                if (OS.IsMac)
                {
                    var r = UnixIntegration.Run("security", MacUntrustArgs(thumbprint), 60000);
                    return r.Ok || r.ExitCode == 44;   // 44 = not in the keychain (anymore)
                }
                var name = "portly-" + thumbprint.ToLowerInvariant() + ".crt";
                bool inSystem = AllAnchorDirs.Any(d => File.Exists(Path.Combine(d, name)));
                if (inSystem && LinuxUntrustElevated(thumbprint) != 0) { /* still removed from the browsers below */ }
                var certutil = Elevate.Which("certutil");
                if (certutil != null)
                    foreach (var dir in NssDirs().Where(d => File.Exists(Path.Combine(d, "cert9.db"))))
                        UnixIntegration.Run(certutil, NssDeleteArgs(NssNickname(thumbprint), dir), 15000);
                return !AllAnchorDirs.Any(d => File.Exists(Path.Combine(d, name)));
            }
            catch (Exception ex) { Log.Warn("Untrust CA: " + ex.Message); return false; }
        }

        /// <summary>Runs in the elevated helper (Linux: "Portly --untrust-ca thumbprint"). Only removes a file Portly made.</summary>
        static int UntrustCaElevatedUnix(string thumbprint)
        {
            try
            {
                if (!Regex.IsMatch(thumbprint ?? "", "^[0-9A-Fa-f]{40}$")) return 3;
                if (OS.IsMac) return 3;
                var name = "portly-" + thumbprint.ToLowerInvariant() + ".crt";
                bool removed = false;
                foreach (var d in AllAnchorDirs)
                {
                    var f = Path.Combine(d, name);
                    if (!File.Exists(f)) continue;
                    File.Delete(f);
                    removed = true;
                }
                if (!removed) return 0;
                var plan = LinuxPlan();
                if (plan == null) return 4;
                var args = plan.UpdateTool.EndsWith("update-ca-certificates", StringComparison.Ordinal) ? new[] { "--fresh" } : plan.UpdateArgs;
                return UnixIntegration.Run(plan.UpdateTool, args, 120000).Ok ? 0 : 2;
            }
            catch { return 2; }
        }
    }
}
