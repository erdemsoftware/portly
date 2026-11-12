using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Portly.Core
{
    /// <summary>Which operating system Portly runs on. The .NET Framework 4.8 build is Windows only.</summary>
    public static class OS
    {
#if NETFRAMEWORK
        public static readonly bool IsWindows = true;
        public static readonly bool IsLinux = false;
        public static readonly bool IsMac = false;
#else
        public static readonly bool IsWindows = OperatingSystem.IsWindows();
        public static readonly bool IsLinux = OperatingSystem.IsLinux();
        public static readonly bool IsMac = OperatingSystem.IsMacOS();
#endif

        /// <summary>"windows", "linux" or "macos": the os field of the package catalog.</summary>
        public static string Name { get { return IsWindows ? "windows" : IsMac ? "macos" : "linux"; } }

        /// <summary>"x64" or "arm64": the arch field of the package catalog.</summary>
        public static string Arch
        {
            get { return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64"; }
        }

        /// <summary>Suffix of executable files: ".exe" on Windows, nothing elsewhere.</summary>
        public static string ExeSuffix { get { return IsWindows ? ".exe" : ""; } }

        public static string Exe(string name) { return name + ExeSuffix; }

        /// <summary>
        /// Linux: version of the GNU C library (2.35 on Ubuntu 22.04), or null on musl (Alpine), macOS and Windows.
        /// Packages built against a newer glibc (catalog field minGlibc) cannot start on an older one.
        /// </summary>
        public static Version Glibc
        {
            get
            {
                if (!_glibcRead) { _glibcRead = true; _glibc = ReadGlibc(); }
                return _glibc;
            }
        }
        static Version _glibc;
        static bool _glibcRead;

#if NETFRAMEWORK
        static Version ReadGlibc() { return null; }
#else
        [DllImport("libc", EntryPoint = "gnu_get_libc_version")]
        static extern IntPtr GnuGetLibcVersion();

        static Version ReadGlibc()
        {
            if (!IsLinux) return null;
            try
            {
                var s = Marshal.PtrToStringAnsi(GnuGetLibcVersion());
                Version v;
                return s != null && Version.TryParse(s.Trim(), out v) ? v : null;
            }
            catch { return null; } // musl has no gnu_get_libc_version
        }
#endif
    }

    public static class Pipes
    {
        /// <summary>
        /// Named pipe server. On Windows the ACL decides who may connect; elsewhere the pipe is a Unix socket
        /// that only the current user can open.
        /// </summary>
        public static NamedPipeServerStream Server(string name, int maxInstances, int inBuffer, int outBuffer, Func<PipeSecurity> security)
        {
#if NETFRAMEWORK
            return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBuffer, outBuffer, security());
#else
            if (OS.IsWindows)
                return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBuffer, outBuffer, security());
            return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, inBuffer, outBuffer);
#endif
        }
    }

    public static class Units
    {
        public static string Bytes(long b)
        {
            if (b < 1024 * 1024) return (b / 1024) + " KB";
            return (b / 1048576.0).ToString("0.0") + " MB";
        }
    }
}
