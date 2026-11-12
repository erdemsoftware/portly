using System;

namespace Portly.Core
{
    /// <summary>Product identity, edition and public links.</summary>
    public static class AppInfo
    {
        public const string Name = "Portly";
        public const string Publisher = "Erdem Software";
        public const string Repo = "erdemsoftware/portly";
        public const string GitHubUrl = "https://github.com/" + Repo;
        public const string WebsiteUrl = "https://erdemsoftware.com";
        public const string ReleasesUrl = GitHubUrl + "/releases";
        public const string TranslateUrl = GitHubUrl + "/tree/main/src/Portly/lang";
        public const string IssuesUrl = GitHubUrl + "/issues";
        public const string ManifestBase = "https://raw.githubusercontent.com/" + Repo + "/main/manifest/";

        /// <summary>Donation page. Can be changed remotely through manifest/app.json ("donateUrl").</summary>
        public static string DonateUrl = "https://buymeacoffee.com/erdemsoftware";

#if LEGACY
        public static readonly bool IsLegacy = true;
        public const string EditionId = "legacy";
        public const string EditionName = "Legacy (Windows 7 / 8 / 8.1)";
#else
        public static readonly bool IsLegacy = false;
        public const string EditionId = "modern";
        public static string EditionName { get { return OS.IsWindows ? "Windows 10 / 11" : OS.IsMac ? "macOS" : "Linux"; } }
#endif

        public static readonly Version Version = typeof(AppInfo).Assembly.GetName().Version;
        public static string VersionText { get { return Version.Major + "." + Version.Minor + "." + Version.Build; } }

        /// <summary>
        /// The version people see: VersionText, or "2.0.0-beta.1" for a pre-release build (dotnet publish -p:Version=2.0.0-beta.1
        /// keeps the assembly version at 2.0.0, which updates compare, and puts the label in the informational version).
        /// </summary>
        public static string VersionLabel
        {
            get
            {
                if (_label != null) return _label;
                var label = VersionText;
                try
                {
                    var attrs = typeof(AppInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                    var info = attrs.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion : "";
                    int plus = info.IndexOf('+');
                    if (plus >= 0) info = info.Substring(0, plus);
                    if (info.StartsWith(VersionText + "-", StringComparison.Ordinal) && info.Length <= VersionText.Length + 24) label = info;
                }
                catch { }
                return _label = label;
            }
        }
        static string _label;

        /// <summary>True for a beta / pre-release build (see VersionLabel).</summary>
        public static bool IsPreRelease { get { return VersionLabel != VersionText; } }

        /// <summary>Updates exist only in the modern edition.</summary>
        public static bool UpdatesSupported { get { return !IsLegacy; } }
    }
}
