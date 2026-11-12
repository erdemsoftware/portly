using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.Forms;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop
{
    public partial class MainWindow
    {
        TextBlock _sitesSub;
        StackPanel _siteList;
        Button _archivedBtn, _sslBtn;
        bool _caTrusted;
        Dictionary<string, string> _sslProblems = new Dictionary<string, string>();

        /// <summary>Project cards side by side like the Windows app: fixed 300 px wide, as many columns as fit.</summary>
        sealed class CardGrid : Panel
        {
            const double MinCard = 300, Gap = 12;

            static int Columns(double width) { return Math.Max(1, (int)Math.Floor((width + Gap) / (MinCard + Gap))); }

            protected override Size MeasureOverride(Size available)
            {
                double total = double.IsInfinity(available.Width) ? MinCard : available.Width;
                int cols = Columns(total);
                double w = MinCard;
                foreach (var c in Children) c.Measure(new Size(w, double.PositiveInfinity));
                var rows = Rows(cols);
                return new Size(total, rows.Count == 0 ? 0 : rows.Sum() + Gap * (rows.Count - 1));
            }

            List<double> Rows(int cols)
            {
                var rows = new List<double>();
                for (int i = 0; i < Children.Count; i++)
                {
                    if (i % cols == 0) rows.Add(0);
                    rows[rows.Count - 1] = Math.Max(rows[rows.Count - 1], Children[i].DesiredSize.Height);
                }
                return rows;
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                int cols = Columns(finalSize.Width);
                double w = MinCard;
                var rows = Rows(cols);
                double y = 0;
                for (int i = 0; i < Children.Count; i++)
                {
                    int r = i / cols, c = i % cols;
                    if (c == 0 && r > 0) y += rows[r - 1] + Gap;
                    Children[i].Arrange(new Rect(c * (w + Gap), y, w, rows[r]));
                }
                return finalSize;
            }
        }

        // ---------------- sites page ----------------
        Control BuildSitesPage()
        {
            var page = new StackPanel { Spacing = 10 };
            _archivedBtn = SitesWebUi.ArchivedButton(this);
            _sslBtn = Ui.Button(I18n.T("sslfix.button"), async () => await OpenSslRepair(), null, "warning");
            page.Children.Add(Ui.PageHeader(I18n.T("nav.sites"), out _sitesSub,
                Ui.Button(I18n.T("main.newProject"), async () => await NewProject(), "primary", "plus"),
                Ui.HeaderIconButton("refresh", I18n.T("sites.refresh"), async () => await ServiceManager.ReloadWebAsync()),
                Ui.HeaderIconButton("folder", I18n.T("sites.folder"), () => Ui.Open(Settings.Current.DocumentRoot)),
                HostsButton(),
                Ui.Button(I18n.T("shares.title"), async () => await OpenShares(), null, "share"),
                _archivedBtn,
                _sslBtn));
            ((Control)page.Children[0]).Margin = new Thickness(0, 0, 0, -10);
            _siteList = new StackPanel { Spacing = 0 };
            page.Children.Add(_siteList);
            return page;
        }

        /// <summary>shared: rebuild the project cards (sites changed, share started, web server restarted ...).</summary>
        public void FillSites()
        {
            if (_siteList == null) return;
            if (!Dispatcher.UIThread.CheckAccess()) { Ui.Post(FillSites); return; }
            _caTrusted = Certs.IsCaTrusted();
            var s = Settings.Current;
            var sites = ServiceManager.CurrentSites;
            _sitesSub.Text = I18n.T("sites.count", sites.Count(x => x.Valid), s.DocumentRoot, s.HostTemplate.Replace("{name}", "*").Replace("{tld}", s.Tld));
            SitesWebUi.UpdateArchived(_archivedBtn);
            _siteList.Children.Clear();
            if (sites.Count == 0)
            {
                var empty = Ui.Card(Ui.Column(8,
                    Ui.Label(I18n.T("sites.emptyTitle"), "subtitle"),
                    Ui.Label(I18n.T("dash.empty", s.DocumentRoot), "muted"),
                    Ui.Button(I18n.T("main.newProject"), async () => await NewProject(), "primary", "plus")), new Thickness(20, 18));
                empty.MaxWidth = 600;
                empty.HorizontalAlignment = HorizontalAlignment.Left;
                _siteList.Children.Add(empty);
            }
            // HTTPS problems per site (missing / expired / old CA / names changed), shown on the cards and the header button
            var ssl = s.Ssl ? SslRepair.Check(sites) : null;
            _sslProblems = ssl == null ? new Dictionary<string, string>() : ssl.Sites.Where(x => x.Problem != null).ToDictionary(x => x.Site.Host, x => x.Problem);
            int sslIssues = _sslProblems.Count + (ssl != null && !ssl.CaTrusted && sites.Any(x => x.Valid) ? 1 : 0);
            SitesWebUi.SetContent(_sslBtn, "warning", I18n.T("sslfix.button") + (sslIssues > 0 ? " (" + sslIssues + ")" : ""));
            if (sslIssues > 0) _sslBtn.Foreground = Ui.Orange; else _sslBtn.ClearValue(TemplatedControl.ForegroundProperty);
            _sslBtn.IsVisible = s.Ssl;

            // a site whose address ends with another site's address is its subdomain: show it under that site
            var parentOf = new Dictionary<Site, Site>();
            foreach (var site in sites)
            {
                var parent = sites.Where(o => o != site && site.Host.EndsWith("." + o.Host, StringComparison.OrdinalIgnoreCase))
                                  .OrderByDescending(o => o.Host.Length).FirstOrDefault();
                if (parent != null) parentOf[site] = parent;
            }
            var roots = sites.Where(x => !parentOf.ContainsKey(x)).ToList();
            var loose = new CardGrid { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var site in roots.Where(x => !parentOf.ContainsValue(x))) loose.Children.Add(SiteCard(site, null));
            if (loose.Children.Count > 0) _siteList.Children.Add(loose);
            foreach (var site in roots.Where(x => parentOf.ContainsValue(x))) _siteList.Children.Add(SiteBlock(site, parentOf));
        }

        /// <summary>A site with its subdomains (group folders, "shop.example" folders): one row, joined by a line.</summary>
        Control SiteBlock(Site root, Dictionary<Site, Site> parentOf)
        {
            var all = new List<Site>();
            Action<Site> add = null;
            add = site =>
            {
                all.Add(site);
                foreach (var kid in parentOf.Where(kv => kv.Value == site).Select(kv => kv.Key).OrderBy(k => k.Host)) add(kid);
            };
            add(root);
            var grid = new CardGrid();
            foreach (var site in all) grid.Children.Add(SiteCard(site, site == root ? null : parentOf[site]));
            var accent = ((ISolidColorBrush)Ui.Accent).Color;
            // the accent line on the left ties the subdomains to their site
            return new Border
            {
                Margin = new Thickness(4, 2, 0, 14), Padding = new Thickness(10, 0, 0, 0), BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)),
                Child = Ui.Column(6, Ui.Label(root.Host + "  ·  " + I18n.T("sites.subCount", all.Count - 1), "muted small"), grid),
            };
        }

        /// <summary>shared: HTTPS repair for sites whose certificate is not trusted.</summary>
        public async Task OpenSslRepair()
        {
            ShowFromTray();
            await new SslRepairForm(ServiceManager.CurrentSites).ShowDialog(this);
            FillSites();
        }

        static void Clickable(Control c, Action click)
        {
            c.Cursor = new Cursor(StandardCursorType.Hand);
            c.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                click();
            };
        }

        static TextBlock Trimmed(string text, string classes, IBrush color = null)
        {
            var t = Ui.Label(text, classes, color);
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            return t;
        }

        Control SiteCard(Site site, Site parent)
        {
            var avatar = BrandTile.Avatar(site.Name);
            avatar.VerticalAlignment = VerticalAlignment.Top;
            // a subdomain shows only its own part ("shop" of shop.clients.portly), the block header names the parent
            var title = parent != null ? "↳ " + site.Host.Substring(0, site.Host.Length - parent.Host.Length - 1)
                      : site.Group != "" ? site.Group + " / " + site.ShortName : site.Name;
            var name = Trimmed(title, "subtitle");
            ToolTip.SetTip(name, site.DocRoot + (parent != null ? "\n" + I18n.T("sites.subOf", site.Host, parent.Host) : ""));
            Control address;
            if (site.Valid)
            {
                var link = Ui.Link("", () => OpenSite(site, Settings.Current.Ssl));
                link.Content = Trimmed(site.PreferredUrl, null);
                link.HorizontalAlignment = HorizontalAlignment.Left;
                if (site.Aliases.Count > 0) ToolTip.SetTip(link, site.PreferredUrl + "\n" + SitesWebUi.AliasText(site));
                address = link;
            }
            else
            {
                var bad = Ui.Label(site.Problem, "small", Ui.Red);
                bad.MaxHeight = 34;
                bad.TextTrimming = TextTrimming.CharacterEllipsis;
                address = bad;
            }
            // what it is built with and its database ("Laravel 12  ·  MySQL: shop"), read from the project's files
            var info = site.Valid ? ProjectInfo.For(site) : new ProjectInfo();
            var infoText = string.Join("  ·  ", new[] { info.Tech, info.DbText }.Where(x => x != ""));
            var infoLabel = Trimmed(infoText, "muted small");
            if (infoText != "") ToolTip.SetTip(infoLabel, infoText);

            var badges = new List<Control>();
            if (site.Valid) badges.Add(new Pill("PHP " + PackageTypes.Label(site.PhpFolder) + (site.PhpOverride != "" ? " •" : ""), new SolidColorBrush(Color.Parse("#777BB4")), false));
            if (site.IsGroup)
                badges.Add(new Pill(I18n.T("sites.group"), Ui.Accent, false));
            var shared = Share.UrlOf(site);
            if (shared != null)
            {
                var sp = new Pill(I18n.T("share.badge"), new SolidColorBrush(Color.Parse("#F38020")));
                Clickable(sp, () => Ui.Open(shared));
                ToolTip.SetTip(sp, shared + "\n" + I18n.T("shares.devices", LanShare.DevicesOf(site.Host, true).Count));
                badges.Add(sp);
            }
            if (site.Valid && Sites.IsPublicName(site.Host))
            {
                // a real domain (example.com): this PC opens either the local project or the live site; one click switches
                bool live = site.ShowLive;
                var dp = new Pill(live ? I18n.T("domain.live") : I18n.T("domain.local"), live ? Ui.Orange : Ui.Accent);
                ToolTip.SetTip(dp, I18n.T(live ? "domain.liveHint" : "domain.localHint", site.Host, site.LocalHost));
                Clickable(dp, () =>
                {
                    Sites.SetLive(site.Name, !live);
                    SetStatus(I18n.T(live ? "domain.toLocal" : "domain.toLive", site.Host, site.LocalHost));
                    Task.Run(() => ServiceManager.SyncHostsInBackground(ServiceManager.RefreshSites()));
                });
                badges.Add(dp);
            }
            var lan = LanShare.UrlOf(site);
            if (lan != null)
            {
                // orange while the web server is stopped: the device gets a "start the web server" page
                bool down = LanShare.WebDown;
                var lp = new Pill(I18n.T("lan.badge"), down ? Ui.Orange : Ui.Green);
                Clickable(lp, async () => await Ui.Run(() => ShareUi.ShowLanQr(this, site.Host)));
                ToolTip.SetTip(lp, lan + (down ? "\n" + I18n.T("lan.webDownTitle") : "") + "\n" + I18n.T("shares.devices", LanShare.DevicesOf(site.Host, false).Count));
                badges.Add(lp);
            }
            if (site.Valid && Settings.Current.Ssl)
            {
                string problem;
                _sslProblems.TryGetValue(site.Host, out problem);
                bool ok = _caTrusted && problem == null;
                var lockPill = new Pill(ok ? "HTTPS" : "HTTPS !", ok ? Ui.Green : (problem != null ? Ui.Red : Ui.Orange));
                if (!ok)
                {
                    Clickable(lockPill, async () => await Ui.Run(OpenSslRepair));
                    ToolTip.SetTip(lockPill, (problem != null ? SslRepair.ProblemText(problem) : I18n.T("sites.httpsUntrusted")) + " - " + I18n.T("sslfix.button"));
                }
                badges.Add(lockPill);
            }
            badges.AddRange(DevWorkflowUi.SiteBadges(site, this, FillSites));
            badges.AddRange(SitesWebUi.CardBadges(site));
            var badgeRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4, Margin = new Thickness(0, 3, 0, 0) };
            foreach (var b in badges) badgeRow.Children.Add(b);

            var text = new StackPanel { Spacing = 7, Margin = new Thickness(0, -2, 0, 0), Children = { name, address, infoLabel, badgeRow } };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, -3, 0) };
            if (site.Valid)
            {
                // Share: on the local network (phone / another PC on the same router) or on the internet (Cloudflare)
                bool sharing = LanShare.UrlOf(site) != null || Share.UrlOf(site) != null;
                var shareBtn = MenuButton("share", sharing ? I18n.T("share.active") : I18n.T("share.button"), I18n.T("share.button"), items => BuildShareMenu(items, site));
                // green while the project is shared (local network or internet)
                if (sharing)
                {
                    shareBtn.Classes.Remove("ghost");
                    shareBtn.Classes.Add("green");
                }
                buttons.Children.Add(shareBtn);
            }
            buttons.Children.Add(MenuButton("more", I18n.T("sites.more"), I18n.T("sites.more"), items => BuildSiteMenu(items, site)));

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
            grid.Children.Add(avatar);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);

            var card = Ui.Card(grid, new Thickness(15, 15, 14, 14));
            card.MinHeight = 122;
            if (!site.Valid)
            {
                var red = ((ISolidColorBrush)Ui.Red).Color;
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(140, red.R, red.G, red.B));
            }
            else
            {
                card.Cursor = new Cursor(StandardCursorType.Hand);
                card.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) OpenSite(site, Settings.Current.Ssl);
                };
            }
            ToolTip.SetTip(card, site.DocRoot);
            card.ContextFlyout = LazyMenu(null, items => BuildSiteMenu(items, site));
            return card;
        }

        /// <summary>Small ghost button that opens a menu built each time it opens.</summary>
        static Button MenuButton(string glyph, string tip, string name, Action<IList> build)
        {
            var b = Ui.Button(null, (Action)null, "ghost", glyph);
            b.Padding = new Thickness(0);
            b.Width = b.Height = 32;
            b.MinHeight = 0;
            ToolTip.SetTip(b, tip);
            Avalonia.Automation.AutomationProperties.SetName(b, name);
            b.Flyout = LazyMenu(PlacementMode.BottomEdgeAlignedRight, build);
            return b;
        }

        /// <summary>
        /// Menu filled each time it opens. Avalonia 12 shows an empty menu when the flyout has no item at all while it opens,
        /// so one placeholder item is there from the start (the real items replace it in Opening).
        /// </summary>
        static MenuFlyout LazyMenu(PlacementMode? placement, Action<IList> build)
        {
            var fly = new MenuFlyout();
            if (placement.HasValue) fly.Placement = placement.Value;
            fly.Items.Add(new MenuItem { Header = "…" });
            fly.Opening += (s, e) => build(fly.Items);
            return fly;
        }

        static MenuItem SiteItem(string text, Func<Task> click)
        {
            var item = new MenuItem { Header = text };
            item.Click += async (s, e) => await Ui.Run(click, item);
            return item;
        }

        static MenuItem SiteItem(string text, Action click)
        {
            return SiteItem(text, () => { click(); return Task.CompletedTask; });
        }

        void BuildSiteMenu(IList items, Site site)
        {
            items.Clear();
            if (site.Valid)
            {
                items.Add(SiteItem(I18n.T("sites.openHttp"), () => OpenSite(site, false)));
                if (Settings.Current.Ssl) items.Add(SiteItem(I18n.T("sites.openHttps"), () => OpenSite(site, true)));
            }
            items.Add(SiteItem(I18n.T("sites.folder"), () => Ui.Open(site.Folder)));
            items.Add(SiteItem(I18n.T("main.terminal"), () => Terminal.Open(site.Folder)));
            foreach (var ed in DesktopApps.Editors)
            {
                var editor = ed;
                if (DesktopApps.IsInstalled(editor))
                    items.Add(SiteItem(I18n.T("tools.openIn", PackageTypes.Get(editor).Name), () => DesktopApps.OpenInEditor(editor, site.Folder)));
            }
            var php = new MenuItem { Header = I18n.T("sites.phpVersion") };
            var def = SiteItem(I18n.T("sites.phpDefault", PackageTypes.Label(PackageTypes.ActiveFolder("php"))), async () => { Sites.SetPhp(site.Name, ""); await ServiceManager.ReloadWebAsync(); });
            def.ToggleType = MenuItemToggleType.CheckBox;
            def.IsChecked = site.PhpOverride == "";
            php.Items.Add(def);
            foreach (var f in PackageTypes.Installed("php"))
            {
                var folder = f;
                var mi = SiteItem("PHP " + PackageTypes.Label(f), async () =>
                {
                    Sites.SetPhp(site.Name, folder);
                    Log.Info(I18n.T("log.sitePhp", site.Name, PackageTypes.Label(folder)));
                    await ServiceManager.ReloadWebAsync();
                });
                mi.ToggleType = MenuItemToggleType.CheckBox;
                mi.IsChecked = string.Equals(site.PhpOverride, f, StringComparison.OrdinalIgnoreCase);
                php.Items.Add(mi);
            }
            items.Add(php);
            items.Add(SiteItem(I18n.T("sites.createDb"), async () => await CreateDatabase(ProjectCreator.DbNameFor(site.Name))));
            // a sub-project in its own folder inside this project (name.project.tld)
            if (site.Valid && site.Name.IndexOf('/') < 0)
                items.Add(SiteItem(I18n.T("sites.addSub"), async () => await NewProject(null, site.Name)));
            if (site.Valid)
            {
                var share = new MenuItem { Header = I18n.T("share.button") };
                BuildShareMenu(share.Items, site);
                items.Add(share);
            }
            DevWorkflowUi.AddSiteMenuItems(items, site, this, FillSites);
            SitesWebUi.AddSiteItems(this, items, site);
            if (OS.IsWindows && Deploy.IsAvailable)
                items.Add(SiteItem(I18n.T("deploy.menu"), async () => { ShowFromTray(); await new DeployForm(site).ShowDialog(this); }));
            items.Add(new Separator());
            items.Add(SiteItem(I18n.T("sites.delete"), async () =>
            {
                if (!await Dialogs.Confirm(this, I18n.T("sites.deleteConfirm", site.Name, site.Folder))) return;
                try { ProjectCreator.MoveToTrash(site); }
                catch (Exception ex) { await Dialogs.Warning(this, ex.Message); return; }
                await ServiceManager.ReloadWebAsync();
            }));
        }

        void BuildShareMenu(IList items, Site site)
        {
            items.Clear();
            // 1. offline: devices on the same router / Wi-Fi (testing on a phone)
            if (LanShare.UrlOf(site) == null) items.Add(SiteItem(I18n.T("lan.start"), async () => await LanShareSite(site)));
            else
            {
                items.Add(SiteItem(I18n.T("lan.copy", LanShare.UrlOf(site)), async () => { try { await Ui.Copy(this, LanShare.UrlOf(site)); } catch { } }));
                items.Add(SiteItem(I18n.T("lan.stop"), () => { LanShare.Stop(site); FillSites(); }));
            }
            items.Add(new Separator());
            // 2. online: a random public Cloudflare address
            if (!Share.IsAvailable) items.Add(new MenuItem { Header = I18n.T("share.start"), IsEnabled = false });
            else if (Share.UrlOf(site) == null) items.Add(SiteItem(I18n.T("share.start"), async () => await ShareSite(site)));
            else
            {
                items.Add(SiteItem(I18n.T("share.copy"), async () => { try { await Ui.Copy(this, Share.UrlOf(site)); } catch { } }));
                items.Add(SiteItem(I18n.T("share.stop"), () => { Share.Stop(site); FillSites(); }));
            }
            items.Add(new Separator());
            if (LanShare.UrlOf(site) != null || Share.UrlOf(site) != null)
                items.Add(SiteItem(I18n.T("share.showQr"), async () => await ShareUi.ShowQr(this, site.Host)));
            items.Add(SiteItem(I18n.T("shares.menu"), async () => await OpenShares()));
        }

        Task LanShareSite(Site site) { return ShareUi.StartLan(this, site); }

        /// <summary>The site becomes public: ShareUi says exactly what happens before anything is downloaded or opened.</summary>
        Task ShareSite(Site site)
        {
            return ShareUi.StartOnline(this, site, s => Ui.Post(() => SetStatus(s)));
        }

        /// <summary>shared: list of active shares.</summary>
        public async Task OpenShares()
        {
            ShowFromTray();
            await new SharesForm().ShowDialog(this);
        }

        /// <summary>shared: hosts button with its menu (services and projects page headers): "Hosts" edits the hosts file or writes Portly's entries again.</summary>
        public Button HostsButton(bool withText = false)
        {
            var b = Ui.Button(withText ? I18n.T("hosts.button") : null, (Action)null, null, "hosts");
            if (!withText)
            {
                b.Padding = new Thickness(0);
                b.Width = b.Height = 40;
                ToolTip.SetTip(b, I18n.T("hosts.button"));
                Avalonia.Automation.AutomationProperties.SetName(b, I18n.T("hosts.button"));
            }
            var fly = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            fly.Items.Add(SiteItem(I18n.T("hosts.edit"), async () => await new HostsForm().ShowDialog(this)));
            fly.Items.Add(SiteItem(I18n.T("hosts.sync"), () => { SetStatus(I18n.T("hosts.syncing")); Task.Run(() => ServiceManager.SyncHostsNow()); }));
            fly.Items.Add(SiteItem(I18n.T("hosts.folder"), () => Ui.Open(Path.GetDirectoryName(HostsFile.FilePath))));
            b.Flyout = fly;
            return b;
        }

        void OpenSite(Site site, bool https)
        {
            if (!site.Valid) { var _ = Dialogs.Warning(this, site.Problem); return; }
            if (!ServiceManager.Web.IsActive) SetStatus(I18n.T("main.webNotRunning"));
            Ui.Open(site.Url(https && Settings.Current.Ssl));
        }
    }
}
