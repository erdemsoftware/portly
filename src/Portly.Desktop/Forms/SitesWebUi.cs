using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>
    /// Main window pieces of the SitesWeb features (domains, own certificates, archive, FrankenPHP, Firefox / Java trust).
    /// The main window only calls these from its existing menu builders and site cards.
    /// </summary>
    public static class SitesWebUi
    {
        static async void Reload() { await ServiceManager.ReloadWebAsync(); }

        static MenuItem Item(string text, Func<Task> click)
        {
            var item = new MenuItem { Header = text };
            item.Click += async (s, e) => await Ui.Run(click, item);
            return item;
        }

        static MenuItem Item(string text, Action click)
        {
            return Item(text, () => { click(); return Task.CompletedTask; });
        }

        // ---------------- site menu ----------------
        /// <summary>Adds MenuItem controls to a menu's item list (ContextMenu.Items, MenuFlyout.Items, MenuItem.Items).</summary>
        public static void AddSiteItems(Window owner, IList items, Site site)
        {
            if (site.Valid && Features.IsOn("aliases"))
                items.Add(Item(I18n.T("aliases.menu"), async () => { if (await new DomainsForm(site).ShowDialog<bool>(owner)) Reload(); }));

            if (site.Valid && Features.IsOn("customCerts") && Settings.Current.Ssl)
            {
                var cert = new MenuItem { Header = I18n.T("cert.menu") };
                cert.Items.Add(Item(I18n.T("cert.choose"), async () => { if (await new CertForm(site).ShowDialog<bool>(owner)) Reload(); }));
                var back = Item(I18n.T("cert.back"), () => { CustomCerts.Remove(site); Log.Info(I18n.T("cert.removed", site.Host)); Reload(); });
                back.IsEnabled = CustomCerts.Has(site);
                cert.Items.Add(back);
                items.Add(cert);
            }

            if (site.Valid && !site.IsGroup && Features.IsOn("frankenphp") && !AppInfo.IsLegacy)
                items.Add(RuntimeMenu(owner, site));

            if (Features.IsOn("archive"))
                items.Add(Item(I18n.T("archive.menu"), async () =>
                {
                    if (!await Dialogs.Confirm(owner, I18n.T("archive.confirm", site.Name, site.Folder))) return;
                    Sites.SetArchived(site.Name, true);
                    Log.Info(I18n.T("archive.done", site.Name));
                    Reload();
                }));
        }

        static MenuItem RuntimeMenu(Window owner, Site site)
        {
            var menu = new MenuItem { Header = I18n.T("franken.menu") };
            var dir = PackageTypes.ActiveDir("frankenphp");
            Func<string, Task> set = async rt =>
            {
                if (rt != "" && dir == null)
                {
                    if (await Dialogs.Confirm(owner, I18n.T("franken.install")))
                        await PackagesForm.Show(owner, "frankenphp", false, null);
                    if (PackageTypes.ActiveDir("frankenphp") == null) return;
                }
                if (rt == "octane" && !FrankenPhpService.HasOctane(site)) { await Dialogs.Info(owner, I18n.T("franken.octaneHow", site.Folder)); return; }
                if (rt == "octane" && !File.Exists(FrankenPhpService.WorkerFile(site))) { await Dialogs.Info(owner, I18n.T("franken.workerHow", site.Folder)); return; }
                Sites.SetRuntime(site.Name, rt);
                Log.Info(I18n.T("franken.set", site.Name, rt == "" ? "PHP (FastCGI)" : rt == "octane" ? "FrankenPHP worker" : "FrankenPHP"));
                Reload();
            };
            foreach (var opt in new[] { Tuple.Create("", I18n.T("franken.classic")), Tuple.Create("frankenphp", "FrankenPHP"), Tuple.Create("octane", I18n.T("franken.worker")) })
            {
                var o = opt;
                var mi = Item(o.Item2, () => set(o.Item1));
                mi.ToggleType = MenuItemToggleType.CheckBox;
                mi.IsChecked = site.Runtime == o.Item1;
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var ver = dir == null ? I18n.T("svc.notInstalled") : FrankenPhpService.VersionText(dir);
            menu.Items.Add(new MenuItem { Header = ver, IsEnabled = false });
            menu.Items.Add(new MenuItem { Header = I18n.T("franken.noSwoole"), IsEnabled = false });
            return menu;
        }

        // ---------------- SSL menu ----------------
        public static void AddSslItems(Window owner, IList items)
        {
            if (FirefoxTrust.HasProfiles)
                items.Add(Item(I18n.T("firefox.menu"), async () =>
                {
                    int n = FirefoxTrust.EnableAll();
                    await Dialogs.Info(owner, n > 0 ? I18n.T("firefox.doneRestart", n) : I18n.T("firefox.already"));
                }));
            var ff = FirefoxTrust.InstallDir();
            // the policy file sits in the Firefox program folder: writing it needs an elevated process (Windows only)
            if (OS.IsWindows && ff != null && !FirefoxTrust.PolicySet(ff))
                items.Add(Item(I18n.T("firefox.policyMenu"), async () =>
                {
                    if (!await Dialogs.Confirm(owner, I18n.T("firefox.policyConfirm", FirefoxTrust.PolicyFile(ff)))) return;
                    var code = WinUtil.RunElevated(Paths.Exe, "--firefox-policy \"" + ff + "\"");
                    if (code == 0) await Dialogs.Info(owner, I18n.T("firefox.doneRestart", 1));
                    else if (code != -2) await Dialogs.Warning(owner, I18n.T("firefox.policyFailed", code));
                }));
            var java = JavaTrust.FindHome();
            if (java != null && JavaTrust.Cacerts(java) != null)
                items.Add(Item(I18n.T("java.menu"), async () =>
                {
                    if (JavaTrust.IsImported(java)) { await Dialogs.Info(owner, I18n.T("java.already", java)); return; }
                    if (!await Dialogs.Confirm(owner, I18n.T("java.confirm", java, JavaTrust.ImportCommand(java)))) return;
                    var err = JavaTrust.Import(java);
                    if (err == null) await Dialogs.Info(owner, I18n.T("java.done", java)); else await Dialogs.Warning(owner, err);
                }));
        }

        // ---------------- site card ----------------
        public static List<Control> CardBadges(Site site)
        {
            var list = new List<Control>();
            if (site.Aliases.Count > 0)
            {
                var a = new Pill("+" + site.Aliases.Count, Ui.Accent, false);
                ToolTip.SetTip(a, string.Join("\n", site.Aliases));
                list.Add(a);
            }
            if (site.Runtime != "")
                list.Add(new Pill(site.Runtime == "octane" ? "Octane" : "FrankenPHP", new SolidColorBrush(Color.Parse("#8E44AD")), false));
            if (site.Valid && CustomCerts.For(site) != null)
            {
                var problem = CustomCerts.Problem(site);
                var c = new Pill(I18n.T("cert.badge"), problem == null ? Ui.Green : Ui.Orange, false);
                ToolTip.SetTip(c, problem ?? CustomCerts.CrtOf(site));
                list.Add(c);
            }
            return list;
        }

        /// <summary>The site card shows the aliases under the address (tooltip of the address too).</summary>
        public static string AliasText(Site site) { return site.Aliases.Count == 0 ? "" : string.Join(", ", site.Aliases); }

        // ---------------- archived list ----------------
        public static Button ArchivedButton(Window owner)
        {
            var b = Ui.Button(I18n.T("archive.list", 0), async () => await new ArchivedForm().ShowDialog(owner), null, "box");
            b.IsVisible = false;
            return b;
        }

        public static void UpdateArchived(Button b)
        {
            if (b == null) return;
            int n = Features.IsOn("archive") ? Sites.ArchivedSites().Count : 0;
            SetContent(b, "box", I18n.T("archive.list", n));
            b.IsVisible = n > 0;
        }

        /// <summary>Replaces the glyph and text of a button made by Ui.Button.</summary>
        internal static void SetContent(Button b, string glyph, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(new Glyph { Name_ = glyph, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            b.Content = row;
            Avalonia.Automation.AutomationProperties.SetName(b, text);
        }

        internal static Site Sample()
        {
            return new Site
            {
                Name = "shop", Host = "shop.portly", Folder = Path.Combine(Paths.Root, "www", "shop"), DocRoot = Path.Combine(Paths.Root, "www", "shop"),
                Valid = true, Aliases = new List<string> { "shop.test", "*.shop.portly" },
            };
        }
    }

    /// <summary>Site menu > Domains: extra names and wildcard subdomains for one site.</summary>
    public class DomainsForm : PortlyDialog
    {
        readonly ObservableCollection<string> _items = new ObservableCollection<string>();
        readonly ListBox _list;
        readonly TextBox _box;
        readonly TextBlock _msg;
        readonly Site _site;
        readonly bool _preview;

        public DomainsForm(Site site) : this(site, null) { }

        DomainsForm(Site site, IEnumerable<string> sample) : base(I18n.T("aliases.title", site.Host), 500)
        {
            _site = site;
            _preview = sample != null;
            Body.Children.Add(Ui.Label(I18n.T("aliases.intro", site.Host, Settings.Current.Tld), "muted"));
            foreach (var a in sample ?? new Ini(Paths.SitesFile).Get(site.Name, "aliases", "").Split(',').Select(x => x.Trim()).Where(x => x != "")) _items.Add(a);
            _list = new ListBox { ItemsSource = _items, Height = 130 };
            Body.Children.Add(_list);
            _box = new TextBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(_box, I18n.T("aliases.add"));
            var row = Ui.Row(_box, Ui.Button(I18n.T("aliases.add"), Add, null, "plus"),
                Ui.Button(I18n.T("aliases.remove"), () => { if (_list.SelectedItem != null) _items.Remove((string)_list.SelectedItem); }));
            Body.Children.Add(row);
            _msg = Ui.Label("", "small");
            Body.Children.Add(_msg);
            Body.Children.Add(Ui.Label(I18n.T("aliases.hint"), "muted small"));
            Buttons.Children.Add(Ui.Button(I18n.T("common.save"), Save, "primary"));
            Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(false)));
            _box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Add(); } };
            Opened += (s, e) => _box.Focus();
        }

        void Add()
        {
            _msg.Foreground = Ui.Red;
            var v = (_box.Text ?? "").Trim().ToLowerInvariant().TrimEnd('.', '/');
            if (v.StartsWith("http://") || v.StartsWith("https://")) { try { v = new Uri(v).Host; } catch { } }
            if (!Sites.IsValidAlias(v)) { _msg.Text = I18n.T("aliases.invalid", v); return; }
            if (v == _site.Host || v == "*." + _site.Host || _items.Contains(v)) { _msg.Text = I18n.T("aliases.duplicate", v); return; }
            var other = ServiceManager.CurrentSites.FirstOrDefault(x => x.Name != _site.Name && (x.Host == v || x.Aliases.Contains(v)));
            if (other != null) { _msg.Text = I18n.T("aliases.taken", v, other.Name); return; }
            _items.Add(v);
            _box.Text = "";
            // wildcards cannot go into the hosts file: explain the .localhost way
            _msg.Foreground = Ui.Muted;
            _msg.Text = v.StartsWith("*.") && HostsFile.NeedsEntry(v.Substring(2)) ? I18n.T("aliases.wildcardNote", v.Substring(2)) : "";
        }

        void Save()
        {
            if (!_preview)
            {
                Sites.SetAliases(_site.Name, _items);
                Log.Info(I18n.T("aliases.saved", _site.Name, _items.Count));
            }
            Close(true);
        }

        public static Window Preview() { return new DomainsForm(SitesWebUi.Sample(), new[] { "shop.test", "*.shop.portly", "www.shop.portly" }); }
    }

    /// <summary>Site menu > Certificate: use an own .pfx or .crt + .key instead of Portly's certificate.</summary>
    public class CertForm : PortlyDialog
    {
        readonly Site _site;
        readonly TextBox _cert, _key, _pass;
        readonly TextBlock _result;
        readonly Button _ok;
        CustomCerts.Candidate _checked;
        bool _confirmStep;

        public CertForm(Site site) : base(I18n.T("cert.title", site.Host), 520)
        {
            _site = site;
            Body.Children.Add(Ui.Label(I18n.T("cert.intro", string.Join(", ", site.AllNames)), "muted"));
            _cert = new TextBox();
            Body.Children.Add(Ui.Field(I18n.T("cert.file"), FileRow(_cert, "Certificate (*.pfx;*.p12;*.crt;*.pem;*.cer)", "*.pfx", "*.p12", "*.crt", "*.pem", "*.cer")));
            _key = new TextBox();
            Body.Children.Add(Ui.Field(I18n.T("cert.keyFile"), FileRow(_key, "Private key (*.key;*.pem)", "*.key", "*.pem")));
            _pass = new TextBox { PasswordChar = '•', Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            Body.Children.Add(Ui.Field(I18n.T("cert.password"), _pass));
            _result = Ui.Label("", "muted small");
            Body.Children.Add(_result);
            _ok = Ui.Button(I18n.T("cert.use"), Use, "primary");
            Buttons.Children.Add(_ok);
            Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(false)));
            EventHandler<TextChangedEventArgs> reset = (s, e) => { _checked = null; _confirmStep = false; _ok.Content = I18n.T("cert.use"); };
            _cert.TextChanged += reset; _key.TextChanged += reset; _pass.TextChanged += reset;
        }

        Control FileRow(TextBox box, string name, params string[] patterns)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            grid.Children.Add(box);
            var browse = Ui.Button(I18n.T("settings.browse"), () => Pick(box, name, patterns), null, "folder");
            Grid.SetColumn(browse, 1);
            grid.Children.Add(browse);
            return grid;
        }

        async Task Pick(TextBox box, string name, string[] patterns)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(name) { Patterns = patterns }, FilePickerFileTypes.All },
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path != null) box.Text = path;
        }

        void Fail(string text) { _result.Foreground = Ui.Red; _result.Text = text; }

        void Use()
        {
            if (_checked == null)
            {
                try { _checked = CustomCerts.Load((_cert.Text ?? "").Trim(), (_key.Text ?? "").Trim(), _pass.Text ?? "", _site); }
                catch (Exception ex) { Fail(ex.Message); return; }
            }
            var c = _checked;
            var warn = new List<string>();
            if (c.Expired) warn.Add(I18n.T("cert.expired", c.Cert.NotAfter.ToShortDateString()));
            if (c.Uncovered.Count > 0) warn.Add(I18n.T("cert.notCovered", string.Join(", ", c.Uncovered)));
            // warnings: second click on the same button confirms (no second dialog)
            if (warn.Count > 0 && !_confirmStep)
            {
                _result.Foreground = Ui.Orange;
                _result.Text = string.Join("\n", warn) + "\n" + I18n.T("cert.useAnyway");
                _ok.Content = I18n.T("cert.useAnywayButton");
                _confirmStep = true;
                return;
            }
            try { CustomCerts.Install(_site, c); }
            catch (Exception ex) { Fail(ex.Message); return; }
            Log.Info(I18n.T("cert.installed", _site.Host, c.Cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)));
            Close(true);
        }

        public static Window Preview()
        {
            var d = new CertForm(SitesWebUi.Sample());
            d._cert.Text = @"C:\certs\shop.example.com.crt";
            d._key.Text = @"C:\certs\shop.example.com.key";
            d._result.Foreground = Ui.Orange;
            d._result.Text = I18n.T("cert.notCovered", "shop.portly") + "\n" + I18n.T("cert.useAnyway");
            d._ok.Content = I18n.T("cert.useAnywayButton");
            return d;
        }
    }

    /// <summary>Archived projects with a "Restore" button each.</summary>
    public class ArchivedForm : PortlyDialog
    {
        readonly List<Site> _sample;

        public ArchivedForm() : this(null) { }

        ArchivedForm(List<Site> sample) : base(I18n.T("archive.title"), 500)
        {
            _sample = sample;
            Fill();
        }

        void Fill()
        {
            Body.Children.Clear();
            Buttons.Children.Clear();
            Body.Children.Add(Ui.Label(I18n.T("archive.intro"), "muted"));
            var list = _sample ?? Sites.ArchivedSites();
            if (list.Count == 0) Body.Children.Add(Ui.Label(I18n.T("archive.none")));
            foreach (var site in list)
            {
                var st = site;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
                var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                name.Children.Add(Ui.Label(st.Name, "bold"));
                var folder = Ui.Label(st.Folder, "muted small");
                folder.TextWrapping = TextWrapping.NoWrap;
                folder.TextTrimming = TextTrimming.CharacterEllipsis;
                ToolTip.SetTip(folder, st.Folder);
                name.Children.Add(folder);
                grid.Children.Add(name);
                var restore = Ui.Button(I18n.T("archive.restore"), async () =>
                {
                    if (_sample == null)
                    {
                        Sites.SetArchived(st.Name, false);
                        Log.Info(I18n.T("archive.restored", st.Name));
                    }
                    else _sample.Remove(st);
                    Fill();
                    if (_sample == null) await ServiceManager.ReloadWebAsync();
                }, "primary");
                Grid.SetColumn(restore, 1);
                grid.Children.Add(restore);
                Body.Children.Add(Ui.Card(grid));
            }
            Buttons.Children.Add(Ui.Button(I18n.T("common.close"), () => Close()));
        }

        public static Window Preview()
        {
            return new ArchivedForm(new List<Site>
            {
                new Site { Name = "old-shop", Folder = @"C:\Portly\www\old-shop" },
                new Site { Name = "clients/acme", Folder = @"C:\Portly\www\clients\acme" },
            });
        }
    }
}
