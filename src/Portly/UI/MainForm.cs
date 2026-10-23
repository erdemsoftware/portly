using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    public class MainForm : Form
    {
        class Row
        {
            public Service Svc;
            public Card Card;
            public BrandTile Tile;
            public Label Name, Detail, Error;
            public LinkLabel Fix;
            public Pill Status;
            public Pill Sec;       // end of life / security fix available (security.json)
            public ComboBox Version;
            public ToggleSwitch Toggle;
            public ModernButton Get;
            public bool ShowGet;   // Control.Visible is false while the page is hidden, so keep our own flag
        }

        readonly bool _startHidden;
        readonly bool _startServices;
        bool _exiting, _trayHintShown, _busyAll;
        NotifyIcon _tray;
        ContextMenuStrip _menu;
        Panel _content, _pageServices, _pageSites, _pageLog;
        ModernButton _navServices, _navSites, _navLog, _startAll, _updateBtn;
        Label _status, _servicesSub, _sitesSub;
        readonly List<Row> _rows = new List<Row>();
        FlowLayoutPanel _siteFlow;
        UpdateNoticeBar _notices;
        RichTextBox _log;
        readonly List<string> _logLines = new List<string>();
        FileSystemWatcher _watcher;
        System.Windows.Forms.Timer _debounce;
        string _page = "services";

        public MainForm(bool startHidden, bool startServices)
        {
            _startHidden = startHidden;
            _startServices = startServices;
            Ui.Apply(this);
            Text = "Portly";
            Size = Ui.S(1020, 740);
            MinimumSize = Ui.S(900, 600);
            StartPosition = FormStartPosition.CenterScreen;

            _menu = new ContextMenuStrip();
            _menu.Opening += (s, e) => { BuildMenu(_menu.Items); e.Cancel = false; };
            _tray = new NotifyIcon { Icon = Ui.TrayIcon(null), Text = "Portly", Visible = true, ContextMenuStrip = _menu };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

            _debounce = new System.Windows.Forms.Timer { Interval = 2000 };
            _debounce.Tick += async (s, e) => { _debounce.Stop(); await OnDocRootChanged(); };

            ServiceManager.Changed += svc => SafeInvoke(() => { UpdateRow(svc); UpdateGlobal(); });
            ServiceManager.SitesChanged += () => SafeInvoke(FillSites);
            int jobsShown = 0;
            Jobs.Changed += () => SafeInvoke(() => { var n = Jobs.ActiveCountAll(); if (n != jobsShown) { jobsShown = n; FillSites(); } });
            Log.Written += line => SafeInvoke(() => AppendLog(line));
            Catalog.Changed += () => SafeInvoke(UpdateBanner);
            SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;

            BuildUi();
            if (_startHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }

            // startup work runs once the message loop is up, whether the window is shown or not (tray start)
            var boot = new System.Windows.Forms.Timer { Interval = 200 };
            boot.Tick += (s, e) => { boot.Stop(); boot.Dispose(); Startup(); };
            boot.Start();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        void SafeInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); } catch { }
        }

        void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (Settings.Current.Theme != "system") return;
            var wasDark = Theme.IsDark;
            Theme.Load("system");
            if (wasDark != Theme.IsDark) SafeInvoke(RebuildTheme);
        }

        /// <summary>Re-create the window content with the current theme / language.</summary>
        public void RebuildTheme()
        {
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            Ui.ApplyDirection(this);
            Theme.ApplyTitleBar(this);
            BuildUi();
        }

        /// <summary>Moved to a monitor with another scale (per-monitor V2): rebuild at the new DPI so everything stays sharp.</summary>
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            e.Cancel = true; // WinForms would only stretch the old controls; we rebuild instead
            Bounds = e.SuggestedRectangle;
            ApplyDpi(e.DeviceDpiNew);
            base.OnDpiChanged(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // opened on a monitor whose scale differs from the primary one
            if (DeviceDpi != Ui.Dpi) BeginInvoke((Action)(() => ApplyDpi(DeviceDpi)));
        }

        public void ApplyDpi(int dpi)
        {
            if (!Ui.SetDpi(dpi)) return;
            Font = Ui.Font;
            MinimumSize = Ui.S(900, 600);
            var old = Controls.Cast<Control>().ToList();
            BuildUi();
            foreach (var c in old) c.Dispose();
        }

        // =====================================================================
        // Layout
        // =====================================================================
        void BuildUi()
        {
            SuspendLayout();
            Controls.Clear();
            _rows.Clear();
            BackColor = Ui.Bg;
            StyleMenu(_menu);

            // ---------------- sidebar ----------------
            var side = new Panel { Dock = DockStyle.Left, Width = Ui.S(224), BackColor = Theme.Current.Sidebar, Padding = Ui.P(12, 0, 12, 12) };
            var logo = new PictureBox { Image = Ui.AppImage(Ui.S(36) > 40 ? 48 : 32), SizeMode = PictureBoxSizeMode.Zoom, Size = Ui.S(36, 36), Location = new Point(Ui.S(18), Ui.S(22)), BackColor = Color.Transparent };
            var brand = Ui.Label("Portly", Ui.Brand);
            brand.Location = new Point(Ui.S(62), Ui.S(18));
            var edition = Ui.Label(AppInfo.IsLegacy ? "Legacy · Win 7/8" : "Erdem Software", Ui.Small, Ui.Muted);
            edition.Location = new Point(Ui.S(64), Ui.S(44));
            var logoArea = new Panel { Dock = DockStyle.Top, Height = Ui.S(84), BackColor = Color.Transparent };
            logoArea.Controls.AddRange(new Control[] { logo, brand, edition });

            var nav = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
            _navServices = NavButton(I18n.T("nav.services"), "services", (s, e) => ShowPage("services"));
            _navSites = NavButton(I18n.T("nav.sites"), "globe", (s, e) => ShowPage("sites"));
            _navLog = NavButton(I18n.T("nav.log"), "list", (s, e) => ShowPage("log"));
            nav.Controls.Add(_navServices);
            nav.Controls.Add(_navSites);
            nav.Controls.Add(_navLog);
            nav.Controls.Add(new Panel { Height = Ui.S(14), Width = Ui.S(10), BackColor = Color.Transparent });
            nav.Controls.Add(NavButton(I18n.T("main.packages"), "box", (s, e) => OpenPackages(null)));
            nav.Controls.Add(NavButton(I18n.T("nav.import"), "import", (s, e) => OpenImport(true)));
            nav.Controls.Add(NavButton(I18n.T("main.settings"), "gear", (s, e) => OpenSettings()));
            var more = NavButton(I18n.T("main.menu"), "more", null);
            more.Click += (s, e) => { BuildMenu(_menu.Items); _menu.Show(more, new Point(more.Width, 0)); };
            nav.Controls.Add(more);

            var sideBottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
            _updateBtn = Ui.Button("", (s, e) => OnBannerClick(), true, "update");
            _updateBtn.AutoSize = false;
            _updateBtn.Size = new Size(Ui.S(198), Ui.S(38));
            _updateBtn.Visible = false;
            _updateBtn.Margin = Ui.P(0, 0, 0, 8);
            var donate = NavButton(I18n.T("main.donate"), "heart", (s, e) => ShowDonate());
            donate.CustomColor = Ui.Heart;
            var ver = Ui.Label("v" + AppInfo.VersionText + " · " + (AppInfo.IsLegacy ? "Windows 7/8" : "Windows 10/11"), Ui.Small, Ui.Muted);
            ver.Margin = Ui.P(12, 6, 0, 0);
            sideBottom.Controls.Add(_updateBtn);
            sideBottom.Controls.Add(donate);
            sideBottom.Controls.Add(ver);

            side.Controls.Add(sideBottom);
            side.Controls.Add(nav);
            side.Controls.Add(logoArea);
            nav.BringToFront();
            Controls.Add(side);
            Controls.Add(new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Ui.Line });

            // ---------------- content ----------------
            _content = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = Ui.P(28, 22, 28, 10) };
            _status = new Label { Dock = DockStyle.Bottom, Height = Ui.S(24), ForeColor = Ui.Muted, Font = Ui.Small, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = I18n.T("main.ready") };
            _pageServices = BuildServicesPage();
            _pageSites = BuildSitesPage();
            _pageLog = BuildLogPage();
            _content.Controls.Add(_pageServices);
            _content.Controls.Add(_pageSites);
            _content.Controls.Add(_pageLog);
            _content.Controls.Add(_status);
            Controls.Add(_content);
            _content.BringToFront();

            ResumeLayout(true);
            Ui.StyleTree(this);
            ShowPage(_page);
            foreach (var r in _rows) UpdateRow(r.Svc);
            UpdateGlobal();
            FillSites();
            UpdateBanner();
        }

        ModernButton NavButton(string text, string glyph, EventHandler click)
        {
            var b = new ModernButton
            {
                Text = text,
                Glyph = glyph,
                Kind = ButtonKind.Nav,
                AutoSize = false,
                Size = new Size(Ui.S(198), Ui.S(40)),
                Padding = Ui.P(14, 0, 8, 0),
                Margin = Ui.P(0, 0, 0, 4),
                Font = Ui.Font,
            };
            if (click != null) b.Click += click;
            return b;
        }

        void ShowPage(string page)
        {
            _page = page;
            _pageServices.Visible = page == "services";
            _pageSites.Visible = page == "sites";
            _pageLog.Visible = page == "log";
            _navServices.Active = page == "services";
            _navSites.Active = page == "sites";
            _navLog.Active = page == "log";
            foreach (var b in new[] { _navServices, _navSites, _navLog }) b.Invalidate();
        }

        static Panel PageHeader(string title, out Label subtitle, params Control[] actions)
        {
            var h = new Panel { Dock = DockStyle.Top, Height = Ui.S(74), BackColor = Color.Transparent };
            var t = Ui.Label(title, Ui.Title);
            t.Location = new Point(0, 0);
            subtitle = Ui.Label("", Ui.Font, Ui.Muted);
            subtitle.Location = new Point(Ui.S(2), Ui.S(38));
            subtitle.AutoSize = false;
            subtitle.AutoEllipsis = true;
            subtitle.Size = new Size(Ui.S(420), Ui.S(22));
            subtitle.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent, Padding = Ui.P(0, 6, 0, 0) };
            foreach (var a in actions) { a.Margin = Ui.P(8, 0, 0, 0); flow.Controls.Add(a); }
            h.Controls.Add(flow);
            h.Controls.Add(t);
            h.Controls.Add(subtitle);
            return h;
        }

        // ---------------- services page ----------------
        Panel BuildServicesPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, AutoScroll = true };
            _startAll = Ui.Button(I18n.T("main.startAll"), async (s, e) => await ToggleAll(), true, "play");
            _startAll.MinimumSize = new Size(Ui.S(170), Ui.S(40));
            _startAll.Font = Ui.Bold;
            ModernButton choose = null;
            choose = Ui.Button(I18n.T("main.chooseServices"), (s, e) => ChooseServices(choose), false, "list");
            choose.MinimumSize = new Size(0, Ui.S(40));
            var header = PageHeader(I18n.T("nav.services"), out _servicesSub, _startAll, choose);

            var list = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Color.Transparent };
            list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var svc in ServiceManager.Rows)
            {
                var row = BuildServiceCard(svc);
                row.Card.Visible = IsServiceShown(svc);
                list.Controls.Add(row.Card);
                _rows.Add(row);
            }

            var quickTitle = Ui.Label(Ui.Upper(I18n.T("main.quickAccess")), Ui.Small, Ui.Muted);
            quickTitle.Dock = DockStyle.Top;
            quickTitle.Padding = Ui.P(2, 18, 0, 8);
            var tiles = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent, Padding = Ui.P(0, 0, 0, 8) };
            tiles.Controls.Add(Tile(I18n.T("main.web"), "globe", (s, e) => OpenLocalhost()));
            ModernButton dbTile = null;
            dbTile = Tile(I18n.T("main.database"), "database", (s, e) => OpenDatabaseTool(dbTile));
            tiles.Controls.Add(dbTile);
            tiles.Controls.Add(Tile(I18n.T("main.mail"), "mail", (s, e) => OpenMailpit()));
            tiles.Controls.Add(Tile(I18n.T("main.terminal"), "terminal", (s, e) => Terminal.Open()));
            tiles.Controls.Add(Tile(I18n.T("main.root"), "folder", (s, e) => WinUtil.OpenFolder(Settings.Current.DocumentRoot)));
            tiles.Controls.Add(Tile("WordPress", "globe", (s, e) => NewProject("wordpress")));
            tiles.Controls.Add(Tile(I18n.T("main.newProject"), "plus", (s, e) => NewProject()));
            if (TomcatService.Enabled)
            {
                ModernButton tcTile = null;
                tcTile = Tile("Tomcat", "globe", (s, e) =>
                {
                    if (!ServiceManager.Tomcat.IsInstalled) { OpenPackages("tomcat"); return; }
                    var m = new ContextMenuStrip();
                    StyleMenu(m);
                    TomcatUi.AddItems(m.Items);
                    m.Show(tcTile, new Point(0, tcTile.Height));
                });
                tiles.Controls.Add(tcTile);
            }

            page.Controls.Add(tiles);
            page.Controls.Add(quickTitle);
            page.Controls.Add(list);
            // "PHP 8.6.0 is out" cards between the header and the services
            _notices = new UpdateNoticeBar { ApplyVersionChange = ApplyVersionChange, OpenPackages = t => OpenPackages(t), Changed = () => { RefreshVersions(); UpdateBanner(); } };
            page.Controls.Add(_notices);
            page.Controls.Add(header);
            return page;
        }

        /// <summary>Extra databases and caches start hidden until they are installed; the user's own choice wins.</summary>
        static bool IsServiceShown(Service svc)
        {
            bool optional = svc.Id == "postgresql" || svc.Id == "mongodb" || svc.Id == "memcached";
            return Settings.Current.IsShown(svc.Id, !optional || svc.IsInstalled);
        }

        /// <summary>Checklist of the service cards shown on the main window; it stays open while ticking.</summary>
        void ChooseServices(Control anchor)
        {
            var m = new ContextMenuStrip { Renderer = new MenuRenderer() };
            foreach (var r in _rows)
            {
                var row = r;
                var item = new ToolStripMenuItem(DisplayName(row.Svc)) { Checked = row.Card.Visible, CheckOnClick = true };
                item.CheckedChanged += async (s, e) =>
                {
                    Settings.Current.SetShown(row.Svc.Id, item.Checked);
                    Settings.Current.Save();
                    row.Card.Visible = item.Checked;
                    // a hidden service would keep running out of sight
                    if (!item.Checked && row.Svc.IsActive) await row.Svc.StopAsync();
                };
                m.Items.Add(item);
            }
            m.Closing += (s, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
            m.Closed += (s, e) => BeginInvoke((Action)m.Dispose);
            m.Show(anchor, new Point(0, anchor.Height));
        }

        ModernButton Tile(string text, string glyph, EventHandler click)
        {
            var b = new ModernButton { Text = text, Glyph = glyph, Kind = ButtonKind.Tile, AutoSize = true, Margin = Ui.P(0, 0, 10, 10), Font = Ui.Font, Radius = 12 };
            b.Click += click;
            return b;
        }

        Row BuildServiceCard(Service svc)
        {
            var row = new Row { Svc = svc };
            var card = new Card { Height = Ui.S(72), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Ui.P(0, 0, 0, 10), Radius = 12 };
            row.Card = card;
            row.Tile = BrandTile.For(svc.Id == "web" || svc.Id == "mailpit" || svc.Id == "mysql" ? svc.PackageType : svc.Id);
            row.Tile.Location = new Point(Ui.S(18), Ui.S(16));
            row.Name = Ui.Label(DisplayName(svc), Ui.Subtitle);
            row.Name.Location = new Point(Ui.S(72), Ui.S(13));
            row.Detail = Ui.Label("", Ui.Font, Ui.Muted);
            row.Detail.Location = new Point(Ui.S(73), Ui.S(39));
            row.Error = new Label { ForeColor = Ui.Red, BackColor = Color.Transparent, AutoSize = false, AutoEllipsis = true, Location = new Point(Ui.S(73), Ui.S(68)), Height = Ui.S(36), Visible = false, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            row.Fix = Ui.Link(I18n.T("svc.fix"), async () => await FixPorts(new[] { svc }));
            row.Fix.Visible = false;
            row.Fix.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            row.Toggle = new ToggleSwitch { Anchor = AnchorStyles.Top | AnchorStyles.Right, AccessibleName = svc.Title };
            row.Toggle.Click += async (s, e) => await OnToggle(row);
            row.Status = new Pill { Anchor = AnchorStyles.Top | AnchorStyles.Right };
            row.Sec = new Pill { Anchor = AnchorStyles.Top | AnchorStyles.Right, Visible = false, Cursor = Cursors.Hand };
            row.Sec.Click += async (s, e) => await OnSecurityPill(row);
            row.Version = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(130), DropDownWidth = Ui.S(280), Anchor = AnchorStyles.Top | AnchorStyles.Right, AccessibleName = svc.Title + " " + I18n.T("col.version") };
            FillVersions(row);
            row.Version.SelectionChangeCommitted += async (s, e) => await OnVersionChanged(row);
            row.Get = Ui.Button(I18n.T("svc.install"), (s, e) => OpenPackages(svc.PackageType), true, "update");
            row.Get.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            row.Get.MinimumSize = new Size(0, Ui.S(30));
            row.Get.AutoSize = false;
            row.Get.Size = row.Get.GetPreferredSize(Size.Empty);

            var cm = new ContextMenuStrip();
            StyleMenu(cm);
            cm.Opening += (s, e) => BuildCardMenu(cm, row);
            card.ContextMenuStrip = cm;
            row.Name.ContextMenuStrip = cm;

            card.Controls.AddRange(new Control[] { row.Tile, row.Name, row.Detail, row.Error, row.Fix, row.Toggle, row.Status, row.Version, row.Get, row.Sec });
            card.Resize += (s, e) => LayoutCard(row);
            card.Width = Ui.S(700);
            LayoutCard(row);
            return row;
        }

        void LayoutCard(Row row)
        {
            int w = row.Card.Width, right = w - Ui.S(18), top = Ui.S(23);
            row.Toggle.Location = new Point(right - row.Toggle.Width, top);
            row.Get.Location = new Point(right - row.Get.Width, top - Ui.S(3));
            // keep the status pills of all cards in one column (PHP has no switch)
            int edge = (row.ShowGet ? row.Get.Left : row.Toggle.Left) - Ui.S(14);
            row.Status.Location = new Point(edge - row.Status.Width, top + 1);
            row.Version.Location = new Point(row.Status.Left - Ui.S(12) - row.Version.Width, top - Ui.S(1));
            if (row.Sec != null) row.Sec.Location = new Point(row.Version.Left - Ui.S(8) - row.Sec.Width, top + 1);
            row.Error.Width = Math.Max(Ui.S(100), w - Ui.S(73) - Ui.S(90));
            row.Fix.Location = new Point(w - Ui.S(18) - row.Fix.Width, Ui.S(68));
        }

        void BuildCardMenu(ContextMenuStrip cm, Row row)
        {
            cm.Items.Clear();
            var svc = row.Svc;
            if (svc.Id != "php")
            {
                var auto = new ToolStripMenuItem(I18n.T("svc.includeInAll")) { Checked = Settings.Current.IsEnabled(svc.Id) };
                auto.Click += (s, e) => { Settings.Current.SetEnabled(svc.Id, !auto.Checked); Settings.Current.Save(); UpdateRow(svc); };
                cm.Items.Add(auto);
            }
            cm.Items.Add(I18n.T("svc.getVersions"), null, (s, e) => OpenPackages(svc.PackageType));
            var log = svc.Id == "web" ? Settings.Current.WebServer + "-error.log" : svc.Id == "mysql" ? "mysql-error.log" : svc.Id == "postgresql" ? "postgresql.log" : svc.Id == "mongodb" ? "mongodb.log" : svc.Id == "memcached" ? "memcached.log" : svc.Id == "redis" ? "redis.log" : svc.Id == "mailpit" ? Settings.Current.MailTool + ".log" : svc.Id == "tomcat" ? TomcatUi.LogFile : "php_errors.log";
            cm.Items.Add(I18n.T("svc.openLog"), null, (s, e) => WinUtil.Edit(Path.Combine(Paths.Logs, log)));
            if (svc.Id == "tomcat") TomcatUi.AddItems(cm.Items);
        }

        /// <summary>MySQL and MariaDB are one card: the same port and data, either engine.</summary>
        static string DisplayName(Service svc) { return svc == ServiceManager.MySql ? I18n.T("svc.dbTitle") : svc.Title; }

        void FillVersions(Row row)
        {
            var type = row.Svc.PackageType;
            row.Version.Items.Clear();
            if (row.Svc == ServiceManager.MySql)
            {
                // both engines in one list: "MySQL 8.4.11", "MariaDB 11.8.9"; picking the other one switches the engine
                var engine = Settings.Current.DbEngine;
                var current = engine + "|" + PackageTypes.ActiveFolder(engine);
                foreach (var en in new[] { "mysql", "mariadb" })
                    foreach (var f in PackageTypes.Installed(en))
                        row.Version.Items.Add(new Item(PackageTypes.Get(en).Name + " " + PackageTypes.Label(f), en + "|" + f));
                int count = row.Version.Items.Count;
                if (count == 0) row.Version.Items.Add(new Item("—", ""));
                row.Version.Items.Add(new Item(I18n.T("svc.getVersions"), "__get"));
                int sel = row.Version.Items.Cast<Item>().ToList().FindIndex(i => string.Equals(i.Value, current, StringComparison.OrdinalIgnoreCase));
                row.Version.SelectedIndex = Math.Max(0, sel);
                row.Version.Tag = row.Version.SelectedIndex;
                return;
            }
            var installed = PackageTypes.Installed(type);
            var active = PackageTypes.ActiveFolder(type);
            foreach (var f in installed) row.Version.Items.Add(new Item(PackageTypes.Label(f), f));
            if (installed.Count == 0) row.Version.Items.Add(new Item("—", ""));
            row.Version.Items.Add(new Item(I18n.T("svc.getVersions"), "__get"));
            int idx = installed.FindIndex(f => string.Equals(f, active, StringComparison.OrdinalIgnoreCase));
            row.Version.SelectedIndex = Math.Max(0, idx);
            row.Version.Tag = row.Version.SelectedIndex;
        }

        void RefreshVersions()
        {
            foreach (var r in _rows) { FillVersions(r); UpdateRow(r.Svc); }
        }

        async Task OnVersionChanged(Row row)
        {
            var item = row.Version.SelectedItem as Item;
            if (item == null) return;
            if (item.Value == "__get" || item.Value == "")
            {
                row.Version.SelectedIndex = (int)row.Version.Tag;
                OpenPackages(row.Svc.PackageType);
                return;
            }
            row.Version.Tag = row.Version.SelectedIndex;
            var type = row.Svc.PackageType;
            var folder = item.Value;
            if (row.Svc == ServiceManager.MySql && folder.Contains("|"))
            {
                var engine = folder.Substring(0, folder.IndexOf('|'));
                folder = folder.Substring(folder.IndexOf('|') + 1);
                if (engine != Settings.Current.DbEngine)
                {
                    await SwitchDbEngine(engine, folder);
                    return;
                }
            }
            if (string.Equals(PackageTypes.ActiveFolder(type), folder, StringComparison.OrdinalIgnoreCase)) return;
            if (type == "mysql" || type == "mariadb")
            {
                // another line has its own data folder: offer to bring the databases along
                var sw = DatabaseUi.BeforeSwitch(this, type, folder);
                if (sw != DatabaseUi.SwitchResult.Continue) { RefreshVersions(); return; }
            }
            Settings.Current.SetVersion(type, folder);
            Settings.Current.Save();
            Log.Info(I18n.T("log.switched", PackageTypes.Get(type).Name, PackageTypes.Label(folder)));
            Terminal.ApplyUserPath();
            UpdateRow(row.Svc);
            await ApplyVersionChange(type);
        }

        /// <summary>MySQL &lt;-&gt; MariaDB from the card (same as Settings > Services > Database engine).</summary>
        async Task SwitchDbEngine(string engine, string folder)
        {
            var sw = DatabaseUi.BeforeSwitch(this, engine, folder);
            if (sw == DatabaseUi.SwitchResult.Cancelled) { RefreshVersions(); return; }
            bool wasActive = ServiceManager.MySql.IsActive;
            if (wasActive) await ServiceManager.MySql.StopAsync();   // stops the engine that runs now
            Settings.Current.DbEngine = engine;
            Settings.Current.SetVersion(engine, folder);
            Settings.Current.Save();
            Log.Info(I18n.T("log.switched", I18n.T("svc.dbTitle"), PackageTypes.Get(engine).Name + " " + PackageTypes.Label(folder)));
            Terminal.ApplyUserPath();
            if (wasActive) await ServiceManager.MySql.StartAsync();
            RefreshVersions();
        }

        async Task ApplyVersionChange(string type)
        {
            CheckVcRuntime(false);
            if (ServiceMode.IsController) { await ServiceMode.CommandAsync("reload"); return; }
            switch (type)
            {
                case "php":
                case "apache":
                case "nginx":
                case "adminer":
                case "phpmyadmin":
                case "mailpit":
                case "mailhog":
                case "smtp4dev":
                    if (type == Settings.Current.MailTool && ServiceManager.Mailpit.IsActive) await ServiceManager.Mailpit.RestartAsync();
                    await ServiceManager.ReloadWebAsync();
                    break;
                case "mysql":
                case "mariadb":
                    if (ServiceManager.MySql.IsActive) await ServiceManager.MySql.RestartAsync();
                    break;
                case "postgresql":
                    if (ServiceManager.Postgres.IsActive) await ServiceManager.Postgres.RestartAsync();
                    break;
                case "mongodb":
                    if (ServiceManager.Mongo.IsActive) await ServiceManager.Mongo.RestartAsync();
                    break;
                case "memcached":
                    if (ServiceManager.Memcached.IsActive) await ServiceManager.Memcached.RestartAsync();
                    break;
                case "redis":
                    if (ServiceManager.Redis.IsActive) await ServiceManager.Redis.RestartAsync();
                    break;
                case "tomcat":
                case "java":
                    if (TomcatUi.NeedsRestart(type)) await ServiceManager.Tomcat.RestartAsync();
                    break;
            }
        }

        async Task OnToggle(Row row)
        {
            var svc = row.Svc;
            if (!svc.IsInstalled) { OpenPackages(svc.PackageType); return; }
            if (svc.State == ServiceState.Starting || svc.State == ServiceState.Stopping) return;
            row.Toggle.Enabled = false;
            try
            {
                if (svc.IsActive) await ServiceManager.StopAsync(svc);
                else
                {
                    if (!await ResolvePorts(new[] { svc.Id == "php" ? ServiceManager.Web : svc })) return;
                    CheckVcRuntime(false);
                    await ServiceManager.StartAsync(svc);
                    if (svc.Id == "web" && svc.State == ServiceState.Running) AskTrustCa();
                }
            }
            finally { row.Toggle.Enabled = true; UpdateRow(svc); UpdateGlobal(); }
        }

        void UpdateRow(Service svc)
        {
            var row = _rows.FirstOrDefault(r => r.Svc == svc);
            if (row == null) return;
            var p = Theme.Current;
            bool installed = svc.IsInstalled;
            row.Card.Visible = IsServiceShown(svc); // an optional database appears once it is installed
            if (svc == ServiceManager.MySql)
            {
                // the card's logo follows the engine (MySQL / MariaDB)
                using (var t = BrandTile.For(svc.PackageType)) { row.Tile.Text = t.Text; row.Tile.Color = t.Color; }
                row.Tile.Invalidate();
            }
            string statusText; Color statusColor;
            switch (svc.State)
            {
                case ServiceState.Running: statusText = I18n.T("svc.running"); statusColor = p.Green; break;
                case ServiceState.Starting: statusText = I18n.T("svc.starting"); statusColor = p.Orange; break;
                case ServiceState.Stopping: statusText = I18n.T("svc.stopping"); statusColor = p.Orange; break;
                case ServiceState.Error: statusText = I18n.T("svc.error"); statusColor = p.Red; break;
                default: statusText = installed ? I18n.T("svc.stopped") : I18n.T("svc.notInstalled"); statusColor = p.Gray; break;
            }
            row.Status.Text = statusText;
            row.Status.Color = statusColor;
            row.Toggle.Checked = svc.State == ServiceState.Running || svc.State == ServiceState.Starting;
            row.Toggle.Busy = svc.State == ServiceState.Starting || svc.State == ServiceState.Stopping;
            row.Toggle.Visible = installed && svc.Id != "php";
            row.ShowGet = !installed;
            row.Get.Visible = !installed;

            var version = PackageTypes.Label(PackageTypes.ActiveFolder(svc.PackageType));
            string ports = svc.Id == "php" ? (svc.IsActive ? "127.0.0.1:" + svc.PortText : I18n.T("svc.withWeb")) : (Settings.Current.AllowLan ? "0.0.0.0:" : "127.0.0.1:") + svc.PortText;
            var detail = installed ? svc.Title + " " + version + "  ·  " + ports : I18n.T("svc.notInstalledHint");
            if (svc.Id != "php" && installed && !Settings.Current.IsEnabled(svc.Id)) detail += "  ·  " + I18n.T("svc.notInAll");
            row.Detail.Text = detail;
            UpdateSecurityPill(row);

            bool err = svc.State == ServiceState.Error && !string.IsNullOrEmpty(svc.LastError);
            row.Error.Text = err ? svc.LastError.Replace("\n", "  ") : "";
            row.Error.Visible = err;
            row.Fix.Visible = err && svc.LastError.Contains(":") && IsPortError(svc.LastError);
            row.Card.Height = err ? Ui.S(104) : Ui.S(72);
            row.Card.BorderColor = err ? Color.FromArgb(120, p.Red) : p.Border;
            LayoutCard(row);
        }

        static bool IsPortError(string text)
        {
            // the port message is built from err.portInUse; compare its stable start
            var probe = I18n.T("err.portInUse", "\u0001", "\u0002");
            var head = probe.Substring(0, Math.Max(0, probe.IndexOf('\u0001')));
            return head.Length > 0 ? text.Contains(head) : false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
        bool _shutdownBlocked;

        /// <summary>While services run, Windows shows "Portly is closing services" at shutdown instead of killing MySQL mid-write.</summary>
        void UpdateShutdownBlock(bool any)
        {
            if (!IsHandleCreated || !OsInfo.IsWin10OrLater && OsInfo.Number < 6.0) return;
            try
            {
                if (any && !_shutdownBlocked) _shutdownBlocked = ShutdownBlockReasonCreate(Handle, I18n.T("main.stopping"));
                else if (!any && _shutdownBlocked) { ShutdownBlockReasonDestroy(Handle); _shutdownBlocked = false; }
            }
            catch { }
        }

        void UpdateGlobal()
        {
            if (_startAll == null) return;
            bool any = ServiceManager.AnyActive;
            UpdateShutdownBlock(any && !ServiceMode.IsController);
            _startAll.Text = any ? I18n.T("main.stopAll") : I18n.T("main.startAll");
            _startAll.Glyph = any ? "stop" : "play";
            _startAll.Kind = any ? ButtonKind.Secondary : ButtonKind.Primary;
            _startAll.Invalidate();
            var rows = ServiceManager.Rows.Where(s => s.Id != "php" && s.IsInstalled).ToList();
            int running = rows.Count(s => s.State == ServiceState.Running);
            _servicesSub.Text = running == 0 ? I18n.T("main.allStopped") : I18n.T("main.statusRunning", running, rows.Count);

            var web = ServiceManager.Web;
            Color? dot = web.State == ServiceState.Running ? Theme.Light.Green : web.State == ServiceState.Error ? Theme.Light.Red : (Color?)null;
            var old = _tray.Icon;
            _tray.Icon = Ui.TrayIcon(dot);
            if (old != null) old.Dispose();
            var names = ServiceManager.Rows.Where(s => s.State == ServiceState.Running).Select(s => s.Title).ToList();
            var tip = "Portly" + (names.Count > 0 ? " - " + string.Join(", ", names) : "");
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        // ---------------- sites page ----------------
        Panel BuildSitesPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            var header = PageHeader(I18n.T("nav.sites"), out _sitesSub,
                Ui.Button(I18n.T("main.newProject"), (s, e) => NewProject(), true, "plus"),
                Ui.Button(I18n.T("sites.refresh"), async (s, e) => await ServiceManager.ReloadWebAsync(), false, "refresh"),
                Ui.Button(I18n.T("sites.folder"), (s, e) => WinUtil.OpenFolder(Settings.Current.DocumentRoot), false, "folder"),
                _archivedBtn = SitesWebUi.ArchivedButton(this),
                _sslBtn = Ui.Button(I18n.T("sslfix.button"), (s, e) => OpenSslRepair(), false, "warning"));
            _siteFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Bg, Padding = Ui.P(0, 4, 0, 0) };
            _siteFlow.Resize += (s, e) => { foreach (var inner in _siteBlocks) inner.MaximumSize = new Size(BlockWidth(), 0); };
            page.Controls.Add(_siteFlow);
            page.Controls.Add(header);
            _siteFlow.BringToFront();
            return page;
        }

        bool _caTrusted;
        ModernButton _archivedBtn;

        void FillSites()
        {
            if (_siteFlow == null) return;
            _caTrusted = Certs.IsCaTrusted();
            var p = Theme.Current;
            var s = Settings.Current;
            var sites = ServiceManager.CurrentSites;
            _sitesSub.Text = I18n.T("sites.count", sites.Count(x => x.Valid), s.DocumentRoot, s.Tld);
            SitesWebUi.UpdateArchived(_archivedBtn);
            _siteFlow.SuspendLayout();
            foreach (Control c in _siteFlow.Controls.Cast<Control>().ToList()) c.Dispose();
            _siteFlow.Controls.Clear();
            if (sites.Count == 0)
            {
                var empty = new Card { Size = Ui.S(600, 130), Radius = 12 };
                var t = Ui.Label(I18n.T("sites.emptyTitle"), Ui.Subtitle);
                t.Location = new Point(Ui.S(20), Ui.S(18));
                var d = Ui.Label(I18n.T("dash.empty", s.DocumentRoot), Ui.Font, Ui.Muted);
                d.MaximumSize = new Size(Ui.S(560), 0);
                d.Location = new Point(Ui.S(20), Ui.S(46));
                var b = Ui.Button(I18n.T("main.newProject"), (o, e) => NewProject(), true, "plus");
                b.Location = new Point(Ui.S(20), Ui.S(82));
                empty.Controls.AddRange(new Control[] { t, d, b });
                _siteFlow.Controls.Add(empty);
            }
            // HTTPS problems per site (missing / expired / old CA / names changed), shown on the cards and the header button
            var ssl = s.Ssl ? SslRepair.Check(sites) : null;
            _sslProblems = ssl == null ? new Dictionary<string, string>() : ssl.Sites.Where(x => x.Problem != null).ToDictionary(x => x.Site.Host, x => x.Problem);
            int sslIssues = _sslProblems.Count + (ssl != null && !ssl.CaTrusted && sites.Any(x => x.Valid) ? 1 : 0);
            _sslBtn.Text = I18n.T("sslfix.button") + (sslIssues > 0 ? " (" + sslIssues + ")" : "");
            _sslBtn.CustomColor = sslIssues > 0 ? (Color?)p.Orange : null;
            _sslBtn.Visible = s.Ssl;

            // a site whose address ends with another site's address is its subdomain: show it under that site
            var parentOf = new Dictionary<Site, Site>();
            foreach (var site in sites)
            {
                var parent = sites.Where(o => o != site && site.Host.EndsWith("." + o.Host, StringComparison.OrdinalIgnoreCase))
                                  .OrderByDescending(o => o.Host.Length).FirstOrDefault();
                if (parent != null) parentOf[site] = parent;
            }
            var roots = sites.Where(x => !parentOf.ContainsKey(x)).ToList();
            _siteBlocks.Clear();
            foreach (var site in roots.Where(x => !parentOf.ContainsValue(x))) _siteFlow.Controls.Add(SiteCard(site, null));
            foreach (var site in roots.Where(x => parentOf.ContainsValue(x))) _siteFlow.Controls.Add(SiteBlock(site, parentOf));
            _siteFlow.ResumeLayout(true);
            Ui.StyleTree(_siteFlow);
        }

        Dictionary<string, string> _sslProblems = new Dictionary<string, string>();
        ModernButton _sslBtn;
        readonly List<FlowLayoutPanel> _siteBlocks = new List<FlowLayoutPanel>();

        /// <summary>A site with its subdomains (group folders, "shop.example" folders): one row, joined by a line.</summary>
        Control SiteBlock(Site root, Dictionary<Site, Site> parentOf)
        {
            var p = Theme.Current;
            var all = new List<Site>();
            Action<Site> add = null;
            add = site =>
            {
                all.Add(site);
                foreach (var kid in parentOf.Where(kv => kv.Value == site).Select(kv => kv.Key).OrderBy(k => k.Host)) add(kid);
            };
            add(root);
            var block = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Ui.P(0, 2, 0, 14), Padding = Ui.P(14, 0, 0, 0), BackColor = Color.Transparent };
            var head = Ui.Label(root.Host + "  ·  " + I18n.T("sites.subCount", all.Count - 1), Ui.Small, Ui.Muted);
            head.Location = new Point(Ui.S(14), 0);
            var inner = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
            inner.Location = new Point(Ui.S(14), head.PreferredHeight + Ui.S(6));
            inner.MaximumSize = new Size(BlockWidth(), 0);
            foreach (var site in all) inner.Controls.Add(SiteCard(site, site == root ? null : parentOf[site]));
            block.Controls.Add(head);
            block.Controls.Add(inner);
            // the accent line on the left ties the subdomains to their site
            block.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(150, p.Accent), Math.Max(2, Ui.S(3))))
                    e.Graphics.DrawLine(pen, Ui.S(4), Ui.S(4), Ui.S(4), block.Height - Ui.S(14));
            };
            _siteBlocks.Add(inner);
            _siteFlow.SetFlowBreak(block, true);
            return block;
        }

        int BlockWidth() { return Math.Max(Ui.S(330), _siteFlow.ClientSize.Width - Ui.S(40)); }

        void OpenSslRepair()
        {
            ShowFromTray();
            using (var f = new SslRepairForm(ServiceManager.CurrentSites)) f.ShowDialog(this);
            FillSites();
        }

        Control SiteCard(Site site, Site parent)
        {
            var p = Theme.Current;
            var card = new Card { Size = Ui.S(300, 104), Radius = 12, Margin = Ui.P(0, 0, 12, 12), Cursor = site.Valid ? Cursors.Hand : Cursors.Default };
            if (!site.Valid) card.BorderColor = Color.FromArgb(140, p.Red);
            var avatar = BrandTile.Avatar(site.Name);
            avatar.Location = new Point(Ui.S(16), Ui.S(16));
            // a subdomain shows only its own part ("shop" of shop.clients.portly), the block header names the parent
            var title = parent != null ? "↳ " + site.Host.Substring(0, site.Host.Length - parent.Host.Length - 1)
                      : site.Group != "" ? site.Group + " / " + site.ShortName : site.Name;
            var name = Ui.Label(title, Ui.Subtitle);
            if (parent != null) new ToolTip().SetToolTip(name, I18n.T("sites.subOf", site.Host, parent.Host));
            name.AutoSize = false;
            name.AutoEllipsis = true;
            name.Size = new Size(Ui.S(190), Ui.S(24));
            name.Location = new Point(Ui.S(64), Ui.S(14));
            Control address;
            if (site.Valid)
            {
                var link = Ui.Link(site.PreferredUrl, () => OpenSite(site, Settings.Current.Ssl));
                link.AutoSize = false;
                link.AutoEllipsis = true;
                link.Size = new Size(Ui.S(220), Ui.S(20));
                address = link;
            }
            else
            {
                var bad = Ui.Label(site.Problem, Ui.Small, p.Red);
                bad.AutoSize = false;
                bad.AutoEllipsis = true;
                bad.Size = new Size(Ui.S(220), Ui.S(36));
                address = bad;
            }
            address.Location = new Point(Ui.S(64), Ui.S(40));
            var php = new Pill { Text = "PHP " + PackageTypes.Label(site.PhpFolder) + (site.PhpOverride != "" ? " •" : ""), Color = ColorTranslator.FromHtml("#777BB4"), Dot = false };
            php.Location = new Point(Ui.S(64), Ui.S(70));
            php.Visible = site.Valid;
            var badges = new List<Control> { php };
            if (site.IsGroup)
                badges.Add(new Pill { Text = I18n.T("sites.group"), Color = p.Accent, Dot = false });
            var shared = Share.UrlOf(site);
            if (shared != null)
            {
                var sp = new Pill { Text = I18n.T("share.badge"), Color = ColorTranslator.FromHtml("#F38020"), Cursor = Cursors.Hand };
                sp.Click += (o, e) => WinUtil.Open(shared);
                new ToolTip().SetToolTip(sp, shared);
                badges.Add(sp);
            }
            if (site.Valid && Settings.Current.Ssl)
            {
                string problem;
                _sslProblems.TryGetValue(site.Host, out problem);
                bool ok = _caTrusted && problem == null;
                var lockPill = new Pill { Text = ok ? "HTTPS" : "HTTPS !", Color = ok ? p.Green : (problem != null ? p.Red : p.Orange), Cursor = ok ? Cursors.Default : Cursors.Hand };
                if (!ok)
                {
                    lockPill.Click += (o, e) => OpenSslRepair();
                    new ToolTip().SetToolTip(lockPill, (problem != null ? SslRepair.ProblemText(problem) : I18n.T("sites.httpsUntrusted")) + " - " + I18n.T("sslfix.button"));
                }
                badges.Add(lockPill);
            }
            badges.AddRange(DevWorkflowUi.SiteBadges(site, this, FillSites));
            badges.AddRange(SitesWebUi.CardBadges(site));
            int bx = Ui.S(64);
            foreach (var b in badges.Where(x => x.Visible || x != php))
            {
                b.Location = new Point(bx, Ui.S(70));
                bx += b.Width + Ui.S(6);
                if (b != php) card.Controls.Add(b);
            }
            var moreBtn = new ModernButton { Glyph = "more", Kind = ButtonKind.Ghost, AutoSize = false, Size = Ui.S(32, 32), Location = new Point(Ui.S(256), Ui.S(10)), AccessibleName = I18n.T("sites.more") };
            var cm = new ContextMenuStrip();
            StyleMenu(cm);
            cm.Opening += (s, e) => BuildSiteMenu(cm.Items, site);
            moreBtn.Click += (s, e) => cm.Show(moreBtn, new Point(0, moreBtn.Height));
            card.ContextMenuStrip = cm;
            card.Controls.AddRange(new Control[] { avatar, name, address, php, moreBtn });
            if (site.Valid)
            {
                EventHandler open = (s, e) => OpenSite(site, Settings.Current.Ssl);
                card.Click += open;
                avatar.Click += open;
                name.Click += open;
            }
            var tip = new ToolTip();
            tip.SetToolTip(card, site.DocRoot);
            tip.SetToolTip(name, site.DocRoot);
            if (site.Aliases.Count > 0) tip.SetToolTip(address, site.PreferredUrl + "\n" + SitesWebUi.AliasText(site));
            return card;
        }

        void BuildSiteMenu(ToolStripItemCollection items, Site site)
        {
            items.Clear();
            if (site.Valid)
            {
                items.Add(I18n.T("sites.openHttp"), null, (s, e) => OpenSite(site, false));
                if (Settings.Current.Ssl) items.Add(I18n.T("sites.openHttps"), null, (s, e) => OpenSite(site, true));
            }
            items.Add(I18n.T("sites.folder"), null, (s, e) => WinUtil.OpenFolder(site.Folder));
            items.Add(I18n.T("main.terminal"), null, (s, e) => Terminal.Open(site.Folder));
            foreach (var ed in DesktopApps.Editors)
            {
                var editor = ed;
                if (DesktopApps.IsInstalled(editor))
                    items.Add(I18n.T("tools.openIn", PackageTypes.Get(editor).Name), null, (s, e) => DesktopApps.OpenInEditor(editor, site.Folder));
            }
            var php = new ToolStripMenuItem(I18n.T("sites.phpVersion"));
            var def = new ToolStripMenuItem(I18n.T("sites.phpDefault", PackageTypes.Label(PackageTypes.ActiveFolder("php")))) { Checked = site.PhpOverride == "" };
            def.Click += async (s, e) => { Sites.SetPhp(site.Name, ""); await ServiceManager.ReloadWebAsync(); };
            php.DropDownItems.Add(def);
            foreach (var f in PackageTypes.Installed("php"))
            {
                var folder = f;
                var mi = new ToolStripMenuItem("PHP " + PackageTypes.Label(f)) { Checked = string.Equals(site.PhpOverride, f, StringComparison.OrdinalIgnoreCase) };
                mi.Click += async (s, e) => { Sites.SetPhp(site.Name, folder); Log.Info(I18n.T("log.sitePhp", site.Name, PackageTypes.Label(folder))); await ServiceManager.ReloadWebAsync(); };
                php.DropDownItems.Add(mi);
            }
            items.Add(php);
            items.Add(I18n.T("sites.createDb"), null, (s, e) => CreateDatabase(ProjectCreator.DbNameFor(site.Name)));
            if (site.Valid && Share.IsAvailable)
            {
                if (Share.UrlOf(site) == null) items.Add(I18n.T("share.start"), null, (s, e) => ShareSite(site));
                else
                {
                    items.Add(I18n.T("share.copy"), null, (s, e) => { try { Clipboard.SetText(Share.UrlOf(site)); } catch { } });
                    items.Add(I18n.T("share.stop"), null, (s, e) => { Share.Stop(site); FillSites(); });
                }
            }
            DevWorkflowUi.AddSiteMenuItems(items, site, this, FillSites);
            SitesWebUi.AddSiteItems(this, items, site);
            if (Deploy.IsAvailable)
                items.Add(I18n.T("deploy.menu"), null, (s, e) => { ShowFromTray(); using (var f = new DeployForm(site)) f.ShowDialog(this); });
            items.Add(new ToolStripSeparator());
            items.Add(I18n.T("sites.delete"), null, async (s, e) =>
            {
                if (!Ui.Confirm(this, I18n.T("sites.deleteConfirm", site.Name, site.Folder))) return;
                try { ProjectCreator.DeleteToRecycleBin(site); }
                catch (Exception ex) { Ui.Error(this, ex.Message); return; }
                await ServiceManager.ReloadWebAsync();
            });
        }

        async void ShareSite(Site site)
        {
            if (ServiceManager.Web.State != ServiceState.Running) { Ui.Error(this, I18n.T("main.webNotRunning")); return; }
            // the site becomes public: say exactly what happens before anything is downloaded or opened
            if (!Ui.Confirm(this, I18n.T("share.confirm", site.Host))) return;
            try
            {
                var url = await Share.StartAsync(site, s => SafeInvoke(() => SetStatus(s)));
                try { Clipboard.SetText(url); } catch { }
                FillSites();
                Ui.Info(this, I18n.T("share.ready", url));
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        void OpenSite(Site site, bool https)
        {
            if (!site.Valid) { Ui.Error(this, site.Problem); return; }
            if (!ServiceManager.Web.IsActive) SetStatus(I18n.T("main.webNotRunning"));
            WinUtil.Open(site.Url(https && Settings.Current.Ssl));
        }

        // ---------------- log page ----------------
        Panel BuildLogPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            Label sub;
            var header = PageHeader(I18n.T("nav.log"), out sub,
                Ui.Button(I18n.T("log.clear"), (s, e) => { _logLines.Clear(); _log.Clear(); }),
                Ui.Button(I18n.T("log.folder"), (s, e) => WinUtil.OpenFolder(Paths.Logs), false, "folder"));
            sub.Text = Paths.Logs;
            var p = Theme.Current;
            var frame = new Card { Dock = DockStyle.Fill, BackColor = p.ConsoleBg, BorderColor = p.ConsoleBg, Padding = Ui.P(14, 12, 6, 12), Radius = 12 };
            _log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = p.ConsoleBg, ForeColor = p.ConsoleText, Font = Ui.Mono, Tag = "console", DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical };
            frame.Controls.Add(_log);
            page.Controls.Add(frame);
            page.Controls.Add(header);
            frame.BringToFront();
            foreach (var l in _logLines) WriteLogLine(l);
            return page;
        }

        void AppendLog(string line)
        {
            _logLines.Add(line);
            if (_logLines.Count > 2000) _logLines.RemoveRange(0, 500);
            WriteLogLine(line);
            SetStatus(line.Length > 10 ? line.Substring(10) : line);
        }

        void WriteLogLine(string line)
        {
            if (_log == null || _log.IsDisposed) return;
            var p = Theme.Current;
            var time = line.Length > 10 ? line.Substring(0, 10) : "";
            var text = line.Length > 10 ? line.Substring(10) : line;
            Color c = text.StartsWith("ERROR") ? Color.FromArgb(248, 113, 113) : text.StartsWith("WARN") ? Color.FromArgb(251, 191, 36) : p.ConsoleText;
            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = p.ConsoleMuted;
            _log.AppendText(time);
            _log.SelectionColor = c;
            _log.AppendText(text + "\n");
            _log.ScrollToCaret();
        }

        void SetStatus(string text) { if (_status != null) _status.Text = text; }

        // =====================================================================
        // Menus
        // =====================================================================
        class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Current.Card; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Current.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Current.Card; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Current.Card; } }
            public override Color MenuBorder { get { return Theme.Current.Border; } }
            public override Color MenuItemBorder { get { return Theme.Current.Hover; } }
            public override Color MenuItemSelected { get { return Theme.Current.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Current.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Current.Hover; } }
            public override Color SeparatorDark { get { return Theme.Current.Border; } }
            public override Color SeparatorLight { get { return Theme.Current.Card; } }
            public override Color CheckBackground { get { return Theme.Current.SidebarActive; } }
            public override Color CheckSelectedBackground { get { return Theme.Current.SidebarActive; } }
            public override Color CheckPressedBackground { get { return Theme.Current.SidebarActive; } }
        }

        class MenuRenderer : ToolStripProfessionalRenderer
        {
            public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Current.Text; base.OnRenderArrow(e); }
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = new RectangleF(e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2, e.ImageRectangle.Width - 4, e.ImageRectangle.Height - 4);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Theme.Current.Accent)) Glyphs.FillRound(e.Graphics, b, r, 3);
                using (var pen = new Pen(Color.White, 1.6f))
                    e.Graphics.DrawLines(pen, new[] { new PointF(r.X + r.Width * .25f, r.Y + r.Height * .52f), new PointF(r.X + r.Width * .43f, r.Y + r.Height * .7f), new PointF(r.X + r.Width * .76f, r.Y + r.Height * .32f) });
            }
        }

        static void StyleMenu(ContextMenuStrip m)
        {
            m.Renderer = new MenuRenderer();
            m.BackColor = Theme.Current.Card;
            m.ForeColor = Theme.Current.Text;
            m.Font = Ui.Font;
            m.ItemAdded += (s, e) => StyleItem(e.Item);
        }

        static void StyleItem(ToolStripItem item)
        {
            item.ForeColor = Theme.Current.Text;
            item.Padding = new Padding(0, 2, 0, 2);
            var mi = item as ToolStripMenuItem;
            if (mi == null) return;
            mi.DropDown.BackColor = Theme.Current.Card;
            mi.DropDown.Renderer = new MenuRenderer();
            mi.DropDown.ItemAdded += (s, e) => StyleItem(e.Item);
            foreach (ToolStripItem sub in mi.DropDownItems) StyleItem(sub);
        }

        void BuildMenu(ToolStripItemCollection items)
        {
            items.Clear();
            var bold = new ToolStripMenuItem(I18n.T("tray.show")) { Font = Ui.Bold };
            bold.Click += (s, e) => ShowFromTray();
            items.Add(bold);
            items.Add(new ToolStripSeparator());
            items.Add(ServiceManager.AnyActive ? I18n.T("main.stopAll") : I18n.T("main.startAll"), null, async (s, e) => await ToggleAll());
            items.Add(I18n.T("main.web") + " (localhost)", null, (s, e) => OpenLocalhost());

            var sites = new ToolStripMenuItem(I18n.T("nav.sites"));
            foreach (var site in ServiceManager.CurrentSites.Where(x => x.Valid))
            {
                var st = site;
                sites.DropDownItems.Add(site.Host, null, (s, e) => OpenSite(st, Settings.Current.Ssl));
            }
            if (sites.DropDownItems.Count == 0) sites.DropDownItems.Add(new ToolStripMenuItem(I18n.T("sites.none")) { Enabled = false });
            sites.DropDownItems.Add(new ToolStripSeparator());
            sites.DropDownItems.Add(I18n.T("main.newProject"), null, (s, e) => NewProject());
            items.Add(sites);

            var db = new ToolStripMenuItem(PackageTypes.Get(Settings.Current.DbEngine).Name);
            var toolsMenu = new ToolStripMenuItem(I18n.T("main.database"));
            BuildDatabaseToolItems(toolsMenu.DropDownItems);
            db.DropDownItems.Add(toolsMenu);
            db.DropDownItems.Add(I18n.T("db.create"), null, (s, e) => CreateDatabase(""));
            db.DropDownItems.Add(I18n.T("sqlimp.menu"), null, (s, e) => { ShowFromTray(); DatabaseUi.ImportSql(this); });
            db.DropDownItems.Add(I18n.T("repair.menu"), null, async (s, e) => { ShowFromTray(); await DatabaseUi.CheckAndRepair(this); });
#if !LEGACY
            if (Features.IsOn("wsl") && WslAccess.Supported)
                db.DropDownItems.Add(I18n.T("wsl.menu"), null, async (s, e) => { ShowFromTray(); await DatabaseUi.WslAccessDialog(this); });
#endif
            db.DropDownItems.Add(I18n.T("db.backup"), null, async (s, e) => await BackupDatabases());
            db.DropDownItems.Add(I18n.T("db.password"), null, (s, e) => ChangeRootPassword());
            db.DropDownItems.Add(I18n.T("db.resetRoot"), null, async (s, e) =>
            {
                if (!Ui.Confirm(this, I18n.T("db.resetRootConfirm"))) return;
                var err = await ServiceManager.MySql.ResetRootPasswordAsync();
                if (err != null) Ui.Error(this, err); else Ui.Info(this, I18n.T("db.resetRootDone"));
            });
            db.DropDownItems.Add(I18n.T("db.recovery"), null, async (s, e) =>
            {
                if (!Ui.Confirm(this, I18n.T("db.recoveryConfirm"))) return;
                await ServiceManager.MySql.StartRecoveryAsync();
                if (ServiceManager.MySql.State == ServiceState.Running) Ui.Info(this, I18n.T("db.recoveryRunning"));
            });
            db.DropDownItems.Add(I18n.T("db.backupFolder"), null, (s, e) => WinUtil.OpenFolder(Path.Combine(Paths.Backup, "mysql")));
            db.DropDownItems.Add(I18n.T("db.config"), null, (s, e) => WinUtil.Edit(Templates.PathOf("mysql.ini.tpl")));
            items.Add(db);

            var phpDir = PackageTypes.ActiveDir("php");
            var php = new ToolStripMenuItem("PHP");
            if (phpDir != null)
            {
                php.DropDownItems.Add("php.ini", null, (s, e) => WinUtil.Edit(PhpConfig.IniPath(phpDir)));
                var ext = new ToolStripMenuItem(I18n.T("php.extensions"));
                foreach (var kv in PhpConfig.ListExtensions(phpDir))
                {
                    var name = kv.Key;
                    var mi = new ToolStripMenuItem(name) { Checked = kv.Value };
                    mi.Click += async (s, e) =>
                    {
                        PhpConfig.ToggleExtension(phpDir, name, !mi.Checked);
                        Log.Info(I18n.T(mi.Checked ? "log.extOff" : "log.extOn", name));
                        await ServiceManager.ReloadWebAsync();
                    };
                    ext.DropDownItems.Add(mi);
                }
                php.DropDownItems.Add(ext);
                if (Features.IsOn("extensions"))
                    php.DropDownItems.Add(I18n.T("ext.menu"), null, (s, e) => { using (var f = new PhpExtensionsForm()) f.ShowDialog(this); });
                var xdebugOn = Xdebug.IsEnabled(phpDir);
                var xd = new ToolStripMenuItem("Xdebug") { Checked = xdebugOn };
                xd.Click += async (s2, e2) =>
                {
                    try
                    {
                        if (xdebugOn) Xdebug.Disable(phpDir);
                        else { SetStatus(I18n.T("log.downloading", "Xdebug")); await Xdebug.EnableAsync(phpDir); }
                        await ServiceManager.ReloadWebAsync();
                    }
                    catch (Exception ex) { Ui.Error(this, "Xdebug: " + ex.Message); }
                };
                php.DropDownItems.Add(xd);
                php.DropDownItems.Add(I18n.T("php.errorLog"), null, (s, e) => WinUtil.Edit(Path.Combine(Paths.Logs, "php_errors.log")));
            }
            else php.DropDownItems.Add(I18n.T("svc.getVersions"), null, (s, e) => OpenPackages("php"));
            items.Add(php);

            var web = new ToolStripMenuItem(ServiceManager.Web.Title);
            web.DropDownItems.Add(I18n.T("web.configFolder"), null, (s, e) => WinUtil.OpenFolder(Path.Combine(Paths.Etc, Settings.Current.WebServer)));
            web.DropDownItems.Add(I18n.T("web.errorLog"), null, (s, e) => WinUtil.Edit(Path.Combine(Paths.Logs, Settings.Current.WebServer + "-error.log")));
            web.DropDownItems.Add(I18n.T("web.templates"), null, (s, e) => WinUtil.OpenFolder(Paths.Templates));
            web.DropDownItems.Add(I18n.T("web.userTemplates"), null, (s, e) => WinUtil.OpenFolder(Paths.UserTemplates));
            items.Add(web);

            var ssl = new ToolStripMenuItem("SSL / HTTPS");
            bool trusted = Certs.IsCaTrusted();
            ssl.DropDownItems.Add(new ToolStripMenuItem(trusted ? I18n.T("ssl.trusted") : I18n.T("ssl.trust"), null, (s, e) => TrustCa()) { Enabled = !trusted });
            ssl.DropDownItems.Add(I18n.T("ssl.regenerate"), null, async (s, e) => { Certs.RegenerateSiteCertificates(); await ServiceManager.ReloadWebAsync(); });
            ssl.DropDownItems.Add(I18n.T("ssl.folder"), null, (s, e) => WinUtil.OpenFolder(Paths.UserSsl));
            SitesWebUi.AddSslItems(this, ssl.DropDownItems);
            items.Add(ssl);

            var tools = new ToolStripMenuItem(I18n.T("menu.tools"));
            tools.DropDownItems.Add(I18n.T("main.terminal"), null, (s, e) => Terminal.Open());
            tools.DropDownItems.Add(I18n.T("import.menu"), null, (s, e) => OpenImport(true));
            foreach (var ed in DesktopApps.Editors)
            {
                var editor = ed;
                if (DesktopApps.IsInstalled(editor))
                    tools.DropDownItems.Add(I18n.T("tools.openIn", PackageTypes.Get(editor).Name), null, (s, e) => DesktopApps.OpenInEditor(editor, Settings.Current.DocumentRoot));
            }
            if (DesktopApps.GitBash != null) tools.DropDownItems.Add("Git Bash", null, (s, e) => DesktopApps.Start(DesktopApps.GitBash, "--cd=\"" + Settings.Current.DocumentRoot + "\""));
            tools.DropDownItems.Add(I18n.T("port.checkNow"), null, async (s, e) => await FixPorts(ServiceManager.Rows.Where(x => x.Id != "php")));
            tools.DropDownItems.Add(I18n.T("health.menu"), null, async (s, e) => await ShowHealth(true));
            tools.DropDownItems.Add(I18n.T("health.defender"), null, (s, e) =>
            {
                if (!Ui.Confirm(this, I18n.T("health.defenderConfirm", Paths.Root, Settings.Current.DocumentRoot))) return;
                var code = HealthCheck.AddDefenderExclusion();
                if (code == 0) Ui.Info(this, I18n.T("health.defenderDone"));
            });
            tools.DropDownItems.Add(I18n.T("verify.menu"), null, (s, e) => ShowVerify(null));
            tools.DropDownItems.Add(I18n.T("tools.hosts"), null, (s, e) => WinUtil.RunElevated("notepad.exe", "\"" + HostsFile.FilePath + "\"", false));
            tools.DropDownItems.Add(I18n.T("tools.root"), null, (s, e) => WinUtil.OpenFolder(Paths.Root));
            tools.DropDownItems.Add(I18n.T("tools.logs"), null, (s, e) => WinUtil.OpenFolder(Paths.Logs));
            items.Add(tools);

            items.Add(new ToolStripSeparator());
            items.Add(I18n.T("main.packages"), null, (s, e) => OpenPackages(null));
            items.Add(I18n.T("main.settings"), null, (s, e) => OpenSettings());
            if (AppInfo.UpdatesSupported)
                items.Add(I18n.T("menu.checkUpdates"), null, async (s, e) => await BackgroundChecks(true, true));
            items.Add(I18n.T("menu.about"), null, (s, e) => { ShowFromTray(); using (var f = new AboutForm()) f.ShowDialog(this); });
            var don = new ToolStripMenuItem("♥ " + I18n.T("main.donate"));
            don.Click += (s, e) => ShowDonate();
            items.Add(don);
            don.ForeColor = Ui.Heart;
            items.Add(new ToolStripSeparator());
            items.Add(I18n.T("menu.exit"), null, async (s, e) => await ExitAsync());
        }

        // =====================================================================
        // Ports
        // =====================================================================
        /// <summary>Checks the ports of the services about to start. Returns false when the user cancelled.</summary>
        async Task<bool> ResolvePorts(IEnumerable<Service> services)
        {
            var list = services.ToList();
            SetStatus(I18n.T("port.checking"));
            var conflicts = await Task.Run(() => PortGuard.Check(list));
            if (conflicts.Count == 0) return true;
            ShowFromTray();
            PortChoice choice;
            using (var f = new PortConflictForm(conflicts))
            {
                f.ShowDialog(this);
                choice = f.Choice;
            }
            if (choice == PortChoice.UseFreePorts)
            {
                var summary = PortGuard.UseSuggestions(conflicts);
                SetStatus(I18n.T("port.changed", summary));
                foreach (var r in _rows) UpdateRow(r.Svc);
                return true;
            }
            if (choice == PortChoice.CloseOwners)
            {
                var failed = await Task.Run(() => PortGuard.CloseOwners(conflicts));
                if (failed.Count == 0) return true;
                Ui.Error(this, I18n.T("port.stillUsed", string.Join("\n", failed)));
            }
            return false;
        }

        /// <summary>"Fix" link on a card, or Tools > Check ports: resolve and start what failed.</summary>
        async Task FixPorts(IEnumerable<Service> services)
        {
            var list = services.Where(s => !s.IsActive).ToList();
            if (list.Count == 0) return;
            var conflicts = await Task.Run(() => PortGuard.Check(list));
            if (conflicts.Count == 0)
            {
                Ui.Info(this, I18n.T("port.allFree"));
                return;
            }
            if (!await ResolvePorts(list)) return;
            foreach (var s in conflicts.Select(c => c.Service).Distinct()) await ServiceManager.StartAsync(s);
        }

        // =====================================================================
        // Actions
        // =====================================================================
        async Task ToggleAll()
        {
            if (_busyAll) return;
            _busyAll = true;
            _startAll.Enabled = false;
            try
            {
                if (ServiceManager.AnyActive) await ServiceManager.StopAllAsync();
                else
                {
                    if (!PackageTypes.Installed("apache").Any() && !PackageTypes.Installed("nginx").Any() && !PackageTypes.Installed(Settings.Current.DbEngine).Any())
                    {
                        OpenPackages(null, true);
                        return;
                    }
                    var s = Settings.Current;
                    var toStart = ServiceManager.Rows.Where(x => x.Id != "php" && s.IsEnabled(x.Id)).ToList();
                    if (!await ResolvePorts(toStart)) return;
                    CheckVcRuntime(false);
                    await ServiceManager.StartAllAsync();
                    if (ServiceManager.Web.State == ServiceState.Running) AskTrustCa();
                }
            }
            finally
            {
                _busyAll = false;
                _startAll.Enabled = true;
                UpdateGlobal();
            }
        }

        void OpenLocalhost()
        {
            var s = Settings.Current;
            bool https = s.Ssl && Certs.IsCaTrusted();
            var port = https ? s.HttpsPort : s.HttpPort;
            var def = https ? 443 : 80;
            WinUtil.Open((https ? "https" : "http") + "://localhost" + (port == def ? "" : ":" + port) + "/");
        }

        void OpenMailpit()
        {
            if (PackageTypes.ActiveDir(Settings.Current.MailTool) == null) { OpenPackages(Settings.Current.MailTool); return; }
            if (!ServiceManager.Mailpit.IsActive) SetStatus(I18n.T("main.mailNotRunning"));
            WinUtil.Open("http://127.0.0.1:" + Settings.Current.MailpitWebPort + "/");
        }

        /// <summary>All database tools: phpMyAdmin / Adminer (web), HeidiSQL, DBeaver, Beekeeper, DbGate, DB Browser, pgAdmin.</summary>
        void OpenDatabaseTool(Control anchor = null)
        {
            var m = new ContextMenuStrip();
            StyleMenu(m);
            BuildDatabaseToolItems(m.Items);
            if (m.Items.Count == 1) { m.Items[0].PerformClick(); return; }
            if (anchor != null) m.Show(anchor, new Point(0, anchor.Height));
            else m.Show(Cursor.Position);
        }

        void BuildDatabaseToolItems(ToolStripItemCollection items)
        {
            var port = Settings.Current.HttpPort;
            foreach (var t in new[] { "phpmyadmin", "adminer" })
            {
                var tool = t;
                if (PackageTypes.ActiveDir(tool) == null) continue;
                items.Add(PackageTypes.Get(tool).Name + "   (web)", null, (s, e) => WinUtil.Open("http://localhost" + (port == 80 ? "" : ":" + port) + "/" + tool + "/"));
            }
            foreach (var t in DesktopApps.DatabaseGuis)
            {
                var tool = t;
                if (!DesktopApps.IsInstalled(tool)) continue;
                items.Add(PackageTypes.Get(tool).Name, null, (s, e) => DesktopApps.OpenDatabaseGui(tool));
            }
            if (DesktopApps.PgAdmin != null) items.Add("pgAdmin 4", null, (s, e) => DesktopApps.Start(DesktopApps.PgAdmin));
            if (items.Count > 0) items.Add(new ToolStripSeparator());
            items.Add(I18n.T("db.moreTools"), null, (s, e) => OpenPackages("heidisql"));
        }

        void NewProject(string template = null)
        {
            ShowFromTray();
            using (var f = new NewProjectForm(template))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                var created = f.CreatedName;
                var openAfter = f.OpenAfter;
                Task.Run(async () =>
                {
                    await ServiceManager.ReloadWebAsync();
                    if (openAfter && ServiceManager.Web.State == ServiceState.Running)
                    {
                        var site = ServiceManager.CurrentSites.FirstOrDefault(x => x.Name == created && x.Valid);
                        if (site != null) WinUtil.Open(site.PreferredUrl);
                    }
                });
            }
        }

        void CreateDatabase(string suggested)
        {
            ShowFromTray();
            DatabaseUi.NewDatabase(this, suggested);
        }

        async Task BackupDatabases()
        {
            ShowFromTray();
            SetStatus(I18n.T("db.backingUp"));
            try
            {
                var file = await Task.Run(() => Database.BackupAll());
                Ui.Info(this, I18n.T("db.backedUp", file));
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        void ChangeRootPassword()
        {
            ShowFromTray();
            if (ServiceManager.MySql.State != ServiceState.Running) { Ui.Error(this, I18n.T("err.mysqlNotRunning")); return; }
            var pass = Ui.Prompt(this, I18n.T("db.password"), I18n.T("db.newPassword"), "", true);
            if (pass == null) return;
            var err = Database.ChangeRootPassword(pass);
            if (err != null) Ui.Error(this, err);
            else Ui.Info(this, I18n.T("db.passwordChanged"));
        }

        void TrustCa()
        {
            ShowFromTray();
            if (Certs.TrustCa()) Ui.Info(this, I18n.T("ssl.trustedDone"));
        }

        void AskTrustCa()
        {
            if (!Settings.Current.Ssl || Certs.IsCaTrusted()) return;
            var ini = Settings.Current.Raw;
            if (ini.GetBool("general", "CaTrustAsked", false)) return;
            ini.Set("general", "CaTrustAsked", true);
            Settings.Current.Save();
            if (Ui.Confirm(this, I18n.T("ssl.ask"))) TrustCa();
        }

        async void OpenSettings()
        {
            ShowFromTray();
            var docRoot = Settings.Current.DocumentRoot;
            SettingsTransfer.ImportResult imported;
            using (var f = new SettingsForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                imported = f.Imported;
            }
            if (docRoot != Settings.Current.DocumentRoot) WatchDocRoot();
            RebuildTheme();
            if (imported == null) return;
            var msg = I18n.T("transfer.imported");
            if (imported.DocumentRootKept) msg += "\n\n" + I18n.T("transfer.docRootKept", Settings.Current.DocumentRoot);
            if (imported.Manual.Count > 0) msg += "\n\n" + I18n.T("transfer.manual", string.Join(", ", imported.Manual));
            if (imported.Missing.Count > 0) msg += "\n\n" + I18n.T("transfer.missing", imported.Missing.Count, I18n.T("pkg.installSelected"));
            Ui.Info(this, msg);
            if (imported.Missing.Count > 0) OpenPackages(null, false, imported.Missing);
            await ServiceManager.ReloadWebAsync();
        }

        void OpenPackages(string type, bool firstRun = false, IEnumerable<PackageEntry> preselect = null)
        {
            ShowFromTray();
            using (var f = new PackagesForm(type, firstRun, preselect)) f.ShowDialog(this);
            RefreshVersions();
            UpdateBanner();
            CheckVcRuntime(false);
            Task.Run(() => ServiceManager.RefreshSites());
        }

        /// <summary>Apache, PHP and MySQL need the Microsoft Visual C++ runtime.</summary>
        void CheckVcRuntime(bool quiet)
        {
            int need = 0;
            foreach (var t in PackageTypes.All)
            {
                var f = PackageTypes.ActiveFolder(t.Id);
                if (f == "") continue;
                var e = Catalog.FindByFolder(t.Id, f);
                if (e != null) need = Math.Max(need, e.Vc);
                else if (f.Contains("VS18")) need = Math.Max(need, 50);
            }
            if (need == 0) return;
            var have = WinUtil.VcRuntimeMinor();
            if (have >= need) return;
            var ini = Settings.Current.Raw;
            if (quiet && ini.Get("general", "VcAskedFor", "") == need.ToString()) return;
            ini.Set("general", "VcAskedFor", need.ToString());
            Settings.Current.Save();
            if (!Ui.Confirm(this, I18n.T("vc.ask"))) return;
            var file = Path.Combine(Paths.Downloads, "vc_redist.x64.exe");
            SetStatus(I18n.T("vc.downloading"));
            Task.Run(async () =>
            {
                try
                {
                    await Net.DownloadFileAsync(Catalog.VcRedistUrl, file, null, CancellationToken.None);
                    var code = WinUtil.RunElevated(file, "/install /passive /norestart");
                    Log.Info(I18n.T("vc.done", code));
                }
                catch (Exception ex) { Log.Error("VC++", ex); }
            });
        }

        // ---------------- security / antivirus ----------------
        void UpdateSecurityPill(Row row)
        {
            if (row.Sec == null) return;
            var st = row.Svc.IsInstalled ? SecurityInfo.StatusOf(row.Svc.PackageType, PackageTypes.ActiveFolder(row.Svc.PackageType)) : null;
            row.Sec.Visible = st != null;
            if (st == null) return;
            row.Sec.Text = st.Short;
            row.Sec.Color = st.Advisories.Count > 0 ? Theme.Current.Red : Theme.Current.Orange;
            row.Sec.AccessibleName = st.Long;
            var tip = row.Sec.Tag as ToolTip;
            if (tip == null) row.Sec.Tag = tip = new ToolTip();
            tip.SetToolTip(row.Sec, st.Long + (st.Fix != null ? "\n" + I18n.T("sec.clickToUpdate", st.Fix.Version) : ""));
        }

        /// <summary>Pill click: one-click update to the fixed build, otherwise the packages window of the type.</summary>
        async Task OnSecurityPill(Row row)
        {
            var type = row.Svc.PackageType;
            var folder = PackageTypes.ActiveFolder(type);
            var st = SecurityInfo.StatusOf(type, folder);
            if (st == null || st.Fix == null || st.Fix.IsInstalled) { OpenPackages(type); return; }
            var name = PackageTypes.Get(type).Name + " " + st.Fix.Version;
            if (!Ui.Confirm(this, I18n.T("sec.updateConfirm", st.Long, name))) return;
            row.Sec.Enabled = false;
            try
            {
                await Installer.UpdateAsync(folder, st.Fix, p => SafeInvoke(() => SetStatus(name + " - " + I18n.T("pkg.stage." + p.Stage) + (p.Percent >= 0 && p.Stage == "download" ? " " + p.Percent + "%" : ""))), CancellationToken.None);
                Terminal.ApplyUserPath();
                RefreshVersions();
                await ApplyVersionChange(type);
                UpdateNoticeBar.OfferRemoveOld(this, new[] { Tuple.Create(type, folder) });
                SetStatus(I18n.T("log.installed", name));
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
            finally { row.Sec.Enabled = true; RefreshVersions(); UpdateBanner(); }
        }

        /// <summary>Start-up: are the .exe/.dll files of the active versions still there? (existence + size only, fast)</summary>
        async Task QuickIntegrityCheck()
        {
            List<IntegrityProblem> problems;
            try { problems = await Task.Run(() => Integrity.QuickCheckActive()); } catch { return; }
            if (problems.Count == 0) return;
            foreach (var p in problems) Log.Warn(I18n.T("verify.problem", p.Count, p.Title));
            if (Visible) ShowVerify(problems);
            else _tray.ShowBalloonTip(8000, "Portly", I18n.T("verify.problem", problems[0].Count, problems[0].Title), ToolTipIcon.Warning);
        }

        void ShowVerify(List<IntegrityProblem> problems)
        {
            ShowFromTray();
            bool repaired;
            using (var f = new VerifyPackagesForm(problems)) { f.ShowDialog(this); repaired = f.Repaired; }
            if (repaired) { RefreshVersions(); Task.Run(() => ServiceManager.RefreshSites()); }
        }

        // ---------------- updates ----------------
        void UpdateBanner()
        {
            if (_updateBtn == null) return;
            if (Updater.Available != null)
            {
                _updateBtn.Text = I18n.T("update.short", Updater.Available.Version);
                _updateBtn.Kind = ButtonKind.Primary;
                _updateBtn.Visible = true;
                return;
            }
            if (_notices != null) _notices.Reload();
            var pending = AppInfo.UpdatesSupported ? Catalog.PendingUpdates().Count : 0;
            _updateBtn.Text = I18n.T("update.packages", pending);
            _updateBtn.Kind = ButtonKind.Secondary;
            _updateBtn.Visible = pending > 0;
        }

        async void OnBannerClick()
        {
            var u = Updater.Available;
            if (u == null) { OpenPackages(null); return; }
            var msg = I18n.T("update.confirm", u.Version, AppInfo.VersionText) + (string.IsNullOrEmpty(u.Notes) ? "" : "\n\n" + u.Notes);
            if (!Ui.Confirm(this, msg)) return;
            _updateBtn.Enabled = false;
            try
            {
                await Updater.ApplyAsync(u, p => SafeInvoke(() => SetStatus(I18n.T("update.downloading", p.Percent < 0 ? Ui.FormatBytes(p.Bytes) : p.Percent + "%"))), CancellationToken.None);
                bool wasRunning = ServiceManager.AnyActive;
                await ServiceManager.StopAllAsync();
                Updater.Restart(wasRunning);
                _exiting = true;
                _tray.Visible = false;
                Application.Exit();
            }
            catch (Exception ex)
            {
                _updateBtn.Enabled = true;
                Ui.Error(this, I18n.T("update.failed", ex.Message));
            }
        }

        async Task BackgroundChecks(bool force, bool interactive)
        {
            await Certs.RefreshBundleAsync();
            if (!AppInfo.UpdatesSupported) return;
            await Donate.RefreshAsync();
            var s = Settings.Current;
            // the app itself every 12 hours; new package versions at the first start of a day
            bool appDue = force, pkgDue = force;
            if (!force && s.CheckUpdates)
            {
                DateTime last;
                appDue = !(DateTime.TryParse(s.LastUpdateCheck, out last) && last > DateTime.Now.AddHours(-12));
                pkgDue = UpdateNotices.DailyCheckDue;
            }
            if (!appDue && !pkgDue) { SafeInvoke(UpdateBanner); return; }
            try
            {
                await Catalog.RefreshAsync();
                UpdateNotices.MarkChecked();
                var u = appDue ? await Updater.CheckAsync() : Updater.Available;
                SafeInvoke(() =>
                {
                    UpdateBanner();
                    if (interactive && u == null && UpdateNotices.Current.Count == 0) Ui.Info(this, I18n.T("update.none", AppInfo.VersionText));
                    if (interactive && UpdateNotices.Current.Count > 0) { ShowFromTray(); ShowPage("services"); }
                    if (u != null && !Visible) _tray.ShowBalloonTip(5000, "Portly", I18n.T("update.banner", u.Version), ToolTipIcon.Info);
                });
            }
            catch (Exception ex)
            {
                Log.Warn(I18n.T("update.checkFailed", ex.Message));
                if (interactive) SafeInvoke(() => Ui.Error(this, I18n.T("update.checkFailed", ex.Message)));
            }
        }

        // ---------------- document root watcher ----------------
        void WatchDocRoot()
        {
            try
            {
                if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
                var root = Settings.Current.DocumentRoot;
                Directory.CreateDirectory(root);
                // two levels deep (group folders), but only folder changes and project marker files count:
                // saving a file inside a project never restarts the web server
                _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName, InternalBufferSize = 32768 };
                Action<string, bool> consider = (fullPath, isDir) =>
                {
                    var rel = fullPath.Length > root.Length ? fullPath.Substring(root.Length).Trim('\\') : "";
                    int depth = rel.Split('\\').Length;
                    var file = Path.GetFileName(fullPath).ToLowerInvariant();
                    bool marker = file == "index.php" || file == "index.html" || file == "index.htm" || file == "composer.json" || file == "package.json" || file == "wp-config.php" || file == "public";
                    if ((isDir && depth <= 2) || (marker && depth <= 3))
                        SafeInvoke(() => { _debounce.Stop(); _debounce.Start(); });
                };
                FileSystemEventHandler h = (s, e) => consider(e.FullPath, Directory.Exists(e.FullPath) || e.ChangeType == WatcherChangeTypes.Deleted && !Path.HasExtension(e.FullPath));
                _watcher.Created += h;
                _watcher.Deleted += h;
                _watcher.Renamed += (s, e) => { consider(e.FullPath, Directory.Exists(e.FullPath)); consider(e.OldFullPath, Directory.Exists(e.FullPath)); };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Warn("Watcher: " + ex.Message); }
        }

        static string SitesKey(IEnumerable<Site> sites)
        {
            return string.Join("|", sites.Select(x => x.Host + ">" + x.DocRoot + ">" + x.Valid + ">" + x.IsGroup));
        }

        async Task OnDocRootChanged()
        {
            // nothing that matters for the web server changed (e.g. a file inside a project)
            var before = SitesKey(ServiceManager.CurrentSites);
            var now = await Task.Run(() => SitesKey(Sites.Scan()));
            if (before == now) return;
            if (Settings.Current.AutoVhosts && ServiceManager.Web.State == ServiceState.Running)
            {
                Log.Info(I18n.T("log.sitesChanged"));
                await ServiceManager.ReloadWebAsync();
            }
            else await Task.Run(() => ServiceManager.RefreshSites());
        }

        // =====================================================================
        // Window lifecycle
        // =====================================================================
        async void Startup()
        {
            Log.Info(I18n.T("log.welcome", AppInfo.VersionText, AppInfo.EditionName, OsInfo.DisplayName));
            await Task.Run(() => ServiceManager.RefreshSites());
            WatchDocRoot();
            Terminal.ApplyUserPath();
            if (ServiceMode.IsController) ServiceMode.StartPolling(); // states come from the Portly Windows service

            var s = Settings.Current;
            if (!s.OsWarningShown)
            {
                s.OsWarningShown = true;
                s.Save();
                if (!AppInfo.IsLegacy && !OsInfo.IsWin10OrLater) Ui.Error(this, I18n.T("os.useLegacy", OsInfo.DisplayName));
                if (AppInfo.IsLegacy && OsInfo.IsWin10OrLater) Ui.Info(this, I18n.T("os.useModern"));
            }
            if (Paths.HasSpaceInRoot) Log.Warn(I18n.T("warn.spaceInPath"));
            if (RunningFromTemp() && Ui.Confirm(this, I18n.T("warn.runningFromTemp")))
            {
                MoveToCPortly();
                return;
            }

            bool nothingInstalled = PackageTypes.All.All(t => PackageTypes.Installed(t.Id).Count == 0);
            bool startFromWelcome = false;
            if (s.ShowWelcome && !_startHidden)
                startFromWelcome = ShowWelcome(nothingInstalled);
            else if (nothingInstalled && !s.FirstRunDone)
                OpenPackages(null, true);
            s.FirstRunDone = true;
            s.Save();
            if (!nothingInstalled) CheckVcRuntime(true);

            // automatic database backup (off by default): checked every 10 minutes
            var backupTimer = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 };
            backupTimer.Tick += (o, e) => Task.Run(() =>
            {
                try { Database.AutoBackupIfDue(); } catch (Exception ex) { Log.Warn("Auto backup: " + ex.Message); }
            });
            backupTimer.Start();

            await ShowHealth(false);
            await QuickIntegrityCheck();
            var checks = BackgroundChecks(false, false);
            if (_startServices || s.StartServicesOnLaunch || startFromWelcome) await ToggleAll();
            await checks;
        }

        /// <summary>Started from inside a zip (WinRAR / Explorer extract to %TEMP%): everything installed would be lost.</summary>
        static bool RunningFromTemp()
        {
            var temp = Path.GetTempPath().TrimEnd('\\');
            return Paths.Root.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Copy the program files to C:\Portly and start from there.</summary>
        void MoveToCPortly()
        {
            var target = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Portly");
            try
            {
                Directory.CreateDirectory(target);
                foreach (var f in new[] { "Portly.exe", "Portly.exe.config", "LICENSE.txt", "README.txt" })
                    if (File.Exists(Path.Combine(Paths.Root, f))) File.Copy(Path.Combine(Paths.Root, f), Path.Combine(target, f), true);
                foreach (var d in new[] { "lang", "templates" })
                    foreach (var f in Directory.GetFiles(Path.Combine(Paths.Root, d), "*", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(target, f.Substring(Paths.Root.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(f, dest, true);
                    }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(target, "Portly.exe")) { WorkingDirectory = target });
                _exiting = true;
                _tray.Visible = false;
                Application.Exit();
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        /// <summary>Laragon / XAMPP import. quiet = only open when something was found (first start).</summary>
        async void OpenImport(bool interactive, List<ImportSource> known = null)
        {
            var sources = known ?? await Task.Run(() => Importer.Detect());
            if (sources.Count == 0)
            {
                if (interactive) Ui.Info(this, I18n.T("import.none"));
                return;
            }
            if (!interactive && !Ui.Confirm(this, I18n.T("import.offer", string.Join(", ", sources.Select(x => x.Name))))) return;
            ShowFromTray();
            using (var f = new ImportForm(sources))
            {
                f.ShowDialog(this);
                if (f.ProjectsChanged) { WatchDocRoot(); await ServiceManager.ReloadWebAsync(); RebuildTheme(); }
            }
        }

        /// <summary>Start screen. Returns true when the user chose "Start all".</summary>
        bool ShowWelcome(bool nothingInstalled)
        {
            WelcomeAction action;
            List<ImportSource> sources;
            using (var f = new WelcomeForm())
            {
                f.ShowDialog(this);
                action = f.Action;
                sources = f.Sources;
            }
            switch (action)
            {
                case WelcomeAction.Packages: OpenPackages(null, nothingInstalled); break;
                case WelcomeAction.Import: OpenImport(true, sources); break;
                case WelcomeAction.StartAll: return true;
            }
            return false;
        }

        /// <summary>Environment problems (OneDrive, non-ASCII paths, long paths, other PHP ...). Each is shown once unless asked.</summary>
        async Task ShowHealth(bool all)
        {
            List<HealthIssue> issues;
            try { issues = await Task.Run(() => HealthCheck.Run()); } catch { return; }
            foreach (var i in issues) Log.Warn(i.Text);
            var ini = Settings.Current.Raw;
            var shown = ini.Get("general", "HealthShown", "").Split('|').ToList();
            var show = all ? issues : issues.Where(i => !shown.Contains(i.Key)).ToList();
            if (show.Count == 0)
            {
                if (all) Ui.Info(this, I18n.T("health.ok"));
                return;
            }
            if (!Visible) return;
            using (var f = new HealthForm(show)) f.ShowDialog(this);
            ini.Set("general", "HealthShown", string.Join("|", shown.Concat(show.Select(i => i.Key)).Distinct().Where(k => k != "")));
            Settings.Current.Save();
        }

        void ShowDonate()
        {
            ShowFromTray();
            using (var f = new DonateForm()) f.ShowDialog(this);
        }

        void ShowFromTray()
        {
            if (!Visible) Show();
            ShowInTaskbar = true;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        /// <summary>Called by the single-instance signal (Portly started a second time).</summary>
        public void BringToFrontFromOtherInstance() { SafeInvoke(ShowFromTray); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_exiting) { base.OnFormClosing(e); return; }
            if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                _exiting = true;
                if (!ServiceMode.IsController) try { ServiceManager.StopAllAsync().Wait(15000); } catch { }
                _tray.Visible = false;
                base.OnFormClosing(e);
                return;
            }
            e.Cancel = true;
            if (Settings.Current.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloonTip(3000, "Portly", I18n.T("tray.hint"), ToolTipIcon.Info);
                }
                return;
            }
            var _ = ExitAsync();
        }

        public async Task ExitAsync()
        {
            if (_exiting) return;
            _exiting = true;
            SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
            Share.StopAll();
            SetStatus(I18n.T("main.stopping"));
            Enabled = false;
            // service mode: the Windows service keeps running without the window
            if (!ServiceMode.IsController) await Task.WhenAny(ServiceManager.StopAllAsync(), Task.Delay(30000));
            _tray.Visible = false;
            _tray.Dispose();
            Application.Exit();
        }
    }
}
