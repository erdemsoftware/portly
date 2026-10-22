using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
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
            public StatusDot Dot;
            public Label Name;
            public ComboBox Version;
            public Label Port;
            public Button Action;
            public CheckBox Auto;
        }

        readonly bool _startHidden;
        readonly bool _startServices;
        bool _exiting, _trayHintShown, _busyAll;
        NotifyIcon _tray;
        ContextMenuStrip _menu;
        Panel _pageServices, _pageSites, _pageLog;
        Button _navServices, _navSites, _navLog, _banner, _startAll;
        Label _status, _errors, _sitesInfo;
        TableLayoutPanel _grid;
        readonly Dictionary<Service, Row> _rows = new Dictionary<Service, Row>();
        ListView _sites;
        TextBox _log;
        FileSystemWatcher _watcher;
        System.Windows.Forms.Timer _debounce;
        string _page = "services";

        public MainForm(bool startHidden, bool startServices)
        {
            _startHidden = startHidden;
            _startServices = startServices;
            Ui.Apply(this);
            Text = "Portly";
            Size = Ui.S(800, 540);
            MinimumSize = Ui.S(700, 460);
            StartPosition = FormStartPosition.CenterScreen;

            _menu = new ContextMenuStrip { Font = Ui.Font };
            _menu.Opening += (s, e) => { BuildMenu(_menu.Items); e.Cancel = false; };
            _tray = new NotifyIcon { Icon = Ui.TrayIcon(null), Text = "Portly", Visible = true, ContextMenuStrip = _menu };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

            _debounce = new System.Windows.Forms.Timer { Interval = 2000 };
            _debounce.Tick += async (s, e) => { _debounce.Stop(); await OnDocRootChanged(); };

            ServiceManager.Changed += svc => SafeInvoke(() => { UpdateRow(svc); UpdateGlobal(); });
            ServiceManager.SitesChanged += () => SafeInvoke(FillSites);
            Log.Written += line => SafeInvoke(() => AppendLog(line));
            Catalog.Changed += () => SafeInvoke(UpdateBanner);

            BuildUi();
            if (_startHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }

            // startup work runs once the message loop is up, whether the window is shown or not (tray start)
            var boot = new System.Windows.Forms.Timer { Interval = 200 };
            boot.Tick += (s, e) => { boot.Stop(); boot.Dispose(); Startup(); };
            boot.Start();
        }

        protected override void SetVisibleCore(bool value)
        {
            // start in the tray without flashing the window
            if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        void SafeInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); } catch { }
        }

        // =====================================================================
        // Layout
        // =====================================================================
        void BuildUi()
        {
            SuspendLayout();
            Controls.Clear();
            _rows.Clear();

            // footer
            var footer = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(30), BackColor = Ui.Card, Padding = Ui.P(14, 0, 14, 0) };
            _status = Ui.Label(I18n.T("main.ready"), Ui.Small, Ui.Muted);
            _status.Dock = DockStyle.Left;
            _status.AutoSize = false;
            _status.Width = Ui.S(520);
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.AutoEllipsis = true;
            var donate = Ui.Link("♥ " + I18n.T("main.donate"), () => WinUtil.Open(AppInfo.DonateUrl));
            donate.LinkColor = Ui.Heart;
            donate.Dock = DockStyle.Right;
            donate.AutoSize = true;
            donate.Padding = Ui.P(0, 7, 0, 0);
            footer.Controls.Add(_status);
            footer.Controls.Add(donate);
            Controls.Add(footer);
            Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Line });

            // navigation
            var nav = new Panel { Dock = DockStyle.Top, Height = Ui.S(46), BackColor = Ui.Bg, Padding = Ui.P(14, 8, 14, 4) };
            var navFlow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            _navServices = NavButton(I18n.T("nav.services"), "services");
            _navSites = NavButton(I18n.T("nav.sites"), "sites");
            _navLog = NavButton(I18n.T("nav.log"), "log");
            navFlow.Controls.AddRange(new Control[] { _navServices, _navSites, _navLog });
            _banner = Ui.Button("", (s, e) => OnBannerClick());
            _banner.Dock = DockStyle.Right;
            _banner.BackColor = Color.FromArgb(254, 243, 199);
            _banner.FlatAppearance.BorderColor = Ui.Orange;
            _banner.Visible = false;
            nav.Controls.Add(navFlow);
            nav.Controls.Add(_banner);
            Controls.Add(nav);
            Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Ui.Line });

            // header
            var header = new Panel { Dock = DockStyle.Top, Height = Ui.S(64), BackColor = Ui.Card };
            var logo = new PictureBox { Image = Ui.AppImage(Ui.S(32) >= 48 ? 48 : 32), SizeMode = PictureBoxSizeMode.Zoom, Size = Ui.S(34, 34), Location = new Point(Ui.S(16), Ui.S(15)) };
            var title = Ui.Label("Portly", Ui.Title);
            title.Location = new Point(Ui.S(56), Ui.S(8));
            var sub = Ui.Label(I18n.T("main.subtitle", AppInfo.VersionText, AppInfo.EditionName), Ui.Small, Ui.Muted);
            sub.Location = new Point(Ui.S(58), Ui.S(38));
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = Ui.P(0, 16, 10, 0), BackColor = Color.Transparent };
            var menuBtn = Ui.Button(I18n.T("main.menu") + " ▾", null);
            menuBtn.Click += (s, e) => { BuildMenu(_menu.Items); _menu.Show(menuBtn, new Point(0, menuBtn.Height)); };
            right.Controls.Add(menuBtn);
            right.Controls.Add(Ui.Button(I18n.T("main.settings"), (s, e) => OpenSettings()));
            right.Controls.Add(Ui.Button(I18n.T("main.packages"), (s, e) => OpenPackages(null)));
            header.Controls.Add(logo);
            header.Controls.Add(title);
            header.Controls.Add(sub);
            header.Controls.Add(right);
            Controls.Add(header);

            // pages
            var content = new Panel { Dock = DockStyle.Fill, Padding = Ui.P(16, 12, 16, 12), BackColor = Ui.Bg };
            _pageServices = BuildServicesPage();
            _pageSites = BuildSitesPage();
            _pageLog = BuildLogPage();
            content.Controls.Add(_pageServices);
            content.Controls.Add(_pageSites);
            content.Controls.Add(_pageLog);
            Controls.Add(content);
            content.BringToFront();

            ResumeLayout(true);
            ShowPage(_page);
            foreach (var r in _rows.Values) UpdateRow(r.Svc);
            UpdateGlobal();
            FillSites();
            UpdateBanner();
        }

        Button NavButton(string text, string page)
        {
            var b = Ui.Button(text, (s, e) => ShowPage(page));
            b.Tag = page;
            b.Margin = Ui.P(0, 0, 4, 0);
            return b;
        }

        void ShowPage(string page)
        {
            _page = page;
            _pageServices.Visible = page == "services";
            _pageSites.Visible = page == "sites";
            _pageLog.Visible = page == "log";
            foreach (var b in new[] { _navServices, _navSites, _navLog })
            {
                bool on = (string)b.Tag == page;
                b.BackColor = on ? Ui.Accent : Ui.Card;
                b.ForeColor = on ? Color.White : Ui.Text;
                b.FlatAppearance.BorderColor = on ? Ui.AccentDark : Ui.Line;
                b.FlatAppearance.MouseOverBackColor = on ? Ui.AccentDark : Color.FromArgb(238, 242, 255);
            }
        }

        // ---------------- services page ----------------
        Panel BuildServicesPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };

            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = Ui.P(0, 8, 0, 0) };
            _startAll = Ui.Button(I18n.T("main.startAll"), async (s, e) => await ToggleAll(), true);
            actions.Controls.Add(_startAll);
            actions.Controls.Add(Ui.Button(I18n.T("main.web"), (s, e) => OpenLocalhost()));
            actions.Controls.Add(Ui.Button(I18n.T("main.database"), (s, e) => OpenDatabaseTool()));
            actions.Controls.Add(Ui.Button(I18n.T("main.terminal"), (s, e) => Terminal.Open()));
            actions.Controls.Add(Ui.Button(I18n.T("main.root"), (s, e) => WinUtil.OpenFolder(Settings.Current.DocumentRoot)));
            actions.Controls.Add(Ui.Button(I18n.T("main.newProject"), (s, e) => NewProject()));

            var card = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Ui.Card, Padding = Ui.P(14, 10, 14, 10) };
            _grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, BackColor = Ui.Card };
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(22)));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(110)));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(150)));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(170)));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(100)));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _grid.Controls.Add(new Label(), 0, 0);
            _grid.Controls.Add(Ui.Label(I18n.T("col.service"), Ui.Small, Ui.Muted), 1, 0);
            _grid.Controls.Add(Ui.Label(I18n.T("col.version"), Ui.Small, Ui.Muted), 2, 0);
            _grid.Controls.Add(Ui.Label(I18n.T("col.port"), Ui.Small, Ui.Muted), 3, 0);
            _grid.Controls.Add(new Label(), 4, 0);
            _grid.Controls.Add(Ui.Label(I18n.T("col.auto"), Ui.Small, Ui.Muted), 5, 0);

            int rowIndex = 1;
            foreach (var svc in ServiceManager.Rows)
            {
                var row = new Row { Svc = svc };
                row.Dot = new StatusDot();
                row.Name = Ui.Label(svc.Title, Ui.Bold);
                row.Name.Margin = Ui.P(0, 7, 6, 0);
                row.Version = new ComboBox { AccessibleName = svc.Title + " " + I18n.T("col.version"), DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(140), DropDownWidth = Ui.S(260), Margin = Ui.P(0, 3, 6, 3), FlatStyle = FlatStyle.System };
                FillVersions(row);
                row.Version.SelectionChangeCommitted += async (s, e) => await OnVersionChanged(row);
                row.Port = Ui.Label("", Ui.Font, Ui.Muted);
                row.Port.Margin = Ui.P(0, 7, 6, 0);
                row.Action = Ui.Button(I18n.T("svc.start"), async (s, e) => await OnRowAction(row));
                row.Action.MinimumSize = new Size(Ui.S(90), Ui.S(28));
                row.Action.Margin = Ui.P(0, 2, 6, 2);
                row.Auto = new CheckBox { AutoSize = true, Margin = Ui.P(6, 7, 0, 0), Checked = Settings.Current.IsEnabled(svc.Id), AccessibleName = svc.Title + ": " + I18n.T("col.auto") };
                row.Auto.CheckedChanged += (s, e) => { Settings.Current.SetEnabled(svc.Id, row.Auto.Checked); Settings.Current.Save(); };
                if (svc.Id == "php")
                {
                    row.Action.Visible = false;
                    row.Auto.Visible = false;
                }
                _grid.Controls.Add(row.Dot, 0, rowIndex);
                _grid.Controls.Add(row.Name, 1, rowIndex);
                _grid.Controls.Add(row.Version, 2, rowIndex);
                _grid.Controls.Add(row.Port, 3, rowIndex);
                _grid.Controls.Add(row.Action, 4, rowIndex);
                _grid.Controls.Add(row.Auto, 5, rowIndex);
                _rows[svc] = row;
                rowIndex++;
            }
            card.Controls.Add(_grid);

            _errors = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Ui.Red, Padding = Ui.P(2, 10, 2, 0), MaximumSize = new Size(Ui.S(740), 0) };
            var hint = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = Ui.Muted,
                Font = Ui.Small,
                Padding = Ui.P(2, 10, 2, 0),
                MaximumSize = new Size(Ui.S(740), 0),
                Text = I18n.T("main.hint", Settings.Current.DocumentRoot, Settings.Current.Tld),
            };

            page.Controls.Add(actions);
            page.Controls.Add(hint);
            page.Controls.Add(_errors);
            page.Controls.Add(card);
            return page;
        }

        void FillVersions(Row row)
        {
            var type = row.Svc.PackageType;
            row.Version.Items.Clear();
            var installed = PackageTypes.Installed(type);
            var active = PackageTypes.ActiveFolder(type);
            foreach (var f in installed) row.Version.Items.Add(new Item(PackageTypes.Label(f), f));
            if (installed.Count == 0) row.Version.Items.Add(new Item(I18n.T("svc.notInstalled"), ""));
            row.Version.Items.Add(new Item(I18n.T("svc.getVersions"), "__get"));
            int idx = installed.FindIndex(f => string.Equals(f, active, StringComparison.OrdinalIgnoreCase));
            row.Version.SelectedIndex = Math.Max(0, idx);
            row.Version.Tag = row.Version.SelectedIndex;
        }

        void RefreshVersions()
        {
            foreach (var r in _rows.Values) { FillVersions(r); UpdateRow(r.Svc); }
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
            if (string.Equals(PackageTypes.ActiveFolder(type), item.Value, StringComparison.OrdinalIgnoreCase)) return;
            Settings.Current.SetVersion(type, item.Value);
            Settings.Current.Save();
            Log.Info(I18n.T("log.switched", PackageTypes.Get(type).Name, PackageTypes.Label(item.Value)));
            Terminal.ApplyUserPath();
            await ApplyVersionChange(type);
        }

        async Task ApplyVersionChange(string type)
        {
            CheckVcRuntime(false);
            switch (type)
            {
                case "php":
                case "apache":
                case "nginx":
                case "adminer":
                case "phpmyadmin":
                case "mailpit":
                    if (type == "mailpit" && ServiceManager.Mailpit.IsActive) await ServiceManager.Mailpit.RestartAsync();
                    await ServiceManager.ReloadWebAsync();
                    break;
                case "mysql":
                    if (ServiceManager.MySql.IsActive) await ServiceManager.MySql.RestartAsync();
                    break;
                case "redis":
                    if (ServiceManager.Redis.IsActive) await ServiceManager.Redis.RestartAsync();
                    break;
            }
        }

        async Task OnRowAction(Row row)
        {
            var svc = row.Svc;
            if (!svc.IsInstalled) { OpenPackages(svc.PackageType); return; }
            row.Action.Enabled = false;
            try
            {
                if (svc.IsActive) await ServiceManager.StopAsync(svc);
                else
                {
                    CheckVcRuntime(false);
                    await ServiceManager.StartAsync(svc);
                    if (svc.Id == "web" && svc.State == ServiceState.Running) AskTrustCa();
                }
            }
            finally { row.Action.Enabled = true; UpdateRow(svc); }
        }

        void UpdateRow(Service svc)
        {
            Row row;
            if (!_rows.TryGetValue(svc, out row))
            {
                // the web row may be Apache or Nginx
                if (svc.Id != "web") return;
                row = _rows.Values.FirstOrDefault(r => r.Svc.Id == "web");
                if (row == null || row.Svc != svc) return;
            }
            Color c;
            switch (svc.State)
            {
                case ServiceState.Running: c = Ui.Green; break;
                case ServiceState.Starting:
                case ServiceState.Stopping: c = Ui.Orange; break;
                case ServiceState.Error: c = Ui.Red; break;
                default: c = Ui.Gray; break;
            }
            row.Dot.DotColor = c;
            if (!svc.IsInstalled) row.Action.Text = I18n.T("svc.install");
            else if (svc.State == ServiceState.Starting) row.Action.Text = I18n.T("svc.starting");
            else if (svc.State == ServiceState.Stopping) row.Action.Text = I18n.T("svc.stopping");
            else row.Action.Text = svc.IsActive ? I18n.T("svc.stop") : I18n.T("svc.start");
            row.Port.Text = svc.Id == "php" ? (svc.IsActive ? svc.PortText : "") : svc.PortText;
            if (svc.Id == "php") row.Port.Text = svc.IsActive ? svc.PortText : I18n.T("svc.withWeb");
        }

        void UpdateGlobal()
        {
            if (_startAll == null) return;
            bool any = ServiceManager.AnyActive;
            _startAll.Text = any ? I18n.T("main.stopAll") : I18n.T("main.startAll");
            var errs = ServiceManager.Rows.Concat(new Service[] { ServiceManager.Apache, ServiceManager.Nginx }).Distinct()
                .Where(s => s.State == ServiceState.Error && !string.IsNullOrEmpty(s.LastError))
                .Select(s => s.Title + ": " + s.LastError).ToList();
            _errors.Text = string.Join("\n", errs);
            _errors.Visible = errs.Count > 0;
            var web = ServiceManager.Web;
            Color? dot = web.State == ServiceState.Running ? Ui.Green : web.State == ServiceState.Error ? Ui.Red : (Color?)null;
            var old = _tray.Icon;
            _tray.Icon = Ui.TrayIcon(dot);
            if (old != null) old.Dispose();
            var running = ServiceManager.Rows.Where(s => s.State == ServiceState.Running).Select(s => s.Title).ToList();
            var tip = "Portly" + (running.Count > 0 ? " - " + string.Join(", ", running) : "");
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        // ---------------- sites page ----------------
        Panel BuildSitesPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = Ui.P(0, 0, 0, 6) };
            bar.Controls.Add(Ui.Button(I18n.T("main.newProject"), (s, e) => NewProject(), true));
            bar.Controls.Add(Ui.Button(I18n.T("sites.open"), (s, e) => { var x = SelectedSite(); if (x != null) OpenSite(x, Settings.Current.Ssl); }));
            bar.Controls.Add(Ui.Button(I18n.T("sites.folder"), (s, e) => { var x = SelectedSite(); WinUtil.OpenFolder(x != null ? x.Folder : Settings.Current.DocumentRoot); }));
            bar.Controls.Add(Ui.Button(I18n.T("main.terminal"), (s, e) => { var x = SelectedSite(); Terminal.Open(x != null ? x.Folder : null); }));
            bar.Controls.Add(Ui.Button(I18n.T("sites.refresh"), async (s, e) => await ServiceManager.ReloadWebAsync()));

            _sitesInfo = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Ui.Muted, Font = Ui.Small, Padding = Ui.P(2, 0, 2, 6) };

            _sites = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = BorderStyle.FixedSingle };
            _sites.Columns.Add(I18n.T("col.name"), Ui.S(150));
            _sites.Columns.Add(I18n.T("col.address"), Ui.S(230));
            _sites.Columns.Add("PHP", Ui.S(70));
            _sites.Columns.Add(I18n.T("col.folder"), Ui.S(260));
            _sites.DoubleClick += (s, e) => { var x = SelectedSite(); if (x != null) OpenSite(x, Settings.Current.Ssl); };
            var cm = new ContextMenuStrip { Font = Ui.Font };
            cm.Opening += (s, e) => { BuildSiteMenu(cm.Items); e.Cancel = SelectedSite() == null; };
            _sites.ContextMenuStrip = cm;

            page.Controls.Add(_sites);
            page.Controls.Add(_sitesInfo);
            page.Controls.Add(bar);
            _sites.BringToFront();
            return page;
        }

        Site SelectedSite()
        {
            if (_sites == null || _sites.SelectedItems.Count == 0) return null;
            return _sites.SelectedItems[0].Tag as Site;
        }

        void FillSites()
        {
            if (_sites == null) return;
            var s = Settings.Current;
            _sitesInfo.Text = I18n.T("sites.info", s.DocumentRoot, s.Tld);
            _sites.BeginUpdate();
            _sites.Items.Clear();
            foreach (var site in ServiceManager.CurrentSites)
            {
                var it = new ListViewItem(site.Name) { Tag = site };
                it.SubItems.Add(site.Valid ? site.PreferredUrl : site.Problem);
                it.SubItems.Add(PackageTypes.Label(site.PhpFolder) + (site.PhpOverride != "" ? " *" : ""));
                it.SubItems.Add(site.DocRoot);
                if (!site.Valid) it.ForeColor = Ui.Red;
                _sites.Items.Add(it);
            }
            _sites.EndUpdate();
        }

        void BuildSiteMenu(ToolStripItemCollection items)
        {
            items.Clear();
            var site = SelectedSite();
            if (site == null) return;
            if (site.Valid)
            {
                items.Add(I18n.T("sites.openHttp"), null, (s, e) => OpenSite(site, false));
                if (Settings.Current.Ssl) items.Add(I18n.T("sites.openHttps"), null, (s, e) => OpenSite(site, true));
            }
            items.Add(I18n.T("sites.folder"), null, (s, e) => WinUtil.OpenFolder(site.Folder));
            items.Add(I18n.T("main.terminal"), null, (s, e) => Terminal.Open(site.Folder));
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
            items.Add(new ToolStripSeparator());
            items.Add(I18n.T("sites.delete"), null, async (s, e) =>
            {
                if (!Ui.Confirm(this, I18n.T("sites.deleteConfirm", site.Name, site.Folder))) return;
                try { ProjectCreator.DeleteToRecycleBin(site); }
                catch (Exception ex) { Ui.Error(this, ex.Message); return; }
                await ServiceManager.ReloadWebAsync();
            });
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
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = Ui.P(0, 0, 0, 6) };
            bar.Controls.Add(Ui.Button(I18n.T("log.clear"), (s, e) => _log.Clear()));
            bar.Controls.Add(Ui.Button(I18n.T("log.folder"), (s, e) => WinUtil.OpenFolder(Paths.Logs)));
            var oldText = _log != null ? _log.Text : "";
            _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Ui.Mono, BackColor = Ui.Card, Text = oldText, BorderStyle = BorderStyle.FixedSingle };
            page.Controls.Add(_log);
            page.Controls.Add(bar);
            _log.BringToFront();
            return page;
        }

        void AppendLog(string line)
        {
            if (_log == null) return;
            if (_log.TextLength > 200000) _log.Text = _log.Text.Substring(100000);
            _log.AppendText(line + Environment.NewLine);
            SetStatus(line.Length > 10 ? line.Substring(10) : line);
        }

        void SetStatus(string text) { if (_status != null) _status.Text = text; }

        // =====================================================================
        // Menu (header button + tray)
        // =====================================================================
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

            var db = new ToolStripMenuItem("MySQL");
            db.DropDownItems.Add(I18n.T("main.database"), null, (s, e) => OpenDatabaseTool());
            db.DropDownItems.Add(I18n.T("db.create"), null, (s, e) => CreateDatabase(""));
            db.DropDownItems.Add(I18n.T("db.backup"), null, async (s, e) => await BackupDatabases());
            db.DropDownItems.Add(I18n.T("db.password"), null, (s, e) => ChangeRootPassword());
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
            ssl.DropDownItems.Add(new ToolStripMenuItem(Certs.IsCaTrusted() ? I18n.T("ssl.trusted") : I18n.T("ssl.trust"), null, (s, e) => TrustCa()) { Enabled = !Certs.IsCaTrusted() });
            ssl.DropDownItems.Add(I18n.T("ssl.regenerate"), null, async (s, e) => { Certs.RegenerateSiteCertificates(); await ServiceManager.ReloadWebAsync(); });
            ssl.DropDownItems.Add(I18n.T("ssl.folder"), null, (s, e) => WinUtil.OpenFolder(Paths.UserSsl));
            items.Add(ssl);

            var tools = new ToolStripMenuItem(I18n.T("menu.tools"));
            tools.DropDownItems.Add(I18n.T("main.terminal"), null, (s, e) => Terminal.Open());
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
            var don = new ToolStripMenuItem("♥ " + I18n.T("main.donate")) { ForeColor = Ui.Heart };
            don.Click += (s, e) => WinUtil.Open(AppInfo.DonateUrl);
            items.Add(don);
            items.Add(new ToolStripSeparator());
            items.Add(I18n.T("menu.exit"), null, async (s, e) => await ExitAsync());
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
                    if (!PackageTypes.Installed("apache").Any() && !PackageTypes.Installed("nginx").Any() && !PackageTypes.Installed("mysql").Any())
                    {
                        OpenPackages(null, true);
                        return;
                    }
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

        void OpenDatabaseTool()
        {
            if (PackageTypes.ActiveDir("adminer") == null && PackageTypes.ActiveDir("phpmyadmin") == null)
            {
                if (Ui.Confirm(this, I18n.T("db.noTool"))) OpenPackages("adminer");
                return;
            }
            var tool = PackageTypes.ActiveDir("adminer") != null ? "adminer" : "phpmyadmin";
            var port = Settings.Current.HttpPort;
            WinUtil.Open("http://localhost" + (port == 80 ? "" : ":" + port) + "/" + tool + "/");
        }

        void NewProject()
        {
            ShowFromTray();
            using (var f = new NewProjectForm())
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
            if (ServiceManager.MySql.State != ServiceState.Running) { Ui.Error(this, I18n.T("err.mysqlNotRunning")); return; }
            var name = Ui.Prompt(this, I18n.T("db.create"), I18n.T("db.name"), suggested);
            if (name == null) return;
            var err = Database.CreateDatabase(name.Trim());
            if (err != null) Ui.Error(this, err);
            else Ui.Info(this, I18n.T("db.created", name.Trim()));
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

        void OpenSettings()
        {
            ShowFromTray();
            var docRoot = Settings.Current.DocumentRoot;
            using (var f = new SettingsForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
            }
            if (docRoot != Settings.Current.DocumentRoot) WatchDocRoot();
            BuildUi();
        }

        void OpenPackages(string type, bool firstRun = false)
        {
            ShowFromTray();
            using (var f = new PackagesForm(type, firstRun)) f.ShowDialog(this);
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

        // ---------------- updates ----------------
        void UpdateBanner()
        {
            if (_banner == null) return;
            if (Updater.Available != null)
            {
                _banner.Text = I18n.T("update.banner", Updater.Available.Version);
                _banner.Visible = true;
                return;
            }
            var pending = AppInfo.UpdatesSupported ? Catalog.PendingUpdates().Count : 0;
            _banner.Text = I18n.T("update.packages", pending);
            _banner.Visible = pending > 0;
        }

        async void OnBannerClick()
        {
            var u = Updater.Available;
            if (u == null) { OpenPackages(null); return; }
            var msg = I18n.T("update.confirm", u.Version, AppInfo.VersionText) + (string.IsNullOrEmpty(u.Notes) ? "" : "\n\n" + u.Notes);
            if (!Ui.Confirm(this, msg)) return;
            _banner.Enabled = false;
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
                _banner.Enabled = true;
                Ui.Error(this, I18n.T("update.failed", ex.Message));
            }
        }

        async Task BackgroundChecks(bool force, bool interactive)
        {
            await Certs.RefreshBundleAsync();
            if (!AppInfo.UpdatesSupported) return;
            var s = Settings.Current;
            if (!force)
            {
                if (!s.CheckUpdates) { SafeInvoke(UpdateBanner); return; }
                DateTime last;
                if (DateTime.TryParse(s.LastUpdateCheck, out last) && last > DateTime.Now.AddHours(-12)) { SafeInvoke(UpdateBanner); return; }
            }
            try
            {
                await Catalog.RefreshAsync();
                var u = await Updater.CheckAsync();
                SafeInvoke(() =>
                {
                    UpdateBanner();
                    if (interactive && u == null) Ui.Info(this, I18n.T("update.none", AppInfo.VersionText));
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
                _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.DirectoryName };
                FileSystemEventHandler h = (s, e) => SafeInvoke(() => { _debounce.Stop(); _debounce.Start(); });
                _watcher.Created += h;
                _watcher.Deleted += h;
                _watcher.Renamed += (s, e) => SafeInvoke(() => { _debounce.Stop(); _debounce.Start(); });
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Warn("Watcher: " + ex.Message); }
        }

        async Task OnDocRootChanged()
        {
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

            var s = Settings.Current;
            if (!s.OsWarningShown)
            {
                s.OsWarningShown = true;
                s.Save();
                if (!AppInfo.IsLegacy && !OsInfo.IsWin10OrLater) Ui.Error(this, I18n.T("os.useLegacy", OsInfo.DisplayName));
                if (AppInfo.IsLegacy && OsInfo.IsWin10OrLater) Ui.Info(this, I18n.T("os.useModern"));
            }
            if (Paths.HasSpaceInRoot) Log.Warn(I18n.T("warn.spaceInPath"));

            bool nothingInstalled = PackageTypes.All.All(t => PackageTypes.Installed(t.Id).Count == 0);
            if (nothingInstalled && !s.FirstRunDone)
            {
                s.FirstRunDone = true;
                s.Save();
                OpenPackages(null, true);
            }
            else CheckVcRuntime(true);

            var checks = BackgroundChecks(false, false);
            if (_startServices || s.StartServicesOnLaunch) await ToggleAll();
            await checks;
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
                try { ServiceManager.StopAllAsync().Wait(15000); } catch { }
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
            SetStatus(I18n.T("main.stopping"));
            Enabled = false;
            await Task.WhenAny(ServiceManager.StopAllAsync(), Task.Delay(30000));
            _tray.Visible = false;
            _tray.Dispose();
            Application.Exit();
        }
    }
}
