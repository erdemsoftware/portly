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
    /// Keeps a Portly block in the Windows hosts file:
    ///   # >>> Portly >>>  ...  # <<< Portly <<<
    /// Writing needs administrator rights, so Portly starts itself elevated (one UAC prompt)
    /// only when the block really changes. Lines outside the block are never touched.
    /// </summary>
    public static class HostsFile
    {
        const string Begin = "# >>> Portly (auto-generated, do not edit) >>>";
        const string End = "# <<< Portly <<<";
        static readonly Regex HostRx = new Regex(@"^[a-z0-9]([a-z0-9_.-]{0,250}[a-z0-9])?$", RegexOptions.IgnoreCase);

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts"); }
        }

        static string BuildBlock(IEnumerable<string> hosts)
        {
            var sb = new StringBuilder();
            sb.Append(Begin).Append("\r\n");
            foreach (var h in hosts.Where(x => HostRx.IsMatch(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                sb.Append("127.0.0.1\t").Append(h).Append("\r\n");
            sb.Append(End).Append("\r\n");
            return sb.ToString();
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

        static string Current()
        {
            try { return File.ReadAllText(FilePath, Encoding.Default); } catch { return ""; }
        }

        static string Compose(string current, string block)
        {
            var rest = Strip(current).TrimEnd('\r', '\n', ' ', '\t');
            if (block.IndexOf("127.0.0.1", StringComparison.Ordinal) < 0) return rest + "\r\n";
            return rest + "\r\n\r\n" + block;
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
            var code = WinUtil.RunElevated(Paths.Exe, "--hosts-apply \"" + tmp + "\"");
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
            try { ProcessUtil.Run("ipconfig.exe", "/flushdns", null, null, 15000); } catch { }
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
                var content = Compose(Current(), BuildBlock(hosts));
                var attr = File.GetAttributes(FilePath);
                bool ro = (attr & FileAttributes.ReadOnly) != 0;
                if (ro) File.SetAttributes(FilePath, attr & ~FileAttributes.ReadOnly);
                File.WriteAllText(FilePath, content, Encoding.Default);
                if (ro) File.SetAttributes(FilePath, attr);
                return 0;
            }
            catch { return 2; }
        }
    }
}
