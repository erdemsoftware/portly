using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>
    /// PHP extensions of one PHP version: the bundled ones (switch on / off in php.ini) and verified downloads
    /// that match the PHP build (install / remove). Opened from the PHP menu and the packages window.
    /// </summary>
    public class PhpExtensionsForm : Form
    {
        class Row
        {
            public string Id;
            public bool Bundled;      // ships with PHP (ext\php_x.dll) and was not installed by Portly
            public bool Enabled;
            public ExtInfo Info;      // downloadable
            public bool Installed;
            public string Why;        // reason when no download fits
        }

        readonly ComboBox _php;
        readonly Label _build, _status;
        readonly ListView _list;
        readonly ProgressBar _progress;
        readonly Button _install, _remove, _toggle, _close;
        CancellationTokenSource _cts;
        bool _busy;

        string PhpDir { get { var i = _php.SelectedItem as Item; return i == null ? null : Path.Combine(Paths.BinOf("php"), i.Value); } }

        public PhpExtensionsForm(string phpFolder = null)
        {
            Ui.Apply(this);
            Text = I18n.T("ext.title");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = Ui.S(760, 560);
            MinimumSize = Ui.S(600, 420);

            var top = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = Ui.P(14, 12, 14, 6), BackColor = Ui.Bg };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            _php = new ThemedCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(200), Margin = Ui.P(0, 3, 8, 0) };
            foreach (var f in PackageTypes.Installed("php")) _php.Items.Add(new Item("PHP " + PackageTypes.Label(f), f));
            var want = phpFolder ?? PackageTypes.ActiveFolder("php");
            _php.SelectedItem = _php.Items.Cast<Item>().FirstOrDefault(i => string.Equals(i.Value, want, StringComparison.OrdinalIgnoreCase));
            if (_php.SelectedIndex < 0 && _php.Items.Count > 0) _php.SelectedIndex = 0;
            _php.SelectedIndexChanged += (s, e) => Fill();
            bar.Controls.Add(Ui.Label(I18n.T("ext.phpVersion")));
            bar.Controls.Add(_php);
            _build = new Label { AutoSize = true, ForeColor = Ui.Muted, Margin = Ui.P(0, 7, 0, 0) };
            bar.Controls.Add(_build);
            var intro = new Label { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(Ui.S(720), 0), Padding = Ui.P(0, 0, 0, 8), Text = I18n.T("ext.intro") };
            top.Controls.Add(bar);
            top.Controls.Add(intro);

            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = BorderStyle.FixedSingle, ShowItemToolTips = true };
            _list.Columns.Add(I18n.T("ext.colName"), Ui.S(170));
            _list.Columns.Add(I18n.T("col.version"), Ui.S(80));
            _list.Columns.Add(I18n.T("col.status"), Ui.S(170));
            _list.Columns.Add(I18n.T("col.note"), Ui.S(270));
            _list.Groups.Add(new ListViewGroup("dl", I18n.T("ext.groupDownload")));
            _list.Groups.Add(new ListViewGroup("bundled", I18n.T("ext.groupBundled")));
            // themed column headers and readable group titles in dark mode (as in the packages window)
            _list.OwnerDraw = true;
            _list.DrawItem += (s, e) => e.DrawDefault = true;
            _list.DrawSubItem += (s, e) => e.DrawDefault = true;
            _list.DrawColumnHeader += (s, e) =>
            {
                var p = Theme.Current;
                using (var b = new SolidBrush(p.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var pen = new Pen(p.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Ui.Bold, new Rectangle(e.Bounds.X + Ui.S(6), e.Bounds.Y, e.Bounds.Width - Ui.S(6), e.Bounds.Height), p.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            };
            if (Theme.IsDark)
            {
                _list.BorderStyle = BorderStyle.None;
                _list.HandleCreated += (s, e) => { try { SetWindowTheme(_list.Handle, "DarkMode_ItemsView", null); } catch { } };
            }
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.DoubleClick += async (s, e) => await PrimaryAction();
            var host = new Panel { Dock = DockStyle.Fill, Padding = Ui.P(14, 0, 14, 0), BackColor = Ui.Bg };
            host.Controls.Add(_list);

            var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = Ui.P(14, 8, 14, 12), BackColor = Ui.Bg };
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = Ui.S(8), Visible = false, Style = ProgressBarStyle.Marquee };
            _status = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Ui.Muted, Padding = Ui.P(0, 6, 0, 6), MaximumSize = new Size(Ui.S(720), 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            _install = Ui.Button(I18n.T("ext.install"), async (s, e) => await InstallSelected(), true);
            _remove = Ui.Button(I18n.T("pkg.remove"), async (s, e) => await RemoveSelected());
            _toggle = Ui.Button(I18n.T("ext.enable"), async (s, e) => await ToggleSelected());
            var ini = Ui.Button("php.ini", (s, e) => { if (PhpDir != null) WinUtil.Edit(PhpConfig.IniPath(PhpDir)); });
            _close = Ui.Button(I18n.T("common.close"), (s, e) => { if (_busy) { if (_cts != null) _cts.Cancel(); } else Close(); });
            buttons.Controls.AddRange(new Control[] { _install, _toggle, _remove, ini, _close });
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(_status);
            bottom.Controls.Add(_progress);

            Controls.Add(host);
            Controls.Add(bottom);
            Controls.Add(top);
            host.BringToFront();
            FormClosing += (s, e) => { if (_busy) e.Cancel = true; };

            PhpExtensions.LoadLocal();
            Fill();
            Shown += async (s, e) =>
            {
                // newest list (modern edition); the embedded copy is shown meanwhile
                await PhpExtensions.RefreshAsync();
                if (!IsDisposed && !_busy) Fill();
            };
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        Row Selected { get { return _list.SelectedItems.Count == 0 ? null : (Row)_list.SelectedItems[0].Tag; } }

        void Fill()
        {
            var dir = PhpDir;
            var sel = Selected == null ? null : Selected.Id;
            _list.BeginUpdate();
            _list.Items.Clear();
            if (dir == null) { _list.EndUpdate(); _build.Text = I18n.T("ext.noPhp"); UpdateButtons(); return; }
            var build = PhpExtensions.Detect(dir);
            _build.Text = build.ToString();
            var ours = PhpExtensions.InstalledByPortly(dir);
            var enabled = PhpConfig.ListExtensions(dir).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var x in PhpExtensions.All.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var r = new Row { Id = x.Id, Info = x, Installed = PhpExtensions.IsInstalled(dir, x.Id), Why = PhpExtensions.Unavailable(x, build) };
                bool on;
                r.Enabled = enabled.TryGetValue(x.Id, out on) && on;
                // shipped with this PHP build (e.g. a PHP that already has redis): handled as bundled below
                if (r.Installed && !ours.Contains(x.Id)) continue;
                var b = x.BuildFor(build);
                string status = r.Installed ? I18n.T(r.Enabled ? "ext.installedOn" : "ext.installedOff", PhpExtensions.InstalledVersion(dir, x.Id))
                    : r.Why != null ? I18n.T("ext.notAvailable") : I18n.T("ext.notInstalled");
                var note = r.Why ?? (x.Description + (x.Note != "" ? "  " + x.Note : ""));
                var it = new ListViewItem(new[] { x.Name, b != null ? b.Version : "-", status, note })
                {
                    Tag = r, Name = x.Id, Group = _list.Groups["dl"], ToolTipText = x.Description + (x.Note != "" ? "\n" + x.Note : "") + (r.Why != null ? "\n" + r.Why : ""),
                    ForeColor = r.Installed ? (r.Enabled ? Ui.Green : Ui.Text) : r.Why != null ? Ui.Muted : Ui.Text,
                };
                _list.Items.Add(it);
            }
            foreach (var kv in PhpConfig.ListExtensions(dir))
            {
                if (ours.Contains(kv.Key)) continue;
                var r = new Row { Id = kv.Key, Bundled = true, Enabled = kv.Value, Installed = true };
                _list.Items.Add(new ListViewItem(new[] { kv.Key, "", I18n.T(kv.Value ? "ext.on" : "ext.off"), "" })
                    { Tag = r, Name = kv.Key, Group = _list.Groups["bundled"], ForeColor = kv.Value ? Ui.Green : Ui.Text });
            }
            _list.EndUpdate();
            if (sel != null && _list.Items.ContainsKey(sel)) { var it = _list.Items[sel]; it.Selected = true; it.EnsureVisible(); }
            UpdateButtons();
        }

        void UpdateButtons()
        {
            var r = Selected;
            _install.Enabled = !_busy && r != null && !r.Bundled && !r.Installed && r.Why == null;
            _remove.Enabled = !_busy && r != null && !r.Bundled && r.Installed;
            _toggle.Enabled = !_busy && r != null && r.Installed;
            _toggle.Text = I18n.T(r != null && r.Enabled ? "ext.disable" : "ext.enable");
            _close.Text = _busy ? I18n.T("common.cancel") : I18n.T("common.close");
            _php.Enabled = !_busy;
        }

        async Task PrimaryAction()
        {
            var r = Selected;
            if (r == null || _busy) return;
            if (r.Installed) await ToggleSelected();
            else if (r.Why == null) await InstallSelected();
        }

        async Task Busy(Func<Task> work)
        {
            _busy = true;
            _cts = new CancellationTokenSource();
            _progress.Visible = true;
            UpdateButtons();
            try { await work(); }
            catch (OperationCanceledException) { _status.Text = I18n.T("pkg.cancelled"); }
            catch (Exception ex) { Log.Error("PHP extensions", ex); _status.Text = ""; Ui.Error(this, ex.Message); }
            finally
            {
                _busy = false;
                _progress.Visible = false;
                Fill();
            }
        }

        void Status(string text) { if (IsHandleCreated) BeginInvoke((Action)(() => _status.Text = text)); }

        async Task InstallSelected()
        {
            var r = Selected;
            var dir = PhpDir;
            if (r == null || dir == null || r.Info == null) return;
            await Busy(async () =>
            {
                await PhpExtensions.InstallAsync(dir, r.Id, Status, _cts.Token);
                Status(I18n.T("ext.restarting"));
                await RestartIfUsed(dir);
                _status.Text = I18n.T("ext.installedDone", r.Info.Name, PackageTypes.Label(Path.GetFileName(dir)));
            });
        }

        async Task RemoveSelected()
        {
            var r = Selected;
            var dir = PhpDir;
            if (r == null || dir == null || !Ui.Confirm(this, I18n.T("ext.removeConfirm", r.Info != null ? r.Info.Name : r.Id, PackageTypes.Label(Path.GetFileName(dir))))) return;
            await Busy(async () =>
            {
                // switch it off and restart first: a running php-cgi keeps the DLL locked
                PhpConfig.ToggleExtension(dir, r.Id, false);
                await RestartIfUsed(dir);
                await Task.Run(() => PhpExtensions.Remove(dir, r.Id));
                _status.Text = I18n.T("ext.removedDone", r.Info != null ? r.Info.Name : r.Id);
            });
        }

        async Task ToggleSelected()
        {
            var r = Selected;
            var dir = PhpDir;
            if (r == null || dir == null) return;
            await Busy(async () =>
            {
                bool on = !r.Enabled;
                PhpConfig.ToggleExtension(dir, r.Id, on);
                if (on)
                {
                    var err = await Task.Run(() => PhpExtensions.CheckLoads(dir, r.Id));
                    if (err != null)
                    {
                        PhpConfig.ToggleExtension(dir, r.Id, false);
                        throw new Exception(I18n.T("ext.startFailed", r.Id, err));
                    }
                }
                Log.Info(I18n.T(on ? "log.extOn" : "log.extOff", r.Id));
                await RestartIfUsed(dir);
            });
        }

        /// <summary>Restart the web server / PHP pools when this PHP version is running.</summary>
        static async Task RestartIfUsed(string dir)
        {
            var folder = Path.GetFileName(dir);
            if (ServiceManager.Php.RunningFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) await ServiceManager.ReloadWebAsync();
        }

        // ---------------- after a PHP install / update ----------------
        /// <summary>
        /// A PHP version was installed or updated: offer to install the downloaded extensions of the previous one there too,
        /// and say which ones have no build for the new version.
        /// </summary>
        public static async Task OfferCopyAsync(IWin32Window owner, string fromFolder, string toFolder)
        {
            if (!Features.IsOn("extensions") || string.IsNullOrEmpty(fromFolder) || string.IsNullOrEmpty(toFolder)) return;
            if (string.Equals(fromFolder, toFolder, StringComparison.OrdinalIgnoreCase)) return;
            var from = Path.Combine(Paths.BinOf("php"), fromFolder);
            var to = Path.Combine(Paths.BinOf("php"), toFolder);
            if (!Directory.Exists(from) || !Directory.Exists(to)) return;
            var plan = await Task.Run(() => PhpExtensions.PlanCopy(from, to));
            if (plan.Item1.Count == 0 && plan.Item2.Count == 0) return;
            string fromLabel = PackageTypes.Label(fromFolder), toLabel = PackageTypes.Label(toFolder);
            var missing = plan.Item2.Count == 0 ? "" : "\n\n" + I18n.T("ext.copyMissing", toLabel) + "\n" + string.Join("\n", plan.Item2.Select(m => "• " + m));
            if (plan.Item1.Count == 0) { Ui.Info(owner, I18n.T("ext.copyNone", fromLabel, toLabel) + missing); return; }
            var names = string.Join(", ", plan.Item1.Select(x => x.Name));
            if (!Ui.Confirm(owner, I18n.T("ext.copyConfirm", fromLabel, toLabel, names) + missing)) return;
            var failed = new List<string>();
            foreach (var x in plan.Item1)
            {
                try { await PhpExtensions.InstallAsync(to, x.Id, null, CancellationToken.None); }
                catch (Exception ex) { failed.Add(x.Name + ": " + ex.Message); }
            }
            await RestartIfUsed(to);
            if (failed.Count > 0) Ui.Error(owner, string.Join("\n\n", failed));
        }
    }
}
