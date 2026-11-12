using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Portly.Core
{
    public static class WinUtil
    {
        /// <summary>Open an address, file or folder with the system's default program (Windows shell, macOS "open", Linux "xdg-open").</summary>
        public static void Open(string target)
        {
            if (!OS.IsWindows)
            {
                var c = UnixIntegration.OpenCommand(target);
                if (!UnixIntegration.Start(c.Item1, c.Item2)) Log.Warn("Open " + target + ": " + c.Item1 + " is not available");
                return;
            }
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Open " + target, ex); }
        }

        public static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                if (!OS.IsWindows) { Open(dir); return; }
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Log.Error("Open folder", ex); }
        }

        /// <summary>Opens a file with Notepad (always available on every Windows); macOS: the default text editor ("open -t"), Linux: xdg-open.</summary>
        public static void Edit(string file)
        {
            if (!OS.IsWindows)
            {
                var c = UnixIntegration.EditCommand(file);
                if (!UnixIntegration.Start(c.Item1, c.Item2)) Log.Warn("Edit " + file + ": " + c.Item1 + " is not available");
                return;
            }
            try { Process.Start("notepad.exe", "\"" + file + "\""); } catch (Exception ex) { Log.Error("Edit", ex); }
        }

        /// <summary>
        /// Run a program elevated (UAC). Returns exit code, or -2 when the user cancelled.
        /// Windows only: the argument text is a Windows command line. Use Elevate.Run / Elevate.RunSelf on every system.
        /// </summary>
        public static int RunElevated(string exe, string args, bool wait = true)
        {
            if (!OS.IsWindows) { Log.Warn("WinUtil.RunElevated is Windows only (use Elevate.Run)"); return -1; }
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi))
                {
                    if (!wait || p == null) return 0;
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return -2; }
            catch (Exception ex) { Log.Error("Elevation", ex); return -1; }
        }

        // ---------------- start with Windows ----------------
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Windows: Run registry key. macOS: LaunchAgent net.portly.app. Linux: ~/.config/autostart/portly.desktop.</summary>
        public static void SetRunAtStartup(bool on)
        {
            if (!OS.IsWindows) { UnixIntegration.SetAutostart(on); return; }
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("Portly", "\"" + Paths.Exe + "\" --autostart");
                    else if (k.GetValue("Portly") != null) k.DeleteValue("Portly");
                }
            }
            catch (Exception ex) { Log.Error("Startup registry", ex); }
        }

        // ---------------- user PATH ----------------
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

        /// <summary>
        /// Replace every Portly folder in the user PATH with the given list (empty list = remove Portly).
        /// Only entries inside the Portly folder are touched.
        /// </summary>
        public static void SetUserPath(IEnumerable<string> portlyDirs)
        {
            // macOS / Linux: a marked block in the shell profile (zsh / bash), removed again with an empty list
            if (!OS.IsWindows) { UnixIntegration.SetShellPath(portlyDirs); return; }
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey("Environment"))
                {
                    var cur = (k.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string) ?? "";
                    var kept = cur.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Where(p => !p.StartsWith(Paths.Root + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
                    var next = portlyDirs.Concat(kept).ToList();
                    var value = string.Join(";", next);
                    if (value == cur) return;
                    k.SetValue("Path", value, RegistryValueKind.ExpandString);
                }
                UIntPtr r;
                SendMessageTimeout((IntPtr)0xffff, 0x001A, UIntPtr.Zero, "Environment", 2, 3000, out r);
                Log.Info("User PATH updated");
            }
            catch (Exception ex) { Log.Error("PATH", ex); }
        }

        // ---------------- Visual C++ runtime ----------------
        /// <summary>Installed VC++ 2015-2022+ x64 runtime minor version (e.g. 50 for 14.50), -1 when missing.</summary>
        public static int VcRuntimeMinor()
        {
            if (!OS.IsWindows) return int.MaxValue;   // no Visual C++ runtime outside Windows: nothing is missing
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"))
                {
                    if (k == null) return -1;
                    if (Convert.ToInt32(k.GetValue("Installed", 0)) != 1) return -1;
                    return Convert.ToInt32(k.GetValue("Minor", 0));
                }
            }
            catch { return -1; }
        }
    }
}
