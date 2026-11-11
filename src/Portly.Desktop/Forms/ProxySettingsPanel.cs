using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Portly.Core;
using Portly.Desktop.UI;

namespace Portly.Desktop.Forms
{
    /// <summary>Settings > General > Proxy: System (default) / None / Manual, with a "Test connection" button.</summary>
    public class ProxySettingsPanel : StackPanel
    {
        readonly ComboBox _mode;
        readonly TextBox _host, _user, _pass;
        readonly Control _manual;
        readonly TextBlock _result;
        readonly Button _test;

        public ProxySettingsPanel()
        {
            var s = Settings.Current;
            Spacing = 6;
            HorizontalAlignment = HorizontalAlignment.Left;

            _mode = FormKit.Combo(200);
            _mode.Items.Add(new Opt(I18n.T("proxy.system"), "system"));
            _mode.Items.Add(new Opt(I18n.T("proxy.none"), "none"));
            _mode.Items.Add(new Opt(I18n.T("proxy.manual"), "manual"));
            FormKit.Select(_mode, s.ProxyMode);
            _test = Ui.Button(I18n.T("proxy.test"), Test);
            Children.Add(Ui.Row(_mode, _test));

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, RowSpacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            _host = FormKit.Text(s.ProxyHost, 220);
            _user = FormKit.Text(s.ProxyUser, 160);
            _pass = FormKit.Text(s.ProxyPassword, 160, true);
            AddRow(grid, I18n.T("proxy.host"), _host);
            AddRow(grid, I18n.T("proxy.user"), _user);
            AddRow(grid, I18n.T("proxy.password"), _pass);
            _manual = grid;
            Children.Add(_manual);

            _result = Ui.Label("", "muted small");
            _result.MaxWidth = 380;
            Children.Add(_result);

            _mode.SelectionChanged += (o, e) => _manual.IsVisible = Mode == "manual";
            _manual.IsVisible = Mode == "manual";
        }

        static void AddRow(Grid g, string label, Control c)
        {
            int r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = Ui.Label(label, "muted small");
            l.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(l, r);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            g.Children.Add(l);
            g.Children.Add(c);
        }

        string Mode { get { return FormKit.Value(_mode); } }

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

        async Task Test()
        {
            var err = Validate();
            if (err != null) { _result.Text = err; _result.Foreground = Ui.Red; return; }
            _test.IsEnabled = false;
            _result.Foreground = Ui.Muted;
            _result.Text = I18n.T("proxy.testing");
            var proxy = Proxy.Create(Mode, _host.Text, _user.Text.Trim(), _pass.Text);
            var url = AppInfo.ManifestBase + "packages.json";
            var res = await Proxy.TestAsync(url, proxy);
            _result.Text = res == null ? I18n.T("proxy.ok") : res;
            _result.Foreground = res == null ? Ui.Green : Ui.Red;
            _test.IsEnabled = true;
        }

        public static Window Preview()
        {
            var w = new PreviewHost(I18n.T("proxy.title"));
            var p = new ProxySettingsPanel();
            FormKit.Select(p._mode, "manual");
            p._host.Text = "proxy.example.com:8080";
            p._user.Text = "dev";
            p._result.Text = I18n.T("proxy.ok");
            p._result.Foreground = Ui.Green;
            w.Add(p);
            return w;
        }

        sealed class PreviewHost : PortlyDialog
        {
            public PreviewHost(string title) : base(title, 480) { }
            public void Add(Control c) { Body.Children.Add(c); }
        }
    }
}
