using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>Combo item with display text and a value (ToString is what the combo shows).</summary>
    internal sealed class Opt
    {
        public string Text, Value;
        public Opt(string text, string value) { Text = text; Value = value; }
        public override string ToString() { return Text; }
    }

    /// <summary>Small form building blocks shared by the database and settings dialogs.</summary>
    internal static class FormKit
    {
        /// <summary>Label column + input column; add rows with AddRow / AddWide.</summary>
        public static Grid Rows()
        {
            return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16, RowSpacing = 8 };
        }

        public static void AddRow(Grid g, string label, Control c)
        {
            int r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = Ui.Label(label);
            l.VerticalAlignment = VerticalAlignment.Center;
            c.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(l, r);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            g.Children.Add(l);
            g.Children.Add(c);
        }

        public static void AddWide(Grid g, Control c)
        {
            int r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(c, r);
            Grid.SetColumnSpan(c, 2);
            g.Children.Add(c);
        }

        public static ComboBox Combo(double width)
        {
            return new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        }

        public static string Value(ComboBox c)
        {
            var o = c.SelectedItem as Opt;
            return o == null ? null : o.Value;
        }

        public static void Select(ComboBox c, string value)
        {
            c.SelectedItem = c.Items.OfType<Opt>().FirstOrDefault(i => i.Value == value) ?? c.Items.OfType<Opt>().FirstOrDefault();
        }

        public static TextBox Text(string text, double width, bool password = false)
        {
            return new TextBox { Text = text ?? "", Width = width, HorizontalAlignment = HorizontalAlignment.Left, PasswordChar = password ? '•' : default(char) };
        }

        public static CheckBox Check(string text, bool value, double maxWidth = 520)
        {
            return new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth }, IsChecked = value };
        }

        public static NumericUpDown Number(decimal min, decimal max, decimal value, double width)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = width, FormatString = "0",
                Increment = 1, HorizontalAlignment = HorizontalAlignment.Left, ParsingNumberStyle = System.Globalization.NumberStyles.Integer,
            };
        }

        public static TextBlock Hint(string text, double maxWidth = 380)
        {
            var l = Ui.Label(text, "muted small");
            l.MaxWidth = maxWidth;
            l.HorizontalAlignment = HorizontalAlignment.Left;
            return l;
        }

        public static string Newlines(string text) { return (text ?? "").Replace("\r\n", "\n"); }

        public static ComboBox CollationCombo(IEnumerable<string> collations, string selected)
        {
            var c = Combo(260);
            foreach (var x in collations) c.Items.Add(new Opt(x == Settings.DefaultCollation ? x + "  (" + I18n.T("dbui.default") + ")" : x, x));
            Select(c, selected);
            return c;
        }
    }

    /// <summary>
    /// Runs a long database job with a status line, then shows its result as copyable text.
    /// Without a job it only shows the text (connection details).
    /// </summary>
    public class DbTaskForm : PortlyDialog
    {
        readonly TextBlock _status;
        readonly ProgressBar _bar;
        readonly TextBox _result;
        readonly Button _close, _cancel;
        readonly Func<Action<string>, CancellationToken, Task<string>> _work;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly List<Button> _extra = new List<Button>();
        bool _busy;
        public bool Failed { get; private set; }
        /// <summary>Decides after the job whether the extra buttons appear.</summary>
        public Func<bool> ShowExtra;
        public string ResultText { get; private set; }

        public DbTaskForm(string title, Func<Action<string>, CancellationToken, Task<string>> work, string text = null) : base(title, 620)
        {
            _work = work;
            _status = Ui.Label(work != null ? I18n.T("dbui.working") : "", "muted");
            _bar = new ProgressBar { IsIndeterminate = true, Height = 6, IsVisible = work != null, Margin = new Thickness(0, 4) };
            _result = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 220, IsVisible = work == null, Text = FormKit.Newlines(text), TextWrapping = TextWrapping.Wrap };
            _result.Classes.Add("mono");
            Body.Children.Add(Ui.Label(title, "subtitle"));
            if (work != null) Body.Children.Add(_status);
            Body.Children.Add(_bar);
            Body.Children.Add(_result);

            _close = Ui.Button(I18n.T("common.close"), () => Close(), "primary");
            _close.IsEnabled = work == null;
            _cancel = Ui.Button(I18n.T("common.cancel"), () => { _cts.Cancel(); _status.Text = I18n.T("dbui.cancelling"); });
            _cancel.IsVisible = work != null;
            var copy = Ui.Button(I18n.T("dbui.copy"), async () => { await Ui.Copy(this, _result.Text); }, null, "copy");
            Buttons.Children.Add(copy);
            Buttons.Children.Add(_cancel);
            Buttons.Children.Add(_close);
            if (work != null) Opened += async (s, e) => await RunAsync();
            Closing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        /// <summary>
        /// Extra button shown after the job (e.g. "Start in recovery mode"). With <paramref name="confirm"/> the first
        /// click only shows that text and asks for a second click (no second dialog over this one).
        /// </summary>
        public void AddButton(string text, Action click, string confirm = null)
        {
            bool armed = false;
            Button b = null;
            b = Ui.Button(text, () =>
            {
                if (confirm != null && !armed)
                {
                    armed = true;
                    _status.Text = confirm;
                    if (!Body.Children.Contains(_status)) Body.Children.Insert(1, _status);
                    b.Content = I18n.T("dbui.clickAgain", text);
                    return;
                }
                click();
            });
            b.IsVisible = _work == null;
            Buttons.Children.Insert(_extra.Count, b);
            _extra.Add(b);
        }

        async Task RunAsync()
        {
            _busy = true;
            try
            {
                ResultText = await _work(msg => Ui.Post(() => _status.Text = msg), _cts.Token);
                _status.Text = I18n.T("dbui.done");
            }
            catch (OperationCanceledException) { Failed = true; ResultText = I18n.T("sqlimp.cancelled"); _status.Text = ResultText; }
            catch (Exception ex) { Failed = true; ResultText = ex.Message; _status.Text = I18n.T("dbui.failed"); Log.Warn(ex.Message); }
            _busy = false;
            _bar.IsVisible = false;
            _cancel.IsVisible = false;
            _close.IsEnabled = true;
            foreach (var b in _extra) b.IsVisible = !Failed && (ShowExtra == null || ShowExtra());
            _result.Text = FormKit.Newlines(ResultText);
            _result.IsVisible = true;
        }

        public static Window Preview()
        {
            var settings = "Host: 127.0.0.1\nPort: 3306\nDatabase: shop\nUser shop\nPassword Xk3pQ9vLmZ2tR7aB\n\nDB_CONNECTION=mysql\nDB_HOST=127.0.0.1\nDB_PORT=3306\nDB_DATABASE=shop\nDB_USERNAME=shop\nDB_PASSWORD=Xk3pQ9vLmZ2tR7aB";
            var f = new DbTaskForm(I18n.T("db.create"), null, I18n.T("db.created", "shop") + "\n\n" + settings);
            f.AddButton(I18n.T("db.recovery"), () => { });
            return f;
        }
    }

    /// <summary>
    /// One line on the main window while root has no password and the server listens beyond this computer.
    /// Settings, password and WSL state change in many places, so it simply looks again every few seconds.
    /// </summary>
    public class RootPasswordNotice : Border
    {
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        bool _sample;

        public RootPasswordNotice()
        {
            Classes.Add("card");
            BorderBrush = new SolidColorBrush(Portly.Desktop.UI.Theme.Current.Orange, 0.65);
            Padding = new Thickness(16, 8, 8, 8);
            Margin = new Thickness(0, 0, 0, 8);
            var text = I18n.T("dbroot.exposed");
            var label = Ui.Label(text);
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(label, text);
            var button = Ui.Button(I18n.T("dbroot.set"), async () => { await DatabaseUi.RootPassword(Ui.OwnerOf(this)); Check(); });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            Grid.SetColumn(button, 1);
            grid.Children.Add(label);
            grid.Children.Add(button);
            Child = grid;
            _timer.Tick += (s, e) => Check();
            AttachedToVisualTree += (s, e) => { _timer.Start(); Check(); };
            DetachedFromVisualTree += (s, e) => _timer.Stop();
            Check();
        }

        void Check() { IsVisible = _sample || (Database.RootPasswordExposed && MySqlService.BinDir != null); }

        public static Window Preview()
        {
            var w = new PortlyDialog("Portly", 760);
            var notice = new RootPasswordNotice();
            w.Content = new StackPanel { Margin = new Thickness(20), Children = { notice } };
            notice._sample = true;
            notice._timer.Stop();
            notice.IsVisible = true;
            return w;
        }
    }

    /// <summary>Database menu actions: new database, SQL import, check and repair, version change, WSL access.</summary>
    public static class DatabaseUi
    {
        public enum SwitchResult { Continue, Done, Cancelled }

        static async Task<bool> MySqlRunning(Window owner)
        {
            if (ServiceManager.MySql.State == ServiceState.Running) return true;
            await Dialogs.Warning(owner, I18n.T("err.mysqlNotRunning"));
            return false;
        }

        // =====================================================================
        // root password
        // =====================================================================
        /// <summary>Set, change or remove the root password (Settings > MySQL and the database menu).</summary>
        public static async Task RootPassword(Window owner)
        {
            if (!await MySqlRunning(owner)) return;
            var f = new RootPasswordDialog(Settings.Current.HasRootPassword);
            if (!await f.ShowDialog<bool>(owner) || f.Done == null) return;
            await Dialogs.Info(owner, f.Done);
        }

        /// <summary>One line for Settings: has root a password, and does it matter right now?</summary>
        public static string RootStatus()
        {
            if (Settings.Current.HasRootPassword) return I18n.T("dbroot.statusSet");
            return I18n.T(Database.RootPasswordExposed ? "dbroot.exposed" : "dbroot.statusNone");
        }

        static ComboBox ActiveCollationCombo()
        {
            var engine = Settings.Current.DbEngine;
            var v = PackageTypes.ParseVersion(PackageTypes.ActiveFolder(engine));
            return FormKit.CollationCombo(DbOptions.Collations(engine, v), DbOptions.EffectiveCollation());
        }

        // =====================================================================
        // New database
        // =====================================================================
        /// <summary>Name, collation and an optional user with rights only on this database.</summary>
        public static async Task NewDatabase(Window owner, string suggested)
        {
            if (!await MySqlRunning(owner)) return;
            var f = new NewDatabaseDialog(suggested, ActiveCollationCombo());
            if (!await f.ShowDialog<bool>(owner) || f.Message == null) return;
            await new DbTaskForm(I18n.T("db.create"), null, f.Message).ShowDialog(owner);
        }

        // =====================================================================
        // SQL import
        // =====================================================================
        public static async Task ImportSql(Window owner)
        {
            if (!await MySqlRunning(owner)) return;
            var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(I18n.T("sqlimp.filter") + " (*.sql;*.sql.gz;*.gz;*.zip)") { Patterns = new[] { "*.sql", "*.gz", "*.zip" } },
                    FilePickerFileTypes.All,
                },
            });
            if (picked.Count == 0) return;
            var file = picked[0].TryGetLocalPath();
            if (file == null) return;
            if (!SqlImport.IsSupported(file)) { await Dialogs.Warning(owner, I18n.T("sqlimp.badFile")); return; }
            var f = new ImportSqlDialog(file, new FileInfo(file).Length, Database.ListDatabases(), ActiveCollationCombo());
            await f.ShowDialog(owner);
        }

        // =====================================================================
        // Check and repair
        // =====================================================================
        public static async Task CheckAndRepair(Window owner)
        {
            var mysql = ServiceManager.MySql;
            if (mysql.State != ServiceState.Running)
            {
                // MariaDB that does not start because of its Aria logs: fix it offline first
                if (!mysql.IsActive && DbRepair.AriaProblemInLog())
                {
                    if (!await Dialogs.Confirm(owner, I18n.T("repair.ariaConfirm", Path.Combine(Paths.Backup, "mysql")))) return;
                    var fx = new DbTaskForm(I18n.T("repair.title"), async (status, ct) =>
                    {
                        var backup = await Task.Run(() => DbRepair.OfflineAriaFix(status));
                        status(I18n.T("dbv.startingNew", mysql.Title));
                        await mysql.StartAsync();
                        return I18n.T("repair.ariaResult", backup) + (mysql.State == ServiceState.Running ? "" : "\n\n" + mysql.LastError);
                    });
                    await fx.ShowDialog(owner);
                    if (mysql.State != ServiceState.Running) return;
                }
                else
                {
                    await mysql.StartAsync();
                    if (mysql.State != ServiceState.Running) { await Dialogs.Warning(owner, I18n.T("repair.cannotStart") + "\n\n" + mysql.LastError); return; }
                }
            }
            DbRepair.Result result = null;
            var f = new DbTaskForm(I18n.T("repair.title"), async (status, ct) =>
            {
                result = await Task.Run(() => DbRepair.CheckAndRepair(status));
                return result.Summary + (result.NeedRecovery.Count > 0 ? "\n\n" + I18n.T("repair.recoveryHint") : "");
            });
            bool recovery = false;
            f.ShowExtra = () => result != null && result.NeedRecovery.Count > 0;
            f.AddButton(I18n.T("db.recovery"), () => { recovery = true; f.Close(); }, I18n.T("db.recoveryConfirm"));
            await f.ShowDialog(owner);
            if (recovery)
            {
                await mysql.StartRecoveryAsync();
                if (mysql.State == ServiceState.Running) await Dialogs.Info(owner, I18n.T("db.recoveryRunning"));
            }
        }

        // =====================================================================
        // Version change
        // =====================================================================
        /// <summary>
        /// Call before activating another MySQL / MariaDB version or engine. Continue = switch as usual,
        /// Done = the databases were moved and the new version is active, Cancelled = keep the current one.
        /// </summary>
        public static async Task<SwitchResult> BeforeSwitch(Window owner, string engine, string folder)
        {
            var dataDir = DbVersionChange.DataDir(engine, folder);
            var block = DbVersionChange.BlockOlder(dataDir, engine, folder);
            if (block != null) { await Dialogs.Warning(owner, block); return SwitchResult.Cancelled; }
            var plan = DbVersionChange.PlanFor(engine, folder);
            if (plan == null) return SwitchResult.Continue;

            var ask = new AskMoveDialog(plan.FromLabel, plan.ToLabel, plan.Databases, plan.FromData, plan.Move);
            int choice = await ask.ShowDialog<int>(owner);
            if (choice == 0) return SwitchResult.Cancelled;
            if (choice == 2) return SwitchResult.Continue;
            var f = new DbTaskForm(I18n.T("dbv.title"), (status, ct) => DbVersionChange.MoveAsync(plan, status, ct));
            await f.ShowDialog(owner);
            return f.Failed ? SwitchResult.Cancelled : SwitchResult.Done;
        }

        // =====================================================================
        // Access from WSL
        // =====================================================================
        public static async Task WslAccessDialog(Window owner)
        {
            if (!OS.IsWindows || !WslAccess.Supported) return;
            var s = Settings.Current;
            if (WslAccess.IsMirrored()) { await Dialogs.Info(owner, I18n.T("wsl.mirrored", s.MysqlPort)); return; }
            var mysql = ServiceManager.MySql;
            var a = WslAccess.Find();
            if (s.WslAccess)
            {
                var text = a == null ? I18n.T("wsl.noAdapter") : WslAccess.ConnectionText(a, s.WslPassword);
                if (a != null && a.Subnet != s.WslSubnet) text = I18n.T("wsl.subnetChanged", s.WslSubnet, a.Subnet) + "\n\n" + text;
                bool off = false, refresh = false;
                var f = new DbTaskForm(I18n.T("feature.wsl"), null, text);
                f.AddButton(I18n.T("wsl.turnOff"), () => { off = true; f.Close(); }, I18n.T("wsl.offConfirm"));
                if (a != null) f.AddButton(I18n.T("wsl.refresh"), () => { refresh = true; f.Close(); });
                await f.ShowDialog(owner);
                if (off)
                {
                    if (!WslAccess.Disable()) return;
                    if (mysql.IsActive) await mysql.RestartAsync();
                    await Dialogs.Info(owner, I18n.T("wsl.offDone"));
                    return;
                }
                if (!refresh) return;
            }
            if (a == null) { await Dialogs.Warning(owner, I18n.T("wsl.noAdapter")); return; }
            if (!s.AllowLan && !WslAccess.MultiBind()) { await Dialogs.Warning(owner, I18n.T("wsl.oldServer")); return; }
            // everything that will happen, including the admin step, before UAC
            if (!await Dialogs.Confirm(owner, I18n.T("wsl.confirm", a.Ip, WslAccess.User + "@" + a.UserHost, WslAccess.FirewallCommand(a)))) return;
            if (mysql.State != ServiceState.Running) await mysql.StartAsync();
            string result;
            try { result = WslAccess.Enable(); }
            catch (Exception ex) { await Dialogs.Warning(owner, ex.Message); return; }
            await mysql.RestartAsync();   // listen on the WSL address too
            await new DbTaskForm(I18n.T("feature.wsl"), null, result).ShowDialog(owner);
        }
    }

    /// <summary>Set, change or remove the root password.</summary>
    public sealed class RootPasswordDialog : PortlyDialog
    {
        readonly TextBox _pass = FormKit.Text("", 260, true), _repeat = FormKit.Text("", 260, true);
        readonly TextBlock _message = Ui.Label("", "error");
        readonly CheckBox _show = new CheckBox { Content = I18n.T("common.show") };
        public string Done { get; private set; }

        public RootPasswordDialog(bool has) : base(I18n.T(has ? "db.password" : "dbroot.set"), 540)
        {
            Body.Children.Add(Ui.Label(I18n.T("dbroot.intro"), "muted"));
            Body.Children.Add(Ui.Field(I18n.T("db.newPassword"), _pass));
            Body.Children.Add(Ui.Field(I18n.T("dbroot.repeat"), _repeat));
            Body.Children.Add(_show);
            Body.Children.Add(_message);
            _pass.HorizontalAlignment = _repeat.HorizontalAlignment = HorizontalAlignment.Stretch;
            _pass.Width = _repeat.Width = double.NaN;
            _show.IsCheckedChanged += (s, e) =>
            {
                var c = _show.IsChecked == true ? default(char) : '•';
                _pass.PasswordChar = _repeat.PasswordChar = c;
            };

            if (has)
            {
                // two clicks in this window: a second dialog on top would hide what it is about
                bool armed = false;
                Button remove = null;
                remove = Ui.Button(I18n.T("dbroot.remove"), async () =>
                {
                    if (!armed)
                    {
                        armed = true;
                        _message.Text = I18n.T("dbroot.removeWarn");
                        remove.Content = I18n.T("dbui.clickAgain", I18n.T("dbroot.remove"));
                        return;
                    }
                    if (!await Apply("")) return;
                    Done = I18n.T("dbroot.removed");
                    Close(true);
                }, "danger");
                Buttons.Children.Add(remove);
            }
            Buttons.Children.Add(Ui.Button(I18n.T("dbroot.generate"), () =>
            {
                _pass.Text = _repeat.Text = Database.RandomPassword(20);
                _show.IsChecked = true;
            }));
            Buttons.Children.Add(Ui.Button(I18n.T("common.save"), Save, "primary"));
            Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(false)));
            KeyDown += async (s, e) => { if (e.Key == Key.Enter) await Ui.Run(Save, this); };
        }

        async Task<bool> Apply(string text)
        {
            var old = Cursor;
            Cursor = new Cursor(StandardCursorType.Wait);
            string err;
            try { err = await Task.Run(() => Database.ChangeRootPassword(text)); }
            finally { Cursor = old; }
            if (err == null) return true;
            _message.Text = err;
            return false;
        }

        async Task Save()
        {
            if (_pass.Text == "") { _message.Text = I18n.T("dbroot.empty"); return; }
            if (_pass.Text != _repeat.Text) { _message.Text = I18n.T("dbroot.mismatch"); return; }
            if (!await Apply(_pass.Text)) return;
            Done = I18n.T("db.passwordChanged");
            Close(true);
        }

        public static Window Preview() { return new RootPasswordDialog(true); }
    }

    /// <summary>Name, collation and an optional user for a new database.</summary>
    public sealed class NewDatabaseDialog : PortlyDialog
    {
        readonly TextBox _name, _user = FormKit.Text("", 220), _pass = FormKit.Text("", 220);
        readonly ComboBox _coll;
        readonly CheckBox _withUser = FormKit.Check(I18n.T("dbui.withUser"), false);
        public string Message { get; private set; }

        public NewDatabaseDialog(string suggested, ComboBox collation) : base(I18n.T("db.create"), 520)
        {
            var t = FormKit.Rows();
            _name = FormKit.Text(suggested, 260);
            FormKit.AddRow(t, I18n.T("dbui.name"), _name);
            _coll = collation;
            FormKit.AddRow(t, I18n.T("dbui.collation"), _coll);
            FormKit.AddWide(t, _withUser);
            _user.IsEnabled = _pass.IsEnabled = false;
            _pass.Text = Database.RandomPassword(16);
            FormKit.AddRow(t, I18n.T("dbui.user"), _user);
            FormKit.AddRow(t, I18n.T("dbui.password"), _pass);
            Body.Children.Add(t);
            _withUser.IsCheckedChanged += (s, e) =>
            {
                bool on = _withUser.IsChecked == true;
                _user.IsEnabled = _pass.IsEnabled = on;
                if (on && _user.Text == "") _user.Text = _name.Text.Trim();
            };
            Buttons.Children.Add(Ui.Button(I18n.T("db.create"), Create, "primary"));
            Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(false)));
            KeyDown += async (s, e) => { if (e.Key == Key.Enter) await Ui.Run(Create, this); };
        }

        async Task Create()
        {
            var n = _name.Text.Trim();
            bool withUser = _withUser.IsChecked == true;
            var collation = FormKit.Value(_coll);
            var err = Database.CreateDatabase(n, collation);
            if (err == null && withUser)
            {
                if (_pass.Text == "") { await Dialogs.Warning(this, I18n.T("dbui.emptyPassword")); return; }
                err = Database.CreateUser(n, _user.Text.Trim(), _pass.Text);
            }
            if (err != null) { await Dialogs.Warning(this, err); return; }
            var s2 = Settings.Current;
            Message = I18n.T("db.created", n) + "\n\n" +
                "Host: 127.0.0.1\nPort: " + s2.MysqlPort + "\n" + I18n.T("dbui.database") + ": " + n + "\n" +
                (withUser
                    ? I18n.T("dbui.user") + " " + _user.Text.Trim() + "\n" + I18n.T("dbui.password") + " " + _pass.Text + "\n\n" +
                      "DB_CONNECTION=mysql\nDB_HOST=127.0.0.1\nDB_PORT=" + s2.MysqlPort + "\nDB_DATABASE=" + n + "\nDB_USERNAME=" + _user.Text.Trim() + "\nDB_PASSWORD=" + _pass.Text
                    : I18n.T("dbui.collation") + " " + Database.CollationOf(n));
            Close(true);
        }

        public static Window Preview()
        {
            var combo = FormKit.CollationCombo(new[] { "utf8mb4_unicode_ci", "utf8mb4_general_ci", "utf8mb4_0900_ai_ci", "utf8mb4_turkish_ci" }, Settings.DefaultCollation);
            var d = new NewDatabaseDialog("shop", combo);
            d._withUser.IsChecked = true;
            return d;
        }
    }

    /// <summary>Imports a .sql / .sql.gz / .zip file into a new or existing database with progress.</summary>
    public sealed class ImportSqlDialog : PortlyDialog
    {
        readonly string _file;
        readonly ComboBox _target, _coll;
        readonly TextBox _newName;
        readonly ProgressBar _bar = new ProgressBar { Minimum = 0, Maximum = 1000, Height = 8 };
        readonly TextBlock _status = Ui.Label(I18n.T("sqlimp.hint"), "muted small");
        readonly TextBox _errBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 120, IsVisible = false };
        readonly Button _close, _start;
        CancellationTokenSource _cts;
        bool _busy;

        public ImportSqlDialog(string file, long size, IEnumerable<string> databases, ComboBox collation) : base(I18n.T("sqlimp.title"), 600)
        {
            _file = file;
            var t = FormKit.Rows();
            FormKit.AddRow(t, I18n.T("sqlimp.file"), Ui.Label(Path.GetFileName(file) + "  (" + Ui.FormatBytes(size) + ")"));
            _target = FormKit.Combo(260);
            _target.Items.Add(new Opt(I18n.T("sqlimp.fromFile"), ""));
            _target.Items.Add(new Opt(I18n.T("sqlimp.newDb"), "__new"));
            foreach (var d in databases) _target.Items.Add(new Opt(d, d));
            _target.SelectedIndex = 1;
            FormKit.AddRow(t, I18n.T("sqlimp.target"), _target);
            var suggested = Regex.Replace(Path.GetFileName(file).Split('.')[0], "[^A-Za-z0-9_]", "_");
            _newName = FormKit.Text(suggested.Length > 64 ? suggested.Substring(0, 64) : suggested, 260);
            FormKit.AddRow(t, I18n.T("dbui.name"), _newName);
            _coll = collation;
            FormKit.AddRow(t, I18n.T("dbui.collation"), _coll);
            _target.SelectionChanged += (s, e) => _newName.IsEnabled = _coll.IsEnabled = FormKit.Value(_target) == "__new";
            _errBox.Classes.Add("mono");
            Body.Children.Add(t);
            Body.Children.Add(_bar);
            Body.Children.Add(_status);
            Body.Children.Add(_errBox);

            _close = Ui.Button(I18n.T("common.close"), () => { if (_busy) { _cts.Cancel(); _status.Text = I18n.T("dbui.cancelling"); } else Close(); });
            _start = Ui.Button(I18n.T("sqlimp.start"), Start, "primary", "import");
            Buttons.Children.Add(_start);
            Buttons.Children.Add(_close);
            Closing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        async Task Start()
        {
            var choice = FormKit.Value(_target);
            string db = choice == "" ? null : choice;
            if (choice == "__new")
            {
                db = _newName.Text.Trim();
                var err = Database.CreateDatabase(db, FormKit.Value(_coll));
                if (err != null) { await Dialogs.Warning(this, err); return; }
            }
            _busy = true;
            _start.IsEnabled = _target.IsEnabled = _newName.IsEnabled = _coll.IsEnabled = false;
            _close.Content = I18n.T("common.cancel");
            _errBox.IsVisible = false;
            _cts = new CancellationTokenSource();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Action<long, long> progress = (done, total) => Ui.Post(() =>
            {
                _bar.Value = total > 0 ? (int)Math.Min(1000, done * 1000 / total) : 0;
                _status.Text = I18n.T("sqlimp.progress", Ui.FormatBytes(done), Ui.FormatBytes(total));
            });
            var token = _cts.Token;
            var file = _file;
            var res = await Task.Run(() => SqlImport.Run(file, db, false, progress, token));
            _busy = false;
            _close.Content = I18n.T("common.close");
            _start.IsEnabled = _target.IsEnabled = true;
            if (res.Cancelled) { _status.Text = I18n.T("sqlimp.cancelled"); return; }
            if (res.Ok)
            {
                _bar.Value = 1000;
                _status.Text = I18n.T("sqlimp.done", db ?? "-", sw.Elapsed.TotalSeconds.ToString("0"));
                return;
            }
            _status.Text = I18n.T("sqlimp.failed");
            _errBox.Text = res.Message + (res.Line > 0 ? "\n\n" + I18n.T("sqlimp.atLine", res.Line) + "\n" + res.Statement : "");
            _errBox.IsVisible = true;
        }

        public static Window Preview()
        {
            var combo = FormKit.CollationCombo(new[] { "utf8mb4_unicode_ci", "utf8mb4_general_ci", "utf8mb4_0900_ai_ci" }, Settings.DefaultCollation);
            var d = new ImportSqlDialog("shop-backup.sql", 48L * 1024 * 1024, new[] { "blog", "shop", "wordpress" }, combo);
            d._bar.Value = 420;
            d._status.Text = I18n.T("sqlimp.progress", Ui.FormatBytes(20L * 1024 * 1024), Ui.FormatBytes(48L * 1024 * 1024));
            return d;
        }
    }

    /// <summary>Version change: bring the databases along, start empty, or cancel. Closes with 1, 2 or 0.</summary>
    public sealed class AskMoveDialog : PortlyDialog
    {
        public AskMoveDialog(string from, string to, IList<string> databases, string fromData, DbMove move) : base(I18n.T("dbv.title"), 600)
        {
            var how = move == DbMove.Copy ? I18n.T("dbv.howCopy", from) : I18n.T("dbv.howDump");
            var list = string.Join(", ", databases.Take(8)) + (databases.Count > 8 ? " ..." : "");
            Body.Children.Add(Ui.Selectable(I18n.T("dbv.ask", from, to, databases.Count, list, fromData) + "\n\n" + how));
            Buttons.Children.Add(Ui.Button(I18n.T("dbv.move"), () => Close(1), "primary", "database"));
            Buttons.Children.Add(Ui.Button(I18n.T("dbv.empty"), () => Close(2)));
            Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(0)));
        }

        public static Window Preview()
        {
            return new AskMoveDialog("MySQL 8.0", "MariaDB 11.4", new[] { "blog", "shop", "wordpress" }, @"C:\Portly\data\mysql-8.0", DbMove.Dump);
        }
    }
}
