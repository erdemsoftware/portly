using System;
using System.Collections.Generic;
using System.Drawing;
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
    /// <summary>Install / update / remove packages. In first-run mode the recommended set is pre-selected.</summary>
    public class PackagesForm : Form
    {
        class PkgRow
        {
            public string Type;
            public string Folder;
            public PackageEntry Entry;   // catalog entry (null for a manually added folder)
            public PackageEntry Update;  // newer build of the same branch
            public bool Installed;
            public string SystemExe;     // installed on Windows outside Portly (VS Code, Git ...): used as is
        }

        static readonly string[] FirstRunTypes = { "apache", "php", "mysql", "phpmyadmin", "composer", "mailpit", "mailhog" };

        readonly bool _firstRun;
        ComboBox _filter;
        ListView _list;
        ProgressBar _progress;
        Label _status;
        Button _install, _remove, _activate, _close, _refresh;
        CancellationTokenSource _cts;
        bool _busy;

        public PackagesForm(string type, bool firstRun, IEnumerable<PackageEntry> preselect = null)
        {
            _firstRun = firstRun;
            Ui.Apply(this);
            Text = I18n.T(firstRun ? "pkg.firstRunTitle" : "pkg.title");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = Ui.S(760, 560);
            MinimumSize = Ui.S(640, 420);

            var top = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = Ui.P(14, 12, 14, 6), BackColor = Ui.Bg };
            var intro = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(Ui.S(720), 0), Padding = Ui.P(0, 0, 0, 8),
                Text = firstRun ? I18n.T("pkg.firstRunIntro") : (AppInfo.IsLegacy ? I18n.T("pkg.legacyNote") : I18n.T("pkg.intro", Catalog.Updated)),
            };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            _filter = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(220), Margin = Ui.P(0, 3, 8, 0), MaxDropDownItems = 20 };
            _filter.Items.Add(new Item(I18n.T("pkg.all"), ""));
            // a category ("cat:db") and, indented below it, its package types
            foreach (var c in PackageTypes.Categories)
            {
                _filter.Items.Add(new Item(I18n.T("pkgcat." + c), "cat:" + c));
                foreach (var t in PackageTypes.Ordered.Where(x => x.Category == c)) _filter.Items.Add(new Item("      " + t.Name, t.Id));
            }
            _filter.SelectedItem = _filter.Items.Cast<Item>().FirstOrDefault(i => i.Value == (type ?? "")) ?? _filter.Items[0];
            _filter.SelectedIndexChanged += (s, e) => Fill();
            bar.Controls.Add(Ui.Label(I18n.T("pkg.show")));
            bar.Controls.Add(_filter);
            if (AppInfo.UpdatesSupported)
            {
                _refresh = Ui.Button(I18n.T("pkg.refresh"), async (s, e) => await RefreshCatalog());
                bar.Controls.Add(_refresh);
            }
            top.Controls.Add(bar);
            top.Controls.Add(intro);

            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = BorderStyle.FixedSingle };
            _list.Columns.Add(I18n.T("col.package"), Ui.S(120));
            _list.Columns.Add(I18n.T("col.version"), Ui.S(110));
            _list.Columns.Add(I18n.T("col.note"), Ui.S(250));
            _list.Columns.Add(I18n.T("col.status"), Ui.S(220));
            // themed column headers (the native header stays light in dark mode)
            _list.OwnerDraw = true;
            _list.DrawItem += (s, e) => e.DrawDefault = true;
            _list.DrawSubItem += (s, e) => e.DrawDefault = true;
            _list.DrawColumnHeader += (s, e) =>
            {
                var p = Theme.Current;
                using (var b = new SolidBrush(p.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var pen = new Pen(p.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Ui.Bold, new Rectangle(e.Bounds.X + Ui.S(6), e.Bounds.Y, e.Bounds.Width - Ui.S(6), e.Bounds.Height), p.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            };
            if (Theme.IsDark)
            {
                _list.BorderStyle = BorderStyle.None;
                // the Windows dark list theme draws readable group titles (the default ones are dark blue)
                _list.HandleCreated += (s, e) => { try { SetWindowTheme(_list.Handle, "DarkMode_ItemsView", null); } catch { } };
            }
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.ItemChecked += (s, e) => UpdateButtons();
            // nothing to install for a program that is already on Windows
            _list.ItemCheck += (s, e) => { if (((PkgRow)_list.Items[e.Index].Tag).SystemExe != null) e.NewValue = CheckState.Unchecked; };
            var listHost = new Panel { Dock = DockStyle.Fill, Padding = Ui.P(14, 0, 14, 0), BackColor = Ui.Bg };
            listHost.Controls.Add(_list);

            var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = Ui.P(14, 8, 14, 12), BackColor = Ui.Bg };
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = Ui.S(8), Visible = false, Style = ProgressBarStyle.Continuous };
            _status = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Ui.Muted, Padding = Ui.P(0, 6, 0, 6), MaximumSize = new Size(Ui.S(720), 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            _install = Ui.Button(I18n.T("pkg.installSelected"), async (s, e) => await InstallChecked(), true);
            _remove = Ui.Button(I18n.T("pkg.remove"), (s, e) => RemoveSelected());
            _activate = Ui.Button(I18n.T("pkg.activate"), async (s, e) => await ActivateSelected());
            _close = Ui.Button(I18n.T("common.close"), (s, e) => { if (_busy) { if (_cts != null) _cts.Cancel(); } else Close(); });
            var manual = Ui.Button(I18n.T("pkg.addManual"), (s, e) => AddManual(), false, "plus");
            var folder = Ui.Button(I18n.T("pkg.openBin"), (s, e) => WinUtil.OpenFolder(Paths.Bin), false, "folder");
            buttons.Controls.AddRange(new Control[] { _install, _activate, _remove, manual, folder, _close });
            if (Features.IsOn("extensions"))
            {
                var exts = Ui.Button(I18n.T("ext.menu"), (s, e) => { using (var f = new PhpExtensionsForm()) f.ShowDialog(this); });
                buttons.Controls.Add(exts);
                buttons.Controls.SetChildIndex(exts, 4);
            }
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(_status);
            bottom.Controls.Add(_progress);

            Controls.Add(listHost);
            Controls.Add(bottom);
            Controls.Add(top);
            listHost.BringToFront();

            Fill();
            if (firstRun) PreselectRecommended();
            if (preselect != null)
            {
                // e.g. the packages of an imported setup: ready to install with one click
                var keys = new HashSet<string>(preselect.Select(p => p.Type + "/" + p.Folder), StringComparer.OrdinalIgnoreCase);
                foreach (ListViewItem it in _list.Items) if (keys.Contains(it.Name)) it.Checked = true;
            }
            UpdateButtons();
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        void Fill()
        {
            var filter = ((Item)_filter.SelectedItem).Value;
            var checkedKeys = new HashSet<string>(_list.CheckedItems.Cast<ListViewItem>().Select(i => i.Name));
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.Groups.Clear();
            // one titled group per category: Web server / PHP / Databases / Database tools / Mail ...
            foreach (var c in PackageTypes.Categories) _list.Groups.Add(new ListViewGroup(c, I18n.T("pkgcat." + c)));
            var rows = new List<PkgRow>();
            foreach (var t in PackageTypes.Ordered)
            {
                if (filter.StartsWith("cat:") ? t.Category != filter.Substring(4) : filter != "" && t.Id != filter) continue;
                var installed = PackageTypes.Installed(t.Id);
                foreach (var e in Catalog.ForType(t.Id))
                    rows.Add(new PkgRow { Type = t.Id, Folder = e.Folder, Entry = e, Installed = e.IsInstalled });
                foreach (var f in installed.Where(f => !rows.Any(r => r.Type == t.Id && string.Equals(r.Folder, f, StringComparison.OrdinalIgnoreCase))))
                    rows.Add(new PkgRow { Type = t.Id, Folder = f, Entry = Catalog.FindByFolder(t.Id, f), Installed = true });
                var sys = installed.Count == 0 ? DesktopApps.SystemExe(t.Id) : null;
                if (sys != null)
                {
                    // above the downloadable versions of the same program
                    int at = rows.FindIndex(r => r.Type == t.Id);
                    rows.Insert(at < 0 ? rows.Count : at, new PkgRow { Type = t.Id, Folder = "", SystemExe = sys });
                }
            }
            foreach (var r in rows)
            {
                if (r.Installed && AppInfo.UpdatesSupported) r.Update = Catalog.UpdateFor(r.Type, r.Folder);
                var name = PackageTypes.Get(r.Type).Name;
                if (r.SystemExe != null)
                {
                    var sysItem = new ListViewItem(new[] { name, SystemVersion(r.SystemExe), I18n.T("pkg.onSystem"), I18n.T("pkg.inUse", r.SystemExe) })
                        { Tag = r, Name = r.Type + "/system", Group = _list.Groups[PackageTypes.Get(r.Type).Category], ForeColor = Ui.Green };
                    _list.Items.Add(sysItem);
                    continue;
                }
                var version = r.Entry != null ? r.Entry.Version : PackageTypes.Label(r.Folder);
                var note = r.Entry == null ? I18n.T("pkg.manual") : JoinNote(r.Entry);
                string status;
                if (r.Installed)
                {
                    bool active = string.Equals(PackageTypes.ActiveFolder(r.Type), r.Folder, StringComparison.OrdinalIgnoreCase);
                    status = active ? I18n.T("pkg.installedActive") : I18n.T("pkg.installed");
                    if (r.Update != null) status += " - " + I18n.T("pkg.updateTo", r.Update.Version);
                }
                else status = "";
                var it = new ListViewItem(new[] { name, version, note, status }) { Tag = r, Name = r.Type + "/" + r.Folder, Group = _list.Groups[PackageTypes.Get(r.Type).Category] };
                if (r.Installed) it.ForeColor = r.Update != null ? Color.FromArgb(180, 83, 9) : Ui.Green;
                if (checkedKeys.Contains(it.Name)) it.Checked = true;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            UpdateButtons();
        }

        static string SystemVersion(string exe)
        {
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
            foreach (ListViewItem it in _list.Items)
            {
                var r = (PkgRow)it.Tag;
                if (r.Installed || r.Entry == null || !r.Entry.Recommended) continue;
                if (!FirstRunTypes.Contains(r.Type)) continue;
                if (PackageTypes.Installed(r.Type).Count > 0) continue;
                it.Checked = true;
            }
        }

        PkgRow Selected { get { return _list.SelectedItems.Count == 0 ? null : (PkgRow)_list.SelectedItems[0].Tag; } }

        void UpdateButtons()
        {
            if (_install == null) return;
            var sel = Selected;
            int count = _list.CheckedItems.Cast<ListViewItem>().Count(i => { var r = (PkgRow)i.Tag; return !r.Installed || r.Update != null; });
            _install.Text = I18n.T("pkg.installSelected") + (count > 0 ? " (" + count + ")" : "");
            _install.Enabled = !_busy && count > 0;
            _remove.Enabled = !_busy && sel != null && sel.Installed;
            _activate.Enabled = !_busy && sel != null && sel.Installed && !string.Equals(PackageTypes.ActiveFolder(sel.Type), sel.Folder, StringComparison.OrdinalIgnoreCase);
            _close.Text = _busy ? I18n.T("common.cancel") : I18n.T("common.close");
            _filter.Enabled = !_busy;
            if (_refresh != null) _refresh.Enabled = !_busy;
        }

        async Task RefreshCatalog()
        {
            _busy = true; UpdateButtons();
            _status.Text = I18n.T("pkg.refreshing");
            var ok = await Catalog.RefreshAsync();
            _status.Text = ok ? I18n.T("pkg.refreshed", Catalog.Updated) : I18n.T("pkg.refreshFailed");
            _busy = false;
            Fill();
        }

        async Task InstallChecked()
        {
            var jobs = _list.CheckedItems.Cast<ListViewItem>().Select(i => (PkgRow)i.Tag).Where(r => !r.Installed || r.Update != null).ToList();
            if (jobs.Count == 0) return;
            _busy = true;
            _cts = new CancellationTokenSource();
            UpdateButtons();
            _progress.Visible = true;
            int done = 0, failed = 0;
            var phpCopies = new List<Tuple<string, string>>();   // (extensions from, to) for new / updated PHP versions
            foreach (var r in jobs)
            {
                var entry = r.Installed ? r.Update : r.Entry;
                if (entry == null) continue;
                var label = entry.TypeName + " " + entry.Version;
                try
                {
                    Action<InstallProgress> report = p => BeginInvoke((Action)(() =>
                    {
                        _progress.Style = p.Percent < 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                        if (p.Percent >= 0) _progress.Value = Math.Min(100, p.Percent);
                        var stage = I18n.T("pkg.stage." + p.Stage);
                        _status.Text = "[" + (done + failed + 1) + "/" + jobs.Count + "] " + label + " - " + stage + (p.Stage == "download" ? " " + (p.Percent >= 0 ? p.Percent + "%" : Ui.FormatBytes(p.Bytes)) : "");
                    }));
                    var phpFrom = r.Installed ? r.Folder : PackageTypes.ActiveFolder("php");
                    if (r.Installed) await Installer.UpdateAsync(r.Folder, entry, report, _cts.Token);
                    else await Installer.InstallAsync(entry, report, _cts.Token);
                    if (entry.Type == "php") phpCopies.Add(Tuple.Create(phpFrom, entry.Folder));
                    done++;
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
                    Ui.Error(this, label + "\n\n" + ex.Message);
                }
            }
            _progress.Visible = false;
            _busy = false;
            foreach (ListViewItem it in _list.Items) it.Checked = false;
            Fill();
            Terminal.ApplyUserPath();
            foreach (var c in phpCopies) await PhpExtensionsForm.OfferCopyAsync(this, c.Item1, c.Item2);
            if (!_cts.IsCancellationRequested) _status.Text = I18n.T("pkg.finished", done, failed);
            if (_firstRun && done > 0 && failed == 0)
            {
                Ui.Info(this, I18n.T("pkg.firstRunDone"));
                Close();
            }
        }

        async Task ActivateSelected()
        {
            var r = Selected;
            if (r == null) return;
            Settings.Current.SetVersion(r.Type, r.Folder);
            Settings.Current.Save();
            Log.Info(I18n.T("log.switched", PackageTypes.Get(r.Type).Name, PackageTypes.Label(r.Folder)));
            Fill();
            if (r.Type == "mysql" && ServiceManager.MySql.IsActive) await ServiceManager.MySql.RestartAsync();
            else if (r.Type == "redis" && ServiceManager.Redis.IsActive) await ServiceManager.Redis.RestartAsync();
            else await ServiceManager.ReloadWebAsync();
        }

        void RemoveSelected()
        {
            var r = Selected;
            if (r == null || !r.Installed) return;
            bool active = string.Equals(PackageTypes.ActiveFolder(r.Type), r.Folder, StringComparison.OrdinalIgnoreCase);
            if (active && IsRunning(r.Type)) { Ui.Error(this, I18n.T("pkg.stopFirst")); return; }
            var msg = I18n.T("pkg.removeConfirm", PackageTypes.Get(r.Type).Name + " " + PackageTypes.Label(r.Folder));
            if (r.Type == "mysql") msg += "\n\n" + I18n.T("pkg.mysqlDataKept");
            if (!Ui.Confirm(this, msg)) return;
            try { Installer.Remove(r.Type, r.Folder); }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
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
        async void AddManual()
        {
            if (_busy) return;
            var type = ((Item)_filter.SelectedItem).Value;
            if (type == "" || type.StartsWith("cat:"))
            {
                using (var pick = new ManualPackageForm()) { if (pick.ShowDialog(this) != DialogResult.OK) return; type = pick.Type; }
            }
            string source = null;
            using (var dlg = new OpenFileDialog { Title = I18n.T("pkg.manualPick", PackageTypes.Get(type).Name), Filter = "Zip (*.zip)|*.zip|" + I18n.T("pkg.manualAnyFile") + " (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) source = dlg.FileName;
            }
            if (source == null)
            {
                using (var fb = new FolderBrowserDialog { Description = I18n.T("pkg.manualFolder", PackageTypes.Get(type).Name) })
                    if (fb.ShowDialog(this) == DialogResult.OK) source = fb.SelectedPath;
            }
            if (source == null) return;
            _busy = true; UpdateButtons();
            _progress.Visible = true; _progress.Style = ProgressBarStyle.Marquee;
            _status.Text = I18n.T("pkg.stage.extract") + "...";
            try
            {
                var folder = await Task.Run(() => Installer.InstallLocal(type, source));
                _status.Text = I18n.T("log.installed", PackageTypes.Get(type).Name + " " + PackageTypes.Label(folder));
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); _status.Text = ""; }
            _progress.Visible = false;
            _busy = false;
            Fill();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                if (_cts != null) _cts.Cancel();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    /// <summary>Asks which kind of package is being added manually.</summary>
    public class ManualPackageForm : Form
    {
        readonly ThemedCombo _type;
        public string Type { get { return ((Item)_type.SelectedItem).Value; } }

        public ManualPackageForm()
        {
            Ui.Apply(this);
            Text = I18n.T("pkg.addManual");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(18);
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var help = Ui.Label(I18n.T("pkg.manualHelp"), Ui.Font, Ui.Muted);
            help.MaximumSize = new Size(Ui.S(420), 0);
            t.Controls.Add(help);
            _type = new ThemedCombo { Width = Ui.S(220), Margin = Ui.P(0, 10, 0, 0) };
            foreach (var pt in PackageTypes.Ordered) _type.Items.Add(new Item(pt.Name + "  (" + I18n.T("pkgcat." + pt.Category) + ")", pt.Id));
            _type.SelectedIndex = 0;
            t.Controls.Add(_type);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 14, 0, 0) };
            var cancel = Ui.Button(I18n.T("common.cancel"), null);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Margin = Padding.Empty;
            var ok = Ui.Button(I18n.T("common.ok"), null, true);
            ok.DialogResult = DialogResult.OK;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            t.Controls.Add(buttons);
            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
