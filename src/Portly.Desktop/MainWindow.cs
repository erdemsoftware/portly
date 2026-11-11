using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;

namespace Portly.Desktop
{
    /// <summary>
    /// Main window, split like src\Portly\UI\MainForm.cs:
    ///   MainWindow.cs           shell: sidebar, pages, status line, tray, menus, startup and background work, log page
    ///   MainWindow.Services.cs  services page
    ///   MainWindow.Sites.cs     projects (sites) page
    /// Members marked "shared" are called across the files; keep their signatures.
    /// </summary>
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }

        readonly bool _startHidden, _startServices;
        string _page = "services";
        ContentControl _host;
        TextBlock _status;
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

            ServiceManager.Changed += s => Ui.Post(() => { UpdateRow(s); UpdateGlobal(); });
            ServiceManager.SitesChanged += () => Ui.Post(FillSites);
            BuildUi();
        }

        void BuildUi()
        {
            var root = new Grid { ColumnDefinitions = new ColumnDefinitions("224,*") };
            root.Children.Add(Sidebar());

            _status = Ui.Label(I18n.T("main.ready"), "muted small");
            _status.TextWrapping = TextWrapping.NoWrap;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.Margin = new Thickness(28, 4, 28, 8);
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
        }

        static Control Page(Control body, bool scroll = true)
        {
            body.Margin = new Thickness(28, 22, 28, 8);
            return scroll ? new ScrollViewer { Content = body } : body;
        }

        Control Sidebar()
        {
            var top = new StackPanel { Spacing = 4, Margin = new Thickness(12, 18, 12, 12) };
            var brand = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(6, 0, 0, 20) };
            brand.Children.Add(new Image { Source = Ui.AppImage, Width = 36, Height = 36 });
            var names = Ui.Column(0, Ui.Label("Portly", "subtitle"), Ui.Label("Erdem Software", "muted small"));
            names.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(names, 1);
            brand.Children.Add(names);
            top.Children.Add(brand);
            top.Children.Add(Nav("services", I18n.T("nav.services"), "services", () => ShowPage("services")));
            top.Children.Add(Nav("sites", I18n.T("nav.sites"), "globe", () => ShowPage("sites")));
            top.Children.Add(Nav("log", I18n.T("nav.log"), "list", () => ShowPage("log")));
            top.Children.Add(new Border { Height = 12 });
            top.Children.Add(Nav(null, I18n.T("main.packages"), "box", async () => await OpenPackages(null)));
            top.Children.Add(Nav(null, I18n.T("nav.import"), "import", async () => await OpenImport(true)));
            top.Children.Add(Nav(null, I18n.T("main.settings"), "gear", async () => await OpenSettings()));

            var bottom = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 12, 14), VerticalAlignment = VerticalAlignment.Bottom };
            var donate = Nav(null, I18n.T("main.donate"), "heart", async () => await ShowDonate());
            donate.Foreground = Ui.Heart;
            bottom.Children.Add(donate);
            bottom.Children.Add(Ui.Label("v" + AppInfo.VersionText + " · " + OsLabel(), "muted small"));
            ((Control)bottom.Children[1]).Margin = new Thickness(12, 6, 0, 0);

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
            return "Windows";
        }

        Button Nav(string page, string text, string glyph, Func<Task> click)
        {
            var b = Ui.Button(text, click, "nav", glyph);
            if (page != null) _nav[page] = b;
            return b;
        }

        Button Nav(string page, string text, string glyph, Action click)
        {
            return Nav(page, text, glyph, () => { click(); return Task.CompletedTask; });
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
        }

        /// <summary>shared: one line under the page ("Apache started", "Backing up ...").</summary>
        public void SetStatus(string text) { if (_status != null) _status.Text = text; }

        /// <summary>shared: bring the window up (from the tray, a notification or another Portly start).</summary>
        public void ShowFromTray()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        // ---- shell-owned (MainWindow.cs): placeholders until the shell is ported ----

        /// <summary>shared: package manager; type preselects a package type, firstRun shows the welcome set.</summary>
        public Task OpenPackages(string type, bool firstRun = false, IEnumerable<PackageEntry> preselect = null) { return NotYet("packages"); }

        /// <summary>shared: new project wizard (template "wordpress", "laravel" ...; parent for a sub-site).</summary>
        public Task NewProject(string template = null, string parent = null) { return NotYet("new project"); }

        public Task OpenSettings() { return NotYet("settings"); }
        public Task OpenImport(bool interactive) { return NotYet("import"); }
        public Task ShowDonate() { return NotYet("donate"); }

        /// <summary>shared: "update available" / notices in the sidebar.</summary>
        public void UpdateBanner() { }

        Control BuildLogPage() { return Ui.Label(I18n.T("nav.log"), "title"); }

        Task NotYet(string what) { return Dialogs.Info(this, what + ": not ported yet"); }
    }
}
