using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>
    /// Main window pieces of the SitesWeb features (domains, own certificates, archive, FrankenPHP, Firefox / Java trust).
    /// MainForm only calls these from its existing menu builders and site cards.
    /// </summary>
    public static class SitesWebUi
    {
        static async void Reload() { await ServiceManager.ReloadWebAsync(); }

        // ---------------- site menu ----------------
        public static void AddSiteItems(Form owner, ToolStripItemCollection items, Site site)
        {
            if (site.Valid && Features.IsOn("aliases"))
                items.Add(I18n.T("aliases.menu"), null, (s, e) =>
                {
                    using (var f = new DomainsForm(site)) if (f.ShowDialog(owner) == DialogResult.OK) Reload();
                });

            if (site.Valid && Features.IsOn("customCerts") && Settings.Current.Ssl)
            {
                var cert = new ToolStripMenuItem(I18n.T("cert.menu"));
                cert.DropDownItems.Add(I18n.T("cert.choose"), null, (s, e) =>
                {
                    using (var f = new CertForm(site)) if (f.ShowDialog(owner) == DialogResult.OK) Reload();
                });
                var back = new ToolStripMenuItem(I18n.T("cert.back")) { Enabled = CustomCerts.Has(site) };
                back.Click += (s, e) => { CustomCerts.Remove(site); Log.Info(I18n.T("cert.removed", site.Host)); Reload(); };
                cert.DropDownItems.Add(back);
                items.Add(cert);
            }

            if (site.Valid && !site.IsGroup && Features.IsOn("frankenphp") && !AppInfo.IsLegacy)
                items.Add(RuntimeMenu(owner, site));

            if (Features.IsOn("archive"))
                items.Add(I18n.T("archive.menu"), null, (s, e) =>
                {
                    if (!Ui.Confirm(owner, I18n.T("archive.confirm", site.Name, site.Folder))) return;
                    Sites.SetArchived(site.Name, true);
                    Log.Info(I18n.T("archive.done", site.Name));
                    Reload();
                });
        }

        static ToolStripMenuItem RuntimeMenu(Form owner, Site site)
        {
            var menu = new ToolStripMenuItem(I18n.T("franken.menu"));
            var dir = PackageTypes.ActiveDir("frankenphp");
            Action<string> set = rt =>
            {
                if (rt != "" && dir == null)
                {
                    if (Ui.Confirm(owner, I18n.T("franken.install")))
                        using (var f = new PackagesForm("frankenphp", false, null)) f.ShowDialog(owner);
                    if (PackageTypes.ActiveDir("frankenphp") == null) return;
                }
                if (rt == "octane" && !FrankenPhpService.HasOctane(site)) { Ui.Info(owner, I18n.T("franken.octaneHow", site.Folder)); return; }
                if (rt == "octane" && !File.Exists(FrankenPhpService.WorkerFile(site))) { Ui.Info(owner, I18n.T("franken.workerHow", site.Folder)); return; }
                Sites.SetRuntime(site.Name, rt);
                Log.Info(I18n.T("franken.set", site.Name, rt == "" ? "PHP (FastCGI)" : rt == "octane" ? "FrankenPHP worker" : "FrankenPHP"));
                Reload();
            };
            foreach (var opt in new[] { Tuple.Create("", I18n.T("franken.classic")), Tuple.Create("frankenphp", "FrankenPHP"), Tuple.Create("octane", I18n.T("franken.worker")) })
            {
                var o = opt;
                var mi = new ToolStripMenuItem(o.Item2) { Checked = site.Runtime == o.Item1 };
                mi.Click += (s, e) => set(o.Item1);
                menu.DropDownItems.Add(mi);
            }
            menu.DropDownItems.Add(new ToolStripSeparator());
            var ver = dir == null ? I18n.T("svc.notInstalled") : FrankenPhpService.VersionText(dir);
            menu.DropDownItems.Add(new ToolStripMenuItem(ver) { Enabled = false });
            menu.DropDownItems.Add(new ToolStripMenuItem(I18n.T("franken.noSwoole")) { Enabled = false });
            return menu;
        }

        // ---------------- SSL menu ----------------
        public static void AddSslItems(Form owner, ToolStripItemCollection items)
        {
            if (FirefoxTrust.HasProfiles)
                items.Add(I18n.T("firefox.menu"), null, (s, e) =>
                {
                    int n = FirefoxTrust.EnableAll();
                    Ui.Info(owner, n > 0 ? I18n.T("firefox.doneRestart", n) : I18n.T("firefox.already"));
                });
            var ff = FirefoxTrust.InstallDir();
            if (ff != null && !FirefoxTrust.PolicySet(ff))
                items.Add(I18n.T("firefox.policyMenu"), null, (s, e) =>
                {
                    if (!Ui.Confirm(owner, I18n.T("firefox.policyConfirm", FirefoxTrust.PolicyFile(ff)))) return;
                    var code = WinUtil.RunElevated(Paths.Exe, "--firefox-policy \"" + ff + "\"");
                    if (code == 0) Ui.Info(owner, I18n.T("firefox.doneRestart", 1));
                    else if (code != -2) Ui.Error(owner, I18n.T("firefox.policyFailed", code));
                });
            var java = JavaTrust.FindHome();
            if (java != null && JavaTrust.Cacerts(java) != null)
                items.Add(I18n.T("java.menu"), null, (s, e) =>
                {
                    if (JavaTrust.IsImported(java)) { Ui.Info(owner, I18n.T("java.already", java)); return; }
                    if (!Ui.Confirm(owner, I18n.T("java.confirm", java, JavaTrust.ImportCommand(java)))) return;
                    var err = JavaTrust.Import(java);
                    if (err == null) Ui.Info(owner, I18n.T("java.done", java)); else Ui.Error(owner, err);
                });
        }

        // ---------------- site card ----------------
        public static List<Control> CardBadges(Site site)
        {
            var p = Theme.Current;
            var list = new List<Control>();
            if (site.Aliases.Count > 0)
            {
                var a = new Pill { Text = "+" + site.Aliases.Count, Color = p.Accent, Dot = false };
                new ToolTip().SetToolTip(a, string.Join("\n", site.Aliases));
                list.Add(a);
            }
            if (site.Runtime != "")
                list.Add(new Pill { Text = site.Runtime == "octane" ? "Octane" : "FrankenPHP", Color = ColorTranslator.FromHtml("#8E44AD"), Dot = false });
            if (site.Valid && CustomCerts.For(site) != null)
            {
                var problem = CustomCerts.Problem(site);
                var c = new Pill { Text = I18n.T("cert.badge"), Color = problem == null ? p.Green : p.Orange, Dot = false };
                new ToolTip().SetToolTip(c, problem ?? CustomCerts.CrtOf(site));
                list.Add(c);
            }
            return list;
        }

        /// <summary>The site card shows the aliases under the address (tooltip of the address too).</summary>
        public static string AliasText(Site site) { return site.Aliases.Count == 0 ? "" : string.Join(", ", site.Aliases); }

        // ---------------- archived list ----------------
        public static ModernButton ArchivedButton(Form owner)
        {
            var b = Ui.Button(I18n.T("archive.list", 0), null, false, "box");
            b.Visible = false;
            b.Click += (s, e) => { using (var f = new ArchivedForm()) f.ShowDialog(owner); };
            return b;
        }

        public static void UpdateArchived(ModernButton b)
        {
            if (b == null) return;
            int n = Features.IsOn("archive") ? Sites.ArchivedSites().Count : 0;
            b.Text = I18n.T("archive.list", n);
            b.AccessibleName = b.Text;
            b.Visible = n > 0;
        }
    }

    /// <summary>Site menu > Domains: extra names and wildcard subdomains for one site.</summary>
    public class DomainsForm : Form
    {
        readonly ListBox _list;
        readonly TextBox _box;
        readonly Label _msg;
        readonly Site _site;

        public DomainsForm(Site site)
        {
            _site = site;
            Ui.Apply(this);
            Text = I18n.T("aliases.title", site.Host);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(16);
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var intro = Ui.Label(I18n.T("aliases.intro", site.Host, Settings.Current.Tld), Ui.Font, Ui.Muted);
            intro.MaximumSize = new Size(Ui.S(440), 0);
            t.Controls.Add(intro);
            _list = new ListBox { Width = Ui.S(440), Height = Ui.S(130), IntegralHeight = false };
            foreach (var a in new Ini(Paths.SitesFile).Get(site.Name, "aliases", "").Split(',').Select(x => x.Trim()).Where(x => x != "")) _list.Items.Add(a);
            t.Controls.Add(_list);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.P(0, 6, 0, 0) };
            _box = new TextBox { Width = Ui.S(260), Margin = Ui.P(0, 4, 8, 0) };
            row.Controls.Add(_box);
            row.Controls.Add(Ui.Button(I18n.T("aliases.add"), (s, e) => Add(), false, "plus"));
            row.Controls.Add(Ui.Button(I18n.T("aliases.remove"), (s, e) => { if (_list.SelectedIndex >= 0) _list.Items.RemoveAt(_list.SelectedIndex); }));
            t.Controls.Add(row);
            _msg = Ui.Label("", Ui.Small, Ui.Red);
            _msg.MaximumSize = new Size(Ui.S(440), 0);
            t.Controls.Add(_msg);
            var hint = Ui.Label(I18n.T("aliases.hint"), Ui.Small, Ui.Muted);
            hint.MaximumSize = new Size(Ui.S(440), 0);
            t.Controls.Add(hint);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 10, 0, 0) };
            var cancel = Ui.Button(I18n.T("common.cancel"), null);
            cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.Button(I18n.T("common.save"), (s, e) => Save(), true);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            t.Controls.Add(buttons);
            Controls.Add(t);
            AcceptButton = null;
            CancelButton = cancel;
            _box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Add(); } };
        }

        void Add()
        {
            _msg.ForeColor = Ui.Red;
            var v = _box.Text.Trim().ToLowerInvariant().TrimEnd('.', '/');
            if (v.StartsWith("http://") || v.StartsWith("https://")) { try { v = new Uri(v).Host; } catch { } }
            if (!Sites.IsValidAlias(v)) { _msg.Text = I18n.T("aliases.invalid", v); return; }
            if (v == _site.Host || v == "*." + _site.Host || _list.Items.Contains(v)) { _msg.Text = I18n.T("aliases.duplicate", v); return; }
            var other = ServiceManager.CurrentSites.FirstOrDefault(x => x.Name != _site.Name && (x.Host == v || x.Aliases.Contains(v)));
            if (other != null) { _msg.Text = I18n.T("aliases.taken", v, other.Name); return; }
            _list.Items.Add(v);
            _box.Clear();
            // wildcards cannot go into the hosts file: explain the .localhost way
            _msg.ForeColor = Ui.Muted;
            _msg.Text = v.StartsWith("*.") && HostsFile.NeedsEntry(v.Substring(2)) ? I18n.T("aliases.wildcardNote", v.Substring(2)) : "";
        }

        void Save()
        {
            Sites.SetAliases(_site.Name, _list.Items.Cast<string>());
            Log.Info(I18n.T("aliases.saved", _site.Name, _list.Items.Count));
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Site menu > Certificate: use an own .pfx or .crt + .key instead of Portly's certificate.</summary>
    public class CertForm : Form
    {
        readonly Site _site;
        readonly TextBox _cert, _key, _pass;
        readonly Label _result;
        readonly ModernButton _ok;
        CustomCerts.Candidate _checked;
        bool _confirmStep;

        public CertForm(Site site)
        {
            _site = site;
            Ui.Apply(this);
            Text = I18n.T("cert.title", site.Host);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(16);
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var intro = Ui.Label(I18n.T("cert.intro", string.Join(", ", site.AllNames)), Ui.Font, Ui.Muted);
            intro.MaximumSize = new Size(Ui.S(460), 0);
            t.Controls.Add(intro);
            t.Controls.Add(Ui.Label(I18n.T("cert.file")));
            _cert = new TextBox { Width = Ui.S(340), Margin = Ui.P(0, 4, 8, 0) };
            t.Controls.Add(Row(_cert, () => Pick(_cert, "Certificate (*.pfx;*.p12;*.crt;*.pem;*.cer)|*.pfx;*.p12;*.crt;*.pem;*.cer")));
            t.Controls.Add(Ui.Label(I18n.T("cert.keyFile")));
            _key = new TextBox { Width = Ui.S(340), Margin = Ui.P(0, 4, 8, 0) };
            t.Controls.Add(Row(_key, () => Pick(_key, "Private key (*.key;*.pem)|*.key;*.pem")));
            t.Controls.Add(Ui.Label(I18n.T("cert.password")));
            _pass = new TextBox { Width = Ui.S(220), UseSystemPasswordChar = true };
            t.Controls.Add(_pass);
            _result = Ui.Label("", Ui.Small, Ui.Muted);
            _result.MaximumSize = new Size(Ui.S(460), 0);
            t.Controls.Add(_result);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 10, 0, 0) };
            var cancel = Ui.Button(I18n.T("common.cancel"), null);
            cancel.DialogResult = DialogResult.Cancel;
            _ok = Ui.Button(I18n.T("cert.use"), (s, e) => Use(), true);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);
            t.Controls.Add(buttons);
            Controls.Add(t);
            CancelButton = cancel;
            EventHandler reset = (s, e) => { _checked = null; _confirmStep = false; _ok.Text = I18n.T("cert.use"); };
            _cert.TextChanged += reset; _key.TextChanged += reset; _pass.TextChanged += reset;
        }

        Control Row(TextBox box, Action browse)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.P(0, 0, 0, 6) };
            row.Controls.Add(box);
            row.Controls.Add(Ui.Button(I18n.T("settings.browse"), (s, e) => browse(), false, "folder"));
            return row;
        }

        void Pick(TextBox box, string filter)
        {
            using (var d = new OpenFileDialog { Filter = filter })
                if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.FileName;
        }

        void Use()
        {
            if (_checked == null)
            {
                try { _checked = CustomCerts.Load(_cert.Text.Trim(), _key.Text.Trim(), _pass.Text, _site); }
                catch (Exception ex) { _result.ForeColor = Ui.Red; _result.Text = ex.Message; return; }
            }
            var c = _checked;
            var warn = new List<string>();
            if (c.Expired) warn.Add(I18n.T("cert.expired", c.Cert.NotAfter.ToShortDateString()));
            if (c.Uncovered.Count > 0) warn.Add(I18n.T("cert.notCovered", string.Join(", ", c.Uncovered)));
            // warnings: second click on the same button confirms (no second dialog)
            if (warn.Count > 0 && !_confirmStep)
            {
                _result.ForeColor = Ui.Orange;
                _result.Text = string.Join("\n", warn) + "\n" + I18n.T("cert.useAnyway");
                _ok.Text = I18n.T("cert.useAnywayButton");
                _confirmStep = true;
                return;
            }
            try { CustomCerts.Install(_site, c); }
            catch (Exception ex) { _result.ForeColor = Ui.Red; _result.Text = ex.Message; return; }
            Log.Info(I18n.T("cert.installed", _site.Host, c.Cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)));
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Archived projects with a "Restore" button each.</summary>
    public class ArchivedForm : Form
    {
        public ArchivedForm()
        {
            Ui.Apply(this);
            Text = I18n.T("archive.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(16);
            Fill();
        }

        void Fill()
        {
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
            var intro = Ui.Label(I18n.T("archive.intro"), Ui.Font, Ui.Muted);
            intro.MaximumSize = new Size(Ui.S(440), 0);
            t.Controls.Add(intro, 0, 0);
            t.SetColumnSpan(intro, 2);
            int r = 1;
            var list = Sites.ArchivedSites();
            if (list.Count == 0) t.Controls.Add(Ui.Label(I18n.T("archive.none")), 0, r++);
            foreach (var site in list)
            {
                var st = site;
                var name = Ui.Label(st.Name + "   " + st.Folder);
                name.MaximumSize = new Size(Ui.S(340), 0);
                t.Controls.Add(name, 0, r);
                t.Controls.Add(Ui.Button(I18n.T("archive.restore"), async (s, e) =>
                {
                    Sites.SetArchived(st.Name, false);
                    Log.Info(I18n.T("archive.restored", st.Name));
                    Fill();
                    await ServiceManager.ReloadWebAsync();
                }, true), 1, r++);
            }
            var close = Ui.Button(I18n.T("common.close"), null);
            close.DialogResult = DialogResult.Cancel;
            close.Margin = Ui.P(0, 12, 0, 0);
            t.Controls.Add(close, 1, r);
            Controls.Add(t);
            CancelButton = close;
            ResumeLayout(true);
            Ui.StyleTree(this);
        }
    }
}
