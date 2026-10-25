using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Portly.Core;
using Portly.Packages;

namespace Portly.Services
{
    /// <summary>
    /// Apache Tomcat (feature "tomcat"). java.exe runs Bootstrap directly (no catalina.bat), so it lives in Portly's
    /// job object. CATALINA_HOME = the package folder (never written to), CATALINA_BASE = data\tomcat\&lt;branch&gt;:
    /// conf\ and the default web apps are copied there on the first start, so a WAR file goes into that webapps\
    /// folder and survives patch updates. Logs: logs\tomcat. The shutdown port gets a random local port and a random
    /// command on every start (the default 8005 / "SHUTDOWN" would let any local program stop it).
    /// </summary>
    public class TomcatService : Service
    {
        Process _p;
        int _shutdownPort;
        string _shutdownCmd;

        public override string Id { get { return "tomcat"; } }
        public override string Title { get { return "Tomcat"; } }
        public override string PackageType { get { return "tomcat"; } }
        public override string PortText { get { return Settings.Current.TomcatPort.ToString(); } }

        public static bool Enabled { get { return Features.IsOn("tomcat"); } }

        /// <summary>data\tomcat\11.0: one base per branch (Tomcat 9 apps use javax.*, 10+ jakarta.*).</summary>
        public static string BaseDir(string tomcatFolder) { return Path.Combine(Paths.Data, "tomcat", PackageTypes.Branch(tomcatFolder)); }
        public static string LogDir { get { return Path.Combine(Paths.Logs, "tomcat"); } }

        /// <summary>webapps folder of the active Tomcat version (null when none is installed).</summary>
        public static string WebappsDir
        {
            get { var f = PackageTypes.ActiveFolder("tomcat"); return f == "" ? null : Path.Combine(BaseDir(f), "webapps"); }
        }

        public static string Url { get { return "http://localhost:" + Settings.Current.TomcatPort + "/"; } }

        /// <summary>Java for Tomcat: the active Portly Java package, else the newest Java installed on Windows
        /// (JAVA_HOME, registry, vendor folders). Null when there is none.</summary>
        public static string FindJavaHome()
        {
            var dir = PackageTypes.ActiveDir("java");
            if (dir != null) return dir;
            return Tools.SystemJava.Newest();
        }

        /// <summary>Feature version of a Java home from its "release" file (JAVA_VERSION="21.0.12.1" -> 21, "1.8.0_x" -> 8).</summary>
        public static int JavaMajor(string javaHome)
        {
            try
            {
                var f = Path.Combine(javaHome, "release");
                var text = File.Exists(f) ? File.ReadAllText(f) : Path.GetFileName(javaHome);
                var m = Regex.Match(text, "JAVA_VERSION=\"?(\\d+)(?:\\.(\\d+))?");
                if (!m.Success) m = Regex.Match(text, "(\\d+)(?:\\.(\\d+))?");
                if (!m.Success) return 0;
                int major = int.Parse(m.Groups[1].Value);
                return major == 1 && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : major;
            }
            catch { return 0; }
        }

        /// <summary>Oldest Java a Tomcat line runs on: 11 -> 17, 10.1 -> 11, 9 -> 8.</summary>
        public static int MinJava(string tomcatFolder)
        {
            var v = PackageTypes.ParseVersion(tomcatFolder);
            return v.Major >= 11 ? 17 : v.Major == 10 ? 11 : 8;
        }

        /// <summary>java.exe arguments, the same as "catalina.bat run" uses.</summary>
        public static string JavaArgs(string home, string cbase)
        {
            var sb = new StringBuilder();
            sb.Append("-Djava.util.logging.config.file=\"").Append(Path.Combine(cbase, "conf", "logging.properties")).Append("\" ");
            sb.Append("-Djava.util.logging.manager=org.apache.juli.ClassLoaderLogManager ");
            sb.Append("-Djdk.tls.ephemeralDHKeySize=2048 -Djava.protocol.handler.pkgs=org.apache.catalina.webresources ");
            sb.Append("-Dorg.apache.catalina.security.SecurityListener.UMASK=0027 ");
            sb.Append("-classpath \"").Append(Path.Combine(home, "bin", "bootstrap.jar")).Append(';').Append(Path.Combine(home, "bin", "tomcat-juli.jar")).Append("\" ");
            sb.Append("-Dcatalina.base=\"").Append(cbase).Append("\" -Dcatalina.home=\"").Append(home).Append("\" ");
            sb.Append("-Djava.io.tmpdir=\"").Append(Path.Combine(cbase, "temp")).Append("\" ");
            sb.Append("org.apache.catalina.startup.Bootstrap start");
            return sb.ToString();
        }

        /// <summary>Environment of the Tomcat process (also what a web app sees as JAVA_HOME / CATALINA_*).</summary>
        public static Dictionary<string, string> BuildEnvironment(string javaHome, string home, string cbase)
        {
            return new Dictionary<string, string>
            {
                { "JAVA_HOME", javaHome },
                { "JRE_HOME", javaHome },
                { "CATALINA_HOME", home },
                { "CATALINA_BASE", cbase },
                { "CATALINA_TMPDIR", Path.Combine(cbase, "temp") },
                // what catalina.bat adds for Java 9+: older libraries reflect into these packages
                { "JDK_JAVA_OPTIONS", "--add-opens=java.base/java.lang=ALL-UNNAMED --add-opens=java.base/java.lang.invoke=ALL-UNNAMED --add-opens=java.base/java.lang.reflect=ALL-UNNAMED --add-opens=java.base/java.io=ALL-UNNAMED --add-opens=java.base/java.util=ALL-UNNAMED --add-opens=java.base/java.util.concurrent=ALL-UNNAMED --add-opens=java.rmi/sun.rmi.transport=ALL-UNNAMED" },
                { "PATH", Path.Combine(javaHome, "bin") + ";" + Environment.GetEnvironmentVariable("PATH") },
            };
        }

        /// <summary>
        /// Create CATALINA_BASE (first start: copy conf\ and the default apps except "examples") and point it at the
        /// current settings: HTTP connector port and address, shutdown port / command, log folder.
        /// </summary>
        public static void PrepareBase(string home, string cbase, int httpPort, string bind, int shutdownPort, string shutdownCmd)
        {
            var conf = Path.Combine(cbase, "conf");
            if (!File.Exists(Path.Combine(conf, "server.xml")))
            {
                CopyDir(Path.Combine(home, "conf"), conf);
                foreach (var app in new[] { "ROOT", "manager", "host-manager", "docs" })
                {
                    var src = Path.Combine(home, "webapps", app);
                    var dst = Path.Combine(cbase, "webapps", app);
                    if (Directory.Exists(src) && !Directory.Exists(dst)) CopyDir(src, dst);
                }
            }
            foreach (var d in new[] { "webapps", "work", "temp" }) Directory.CreateDirectory(Path.Combine(cbase, d));
            Directory.CreateDirectory(LogDir);

            var serverXml = Path.Combine(conf, "server.xml");
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(serverXml);
            var server = doc.DocumentElement;
            server.SetAttribute("port", shutdownPort.ToString());
            server.SetAttribute("address", "127.0.0.1");
            server.SetAttribute("shutdown", shutdownCmd);
            var http = server.GetElementsByTagName("Connector").Cast<XmlElement>().FirstOrDefault(IsPlainHttp);
            if (http == null) throw new Exception("server.xml: no HTTP connector");
            http.SetAttribute("port", httpPort.ToString());
            http.SetAttribute("address", bind);
            foreach (var valve in server.GetElementsByTagName("Valve").Cast<XmlElement>())
                if (valve.GetAttribute("className").EndsWith(".AccessLogValve", StringComparison.Ordinal)) valve.SetAttribute("directory", Paths.Fwd(LogDir));
            using (var w = XmlWriter.Create(serverXml, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = !(doc.FirstChild is XmlDeclaration) }))
                doc.Save(w);

            var logging = Path.Combine(conf, "logging.properties");
            if (File.Exists(logging))
            {
                var text = File.ReadAllText(logging);
                var fixedText = text.Replace("${catalina.base}/logs", Paths.Fwd(LogDir));
                if (fixedText != text) File.WriteAllText(logging, fixedText, new UTF8Encoding(false));
            }
        }

        /// <summary>The plain HTTP/1.1 connector (not AJP, not the TLS one).</summary>
        static bool IsPlainHttp(XmlElement c)
        {
            var proto = c.GetAttribute("protocol");
            if (proto.StartsWith("AJP", StringComparison.OrdinalIgnoreCase) || proto.IndexOf("Ajp", StringComparison.Ordinal) >= 0) return false;
            return !string.Equals(c.GetAttribute("SSLEnabled"), "true", StringComparison.OrdinalIgnoreCase);
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        static int FreeLocalPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        static string RandomCommand()
        {
            var b = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return "PORTLY-" + BitConverter.ToString(b).Replace("-", "");
        }

        public override async Task StartAsync()
        {
            var home = PackageTypes.ActiveDir("tomcat");
            if (home == null) { SetState(ServiceState.Error, I18n.T("err.notInstalled", "Tomcat")); return; }
            if (_p != null && !_p.HasExited) { SetState(ServiceState.Running); return; }
            var java = FindJavaHome();
            if (java == null) { SetState(ServiceState.Error, I18n.T("tomcat.noJava")); return; }
            int need = MinJava(Path.GetFileName(home)), have = JavaMajor(java);
            if (have > 0 && have < need) { SetState(ServiceState.Error, I18n.T("tomcat.oldJava", "Tomcat " + VersionLabel, need, have)); return; }
            StopRequested = false;
            SetState(ServiceState.Starting);
            var s = Settings.Current;
            var port = s.TomcatPort;
            var cbase = BaseDir(Path.GetFileName(home));
            _shutdownPort = FreeLocalPort();
            _shutdownCmd = RandomCommand();
            string error = await Task.Run(() =>
            {
                try { PrepareBase(home, cbase, port, s.AllowLan ? "0.0.0.0" : "127.0.0.1", _shutdownPort, _shutdownCmd); return null; }
                catch (Exception ex) { Log.Error("Tomcat config", ex); return ex.Message; }
            });
            if (error != null) { SetState(ServiceState.Error, error); return; }
            if (!EnsurePortFree(port)) return;
            _p = Launch(Path.Combine(java, "bin", "java.exe"), JavaArgs(home, cbase), cbase, BuildEnvironment(java, home, cbase), @"tomcat\console", OnExit);
            if (await WaitForPort(port, _p, 90000))
            {
                SetState(ServiceState.Running);
                Log.Info(I18n.T("log.started", "Tomcat " + VersionLabel + " (Java " + have + ")"));
            }
            else
            {
                if (!_p.HasExited) StopProcess(_p, false, 0);
                SetState(ServiceState.Error, (_p.HasExited ? ExitReason(_p) : I18n.T("err.timeout")) + " " + I18n.T("err.seeLog", @"tomcat\console.log"));
            }
        }

        void OnExit(Process p)
        {
            if (p != _p || StopRequested) return;
            SetState(ServiceState.Error, ExitReason(p) + " " + I18n.T("err.seeLog", @"tomcat\console.log"));
        }

        /// <summary>Tomcat's own clean stop: the shutdown command on its local shutdown port.</summary>
        static bool SendShutdown(int port, string cmd)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    if (!c.ConnectAsync(IPAddress.Loopback, port).Wait(3000)) return false;
                    var b = Encoding.ASCII.GetBytes(cmd);
                    c.GetStream().Write(b, 0, b.Length);
                    c.GetStream().Flush();
                }
                return true;
            }
            catch { return false; }
        }

        public override async Task StopAsync()
        {
            StopRequested = true;
            var p = _p;
            if (p == null || p.HasExited) { _p = null; SetState(ServiceState.Stopped); return; }
            SetState(ServiceState.Stopping);
            int sp = _shutdownPort; string cmd = _shutdownCmd;
            await Task.Run(() =>
            {
                // web apps get time to finish; then Ctrl+C, then the process tree is killed
                if (!(SendShutdown(sp, cmd) && p.WaitForExit(20000))) StopProcess(p, true, 5000);
            });
            _p = null;
            SetState(ServiceState.Stopped);
            Log.Info(I18n.T("log.stopped", "Tomcat"));
        }
    }
}
