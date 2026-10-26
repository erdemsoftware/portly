using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>Starting a share from a project card or from "Shared projects".</summary>
    public static class ShareUi
    {
        /// <summary>Local network share (phone, tablet, another PC on the same router). Returns the address or null.</summary>
        public static string StartLan(IWin32Window owner, Site site)
        {
            if (ServiceManager.Web.State != ServiceState.Running) { Ui.Error(owner, I18n.T("main.webNotRunning")); return null; }
            // the explanation only the first time; a remembered share starts again directly
            if (!ShareStore.Get(site.Host).Lan && !Ui.Confirm(owner, I18n.T("lan.confirm", site.Host))) return null;
            try
            {
                var url = LanShare.Start(site);
                try { Clipboard.SetText(url); } catch { }
                ShowLanQr(owner, site.Host);
                return url;
            }
            catch (Exception ex) { Ui.Error(owner, ex.Message); return null; }
        }

        /// <summary>QR code + explanation for a running local network share (https: also the certificate code).</summary>
        public static void ShowLanQr(IWin32Window owner, string host)
        {
            var url = LanShare.UrlOf(host);
            if (url == null) return;
            bool https = url.StartsWith("https://");
            var text = I18n.T("lan.qrText");
            if (ShareStore.Get(host).LanPassword != "") text += "\n\n" + I18n.T("lan.qrPassword");
            if (https) text += "\n\n" + I18n.T("lan.qrHttps", "http" + url.Substring(5));
            QrDialog.ShowFor(owner, I18n.T("lan.qrTitle", host), text, url, https ? "http" + url.Substring(5) + "portly-ca" : null);
        }

        /// <summary>The QR window of whatever share of the project runs (local network first).</summary>
        public static void ShowQr(IWin32Window owner, string host)
        {
            if (LanShare.UrlOf(host) != null) ShowLanQr(owner, host);
            else if (Share.UrlOf(host) != null)
                QrDialog.ShowFor(owner, I18n.T("share.qrTitle"), I18n.T("share.qrText") + (ShareStore.Get(host).OnlinePassword != "" ? "\n\n" + I18n.T("lan.qrPassword") : ""), Share.UrlOf(host));
        }

        /// <summary>Internet share with a Cloudflare quick tunnel (asks every time: the site becomes public).</summary>
        public static async Task<string> StartOnline(IWin32Window owner, Site site, Action<string> status)
        {
            if (ServiceManager.Web.State != ServiceState.Running) { Ui.Error(owner, I18n.T("main.webNotRunning")); return null; }
            if (!Ui.Confirm(owner, I18n.T("share.confirm", site.Host))) return null;
            try
            {
                var url = await Share.StartAsync(site, status);
                try { Clipboard.SetText(url); } catch { }
                ShowQr(owner, site.Host);
                return url;
            }
            catch (Exception ex) { Ui.Error(owner, ex.Message); return null; }
        }
    }

    /// <summary>"Shared projects": every local network and internet share, running or stopped, with its address.</summary>
    public class SharesForm : Form
    {
        readonly FlowLayoutPanel _list;
        readonly Label _status;
        readonly Action _changed;

        public SharesForm()
        {
            Ui.Apply(this);
            Text = I18n.T("shares.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(22);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            t.Controls.Add(Ui.Label(I18n.T("shares.title"), Ui.Title));
            var intro = Ui.Label(I18n.T("shares.intro"), Ui.Font, Ui.Muted);
            intro.MaximumSize = new Size(Ui.S(660), 0);
            intro.Margin = Ui.P(0, 6, 0, 10);
            t.Controls.Add(intro);
            _list = new ScrollFlow { FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = Ui.S(690), Height = Ui.S(380), Margin = Padding.Empty };
            t.Controls.Add(_list);
            _status = Ui.Label("", Ui.Small, Ui.Muted);
            _status.Margin = Ui.P(0, 6, 0, 0);
            t.Controls.Add(_status);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 12, 0, 0) };
            var close = Ui.Button(I18n.T("common.close"), (s, e) => Close());
            close.Margin = Padding.Empty;
            buttons.Controls.Add(close);
            t.Controls.Add(buttons);
            Controls.Add(t);
            CancelButton = close;

            _changed = () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)Fill); } catch { } };
            ShareStore.Changed += _changed;
            // "2 min ago" and the time left move on by themselves
            var tick = new Timer { Interval = 30000 };
            tick.Tick += (s, e) => Fill();
            tick.Start();
            FormClosed += (s, e) => { ShareStore.Changed -= _changed; tick.Dispose(); };
            Fill();
        }

        static Site SiteOf(string host) { return ServiceManager.CurrentSites.FirstOrDefault(x => x.Valid && string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase)); }

        void Fill()
        {
            _list.SuspendLayout();
            foreach (Control c in _list.Controls.Cast<Control>().ToList()) c.Dispose();
            _list.Controls.Clear();
            var all = ShareStore.All();
            if (all.Count == 0)
            {
                var empty = Ui.Label(I18n.T("shares.empty"), Ui.Font, Ui.Muted);
                empty.MaximumSize = new Size(Ui.S(640), 0);
                empty.Margin = Ui.P(4, 20, 0, 0);
                _list.Controls.Add(empty);
            }
            foreach (var e in all)
            {
                if (e.Lan) _list.Controls.Add(Row(e, true));
                if (e.Online) _list.Controls.Add(Row(e, false));
            }
            _list.ResumeLayout(true);
            Ui.StyleTree(_list);
        }

        Control Row(ShareEntry e, bool lan)
        {
            var p = Theme.Current;
            var site = SiteOf(e.Host);
            bool running = lan ? LanShare.IsRunning(e.Host) : Share.IsRunning(e.Host);
            var url = lan ? (running ? LanShare.UrlOf(e.Host) : LanShare.AddressFor(e.LanPort)) : (running ? Share.UrlOf(e.Host) : e.OnlineUrl);
            var devices = running ? LanShare.DevicesOf(e.Host, !lan) : new List<ShareDevice>();
            var card = new Card { Size = Ui.S(660, running ? 128 : 104), Radius = 10, Margin = Ui.P(0, 0, 0, 8) };

            var name = Ui.Label(e.Host, Ui.Subtitle);
            name.AutoSize = false;
            name.AutoEllipsis = true;
            name.Size = Ui.S(250, 24);
            name.Location = new Point(Ui.S(14), Ui.S(10));
            var kind = new Pill { Text = lan ? I18n.T("shares.lan") : I18n.T("shares.online"), Color = lan ? p.Accent : ColorTranslator.FromHtml("#F38020"), Dot = false };
            kind.Location = new Point(Ui.S(270), Ui.S(11));
            // running but the web server is stopped: devices get a "start the web server" page
            bool webDown = running && LanShare.WebDown;
            var state = new Pill
            {
                Text = site == null ? I18n.T("shares.noProject") : webDown ? I18n.T("shares.webDown") : running ? I18n.T("shares.on") : I18n.T("shares.off"),
                Color = site == null ? p.Red : webDown ? p.Orange : running ? p.Green : p.Gray,
            };
            state.Location = new Point(kind.Right + Ui.S(6), Ui.S(11));
            card.Controls.AddRange(new Control[] { name, kind, state });

            Control address;
            if (url != "" && url != null)
            {
                var link = Ui.Link(url, () => WinUtil.Open(url));
                link.AutoSize = false;
                link.AutoEllipsis = true;
                link.Size = Ui.S(400, 20);
                address = link;
            }
            else address = Ui.Label(lan ? I18n.T("lan.noNetwork") : "-", Ui.Small, Ui.Muted);
            address.Location = new Point(Ui.S(14), Ui.S(40));
            card.Controls.Add(address);
            // a stopped internet share: the old address will not work again, the next start gives a new one
            var detail = !lan && !running && e.OnlineUrl != "" ? I18n.T("shares.oldUrl") : lan && e.LanPort > 0 ? I18n.T("shares.port", e.LanPort) : "";
            var expires = running ? LanShare.ExpiresAt(e.Host, !lan) : null;
            int minutes = lan ? e.LanMinutes : e.OnlineMinutes;
            var flags = string.Join("  ·  ", new[]
            {
                (lan ? e.LanPassword : e.OnlinePassword) != "" ? I18n.T("shares.hasPassword") : "",
                lan && e.LanAuto ? I18n.T("shares.autoOn") : "",
                expires != null ? I18n.T("shares.closesIn", Ago(expires.Value - DateTime.Now)) : minutes > 0 ? I18n.T("shares.limit", Duration(minutes)) : "",
                !lan && e.FixedHost != "" && !running ? e.FixedHost : "",
            }.Where(x => x != ""));
            var note = string.Join("  ·  ", new[] { detail, flags, e.Note }.Where(x => x != ""));
            if (note != "")
            {
                var n = Ui.Label(note, Ui.Small, Ui.Muted);
                n.AutoSize = false;
                n.AutoEllipsis = true;
                n.Size = Ui.S(400, 18);
                n.Location = new Point(Ui.S(14), Ui.S(64));
                new ToolTip().SetToolTip(n, note);
                card.Controls.Add(n);
            }
            if (running)
            {
                // who opened it, and PHP errors their pages caused (details in the Log page)
                int errors = devices.Sum(d => d.Errors);
                var text = devices.Count == 0 ? I18n.T("shares.noDevices")
                    : I18n.T("shares.devices", devices.Count) + ": " + string.Join(", ", devices.Take(2).Select(d => (d.Name != "" ? d.Name : d.Ip) + " (" + Ago(DateTime.Now - d.Last) + ")"))
                      + (devices.Count > 2 ? " ..." : "");
                var dl = Ui.Label((errors > 0 ? "⚠ " + I18n.T("shares.errors", errors) + "  ·  " : "") + text, Ui.Small, errors > 0 ? p.Orange : p.Muted);
                dl.AutoSize = false;
                dl.AutoEllipsis = true;
                dl.Size = Ui.S(400, 18);
                dl.Location = new Point(Ui.S(14), Ui.S(86));
                if (devices.Count > 0)
                    new ToolTip().SetToolTip(dl, string.Join("\n", devices.Select(d => d.Label + "  ·  " + I18n.T("shares.deviceLine", d.Requests, Ago(DateTime.Now - d.Last)) + (d.Errors > 0 ? "  ·  ⚠ " + d.Errors : ""))));
                card.Controls.Add(dl);
            }

            // actions on the right
            var actions = new List<Control>(); // placed straight on the card, right-aligned (a panel in between painted over them)
            if (site != null)
            {
                actions.Add(Small(running ? I18n.T("shares.stop") : I18n.T("shares.start"), running ? "stop" : "play", async () =>
                {
                    if (running) { if (lan) LanShare.Stop(e.Host); else Share.Stop(e.Host); }
                    else if (lan) ShareUi.StartLan(this, site);
                    else await ShareUi.StartOnline(this, site, s => { if (!IsDisposed) BeginInvoke((Action)(() => _status.Text = s)); });
                    Fill();
                }));
            }
            if (running)
                actions.Add(Small("QR", null, () => { ShareUi.ShowQr(this, e.Host); return Task.FromResult(0); }));
            if (url != "" && url != null && (lan || running))
                actions.Add(Small(I18n.T("shares.copy"), null, () => { try { Clipboard.SetText(url); } catch { } _status.Text = I18n.T("lan.copied", url); return Task.FromResult(0); }));
            actions.Add(Small(I18n.T("shares.edit"), null, () => { Edit(e, lan); return Task.FromResult(0); }));
            actions.Add(Small(I18n.T("shares.remove"), null, () =>
            {
                if (!Ui.Confirm(this, I18n.T("shares.removeConfirm", e.Host))) return Task.FromResult(0);
                if (lan) { LanShare.Stop(e.Host); ShareStore.Update(e.Host, x => { x.Lan = false; x.LanPort = 0; }); }
                else { Share.Stop(e.Host); ShareStore.Update(e.Host, x => { x.Online = false; x.OnlineUrl = ""; }); }
                return Task.FromResult(0);
            }));
            int x = card.Width - Ui.S(12);
            foreach (var b in Enumerable.Reverse(actions))
            {
                b.Location = new Point(x - b.Width, card.Height - b.Height - Ui.S(10));
                x = b.Left - Ui.S(6);
                card.Controls.Add(b);
            }
            // texts stop before the buttons
            foreach (Control c in card.Controls)
                if (c.Top >= Ui.S(36) && !actions.Contains(c)) c.Width = Math.Min(c.Width, x - c.Left - Ui.S(6));
            return card;
        }

        /// <summary>"2 min", "1 h 5 min", "now".</summary>
        static string Ago(TimeSpan span)
        {
            if (span.TotalSeconds < 60) return I18n.T("time.now");
            return Duration((int)Math.Round(span.TotalMinutes));
        }

        static string Duration(int minutes)
        {
            if (minutes < 60) return I18n.T("time.min", minutes);
            if (minutes < 1440) return minutes % 60 == 0 ? I18n.T("time.hour", minutes / 60) : I18n.T("time.hour", minutes / 60) + " " + I18n.T("time.min", minutes % 60);
            return I18n.T("time.day", minutes / 1440);
        }

        static ModernButton Small(string text, string glyph, Func<Task> click)
        {
            var b = Ui.Button(text, async (s, e) => await click(), false, glyph);
            b.Margin = Padding.Empty;
            b.Size = b.GetPreferredSize(Size.Empty); // known before it is placed on the card
            return b;
        }

        static readonly int[] LimitMinutes = { 0, 15, 30, 60, 120, 240, 480, 1440 };

        /// <summary>
        /// Settings of one share. Local network: port, password, start with Portly, time limit, note.
        /// Internet: fixed address on the user's Cloudflare domain, password, time limit, note.
        /// </summary>
        void Edit(ShareEntry e, bool lan)
        {
            using (var f = new Form())
            {
                Ui.Apply(f);
                f.Text = I18n.T("shares.edit");
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.AutoSize = true;
                f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                f.Padding = Ui.P(20);
                var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
                t.Controls.Add(Ui.Label(e.Host + "  ·  " + (lan ? I18n.T("shares.lan") : I18n.T("shares.online")), Ui.Subtitle));
                Func<string, Label> heading = text => { var l = Ui.Label(text, Ui.Bold); l.Margin = Ui.P(0, 12, 0, 2); t.Controls.Add(l); return l; };
                Action<string> hintOf = text => { var h = Ui.Label(text, Ui.Small, Ui.Muted); h.MaximumSize = new Size(Ui.S(420), 0); t.Controls.Add(h); };

                TextBox port = null, fixedHost = null;
                CheckBox auto = null;
                if (lan)
                {
                    heading(I18n.T("shares.portLabel"));
                    port = new TextBox { Width = Ui.S(120), Text = e.LanPort > 0 ? e.LanPort.ToString() : "" };
                    t.Controls.Add(port);
                    hintOf(I18n.T("shares.portHint"));
                }
                else
                {
                    // fixed address: the user's own domain through their Cloudflare account
                    heading(I18n.T("cf.fixedLabel"));
                    fixedHost = new TextBox { Width = Ui.S(300), Text = e.FixedHost };
                    t.Controls.Add(fixedHost);
                    hintOf(I18n.T("cf.fixedHint"));
                    var cfState = Ui.Label("", Ui.Small, Ui.Muted);
                    cfState.MaximumSize = new Size(Ui.S(420), 0);
                    ModernButton cfButton = null;
                    Action showState = () =>
                    {
                        cfState.Text = CloudflareTunnel.IsConnected ? "✓ " + I18n.T("cf.connectedState") : I18n.T("cf.notConnectedState");
                        cfState.ForeColor = CloudflareTunnel.IsConnected ? Theme.Current.Green : Ui.Muted;
                        cfButton.Text = CloudflareTunnel.IsConnected ? I18n.T("cf.disconnect") : I18n.T("cf.connect");
                    };
                    cfButton = Ui.Button(I18n.T("cf.connect"), async (s, a) =>
                    {
                        if (CloudflareTunnel.IsConnected)
                        {
                            if (!Ui.Confirm(f, I18n.T("cf.disconnectConfirm"))) return;
                            CloudflareTunnel.Disconnect();
                            showState();
                            return;
                        }
                        cfButton.Enabled = false;
                        try
                        {
                            Action<string> status = msg => { if (!f.IsDisposed) f.BeginInvoke((Action)(() => cfState.Text = msg)); };
                            var exe = await Share.EnsureCloudflaredAsync(status);
                            await CloudflareTunnel.ConnectAsync(exe, status, System.Threading.CancellationToken.None);
                        }
                        catch (Exception ex) { if (!f.IsDisposed) Ui.Error(f, ex.Message); }
                        finally { if (!f.IsDisposed) { cfButton.Enabled = true; showState(); } }
                    }, false);
                    cfButton.Margin = Ui.P(0, 6, 0, 2);
                    t.Controls.Add(cfButton);
                    t.Controls.Add(cfState);
                    showState();
                }

                heading(I18n.T("shares.passwordLabel"));
                var password = new TextBox { Width = Ui.S(220), Text = lan ? e.LanPassword : e.OnlinePassword, UseSystemPasswordChar = true, MaxLength = 64 };
                t.Controls.Add(password);
                var show = new CheckBox { Text = I18n.T("common.show"), AutoSize = true };
                show.CheckedChanged += (s, a) => password.UseSystemPasswordChar = !show.Checked;
                t.Controls.Add(show);
                hintOf(I18n.T(lan ? "shares.passwordHint" : "shares.passwordHintOnline"));

                heading(I18n.T("shares.limitLabel"));
                var limit = new ThemedCombo { Width = Ui.S(220) };
                foreach (var m in LimitMinutes) limit.Items.Add(m == 0 ? I18n.T("shares.noLimit") : Duration(m));
                int current = lan ? e.LanMinutes : e.OnlineMinutes;
                limit.SelectedIndex = Math.Max(0, Array.IndexOf(LimitMinutes, current));
                t.Controls.Add(limit);
                hintOf(I18n.T("shares.limitHint"));

                if (lan)
                {
                    auto = new CheckBox { Text = I18n.T("shares.autoStart"), AutoSize = true, Checked = e.LanAuto, Margin = Ui.P(0, 12, 0, 0) };
                    t.Controls.Add(auto);
                }
                heading(I18n.T("shares.noteLabel"));
                var note = new TextBox { Width = Ui.S(420), Text = e.Note, MaxLength = 200 };
                t.Controls.Add(note);

                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 14, 0, 0) };
                var cancel = Ui.Button(I18n.T("common.cancel"), (s, a) => f.Close());
                cancel.Margin = Padding.Empty;
                var save = Ui.Button(I18n.T("common.save"), (s, a) =>
                {
                    int newPort = e.LanPort;
                    if (lan && (!int.TryParse(port.Text.Trim(), out newPort) || newPort != e.LanPort && !LanShare.PortFree(newPort, e.Host)))
                    {
                        Ui.Error(f, I18n.T("shares.portBad", port.Text.Trim()));
                        return;
                    }
                    if (!lan && fixedHost.Text.Trim() != "" && !CloudflareTunnel.IsValidHost(fixedHost.Text.Trim()))
                    {
                        Ui.Error(f, I18n.T("cf.badHost", fixedHost.Text.Trim()));
                        return;
                    }
                    f.DialogResult = DialogResult.OK;
                    f.Close();
                }, true);
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(save);
                t.Controls.Add(buttons);
                f.Controls.Add(t);
                f.AcceptButton = save;
                f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;

                int minutes = LimitMinutes[Math.Max(0, limit.SelectedIndex)];
                int chosen = lan ? int.Parse(port.Text.Trim()) : e.LanPort;
                bool running = lan ? LanShare.IsRunning(e.Host) : Share.IsRunning(e.Host);
                bool moved = lan && chosen != e.LanPort && running;
                var newFixed = lan ? e.FixedHost : fixedHost.Text.Trim().ToLowerInvariant();
                if (moved) LanShare.Stop(e.Host);
                ShareStore.Update(e.Host, x =>
                {
                    x.Note = note.Text.Trim();
                    if (lan) { x.LanPort = chosen; x.LanPassword = password.Text; x.LanAuto = auto.Checked; x.LanMinutes = minutes; }
                    else { x.OnlinePassword = password.Text; x.OnlineMinutes = minutes; x.FixedHost = newFixed; }
                });
                if (moved)
                {
                    var site = SiteOf(e.Host);
                    try { if (site != null) LanShare.Start(site); }
                    catch (Exception ex) { Ui.Error(this, ex.Message); }
                }
                else if (running)
                {
                    // a running share: the new password at once (devices log in again), the time limit counted from now
                    LanShare.SetPassword(e.Host, password.Text, !lan);
                    if (minutes != (lan ? e.LanMinutes : e.OnlineMinutes))
                    {
                        var host = e.Host;
                        LanShare.SetExpiry(host, !lan, minutes > 0 ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null,
                            lan ? (Action)(() => LanShare.Stop(host)) : () => Share.Stop(host));
                    }
                    if (!lan && newFixed != e.FixedHost) _status.Text = I18n.T("cf.nextStart");
                }
                Fill();
            }
        }
    }
}
