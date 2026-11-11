using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Portly.Core;

namespace Portly.Tools
{
    /// <summary>
    /// Large SQL import: .sql, .sql.gz or .zip (first .sql inside) streamed into the mysql client line by line,
    /// so a file of several GB never sits in memory. Compatibility fixes (DumpFilter) are applied on the way.
    /// </summary>
    public static class SqlImport
    {
        public class Result
        {
            public bool Ok;
            public bool Cancelled;
            public int Errors;
            public string Message = "";
            public long Line;
            public string Statement = "";
        }

        public static bool IsSupported(string file)
        {
            var f = (file ?? "").ToLowerInvariant();
            return f.EndsWith(".sql") || f.EndsWith(".sql.gz") || f.EndsWith(".gz") || f.EndsWith(".zip");
        }

        /// <summary>Opens the SQL text. <paramref name="counter"/> counts the bytes that make up <paramref name="total"/>.</summary>
        static Stream Open(string file, out CountingStream counter, out long total)
        {
            var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var lower = file.ToLowerInvariant();
            if (lower.EndsWith(".zip"))
            {
                var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
                if (entry == null) { zip.Dispose(); throw new Exception(I18n.T("sqlimp.noSqlInZip")); }
                total = entry.Length;
                counter = new CountingStream(entry.Open(), zip);
                return counter;
            }
            total = fs.Length;
            counter = new CountingStream(fs, null);
            if (lower.EndsWith(".gz")) return new GZipStream(counter, CompressionMode.Decompress);
            return counter;
        }

        /// <summary>
        /// Import into <paramref name="database"/> (null: the file chooses with USE / CREATE DATABASE).
        /// force = continue after errors (used for whole dumps); otherwise stop at the first error and report its line.
        /// </summary>
        public static Result Run(string file, string database, bool force, Action<long, long> progress, CancellationToken ct)
        {
            var res = new Result();
            if (database != null && !Database.IsValidName(database)) { res.Message = I18n.T("err.dbName"); return res; }
            CountingStream counter;
            long total;
            string cnf = null;
            var errors = new StringBuilder();
            System.Diagnostics.Process p = null;
            try
            {
                using (var input = Open(file, out counter, out total))
                {
                    p = Database.StartClient("mysql.exe", "--default-character-set=utf8mb4" + (force ? " --force" : "") + (database != null ? " \"" + database + "\"" : ""), out cnf);
                    var proc = p;
                    p.ErrorDataReceived += (o, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        lock (errors) { if (e.Data.StartsWith("ERROR")) res.Errors++; if (errors.Length < 20000) errors.AppendLine(e.Data); }
                    };
                    p.OutputDataReceived += (o, e) => { };
                    p.BeginErrorReadLine();
                    p.BeginOutputReadLine();
                    using (ct.Register(() => ProcessUtil.KillTree(proc.Id)))
                    {
                        var filter = DumpFilter.ForActive();
                        long lastReport = -1;
                        try
                        {
                            using (var reader = new StreamReader(input, new UTF8Encoding(false), true, 1 << 16))
                            using (var writer = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false), 1 << 16))
                            {
                                string line;
                                while ((line = reader.ReadLine()) != null)
                                {
                                    if (ct.IsCancellationRequested || p.HasExited) break;
                                    writer.Write(filter.Apply(line));
                                    writer.Write('\n');
                                    if (progress != null && counter.Count - lastReport >= 256 * 1024)
                                    {
                                        lastReport = counter.Count;
                                        progress(Math.Min(counter.Count, total), total);
                                    }
                                }
                            }
                        }
                        catch (IOException) { }   // the client stopped at an error: the pipe is closed
                        p.WaitForExit();
                        if (progress != null && !ct.IsCancellationRequested) progress(total, total);
                    }
                    if (ct.IsCancellationRequested) { res.Cancelled = true; res.Message = I18n.T("sqlimp.cancelled"); return res; }
                    var errText = Database.Clean(new RunResult { Output = errors.ToString() });
                    res.Ok = p.ExitCode == 0 || force;
                    if (!res.Ok || res.Errors > 0)
                    {
                        var first = errText.Split('\n').FirstOrDefault(l => l.StartsWith("ERROR")) ?? errText;
                        res.Message = first;
                        var m = Regex.Match(first, @"at line (\d+)");
                        if (m.Success) { res.Line = long.Parse(m.Groups[1].Value); res.Statement = LineOf(file, res.Line); }
                    }
                }
            }
            catch (Exception ex) { res.Ok = false; res.Message = ex.Message; }
            finally
            {
                if (cnf != null) try { File.Delete(cnf); } catch { }
                if (p != null) p.Dispose();
            }
            if (res.Ok) Log.Info(I18n.T("sqlimp.logDone", Path.GetFileName(file), database ?? "-", res.Errors));
            return res;
        }

        /// <summary>The text of one line of the file (shortened), to show where an error happened.</summary>
        static string LineOf(string file, long number)
        {
            try
            {
                CountingStream c; long t;
                using (var input = Open(file, out c, out t))
                using (var reader = new StreamReader(input, new UTF8Encoding(false)))
                {
                    string line; long n = 0;
                    while ((line = reader.ReadLine()) != null)
                        if (++n == number) return line.Length > 400 ? line.Substring(0, 400) + " ..." : line;
                }
            }
            catch { }
            return "";
        }

        /// <summary>Counts the bytes read through it (progress of the compressed / raw input).</summary>
        class CountingStream : Stream
        {
            readonly Stream _inner;
            readonly IDisposable _owner;
            public long Count;
            public CountingStream(Stream inner, IDisposable owner) { _inner = inner; _owner = owner; }
            public override int Read(byte[] buffer, int offset, int count) { int n = _inner.Read(buffer, offset, count); Count += n; return n; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { return Count; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { _inner.Dispose(); if (_owner != null) _owner.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
