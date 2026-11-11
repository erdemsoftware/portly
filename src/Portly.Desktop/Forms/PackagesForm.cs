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
using Avalonia.VisualTree;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>Install / update / remove packages. In first-run mode the recommended set is pre-selected.</summary>
    public class PackagesForm : PortlyDialog
    {
        sealed class PkgRow
        {
            public string Type;
            public string Folder;
            public PackageEntry Entry;   // catalog entry (null for a manually added folder)
            public PackageEntry Update;  // newer build of the same branch
            public bool Installed;
            public string SystemExe;     // installed on the system outside Portly (VS Code, Git ...): used as is
        }

        sealed class Line
        {
            public PkgRow Row;
            public string Key;
            public Border Box;
            public CheckBox Check;
        }

        sealed class Item
        {
            public string Text, Value;
            public Item(string text, string value) { Text = text; Value = value; }
            public override string ToString() { return Text; }
        }

        const string Columns = "34,120,100,1.1*,1.5*";
        static readonly string[] FirstRunTypes = { "apache", "php", "mysql", "phpmyadmin", "composer", "mailpit", "mailhog" };

        readonly bool _firstRun;
        readonly bool _preview;
        readonly ComboBox _filter;
        readonly StackPanel _rows = new StackPanel();
        readonly ProgressBar _progress;
        readonly TextBlock _status;
        readonly Button _install, _remove, _activate, _close, _refresh;
        readonly List<Line> _lines = new List<Line>();
        Line _selected;
        CancellationTokenSource _cts;
        bool _busy, _changed;

        public PackagesForm(string type, bool firstRun, IEnumerable<PackageEntry> preselect = null) : this(type, firstRun, preselect, false) { }

        PackagesForm(string type, bool firstRun, IEnumerable<PackageEntry> preselect, bool preview) : base(I18n.T(firstRun ? "pkg.firstRunTitle" : "pkg.title"), 920)
        {
            _firstRun = firstRun;
            _preview = preview;
            MakeResizable(660);
            MinWidth = 700;
            MinHeight = 440;
            ShowInTaskbar = false;

            var intro = Ui.Label(firstRun ? I18n.T("pkg.firstRunIntro") : (AppInfo.IsLegacy ? I18n.T("pkg.legacyNote") : I18n.T("pkg.intro", Catalog.Updated)), "muted");
            _filter = new ComboBox { Width = 240, MaxDropDownHeight = 420 };
            var items = new List<Item> { new Item(I18n.T("pkg.all"), "") };
            // a category ("cat:db") and, indented below it, its package types
            foreach (var c in PackageTypes.Categories)
            {
                items.Add(new Item(I18n.T("pkgcat." + c), "cat:" + c));
                foreach (var t in PackageTypes.Ordered.Where(x => x.Category == c)) items.Add(new Item("      " + t.Name, t.Id));
            }
            _filter.ItemsSource = items;
            _filter.SelectedItem = items.FirstOrDefault(i => i.Value == (type ?? "")) ?? items[0];
            _filter.SelectionChanged += (s, e) => { _selected = null; Fill(); };

            var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
            bar.Children.Add(new TextBlock { Text = I18n.T("pkg.show"), VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(_filter);
            if (AppInfo.UpdatesSupported)
            {
                _refresh = Ui.Button(I18n.T("pkg.refresh"), async () => await RefreshCatalog());
                bar.Children.Add(_refresh);
            }
            // free disk space: versions nothing uses any more
            bar.Children.Add(Ui.Button(I18n.T("cleanup.button"), async () =>
            {
                if (_busy) return;
                if (await CleanupForm.ShowAsync(this) > 0) _changed = true;
                Fill();
            }));
            var top = Ui.Column(8, intro, bar);
            top.Margin = new Thickness(0, 0, 0, 10);

            // list: header + scrolling rows inside one card-like frame
            var head = RowGrid();
            head.Margin = new Thickness(0, 0, 0, 0);
            string[] titles = { "", I18n.T("col.package"), I18n.T("col.version"), I18n.T("col.note"), I18n.T("col.status") };
            for (int i = 1; i < titles.Length; i++)
            {
                var t = Ui.Label(titles[i], "bold small", Ui.Muted);
                t.Margin = new Thickness(6, 0, 0, 0);
                Grid.SetColumn(t, i);
                head.Children.Add(t);
            }
            var headBox = new Border { Padding = new Thickness(0, 8), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
            var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var listDock = new DockPanel();
            DockPanel.SetDock(headBox, Dock.Top);
            listDock.Children.Add(headBox);
            listDock.Children.Add(scroll);
            var listCard = Ui.Card(listDock, new Thickness(0));
            listCard.ClipToBounds = true;

            _progress = new ProgressBar { Height = 8, IsVisible = false, Minimum = 0, Maximum = 100 };
            _status = Ui.Label("", "muted");
            _install = Ui.Button(I18n.T("pkg.installSelected"), async () => await InstallChecked(), "primary");
            _remove = Ui.Button(I18n.T("pkg.remove"), async () => await RemoveSelected());
            _activate = Ui.Button(I18n.T("pkg.activate"), async () => await ActivateSelected());
            _close = Ui.Button(I18n.T("common.close"), () => { if (_busy) { if (_cts != null) _cts.Cancel(); } else Close(); });
            var manual = Ui.Button(I18n.T("pkg.addManual"), async () => await AddManual(), null, "plus");
            var folder = Ui.Button(I18n.T("pkg.openBin"), () => { Directory.CreateDirectory(Paths.Bin); Ui.Open(Paths.Bin); }, null, "folder");
            // narrow windows / long translations: wrap instead of cutting the last button off
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
            buttons.Children.Add(_install);
            buttons.Children.Add(_activate);
            buttons.Children.Add(_remove);
            buttons.Children.Add(manual);
            buttons.Children.Add(folder);
            if (Features.IsOn("extensions")) buttons.Children.Add(Ui.Button(I18n.T("ext.menu"), async () => await PhpExtensionsForm.ShowAsync(this)));
            buttons.Children.Add(_close);
            var bottom = Ui.Column(6, _progress, _status, buttons);
            bottom.Margin = new Thickness(0, 10, 0, 0);

            var dock = new DockPanel { Margin = new Thickness(20, 16, 20, 18) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(bottom);
            dock.Children.Add(listCard);
            Content = dock;

            Fill();
            if (firstRun) PreselectRecommended();
            if (preselect != null)
            {
                // e.g. the packages of an imported setup: ready to install with one click
                var keys = new HashSet<string>(preselect.Select(p => p.Type + "/" + p.Folder), StringComparer.OrdinalIgnoreCase);
                foreach (var l in _lines) if (l.Check != null && keys.Contains(l.Key)) l.Check.IsChecked = true;
            }
            UpdateButtons();
        }

        /// <summary>Opens the package manager. True when something was installed, updated, removed, activated or added.</summary>
        public static async Task<bool> Show(Window owner, string type, bool firstRun, IEnumerable<PackageEntry> preselect = null)
        {
            var d = new PackagesForm(type, firstRun, preselect);
            await d.ShowDialog(owner);
            return d._changed;
        }

        public static Window Preview() { return new PackagesForm(null, false, Catalog.ForType("nginx").Take(1).Concat(Catalog.ForType("redis").Take(1)).ToList(), true); }

        static Grid RowGrid() { return new Grid { ColumnDefinitions = new ColumnDefinitions(Columns) }; }

        void Fill()
        {
            var filter = ((Item)_filter.SelectedItem).Value;
            var checkedKeys = new HashSet<string>(_lines.Where(l => l.Check != null && l.Check.IsChecked == true).Select(l => l.Key));
            var selectedKey = _selected == null ? null : _selected.Key;
            _rows.Children.Clear();
            _lines.Clear();
            _selected = null;
            var rows = new List<PkgRow>();
            foreach (var t in PackageTypes.Ordered)
            {
                if (filter.StartsWith("cat:") ? t.Category != filter.Substring(4) : filter != "" && t.Id != filter) continue;
                var installed = _preview ? new List<string>() : PackageTypes.Installed(t.Id);
                foreach (var e in Catalog.ForType(t.Id))
                    rows.Add(new PkgRow { Type = t.Id, Folder = e.Folder, Entry = e, Installed = _preview ? PreviewInstalled(e) : e.IsInstalled });
                foreach (var f in installed.Where(f => !rows.Any(r => r.Type == t.Id && string.Equals(r.Folder, f, StringComparison.OrdinalIgnoreCase))))
                    rows.Add(new PkgRow { Type = t.Id, Folder = f, Entry = Catalog.FindByFolder(t.Id, f), Installed = true });
                var sys = installed.Count == 0 && !_preview ? DesktopApps.SystemExe(t.Id) : null;
                if (sys != null)
                {
                    // above the downloadable versions of the same program
                    int at = rows.FindIndex(r => r.Type == t.Id);
                    rows.Insert(at < 0 ? rows.Count : at, new PkgRow { Type = t.Id, Folder = "", SystemExe = sys });
                }
            }
            foreach (var c in PackageTypes.Categories)
            {
                var group = rows.Where(r => PackageTypes.Get(r.Type).Category == c).ToList();
                if (group.Count == 0) continue;
                _rows.Children.Add(new Border
                {
                    Padding = new Thickness(12, 12, 12, 4),
                    Child = Ui.Label(Ui.Upper(I18n.T("pkgcat." + c)), "section"),
                });
                foreach (var r in group) AddRow(r, checkedKeys);
            }
            if (selectedKey != null) Select(_lines.FirstOrDefault(l => l.Key == selectedKey));
            UpdateButtons();
        }

        static bool PreviewInstalled(PackageEntry e)
        {
            if (e.Type != "apache" && e.Type != "php" && e.Type != "mysql" && e.Type != "phpmyadmin") return false;
            return Catalog.ForType(e.Type).FirstOrDefault() == e;
        }

        bool IsActiveVersion(PkgRow r)
        {
            if (_preview) return r.Installed;
            return string.Equals(PackageTypes.ActiveFolder(r.Type), r.Folder, StringComparison.OrdinalIgnoreCase);
        }

        void AddRow(PkgRow r, HashSet<string> checkedKeys)
        {
            if (r.Installed && AppInfo.UpdatesSupported)
            {
                r.Update = _preview ? PreviewUpdate(r) : Catalog.UpdateFor(r.Type, r.Folder);
            }
            var name = PackageTypes.Get(r.Type).Name;
            string version, note, status, tip = null;
            IBrush fore = null;
            if (r.SystemExe != null)
            {
                // a system Tomcat runs as its own service: shown for information, Portly runs its own copy
                version = SystemVersion(r.SystemExe);
                note = r.Type == "tomcat" ? I18n.T("pkg.onSystemTomcat") : I18n.T("pkg.onSystem");
                status = r.Type == "tomcat" ? r.SystemExe : I18n.T("pkg.inUse", r.SystemExe);
                fore = Ui.Green;
            }
            else
            {
                version = r.Entry != null ? r.Entry.Version : PackageTypes.Label(r.Folder);
                note = r.Entry == null ? I18n.T("pkg.manual") : JoinNote(r.Entry);
                if (r.Installed)
                {
                    status = IsActiveVersion(r) ? I18n.T("pkg.installedActive") : I18n.T("pkg.installed");
                    if (r.Update != null) status += " - " + I18n.T("pkg.updateTo", r.Update.Version);
                    fore = r.Update != null ? Ui.Orange : Ui.Green;
                }
                else status = "";
                // end of life / known vulnerabilities (security.json)
                var sec = r.Installed && !_preview ? SecurityInfo.StatusOf(r.Type, r.Folder) : null;
                if (sec != null)
                {
                    status += " - " + sec.Long;
                    fore = sec.Advisories.Count > 0 ? Ui.Red : Ui.Orange;
                    tip = sec.Long;
                }
            }

            var line = new Line { Row = r, Key = r.Type + "/" + (r.SystemExe != null ? "system" : r.Folder) };
            var grid = RowGrid();
            if (r.SystemExe == null)
            {
                line.Check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), IsChecked = checkedKeys.Contains(line.Key) };
                line.Check.IsCheckedChanged += (s, e) => UpdateButtons();
                grid.Children.Add(line.Check);
            }
            string[] cells = { name, version, note, status };
            for (int i = 0; i < cells.Length; i++)
            {
                var t = Ui.Label(cells[i], null, fore);
                t.TextWrapping = TextWrapping.NoWrap;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.VerticalAlignment = VerticalAlignment.Center;
                t.Margin = new Thickness(6, 0, 6, 0);
                if (i == 0) t.FontWeight = FontWeight.SemiBold;
                ToolTip.SetTip(t, tip ?? (cells[i] == "" ? null : cells[i]));
                Grid.SetColumn(t, i + 1);
                grid.Children.Add(t);
            }
            line.Box = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 2), Child = grid, Tag = line };
            line.Box.PointerPressed += (s, e) => { if (!(e.Source is CheckBox) && !(e.Source is Visual v && v.FindAncestorOfType<CheckBox>() != null)) Select(line); };
            _rows.Children.Add(line.Box);
            _lines.Add(line);
        }

        PackageEntry PreviewUpdate(PkgRow r)
        {
            return r.Type == "php" ? Catalog.ForType("php").Skip(1).FirstOrDefault() : null;
        }

        void Select(Line l)
        {
            if (_selected != null) _selected.Box.Background = Brushes.Transparent;
            _selected = l;
            if (l != null) l.Box.Background = Ui.Brush("SidebarActive");
            UpdateButtons();
        }

        static string SystemVersion(string exe)
        {
            // Java: JAVA_VERSION from the "release" file (1.8.0_491, 21.0.4); Tomcat: its folder ("Tomcat 10.1")
            var home = Path.GetDirectoryName(Path.GetDirectoryName(exe));
            if (exe.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase) || exe.EndsWith("/java", StringComparison.Ordinal))
            {
                try
                {
                    var m = Regex.Match(File.ReadAllText(Path.Combine(home, "release")), "JAVA_VERSION=\"([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value;
                }
                catch { }
            }
            if (exe.EndsWith("bootstrap.jar", StringComparison.OrdinalIgnoreCase))
                return Regex.Match(Path.GetFileName(home), "[\\d.]+").Value;
            try { var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe); return (v.ProductVersion ?? v.FileVersion ?? "").Split(' ', '+')[0]; }
            catch { return ""; }
        }

        static string JoinNote(PackageEntry e)
        {
            var parts = new List<string>();
            if (e.Recommended) parts.Add(I18n.T("pkg.recommended"));
            if (e.Tag != "") parts.Add(e.Tag);
            if (e.Note != "") parts.Add(e.Note);
            return string.Join(" · ", parts);
        }

        void PreselectRecommended()
        {
            foreach (var l in _lines)
            {
                var r = l.Row;
                if (l.Check == null || r.Installed || r.Entry == null || !r.Entry.Recommended) continue;
                if (!FirstRunTypes.Contains(r.Type)) continue;
                if (!_preview && PackageTypes.Installed(r.Type).Count > 0) continue;
                l.Check.IsChecked = true;
            }
        }

        PkgRow Selected { get { return _selected == null ? null : _selected.Row; } }

        List<PkgRow> Checked()
        {
            return _lines.Where(l => l.Check != null && l.Check.IsChecked == true).Select(l => l.Row).ToList();
        }

        void UpdateButtons()
        {
            if (_install == null) return;
            var sel = Selected;
            int count = Checked().Count(r => !r.Installed || r.Update != null);
            _install.Content = I18n.T("pkg.installSelected") + (count > 0 ? " (" + count + ")" : "");
            _install.IsEnabled = !_busy && count > 0;
            _remove.IsEnabled = !_busy && sel != null && sel.Installed;
            _activate.IsEnabled = !_busy && sel != null && sel.Installed && !IsActiveVersion(sel);
            _close.Content = _busy ? I18n.T("common.cancel") : I18n.T("common.close");
            _filter.IsEnabled = !_busy;
            if (_refresh != null) _refresh.IsEnabled = !_busy;
        }

        async Task RefreshCatalog()
        {
            _busy = true; UpdateButtons();
            _status.Text = I18n.T("pkg.refreshing");
            var ok = await Catalog.RefreshAsync();
            _status.Text = Catalog.TrustError ?? (ok ? I18n.T("pkg.refreshed", Catalog.Updated) : I18n.T("pkg.refreshFailed"));
            _busy = false;
            Fill();
        }

        async Task InstallChecked()
        {
            var jobs = Checked().Where(r => !r.Installed || r.Update != null).ToList();
            if (jobs.Count == 0) return;
            _busy = true;
            _cts = new CancellationTokenSource();
            UpdateButtons();
            _progress.IsVisible = true;
            int done = 0, failed = 0;
            var phpCopies = new List<Tuple<string, string>>();   // (extensions from, to) for new / updated PHP versions
            var replaced = new List<Tuple<string, string>>();
            foreach (var r in jobs)
            {
                var entry = r.Installed ? r.Update : r.Entry;
                if (entry == null) continue;
                var label = entry.TypeName + " " + entry.Version;
                try
                {
                    Action<InstallProgress> report = p => Ui.Post(() =>
                    {
                        _progress.IsIndeterminate = p.Percent < 0;
                        if (p.Percent >= 0) _progress.Value = Math.Min(100, p.Percent);
                        var stage = I18n.T("pkg.stage." + p.Stage);
                        _status.Text = "[" + (done + failed + 1) + "/" + jobs.Count + "] " + label + " - " + stage + (p.Stage == "download" ? " " + (p.Percent >= 0 ? p.Percent + "%" : Ui.FormatBytes(p.Bytes)) : "");
                    });
                    var phpFrom = r.Installed ? r.Folder : PackageTypes.ActiveFolder("php");
                    if (r.Installed) { await Installer.UpdateAsync(r.Folder, entry, report, _cts.Token); replaced.Add(Tuple.Create(r.Type, r.Folder)); }
                    else await Installer.InstallAsync(entry, report, _cts.Token);
                    if (entry.Type == "php") phpCopies.Add(Tuple.Create(phpFrom, entry.Folder));
                    done++;
                    _changed = true;
                }
                catch (OperationCanceledException)
                {
                    _status.Text = I18n.T("pkg.cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Error(label, ex);
                    await Dialogs.Warning(this, label + "\n\n" + ex.Message);
                }
            }
            _progress.IsVisible = false;
            _busy = false;
            foreach (var l in _lines) if (l.Check != null) l.Check.IsChecked = false;
            Fill();
            Terminal.ApplyUserPath();
            foreach (var c in phpCopies) await PhpExtensionsForm.OfferCopyAsync(this, c.Item1, c.Item2);
            if (!_cts.IsCancellationRequested) _status.Text = I18n.T("pkg.finished", done, failed);
            if (replaced.Count > 0) { await OfferRemoveOld(replaced); Fill(); }
            if (_firstRun && done > 0 && failed == 0)
            {
                await Dialogs.Info(this, I18n.T("pkg.firstRunDone"));
                Close();
            }
        }

        /// <summary>After a patch update: the previous build of the branch is usually no longer needed (type, old folder).</summary>
        async Task OfferRemoveOld(IEnumerable<Tuple<string, string>> replaced)
        {
            var sitePhp = Cleanup.SitePhpFolders();
            var removable = replaced
                .Select(t => new { Type = t.Item1, OldFolder = t.Item2, Dir = Path.Combine(Paths.BinOf(t.Item1), t.Item2) })
                .Where(n => !string.Equals(PackageTypes.ActiveFolder(n.Type), n.OldFolder, StringComparison.OrdinalIgnoreCase))
                .Where(n => !(n.Type == "php" && sitePhp.Contains(n.OldFolder)))
                .Where(x => Directory.Exists(x.Dir) && !Cleanup.IsInUse(x.Dir))
                .ToList();
            if (removable.Count == 0) return;
            long size = removable.Sum(x => Cleanup.FolderSize(x.Dir));
            var names = string.Join(", ", removable.Select(x => PackageTypes.Get(x.Type).Name + " " + PackageTypes.Label(x.OldFolder)));
            if (!await Dialogs.Confirm(this, I18n.T("notice.removeOld", names, Ui.FormatBytes(size)))) return;
            foreach (var x in removable)
            {
                try { Installer.Remove(x.Type, x.OldFolder); }
                catch (Exception ex) { Log.Error(x.OldFolder, ex); }
            }
        }

        async Task ActivateSelected()
        {
            var r = Selected;
            if (r == null) return;
            if (r.Type == Settings.Current.DbEngine)
            {
                // another line has its own data folder: offer to bring the databases along
                var sw = await DatabaseUi.BeforeSwitch(this, r.Type, r.Folder);
                if (sw != DatabaseUi.SwitchResult.Continue) { _changed = true; Fill(); return; }
            }
            Settings.Current.SetVersion(r.Type, r.Folder);
            Settings.Current.Save();
            _changed = true;
            Log.Info(I18n.T("log.switched", PackageTypes.Get(r.Type).Name, PackageTypes.Label(r.Folder)));
            Fill();
            if (r.Type == Settings.Current.DbEngine && ServiceManager.MySql.IsActive) await ServiceManager.MySql.RestartAsync();
            else if (r.Type == "redis" && ServiceManager.Redis.IsActive) await ServiceManager.Redis.RestartAsync();
            else await ServiceManager.ReloadWebAsync();
        }

        async Task RemoveSelected()
        {
            var r = Selected;
            if (r == null || !r.Installed) return;
            bool active = IsActiveVersion(r);
            if (active && IsRunning(r.Type)) { await Dialogs.Warning(this, I18n.T("pkg.stopFirst")); return; }
            var msg = I18n.T("pkg.removeConfirm", PackageTypes.Get(r.Type).Name + " " + PackageTypes.Label(r.Folder));
            if (r.Type == "mysql") msg += "\n\n" + I18n.T("pkg.mysqlDataKept");
            if (!await Dialogs.Confirm(this, msg)) return;
            try { Installer.Remove(r.Type, r.Folder); _changed = true; }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
            Fill();
        }

        static bool IsRunning(string type)
        {
            switch (type)
            {
                case "apache": return ServiceManager.Apache.IsActive;
                case "nginx": return ServiceManager.Nginx.IsActive;
                case "php": return ServiceManager.Php.IsActive;
                case "mysql":
                case "mariadb": return ServiceManager.MySql.IsActive && Settings.Current.DbEngine == type;
                case "postgresql": return ServiceManager.Postgres.IsActive;
                case "mongodb": return ServiceManager.Mongo.IsActive;
                case "memcached": return ServiceManager.Memcached.IsActive;
                case "redis": return ServiceManager.Redis.IsActive;
                case "mailpit":
                case "mailhog":
                case "smtp4dev": return ServiceManager.Mailpit.IsActive && Settings.Current.MailTool == type;
                default: return false;
            }
        }

        /// <summary>Add a version that is not in the catalog: pick a .zip (or an already extracted folder).</summary>
        async Task AddManual()
        {
            if (_busy) return;
            var type = ((Item)_filter.SelectedItem).Value;
            if (type == "" || type.StartsWith("cat:"))
            {
                var pick = new ManualPackageForm();
                if (await pick.ShowDialog<bool>(this) != true) return;
                type = pick.Type;
            }
            string source = null;
            var storage = StorageProvider;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = I18n.T("pkg.manualPick", PackageTypes.Get(type).Name),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Zip (*.zip)") { Patterns = new[] { "*.zip" } },
                    new FilePickerFileType(I18n.T("pkg.manualAnyFile") + " (*.*)") { Patterns = new[] { "*" } },
                },
            });
            if (files.Count > 0) source = files[0].TryGetLocalPath();
            if (source == null)
            {
                var dirs = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = I18n.T("pkg.manualFolder", PackageTypes.Get(type).Name), AllowMultiple = false });
                if (dirs.Count > 0) source = dirs[0].TryGetLocalPath();
            }
            if (source == null) return;
            _busy = true; UpdateButtons();
            _progress.IsVisible = true; _progress.IsIndeterminate = true;
            _status.Text = I18n.T("pkg.stage.extract") + "...";
            try
            {
                var folder = await Task.Run(() => Installer.InstallLocal(type, source));
                _status.Text = I18n.T("log.installed", PackageTypes.Get(type).Name + " " + PackageTypes.Label(folder));
                _changed = true;
            }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); _status.Text = ""; }
            _progress.IsVisible = false;
            _busy = false;
            Fill();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                if (_cts != null) _cts.Cancel();
                return;
            }
            base.OnClosing(e);
        }
    }

    /// <summary>Asks which kind of package is being added manually.</summary>
    public class ManualPackageForm : PortlyDialog
    {
        readonly ComboBox _type;
        readonly List<string> _ids = new List<string>();

        public string Type { get { return _type.SelectedIndex < 0 ? null : _ids[_type.SelectedIndex]; } }

        public ManualPackageForm() : base(I18n.T("pkg.addManual"), 460)
        {
            Body.Children.Add(Ui.Label(I18n.T("pkg.manualHelp"), "muted"));
            _type = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 420 };
            var names = new List<string>();
            foreach (var pt in PackageTypes.Ordered)
            {
                names.Add(pt.Name + "  (" + I18n.T("pkgcat." + pt.Category) + ")");
                _ids.Add(pt.Id);
            }
            _type.ItemsSource = names;
            _type.SelectedIndex = 0;
            Body.Children.Add(_type);
            Buttons.Children.Add(Ui.Button(I18n.T("common.ok"), () => Close(true), "primary"));
            Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(false)));
            KeyDown += (s, e) => { if (e.Key == Key.Enter) Close(true); };
        }

        public static Window Preview() { return new ManualPackageForm(); }
    }
}
