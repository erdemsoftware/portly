using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.Tools
{
    /// <summary>Server profile of one site (usr\deploy.ini, one section per site). The database password is DPAPI-protected.</summary>
    public class DeployProfile
    {
        public string Site = "";
        public string Host = "";
        public int Port = 22;
        public string User = "";
        public string KeyFile = "";
        public string RemoteDir = "";
        /// <summary>Read the database settings from wp-config.php / .env on the server (no password stored here).</summary>
        public bool DbFromServer = true;
        public bool Database = true;
        public string DbName = "", DbUser = "", DbPassword = "", DbHost = "localhost";
        public string LocalDb = "";
        public string LocalUrl = "", RemoteUrl = "";
        public List<string> Excludes = new List<string>(Deploy.DefaultExcludes);

        public string Target { get { return User + "@" + Host; } }
        public bool IsComplete { get { return Host != "" && User != "" && RemoteDir != ""; } }

        public DeployProfile Clone()
        {
            var c = (DeployProfile)MemberwiseClone();
            c.Excludes = new List<string>(Excludes);
            return c;
        }
    }

    public class DeploySummary
    {
        public int Files;
        public long Bytes;
        public bool Database;
        public string LocalDb = "";
        public string RemoteDir = "";
    }

    /// <summary>
    /// Push / pull a site to a server over SSH with the OpenSSH client and tar.exe that ship with Windows 10 1809+.
    /// Files travel as a tar.gz stream through ssh, the database as a gzip'ed mysqldump. Passwords never appear on a
    /// command line or in the log: the server reads them from its own wp-config.php / .env, or from ssh's standard
    /// input into an option file created with umask 077 and deleted on exit.
    /// </summary>
    public static class Deploy
    {
        public static readonly string[] DefaultExcludes = { ".git", "node_modules", ".env", "*.log", "storage/logs", "wp-content/cache", "wp-config.php" };

        static string Sys32 { get { return Environment.GetFolderPath(Environment.SpecialFolder.System); } }
        public static string SshExe { get { return Path.Combine(Sys32, "OpenSSH", "ssh.exe"); } }
        public static string SshKeygenExe { get { return Path.Combine(Sys32, "OpenSSH", "ssh-keygen.exe"); } }
        public static string TarExe { get { return Path.Combine(Sys32, "tar.exe"); } }
        public static string IniFile { get { return Path.Combine(Paths.Usr, "deploy.ini"); } }
        public static string SshDir { get { return Path.Combine(Paths.Usr, "ssh"); } }
        public static string KnownHosts { get { return Path.Combine(SshDir, "known_hosts"); } }
        public static string DefaultKey { get { return Path.Combine(SshDir, "id_ed25519_portly"); } }
        public static string BackupDir { get { return Path.Combine(Paths.Backup, "deploy"); } }

        /// <summary>Feature on, Modern edition, Windows 10 with the OpenSSH client and tar.</summary>
        public static bool IsAvailable
        {
            get { return !AppInfo.IsLegacy && OsInfo.IsWin10OrLater && Features.IsOn("deploy") && File.Exists(SshExe) && File.Exists(TarExe); }
        }

        // ---------------- profiles ----------------
        public static DeployProfile Load(string site, string iniFile = null)
        {
            var ini = new Ini(iniFile ?? IniFile);
            var p = new DeployProfile { Site = site, LocalDb = ProjectCreator.DbNameFor(site) };
            if (!ini.Sections().Contains(site)) return p;
            p.Host = ini.Get(site, "Host", "");
            p.Port = ini.GetInt(site, "Port", 22);
            p.User = ini.Get(site, "User", "");
            p.KeyFile = ini.Get(site, "KeyFile", "");
            p.RemoteDir = ini.Get(site, "RemoteDir", "");
            p.Database = ini.GetBool(site, "Database", true);
            p.DbFromServer = ini.GetBool(site, "DbFromServer", true);
            p.DbName = ini.Get(site, "DbName", "");
            p.DbUser = ini.Get(site, "DbUser", "");
            p.DbHost = ini.Get(site, "DbHost", "localhost");
            p.DbPassword = Unprotect(ini.Get(site, "DbPassword", ""));
            p.LocalDb = ini.Get(site, "LocalDb", p.LocalDb);
            p.LocalUrl = ini.Get(site, "LocalUrl", "");
            p.RemoteUrl = ini.Get(site, "RemoteUrl", "");
            var ex = ini.Get(site, "Exclude", null);
            if (ex != null) p.Excludes = SplitExcludes(ex);
            return p;
        }

        public static void Save(DeployProfile p, string iniFile = null)
        {
            var ini = new Ini(iniFile ?? IniFile);
            var s = p.Site;
            ini.Set(s, "Host", p.Host.Trim());
            ini.Set(s, "Port", p.Port);
            ini.Set(s, "User", p.User.Trim());
            ini.Set(s, "KeyFile", p.KeyFile.Trim());
            ini.Set(s, "RemoteDir", p.RemoteDir.Trim());
            ini.Set(s, "Database", p.Database);
            ini.Set(s, "DbFromServer", p.DbFromServer);
            ini.Set(s, "DbName", p.DbName.Trim());
            ini.Set(s, "DbUser", p.DbUser.Trim());
            ini.Set(s, "DbHost", p.DbHost.Trim());
            ini.Set(s, "DbPassword", Protect(p.DbPassword));
            ini.Set(s, "LocalDb", p.LocalDb.Trim());
            ini.Set(s, "LocalUrl", p.LocalUrl.Trim());
            ini.Set(s, "RemoteUrl", p.RemoteUrl.Trim());
            ini.Set(s, "Exclude", string.Join(", ", p.Excludes));
            ini.Save();
        }

        public static List<string> SplitExcludes(string text)
        {
            return (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().Replace('\\', '/').Trim('/')).Where(x => x != "" && x != "." && !x.Contains("..")).Distinct().ToList();
        }

        static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
        }

        static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        // ---------------- validation ----------------
        static readonly Regex HostRx = new Regex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,252})$|^\[?[0-9A-Fa-f:]+\]?$");
        static readonly Regex UserRx = new Regex(@"^[A-Za-z0-9._][A-Za-z0-9._-]{0,63}$");
        static readonly Regex UrlRx = new Regex(@"^https?://[^\s'""]+$", RegexOptions.IgnoreCase);

        /// <summary>Returns null when the profile can be used, otherwise the problem.</summary>
        public static string Validate(DeployProfile p)
        {
            if (!HostRx.IsMatch(p.Host ?? "")) return I18n.T("deploy.errHost");
            if (!UserRx.IsMatch(p.User ?? "")) return I18n.T("deploy.errUser");
            if (p.Port < 1 || p.Port > 65535) return I18n.T("deploy.errPort");
            if (string.IsNullOrWhiteSpace(p.RemoteDir) || p.RemoteDir.IndexOfAny(new[] { '\n', '\r', '\0' }) >= 0) return I18n.T("deploy.errRemoteDir");
            var dir = p.RemoteDir.Trim().TrimEnd('/');
            if (dir == "" || dir == "~" || dir == "/" || Regex.IsMatch(dir, @"^/(bin|boot|dev|etc|lib|lib64|proc|root|sbin|sys|usr|var)$")) return I18n.T("deploy.errRemoteDir");
            if (p.KeyFile != "" && !File.Exists(p.KeyFile)) return I18n.T("deploy.errKey", p.KeyFile);
            if ((p.LocalUrl != "" || p.RemoteUrl != "") && !(UrlRx.IsMatch(p.LocalUrl) && UrlRx.IsMatch(p.RemoteUrl))) return I18n.T("deploy.errUrl");
            if (p.Database && !Database.IsValidName(p.LocalDb)) return I18n.T("err.dbName");
            if (p.Database && !p.DbFromServer && (p.DbName == "" || p.DbUser == "")) return I18n.T("deploy.errDb");
            return null;
        }

        // ---------------- command lines (unit-tested, no server needed) ----------------
        /// <summary>POSIX shell single-quoted string.</summary>
        public static string Sh(string s) { return "'" + (s ?? "").Replace("'", "'\\''") + "'"; }

        /// <summary>Windows command-line argument (MSVC rules), for ssh.exe / tar.exe.</summary>
        public static string Arg(string s)
        {
            if (s == "") return "\"\"";
            if (s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (var c in s)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
                sb.Append('\\', bs).Append(c); bs = 0;
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// ssh.exe arguments. BatchMode: never prompt (a hidden prompt would hang); accept-new: remember a new
        /// server key, refuse a changed one. Portly keeps its own known_hosts in usr\ssh.
        /// </summary>
        public static string SshArgs(DeployProfile p, string remoteCommand)
        {
            var sb = new StringBuilder();
            sb.Append("-o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 -o ServerAliveInterval=30");
            sb.Append(" -o ").Append(Arg("UserKnownHostsFile=" + KnownHosts));
            if (p.KeyFile != "") sb.Append(" -o IdentitiesOnly=yes -i ").Append(Arg(p.KeyFile));
            if (p.Port != 22) sb.Append(" -p ").Append(p.Port);
            sb.Append(' ').Append(Arg(p.Target));
            if (remoteCommand != null) sb.Append(' ').Append(Arg(remoteCommand));
            return sb.ToString();
        }

        /// <summary>
        /// The remote command: the script travels base64-encoded, so no character of it is changed by Windows or
        /// the login shell; "sh -c" keeps ssh's standard input for the data stream.
        /// </summary>
        public static string RemoteCommand(string script)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n")));
            return "sh -c \"$(echo " + b64 + " | base64 -d)\"";
        }

        /// <summary>--exclude arguments for GNU tar on the server.</summary>
        public static string RemoteExcludes(IEnumerable<string> excludes)
        {
            return string.Join(" ", excludes.Select(x => "--exclude=" + Sh(x)));
        }

        /// <summary>--exclude arguments for tar.exe (bsdtar) on Windows.</summary>
        public static string LocalExcludes(IEnumerable<string> excludes)
        {
            return string.Join(" ", excludes.Select(x => "--exclude " + Arg(x)));
        }

        /// <summary>Does an exclude pattern match this relative path (forward or back slashes)? Same rules as tar.</summary>
        public static bool IsExcluded(string relPath, IEnumerable<string> excludes)
        {
            var rel = relPath.Replace('\\', '/').Trim('/');
            if (rel == "") return false;
            var parts = rel.Split('/');
            foreach (var raw in excludes)
            {
                var pat = raw.Replace('\\', '/').Trim('/');
                if (pat == "") continue;
                var pp = pat.Split('/');
                // the pattern matches a run of whole path components ending at this path or at one of its
                // parent folders (an excluded folder excludes everything inside it)
                for (int end = pp.Length; end <= parts.Length; end++)
                {
                    bool ok = true;
                    for (int i = 0; i < pp.Length && ok; i++)
                        ok = Glob(pp[i], parts[end - pp.Length + i]);
                    if (ok) return true;
                }
            }
            return false;
        }

        static bool Glob(string pattern, string name)
        {
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase);
        }

        public const string BackupFolder = "portly-backups";
        const string DumpFailed = "PORTLY_DUMP_FAILED";

        /// <summary>Shell lines that write the database option file $CNF (umask 077, removed on exit).</summary>
        public static string DbSetupScript(DeployProfile p)
        {
            var sb = new StringBuilder();
            sb.Append("umask 077\nCNF=$(mktemp) || exit 3\ntrap 'rm -f \"$CNF\"' EXIT\n");
            if (p.DbFromServer)
            {
                sb.Append("if [ -f \"$D/wp-config.php\" ]; then\n");
                // value in single quotes (may contain "), else in double quotes
                sb.Append("  g() { v=$(sed -n \"s/.*define( *['\\\"]$1['\\\"] *, *'\\([^']*\\)'.*/\\1/p\" \"$D/wp-config.php\" | head -n 1)\n");
                sb.Append("    [ -n \"$v\" ] || v=$(sed -n \"s/.*define( *['\\\"]$1['\\\"] *, *\\\"\\([^\\\"]*\\)\\\".*/\\1/p\" \"$D/wp-config.php\" | head -n 1)\n");
                sb.Append("    printf '%s' \"$v\"; }\n");
                sb.Append("  N=$(g DB_NAME); U=$(g DB_USER); P=$(g DB_PASSWORD); H=$(g DB_HOST)\n");
                sb.Append("elif [ -f \"$D/.env\" ]; then\n");
                sb.Append("  g() { grep \"^$1=\" \"$D/.env\" | head -n 1 | cut -d= -f2- | sed -e 's/\\r$//' -e 's/^[\"'\\'']//' -e 's/[\"'\\'']$//'; }\n");
                sb.Append("  N=$(g DB_DATABASE); U=$(g DB_USERNAME); P=$(g DB_PASSWORD); H=$(g DB_HOST)\n");
                sb.Append("fi\n");
                sb.Append("[ -n \"$N\" ] || { echo 'PORTLY: no database settings found in wp-config.php or .env on the server' >&2; exit 3; }\n");
            }
            else
            {
                // the first lines of standard input: a marker, then user, password, host (never on a command line)
                sb.Append("IFS= read -r M; IFS= read -r U; IFS= read -r P; IFS= read -r H\n");
                sb.Append("N=").Append(Sh(p.DbName)).Append('\n');
            }
            sb.Append("H=${H:-localhost}; DP=''\n");
            sb.Append("case \"$H\" in *:*) DP=${H##*:}; H=${H%%:*};; esac\n");
            sb.Append("P=$(printf '%s' \"$P\" | sed -e 's/\\\\/\\\\\\\\/g' -e 's/\"/\\\\\"/g')\n");
            sb.Append("printf '[client]\\nuser=%s\\npassword=\"%s\"\\nhost=%s\\n' \"$U\" \"$P\" \"$H\" > \"$CNF\"\n");
            sb.Append("[ -n \"$DP\" ] && printf 'port=%s\\n' \"$DP\" >> \"$CNF\"\n");
            // --defaults-extra-file must be the first option of mysqldump (mktemp paths have no spaces)
            sb.Append("DUMP=\"mysqldump --defaults-extra-file=$CNF --single-transaction --quick --routines --triggers --no-tablespaces --default-character-set=utf8mb4\"\n");
            return sb.ToString();
        }

        /// <summary>The credential lines sent on standard input when the database is not read from the server.</summary>
        public static byte[] CredentialInput(DeployProfile p)
        {
            if (!p.Database || p.DbFromServer) return new byte[0];
            return Encoding.UTF8.GetBytes("PORTLY\n" + p.DbUser + "\n" + p.DbPassword + "\n" + (p.DbHost == "" ? "localhost" : p.DbHost) + "\n");
        }

        static string Head(DeployProfile p) { return "D=" + Sh(p.RemoteDir.Trim().TrimEnd('/')) + "\n"; }

        /// <summary>Server side of "Test connection": what is there and which tools exist.</summary>
        public static string ProbeScript(DeployProfile p)
        {
            return Head(p) + "echo PORTLY_OK\n[ -d \"$D\" ] && echo PORTLY_DIR\n[ -f \"$D/wp-config.php\" ] && echo PORTLY_WP\n"
                + "command -v tar >/dev/null && echo PORTLY_TAR\ncommand -v mysqldump >/dev/null && echo PORTLY_MYSQLDUMP\ncommand -v wp >/dev/null && echo PORTLY_WPCLI\nexit 0\n";
        }

        public static string PullFilesScript(DeployProfile p)
        {
            return Head(p) + "cd \"$D\" || exit 2\ntar czf - " + RemoteExcludes(p.Excludes) + " .\n";
        }

        public static string PullDbScript(DeployProfile p)
        {
            // sh has no pipefail: a failed dump says so on standard error (checked by the caller)
            return Head(p) + DbSetupScript(p) + "( $DUMP \"$N\" || echo " + DumpFailed + " >&2 ) | gzip -c\n";
        }

        public static string BackupScript(DeployProfile p, string stamp)
        {
            var name = Regex.Replace(p.Site, "[^A-Za-z0-9_-]", "_") + "-" + stamp;
            var sb = new StringBuilder(Head(p));
            sb.Append("B=\"$HOME/" + BackupFolder + "\"; mkdir -p \"$B\" && chmod 700 \"$B\" || exit 2\n");
            sb.Append("if [ -d \"$D\" ]; then tar czf \"$B/" + name + ".tar.gz\" -C \"$D\" . || exit 2; echo \"PORTLY_BACKUP $B/" + name + ".tar.gz\"; fi\n");
            if (p.Database)
            {
                sb.Append(DbSetupScript(p));
                sb.Append("F=$(mktemp); ( $DUMP \"$N\" || echo x > \"$F\" ) | gzip -c > \"$B/" + name + ".sql.gz\"\n");
                sb.Append("if [ -s \"$F\" ]; then rm -f \"$F\"; echo " + DumpFailed + " >&2; exit 4; fi; rm -f \"$F\"\n");
                sb.Append("echo \"PORTLY_BACKUP $B/" + name + ".sql.gz\"\n");
            }
            return sb.ToString();
        }

        public static string PushFilesScript(DeployProfile p)
        {
            return Head(p) + "mkdir -p \"$D\" && cd \"$D\" || exit 2\ntar xzf - -m --no-same-owner || exit 2\necho PORTLY_FILES_OK\n";
        }

        public static string PushDbScript(DeployProfile p, bool remoteReplace)
        {
            var sb = new StringBuilder(Head(p));
            sb.Append(DbSetupScript(p));
            sb.Append("gunzip -c | mysql --defaults-extra-file=\"$CNF\" --default-character-set=utf8mb4 \"$N\" || exit 4\necho PORTLY_DB_OK\n");
            if (remoteReplace)
                sb.Append("cd \"$D\" && wp search-replace " + Sh(p.LocalUrl.TrimEnd('/')) + " " + Sh(p.RemoteUrl.TrimEnd('/')) + " --all-tables --skip-plugins --skip-themes --report-changed-only || exit 5\n");
            return sb.ToString();
        }

        // ---------------- keys and host keys ----------------
        /// <summary>Creates usr\ssh\id_ed25519_portly (no passphrase, readable only by this user). Returns the public key.</summary>
        public static string CreateKey()
        {
            Directory.CreateDirectory(SshDir);
            if (!File.Exists(DefaultKey))
            {
                var comment = "portly@" + Environment.MachineName.ToLowerInvariant();
                var r = ProcessUtil.Run(SshKeygenExe, "-q -t ed25519 -N \"\" -C " + Arg(comment) + " -f " + Arg(DefaultKey), SshDir, null, 30000);
                if (!File.Exists(DefaultKey)) throw new Exception(r.Output);
            }
            RestrictToUser(DefaultKey);
            return PublicKey(DefaultKey);
        }

        public static string PublicKey(string keyFile)
        {
            var pub = keyFile + ".pub";
            return File.Exists(pub) ? File.ReadAllText(pub).Trim() : "";
        }

        /// <summary>OpenSSH refuses private keys other users can read ("UNPROTECTED PRIVATE KEY FILE").</summary>
        public static void RestrictToUser(string file)
        {
            try
            {
                var sec = new FileSecurity();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(file, sec);
            }
            catch (Exception ex) { Log.Warn("SSH key permissions: " + ex.Message); }
        }

        static string KnownName(DeployProfile p) { return p.Port == 22 ? p.Host : "[" + p.Host + "]:" + p.Port; }

        static bool IsKnown(DeployProfile p)
        {
            if (!File.Exists(KnownHosts)) return false;
            return ProcessUtil.Run(SshKeygenExe, "-F " + Arg(KnownName(p)) + " -f " + Arg(KnownHosts), SshDir, null, 15000).Ok;
        }

        static string Fingerprints(DeployProfile p)
        {
            var r = ProcessUtil.Run(SshKeygenExe, "-l -F " + Arg(KnownName(p)) + " -f " + Arg(KnownHosts), SshDir, null, 15000);
            return string.Join("\n", r.Output.Split('\n').Select(l => l.Trim()).Where(l => l != "" && !l.StartsWith("#")));
        }

        // ---------------- operations ----------------
        public class Probe { public bool Ok, Dir, WordPress, Tar, MySqlDump, WpCli; public string Output = "", NewHostKey; }

        public static async Task<Probe> TestAsync(DeployProfile p, CancellationToken ct)
        {
            Directory.CreateDirectory(SshDir);
            bool known = IsKnown(p);
            var res = new Probe();
            var output = new MemoryStream();
            var err = new StringBuilder();
            int code = await RunAsync(SshExe, SshArgs(p, RemoteCommand(ProbeScript(p))), null, s => s.CopyToAsync(output), err, null, ct);
            var lines = new HashSet<string>(Encoding.UTF8.GetString(output.ToArray()).Split('\n').Select(l => l.Trim()));
            res.Ok = code == 0 && lines.Contains("PORTLY_OK");
            res.Dir = lines.Contains("PORTLY_DIR");
            res.WordPress = lines.Contains("PORTLY_WP");
            res.Tar = lines.Contains("PORTLY_TAR");
            res.MySqlDump = lines.Contains("PORTLY_MYSQLDUMP");
            res.WpCli = lines.Contains("PORTLY_WPCLI");
            res.Output = err.ToString().Trim();
            if (res.Ok && !known) res.NewHostKey = Fingerprints(p);
            return res;
        }

        public static DeploySummary Summarize(Site site, DeployProfile p)
        {
            var s = new DeploySummary { RemoteDir = p.RemoteDir, LocalDb = p.LocalDb };
            foreach (var f in EnumerateFiles(site.Folder, p.Excludes)) { s.Files++; s.Bytes += f.Length; }
            s.Database = p.Database && Database.ListDatabases().Contains(p.LocalDb, StringComparer.OrdinalIgnoreCase);
            return s;
        }

        static IEnumerable<FileInfo> EnumerateFiles(string root, List<string> excludes)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                FileSystemInfo[] items;
                try { items = d.GetFileSystemInfos(); } catch { continue; }
                foreach (var i in items)
                {
                    var rel = i.FullName.Substring(root.Length).TrimStart('\\');
                    if (IsExcluded(rel, excludes)) continue;
                    // junctions / symlinks are not followed (a linked node_modules or vendor could be huge or loop)
                    if ((i.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (i is DirectoryInfo) stack.Push((DirectoryInfo)i);
                    else yield return (FileInfo)i;
                }
            }
        }

        /// <summary>Server -> this computer. Local files are backed up to backup\deploy first.</summary>
        public static async Task PullAsync(Site site, DeployProfile p, bool files, bool database, Action<string> log, CancellationToken ct)
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            if (files)
            {
                var tmp = Path.Combine(Paths.Tmp, "deploy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tmp);
                try
                {
                    log(I18n.T("deploy.logDownloading"));
                    var tar = Start(TarExe, "-xzf - -C " + Arg(tmp), true, false);
                    var tarErrTask = tar.StandardError.ReadToEndAsync();
                    var err = new StringBuilder();
                    long bytes = 0;
                    int code = await RunAsync(SshExe, SshArgs(p, RemoteCommand(PullFilesScript(p))), null,
                        s => CopyAsync(s, tar.StandardInput.BaseStream, n => { bytes += n; }, log, ct), err, log, ct);
                    try { tar.StandardInput.Close(); } catch { }
                    await Task.Run(() => tar.WaitForExit(), ct);
                    var tarErr = await tarErrTask;
                    if (code != 0) throw new Exception(I18n.T("deploy.errRemote", code, Last(err.ToString())));
                    if (tar.ExitCode != 0) throw new Exception("tar: " + Last(tarErr));
                    log(I18n.T("deploy.logReceived", SizeText(bytes)));

                    if (Directory.Exists(site.Folder) && Directory.EnumerateFileSystemEntries(site.Folder).Any())
                    {
                        var zip = BackupLocal(site, p, stamp);
                        log(I18n.T("deploy.logLocalBackup", zip));
                    }
                    Directory.CreateDirectory(site.Folder);
                    ReplaceContents(site.Folder, tmp, p.Excludes);
                    log(I18n.T("deploy.logFilesDone"));
                }
                finally { try { Directory.Delete(tmp, true); } catch { } }
                EnsureLocalWpConfig(site, p, log, ct);
            }
            if (database && p.Database)
            {
                if (ServiceManager.MySql.State != ServiceState.Running) throw new Exception(I18n.T("err.mysqlNotRunning"));
                var err0 = Tools.Database.CreateDatabase(p.LocalDb);
                if (err0 != null) throw new Exception(err0);
                log(I18n.T("deploy.logDbDownloading", p.LocalDb));
                var cnf = LocalClientFile();
                try
                {
                    var mysql = Start(Path.Combine(MySqlService.BinDir, "mysql.exe"), "--defaults-extra-file=" + Arg(cnf) + " --default-character-set=utf8mb4 " + p.LocalDb, true, false);
                    var myErrTask = mysql.StandardError.ReadToEndAsync();
                    var err = new StringBuilder();
                    long bytes = 0;
                    var cred = CredentialInput(p);
                    int code = await RunAsync(SshExe, SshArgs(p, RemoteCommand(PullDbScript(p))),
                        cred.Length == 0 ? (Func<Stream, Task>)null : s => s.WriteAsync(cred, 0, cred.Length),
                        async s =>
                        {
                            using (var gz = new GZipStream(s, CompressionMode.Decompress))
                                await CopyAsync(gz, mysql.StandardInput.BaseStream, n => { bytes += n; }, log, ct);
                        }, err, log, ct);
                    try { mysql.StandardInput.Close(); } catch { }
                    await Task.Run(() => mysql.WaitForExit(), ct);
                    var myErr = await myErrTask;
                    if (code != 0 || err.ToString().Contains(DumpFailed)) throw new Exception(I18n.T("deploy.errRemote", code, Last(err.ToString())));
                    if (mysql.ExitCode != 0) throw new Exception("mysql: " + Last(myErr));
                    log(I18n.T("deploy.logDbDone", SizeText(bytes)));
                }
                finally { try { File.Delete(cnf); } catch { } }
                if (IsWordPress(site) && p.RemoteUrl != "" && p.LocalUrl != "")
                    await LocalWpAsync(site, "search-replace " + Arg(p.RemoteUrl.TrimEnd('/')) + " " + Arg(p.LocalUrl.TrimEnd('/')) + " --all-tables --skip-plugins --skip-themes --report-changed-only", log, ct);
            }
            log(I18n.T("deploy.logPullDone"));
        }

        /// <summary>This computer -> server. The caller has shown the summary and the user typed the site name.</summary>
        public static async Task PushAsync(Site site, DeployProfile p, bool files, bool database, bool remoteBackup, Action<string> log, CancellationToken ct)
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var cred = CredentialInput(p);
            database = database && p.Database;
            Probe probe = await TestAsync(p, ct);
            if (!probe.Ok) throw new Exception(I18n.T("deploy.errConnect", Last(probe.Output)));

            if (remoteBackup)
            {
                log(I18n.T("deploy.logRemoteBackup"));
                var bp = p;
                if (!database) { bp = p.Clone(); bp.Database = false; }
                var output = new MemoryStream();
                var err = new StringBuilder();
                var bcred = CredentialInput(bp);
                int code = await RunAsync(SshExe, SshArgs(bp, RemoteCommand(BackupScript(bp, stamp))),
                    bcred.Length == 0 ? (Func<Stream, Task>)null : s => s.WriteAsync(bcred, 0, bcred.Length),
                    s => s.CopyToAsync(output), err, log, ct);
                if (code != 0) throw new Exception(I18n.T("deploy.errBackup", Last(err.ToString())));
                foreach (var line in Encoding.UTF8.GetString(output.ToArray()).Split('\n').Where(l => l.StartsWith("PORTLY_BACKUP ")))
                    log(I18n.T("deploy.logRemoteBackupFile", line.Substring(14).Trim()));
            }

            if (files)
            {
                log(I18n.T("deploy.logUploading"));
                var tar = Start(TarExe, "-czf - -C " + Arg(site.Folder) + " " + LocalExcludes(p.Excludes) + " .", false, true);
                var err = new StringBuilder();
                long bytes = 0;
                var outText = new MemoryStream();
                var readTarErr = tar.StandardError.ReadToEndAsync();
                int code = await RunAsync(SshExe, SshArgs(p, RemoteCommand(PushFilesScript(p))),
                    s => CopyAsync(tar.StandardOutput.BaseStream, s, n => { bytes += n; }, log, ct),
                    s => s.CopyToAsync(outText), err, log, ct);
                await Task.Run(() => tar.WaitForExit(), ct);
                if (tar.ExitCode != 0) throw new Exception("tar: " + Last(await readTarErr));
                if (code != 0 || !Encoding.UTF8.GetString(outText.ToArray()).Contains("PORTLY_FILES_OK"))
                    throw new Exception(I18n.T("deploy.errRemote", code, Last(err.ToString())));
                log(I18n.T("deploy.logSent", SizeText(bytes)));
            }

            if (database)
            {
                if (ServiceManager.MySql.State != ServiceState.Running) throw new Exception(I18n.T("err.mysqlNotRunning"));
                bool wp = IsWordPress(site) && p.RemoteUrl != "" && p.LocalUrl != "";
                bool remoteReplace = wp && probe.WpCli;
                var dump = Path.Combine(Paths.Tmp, "deploy-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sql");
                try
                {
                    log(I18n.T("deploy.logDbExport", p.LocalDb));
                    if (wp && !remoteReplace)
                    {
                        // no WP-CLI on the server: replace the addresses in an exported copy (the local database stays as it is)
                        await LocalWpAsync(site, "search-replace " + Arg(p.LocalUrl.TrimEnd('/')) + " " + Arg(p.RemoteUrl.TrimEnd('/')) + " --all-tables --skip-plugins --skip-themes --export=" + Arg(dump), log, ct);
                    }
                    else
                    {
                        var cnf = LocalClientFile();
                        try
                        {
                            var r = await Task.Run(() => ProcessUtil.Run(Path.Combine(MySqlService.BinDir, "mysqldump.exe"),
                                "--defaults-extra-file=" + Arg(cnf) + " --single-transaction --routines --triggers --default-character-set=utf8mb4 --result-file=" + Arg(dump) + " " + p.LocalDb,
                                MySqlService.BinDir, null, 3600000), ct);
                            if (!r.Ok || !File.Exists(dump)) throw new Exception("mysqldump: " + Last(r.Output));
                        }
                        finally { try { File.Delete(cnf); } catch { } }
                    }
                    log(I18n.T("deploy.logDbUploading"));
                    var err = new StringBuilder();
                    var outText = new MemoryStream();
                    int code = await RunAsync(SshExe, SshArgs(p, RemoteCommand(PushDbScript(p, remoteReplace))),
                        async s =>
                        {
                            if (cred.Length > 0) await s.WriteAsync(cred, 0, cred.Length);
                            using (var gz = new GZipStream(s, CompressionLevel.Fastest, true))
                            using (var f = File.OpenRead(dump))
                                await CopyAsync(f, gz, null, log, ct);
                        },
                        s => s.CopyToAsync(outText), err, log, ct);
                    if (code != 0 || !Encoding.UTF8.GetString(outText.ToArray()).Contains("PORTLY_DB_OK"))
                        throw new Exception(I18n.T("deploy.errRemote", code, Last(err.ToString())));
                    log(I18n.T("deploy.logDbDone", SizeText(new FileInfo(dump).Length)));
                    if (remoteReplace) log(I18n.T("deploy.logReplacedRemote", p.LocalUrl, p.RemoteUrl));
                }
                finally { try { File.Delete(dump); } catch { } }
            }
            log(I18n.T("deploy.logPushDone"));
        }

        static string SizeText(long bytes) { return Portly.UI.Ui.FormatBytes(bytes); }

        static string Last(string text)
        {
            var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 6)));
        }

        public static bool IsWordPress(Site site) { return File.Exists(Path.Combine(site.Folder, "wp-config.php")) || File.Exists(Path.Combine(site.Folder, "wp-load.php")); }

        // ---------------- local helpers ----------------
        /// <summary>Zip of the local files that a pull replaces (excluded ones stay untouched, so they are not in it).</summary>
        static string BackupLocal(Site site, DeployProfile p, string stamp)
        {
            Directory.CreateDirectory(BackupDir);
            var zip = Path.Combine(BackupDir, Regex.Replace(site.Name, "[^A-Za-z0-9_-]", "_") + "-" + stamp + ".zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var f in EnumerateFiles(site.Folder, p.Excludes))
                    z.CreateEntryFromFile(f.FullName, f.FullName.Substring(site.Folder.Length).TrimStart('\\').Replace('\\', '/'), CompressionLevel.Fastest);
            return zip;
        }

        /// <summary>Make target equal to source, except the excluded paths of target which are kept.</summary>
        public static void ReplaceContents(string target, string source, List<string> excludes)
        {
            DeleteNotExcluded(target, target, excludes);
            CopyTree(source, target);
        }

        static void DeleteNotExcluded(string root, string dir, List<string> excludes)
        {
            foreach (var e in Directory.GetFileSystemEntries(dir))
            {
                var rel = e.Substring(root.Length).TrimStart('\\');
                if (IsExcluded(rel, excludes)) continue;
                var attr = File.GetAttributes(e);
                if ((attr & FileAttributes.Directory) != 0)
                {
                    if ((attr & FileAttributes.ReparsePoint) != 0) { Directory.Delete(e); continue; } // the link, never its target
                    DeleteNotExcluded(root, e, excludes);
                    if (!Directory.EnumerateFileSystemEntries(e).Any()) Directory.Delete(e);
                }
                else
                {
                    if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(e, attr & ~FileAttributes.ReadOnly);
                    File.Delete(e);
                }
            }
        }

        static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var f in Directory.GetFiles(source))
            {
                var dest = Path.Combine(target, Path.GetFileName(f));
                if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                File.Copy(f, dest, true);
            }
            foreach (var d in Directory.GetDirectories(source)) CopyTree(d, Path.Combine(target, Path.GetFileName(d)));
        }

        /// <summary>wp-config.php is not transferred (it holds the server's database login). A first pull creates a local one.</summary>
        static void EnsureLocalWpConfig(Site site, DeployProfile p, Action<string> log, CancellationToken ct)
        {
            var cfg = Path.Combine(site.Folder, "wp-config.php");
            var sample = Path.Combine(site.Folder, "wp-config-sample.php");
            if (File.Exists(cfg) || !File.Exists(sample) || !p.Database) return;
            var port = Settings.Current.MysqlPort;
            var text = File.ReadAllText(sample, Encoding.UTF8);
            text = Define(text, "DB_NAME", p.LocalDb);
            text = Define(text, "DB_USER", "root");
            text = Define(text, "DB_PASSWORD", Settings.Current.MysqlRootPassword);
            text = Define(text, "DB_HOST", "127.0.0.1" + (port == 3306 ? "" : ":" + port));
            foreach (var key in new[] { "AUTH_KEY", "SECURE_AUTH_KEY", "LOGGED_IN_KEY", "NONCE_KEY", "AUTH_SALT", "SECURE_AUTH_SALT", "LOGGED_IN_SALT", "NONCE_SALT" })
                text = Define(text, key, Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
            File.WriteAllText(cfg, text, new UTF8Encoding(false));
            log(I18n.T("deploy.logWpConfig"));
        }

        static string Define(string cfg, string constant, string value)
        {
            var php = (value ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
            return Regex.Replace(cfg, @"(define\(\s*'" + constant + @"'\s*,\s*')(?:[^'\\]|\\.)*(')", m => m.Groups[1].Value + php + m.Groups[2].Value);
        }

        static string LocalClientFile()
        {
            if (MySqlService.BinDir == null) throw new Exception(I18n.T("err.notInstalled", "MySQL"));
            var pass = Settings.Current.MysqlRootPassword.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var f = Path.Combine(Paths.Tmp, "my-deploy-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cnf");
            File.WriteAllText(f, "[client]\r\nuser=root\r\npassword=\"" + pass + "\"\r\nhost=127.0.0.1\r\nport=" + Settings.Current.MysqlPort + "\r\n", new UTF8Encoding(false));
            return f;
        }

        static async Task LocalWpAsync(Site site, string args, Action<string> log, CancellationToken ct)
        {
            if (PackageTypes.ActiveDir("wpcli") == null)
            {
                var entry = Catalog.Recommended("wpcli");
                if (entry == null) throw new Exception(I18n.T("err.notInstalled", "WP-CLI"));
                log(I18n.T("log.downloading", "WP-CLI"));
                await Installer.InstallAsync(entry, null, ct);
            }
            var phpDir = PackageTypes.ActiveDir("php");
            if (phpDir == null) throw new Exception(I18n.T("err.notInstalled", "PHP"));
            var phar = Path.Combine(PackageTypes.ActiveDir("wpcli"), "wp-cli.phar");
            log("wp " + args);
            var r = await Task.Run(() => ProcessUtil.Run(Path.Combine(phpDir, "php.exe"),
                "-d error_reporting=24575 -d display_errors=stderr " + Arg(phar) + " " + args + " --path=" + Arg(site.Folder), site.Folder, null, 1800000), ct);
            if (!r.Ok) throw new Exception("WP-CLI: " + Last(r.Output));
            log(Last(r.Output));
        }

        // ---------------- processes ----------------
        static Process Start(string exe, string args, bool stdin, bool stdout)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdin,
                RedirectStandardOutput = stdout,
                RedirectStandardError = true,
                WorkingDirectory = Paths.Tmp,
            };
            var p = Process.Start(psi);
            JobObject.Add(p);
            return p;
        }

        /// <summary>
        /// Runs a program with binary standard input / output streams. Standard error goes to the log line by line
        /// (ssh prints only its own messages there, never passwords). Returns the exit code; -1 when cancelled.
        /// </summary>
        static async Task<int> RunAsync(string exe, string args, Func<Stream, Task> writeStdin, Func<Stream, Task> readStdout, StringBuilder stderr, Action<string> log, CancellationToken ct)
        {
            var p = Start(exe, args, true, readStdout != null);
            using (ct.Register(() => { try { ProcessUtil.KillTree(p.Id); } catch { } }))
            {
                var errTask = Task.Run(() =>
                {
                    string line;
                    while ((line = p.StandardError.ReadLine()) != null)
                    {
                        lock (stderr) stderr.AppendLine(line);
                        if (log != null && line.Trim() != "" && !line.StartsWith("Warning: Permanently added")) log(line);
                    }
                });
                var outTask = readStdout != null ? readStdout(p.StandardOutput.BaseStream) : Task.FromResult(0);
                try
                {
                    if (writeStdin != null) await writeStdin(p.StandardInput.BaseStream);
                }
                finally { try { p.StandardInput.Close(); } catch { } }
                await outTask;
                await errTask;
                await Task.Run(() => p.WaitForExit());
                ct.ThrowIfCancellationRequested();
                return p.ExitCode;
            }
        }

        static async Task CopyAsync(Stream from, Stream to, Action<int> counted, Action<string> log, CancellationToken ct)
        {
            var buf = new byte[81920];
            long total = 0, nextReport = 8L << 20;
            int n;
            while ((n = await from.ReadAsync(buf, 0, buf.Length, ct)) > 0)
            {
                await to.WriteAsync(buf, 0, n, ct);
                total += n;
                if (counted != null) counted(n);
                if (log != null && total >= nextReport) { log(I18n.T("deploy.logProgress", SizeText(total))); nextReport += 8L << 20; }
            }
            await to.FlushAsync(ct);
        }
    }
}
