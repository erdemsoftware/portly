using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
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
    static partial class Program
    {
        const int GuardWeb = 18725, GuardSmtp = 18726, GuardPhpBase = 18777, GuardXdebug = 18903;
        const string GuardWsHead = "Connection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n";

        [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr process);

        /// <summary>--service-guards: PHP pool health check and the Host check of the mail catchers, on ports of their own.</summary>
        static int ServiceGuardsOnly()
        {
            Paths.EnsureAll();
            Settings.Load();
            var s = Settings.Current;
            s.MailpitWebPort = GuardWeb; s.MailpitSmtpPort = GuardSmtp; s.PhpBasePort = GuardPhpBase;
            s.PhpChildren = 2; s.PhpMaxRequests = 1000; s.MailTool = "mailpit";
            s.Save();
            I18n.Load("en");
            Log.Written += l => Console.WriteLine("    | " + l);
            try
            {
                PhpWatchChecks(s).GetAwaiter().GetResult();
                PhpSlowStartCheck().GetAwaiter().GetResult();
                MailpitOwnCheck().GetAwaiter().GetResult();
                MailpitForwarderChecks(s).GetAwaiter().GetResult();
                MailHogChecks(s).GetAwaiter().GetResult();
                Smtp4devChecks(s).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Check("service guards", false, ex.ToString()); }
            Console.WriteLine(_ok + " passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        // ---------------------------------------------- PHP ----------------------------------------------

        /// <summary>A php-cgi that never opens its port (issue #3): the start fails and the process does not stay behind.</summary>
        static async Task PhpSlowStartCheck()
        {
            var folder = "php-0.0.0-neverlistens";
            var dir = Path.Combine(Paths.BinOf("php"), folder);
            var exe = Path.Combine(dir, "php-cgi.exe");
            Directory.CreateDirectory(dir);
            using (var cs = new Microsoft.CSharp.CSharpCodeProvider())
            {
                var o = new System.CodeDom.Compiler.CompilerParameters { GenerateExecutable = true, OutputAssembly = exe };
                var r = cs.CompileAssemblyFromSource(o, "class P { static void Main() { System.Threading.Thread.Sleep(300000); } }");
                if (r.Errors.HasErrors) { Check("Guards: fake php-cgi compiled", false, r.Errors[0].ToString()); return; }
            }
            try
            {
                var php = new PhpService();
                var t = DateTime.Now;
                await php.StartAsync(new List<string> { folder });
                var left = Process.GetProcessesByName("php-cgi").Where(p =>
                {
                    try { return string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return false; }
                }).ToList();
                Check("Guards: PHP that never opens its port is reported, after the full wait", php.State == ServiceState.Error && (php.LastError ?? "").Contains(I18n.T("err.timeout")) && (DateTime.Now - t).TotalSeconds >= 29, php.State + " " + php.LastError);
                Check("Guards: that php-cgi is closed, not left running (issue #3)", left.Count == 0, left.Count + " left");
                foreach (var p in left) try { p.Kill(); } catch { }
            }
            finally
            {
                for (int i = 0; i < 10 && Directory.Exists(dir); i++) { try { Directory.Delete(dir, true); } catch { Thread.Sleep(300); } }
            }
        }

        static int ListenerPid(int port)
        {
            var row = PoolInfo.TcpRows().FirstOrDefault(r => r.State == 2 && r.LocalPort == port);
            return row == null ? 0 : row.Pid;
        }

        static void SuspendPool(int parent)
        {
            foreach (var pid in PoolInfo.Workers(parent).Concat(new[] { parent }))
                using (var p = Process.GetProcessById(pid)) NtSuspendProcess(p.Handle);
        }

        static async Task<bool> WaitFor(Func<bool> done, int ms)
        {
            var until = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < until) { if (done()) return true; await Task.Delay(100); }
            return done();
        }

        static bool Answers(int port) { int local; return FastCgiProbe.Ask(port, 1000, out local) == FastCgiResult.Answered; }

        static byte[] Pair(string name, string value)
        {
            var n = Encoding.ASCII.GetBytes(name);
            var v = Encoding.ASCII.GetBytes(value);
            var m = new MemoryStream();
            foreach (var part in new[] { n, v })
            {
                if (part.Length < 128) m.WriteByte((byte)part.Length);
                else m.Write(new[] { (byte)(0x80 | part.Length >> 24), (byte)(part.Length >> 16), (byte)(part.Length >> 8), (byte)part.Length }, 0, 4);
            }
            m.Write(n, 0, n.Length);
            m.Write(v, 0, v.Length);
            return m.ToArray();
        }

        /// <summary>A FastCGI request as a web server sends it; the connection stays open for the answer.</summary>
        static TcpClient RunScript(int port, string file, string query = "")
        {
            var c = new TcpClient("127.0.0.1", port) { ReceiveTimeout = 30000 };
            var p = new MemoryStream();
            foreach (var kv in new Dictionary<string, string> { { "SCRIPT_FILENAME", file }, { "QUERY_STRING", query }, { "REQUEST_METHOD", "GET" }, { "SERVER_PROTOCOL", "HTTP/1.1" }, { "GATEWAY_INTERFACE", "CGI/1.1" }, { "REDIRECT_STATUS", "200" } })
            {
                var b = Pair(kv.Key, kv.Value);
                p.Write(b, 0, b.Length);
            }
            var m = new MemoryStream();
            m.Write(new byte[] { 1, 1, 0, 1, 0, 8, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 16);
            m.Write(new byte[] { 1, 4, 0, 1, (byte)(p.Length >> 8), (byte)p.Length, 0, 0 }, 0, 8);
            p.WriteTo(m);
            m.Write(new byte[] { 1, 4, 0, 1, 0, 0, 0, 0, 1, 5, 0, 1, 0, 0, 0, 0 }, 0, 16);
            var all = m.ToArray();
            c.GetStream().Write(all, 0, all.Length);
            return c;
        }

        static string ReadAnswer(TcpClient c)
        {
            using (c)
            {
                var o = new MemoryStream();
                try { c.GetStream().CopyTo(o); } catch { }
                return Encoding.ASCII.GetString(o.ToArray());
            }
        }

        static void SetIni(string dir, string key, string value)
        {
            var path = PhpConfig.IniPath(dir);
            var text = File.ReadAllText(path);
            var line = key + " = " + value;
            var rx = new Regex(@"(?m)^\s*" + Regex.Escape(key) + @"\s*=.*$");
            File.WriteAllText(path, rx.IsMatch(text) ? rx.Replace(text, line) : text + "\r\n" + line + "\r\n");
        }

        static async Task RestartPool(PhpService php, string folder)
        {
            await php.StopAsync();
            await php.StartAsync(new List<string> { folder });
        }

        static async Task PhpWatchChecks(Settings s)
        {
            // 8.4: php-cgi of one version shares its OPcache memory with any other copy of it, and the computer may run 8.5
            var folder = PackageTypes.Installed("php").FirstOrDefault(f => f.StartsWith("php-8.4"));
            if (folder == null) { Check("PHP 8.4 is in bin\\php", false); return; }
            var dir = Path.Combine(Paths.BinOf("php"), folder);
            var www = Path.Combine(Paths.Tmp, "guards");
            Directory.CreateDirectory(www);
            var sleep = Path.Combine(www, "sleep.php");
            File.WriteAllText(sleep, "<?php set_time_limit(0); sleep((int)$_GET['s']); echo 'done';");

            PhpService.WatchTickMs = 100; PhpService.WatchIntervalMs = 800; PhpService.WatchRetryMs = 200;
            PhpService.WatchProbeTimeoutMs = 500; PhpService.WatchFailures = 3; PhpService.WatchMaxRestarts = 100;
            PhpService.WatchBackoffMs = 300; PhpService.WatchBusyLimitMs = 3000;

            var php = new PhpService();
            var problems = new List<string>();
            php.Problem += t => { lock (problems) problems.Add(t); };
            Func<int> problemCount = () => { lock (problems) return problems.Count; };
            Func<string> lastProblem = () => { lock (problems) return problems.LastOrDefault() ?? ""; };

            await php.StartAsync(new List<string> { folder });
            int port = php.PortFor(folder);
            Check("Guards: pool up with two workers", php.State == ServiceState.Running && await WaitFor(() => PoolInfo.Workers(ListenerPid(port)).Count == 2, 5000), php.LastError ?? "");

            // ---------- the check itself ----------
            Check("Guards: FCGI_GET_VALUES is answered by real php-cgi", Answers(port));
            int local;
            Check("Guards: a closed port counts as refused", FastCgiProbe.Ask(GuardPhpBase + 500, 4000, out local) == FastCgiResult.Refused);
            int pid = ListenerPid(port);
            await Task.Delay(3000);
            Check("Guards: a healthy pool is left alone", ListenerPid(port) == pid && problemCount() == 0);
            var before = RunScript(port, sleep, "s=0");
            Check("Guards: a check runs no script and the pool serves pages", ReadAnswer(before).Contains("done"));

            // ---------- long requests on all workers, more waiting behind them ----------
            var one = RunScript(port, sleep, "s=8");
            var two = RunScript(port, sleep, "s=8");
            await Task.Delay(500);
            var queued = RunScript(port, sleep, "s=0");
            await Task.Delay(1000);
            Check("Guards: a pool with all workers busy does not answer (what the check has to cope with)", !Answers(port));
            await Task.Delay(5000);
            Check("Guards: busy pool is not restarted", ListenerPid(port) == pid && problemCount() == 0, lastProblem());
            Check("Guards: the long requests finish", ReadAnswer(one).Contains("done") && ReadAnswer(two).Contains("done") && ReadAnswer(queued).Contains("done"));

            // ---------- long requests whose visitor left: the worker is still in the script ----------
            RunScript(port, sleep, "s=5").Close();
            RunScript(port, sleep, "s=5").Close();
            await Task.Delay(4000);
            Check("Guards: requests of a closed browser tab do not look like a stall", ListenerPid(port) == pid && problemCount() == 0, lastProblem());
            await WaitFor(() => Answers(port), 4000);

            // ---------- suspended: idle workers that cannot answer ----------
            SuspendPool(pid);
            var hung = Stopwatch.StartNew();
            Check("Guards: hung pool is detected and restarted", await WaitFor(() => ListenerPid(port) != 0 && ListenerPid(port) != pid, 10000), "after " + hung.ElapsedMilliseconds + " ms");
            Check("Guards: the log says why", Regex.IsMatch(lastProblem(), "hung|hangSilent"), lastProblem());
            Check("Guards: restarted pool works", await WaitFor(() => Answers(port), 5000) && php.State == ServiceState.Running && ReadAnswer(RunScript(port, sleep, "s=0")).Contains("done"));
            var mine = Process.GetCurrentProcess().Id;
            Check("Guards: checks leave no socket behind", !PoolInfo.TcpRows().Any(r => r.Pid == mine && r.RemotePort == port && r.State == PoolInfo.Established));

            // ---------- the worst case: hung, and requests pile up on it ----------
            SetIni(dir, "max_execution_time", "1");
            await RestartPool(php, folder);
            pid = ListenerPid(port);
            await WaitFor(() => PoolInfo.Workers(pid).Count == 2 && Answers(port), 5000);
            SuspendPool(pid);
            var pile = Enumerable.Range(0, 3).Select(i => new TcpClient("127.0.0.1", port)).ToList();
            var stuck = await WaitFor(() => ListenerPid(port) != 0 && ListenerPid(port) != pid, 12000);
            Check("Guards: hung pool with requests queued on it is restarted after the time limit", stuck && Regex.IsMatch(lastProblem(), "stuck|hangStuck"), lastProblem());
            pile.ForEach(c => c.Close());
            await WaitFor(() => Answers(port), 5000);

            // ---------- long past the limit, but working ----------
            pid = ListenerPid(port);
            await WaitFor(() => PoolInfo.Workers(pid).Count == 2 && Answers(port), 5000);
            var spin = Path.Combine(www, "spin.php");
            File.WriteAllText(spin, "<?php set_time_limit(0); $t = microtime(true); while (microtime(true) - $t < 7) { } echo 'done';");
            int seen = problemCount();
            var s1 = RunScript(port, spin);
            var s2 = RunScript(port, spin);
            await Task.Delay(6000);
            Check("Guards: requests that use CPU are left alone past the time limit", ListenerPid(port) == pid && problemCount() == seen, lastProblem());
            Check("Guards: and they finish", ReadAnswer(s1).Contains("done") && ReadAnswer(s2).Contains("done"));

            // ---------- paused in the debugger (real Xdebug, a client that never says anything) ----------
            if (File.Exists(Path.Combine(dir, "ext", "php_xdebug.dll")))
            {
                await Xdebug.EnableAsync(dir);
                SetIni(dir, "xdebug.client_port", GuardXdebug.ToString());
                await RestartPool(php, folder);
                pid = ListenerPid(port);
                await WaitFor(() => PoolInfo.Workers(pid).Count == 2 && Answers(port), 5000);
                var client = new TcpListener(IPAddress.Loopback, GuardXdebug);
                client.Start();
                var d1 = RunScript(port, sleep, "s=0&XDEBUG_TRIGGER=1");
                var d2 = RunScript(port, sleep, "s=0&XDEBUG_TRIGGER=1");
                await WaitFor(() => PoolInfo.TcpRows().Count(r => r.RemotePort == GuardXdebug && r.State == PoolInfo.Established) == 2, 5000);
                int count = problemCount();
                await Task.Delay(6000);
                Check("Guards: real Xdebug session is open on both workers", PoolInfo.TcpRows().Count(r => r.RemotePort == GuardXdebug && r.State == PoolInfo.Established) == 2);
                Check("Guards: workers stopped in the debugger are left alone, past the time limit", !Answers(port) && ListenerPid(port) == pid && problemCount() == count, lastProblem());
                client.Stop();
                d1.Close(); d2.Close();
                Xdebug.Disable(dir);
            }
            SetIni(dir, "max_execution_time", "300");

            // ---------- it keeps hanging: backoff, then Portly gives up and says so ----------
            PhpService.WatchMaxRestarts = 2;
            PhpService.WatchBackoffMs = 800;
            await RestartPool(php, folder);
            var gaps = new List<long>();
            for (int round = 0; round < 2; round++)
            {
                pid = ListenerPid(port);
                await WaitFor(() => PoolInfo.Workers(pid).Count == 2, 5000);
                SuspendPool(pid);
                await WaitFor(() => ListenerPid(port) != 0 && ListenerPid(port) != pid, 15000);
                var sw = Stopwatch.StartNew();
                await WaitFor(() => Answers(port), 5000);
                pid = ListenerPid(port);
                gaps.Add(sw.ElapsedMilliseconds);
            }
            var last = ListenerPid(port);
            await WaitFor(() => PoolInfo.Workers(last).Count == 2, 5000);
            SuspendPool(last);
            bool gaveUp = await WaitFor(() => php.State == ServiceState.Error, 20000);
            Check("Guards: gives up after N restarts with an error state", gaveUp && Regex.IsMatch(php.LastError ?? "", "restarts|hangGaveUp"), php.LastError ?? "");
            var count2 = problemCount();
            await Task.Delay(3000);
            Check("Guards: nothing is left running or retried after giving up", ListenerPid(port) == 0 && php.RunningFolders.Count == 0 && problemCount() == count2);
            await php.StartAsync(new List<string> { folder });
            Check("Guards: starting by hand works again", php.State == ServiceState.Running && await WaitFor(() => Answers(port), 5000));

            // ---------- a stop while it hangs: no restart comes after ----------
            PhpService.WatchMaxRestarts = 100;
            pid = ListenerPid(port);
            await WaitFor(() => PoolInfo.Workers(pid).Count == 2, 5000);
            SuspendPool(pid);
            await php.StopAsync();
            int count3 = problemCount();
            await Task.Delay(3000);
            Check("Guards: stopped means stopped, also from a hung state", php.State == ServiceState.Stopped && ListenerPid(port) == 0 && problemCount() == count3);
        }

        // ---------------------------------------------- mail ----------------------------------------------

        /// <summary>Sends a request as raw bytes; everything that comes back within the time limit or until the close.</summary>
        static string Http(int port, string request, int ms = 3000, Func<string, bool> enough = null)
        {
            using (var c = new TcpClient("127.0.0.1", port))
            {
                c.ReceiveTimeout = 500;
                var st = c.GetStream();
                var b = Encoding.ASCII.GetBytes(request);
                st.Write(b, 0, b.Length);
                var o = new StringBuilder();
                var buf = new byte[65536];
                var until = DateTime.Now.AddMilliseconds(ms);
                while (DateTime.Now < until)
                {
                    int n;
                    try { n = st.Read(buf, 0, buf.Length); }
                    catch (IOException) { if (enough != null && enough(o.ToString())) break; continue; }
                    if (n <= 0) break;
                    o.Append(Encoding.GetEncoding(28591).GetString(buf, 0, n));
                    if (enough != null && enough(o.ToString())) break;
                }
                return o.ToString();
            }
        }

        static string Status(string response) { return response.Split('\r')[0]; }
        static string Get(int port, string host, string path, string extra = "")
        {
            return Http(port, "GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\n" + extra + "Connection: close\r\n\r\n");
        }
        static string Ws(int port, string host, string path, string origin)
        {
            return Status(Http(port, "GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\n" + (origin != null ? "Origin: " + origin + "\r\n" : "") + GuardWsHead + "\r\n", 2000, r => r.Contains("\r\n\r\n")));
        }

        /// <summary>Opens the WebSocket, catches one mail through SMTP, and looks for its subject in what the socket pushes.</summary>
        static bool LivePush(int port, string host, string path, string subject)
        {
            using (var c = new TcpClient("127.0.0.1", port))
            {
                var st = c.GetStream();
                var b = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\nOrigin: http://" + host + "\r\n" + GuardWsHead + "\r\n");
                st.Write(b, 0, b.Length);
                c.ReceiveTimeout = 1000;
                var o = new StringBuilder();
                var buf = new byte[65536];
                var until = DateTime.Now.AddSeconds(8);
                bool sent = false;
                while (DateTime.Now < until)
                {
                    try
                    {
                        int n = st.Read(buf, 0, buf.Length);
                        if (n <= 0) return false;
                        o.Append(Encoding.GetEncoding(28591).GetString(buf, 0, n));
                    }
                    catch (IOException) { }
                    if (!sent && o.ToString().Contains(" 101 "))
                    {
                        sent = true;
                        using (var smtp = new System.Net.Mail.SmtpClient("127.0.0.1", GuardSmtp))
                            smtp.Send("a@portly.local", "b@portly.local", subject, "body");
                    }
                    if (o.ToString().Contains(subject)) return true;
                }
                return false;
            }
        }

        static int ToolPid(MailpitService svc)
        {
            var p = (Process)typeof(MailpitService).GetField("_p", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(svc);
            return p == null ? 0 : p.Id;
        }

        /// <summary>The private port the tool listens on behind the forwarder (0 = none).</summary>
        static int PrivateWebPort(MailpitService svc)
        {
            var pid = ToolPid(svc);
            var row = PoolInfo.TcpRows().FirstOrDefault(r => r.State == 2 && r.Pid == pid && r.LocalPort != GuardSmtp);
            return row == null ? 0 : row.LocalPort;
        }

        static async Task<MailpitService> StartMail(Settings s, string tool)
        {
            s.MailTool = tool;
            s.Save();
            var svc = new MailpitService();
            await svc.StartAsync();
            Check("Guards: " + tool + " starts", svc.State == ServiceState.Running, svc.LastError ?? "");
            return svc;
        }

        /// <summary>Mailpit 1.31.1+: its own --allowed-hosts, nothing of Portly between the browser and the tool.</summary>
        static async Task MailpitOwnCheck()
        {
            var svc = await StartMail(Settings.Current, "mailpit");
            int p = GuardWeb, pid = ToolPid(svc);
            Check("Guards: Mailpit itself listens on the web port", ListenerPid(p) == pid);
            Check("Guards: Mailpit serves localhost", Status(Get(p, "localhost:" + p, "/api/v1/info")).Contains("200"));
            Check("Guards: Mailpit serves 127.0.0.1 (what Portly's buttons open)", Status(Get(p, "127.0.0.1:" + p, "/api/v1/info")).Contains("200"));
            Check("Guards: Mailpit serves [::1]", Status(Get(p, "[::1]:" + p, "/api/v1/info")).Contains("200"));
            Check("Guards: Mailpit refuses a foreign Host", Status(Get(p, "evil.example:" + p, "/api/v1/messages")).Contains("403"));
            Check("Guards: Mailpit WebSocket from its own page", Ws(p, "127.0.0.1:" + p, "/api/events", "http://127.0.0.1:" + p).Contains("101"));
            Check("Guards: Mailpit WebSocket refuses another site", Ws(p, "127.0.0.1:" + p, "/api/events", "http://evil.example").Contains("403"));
            Check("Guards: Mailpit live view", LivePush(p, "localhost:" + p, "/api/events", "Portly own check"));
            await svc.StopAsync();
        }

        /// <summary>A Mailpit without --allowed-hosts (Portly thinks so): the forwarder has to do it, with the real tool behind it.</summary>
        static async Task MailpitForwarderChecks(Settings s)
        {
            MailpitService.IgnoreAllowedHosts = true;
            var svc = await StartMail(s, "mailpit");
            MailpitService.IgnoreAllowedHosts = false;
            int p = GuardWeb, inner = PrivateWebPort(svc);
            Check("Guards: forwarder has the web port, the tool a private one", ListenerPid(p) == Process.GetCurrentProcess().Id && inner != 0 && inner != p, "private " + inner);
            Check("Guards: forwarder listens on 127.0.0.1 only", PoolInfo.TcpRows().Count(r => r.State == 2 && r.LocalPort == p) == 1 && IPGlobalPropsLoopbackOnly(p));
            var lan = LanShare.LocalIp();
            if (lan != null)
            {
                bool reached = false;
                try { using (var c = new TcpClient()) { c.Connect(lan, p); reached = true; } } catch (SocketException) { }
                Check("Guards: forwarder cannot be reached from the network address " + lan, !reached);
            }

            var page = Get(p, "localhost:" + p, "/");
            Check("Guards: forwarder passes the real Mailpit page", Status(page).Contains("200") && page.IndexOf("mailpit", StringComparison.OrdinalIgnoreCase) >= 0, Status(page));
            Check("Guards: forwarder allows 127.0.0.1 and [::1]", Status(Get(p, "127.0.0.1:" + p, "/api/v1/info")).Contains("200") && Status(Get(p, "[::1]:" + p, "/api/v1/info")).Contains("200"));
            foreach (var host in new[] { "evil.example", "evil.example:" + p, "localhost.evil.example", "127.0.0.1.nip.io:" + p, "0.0.0.0:" + p })
                Check("Guards: forwarder refuses Host " + host, Status(Get(p, host, "/api/v1/messages")).Contains("403"));
            Check("Guards: forwarder refuses a request without Host", Status(Http(p, "GET /api/v1/messages HTTP/1.1\r\nConnection: close\r\n\r\n")).Contains("403"));
            Check("Guards: forwarder refuses two Host headers", Status(Http(p, "GET / HTTP/1.1\r\nHost: localhost\r\nHost: evil.example\r\n\r\n")).Contains("400"));
            Check("Guards: forwarder refuses an address in the request line", Status(Http(p, "GET http://evil.example/ HTTP/1.1\r\nHost: localhost\r\n\r\n")).Contains("400"));
            Check("Guards: forwarder refuses a call from another site", Status(Get(p, "127.0.0.1:" + p, "/api/v1/messages", "Origin: http://evil.example\r\n")).Contains("403"));
            Check("Guards: forwarder allows a call from a local page", Status(Get(p, "127.0.0.1:" + p, "/api/v1/messages", "Origin: http://127.0.0.1:" + p + "\r\n")).Contains("200"));

            var both = Http(p, "GET /api/v1/info HTTP/1.1\r\nHost: localhost\r\n\r\nGET /api/v1/messages HTTP/1.1\r\nHost: evil.example\r\n\r\n", 2000);
            Check("Guards: a second request on the connection never reaches the tool", Regex.Matches(both, "HTTP/1.1 ").Count == 1 && Status(both).Contains("200"), Status(both));
            var bodyText = "{\"IDs\":[]}";
            var post = Http(p, "DELETE /api/v1/messages HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: " + bodyText.Length + "\r\n\r\n" + bodyText);
            Check("Guards: a request with a body passes", Status(post).Contains("200"), Status(post));
            Check("Guards: forwarder refuses a chunked body", Status(Http(p, "POST /api/v1/send HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n")).Contains("411"));

            Check("Guards: forwarder WebSocket from the page", Ws(p, "127.0.0.1:" + p, "/api/events", "http://127.0.0.1:" + p).Contains("101"));
            Check("Guards: forwarder WebSocket refuses another site", Ws(p, "127.0.0.1:" + p, "/api/events", "http://evil.example").Contains("403"));
            Check("Guards: forwarder WebSocket refuses a foreign Host", Ws(p, "evil.example:" + p, "/api/events", "http://evil.example:" + p).Contains("403"));
            Check("Guards: live view works through the forwarder", LivePush(p, "localhost:" + p, "/api/events", "Portly forwarder check"));
            Check("Guards: without the forwarder the tool asks for a password", Status(Get(inner, "localhost", "/api/v1/messages")).Contains("401") && Ws(inner, "localhost", "/api/events", null).Contains("401"));
            Check("Guards: the password file is gone after the start", !Directory.GetFiles(Path.Combine(Paths.Data, "mailpit"), "portly-ui-auth").Any());

            // a WebSocket that is open while Portly stops
            using (var open = new TcpClient("127.0.0.1", p))
            {
                var b = Encoding.ASCII.GetBytes("GET /api/events HTTP/1.1\r\nHost: localhost\r\n" + GuardWsHead + "\r\n");
                var st = open.GetStream();
                st.Write(b, 0, b.Length);
                open.ReceiveTimeout = 3000;
                st.Read(new byte[512], 0, 512);
                await svc.StopAsync();
                bool closed;
                try { closed = st.Read(new byte[512], 0, 512) == 0; } catch (IOException ex) { closed = !(ex.InnerException is SocketException se) || se.SocketErrorCode != SocketError.TimedOut; }
                Check("Guards: stopping ends open connections", closed);
            }
            Check("Guards: stopping leaves nothing listening", ListenerPid(p) == 0 && ListenerPid(inner) == 0);

            // the tool dies: the forwarder must not stay on the port
            MailpitService.IgnoreAllowedHosts = true;
            svc = await StartMail(s, "mailpit");
            MailpitService.IgnoreAllowedHosts = false;
            using (var tool = Process.GetProcessById(ToolPid(svc))) { tool.Kill(); tool.WaitForExit(5000); }
            Check("Guards: a crash of the tool closes the forwarder", await WaitFor(() => ListenerPid(p) == 0 && svc.State == ServiceState.Error, 5000), svc.LastError ?? "");
            await svc.StopAsync();
            Check("Guards: the service starts again after that", await RestartsClean(s));
        }

        static async Task<bool> RestartsClean(Settings s)
        {
            MailpitService.IgnoreAllowedHosts = true;
            var svc = await StartMail(s, "mailpit");
            MailpitService.IgnoreAllowedHosts = false;
            var ok = Status(Get(GuardWeb, "localhost", "/api/v1/info")).Contains("200");
            await svc.StopAsync();
            return ok && ListenerPid(GuardWeb) == 0;
        }

        static bool IPGlobalPropsLoopbackOnly(int port)
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(e => e.Port == port).All(e => IPAddress.IsLoopback(e.Address));
        }

        /// <summary>MailHog has no Host check and its WebSocket takes every site: always behind the forwarder (the Legacy default).</summary>
        static async Task MailHogChecks(Settings s)
        {
            if (PackageTypes.ActiveDir("mailhog") == null) { Check("MailHog is in bin\\mailhog", false); return; }
            var svc = await StartMail(s, "mailhog");
            int p = GuardWeb, inner = PrivateWebPort(svc);
            var page = Get(p, "127.0.0.1:" + p, "/");
            Check("Guards: real MailHog page", Status(page).Contains("200") && page.Contains("MailHog"), Status(page));
            Check("Guards: MailHog API", Status(Get(p, "localhost:" + p, "/api/v2/messages")).Contains("200"));
            Check("Guards: MailHog refuses a foreign Host", Status(Get(p, "evil.example:" + p, "/api/v2/messages")).Contains("403"));
            Check("Guards: MailHog WebSocket refuses another site", Ws(p, "127.0.0.1:" + p, "/api/v2/websocket", "http://evil.example").Contains("403"));
            Check("Guards: MailHog live view through the forwarder", LivePush(p, "127.0.0.1:" + p, "/api/v2/websocket", "Portly MailHog check"));
            Check("Guards: MailHog event stream through the forwarder", Http(p, "GET /api/v1/events HTTP/1.1\r\nHost: localhost\r\n\r\n", 1500, r => r.Contains("\r\n\r\n")).Contains("text/event-stream"));
            Check("Guards: MailHog behind the forwarder needs the password", inner != 0 && Status(Get(inner, "127.0.0.1", "/api/v2/messages")).Contains("401") && Ws(inner, "127.0.0.1", "/api/v2/websocket", "http://evil.example").Contains("401"));
            await svc.StopAsync();
            Check("Guards: MailHog stop leaves nothing listening", ListenerPid(p) == 0 && ListenerPid(inner) == 0);
        }

        /// <summary>smtp4dev: ASP.NET's Host filter, switched on with one environment variable.</summary>
        static async Task Smtp4devChecks(Settings s)
        {
            if (PackageTypes.ActiveDir("smtp4dev") == null) { Check("smtp4dev is in bin\\smtp4dev", false); return; }
            var svc = await StartMail(s, "smtp4dev");
            int p = GuardWeb;
            await WaitFor(() => Status(Get(p, "127.0.0.1:" + p, "/")).Contains("200"), 15000);
            Check("Guards: smtp4dev serves 127.0.0.1 and localhost", Status(Get(p, "127.0.0.1:" + p, "/")).Contains("200") && Status(Get(p, "localhost:" + p, "/")).Contains("200"));
            Check("Guards: smtp4dev refuses a foreign Host", Status(Get(p, "evil.example:" + p, "/")).Contains("400"));
            await svc.StopAsync();
        }
    }
}
