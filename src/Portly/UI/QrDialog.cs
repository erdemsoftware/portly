using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Portly.Core;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>
    /// A shared address as a QR code for the phone camera, with the explanation next to it. For an https local share a
    /// second code opens the Portly certificate (to install once, so the site opens without a warning).
    /// </summary>
    public class QrDialog : Form
    {
        readonly PictureBox _pic;
        readonly LinkLabel _link;
        readonly Label _caption;
        string _shown;

        public QrDialog(string heading, string text, string url, string caUrl = null)
        {
            Ui.Apply(this);
            Text = "Portly";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(22, 20, 22, 16);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            // left: the code on a white card (cameras need the light border), the address under it
            var left = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = Ui.P(0, 0, 20, 0) };
            _pic = new PictureBox { Size = Ui.S(220, 220), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White, Margin = Padding.Empty };
            left.Controls.Add(_pic);
            _caption = Ui.Label("", Ui.Small, Ui.Muted);
            _caption.Margin = Ui.P(0, 6, 0, 0);
            _caption.MaximumSize = new Size(Ui.S(220), 0);
            left.Controls.Add(_caption);
            t.Controls.Add(left, 0, 0);

            var right = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
            var h = Ui.Label(heading, Ui.Subtitle);
            h.MaximumSize = new Size(Ui.S(360), 0);
            right.Controls.Add(h);
            _link = new LinkLabel { AutoSize = true, Font = Ui.Bold, LinkColor = Ui.Accent, ActiveLinkColor = Ui.AccentDark, VisitedLinkColor = Ui.Accent, Margin = Ui.P(0, 8, 0, 0), MaximumSize = new Size(Ui.S(360), 0) };
            _link.LinkClicked += (s, e) => WinUtil.Open(_shown);
            right.Controls.Add(_link);
            if (!string.IsNullOrEmpty(text))
            {
                var body = Ui.Label(text, Ui.Font, Ui.Muted);
                body.MaximumSize = new Size(Ui.S(360), 0);
                body.Margin = Ui.P(0, 12, 0, 0);
                right.Controls.Add(body);
            }
            t.Controls.Add(right, 1, 0);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 16, 0, 0) };
            var close = Ui.Button(I18n.T("common.close"), (s, e) => Close(), true);
            close.Margin = Padding.Empty;
            buttons.Controls.Add(close);
            var copy = Ui.Button(I18n.T("shares.copy"), (s, e) => { try { Clipboard.SetText(_shown); } catch { } }, false);
            copy.Margin = Ui.P(0, 0, 8, 0);
            buttons.Controls.Add(copy);
            if (!string.IsNullOrEmpty(caUrl))
            {
                // switch between the site and the certificate code
                ModernButton toggle = null;
                toggle = Ui.Button(I18n.T("qr.showCa"), (s, e) =>
                {
                    bool ca = _shown != caUrl;
                    Show(ca ? caUrl : url, ca ? I18n.T("qr.caCaption") : I18n.T("qr.siteCaption"));
                    toggle.Text = ca ? I18n.T("qr.showSite") : I18n.T("qr.showCa");
                }, false);
                toggle.Margin = Ui.P(0, 0, 8, 0);
                buttons.Controls.Add(toggle);
            }
            t.Controls.Add(buttons, 0, 1);
            t.SetColumnSpan(buttons, 2);
            Controls.Add(t);
            AcceptButton = close;
            CancelButton = close;
            Show(url, I18n.T("qr.siteCaption"));
        }

        void Show(string url, string caption)
        {
            _shown = url;
            _link.Text = url;
            _caption.Text = caption;
            var old = _pic.Image;
            try
            {
                // whole pixels per module: sharp edges for the camera
                var qr = QrCode.Encode(url);
                _pic.Image = qr.ToBitmap(Math.Max(2, _pic.Width / (qr.Size + 8)));
            }
            catch { _pic.Image = null; }
            if (old != null) old.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _pic.Image != null) _pic.Image.Dispose();
            base.Dispose(disposing);
        }

        public static void ShowFor(IWin32Window owner, string heading, string text, string url, string caUrl = null)
        {
            using (var d = new QrDialog(heading, text, url, caUrl))
            {
                var f = owner as Form;
                d.StartPosition = f != null && f.Visible ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                d.ShowDialog(owner);
            }
        }
    }
}
