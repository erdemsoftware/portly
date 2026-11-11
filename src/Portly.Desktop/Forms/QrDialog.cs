using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>
    /// A shared address as a QR code for the phone camera, with the explanation next to it. For an https local share a
    /// second code opens the Portly certificate (to install once, so the site opens without a warning).
    /// </summary>
    public class QrDialog : PortlyDialog
    {
        const double PicSize = 220;
        readonly QrView _view = new QrView { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly Button _link;
        readonly TextBlock _linkText = Ui.Label("", "bold");
        readonly TextBlock _caption = Ui.Label("", "muted small");
        string _shown;

        public QrDialog(string heading, string text, string url, string caUrl = null) : base("Portly", 700)
        {
            var pic = new Border { Width = PicSize, Height = PicSize, Background = Brushes.White, CornerRadius = new CornerRadius(8), Child = _view };
            _caption.MaxWidth = PicSize;
            var left = Ui.Column(6, pic, _caption);
            left.VerticalAlignment = VerticalAlignment.Top;

            _link = Ui.Link("", () => Ui.Open(_shown));
            _link.Content = _linkText;
            _link.HorizontalAlignment = HorizontalAlignment.Left;
            _link.HorizontalContentAlignment = HorizontalAlignment.Left;
            _linkText.Foreground = Ui.Accent;
            var right = Ui.Column(8, Ui.Label(heading, "subtitle"), _link);
            if (!string.IsNullOrEmpty(text))
            {
                var body = Ui.Label(text, null, Ui.Muted);
                body.Margin = new Thickness(0, 4, 0, 0);
                right.Children.Add(body);
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 20 };
            grid.Children.Add(left);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            Body.Children.Add(grid);

            if (!string.IsNullOrEmpty(caUrl))
            {
                Button toggle = null;
                toggle = Ui.Button(I18n.T("qr.showCa"), () =>
                {
                    bool ca = _shown != caUrl;
                    Display(ca ? caUrl : url, ca ? I18n.T("qr.caCaption") : I18n.T("qr.siteCaption"));
                    toggle.Content = ca ? I18n.T("qr.showSite") : I18n.T("qr.showCa");
                });
                Buttons.Children.Add(toggle);
            }
            Buttons.Children.Add(Ui.Button(I18n.T("shares.copy"), async () => { try { await Ui.Copy(this, _shown); } catch { } }));
            Buttons.Children.Add(Ui.Button(I18n.T("common.close"), () => Close(), "primary"));
            Display(url, I18n.T("qr.siteCaption"));
        }

        void Display(string url, string caption)
        {
            _shown = url;
            _linkText.Text = url;
            _caption.Text = caption;
            try { _view.Code = QrCode.Encode(url); }
            catch { _view.Code = null; }
        }

        public static async Task ShowFor(Window owner, string heading, string text, string url, string caUrl = null)
        {
            var d = new QrDialog(heading, text, url, caUrl);
            if (owner == null || !owner.IsVisible)
            {
                var tcs = new TaskCompletionSource<bool>();
                d.Closed += (s, e) => tcs.TrySetResult(true);
                d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                d.Show();
                await tcs.Task;
            }
            else await d.ShowDialog(owner);
        }

        public static Window Preview()
        {
            return new QrDialog("Local network: shop.portly", I18n.T("lan.qrText") + "\n\n" + I18n.T("lan.qrPassword"), "https://192.168.1.24:8443/", "http://192.168.1.24:8443/portly-ca");
        }

        /// <summary>Black on white with a 4-module quiet zone, whole pixels per module so the edges stay sharp for the camera.</summary>
        sealed class QrView : Control
        {
            QrCode _code;

            public QrCode Code { get { return _code; } set { _code = value; InvalidateMeasure(); InvalidateVisual(); } }

            protected override Size MeasureOverride(Size availableSize)
            {
                return new Size(PicSize, PicSize);
            }

            public override void Render(DrawingContext ctx)
            {
                if (_code == null) return;
                const int border = 4;
                int scale = Math.Max(2, (int)(PicSize / (_code.Size + border * 2)));
                int px = (_code.Size + border * 2) * scale;
                double ox = Math.Floor((Bounds.Width - px) / 2), oy = Math.Floor((Bounds.Height - px) / 2);
                ctx.DrawRectangle(Brushes.White, null, new Rect(ox, oy, px, px));
                for (int y = 0; y < _code.Size; y++)
                    for (int x = 0; x < _code.Size; x++)
                        if (_code[x, y]) ctx.DrawRectangle(Brushes.Black, null, new Rect(ox + (x + border) * scale, oy + (y + border) * scale, scale, scale));
            }
        }
    }
}
