using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Portly.Core;

namespace Portly.UI
{
    public class Palette
    {
        public Color Bg, Sidebar, SidebarActive, SidebarActiveText, Card, Border, Text, Muted,
                     Accent, AccentHover, AccentText, Input, Hover, Green, Orange, Red, Gray, Heart,
                     ConsoleBg, ConsoleText, ConsoleMuted;
    }

    /// <summary>Light / dark colors. "system" follows the Windows app theme (Windows 10+; Windows 7/8 are light).</summary>
    public static class Theme
    {
        static Color C(string hex) { return ColorTranslator.FromHtml(hex); }

        public static readonly Palette Light = new Palette
        {
            Bg = C("#F4F6FA"), Sidebar = C("#FFFFFF"), SidebarActive = C("#EEF2FF"), SidebarActiveText = C("#1D4ED8"),
            Card = C("#FFFFFF"), Border = C("#E4E7EC"), Text = C("#101828"), Muted = C("#667085"),
            Accent = C("#2563EB"), AccentHover = C("#1D4ED8"), AccentText = Color.White, Input = C("#FFFFFF"), Hover = C("#F2F4F7"),
            Green = C("#16A34A"), Orange = C("#F59E0B"), Red = C("#DC2626"), Gray = C("#98A2B3"), Heart = C("#E11D48"),
            ConsoleBg = C("#0F172A"), ConsoleText = C("#CBD5E1"), ConsoleMuted = C("#64748B"),
        };

        public static readonly Palette Dark = new Palette
        {
            Bg = C("#0F1115"), Sidebar = C("#15181E"), SidebarActive = C("#1E2A44"), SidebarActiveText = C("#93C5FD"),
            Card = C("#1A1E25"), Border = C("#2A303A"), Text = C("#E6E8EC"), Muted = C("#98A2B3"),
            Accent = C("#3B82F6"), AccentHover = C("#2563EB"), AccentText = Color.White, Input = C("#222731"), Hover = C("#232833"),
            Green = C("#22C55E"), Orange = C("#F59E0B"), Red = C("#F87171"), Gray = C("#4B5563"), Heart = C("#FB7185"),
            ConsoleBg = C("#0B0D11"), ConsoleText = C("#CBD5E1"), ConsoleMuted = C("#64748B"),
        };

        public static Palette Current { get; private set; } = Light;
        public static bool IsDark { get { return Current == Dark; } }

        /// <summary>"system", "light" or "dark".</summary>
        public static void Load(string preference)
        {
            bool dark = preference == "dark" || (preference != "light" && SystemPrefersDark());
            Current = dark ? Dark : Light;
        }

        public static bool SystemPrefersDark()
        {
            if (!OsInfo.IsWin10OrLater) return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v != null && Convert.ToInt32(v) == 0;
                }
            }
            catch { return false; }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Dark title bar on Windows 10 1809+ / 11. Ignored elsewhere.</summary>
        public static void ApplyTitleBar(Form f)
        {
            if (!OsInfo.IsWin10OrLater || OsInfo.Version.Build < 17763) return;
            try
            {
                int on = IsDark ? 1 : 0;
                int attr = OsInfo.Version.Build >= 18985 ? 20 : 19;
                DwmSetWindowAttribute(f.Handle, attr, ref on, 4);
            }
            catch { }
        }
    }
}
