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

namespace Portly.Desktop.Forms
{
    /// <summary>Packages > Remove unused...: versions that are neither active nor chosen by a site, with their sizes.</summary>
    public class CleanupForm : PortlyDialog
    {
        sealed class Line
        {
            public UnusedVersion Item;
            public CheckBox Check;
        }

        readonly StackPanel _rows = new StackPanel();
        readonly TextBlock _info;
        readonly Button _remove;
        readonly List<Line> _lines = new List<Line>();
        readonly bool _sample;
        public int Removed;

        public CleanupForm() : this(null) { }

        CleanupForm(List<UnusedVersion> sample) : base(I18n.T("cleanup.title"), 560)
        {
            _sample = sample != null;
            MakeResizable(460);
            MinWidth = 460;
            MinHeight = 360;

            var intro = Ui.Label(I18n.T("cleanup.intro"), "muted");
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*,110") };
            var h1 = Ui.Label(I18n.T("col.package"), "bold small", Ui.Muted);
            var h2 = Ui.Label(I18n.T("cleanup.size"), "bold small", Ui.Muted);
            h1.Margin = new Thickness(6, 0, 0, 0);
            h2.Margin = new Thickness(0, 0, 12, 0);
            h2.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(h1, 1);
            Grid.SetColumn(h2, 2);
            head.Children.Add(h1);
            head.Children.Add(h2);
            var headBox = new Border { Padding = new Thickness(0, 8), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
            var listDock = new DockPanel();
            DockPanel.SetDock(headBox, Dock.Top);
            listDock.Children.Add(headBox);
            listDock.Children.Add(new ScrollViewer { Content = _rows });
            var card = Ui.Card(listDock, new Thickness(0));
            card.ClipToBounds = true;

            _info = Ui.Label("", "muted");
            _remove = Ui.Button(I18n.T("cleanup.remove"), async () => await RemoveChecked(), "primary");
            var close = Ui.Button(I18n.T("common.close"), () => Close());
            var buttons = Ui.Row(_remove, close);

            var bottom = Ui.Column(8, _info, buttons);
            bottom.Margin = new Thickness(0, 10, 0, 0);
            var dock = new DockPanel { Margin = new Thickness(20, 16, 20, 18) };
            intro.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(intro, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(intro);
            dock.Children.Add(bottom);
            dock.Children.Add(card);
            Content = dock;

            if (sample != null) Show(sample);
            else Opened += async (s, e) => await Fill();
        }

        /// <summary>Opens the cleanup window. Returns how many versions were removed.</summary>
        public static async Task<int> ShowAsync(Window owner)
        {
            var d = new CleanupForm();
            await d.ShowDialog(owner);
            return d.Removed;
        }

        public static Window Preview()
        {
            return new CleanupForm(new List<UnusedVersion>
            {
                new UnusedVersion { Type = "php", Folder = "php-8.3.14", Size = 118L * 1024 * 1024 },
                new UnusedVersion { Type = "php", Folder = "php-8.2.26", Size = 112L * 1024 * 1024 },
                new UnusedVersion { Type = "mysql", Folder = "mysql-8.0.40", Size = 640L * 1024 * 1024 },
                new UnusedVersion { Type = "apache", Folder = "httpd-2.4.62", Size = 54L * 1024 * 1024 },
            });
        }

        async Task Fill()
        {
            _info.Text = I18n.T("cleanup.scanning");
            _remove.IsEnabled = false;
            var items = await Task.Run(() => Cleanup.Find());
            Show(items);
        }

        void Show(List<UnusedVersion> items)
        {
            _rows.Children.Clear();
            _lines.Clear();
            foreach (var u in items.OrderBy(x => x.Type).ThenBy(x => x.Folder))
            {
                var line = new Line { Item = u, Check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), MinWidth = 0, MinHeight = 0, Padding = new Thickness(0) } };
                line.Check.IsCheckedChanged += (s, e) => UpdateInfo();
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*,110") };
                var name = Ui.Label(u.Title);
                name.Margin = new Thickness(6, 0, 0, 0);
                name.VerticalAlignment = VerticalAlignment.Center;
                var size = Ui.Label(Ui.FormatBytes(u.Size));
                size.Margin = new Thickness(0, 0, 12, 0);
                size.HorizontalAlignment = HorizontalAlignment.Right;
                size.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(name, 1);
                Grid.SetColumn(size, 2);
                grid.Children.Add(line.Check);
                grid.Children.Add(name);
                grid.Children.Add(size);
                _rows.Children.Add(new Border { Padding = new Thickness(0, 2), Child = grid });
                _lines.Add(line);
            }
            if (items.Count == 0) _info.Text = I18n.T("cleanup.none");
            UpdateInfo();
        }

        void UpdateInfo()
        {
            var sel = Checked();
            _remove.IsEnabled = sel.Count > 0;
            if (_lines.Count > 0) _info.Text = I18n.T("cleanup.selected", sel.Count, Ui.FormatBytes(sel.Sum(x => x.Size)));
        }

        List<UnusedVersion> Checked() { return _lines.Where(l => l.Check.IsChecked == true).Select(l => l.Item).ToList(); }

        async Task RemoveChecked()
        {
            var sel = Checked();
            if (sel.Count == 0 || _sample) return;
            if (!await Dialogs.Confirm(this, I18n.T("cleanup.confirm", sel.Count, Ui.FormatBytes(sel.Sum(x => x.Size))))) return;
            _remove.IsEnabled = false;
            var skipped = new List<string>();
            await Task.Run(() =>
            {
                foreach (var u in sel)
                {
                    // never remove a version while one of its programs runs
                    if (Cleanup.IsInUse(u.Dir)) { skipped.Add(u.Title); continue; }
                    try { Installer.Remove(u.Type, u.Folder); Removed++; }
                    catch (Exception ex) { skipped.Add(u.Title + ": " + ex.Message); }
                }
            });
            if (skipped.Count > 0) await Dialogs.Warning(this, I18n.T("cleanup.skipped", string.Join("\n", skipped)));
            await Fill();
        }
    }
}
