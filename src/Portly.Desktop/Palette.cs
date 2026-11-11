using Avalonia.Media;

namespace Portly.Desktop
{
    /// <summary>Same colors as the Windows app (src\Portly\UI\Theme.cs).</summary>
    public sealed class Palette
    {
        public IBrush Bg, Sidebar, SidebarActive, SidebarActiveText, Card, Border, Text, Muted, Accent, AccentText, Hover, Green, Orange, Red, Gray;

        static IBrush B(string hex) { return new SolidColorBrush(Color.Parse(hex)); }

        public static readonly Palette Light = new Palette
        {
            Bg = B("#F4F6FA"), Sidebar = B("#FFFFFF"), SidebarActive = B("#EEF2FF"), SidebarActiveText = B("#1D4ED8"),
            Card = B("#FFFFFF"), Border = B("#E4E7EC"), Text = B("#101828"), Muted = B("#667085"),
            Accent = B("#2563EB"), AccentText = Brushes.White, Hover = B("#F2F4F7"),
            Green = B("#16A34A"), Orange = B("#F59E0B"), Red = B("#DC2626"), Gray = B("#98A2B3"),
        };

        public static readonly Palette Dark = new Palette
        {
            Bg = B("#0F1115"), Sidebar = B("#15181E"), SidebarActive = B("#1E2A44"), SidebarActiveText = B("#93C5FD"),
            Card = B("#1A1E25"), Border = B("#2A303A"), Text = B("#E6E8EC"), Muted = B("#98A2B3"),
            Accent = B("#3B82F6"), AccentText = Brushes.White, Hover = B("#232833"),
            Green = B("#22C55E"), Orange = B("#F59E0B"), Red = B("#F87171"), Gray = B("#4B5563"),
        };

        /// <summary>Brand color of a service tile.</summary>
        public static IBrush Brand(string id)
        {
            switch (id)
            {
                case "php": return B("#777BB4");
                case "apache": return B("#D22128");
                case "nginx": return B("#009639");
                case "mysql": return B("#00758F");
                case "redis": return B("#DC382D");
                case "mailpit": return B("#0EA5E9");
                case "mariadb": return B("#C0765A");
                case "postgresql": return B("#336791");
                case "mongodb": return B("#47A248");
                case "memcached": return B("#4B8BBE");
                case "caddy": return B("#1F88C0");
                case "frankenphp": return B("#A855F7");
                case "tomcat": return B("#D97706");
                default: return B("#64748B");
            }
        }
    }
}
