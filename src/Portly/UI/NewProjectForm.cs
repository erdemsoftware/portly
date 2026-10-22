using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Portly.Core;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    public class NewProjectForm : Form
    {
        readonly TextBox _name;
        readonly ThemedCombo _template;
        readonly CheckBox _db, _open;
        readonly Label _status, _url;
        readonly Button _create, _cancel;
        CancellationTokenSource _cts;

        public string CreatedName { get; private set; }
        public bool OpenAfter { get { return _open.Checked; } }

        public NewProjectForm()
        {
            Ui.Apply(this);
            Text = I18n.T("project.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(16);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            t.Controls.Add(Ui.Label(I18n.T("project.name"), Ui.Bold));
            _name = new TextBox { Width = Ui.S(360), Margin = Ui.P(0, 2, 0, 2) };
            t.Controls.Add(_name);
            _url = Ui.Label(" ", Ui.Small, Ui.Muted);
            t.Controls.Add(_url);
            _name.TextChanged += (s, e) => UpdateUrl();

            t.Controls.Add(Ui.Label(I18n.T("project.template"), Ui.Bold));
            _template = new ThemedCombo { Width = Ui.S(360), Margin = Ui.P(0, 2, 0, 2) };
            _template.Items.Add(new Item(I18n.T("project.blank"), "blank"));
            _template.Items.Add(new Item(I18n.T("project.wordpress"), "wordpress"));
            foreach (var kv in ProjectCreator.ComposerTemplates)
                _template.Items.Add(new Item(kv.Value.Item1 + "  ·  " + I18n.T("project.viaComposer"), kv.Key));
            _template.SelectedIndex = 0;
            t.Controls.Add(_template);

            _db = new CheckBox { Text = I18n.T("project.createDb"), AutoSize = true, Checked = true, Margin = Ui.P(0, 10, 0, 0) };
            _open = new CheckBox { Text = I18n.T("project.openAfter"), AutoSize = true, Checked = true };
            t.Controls.Add(_db);
            t.Controls.Add(_open);

            _status = new Label { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(Ui.S(360), 0), Margin = Ui.P(0, 10, 0, 0) };
            t.Controls.Add(_status);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 12, 0, 0) };
            _cancel = Ui.Button(I18n.T("common.cancel"), (s, e) => { if (_cts != null) _cts.Cancel(); else Close(); });
            _create = Ui.Button(I18n.T("project.create"), (s, e) => Create(), true);
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_create);
            t.Controls.Add(buttons);
            Controls.Add(t);
            AcceptButton = _create;
            UpdateUrl();
        }

        void UpdateUrl()
        {
            var n = _name.Text.Trim();
            var s = Settings.Current;
            _url.Text = n == "" ? I18n.T("project.urlHint", s.Tld) : (s.Ssl ? "https://" : "http://") + n.ToLowerInvariant() + "." + s.Tld;
            _url.ForeColor = n == "" || Config.Sites.IsValidName(n) ? Ui.Muted : Ui.Red;
        }

        async void Create()
        {
            var name = _name.Text.Trim();
            var err = ProjectCreator.ValidateName(name);
            if (err != null) { Ui.Error(this, err); return; }
            var template = ((Item)_template.SelectedItem).Value;
            if (_db.Checked && ServiceManager.MySql.State != ServiceState.Running)
            {
                if (!Ui.Confirm(this, I18n.T("project.mysqlOff"))) return;
                _db.Checked = false;
            }
            _create.Enabled = false;
            _cts = new CancellationTokenSource();
            try
            {
                var msg = await ProjectCreator.CreateAsync(name, template, _db.Checked, s => BeginInvoke((Action)(() => _status.Text = s)), _cts.Token);
                CreatedName = name;
                Ui.Info(this, msg);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException) { _status.Text = I18n.T("pkg.cancelled"); }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
            finally
            {
                _cts = null;
                if (!IsDisposed) _create.Enabled = true;
            }
        }
    }
}
