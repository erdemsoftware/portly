using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>Site card badges and site menu items of the DevWorkflow features (keeps page edits to two calls).</summary>
    public static class DevWorkflowUi
    {
        /// <summary>"2 jobs" and "portly.yml" pills for a site card.</summary>
        public static List<Control> SiteBadges(Site site, Window owner, Action refresh)
        {
            var list = new List<Control>();
            if (!site.Valid) return list;
            if (Features.IsOn("jobs"))
            {
                int n = Jobs.ActiveCount(site.Name);
                if (n > 0)
                {
                    var pill = new Pill(I18n.T("jobs.badge", n), Ui.Green);
                    Clickable(pill, async () => { await new JobsForm(site).ShowDialog(owner); refresh(); });
                    ToolTip.SetTip(pill, I18n.T("jobs.badgeTip", n));
                    list.Add(pill);
                }
            }
            if (ProjectFile.IsPending(site))
            {
                var pill = new Pill("portly.yml", Ui.Orange);
                Clickable(pill, () => ApplyProjectFile(site, owner, refresh));
                ToolTip.SetTip(pill, I18n.T("pf.notice"));
                list.Add(pill);
            }
            return list;
        }

        static void Clickable(Control c, Func<Task> click)
        {
            c.Cursor = new Cursor(StandardCursorType.Hand);
            c.PointerPressed += async (s, e) =>
            {
                if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                await Ui.Run(click, c);
            };
        }

        static async Task ApplyProjectFile(Site site, Window owner, Action refresh)
        {
            await new ProjectFileForm(site).ShowDialog(owner);
            refresh();
        }

        /// <summary>Adds MenuItem controls to a menu's item list (ContextMenu.Items, MenuFlyout.Items, MenuItem.Items).</summary>
        public static void AddSiteMenuItems(IList items, Site site, Window owner, Action refresh)
        {
            if (Features.IsOn("logs"))
                Add(items, I18n.T("logs.menu"), () => new LogsForm(site).ShowDialog(owner));
            if (Features.IsOn("jobs") && site.Valid)
                Add(items, I18n.T("jobs.menu"), async () => { await new JobsForm(site).ShowDialog(owner); refresh(); });
            if (Features.IsOn("dumps") && site.Valid)
                Add(items, I18n.T("profiler.menu"), () => new ProfilerForm(site).ShowDialog(owner));
            if (Features.IsOn("projectFile") && site.Valid)
            {
                if (ProjectFile.Find(site) != null)
                    Add(items, I18n.T("pf.menuApply"), () => ApplyProjectFile(site, owner, refresh));
                Add(items, I18n.T("pf.menuCreate"), async () =>
                {
                    var existing = ProjectFile.Find(site);
                    if (existing != null && !await Dialogs.Confirm(owner, I18n.T("pf.overwrite", existing))) return;
                    try
                    {
                        var file = ProjectFile.Write(site);
                        DevUi.Edit(file);
                        refresh();
                    }
                    catch (Exception ex) { await Dialogs.Warning(owner, ex.Message); }
                });
            }
        }

        static void Add(IList items, string text, Func<Task> click)
        {
            var item = new MenuItem { Header = text };
            item.Click += async (s, e) => await Ui.Run(click, item);
            items.Add(item);
        }
    }

    /// <summary>Small helpers shared by the developer-workflow dialogs.</summary>
    static class DevUi
    {
        /// <summary>Opens a file in a text editor (Notepad on Windows, the default app elsewhere).</summary>
        public static void Edit(string file)
        {
            if (OS.IsWindows) WinUtil.Edit(file);
            else Ui.Open(file);
        }

        public static void OpenFolder(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); } catch { }
            Ui.Open(dir);
        }

        /// <summary>Window content that fills a resizable dialog (no button bar): padding around the content.</summary>
        public static void Fill(PortlyDialog d, Control content, double width, double height, double minWidth, double minHeight)
        {
            d.Width = width;
            d.Height = height;
            d.MinWidth = minWidth;
            d.MinHeight = minHeight;
            d.SizeToContent = SizeToContent.Manual;
            d.CanResize = true;
            d.MaxHeight = double.PositiveInfinity;
            d.Content = new Border { Padding = new Thickness(16, 12), Child = content };
        }

        /// <summary>Toolbar that wraps onto more lines in a narrow window.</summary>
        public static WrapPanel Bar(params Control[] children)
        {
            var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
            foreach (var c in children)
            {
                c.Margin = new Thickness(0, 0, 8, 6);
                bar.Children.Add(c);
            }
            return bar;
        }

        /// <summary>Folder under the temp folder for screenshot sample files.</summary>
        public static string Temp(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), "portly-preview", name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Card with a header row and selectable rows (the ListView of the Windows app).</summary>
    sealed class GridList : UserControl
    {
        readonly string _columns;
        readonly bool[] _right;
        readonly ListBox _box;

        public ListBox Box { get { return _box; } }

        /// <param name="columns">Avalonia column definitions, e.g. "130,2*,110,*".</param>
        public GridList(string columns, string[] headers, bool[] right = null)
        {
            _columns = columns;
            _right = right ?? new bool[headers.Length];
            var head = Row(headers, true, null);
            var headBox = new Border { Child = head, Padding = new Thickness(12, 8), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
            _box = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            Mark(":selected", Ui.Brush("SidebarActive"));
            Mark(":selected:pointerover", Ui.Brush("SidebarActive"));
            Mark(":pointerover", Ui.Brush("Hover"));
            var dock = new DockPanel();
            DockPanel.SetDock(headBox, Dock.Top);
            dock.Children.Add(headBox);
            dock.Children.Add(_box);
            var card = Ui.Card(dock, new Thickness(0));
            card.ClipToBounds = true;
            Content = card;
        }

        void Mark(string state, IBrush background)
        {
            var style = new Style(x => x.OfType<ListBoxItem>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"));
            style.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, background));
            style.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, Ui.Text));
            _box.Styles.Add(style);
        }

        Grid Row(string[] cells, bool header, IBrush color)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(_columns), ColumnSpacing = 12 };
            for (int i = 0; i < cells.Length; i++)
            {
                var t = new TextBlock
                {
                    Text = cells[i] ?? "", TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                    HorizontalAlignment = _right[i] ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                };
                if (header) { t.FontSize = 11; t.FontWeight = FontWeight.SemiBold; t.Foreground = Ui.Muted; t.Text = Ui.Upper(t.Text); }
                else if (color != null) t.Foreground = color;
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }
            return g;
        }

        public void Clear() { _box.Items.Clear(); }

        public ListBoxItem Add(object tag, string[] cells, IBrush color = null)
        {
            var item = new ListBoxItem { Tag = tag, Content = Row(cells, false, color), Padding = new Thickness(12, 6), MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _box.Items.Add(item);
            return item;
        }

        public object Selected
        {
            get { var i = _box.SelectedItem as ListBoxItem; return i == null ? null : i.Tag; }
        }
    }
}
