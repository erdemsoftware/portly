using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Core;
using Portly.Packages;

namespace Portly.UI
{
    /// <summary>Packages > Remove unused...: versions that are neither active nor chosen by a site, with their sizes.</summary>
    public class CleanupForm : Form
    {
        readonly ListView _list;
        readonly Label _info;
        readonly ModernButton _remove;
        public int Removed;

        public CleanupForm()
        {
            Ui.Apply(this);
            Text = I18n.T("cleanup.title");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = Ui.S(560, 460);
            MinimumSize = Ui.S(460, 360);

            var intro = new Label { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(Ui.S(520), 0), Padding = Ui.P(16, 14, 16, 8), Text = I18n.T("cleanup.intro") };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, BorderStyle = BorderStyle.FixedSingle };
            _list.Columns.Add(I18n.T("col.package"), Ui.S(260));
            _list.Columns.Add(I18n.T("cleanup.size"), Ui.S(110), HorizontalAlignment.Right);
            _list.ItemChecked += (s, e) => UpdateInfo();
            var host = new Panel { Dock = DockStyle.Fill, Padding = Ui.P(16, 0, 16, 0), BackColor = Ui.Bg };
            host.Controls.Add(_list);

            var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = Ui.P(16, 8, 16, 12), BackColor = Ui.Bg };
            _info = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Ui.Muted, Padding = Ui.P(0, 0, 0, 8) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            _remove = Ui.Button(I18n.T("cleanup.remove"), async (s, e) => await RemoveChecked(), true);
            var close = Ui.Button(I18n.T("common.close"), (s, e) => Close());
            buttons.Controls.Add(_remove);
            buttons.Controls.Add(close);
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(_info);

            Controls.Add(host);
            Controls.Add(bottom);
            Controls.Add(intro);
            host.BringToFront();
            CancelButton = close;
            Shown += async (s, e) => await Fill();
        }

        async Task Fill()
        {
            _info.Text = I18n.T("cleanup.scanning");
            _remove.Enabled = false;
            var items = await Task.Run(() => Cleanup.Find());
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var u in items.OrderBy(x => x.Type).ThenBy(x => x.Folder))
                _list.Items.Add(new ListViewItem(new[] { u.Title, Ui.FormatBytes(u.Size) }) { Tag = u });
            _list.EndUpdate();
            if (items.Count == 0) _info.Text = I18n.T("cleanup.none");
            UpdateInfo();
        }

        void UpdateInfo()
        {
            var sel = Checked();
            _remove.Enabled = sel.Count > 0;
            if (_list.Items.Count > 0) _info.Text = I18n.T("cleanup.selected", sel.Count, Ui.FormatBytes(sel.Sum(x => x.Size)));
        }

        List<UnusedVersion> Checked() { return _list.CheckedItems.Cast<ListViewItem>().Select(i => (UnusedVersion)i.Tag).ToList(); }

        async Task RemoveChecked()
        {
            var sel = Checked();
            if (sel.Count == 0) return;
            if (!Ui.Confirm(this, I18n.T("cleanup.confirm", sel.Count, Ui.FormatBytes(sel.Sum(x => x.Size))))) return;
            _remove.Enabled = false;
            var skipped = new List<string>();
            await Task.Run(() =>
            {
                foreach (var u in sel)
                {
                    // never remove a version while one of its programs runs
                    if (Cleanup.IsInUse(u.Dir)) { skipped.Add(u.Title); continue; }
                    try { Installer.Remove(u.Type, u.Folder); Removed++; }
                    catch (Exception ex) { skipped.Add(u.Title + ": " + ex.Message); }
                }
            });
            if (skipped.Count > 0) Ui.Error(this, I18n.T("cleanup.skipped", string.Join("\n", skipped)));
            await Fill();
        }
    }
}
