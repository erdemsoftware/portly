using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>
    /// PHP extensions of one PHP version: the bundled ones (switch on / off in php.ini) and verified downloads
    /// that match the PHP build (install / remove). Opened from the PHP menu and the packages window.
    /// </summary>
    public class PhpExtensionsForm : PortlyDialog
    {
        sealed class Row
        {
            public string Id;
            public bool Bundled;      // ships with PHP (ext\php_x.dll) and was not installed by Portly
            public bool Enabled;
            public ExtInfo Info;      // downloadable
            public bool Installed;
            public string Why;        // reason when no download fits
            public Border Box;
        }

        sealed class Item
        {
            public string Text, Value;
            public Item(string text, string value) { Text = text; Value = value; }
            public override string ToString() { return Text; }
        }

        const string Columns = "190,90,190,*";

        readonly bool _preview;
        readonly ComboBox _php;
        readonly TextBlock _build, _status;
        readonly StackPanel _rows = new StackPanel();
        readonly ProgressBar _progress;
        readonly Button _install, _remove, _toggle, _close;
        Row _selected;
        CancellationTokenSource _cts;
        bool _busy, _closed;

        string PhpDir { get { var i = _php.SelectedItem as Item; return i == null ? null : Path.Combine(Paths.BinOf("php"), i.Value); } }

        public PhpExtensionsForm(string phpFolder = null) : this(phpFolder, false) { }

        PhpExtensionsForm(string phpFolder, bool preview) : base(I18n.T("ext.title"), 860)
        {
            _preview = preview;
            MakeResizable(600);
            MinWidth = 640;
            MinHeight = 420;

            _php = new ComboBox { Width = 210 };
            var folders = preview ? new List<string> { "php-8.4.12-nts-Win32-vs17-x64", "php-8.3.25-nts-Win32-vs16-x64" } : PackageTypes.Installed("php");
            var items = folders.Select(f => new Item("PHP " + PackageTypes.Label(f), f)).ToList();
            _php.ItemsSource = items;
            var want = phpFolder ?? (preview ? folders[0] : PackageTypes.ActiveFolder("php"));
            _php.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Value, want, StringComparison.OrdinalIgnoreCase));
            if (_php.SelectedIndex < 0 && items.Count > 0) _php.SelectedIndex = 0;
            _php.SelectionChanged += (s, e) => { _selected = null; Fill(); };
            _build = Ui.Label("", "muted");
            _build.VerticalAlignment = VerticalAlignment.Center;
            var bar = Ui.Row(new TextBlock { Text = I18n.T("ext.phpVersion"), VerticalAlignment = VerticalAlignment.Center }, _php, _build);
            var intro = Ui.Label(I18n.T("ext.intro"), "muted");
            var top = Ui.Column(8, intro, bar);
            top.Margin = new Thickness(0, 0, 0, 10);

            var head = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns) };
            string[] titles = { I18n.T("ext.colName"), I18n.T("col.version"), I18n.T("col.status"), I18n.T("col.note") };
            for (int i = 0; i < titles.Length; i++)
            {
                var t = Ui.Label(titles[i], "bold small", Ui.Muted);
                t.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(t, i);
                head.Children.Add(t);
            }
            var headBox = new Border { Padding = new Thickness(0, 8), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
            var listDock = new DockPanel();
            DockPanel.SetDock(headBox, Dock.Top);
            listDock.Children.Add(headBox);
            listDock.Children.Add(new ScrollViewer { Content = _rows });
            var card = Ui.Card(listDock, new Thickness(0));
            card.ClipToBounds = true;

            _progress = new ProgressBar { Height = 8, IsVisible = false, IsIndeterminate = true };
            _status = Ui.Label("", "muted");
            _install = Ui.Button(I18n.T("ext.install"), async () => await InstallSelected(), "primary");
            _remove = Ui.Button(I18n.T("pkg.remove"), async () => await RemoveSelected());
            _toggle = Ui.Button(I18n.T("ext.enable"), async () => await ToggleSelected());
            var ini = Ui.Button("php.ini", () =>
            {
                if (PhpDir == null) return;
                if (OS.IsWindows) WinUtil.Edit(PhpConfig.IniPath(PhpDir)); else Ui.Open(PhpConfig.IniPath(PhpDir));
            });
            _close = Ui.Button(I18n.T("common.close"), () => { if (_busy) { if (_cts != null) _cts.Cancel(); } else Close(); });
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
            foreach (var b in new[] { _install, _toggle, _remove, ini, _close }) buttons.Children.Add(b);
            var bottom = Ui.Column(6, _progress, _status, buttons);
            bottom.Margin = new Thickness(0, 10, 0, 0);

            var dock = new DockPanel { Margin = new Thickness(20, 16, 20, 18) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(bottom);
            dock.Children.Add(card);
            Content = dock;
            Closing += (s, e) => { if (_busy) e.Cancel = true; };
            Closed += (s, e) => _closed = true;

            PhpExtensions.LoadLocal();
            Fill();
            if (!preview)
                Opened += async (s, e) =>
                {
                    // newest list (modern edition); the embedded copy is shown meanwhile
                    await PhpExtensions.RefreshAsync();
                    if (!_closed && !_busy) Fill();
                    if (!_closed && PhpExtensions.TrustError != null) _status.Text = PhpExtensions.TrustError;
                };
        }

        /// <summary>Opens the extensions window of a PHP version (null = the active one).</summary>
        public static async Task ShowAsync(Window owner, string phpFolder = null)
        {
            await new PhpExtensionsForm(phpFolder).ShowDialog(owner);
        }

        public static Window Preview() { return new PhpExtensionsForm(null, true); }

        static readonly string[] PreviewBundled = { "curl", "fileinfo", "gd", "intl", "mbstring", "mysqli", "openssl", "pdo_mysql", "pdo_sqlite", "sodium", "zip" };

        void Fill()
        {
            var dir = PhpDir;
            var sel = _selected == null ? null : _selected.Id;
            _rows.Children.Clear();
            _selected = null;
            if (dir == null) { _build.Text = I18n.T("ext.noPhp"); UpdateButtons(); return; }
            var build = _preview
                ? new PhpBuild { Folder = Path.GetFileName(dir), Branch = "8.4", Version = "8.4.12", ThreadSafe = false, Compiler = "vs17", Arch = "x64", Api = "API20240924" }
                : PhpExtensions.Detect(dir);
            _build.Text = build.ToString();
            var ours = _preview ? new List<string>(PhpExtensions.All.Take(2).Select(x => x.Id)) : PhpExtensions.InstalledByPortly(dir);
            var listed = _preview
                ? PreviewBundled.Select(x => new KeyValuePair<string, bool>(x, x != "intl" && x != "sodium"))
                    .Concat(new[] { new KeyValuePair<string, bool>(ours[0], true), new KeyValuePair<string, bool>(ours[1], false) }).ToList()
                : PhpConfig.ListExtensions(dir);
            var enabled = listed.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            var download = new List<Row>();
            foreach (var x in PhpExtensions.All.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var r = new Row
                {
                    Id = x.Id, Info = x, Installed = _preview ? ours.Contains(x.Id) : PhpExtensions.IsInstalled(dir, x.Id),
                    Why = PhpExtensions.Unavailable(x, build),
                };
                bool on;
                r.Enabled = enabled.TryGetValue(x.Id, out on) && on;
                // shipped with this PHP build (e.g. a PHP that already has redis): handled as bundled below
                if (r.Installed && !ours.Contains(x.Id)) continue;
                download.Add(r);
            }
            AddGroup(I18n.T("ext.groupDownload"));
            foreach (var r in download)
            {
                var x = r.Info;
                var b = x.BuildFor(build);
                var version = _preview && r.Installed ? (b != null ? b.Version : "-") : r.Installed ? PhpExtensions.InstalledVersion(dir, x.Id) : "";
                string status = r.Installed ? I18n.T(r.Enabled ? "ext.installedOn" : "ext.installedOff", version)
                    : r.Why != null ? I18n.T("ext.notAvailable") : I18n.T("ext.notInstalled");
                var note = r.Why ?? (x.Description + (x.Note != "" ? "  " + x.Note : ""));
                var tip = x.Description + (x.Note != "" ? "\n" + x.Note : "") + (r.Why != null ? "\n" + r.Why : "");
                var fore = r.Installed ? (r.Enabled ? Ui.Green : Ui.Text) : r.Why != null ? Ui.Muted : Ui.Text;
                AddRow(r, new[] { x.Name, b != null ? b.Version : "-", status, note }, fore, tip);
            }
            var bundled = new List<Row>();
            foreach (var kv in listed)
            {
                if (ours.Contains(kv.Key)) continue;
                bundled.Add(new Row { Id = kv.Key, Bundled = true, Enabled = kv.Value, Installed = true });
            }
            if (bundled.Count > 0) AddGroup(I18n.T("ext.groupBundled"));
            foreach (var r in bundled)
                AddRow(r, new[] { r.Id, "", I18n.T(r.Enabled ? "ext.on" : "ext.off"), "" }, r.Enabled ? Ui.Green : Ui.Text, null);
            if (sel != null)
            {
                // rows are rebuilt: keep the selection
                var again = _rows.Children.Select(c => c.Tag as Row).FirstOrDefault(r => r != null && r.Id == sel);
                if (again != null) Select(again);
            }
            UpdateButtons();
        }

        void AddGroup(string title)
        {
            _rows.Children.Add(new Border { Padding = new Thickness(12, 12, 12, 4), Child = Ui.Label(Ui.Upper(title), "section") });
        }

        void AddRow(Row r, string[] cells, IBrush fore, string tip)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns) };
            for (int i = 0; i < cells.Length; i++)
            {
                var t = Ui.Label(cells[i], null, fore);
                t.TextWrapping = TextWrapping.NoWrap;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.VerticalAlignment = VerticalAlignment.Center;
                t.Margin = new Thickness(12, 0, 6, 0);
                if (i == 0) t.FontWeight = FontWeight.SemiBold;
                Grid.SetColumn(t, i);
                grid.Children.Add(t);
            }
            r.Box = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 7), Child = grid, Tag = r };
            if (!string.IsNullOrEmpty(tip)) ToolTip.SetTip(r.Box, tip);
            r.Box.PointerPressed += (s, e) => Select(r);
            r.Box.DoubleTapped += async (s, e) => await Ui.Run(PrimaryAction, this);
            _rows.Children.Add(r.Box);
        }

        void Select(Row r)
        {
            if (_selected != null && _selected.Box != null) _selected.Box.Background = Brushes.Transparent;
            _selected = r;
            if (r != null) r.Box.Background = Ui.Brush("SidebarActive");
            UpdateButtons();
        }

        void UpdateButtons()
        {
            var r = _selected;
            _install.IsEnabled = !_busy && r != null && !r.Bundled && !r.Installed && r.Why == null;
            _remove.IsEnabled = !_busy && r != null && !r.Bundled && r.Installed;
            _toggle.IsEnabled = !_busy && r != null && r.Installed;
            _toggle.Content = I18n.T(r != null && r.Enabled ? "ext.disable" : "ext.enable");
            _close.Content = _busy ? I18n.T("common.cancel") : I18n.T("common.close");
            _php.IsEnabled = !_busy;
        }

        async Task PrimaryAction()
        {
            var r = _selected;
            if (r == null || _busy) return;
            if (r.Installed) await ToggleSelected();
            else if (r.Why == null) await InstallSelected();
        }

        async Task Busy(Func<Task> work)
        {
            _busy = true;
            _cts = new CancellationTokenSource();
            _progress.IsVisible = true;
            UpdateButtons();
            try { await work(); }
            catch (OperationCanceledException) { _status.Text = I18n.T("pkg.cancelled"); }
            catch (Exception ex) { Log.Error("PHP extensions", ex); _status.Text = ""; await Dialogs.Warning(this, ex.Message); }
            finally
            {
                _busy = false;
                _progress.IsVisible = false;
                Fill();
            }
        }

        void Status(string text) { Ui.Post(() => _status.Text = text); }

        async Task InstallSelected()
        {
            var r = _selected;
            var dir = PhpDir;
            if (r == null || dir == null || r.Info == null) return;
            await Busy(async () =>
            {
                await PhpExtensions.InstallAsync(dir, r.Id, Status, _cts.Token);
                Status(I18n.T("ext.restarting"));
                await RestartIfUsed(dir);
                _status.Text = I18n.T("ext.installedDone", r.Info.Name, PackageTypes.Label(Path.GetFileName(dir)));
            });
        }

        async Task RemoveSelected()
        {
            var r = _selected;
            var dir = PhpDir;
            if (r == null || dir == null || !await Dialogs.Confirm(this, I18n.T("ext.removeConfirm", r.Info != null ? r.Info.Name : r.Id, PackageTypes.Label(Path.GetFileName(dir))))) return;
            await Busy(async () =>
            {
                // switch it off and restart first: a running php-cgi keeps the DLL locked
                PhpConfig.ToggleExtension(dir, r.Id, false);
                await RestartIfUsed(dir);
                await Task.Run(() => PhpExtensions.Remove(dir, r.Id));
                _status.Text = I18n.T("ext.removedDone", r.Info != null ? r.Info.Name : r.Id);
            });
        }

        async Task ToggleSelected()
        {
            var r = _selected;
            var dir = PhpDir;
            if (r == null || dir == null) return;
            await Busy(async () =>
            {
                bool on = !r.Enabled;
                PhpConfig.ToggleExtension(dir, r.Id, on);
                if (on)
                {
                    var err = await Task.Run(() => PhpExtensions.CheckLoads(dir, r.Id));
                    if (err != null)
                    {
                        PhpConfig.ToggleExtension(dir, r.Id, false);
                        throw new Exception(I18n.T("ext.startFailed", r.Id, err));
                    }
                }
                Log.Info(I18n.T(on ? "log.extOn" : "log.extOff", r.Id));
                await RestartIfUsed(dir);
            });
        }

        /// <summary>Restart the web server / PHP pools when this PHP version is running.</summary>
        static async Task RestartIfUsed(string dir)
        {
            var folder = Path.GetFileName(dir);
            if (ServiceManager.Php.RunningFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) await ServiceManager.ReloadWebAsync();
        }

        // ---------------- after a PHP install / update ----------------
        /// <summary>
        /// A PHP version was installed or updated: offer to install the downloaded extensions of the previous one there too,
        /// and say which ones have no build for the new version.
        /// </summary>
        public static async Task OfferCopyAsync(Window owner, string fromFolder, string toFolder)
        {
            if (!Features.IsOn("extensions") || string.IsNullOrEmpty(fromFolder) || string.IsNullOrEmpty(toFolder)) return;
            if (string.Equals(fromFolder, toFolder, StringComparison.OrdinalIgnoreCase)) return;
            var from = Path.Combine(Paths.BinOf("php"), fromFolder);
            var to = Path.Combine(Paths.BinOf("php"), toFolder);
            if (!Directory.Exists(from) || !Directory.Exists(to)) return;
            var plan = await Task.Run(() => PhpExtensions.PlanCopy(from, to));
            if (plan.Item1.Count == 0 && plan.Item2.Count == 0) return;
            string fromLabel = PackageTypes.Label(fromFolder), toLabel = PackageTypes.Label(toFolder);
            var missing = plan.Item2.Count == 0 ? "" : "\n\n" + I18n.T("ext.copyMissing", toLabel) + "\n" + string.Join("\n", plan.Item2.Select(m => "• " + m));
            if (plan.Item1.Count == 0) { await Dialogs.Info(owner, I18n.T("ext.copyNone", fromLabel, toLabel) + missing); return; }
            var names = string.Join(", ", plan.Item1.Select(x => x.Name));
            if (!await Dialogs.Confirm(owner, I18n.T("ext.copyConfirm", fromLabel, toLabel, names) + missing)) return;
            var failed = new List<string>();
            foreach (var x in plan.Item1)
            {
                try { await PhpExtensions.InstallAsync(to, x.Id, null, CancellationToken.None); }
                catch (Exception ex) { failed.Add(x.Name + ": " + ex.Message); }
            }
            await RestartIfUsed(to);
            if (failed.Count > 0) await Dialogs.Warning(owner, string.Join("\n\n", failed));
        }
    }
}
