using System;
using System.Runtime.InteropServices;

namespace Portly.Core
{
    /// <summary>Real Windows version (RtlGetVersion is not affected by compatibility shims).</summary>
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
        public static double Number { get { return Version.Major + Version.Minor / 10.0; } }
        public static bool IsWin10OrLater { get { return Version.Major >= 10; } }
        public static bool Is64Bit { get { return Environment.Is64BitOperatingSystem; } }

        public static string DisplayName
        {
            get
            {
                if (Version.Major >= 10) return Version.Build >= 22000 ? "Windows 11" : "Windows 10";
                if (Version.Major == 6 && Version.Minor == 3) return "Windows 8.1";
                if (Version.Major == 6 && Version.Minor == 2) return "Windows 8";
                if (Version.Major == 6 && Version.Minor == 1) return "Windows 7";
                return "Windows " + Version;
            }
        }
    }
}
