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
    ///   # >>> Portly 1a2b3c4d (auto-generated, do not edit) >>>  ...  # <<< Portly 1a2b3c4d <<<
    /// The id is this Portly folder's: the hosts file is shared by every Portly on the PC (a 2.0 beta next to 1.x, two
    /// copies), and each one changes only its own block. Portly 1.x wrote a block without an id; it is taken over only when
    /// this Portly writes all of its names anyway, so the block of another Portly is never emptied.
    /// Writing needs administrator rights, so Portly starts itself elevated (one UAC / password prompt)
    /// only when the block really changes. Lines outside the block are never touched.
    /// On macOS and Linux the default domain ending is "localhost": browsers resolve *.localhost themselves, so the
    /// file is only written for names with another ending.
    /// </summary>
    public static class HostsFile
    {
        const string LegacyBegin = "# >>> Portly (auto-generated, do not edit) >>>";
        const string LegacyEnd = "# <<< Portly <<<";
        static readonly Regex IdRx = new Regex(@"^[0-9a-f]{8}$");
        static string Begin(string id) { return "# >>> Portly " + id + " (auto-generated, do not edit) >>>"; }
        static string End(string id) { return "# <<< Portly " + id + " <<<"; }

        /// <summary>This Portly folder's block id (8 hex digits, the same for every spelling of the folder on Windows).</summary>
        public static string Id
        {
            get
            {
                var root = Path.GetFullPath(Paths.Root).TrimEnd('\\', '/');
                if (OS.IsWindows) root = root.ToLowerInvariant();
                uint h = 2166136261;
                foreach (var b in Encoding.UTF8.GetBytes(root)) { h ^= b; h *= 16777619; }
                return h.ToString("x8");
            }
        }
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

        static string BuildBlock(IEnumerable<string> hosts, string id)
        {
            var sb = new StringBuilder();
            sb.Append(Begin(id)).Append(Nl);
            foreach (var h in hosts.Where(x => HostRx.IsMatch(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                sb.Append("127.0.0.1\t").Append(h).Append(Nl);
            sb.Append(End(id)).Append(Nl);
            return sb.ToString();
        }

        /// <summary>The text of a hosts file after Portly's block was set to these names (pure: no file is read or written).</summary>
        public static string ApplyBlock(string current, IEnumerable<string> hosts) { return ApplyBlock(current, hosts, Id); }

        public static string ApplyBlock(string current, IEnumerable<string> hosts, string id)
        {
            var list = hosts.ToList();
            return Compose(current ?? "", BuildBlock(list, id), list, id);
        }

        /// <summary>The text of a hosts file without this Portly's block.</summary>
        public static string RemoveBlock(string current) { return RemoveBlock(current, Id); }

        public static string RemoveBlock(string current, string id)
        {
            var rest = Strip(current ?? "", Begin(id), End(id)).TrimEnd('\r', '\n', ' ', '\t');
            return rest == "" ? "" : rest + Nl;
        }

        static string Strip(string content, string begin, string end)
        {
            int b = content.IndexOf(begin, StringComparison.Ordinal);
            if (b < 0) return content;
            int e = content.IndexOf(end, b, StringComparison.Ordinal);
            if (e < 0) return content;
            e += end.Length;
            while (e < content.Length && (content[e] == '\r' || content[e] == '\n')) e++;
            return content.Substring(0, b) + content.Substring(e);
        }

        /// <summary>Names in Portly 1.x's block (the one without an id), or null when there is none.</summary>
        static List<string> LegacyNames(string content)
        {
            int b = content.IndexOf(LegacyBegin, StringComparison.Ordinal);
            if (b < 0) return null;
            int e = content.IndexOf(LegacyEnd, b, StringComparison.Ordinal);
            if (e < 0) return null;
            var names = new List<string>();
            foreach (var line in content.Substring(b + LegacyBegin.Length, e - b - LegacyBegin.Length).Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && !parts[0].StartsWith("#")) names.AddRange(parts.Skip(1));
            }
            return names;
        }

        /// <summary>This Portly's block is in the hosts file (it may have to be emptied, e.g. after the last real domain went live).</summary>
        public static bool HasPortlyBlock() { return Current().IndexOf(Begin(Id), StringComparison.Ordinal) >= 0; }

        static string Current()
        {
            try { return File.ReadAllText(FilePath, Encoding.Default); } catch { return ""; }
        }

        static string Compose(string current, string block, List<string> hosts, string id)
        {
            var rest = Strip(current, Begin(id), End(id));
            // Portly 1.x's block (no id): taken over when this Portly writes every name in it anyway (1.x updated to 2.x in the
            // same folder); otherwise it belongs to another Portly on this PC and stays as it is
            var legacy = LegacyNames(rest);
            if (legacy != null && hosts.Count > 0 && legacy.All(n => hosts.Contains(n, StringComparer.OrdinalIgnoreCase)))
                rest = Strip(rest, LegacyBegin, LegacyEnd);
            rest = rest.TrimEnd('\r', '\n', ' ', '\t');
            if (block.IndexOf("127.0.0.1", StringComparison.Ordinal) < 0) return rest + Nl;
            return rest + Nl + Nl + block;
        }

        /// <summary>Update the hosts file if needed. Returns false when the user refused elevation.</summary>
        public static bool Sync(IEnumerable<string> hosts)
        {
            var list = hosts.ToList();
            var id = Id;
            var block = BuildBlock(list, id);
            var current = Current();
            var wanted = Compose(current, block, list, id);
            if (wanted == current) return true;

            if (TryWrite(wanted)) return Verify(block, id);

            int code;
            if (!OS.IsWindows) code = UnixElevatedWrite(wanted);
            else
            {
                var tmp = Path.Combine(Paths.Tmp, "hosts-block.txt");
                // the id goes along: the elevated helper may not see PORTLY_HOME, so it could not work it out itself
                File.WriteAllText(tmp, "# id " + id + Nl + block, Encoding.ASCII);
                code = Elevate.RunSelf("--hosts-apply", tmp);
                try { File.Delete(tmp); } catch { }
            }
            if (code == 0) return Verify(block, id);
            Log.Warn(code == -2 ? I18n.T("log.hostsCancelled") : I18n.T("log.hostsFailed", code));
            return false;
        }

        /// <summary>
        /// Read the file back: antivirus (Defender "HostsFileHijack") or another tool may undo the change silently.
        /// Then flush the DNS cache so browsers see the new names at once.
        /// </summary>
        static bool Verify(string block, string id)
        {
            System.Threading.Thread.Sleep(300);
            // an empty list removes the block: then check that it is gone
            var now = Current();
            bool ok = block.IndexOf("127.0.0.1", StringComparison.Ordinal) >= 0 ? now.Contains(block.Trim()) : now.IndexOf(Begin(id), StringComparison.Ordinal) < 0;
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

        /// <summary>
        /// macOS / Linux: a fixed shell line writes the text as root. Portly itself is not run as root (an AppImage
        /// lives on the user's FUSE mount, which root cannot open); the text goes in as an argument, never as code.
        /// </summary>
        public const string UnixWriteScript = "printf '%s' \"$2\" > \"$1\"";

        static int UnixElevatedWrite(string content)
        {
            return Elevate.Run("/bin/sh", "-c", UnixWriteScript, "sh", FilePath, content);
        }

        /// <summary>The whole file for the editor. Throws when it cannot be read: an empty text must never be saved over it.</summary>
        public static string ReadAll() { return File.ReadAllText(FilePath, Encoding.Default); }

        /// <summary>Save the editor's text. Returns 0, -2 (UAC refused) or an error code.</summary>
        public static int SaveAll(string content)
        {
            if (!TryWrite(content))
            {
                int code;
                if (!OS.IsWindows) code = UnixElevatedWrite(content);
                else
                {
                    var tmp = Path.Combine(Paths.Tmp, "hosts-edit.txt");
                    File.WriteAllText(tmp, content, Encoding.Default);
                    code = Elevate.RunSelf("--hosts-write", tmp);
                    try { File.Delete(tmp); } catch { }
                }
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
                string id = null;
                foreach (var l in lines)
                {
                    var t = l.Trim();
                    if (id == null && t.StartsWith("# id ")) { id = t.Substring(5).Trim(); if (!IdRx.IsMatch(id)) return 3; continue; }
                    if (t == "" || t.StartsWith("#")) continue;
                    var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || parts[0] != "127.0.0.1" || !HostRx.IsMatch(parts[1])) return 3;
                    hosts.Add(parts[1]);
                }
                if (id == null) id = Id;
                WriteOver(Compose(Current(), BuildBlock(hosts, id), hosts, id));
                return 0;
            }
            catch { return 2; }
        }
    }
}
