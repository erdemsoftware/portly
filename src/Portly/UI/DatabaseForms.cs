using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>
    /// Runs a long database job with a status line, then shows its result as copyable text.
    /// Without a job it only shows the text (connection details).
    /// </summary>
    public class DbTaskForm : Form
    {
        readonly Label _status;
        readonly ProgressBar _bar;
        readonly TextBox _result;
        readonly ModernButton _close, _cancel;
        readonly FlowLayoutPanel _buttons;
        readonly Func<Action<string>, CancellationToken, Task<string>> _work;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        bool _busy;
        public bool Failed { get; private set; }
        /// <summary>Decides after the job whether the extra buttons appear.</summary>
        public Func<bool> ShowExtra;
        public string ResultText { get; private set; }

        public DbTaskForm(string title, Func<Action<string>, CancellationToken, Task<string>> work, string text = null)
        {
            _work = work;
            Ui.Apply(this);
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(20);
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            t.Controls.Add(Ui.Label(title, Ui.Subtitle));
            _status = Ui.Label(work != null ? I18n.T("dbui.working") : "", Ui.Font, Ui.Muted);
            _status.MaximumSize = new Size(Ui.S(560), 0);
            t.Controls.Add(_status);
            _bar = new ProgressBar { Width = Ui.S(560), Height = Ui.S(6), Style = ProgressBarStyle.Marquee, Visible = work != null, Margin = Ui.P(0, 8, 0, 8) };
            t.Controls.Add(_bar);
            _result = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = Ui.S(560), Height = Ui.S(220), Visible = work == null, Font = Ui.Mono, Text = (text ?? "").Replace("\n", "\r\n") };
            t.Controls.Add(_result);
            _buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 12, 0, 0) };
            _close = Ui.Button(I18n.T("common.close"), (s, e) => Close(), true);
            _close.Enabled = work == null;
            _cancel = Ui.Button(I18n.T("common.cancel"), (s, e) => { _cts.Cancel(); _status.Text = I18n.T("dbui.cancelling"); });
            _cancel.Visible = work != null;
            var copy = Ui.Button(I18n.T("dbui.copy"), (s, e) => { try { Clipboard.SetText(_result.Text); } catch { } });
            _buttons.Controls.Add(_close);
            _buttons.Controls.Add(_cancel);
            _buttons.Controls.Add(copy);
            t.Controls.Add(_buttons);
            Controls.Add(t);
            if (work != null) Shown += async (s, e) => await RunAsync();
            FormClosing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        readonly List<ModernButton> _extra = new List<ModernButton>();

        /// <summary>
        /// Extra button shown after the job (e.g. "Start in recovery mode"). With <paramref name="confirm"/> the first
        /// click only shows that text and asks for a second click (no second dialog over this one).
        /// </summary>
        public void AddButton(string text, Action click, string confirm = null)
        {
            bool armed = false;
            ModernButton b = null;
            b = Ui.Button(text, (s, e) =>
            {
                if (confirm != null && !armed)
                {
                    armed = true;
                    _status.Text = confirm;
                    b.Text = I18n.T("dbui.clickAgain", text);
                    return;
                }
                click();
            });
            b.Visible = _work == null;
            _extra.Add(b);
            _buttons.Controls.Add(b);
        }

        async Task RunAsync()
        {
            _busy = true;
            try
            {
                ResultText = await _work(msg => { if (IsHandleCreated) BeginInvoke((Action)(() => _status.Text = msg)); }, _cts.Token);
                _status.Text = I18n.T("dbui.done");
            }
            catch (OperationCanceledException) { Failed = true; ResultText = I18n.T("sqlimp.cancelled"); _status.Text = ResultText; }
            catch (Exception ex) { Failed = true; ResultText = ex.Message; _status.Text = I18n.T("dbui.failed"); Log.Warn(ex.Message); }
            _busy = false;
            _bar.Visible = false;
            _cancel.Visible = false;
            _close.Enabled = true;
            foreach (var b in _extra) b.Visible = !Failed && (ShowExtra == null || ShowExtra());
            _result.Text = (ResultText ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            _result.Visible = true;
        }
    }

    /// <summary>Database menu actions: new database, SQL import, check and repair, version change, WSL access.</summary>
    public static class DatabaseUi
    {
        static ThemedCombo CollationCombo(string engine, Version v, string selected)
        {
            var c = new ThemedCombo { Width = Ui.S(260) };
            foreach (var x in DbOptions.Collations(engine, v)) c.Items.Add(new Item(x == Settings.DefaultCollation ? x + "  (" + I18n.T("dbui.default") + ")" : x, x));
            c.SelectedItem = c.Items.Cast<Item>().FirstOrDefault(i => i.Value == selected) ?? c.Items[0];
            return c;
        }

        static ThemedCombo ActiveCollationCombo()
        {
            var engine = Settings.Current.DbEngine;
            return CollationCombo(engine, PackageTypes.ParseVersion(PackageTypes.ActiveFolder(engine)), DbOptions.EffectiveCollation());
        }

        static Form Dialog(string title)
        {
            var f = new Form();
            Ui.Apply(f);
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Padding = Ui.P(20);
            return f;
        }

        static void Row(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++;
            var l = Ui.Label(label);
            l.Margin = Ui.P(0, 8, 16, 4);
            c.Margin = Ui.P(0, 4, 0, 4);
            t.Controls.Add(l, 0, r);
            t.Controls.Add(c, 1, r);
        }

        static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        static FlowLayoutPanel OkCancel(Form f, string okText, Func<bool> onOk)
        {
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 12, 0, 0) };
            var cancel = Ui.Button(I18n.T("common.cancel"), null);
            cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.Button(okText, (s, e) => { if (onOk()) { f.DialogResult = DialogResult.OK; f.Close(); } }, true);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            return buttons;
        }

        // =====================================================================
        // New database
        // =====================================================================
        /// <summary>Name, collation and an optional user with rights only on this database.</summary>
        public static void NewDatabase(IWin32Window owner, string suggested)
        {
            if (ServiceManager.MySql.State != ServiceState.Running) { Ui.Error(owner, I18n.T("err.mysqlNotRunning")); return; }
            using (var f = Dialog(I18n.T("db.create")))
            {
                var t = Grid();
                var name = new TextBox { Text = suggested ?? "", Width = Ui.S(260) };
                Row(t, I18n.T("dbui.name"), name);
                var coll = ActiveCollationCombo();
                Row(t, I18n.T("dbui.collation"), coll);
                var withUser = new CheckBox { Text = I18n.T("dbui.withUser"), AutoSize = true };
                int r = t.RowCount++;
                t.Controls.Add(withUser, 0, r);
                t.SetColumnSpan(withUser, 2);
                var user = new TextBox { Width = Ui.S(200), Enabled = false };
                var pass = new TextBox { Width = Ui.S(200), Enabled = false, Text = Database.RandomPassword(16) };
                Row(t, I18n.T("dbui.user"), user);
                Row(t, I18n.T("dbui.password"), pass);
                withUser.CheckedChanged += (s, e) =>
                {
                    user.Enabled = pass.Enabled = withUser.Checked;
                    if (withUser.Checked && user.Text == "") user.Text = name.Text.Trim();
                };
                string message = null;
                var buttons = OkCancel(f, I18n.T("db.create"), () =>
                {
                    var n = name.Text.Trim();
                    var err = Database.CreateDatabase(n, ((Item)coll.SelectedItem).Value);
                    if (err == null && withUser.Checked)
                    {
                        if (pass.Text == "") { Ui.Error(f, I18n.T("dbui.emptyPassword")); return false; }
                        err = Database.CreateUser(n, user.Text.Trim(), pass.Text);
                    }
                    if (err != null) { Ui.Error(f, err); return false; }
                    var s2 = Settings.Current;
                    message = I18n.T("db.created", n) + "\n\n" +
                        "Host: 127.0.0.1\nPort: " + s2.MysqlPort + "\n" + I18n.T("dbui.database") + ": " + n + "\n" +
                        (withUser.Checked
                            ? I18n.T("dbui.user") + " " + user.Text.Trim() + "\n" + I18n.T("dbui.password") + " " + pass.Text + "\n\n" +
                              "DB_CONNECTION=mysql\nDB_HOST=127.0.0.1\nDB_PORT=" + s2.MysqlPort + "\nDB_DATABASE=" + n + "\nDB_USERNAME=" + user.Text.Trim() + "\nDB_PASSWORD=" + pass.Text
                            : I18n.T("dbui.collation") + " " + Database.CollationOf(n));
                    return true;
                });
                var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
                root.Controls.Add(t);
                root.Controls.Add(buttons);
                f.Controls.Add(root);
                if (f.ShowDialog(owner) != DialogResult.OK || message == null) return;
                using (var res = new DbTaskForm(I18n.T("db.create"), null, message)) res.ShowDialog(owner);
            }
        }

        // =====================================================================
        // SQL import
        // =====================================================================
        public static void ImportSql(IWin32Window owner)
        {
            if (ServiceManager.MySql.State != ServiceState.Running) { Ui.Error(owner, I18n.T("err.mysqlNotRunning")); return; }
            string file;
            using (var dlg = new OpenFileDialog { Filter = I18n.T("sqlimp.filter") + " (*.sql;*.sql.gz;*.gz;*.zip)|*.sql;*.gz;*.zip|*.*|*.*" })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                file = dlg.FileName;
            }
            if (!SqlImport.IsSupported(file)) { Ui.Error(owner, I18n.T("sqlimp.badFile")); return; }
            using (var f = Dialog(I18n.T("sqlimp.title")))
            {
                var t = Grid();
                Row(t, I18n.T("sqlimp.file"), Ui.Label(Path.GetFileName(file) + "  (" + Ui.FormatBytes(new FileInfo(file).Length) + ")"));
                var target = new ThemedCombo { Width = Ui.S(260) };
                target.Items.Add(new Item(I18n.T("sqlimp.fromFile"), ""));
                target.Items.Add(new Item(I18n.T("sqlimp.newDb"), "__new"));
                foreach (var d in Database.ListDatabases()) target.Items.Add(new Item(d, d));
                target.SelectedIndex = 1;
                Row(t, I18n.T("sqlimp.target"), target);
                var suggested = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(file).Split('.')[0], "[^A-Za-z0-9_]", "_");
                var newName = new TextBox { Width = Ui.S(260), Text = suggested.Length > 64 ? suggested.Substring(0, 64) : suggested };
                Row(t, I18n.T("dbui.name"), newName);
                var coll = ActiveCollationCombo();
                Row(t, I18n.T("dbui.collation"), coll);
                target.SelectedIndexChanged += (s, e) => newName.Enabled = coll.Enabled = ((Item)target.SelectedItem).Value == "__new";
                var bar = new ProgressBar { Width = Ui.S(420), Height = Ui.S(8), Minimum = 0, Maximum = 1000, Margin = Ui.P(0, 12, 0, 4) };
                var status = Ui.Label(I18n.T("sqlimp.hint"), Ui.Small, Ui.Muted);
                status.MaximumSize = new Size(Ui.S(520), 0);
                var errBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = Ui.S(520), Height = Ui.S(120), Font = Ui.Mono, Visible = false };

                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 12, 0, 0) };
                CancellationTokenSource cts = null;
                bool busy = false;
                var close = Ui.Button(I18n.T("common.close"), (s, e) => { if (busy) { cts.Cancel(); status.Text = I18n.T("dbui.cancelling"); } else f.Close(); });
                ModernButton start = null;
                start = Ui.Button(I18n.T("sqlimp.start"), async (s, e) =>
                {
                    var choice = ((Item)target.SelectedItem).Value;
                    string db = choice == "" ? null : choice;
                    if (choice == "__new")
                    {
                        db = newName.Text.Trim();
                        var err = Database.CreateDatabase(db, ((Item)coll.SelectedItem).Value);
                        if (err != null) { Ui.Error(f, err); return; }
                    }
                    busy = true;
                    start.Enabled = target.Enabled = newName.Enabled = coll.Enabled = false;
                    close.Text = I18n.T("common.cancel");
                    errBox.Visible = false;
                    cts = new CancellationTokenSource();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Action<long, long> progress = (done, total) =>
                    {
                        if (!f.IsHandleCreated) return;
                        f.BeginInvoke((Action)(() =>
                        {
                            bar.Value = total > 0 ? (int)Math.Min(1000, done * 1000 / total) : 0;
                            status.Text = I18n.T("sqlimp.progress", Ui.FormatBytes(done), Ui.FormatBytes(total));
                        }));
                    };
                    var token = cts.Token;
                    var res = await Task.Run(() => SqlImport.Run(file, db, false, progress, token));
                    busy = false;
                    close.Text = I18n.T("common.close");
                    start.Enabled = target.Enabled = true;
                    if (res.Cancelled) { status.Text = I18n.T("sqlimp.cancelled"); return; }
                    if (res.Ok)
                    {
                        bar.Value = 1000;
                        status.Text = I18n.T("sqlimp.done", db ?? "-", sw.Elapsed.TotalSeconds.ToString("0"));
                        return;
                    }
                    status.Text = I18n.T("sqlimp.failed");
                    errBox.Text = res.Message + (res.Line > 0 ? "\r\n\r\n" + I18n.T("sqlimp.atLine", res.Line) + "\r\n" + res.Statement : "");
                    errBox.Visible = true;
                }, true, "import");
                buttons.Controls.Add(close);
                buttons.Controls.Add(start);
                f.FormClosing += (s, e) => { if (busy) e.Cancel = true; };

                var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
                root.Controls.Add(t);
                root.Controls.Add(bar);
                root.Controls.Add(status);
                root.Controls.Add(errBox);
                root.Controls.Add(buttons);
                f.Controls.Add(root);
                f.ShowDialog(owner);
            }
        }

        // =====================================================================
        // Check and repair
        // =====================================================================
        public static async Task CheckAndRepair(IWin32Window owner)
        {
            var mysql = ServiceManager.MySql;
            if (mysql.State != ServiceState.Running)
            {
                // MariaDB that does not start because of its Aria logs: fix it offline first
                if (!mysql.IsActive && DbRepair.AriaProblemInLog())
                {
                    if (!Ui.Confirm(owner, I18n.T("repair.ariaConfirm", Path.Combine(Paths.Backup, "mysql")))) return;
                    using (var fx = new DbTaskForm(I18n.T("repair.title"), async (status, ct) =>
                    {
                        var backup = await Task.Run(() => DbRepair.OfflineAriaFix(status));
                        status(I18n.T("dbv.startingNew", mysql.Title));
                        await mysql.StartAsync();
                        return I18n.T("repair.ariaResult", backup) + (mysql.State == ServiceState.Running ? "" : "\n\n" + mysql.LastError);
                    })) fx.ShowDialog(owner);
                    if (mysql.State != ServiceState.Running) return;
                }
                else
                {
                    await mysql.StartAsync();
                    if (mysql.State != ServiceState.Running) { Ui.Error(owner, I18n.T("repair.cannotStart") + "\n\n" + mysql.LastError); return; }
                }
            }
            DbRepair.Result result = null;
            using (var f = new DbTaskForm(I18n.T("repair.title"), async (status, ct) =>
            {
                result = await Task.Run(() => DbRepair.CheckAndRepair(status));
                return result.Summary + (result.NeedRecovery.Count > 0 ? "\n\n" + I18n.T("repair.recoveryHint") : "");
            }))
            {
                bool recovery = false;
                f.ShowExtra = () => result != null && result.NeedRecovery.Count > 0;
                f.AddButton(I18n.T("db.recovery"), () => { recovery = true; f.Close(); }, I18n.T("db.recoveryConfirm"));
                f.ShowDialog(owner);
                if (recovery)
                {
                    await mysql.StartRecoveryAsync();
                    if (mysql.State == ServiceState.Running) Ui.Info(owner, I18n.T("db.recoveryRunning"));
                }
            }
        }

        // =====================================================================
        // Version change
        // =====================================================================
        public enum SwitchResult { Continue, Done, Cancelled }

        /// <summary>
        /// Call before activating another MySQL / MariaDB version or engine. Continue = switch as usual,
        /// Done = the databases were moved and the new version is active, Cancelled = keep the current one.
        /// </summary>
        public static SwitchResult BeforeSwitch(IWin32Window owner, string engine, string folder)
        {
            var dataDir = DbVersionChange.DataDir(engine, folder);
            var block = DbVersionChange.BlockOlder(dataDir, engine, folder);
            if (block != null) { Ui.Error(owner, block); return SwitchResult.Cancelled; }
            var plan = DbVersionChange.PlanFor(engine, folder);
            if (plan == null) return SwitchResult.Continue;

            int choice = AskMove(owner, plan);
            if (choice == 0) return SwitchResult.Cancelled;
            if (choice == 2) return SwitchResult.Continue;
            using (var f = new DbTaskForm(I18n.T("dbv.title"), (status, ct) => DbVersionChange.MoveAsync(plan, status, ct)))
            {
                f.ShowDialog(owner);
                return f.Failed ? SwitchResult.Cancelled : SwitchResult.Done;
            }
        }

        /// <summary>1 = move the databases, 2 = start with an empty server, 0 = cancel.</summary>
        static int AskMove(IWin32Window owner, DbVersionChange.Plan p)
        {
            using (var f = Dialog(I18n.T("dbv.title")))
            {
                var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
                var how = p.Move == DbMove.Copy ? I18n.T("dbv.howCopy", p.FromLabel) : I18n.T("dbv.howDump");
                var text = Ui.Label(I18n.T("dbv.ask", p.FromLabel, p.ToLabel, p.Databases.Count, string.Join(", ", p.Databases.Take(8)) + (p.Databases.Count > 8 ? " ..." : ""), p.FromData) + "\n\n" + how);
                text.MaximumSize = new Size(Ui.S(540), 0);
                t.Controls.Add(text);
                int result = 0;
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 14, 0, 0) };
                var cancel = Ui.Button(I18n.T("common.cancel"), (s, e) => { result = 0; f.Close(); });
                var empty = Ui.Button(I18n.T("dbv.empty"), (s, e) => { result = 2; f.Close(); });
                var move = Ui.Button(I18n.T("dbv.move"), (s, e) => { result = 1; f.Close(); }, true, "database");
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(empty);
                buttons.Controls.Add(move);
                t.Controls.Add(buttons);
                f.Controls.Add(t);
                f.CancelButton = cancel;
                f.ShowDialog(owner);
                return result;
            }
        }

#if !LEGACY
        // =====================================================================
        // Access from WSL
        // =====================================================================
        public static async Task WslAccessDialog(IWin32Window owner)
        {
            var s = Settings.Current;
            if (!WslAccess.Supported) return;
            if (WslAccess.IsMirrored()) { Ui.Info(owner, I18n.T("wsl.mirrored", s.MysqlPort)); return; }
            var mysql = ServiceManager.MySql;
            var a = WslAccess.Find();
            if (s.WslAccess)
            {
                var text = a == null ? I18n.T("wsl.noAdapter") : WslAccess.ConnectionText(a, s.WslPassword);
                if (a != null && a.Subnet != s.WslSubnet) text = I18n.T("wsl.subnetChanged", s.WslSubnet, a.Subnet) + "\n\n" + text;
                bool off = false, refresh = false;
                using (var f = new DbTaskForm(I18n.T("feature.wsl"), null, text))
                {
                    f.AddButton(I18n.T("wsl.turnOff"), () => { off = true; f.Close(); }, I18n.T("wsl.offConfirm"));
                    if (a != null) f.AddButton(I18n.T("wsl.refresh"), () => { refresh = true; f.Close(); });
                    f.ShowDialog(owner);
                }
                if (off)
                {
                    if (!WslAccess.Disable()) return;
                    if (mysql.IsActive) await mysql.RestartAsync();
                    Ui.Info(owner, I18n.T("wsl.offDone"));
                    return;
                }
                if (!refresh) return;
            }
            if (a == null) { Ui.Error(owner, I18n.T("wsl.noAdapter")); return; }
            if (!s.AllowLan && !WslAccess.MultiBind()) { Ui.Error(owner, I18n.T("wsl.oldServer")); return; }
            // everything that will happen, including the admin step, before UAC
            if (!Ui.Confirm(owner, I18n.T("wsl.confirm", a.Ip, WslAccess.User + "@" + a.UserHost, WslAccess.FirewallCommand(a)))) return;
            if (mysql.State != ServiceState.Running) await mysql.StartAsync();
            string result;
            try { result = WslAccess.Enable(); }
            catch (Exception ex) { Ui.Error(owner, ex.Message); return; }
            await mysql.RestartAsync();   // listen on the WSL address too
            using (var f = new DbTaskForm(I18n.T("feature.wsl"), null, result)) f.ShowDialog(owner);
        }
#endif
    }
}
