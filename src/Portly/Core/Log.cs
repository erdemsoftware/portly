using System;
using System.IO;
using System.Text;

namespace Portly.Core
{
    public static class Log
    {
        static readonly object Sync = new object();
        public static event Action<string> Written;

        static string FilePath { get { return Path.Combine(Paths.Logs, "portly.log"); } }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }
        public static void Error(string msg, Exception ex) { Write("ERROR", msg + ": " + ex.Message); WriteFileOnly(ex.ToString()); }

        static void Write(string level, string msg)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + (level == "INFO" ? "" : level + ": ") + msg;
            WriteFileOnly(DateTime.Now.ToString("yyyy-MM-dd ") + line);
            var h = Written;
            if (h != null) { try { h(line); } catch { } }
        }

        public static void WriteFileOnly(string text)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Paths.Logs);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        var old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, text + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
