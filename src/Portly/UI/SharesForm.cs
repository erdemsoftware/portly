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
                // https: the phone does not know the Portly CA yet; say where to get it (served by the share itself)
                Ui.Info(owner, url.StartsWith("https://")
                    ? I18n.T("lan.readyHttps", url, "http" + url.Substring(5) + "portly-ca.crt", "http" + url.Substring(5))
                    : I18n.T("lan.ready", url));
                return url;
            }
            catch (Exception ex) { Ui.Error(owner, ex.Message); return null; }
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
                Ui.Info(owner, I18n.T("share.ready", url));
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
            FormClosed += (s, e) => ShareStore.Changed -= _changed;
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
            var card = new Card { Size = Ui.S(660, 104), Radius = 10, Margin = Ui.P(0, 0, 0, 8) };

            var name = Ui.Label(e.Host, Ui.Subtitle);
            name.AutoSize = false;
            name.AutoEllipsis = true;
            name.Size = Ui.S(250, 24);
            name.Location = new Point(Ui.S(14), Ui.S(10));
            var kind = new Pill { Text = lan ? I18n.T("shares.lan") : I18n.T("shares.online"), Color = lan ? p.Accent : ColorTranslator.FromHtml("#F38020"), Dot = false };
            kind.Location = new Point(Ui.S(270), Ui.S(11));
            var state = new Pill { Text = site == null ? I18n.T("shares.noProject") : running ? I18n.T("shares.on") : I18n.T("shares.off"), Color = site == null ? p.Red : running ? p.Green : p.Gray };
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
            var note = string.Join("  ·  ", new[] { detail, e.Note }.Where(x => x != ""));
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

        static ModernButton Small(string text, string glyph, Func<Task> click)
        {
            var b = Ui.Button(text, async (s, e) => await click(), false, glyph);
            b.Margin = Padding.Empty;
            b.Size = b.GetPreferredSize(Size.Empty); // known before it is placed on the card
            return b;
        }

        /// <summary>Change the note, and for a local network share the port (a running share moves to the new port).</summary>
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
                TextBox port = null;
                if (lan)
                {
                    var pl = Ui.Label(I18n.T("shares.portLabel"), Ui.Bold);
                    pl.Margin = Ui.P(0, 12, 0, 2);
                    t.Controls.Add(pl);
                    port = new TextBox { Width = Ui.S(120), Text = e.LanPort > 0 ? e.LanPort.ToString() : "" };
                    t.Controls.Add(port);
                    var hint = Ui.Label(I18n.T("shares.portHint"), Ui.Small, Ui.Muted);
                    hint.MaximumSize = new Size(Ui.S(400), 0);
                    t.Controls.Add(hint);
                }
                var nl = Ui.Label(I18n.T("shares.noteLabel"), Ui.Bold);
                nl.Margin = Ui.P(0, 12, 0, 2);
                t.Controls.Add(nl);
                var note = new TextBox { Width = Ui.S(400), Text = e.Note, MaxLength = 200 };
                t.Controls.Add(note);
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 14, 0, 0) };
                var cancel = Ui.Button(I18n.T("common.cancel"), (s, a) => f.Close());
                cancel.Margin = Padding.Empty;
                var save = Ui.Button(I18n.T("common.save"), (s, a) =>
                {
                    int newPort = e.LanPort;
                    if (lan && !int.TryParse(port.Text.Trim(), out newPort) || lan && newPort != e.LanPort && !LanShare.PortFree(newPort, e.Host))
                    {
                        Ui.Error(f, I18n.T("shares.portBad", port.Text.Trim()));
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

                int chosen = lan ? int.Parse(port.Text.Trim()) : e.LanPort;
                bool moved = lan && chosen != e.LanPort && LanShare.IsRunning(e.Host);
                if (moved) LanShare.Stop(e.Host);
                ShareStore.Update(e.Host, x => { x.Note = note.Text.Trim(); if (lan) x.LanPort = chosen; });
                if (moved)
                {
                    var site = SiteOf(e.Host);
                    try { if (site != null) LanShare.Start(site); }
                    catch (Exception ex) { Ui.Error(this, ex.Message); }
                }
                Fill();
            }
        }
    }
}
