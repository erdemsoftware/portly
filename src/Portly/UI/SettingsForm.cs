using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Core;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    public class SettingsForm : Form
    {
        ComboBox _lang, _terminal;
        TextBox _docRoot, _tld;
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
            ClientSize = Ui.S(560, 500);
            var s = Settings.Current;

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(Ui.S(12), Ui.S(4)) };

            // ---------------- general ----------------
            var general = Page(I18n.T("settings.general"));
            var g = Grid();
            _lang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(260) };
            _lang.Items.Add(new Item(I18n.T("settings.langAuto"), "auto"));
            foreach (var li in I18n.Available()) _lang.Items.Add(new Item(li.ToString(), li.Code));
            _lang.SelectedItem = _lang.Items.Cast<Item>().FirstOrDefault(i => i.Value == s.Language) ?? _lang.Items[0];
            AddRow(g, I18n.T("settings.language"), _lang);
            var langHelp = Ui.Link(I18n.T("settings.translate"), () => WinUtil.Open(AppInfo.TranslateUrl));
            AddRow(g, "", langHelp);

            var rootPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            _docRoot = new TextBox { Text = s.DocumentRoot, Width = Ui.S(270) };
            rootPanel.Controls.Add(_docRoot);
            rootPanel.Controls.Add(Ui.Button(I18n.T("settings.browse"), (o, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = _docRoot.Text, ShowNewFolderButton = true })
                    if (d.ShowDialog(this) == DialogResult.OK) _docRoot.Text = d.SelectedPath;
            }));
            AddRow(g, I18n.T("settings.docRoot"), rootPanel);
            _tld = new TextBox { Text = s.Tld, Width = Ui.S(100) };
            AddRow(g, I18n.T("settings.tld"), _tld);
            AddRow(g, "", Hint(I18n.T("settings.tldHint")));
            _terminal = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(160) };
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
            general.Controls.Add(g);

            // ---------------- services ----------------
            var services = Page(I18n.T("settings.services"));
            var g2 = Grid();
            var web = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            _apache = new RadioButton { Text = "Apache", AutoSize = true, Checked = s.WebServer == "apache" };
            _nginx = new RadioButton { Text = "Nginx", AutoSize = true, Checked = s.WebServer == "nginx" };
            web.Controls.Add(_apache);
            web.Controls.Add(_nginx);
            AddRow(g2, I18n.T("settings.webServer"), web);
            _http = Port(s.HttpPort); AddRow(g2, I18n.T("settings.httpPort"), _http);
            _https = Port(s.HttpsPort); AddRow(g2, I18n.T("settings.httpsPort"), _https);
            _mysql = Port(s.MysqlPort); AddRow(g2, I18n.T("settings.mysqlPort"), _mysql);
            _redis = Port(s.RedisPort); AddRow(g2, I18n.T("settings.redisPort"), _redis);
            _mailWeb = Port(s.MailpitWebPort); AddRow(g2, I18n.T("settings.mailWebPort"), _mailWeb);
            _mailSmtp = Port(s.MailpitSmtpPort); AddRow(g2, I18n.T("settings.mailSmtpPort"), _mailSmtp);
            _phpPort = Port(s.PhpBasePort); AddRow(g2, I18n.T("settings.phpPort"), _phpPort);
            _phpChildren = new NumericUpDown { Minimum = 1, Maximum = 64, Value = Math.Max(1, Math.Min(64, s.PhpChildren)), Width = Ui.S(80) };
            AddRow(g2, I18n.T("settings.phpChildren"), _phpChildren);
            _allowLan = Check(I18n.T("settings.allowLan"), s.AllowLan, g2);
            AddRow(g2, "", Hint(I18n.T("settings.allowLanHint")));
            services.Controls.Add(g2);

            tabs.TabPages.Add(general);
            tabs.TabPages.Add(services);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = Ui.P(10), BackColor = Ui.Bg };
            var cancel = Ui.Button(I18n.T("common.cancel"), null);
            cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.Button(I18n.T("common.save"), (o, e) => Save(), true);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            Controls.Add(tabs);
            Controls.Add(buttons);
            CancelButton = cancel;
        }

        static TabPage Page(string title)
        {
            return new TabPage(title) { BackColor = Ui.Card, Padding = Ui.P(12), AutoScroll = true };
        }

        static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        static void AddRow(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++;
            var l = Ui.Label(label);
            l.Margin = Ui.P(0, 6, 12, 4);
            c.Margin = Ui.P(0, 3, 0, 3);
            t.Controls.Add(l, 0, r);
            t.Controls.Add(c, 1, r);
        }

        static CheckBox Check(string text, bool value, TableLayoutPanel t)
        {
            var c = new CheckBox { Text = text, Checked = value, AutoSize = true, MaximumSize = new Size(Ui.S(500), 0) };
            int r = t.RowCount++;
            c.Margin = Ui.P(0, 3, 0, 3);
            t.Controls.Add(c, 0, r);
            t.SetColumnSpan(c, 2);
            return c;
        }

        static Label Hint(string text)
        {
            var l = Ui.Label(text, Ui.Small, Ui.Muted);
            l.MaximumSize = new Size(Ui.S(360), 0);
            return l;
        }

        static NumericUpDown Port(int value)
        {
            return new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Max(1, Math.Min(65535, value)), Width = Ui.S(80) };
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
            var ports = new[] { _http.Value, _https.Value, _mysql.Value, _redis.Value, _mailWeb.Value, _mailSmtp.Value };
            if (ports.Distinct().Count() != ports.Length) { Ui.Error(this, I18n.T("settings.duplicatePorts")); return; }

            bool webChanged = (_nginx.Checked ? "nginx" : "apache") != s.WebServer;
            bool webReload = root != s.DocumentRoot || tld != s.Tld || _autoVhosts.Checked != s.AutoVhosts || _ssl.Checked != s.Ssl
                || (int)_http.Value != s.HttpPort || (int)_https.Value != s.HttpsPort || _allowLan.Checked != s.AllowLan
                || (int)_phpPort.Value != s.PhpBasePort || (int)_phpChildren.Value != s.PhpChildren
                || (int)_mailSmtp.Value != s.MailpitSmtpPort || (int)_mysql.Value != s.MysqlPort;
            bool mysqlRestart = (int)_mysql.Value != s.MysqlPort || _allowLan.Checked != s.AllowLan;
            bool redisRestart = (int)_redis.Value != s.RedisPort || _allowLan.Checked != s.AllowLan;
            bool mailRestart = (int)_mailWeb.Value != s.MailpitWebPort || (int)_mailSmtp.Value != s.MailpitSmtpPort;
            var lang = ((Item)_lang.SelectedItem).Value;

            s.Language = lang;
            s.DocumentRoot = root;
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
            s.AllowLan = _allowLan.Checked;
            s.Save();

            I18n.Load(lang);
            if (pathChanged) Terminal.ApplyUserPath();
            Log.Info(I18n.T("log.settingsSaved"));

            Task.Run(async () =>
            {
                if (webChanged) await ServiceManager.SwitchWebServerAsync(_nginx.Checked ? "nginx" : "apache");
                if (mysqlRestart && ServiceManager.MySql.IsActive) await ServiceManager.MySql.RestartAsync();
                if (redisRestart && ServiceManager.Redis.IsActive) await ServiceManager.Redis.RestartAsync();
                if (mailRestart && ServiceManager.Mailpit.IsActive) await ServiceManager.Mailpit.RestartAsync();
                if (webReload && !webChanged) await ServiceManager.ReloadWebAsync();
                else if (!webChanged) ServiceManager.RefreshSites();
            });
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
