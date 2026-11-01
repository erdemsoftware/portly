using System;
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
        readonly TextBox _text;
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
            _text = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, AcceptsTab = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Ui.Mono, MaxLength = 0 };
            var host = new Panel { Dock = DockStyle.Fill, Padding = Ui.P(16, 4, 16, 4), BackColor = Ui.Bg };
            host.Controls.Add(_text);

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
                if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; await Save(); }
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
