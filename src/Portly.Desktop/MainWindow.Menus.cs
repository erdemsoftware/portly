using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
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
        // ---------------- log page ----------------
        SelectableTextBlock _logText;
        ScrollViewer _logScroll;
        bool _logScrollPending;
        readonly List<string> _logLines = new List<string>();

        Control BuildLogPage()
        {
            TextBlock sub;
            var header = Ui.PageHeader(I18n.T("nav.log"), out sub,
                Ui.Button(I18n.T("log.clear"), () => { _logLines.Clear(); _logText.Inlines.Clear(); }),
                Ui.Button(I18n.T("log.folder"), () => OpenFolder(Paths.Logs), null, "folder"));
            sub.Text = Paths.Logs;

            _logText = Ui.Selectable("", "mono");
            _logText.Foreground = Ui.Brush("ConsoleText");
            _logText.FontSize = 12.5;
            _logText.FontWeight = FontWeight.Normal;
            _logScroll = new ScrollViewer { Content = _logText, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var console = new Border { Child = _logScroll, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(14, 12, 6, 12), CornerRadius = new CornerRadius(12) };
            console.Classes.Add("console");

            var page = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            page.Children.Add(header);
            page.Children.Add(console);
            FillLog();
            return page;
        }

        void FillLog()
        {
            _logText.Inlines.Clear();
            foreach (var l in _logLines) WriteLogLine(l);
            ScrollLogToEnd();
        }

        void AppendLog(string line)
        {
            _logLines.Add(line);
            bool trimmed = _logLines.Count > 2000;
            if (trimmed) _logLines.RemoveRange(0, 500);
            if (trimmed) FillLog();
            else
            {
                WriteLogLine(line);
                ScrollLogToEnd();
            }
            SetStatus(line.Length > 10 ? line.Substring(10) : line);
        }

        void WriteLogLine(string line)
        {
            if (_logText == null) return;
            var time = line.Length > 10 ? line.Substring(0, 10) : "";
            var text = line.Length > 10 ? line.Substring(10) : line;
            IBrush c = text.StartsWith("ERROR") ? new SolidColorBrush(Color.FromRgb(248, 113, 113)) : text.StartsWith("WARN") ? new SolidColorBrush(Color.FromRgb(251, 191, 36)) : Ui.Brush("ConsoleText");
            _logText.Inlines.Add(new Run(time) { Foreground = Ui.Brush("ConsoleMuted") });
            _logText.Inlines.Add(new Run(text + "\n") { Foreground = c });
        }

        void ScrollLogToEnd()
        {
            if (_logScroll == null || _logScrollPending) return;
            _logScrollPending = true;
            Dispatcher.UIThread.Post(() => { _logScrollPending = false; if (_logScroll != null) _logScroll.ScrollToEnd(); }, DispatcherPriority.Background);
        }

        // ---------------- menu model ----------------
        // one description of the menu, turned into a NativeMenu (tray) or a MenuFlyout ("More", database tools)
        sealed class Mi
        {
            public string Text;
            public Func<Task> Click;
            public bool Checked, Disabled, Separator;
            public IBrush Color;
            public MenuList Items;
        }

        sealed class MenuList : List<Mi>
        {
            public void Add(string text, Func<Task> click) { Add(new Mi { Text = text, Click = click }); }
            public void Add(string text, Action click) { Add(new Mi { Text = text, Click = () => { click(); return Task.CompletedTask; } }); }
            public void Sep() { Add(new Mi { Separator = true }); }
            public MenuList Sub(string text)
            {
                var m = new MenuList();
                Add(new Mi { Text = text, Items = m });
                return m;
            }
        }

        static void FillNativeMenu(NativeMenu menu, MenuList list)
        {
            menu.Items.Clear();
            foreach (var m in list)
            {
                if (m.Separator) { menu.Items.Add(new NativeMenuItemSeparator()); continue; }
                var item = new NativeMenuItem(m.Text) { IsEnabled = !m.Disabled };
                if (m.Checked) { item.ToggleType = MenuItemToggleType.CheckBox; item.IsChecked = true; }
                if (m.Items != null)
                {
                    item.Menu = new NativeMenu();
                    FillNativeMenu(item.Menu, m.Items);
                }
                else if (m.Click != null)
                {
                    var click = m.Click;
                    item.Click += async (s, e) => await Ui.Run(click);
                }
                menu.Items.Add(item);
            }
        }

        static List<Control> ToMenuItems(MenuList list)
        {
            var items = new List<Control>();
            foreach (var m in list)
            {
                if (m.Separator) { items.Add(new Separator()); continue; }
                var item = new MenuItem { Header = m.Text, IsEnabled = !m.Disabled };
                if (m.Color != null) item.Foreground = m.Color;
                if (m.Checked) { item.ToggleType = MenuItemToggleType.CheckBox; item.IsChecked = true; }
                if (m.Items != null) item.ItemsSource = ToMenuItems(m.Items);
                else if (m.Click != null)
                {
                    var click = m.Click;
                    item.Click += async (s, e) => await Ui.Run(click);
                }
                items.Add(item);
            }
            return items;
        }

        MenuFlyout Flyout(MenuList list, PlacementMode placement)
        {
            var f = new MenuFlyout { Placement = placement };
            foreach (var c in ToMenuItems(list)) f.Items.Add(c);
            return f;
        }

        void ShowMoreMenu(Control anchor)
        {
            Flyout(BuildMenu(false), I18n.IsRtl ? PlacementMode.Left : PlacementMode.Right).ShowAt(anchor);
        }

        /// <summary>--screenshot --menu: the "More" menu next to its button.</summary>
        internal void PreviewMenu()
        {
            Button more;
            if (_nav.Count > 0 && Content is Grid g && g.Children[0] is Border b) more = FindMore(b);
            else more = null;
            if (more != null) ShowMoreMenu(more);
        }

        static Button FindMore(Visual root)
        {
            Button last = null;
            foreach (var v in root.GetVisualDescendants().OfType<Button>())
                if (v.Classes.Contains("nav") && Avalonia.Automation.AutomationProperties.GetName(v) == I18n.T("main.more")) last = v;
            return last;
        }

        // ---------------- small helpers ----------------
        static void OpenFolder(string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { }
            Ui.Open(dir);
        }

        static void EditFile(string file)
        {
            if (OS.IsWindows) WinUtil.Edit(file);
            else if (OS.IsMac) { try { System.Diagnostics.Process.Start("open", new[] { "-t", file }); } catch (Exception ex) { Log.Warn("Edit " + file + ": " + ex.Message); } }
            else Ui.Open(file);
        }

        static string LocalUrl(string path)
        {
            var port = Settings.Current.HttpPort;
            return "http://localhost" + (port == 80 ? "" : ":" + port) + path;
        }

        void OpenSiteMenu(Site site, bool https)
        {
            if (!site.Valid) { Dialogs.Warning(this, site.Problem); return; }
            if (!ServiceManager.Web.IsActive) SetStatus(I18n.T("main.webNotRunning"));
            Ui.Open(site.Url(https && Settings.Current.Ssl));
        }

        // =====================================================================
        // Menus
        // =====================================================================
        /// <param name="tray">The tray icon's menu (with "Show Portly", Start all, Packages, Settings ...);
        /// false = the window's "More" menu, without what the window itself already shows.</param>
        MenuList BuildMenu(bool tray)
        {
            var items = new MenuList();
            if (tray)
            {
                items.Add(I18n.T("tray.show"), () => ShowFromTray());
                items.Sep();
                items.Add(ServiceManager.AnyBulkActive ? I18n.T("main.stopAll") : I18n.T("main.startAll"), async () => await ToggleAll());
            }
            items.Add(I18n.T("main.web") + " (localhost)", () => OpenLocalhost());

            var sites = items.Sub(I18n.T("nav.sites"));
            foreach (var site in ServiceManager.CurrentSites.Where(x => x.Valid))
            {
                var st = site;
                sites.Add(site.Host, () => OpenSiteMenu(st, Settings.Current.Ssl));
            }
            if (sites.Count == 0) sites.Add(new Mi { Text = I18n.T("sites.none"), Disabled = true });
            sites.Sep();
            sites.Add(I18n.T("main.newProject"), async () => await NewProject());

            var db = items.Sub(I18n.T("svc.dbTitle"));
            var toolsMenu = db.Sub(I18n.T("main.database"));
            BuildDatabaseToolItems(toolsMenu);
            db.Add(I18n.T("db.create"), async () => await CreateDatabase(""));
            db.Add(I18n.T("sqlimp.menu"), async () => { ShowFromTray(); await DatabaseUi.ImportSql(this); });
            db.Add(I18n.T("repair.menu"), async () => { ShowFromTray(); await DatabaseUi.CheckAndRepair(this); });
            if (OS.IsWindows && Features.IsOn("wsl") && WslAccess.Supported)
                db.Add(I18n.T("wsl.menu"), async () => { ShowFromTray(); await DatabaseUi.WslAccessDialog(this); });
            db.Add(I18n.T("db.backup"), async () => await BackupDatabases());
            db.Add(I18n.T("db.password"), async () => await ChangeRootPassword());
            db.Add(I18n.T("db.resetRoot"), async () =>
            {
                if (!await Dialogs.Confirm(this, I18n.T("db.resetRootConfirm"))) return;
                var err = await ServiceManager.MySql.ResetRootPasswordAsync();
                if (err != null) await Dialogs.Warning(this, err); else await Dialogs.Info(this, I18n.T("db.resetRootDone"));
            });
            db.Add(I18n.T("db.recovery"), async () =>
            {
                if (!await Dialogs.Confirm(this, I18n.T("db.recoveryConfirm"))) return;
                await ServiceManager.MySql.StartRecoveryAsync();
                if (ServiceManager.MySql.State == ServiceState.Running) await Dialogs.Info(this, I18n.T("db.recoveryRunning"));
            });
            db.Add(I18n.T("db.backupFolder"), () => OpenFolder(Path.Combine(Paths.Backup, "mysql")));
            db.Add(I18n.T("db.config"), () => EditFile(Templates.PathOf("mysql.ini.tpl")));

            var phpDir = PackageTypes.ActiveDir("php");
            var php = items.Sub("PHP");
            if (phpDir != null)
            {
                php.Add("php.ini", () => EditFile(PhpConfig.IniPath(phpDir)));
                var ext = php.Sub(I18n.T("php.extensions"));
                foreach (var kv in PhpConfig.ListExtensions(phpDir))
                {
                    var name = kv.Key;
                    var on = kv.Value;
                    ext.Add(new Mi
                    {
                        Text = name, Checked = on,
                        Click = async () =>
                        {
                            PhpConfig.ToggleExtension(phpDir, name, !on);
                            Log.Info(I18n.T(on ? "log.extOff" : "log.extOn", name));
                            await ServiceManager.ReloadWebAsync();
                        },
                    });
                }
                if (Features.IsOn("extensions"))
                    php.Add(I18n.T("ext.menu"), async () => await PhpExtensionsForm.ShowAsync(this));
                var xdebugOn = Xdebug.IsEnabled(phpDir);
                php.Add(new Mi
                {
                    Text = "Xdebug", Checked = xdebugOn,
                    Click = async () =>
                    {
                        try
                        {
                            if (xdebugOn) Xdebug.Disable(phpDir);
                            else { SetStatus(I18n.T("log.downloading", "Xdebug")); await Xdebug.EnableAsync(phpDir); }
                            await ServiceManager.ReloadWebAsync();
                        }
                        catch (Exception ex) { await Dialogs.Warning(this, "Xdebug: " + ex.Message); }
                    },
                });
                php.Add(I18n.T("php.errorLog"), () => EditFile(Path.Combine(Paths.Logs, "php_errors.log")));
            }
            else php.Add(I18n.T("svc.getVersions"), async () => await OpenPackages("php"));

            var web = items.Sub(ServiceManager.Web.Title);
            web.Add(I18n.T("web.configFolder"), () => OpenFolder(Path.Combine(Paths.Etc, Settings.Current.WebServer)));
            web.Add(I18n.T("web.errorLog"), () => EditFile(Path.Combine(Paths.Logs, Settings.Current.WebServer + "-error.log")));
            web.Add(I18n.T("web.templates"), () => OpenFolder(Paths.Templates));
            web.Add(I18n.T("web.userTemplates"), () => OpenFolder(Paths.UserTemplates));

            var ssl = items.Sub("SSL / HTTPS");
            bool trusted = Certs.IsCaTrusted();
            ssl.Add(new Mi { Text = trusted ? I18n.T("ssl.trusted") : I18n.T("ssl.trust"), Disabled = trusted, Click = async () => await TrustCa() });
            ssl.Add(I18n.T("ssl.regenerate"), async () => { Certs.RegenerateSiteCertificates(); await ServiceManager.ReloadWebAsync(); });
            ssl.Add(I18n.T("ssl.folder"), () => OpenFolder(Paths.UserSsl));
            AddBrowserTrustItems(ssl);

            var tools = items.Sub(I18n.T("menu.tools"));
            tools.Add(I18n.T("main.terminal"), () => Terminal.Open());
            tools.Add(I18n.T("import.menu"), async () => await OpenImport(true));
            foreach (var ed in DesktopApps.Editors)
            {
                var editor = ed;
                if (DesktopApps.IsInstalled(editor))
                    tools.Add(I18n.T("tools.openIn", PackageTypes.Get(editor).Name), () => DesktopApps.OpenInEditor(editor, Settings.Current.DocumentRoot));
            }
            if (OS.IsWindows && DesktopApps.GitBash != null) tools.Add("Git Bash", () => DesktopApps.Start(DesktopApps.GitBash, "--cd=\"" + Settings.Current.DocumentRoot + "\""));
            tools.Add(I18n.T("port.checkNow"), async () => await FixPorts(ServiceManager.Rows.Where(x => x.Id != "php")));
            tools.Add(I18n.T("health.menu"), async () => await ShowHealth(true));
            if (OS.IsWindows)
                tools.Add(I18n.T("health.defender"), async () =>
                {
                    if (!await Dialogs.Confirm(this, I18n.T("health.defenderConfirm", Paths.Root, Settings.Current.DocumentRoot))) return;
                    var code = HealthCheck.AddDefenderExclusion();
                    if (code == 0) await Dialogs.Info(this, I18n.T("health.defenderDone"));
                });
            tools.Add(I18n.T("verify.menu"), async () => await ShowVerify(null));
            if (OS.IsWindows) tools.Add(I18n.T("tools.hosts"), () => WinUtil.RunElevated("notepad.exe", "\"" + HostsFile.FilePath + "\"", false));
            tools.Add(I18n.T("tools.root"), () => OpenFolder(Paths.Root));
            tools.Add(I18n.T("tools.logs"), () => OpenFolder(Paths.Logs));

            items.Sep();
            if (tray)
            {
                items.Add(I18n.T("main.packages"), async () => await OpenPackages(null));
                items.Add(I18n.T("main.settings"), async () => await OpenSettings());
            }
            if (AppInfo.UpdatesSupported)
                items.Add(I18n.T("menu.checkUpdates"), async () => await BackgroundChecks(true, true));
            items.Add(I18n.T("menu.about"), async () => { ShowFromTray(); await AboutForm.Show(this); });
            if (tray) items.Add(new Mi { Text = "♥ " + I18n.T("main.donate"), Color = Ui.Heart, Click = async () => await ShowDonate() });
            items.Sep();
            items.Add(I18n.T("menu.exit"), async () => await ExitAsync());
            return items;
        }

        /// <summary>Trust the Portly CA in Firefox and Java too (they keep their own certificate stores).</summary>
        void AddBrowserTrustItems(MenuList ssl)
        {
            if (FirefoxTrust.HasProfiles)
                ssl.Add(I18n.T("firefox.menu"), async () =>
                {
                    int n = FirefoxTrust.EnableAll();
                    await Dialogs.Info(this, n > 0 ? I18n.T("firefox.doneRestart", n) : I18n.T("firefox.already"));
                });
            var ff = FirefoxTrust.InstallDir();
            if (OS.IsWindows && ff != null && !FirefoxTrust.PolicySet(ff))
                ssl.Add(I18n.T("firefox.policyMenu"), async () =>
                {
                    if (!await Dialogs.Confirm(this, I18n.T("firefox.policyConfirm", FirefoxTrust.PolicyFile(ff)))) return;
                    var code = WinUtil.RunElevated(Paths.Exe, "--firefox-policy \"" + ff + "\"");
                    if (code == 0) await Dialogs.Info(this, I18n.T("firefox.doneRestart", 1));
                    else if (code != -2) await Dialogs.Warning(this, I18n.T("firefox.policyFailed", code));
                });
            var java = JavaTrust.FindHome();
            if (java != null && JavaTrust.Cacerts(java) != null)
                ssl.Add(I18n.T("java.menu"), async () =>
                {
                    if (JavaTrust.IsImported(java)) { await Dialogs.Info(this, I18n.T("java.already", java)); return; }
                    if (!await Dialogs.Confirm(this, I18n.T("java.confirm", java, JavaTrust.ImportCommand(java)))) return;
                    var err = JavaTrust.Import(java);
                    if (err == null) await Dialogs.Info(this, I18n.T("java.done", java)); else await Dialogs.Warning(this, err);
                });
        }

        /// <summary>All database tools: phpMyAdmin / Adminer (web), HeidiSQL, DBeaver, Beekeeper, DbGate, DB Browser, pgAdmin.</summary>
        void BuildDatabaseToolItems(MenuList items)
        {
            foreach (var t in new[] { "phpmyadmin", "adminer" })
            {
                var tool = t;
                if (PackageTypes.ActiveDir(tool) == null) continue;
                items.Add(PackageTypes.Get(tool).Name + "   (web)", () => Ui.Open(LocalUrl("/" + tool + "/")));
            }
            foreach (var t in DesktopApps.DatabaseGuis)
            {
                var tool = t;
                if (!DesktopApps.IsInstalled(tool)) continue;
                items.Add(PackageTypes.Get(tool).Name, () => DesktopApps.OpenDatabaseGui(tool));
            }
            if (DesktopApps.PgAdmin != null) items.Add("pgAdmin 4", () => DesktopApps.Start(DesktopApps.PgAdmin));
            if (items.Count > 0) items.Sep();
            items.Add(I18n.T("db.moreTools"), async () => await OpenPackages("heidisql"));
        }

        /// <summary>shared: menu of the installed database tools next to anchor (a single tool opens right away).</summary>
        public async Task OpenDatabaseTool(Control anchor = null)
        {
            var list = new MenuList();
            BuildDatabaseToolItems(list);
            if (list.Count == 1) { await list[0].Click(); return; }
            var flyout = Flyout(list, PlacementMode.Bottom);
            if (anchor != null) flyout.ShowAt(anchor);
            else flyout.ShowAt(this, true);
        }
    }
}
