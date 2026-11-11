using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Portly.Config;
using Portly.Core;

namespace Portly.CoreTests
{
    static partial class Program
    {
        static X509Certificate2 FakeCa(string cn)
        {
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=" + cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            }
        }

        static void Suite_CertsLinuxScript()
        {
            var ca = FakeCa("Portly Local CA (test)");
            var dir = Path.Combine(Paths.Tmp, "anchors-test");
            var plan = new Certs.LinuxTrustPlan { AnchorDir = dir, UpdateTool = OS.IsWindows ? "cmd" : "true" };
            var args = Certs.LinuxTrustArgs(ca, plan);
            Check("trust args: script, then fixed paths and the PEM", args[0] == "-c" && args[1] == Certs.LinuxTrustScript && args[4] == plan.AnchorFile(ca.Thumbprint) && args[5].StartsWith("-----BEGIN CERTIFICATE-----"));
            bool refused = false;
            try { Certs.LinuxTrustArgs(FakeCa("Evil CA"), plan); } catch (InvalidOperationException) { refused = true; }
            Check("trust args refuse a certificate that is not a Portly CA", refused);
            refused = false;
            try { Certs.LinuxUntrustArgs("../../etc/passwd", plan); } catch (ArgumentException) { refused = true; }
            Check("untrust args refuse anything but a thumbprint", refused);
            if (OS.IsWindows) return;

            // the root script itself, run as the user against a temp folder (no pkexec)
            var p = Process.Start(new ProcessStartInfo("/bin/sh", args) { UseShellExecute = false, RedirectStandardError = true });
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            var file = plan.AnchorFile(ca.Thumbprint);
            Check("root script writes the certificate", p.ExitCode == 0 && File.Exists(file), err);
            if (File.Exists(file))
            {
                var back = X509Certificate2.CreateFromPem(File.ReadAllText(file));
                Check("written certificate is the same one", back.Thumbprint == ca.Thumbprint);
                Check("written certificate is world-readable (0644)", File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
            }
            var tricky = new[] { "-c", Certs.LinuxTrustScript, "sh", dir, Path.Combine(dir, "x.crt"), "$(touch " + Path.Combine(dir, "pwned") + ")'; touch " + Path.Combine(dir, "pwned2") + "'", "true" };
            var q = Process.Start(new ProcessStartInfo("/bin/sh", tricky) { UseShellExecute = false });
            q.WaitForExit(30000);
            Check("root script never runs its arguments as commands", !File.Exists(Path.Combine(dir, "pwned")) && !File.Exists(Path.Combine(dir, "pwned2")));
        }
    }
}
