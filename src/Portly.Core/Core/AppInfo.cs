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
        public const string EditionName = "Windows 10 / 11";
#endif

        public static readonly Version Version = typeof(AppInfo).Assembly.GetName().Version;
        public static string VersionText { get { return Version.Major + "." + Version.Minor + "." + Version.Build; } }

        /// <summary>Updates exist only in the modern edition.</summary>
        public static bool UpdatesSupported { get { return !IsLegacy; } }
    }
}
