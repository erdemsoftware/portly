using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.CoreTests
{
    /// <summary>
    /// PORTLY_E2E=1 (and not PORTLY_E2E_EXTRA=0): start Redis, MariaDB, PostgreSQL and MongoDB, whichever Suite_E2EInstall found in
    /// the catalog for this system, on test ports. Each one must answer a real request, stop on request and leave no process.
    /// Runs with the "Services" filter of the CI step (the runner matches the start of a suite name).
    /// </summary>
    static partial class Program
    {
        static void Suite_ServicesExtra()
        {
            if (!ExtraServicesWanted) { Console.WriteLine("  (set PORTLY_E2E=1 to start Redis, MariaDB, PostgreSQL and MongoDB)"); return; }
            Paths.EnsureAll();
            ProvideTemplates();
            var s = Settings.Current;
            int oldRedis = s.RedisPort, oldMysql = s.MysqlPort, oldPg = s.PostgresPort, oldMongo = s.MongoPort;
            var oldEngine = s.DbEngine;
            var before = LeftoverProcesses();
            try
            {
                s.RedisPort = 16379; s.MysqlPort = 13307; s.PostgresPort = 15432; s.MongoPort = 17017;
                Run("extra: redis", ExtraRedis);
                Run("extra: mariadb", ExtraMariaDb);
                Run("extra: postgresql", ExtraPostgres);
                Run("extra: mongodb", ExtraMongo);
            }
            finally
            {
                try { ServiceManager.StopAllAsync().GetAwaiter().GetResult(); } catch { }
                s.RedisPort = oldRedis; s.MysqlPort = oldMysql; s.PostgresPort = oldPg; s.MongoPort = oldMongo; s.DbEngine = oldEngine;
            }
            List<string> left = null;
            for (int i = 0; i < 40; i++)
            {
                left = LeftoverProcesses().Where(x => !before.Contains(x)).ToList();
                if (left.Count == 0) break;
                Thread.Sleep(250);
            }
            Check("extra: no child process left", left.Count == 0, string.Join(", ", left));
            Check("extra: test ports free again", !new[] { 16379, 13307, 15432, 17017 }.Any(ProcessUtil.IsPortInUse));
        }

        /// <summary>True when the service is installed; otherwise says so (a type the catalog does not offer here is no failure).</summary>
        static bool ExtraInstalled(Service svc, string what)
        {
            if (svc.IsInstalled) return true;
            Console.WriteLine("  SKIP " + what + ": not installed on " + OS.Name + " " + OS.Arch + " (the catalog offers none)");
            return false;
        }

        static void ExtraStop(Service svc, string name, int port)
        {
            svc.StopAsync().GetAwaiter().GetResult();
            Check("extra: " + name + " stopped", svc.State == ServiceState.Stopped, svc.LastError);
            var until = DateTime.Now.AddSeconds(10);
            while (ProcessUtil.IsPortInUse(port) && DateTime.Now < until) Thread.Sleep(200);
            Check("extra: " + name + " port free", !ProcessUtil.IsPortInUse(port));
        }

        static void ExtraRedis()
        {
            var svc = ServiceManager.Redis;
            if (!ExtraInstalled(svc, "redis")) return;
            svc.StartAsync().GetAwaiter().GetResult();
            Check("extra: Redis running", svc.State == ServiceState.Running, svc.LastError);
            if (svc.State == ServiceState.Running)
            {
                var cli = PackageTypes.ActiveExe("redis", "redis-cli");
                var r = ProcessUtil.Run(cli, "-h 127.0.0.1 -p 16379 ping", null, null, 30000);
                Check("extra: redis-cli PING answers PONG", r.Output.Trim() == "PONG", r.Output);
                r = ProcessUtil.Run(cli, "-h 127.0.0.1 -p 16379 set portly-e2e ok", null, null, 30000);
                var g = ProcessUtil.Run(cli, "-h 127.0.0.1 -p 16379 get portly-e2e", null, null, 30000);
                Check("extra: redis stores and returns a value", r.Output.Trim() == "OK" && g.Output.Trim() == "ok", r.Output.Trim() + " / " + g.Output.Trim());
            }
            ExtraStop(svc, "Redis", 16379);
        }

        static void ExtraMariaDb()
        {
            var s = Settings.Current;
            s.DbEngine = "mariadb";
            var svc = ServiceManager.MySql;
            if (!ExtraInstalled(svc, "mariadb")) return;
            svc.StartAsync().GetAwaiter().GetResult();
            Check("extra: MariaDB running", svc.State == ServiceState.Running, svc.LastError);
            if (svc.State == ServiceState.Running)
            {
                var bin = Path.Combine(PackageTypes.ActiveDir("mariadb"), "bin");
                var client = File.Exists(Path.Combine(bin, "mariadb")) ? "mariadb" : "mysql";
                var r = ProcessUtil.Run(Path.Combine(bin, client), "--protocol=TCP -h127.0.0.1 -P13307 -uroot --skip-ssl -N -B -e \"select 1\"", bin, null, 60000);
                Check("extra: mariadb client runs select 1", r.Ok && r.Output.Split('\n').Any(l => l.Trim() == "1"), r.Output);
                r = ProcessUtil.Run(Path.Combine(bin, client), "--protocol=TCP -h127.0.0.1 -P13307 -uroot --skip-ssl -N -B -e \"select version()\"", bin, null, 60000);
                Check("extra: the server is MariaDB", r.Ok && r.Output.Contains("MariaDB"), r.Output.Trim());
            }
            ExtraStop(svc, "MariaDB", 13307);
            s.DbEngine = "mysql";
        }

        static void ExtraPostgres()
        {
            var svc = ServiceManager.Postgres;
            if (!ExtraInstalled(svc, "postgresql")) return;
            svc.StartAsync().GetAwaiter().GetResult();
            Check("extra: PostgreSQL running", svc.State == ServiceState.Running, svc.LastError);
            if (svc.State == ServiceState.Running)
            {
                var answer = PgSelectOne(15432);
                Check("extra: PostgreSQL answers select 1 (wire protocol)", answer == "1", answer);
                var psql = Path.Combine(PackageTypes.ActiveDir("postgresql"), "bin", OS.Exe("psql"));
                if (File.Exists(psql))
                {
                    var r = ProcessUtil.Run(psql, "-h 127.0.0.1 -p 15432 -U postgres -At -c \"select 1\"", null, null, 60000);
                    Check("extra: psql runs select 1", r.Ok && r.Output.Trim() == "1", r.Output);
                }
            }
            ExtraStop(svc, "PostgreSQL", 15432);
        }

        static void ExtraMongo()
        {
            var svc = ServiceManager.Mongo;
            if (!ExtraInstalled(svc, "mongodb")) return;
            svc.StartAsync().GetAwaiter().GetResult();
            Check("extra: MongoDB running", svc.State == ServiceState.Running, svc.LastError);
            if (svc.State == ServiceState.Running)
            {
                var answer = MongoHello(17017);
                Check("extra: MongoDB answers hello (OP_MSG)", answer.Contains("isWritablePrimary") && answer.Contains("maxWireVersion"), answer);
                var log = Path.Combine(Paths.Logs, "mongodb.log");
                var text = File.Exists(log) ? File.ReadAllText(log) : "";
                Check("extra: mongod log says it waits for connections", text.Contains("Waiting for connections"), text.Length > 300 ? text.Substring(text.Length - 300) : text);
                var shell = Path.Combine(PackageTypes.ActiveDir("mongodb"), "bin", OS.Exe("mongosh"));
                if (File.Exists(shell))
                {
                    var r = ProcessUtil.Run(shell, "--quiet --port 17017 --eval \"db.runCommand({ping:1}).ok\"", null, null, 60000);
                    Check("extra: mongosh ping", r.Ok && r.Output.Trim() == "1", r.Output);
                }
            }
            ExtraStop(svc, "MongoDB", 17017);
        }

        // ------------------------------------------------------------------ wire protocols (no client program needed)

        static byte[] ReadExactly(NetworkStream ns, int count)
        {
            var buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = ns.Read(buf, got, count - got);
                if (n <= 0) throw new IOException("connection closed after " + got + " of " + count + " bytes");
                got += n;
            }
            return buf;
        }

        static int BigEndian(byte[] b, int at) { return (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]; }

        static void PutBigEndian(List<byte> list, int v) { list.Add((byte)(v >> 24)); list.Add((byte)(v >> 16)); list.Add((byte)(v >> 8)); list.Add((byte)v); }

        /// <summary>PostgreSQL protocol 3.0 over TCP with trust authentication: startup as "postgres", then a simple query returning one value.</summary>
        static string PgSelectOne(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    c.ReceiveTimeout = c.SendTimeout = 20000;
                    c.Connect("127.0.0.1", port);
                    var ns = c.GetStream();
                    var body = new List<byte>();
                    PutBigEndian(body, 196608);
                    body.AddRange(Encoding.ASCII.GetBytes("user\0postgres\0database\0postgres\0\0"));
                    var msg = new List<byte>();
                    PutBigEndian(msg, body.Count + 4);
                    msg.AddRange(body);
                    ns.Write(msg.ToArray(), 0, msg.Count);
                    if (!PgUntilReady(ns, null)) return "no ReadyForQuery after the startup message";
                    var sql = Encoding.UTF8.GetBytes("select 1\0");
                    var q = new List<byte> { (byte)'Q' };
                    PutBigEndian(q, sql.Length + 4);
                    q.AddRange(sql);
                    ns.Write(q.ToArray(), 0, q.Count);
                    string value = null;
                    PgUntilReady(ns, d =>
                    {
                        // DataRow: int16 columns, then int32 length + bytes per column
                        int len = BigEndian(d, 2);
                        if (len > 0) value = Encoding.UTF8.GetString(d, 6, len);
                    });
                    return value ?? "no data row";
                }
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        /// <summary>Read backend messages up to ReadyForQuery ('Z'); an ErrorResponse or a password request fails.</summary>
        static bool PgUntilReady(NetworkStream ns, Action<byte[]> onDataRow)
        {
            for (int i = 0; i < 200; i++)
            {
                var head = ReadExactly(ns, 5);
                int len = BigEndian(head, 1) - 4;
                var payload = len > 0 ? ReadExactly(ns, len) : new byte[0];
                char type = (char)head[0];
                if (type == 'E') throw new Exception("PostgreSQL error: " + Encoding.UTF8.GetString(payload).Replace('\0', ' '));
                if (type == 'R' && BigEndian(payload, 0) != 0) throw new Exception("PostgreSQL asks for authentication type " + BigEndian(payload, 0));
                if (type == 'D' && onDataRow != null) onDataRow(payload);
                if (type == 'Z') return true;
            }
            return false;
        }

        /// <summary>MongoDB OP_MSG with { hello: 1, $db: "admin" }; returns the reply text (field names are readable in it).</summary>
        static string MongoHello(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    c.ReceiveTimeout = c.SendTimeout = 20000;
                    c.Connect("127.0.0.1", port);
                    var ns = c.GetStream();
                    var doc = new List<byte>();
                    doc.AddRange(new byte[] { 0, 0, 0, 0 });                       // size, filled in below
                    doc.Add(0x10); doc.AddRange(Encoding.ASCII.GetBytes("hello\0")); doc.AddRange(new byte[] { 1, 0, 0, 0 });
                    doc.Add(0x02); doc.AddRange(Encoding.ASCII.GetBytes("$db\0")); doc.AddRange(new byte[] { 6, 0, 0, 0 }); doc.AddRange(Encoding.ASCII.GetBytes("admin\0"));
                    doc.Add(0);
                    var size = BitConverter.GetBytes(doc.Count);
                    for (int i = 0; i < 4; i++) doc[i] = size[i];
                    var msg = new List<byte>();
                    msg.AddRange(BitConverter.GetBytes(16 + 4 + 1 + doc.Count));    // messageLength (little endian)
                    msg.AddRange(BitConverter.GetBytes(1));                         // requestID
                    msg.AddRange(BitConverter.GetBytes(0));                         // responseTo
                    msg.AddRange(BitConverter.GetBytes(2013));                      // OP_MSG
                    msg.AddRange(BitConverter.GetBytes(0));                         // flagBits
                    msg.Add(0);                                                     // section kind 0: one document
                    msg.AddRange(doc);
                    ns.Write(msg.ToArray(), 0, msg.Count);
                    var head = ReadExactly(ns, 16);
                    int total = BitConverter.ToInt32(head, 0);
                    var rest = ReadExactly(ns, total - 16);
                    return Encoding.UTF8.GetString(rest).Replace('\0', ' ');
                }
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }
    }
}
