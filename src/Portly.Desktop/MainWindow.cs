using Th = Portly.Desktop.UI.Theme;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Portly.Core;
using Portly.Desktop.Forms;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;
using Shapes = Avalonia.Controls.Shapes;

namespace Portly.Desktop
{
    /// <summary>
    /// Main window, split like src\Portly\UI\MainForm.cs:
    ///   MainWindow.cs           shell: sidebar, pages, status line, tray, window lifecycle
    ///   MainWindow.Menus.cs     log page, tray menu and "More" menu
    ///   MainWindow.Actions.cs   packages, settings, database, project and certificate actions
    ///   MainWindow.Startup.cs   start-up and background work, updates, document root watcher
    ///   MainWindow.Services.cs  services page
    ///   MainWindow.Sites.cs     projects (sites) page
    /// Members marked "shared" are called across the files; keep their signatures.
    /// </summary>
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }

        readonly bool _startHidden, _startServices;
        bool _exiting, _stopped;
        string _page = "services";
        ContentControl _host;
        TextBlock _status;
        Button _updateBtn, _updateTop;
        UpdateNoticeBar _notices;
        TrayIcon _tray;
        string _trayKey;
        readonly Dictionary<string, WindowIcon> _trayIcons = new Dictionary<string, WindowIcon>();
        readonly Dictionary<string, Button> _nav = new Dictionary<string, Button>();
        readonly Dictionary<string, Control> _pages = new Dictionary<string, Control>();

        public MainWindow() : this(false, false) { }

        public MainWindow(bool startHidden, bool startServices)
        {
            Instance = this;
            _startHidden = startHidden;
            _startServices = startServices;
            Title = "Portly";
            Icon = Ui.AppIcon;
            Width = 1100;
            Height = 740;
            MinWidth = 900;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FlowDirection = I18n.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            ServiceManager.Changed += svc => Ui.Post(() =>
            {
                UpdateRow(svc);
                UpdateGlobal();
                UpdateTray();
                // the web server has just made the site certificates (the HTTPS badges were checked before that), and the
                // "On the network" badge turns orange / green with it
                if (svc == ServiceManager.Web && (svc.State == ServiceState.Running || ShareStore.All().Any(x => LanShare.IsRunning(x.Host)))) FillSites();
            });
            ServiceManager.SitesChanged += () => Ui.Post(FillSites);
            ShareStore.Changed += () => Ui.Post(FillSites); // a share started, stopped or ended by itself
            int jobsShown = 0;
            Jobs.Changed += () => Ui.Post(() => { var n = Jobs.ActiveCountAll(); if (n != jobsShown) { jobsShown = n; FillSites(); } });
            Log.Written += line => Ui.Post(() => AppendLog(line));
            ServiceManager.Php.Problem += text => Ui.Post(() => SetStatus(text));
            Catalog.Changed += () => Ui.Post(UpdateBanner);

            BuildUi();
            if (Program.IsScreenshot) return;

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _debounce.Tick += async (s, e) => { _debounce.Stop(); try { await OnDocRootChanged(); } catch (Exception ex) { Log.Warn(ex.Message); } };
            CreateTray();
            if (Application.Current != null) Application.Current.ActualThemeVariantChanged += OnSystemThemeChanged;
            // startup work runs once the loop is up, whether the window is shown or not (tray start)
            DispatcherTimer.RunOnce(() => { var _ = RunStartup(); }, TimeSpan.FromMilliseconds(200));
        }

        void OnSystemThemeChanged(object sender, EventArgs e)
        {
            if (Settings.Current.Theme != "system" || Application.Current == null) return;
            var wasDark = Th.IsDark;
            Th.Apply(Application.Current, "system");
            if (wasDark != Th.IsDark) RebuildTheme();
        }

        /// <summary>Re-create the window content with the current theme / language.</summary>
        public void RebuildTheme()
        {
            FlowDirection = I18n.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            _trayIcons.Clear();
            _trayKey = null;
            BuildUi();
            UpdateTray();
        }

        void BuildUi()
        {
            _nav.Clear();
            _pages.Clear();
            var root = new Grid { ColumnDefinitions = new ColumnDefinitions("225,*") };
            root.Children.Add(Sidebar());

            _status = Ui.Label(I18n.T("main.ready"), "muted small");
            _status.TextWrapping = TextWrapping.NoWrap;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.Margin = new Thickness(28, 4, 28, 15);
            _host = new ContentControl();
            var content = new DockPanel();
            DockPanel.SetDock(_status, Dock.Bottom);
            content.Children.Add(_status);
            content.Children.Add(_host);
            Grid.SetColumn(content, 1);
            root.Children.Add(content);
            Content = root;

            _pages["services"] = Page(BuildServicesPage());
            _pages["sites"] = Page(BuildSitesPage());
            _pages["log"] = Page(BuildLogPage(), false);
            ShowPage(_page);
            UpdateGlobal();
            FillSites();
            UpdateBanner();
        }

        static Control Page(Control body, bool scroll = true)
        {
            body.Margin = new Thickness(28, 22, 28, 8);
            return scroll ? new ScrollViewer { Content = body } : body;
        }

        Control Sidebar()
        {
            var top = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 12, 12) };
            var brand = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Margin = new Thickness(18, 22, 0, 0), Height = 58 };
            brand.Children.Add(new Image { Source = Ui.AppImage, Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Top });
            var names = Ui.Column(1, Ui.Label("Portly", "brand"), Ui.Label(AppInfo.IsLegacy ? "Legacy · Win 7/8" : "Erdem Software", "muted small"));
            names.Children[1].Margin = new Thickness(2, 0, 0, 0);
            names.VerticalAlignment = VerticalAlignment.Top;
            names.Margin = new Thickness(0, -4, 0, 0);
            Grid.SetColumn(names, 1);
            brand.Children.Add(names);
            top.Children.Add(brand);
            top.Children.Add(Nav("services", I18n.T("nav.services"), "services", () => ShowPage("services")));
            top.Children.Add(Nav("sites", I18n.T("nav.sites"), "globe", () => ShowPage("sites")));
            top.Children.Add(Nav("log", I18n.T("nav.log"), "list", () => ShowPage("log")));
            top.Children.Add(new Border { Height = 16 });
            top.Children.Add(Nav(null, I18n.T("main.packages"), "box", async () => await OpenPackages(null)));
            top.Children.Add(Nav(null, I18n.T("nav.import"), "import", async () => await OpenImport(true)));
            top.Children.Add(Nav(null, I18n.T("main.settings"), "gear", async () => await OpenSettings()));
            Button more = null;
            // its own menu: the tray menu also carries "Show Portly", Start all, Packages, Settings ... which the window already has
            more = Nav(null, I18n.T("main.more"), "more", () => ShowMoreMenu(more));
            top.Children.Add(more);

            var bottom = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 12, 12), VerticalAlignment = VerticalAlignment.Bottom };
            _updateBtn = Ui.Button("", OnBannerClick, "primary", "update");
            _updateBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _updateBtn.Width = 198;
            _updateBtn.Height = 38;
            _updateBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
            _updateBtn.Margin = new Thickness(0, 0, 0, 4);
            _updateBtn.IsVisible = false;
            bottom.Children.Add(_updateBtn);
            var donate = Nav(null, I18n.T("main.donate"), "heart", async () => await ShowDonate());
            donate.Foreground = Ui.Heart;
            bottom.Children.Add(donate);
            var ver = Ui.Label("v" + AppInfo.VersionText + " · " + OsLabel(), "muted small");
            ver.Margin = new Thickness(12, 10, 0, 4);
            bottom.Children.Add(ver);

            var grid = new Grid();
            grid.Children.Add(top);
            grid.Children.Add(bottom);
            var side = new Border { Child = grid };
            side.Classes.Add("sidebar");
            return side;
        }

        static string OsLabel()
        {
            if (OS.IsMac) return "macOS";
            if (OS.IsLinux) return "Linux";
            return AppInfo.IsLegacy ? "Windows 7/8" : "Windows 10/11";
        }

        Button Nav(string page, string text, string glyph, Func<Task> click)
        {
            var b = Ui.Button(text, click, "nav side", glyph);
            if (page != null) _nav[page] = b;
            return b;
        }

        Button Nav(string page, string text, string glyph, Action click)
        {
            return Nav(page, text, glyph, () => { click(); return Task.CompletedTask; });
        }

        static void SetButton(Button b, string text, string glyph)
        {
            b.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center,
                Children = { new Glyph { Name_ = glyph, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            };
            Avalonia.Automation.AutomationProperties.SetName(b, text);
        }

        // =====================================================================
        // shared: called from every part of the window
        // =====================================================================

        /// <summary>shared: switch to "services", "sites" or "log".</summary>
        public void ShowPage(string page)
        {
            Control c;
            if (!_pages.TryGetValue(page, out c)) return;
            _page = page;
            _host.Content = c;
            foreach (var kv in _nav)
            {
                if (kv.Key == page) kv.Value.Classes.Add("selected");
                else kv.Value.Classes.Remove("selected");
            }
            if (page == "log") ScrollLogToEnd();
        }

        /// <summary>shared: one line under the page ("Apache started", "Backing up ...").</summary>
        public void SetStatus(string text) { if (_status != null) _status.Text = text; }

        /// <summary>shared: bring the window up (from the tray, a notification or another Portly start).</summary>
        public void ShowFromTray()
        {
            if (!IsVisible) Show();
            ShowInTaskbar = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>Called by the single-instance signal (Portly started a second time).</summary>
        public void BringToFrontFromOtherInstance()
        {
            Ui.Post(() =>
            {
                ShowFromTray();
                Topmost = true;
                Topmost = false;
            });
        }

        // =====================================================================
        // Tray
        // =====================================================================
        void CreateTray()
        {
            var menu = new NativeMenu();
            menu.Opening += (s, e) => FillNativeMenu(menu, BuildMenu(true));
            FillNativeMenu(menu, BuildMenu(true));
            _tray = new TrayIcon { Icon = TrayIconFor(""), ToolTipText = "Portly", Menu = menu, IsVisible = true };
            _tray.Clicked += (s, e) => ShowFromTray();
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _tray });
        }

        void UpdateTray()
        {
            if (_tray == null) return;
            var web = ServiceManager.Web;
            var key = web.State == ServiceState.Running ? "green" : web.State == ServiceState.Error ? "red" : "";
            if (key != _trayKey)
            {
                _trayKey = key;
                _tray.Icon = TrayIconFor(key);
            }
            var names = ServiceManager.Rows.Where(s => s.State == ServiceState.Running).Select(s => s.Title).ToList();
            _tray.ToolTipText = "Portly" + (names.Count > 0 ? " - " + string.Join(", ", names) : "");
        }

        /// <summary>App icon with a small status dot: green = web server running, red = web server failed.</summary>
        WindowIcon TrayIconFor(string key)
        {
            WindowIcon icon;
            if (_trayIcons.TryGetValue(key, out icon)) return icon;
            icon = Ui.AppIcon;
            if (key != "" && Ui.AppImage != null)
            {
                try
                {
                    const int size = 64;
                    var dot = key == "green" ? Palette.Light.Green : Palette.Light.Red;
                    var panel = new Grid { Width = size, Height = size };
                    panel.Children.Add(new Image { Source = Ui.AppImage, Width = size, Height = size });
                    panel.Children.Add(new Shapes.Ellipse
                    {
                        Width = 30, Height = 30, Fill = new SolidColorBrush(dot), Stroke = Brushes.White, StrokeThickness = 4,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                    });
                    panel.Measure(new Size(size, size));
                    panel.Arrange(new Rect(0, 0, size, size));
                    var bmp = new RenderTargetBitmap(new PixelSize(size, size));
                    bmp.Render(panel);
                    icon = new WindowIcon(bmp);
                }
                catch (Exception ex) { Log.Warn("Tray icon: " + ex.Message); }
            }
            _trayIcons[key] = icon;
            return icon;
        }

        void DisposeTray()
        {
            if (_tray == null) return;
            try { _tray.IsVisible = false; _tray.Dispose(); } catch { }
            _tray = null;
        }

        // =====================================================================
        // Window lifecycle
        // =====================================================================
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (_exiting) { base.OnClosing(e); return; }
            if (e.CloseReason == WindowCloseReason.OSShutdown || e.CloseReason == WindowCloseReason.ApplicationShutdown)
            {
                _exiting = true;
                StopOnShutdown();
                DisposeTray();
                base.OnClosing(e);
                return;
            }
            e.Cancel = true;
            base.OnClosing(e);
            if (Settings.Current.CloseToTray && !e.IsProgrammatic && _tray != null)
            {
                Ui.Post(Hide);
                // only the very first time: later it would sit over the tray icon right when the user wants to use it
                if (!Settings.Current.TrayHintShown)
                {
                    Settings.Current.TrayHintShown = true;
                    Settings.Current.Save();
                    Log.Info(I18n.T("tray.hint"));
                }
                return;
            }
            var _ = ExitAsync();
        }

        /// <summary>The process is going away (shutdown, Quit from the dock menu): stop the services before it does.</summary>
        public void StopOnShutdown()
        {
            if (_stopped) return;
            _stopped = true;
            try
            {
                Share.StopAll();
                LanShare.StopAll();
            }
            catch { }
            // service mode: the Windows service keeps running without the window
            if (!(OS.IsWindows && ServiceMode.IsController))
                try { Task.Run(() => ServiceManager.StopAllAsync()).Wait(15000); } catch { }
        }

        public async Task ExitAsync()
        {
            if (_exiting) return;
            _exiting = true;
            _stopped = true;
            Share.StopAll();
            LanShare.StopAll();
            SetStatus(I18n.T("main.stopping"));
            IsEnabled = false;
            // service mode: the Windows service keeps running without the window
            if (!(OS.IsWindows && ServiceMode.IsController)) await Task.WhenAny(ServiceManager.StopAllAsync(), Task.Delay(30000));
            ExitApp();
        }

        void ExitApp()
        {
            _exiting = true;
            DisposeTray();
            var desktop = Application.Current == null ? null : Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop != null) desktop.Shutdown();
            else Close();
        }
    }
}
