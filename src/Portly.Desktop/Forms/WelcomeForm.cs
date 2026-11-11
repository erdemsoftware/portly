using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    public enum WelcomeAction { None, Packages, Import, StartAll }

    /// <summary>
    /// Start screen: shown on every start until "Don't show again" is ticked.
    /// 1. install packages  2. import from another stack  3. start.
    /// </summary>
    public class WelcomeForm : PortlyDialog
    {
        public WelcomeAction Action { get; private set; }
        public List<ImportSource> Sources { get; private set; }
        readonly CheckBox _dontShow;
        TextBlock _importText;
        readonly Button _importBtn;
        readonly bool _preview;

        /// <summary>Shows the dialog and returns it after it closed: read Action and Sources from the result.</summary>
        public static new async Task<WelcomeForm> Show(Window owner)
        {
            var f = new WelcomeForm();
            await f.ShowDialog(owner);
            return f;
        }

        public WelcomeForm() : this(false) { }

        WelcomeForm(bool preview) : base("Portly", 620)
        {
            _preview = preview;
            var names = Ui.Column(2, Ui.Label(I18n.T("welcome.title"), "title"), Ui.Label(I18n.T("welcome.subtitle"), "muted"));
            var head = Ui.Row(new Image { Source = Ui.AppImage, Width = 48, Height = 48 }, names);
            head.Spacing = 14;
            Body.Children.Add(head);

            // 1. packages
            var installed = PackageTypes.All.Where(x => PackageTypes.Installed(x.Id).Count > 0).Select(x => x.Name).ToList();
            if (preview) installed = new List<string> { "Apache", "PHP", "MySQL" };
            var pkText = installed.Count == 0 ? I18n.T("welcome.packagesNone") : I18n.T("welcome.packagesSome", string.Join(", ", installed));
            Body.Children.Add(Step("1", I18n.T("welcome.packagesTitle"), pkText, installed.Count == 0, null,
                Ui.Button(installed.Count == 0 ? I18n.T("welcome.installRecommended") : I18n.T("main.packages"), () => Done(WelcomeAction.Packages), installed.Count == 0 ? "primary" : null, "box")));

            // 2. import (detected in the background)
            _importBtn = Ui.Button(I18n.T("import.start"), () => Done(WelcomeAction.Import), null, "import");
            _importBtn.IsEnabled = false;
            Body.Children.Add(Step("2", I18n.T("welcome.importTitle"), I18n.T("welcome.importSearching"), false, t => _importText = t, _importBtn));

            // 3. start
            var root = Settings.Current.DocumentRoot;
            Body.Children.Add(Step("3", I18n.T("welcome.startTitle"), I18n.T("welcome.startText", root, Settings.Current.Tld), installed.Count > 0, null,
                Ui.Button(I18n.T("main.startAll"), () => Done(WelcomeAction.StartAll), installed.Count > 0 ? "primary" : null, "play")));

            _dontShow = new CheckBox { Content = I18n.T("welcome.dontShow") };
            var links = Ui.Row(
                Ui.Link(I18n.T("welcome.guide"), () => Ui.Open(AppInfo.GitHubUrl + "/blob/main/docs/04-kullanim.md")),
                Ui.Link(I18n.T("main.donate"), () => Ui.Open(AppInfo.DonateUrl)));
            links.Spacing = 18;
            links.Margin = new Thickness(0, 4, 0, 0);
            var left = Ui.Column(4, _dontShow, links);
            left.VerticalAlignment = VerticalAlignment.Center;
            var close = Ui.Button(I18n.T("common.close"), () => Done(WelcomeAction.None));
            close.VerticalAlignment = VerticalAlignment.Center;
            var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
            bottom.Children.Add(left);
            Grid.SetColumn(close, 1);
            bottom.Children.Add(close);
            Body.Children.Add(bottom);

            Opened += async (s, e) => await DetectSources();
            Closing += (s, e) =>
            {
                if (!_preview && _dontShow.IsChecked == true && Settings.Current.ShowWelcome) { Settings.Current.ShowWelcome = false; Settings.Current.Save(); }
            };
        }

        /// <summary>A numbered step card; the description label is handed to describe.</summary>
        static Control Step(string number, string title, string text, bool highlight, Action<TextBlock> describe, Button action)
        {
            var badge = BrandTile.Badge(number, highlight ? Ui.Accent : Ui.Gray);
            badge.VerticalAlignment = VerticalAlignment.Top;
            var desc = Ui.Label(text, "muted");
            if (describe != null) describe(desc);
            action.MinWidth = 150;
            action.HorizontalContentAlignment = HorizontalAlignment.Center;
            action.VerticalAlignment = VerticalAlignment.Center;
            var texts = Ui.Column(3, Ui.Label(title, "subtitle"), desc);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14 };
            grid.Children.Add(badge);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
            var card = Ui.Card(grid, new Thickness(16));
            if (highlight)
            {
                var c = ((ISolidColorBrush)Ui.Accent).Color;
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(160, c.R, c.G, c.B));
            }
            return card;
        }

        async Task DetectSources()
        {
            if (_preview) Sources = new List<ImportSource> { new ImportSource { Kind = "laragon", Product = "Laragon", Root = @"C:\laragon" } };
            else
            {
                try { Sources = await Task.Run(() => Importer.Detect()); }
                catch { Sources = new List<ImportSource>(); }
            }
            if (!IsVisible) return;
            if (Sources.Count == 0) _importText.Text = I18n.T("welcome.importNone");
            else
            {
                _importText.Text = I18n.T("welcome.importFound", string.Join(", ", Sources.Select(x => x.Product).Distinct()));
                _importBtn.IsEnabled = true;
                _importBtn.Classes.Add("primary");
            }
        }

        void Done(WelcomeAction action)
        {
            Action = action;
            if (!_preview && _dontShow.IsChecked == true) { Settings.Current.ShowWelcome = false; Settings.Current.Save(); }
            Close();
        }

        public static Window Preview() { return new WelcomeForm(true); }
    }
}
