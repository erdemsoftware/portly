using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.SmokeTest
{
    static partial class Program
    {
        /// <summary>Smoke tests of the "Tomcat" feature. Downloads (Java + Tomcat, ~60 MB) only with PORTLY_SLOW_TESTS=1.</summary>
        static async Task Tests_Tomcat(Settings s, string www)
        {
            // the catalog downloaded from GitHub has no Tomcat until this manifest is pushed: use the built-in one meanwhile
            bool swapped = false;
            if (!Catalog.ForType("tomcat").Any())
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                var json = (string)typeof(Catalog).GetMethod("Embedded", flags).Invoke(null, null);
                typeof(Catalog).GetMethod("Parse", flags).Invoke(null, new object[] { json });
                swapped = true;
            }
            try
            {
                TomcatFastChecks(s);
                if (Environment.GetEnvironmentVariable("PORTLY_SLOW_TESTS") == "1") await TomcatLiveChecks(s);
            }
            finally { if (swapped) Catalog.LoadLocal(); }
        }

        static void TomcatFastChecks(Settings s)
        {
            Check("tomcat: default port 8080", s.Raw.Get("services", "TomcatPort", "") != "" || s.TomcatPort == Settings.DefaultTomcatPort, s.TomcatPort.ToString());
            Check("tomcat: catalog has Tomcat 11 / 10.1 / 9 with SHA-256", new[] { "11.0", "10.1", "9.0" }.All(b => Catalog.ForType("tomcat").Any(e => e.Branch == b && e.Sha256.Length == 64)));
            Check("tomcat: catalog has Temurin 21 and 25 (JRE and JDK) with SHA-256",
                new[] { "21.", "25." }.All(v => Catalog.ForType("java").Count(e => e.Version.StartsWith(v) && e.Sha256.Length == 64) >= 2));
            Check("tomcat: minimum Java per line", TomcatService.MinJava("apache-tomcat-11.0.27") == 17 && TomcatService.MinJava("apache-tomcat-10.1.61") == 11 && TomcatService.MinJava("apache-tomcat-9.0.123") == 8);

            var tmp = Path.Combine(Paths.Tmp, "tomcat-test");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            var jdk = Path.Combine(tmp, "jdk");
            Directory.CreateDirectory(jdk);
            File.WriteAllText(Path.Combine(jdk, "release"), "IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"21.0.12.1\"\n");
            var jdk8 = Path.Combine(tmp, "jdk8");
            Directory.CreateDirectory(jdk8);
            File.WriteAllText(Path.Combine(jdk8, "release"), "JAVA_VERSION=\"1.8.0_462\"\n");
            Check("tomcat: Java version from the release file", TomcatService.JavaMajor(jdk) == 21 && TomcatService.JavaMajor(jdk8) == 8, TomcatService.JavaMajor(jdk) + " / " + TomcatService.JavaMajor(jdk8));

            var home = Path.Combine(tmp, "apache-tomcat-11.0.27");
            var cbase = Path.Combine(tmp, "base");
            var args = TomcatService.JavaArgs(home, cbase);
            Check("tomcat: java command line runs Bootstrap with base / home / tmpdir",
                args.EndsWith("org.apache.catalina.startup.Bootstrap start") && args.Contains("-Dcatalina.base=\"" + cbase + "\"") && args.Contains("-Dcatalina.home=\"" + home + "\"")
                && args.Contains("-Djava.io.tmpdir=\"" + Path.Combine(cbase, "temp") + "\"") && args.Contains("bootstrap.jar;") && args.Contains("logging.properties\""), args);
            var env = TomcatService.BuildEnvironment(jdk, home, cbase);
            Check("tomcat: environment JAVA_HOME / JRE_HOME / CATALINA_HOME / CATALINA_BASE",
                env["JAVA_HOME"] == jdk && env["JRE_HOME"] == jdk && env["CATALINA_HOME"] == home && env["CATALINA_BASE"] == cbase && env["PATH"].StartsWith(Path.Combine(jdk, "bin") + ";") && env["JDK_JAVA_OPTIONS"].Contains("--add-opens"));

            // a reduced copy of Tomcat's own server.xml: first start copies conf\ and the default apps, then patches it
            Directory.CreateDirectory(Path.Combine(home, "conf"));
            Directory.CreateDirectory(Path.Combine(home, "webapps", "ROOT"));
            Directory.CreateDirectory(Path.Combine(home, "webapps", "examples"));
            File.WriteAllText(Path.Combine(home, "webapps", "ROOT", "index.jsp"), "x");
            File.WriteAllText(Path.Combine(home, "conf", "server.xml"),
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- licence -->\n<Server port=\"8005\" shutdown=\"SHUTDOWN\">\n  <Service name=\"Catalina\">\n" +
                "    <Connector port=\"8080\" protocol=\"HTTP/1.1\" connectionTimeout=\"20000\" redirectPort=\"8443\" />\n" +
                "    <!-- <Connector port=\"8443\" protocol=\"org.apache.coyote.http11.Http11NioProtocol\" SSLEnabled=\"true\" /> -->\n" +
                "    <Connector protocol=\"AJP/1.3\" address=\"::1\" port=\"8009\" redirectPort=\"8443\" />\n" +
                "    <Engine name=\"Catalina\" defaultHost=\"localhost\"><Host name=\"localhost\" appBase=\"webapps\">\n" +
                "      <Valve className=\"org.apache.catalina.valves.AccessLogValve\" directory=\"logs\" prefix=\"localhost_access_log\" suffix=\".txt\" />\n" +
                "    </Host></Engine>\n  </Service>\n</Server>\n");
            File.WriteAllText(Path.Combine(home, "conf", "logging.properties"), "1catalina.org.apache.juli.AsyncFileHandler.directory = ${catalina.base}/logs\n");
            TomcatService.PrepareBase(home, cbase, 19080, "127.0.0.1", 19005, "PORTLY-TEST");
            var xml = File.ReadAllText(Path.Combine(cbase, "conf", "server.xml"));
            Check("tomcat: server.xml HTTP connector on the Portly port, local only", xml.Contains("<Connector port=\"19080\" protocol=\"HTTP/1.1\"") && xml.Contains("address=\"127.0.0.1\" />"), xml);
            Check("tomcat: AJP connector untouched", xml.Contains("<Connector protocol=\"AJP/1.3\" address=\"::1\" port=\"8009\""));
            Check("tomcat: shutdown port / command replaced, bound to 127.0.0.1", xml.Contains("<Server port=\"19005\" shutdown=\"PORTLY-TEST\" address=\"127.0.0.1\">") || (xml.Contains("port=\"19005\"") && xml.Contains("shutdown=\"PORTLY-TEST\"") && !xml.Contains("8005")));
            Check("tomcat: comments and declaration kept", xml.StartsWith("<?xml") && xml.Contains("<!-- licence -->"));
            Check("tomcat: logs go to logs\\tomcat", xml.Contains("directory=\"" + Paths.Fwd(TomcatService.LogDir) + "\"")
                && File.ReadAllText(Path.Combine(cbase, "conf", "logging.properties")).Contains(Paths.Fwd(TomcatService.LogDir)));
            Check("tomcat: base gets ROOT but not the examples", File.Exists(Path.Combine(cbase, "webapps", "ROOT", "index.jsp")) && !Directory.Exists(Path.Combine(cbase, "webapps", "examples")) && Directory.Exists(Path.Combine(cbase, "work")));
            // a second start keeps the user's conf and only re-applies the ports
            File.AppendAllText(Path.Combine(cbase, "conf", "tomcat-users.xml"), "<tomcat-users/>");
            TomcatService.PrepareBase(home, cbase, 19081, "0.0.0.0", 19006, "PORTLY-TEST2");
            xml = File.ReadAllText(Path.Combine(cbase, "conf", "server.xml"));
            Check("tomcat: restart re-applies port and LAN address, keeps own files", xml.Contains("port=\"19081\"") && xml.Contains("address=\"0.0.0.0\"") && xml.Contains("PORTLY-TEST2") && File.Exists(Path.Combine(cbase, "conf", "tomcat-users.xml")));

            Check("tomcat: port conflict check knows the Tomcat port", PortGuard.Needs(ServiceManager.Tomcat).Any(n => n.Item1 == "TomcatPort" && n.Item3 == s.TomcatPort));
            bool was = Features.IsOn("tomcat");
            Features.Set("tomcat", false);
            bool hiddenOff = !ServiceManager.Rows.Contains(ServiceManager.Tomcat) && !PackageTypes.Ordered.Any(t => t.Id == "tomcat") && PackageTypes.Ordered.Any(t => t.Id == "java");
            Features.Set("tomcat", true);
            bool shownOn = ServiceManager.Rows.Contains(ServiceManager.Tomcat) && PackageTypes.Ordered.Any(t => t.Id == "tomcat");
            Features.Set("tomcat", was);
            Check("tomcat: row and package only while the feature is on (Java always listed)", hiddenOff && shownOn);
            try { Directory.Delete(tmp, true); } catch { }
        }

        static async Task TomcatLiveChecks(Settings s)
        {
            bool was = Features.IsOn("tomcat");
            int oldPort = s.TomcatPort;
            Features.Set("tomcat", true);
            try
            {
                foreach (var type in new[] { "java", "tomcat" })
                {
                    if (PackageTypes.Installed(type).Any()) continue;
                    var e = Catalog.Recommended(type);
                    if (e == null) { Check("tomcat: " + type + " in catalog", false); return; }
                    await Installer.InstallAsync(e, null, CancellationToken.None);
                }
                Check("tomcat: Java and Tomcat installed", ServiceManager.Tomcat.IsInstalled && TomcatService.FindJavaHome() != null);
                s.TomcatPort = ProcessUtil.FreePort(Enumerable.Range(19080, 50), new int[0]);
                await ServiceManager.Tomcat.StartAsync();
                Check("tomcat: running", ServiceManager.Tomcat.State == ServiceState.Running, ServiceManager.Tomcat.LastError ?? "");
                var page = Get("http://127.0.0.1:" + s.TomcatPort + "/", null);
                Check("tomcat: default page served", page.Contains("Apache Tomcat"), page.Length > 120 ? page.Substring(0, 120) : page);
                Check("tomcat: base folder outside the package", Directory.Exists(Path.Combine(TomcatService.WebappsDir, "ROOT")) && !TomcatService.WebappsDir.StartsWith(Paths.Bin));
                await ServiceManager.Tomcat.StopAsync();
                await Task.Delay(500);
                Check("tomcat: stopped and port free", ServiceManager.Tomcat.State == ServiceState.Stopped && !ProcessUtil.IsPortInUse(s.TomcatPort));
            }
            finally
            {
                if (ServiceManager.Tomcat.IsActive) await ServiceManager.Tomcat.StopAsync();
                s.TomcatPort = oldPort;
                Features.Set("tomcat", was);
                s.Save();
            }
        }
    }
}
