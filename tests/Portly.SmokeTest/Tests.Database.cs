using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>Smoke tests of the "Database" features. Runs while Apache, PHP and MySQL are up (see Run()).</summary>
        static async Task Tests_Database(Settings s, string www)
        {
            // ---------- version change decision (pure logic) ----------
            Func<string, string, string, string, DbMove> d = (fe, fv, te, tv) => DbVersionChange.Decide(fe, Version.Parse(fv), te, Version.Parse(tv));
            Check("db version: same line = nothing", d("mysql", "8.4.3", "mysql", "8.4.11") == DbMove.None);
            Check("db version: MySQL 8.0 -> 8.4 copies", d("mysql", "8.0.40", "mysql", "8.4.11") == DbMove.Copy);
            Check("db version: MySQL 5.7 -> 8.0 copies, 5.7 -> 8.4 dumps", d("mysql", "5.7.44", "mysql", "8.0.40") == DbMove.Copy && d("mysql", "5.7.44", "mysql", "8.4.11") == DbMove.Dump);
            Check("db version: MySQL 8.4 -> 9.x copies", d("mysql", "8.4.11", "mysql", "9.5.0") == DbMove.Copy);
            Check("db version: MariaDB 10.6 -> 11.8 copies", d("mariadb", "10.6.20", "mariadb", "11.8.3") == DbMove.Copy);
            Check("db version: downgrade dumps", d("mysql", "9.5.0", "mysql", "8.4.11") == DbMove.Dump && d("mariadb", "11.8.3", "mariadb", "10.11.9") == DbMove.Dump);
            Check("db version: engine change dumps", d("mysql", "8.4.11", "mariadb", "11.8.3") == DbMove.Dump && d("mariadb", "11.8.3", "mysql", "8.4.11") == DbMove.Dump);

            // ---------- dump filter ----------
            var toMysql = new DumpFilter(false, new Version(8, 4));
            var toMaria = new DumpFilter(true, new Version(11, 8));
            Check("dump filter: MariaDB header dropped for MySQL", toMysql.Apply("/*M!999999\\- enable the sandbox mode */") == "");
            Check("dump filter: uca1400 -> unicode_ci for MySQL", toMysql.Apply("COLLATE=utf8mb4_uca1400_ai_ci;").Contains("utf8mb4_unicode_ci"));
            Check("dump filter: MySQL 8 comment and 0900 collation fixed for MariaDB",
                toMaria.Apply("CREATE DATABASE `x` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci */ /*!80016 DEFAULT ENCRYPTION='N' */;") == "CREATE DATABASE `x` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci */ ;");
            Check("dump filter: multi-line GTID_PURGED skipped", toMaria.Apply("SET @@GLOBAL.GTID_PURGED=/*!80000 '+'*/ 'a:1-5,") == "" && toMaria.Apply("b:1-3';") == "" && toMaria.Apply("SELECT 1;") == "SELECT 1;");

            // ---------- collations offered ----------
            Check("collations: MySQL 8 offers utf8mb4_tr_0900_ai_ci, MariaDB 11 uca1400 turkish",
                DbOptions.Collations("mysql", new Version(8, 4)).Contains("utf8mb4_tr_0900_ai_ci") && !DbOptions.Collations("mysql", new Version(5, 7)).Contains("utf8mb4_0900_ai_ci")
                && DbOptions.Collations("mariadb", new Version(11, 8)).Contains("utf8mb4_uca1400_turkish_ai_ci") && !DbOptions.Collations("mariadb", new Version(10, 6)).Contains("utf8mb4_uca1400_ai_ci"));

            // ---------- new database with Turkish collation + own user ----------
            Check("create database with utf8mb4_turkish_ci", Database.CreateDatabase("t_turkish", "utf8mb4_turkish_ci") == null && Database.CollationOf("t_turkish") == "utf8mb4_turkish_ci", Database.CollationOf("t_turkish") ?? "");
            var userErr = Database.CreateUser("t_turkish", "t_turkish_u", "Pw-Smoke-123");
            var bin = MySqlService.BinDir;
            var cnf = Path.Combine(Paths.Tmp, "smoke-user.cnf");
            File.WriteAllText(cnf, "[client]\r\nuser=t_turkish_u\r\npassword=\"Pw-Smoke-123\"\r\nhost=127.0.0.1\r\nport=" + s.MysqlPort + "\r\n");
            var login = ProcessUtil.Run(Path.Combine(bin, "mysql.exe"), "--defaults-extra-file=\"" + cnf + "\" --batch --skip-column-names t_turkish", bin, null, 20000, "SELECT DATABASE();");
            var other = ProcessUtil.Run(Path.Combine(bin, "mysql.exe"), "--defaults-extra-file=\"" + cnf + "\" --batch --skip-column-names", bin, null, 20000, "USE mysql;");
            try { File.Delete(cnf); } catch { }
            Check("new database user can log in (only to its database)", userErr == null && login.Ok && login.Output.Contains("t_turkish") && !other.Ok, userErr ?? login.Output);

            // ---------- check and repair ----------
            Database.Sql("CREATE TABLE IF NOT EXISTS t_turkish.myi (id INT PRIMARY KEY, ad VARCHAR(20)) ENGINE=MyISAM; INSERT IGNORE INTO t_turkish.myi VALUES (1,'a'),(2,'b');");
            var rep = DbRepair.CheckAndRepair(_ => { });
            Check("check and repair: tables OK", rep.Checked > 5 && rep.Failed.Count == 0 && rep.NeedRecovery.Count == 0, rep.Summary);
            var parsed = DbRepair.Parse("a.t1   OK\nb.t2\nwarning  : Table is marked as crashed\nerror    : Corrupt\nc.t3   note     : The storage engine for the table doesn't support check\n");
            Check("check and repair: output parsed", parsed["a.t1"] == "" && parsed["b.t2"].Contains("Corrupt") && parsed["c.t3"] == "");

            // ---------- large SQL import (.sql.gz) with progress ----------
            const int rows = 60000;
            var gz = Path.Combine(Paths.Tmp, "smoke-import.sql.gz");
            using (var fs = File.Create(gz))
            using (var z = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new StreamWriter(z, new UTF8Encoding(false)))
            {
                w.Write("DROP TABLE IF EXISTS big;\nCREATE TABLE big (id INT PRIMARY KEY, txt VARCHAR(200)) COLLATE utf8mb4_uca1400_ai_ci;\n");
                for (int i = 0; i < rows; i += 500)
                {
                    w.Write("INSERT INTO big VALUES ");
                    for (int j = i; j < i + 500; j++) w.Write((j > i ? "," : "") + "(" + j + ",'" + Guid.NewGuid().ToString("N") + " satır " + j + " " + new string('x', 60) + "')");
                    w.Write(";\n");
                }
            }
            Database.CreateDatabase("t_import");
            int calls = 0;
            var imp = SqlImport.Run(gz, "t_import", false, (a, b) => calls++, CancellationToken.None);
            var count = Database.Sql("SELECT COUNT(*) FROM t_import.big;");
            Check("import .sql.gz with progress", imp.Ok && calls > 1 && count.Output.Trim().EndsWith(rows.ToString()),
                (imp.Message ?? "") + " calls=" + calls + " size=" + new FileInfo(gz).Length + " count=" + count.Output.Trim());
            var bad = Path.Combine(Paths.Tmp, "smoke-bad.sql");
            File.WriteAllText(bad, "SELECT 1;\nSELECT 2;\nTHIS IS NOT SQL;\nSELECT 3;\n");
            var badRes = SqlImport.Run(bad, "t_import", false, null, CancellationToken.None);
            Check("import reports the failing line", !badRes.Ok && badRes.Line == 3 && badRes.Statement.Contains("NOT SQL"), badRes.Message + " line " + badRes.Line);
            try { File.Delete(gz); File.Delete(bad); } catch { }

            // ---------- version marker / block ----------
            var dataDir = MySqlService.DataDirFor(PackageTypes.ActiveFolder("mysql"));
            Check("data folder remembers its server version", File.Exists(Path.Combine(dataDir, DbVersionChange.MarkerFile)));
            Check("older server blocked on newer data", DbVersionChange.BlockOlder(dataDir, "mysql", "mysql-8.0.1-winx64") != null && DbVersionChange.BlockOlder(dataDir, "mysql", PackageTypes.ActiveFolder("mysql")) == null);

            // ---------- heavy: move databases MySQL -> MariaDB (dump and load) ----------
            if (Environment.GetEnvironmentVariable("PORTLY_SLOW_TESTS") == "1" && PackageTypes.Installed("mariadb").Any())
            {
                var mariaFolder = PackageTypes.ActiveFolder("mariadb");
                var mariaData = DbVersionChange.DataDir("mariadb", mariaFolder);
                var aside = mariaData + ".smoke-aside";
                try
                {
                    if (Directory.Exists(mariaData)) Directory.Move(mariaData, aside);
                    var plan = DbVersionChange.PlanFor("mariadb", mariaFolder);
                    Check("move plan MySQL -> MariaDB = dump", plan != null && plan.Move == DbMove.Dump);
                    if (plan != null)
                    {
                        var text = await DbVersionChange.MoveAsync(plan, m => Console.WriteLine("    . " + m), CancellationToken.None);
                        var c2 = Database.Sql("SELECT COUNT(*) FROM t_import.big;");
                        Check("databases moved to MariaDB", s.DbEngine == "mariadb" && c2.Output.Trim().EndsWith(rows.ToString()) && Database.CollationOf("t_turkish") == "utf8mb4_turkish_ci", text);
                    }
                }
                catch (Exception ex) { Check("databases moved to MariaDB", false, ex.Message); }
                finally
                {
                    await ServiceManager.MySql.StopAsync();
                    s.DbEngine = "mysql"; s.Save();
                    try
                    {
                        if (Directory.Exists(aside)) { if (Directory.Exists(mariaData)) Directory.Delete(mariaData, true); Directory.Move(aside, mariaData); }
                    }
                    catch (Exception ex) { Console.WriteLine("    restore MariaDB data: " + ex.Message); }
                    await ServiceManager.MySql.StartAsync();
                }
            }

            foreach (var db in new[] { "t_turkish", "t_import" }) Database.Sql("DROP DATABASE IF EXISTS `" + db + "`;");
            Database.Sql("DROP USER IF EXISTS 't_turkish_u'@'localhost', 't_turkish_u'@'127.0.0.1', 't_turkish_u'@'::1';");
        }
    }
}
