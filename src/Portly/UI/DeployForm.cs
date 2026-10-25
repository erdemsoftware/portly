using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>
    /// Site menu > Server...: SSH profile of one site, connection test, Pull (default) and Push.
    /// Push always shows a summary first and asks for the site name in the same window (inline second step).
    /// </summary>
    public class DeployForm : Form
    {
        readonly Site _site;
        DeployProfile _p;
        TextBox _host, _user, _key, _dir, _dbName, _dbUser, _dbPass, _dbHost, _localDb, _localUrl, _remoteUrl, _excludes, _pub, _confirmName;
        NumericUpDown _port;
        CheckBox _db, _dbAuto, _files, _dbTransfer, _backup;
        RichTextBox _log;
        Panel _pubPanel, _confirmPanel;
        Label _summary;
        ModernButton _pull, _push, _pushNow, _test, _close;
        CancellationTokenSource _cts;
        bool _busy;

        public DeployForm(Site site)
        {
            _site = site;
            _p = Deploy.Load(site.Name);
            if (_p.LocalUrl == "" && Deploy.IsWordPress(site)) _p.LocalUrl = site.PreferredUrl.TrimEnd('/');
            Ui.Apply(this);
            Text = I18n.T("deploy.title", site.Name);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = Ui.S(720, 720);
            MinimumSize = Ui.S(600, 520);

            var scroll = new ScrollPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = Ui.P(22, 14, 22, 8) };
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Color.Transparent };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var warn = Ui.Label(I18n.T("deploy.warning"), Ui.Font, Ui.Orange);
            warn.MaximumSize = new Size(Ui.S(640), 0);
            warn.Margin = Ui.P(2, 0, 0, 6);
            stack.Controls.Add(warn);

            // ---------------- connection ----------------
            var g = Grid();
            _host = Box(_p.Host, 260); Row(g, I18n.T("deploy.host"), _host);
            _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = _p.Port, Width = Ui.S(90) }; Row(g, I18n.T("deploy.port"), _port);
            _user = Box(_p.User, 200); Row(g, I18n.T("deploy.user"), _user);
            var keyRow = Flow();
            _key = Box(_p.KeyFile, 300);
            keyRow.Controls.Add(_key);
            keyRow.Controls.Add(Ui.Button(I18n.T("settings.browse"), (s, e) => BrowseKey(), false, "folder"));
            keyRow.Controls.Add(Ui.Button(I18n.T("deploy.createKey"), (s, e) => CreateKey(), false, "plus"));
            Row(g, I18n.T("deploy.key"), keyRow);
            _pubPanel = new Panel { AutoSize = true, Visible = false, BackColor = Color.Transparent };
            var pubStack = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, BackColor = Color.Transparent };
            var pubHint = Hint(I18n.T("deploy.pubHint"));
            pubStack.Controls.Add(pubHint);
            _pub = new TextBox { ReadOnly = true, Width = Ui.S(440), Font = Ui.Mono, Margin = Ui.P(0, 4, 0, 4), AccessibleName = I18n.T("deploy.publicKey") };
            pubStack.Controls.Add(_pub);
            pubStack.Controls.Add(Ui.Link(I18n.T("deploy.copyKey"), () => { try { Clipboard.SetText(_pub.Text); Log(I18n.T("deploy.copied")); } catch { } }));
            _pubPanel.Controls.Add(pubStack);
            Row(g, "", _pubPanel);
            _dir = Box(_p.RemoteDir, 360); Row(g, I18n.T("deploy.remoteDir"), _dir);
            Row(g, "", Hint(I18n.T("deploy.remoteDirHint")));
            _test = Ui.Button(I18n.T("deploy.test"), async (s, e) => await TestAsync(), false, "refresh");
            Row(g, "", _test);
            Section(stack, I18n.T("deploy.connection"), g);

            // ---------------- database ----------------
            var g2 = Grid();
            _db = Check(I18n.T("deploy.dbOn"), _p.Database, g2);
            _localDb = Box(_p.LocalDb, 200); Row(g2, I18n.T("deploy.localDb"), _localDb);
            _dbAuto = Check(I18n.T("deploy.dbAuto"), _p.DbFromServer, g2);
            _dbName = Box(_p.DbName, 200); Row(g2, I18n.T("deploy.dbName"), _dbName);
            _dbUser = Box(_p.DbUser, 200); Row(g2, I18n.T("deploy.dbUser"), _dbUser);
            _dbPass = Box(_p.DbPassword, 200); _dbPass.UseSystemPasswordChar = true; Row(g2, I18n.T("deploy.dbPass"), _dbPass);
            _dbHost = Box(_p.DbHost, 200); Row(g2, I18n.T("deploy.dbHost"), _dbHost);
            _db.CheckedChanged += (s, e) => UpdateDbFields();
            _dbAuto.CheckedChanged += (s, e) => UpdateDbFields();
            Section(stack, I18n.T("deploy.database"), g2);

            // ---------------- WordPress / excludes ----------------
            var g3 = Grid();
            _localUrl = Box(_p.LocalUrl, 320); Row(g3, I18n.T("deploy.localUrl"), _localUrl);
            _remoteUrl = Box(_p.RemoteUrl, 320); Row(g3, I18n.T("deploy.remoteUrl"), _remoteUrl);
            Row(g3, "", Hint(I18n.T("deploy.urlHint")));
            _excludes = new TextBox { Text = string.Join(", ", _p.Excludes), Width = Ui.S(420), Height = Ui.S(48), Multiline = true, AccessibleName = I18n.T("deploy.excludes") };
            Row(g3, I18n.T("deploy.excludes"), _excludes);
            Row(g3, "", Hint(I18n.T("deploy.excludesHint")));
            Section(stack, I18n.T("deploy.transfer"), g3);

            // ---------------- actions ----------------
            var g4 = Grid();
            _files = Check(I18n.T("deploy.files"), true, g4);
            _dbTransfer = Check(I18n.T("deploy.dbTransfer"), _p.Database, g4);
            _backup = Check(I18n.T("deploy.remoteBackup"), true, g4);
            var actions = Flow();
            _pull = Ui.Button(I18n.T("deploy.pull"), async (s, e) => await PullAsync(), true, "import");
            _push = Ui.Button(I18n.T("deploy.push"), (s, e) => ShowPushConfirm(), false, "update");
            actions.Controls.Add(_pull);
            actions.Controls.Add(_push);
            actions.Controls.Add(Ui.Button(I18n.T("common.save"), (s, e) => { if (Collect(true)) Log(I18n.T("deploy.saved")); }));
            Row(g4, "", actions);
            Row(g4, "", Hint(I18n.T("deploy.pullHint")));

            // inline second step of Push: summary + type the site name
            _confirmPanel = new Panel { AutoSize = true, Visible = false, BackColor = Color.Transparent };
            var cs = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, BackColor = Color.Transparent };
            _summary = Ui.Label("", Ui.Font, Ui.Red);
            _summary.MaximumSize = new Size(Ui.S(560), 0);
            cs.Controls.Add(_summary);
            var confirmRow = Flow();
            _confirmName = Box("", 200);
            _confirmName.AccessibleName = I18n.T("deploy.typeName", _site.Name);
            _confirmName.TextChanged += (s, e) => _pushNow.Enabled = _confirmName.Text.Trim() == _site.Name;
            _pushNow = Ui.Button(I18n.T("deploy.pushNow"), async (s, e) => await PushAsync(), false, null);
            _pushNow.Kind = ButtonKind.Danger;
            _pushNow.Enabled = false;
            confirmRow.Controls.Add(_confirmName);
            confirmRow.Controls.Add(_pushNow);
            confirmRow.Controls.Add(Ui.Button(I18n.T("common.cancel"), (s, e) => { _confirmPanel.Visible = false; _confirmName.Text = ""; }));
            cs.Controls.Add(confirmRow);
            _confirmPanel.Controls.Add(cs);
            Row(g4, "", _confirmPanel);
            Section(stack, I18n.T("deploy.actions"), g4);

            // ---------------- log ----------------
            var p = Theme.Current;
            _log = new RichTextBox { Height = Ui.S(170), Dock = DockStyle.Top, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = p.ConsoleBg, ForeColor = p.ConsoleText, Font = Ui.Mono, Tag = "console", DetectUrls = false, Margin = Ui.P(0, 12, 0, 0), AccessibleName = I18n.T("nav.log") };
            stack.Controls.Add(_log);
            scroll.Controls.Add(stack);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = Ui.P(22, 10, 22, 12), BackColor = Ui.Bg };
            _close = Ui.Button(I18n.T("common.close"), (s, e) => { if (_busy) { if (_cts != null) _cts.Cancel(); } else Close(); });
            _close.Margin = Padding.Empty;
            buttons.Controls.Add(_close);
            Controls.Add(scroll);
            Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Line });
            Controls.Add(buttons);
            scroll.BringToFront();
            AcceptButton = null;
            UpdateDbFields();
            // Pull is the highlighted (primary) action; focus stays on the first field so the window opens at the top
            Shown += (s, e) => _host.Focus();
            if (_p.KeyFile != "" && Deploy.PublicKey(_p.KeyFile) != "") ShowPublicKey(Deploy.PublicKey(_p.KeyFile));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy) { e.Cancel = true; if (_cts != null) _cts.Cancel(); return; }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (_confirmPanel.Visible) { _confirmPanel.Visible = false; return true; }
                if (_busy) { if (_cts != null) _cts.Cancel(); return true; }
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void UpdateDbFields()
        {
            bool on = _db.Checked;
            _localDb.Enabled = _dbAuto.Enabled = _dbTransfer.Enabled = on;
            bool manual = on && !_dbAuto.Checked;
            _dbName.Enabled = _dbUser.Enabled = _dbPass.Enabled = _dbHost.Enabled = manual;
            if (!on) _dbTransfer.Checked = false;
        }

        /// <summary>Copy the fields into the profile; with save the profile is validated and written to usr\deploy.ini.</summary>
        bool Collect(bool save)
        {
            var p = new DeployProfile
            {
                Site = _site.Name,
                Host = _host.Text.Trim(),
                Port = (int)_port.Value,
                User = _user.Text.Trim(),
                KeyFile = _key.Text.Trim(),
                RemoteDir = _dir.Text.Trim(),
                Database = _db.Checked,
                DbFromServer = _dbAuto.Checked,
                DbName = _dbName.Text.Trim(),
                DbUser = _dbUser.Text.Trim(),
                DbPassword = _dbPass.Text,
                DbHost = _dbHost.Text.Trim(),
                LocalDb = _localDb.Text.Trim(),
                LocalUrl = _localUrl.Text.Trim(),
                RemoteUrl = _remoteUrl.Text.Trim(),
                Excludes = Deploy.SplitExcludes(_excludes.Text),
            };
            var err = Deploy.Validate(p);
            if (err != null) { Ui.Error(this, err); return false; }
            _p = p;
            if (save) Deploy.Save(p);
            return true;
        }

        void BrowseKey()
        {
            using (var d = new OpenFileDialog { InitialDirectory = Directory.Exists(Deploy.SshDir) ? Deploy.SshDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
                if (d.ShowDialog(this) == DialogResult.OK) _key.Text = d.FileName;
        }

        void CreateKey()
        {
            try
            {
                var pub = Deploy.CreateKey();
                _key.Text = Deploy.DefaultKey;
                ShowPublicKey(pub);
                Log(I18n.T("deploy.keyCreated", Deploy.DefaultKey));
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        void ShowPublicKey(string pub)
        {
            _pub.Text = pub;
            _pubPanel.Visible = true;
        }

        void Log(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)(() => Log(line))); return; }
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\n");
            _log.ScrollToCaret();
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            foreach (var b in new Control[] { _pull, _push, _test, _pushNow }) b.Enabled = !busy;
            if (!busy) _pushNow.Enabled = _confirmName.Text.Trim() == _site.Name;
            _close.Text = busy ? I18n.T("common.cancel") : I18n.T("common.close");
            UseWaitCursor = busy;
        }

        async Task Run(Func<CancellationToken, Task> work)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try { await work(_cts.Token); }
            catch (OperationCanceledException) { Log(I18n.T("deploy.cancelled")); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); Ui.Error(this, ex.Message); }
            finally { SetBusy(false); _cts.Dispose(); _cts = null; }
        }

        async Task TestAsync()
        {
            if (!Collect(true)) return;
            await Run(async ct =>
            {
                Log(I18n.T("deploy.connecting", _p.Target, _p.Port));
                var r = await Deploy.TestAsync(_p, ct);
                if (!r.Ok) { Log("ERROR: " + r.Output); Ui.Error(this, I18n.T("deploy.errConnect", r.Output)); return; }
                if (!string.IsNullOrEmpty(r.NewHostKey)) Log(I18n.T("deploy.hostKey", r.NewHostKey));
                Log(I18n.T("deploy.connected"));
                Log((r.Dir ? "✓ " : "✗ ") + I18n.T("deploy.probeDir", _p.RemoteDir));
                Log((r.Tar ? "✓ " : "✗ ") + "tar");
                if (_p.Database) Log((r.MySqlDump ? "✓ " : "✗ ") + "mysqldump");
                if (r.WordPress) Log((r.WpCli ? "✓ " : "– ") + I18n.T("deploy.probeWp"));
                var msg = I18n.T("deploy.testOk");
                if (!string.IsNullOrEmpty(r.NewHostKey)) msg += "\n\n" + I18n.T("deploy.hostKey", r.NewHostKey);
                Ui.Info(this, msg);
            });
        }

        async Task PullAsync()
        {
            if (!Collect(true)) return;
            bool files = _files.Checked, db = _dbTransfer.Checked && _p.Database;
            if (!files && !db) return;
            if (db && ServiceManager.MySql.State != ServiceState.Running) { Ui.Error(this, I18n.T("err.mysqlNotRunning")); return; }
            if (!Ui.Confirm(this, I18n.T("deploy.pullConfirm", _p.Target + ":" + _p.RemoteDir, _site.Folder, files ? "✓" : "✗", db ? _p.LocalDb : "✗", Deploy.BackupDir))) return;
            await Run(ct => Deploy.PullAsync(_site, _p, files, db, Log, ct));
        }

        async void ShowPushConfirm()
        {
            if (!Collect(true)) return;
            bool files = _files.Checked, db = _dbTransfer.Checked && _p.Database;
            if (!files && !db) return;
            if (db && ServiceManager.MySql.State != ServiceState.Running) { Ui.Error(this, I18n.T("err.mysqlNotRunning")); return; }
            SetBusy(true);
            DeploySummary s;
            try { s = await Task.Run(() => Deploy.Summarize(_site, _p)); }
            finally { SetBusy(false); }
            var text = I18n.T("deploy.pushSummary", _p.Target, _p.RemoteDir,
                files ? I18n.T("deploy.sumFiles", s.Files, Ui.FormatBytes(s.Bytes)) : "✗",
                db ? (s.Database ? _p.LocalDb : I18n.T("deploy.sumNoDb", _p.LocalDb)) : "✗",
                _backup.Checked ? I18n.T("deploy.sumBackup", "~/" + Deploy.BackupFolder) : I18n.T("deploy.sumNoBackup"));
            _summary.Text = text + "\n\n" + I18n.T("deploy.typeName", _site.Name);
            _confirmName.Text = "";
            _confirmPanel.Visible = true;
            _confirmName.Focus();
        }

        async Task PushAsync()
        {
            if (_confirmName.Text.Trim() != _site.Name || !Collect(false)) return;
            bool files = _files.Checked, db = _dbTransfer.Checked && _p.Database, backup = _backup.Checked;
            _confirmPanel.Visible = false;
            _confirmName.Text = "";
            await Run(ct => Deploy.PushAsync(_site, _p, files, db, backup, Log, ct));
        }

        // ---------------- layout helpers (same look as Settings) ----------------
        static TextBox Box(string text, int width) { return new TextBox { Text = text ?? "", Width = Ui.S(width) }; }

        static FlowLayoutPanel Flow() { return new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Color.Transparent }; }

        static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        static void Row(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++;
            var l = Ui.Label(label);
            l.Margin = Ui.P(0, 8, 16, 4);
            c.Margin = Ui.P(0, 4, 0, 4);
            if (label != "" && c.AccessibleName == null) c.AccessibleName = label;
            t.Controls.Add(l, 0, r);
            t.Controls.Add(c, 1, r);
        }

        static CheckBox Check(string text, bool value, TableLayoutPanel t)
        {
            var c = new CheckBox { Text = text, Checked = value, AutoSize = true, MaximumSize = new Size(Ui.S(600), 0), BackColor = Color.Transparent, Margin = Ui.P(0, 5, 0, 5) };
            int r = t.RowCount++;
            t.Controls.Add(c, 0, r);
            t.SetColumnSpan(c, 2);
            return c;
        }

        static Label Hint(string text)
        {
            var l = Ui.Label(text, Ui.Small, Ui.Muted);
            l.MaximumSize = new Size(Ui.S(460), 0);
            return l;
        }

        static void Section(TableLayoutPanel stack, string title, TableLayoutPanel grid)
        {
            var h = Ui.Label(Ui.Upper(title), Ui.Small, Ui.Muted);
            h.Margin = Ui.P(2, 14, 0, 6);
            stack.Controls.Add(h);
            var card = new Card { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Ui.P(16, 12, 16, 12), Dock = DockStyle.Fill, Margin = Padding.Empty, Radius = 12, AccessibleName = title };
            grid.Dock = DockStyle.Top;
            card.Controls.Add(grid);
            stack.Controls.Add(card);
        }
    }
}
