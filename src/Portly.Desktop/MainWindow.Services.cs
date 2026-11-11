using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.Forms;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop
{
    public partial class MainWindow
    {
        sealed class Row
        {
            public Service Svc;
            public Border Card;
            public ContentControl Tile;
            public string TileType;
            public TextBlock Name, Detail, Error;
            public Control ErrorLine;
            public Button Fix, Restart, Get;
            public Pill Status, Sec;
            public ComboBox Version;
            public ToggleSwitch Toggle;
            public LockToggle Bulk;
            public bool Filling, Working;
            public int PrevIndex;
        }

        sealed class VersionItem
        {
            public string Text, Value;
            public VersionItem(string text, string value) { Text = text; Value = value; }
            public override string ToString() { return Text; }
        }

        readonly List<Row> _rows = new List<Row>();
        TextBlock _servicesSub;
        Button _startAll;
        bool _busyAll, _syncingBulk, _updating;

        // =====================================================================
        // services page
        // =====================================================================
        Control BuildServicesPage()
        {
            _rows.Clear();
            var page = new StackPanel { Spacing = 10 };
            _startAll = Ui.Button(I18n.T("main.startAll"), ToggleAll, "primary", "play");
            _startAll.MinWidth = 170;
            _startAll.MinHeight = 40;
            _startAll.Classes.Add("bold");
            var choose = Ui.Button(I18n.T("main.chooseServices"), (Action)null, null, "list");
            choose.MinHeight = 40;
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            menu.Opening += (s, e) => FillChooseServices(menu);
            choose.Flyout = menu;
            // "Portly x.y is ready": also at the top of this page; UpdateBanner (shell) sets its text, visibility and click
            _updateTop = Ui.Button("", (Action)null, "primary", "update");
            _updateTop.MinHeight = 40;
            _updateTop.Classes.Add("green");
            _updateTop.IsVisible = false;
            var hosts = HostsButton(true);
            hosts.MinHeight = 40;
            var header = Ui.PageHeader(I18n.T("nav.services"), out _servicesSub, _startAll, choose, hosts, _updateTop);
            header.Margin = new Thickness(0, 0, 0, -10);
            page.Children.Add(header);

            // "PHP 8.6.0 is out" cards between the header and the services
            _notices = new UpdateNoticeBar { ApplyVersionChange = ApplyVersionChange, OpenPackages = t => { var _ = OpenPackages(t); }, Changed = () => { RefreshVersions(); UpdateBanner(); } };
            page.Children.Add(_notices);
            page.Children.Add(new RootPasswordNotice());

            foreach (var svc in ServiceManager.Rows)
            {
                var row = BuildServiceCard(svc);
                row.Card.IsVisible = IsServiceShown(svc);
                _rows.Add(row);
                page.Children.Add(row.Card);
            }

            var quick = Ui.Section(I18n.T("main.quickAccess"));
            quick.Margin = new Thickness(2, 8, 0, 0);
            page.Children.Add(quick);
            var tiles = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 10, Margin = new Thickness(0, -2, 0, 0) };
            tiles.Children.Add(Tile(I18n.T("main.web"), "globe", OpenLocalhost));
            Button dbTile = null;
            dbTile = Tile(I18n.T("main.database"), "database", async () => await OpenDatabaseQuick(dbTile));
            // right-click: every database tool (Adminer, HeidiSQL, DBeaver ...)
            dbTile.ContextRequested += async (s, e) => { e.Handled = true; await Ui.Run(() => OpenDatabaseTool(dbTile), dbTile); };
            ToolTip.SetTip(dbTile, I18n.T("main.databaseHint"));
            tiles.Children.Add(dbTile);
            tiles.Children.Add(Tile(I18n.T("main.mail"), "mail", OpenMailpit));
            tiles.Children.Add(Tile(I18n.T("main.terminal"), "terminal", () => Terminal.Open()));
            tiles.Children.Add(Tile(I18n.T("main.root"), "folder", () => Ui.Open(Settings.Current.DocumentRoot)));
            tiles.Children.Add(Tile(I18n.T("main.quickInstall"), "import", async () => await NewProject("wordpress")));
            tiles.Children.Add(Tile(I18n.T("main.newProject"), "plus", async () => await NewProject()));
            if (TomcatService.Enabled)
            {
                Button tcTile = null;
                tcTile = Tile("Tomcat", "globe", async () =>
                {
                    if (!ServiceManager.Tomcat.IsInstalled) { await OpenPackages("tomcat"); return; }
                    var m = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = tcTile };
                    AddTomcatItems(m.Items);
                    m.Open(tcTile);
                });
                tiles.Children.Add(tcTile);
            }
            page.Children.Add(tiles);
            return page;
        }

        static Button Tile(string text, string glyph, Func<Task> click) { return Ui.Button(text, click, "tile", glyph); }

        static Button Tile(string text, string glyph, Action click) { return Ui.Button(text, click, "tile", glyph); }

        /// <summary>Extra databases and caches start hidden until they are installed; the user's own choice wins.</summary>
        static bool IsServiceShown(Service svc)
        {
            bool optional = svc.Id == "postgresql" || svc.Id == "mongodb" || svc.Id == "memcached";
            return Settings.Current.IsShown(svc.Id, !optional || svc.IsInstalled);
        }

        /// <summary>Checklist of the service cards shown on the main window; it stays open while ticking.</summary>
        void FillChooseServices(MenuFlyout menu)
        {
            menu.Items.Clear();
            foreach (var r in _rows)
            {
                var row = r;
                var item = new MenuItem { Header = DisplayName(row.Svc), ToggleType = MenuItemToggleType.CheckBox, IsChecked = row.Card.IsVisible, StaysOpenOnClick = true };
                item.PropertyChanged += async (s, e) =>
                {
                    if (e.Property != MenuItem.IsCheckedProperty) return;
                    bool on = item.IsChecked;
                    Settings.Current.SetShown(row.Svc.Id, on);
                    Settings.Current.Save();
                    row.Card.IsVisible = on;
                    // a hidden service would keep running out of sight
                    if (!on && row.Svc.IsActive) await row.Svc.StopAsync();
                };
                menu.Items.Add(item);
            }
        }

        // =====================================================================
        // service card
        // =====================================================================
        Row BuildServiceCard(Service svc)
        {
            var row = new Row { Svc = svc };
            row.Tile = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
            row.Name = Ui.Label(DisplayName(svc), "subtitle");
            row.Name.TextWrapping = TextWrapping.NoWrap;
            row.Name.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Detail = Ui.Label("", "muted");
            row.Detail.TextWrapping = TextWrapping.NoWrap;
            row.Detail.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Error = Ui.Label("", "error");
            row.Error.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Error.MaxHeight = 38;
            row.Fix = Ui.Link(I18n.T("svc.fix"), (Action)null);
            row.Fix.IsVisible = false;
            row.Fix.VerticalAlignment = VerticalAlignment.Top;
            row.Fix.Click += async (s, e) => await Ui.Run(() => FixPorts(new[] { svc }), row.Fix);

            row.Toggle = new ToggleSwitch { OnContent = null, OffContent = null, VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(row.Toggle, svc.Title);
            row.Toggle.IsCheckedChanged += async (s, e) => { if (!_updating) await Ui.Run(() => OnToggle(row), null); };

            // next to the run switch: is this service part of "Start all" / "Stop all"?
            row.Restart = Ui.IconButton("refresh", I18n.T("svc.restart"), (Action)null);
            Avalonia.Automation.AutomationProperties.SetName(row.Restart, I18n.T("svc.restart") + " - " + svc.Title);
            row.Restart.Click += async (s, e) => await Ui.Run(() => OnRestart(row), row.Restart);
            var flat = new Style(x => x.OfType<Button>().Class(":disabled").Template().OfType<ContentPresenter>());
            flat.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent));
            row.Restart.Styles.Add(flat);
            row.Bulk = new LockToggle { IsChecked = Settings.Current.IsEnabled(svc.Id), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(row.Bulk, I18n.T("svc.bulkHint"));
            Avalonia.Automation.AutomationProperties.SetName(row.Bulk, I18n.T("svc.bulk") + " - " + svc.Title);
            row.Bulk.IsCheckedChanged += (s, e) =>
            {
                if (_syncingBulk) return;
                Settings.Current.SetEnabled(svc.Id, row.Bulk.IsChecked == true);
                Settings.Current.Save();
                UpdateGlobal();
            };
            row.Status = new Pill();
            row.Sec = new Pill { IsVisible = false, Cursor = new Cursor(StandardCursorType.Hand) };
            row.Sec.PointerPressed += async (s, e) => await Ui.Run(() => OnSecurityPill(row), row.Sec);
            row.Version = new ComboBox { Width = 130, VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(row.Version, svc.Title + " " + I18n.T("col.version"));
            FillVersions(row);
            row.Version.SelectionChanged += async (s, e) => { if (!row.Filling) await Ui.Run(() => OnVersionChanged(row), null); };
            row.Get = Ui.Button(I18n.T("svc.install"), async () => await OpenPackages(svc.PackageType), "primary", "update");
            row.Get.VerticalAlignment = VerticalAlignment.Center;
            row.Get.HorizontalAlignment = HorizontalAlignment.Right;
            row.Get.MinHeight = 30;

            // positions as in the Windows app: logo at 18, text at 72, switch 18 from the right edge, the padlock 12 and
            // restart 6 left of it, then the status, the version list (130) and the security badge, each right-aligned to the next
            var text = Ui.Column(7, row.Name, row.Detail);
            text.VerticalAlignment = VerticalAlignment.Top;
            text.Margin = new Thickness(14, -3, 8, 0);

            row.Sec.Margin = new Thickness(0, 0, 8, 0);
            row.Version.Margin = new Thickness(0, 0, 12, 0);
            var cluster = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            cluster.Children.Add(row.Sec);
            cluster.Children.Add(row.Version);
            cluster.Children.Add(row.Status);
            row.Bulk.Margin = new Thickness(6, 0, 12, 0);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Width = 118, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(row.Restart);
            actions.Children.Add(row.Bulk);
            actions.Children.Add(row.Toggle);
            var right = new Panel { VerticalAlignment = VerticalAlignment.Center, Children = { actions, row.Get } };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            Place(grid, row.Tile, 0);
            Place(grid, text, 1);
            Place(grid, cluster, 2);
            Place(grid, right, 3);

            var errLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
            errLine.Children.Add(row.Error);
            Place(errLine, row.Fix, 1);
            row.ErrorLine = errLine;
            var body = Ui.Column(0, grid, errLine);

            row.Card = Ui.Card(body, new Thickness(17, 15));
            row.Card.MinHeight = 72;
            var cm = new ContextMenu();
            cm.Opening += (s, e) => BuildCardMenu(cm, row);
            row.Card.ContextMenu = cm;
            UpdateRow(svc, row);
            return row;
        }

        static void Place(Grid grid, Control c, int column)
        {
            Grid.SetColumn(c, column);
            grid.Children.Add(c);
        }

        void BuildCardMenu(ContextMenu cm, Row row)
        {
            cm.Items.Clear();
            var svc = row.Svc;
            if (svc.Id != "php")
            {
                var auto = new MenuItem { Header = I18n.T("svc.includeInAll"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = Settings.Current.IsEnabled(svc.Id) };
                auto.Click += (s, e) => { Settings.Current.SetEnabled(svc.Id, !Settings.Current.IsEnabled(svc.Id)); Settings.Current.Save(); UpdateRow(svc); UpdateGlobal(); };
                cm.Items.Add(auto);
            }
            var get = new MenuItem { Header = I18n.T("svc.getVersions") };
            get.Click += async (s, e) => await Ui.Run(() => OpenPackages(svc.PackageType), null);
            cm.Items.Add(get);
            var log = svc.Id == "web" ? Settings.Current.WebServer + "-error.log" : svc.Id == "mysql" ? "mysql-error.log" : svc.Id == "postgresql" ? "postgresql.log" : svc.Id == "mongodb" ? "mongodb.log" : svc.Id == "memcached" ? "memcached.log" : svc.Id == "redis" ? "redis.log" : svc.Id == "mailpit" ? Settings.Current.MailTool + ".log" : svc.Id == "tomcat" ? Path.Combine(TomcatService.LogDir, "console.log") : "php_errors.log";
            var open = new MenuItem { Header = I18n.T("svc.openLog") };
            open.Click += (s, e) => Ui.Open(Path.Combine(Paths.Logs, log));
            cm.Items.Add(open);
            if (svc.Id == "tomcat") AddTomcatItems(cm.Items);
        }

        /// <summary>"Open Tomcat page" and "Open webapps folder" (a WAR file dropped there is deployed).</summary>
        static void AddTomcatItems(ItemCollection items)
        {
            if (!TomcatService.Enabled) return;
            var page = new MenuItem { Header = I18n.T("tomcat.openPage"), IsEnabled = ServiceManager.Tomcat.IsInstalled };
            page.Click += (s, e) => Ui.Open(TomcatService.Url);
            items.Add(page);
            var apps = new MenuItem { Header = I18n.T("tomcat.openWebapps"), IsEnabled = ServiceManager.Tomcat.IsInstalled };
            apps.Click += (s, e) =>
            {
                var dir = TomcatService.WebappsDir;
                if (dir == null) return;
                Directory.CreateDirectory(dir);
                Ui.Open(dir);
            };
            items.Add(apps);
        }

        /// <summary>MySQL and MariaDB are one card: the same port and data, either engine.</summary>
        static string DisplayName(Service svc) { return svc == ServiceManager.MySql ? I18n.T("svc.dbTitle") : svc.Title; }

        // =====================================================================
        // versions
        // =====================================================================
        void FillVersions(Row row)
        {
            row.Filling = true;
            try
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
                            row.Version.Items.Add(new VersionItem(PackageTypes.Get(en).Name + " " + PackageTypes.Label(f), en + "|" + f));
                    if (row.Version.Items.Count == 0) row.Version.Items.Add(new VersionItem("—", ""));
                    row.Version.Items.Add(new VersionItem(I18n.T("svc.getVersions"), "__get"));
                    int sel = row.Version.Items.Cast<VersionItem>().ToList().FindIndex(i => string.Equals(i.Value, current, StringComparison.OrdinalIgnoreCase));
                    row.Version.SelectedIndex = Math.Max(0, sel);
                    row.PrevIndex = row.Version.SelectedIndex;
                    return;
                }
                var installed = PackageTypes.Installed(type);
                var active = PackageTypes.ActiveFolder(type);
                foreach (var f in installed) row.Version.Items.Add(new VersionItem(PackageTypes.Label(f), f));
                if (installed.Count == 0) row.Version.Items.Add(new VersionItem("—", ""));
                row.Version.Items.Add(new VersionItem(I18n.T("svc.getVersions"), "__get"));
                int idx = installed.FindIndex(f => string.Equals(f, active, StringComparison.OrdinalIgnoreCase));
                row.Version.SelectedIndex = Math.Max(0, idx);
                row.PrevIndex = row.Version.SelectedIndex;
            }
            finally { row.Filling = false; }
        }

        /// <summary>shared: version lists on the cards after packages were installed / removed.</summary>
        public void RefreshVersions()
        {
            foreach (var r in _rows) { FillVersions(r); UpdateRow(r.Svc, r); }
        }

        void SelectVersion(Row row, int index)
        {
            row.Filling = true;
            try { row.Version.SelectedIndex = index; }
            finally { row.Filling = false; }
        }

        async Task OnVersionChanged(Row row)
        {
            var item = row.Version.SelectedItem as VersionItem;
            if (item == null) return;
            if (item.Value == "__get" || item.Value == "")
            {
                SelectVersion(row, row.PrevIndex);
                await OpenPackages(row.Svc.PackageType);
                return;
            }
            row.PrevIndex = row.Version.SelectedIndex;
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
                var sw = await DatabaseUi.BeforeSwitch(this, type, folder);
                if (sw != DatabaseUi.SwitchResult.Continue) { RefreshVersions(); return; }
            }
            Settings.Current.SetVersion(type, folder);
            Settings.Current.Save();
            Log.Info(I18n.T("log.switched", PackageTypes.Get(type).Name, PackageTypes.Label(folder)));
            ApplyUserPath();
            UpdateRow(row.Svc);
            await ApplyVersionChange(type);
        }

        static void ApplyUserPath()
        {
            try { Terminal.ApplyUserPath(); }
            catch (Exception ex) { Log.Warn("PATH: " + ex.Message); }
        }

        /// <summary>MySQL &lt;-&gt; MariaDB from the card (same as Settings > Services > Database engine).</summary>
        async Task SwitchDbEngine(string engine, string folder)
        {
            var sw = await DatabaseUi.BeforeSwitch(this, engine, folder);
            if (sw == DatabaseUi.SwitchResult.Cancelled) { RefreshVersions(); return; }
            bool wasActive = ServiceManager.MySql.IsActive;
            if (wasActive) await ServiceManager.MySql.StopAsync();   // stops the engine that runs now
            Settings.Current.DbEngine = engine;
            Settings.Current.SetVersion(engine, folder);
            Settings.Current.Save();
            Log.Info(I18n.T("log.switched", I18n.T("svc.dbTitle"), PackageTypes.Get(engine).Name + " " + PackageTypes.Label(folder)));
            ApplyUserPath();
            if (wasActive) await ServiceManager.MySql.StartAsync();
            RefreshVersions();
        }

        async Task ApplyVersionChange(string type)
        {
            if (OS.IsWindows) await CheckVcRuntime(false);
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
                    if (ServiceManager.Tomcat.IsActive) await ServiceManager.Tomcat.RestartAsync();
                    break;
            }
        }

        // =====================================================================
        // actions
        // =====================================================================
        async Task OnToggle(Row row)
        {
            var svc = row.Svc;
            if (!svc.IsInstalled) { UpdateRow(svc); await OpenPackages(svc.PackageType); return; }
            if (svc.State == ServiceState.Starting || svc.State == ServiceState.Stopping || row.Working) { UpdateRow(svc); return; }
            SetWorking(row, true);
            try
            {
                if (svc.IsActive) await ServiceManager.StopAsync(svc);
                else
                {
                    if (!await ResolvePorts(new[] { svc.Id == "php" ? ServiceManager.Web : svc })) return;
                    if (OS.IsWindows) await CheckVcRuntime(false);
                    await ServiceManager.StartAsync(svc);
                    if (svc.Id == "web" && svc.State == ServiceState.Running) await OfferTrustCa();
                }
            }
            finally { SetWorking(row, false); UpdateRow(svc); UpdateGlobal(); }
        }

        /// <summary>Restart one service (the web server also takes its PHP pools and the site configs with it).</summary>
        async Task OnRestart(Row row)
        {
            var svc = row.Svc;
            if (svc.State != ServiceState.Running) return;
            SetWorking(row, true);
            try
            {
                if (svc.Id == "web") await ServiceManager.ReloadWebAsync();
                else await ServiceManager.RestartAsync(svc);
            }
            catch (Exception ex) { Log.Error(I18n.T("svc.restartFailed", svc.Title), ex); }
            finally { SetWorking(row, false); UpdateRow(svc); UpdateGlobal(); }
        }

        void SetWorking(Row row, bool on)
        {
            row.Working = on;
            row.Toggle.IsEnabled = !on;
            row.Restart.IsEnabled = !on;
        }

        /// <summary>First time the web server runs with HTTPS: offer to trust Portly's local CA.</summary>
        async Task OfferTrustCa()
        {
            if (!Settings.Current.Ssl || Certs.IsCaTrusted()) return;
            var ini = Settings.Current.Raw;
            if (ini.GetBool("general", "CaTrustAsked", false)) return;
            ini.Set("general", "CaTrustAsked", true);
            Settings.Current.Save();
            if (await Dialogs.Confirm(this, I18n.T("ssl.ask"))) await TrustCa();
        }

        // ---------------- security / antivirus ----------------
        void UpdateSecurityPill(Row row)
        {
            var st = row.Svc.IsInstalled ? SecurityInfo.StatusOf(row.Svc.PackageType, PackageTypes.ActiveFolder(row.Svc.PackageType)) : null;
            row.Sec.IsVisible = st != null;
            if (st == null) return;
            row.Sec.Set(st.Short, st.Advisories.Count > 0 ? Ui.Red : Ui.Orange);
            ToolTip.SetTip(row.Sec, st.Long + (st.Fix != null ? "\n" + I18n.T("sec.clickToUpdate", st.Fix.Version) : ""));
        }

        /// <summary>Pill click: one-click update to the fixed build, otherwise the packages window of the type.</summary>
        async Task OnSecurityPill(Row row)
        {
            var type = row.Svc.PackageType;
            var folder = PackageTypes.ActiveFolder(type);
            var st = SecurityInfo.StatusOf(type, folder);
            if (st == null || st.Fix == null || st.Fix.IsInstalled) { await OpenPackages(type); return; }
            var name = PackageTypes.Get(type).Name + " " + st.Fix.Version;
            if (!await Dialogs.Confirm(this, I18n.T("sec.updateConfirm", st.Long, name))) return;
            row.Sec.IsEnabled = false;
            try
            {
                await Installer.UpdateAsync(folder, st.Fix, p => Ui.Post(() => SetStatus(name + " - " + I18n.T("pkg.stage." + p.Stage) + (p.Percent >= 0 && p.Stage == "download" ? " " + p.Percent + "%" : ""))), CancellationToken.None);
                ApplyUserPath();
                RefreshVersions();
                await ApplyVersionChange(type);
                await OfferRemoveOld(type, folder);
                SetStatus(I18n.T("log.installed", name));
            }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
            finally { row.Sec.IsEnabled = true; RefreshVersions(); UpdateBanner(); }
        }

        /// <summary>After a patch update: the previous build of the branch is usually no longer needed.</summary>
        async Task OfferRemoveOld(string type, string oldFolder)
        {
            if (string.Equals(PackageTypes.ActiveFolder(type), oldFolder, StringComparison.OrdinalIgnoreCase)) return;
            if (type == "php" && Cleanup.SitePhpFolders().Contains(oldFolder)) return;
            var dir = Path.Combine(Paths.BinOf(type), oldFolder);
            if (!Directory.Exists(dir) || Cleanup.IsInUse(dir)) return;
            var names = PackageTypes.Get(type).Name + " " + PackageTypes.Label(oldFolder);
            if (!await Dialogs.Confirm(this, I18n.T("notice.removeOld", names, Ui.FormatBytes(Cleanup.FolderSize(dir))))) return;
            try { Installer.Remove(type, oldFolder); }
            catch (Exception ex) { Log.Error(oldFolder, ex); }
        }

        // =====================================================================
        // ports
        // =====================================================================
        /// <summary>shared: check the services' ports before starting; false = the user cancelled.</summary>
        public async Task<bool> ResolvePorts(IEnumerable<Service> services)
        {
            var list = services.ToList();
            if (list.Contains(ServiceManager.Web) && !await EnsureLowPorts()) return false;
            SetStatus(I18n.T("port.checking"));
            var conflicts = await Task.Run(() => PortGuard.Check(list));
            if (conflicts.Count == 0) return true;
            ShowFromTray();
            var choice = await PortConflictForm.Show(this, conflicts);
            if (choice == PortChoice.UseFreePorts)
            {
                var summary = PortGuard.UseSuggestions(conflicts);
                SetStatus(I18n.T("port.changed", summary));
                foreach (var r in _rows) UpdateRow(r.Svc, r);
                return true;
            }
            if (choice == PortChoice.CloseOwners)
            {
                var failed = await Task.Run(() => PortGuard.CloseOwners(conflicts));
                if (failed.Count == 0) return true;
                await Dialogs.Warning(this, I18n.T("port.stillUsed", string.Join("\n", failed)));
            }
            return false;
        }

        /// <summary>Linux: ports below 1024 need a one-time system setting before a normal user may listen on them.</summary>
        async Task<bool> EnsureLowPorts()
        {
            var s = Settings.Current;
            if (!LowPorts.NeedsSetup(s.HttpPort) && !LowPorts.NeedsSetup(s.HttpsPort)) return true;
            if (!await Dialogs.Confirm(this, I18n.T("lowports.ask", s.HttpPort, s.HttpsPort))) return false;
            if (await Task.Run(() => LowPorts.Setup())) return true;
            await Dialogs.Warning(this, I18n.T("lowports.failed", s.HttpPort, s.HttpsPort));
            return false;
        }

        /// <summary>shared: "Fix" link on a card, or Tools > Check ports: resolve and start what failed.</summary>
        public async Task FixPorts(IEnumerable<Service> services)
        {
            var list = services.Where(s => !s.IsActive).ToList();
            if (list.Count == 0) return;
            var conflicts = await Task.Run(() => PortGuard.Check(list));
            if (conflicts.Count == 0)
            {
                await Dialogs.Info(this, I18n.T("port.allFree"));
                return;
            }
            if (!await ResolvePorts(list)) return;
            foreach (var s in conflicts.Select(c => c.Service).Distinct()) await ServiceManager.StartAsync(s);
        }

        /// <summary>shared: Start all / Stop all (button, tray menu).</summary>
        public async Task ToggleAll()
        {
            if (_busyAll) return;
            _busyAll = true;
            _startAll.IsEnabled = false;
            try
            {
                if (ServiceManager.AnyBulkActive) await ServiceManager.StopBulkAsync();
                else
                {
                    if (!PackageTypes.Installed("apache").Any() && !PackageTypes.Installed("nginx").Any() && !PackageTypes.Installed(Settings.Current.DbEngine).Any())
                    {
                        await OpenPackages(null, true);
                        return;
                    }
                    var s = Settings.Current;
                    var toStart = ServiceManager.Rows.Where(x => x.Id != "php" && s.IsEnabled(x.Id)).ToList();
                    if (!await ResolvePorts(toStart)) return;
                    if (OS.IsWindows) await CheckVcRuntime(false);
                    await ServiceManager.StartAllAsync();
                    if (ServiceManager.Web.State == ServiceState.Running) await OfferTrustCa();
                }
            }
            finally
            {
                _busyAll = false;
                _startAll.IsEnabled = true;
                UpdateGlobal();
            }
        }

        // =====================================================================
        // state
        // =====================================================================
        /// <summary>shared: refresh one service card (state, version, error).</summary>
        public void UpdateRow(Service svc) { UpdateRow(svc, _rows.FirstOrDefault(r => r.Svc == svc)); }

        void UpdateRow(Service svc, Row row)
        {
            if (row == null) return;
            bool installed = svc.IsInstalled;
            row.Card.IsVisible = IsServiceShown(svc);   // an optional database appears once it is installed
            var tileType = svc.Id == "web" || svc.Id == "mailpit" || svc.Id == "mysql" ? svc.PackageType : svc.Id;
            if (row.TileType != tileType)
            {
                // the card's logo follows the engine (MySQL / MariaDB)
                row.TileType = tileType;
                row.Tile.Content = new BrandTile(tileType, svc.Title);
            }
            string statusText; IBrush statusColor;
            switch (svc.State)
            {
                case ServiceState.Running: statusText = I18n.T("svc.running"); statusColor = Ui.Green; break;
                case ServiceState.Starting: statusText = I18n.T("svc.starting"); statusColor = Ui.Orange; break;
                case ServiceState.Stopping: statusText = I18n.T("svc.stopping"); statusColor = Ui.Orange; break;
                case ServiceState.Error: statusText = I18n.T("svc.error"); statusColor = Ui.Red; break;
                default: statusText = installed ? I18n.T("svc.stopped") : I18n.T("svc.notInstalled"); statusColor = Ui.Gray; break;
            }
            row.Status.Set(statusText, statusColor);
            bool busy = svc.State == ServiceState.Starting || svc.State == ServiceState.Stopping;
            _updating = true;
            try { row.Toggle.IsChecked = svc.State == ServiceState.Running || svc.State == ServiceState.Starting; }
            finally { _updating = false; }
            bool showSwitch = installed && svc.Id != "php";
            row.Toggle.IsVisible = showSwitch;
            row.Bulk.IsVisible = showSwitch;
            row.Restart.IsVisible = showSwitch;
            row.Toggle.IsEnabled = !busy && !row.Working;
            row.Toggle.Classes.Set("busy", busy);
            row.Restart.IsEnabled = svc.State == ServiceState.Running && !row.Working;   // nothing to restart while it is stopped
            _syncingBulk = true;
            row.Bulk.IsChecked = Settings.Current.IsEnabled(svc.Id);   // also changed from the card menu
            _syncingBulk = false;
            row.Get.IsVisible = !installed;

            var version = PackageTypes.Label(PackageTypes.ActiveFolder(svc.PackageType));
            string ports = svc.Id == "php" ? (svc.IsActive ? "127.0.0.1:" + svc.PortText : I18n.T("svc.withWeb")) : (Settings.Current.AllowLan ? "0.0.0.0:" : "127.0.0.1:") + svc.PortText;
            row.Detail.Text = installed ? svc.Title + " " + version + "  ·  " + ports : I18n.T("svc.notInstalledHint");
            ToolTip.SetTip(row.Detail, row.Detail.Text);
            UpdateSecurityPill(row);

            bool err = svc.State == ServiceState.Error && !string.IsNullOrEmpty(svc.LastError);
            row.Error.Text = err ? svc.LastError.Replace("\n", "  ") : "";
            ToolTip.SetTip(row.Error, err ? svc.LastError : null);
            row.ErrorLine.IsVisible = err;
            row.Fix.IsVisible = err && svc.LastError.Contains(":") && IsPortError(svc.LastError);
            if (err) row.Card.BorderBrush = new SolidColorBrush(((ISolidColorBrush)Ui.Red).Color, .47);
            else row.Card.ClearValue(Border.BorderBrushProperty);
        }

        static bool IsPortError(string text)
        {
            // the port message is built from err.portInUse; compare its stable start
            var probe = I18n.T("err.portInUse", "\u0001", "\u0002");
            var head = probe.Substring(0, Math.Max(0, probe.IndexOf('\u0001')));
            return head.Length > 0 ? text.Contains(head) : false;
        }

        /// <summary>shared: header subtitle and the Start all / Stop all button.</summary>
        public void UpdateGlobal()
        {
            if (_startAll == null) return;
            // the button follows the bulk group only: a service started by hand with "Bulk" off does not turn it into "Stop all"
            bool any = ServiceManager.AnyBulkActive;
            var label = I18n.T(any ? "main.stopAll" : "main.startAll");
            _startAll.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { new Glyph { Name_ = any ? "stop" : "play", VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } },
            };
            Avalonia.Automation.AutomationProperties.SetName(_startAll, label);
            if (any) _startAll.Classes.Remove("primary");
            else if (!_startAll.Classes.Contains("primary")) _startAll.Classes.Add("primary");
            var rows = ServiceManager.Rows.Where(s => s.Id != "php" && s.IsInstalled).ToList();
            int running = rows.Count(s => s.State == ServiceState.Running);
            _servicesSub.Text = running == 0 ? I18n.T("main.allStopped") : I18n.T("main.statusRunning", running, rows.Count);
        }
    }
}
