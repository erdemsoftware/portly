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
        }

        static readonly string[] FirstRunTypes = { "apache", "php", "mysql", "adminer", "composer", "mailpit" };

        readonly bool _firstRun;
        ComboBox _filter;
        ListView _list;
        ProgressBar _progress;
        Label _status;
        Button _install, _remove, _activate, _close, _refresh;
        CancellationTokenSource _cts;
        bool _busy;

        public PackagesForm(string type, bool firstRun)
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
            _filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(180), Margin = Ui.P(0, 3, 8, 0) };
            _filter.Items.Add(new Item(I18n.T("pkg.all"), ""));
            foreach (var t in PackageTypes.All) _filter.Items.Add(new Item(t.Name, t.Id));
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
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.ItemChecked += (s, e) => UpdateButtons();
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
            buttons.Controls.AddRange(new Control[] { _install, _activate, _remove, _close });
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(_status);
            bottom.Controls.Add(_progress);

            Controls.Add(listHost);
            Controls.Add(bottom);
            Controls.Add(top);
            listHost.BringToFront();

            Fill();
            if (firstRun) PreselectRecommended();
            UpdateButtons();
        }

        void Fill()
        {
            var filter = ((Item)_filter.SelectedItem).Value;
            var checkedKeys = new HashSet<string>(_list.CheckedItems.Cast<ListViewItem>().Select(i => i.Name));
            _list.BeginUpdate();
            _list.Items.Clear();
            var rows = new List<PkgRow>();
            foreach (var t in PackageTypes.All)
            {
                if (filter != "" && t.Id != filter) continue;
                var installed = PackageTypes.Installed(t.Id);
                foreach (var e in Catalog.ForType(t.Id))
                    rows.Add(new PkgRow { Type = t.Id, Folder = e.Folder, Entry = e, Installed = e.IsInstalled });
                foreach (var f in installed.Where(f => !rows.Any(r => r.Type == t.Id && string.Equals(r.Folder, f, StringComparison.OrdinalIgnoreCase))))
                    rows.Add(new PkgRow { Type = t.Id, Folder = f, Entry = Catalog.FindByFolder(t.Id, f), Installed = true });
            }
            foreach (var r in rows)
            {
                if (r.Installed && AppInfo.UpdatesSupported) r.Update = Catalog.UpdateFor(r.Type, r.Folder);
                var name = PackageTypes.Get(r.Type).Name;
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
                var it = new ListViewItem(new[] { name, version, note, status }) { Tag = r, Name = r.Type + "/" + r.Folder };
                if (r.Installed) it.ForeColor = r.Update != null ? Color.FromArgb(180, 83, 9) : Ui.Green;
                if (checkedKeys.Contains(it.Name)) it.Checked = true;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            UpdateButtons();
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
                    if (r.Installed) await Installer.UpdateAsync(r.Folder, entry, report, _cts.Token);
                    else await Installer.InstallAsync(entry, report, _cts.Token);
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
                case "mysql": return ServiceManager.MySql.IsActive;
                case "redis": return ServiceManager.Redis.IsActive;
                case "mailpit": return ServiceManager.Mailpit.IsActive;
                default: return false;
            }
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
}
