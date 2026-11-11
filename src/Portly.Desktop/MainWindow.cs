using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Portly.Core;
using Portly.Services;

namespace Portly.Desktop
{
    public class MainWindow : Window
    {
        sealed class Row
        {
            public Service Svc;
            public TextBlock Detail, Status, Error;
            public Border StatusPill;
            public Ellipse Dot;
            public ToggleSwitch Toggle;
        }

        readonly Palette P;
        readonly Dictionary<Service, Row> _rows = new Dictionary<Service, Row>();
        TextBlock _subtitle;
        Button _startAll;
        bool _updating, _busyAll;

        public MainWindow()
        {
            var dark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
            P = dark ? Palette.Dark : Palette.Light;
            Title = "Portly";
            Width = 1100;
            Height = 720;
            MinWidth = 760;
            MinHeight = 480;
            Background = P.Bg;
            FlowDirection = I18n.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Icon = LoadIcon();

            var root = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
            var sidebar = Sidebar();
            Grid.SetColumn(sidebar, 0);
            root.Children.Add(sidebar);
            var page = new ScrollViewer { Content = ServicesPage() };
            Grid.SetColumn(page, 1);
            root.Children.Add(page);
            Content = root;

            ServiceManager.Changed += s => Dispatcher.UIThread.Post(() => Refresh(s));
            Refresh(null);
        }

        static WindowIcon LoadIcon()
        {
            using (var s = typeof(MainWindow).Assembly.GetManifestResourceStream("portly.png"))
                return s == null ? null : new WindowIcon(new Bitmap(s));
        }

        Control Sidebar()
        {
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(14, 18, 14, 18) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 0, 0, 22) };
            using (var s = typeof(MainWindow).Assembly.GetManifestResourceStream("portly.png"))
                if (s != null) brand.Children.Add(new Image { Source = new Bitmap(s), Width = 30, Height = 30 });
            brand.Children.Add(new TextBlock { Text = "Portly", FontSize = 19, FontWeight = FontWeight.Bold, Foreground = P.Text, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(brand);
            stack.Children.Add(NavItem(I18n.T("nav.services"), true));
            stack.Children.Add(NavItem(I18n.T("nav.sites"), false));
            stack.Children.Add(NavItem(I18n.T("nav.log"), false));

            var foot = new TextBlock
            {
                Text = "v" + AppInfo.VersionText + " · " + OS.Name + " " + OS.Arch,
                Foreground = P.Muted, FontSize = 12, Margin = new Thickness(20, 0, 0, 16), VerticalAlignment = VerticalAlignment.Bottom,
            };
            var dock = new Grid();
            dock.Children.Add(stack);
            dock.Children.Add(foot);
            return new Border { Background = P.Sidebar, BorderBrush = P.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = dock };
        }

        Control NavItem(string text, bool active)
        {
            return new Border
            {
                Background = active ? P.SidebarActive : Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9),
                Child = new TextBlock { Text = text, FontSize = 14, FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal, Foreground = active ? P.SidebarActiveText : P.Text },
            };
        }

        Control ServicesPage()
        {
            var page = new StackPanel { Spacing = 14, Margin = new Thickness(28, 24, 28, 28) };

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var titles = new StackPanel { Spacing = 2 };
            titles.Children.Add(new TextBlock { Text = I18n.T("nav.services"), FontSize = 24, FontWeight = FontWeight.Bold, Foreground = P.Text });
            _subtitle = new TextBlock { Foreground = P.Muted, FontSize = 13 };
            titles.Children.Add(_subtitle);
            header.Children.Add(titles);
            _startAll = new Button
            {
                Background = P.Accent, Foreground = P.AccentText, Padding = new Thickness(18, 9), CornerRadius = new CornerRadius(8),
                FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            };
            _startAll.Click += async (s, e) => await StartStopAll();
            Grid.SetColumn(_startAll, 1);
            header.Children.Add(_startAll);
            page.Children.Add(header);

            page.Children.Add(new TextBlock { Text = Upper(I18n.T("main.quickAccess")), Foreground = P.Muted, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            var tiles = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var key in new[] { "main.web", "main.database", "main.mail", "main.terminal", "main.root", "main.newProject" })
                tiles.Children.Add(Tile(I18n.T(key)));
            page.Children.Add(tiles);

            foreach (var svc in ServiceManager.Rows) page.Children.Add(ServiceCard(svc));
            return page;
        }

        Control Tile(string text)
        {
            return new Border
            {
                Background = P.Card, BorderBrush = P.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12), Margin = new Thickness(0, 0, 10, 10), MinWidth = 120,
                Child = new TextBlock { Text = text, Foreground = P.Text, FontSize = 13, FontWeight = FontWeight.Medium },
            };
        }

        Control ServiceCard(Service svc)
        {
            var row = new Row { Svc = svc };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 14 };

            var initials = svc.Title.Length >= 2 ? svc.Title.Substring(0, 2) : svc.Title;
            var tile = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = Palette.Brand(svc.PackageType),
                Child = new TextBlock { Text = initials, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            grid.Children.Add(tile);

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = svc.Title, Foreground = P.Text, FontSize = 15, FontWeight = FontWeight.SemiBold });
            row.Detail = new TextBlock { Foreground = P.Muted, FontSize = 12.5 };
            text.Children.Add(row.Detail);
            row.Error = new TextBlock { Foreground = P.Red, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            text.Children.Add(row.Error);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            row.Dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            row.Status = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var pill = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            pill.Children.Add(row.Dot);
            pill.Children.Add(row.Status);
            row.StatusPill = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 4), Background = P.Hover, Child = pill, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(row.StatusPill, 2);
            grid.Children.Add(row.StatusPill);

            row.Toggle = new ToggleSwitch { OnContent = null, OffContent = null, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            AutomationName(row.Toggle, svc.Title);
            row.Toggle.IsCheckedChanged += async (s, e) => await Toggle(row);
            Grid.SetColumn(row.Toggle, 3);
            grid.Children.Add(row.Toggle);

            _rows[svc] = row;
            return new Border
            {
                Background = P.Card, BorderBrush = P.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 12), Child = grid,
            };
        }

        /// <summary>Upper case by the UI language: Turkish "Hızlı erişim" -> "HIZLI ERİŞİM".</summary>
        static string Upper(string text)
        {
            try { return text.ToUpper(new System.Globalization.CultureInfo(I18n.Code)); }
            catch { return text.ToUpperInvariant(); }
        }

        static void AutomationName(Control c, string name) { Avalonia.Automation.AutomationProperties.SetName(c, name); }

        async Task Toggle(Row row)
        {
            if (_updating) return;
            bool on = row.Toggle.IsChecked == true;
            try
            {
                if (on) await ServiceManager.StartAsync(row.Svc);
                else await ServiceManager.StopAsync(row.Svc);
            }
            catch (Exception ex) { Log.Error("Toggle " + row.Svc.Id, ex); }
            Refresh(row.Svc);
        }

        async Task StartStopAll()
        {
            if (_busyAll) return;
            _busyAll = true;
            _startAll.IsEnabled = false;
            try
            {
                if (ServiceManager.AnyBulkActive) await ServiceManager.StopAllAsync();
                else await ServiceManager.StartAllAsync();
            }
            catch (Exception ex) { Log.Error("Start / stop all", ex); }
            finally
            {
                _busyAll = false;
                _startAll.IsEnabled = true;
                Refresh(null);
            }
        }

        void Refresh(Service only)
        {
            _updating = true;
            try
            {
                foreach (var row in _rows.Values)
                {
                    if (only != null && row.Svc != only) continue;
                    var svc = row.Svc;
                    var parts = new List<string>();
                    parts.Add(svc.IsInstalled ? svc.VersionLabel : I18n.T("svc.notInstalled"));
                    if (svc.PortText != "") parts.Add(svc.PortText);
                    row.Detail.Text = string.Join("  ·  ", parts);

                    string status;
                    IBrush color;
                    switch (svc.State)
                    {
                        case ServiceState.Running: status = I18n.T("svc.running"); color = P.Green; break;
                        case ServiceState.Starting: status = I18n.T("svc.starting"); color = P.Orange; break;
                        case ServiceState.Stopping: status = I18n.T("svc.stopping"); color = P.Orange; break;
                        case ServiceState.Error: status = I18n.T("svc.error"); color = P.Red; break;
                        default: status = I18n.T("svc.stopped"); color = P.Gray; break;
                    }
                    row.Status.Text = status;
                    row.Status.Foreground = color;
                    row.Dot.Fill = color;
                    row.Error.Text = svc.State == ServiceState.Error ? svc.LastError : "";
                    row.Error.IsVisible = svc.State == ServiceState.Error && !string.IsNullOrEmpty(svc.LastError);
                    row.Toggle.IsChecked = svc.IsActive;
                    row.Toggle.IsEnabled = svc.IsInstalled && svc.State != ServiceState.Starting && svc.State != ServiceState.Stopping;
                }
                int running = _rows.Keys.Count(s => s.State == ServiceState.Running);
                int installed = _rows.Keys.Count(s => s.IsInstalled);
                _subtitle.Text = running + " / " + installed + " " + I18n.T("svc.running").ToLowerInvariant();
                _startAll.Content = I18n.T(ServiceManager.AnyBulkActive ? "main.stopAll" : "main.startAll");
            }
            finally { _updating = false; }
        }
    }
}
