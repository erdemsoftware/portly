using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;

namespace Portly.UI
{
    /// <summary>
    /// Hosts file editor inside Portly: reading needs no rights, saving asks for UAC once. Notepad is not used because
    /// Windows 11 hands it to the Store app, so opening it as administrator depends on that app.
    /// </summary>
    public class HostsForm : Form
    {
        readonly TextBox _text, _find;
        readonly Label _count;
        readonly ModernButton _save, _close;
        string _loaded;
        bool _busy;

        public HostsForm()
        {
            Ui.Apply(this);
            Text = I18n.T("hosts.editTitle");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            KeyPreview = true;
            Size = Ui.S(760, 560);
            MinimumSize = Ui.S(520, 360);

            var top = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = Ui.P(16, 14, 16, 6), BackColor = Ui.Bg };
            top.Controls.Add(new Label { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(Ui.S(710), 0), ForeColor = Ui.Muted, Text = I18n.T("hosts.editIntro", HostsFile.FilePath) });

            // MaxLength 0: a multiline box stops typing at 32 KB otherwise, and hosts files can be bigger
            // HideSelection off: the match stays visible while the focus is in the search box
            _text = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, AcceptsTab = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Ui.Mono, MaxLength = 0, HideSelection = false };
            var host = new Panel { Dock = DockStyle.Fill, Padding = Ui.P(16, 4, 16, 4), BackColor = Ui.Bg };
            host.Controls.Add(_text);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Padding = Ui.P(0, 0, 0, 6) };
            bar.Controls.Add(Ui.Label(I18n.T("hosts.find")));
            _find = new TextBox { Width = Ui.S(220), Margin = Ui.P(0, 4, 8, 0) };
            bar.Controls.Add(_find);
            bar.Controls.Add(FindButton("▲", "hosts.findPrev", false));
            bar.Controls.Add(FindButton("▼", "hosts.findNext", true));
            _count = Ui.Label("", Ui.Small, Ui.Muted);
            _count.Margin = Ui.P(4, 8, 0, 4);
            bar.Controls.Add(_count);
            host.Controls.Add(bar);

            var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = Ui.P(16, 6, 16, 12), BackColor = Ui.Bg };
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            _save = Ui.Button(I18n.T("common.save"), async (s, e) => await Save(), true);
            _close = Ui.Button(I18n.T("common.close"), (s, e) => Close());
            row.Controls.AddRange(new Control[] { _save, _close });
            bottom.Controls.Add(row);

            Controls.Add(host);
            Controls.Add(bottom);
            Controls.Add(top);
            host.BringToFront();

            KeyDown += async (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; _find.Focus(); _find.SelectAll(); }
                else if (e.KeyCode == Keys.F3) { e.SuppressKeyPress = true; Find(!e.Shift); }
                else if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; await Save(); }
            };
            _find.TextChanged += (s, e) => Find(true, true);
            _text.TextChanged += (s, e) => ShowCount(FindAll(_text.Text, _find.Text));
            _find.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Find(!e.Shift); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _text.Focus(); }
            };
            FormClosing += (s, e) =>
            {
                if (_busy) { e.Cancel = true; return; }
                if (Changed && !MessageDialog.Confirm(this, I18n.T("hosts.unsaved"))) e.Cancel = true;
            };
            Load += (s, e) => Reload();
            Shown += (s, e) => { _text.SelectionLength = 0; _text.Focus(); };
        }

        bool Changed { get { return _loaded != null && _text.Text != _loaded; } }

        ModernButton FindButton(string arrow, string key, bool forward)
        {
            var b = Ui.Button(arrow, (s, e) => Find(forward));
            b.Padding = Ui.P(8, 6, 8, 6);
            b.Margin = Ui.P(0, 0, 4, 0);
            b.AccessibleName = I18n.T(key);
            return b;
        }

        /// <summary>Where every match starts, ignoring case.</summary>
        public static List<int> FindAll(string text, string query)
        {
            var all = new List<int>();
            if (string.IsNullOrEmpty(query)) return all;
            for (var i = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(query, i + query.Length, StringComparison.OrdinalIgnoreCase))
                all.Add(i);
            return all;
        }

        /// <summary>The match to go to from a position, wrapping around at either end; -1 without matches.</summary>
        public static int FindStep(List<int> all, int from, bool forward)
        {
            if (all.Count == 0) return -1;
            if (forward)
            {
                foreach (var at in all) if (at >= from) return at;
                return all[0];
            }
            for (var i = all.Count - 1; i >= 0; i--) if (all[i] < from) return all[i];
            return all[all.Count - 1];
        }

        /// <summary>stay: typing keeps a match that still fits instead of moving on to the next one.</summary>
        void Find(bool forward, bool stay = false)
        {
            var query = _find.Text;
            if (query.Length == 0 && !stay) { _find.Focus(); return; }
            var all = FindAll(_text.Text, query);
            var at = FindStep(all, _text.SelectionStart + (forward && !stay && _text.SelectionLength > 0 ? 1 : 0), forward);
            if (at >= 0)
            {
                _text.Select(at, query.Length);
                _text.ScrollToCaret();
            }
            ShowCount(all);
        }

        void ShowCount(List<int> all)
        {
            var query = _find.Text;
            if (query.Length == 0) _count.Text = "";
            else if (all.Count == 0) _count.Text = I18n.T("hosts.noMatch");
            else
            {
                var i = _text.SelectionLength == query.Length ? all.IndexOf(_text.SelectionStart) : -1;
                _count.Text = (i >= 0 ? (i + 1) + " / " : "") + all.Count;
            }
        }

        /// <summary>Without a readable file the box stays locked: saving an empty text would wipe it.</summary>
        void Reload()
        {
            try
            {
                _loaded = Lines(HostsFile.ReadAll());
                _text.Text = _loaded;
            }
            catch (Exception ex)
            {
                _loaded = null;
                _text.ReadOnly = true;
                _save.Enabled = false;
                MessageDialog.Warning(this, I18n.T("hosts.readFailed", ex.Message));
            }
        }

        /// <summary>The box needs CRLF to show line breaks.</summary>
        static string Lines(string s) { return s.Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        async Task Save()
        {
            if (_busy || _loaded == null) return;
            if (!Changed) { Close(); return; }
            string disk;
            try { disk = Lines(HostsFile.ReadAll()); }
            catch (Exception ex) { MessageDialog.Warning(this, I18n.T("hosts.readFailed", ex.Message)); return; }
            // Portly's own sync or another program wrote in the meantime: do not overwrite that with an old copy
            if (disk != _loaded && !MessageDialog.Confirm(this, I18n.T("hosts.changedOnDisk"))) return;

            var content = _text.Text;
            _busy = true;
            _save.Enabled = _close.Enabled = _text.Enabled = false;
            int code;
            try { code = await Task.Run(() => HostsFile.SaveAll(content)); }
            catch (Exception ex) { Log.Error("Hosts save", ex); code = -1; }
            _busy = false;
            _save.Enabled = _close.Enabled = _text.Enabled = true;

            if (code == 0)
            {
                _loaded = content;
                Log.Info(I18n.T("log.hostsUpdated"));
                Close();
            }
            else if (code == -2) MessageDialog.Warning(this, I18n.T("hosts.saveCancelled"));
            else
            {
                Log.Warn(I18n.T("hosts.saveFailed", code));
                MessageDialog.Warning(this, I18n.T("hosts.saveFailed", code));
            }
        }
    }
}
