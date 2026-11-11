using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Config;
using Portly.Core;
using Portly.Packages;

namespace Portly.Services
{
    /// <summary>
    /// Health check of the running PHP pools. OnExit sees a crash, but a pool that holds its port and answers nobody leaves
    /// only 503s behind. So every 30 s each pool gets a FastCGI management question, which a free worker answers without
    /// running a script.
    ///
    /// No answer does not mean hung: a pool with all workers on long requests cannot answer either, and the connection then
    /// simply waits in the queue. Windows shows every connection of the pool under the parent process, accepted or queued
    /// alike, so what can be told apart is this (measured on real php-cgi):
    ///  - connections on the port (without the checks) fewer than workers: a worker is free and still silent, so the pool is
    ///    stalled. Three of these in a row = hung.
    ///  - as many connections as workers or more: busy. Hung only if for a long time (10 min, or 1.2 x max_execution_time)
    ///    no request ended and no worker used CPU or disk. Requests can pile up on a hung pool, which is why this is needed.
    ///  - a worker connected to the Xdebug client: someone is debugging, a stopped worker is what that looks like.
    /// After a restart the next check waits longer each time; after three restarts in 30 minutes Portly stops trying
    /// and shows the error. The restart itself is OnExit's: the pool is ended and comes back like a crashed one.
    /// </summary>
    public partial class PhpService
    {
        // settings of the check; only the smoke test changes them
        public static int WatchTickMs = 1000;
        public static int WatchIntervalMs = 30000;
        public static int WatchRetryMs = 10000;
        public static int WatchProbeTimeoutMs = 5000;
        public static int WatchFailures = 3;
        public static int WatchMaxRestarts = 3;
        public static int WatchBackoffMs = 60000;
        public static int WatchRestartWindowMs = 30 * 60 * 1000;
        public static int WatchBusyLimitMs = 10 * 60 * 1000;

        /// <summary>A pool hung or was given up: text for the status line / tray. Comes on a background thread.</summary>
        public event Action<string> Problem;

        class Watched
        {
            public Process Proc;
            public int Port, DebugPort;
            public long BusyLimitMs;             // 0 = requests may run forever
            public DateTime NextProbe, LastProbe;
            public int Fails;
            public readonly HashSet<int> Probes = new HashSet<int>();   // local ports of our own unanswered checks
            public DateTime? BusySince;
            public HashSet<int> Requests = new HashSet<int>();
            public long Activity;
        }

        readonly Dictionary<string, Watched> _watched = new Dictionary<string, Watched>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<DateTime>> _hangs = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<Process> _endedHung = new HashSet<Process>();
        CancellationTokenSource _watchStop;

        void StartWatch()
        {
            lock (Sync)
            {
                if (_watchStop != null) return;
                var cts = new CancellationTokenSource();
                _watchStop = cts;
                Task.Run(() => WatchLoop(cts.Token));
            }
        }

        void StopWatch()
        {
            lock (Sync)
            {
                if (_watchStop != null) _watchStop.Cancel();
                _watchStop = null;
                _watched.Clear();
            }
        }

        async Task WatchLoop(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(WatchTickMs, stop); }
                catch (OperationCanceledException) { return; }
                try { WatchOnce(stop); }
                catch (Exception ex) { Log.WriteFileOnly("PHP health check: " + ex); }
            }
        }

        void WatchOnce(CancellationToken stop)
        {
            List<KeyValuePair<string, Process>> pools;
            lock (Sync) pools = _procs.ToList();
            foreach (var pool in pools)
            {
                // a switch or stop in progress: the pools are about to change anyway
                if (stop.IsCancellationRequested || StopRequested || State == ServiceState.Starting || State == ServiceState.Stopping) return;
                Watched w;
                lock (Sync)
                {
                    if (!_watched.TryGetValue(pool.Key, out w) || w.Proc != pool.Value)
                        _watched[pool.Key] = w = NewWatch(pool.Key, pool.Value);
                }
                if (DateTime.Now >= w.NextProbe && !pool.Value.HasExited) Check(pool.Key, w, stop);
            }
        }

        /// <summary>Called under the lock. A new pool gets time to start; one that hung before gets more of it each time.</summary>
        Watched NewWatch(string folder, Process p)
        {
            var dir = Path.Combine(Paths.BinOf("php"), folder);
            var w = new Watched { Proc = p, Port = PortFor(folder), LastProbe = DateTime.Now };
            long wait = WatchIntervalMs;
            List<DateTime> hangs;
            if (_hangs.TryGetValue(folder, out hangs) && hangs.Count > 0)
                wait = Math.Max(wait, (long)WatchBackoffMs << Math.Min(hangs.Count - 1, 8));
            w.NextProbe = DateTime.Now.AddMilliseconds(wait);
            try
            {
                int port;
                if (Tools.Xdebug.IsEnabled(dir)) w.DebugPort = int.TryParse(PhpConfig.Get(dir, "xdebug.client_port").Trim(' ', '"'), out port) ? port : 9003;
                int seconds;
                if (!int.TryParse(PhpConfig.Get(dir, "max_execution_time").Trim(' ', '"'), out seconds)) seconds = 300;
                w.BusyLimitMs = seconds <= 0 ? 0 : Math.Max(WatchBusyLimitMs, seconds * 1200L);
            }
            catch { w.BusyLimitMs = WatchBusyLimitMs; }
            return w;
        }

        void Check(string folder, Watched w, CancellationToken stop)
        {
            var now = DateTime.Now;
            // the computer slept in between: what was counted before says nothing any more
            if ((now - w.LastProbe).TotalMilliseconds > 3.0 * WatchIntervalMs + WatchProbeTimeoutMs) { w.Fails = 0; w.BusySince = null; }
            int localPort;
            var result = FastCgiProbe.Ask(w.Port, WatchProbeTimeoutMs, out localPort);
            w.LastProbe = DateTime.Now;
            if (result == FastCgiResult.Answered)
            {
                w.Fails = 0;
                w.BusySince = null;
                w.Probes.Clear();
                w.NextProbe = w.LastProbe.AddMilliseconds(WatchIntervalMs);
                return;
            }
            var label = PackageTypes.Label(folder);
            string reason;
            if (result == FastCgiResult.Refused) reason = I18n.T("php.hangRefused", label, w.Port);
            else
            {
                if (localPort > 0 && w.Probes.Count < 256) w.Probes.Add(localPort);
                var workers = PoolInfo.Workers(w.Proc.Id);
                var rows = PoolInfo.TcpRows();
                if (w.DebugPort > 0 && rows.Any(r => r.State == PoolInfo.Established && r.RemotePort == w.DebugPort && workers.Contains(r.Pid)))
                {
                    w.Fails = 0;
                    w.BusySince = null;
                    w.NextProbe = w.LastProbe.AddMilliseconds(WatchIntervalMs);
                    return;
                }
                var requests = new HashSet<int>(rows.Where(r => r.LocalPort == w.Port && (r.State == PoolInfo.Established || r.State == PoolInfo.CloseWait) && !w.Probes.Contains(r.RemotePort)).Select(r => r.RemotePort));
                if (workers.Count == 0) reason = I18n.T("php.hangNoWorkers", label, w.Port);
                else if (requests.Count < workers.Count) reason = I18n.T("php.hangSilent", label, w.Port, requests.Count, workers.Count);
                else
                {
                    w.Fails = 0;
                    var activity = PoolInfo.Activity(workers);
                    if (w.BusySince == null || activity != w.Activity || !w.Requests.IsSubsetOf(requests)) w.BusySince = w.LastProbe;
                    w.Requests = requests;
                    w.Activity = activity;
                    w.NextProbe = w.LastProbe.AddMilliseconds(WatchIntervalMs);
                    var busyMs = (w.LastProbe - w.BusySince.Value).TotalMilliseconds;
                    if (w.BusyLimitMs > 0 && busyMs >= w.BusyLimitMs)
                        Hung(folder, w, I18n.T("php.hangStuck", label, workers.Count, Math.Max(1, (int)Math.Round(busyMs / 60000))), stop);
                    return;
                }
                w.BusySince = null;
            }
            w.NextProbe = w.LastProbe.AddMilliseconds(WatchRetryMs);
            if (++w.Fails >= WatchFailures) Hung(folder, w, reason, stop);
        }

        void Hung(string folder, Watched w, string reason, CancellationToken stop)
        {
            bool giveUp;
            int restarts;
            lock (Sync)
            {
                Process cur;
                if (stop.IsCancellationRequested || StopRequested || !_procs.TryGetValue(folder, out cur) || cur != w.Proc) return;
                List<DateTime> times;
                if (!_hangs.TryGetValue(folder, out times)) _hangs[folder] = times = new List<DateTime>();
                times.RemoveAll(t => t < DateTime.Now.AddMilliseconds(-WatchRestartWindowMs));
                restarts = times.Count;
                giveUp = restarts >= WatchMaxRestarts;
                if (giveUp) _procs.Remove(folder); // OnExit leaves it down, and a new start does not take it for a running pool
                else { times.Add(DateTime.Now); _endedHung.Add(w.Proc); }
            }
            var h = Problem;
            if (giveUp)
            {
                var text = reason + " - " + I18n.T("php.hangGaveUp", restarts);
                ProcessUtil.KillTree(w.Proc.Id);
                SetState(ServiceState.Error, text);
                if (h != null) h(text);
                return;
            }
            Log.Warn(reason + " - " + I18n.T("log.restarting"));
            if (h != null) h(reason);
            ProcessUtil.KillTree(w.Proc.Id); // OnExit starts it again
        }
    }

    public enum FastCgiResult { Answered, Silent, Refused }

    /// <summary>FCGI_GET_VALUES: a management record any FastCGI worker answers by itself, no script runs.</summary>
    public static class FastCgiProbe
    {
        static readonly byte[] Question = BuildQuestion();

        static byte[] BuildQuestion()
        {
            var name = Encoding.ASCII.GetBytes("FCGI_MAX_CONNS");
            var body = new byte[2 + name.Length];
            body[0] = (byte)name.Length; // name length, value length 0
            name.CopyTo(body, 2);
            var head = new byte[] { 1 /* version */, 9 /* FCGI_GET_VALUES */, 0, 0 /* request id 0 */, 0, (byte)body.Length, 0, 0 };
            return head.Concat(body).ToArray();
        }

        /// <param name="localPort">Our end of the connection, 0 when there was none.</param>
        public static FastCgiResult Ask(int port, int timeoutMs, out int localPort)
        {
            localPort = 0;
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                try
                {
                    s.NoDelay = true;
                    var connect = s.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(timeoutMs)) return FastCgiResult.Silent;
                    s.EndConnect(connect);
                }
                catch (SocketException) { return FastCgiResult.Refused; }
                try
                {
                    localPort = ((IPEndPoint)s.LocalEndPoint).Port;
                    s.SendTimeout = timeoutMs;
                    s.ReceiveTimeout = timeoutMs;
                    s.Send(Question);
                    var reply = new byte[8];
                    return s.Receive(reply) > 0 ? FastCgiResult.Answered : FastCgiResult.Silent;
                }
                catch (SocketException) { return FastCgiResult.Silent; }
            }
        }
    }

    /// <summary>What Windows knows about a pool: its worker processes, the connections on its port, and whether they do anything.</summary>
    public static class PoolInfo
    {
        public const int Established = 5, CloseWait = 8;

        public class TcpRow { public int State, LocalPort, RemotePort, Pid; }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

        /// <summary>IPv4 connections with their owner: PHP binds 127.0.0.1 and Xdebug is set to it.</summary>
        public static List<TcpRow> TcpRows()
        {
            const int AfInet = 2, OwnerPidAll = 5, Insufficient = 122, Fields = 6;
            var rows = new List<TcpRow>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, OwnerPidAll, 0);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var mem = Marshal.AllocHGlobal(size);
                try
                {
                    var r = GetExtendedTcpTable(mem, ref size, false, AfInet, OwnerPidAll, 0);
                    if (r == Insufficient) continue;
                    if (r != 0) return rows;
                    int count = Marshal.ReadInt32(mem);
                    for (int i = 0; i < count; i++)
                    {
                        Func<int, int> field = n => Marshal.ReadInt32(mem, 4 + (i * Fields + n) * 4);
                        rows.Add(new TcpRow { State = field(0), LocalPort = NetPort(field(2)), RemotePort = NetPort(field(4)), Pid = field(5) });
                    }
                    return rows;
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
            return rows;
        }

        static int NetPort(int v) { return ((v & 0xFF) << 8) | ((v >> 8) & 0xFF); }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint Size, Usage, Pid;
            public IntPtr Heap;
            public uint Module, Threads, ParentPid;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] static extern bool First(IntPtr snapshot, ref ProcessEntry e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] static extern bool Next(IntPtr snapshot, ref ProcessEntry e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>The php-cgi.exe children of the pool's parent process. The parent itself only keeps them running.</summary>
        public static List<int> Workers(int parent)
        {
            var list = new List<int>();
            var snap = CreateToolhelp32Snapshot(2 /* processes */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                var e = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                for (bool more = First(snap, ref e); more; more = Next(snap, ref e))
                    if (e.ParentPid == parent && string.Equals(e.Exe, "php-cgi.exe", StringComparison.OrdinalIgnoreCase)) list.Add((int)e.Pid);
            }
            finally { CloseHandle(snap); }
            return list;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        /// <summary>A number that stays the same only while none of these processes uses CPU or does any I/O.</summary>
        public static long Activity(IEnumerable<int> pids)
        {
            long sum = 0;
            foreach (var pid in pids)
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        sum += p.TotalProcessorTime.Ticks;
                        IoCounters io;
                        if (GetProcessIoCounters(p.Handle, out io)) sum += (long)(io.ReadBytes + io.WriteBytes + io.OtherBytes);
                    }
                }
                catch { } // ended in between
            }
            return sum;
        }
    }
}
