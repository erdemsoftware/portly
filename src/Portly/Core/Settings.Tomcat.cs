namespace Portly.Core
{
    /// <summary>Apache Tomcat (feature "tomcat").</summary>
    public partial class Settings
    {
        public const int DefaultTomcatPort = 8080;

        /// <summary>HTTP port of Tomcat's connector (127.0.0.1 unless LAN access is on).</summary>
        public int TomcatPort { get { return _ini.GetInt("services", "TomcatPort", DefaultTomcatPort); } set { _ini.Set("services", "TomcatPort", value); } }
    }
}
