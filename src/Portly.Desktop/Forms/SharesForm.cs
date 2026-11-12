using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>Starting a share from a project card or from "Shared projects".</summary>
    public static class ShareUi
    {
        /// <summary>Local network share (phone, tablet, another PC on the same router). Returns the address or null.</summary>
        public static async Task<string> StartLan(Window owner, Site site)
        {
            if (ServiceManager.Web.State != ServiceState.Running) { await Dialogs.Warning(owner, I18n.T("main.webNotRunning")); return null; }
            // the explanation only the first time; a remembered share starts again directly
            if (!ShareStore.Get(site.Host).Lan && !await Dialogs.Confirm(owner, I18n.T("lan.confirmRule", site.Host))) return null;
            try
            {
                var url = LanShare.Start(site);
                try { await Ui.Copy(owner, url); } catch { }
                await ShowLanQr(owner, site.Host);
                return url;
            }
            catch (Exception ex) { await Dialogs.Warning(owner, ex.Message); return null; }
        }

        /// <summary>Easy to type on a phone, hard to guess: e.g. "kite-4827-moon".</summary>
        internal static string SuggestPassword()
        {
            var words = new[] { "kite", "moon", "river", "pixel", "cedar", "comet", "lotus", "amber", "delta", "maple", "orbit", "sonic", "tiger", "ultra", "vivid", "wave" };
            var b = new byte[6];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            return words[b[0] % words.Length] + "-" + (1000 + (b[1] << 8 | b[2]) % 9000) + "-" + words[b[3] % words.Length];
        }

        /// <summary>QR code + explanation for a running local network share (https: also the certificate code).</summary>
        public static async Task ShowLanQr(Window owner, string host)
        {
            var url = LanShare.UrlOf(host);
            if (url == null) return;
            bool https = url.StartsWith("https://");
            var text = I18n.T("lan.qrText");
            // why devices may not get through (UAC refused, network not private ...): first, the code alone would not work
            var note = ShareFirewall.Note(ShareStore.Get(host).LanPort);
            if (note != null) text = note + "\n\n" + text;
            if (ShareStore.Get(host).LanPassword != "") text += "\n\n" + I18n.T("lan.qrPassword");
            if (https) text += "\n\n" + I18n.T("lan.qrHttps", "http" + url.Substring(5));
            await QrDialog.ShowFor(owner, I18n.T("lan.qrTitle", host), text, url, https ? "http" + url.Substring(5) + "portly-ca" : null);
        }

        /// <summary>The QR window of whatever share of the project runs (local network first).</summary>
        public static async Task ShowQr(Window owner, string host)
        {
            if (LanShare.UrlOf(host) != null) await ShowLanQr(owner, host);
            else if (Share.UrlOf(host) != null)
                await QrDialog.ShowFor(owner, I18n.T("share.qrTitle"), I18n.T("share.qrText") + (ShareStore.Get(host).OnlinePassword != "" ? "\n\n" + I18n.T("lan.qrPassword") : ""), Share.UrlOf(host));
        }

        /// <summary>Internet share with a Cloudflare quick tunnel (asks every time: the site becomes public).</summary>
        public static async Task<string> StartOnline(Window owner, Site site, Action<string> status)
        {
            if (ServiceManager.Web.State != ServiceState.Running) { await Dialogs.Warning(owner, I18n.T("main.webNotRunning")); return null; }
            if (!await Dialogs.Confirm(owner, I18n.T("share.confirm", site.Host))) return null;
            // the shared app talks to the database as root without a password: a hole in the app is a hole in the data.
            // Offer a share password (asked before the site opens), with a ready one to accept.
            if (Settings.Current.MysqlRootPassword == "" && ShareStore.Get(site.Host).OnlinePassword == "" && await Dialogs.Confirm(owner, I18n.T("share.rootWarn")))
            {
                var suggested = SuggestPassword();
                var pw = await Dialogs.Prompt(owner, I18n.T("shares.passwordLabel"), I18n.T("share.passwordAsk"), suggested);
                if (pw == null) return null;
                if (pw.Trim() != "") ShareStore.Update(site.Host, e => e.OnlinePassword = pw.Trim());
            }
            try
            {
                var url = await Share.StartAsync(site, status);
                try { await Ui.Copy(owner, url); } catch { }
                await ShowQr(owner, site.Host);
                return url;
            }
            catch (Exception ex) { await Dialogs.Warning(owner, ex.Message); return null; }
        }
    }

    /// <summary>"Shared projects": every local network and internet share, running or stopped, with its address.</summary>
    public class SharesForm : PortlyDialog
    {
        readonly StackPanel _list = new StackPanel { Spacing = 8 };
        readonly TextBlock _status = Ui.Label("", "muted small");
        readonly Action _changed;
        readonly DispatcherTimer _tick;
        readonly List<ShareEntry> _sample;
        bool _closed;

        public SharesForm() : this(null) { }

        SharesForm(List<ShareEntry> sample) : base(I18n.T("shares.title"), 760)
        {
            _sample = sample;
            MakeResizable(660);
            Body.Children.Add(Ui.Label(I18n.T("shares.title"), "title"));
            Body.Children.Add(Ui.Label(I18n.T("shares.intro"), "muted"));
            Body.Children.Add(_list);
            Body.Children.Add(_status);
            Buttons.Children.Add(Ui.Button(I18n.T("common.close"), () => Close()));

            _changed = () => Ui.Post(() => { if (!_closed) Fill(); });
            ShareStore.Changed += _changed;
            // "2 min ago" and the time left move on by themselves
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _tick.Tick += (s, e) => Fill();
            _tick.Start();
            Closed += (s, e) => { _closed = true; ShareStore.Changed -= _changed; _tick.Stop(); };
            Fill();
        }

        Site SiteOf(string host)
        {
            if (_sample != null) return host == "old-demo" ? null : new Site { Name = host, Host = host, Valid = true };
            return ServiceManager.CurrentSites.FirstOrDefault(x => x.Valid && string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));
        }

        bool IsRunning(string host, bool lan)
        {
            if (_sample != null) return lan ? host == "shop" : host == "blog";
            return lan ? LanShare.IsRunning(host) : Share.IsRunning(host);
        }

        void Fill()
        {
            _list.Children.Clear();
            var all = _sample ?? ShareStore.All();
            if (all.Count == 0)
            {
                var empty = Ui.Label(I18n.T("shares.empty"), null, Ui.Muted);
                empty.Margin = new Thickness(4, 20, 0, 0);
                _list.Children.Add(empty);
            }
            foreach (var e in all)
            {
                if (e.Lan) _list.Children.Add(Row(e, true));
                if (e.Online) _list.Children.Add(Row(e, false));
            }
        }

        Control Row(ShareEntry e, bool lan)
        {
            var site = SiteOf(e.Host);
            bool running = IsRunning(e.Host, lan);
            var url = lan ? (running ? (_sample != null ? "http://192.168.1.24:8081" : LanShare.UrlOf(e.Host)) : LanShare.AddressFor(e.LanPort))
                          : (running ? (_sample != null ? "https://quiet-lotus-pixel.trycloudflare.com" : Share.UrlOf(e.Host)) : e.OnlineUrl);
            var devices = running ? (_sample != null ? SampleDevices() : LanShare.DevicesOf(e.Host, !lan)) : new List<ShareDevice>();

            var kind = new Pill(lan ? I18n.T("shares.lan") : I18n.T("shares.online"), lan ? Ui.Accent : new SolidColorBrush(Color.Parse("#F38020")), false);
            // running but the web server is stopped: devices get a "start the web server" page
            bool webDown = running && _sample == null && LanShare.WebDown;
            var state = new Pill(
                site == null ? I18n.T("shares.noProject") : webDown ? I18n.T("shares.webDown") : running ? I18n.T("shares.on") : I18n.T("shares.off"),
                site == null ? Ui.Red : webDown ? Ui.Orange : running ? Ui.Green : Ui.Gray);
            var head = Ui.Row(Ui.Label(e.Host, "subtitle"), kind, state);
            head.Spacing = 10;
            foreach (var c in head.Children) c.VerticalAlignment = VerticalAlignment.Center;
            var col = Ui.Column(4, head);

            if (!string.IsNullOrEmpty(url))
            {
                var link = Ui.Link(url, () => Ui.Open(url));
                link.HorizontalAlignment = HorizontalAlignment.Left;
                col.Children.Add(link);
            }
            else col.Children.Add(Ui.Label(lan ? I18n.T("lan.noNetwork") : "-", "muted small"));

            // a stopped internet share: the old address will not work again, the next start gives a new one
            var detail = !lan && !running && e.OnlineUrl != "" ? I18n.T("shares.oldUrl") : lan && e.LanPort > 0 ? I18n.T("shares.port", e.LanPort) : "";
            var expires = running && _sample == null ? LanShare.ExpiresAt(e.Host, !lan) : null;
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
                var n = Ui.Label(note, "muted small");
                n.TextTrimming = TextTrimming.CharacterEllipsis;
                n.TextWrapping = TextWrapping.NoWrap;
                ToolTip.SetTip(n, note);
                col.Children.Add(n);
            }
            if (running)
            {
                // who opened it, and PHP errors their pages caused (details in the Log page)
                int errors = devices.Sum(d => d.Errors);
                var text = devices.Count == 0 ? I18n.T("shares.noDevices")
                    : I18n.T("shares.devices", devices.Count) + ": " + string.Join(", ", devices.Take(2).Select(d => (d.Name != "" ? d.Name : d.Ip) + " (" + Ago(DateTime.Now - d.Last) + ")"))
                      + (devices.Count > 2 ? " ..." : "");
                var dl = Ui.Label((errors > 0 ? "⚠ " + I18n.T("shares.errors", errors) + "  ·  " : "") + text, "small", errors > 0 ? Ui.Orange : Ui.Muted);
                dl.TextTrimming = TextTrimming.CharacterEllipsis;
                dl.TextWrapping = TextWrapping.NoWrap;
                if (devices.Count > 0)
                    ToolTip.SetTip(dl, string.Join("\n", devices.Select(d => d.Label + "  ·  " + I18n.T("shares.deviceLine", d.Requests, Ago(DateTime.Now - d.Last)) + (d.Errors > 0 ? "  ·  ⚠ " + d.Errors : ""))));
                col.Children.Add(dl);
            }

            // actions under the texts, right-aligned
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            if (site != null)
            {
                actions.Children.Add(Small(running ? I18n.T("shares.stop") : I18n.T("shares.start"), running ? "stop" : "play", async () =>
                {
                    if (running) { if (lan) LanShare.Stop(e.Host); else Share.Stop(e.Host); }
                    else if (lan) await ShareUi.StartLan(this, site);
                    else await ShareUi.StartOnline(this, site, s => Ui.Post(() => { if (!_closed) _status.Text = s; }));
                    Fill();
                }));
            }
            if (running)
                actions.Children.Add(Small("QR", null, () => ShareUi.ShowQr(this, e.Host)));
            if (!string.IsNullOrEmpty(url) && (lan || running))
                actions.Children.Add(Small(I18n.T("shares.copy"), null, async () => { try { await Ui.Copy(this, url); } catch { } _status.Text = I18n.T("lan.copied", url); }));
            actions.Children.Add(Small(I18n.T("shares.edit"), null, () => Edit(e, lan)));
            actions.Children.Add(Small(I18n.T("shares.remove"), null, async () =>
            {
                if (!await Dialogs.Confirm(this, I18n.T("shares.removeConfirm", e.Host))) return;
                if (lan) { LanShare.Stop(e.Host); ShareStore.Update(e.Host, x => { x.Lan = false; x.LanPort = 0; }); }
                else { Share.Stop(e.Host); ShareStore.Update(e.Host, x => { x.Online = false; x.OnlineUrl = ""; }); }
            }));
            col.Children.Add(actions);
            return Ui.Card(col);
        }

        static List<ShareDevice> SampleDevices()
        {
            return new List<ShareDevice>
            {
                new ShareDevice { Ip = "192.168.1.31", Name = "iPhone · Safari", First = DateTime.Now.AddMinutes(-12), Last = DateTime.Now.AddMinutes(-2), Requests = 48 },
                new ShareDevice { Ip = "192.168.1.40", Name = "Android (Pixel 9) · Chrome", First = DateTime.Now.AddMinutes(-30), Last = DateTime.Now.AddMinutes(-9), Requests = 130, Errors = 2 },
                new ShareDevice { Ip = "192.168.1.52", Name = "", First = DateTime.Now.AddHours(-1), Last = DateTime.Now.AddMinutes(-40), Requests = 6 },
            };
        }

        /// <summary>"2 min", "1 h 5 min", "now".</summary>
        internal static string Ago(TimeSpan span)
        {
            if (span.TotalSeconds < 60) return I18n.T("time.now");
            return Duration((int)Math.Round(span.TotalMinutes));
        }

        internal static string Duration(int minutes)
        {
            if (minutes < 60) return I18n.T("time.min", minutes);
            if (minutes < 1440) return minutes % 60 == 0 ? I18n.T("time.hour", minutes / 60) : I18n.T("time.hour", minutes / 60) + " " + I18n.T("time.min", minutes % 60);
            return I18n.T("time.day", minutes / 1440);
        }

        static Button Small(string text, string glyph, Func<Task> click)
        {
            var b = Ui.Button(text, click, null, glyph);
            b.Padding = new Thickness(10, 4);
            b.FontSize = 12.5;
            return b;
        }

        /// <summary>
        /// Settings of one share. Local network: port, password, start with Portly, time limit, note.
        /// Internet: fixed address on the user's Cloudflare domain, password, time limit, note.
        /// </summary>
        async Task Edit(ShareEntry e, bool lan)
        {
            var dlg = new ShareEditDialog(e, lan);
            if (!await dlg.ShowDialog<bool>(this)) return;

            int minutes = dlg.Minutes;
            int chosen = lan ? dlg.Port : e.LanPort;
            bool running = lan ? LanShare.IsRunning(e.Host) : Share.IsRunning(e.Host);
            bool moved = lan && chosen != e.LanPort && running;
            var newFixed = lan ? e.FixedHost : dlg.FixedHost.ToLowerInvariant();
            var password = dlg.Password;
            var note = dlg.Note;
            var auto = dlg.Auto;
            if (moved) LanShare.Stop(e.Host, true);
            ShareStore.Update(e.Host, x =>
            {
                x.Note = note;
                if (lan) { x.LanPort = chosen; x.LanPassword = password; x.LanAuto = auto; x.LanMinutes = minutes; }
                else { x.OnlinePassword = password; x.OnlineMinutes = minutes; x.FixedHost = newFixed; }
            });
            if (moved)
            {
                var site = SiteOf(e.Host);
                try { if (site != null) LanShare.Start(site); }
                catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
            }
            else if (running)
            {
                // a running share: the new password at once (devices log in again), the time limit counted from now
                LanShare.SetPassword(e.Host, password, !lan);
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

        public static Window Preview()
        {
            return new SharesForm(new List<ShareEntry>
            {
                new ShareEntry { Host = "shop", Lan = true, LanPort = 8081, LanPassword = "x", LanAuto = true, LanMinutes = 60, Note = "for the client demo", Online = true, OnlineUrl = "https://old-random-name.trycloudflare.com" },
                new ShareEntry { Host = "blog", Online = true, OnlineUrl = "https://quiet-lotus-pixel.trycloudflare.com", OnlinePassword = "x" },
                new ShareEntry { Host = "old-demo", Lan = true, LanPort = 8090, Note = "phone test" },
            });
        }
    }

    /// <summary>Settings window of one share (see SharesForm.Edit); Save returns true.</summary>
    internal class ShareEditDialog : PortlyDialog
    {
        static readonly int[] LimitMinutes = { 0, 15, 30, 60, 120, 240, 480, 1440 };
        readonly ShareEntry _e;
        readonly bool _lan;
        readonly TextBox _port = new TextBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        readonly TextBox _fixed = new TextBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        readonly TextBox _password = new TextBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, PasswordChar = '•', MaxLength = 64 };
        readonly ComboBox _limit = new ComboBox { Width = 220 };
        readonly CheckBox _auto = new CheckBox { Content = I18n.T("shares.autoStart") };
        readonly TextBox _note = new TextBox { MaxLength = 200 };

        public int Port { get { int p; int.TryParse(_port.Text.Trim(), out p); return p; } }
        public string FixedHost { get { return _fixed.Text.Trim(); } }
        public string Password { get { return _password.Text ?? ""; } }
        public int Minutes { get { return LimitMinutes[Math.Max(0, _limit.SelectedIndex)]; } }
        public bool Auto { get { return _auto.IsChecked == true; } }
        public string Note { get { return (_note.Text ?? "").Trim(); } }

        public ShareEditDialog(ShareEntry e, bool lan) : base(I18n.T("shares.edit"), 480)
        {
            _e = e;
            _lan = lan;
            Body.Spacing = 4;
            Body.Children.Add(Ui.Label(e.Host + "  ·  " + (lan ? I18n.T("shares.lan") : I18n.T("shares.online")), "subtitle"));

            if (lan)
            {
                _port.Text = e.LanPort > 0 ? e.LanPort.ToString() : "";
                Heading(I18n.T("shares.portLabel"));
                Body.Children.Add(_port);
                Hint(I18n.T("shares.portHint"));
            }
            else
            {
                // fixed address: the user's own domain through their Cloudflare account
                _fixed.Text = e.FixedHost;
                Heading(I18n.T("cf.fixedLabel"));
                Body.Children.Add(_fixed);
                Hint(I18n.T("cf.fixedHint"));
                var cfState = Ui.Label("", "muted small");
                Button cfButton = null;
                Action showState = () =>
                {
                    cfState.Text = CloudflareTunnel.IsConnected ? "✓ " + I18n.T("cf.connectedState") : I18n.T("cf.notConnectedState");
                    cfState.Foreground = CloudflareTunnel.IsConnected ? Ui.Green : Ui.Muted;
                    cfButton.Content = CloudflareTunnel.IsConnected ? I18n.T("cf.disconnect") : I18n.T("cf.connect");
                };
                cfButton = Ui.Button(I18n.T("cf.connect"), async () =>
                {
                    if (CloudflareTunnel.IsConnected)
                    {
                        if (!await Dialogs.Confirm(this, I18n.T("cf.disconnectConfirm"))) return;
                        CloudflareTunnel.Disconnect();
                        showState();
                        return;
                    }
                    cfButton.IsEnabled = false;
                    try
                    {
                        Action<string> status = msg => Ui.Post(() => cfState.Text = msg);
                        var exe = await Share.EnsureCloudflaredAsync(status);
                        await CloudflareTunnel.ConnectAsync(exe, status, CancellationToken.None);
                    }
                    catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
                    finally { cfButton.IsEnabled = true; showState(); }
                });
                cfButton.HorizontalAlignment = HorizontalAlignment.Left;
                cfButton.Margin = new Thickness(0, 6, 0, 2);
                Body.Children.Add(cfButton);
                Body.Children.Add(cfState);
                showState();
            }

            Heading(I18n.T("shares.passwordLabel"));
            _password.Text = lan ? e.LanPassword : e.OnlinePassword;
            Body.Children.Add(_password);
            var show = new CheckBox { Content = I18n.T("common.show") };
            show.IsCheckedChanged += (s, a) => _password.PasswordChar = show.IsChecked == true ? default(char) : '•';
            Body.Children.Add(show);
            Hint(I18n.T(lan ? "shares.passwordHint" : "shares.passwordHintOnline"));

            Heading(I18n.T("shares.limitLabel"));
            foreach (var m in LimitMinutes) _limit.Items.Add(m == 0 ? I18n.T("shares.noLimit") : SharesForm.Duration(m));
            int current = lan ? e.LanMinutes : e.OnlineMinutes;
            _limit.SelectedIndex = Math.Max(0, Array.IndexOf(LimitMinutes, current));
            Body.Children.Add(_limit);
            Hint(I18n.T("shares.limitHint"));

            if (lan)
            {
                _auto.IsChecked = e.LanAuto;
                _auto.Margin = new Thickness(0, 12, 0, 0);
                Body.Children.Add(_auto);
            }
            Heading(I18n.T("shares.noteLabel"));
            _note.Text = e.Note;
            Body.Children.Add(_note);

            Buttons.Children.Add(Ui.Button(I18n.T("common.save"), Save, "primary"));
            Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(false)));
        }

        void Heading(string text)
        {
            var l = Ui.Label(text, "bold");
            l.Margin = new Thickness(0, 12, 0, 2);
            Body.Children.Add(l);
        }

        void Hint(string text) { Body.Children.Add(Ui.Label(text, "muted small")); }

        async Task Save()
        {
            if (_lan)
            {
                int newPort;
                if (!int.TryParse(_port.Text.Trim(), out newPort) || newPort != _e.LanPort && !LanShare.PortFree(newPort, _e.Host))
                {
                    await Dialogs.Warning(this, I18n.T("shares.portBad", _port.Text.Trim()));
                    return;
                }
            }
            else if (FixedHost != "" && !CloudflareTunnel.IsValidHost(FixedHost))
            {
                await Dialogs.Warning(this, I18n.T("cf.badHost", FixedHost));
                return;
            }
            Close(true);
        }

        public static Window Preview()
        {
            return new ShareEditDialog(new ShareEntry { Host = "shop", Lan = true, LanPort = 8081, LanPassword = "kite-4827-moon", LanAuto = true, LanMinutes = 60, Note = "for the client demo" }, true);
        }
    }
}
