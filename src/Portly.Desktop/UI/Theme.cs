using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Portly.Core;

namespace Portly.Desktop.UI
{
    /// <summary>Same colors as the Windows app (src\Portly\UI\Theme.cs).</summary>
    public sealed class Palette
    {
        public Color Bg, Sidebar, SidebarActive, SidebarActiveText, Card, Line, Text, Muted, Accent, AccentHover, AccentText,
            Input, Hover, Green, Orange, Red, Gray, Heart, ConsoleBg, ConsoleText, ConsoleMuted, RedHover, GreenHover, SwitchOff, DisabledFill, DisabledText, DisabledLine;

        /// <summary>Colors derived from the others: darker red on hover, off switch, flat look of a disabled button.</summary>
        Palette Finish(bool dark)
        {
            RedHover = GlyphPainter.Mix(Red, Colors.Black, .1);
            GreenHover = GlyphPainter.Mix(Green, Colors.Black, .1);
            SwitchOff = dark ? Color.FromRgb(70, 76, 88) : Color.FromRgb(208, 213, 221);
            DisabledFill = GlyphPainter.Mix(Bg, Text, dark ? .07 : .05);
            DisabledText = GlyphPainter.Mix(Bg, Text, .38);
            DisabledLine = GlyphPainter.Mix(Bg, Text, .12);
            return this;
        }

        static Color C(string hex) { return Color.Parse(hex); }

        public static readonly Palette Light = new Palette
        {
            Bg = C("#F4F6FA"), Sidebar = C("#FFFFFF"), SidebarActive = C("#EEF2FF"), SidebarActiveText = C("#1D4ED8"),
            Card = C("#FFFFFF"), Line = C("#E4E7EC"), Text = C("#101828"), Muted = C("#667085"),
            Accent = C("#2563EB"), AccentHover = C("#1D4ED8"), AccentText = Colors.White, Input = C("#FFFFFF"), Hover = C("#F2F4F7"),
            Green = C("#16A34A"), Orange = C("#F59E0B"), Red = C("#DC2626"), Gray = C("#98A2B3"), Heart = C("#E11D48"),
            ConsoleBg = C("#0F172A"), ConsoleText = C("#CBD5E1"), ConsoleMuted = C("#64748B"),
        }.Finish(false);

        public static readonly Palette Dark = new Palette
        {
            Bg = C("#0F1115"), Sidebar = C("#15181E"), SidebarActive = C("#1E2A44"), SidebarActiveText = C("#93C5FD"),
            Card = C("#1A1E25"), Line = C("#2A303A"), Text = C("#E6E8EC"), Muted = C("#98A2B3"),
            Accent = C("#3B82F6"), AccentHover = C("#2563EB"), AccentText = Colors.White, Input = C("#222731"), Hover = C("#232833"),
            Green = C("#22C55E"), Orange = C("#F59E0B"), Red = C("#F87171"), Gray = C("#4B5563"), Heart = C("#FB7185"),
            ConsoleBg = C("#0B0D11"), ConsoleText = C("#CBD5E1"), ConsoleMuted = C("#64748B"),
        }.Finish(true);
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
            ApplyFonts(r);
            ApplyInputs(r, p);
        }

        /// <summary>Segoe UI sizes of the Windows app (9 pt body, 9.5 pt bold, 16 pt title ...) in px; macOS and Linux use the same steps, one px larger.</summary>
        public static double FontScale { get { return OS.IsWindows ? 1 : 13.0 / 12; } }

        public static string MonoFamily { get { return "Consolas, Menlo, DejaVu Sans Mono, Cascadia Mono, monospace"; } }

        static void ApplyFonts(IResourceDictionary r)
        {
            double f = FontScale;
            r["FontSizeBase"] = 12 * f;
            r["FontSizeBold"] = 12.667 * f;
            r["FontSizeSmall"] = 11 * f;
            r["FontSizeSubtitle"] = 14 * f;
            r["FontSizeBrand"] = 18.667 * f;
            r["FontSizeTitle"] = 21.333 * f;
            r["FontSizeMono"] = 12.667 * f;
            r["ControlContentThemeFontSize"] = 12 * f;
            r["TabItemHeaderFontSize"] = 12 * f;
            r["ToolTipContentThemeFontSize"] = 12 * f;
            // Windows: Segoe UI like the WinForms app; macOS: the system font; Linux keeps the bundled Inter
            if (OS.IsWindows) r["ContentControlThemeFontFamily"] = new FontFamily("Segoe UI");
            else if (OS.IsMac) r["ContentControlThemeFontFamily"] = new FontFamily(".AppleSystemUIFont, Helvetica Neue, Inter");
            r["MonoFontFamily"] = new FontFamily(MonoFamily);
            // the Windows app uses the face "Segoe UI Semibold", which Skia cannot pick on Windows (weight 600 gives the Regular face): 650 gives the Bold face, the closest look
            r["SemiboldFamily"] = OS.IsWindows ? new FontFamily("Segoe UI") : OS.IsMac ? new FontFamily(".AppleSystemUIFont, Helvetica Neue, Inter") : FontFamily.Default;
            r["SemiboldWeight"] = OS.IsWindows ? (FontWeight)650 : FontWeight.SemiBold;
        }

        /// <summary>Text boxes, drop-downs and menus in the window colors (the WinForms inputs are flat with a thin line, accent when focused).</summary>
        static void ApplyInputs(IResourceDictionary r, Palette p)
        {
            Func<Color, IBrush> Br = c => new SolidColorBrush(c);
            r["TextControlBackground"] = r["TextControlBackgroundPointerOver"] = r["TextControlBackgroundFocused"] = Br(p.Input);
            r["TextControlBackgroundDisabled"] = Br(p.DisabledFill);
            r["TextControlBorderBrush"] = r["TextControlBorderBrushPointerOver"] = Br(p.Line);
            r["TextControlBorderBrushFocused"] = Br(p.Accent);
            r["TextControlBorderBrushDisabled"] = Br(p.DisabledLine);
            r["TextControlForeground"] = r["TextControlForegroundPointerOver"] = r["TextControlForegroundFocused"] = Br(p.Text);
            r["TextControlForegroundDisabled"] = Br(p.DisabledText);
            r["TextControlPlaceholderForeground"] = r["TextControlPlaceholderForegroundPointerOver"] = r["TextControlPlaceholderForegroundFocused"] = Br(p.Muted);
            r["TextControlThemeMinHeight"] = 28.0;
            r["TextControlThemePadding"] = new Thickness(8, 4, 8, 4);

            r["ComboBoxBackground"] = r["ComboBoxBackgroundPointerOver"] = r["ComboBoxBackgroundPressed"] = r["ComboBoxBackgroundUnfocused"] = Br(p.Input);
            r["ComboBoxBackgroundDisabled"] = Br(p.DisabledFill);
            r["ComboBoxBorderBrush"] = r["ComboBoxBackgroundBorderBrushUnfocused"] = Br(p.Line);
            r["ComboBoxBorderBrushPointerOver"] = r["ComboBoxBorderBrushPressed"] = r["ComboBoxBackgroundBorderBrushFocused"] = Br(p.Accent);
            r["ComboBoxBorderBrushDisabled"] = Br(p.DisabledLine);
            r["ComboBoxForeground"] = r["ComboBoxForegroundFocused"] = r["ComboBoxForegroundFocusedPressed"] = Br(p.Text);
            r["ComboBoxForegroundDisabled"] = Br(p.DisabledText);
            r["ComboBoxDropDownGlyphForeground"] = r["ComboBoxDropDownGlyphForegroundFocused"] = r["ComboBoxDropDownGlyphForegroundFocusedPressed"] = Br(p.Muted);
            r["ComboBoxDropDownGlyphForegroundDisabled"] = Br(p.DisabledLine);
            r["ComboBoxDropDownBackground"] = Br(p.Input);
            r["ComboBoxDropDownBorderBrush"] = Br(p.Line);
            r["ComboBoxItemBackgroundPointerOver"] = r["ComboBoxItemBackgroundPressed"] = Br(p.Hover);
            r["ComboBoxItemBackgroundSelected"] = r["ComboBoxItemBackgroundSelectedPointerOver"] = r["ComboBoxItemBackgroundSelectedPressed"] = Br(p.SidebarActive);
            r["ComboBoxItemForegroundSelected"] = r["ComboBoxItemForegroundSelectedPointerOver"] = r["ComboBoxItemForegroundSelectedPressed"] = Br(p.SidebarActiveText);
            r["ComboBoxItemForeground"] = r["ComboBoxItemForegroundPointerOver"] = r["ComboBoxItemForegroundPressed"] = Br(p.Text);
            r["ComboBoxMinHeight"] = 28.0;
            r["ComboBoxPadding"] = new Thickness(8, 3, 4, 3);
            r["ToolTipBackground"] = Br(p.Card);
            r["ToolTipBorderBrush"] = Br(p.Line);
            r["ToolTipForeground"] = Br(p.Text);
            r["MenuFlyoutPresenterBackground"] = Br(p.Card);
            r["MenuFlyoutPresenterBorderBrush"] = Br(p.Line);
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
                case "mariadb": return B("#003545");
                case "postgresql": return B("#336791");
                case "mongodb": return B("#13AA52");
                case "redis": return B("#DC382D");
                case "memcached": return B("#5B8F2E");
                case "mailpit": return B("#0EA5E9");
                case "mailhog": return B("#8B5CF6");
                case "smtp4dev": return B("#0F766E");
                case "tomcat": case "java": return B("#D97706");
                case "nodejs": return B("#5FA04E");
                case "cloudflared": return B("#F38020");
                default: return B("#708090");
            }
        }

        /// <summary>Short text on a service tile, as in the Windows app ("A" Apache, "My" MySQL, "php"); null for a type it does not know.</summary>
        public static string BrandText(string type)
        {
            switch (type)
            {
                case "apache": return "A";
                case "nginx": return "N";
                case "php": return "php";
                case "mysql": return "My";
                case "redis": return "R";
                case "mailpit": return "M";
                case "mariadb": return "Ma";
                case "postgresql": return "Pg";
                case "mongodb": return "Mo";
                case "memcached": return "Mc";
                case "mailhog": return "MH";
                case "smtp4dev": return "S4";
                case "tomcat": return "Tc";
                default: return null;
            }
        }

        static IBrush B(string hex) { return new SolidColorBrush(Color.Parse(hex)); }
    }
}
