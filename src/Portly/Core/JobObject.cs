using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Portly.Core
{
    /// <summary>
    /// All service processes are put in a Windows job object, so if Portly ever crashes
    /// Apache, MySQL, PHP ... are closed by Windows instead of being left running.
    /// </summary>
    public static class JobObject
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct BASIC_LIMIT { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct EXTENDED_LIMIT { public BASIC_LIMIT Basic; public IO_COUNTERS Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }

        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        static IntPtr _job = IntPtr.Zero;
        static bool _failed;

        static void Ensure()
        {
            if (_job != IntPtr.Zero || _failed) return;
            try
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new EXTENDED_LIMIT();
                info.Basic.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int len = Marshal.SizeOf(typeof(EXTENDED_LIMIT));
                IntPtr ptr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(_job, 9 /* ExtendedLimitInformation */, ptr, (uint)len)) _failed = true;
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { _failed = true; }
        }

        /// <summary>Best effort: on Windows 7 this fails when Portly itself already runs inside a job.</summary>
        public static void Add(Process p)
        {
            Ensure();
            if (_failed || _job == IntPtr.Zero) return;
            try { AssignProcessToJobObject(_job, p.Handle); } catch { }
        }
    }
}
