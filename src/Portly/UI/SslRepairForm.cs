using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>"Fix SSL": shows what is wrong with the local HTTPS setup and repairs it for all sites at once.</summary>
    public class SslRepairForm : Form
    {
        readonly List<Site> _sites;
        readonly FlowLayoutPanel _list;
        readonly Label _summary;
        readonly ModernButton _fix, _close;

        public SslRepairForm(List<Site> sites)
        {
            _sites = sites;
            Ui.Apply(this);
            Text = I18n.T("sslfix.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(22);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var title = Ui.Label(I18n.T("sslfix.title"), Ui.Title);
            t.Controls.Add(title);
            var intro = Ui.Label(I18n.T("sslfix.intro"), Ui.Font, Ui.Muted);
            intro.MaximumSize = new Size(Ui.S(540), 0);
            intro.Margin = Ui.P(0, 6, 0, 10);
            t.Controls.Add(intro);
            _summary = Ui.Label("", Ui.Bold);
            _summary.MaximumSize = new Size(Ui.S(540), 0);
            _summary.Margin = Ui.P(0, 0, 0, 8);
            t.Controls.Add(_summary);
            _list = new ScrollFlow { FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = Ui.S(560), Margin = Padding.Empty };
            t.Controls.Add(_list);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 12, 0, 0) };
            _close = Ui.Button(I18n.T("common.close"), (s, e) => Close());
            _close.Margin = Padding.Empty;
            _fix = Ui.Button(I18n.T("sslfix.fix"), async (s, e) => await Fix(), true, "refresh");
            buttons.Controls.Add(_close);
            buttons.Controls.Add(_fix);
            t.Controls.Add(buttons);
            Controls.Add(t);
            CancelButton = _close;
            Show(SslRepair.Check(_sites), false);
        }

        void Show(SslRepair.Result r, bool after)
        {
            _list.Controls.Clear();
            if (r.SslOff)
            {
                _summary.Text = I18n.T("sslfix.sslOff");
                _fix.Enabled = false;
                return;
            }
            int bad = r.Sites.Count(x => x.Problem != null && !x.Renewed);
            // the CA line first: without a trusted CA every site shows a warning
            AddLine(r.CaTrusted, r.CaTrusted ? I18n.T("sslfix.caOk") : I18n.T("sslfix.caNot"), null);
            if (after && r.FirefoxProfiles > 0) AddLine(true, I18n.T("sslfix.firefox", r.FirefoxProfiles), null);
            foreach (var sr in r.Sites.OrderBy(x => x.Problem == null || x.Renewed).ThenBy(x => x.Site.Host))
            {
                if (sr.Renewed) AddLine(true, sr.Site.Host, I18n.T("sslfix.renewed", SslRepair.ProblemText(sr.Problem)));
                else if (sr.Problem != null) AddLine(false, sr.Site.Host, SslRepair.ProblemText(sr.Problem));
                else AddLine(true, sr.Site.Host, I18n.T("sslfix.siteOk"));
            }
            _list.Height = Math.Min(_list.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical) + Ui.S(4), Ui.S(360));

            if (!after)
                _summary.Text = !r.CaTrusted || bad > 0 ? I18n.T("sslfix.found", bad + (r.CaTrusted ? 0 : 1)) : I18n.T("sslfix.allOk");
            else
                _summary.Text = I18n.T("sslfix.done", r.Renewed) + (r.CaTrusted ? "" : "\n" + I18n.T("sslfix.caStillNot")) + "\n" + I18n.T("sslfix.browserHint");
            _fix.Visible = !after;
            _fix.Enabled = !r.CaTrusted || bad > 0;
        }

        void AddLine(bool ok, string what, string detail)
        {
            var p = Theme.Current;
            var row = new Panel { Width = Ui.S(540), Height = Ui.S(detail == null ? 26 : 40), Margin = Ui.P(0, 0, 0, 4), BackColor = Color.Transparent };
            var dot = Ui.Label(ok ? "✓" : "!", Ui.Bold, ok ? p.Green : p.Red);
            dot.Location = new Point(0, Ui.S(2));
            var name = Ui.Label(what, Ui.Font);
            name.Location = new Point(Ui.S(22), Ui.S(2));
            name.AutoSize = false;
            name.AutoEllipsis = true;
            name.Size = new Size(Ui.S(510), Ui.S(20));
            row.Controls.Add(dot);
            row.Controls.Add(name);
            if (detail != null)
            {
                var d = Ui.Label(detail, Ui.Small, ok ? Ui.Muted : p.Red);
                d.Location = new Point(Ui.S(22), Ui.S(21));
                d.AutoSize = false;
                d.AutoEllipsis = true;
                d.Size = new Size(Ui.S(510), Ui.S(18));
                row.Controls.Add(d);
            }
            _list.Controls.Add(row);
        }

        async System.Threading.Tasks.Task Fix()
        {
            _fix.Enabled = _close.Enabled = false;
            _summary.Text = I18n.T("sslfix.working");
            try
            {
                var r = await SslRepair.FixAsync(_sites, true);
                Show(r, true);
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
            _close.Enabled = true;
        }
    }
}
