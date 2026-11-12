using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Portly.Core
{
    /// <summary>
    /// Real Windows version (RtlGetVersion is not affected by compatibility shims). On macOS / Linux the operating
    /// system counts as "modern" (IsWin10OrLater, Number 10.0, so no package is filtered by a Windows version) and
    /// DisplayName is its own name and version ("macOS 15.1", "Ubuntu 24.04.1 LTS").
    /// </summary>
    public static class OsInfo
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor, wServicePackMinor, wSuiteMask;
            public byte wProductType, wReserved;
        }

        [DllImport("ntdll.dll")]
        static extern int RtlGetVersion(ref OSVERSIONINFOEX info);

        public static readonly Version Version = Read();

        static Version Read()
        {
            try
            {
                var v = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX)) };
                if (RtlGetVersion(ref v) == 0) return new Version(v.dwMajorVersion, v.dwMinorVersion, v.dwBuildNumber);
            }
            catch { }
            return Environment.OSVersion.Version;
        }

        /// <summary>Major.minor as a number: 6.1 = Win7, 6.2 = Win8, 6.3 = Win8.1, 10.0 = Win10/11.</summary>
        public static double Number { get { return OS.IsWindows ? Version.Major + Version.Minor / 10.0 : 10.0; } }
        /// <summary>Windows 10 or later; true on macOS / Linux too ("a current system"). Windows-only features must also check OS.IsWindows.</summary>
        public static bool IsWin10OrLater { get { return !OS.IsWindows || Version.Major >= 10; } }
        public static bool Is64Bit { get { return Environment.Is64BitOperatingSystem; } }

        static string _unixName;

        /// <summary>The "PRETTY_NAME" of an os-release file ("Ubuntu 24.04.1 LTS"), else NAME + VERSION_ID, else "".</summary>
        public static string ParseOsRelease(string text)
        {
            string pretty = "", name = "", ver = "";
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var l = raw.Trim();
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                var k = l.Substring(0, eq);
                var v = l.Substring(eq + 1).Trim().Trim('"', '\'');
                if (k == "PRETTY_NAME") pretty = v;
                else if (k == "NAME") name = v;
                else if (k == "VERSION_ID") ver = v;
            }
            if (pretty != "") return pretty;
            return (name + " " + ver).Trim();
        }

        static string UnixName()
        {
            if (_unixName != null) return _unixName;
            string n = "";
            try
            {
                if (OS.IsMac)
                {
                    var r = ProcessUtil.Run("sw_vers", "-productVersion", "/", null, 5000);
                    n = "macOS" + (r.Ok && r.Output.Trim() != "" ? " " + r.Output.Trim() : "");
                }
                else
                {
                    foreach (var f in new[] { "/etc/os-release", "/usr/lib/os-release" })
                        if (File.Exists(f)) { n = ParseOsRelease(File.ReadAllText(f)); if (n != "") break; }
                    if (n == "") n = "Linux " + Environment.OSVersion.Version;
                }
            }
            catch { n = OS.IsMac ? "macOS" : "Linux"; }
            return _unixName = n;
        }

        public static string DisplayName
        {
            get
            {
                if (!OS.IsWindows) return UnixName();
                if (Version.Major >= 10) return Version.Build >= 22000 ? "Windows 11" : "Windows 10";
                if (Version.Major == 6 && Version.Minor == 3) return "Windows 8.1";
                if (Version.Major == 6 && Version.Minor == 2) return "Windows 8";
                if (Version.Major == 6 && Version.Minor == 1) return "Windows 7";
                return "Windows " + Version;
            }
        }
    }
}
