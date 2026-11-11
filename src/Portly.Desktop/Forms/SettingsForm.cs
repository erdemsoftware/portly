using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>What the settings dialog changed, for the main window (it rebuilds texts, theme, folders and service state).</summary>
    public class SettingsResult
    {
        /// <summary>Settings were saved or imported (false = cancelled).</summary>
        public bool Saved;
        public bool DocumentRootChanged;
        public bool LanguageChanged;
        public bool ThemeChanged;
        public bool WebServerChanged;
        /// <summary>Install / remove of the Windows service was confirmed (runs in Applying).</summary>
        public bool ServiceModeChanged;
        /// <summary>Set when settings were imported from a file; the main window then offers the missing packages.</summary>
        public SettingsTransfer.ImportResult Imported;
        /// <summary>The background work (service restarts, web reload) started by Save; completes when it is done.</summary>
        public Task Applying = Task.CompletedTask;
    }

    public class SettingsForm : PortlyDialog
    {
        ComboBox _lang, _terminal, _theme, _mailTool, _backup, _dbEngine, _collation, _storageEngine;
        ProxySettingsPanel _proxy;
        NumericUpDown _pgPort, _mongoPort, _memPort, _tomcatPort, _backupKeep;
        TextBox _docRoot, _tld, _hostTemplate, _excluded;
        CheckBox _legacySql, _runAsService;
        readonly Dictionary<string, CheckBox> _features = new Dictionary<string, CheckBox>();
        CheckBox _autoVhosts, _ssl, _runAtStartup, _startMin, _startServices, _closeToTray, _addToPath, _checkUpdates, _allowLan;
        RadioButton _apache, _nginx, _caddy;
        NumericUpDown _http, _https, _mysql, _redis, _mailWeb, _mailSmtp, _phpPort, _phpChildren;
        readonly StackPanel _stack = new StackPanel { Spacing = 0 };

        public SettingsResult Result { get; private set; } = new SettingsResult();

        /// <summary>Opens the settings; returns what changed (Saved = false when cancelled).</summary>
        public static async Task<SettingsResult> Open(Window owner)
        {
            var f = new SettingsForm();
            await f.ShowDialog(owner);
            return f.Result;
        }

        public SettingsForm() : base(I18n.T("settings.title"), 690)
        {
            MakeResizable(700);
            var s = Settings.Current;
            Body.Spacing = 0;
            Body.Children.Add(_stack);

            // ---------------- appearance ----------------
            var gA = FormKit.Rows();
            _theme = FormKit.Combo(220);
            _theme.Items.Add(new Opt(I18n.T("settings.themeSystem"), "system"));
            _theme.Items.Add(new Opt(I18n.T("settings.themeLight"), "light"));
            _theme.Items.Add(new Opt(I18n.T("settings.themeDark"), "dark"));
            FormKit.Select(_theme, s.Theme);
            FormKit.AddRow(gA, I18n.T("settings.theme"), _theme);
            _lang = FormKit.Combo(260);
            _lang.Items.Add(new Opt(I18n.T("settings.langAuto"), "auto"));
            foreach (var li in I18n.Available()) _lang.Items.Add(new Opt(li.ToString(), li.Code));
            FormKit.Select(_lang, s.Language);
            FormKit.AddRow(gA, I18n.T("settings.language"), _lang);
            var translate = Ui.Link(I18n.T("settings.translate"), () => Ui.Open(AppInfo.TranslateUrl));
            translate.HorizontalAlignment = HorizontalAlignment.Left;
            FormKit.AddRow(gA, "", translate);
            Section(I18n.T("settings.appearance"), gA);

            // ---------------- general ----------------
            var g = FormKit.Rows();
            _docRoot = FormKit.Text(s.DocumentRoot, 280);
            var browse = Ui.Button(I18n.T("settings.browse"), BrowseRoot, null, "folder");
            FormKit.AddRow(g, I18n.T("settings.docRoot"), Ui.Row(_docRoot, browse));
            _tld = FormKit.Text(s.Tld, 110);
            FormKit.AddRow(g, I18n.T("settings.tld"), _tld);
            FormKit.AddRow(g, "", Hint(I18n.T("settings.tldHint")));
            // Laragon's "{name}": every folder under its own name; folders named like a domain (example.com) always are
            _hostTemplate = FormKit.Text(s.HostTemplate, 160);
            FormKit.AddRow(g, I18n.T("settings.hostTemplate"), _hostTemplate);
            FormKit.AddRow(g, "", Hint(I18n.T("settings.hostTemplateHint")));
            if (OS.IsWindows)
            {
                _terminal = FormKit.Combo(200);
                _terminal.Items.Add(new Opt("Command Prompt (cmd)", "cmd"));
                _terminal.Items.Add(new Opt("PowerShell", "powershell"));
                _terminal.SelectedIndex = s.Terminal == "powershell" ? 1 : 0;
                FormKit.AddRow(g, I18n.T("settings.terminal"), _terminal);
            }
            _autoVhosts = Check(I18n.T("settings.autoVhosts"), s.AutoVhosts, g);
            _ssl = Check(I18n.T("settings.ssl"), s.Ssl, g);
            if (OS.IsWindows) _runAtStartup = Check(I18n.T("settings.runAtStartup"), s.RunAtStartup, g);
            _startMin = Check(I18n.T("settings.startMinimized"), s.StartMinimized, g);
            _startServices = Check(I18n.T("settings.startServices"), s.StartServicesOnLaunch, g);
            _closeToTray = Check(I18n.T("settings.closeToTray"), s.CloseToTray, g);
            if (OS.IsWindows) _addToPath = Check(I18n.T("settings.addToPath"), s.AddToPath, g);
            if (Features.IsOn("archive"))
            {
                _excluded = FormKit.Text(s.ExcludedFolders, 320);
                FormKit.AddRow(g, I18n.T("archive.excluded"), _excluded);
                FormKit.AddRow(g, "", Hint(I18n.T("archive.excludedHint", Settings.DefaultExcludedFolders)));
            }
            _checkUpdates = Check(AppInfo.UpdatesSupported ? I18n.T("settings.checkUpdates") : I18n.T("settings.noUpdatesLegacy"), AppInfo.UpdatesSupported && s.CheckUpdates, g);
            _checkUpdates.IsEnabled = AppInfo.UpdatesSupported;
            _proxy = new ProxySettingsPanel();
            FormKit.AddRow(g, I18n.T("proxy.title"), _proxy);
            _proxy.VerticalAlignment = VerticalAlignment.Top;
            if (OS.IsWindows && Features.IsOn("serviceMode"))
            {
                _runAsService = Check(I18n.T("service.setting"), s.RunAsService, g);
                FormKit.AddRow(g, "", Hint(I18n.T("service.settingHint"), true));
            }
            Section(I18n.T("settings.general"), g);

            // ---------------- services ----------------
            var g2 = FormKit.Rows();
            var web = Ui.Row();
            web.Spacing = 16;
            _apache = new RadioButton { Content = "Apache", GroupName = "web", IsChecked = s.WebServer == "apache" };
            _nginx = new RadioButton { Content = "Nginx", GroupName = "web", IsChecked = s.WebServer == "nginx" };
            web.Children.Add(_apache);
            web.Children.Add(_nginx);
            // Caddy only while its optional feature is on (Windows 10+ edition)
            _caddy = new RadioButton { Content = "Caddy", GroupName = "web", IsChecked = s.WebServer == "caddy", IsVisible = !AppInfo.IsLegacy && Features.IsOn("caddy") };
            web.Children.Add(_caddy);
            FormKit.AddRow(g2, I18n.T("settings.webServer"), web);
            _dbEngine = FormKit.Combo(200);
            foreach (var en in new[] { "mysql", "mariadb" })
                _dbEngine.Items.Add(new Opt(PackageTypes.Get(en).Name + (PackageTypes.Installed(en).Count > 0 ? "" : "  (" + I18n.T("svc.notInstalled") + ")"), en));
            FormKit.Select(_dbEngine, s.DbEngine);
            FormKit.AddRow(g2, I18n.T("settings.dbEngine"), _dbEngine);
            _mailTool = FormKit.Combo(200);
            foreach (var tool in MailpitService.Tools)
            {
                var installed = PackageTypes.Installed(tool).Count > 0;
                _mailTool.Items.Add(new Opt(PackageTypes.Get(tool).Name + (installed ? "" : "  (" + I18n.T("svc.notInstalled") + ")"), tool));
            }
            FormKit.Select(_mailTool, s.MailTool);
            FormKit.AddRow(g2, I18n.T("settings.mailTool"), _mailTool);
            _http = Port(s.HttpPort); FormKit.AddRow(g2, I18n.T("settings.httpPort"), _http);
            _https = Port(s.HttpsPort); FormKit.AddRow(g2, I18n.T("settings.httpsPort"), _https);
            _mysql = Port(s.MysqlPort); FormKit.AddRow(g2, I18n.T("settings.mysqlPort"), _mysql);
            _redis = Port(s.RedisPort); FormKit.AddRow(g2, I18n.T("settings.redisPort"), _redis);
            _pgPort = Port(s.PostgresPort); FormKit.AddRow(g2, "PostgreSQL port", _pgPort);
            _mongoPort = Port(s.MongoPort); FormKit.AddRow(g2, "MongoDB port", _mongoPort);
            _memPort = Port(s.MemcachedPort); FormKit.AddRow(g2, "Memcached port", _memPort);
            _tomcatPort = Port(s.TomcatPort);
            if (TomcatService.Enabled) FormKit.AddRow(g2, I18n.T("settings.tomcatPort"), _tomcatPort);
            _mailWeb = Port(s.MailpitWebPort); FormKit.AddRow(g2, I18n.T("settings.mailWebPort"), _mailWeb);
            _mailSmtp = Port(s.MailpitSmtpPort); FormKit.AddRow(g2, I18n.T("settings.mailSmtpPort"), _mailSmtp);
            _phpPort = Port(s.PhpBasePort); FormKit.AddRow(g2, I18n.T("settings.phpPort"), _phpPort);
            _phpChildren = FormKit.Number(1, 64, s.PhpChildren, 150);
            FormKit.AddRow(g2, I18n.T("settings.phpChildren"), _phpChildren);
            FormKit.AddRow(g2, "", Hint(I18n.T("settings.portHint")));
            _allowLan = Check(I18n.T("settings.allowLan"), s.AllowLan, g2);
            FormKit.AddRow(g2, "", Hint(I18n.T("settings.allowLanHint"), true));
            Section(I18n.T("settings.services"), g2);

            // ---------------- databases ----------------
            var g3 = FormKit.Rows();
            // works at once on the running server, not with the Save button
            var rootHint = Hint(DatabaseUi.RootStatus());
            var rootButton = Ui.Button(I18n.T(s.HasRootPassword ? "db.password" : "dbroot.set"), async () =>
            {
                await DatabaseUi.RootPassword(this);
                rootHint.Text = DatabaseUi.RootStatus();
                SetRootButton(Settings.Current.HasRootPassword);
            });
            _rootButton = rootButton;
            rootButton.HorizontalAlignment = HorizontalAlignment.Left;
            FormKit.AddRow(g3, I18n.T("dbroot.title"), rootButton);
            FormKit.AddRow(g3, "", rootHint);
            _backup = FormKit.Combo(220);
            foreach (var h in new[] { 0, 24, 12, 6 })
                _backup.Items.Add(new Opt(h == 0 ? I18n.T("settings.backupOff") : I18n.T("settings.backupEvery", h), h.ToString()));
            FormKit.Select(_backup, s.AutoBackupHours.ToString());
            FormKit.AddRow(g3, I18n.T("settings.autoBackup"), _backup);
            _backupKeep = FormKit.Number(1, 100, s.AutoBackupKeep, 150);
            FormKit.AddRow(g3, I18n.T("settings.backupKeep"), _backupKeep);
            FormKit.AddRow(g3, "", Hint(I18n.T("settings.backupHint")));
            _legacySql = Check(I18n.T("settings.legacySql"), s.LegacySqlMode, g3);
            // database "language" and default table type: only what the chosen engine / version supports
            _collation = FormKit.Combo(260);
            FormKit.AddRow(g3, I18n.T("dbui.collation"), _collation);
            _storageEngine = FormKit.Combo(160);
            FormKit.AddRow(g3, I18n.T("dbui.storageEngine"), _storageEngine);
            FormKit.AddRow(g3, "", Hint(I18n.T("dbui.collationHint")));
            FillDbDefaults(s.DbCollation, s.DbStorageEngine);
            _dbEngine.SelectionChanged += (o, e) => FillDbDefaults(FormKit.Value(_collation), FormKit.Value(_storageEngine));
            Section("MySQL", g3);

            // optional features: rarely used ones start off and are switched on here
            var g4 = FormKit.Rows();
            FormKit.AddRow(g4, "", Hint(I18n.T("features.intro")));
            foreach (var f in Features.All)
            {
                // Windows service mode and WSL access exist only on Windows
                if (!OS.IsWindows && (f.Id == "serviceMode" || f.Id == "wsl")) continue;
                _features[f.Id] = Check(I18n.T(f.TitleKey), Features.IsOn(f.Id), g4);
                FormKit.AddRow(g4, "", Hint(I18n.T(f.HintKey), true));
            }
            Section(I18n.T("features.title"), g4);

            // move the whole setup to another computer
            Buttons.Children.Add(Ui.Button(I18n.T("transfer.export"), ExportSettings, null, "box"));
            Buttons.Children.Add(Ui.Button(I18n.T("transfer.import"), ImportSettings, null, "import"));
            Buttons.Children.Add(new Border { Width = 16 });
            Buttons.Children.Add(Ui.Button(I18n.T("common.save"), Save, "primary"));
            Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(false)));
        }

        Button _rootButton;

        void SetRootButton(bool has) { _rootButton.Content = I18n.T(has ? "db.password" : "dbroot.set"); }

        async Task BrowseRoot()
        {
            var start = Directory.Exists(_docRoot.Text) ? await StorageProvider.TryGetFolderFromPathAsync(_docRoot.Text) : null;
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, SuggestedStartLocation = start });
            if (picked.Count == 0) return;
            var path = picked[0].TryGetLocalPath();
            if (path != null) _docRoot.Text = path;
        }

        void FillDbDefaults(string collation, string storage)
        {
            var engine = FormKit.Value(_dbEngine);
            var v = PackageTypes.ParseVersion(PackageTypes.ActiveFolder(engine));
            _collation.Items.Clear();
            foreach (var c in DbOptions.Collations(engine, v)) _collation.Items.Add(new Opt(c, c));
            FormKit.Select(_collation, collation);
            _storageEngine.Items.Clear();
            foreach (var x in DbOptions.StorageEngines(engine)) _storageEngine.Items.Add(new Opt(x, x));
            _storageEngine.SelectedItem = _storageEngine.Items.OfType<Opt>().FirstOrDefault(i => i.Value.Equals(storage, StringComparison.OrdinalIgnoreCase)) ?? _storageEngine.Items.OfType<Opt>().FirstOrDefault();
        }

        /// <summary>Install / remove the Portly Windows service (one UAC prompt). Runs after the window closed.</summary>
        static async Task SwitchServiceModeAsync(bool on)
        {
            var s = Settings.Current;
            if (on)
            {
                // the programs move from this window into the service
                await ServiceManager.StopAllAsync();
                int code = ServiceMode.Install();
                if (code != 0) { Log.Error(I18n.T("service.installFailed", code)); return; }
                s.RunAsService = true;
                s.Save();
                Log.Info(I18n.T("service.installed"));
                ServiceMode.StartPolling();
                await ServiceManager.StartAllAsync();
            }
            else
            {
                int code = ServiceMode.Uninstall();
                if (code != 0) { Log.Error(I18n.T("service.installFailed", code)); Features.Set("serviceMode", true); s.Save(); return; }
                s.RunAsService = false;
                s.Save();
                Log.Info(I18n.T("service.removed"));
            }
        }

        async Task ExportSettings()
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "portly-settings-" + DateTime.Now.ToString("yyyyMMdd") + ".zip",
                DefaultExtension = "zip",
                FileTypeChoices = new[] { new FilePickerFileType("Zip (*.zip)") { Patterns = new[] { "*.zip" } } },
            });
            var path = file == null ? null : file.TryGetLocalPath();
            if (path == null) return;
            try
            {
                await Task.Run(() => SettingsTransfer.Export(path));
                await Dialogs.Info(this, I18n.T("transfer.exported", path, I18n.T("db.backup")));
            }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
        }

        async Task ImportSettings()
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Zip (*.zip)") { Patterns = new[] { "*.zip" } } },
            });
            var path = picked.Count == 0 ? null : picked[0].TryGetLocalPath();
            if (path == null) return;
            if (!await Dialogs.Confirm(this, I18n.T("transfer.importConfirm"))) return;
            var oldRoot = Settings.Current.DocumentRoot;
            try { Result.Imported = await Task.Run(() => SettingsTransfer.Import(path)); }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); return; }
            // the settings on this form are now out of date: close without saving them
            Result.Saved = true;
            Result.LanguageChanged = Result.ThemeChanged = true;
            Result.DocumentRootChanged = oldRoot != Settings.Current.DocumentRoot;
            Close(true);
        }

        void Section(string title, Control grid)
        {
            var h = Ui.Section(title);
            h.Margin = new Thickness(2, _stack.Children.Count == 0 ? 0 : 18, 0, 8);
            _stack.Children.Add(h);
            _stack.Children.Add(Ui.Card(grid, new Thickness(18, 14)));
        }

        static CheckBox Check(string text, bool value, Grid g)
        {
            var c = FormKit.Check(text, value);
            FormKit.AddWide(g, c);
            return c;
        }

        static TextBlock Hint(string text, bool underCheck = false)
        {
            var l = FormKit.Hint(text);
            l.Margin = new Thickness(underCheck ? 28 - 16 : 0, -4, 0, 0);
            return l;
        }

        static NumericUpDown Port(int value) { return FormKit.Number(1, 65535, value, 150); }

        async Task Save()
        {
            var s = Settings.Current;
            var tld = _tld.Text.Trim().Trim('.').ToLowerInvariant();
            if (!Regex.IsMatch(tld, @"^[a-z][a-z0-9-]{0,20}$")) { await Dialogs.Warning(this, I18n.T("settings.badTld")); return; }
            var hostTemplate = _hostTemplate.Text.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(hostTemplate, @"^([a-z0-9-]+\.)*\{name\}(\.([a-z0-9-]+|\{tld\}))*$")) { await Dialogs.Warning(this, I18n.T("settings.badHostTemplate")); return; }
            var root = _docRoot.Text.Trim();
            try
            {
                if (root == "" || !Path.IsPathRooted(root)) throw new Exception();
                Directory.CreateDirectory(root);
            }
            catch { await Dialogs.Warning(this, I18n.T("settings.badRoot")); return; }
            var proxyError = _proxy.Validate();
            if (proxyError != null) { await Dialogs.Warning(this, proxyError); return; }
            var ports = new[] { Num(_http), Num(_https), Num(_mysql), Num(_redis), Num(_mailWeb), Num(_mailSmtp), Num(_pgPort), Num(_mongoPort), Num(_memPort) };
            if (TomcatService.Enabled) ports = ports.Concat(new[] { Num(_tomcatPort) }).ToArray();
            if (ports.Distinct().Count() != ports.Length) { await Dialogs.Warning(this, I18n.T("settings.duplicatePorts")); return; }
            // the PHP FastCGI range must not overlap the other ports
            var phpLow = Num(_phpPort);
            if (ports.Any(p => p >= phpLow && p < phpLow + 100)) { await Dialogs.Warning(this, I18n.T("settings.phpPortOverlap", phpLow, phpLow + 99)); return; }

            var server = _caddy.IsChecked == true && _caddy.IsVisible ? "caddy" : _nginx.IsChecked == true ? "nginx" : "apache";
            bool webChanged = server != s.WebServer;
            bool excludedChanged = _excluded != null && _excluded.Text.Trim() != s.ExcludedFolders;
            if (excludedChanged) s.ExcludedFolders = _excluded.Text.Trim();
            bool autoVhosts = _autoVhosts.IsChecked == true, ssl = _ssl.IsChecked == true, allowLan = _allowLan.IsChecked == true;
            bool webReload = root != s.DocumentRoot || tld != s.Tld || hostTemplate != s.HostTemplate || autoVhosts != s.AutoVhosts || ssl != s.Ssl
                || Num(_http) != s.HttpPort || Num(_https) != s.HttpsPort || allowLan != s.AllowLan
                || Num(_phpPort) != s.PhpBasePort || Num(_phpChildren) != s.PhpChildren
                || Num(_mailSmtp) != s.MailpitSmtpPort || Num(_mysql) != s.MysqlPort || excludedChanged;
            var dbEngine = FormKit.Value(_dbEngine);
            if (dbEngine != s.DbEngine)
            {
                // the other engine has its own data folder: offer to bring the databases along (dump and load)
                var sw = await DatabaseUi.BeforeSwitch(this, dbEngine, PackageTypes.ActiveFolder(dbEngine));
                if (sw == DatabaseUi.SwitchResult.Cancelled) return;
            }
            var collation = FormKit.Value(_collation);
            var storage = FormKit.Value(_storageEngine);
            bool dbDefaultsChanged = collation != s.DbCollation || storage != s.DbStorageEngine;
            s.DbCollation = collation;
            s.DbStorageEngine = storage;
            bool mysqlRestart = Num(_mysql) != s.MysqlPort || allowLan != s.AllowLan || dbEngine != s.DbEngine || dbDefaultsChanged;
            bool mysqlWasActive = ServiceManager.MySql.IsActive;
            bool pgRestart = Num(_pgPort) != s.PostgresPort || allowLan != s.AllowLan;
            bool mongoRestart = Num(_mongoPort) != s.MongoPort || allowLan != s.AllowLan;
            bool memRestart = Num(_memPort) != s.MemcachedPort || allowLan != s.AllowLan;
            bool tomcatRestart = Num(_tomcatPort) != s.TomcatPort || allowLan != s.AllowLan;
            bool redisRestart = Num(_redis) != s.RedisPort || allowLan != s.AllowLan;
            var mailTool = FormKit.Value(_mailTool);
            bool mailRestart = Num(_mailWeb) != s.MailpitWebPort || Num(_mailSmtp) != s.MailpitSmtpPort || mailTool != s.MailTool;
            bool mailWasActive = ServiceManager.Mailpit.IsActive;
            var lang = FormKit.Value(_lang);
            var theme = FormKit.Value(_theme);
            var result = Result;
            result.LanguageChanged = lang != s.Language;
            result.ThemeChanged = theme != s.Theme;
            result.DocumentRootChanged = root != s.DocumentRoot;
            result.WebServerChanged = webChanged;

            s.Language = lang;
            s.Theme = theme;
            s.DocumentRoot = root;
            if (tld != s.Tld) s.Raw.Set("general", "TldChosen", "1");
            s.Tld = tld;
            s.HostTemplate = hostTemplate;
            if (_terminal != null) s.Terminal = FormKit.Value(_terminal);
            s.AutoVhosts = autoVhosts;
            s.Ssl = ssl;
            if (_runAtStartup != null)
            {
                bool startup = _runAtStartup.IsChecked == true;
                if (s.RunAtStartup != startup || startup) WinUtil.SetRunAtStartup(startup);
                s.RunAtStartup = startup;
            }
            s.StartMinimized = _startMin.IsChecked == true;
            s.StartServicesOnLaunch = _startServices.IsChecked == true;
            s.CloseToTray = _closeToTray.IsChecked == true;
            bool pathChanged = false;
            if (_addToPath != null)
            {
                pathChanged = s.AddToPath != (_addToPath.IsChecked == true);
                s.AddToPath = _addToPath.IsChecked == true;
            }
            if (AppInfo.UpdatesSupported) s.CheckUpdates = _checkUpdates.IsChecked == true;
            _proxy.Save(s);
            s.HttpPort = Num(_http);
            s.HttpsPort = Num(_https);
            s.MysqlPort = Num(_mysql);
            s.RedisPort = Num(_redis);
            s.MailpitWebPort = Num(_mailWeb);
            s.MailpitSmtpPort = Num(_mailSmtp);
            s.PhpBasePort = Num(_phpPort);
            s.PhpChildren = Num(_phpChildren);
            bool lanChanged = s.AllowLan != allowLan;
            bool legacySql = _legacySql.IsChecked == true;
            bool sqlModeChanged = s.LegacySqlMode != legacySql;
            s.LegacySqlMode = legacySql;
            // service mode: switching the feature off also removes the Windows service
            bool serviceFeature = _features.ContainsKey("serviceMode");
            bool serviceWanted = serviceFeature && _features["serviceMode"].IsChecked == true && (_runAsService == null ? s.RunAsService : _runAsService.IsChecked == true);
            bool serviceChange = serviceWanted != s.RunAsService;
            if (serviceChange && !await Dialogs.Confirm(this, I18n.T(serviceWanted ? "service.confirmOn" : "service.confirmOff", Paths.Root))) serviceChange = false;
            foreach (var kv in _features) Features.Set(kv.Key, kv.Value.IsChecked == true);
            // WSL access switched off: also remove its database user, firewall rule and bind address
            if (OS.IsWindows && !Features.IsOn("wsl") && s.WslAccess && await Dialogs.Confirm(this, I18n.T("wsl.offConfirm")))
            {
                WslAccess.Disable();
                mysqlRestart = true;
            }
            if (OS.IsWindows && !serviceChange && s.RunAsService && serviceFeature && _features["serviceMode"].IsChecked != true) Features.Set("serviceMode", true); // refused: keep it controllable
            s.AllowLan = allowLan;
            if (sqlModeChanged) mysqlRestart = true;
            // LAN access needs an inbound firewall rule for the web ports (removed again when switched off)
            if (OS.IsWindows && lanChanged && await Dialogs.Confirm(this, I18n.T(allowLan ? "settings.firewallAdd" : "settings.firewallRemove")))
                HealthCheck.SetFirewallRule(allowLan);
            if (!mysqlWasActive) s.DbEngine = dbEngine;
            s.MailTool = mailTool;
            s.PostgresPort = Num(_pgPort);
            s.MongoPort = Num(_mongoPort);
            s.MemcachedPort = Num(_memPort);
            s.TomcatPort = Num(_tomcatPort);
            s.AutoBackupHours = int.Parse(FormKit.Value(_backup));
            s.AutoBackupKeep = Num(_backupKeep);
            s.Save();

            I18n.Load(lang);
            Portly.Desktop.UI.Theme.Apply(Application.Current, theme);
            if (pathChanged) Terminal.ApplyUserPath();
            Log.Info(I18n.T("log.settingsSaved"));

            result.Saved = true;
            result.ServiceModeChanged = serviceChange;
            result.Applying = Task.Run(async () =>
            {
                try
                {
                    if (serviceChange) { await SwitchServiceModeAsync(serviceWanted); return; }
                    if (ServiceMode.IsController)
                    {
                        // the Windows service runs everything: save what changed and let it restart with the new settings
                        if (webChanged) Settings.Current.WebServer = server;
                        Settings.Current.DbEngine = dbEngine;
                        Settings.Current.Save();
                        await ServiceMode.CommandAsync("reload");
                        return;
                    }
                    if (webChanged) await ServiceManager.SwitchWebServerAsync(server);
                    if (mysqlRestart && mysqlWasActive)
                    {
                        await ServiceManager.MySql.StopAsync();   // stops the engine that is running now
                        Settings.Current.DbEngine = dbEngine;
                        Settings.Current.Save();
                        await ServiceManager.MySql.StartAsync();
                    }
                    if (pgRestart && ServiceManager.Postgres.IsActive) await ServiceManager.Postgres.RestartAsync();
                    if (mongoRestart && ServiceManager.Mongo.IsActive) await ServiceManager.Mongo.RestartAsync();
                    if (memRestart && ServiceManager.Memcached.IsActive) await ServiceManager.Memcached.RestartAsync();
                    // feature switched off: Tomcat no longer has a row, so stop it here
                    if (ServiceManager.Tomcat.IsActive && (!TomcatService.Enabled || tomcatRestart))
                    {
                        if (TomcatService.Enabled) await ServiceManager.Tomcat.RestartAsync();
                        else await ServiceManager.Tomcat.StopAsync();
                    }
                    if (redisRestart && ServiceManager.Redis.IsActive) await ServiceManager.Redis.RestartAsync();
                    if (mailRestart && mailWasActive)
                    {
                        await ServiceManager.Mailpit.StopAsync();
                        await ServiceManager.Mailpit.StartAsync();
                    }
                    if (webReload && !webChanged) await ServiceManager.ReloadWebAsync();
                    else if (!webChanged) ServiceManager.RefreshSites();
                }
                catch (Exception ex) { Log.Error("Settings apply", ex); }
            });
            Close(true);
        }

        static int Num(NumericUpDown n) { return (int)(n.Value ?? n.Minimum); }

        public static Window Preview() { return new SettingsForm(); }
    }
}
