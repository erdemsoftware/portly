using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Portly.Core;

namespace Portly.Config
{
    /// <summary>
    /// Keeps a Portly block in the hosts file (Windows: System32\drivers\etc\hosts, macOS / Linux: /etc/hosts):
    ///   # >>> Portly >>>  ...  # <<< Portly <<<
    /// Writing needs administrator rights, so Portly starts itself elevated (one UAC / password prompt)
    /// only when the block really changes. Lines outside the block are never touched.
    /// On macOS and Linux the default domain ending is "localhost": browsers resolve *.localhost themselves, so the
    /// file is only written for names with another ending.
    /// </summary>
    public static class HostsFile
    {
        const string Begin = "# >>> Portly (auto-generated, do not edit) >>>";
        const string End = "# <<< Portly <<<";
        static readonly Regex HostRx = new Regex(@"^[a-z0-9]([a-z0-9_.-]{0,250}[a-z0-9])?$", RegexOptions.IgnoreCase);

        /// <summary>Must this name go into the hosts file? Not for wildcards (impossible) or *.localhost (browsers resolve it).</summary>
        public static bool NeedsEntry(string host)
        {
            if (string.IsNullOrEmpty(host) || host.Contains("*")) return false;
            var h = host.ToLowerInvariant();
            return h != "localhost" && !h.EndsWith(".localhost") && HostRx.IsMatch(h);
        }

        public static string FilePath
        {
            get
            {
                if (!OS.IsWindows) return "/etc/hosts";
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            }
        }

        /// <summary>Line break of the file: CRLF on Windows, LF elsewhere.</summary>
        static string Nl { get { return OS.IsWindows ? "\r\n" : "\n"; } }

        static string BuildBlock(IEnumerable<string> hosts)
        {
            var sb = new StringBuilder();
            sb.Append(Begin).Append(Nl);
            foreach (var h in hosts.Where(x => HostRx.IsMatch(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                sb.Append("127.0.0.1\t").Append(h).Append(Nl);
            sb.Append(End).Append(Nl);
            return sb.ToString();
        }

        /// <summary>The text of a hosts file after Portly's block was set to these names (pure: no file is read or written).</summary>
        public static string ApplyBlock(string current, IEnumerable<string> hosts) { return Compose(current ?? "", BuildBlock(hosts)); }

        /// <summary>The text of a hosts file without Portly's block.</summary>
        public static string RemoveBlock(string current)
        {
            var rest = Strip(current ?? "").TrimEnd('\r', '\n', ' ', '\t');
            return rest == "" ? "" : rest + Nl;
        }

        static string Strip(string content)
        {
            int b = content.IndexOf(Begin, StringComparison.Ordinal);
            if (b < 0) return content;
            int e = content.IndexOf(End, b, StringComparison.Ordinal);
            if (e < 0) return content;
            e += End.Length;
            while (e < content.Length && (content[e] == '\r' || content[e] == '\n')) e++;
            return content.Substring(0, b) + content.Substring(e);
        }

        /// <summary>Portly's block is in the hosts file (it may have to be emptied, e.g. after the last real domain went live).</summary>
        public static bool HasPortlyBlock() { return Current().IndexOf(Begin, StringComparison.Ordinal) >= 0; }

        static string Current()
        {
            try { return File.ReadAllText(FilePath, Encoding.Default); } catch { return ""; }
        }

        static string Compose(string current, string block)
        {
            var rest = Strip(current).TrimEnd('\r', '\n', ' ', '\t');
            if (block.IndexOf("127.0.0.1", StringComparison.Ordinal) < 0) return rest + Nl;
            return rest + Nl + Nl + block;
        }

        /// <summary>Update the hosts file if needed. Returns false when the user refused elevation.</summary>
        public static bool Sync(IEnumerable<string> hosts)
        {
            var block = BuildBlock(hosts);
            var current = Current();
            var wanted = Compose(current, block);
            if (wanted == current) return true;

            if (TryWrite(wanted)) return Verify(block);

            var tmp = Path.Combine(Paths.Tmp, "hosts-block.txt");
            File.WriteAllText(tmp, block, Encoding.ASCII);
            var code = Elevate.RunSelf("--hosts-apply", tmp);
            try { File.Delete(tmp); } catch { }
            if (code == 0) return Verify(block);
            Log.Warn(code == -2 ? I18n.T("log.hostsCancelled") : I18n.T("log.hostsFailed", code));
            return false;
        }

        /// <summary>
        /// Read the file back: antivirus (Defender "HostsFileHijack") or another tool may undo the change silently.
        /// Then flush the DNS cache so browsers see the new names at once.
        /// </summary>
        static bool Verify(string block)
        {
            System.Threading.Thread.Sleep(300);
            bool ok = Current().Contains(block.Trim());
            FlushDns();
            if (ok) { Log.Info(I18n.T("log.hostsUpdated")); return true; }
            Log.Warn(I18n.T("hosts.blocked", FilePath));
            return true; // do not ask for UAC again in this session; the warning explains what to do
        }

        static bool TryWrite(string content)
        {
            try
            {
                var attr = File.GetAttributes(FilePath);
                if ((attr & FileAttributes.ReadOnly) != 0) return false;
                File.WriteAllText(FilePath, content, Encoding.Default);
                return true;
            }
            catch { return false; }
        }

        /// <summary>The whole file for the editor. Throws when it cannot be read: an empty text must never be saved over it.</summary>
        public static string ReadAll() { return File.ReadAllText(FilePath, Encoding.Default); }

        /// <summary>Save the editor's text. Returns 0, -2 (UAC refused) or an error code.</summary>
        public static int SaveAll(string content)
        {
            if (!TryWrite(content))
            {
                var tmp = Path.Combine(Paths.Tmp, "hosts-edit.txt");
                File.WriteAllText(tmp, content, Encoding.Default);
                var code = Elevate.RunSelf("--hosts-write", tmp);
                try { File.Delete(tmp); } catch { }
                if (code != 0) return code;
            }
            System.Threading.Thread.Sleep(300);
            if (ReadAll() != content) return 4; // antivirus or another tool undid it
            FlushDns();
            return 0;
        }

        /// <summary>Windows: ipconfig /flushdns. macOS: dscacheutil. Linux resolvers do not cache /etc/hosts.</summary>
        static void FlushDns()
        {
            try
            {
                if (OS.IsWindows) ProcessUtil.Run("ipconfig.exe", "/flushdns", null, null, 15000);
                else if (OS.IsMac) ProcessUtil.Run("dscacheutil", "-flushcache", "/", null, 15000);
            }
            catch { }
        }

        /// <summary>Runs in the elevated helper process ("Portly.exe --hosts-write file"): the user's own edit, whole file.</summary>
        public static int WriteElevated(string file)
        {
            try
            {
                if (new FileInfo(file).Length > 4 * 1024 * 1024) return 3;
                var content = File.ReadAllText(file, Encoding.Default);
                if (content.IndexOf('\0') >= 0) return 3;
                WriteOver(content);
                return 0;
            }
            catch { return 2; }
        }

        static void WriteOver(string content)
        {
            var attr = File.GetAttributes(FilePath);
            bool ro = (attr & FileAttributes.ReadOnly) != 0;
            if (ro) File.SetAttributes(FilePath, attr & ~FileAttributes.ReadOnly);
            File.WriteAllText(FilePath, content, Encoding.Default);
            if (ro) File.SetAttributes(FilePath, attr);
        }

        /// <summary>Runs in the elevated helper process ("Portly.exe --hosts-apply file").</summary>
        public static int ApplyElevated(string blockFile)
        {
            try
            {
                var lines = File.ReadAllLines(blockFile);
                // accept only our own well-formed lines
                var hosts = new List<string>();
                foreach (var l in lines)
                {
                    var t = l.Trim();
                    if (t == "" || t.StartsWith("#")) continue;
                    var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || parts[0] != "127.0.0.1" || !HostRx.IsMatch(parts[1])) return 3;
                    hosts.Add(parts[1]);
                }
                WriteOver(Compose(Current(), BuildBlock(hosts)));
                return 0;
            }
            catch { return 2; }
        }
    }
}
