using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>"Fix SSL": shows what is wrong with the local HTTPS setup and repairs it for all sites at once.</summary>
    public class SslRepairForm : PortlyDialog
    {
        readonly List<Site> _sites;
        readonly StackPanel _list = new StackPanel { Spacing = 4 };
        readonly TextBlock _summary = Ui.Label("", "bold");
        readonly TextBlock _upgradeText = Ui.Label("", "muted small");
        readonly Button _fix, _close, _upgrade;

        public SslRepairForm(List<Site> sites) : this(sites, null) { }

        SslRepairForm(List<Site> sites, SslRepair.Result sample) : base(I18n.T("sslfix.title"), 620)
        {
            _sites = sites;
            Body.Children.Add(Ui.Label(I18n.T("sslfix.title"), "title"));
            Body.Children.Add(Ui.Label(I18n.T("sslfix.intro"), "muted"));
            Body.Children.Add(_summary);
            Body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 360 });
            Body.Children.Add(_upgradeText);

            _upgrade = Ui.Button(I18n.T("sslfix.upgrade"), Upgrade);
            _fix = Ui.Button(I18n.T("sslfix.fix"), Fix, "primary", "refresh");
            _close = Ui.Button(I18n.T("common.close"), () => Close());
            Buttons.Children.Add(_upgrade);
            Buttons.Children.Add(_fix);
            Buttons.Children.Add(_close);
            Display(sample ?? SslRepair.Check(_sites), false);
        }

        void Display(SslRepair.Result r, bool after)
        {
            _list.Children.Clear();
            if (r.SslOff)
            {
                _summary.Text = I18n.T("sslfix.sslOff");
                _fix.IsEnabled = false;
                _upgrade.IsVisible = _upgradeText.IsVisible = false;
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

            if (!after)
                _summary.Text = !r.CaTrusted || bad > 0 ? I18n.T("sslfix.found", bad + (r.CaTrusted ? 0 : 1)) : I18n.T("sslfix.allOk");
            else
                _summary.Text = I18n.T("sslfix.done", r.Renewed) + (r.CaTrusted ? "" : "\n" + I18n.T("sslfix.caStillNot")) + "\n" + I18n.T("sslfix.browserHint");
            _fix.IsVisible = !after;
            // sites outside the authority's names: only the upgrade below helps them
            _fix.IsEnabled = !r.CaTrusted || bad > r.OutsideCa.Count || r.OldCas > 0;
            if (r.OldCas > 0) AddLine(false, I18n.T("sslfix.oldCaLeft"), null);
            // optional: an existing authority is never replaced without being asked
            var outside = r.OutsideCa;
            _upgrade.IsVisible = _upgradeText.IsVisible = r.CaUnrestricted || outside.Count > 0;
            _upgrade.IsEnabled = true;
            _upgradeText.Text = outside.Count > 0 ? I18n.T("sslfix.outside", string.Join(", ", outside.Select(x => x.Site.Host))) : I18n.T("sslfix.upgradeIntro");
        }

        void AddLine(bool ok, string what, string detail)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*"), Margin = new Thickness(0, 0, 0, 2) };
            var dot = Ui.Label(ok ? "✓" : "!", "bold", ok ? Ui.Green : Ui.Red);
            dot.VerticalAlignment = VerticalAlignment.Top;
            var texts = Ui.Column(0, Ui.Label(what));
            if (detail != null) texts.Children.Add(Ui.Label(detail, "small", ok ? Ui.Muted : Ui.Red));
            Grid.SetColumn(texts, 1);
            grid.Children.Add(dot);
            grid.Children.Add(texts);
            _list.Children.Add(grid);
        }

        async Task Upgrade()
        {
            if (!await Dialogs.Confirm(this, I18n.T("sslfix.upgradeConfirm", string.Join(", ", Certs.PermittedNames())))) return;
            _fix.IsEnabled = _close.IsEnabled = _upgrade.IsEnabled = false;
            _summary.Text = I18n.T("sslfix.working");
            try
            {
                var u = await SslRepair.UpgradeCaAsync(_sites);
                Display(SslRepair.Check(_sites), false);
                _summary.Text = u.Error ?? I18n.T("sslfix.upgradeDone", u.Renewed) + (u.OldRemoved ? "" : "\n" + I18n.T("sslfix.oldCaLeft"))
                    + (u.JavaError != null ? "\n" + u.JavaError : "") + "\n" + I18n.T("sslfix.browserHint");
            }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
            _close.IsEnabled = true;
        }

        async Task Fix()
        {
            _fix.IsEnabled = _close.IsEnabled = false;
            _summary.Text = I18n.T("sslfix.working");
            try
            {
                var r = await SslRepair.FixAsync(_sites, true);
                Display(r, true);
            }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
            _close.IsEnabled = true;
        }

        public static Window Preview()
        {
            Func<string, string, bool, SslRepair.SiteResult> site = (host, problem, renewed) =>
                new SslRepair.SiteResult { Site = new Site { Name = host, Host = host, Valid = true }, Problem = problem, Renewed = renewed };
            var r = new SslRepair.Result { CaTrusted = true, CaUnrestricted = true };
            r.Sites.Add(site("shop.portly", null, false));
            r.Sites.Add(site("blog.portly", "expiring", false));
            r.Sites.Add(site("api.shop.portly", "names", false));
            r.Sites.Add(site("wapflow.portly", null, false));
            return new SslRepairForm(new List<Site>(), r);
        }
    }
}
