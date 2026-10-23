using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>Feature "dumps": site menu > Profiler... Switch profiling on for the site's PHP, list profiles, show the slowest functions.</summary>
    public class ProfilerForm : Form
    {
        readonly Site _site;
        readonly string _phpDir;
        readonly ListView _files, _funcs;
        readonly Label _state, _summary;
        readonly ModernButton _toggle;

        public ProfilerForm(Site site)
        {
            _site = site;
            _phpDir = site.PhpFolder == "" ? null : Path.Combine(Paths.BinOf("php"), site.PhpFolder);
            Ui.Apply(this);
            Text = I18n.T("profiler.title", site.Name);
            Size = Ui.S(980, 660);
            MinimumSize = Ui.S(700, 480);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Padding = Ui.P(16, 12, 16, 12);

            _state = Ui.Label("", Ui.Bold);
            _state.Dock = DockStyle.Top;
            var hint = Ui.Label(I18n.T("profiler.hint"), Ui.Small, Ui.Muted);
            hint.Dock = DockStyle.Top;
            hint.AutoSize = false;
            hint.Height = Ui.S(36);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Padding = Ui.P(0, 6, 0, 8) };
            _toggle = Ui.Button("", async (s, e) => await Toggle(), true);
            bar.Controls.Add(_toggle);
            bar.Controls.Add(Ui.Button(I18n.T("profiler.profileRequest"), (s, e) => ProfileRequest(), false, "external"));
            bar.Controls.Add(Ui.Button(I18n.T("sites.refresh"), (s, e) => FillFiles(), false, "refresh"));
            bar.Controls.Add(Ui.Button(I18n.T("profiler.deleteAll"), (s, e) =>
            {
                if (!Ui.Confirm(this, I18n.T("profiler.deleteConfirm"))) return;
                Profiler.DeleteAll(_site);
                FillFiles();
            }));
            bar.Controls.Add(Ui.Button(I18n.T("log.folder"), (s, e) => WinUtil.OpenFolder(Profiler.Dir), false, "folder"));

            _files = new ListView { Dock = DockStyle.Top, Height = Ui.S(170), View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = BorderStyle.FixedSingle };
            _files.Columns.Add(I18n.T("profiler.col.time"), Ui.S(150));
            _files.Columns.Add(I18n.T("profiler.col.request"), Ui.S(420));
            _files.Columns.Add(I18n.T("profiler.col.size"), Ui.S(90));
            _files.SelectedIndexChanged += async (s, e) => await ShowProfile();

            _summary = Ui.Label("", Ui.Font, Ui.Muted);
            _summary.Dock = DockStyle.Top;
            _summary.Padding = Ui.P(0, 8, 0, 4);

            _funcs = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.FixedSingle };
            _funcs.Columns.Add(I18n.T("profiler.col.function"), Ui.S(460));
            _funcs.Columns.Add(I18n.T("profiler.col.inclusive"), Ui.S(110), HorizontalAlignment.Right);
            _funcs.Columns.Add("%", Ui.S(60), HorizontalAlignment.Right);
            _funcs.Columns.Add(I18n.T("profiler.col.self"), Ui.S(110), HorizontalAlignment.Right);
            _funcs.Columns.Add(I18n.T("profiler.col.calls"), Ui.S(80), HorizontalAlignment.Right);

            Controls.Add(_funcs);
            Controls.Add(_summary);
            Controls.Add(_files);
            Controls.Add(bar);
            Controls.Add(hint);
            Controls.Add(_state);
            _funcs.BringToFront();
            Load += (s, e) => { UpdateState(); FillFiles(); };
        }

        void UpdateState()
        {
            bool on = Profiler.IsOn(_phpDir);
            var label = PackageTypes.Label(_site.PhpFolder);
            _state.Text = _phpDir == null ? I18n.T("err.notInstalled", "PHP") : on ? I18n.T("profiler.on", label) : I18n.T("profiler.off", label);
            _state.ForeColor = on ? Ui.Green : Ui.Text;
            _toggle.Text = on ? I18n.T("profiler.turnOff") : I18n.T("profiler.turnOn");
            _toggle.Enabled = _phpDir != null;
            _toggle.Invalidate();
        }

        async Task Toggle()
        {
            if (_phpDir == null) return;
            _toggle.Enabled = false;
            try
            {
                if (Profiler.IsOn(_phpDir)) Profiler.Disable(_phpDir);
                else
                {
                    // Xdebug may have to be downloaded (xdebug.org) the first time
                    if (!Xdebug.IsEnabled(_phpDir) && !Ui.Confirm(this, I18n.T("profiler.needsXdebug", Xdebug.DownloadUrl(_site.PhpFolder)))) return;
                    await Profiler.EnableAsync(_phpDir);
                }
                await ServiceManager.ReloadWebAsync();
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
            finally { _toggle.Enabled = true; UpdateState(); }
        }

        void ProfileRequest()
        {
            if (!_site.Valid) return;
            if (!Profiler.IsOn(_phpDir)) { Ui.Info(this, I18n.T("profiler.turnOnFirst")); return; }
            WinUtil.Open(_site.PreferredUrl + "?XDEBUG_TRIGGER=1");
        }

        void FillFiles()
        {
            _files.BeginUpdate();
            _files.Items.Clear();
            foreach (var p in Profiler.List(_site))
                _files.Items.Add(new ListViewItem(new[] { p.Time.ToString("yyyy-MM-dd HH:mm:ss"), p.Request != "" ? p.Request : p.Script, Ui.FormatBytes(p.Bytes) }) { Tag = p });
            if (_files.Items.Count == 0) _files.Items.Add(new ListViewItem(new[] { "", I18n.T("profiler.none"), "" }) { ForeColor = Ui.Muted });
            _files.EndUpdate();
            _funcs.Items.Clear();
            _summary.Text = "";
        }

        async Task ShowProfile()
        {
            if (_files.SelectedItems.Count == 0) return;
            var p = _files.SelectedItems[0].Tag as ProfileFile;
            if (p == null) return;
            _summary.Text = I18n.T("profiler.reading");
            ProfileSummary sum;
            try { sum = await Task.Run(() => Profiler.Parse(p.File)); }
            catch (Exception ex) { _summary.Text = ex.Message; return; }
            _summary.Text = I18n.T("profiler.summary", sum.Script, sum.TotalMs.ToString("0.0"));
            _funcs.BeginUpdate();
            _funcs.Items.Clear();
            foreach (var f in sum.Functions)
            {
                var pct = sum.TotalMs > 0 ? f.InclusiveMs * 100 / sum.TotalMs : 0;
                _funcs.Items.Add(new ListViewItem(new[] { f.Name, f.InclusiveMs.ToString("0.00"), pct.ToString("0.0"), f.SelfMs.ToString("0.00"), f.Calls.ToString() }));
            }
            _funcs.EndUpdate();
        }
    }
}
