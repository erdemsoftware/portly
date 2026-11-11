using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>
    /// Site menu > Server...: SSH profile of one site, connection test, Pull (default) and Push.
    /// Push always shows a summary first and asks for the site name in the same window (inline second step).
    /// </summary>
    public class DeployForm : PortlyDialog
    {
        readonly Site _site;
        DeployProfile _p;
        TextBox _host, _user, _key, _dir, _dbName, _dbUser, _dbPass, _dbHost, _localDb, _localUrl, _remoteUrl, _excludes, _pub, _confirmName;
        NumericUpDown _port;
        CheckBox _db, _dbAuto, _files, _dbTransfer, _backup;
        SelectableTextBlock _log;
        ScrollViewer _logScroll;
        StackPanel _pubPanel, _confirmPanel;
        TextBlock _summary;
        Button _pull, _push, _pushNow, _test, _close;
        CancellationTokenSource _cts;
        bool _busy;

        public DeployForm(Site site) : this(site, null) { }

        DeployForm(Site site, DeployProfile profile) : base(I18n.T("deploy.title", site.Name), 720)
        {
            _site = site;
            _p = profile ?? Deploy.Load(site.Name);
            if (profile == null && _p.LocalUrl == "" && Deploy.IsWordPress(site)) _p.LocalUrl = site.PreferredUrl.TrimEnd('/');
            MakeResizable(720);
            MinWidth = 600;
            MinHeight = 520;
            Body.Spacing = 4;
            Grid.SetIsSharedSizeScope(Body, true);

            Body.Children.Add(Ui.Label(I18n.T("deploy.warning"), null, Ui.Orange));

            // ---------------- connection ----------------
            var g = NewGrid();
            _host = Box(_p.Host, 260); Row(g, I18n.T("deploy.host"), _host);
            _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = _p.Port, Width = 120, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Left };
            Row(g, I18n.T("deploy.port"), _port);
            _user = Box(_p.User, 200); Row(g, I18n.T("deploy.user"), _user);
            _key = Box(_p.KeyFile, 360);
            Row(g, I18n.T("deploy.key"), Ui.Column(6, _key, Ui.Row(Ui.Button(I18n.T("settings.browse"), BrowseKey, null, "folder"), Ui.Button(I18n.T("deploy.createKey"), CreateKey, null, "plus"))), true);
            _pub = new TextBox { IsReadOnly = true, Width = 440, HorizontalAlignment = HorizontalAlignment.Left };
            _pub.Classes.Add("mono");
            Avalonia.Automation.AutomationProperties.SetName(_pub, I18n.T("deploy.publicKey"));
            _pubPanel = Ui.Column(4, Hint(I18n.T("deploy.pubHint")), _pub, Ui.Link(I18n.T("deploy.copyKey"), async () => { try { await Ui.Copy(this, _pub.Text); Log(I18n.T("deploy.copied")); } catch { } }));
            _pubPanel.IsVisible = false;
            Row(g, "", _pubPanel);
            _dir = Box(_p.RemoteDir, 360); Row(g, I18n.T("deploy.remoteDir"), _dir);
            Row(g, "", Hint(I18n.T("deploy.remoteDirHint")));
            _test = Ui.Button(I18n.T("deploy.test"), TestAsync, null, "refresh");
            _test.HorizontalAlignment = HorizontalAlignment.Left;
            Row(g, "", _test);
            Section(I18n.T("deploy.connection"), g);

            // ---------------- database ----------------
            var g2 = NewGrid();
            _db = Check(I18n.T("deploy.dbOn"), _p.Database, g2);
            _localDb = Box(_p.LocalDb, 200); Row(g2, I18n.T("deploy.localDb"), _localDb);
            _dbAuto = Check(I18n.T("deploy.dbAuto"), _p.DbFromServer, g2);
            _dbName = Box(_p.DbName, 200); Row(g2, I18n.T("deploy.dbName"), _dbName);
            _dbUser = Box(_p.DbUser, 200); Row(g2, I18n.T("deploy.dbUser"), _dbUser);
            _dbPass = Box(_p.DbPassword, 200); _dbPass.PasswordChar = '•'; Row(g2, I18n.T("deploy.dbPass"), _dbPass);
            _dbHost = Box(_p.DbHost, 200); Row(g2, I18n.T("deploy.dbHost"), _dbHost);
            _db.IsCheckedChanged += (s, e) => UpdateDbFields();
            _dbAuto.IsCheckedChanged += (s, e) => UpdateDbFields();
            Section(I18n.T("deploy.database"), g2);

            // ---------------- WordPress / excludes ----------------
            var g3 = NewGrid();
            _localUrl = Box(_p.LocalUrl, 320); Row(g3, I18n.T("deploy.localUrl"), _localUrl);
            _remoteUrl = Box(_p.RemoteUrl, 320); Row(g3, I18n.T("deploy.remoteUrl"), _remoteUrl);
            Row(g3, "", Hint(I18n.T("deploy.urlHint")));
            _excludes = new TextBox { Text = string.Join(", ", _p.Excludes), Width = 420, Height = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left };
            Avalonia.Automation.AutomationProperties.SetName(_excludes, I18n.T("deploy.excludes"));
            Row(g3, I18n.T("deploy.excludes"), _excludes, true);
            Row(g3, "", Hint(I18n.T("deploy.excludesHint")));
            Section(I18n.T("deploy.transfer"), g3);

            // ---------------- actions ----------------
            var g4 = NewGrid();
            _files = Check(I18n.T("deploy.files"), true, g4);
            _dbTransfer = Check(I18n.T("deploy.dbTransfer"), _p.Database, g4);
            _backup = Check(I18n.T("deploy.remoteBackup"), true, g4);
            _pull = Ui.Button(I18n.T("deploy.pull"), PullAsync, "primary", "import");
            _push = Ui.Button(I18n.T("deploy.push"), ShowPushConfirm, null, "update");
            var actions = Ui.Row(_pull, _push, Ui.Button(I18n.T("common.save"), async () => { if (await Collect(true)) Log(I18n.T("deploy.saved")); }));
            Row(g4, "", actions);
            Row(g4, "", Hint(I18n.T("deploy.pullHint")));

            // inline second step of Push: summary + type the site name
            _summary = Ui.Label("", null, Ui.Red);
            _confirmName = Box("", 200);
            Avalonia.Automation.AutomationProperties.SetName(_confirmName, I18n.T("deploy.typeName", _site.Name));
            _confirmName.TextChanged += (s, e) => { if (_pushNow != null) _pushNow.IsEnabled = !_busy && (_confirmName.Text ?? "").Trim() == _site.Name; };
            _pushNow = Ui.Button(I18n.T("deploy.pushNow"), PushAsync, "danger");
            _pushNow.IsEnabled = false;
            var cancelPush = Ui.Button(I18n.T("common.cancel"), () => { _confirmPanel.IsVisible = false; _confirmName.Text = ""; });
            _confirmPanel = Ui.Column(6, _summary, Ui.Row(_confirmName, _pushNow, cancelPush));
            _confirmPanel.IsVisible = false;
            Row(g4, "", _confirmPanel);
            Section(I18n.T("deploy.actions"), g4);

            // ---------------- log ----------------
            _log = Ui.Selectable("", "mono");
            _log.Foreground = Ui.Brush("ConsoleText");
            _log.FontSize = 12;
            Avalonia.Automation.AutomationProperties.SetName(_log, I18n.T("nav.log"));
            _logScroll = new ScrollViewer { Content = _log, Height = 150, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var console = new Border { Child = _logScroll, Margin = new Thickness(0, 12, 0, 0) };
            console.Classes.Add("console");
            Body.Children.Add(console);

            _close = Ui.Button(I18n.T("common.close"), () => { if (_busy) { if (_cts != null) _cts.Cancel(); } else Close(); });
            Buttons.Children.Add(_close);
            UpdateDbFields();

            // Esc: close the Push confirmation first, cancel a running job, otherwise close the window
            AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (_confirmPanel.IsVisible) _confirmPanel.IsVisible = false;
                else if (_busy) { if (_cts != null) _cts.Cancel(); }
                else Close();
            }, RoutingStrategies.Tunnel);
            Closing += (s, e) => { if (_busy) { e.Cancel = true; if (_cts != null) _cts.Cancel(); } };
            // Pull is the highlighted (primary) action; focus stays on the first field so the window opens at the top
            Opened += (s, e) => _host.Focus();
            if (profile == null && _p.KeyFile != "" && Deploy.PublicKey(_p.KeyFile) != "") ShowPublicKey(Deploy.PublicKey(_p.KeyFile));
        }

        void UpdateDbFields()
        {
            bool on = _db.IsChecked == true;
            _localDb.IsEnabled = _dbAuto.IsEnabled = _dbTransfer.IsEnabled = on;
            bool manual = on && _dbAuto.IsChecked != true;
            _dbName.IsEnabled = _dbUser.IsEnabled = _dbPass.IsEnabled = _dbHost.IsEnabled = manual;
            if (!on) _dbTransfer.IsChecked = false;
        }

        static string T(TextBox b) { return b.Text ?? ""; }

        /// <summary>Copy the fields into the profile; with save the profile is validated and written to usr\deploy.ini.</summary>
        async Task<bool> Collect(bool save)
        {
            var p = new DeployProfile
            {
                Site = _site.Name,
                Host = T(_host).Trim(),
                Port = (int)(_port.Value ?? 22),
                User = T(_user).Trim(),
                KeyFile = T(_key).Trim(),
                RemoteDir = T(_dir).Trim(),
                Database = _db.IsChecked == true,
                DbFromServer = _dbAuto.IsChecked == true,
                DbName = T(_dbName).Trim(),
                DbUser = T(_dbUser).Trim(),
                DbPassword = T(_dbPass),
                DbHost = T(_dbHost).Trim(),
                LocalDb = T(_localDb).Trim(),
                LocalUrl = T(_localUrl).Trim(),
                RemoteUrl = T(_remoteUrl).Trim(),
                Excludes = Deploy.SplitExcludes(T(_excludes)),
            };
            var err = Deploy.Validate(p);
            if (err != null) { await Dialogs.Warning(this, err); return false; }
            _p = p;
            if (save) Deploy.Save(p);
            return true;
        }

        async Task BrowseKey()
        {
            var sp = GetTopLevel(this).StorageProvider;
            var start = await sp.TryGetFolderFromPathAsync(Directory.Exists(Deploy.SshDir) ? Deploy.SshDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { SuggestedStartLocation = start, AllowMultiple = false });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path != null) _key.Text = path;
        }

        async Task CreateKey()
        {
            try
            {
                var pub = Deploy.CreateKey();
                _key.Text = Deploy.DefaultKey;
                ShowPublicKey(pub);
                Log(I18n.T("deploy.keyCreated", Deploy.DefaultKey));
            }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
        }

        void ShowPublicKey(string pub)
        {
            _pub.Text = pub;
            _pubPanel.IsVisible = true;
        }

        void Log(string line)
        {
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Log(line)); return; }
            _log.Text += DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\n";
            Dispatcher.UIThread.Post(() => _logScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            foreach (var b in new[] { _pull, _push, _test, _pushNow }) b.IsEnabled = !busy;
            if (!busy) _pushNow.IsEnabled = (_confirmName.Text ?? "").Trim() == _site.Name;
            _close.Content = busy ? I18n.T("common.cancel") : I18n.T("common.close");
            Cursor = busy ? new Cursor(StandardCursorType.Wait) : null;
        }

        async Task Run(Func<CancellationToken, Task> work)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try { await work(_cts.Token); }
            catch (OperationCanceledException) { Log(I18n.T("deploy.cancelled")); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); await Dialogs.Warning(this, ex.Message); }
            finally { SetBusy(false); _cts.Dispose(); _cts = null; }
        }

        async Task TestAsync()
        {
            if (!await Collect(true)) return;
            await Run(async ct =>
            {
                Log(I18n.T("deploy.connecting", _p.Target, _p.Port));
                var r = await Deploy.TestAsync(_p, ct);
                if (!r.Ok) { Log("ERROR: " + r.Output); await Dialogs.Warning(this, I18n.T("deploy.errConnect", r.Output)); return; }
                if (!string.IsNullOrEmpty(r.NewHostKey)) Log(I18n.T("deploy.hostKey", r.NewHostKey));
                Log(I18n.T("deploy.connected"));
                Log((r.Dir ? "✓ " : "✗ ") + I18n.T("deploy.probeDir", _p.RemoteDir));
                Log((r.Tar ? "✓ " : "✗ ") + "tar");
                if (_p.Database) Log((r.MySqlDump ? "✓ " : "✗ ") + "mysqldump");
                if (r.WordPress) Log((r.WpCli ? "✓ " : "– ") + I18n.T("deploy.probeWp"));
                var msg = I18n.T("deploy.testOk");
                if (!string.IsNullOrEmpty(r.NewHostKey)) msg += "\n\n" + I18n.T("deploy.hostKey", r.NewHostKey);
                await Dialogs.Info(this, msg);
            });
        }

        async Task PullAsync()
        {
            if (!await Collect(true)) return;
            bool files = _files.IsChecked == true, db = _dbTransfer.IsChecked == true && _p.Database;
            if (!files && !db) return;
            if (db && ServiceManager.MySql.State != ServiceState.Running) { await Dialogs.Warning(this, I18n.T("err.mysqlNotRunning")); return; }
            if (!await Dialogs.Confirm(this, I18n.T("deploy.pullConfirm", _p.Target + ":" + _p.RemoteDir, _site.Folder, files ? "✓" : "✗", db ? _p.LocalDb : "✗", Deploy.BackupDir))) return;
            await Run(ct => Deploy.PullAsync(_site, _p, files, db, Log, ct));
        }

        async Task ShowPushConfirm()
        {
            if (!await Collect(true)) return;
            bool files = _files.IsChecked == true, db = _dbTransfer.IsChecked == true && _p.Database;
            if (!files && !db) return;
            if (db && ServiceManager.MySql.State != ServiceState.Running) { await Dialogs.Warning(this, I18n.T("err.mysqlNotRunning")); return; }
            SetBusy(true);
            DeploySummary s;
            try { s = await Task.Run(() => Deploy.Summarize(_site, _p)); }
            finally { SetBusy(false); }
            var text = I18n.T("deploy.pushSummary", _p.Target, _p.RemoteDir,
                files ? I18n.T("deploy.sumFiles", s.Files, Ui.FormatBytes(s.Bytes)) : "✗",
                db ? (s.Database ? _p.LocalDb : I18n.T("deploy.sumNoDb", _p.LocalDb)) : "✗",
                _backup.IsChecked == true ? I18n.T("deploy.sumBackup", "~/" + Deploy.BackupFolder) : I18n.T("deploy.sumNoBackup"));
            _summary.Text = text + "\n\n" + I18n.T("deploy.typeName", _site.Name);
            _confirmName.Text = "";
            _confirmPanel.IsVisible = true;
            _confirmName.Focus();
        }

        async Task PushAsync()
        {
            if ((_confirmName.Text ?? "").Trim() != _site.Name || !await Collect(false)) return;
            bool files = _files.IsChecked == true, db = _dbTransfer.IsChecked == true && _p.Database, backup = _backup.IsChecked == true;
            _confirmPanel.IsVisible = false;
            _confirmName.Text = "";
            await Run(ct => Deploy.PushAsync(_site, _p, files, db, backup, Log, ct));
        }

        // ---------------- layout helpers (same look as Settings) ----------------
        static TextBox Box(string text, double width)
        {
            return new TextBox { Text = text ?? "", Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        }

        static Grid NewGrid()
        {
            var g = new Grid { ColumnSpacing = 16 };
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "L" });
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            return g;
        }

        static void Row(Grid g, string label, Control c, bool top = false)
        {
            int r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = Ui.Label(label);
            l.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center;
            l.Margin = new Thickness(0, top ? 12 : 0, 0, 0);
            c.Margin = new Thickness(0, 4);
            Grid.SetRow(l, r);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            g.Children.Add(l);
            g.Children.Add(c);
        }

        static CheckBox Check(string text, bool value, Grid g)
        {
            var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 3) };
            int r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(c, r);
            Grid.SetColumnSpan(c, 2);
            g.Children.Add(c);
            return c;
        }

        static TextBlock Hint(string text)
        {
            var l = Ui.Label(text, "muted small");
            l.MaxWidth = 460;
            l.HorizontalAlignment = HorizontalAlignment.Left;
            return l;
        }

        void Section(string title, Grid grid)
        {
            var h = Ui.Section(title);
            h.Margin = new Thickness(2, 14, 0, 4);
            Body.Children.Add(h);
            var card = Ui.Card(grid, new Thickness(16, 10));
            Avalonia.Automation.AutomationProperties.SetName(card, title);
            Body.Children.Add(card);
        }

        public static Window Preview()
        {
            var site = new Site { Name = "shop", Host = "shop.portly", Folder = @"E:\projeler\shop", Valid = true };
            var p = new DeployProfile
            {
                Site = "shop", Host = "server.example.com", Port = 22, User = "deploy", KeyFile = @"C:\Portly\usr\ssh\id_ed25519_portly",
                RemoteDir = "/var/www/shop/public_html", LocalDb = "shop", LocalUrl = "https://shop.portly", RemoteUrl = "https://shop.example.com",
            };
            var f = new DeployForm(site, p);
            f.MaxHeight = 1700;
            f.Height = 1650;
            f.ShowPublicKey("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIPortlySampleKeyOnlyForThePreviewWindow portly@shop");
            f.Log(I18n.T("deploy.connecting", p.Target, p.Port));
            f.Log(I18n.T("deploy.connected"));
            f.Log("✓ " + I18n.T("deploy.probeDir", p.RemoteDir));
            f.Log("✓ tar");
            return f;
        }
    }
}
