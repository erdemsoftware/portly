using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>Feature "dumps": site menu > Profiler... Switch profiling on for the site's PHP, list profiles, show the slowest functions.</summary>
    public class ProfilerForm : PortlyDialog
    {
        readonly Site _site;
        readonly string _phpDir;
        readonly GridList _files, _funcs;
        readonly TextBlock _state, _summary;
        readonly Button _toggle;
        bool _preview;

        public ProfilerForm(Site site) : base(I18n.T("profiler.title", site.Name), 980)
        {
            _site = site;
            _phpDir = site.PhpFolder == "" ? null : Path.Combine(Paths.BinOf("php"), site.PhpFolder);

            _state = Ui.Label("", "bold");
            var hint = Ui.Label(I18n.T("profiler.hint"), "muted small");
            hint.Margin = new Thickness(0, 4, 0, 0);

            _toggle = Ui.Button("", Toggle, "primary");
            var bar = DevUi.Bar(
                _toggle,
                Ui.Button(I18n.T("profiler.profileRequest"), ProfileRequest, null, "external"),
                Ui.Button(I18n.T("sites.refresh"), FillFiles, null, "refresh"),
                Ui.Button(I18n.T("profiler.deleteAll"), async () =>
                {
                    if (!await Dialogs.Confirm(this, I18n.T("profiler.deleteConfirm"))) return;
                    Profiler.DeleteAll(_site);
                    FillFiles();
                }),
                Ui.Button(I18n.T("log.folder"), () => DevUi.OpenFolder(Profiler.Dir), null, "folder"));

            _files = new GridList("160,*,90", new[] { I18n.T("profiler.col.time"), I18n.T("profiler.col.request"), I18n.T("profiler.col.size") }, new[] { false, false, true });
            _files.Height = 170;
            _files.Box.SelectionChanged += async (s, e) => await Ui.Run(ShowProfile, _files);

            _summary = Ui.Label("", null, Ui.Muted);
            _summary.Margin = new Thickness(0, 10, 0, 6);
            _funcs = new GridList("*,100,60,100,80", new[] { I18n.T("profiler.col.function"), I18n.T("profiler.col.inclusive"), "%", I18n.T("profiler.col.self"), I18n.T("profiler.col.calls") }, new[] { false, true, true, true, true });
            _funcs.Box.SelectionMode = SelectionMode.Single;

            var grid = new DockPanel();
            foreach (Control c in new Control[] { _state, hint, bar, _files, _summary })
            {
                DockPanel.SetDock(c, Dock.Top);
                grid.Children.Add(c);
            }
            grid.Children.Add(_funcs);
            DevUi.Fill(this, grid, 980, 660, 700, 480);
            Opened += (s, e) => { if (_preview) return; UpdateState(); FillFiles(); };
        }

        void UpdateState()
        {
            bool on = Profiler.IsOn(_phpDir);
            var label = PackageTypes.Label(_site.PhpFolder);
            _state.Text = _phpDir == null ? I18n.T("err.notInstalled", "PHP") : on ? I18n.T("profiler.on", label) : I18n.T("profiler.off", label);
            _state.Foreground = on ? Ui.Green : Ui.Text;
            _toggle.Content = on ? I18n.T("profiler.turnOff") : I18n.T("profiler.turnOn");
            _toggle.IsEnabled = _phpDir != null;
        }

        async Task Toggle()
        {
            if (_phpDir == null) return;
            _toggle.IsEnabled = false;
            try
            {
                if (Profiler.IsOn(_phpDir)) Profiler.Disable(_phpDir);
                else
                {
                    // Xdebug may have to be downloaded (xdebug.org) the first time
                    if (!Xdebug.IsEnabled(_phpDir) && !await Dialogs.Confirm(this, I18n.T("profiler.needsXdebug", Xdebug.DownloadUrl(_site.PhpFolder)))) return;
                    await Profiler.EnableAsync(_phpDir);
                }
                await ServiceManager.ReloadWebAsync();
            }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
            finally { _toggle.IsEnabled = true; UpdateState(); }
        }

        async Task ProfileRequest()
        {
            if (!_site.Valid) return;
            if (!Profiler.IsOn(_phpDir)) { await Dialogs.Info(this, I18n.T("profiler.turnOnFirst")); return; }
            Ui.Open(_site.PreferredUrl + "?XDEBUG_TRIGGER=1");
        }

        void FillFiles()
        {
            _files.Clear();
            foreach (var p in Profiler.List(_site))
                _files.Add(p, new[] { p.Time.ToString("yyyy-MM-dd HH:mm:ss"), p.Request != "" ? p.Request : p.Script, Ui.FormatBytes(p.Bytes) });
            if (_files.Box.ItemCount == 0) _files.Add(null, new[] { "", I18n.T("profiler.none"), "" }, Ui.Muted).IsHitTestVisible = false;
            _funcs.Clear();
            _summary.Text = "";
        }

        async Task ShowProfile()
        {
            var p = _files.Selected as ProfileFile;
            if (p == null || _preview) return;
            _summary.Text = I18n.T("profiler.reading");
            ProfileSummary sum;
            try { sum = await Task.Run(() => Profiler.Parse(p.File)); }
            catch (Exception ex) { _summary.Text = ex.Message; return; }
            if (_files.Selected != p) return;
            ShowSummary(sum);
        }

        void ShowSummary(ProfileSummary sum)
        {
            _summary.Text = I18n.T("profiler.summary", sum.Script, sum.TotalMs.ToString("0.0"));
            _funcs.Clear();
            foreach (var f in sum.Functions)
            {
                var pct = sum.TotalMs > 0 ? f.InclusiveMs * 100 / sum.TotalMs : 0;
                _funcs.Add(f, new[] { f.Name, f.InclusiveMs.ToString("0.00"), pct.ToString("0.0"), f.SelfMs.ToString("0.00"), f.Calls.ToString() });
            }
        }

        public static Window Preview()
        {
            var dir = DevUi.Temp("profiler");
            var site = new Site { Name = "shop", Host = "shop.portly", Folder = dir, DocRoot = dir, Valid = true };
            var f = new ProfilerForm(site) { _preview = true };
            f._state.Text = I18n.T("profiler.on", "8.5");
            f._state.Foreground = Ui.Green;
            f._toggle.Content = I18n.T("profiler.turnOff");
            f._toggle.IsEnabled = true;
            var now = new DateTime(2026, 11, 11, 9, 44, 12);
            var samples = new[] { ("/checkout", 412000L), ("/cart", 298000L), ("/products?page=2", 351000L), ("/", 186000L) };
            for (int i = 0; i < samples.Length; i++)
                f._files.Add(new ProfileFile { Time = now.AddMinutes(-i * 3), Request = samples[i].Item1, Bytes = samples[i].Item2 },
                    new[] { now.AddMinutes(-i * 3).ToString("yyyy-MM-dd HH:mm:ss"), samples[i].Item1, Ui.FormatBytes(samples[i].Item2) });
            var sum = new ProfileSummary { Script = "/checkout", TotalMs = 412.6 };
            sum.Functions.Add(new ProfileFunction { Name = "{main}", InclusiveMs = 412.6, SelfMs = 3.1, Calls = 1 });
            sum.Functions.Add(new ProfileFunction { Name = "App\\Http\\Controllers\\CheckoutController->store", InclusiveMs = 388.2, SelfMs = 12.4, Calls = 1 });
            sum.Functions.Add(new ProfileFunction { Name = "Illuminate\\Database\\Connection->select", InclusiveMs = 201.7, SelfMs = 38.9, Calls = 24 });
            sum.Functions.Add(new ProfileFunction { Name = "PDOStatement->execute", InclusiveMs = 162.8, SelfMs = 162.8, Calls = 24 });
            sum.Functions.Add(new ProfileFunction { Name = "Illuminate\\Support\\Collection->map", InclusiveMs = 54.3, SelfMs = 9.6, Calls = 17 });
            sum.Functions.Add(new ProfileFunction { Name = "json_encode", InclusiveMs = 7.9, SelfMs = 7.9, Calls = 31 });
            f.ShowSummary(sum);
            f._files.Box.SelectedIndex = 0;
            return f;
        }
    }
}
