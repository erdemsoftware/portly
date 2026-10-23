using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Portly.Core;

namespace Portly.UI
{
    /// <summary>Settings > General > Proxy: System (default) / None / Manual, with a "Test connection" button.</summary>
    public class ProxySettingsPanel : TableLayoutPanel
    {
        readonly ThemedCombo _mode;
        readonly TextBox _host, _user, _pass;
        readonly Panel _manual;
        readonly Label _result;
        readonly ModernButton _test;

        public ProxySettingsPanel()
        {
            var s = Settings.Current;
            AutoSize = true;
            ColumnCount = 1;
            BackColor = Color.Transparent;
            Margin = Padding.Empty;

            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
            _mode = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(200), Margin = Ui.P(0, 4, 8, 0) };
            _mode.Items.Add(new Item(I18n.T("proxy.system"), "system"));
            _mode.Items.Add(new Item(I18n.T("proxy.none"), "none"));
            _mode.Items.Add(new Item(I18n.T("proxy.manual"), "manual"));
            _mode.SelectedItem = _mode.Items.Cast<Item>().First(i => i.Value == s.ProxyMode);
            _test = Ui.Button(I18n.T("proxy.test"), async (o, e) => await Test());
            line.Controls.Add(_mode);
            line.Controls.Add(_test);
            Controls.Add(line);

            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent, Margin = Ui.P(0, 6, 0, 0) };
            _host = new TextBox { Text = s.ProxyHost, Width = Ui.S(220) };
            _user = new TextBox { Text = s.ProxyUser, Width = Ui.S(160) };
            _pass = new TextBox { Text = s.ProxyPassword, Width = Ui.S(160), UseSystemPasswordChar = true };
            AddRow(grid, I18n.T("proxy.host"), _host);
            AddRow(grid, I18n.T("proxy.user"), _user);
            AddRow(grid, I18n.T("proxy.password"), _pass);
            _manual = new Panel { AutoSize = true, BackColor = Color.Transparent, Margin = Padding.Empty };
            _manual.Controls.Add(grid);
            Controls.Add(_manual);

            _result = Ui.Label("", Ui.Small, Ui.Muted);
            _result.MaximumSize = new Size(Ui.S(380), 0);
            Controls.Add(_result);

            _mode.SelectedIndexChanged += (o, e) => _manual.Visible = Mode == "manual";
            _manual.Visible = Mode == "manual";
        }

        static void AddRow(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++;
            var l = Ui.Label(label, Ui.Small, Ui.Muted);
            l.Margin = Ui.P(0, 6, 10, 2);
            c.Margin = Ui.P(0, 2, 0, 2);
            t.Controls.Add(l, 0, r);
            t.Controls.Add(c, 1, r);
        }

        string Mode { get { return ((Item)_mode.SelectedItem).Value; } }

        /// <summary>null when valid, otherwise the error to show.</summary>
        public string Validate()
        {
            if (Mode == "manual" && Proxy.ManualUri(_host.Text) == null) return I18n.T("proxy.badHost");
            return null;
        }

        public void Save(Settings s)
        {
            s.ProxyMode = Mode;
            s.ProxyHost = _host.Text;
            s.ProxyUser = _user.Text.Trim();
            s.ProxyPassword = _pass.Text;
        }

        async System.Threading.Tasks.Task Test()
        {
            var err = Validate();
            if (err != null) { _result.Text = err; _result.ForeColor = Ui.Red; return; }
            _test.Enabled = false;
            _result.ForeColor = Ui.Muted;
            _result.Text = I18n.T("proxy.testing");
            var proxy = Proxy.Create(Mode, _host.Text, _user.Text.Trim(), _pass.Text);
            var url = AppInfo.ManifestBase + "packages.json";
            var res = await Proxy.TestAsync(url, proxy);
            _result.Text = res == null ? I18n.T("proxy.ok") : res;
            _result.ForeColor = res == null ? Ui.Green : Ui.Red;
            _test.Enabled = true;
        }
    }
}
