using System;

namespace Portly.Core
{
    /// <summary>Settings of the DevWorkflow features: background jobs, hooks, profiler.</summary>
    public partial class Settings
    {
        /// <summary>Seconds a hook script (usr\hooks\*.cmd, portly.yml hooks) may run before it is stopped.</summary>
        public int HookTimeoutSeconds { get { return Math.Max(5, _ini.GetInt("devworkflow", "HookTimeoutSeconds", 120)); } set { _ini.Set("devworkflow", "HookTimeoutSeconds", value); } }

        /// <summary>A background job log is rotated (to .1) when it grows past this size.</summary>
        public int JobLogMaxMb { get { return Math.Max(1, _ini.GetInt("devworkflow", "JobLogMaxMb", 5)); } set { _ini.Set("devworkflow", "JobLogMaxMb", value); } }

        /// <summary>PHP folders where the profiler switched Xdebug on itself (so switching the profiler off removes it again).</summary>
        public string ProfilerXdebugBy { get { return _ini.Get("devworkflow", "ProfilerXdebugBy", ""); } set { _ini.Set("devworkflow", "ProfilerXdebugBy", value); } }
    }
}
