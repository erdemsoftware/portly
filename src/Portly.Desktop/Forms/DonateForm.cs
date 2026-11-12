using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Portly.Core;
using Portly.Desktop.UI;

namespace Portly.Desktop.Forms
{
    /// <summary>Donation options: open a payment page, or copy a crypto address / IBAN.</summary>
    public class DonateForm : PortlyDialog
    {
        public static new Task Show(Window owner) { return new DonateForm().ShowDialog(owner); }

        public DonateForm() : this(Donate.Load()) { }

        DonateForm(List<DonateMethod> methods) : base(I18n.T("donate.title"), 580)
        {
            var head = Ui.Row(new Glyph { Name_ = "heart", Color = Ui.Heart, Width = 30, Height = 30 }, Ui.Label(I18n.T("donate.title"), "title"));
            head.Spacing = 10;
            Body.Children.Add(head);
            Body.Children.Add(Ui.Label(I18n.T("about.donateText"), "muted"));

            if (methods.Count == 0)
            {
                Body.Children.Add(Ui.Label(I18n.T("donate.none")));
                var gh = Ui.Link("GitHub", () => Ui.Open(AppInfo.GitHubUrl));
                gh.HorizontalAlignment = HorizontalAlignment.Left;
                Body.Children.Add(gh);
            }
            else
            {
                var list = Ui.Column(8);
                foreach (var m in methods) list.Children.Add(MethodCard(m));
                Body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 470, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            }
            Buttons.Children.Add(Ui.Button(I18n.T("common.close"), () => Close()));
        }

        static Control MethodCard(DonateMethod method)
        {
            var left = Ui.Column(2, Ui.Label(method.Name, "bold"), Ui.Label(method.Note, "small muted"));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14 };
            Button action;
            if (method.Kind == "copy")
            {
                var value = Ui.Selectable(method.Value, "mono");
                value.Margin = new Thickness(0, 6, 0, 0);
                left.Children.Add(value);
                action = null;
                action = Ui.Button(I18n.T("donate.copy"), async () =>
                {
                    await Ui.Copy(grid, method.Value);
                    action.Content = I18n.T("donate.copied");
                });
            }
            else action = Ui.Button(I18n.T("donate.open"), () => Ui.Open(method.Value), "primary", "external");
            action.MinWidth = 110;
            action.HorizontalContentAlignment = HorizontalAlignment.Center;
            action.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(left);
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
            return Ui.Card(grid, new Thickness(16, 12));
        }

        public static Window Preview()
        {
            return new DonateForm(new List<DonateMethod>
            {
                new DonateMethod { Kind = "link", Name = "Buy Me a Coffee", Note = "Card or PayPal, one time or monthly", Value = AppInfo.DonateUrl },
                new DonateMethod { Kind = "link", Name = "GitHub Sponsors", Note = "Monthly support through GitHub", Value = "https://github.com/sponsors/erdemsoftware" },
                new DonateMethod { Kind = "copy", Name = "Bitcoin (BTC)", Note = "Send only BTC to this address", Value = "bc1qexampleexampleexampleexampleexample0" },
                new DonateMethod { Kind = "copy", Name = "IBAN (TRY)", Note = "Erdem Software", Value = "TR00 0000 0000 0000 0000 0000 00" },
            });
        }
    }
}
