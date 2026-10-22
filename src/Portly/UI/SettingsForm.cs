using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    public class SettingsForm : Form
    {
        ComboBox _lang, _terminal, _theme, _mailTool, _backup, _dbEngine;
        NumericUpDown _pgPort, _mongoPort, _memPort;
        NumericUpDown _backupKeep;
        TextBox _docRoot, _tld;
        CheckBox _legacySql;
        CheckBox _autoVhosts, _ssl, _runAtStartup, _startMin, _startServices, _closeToTray, _addToPath, _checkUpdates, _allowLan;
        RadioButton _apache, _nginx;
        NumericUpDown _http, _https, _mysql, _redis, _mailWeb, _mailSmtp, _phpPort, _phpChildren;

        public SettingsForm()
        {
            Ui.Apply(this);
            Text = I18n.T("settings.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = Ui.S(640, 620);
            var s = Settings.Current;

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Bg, Padding = Ui.P(24, 16, 24, 8) };
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Color.Transparent };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // ---------------- appearance ----------------
            var gA = Grid();
            _theme = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(220) };
            _theme.Items.Add(new Item(I18n.T("settings.themeSystem"), "system"));
            _theme.Items.Add(new Item(I18n.T("settings.themeLight"), "light"));
            _theme.Items.Add(new Item(I18n.T("settings.themeDark"), "dark"));
            _theme.SelectedItem = _theme.Items.Cast<Item>().First(i => i.Value == s.Theme);
            AddRow(gA, I18n.T("settings.theme"), _theme);
            _lang = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(260) };
            _lang.Items.Add(new Item(I18n.T("settings.langAuto"), "auto"));
            foreach (var li in I18n.Available()) _lang.Items.Add(new Item(li.ToString(), li.Code));
            _lang.SelectedItem = _lang.Items.Cast<Item>().FirstOrDefault(i => i.Value == s.Language) ?? _lang.Items[0];
            AddRow(gA, I18n.T("settings.language"), _lang);
            AddRow(gA, "", Ui.Link(I18n.T("settings.translate"), () => WinUtil.Open(AppInfo.TranslateUrl)));
            Section(stack, I18n.T("settings.appearance"), gA);

            // ---------------- general ----------------
            var g = Grid();
            var rootPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Color.Transparent };
            _docRoot = new TextBox { Text = s.DocumentRoot, Width = Ui.S(280), Margin = Ui.P(0, 4, 8, 0) };
            rootPanel.Controls.Add(_docRoot);
            rootPanel.Controls.Add(Ui.Button(I18n.T("settings.browse"), (o, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = _docRoot.Text, ShowNewFolderButton = true })
                    if (d.ShowDialog(this) == DialogResult.OK) _docRoot.Text = d.SelectedPath;
            }, false, "folder"));
            AddRow(g, I18n.T("settings.docRoot"), rootPanel);
            _tld = new TextBox { Text = s.Tld, Width = Ui.S(110) };
            AddRow(g, I18n.T("settings.tld"), _tld);
            AddRow(g, "", Hint(I18n.T("settings.tldHint")));
            _terminal = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(200) };
            _terminal.Items.Add(new Item("Command Prompt (cmd)", "cmd"));
            _terminal.Items.Add(new Item("PowerShell", "powershell"));
            _terminal.SelectedIndex = s.Terminal == "powershell" ? 1 : 0;
            AddRow(g, I18n.T("settings.terminal"), _terminal);
            _autoVhosts = Check(I18n.T("settings.autoVhosts"), s.AutoVhosts, g);
            _ssl = Check(I18n.T("settings.ssl"), s.Ssl, g);
            _runAtStartup = Check(I18n.T("settings.runAtStartup"), s.RunAtStartup, g);
            _startMin = Check(I18n.T("settings.startMinimized"), s.StartMinimized, g);
            _startServices = Check(I18n.T("settings.startServices"), s.StartServicesOnLaunch, g);
            _closeToTray = Check(I18n.T("settings.closeToTray"), s.CloseToTray, g);
            _addToPath = Check(I18n.T("settings.addToPath"), s.AddToPath, g);
            _checkUpdates = Check(AppInfo.UpdatesSupported ? I18n.T("settings.checkUpdates") : I18n.T("settings.noUpdatesLegacy"), AppInfo.UpdatesSupported && s.CheckUpdates, g);
            _checkUpdates.Enabled = AppInfo.UpdatesSupported;
            Section(stack, I18n.T("settings.general"), g);

            // ---------------- services ----------------
            var g2 = Grid();
            var web = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Color.Transparent };
            _apache = new RadioButton { Text = "Apache", AutoSize = true, Checked = s.WebServer == "apache" };
            _nginx = new RadioButton { Text = "Nginx", AutoSize = true, Checked = s.WebServer == "nginx" };
            web.Controls.Add(_apache);
            web.Controls.Add(_nginx);
            AddRow(g2, I18n.T("settings.webServer"), web);
            _dbEngine = new ThemedCombo { Width = Ui.S(200) };
            foreach (var en in new[] { "mysql", "mariadb" })
                _dbEngine.Items.Add(new Item(PackageTypes.Get(en).Name + (PackageTypes.Installed(en).Count > 0 ? "" : "  (" + I18n.T("svc.notInstalled") + ")"), en));
            _dbEngine.SelectedItem = _dbEngine.Items.Cast<Item>().First(i => i.Value == s.DbEngine);
            AddRow(g2, I18n.T("settings.dbEngine"), _dbEngine);
            _mailTool = new ThemedCombo { Width = Ui.S(200) };
            foreach (var tool in MailpitService.Tools)
            {
                var installed = PackageTypes.Installed(tool).Count > 0;
                _mailTool.Items.Add(new Item(PackageTypes.Get(tool).Name + (installed ? "" : "  (" + I18n.T("svc.notInstalled") + ")"), tool));
            }
            _mailTool.SelectedItem = _mailTool.Items.Cast<Item>().First(i => i.Value == s.MailTool);
            AddRow(g2, I18n.T("settings.mailTool"), _mailTool);
            _http = Port(s.HttpPort); AddRow(g2, I18n.T("settings.httpPort"), _http);
            _https = Port(s.HttpsPort); AddRow(g2, I18n.T("settings.httpsPort"), _https);
            _mysql = Port(s.MysqlPort); AddRow(g2, I18n.T("settings.mysqlPort"), _mysql);
            _redis = Port(s.RedisPort); AddRow(g2, I18n.T("settings.redisPort"), _redis);
            _pgPort = Port(s.PostgresPort); AddRow(g2, "PostgreSQL port", _pgPort);
            _mongoPort = Port(s.MongoPort); AddRow(g2, "MongoDB port", _mongoPort);
            _memPort = Port(s.MemcachedPort); AddRow(g2, "Memcached port", _memPort);
            _mailWeb = Port(s.MailpitWebPort); AddRow(g2, I18n.T("settings.mailWebPort"), _mailWeb);
            _mailSmtp = Port(s.MailpitSmtpPort); AddRow(g2, I18n.T("settings.mailSmtpPort"), _mailSmtp);
            _phpPort = Port(s.PhpBasePort); AddRow(g2, I18n.T("settings.phpPort"), _phpPort);
            _phpChildren = new NumericUpDown { Minimum = 1, Maximum = 64, Value = Math.Max(1, Math.Min(64, s.PhpChildren)), Width = Ui.S(90) };
            AddRow(g2, I18n.T("settings.phpChildren"), _phpChildren);
            AddRow(g2, "", Hint(I18n.T("settings.portHint")));
            _allowLan = Check(I18n.T("settings.allowLan"), s.AllowLan, g2);
            AddRow(g2, "", Hint(I18n.T("settings.allowLanHint")));
            Section(stack, I18n.T("settings.services"), g2);

            // ---------------- databases ----------------
            var g3 = Grid();
            _backup = new ThemedCombo { Width = Ui.S(220) };
            foreach (var h in new[] { 0, 24, 12, 6 })
                _backup.Items.Add(new Item(h == 0 ? I18n.T("settings.backupOff") : I18n.T("settings.backupEvery", h), h.ToString()));
            _backup.SelectedItem = _backup.Items.Cast<Item>().FirstOrDefault(i => i.Value == s.AutoBackupHours.ToString()) ?? _backup.Items[0];
            AddRow(g3, I18n.T("settings.autoBackup"), _backup);
            _backupKeep = new NumericUpDown { Minimum = 1, Maximum = 100, Value = Math.Max(1, Math.Min(100, s.AutoBackupKeep)), Width = Ui.S(90) };
            AddRow(g3, I18n.T("settings.backupKeep"), _backupKeep);
            AddRow(g3, "", Hint(I18n.T("settings.backupHint")));
            _legacySql = Check(I18n.T("settings.legacySql"), s.LegacySqlMode, g3);
            Section(stack, "MySQL", g3);

            scroll.Controls.Add(stack);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = Ui.P(24, 12, 24, 14), BackColor = Ui.Bg };
            var cancel = Ui.Button(I18n.T("common.cancel"), null);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Margin = Padding.Empty;
            var ok = Ui.Button(I18n.T("common.save"), (o, e) => Save(), true);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            // move the whole setup to another computer
            var import = Ui.Button(I18n.T("transfer.import"), (o, e) => ImportSettings(), false, "import");
            import.Margin = Ui.P(0, 0, 24, 0);
            var export = Ui.Button(I18n.T("transfer.export"), (o, e) => ExportSettings(), false, "box");
            export.Margin = Ui.P(0, 0, 8, 0);
            buttons.Controls.Add(import);
            buttons.Controls.Add(export);
            Controls.Add(scroll);
            Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Line });
            Controls.Add(buttons);
            scroll.BringToFront();
            CancelButton = cancel;
        }

        /// <summary>Set when settings were imported from a file; the main window then offers the missing packages.</summary>
        public SettingsTransfer.ImportResult Imported;

        void ExportSettings()
        {
            using (var dlg = new SaveFileDialog { Filter = "Zip (*.zip)|*.zip", FileName = "portly-settings-" + DateTime.Now.ToString("yyyyMMdd") + ".zip" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SettingsTransfer.Export(dlg.FileName);
                    Ui.Info(this, I18n.T("transfer.exported", dlg.FileName, I18n.T("db.backup")));
                }
                catch (Exception ex) { Ui.Error(this, ex.Message); }
            }
        }

        void ImportSettings()
        {
            using (var dlg = new OpenFileDialog { Filter = "Zip (*.zip)|*.zip" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!Ui.Confirm(this, I18n.T("transfer.importConfirm"))) return;
                try { Imported = SettingsTransfer.Import(dlg.FileName); }
                catch (Exception ex) { Ui.Error(this, ex.Message); return; }
            }
            // the settings on this form are now out of date: close without saving them
            DialogResult = DialogResult.OK;
            Close();
        }

        static void Section(TableLayoutPanel stack, string title, TableLayoutPanel grid)
        {
            var h = Ui.Label(Ui.Upper(title), Ui.Small, Ui.Muted);
            h.Margin = Ui.P(2, stack.Controls.Count == 0 ? 0 : 18, 0, 8);
            stack.Controls.Add(h);
            var card = new Card { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Ui.P(18, 14, 18, 14), Dock = DockStyle.Fill, Margin = Padding.Empty, Radius = 12 };
            grid.Dock = DockStyle.Top;
            card.Controls.Add(grid);
            stack.Controls.Add(card);
        }

        static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        static void AddRow(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++;
            var l = Ui.Label(label);
            l.Margin = Ui.P(0, 8, 16, 4);
            c.Margin = Ui.P(0, 4, 0, 4);
            t.Controls.Add(l, 0, r);
            t.Controls.Add(c, 1, r);
        }

        static CheckBox Check(string text, bool value, TableLayoutPanel t)
        {
            var c = new CheckBox { Text = text, Checked = value, AutoSize = true, MaximumSize = new Size(Ui.S(540), 0), BackColor = Color.Transparent };
            int r = t.RowCount++;
            c.Margin = Ui.P(0, 5, 0, 5);
            t.Controls.Add(c, 0, r);
            t.SetColumnSpan(c, 2);
            return c;
        }

        static Label Hint(string text)
        {
            var l = Ui.Label(text, Ui.Small, Ui.Muted);
            l.MaximumSize = new Size(Ui.S(380), 0);
            return l;
        }

        static NumericUpDown Port(int value)
        {
            return new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Max(1, Math.Min(65535, value)), Width = Ui.S(90) };
        }

        void Save()
        {
            var s = Settings.Current;
            var tld = _tld.Text.Trim().Trim('.').ToLowerInvariant();
            if (!Regex.IsMatch(tld, @"^[a-z][a-z0-9-]{0,20}$")) { Ui.Error(this, I18n.T("settings.badTld")); return; }
            var root = _docRoot.Text.Trim();
            try
            {
                if (root == "" || !Path.IsPathRooted(root)) throw new Exception();
                Directory.CreateDirectory(root);
            }
            catch { Ui.Error(this, I18n.T("settings.badRoot")); return; }
            var ports = new[] { _http.Value, _https.Value, _mysql.Value, _redis.Value, _mailWeb.Value, _mailSmtp.Value, _pgPort.Value, _mongoPort.Value, _memPort.Value };
            if (ports.Distinct().Count() != ports.Length) { Ui.Error(this, I18n.T("settings.duplicatePorts")); return; }
            // the PHP FastCGI range must not overlap the other ports
            var phpLow = (int)_phpPort.Value;
            if (ports.Any(p => p >= phpLow && p < phpLow + 100)) { Ui.Error(this, I18n.T("settings.phpPortOverlap", phpLow, phpLow + 99)); return; }

            bool webChanged = (_nginx.Checked ? "nginx" : "apache") != s.WebServer;
            bool webReload = root != s.DocumentRoot || tld != s.Tld || _autoVhosts.Checked != s.AutoVhosts || _ssl.Checked != s.Ssl
                || (int)_http.Value != s.HttpPort || (int)_https.Value != s.HttpsPort || _allowLan.Checked != s.AllowLan
                || (int)_phpPort.Value != s.PhpBasePort || (int)_phpChildren.Value != s.PhpChildren
                || (int)_mailSmtp.Value != s.MailpitSmtpPort || (int)_mysql.Value != s.MysqlPort;
            var dbEngine = ((Item)_dbEngine.SelectedItem).Value;
            bool mysqlRestart = (int)_mysql.Value != s.MysqlPort || _allowLan.Checked != s.AllowLan || dbEngine != s.DbEngine;
            bool mysqlWasActive = ServiceManager.MySql.IsActive;
            bool pgRestart = (int)_pgPort.Value != s.PostgresPort || _allowLan.Checked != s.AllowLan;
            bool mongoRestart = (int)_mongoPort.Value != s.MongoPort || _allowLan.Checked != s.AllowLan;
            bool memRestart = (int)_memPort.Value != s.MemcachedPort || _allowLan.Checked != s.AllowLan;
            bool redisRestart = (int)_redis.Value != s.RedisPort || _allowLan.Checked != s.AllowLan;
            var mailTool = ((Item)_mailTool.SelectedItem).Value;
            bool mailRestart = (int)_mailWeb.Value != s.MailpitWebPort || (int)_mailSmtp.Value != s.MailpitSmtpPort || mailTool != s.MailTool;
            bool mailWasActive = ServiceManager.Mailpit.IsActive;
            var lang = ((Item)_lang.SelectedItem).Value;
            var theme = ((Item)_theme.SelectedItem).Value;

            s.Language = lang;
            s.Theme = theme;
            s.DocumentRoot = root;
            if (tld != s.Tld) s.Raw.Set("general", "TldChosen", "1");
            s.Tld = tld;
            s.Terminal = ((Item)_terminal.SelectedItem).Value;
            s.AutoVhosts = _autoVhosts.Checked;
            s.Ssl = _ssl.Checked;
            if (s.RunAtStartup != _runAtStartup.Checked || _runAtStartup.Checked) WinUtil.SetRunAtStartup(_runAtStartup.Checked);
            s.RunAtStartup = _runAtStartup.Checked;
            s.StartMinimized = _startMin.Checked;
            s.StartServicesOnLaunch = _startServices.Checked;
            s.CloseToTray = _closeToTray.Checked;
            bool pathChanged = s.AddToPath != _addToPath.Checked;
            s.AddToPath = _addToPath.Checked;
            if (AppInfo.UpdatesSupported) s.CheckUpdates = _checkUpdates.Checked;
            s.HttpPort = (int)_http.Value;
            s.HttpsPort = (int)_https.Value;
            s.MysqlPort = (int)_mysql.Value;
            s.RedisPort = (int)_redis.Value;
            s.MailpitWebPort = (int)_mailWeb.Value;
            s.MailpitSmtpPort = (int)_mailSmtp.Value;
            s.PhpBasePort = (int)_phpPort.Value;
            s.PhpChildren = (int)_phpChildren.Value;
            bool lanChanged = s.AllowLan != _allowLan.Checked;
            bool sqlModeChanged = s.LegacySqlMode != _legacySql.Checked;
            s.LegacySqlMode = _legacySql.Checked;
            s.AllowLan = _allowLan.Checked;
            if (sqlModeChanged) mysqlRestart = true;
            // LAN access needs an inbound firewall rule for the web ports (removed again when switched off)
            if (lanChanged && Ui.Confirm(this, I18n.T(_allowLan.Checked ? "settings.firewallAdd" : "settings.firewallRemove")))
                HealthCheck.SetFirewallRule(_allowLan.Checked);
            if (!mysqlWasActive) s.DbEngine = dbEngine;
            s.MailTool = mailTool;
            s.PostgresPort = (int)_pgPort.Value;
            s.MongoPort = (int)_mongoPort.Value;
            s.MemcachedPort = (int)_memPort.Value;
            s.AutoBackupHours = int.Parse(((Item)_backup.SelectedItem).Value);
            s.AutoBackupKeep = (int)_backupKeep.Value;
            s.Save();

            I18n.Load(lang);
            Theme.Load(theme);
            if (pathChanged) Terminal.ApplyUserPath();
            Log.Info(I18n.T("log.settingsSaved"));

            Task.Run(async () =>
            {
                if (webChanged) await ServiceManager.SwitchWebServerAsync(_nginx.Checked ? "nginx" : "apache");
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
                if (redisRestart && ServiceManager.Redis.IsActive) await ServiceManager.Redis.RestartAsync();
                if (mailRestart && mailWasActive)
                {
                    await ServiceManager.Mailpit.StopAsync();
                    await ServiceManager.Mailpit.StartAsync();
                }
                if (webReload && !webChanged) await ServiceManager.ReloadWebAsync();
                else if (!webChanged) ServiceManager.RefreshSites();
            });
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
