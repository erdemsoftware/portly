using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Portly.Core;

namespace Portly.Desktop.UI
{
    /// <summary>Same colors as the Windows app (src\Portly\UI\Theme.cs).</summary>
    public sealed class Palette
    {
        public Color Bg, Sidebar, SidebarActive, SidebarActiveText, Card, Line, Text, Muted, Accent, AccentHover, AccentText,
            Input, Hover, Green, Orange, Red, Gray, Heart, ConsoleBg, ConsoleText, ConsoleMuted;

        static Color C(string hex) { return Color.Parse(hex); }

        public static readonly Palette Light = new Palette
        {
            Bg = C("#F4F6FA"), Sidebar = C("#FFFFFF"), SidebarActive = C("#EEF2FF"), SidebarActiveText = C("#1D4ED8"),
            Card = C("#FFFFFF"), Line = C("#E4E7EC"), Text = C("#101828"), Muted = C("#667085"),
            Accent = C("#2563EB"), AccentHover = C("#1D4ED8"), AccentText = Colors.White, Input = C("#FFFFFF"), Hover = C("#F2F4F7"),
            Green = C("#16A34A"), Orange = C("#F59E0B"), Red = C("#DC2626"), Gray = C("#98A2B3"), Heart = C("#E11D48"),
            ConsoleBg = C("#0F172A"), ConsoleText = C("#CBD5E1"), ConsoleMuted = C("#64748B"),
        };

        public static readonly Palette Dark = new Palette
        {
            Bg = C("#0F1115"), Sidebar = C("#15181E"), SidebarActive = C("#1E2A44"), SidebarActiveText = C("#93C5FD"),
            Card = C("#1A1E25"), Line = C("#2A303A"), Text = C("#E6E8EC"), Muted = C("#98A2B3"),
            Accent = C("#3B82F6"), AccentHover = C("#2563EB"), AccentText = Colors.White, Input = C("#222731"), Hover = C("#232833"),
            Green = C("#22C55E"), Orange = C("#F59E0B"), Red = C("#F87171"), Gray = C("#4B5563"), Heart = C("#FB7185"),
            ConsoleBg = C("#0B0D11"), ConsoleText = C("#CBD5E1"), ConsoleMuted = C("#64748B"),
        };
    }

    /// <summary>
    /// Current palette. Its colors are also application resources with the same names
    /// ({DynamicResource Muted} in Styles.axaml, Ui.Brush("Muted") in code).
    /// </summary>
    public static class Theme
    {
        public static Palette Current { get; private set; } = Palette.Light;
        public static bool IsDark { get { return Current == Palette.Dark; } }

        /// <summary>"system", "light" or "dark" (Settings.Theme).</summary>
        public static void Apply(Application app, string preference)
        {
            app.RequestedThemeVariant = preference == "dark" ? ThemeVariant.Dark : preference == "light" ? ThemeVariant.Light : ThemeVariant.Default;
            var dark = preference == "dark" || (preference != "light" && app.ActualThemeVariant == ThemeVariant.Dark);
            Current = dark ? Palette.Dark : Palette.Light;
            var p = Current;
            var r = app.Resources;
            foreach (var f in typeof(Palette).GetFields())
            {
                if (f.FieldType != typeof(Color)) continue;
                var c = (Color)f.GetValue(p);
                r[f.Name] = new SolidColorBrush(c);
                r[f.Name + "Color"] = c;
            }
            // Fluent controls (toggle, check box, slider, focus) follow Portly's blue
            r["SystemAccentColor"] = p.Accent;
            r["SystemAccentColorDark1"] = p.AccentHover;
            r["SystemAccentColorDark2"] = p.AccentHover;
            r["SystemAccentColorLight1"] = p.Accent;
            r["SystemAccentColorLight2"] = p.Accent;
        }

        /// <summary>Brand color of a service / package tile (by package type id).</summary>
        public static IBrush Brand(string type)
        {
            switch (type)
            {
                case "php": return B("#777BB4");
                case "apache": return B("#D22128");
                case "nginx": return B("#009639");
                case "caddy": return B("#1F88C0");
                case "frankenphp": return B("#A855F7");
                case "mysql": return B("#00758F");
                case "mariadb": return B("#C0765A");
                case "postgresql": return B("#336791");
                case "mongodb": return B("#47A248");
                case "redis": return B("#DC382D");
                case "memcached": return B("#4B8BBE");
                case "mailpit": case "mailhog": case "smtp4dev": return B("#0EA5E9");
                case "tomcat": case "java": return B("#D97706");
                case "nodejs": return B("#5FA04E");
                case "cloudflared": return B("#F38020");
                default: return B("#64748B");
            }
        }

        static IBrush B(string hex) { return new SolidColorBrush(Color.Parse(hex)); }
    }
}
