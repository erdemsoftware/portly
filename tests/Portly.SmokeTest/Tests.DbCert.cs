using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.SmokeTest
{
    /// <summary>
    /// --db-cert: root password on real MySQL / MariaDB servers (own data folders under build\smoketest, port 33987)
    /// and the name-constrained local CA, checked by real TLS clients. Never touches the Windows trust store.
    /// </summary>
    static partial class Program
    {
        const int DbCertPort = 33987;
        const string GitOpenssl = @"C:\Program Files\Git\usr\bin\openssl.exe";
        const string WinCurl = @"C:\Windows\System32\curl.exe";
        const string MainBuildBin = @"E:\projeler\Portly\build\smoketest\bin";
        const string PortlyBin = @"C:\Portly\bin";

        static string _phpExe, _nodeExe;

        static int DbCertOnly()
        {
            Net.Init();
            Paths.EnsureAll();
            Settings.Load();
            Settings.RootPasswordChanged += ToolApps.RefreshPhpMyAdmin;
            I18n.Load("en");
            Log.Written += l => { if (l.Contains("WARN") || l.Contains("ERROR")) Console.WriteLine("    | " + l); };
            var s = Settings.Current;
            s.MysqlPort = DbCertPort; s.HttpPort = 8791; s.HttpsPort = 8794; s.Tld = "portly"; s.Ssl = true;
            s.HostTemplate = "{name}.{tld}";
            s.DocumentRoot = Path.Combine(Paths.Root, "www");
            s.AllowLan = false;
            Directory.CreateDirectory(s.DocumentRoot);
            foreach (var d in Directory.GetDirectories(s.DocumentRoot)) Directory.Delete(d, true);
            if (File.Exists(Paths.SitesFile)) File.Delete(Paths.SitesFile);
            s.Save();
            Environment.SetEnvironmentVariable("JAVA_HOME", null);
            Console.WriteLine("Root: " + Paths.Root);
            _phpExe = Directory.GetFiles(Path.Combine(PortlyBin, "php"), "php.exe", SearchOption.AllDirectories).FirstOrDefault();
            _nodeExe = Directory.GetFiles(Path.Combine(PortlyBin, "nodejs"), "node.exe", SearchOption.AllDirectories).FirstOrDefault();
            Console.WriteLine("openssl: " + First(Run(GitOpenssl, "version")));
            Console.WriteLine("curl (schannel): " + First(Run(WinCurl, "--version")));
            Console.WriteLine("node: " + First(Run(_nodeExe, "--version")) + "   php: " + First(Run(_phpExe, "-n -v")));
            Console.WriteLine("Windows: " + Environment.OSVersion.VersionString);
            var only = Environment.GetEnvironmentVariable("PORTLY_DBCERT_ONLY") ?? "";
            try
            {
                if (only != "db") DbCertCaTests().GetAwaiter().GetResult();
                if (only != "ca") DbCertDatabaseTests().GetAwaiter().GetResult();
                DbCertTransferTests();
            }
            catch (Exception ex) { Check("db-cert run", false, ex.ToString()); }
            finally
            {
                try { ServiceManager.MySql.StopAsync().Wait(40000); } catch { }
                s = Settings.Current;
                s.MysqlRootPassword = "";
                s.AllowLan = false;
                s.Save();
            }
            Console.WriteLine();
            Console.WriteLine(_ok + " passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        static RunResult Run(string exe, string args, int timeout = 60000, IDictionary<string, string> env = null, string stdin = null)
        {
            return ProcessUtil.Run(exe, args, Path.GetDirectoryName(exe), env, timeout, stdin);
        }

        static string First(RunResult r) { return (r.Output.Split('\n').FirstOrDefault() ?? "").Trim(); }

        // ===================================================================== database
        static void LinkPackage(string type, string folder, string source)
        {
            var dst = Path.Combine(Paths.BinOf(type), folder);
            Directory.CreateDirectory(Paths.BinOf(type));
            if (!Directory.Exists(dst)) Run("cmd.exe", "/c mklink /J \"" + dst + "\" \"" + source + "\"");
        }

        /// <summary>The junctions must be removed as links: a recursive delete would go into the real package.</summary>
        static void UnlinkPackages()
        {
            foreach (var type in new[] { "mysql", "mariadb" })
                if (Directory.Exists(Paths.BinOf(type)))
                    foreach (var d in Directory.GetDirectories(Paths.BinOf(type)))
                        if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d, false);
        }

        static string RootsWithoutPassword()
        {
            var r = Database.Sql("SELECT COUNT(*) FROM mysql.user WHERE User = 'root' AND authentication_string = '';");
            return r.Ok ? Database.Clean(r).Trim() : "?" + r.Output;
        }

        static int RootAccountCount()
        {
            int n;
            return int.TryParse(Database.Clean(Database.Sql("SELECT COUNT(*) FROM mysql.user WHERE User = 'root';")).Trim(), out n) ? n : -1;
        }

        /// <summary>What phpMyAdmin and every PHP project do: mysqli over TCP (MySQL 8+: caching_sha2 without TLS needs the RSA path).</summary>
        static RunResult PhpLogin(string password)
        {
            var ext = Path.Combine(Path.GetDirectoryName(_phpExe), "ext");
            var code = "$m = @new mysqli('127.0.0.1', 'root', base64_decode(getenv('T')), '', " + DbCertPort + "); echo $m->connect_errno ? 'FAIL ' . $m->connect_error : 'OK ' . $m->server_info;";
            return Run(_phpExe, "-n -d extension_dir=\"" + ext + "\" -d extension=openssl -d extension=mysqli -r \"" + code + "\"", 30000,
                new Dictionary<string, string> { { "T", Convert.ToBase64String(Encoding.UTF8.GetBytes(password)) } });
        }

        static async Task StartClean(string engine, string folder)
        {
            var s = Settings.Current;
            await ServiceManager.MySql.StopAsync();
            s.DbEngine = engine;
            s.SetVersion(engine, folder);
            s.Save();
            var data = MySqlService.DataDirFor(folder);
            if (Directory.Exists(data)) Directory.Delete(data, true);
            await ServiceManager.MySql.StartAsync();
        }

        static async Task DbCertDatabaseTests()
        {
            var s = Settings.Current;
            var engines = new List<Tuple<string, string, string>>
            {
                Tuple.Create("mysql", "mysql-8.4.11-winx64", Path.Combine(PortlyBin, "mysql", "mysql-8.4.11-winx64")),
                Tuple.Create("mysql", "mysql-9.7.4-winx64", Path.Combine(MainBuildBin, "mysql", "mysql-9.7.4-winx64")),
                Tuple.Create("mariadb", "mariadb-11.8.9-winx64", Path.Combine(MainBuildBin, "mariadb", "mariadb-11.8.9-winx64")),
                Tuple.Create("mariadb", "mariadb-12.3.3-winx64", Path.Combine(MainBuildBin, "mariadb", "mariadb-12.3.3-winx64")),
            }.Where(e => Directory.Exists(e.Item3)).ToList();

            s.MysqlRootPassword = ""; s.Save();
            // a quote, a double quote, a backslash, a space, '#' and non-ASCII: option file, SQL string and stdin all count
            const string tricky = "Pa'ss\"w\\ord #1$ş€";
            try
            {
                foreach (var e in engines)
                {
                    var label = e.Item2 + ": ";
                    LinkPackage(e.Item1, e.Item2, e.Item3);
                    await StartClean(e.Item1, e.Item2);
                    Check(label + "server runs with an empty root", ServiceManager.MySql.State == ServiceState.Running && Database.LoginWorks(""), ServiceManager.MySql.LastError ?? "");
                    Console.WriteLine("    " + Database.Clean(Database.Sql("SELECT VERSION();")) + ", root accounts: " + RootAccountCount());

                    // set
                    var err = Database.ChangeRootPassword(tricky);
                    Check(label + "set a password with special characters", err == null, err ?? "");
                    Check(label + "the new password logs in, an empty one does not", Database.LoginWorks(tricky) && !Database.LoginWorks(""));
                    Check(label + "no root account keeps an empty password", RootsWithoutPassword() == "0", RootsWithoutPassword() + " of " + RootAccountCount());
                    Check(label + "stored encrypted", s.Raw.Get("mysql", "RootPassword") != "" && !File.ReadAllText(Paths.SettingsFile).Contains("Pa'ss") && s.MysqlRootPassword == tricky);
                    var php = PhpLogin(tricky);
                    Check(label + "PHP mysqli logs in with it over TCP", php.Output.StartsWith("OK"), php.Output);
                    Check(label + "PHP mysqli is refused with an empty password", !PhpLogin("").Output.StartsWith("OK"));

                    // every Portly connection
                    Check(label + "mysqladmin", Database.Admin("status", 15000).Ok);
                    var check = Database.Tool(e.Item1 == "mariadb" ? "mariadb-check.exe" : "mysqlcheck.exe", "--check --databases mysql", 60000);
                    Check(label + "mysqlcheck", check.Ok, check.Ok ? "" : check.Output);
                    Check(label + "create database and user", Database.CreateDatabase("dbcert_x") == null && Database.OtherUserCount() >= 0);
                    var backup = Database.BackupAll("dbcert-");
                    Check(label + "mysqldump backup", File.Exists(backup) && new FileInfo(backup).Length > 1000);
                    File.Delete(backup);
                    var sql = Path.Combine(Paths.Tmp, "dbcert.sql");
                    File.WriteAllText(sql, "USE dbcert_x; CREATE TABLE t (id INT);");
                    var imp = SqlImport.Run(sql, null, false, null, CancellationToken.None);
                    Check(label + "SQL import", imp.Errors == 0 && Database.Clean(Database.Sql("SHOW TABLES FROM dbcert_x;")) == "t", imp.Message ?? "");
                    File.Delete(sql);

                    // a change that cannot be proven must leave everything as it was
                    err = Database.ChangeRootPassword("Second-Pass-1", p => false);
                    Check(label + "failed verification rolls back", err != null && Database.LoginWorks(tricky) && !Database.LoginWorks("Second-Pass-1") && s.MysqlRootPassword == tricky, err ?? "");
                    Check(label + "... on every account", RootsWithoutPassword() == "0");
                    err = Database.ChangeRootPassword("bad\nline");
                    Check(label + "control characters are refused", err != null && Database.LoginWorks(tricky));

                    // a password the server itself may refuse (caching_sha2 has a length limit): whichever way, root stays usable
                    var longPass = new string('x', 400);
                    err = Database.ChangeRootPassword(longPass);
                    Console.WriteLine("    400 characters: " + (err ?? "accepted"));
                    Check(label + "a refused password leaves the old one", err == null ? Database.LoginWorks(longPass) : Database.LoginWorks(tricky) && !Database.LoginWorks(longPass) && s.MysqlRootPassword == tricky, err ?? "");
                    if (err == null) Check(label + "back to the test password", Database.ChangeRootPassword(tricky) == null);

                    // a wrong stored password: nothing happens, and it says what to do
                    s.MysqlRootPassword = "wrong";
                    err = Database.ChangeRootPassword("Third-Pass-1");
                    Check(label + "wrong stored password changes nothing", err != null && !Database.LoginWorks("Third-Pass-1"), err ?? "");
                    s.MysqlRootPassword = tricky;

                    // change, then a clean stop with the password and a start
                    err = Database.ChangeRootPassword("Second-Pass-2");
                    Check(label + "change: the old password stops working", err == null && Database.LoginWorks("Second-Pass-2") && !Database.LoginWorks(tricky), err ?? "");
                    await ServiceManager.MySql.StopAsync();
                    Check(label + "mysqladmin shutdown with the password is clean", ServiceManager.MySql.LastStopClean);
                    await ServiceManager.MySql.StartAsync();
                    Check(label + "restart keeps the login", ServiceManager.MySql.State == ServiceState.Running && Database.LoginWorks("Second-Pass-2"));

                    // forgotten password
                    s.MysqlRootPassword = "forgotten"; s.Save();
                    err = await ServiceManager.MySql.ResetRootPasswordAsync();
                    Check(label + "reset opens every root account", err == null && Database.LoginWorks("") && RootsWithoutPassword() == RootAccountCount().ToString() && s.MysqlRootPassword == "", err ?? RootsWithoutPassword());

                    // set and remove
                    err = Database.ChangeRootPassword("Fourth-Pass-1");
                    var err2 = Database.ChangeRootPassword("");
                    Check(label + "remove empties every root account", err == null && err2 == null && Database.LoginWorks("") && s.MysqlRootPassword == "" && !s.HasRootPassword
                        && RootsWithoutPassword() == RootAccountCount().ToString(), (err ?? "") + (err2 ?? ""));

                    // a new data folder of another version starts with an empty root: the stored password goes along
                    err = Database.ChangeRootPassword("Carried-Over-1");
                    Check(label + "password set for the next test", err == null, err ?? "");
                    await StartClean(e.Item1, e.Item2);
                    Check(label + "a fresh data folder gets the stored password", ServiceManager.MySql.State == ServiceState.Running && Database.LoginWorks("Carried-Over-1") && !Database.LoginWorks("") && RootsWithoutPassword() == "0", ServiceManager.MySql.LastError ?? RootsWithoutPassword());
                    err = Database.ChangeRootPassword("");
                    Check(label + "cleared again", err == null, err ?? "");

                    if (e == engines[0]) GeneralLogFinding(s);
                    await PhpMyAdminChecks(s, label);
                    await ServiceManager.MySql.StopAsync();
                }
            }
            finally
            {
                await ServiceManager.MySql.StopAsync();
                UnlinkPackages();
            }
        }

        /// <summary>
        /// Honest record of what mysql.ini could not close: with a fixed general_log_file in the option file root can still
        /// move it anywhere (secure-file-priv does not apply), so the template stays without it.
        /// </summary>
        static void GeneralLogFinding(Settings s)
        {
            var f = Paths.Fwd(Path.Combine(Paths.Root, "www", "dbcert-general-log.txt"));
            var r = Database.Sql("SET GLOBAL general_log_file = '" + f + "'; SET GLOBAL general_log = 1; SELECT 'hello-from-log'; SET GLOBAL general_log = 0; SET GLOBAL general_log_file = 'general.log';");
            Check("finding: root can still point general_log at any folder (only a password stops this)", r.Ok && File.Exists(f) && File.ReadAllText(f).Contains("hello-from-log"), r.Ok ? "" : r.Output);
            try { File.Delete(f); } catch { }
        }

        static void CopyDir(string from, string to)
        {
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(to + d.Substring(from.Length));
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, to + f.Substring(from.Length), true);
        }

        /// <summary>The login mode of phpMyAdmin's config, then the real login page through PHP's own web server.</summary>
        static async Task PhpMyAdminChecks(Settings s, string label)
        {
            var src = Directory.GetDirectories(Path.Combine(MainBuildBin, "phpmyadmin")).FirstOrDefault();
            if (src == null) { Console.WriteLine("    phpMyAdmin not available: skipped"); return; }
            var dir = Path.Combine(Paths.Tmp, "dbcert-pma");
            if (!Directory.Exists(dir)) CopyDir(src, dir);
            Func<string> cfg = () => { ToolApps.ConfigurePhpMyAdmin(dir); return File.ReadAllText(Path.Combine(dir, "config.inc.php")); };

            s.AllowLan = false;
            var c = cfg();
            Check(label + "LAN off, no password: phpMyAdmin opens logged in", c.Contains("'auth_type'] = 'config'"));
            s.AllowLan = true;
            c = cfg();
            Check(label + "LAN on, no password: phpMyAdmin shows the login page", c.Contains("'auth_type'] = 'cookie'") && !c.Contains("'config'") && c.Contains("AllowNoPassword"));
            Check(label + "notice shows: no password and LAN on", Database.RootPasswordExposed);
            var login = PmaEmptyPasswordLogin(dir);
            Check(label + "that login page accepts root with an empty password (nothing breaks)", login, "");
            s.AllowLan = false;
            Check(label + "no notice with LAN off", !Database.RootPasswordExposed);
            s.WslAccess = true;
            bool wslOn = Features.IsOn("wsl");
            Features.Set("wsl", true);
            Check(label + "notice shows with the WSL bind", Database.RootPasswordExposed);
            Features.Set("wsl", wslOn);
            s.WslAccess = false;

            var err = Database.ChangeRootPassword("Pma-Pass-1");
            c = File.ReadAllText(Path.Combine(dir, "config.inc.php"));
            cfg();
            c = File.ReadAllText(Path.Combine(dir, "config.inc.php"));
            Check(label + "a password: login page, and the password is not in the file", err == null && c.Contains("'auth_type'] = 'cookie'") && !c.Contains("Pma-Pass-1"), err ?? "");
            s.AllowLan = true;
            Check(label + "no notice once root has a password", !Database.RootPasswordExposed);
            s.AllowLan = false;
            await Task.Yield();
            Database.ChangeRootPassword("");
        }

        /// <summary>phpMyAdmin 5.2 under "php -S": fetch the form, post root + empty password, must end logged in.</summary>
        static bool PmaEmptyPasswordLogin(string dir)
        {
            int port = 18799;
            var phpDir = Path.GetDirectoryName(_phpExe);
            var ext = Path.Combine(phpDir, "ext");
            var session = Path.Combine(Paths.Tmp, "dbcert-sess");
            Directory.CreateDirectory(session);
            var psi = new ProcessStartInfo(_phpExe, "-n -d extension_dir=\"" + ext + "\" -d extension=mysqli -d extension=mbstring -d extension=openssl -d session.save_path=\"" + session + "\" -S 127.0.0.1:" + port + " -t \"" + dir + "\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi))
            {
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
                try
                {
                    Thread.Sleep(800);
                    var cookies = new CookieContainer();
                    Func<string, string, string> http = (url, body) =>
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.CookieContainer = cookies;
                        req.Proxy = null;
                        req.Timeout = 20000;
                        req.AllowAutoRedirect = true;
                        if (body != null)
                        {
                            req.Method = "POST";
                            req.ContentType = "application/x-www-form-urlencoded";
                            var b = Encoding.UTF8.GetBytes(body);
                            using (var st = req.GetRequestStream()) st.Write(b, 0, b.Length);
                        }
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        using (var rd = new StreamReader(resp.GetResponseStream())) return rd.ReadToEnd();
                    };
                    var page = http("http://127.0.0.1:" + port + "/index.php", null);
                    var token = Regex.Match(page, @"name=""token"" value=""([^""]+)""");
                    if (!page.Contains("input_username") || !token.Success) { Console.WriteLine("    no login form: " + page.Length); return false; }
                    var set = Regex.Match(page, @"name=""set_session"" value=""([^""]+)""");
                    var after = http("http://127.0.0.1:" + port + "/index.php", "pma_username=root&pma_password=&server=1&token=" + Uri.EscapeDataString(WebUtility.HtmlDecode(token.Groups[1].Value)) + (set.Success ? "&set_session=" + set.Groups[1].Value : ""));
                    return !after.Contains("input_username") && after.Contains("logout");
                }
                catch (Exception ex) { Console.WriteLine("    " + ex.Message); return false; }
                finally { try { p.Kill(); p.WaitForExit(5000); } catch { } }
            }
        }

        // ===================================================================== settings export
        static void DbCertTransferTests()
        {
            var s = Settings.Current;
            s.MysqlRootPassword = "Exported-Never-1";
            s.MachineRootPassword = "Exported-Never-2";
            s.ProxyPassword = "Exported-Never-3";
            s.Raw.Set("ssl", "OldCaThumbprint", "AB");
            s.Save();
            var zip = Path.Combine(Paths.Tmp, "dbcert-export.zip");
            SettingsTransfer.Export(zip);
            string text;
            using (var z = ZipFile.OpenRead(zip))
            using (var r = new StreamReader(z.GetEntry("portly.ini").Open())) text = r.ReadToEnd();
            Check("settings export holds no DPAPI value", !text.Contains("RootPassword") && !text.Contains("ProxyPassword") && !text.Contains("OldCaThumbprint"), "");
            var stored = s.Raw.Get("mysql", "RootPassword");
            SettingsTransfer.Import(zip);
            Check("settings import keeps this computer's password", s.Raw.Get("mysql", "RootPassword") == stored && s.MysqlRootPassword == "Exported-Never-1");
            File.Delete(zip);
            s.MysqlRootPassword = ""; s.MachineRootPassword = ""; s.ProxyPassword = ""; s.Raw.Set("ssl", "OldCaThumbprint", "");
            s.Save();
        }

        // ===================================================================== CA
        static string UserCa { get { return Path.Combine(Paths.UserSsl, "portly-ca.crt"); } }
        static string UserCaKey { get { return Path.Combine(Paths.UserSsl, "portly-ca.key.dpapi"); } }

        static X509Certificate2 ReadCa() { return new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(UserCa))); }

        static string Pem(string label, byte[] der)
        {
            return "-----BEGIN " + label + "-----\n" + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks).Replace("\r", "") + "\n-----END " + label + "-----\n";
        }

        static X509Certificate2 Leaf(string crtFile)
        {
            var t = File.ReadAllText(crtFile);
            return new X509Certificate2(Encoding.ASCII.GetBytes(t.Substring(0, t.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal) + 25)));
        }

        static void ClearCa()
        {
            foreach (var f in Directory.GetFiles(Paths.UserSsl, "portly-ca*")) File.Delete(f);
            Certs.RegenerateSiteCertificates();
        }

        static void AddSite(string name)
        {
            var d = Path.Combine(Settings.Current.DocumentRoot, name);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "index.php"), "<?php echo 1;");
        }

        // ---------- Windows chain engine with an in-memory root: the real CryptoAPI, without touching any store
        [DllImport("crypt32.dll", SetLastError = true)] static extern IntPtr CertOpenStore(IntPtr provider, uint enc, IntPtr prov, uint flags, IntPtr para);
        [DllImport("crypt32.dll", SetLastError = true)] static extern bool CertAddCertificateContextToStore(IntPtr store, IntPtr ctx, uint disposition, IntPtr added);
        [DllImport("crypt32.dll", SetLastError = true)] static extern bool CertCreateCertificateChainEngine(ref ChainEngineConfig config, out IntPtr engine);
        [DllImport("crypt32.dll", SetLastError = true)] static extern bool CertGetCertificateChain(IntPtr engine, IntPtr cert, IntPtr time, IntPtr extra, ref ChainPara para, uint flags, IntPtr reserved, out IntPtr chain);
        [DllImport("crypt32.dll")] static extern void CertFreeCertificateChain(IntPtr chain);
        [DllImport("crypt32.dll")] static extern void CertFreeCertificateChainEngine(IntPtr engine);
        [DllImport("crypt32.dll")] static extern bool CertCloseStore(IntPtr store, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct ChainEngineConfig
        {
            public uint cbSize; public IntPtr hRestrictedRoot, hRestrictedTrust, hRestrictedOther; public uint cAdditionalStore; public IntPtr rghAdditionalStore;
            public uint dwFlags, dwUrlRetrievalTimeout, MaximumCachedCertificates, CycleDetectionModulus; public IntPtr hExclusiveRoot, hExclusiveTrustedPeople; public uint dwExclusiveFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EnhUsage { public uint Count; public IntPtr Oids; }
        [StructLayout(LayoutKind.Sequential)] struct UsageMatch { public uint Type; public EnhUsage Usage; }
        [StructLayout(LayoutKind.Sequential)] struct ChainPara { public uint cbSize; public UsageMatch Requested; }

        const uint NotPermitted = 0x4000;

        /// <summary>dwErrorStatus of the chain from <paramref name="leaf"/> to <paramref name="root"/> as the only trust anchor (0 = valid).</summary>
        static uint WinChain(X509Certificate2 leaf, X509Certificate2 root, string usageOid = "1.3.6.1.5.5.7.3.1")
        {
            var store = CertOpenStore((IntPtr)2 /* CERT_STORE_PROV_MEMORY */, 0, IntPtr.Zero, 0, IntPtr.Zero);
            CertAddCertificateContextToStore(store, root.Handle, 4, IntPtr.Zero);
            var cfg = new ChainEngineConfig { cbSize = (uint)Marshal.SizeOf(typeof(ChainEngineConfig)), hExclusiveRoot = store, dwExclusiveFlags = 1 };
            IntPtr engine, chain;
            if (!CertCreateCertificateChainEngine(ref cfg, out engine)) throw new Exception("chain engine: " + Marshal.GetLastWin32Error());
            var oid = Marshal.StringToHGlobalAnsi(usageOid);
            var arr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(arr, oid);
            try
            {
                var para = new ChainPara { cbSize = (uint)Marshal.SizeOf(typeof(ChainPara)), Requested = new UsageMatch { Usage = new EnhUsage { Count = 1, Oids = arr } } };
                if (!CertGetCertificateChain(engine, leaf.Handle, IntPtr.Zero, store, ref para, 0, IntPtr.Zero, out chain)) throw new Exception("chain: " + Marshal.GetLastWin32Error());
                var status = (uint)Marshal.ReadInt32(chain, 4);
                CertFreeCertificateChain(chain);
                return status;
            }
            finally
            {
                Marshal.FreeHGlobal(arr); Marshal.FreeHGlobal(oid);
                CertFreeCertificateChainEngine(engine);
                CertCloseStore(store, 0);
            }
        }

        /// <summary>
        /// The managed X509Chain: the root is unknown to Windows (no store is touched). It sees only that the extension
        /// is well-formed: with an untrusted root CryptoAPI skips the name check, so forgeries are tested on WinChain.
        /// </summary>
        static List<X509ChainStatusFlags> ManagedChain(X509Certificate2 leaf, X509Certificate2 root)
        {
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                chain.ChainPolicy.ExtraStore.Add(root);
                chain.Build(leaf);
                return chain.ChainStatus.Select(x => x.Status).ToList();
            }
        }

        static string Hex(uint v) { return "0x" + v.ToString("X"); }

        static void ExpectGood(string what, X509Certificate2 leaf, X509Certificate2 ca)
        {
            var st = WinChain(leaf, ca);
            Check("Windows chain engine accepts " + what, st == 0, Hex(st));
            var m = ManagedChain(leaf, ca);
            Check("X509Chain accepts " + what, !m.Contains(X509ChainStatusFlags.HasNotPermittedNameConstraint) && !m.Contains(X509ChainStatusFlags.InvalidNameConstraints) && !m.Contains(X509ChainStatusFlags.HasNotSupportedNameConstraint) && !m.Contains(X509ChainStatusFlags.HasNotDefinedNameConstraint), string.Join(",", m));
        }

        static void ExpectForged(string what, X509Certificate2 leaf, X509Certificate2 ca)
        {
            var st = WinChain(leaf, ca);
            Check("Windows chain engine refuses " + what, (st & NotPermitted) != 0, Hex(st));
        }

        static string WritePem(string name, byte[] der)
        {
            var dir = Path.Combine(Paths.Tmp, "dbcert-tls");
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, name);
            File.WriteAllText(f, Pem("CERTIFICATE", der));
            return f;
        }

        /// <summary>openssl s_server on 127.0.0.1 with one certificate.</summary>
        static Process Serve(string crt, string key, int port)
        {
            var psi = new ProcessStartInfo(GitOpenssl, "s_server -accept 127.0.0.1:" + port + " -cert \"" + crt + "\" -key \"" + key + "\" -www -quiet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
            var p = Process.Start(psi);
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            for (int i = 0; i < 50; i++)
            {
                try { using (var c = new TcpClient("127.0.0.1", port)) return p; } catch { Thread.Sleep(100); }
            }
            return p;
        }

        /// <summary>Every TLS client available. Returns (client name, verified) for one host name on one port.</summary>
        static List<Tuple<string, bool, string>> Clients(string host, int port, string caFile)
        {
            var list = new List<Tuple<string, bool, string>>();
            var ca = "\"" + caFile + "\"";
            var osl = Run(GitOpenssl, "s_client -connect 127.0.0.1:" + port + " -servername " + host + " -CAfile " + ca + " -no-CApath -no-CAstore -verify_return_error -verify_hostname " + host, 20000, null, "Q\n");
            list.Add(Tuple.Create("openssl", osl.Ok && osl.Output.Contains("Verify return code: 0 (ok)"), Grep(osl.Output, "verify error|Verify return code")));
            var cu = Run(WinCurl, "--ssl-no-revoke -sS -o NUL --noproxy * --max-time 15 --cacert " + ca + " --resolve " + host + ":" + port + ":127.0.0.1 https://" + host + ":" + port + "/", 25000);
            list.Add(Tuple.Create("curl (schannel)", cu.Ok, First(cu)));
            var js = "const t=require('tls'),f=require('fs');const c=t.connect({host:'127.0.0.1',port:" + port + ",servername:process.env.H,ca:f.readFileSync(process.env.C)},()=>{console.log('OK');c.destroy()});c.on('error',e=>{console.log('ERR '+e.code+' '+e.message);process.exit(1)})";
            var n = Run(_nodeExe, "-e \"" + js + "\"", 20000, new Dictionary<string, string> { { "H", host }, { "C", caFile } });
            list.Add(Tuple.Create("node", n.Output.StartsWith("OK"), n.Output));
            var ext = Path.Combine(Path.GetDirectoryName(_phpExe), "ext");
            var php = "$c=curl_init('https://'.getenv('H').':" + port + "/');curl_setopt_array($c,[CURLOPT_RETURNTRANSFER=>1,CURLOPT_CAINFO=>getenv('C'),CURLOPT_RESOLVE=>[getenv('H').':" + port + ":127.0.0.1'],CURLOPT_TIMEOUT=>10,CURLOPT_PROXY=>'']);$r=curl_exec($c);echo $r===false?'ERR '.curl_error($c):'OK';";
            var ph = Run(_phpExe, "-n -d extension_dir=\"" + ext + "\" -d extension=openssl -d extension=curl -r \"" + php + "\"", 25000, new Dictionary<string, string> { { "H", host }, { "C", caFile } });
            list.Add(Tuple.Create("php curl", ph.Output.StartsWith("OK"), ph.Output));
            return list;
        }

        static string Grep(string text, string pattern)
        {
            return string.Join(" | ", text.Split('\n').Where(l => Regex.IsMatch(l, pattern)).Select(l => l.Trim()).Take(3));
        }

        /// <summary>A CA as Portly made them before: no constraints. The control: it does get google.com past every client.</summary>
        static void MakeControlForgery(out string caFile, out string crt, out string key)
        {
            using (var caRsa = RSA.Create(2048))
            using (var leafRsa = RSA.Create(2048))
            {
                var caReq = new CertificateRequest("CN=Unconstrained control CA", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
                caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
                using (var ca = caReq.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30)))
                {
                    var req = new CertificateRequest("CN=google.com", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var san = new SubjectAlternativeNameBuilder();
                    san.AddDnsName("google.com");
                    req.CertificateExtensions.Add(san.Build());
                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    using (var leaf = req.Create(ca, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(20), new byte[] { 1, 2, 3, 4 }))
                    {
                        caFile = WritePem("control-ca.crt", ca.RawData);
                        crt = WritePem("control-leaf.crt", leaf.RawData);
                        key = Path.Combine(Paths.Tmp, "dbcert-tls", "control-leaf.key");
                        using (var cng = new RSACng())
                        {
                            cng.ImportParameters(leafRsa.ExportParameters(true));
                            var export = cng.Key;
                            // RSA PKCS#1 from the parameters: enough for s_server
                            File.WriteAllText(key, Pem("RSA PRIVATE KEY", Pkcs1(leafRsa.ExportParameters(true))));
                        }
                    }
                }
            }
        }

        static byte[] Pkcs1(RSAParameters p)
        {
            Func<byte[], byte[]> asInt = v =>
            {
                var t = v.SkipWhile((b, i) => b == 0 && i < v.Length - 1).ToList();
                if ((t[0] & 0x80) != 0) t.Insert(0, 0);
                return Der(2, t.ToArray());
            };
            var body = new List<byte>();
            foreach (var part in new[] { new byte[] { 0 }, p.Modulus, p.Exponent, p.D, p.P, p.Q, p.DP, p.DQ, p.InverseQ }) body.AddRange(asInt(part));
            return Der(0x30, body.ToArray());
        }

        static byte[] Der(byte tag, byte[] c)
        {
            var r = new List<byte> { tag };
            if (c.Length < 0x80) r.Add((byte)c.Length);
            else if (c.Length < 0x100) { r.Add(0x81); r.Add((byte)c.Length); }
            else { r.Add(0x82); r.Add((byte)(c.Length >> 8)); r.Add((byte)c.Length); }
            r.AddRange(c);
            return r.ToArray();
        }

        // ---------- real Chrome: a temporary profile has its own table of trusted certificates (chrome://certificate-manager,
        // "Installed by you"), so no Windows store and none of the user's browsers are involved
        const string ChromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        static readonly string SqliteExe = Directory.Exists(Path.Combine(MainBuildBin, "sqlite")) ? Directory.GetFiles(Path.Combine(MainBuildBin, "sqlite"), "sqlite3.exe", SearchOption.AllDirectories).FirstOrDefault() : null;

        static string RunChrome(string profile, string url, string host, string netLog)
        {
            var args = "--headless=new --disable-gpu --no-first-run --no-default-browser-check --no-proxy-server --user-data-dir=\"" + profile + "\"" +
                       (host != null ? " --host-resolver-rules=\"MAP " + host + " 127.0.0.1\"" : "") + (netLog != null ? " --log-net-log=\"" + netLog + "\"" : "") +
                       " --timeout=12000 --dump-dom " + url;
            var psi = new ProcessStartInfo(ChromeExe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var sb = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (o, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (o, e) => { };
                p.Start();
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(40000)) ProcessUtil.KillTree(p.Id);
            }
            lock (sb) return sb.ToString();
        }

        /// <summary>A new Chrome profile that trusts only this one extra root.</summary>
        static string ChromeProfile(string name, X509Certificate2 root)
        {
            var profile = Path.Combine(Paths.Tmp, "dbcert-chrome-" + name);
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
            RunChrome(profile, "about:blank", null, null);
            var db = Path.Combine(profile, "Default", "ServerCertificate");
            if (!File.Exists(db)) return null;
            var hash = BitConverter.ToString(SHA256.Create().ComputeHash(root.RawData)).Replace("-", "").ToLowerInvariant();
            var der = BitConverter.ToString(root.RawData).Replace("-", "");
            // CertificateMetadata { trust { trust_type = 3 } }, the value the certificate manager stores for "trusted"
            const string trust = "0A020803";
            var r = Run(SqliteExe, "\"" + db + "\"", 20000, null, "INSERT INTO certificates VALUES('" + hash + "', X'" + der + "', X'" + trust + "');\n");
            return r.Ok ? profile : null;
        }

        static void ChromeChecks(X509Certificate2 ca, X509Certificate2 controlCa, Tuple<string, string> good, Tuple<string, string> forged, Tuple<string, string> controlForged)
        {
            if (!File.Exists(ChromeExe) || SqliteExe == null) { Console.WriteLine("    Chrome or sqlite3 not available: skipped"); return; }
            Console.WriteLine("chrome: " + FileVersionInfo.GetVersionInfo(ChromeExe).FileVersion + " (headless, temporary profile)");
            var profile = ChromeProfile("constrained", ca);
            var control = ChromeProfile("control", controlCa);
            if (profile == null || control == null) { Check("Chrome: temporary profile with its own trusted root", false); return; }
            const string served = "s_server";
            Func<string, string, int, string> load = (prof, host, port) =>
            {
                var log = Path.Combine(prof, "net.json");
                var dom = RunChrome(prof, "https://" + host + ":" + port + "/", host, log);
                var net = File.Exists(log) ? File.ReadAllText(log) : "";
                var codes = Regex.Matches(net, "\"net_error\":(-2\\d\\d)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct();
                var msg = Regex.Matches(net, "\"(?:error|verify_result|cert_status)[^,]{0,120}").Cast<Match>().Select(m => m.Value).Distinct().Take(3);
                return (dom.Contains(served) ? "LOADED " : "BLOCKED ") + string.Join(",", codes) + " " + string.Join(" ", msg);
            };
            using (var a = Serve(good.Item1, good.Item2, 18811))
            using (var b = Serve(forged.Item1, forged.Item2, 18812))
            using (var c = Serve(controlForged.Item1, controlForged.Item2, 18813))
            {
                try
                {
                    var site = load(profile, "shop.portly", 18811);
                    Check("Chrome: loads https://shop.portly from the constrained CA", site.StartsWith("LOADED"), site);
                    var wild = load(profile, "www.shop.portly", 18811);
                    Check("Chrome: loads the wildcard", wild.StartsWith("LOADED"), wild);
                    var ctl = load(control, "google.com", 18813);
                    Check("control Chrome: an unconstrained CA gets the forged name loaded", ctl.StartsWith("LOADED"), ctl);
                    var fake = load(profile, "google.com", 18812);
                    Check("Chrome: blocks google.com from the constrained CA", fake.StartsWith("BLOCKED"), fake);
                }
                finally { foreach (var p in new[] { a, b, c }) try { p.Kill(); p.WaitForExit(3000); } catch { } }
            }
        }

        /// <summary>The CA's EKU: on Windows it also rules out a code-signing certificate, which name constraints do not.</summary>
        static void EkuCheck()
        {
            Func<bool, uint> codeSign = withEku =>
            {
                using (var caRsa = RSA.Create(2048))
                using (var leafRsa = RSA.Create(2048))
                {
                    var caReq = new CertificateRequest("CN=EKU test CA", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
                    caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                    if (withEku) caReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    using (var ca = caReq.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30)))
                    {
                        var req = new CertificateRequest("CN=Forged publisher", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, false));
                        using (var leaf = req.Create(ca, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(20), new byte[] { 1, 2, 3, 5 }))
                            return WinChain(leaf, ca, "1.3.6.1.5.5.7.3.3");
                    }
                }
            };
            Check("control: a CA without EKU can make a code-signing certificate", codeSign(false) == 0, Hex(codeSign(false)));
            var st = codeSign(true);
            Check("a CA with the server EKU cannot (Windows)", (st & 0x10) != 0, Hex(st));   // CERT_TRUST_IS_NOT_VALID_FOR_USAGE
        }

        static async Task DbCertCaTests()
        {
            var s = Settings.Current;
            ClearCa();
            // ---------- which names a new CA gets
            AddSite("shop");
            AddSite("example.org");
            AddSite("www.realdomain.com.tr");
            Sites.SetAliases("shop", new[] { "shop.dev", "*.alias.example.net" });
            var names = Certs.PermittedNames();
            Console.WriteLine("    permitted: " + string.Join(", ", names));
            Check("names: local endings, ending and domains in use", new[] { "localhost", "test", "portly", "example.org", "www.realdomain.com.tr", "shop.dev", "alias.example.net", "home.arpa" }.All(names.Contains) && !names.Contains("com"), string.Join(",", names));
            Check("names: nothing that another entry covers", !names.Any(n => names.Any(o => n.EndsWith("." + o))));
            Check("names: reduce drops wildcards, invalid and covered names", string.Join(",", Certs.ReduceNames(new[] { "*.a.test", "a.test", "b.a.test", "Özel Proje", "under_score.x", "LOCALHOST.", "" })) == "a.test,localhost");
            s.Tld = "corp"; s.HostTemplate = "{name}.{tld}.intra";
            names = Certs.PermittedNames();
            Check("names: the address format of future projects", names.Contains("corp.intra") && names.Contains("corp"), string.Join(",", names));
            s.Tld = "portly"; s.HostTemplate = "{name}.{tld}";

            // ---------- DER, as the libraries read it
            var ca = Certs.EnsureCa();
            var caFile = UserCa;
            var txt = Run(GitOpenssl, "x509 -in \"" + caFile + "\" -noout -ext nameConstraints,extendedKeyUsage").Output;
            Console.WriteLine(string.Join("\n", txt.Split('\n').Select(l => "    " + l.TrimEnd())));
            Check("openssl reads a critical nameConstraints extension", txt.Contains("Name Constraints: critical") && txt.Contains("DNS:portly") && txt.Contains("IP:127.0.0.0/255.0.0.0") && txt.Contains("IP:192.168.0.0/255.255.0.0") && txt.Contains("IP:172.16.0.0/255.240.0.0"), "");
            Check("... with IPv6 loopback, ULA and link-local", Regex.IsMatch(txt, @"IP:0:0:0:0:0:0:0:1/FFFF:FFFF:FFFF:FFFF:FFFF:FFFF:FFFF:FFFF|IP:::1/", RegexOptions.IgnoreCase) && Regex.IsMatch(txt, @"IP:FC00", RegexOptions.IgnoreCase) && Regex.IsMatch(txt, @"IP:FE80", RegexOptions.IgnoreCase), "");
            var ext = ca.Extensions["2.5.29.30"];
            Check("critical in .NET too", ext != null && ext.Critical && Certs.IsRestricted(ca));
            Check("parser reads back what was written", Certs.PermittedDns(ca).SequenceEqual(Certs.PermittedNames()), string.Join(",", Certs.PermittedDns(ca)));
            Check("the authority still validates itself", Certs.EnsureCa().Thumbprint == ca.Thumbprint);

            // ---------- private key file
            var keyText = File.ReadAllText(UserCaKey);
            bool wrongEntropyFails = false;
            try { ProtectedData.Unprotect(Convert.FromBase64String(keyText), null, DataProtectionScope.CurrentUser); } catch (CryptographicException) { wrongEntropyFails = true; }
            Check("CA key is a DPAPI blob for this Windows user, no plain key on disk", !keyText.Contains("PRIVATE KEY") && wrongEntropyFails && !File.Exists(Path.Combine(Paths.UserSsl, "portly-ca.key")));

            // ---------- what Portly issues must pass
            var shop = Certs.EnsureSite("shop.portly", new[] { "shop.portly", "*.shop.portly", "shop.dev", "*.alias.example.net" });
            var real = Certs.EnsureSite("example.org", new[] { "example.org", "*.example.org", "example.org.portly" });
            var local = Certs.EnsureSite("localhost");
            var lan = Certs.LanCertificate(IPAddress.Parse("192.168.1.20"));
            var lan10 = Certs.LanCertificate(IPAddress.Parse("10.1.2.3"));
            var lanLoop = Certs.LanCertificate(IPAddress.Loopback);
            ExpectGood("a site with aliases", Leaf(shop.Item1), ca);
            ExpectGood("a real-domain site", Leaf(real.Item1), ca);
            ExpectGood("localhost (IPv4 and IPv6 loopback)", Leaf(local.Item1), ca);
            ExpectGood("the LAN address 192.168.1.20", lan, ca);
            ExpectGood("the LAN address 10.1.2.3", lan10, ca);

            // ---------- the attack: what a thief of the key would do, with the same Portly code
            var google = Certs.EnsureSite("google.com", new[] { "google.com", "*.google.com" });
            var evilIp = Certs.LanCertificate(IPAddress.Parse("8.8.8.8"));
            var mixed = Certs.EnsureSite("mixed.portly", new[] { "mixed.portly", "bank.example.com" });
            ExpectForged("google.com", Leaf(google.Item1), ca);
            ExpectForged("the public address 8.8.8.8", evilIp, ca);
            ExpectForged("a good name next to a foreign one", Leaf(mixed.Item1), ca);
            Check("Portly says google.com is outside", Certs.OutsideCa(ca, new[] { "google.com", "*.google.com" }).Count == 2 && Certs.SiteProblem("google.com", new[] { "google.com", "*.google.com" }) == "constraint");
            Check("a name that only ends the same way is outside", Certs.OutsideCa(ca, new[] { "evilportly", "notexample.org" }).Count == 2);
            Check("subdomains of a permitted name are inside", Certs.OutsideCa(ca, new[] { "a.b.example.org", "*.x.portly", "EXAMPLE.ORG" }).Count == 0);

            // openssl verify: one more independent library, with the name check
            var leafPem = new Func<X509Certificate2, string>(c => WritePem("leaf-" + c.Thumbprint + ".crt", c.RawData));
            Func<string, X509Certificate2, bool> verify = (flags, c) => { var r = Run(GitOpenssl, "verify -CAfile \"" + caFile + "\" -no-CApath -no-CAstore " + flags + " \"" + leafPem(c) + "\""); return r.Ok && r.Output.Contains(": OK"); };
            Check("openssl verify accepts the LAN certificate (IP SAN, no dNSName)", verify("-verify_ip 192.168.1.20", lan));
            Check("openssl verify accepts a site", verify("-verify_hostname shop.portly", Leaf(shop.Item1)));
            Check("openssl verify refuses google.com", !verify("-verify_hostname google.com", Leaf(google.Item1)));
            Check("openssl verify refuses 8.8.8.8", !verify("-verify_ip 8.8.8.8", evilIp));

            EkuCheck();

            // ---------- real TLS on a real socket
            string ctlCa, ctlCrt, ctlKey;
            MakeControlForgery(out ctlCa, out ctlCrt, out ctlKey);
            using (var good = Serve(shop.Item1, shop.Item2, 18801))
            using (var bad = Serve(google.Item1, google.Item2, 18802))
            using (var ctl = Serve(ctlCrt, ctlKey, 18803))
            using (var loc = Serve(local.Item1, local.Item2, 18804))
            {
                try
                {
                    foreach (var c in Clients("shop.portly", 18801, caFile)) Check(c.Item1 + ": accepts https://shop.portly", c.Item2, c.Item3);
                    foreach (var c in Clients("www.shop.portly", 18801, caFile)) Check(c.Item1 + ": accepts the wildcard", c.Item2, c.Item3);
                    foreach (var c in Clients("localhost", 18804, caFile)) Check(c.Item1 + ": accepts https://localhost", c.Item2, c.Item3);
                    foreach (var c in Clients("google.com", 18803, ctlCa)) Check("control " + c.Item1 + ": an unconstrained CA gets google.com accepted", c.Item2, c.Item3);
                    foreach (var c in Clients("google.com", 18802, caFile)) Check(c.Item1 + ": refuses google.com from the constrained CA", !c.Item2, c.Item3);
                }
                finally { foreach (var p in new[] { good, bad, ctl, loc }) try { p.Kill(); p.WaitForExit(3000); } catch { } }
            }
            ChromeChecks(ca, new X509Certificate2(ctlCa), shop, google, Tuple.Create(ctlCrt, ctlKey));

            // the LAN certificate through SChannel, as LanShare serves it
            var listener = new TcpListener(IPAddress.Loopback, 18805);
            listener.Start();
            var server = Task.Run(async () =>
            {
                while (true)
                    try
                    {
                        using (var c = await listener.AcceptTcpClientAsync())
                        using (var ssl = new SslStream(c.GetStream()))
                        {
                            await ssl.AuthenticateAsServerAsync(lanLoop, false, System.Security.Authentication.SslProtocols.Tls12, false);
                            var b = new byte[2048];
                            await ssl.ReadAsync(b, 0, b.Length);
                            var bytes = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Type: text/html\r\nContent-Length: 2\r\n\r\nok");
                            await ssl.WriteAsync(bytes, 0, bytes.Length);
                        }
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (InvalidOperationException) { return; }
                    catch { }
            });
            var lr = Run(WinCurl, "--ssl-no-revoke -sS --noproxy * --max-time 10 --cacert \"" + caFile + "\" https://127.0.0.1:18805/", 20000);
            Check("curl (schannel): accepts the LAN certificate over TLS", lr.Ok && lr.Output == "ok", First(lr));
            if (File.Exists(ChromeExe) && File.Exists(Path.Combine(Paths.Tmp, "dbcert-chrome-constrained", "Default", "ServerCertificate")))
            {
                var dom = RunChrome(Path.Combine(Paths.Tmp, "dbcert-chrome-constrained"), "https://127.0.0.1:18805/", null, null);
                Check("Chrome: loads the LAN address certificate (IP in the SAN, no CN)", dom.Contains(">ok<"), dom.Length + " bytes");
            }
            listener.Stop();
            await Task.WhenAny(server, Task.Delay(3000));

            // ---------- Java: the extension must parse (keytool), and a replaced CA replaces the alias
            await JavaChecks();
            UiChecks();

            // ---------- the upgrade flow
            await UpgradeChecks();
            ClearCa();
            foreach (var d in Directory.GetDirectories(s.DocumentRoot)) Directory.Delete(d, true);
            if (File.Exists(Paths.SitesFile)) File.Delete(Paths.SitesFile);
        }

        /// <summary>The new windows build without an exception; pictures in tmp\dbcert-ui for a look at the layout.</summary>
        static void UiChecks()
        {
            Exception failed = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var dir = Path.Combine(Paths.Tmp, "dbcert-ui");
                    Directory.CreateDirectory(dir);
                    Portly.UI.Theme.Load(Settings.Current.Theme);
                    Action<System.Windows.Forms.Form, string> shot = (f, name) =>
                    {
                        f.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                        f.Location = new System.Drawing.Point(-3000, 0);
                        f.ShowInTaskbar = false;
                        f.Show();
                        System.Windows.Forms.Application.DoEvents();
                        using (var b = new System.Drawing.Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(b, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                            b.Save(Path.Combine(dir, name), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        f.Close();
                    };
                    WriteOldStyleCa();
                    using (var f = new Portly.UI.SslRepairForm(Sites.Scan())) shot(f, "ssl.png");
                    var s = Settings.Current;
                    s.AllowLan = true;
                    using (var f = new System.Windows.Forms.Form { Width = 760, Height = 90 })
                    {
                        Portly.UI.Ui.Apply(f);
                        var n = new Portly.UI.RootPasswordNotice();
                        n.Visible = true;   // it also needs an installed database, which this group has not linked yet
                        f.Controls.Add(n);
                        shot(f, "notice.png");
                    }
                    s.AllowLan = false;
                }
                catch (Exception ex) { failed = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Check("UI: repair window and notice build", failed == null, failed == null ? "" : failed.ToString());
        }

        static string _javaHome;

        static async Task JavaChecks()
        {
            var src = Directory.GetDirectories(Path.Combine(MainBuildBin, "java")).FirstOrDefault();
            if (src == null || !File.Exists(Path.Combine(src, "bin", "keytool.exe"))) { Console.WriteLine("    Java not available: skipped"); return; }
            _javaHome = Path.Combine(Paths.Tmp, "dbcert-java");
            if (!Directory.Exists(_javaHome)) CopyDir(src, _javaHome);
            Environment.SetEnvironmentVariable("JAVA_HOME", _javaHome);
            Console.WriteLine("java: " + First(Run(Path.Combine(_javaHome, "bin", "java.exe"), "-version")));
            var print = Run(JavaTrust.Keytool(_javaHome), "-printcert -file \"" + UserCa + "\"");
            Check("keytool parses the extension", print.Ok && print.Output.Contains("NameConstraints") && print.Output.Contains("DNSName: portly"), print.Ok ? "" : print.Output);
            var err = JavaTrust.Import(_javaHome);
            Check("Java import", err == null, err ?? "");
            await Task.Yield();
        }

        /// <summary>Written the way Portly 1.2 and older made them: valid, trusted by the user, no constraints.</summary>
        static void WriteOldStyleCa()
        {
            ClearCa();
            var p = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowPlaintextExport, KeyUsage = CngKeyUsages.AllUsages };
            p.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            using (var rsa = new RSACng(CngKey.Create(CngAlgorithm.Rsa, null, p)))
            {
                var name = new X500DistinguishedName("CN=Portly Local CA (" + Environment.MachineName + "), O=Portly by Erdem Software, OU=Local development only");
                var req = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
                req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
                var ca = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
                File.WriteAllText(UserCa, Pem("CERTIFICATE", ca.RawData));
                var blob = ProtectedData.Protect(rsa.Key.Export(CngKeyBlobFormat.Pkcs8PrivateBlob), Encoding.ASCII.GetBytes("Portly CA"), DataProtectionScope.CurrentUser);
                File.WriteAllText(UserCaKey, Convert.ToBase64String(blob));
            }
        }

        static async Task UpgradeChecks()
        {
            WriteOldStyleCa();
            var old = ReadCa();
            var sites = Sites.Scan();
            var shop = sites.First(x => x.Name == "shop");
            Certs.EnsureSite(shop.Host, shop.AllNames);
            var before = File.ReadAllText(Path.Combine(Certs.SitesDir, shop.Host + ".crt"));
            var check = SslRepair.Check(sites);
            Check("upgrade: an old CA is left alone and only offered", check.CaUnrestricted && Certs.EnsureCa().Thumbprint == old.Thumbprint && check.Sites.First(x => x.Site == shop).Problem == null);
            ExpectGood("a site of the old CA", Leaf(Path.Combine(Certs.SitesDir, shop.Host + ".crt")), old);

            // the administrator prompt refused
            var refused = await SslRepair.UpgradeCaAsync(sites, f => false, t => { throw new Exception("must not run"); });
            Check("upgrade: refusing the prompt changes nothing", refused.Error != null && ReadCa().Thumbprint == old.Thumbprint
                && File.ReadAllText(Path.Combine(Certs.SitesDir, shop.Host + ".crt")) == before && !Directory.GetFiles(Paths.UserSsl, "portly-ca.new*").Any() && Certs.OldCas.Count == 0);

            // accepted, removal of the old one refused: stays on the list
            var order = new List<string>();
            string trusted = null;
            var u = await SslRepair.UpgradeCaAsync(sites, f =>
            {
                trusted = new X509Certificate2(Encoding.ASCII.GetBytes(File.ReadAllText(f))).Thumbprint;
                order.Add("trust" + (ReadCa().Thumbprint == old.Thumbprint ? ":old-still-current" : ""));
                return true;
            }, t =>
            {
                order.Add("untrust:" + (t == old.Thumbprint) + (ReadCa().Thumbprint == trusted ? ":new-current" : ""));
                return false;
            });
            var ca = ReadCa();
            Check("upgrade: order is trust new, switch, remove old", string.Join(" ", order) == "trust:old-still-current untrust:True:new-current", string.Join(" ", order));
            Check("upgrade: the new CA is the one trusted, and it is restricted", ca.Thumbprint == trusted && ca.Thumbprint != old.Thumbprint && Certs.IsRestricted(ca) && u.Error == null);
            Check("upgrade: sites reissued", u.Renewed == sites.Count(x => x.Valid), u.Renewed + " of " + sites.Count);
            foreach (var site in sites.Where(x => x.Valid)) ExpectGood("after the upgrade " + site.Host, Leaf(Path.Combine(Certs.SitesDir, site.Host + ".crt")), ca);
            Check("upgrade: no site is left on the old CA", SslRepair.Check(sites).Sites.All(x => x.Problem == null), string.Join(",", SslRepair.Check(sites).Sites.Select(x => x.Site.Host + "=" + x.Problem)));
            Check("upgrade: PHP bundle has the new CA", File.ReadAllText(Certs.BundleFile).Replace("\r", "").Contains(File.ReadAllText(UserCa).Replace("\r", "").Trim()));
            var lan = Certs.LanCertificate(IPAddress.Parse("192.168.1.20"));
            ExpectGood("the cached LAN certificate, renewed", lan, ca);
            Check("upgrade: the refused removal is remembered", !u.OldRemoved && Certs.OldCas.SequenceEqual(new[] { old.Thumbprint }) && SslRepair.Check(sites).OldCas == 1);
            Check("upgrade: the next try removes only that one", Certs.RemoveOldCas(t => t == old.Thumbprint) && Certs.OldCas.Count == 0);
            if (_javaHome != null)
            {
                var keytool = Run(JavaTrust.Keytool(_javaHome), "-list -noprompt -keystore \"" + JavaTrust.Cacerts(_javaHome) + "\" -storepass changeit -alias " + JavaTrust.Alias);
                var hash = BitConverter.ToString(SHA256.Create().ComputeHash(ca.RawData)).Replace("-", ":");
                Check("upgrade: Java holds the new CA under the same alias", keytool.Output.Contains(hash) && u.JavaError == null, u.JavaError ?? "");
            }

            // a domain added after the CA was made: reported, then a new CA takes it in
            AddSite("addedlater.example.com");
            sites = Sites.Scan();
            var added = sites.First(x => x.Host == "addedlater.example.com");
            Certs.EnsureSite(added.Host, added.AllNames);
            var r = SslRepair.Check(sites);
            Check("a real domain added later is reported, a new certificate would not help", r.OutsideCa.Count == 1 && r.OutsideCa[0].Site == added);
            var fix = await SslRepair.FixAsync(sites, false);
            Check("Fix SSL does not pretend to repair it", fix.Renewed == 0 && fix.OutsideCa.Count == 1, fix.Renewed + "");
            var old2 = ReadCa();
            var widen = await SslRepair.UpgradeCaAsync(sites, f => true, t => t == old2.Thumbprint);
            ca = ReadCa();
            Check("the upgrade takes it in", widen.Error == null && widen.OldRemoved && Certs.PermittedDns(ca).Contains("addedlater.example.com") && SslRepair.Check(sites).OutsideCa.Count == 0);
            ExpectGood(added.Host, Leaf(Path.Combine(Certs.SitesDir, added.Host + ".crt")), ca);

            // the ending changed in Settings
            var s = Settings.Current;
            s.Tld = "local2";
            var sites2 = Sites.Scan();
            r = SslRepair.Check(sites2);
            Check("a new ending in Settings is reported", r.OutsideCa.Count > 0, r.OutsideCa.Count + "");
            s.Tld = "portly";
        }
    }
}
