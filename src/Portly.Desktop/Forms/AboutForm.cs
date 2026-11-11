using Th = Portly.Desktop.UI.Theme;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Portly.Core;
using Portly.Desktop.UI;

namespace Portly.Desktop.Forms
{
    public class AboutForm : PortlyDialog
    {
        public static new Task Show(Window owner) { return new AboutForm().ShowDialog(owner); }

        Task ShowDonate() { return DonateForm.Show(this); }

        public AboutForm() : base(I18n.T("about.title"), 520)
        {
            var heart = Ui.Heart;
            var names = Ui.Column(2, Ui.Label("Portly " + AppInfo.VersionText, "title"), Ui.Label(AppInfo.EditionName + "  ·  " + I18n.T("about.by"), "muted"));
            var head = Ui.Row(new Image { Source = Ui.AppImage, Width = 48, Height = 48 }, names);
            head.Spacing = 12;
            Body.Children.Add(head);

            Body.Children.Add(Ui.Label(I18n.T("about.description")));
            if (AppInfo.IsLegacy) Body.Children.Add(Ui.Label(I18n.T("about.legacy"), "small muted"));

            var links = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var l in new[]
            {
                Ui.Link("GitHub", () => Ui.Open(AppInfo.GitHubUrl)),
                Ui.Link("erdemsoftware.com", () => Ui.Open(AppInfo.WebsiteUrl)),
                Ui.Link(I18n.T("about.translate"), () => Ui.Open(AppInfo.TranslateUrl)),
                Ui.Link(I18n.T("about.issue"), () => Ui.Open(AppInfo.IssuesUrl)),
            })
            {
                l.Margin = new Thickness(0, 0, 18, 4);
                links.Children.Add(l);
            }
            Body.Children.Add(links);

            // heart-colored button (the primary style paints the template presenter, so override it there)
            Styles.Add(new Style(x => x.OfType<Button>().Class("heart").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
                { Setters = { new Setter(ContentPresenter.BackgroundProperty, heart) } });
            Styles.Add(new Style(x => x.OfType<Button>().Class("heart").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
                { Setters = { new Setter(ContentPresenter.BackgroundProperty, heart) } });
            var donate = Ui.Button(I18n.T("main.donate"), ShowDonate, "primary heart", "heart");
            donate.HorizontalAlignment = HorizontalAlignment.Left;
            var dText = Ui.Label(I18n.T("about.donateText"));
            var box = Ui.Card(Ui.Column(10, dText, donate), new Thickness(14));
            var c = ((ISolidColorBrush)heart).Color;
            box.Background = new SolidColorBrush(Th.IsDark ? Color.FromRgb(40, 24, 30) : Color.FromRgb(255, 241, 242));
            box.BorderBrush = new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B));
            Body.Children.Add(box);

            Body.Children.Add(Ui.Label(I18n.T("about.license") + "\n" + I18n.T("about.thirdParty"), "small muted"));
            Buttons.Children.Add(Ui.Button(I18n.T("common.close"), () => Close()));
        }

        public static Window Preview() { return new AboutForm(); }
    }
}
