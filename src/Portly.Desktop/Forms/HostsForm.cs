using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;

namespace Portly.Desktop.Forms
{
    /// <summary>
    /// Hosts file editor inside Portly: reading needs no rights, saving asks for elevation once (UAC on Windows).
    /// Search bar: Ctrl+F, F3 / Shift+F3, Enter / Shift+Enter, Esc, match counter.
    /// </summary>
    public class HostsForm : PortlyDialog
    {
        readonly TextBox _text, _find;
        readonly TextBlock _count;
        readonly Button _save, _close;
        string _loaded;
        bool _busy, _force, _preview;

        public HostsForm() : this(null) { }

        HostsForm(string sample) : base(I18n.T("hosts.editTitle"), 760)
        {
            _preview = sample != null;
            MakeResizable(560);
            MinWidth = 520;
            MinHeight = 360;
            ShowInTaskbar = false;

            // an editor fills the window: replace the scrolling body of the base class by a docked layout
            var dock = (DockPanel)Content;
            dock.Children.Remove(dock.Children[1]);

            var intro = Ui.Label(I18n.T("hosts.editIntro", HostsFile.FilePath), "muted");
            _find = new TextBox { Width = 220, VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(_find, I18n.T("hosts.find"));
            _count = Ui.Label("", "muted small");
            _count.VerticalAlignment = VerticalAlignment.Center;
            var bar = Ui.Row(Ui.Label(I18n.T("hosts.find")), _find, FindButton("▲", "hosts.findPrev", false), FindButton("▼", "hosts.findNext", true), _count);
            bar.Children[0].VerticalAlignment = VerticalAlignment.Center;
            bar.Margin = new Thickness(0, 10, 0, 8);

            // HideSelection off: the match stays visible while the focus is in the search box
            _text = new TextBox
            {
                AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, NewLine = "\n",
                IsInactiveSelectionHighlightEnabled = true, VerticalContentAlignment = VerticalAlignment.Top,
            };
            _text.Classes.Add("mono");
            _text.SelectionBrush = new SolidColorBrush(UI.Theme.Current.Accent, 0.4);
            ScrollViewer.SetHorizontalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);

            var top = new StackPanel { Children = { intro, bar } };
            DockPanel.SetDock(top, Dock.Top);
            var area = new DockPanel { Margin = new Thickness(24, 20, 24, 8) };
            area.Children.Add(top);
            area.Children.Add(_text);
            dock.Children.Add(area);

            _save = Ui.Button(I18n.T("common.save"), Save, "primary");
            _close = Ui.Button(I18n.T("common.close"), () => Close());
            Buttons.Children.Add(_save);
            Buttons.Children.Add(_close);

            AddHandler(KeyDownEvent, async (s, e) =>
            {
                bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
                if (ctrl && e.Key == Key.F) { e.Handled = true; _find.Focus(); _find.SelectAll(); }
                else if (e.Key == Key.F3) { e.Handled = true; Find((e.KeyModifiers & KeyModifiers.Shift) == 0); }
                else if (ctrl && e.Key == Key.S) { e.Handled = true; await Save(); }
            }, RoutingStrategies.Tunnel);
            _find.TextChanged += (s, e) => Find(true, true);
            _text.TextChanged += (s, e) => ShowCount(FindAll(_text.Text ?? "", _find.Text ?? ""));
            _find.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Find((e.KeyModifiers & KeyModifiers.Shift) == 0); }
                else if (e.Key == Key.Escape) { e.Handled = true; _text.Focus(); }
            };
            Closing += async (s, e) =>
            {
                if (_force) return;
                if (_busy) { e.Cancel = true; return; }
                if (!Changed) return;
                e.Cancel = true;
                if (await Dialogs.Confirm(this, I18n.T("hosts.unsaved"))) { _force = true; Close(); }
            };
            Opened += (s, e) => { _text.SelectionStart = _text.SelectionEnd = 0; _text.Focus(); };
            if (sample != null) { _loaded = Normalize(sample); _text.Text = _loaded; }
            else Reload();
        }

        bool Changed { get { return _loaded != null && Normalize(_text.Text ?? "") != _loaded; } }

        static string Normalize(string s) { return (s ?? "").Replace("\r\n", "\n"); }

        /// <summary>The file wants the platform's line breaks back.</summary>
        static string Lines(string s) { return Normalize(s).Replace("\n", Environment.NewLine); }

        Button FindButton(string arrow, string key, bool forward)
        {
            var b = Ui.Button(arrow, () => Find(forward));
            b.Padding = new Thickness(9, 6);
            Avalonia.Automation.AutomationProperties.SetName(b, I18n.T(key));
            return b;
        }

        /// <summary>Where every match starts, ignoring case.</summary>
        public static List<int> FindAll(string text, string query)
        {
            var all = new List<int>();
            if (string.IsNullOrEmpty(query)) return all;
            for (var i = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(query, i + query.Length, StringComparison.OrdinalIgnoreCase))
                all.Add(i);
            return all;
        }

        /// <summary>The match to go to from a position, wrapping around at either end; -1 without matches.</summary>
        public static int FindStep(List<int> all, int from, bool forward)
        {
            if (all.Count == 0) return -1;
            if (forward)
            {
                foreach (var at in all) if (at >= from) return at;
                return all[0];
            }
            for (var i = all.Count - 1; i >= 0; i--) if (all[i] < from) return all[i];
            return all[all.Count - 1];
        }

        int SelStart { get { return Math.Min(_text.SelectionStart, _text.SelectionEnd); } }
        int SelLength { get { return Math.Abs(_text.SelectionEnd - _text.SelectionStart); } }

        /// <summary>stay: typing keeps a match that still fits instead of moving on to the next one.</summary>
        void Find(bool forward, bool stay = false)
        {
            var query = _find.Text ?? "";
            if (query.Length == 0 && !stay) { _find.Focus(); return; }
            var all = FindAll(_text.Text ?? "", query);
            var at = FindStep(all, SelStart + (forward && !stay && SelLength > 0 ? 1 : 0), forward);
            if (at >= 0)
            {
                _text.CaretIndex = at + query.Length;
                _text.SelectionStart = at;
                _text.SelectionEnd = at + query.Length;
                Reveal(at);
            }
            ShowCount(all);
        }

        /// <summary>Scroll the match into view (the box does not do it while the search box has the focus).</summary>
        void Reveal(int at)
        {
            var sv = _text.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv == null) return;
            var text = _text.Text ?? "";
            int line = 0;
            for (int i = 0; i < at && i < text.Length; i++) if (text[i] == '\n') line++;
            var probe = new FormattedText("Ag", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(_text.FontFamily), _text.FontSize, Brushes.Black);
            double lh = probe.Height, y = line * lh, view = sv.Viewport.Height;
            if (y < sv.Offset.Y || y + lh > sv.Offset.Y + view)
                sv.Offset = new Vector(sv.Offset.X, Math.Max(0, y - view / 2));
        }

        void ShowCount(List<int> all)
        {
            var query = _find.Text ?? "";
            if (query.Length == 0) _count.Text = "";
            else if (all.Count == 0) _count.Text = I18n.T("hosts.noMatch");
            else
            {
                var i = SelLength == query.Length ? all.IndexOf(SelStart) : -1;
                _count.Text = (i >= 0 ? (i + 1) + " / " : "") + all.Count;
            }
        }

        /// <summary>Without a readable file the box stays locked: saving an empty text would wipe it.</summary>
        void Reload()
        {
            try
            {
                _loaded = Normalize(HostsFile.ReadAll());
                _text.Text = _loaded;
            }
            catch (Exception ex)
            {
                _loaded = null;
                _text.IsReadOnly = true;
                _save.IsEnabled = false;
                Opened += async (s, e) => await Dialogs.Warning(this, I18n.T("hosts.readFailed", ex.Message));
            }
        }

        async Task Save()
        {
            if (_busy || _loaded == null || _preview) return;
            if (!Changed) { Close(); return; }
            string disk;
            try { disk = Normalize(HostsFile.ReadAll()); }
            catch (Exception ex) { await Dialogs.Warning(this, I18n.T("hosts.readFailed", ex.Message)); return; }
            // Portly's own sync or another program wrote in the meantime: do not overwrite that with an old copy
            if (disk != _loaded && !await Dialogs.Confirm(this, I18n.T("hosts.changedOnDisk"))) return;

            var content = Lines(_text.Text ?? "");
            _busy = true;
            _save.IsEnabled = _close.IsEnabled = _text.IsEnabled = false;
            int code;
            try { code = await Task.Run(() => HostsFile.SaveAll(content)); }
            catch (Exception ex) { Log.Error("Hosts save", ex); code = -1; }
            _busy = false;
            _save.IsEnabled = _close.IsEnabled = _text.IsEnabled = true;

            if (code == 0)
            {
                _loaded = Normalize(content);
                Log.Info(I18n.T("log.hostsUpdated"));
                Close();
            }
            else if (code == -2) await Dialogs.Warning(this, I18n.T("hosts.saveCancelled"));
            else
            {
                Log.Warn(I18n.T("hosts.saveFailed", code));
                await Dialogs.Warning(this, I18n.T("hosts.saveFailed", code));
            }
        }

        public static Window Preview()
        {
            var sb = new StringBuilder();
            sb.Append("# Copyright (c) 1993-2009 Microsoft Corp.\n#\n# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.\n#\n");
            sb.Append("# localhost name resolution is handled within DNS itself.\n#\t127.0.0.1       localhost\n#\t::1             localhost\n\n");
            sb.Append("# >>> Portly (auto-generated, do not edit) >>>\n");
            foreach (var h in new[] { "shop.portly", "blog.portly", "api.shop.portly", "hakedis.mayerdem.portly", "wapflow.portly" })
                sb.Append("127.0.0.1\t").Append(h).Append("\n");
            sb.Append("# <<< Portly <<<\n\n127.0.0.1\tdev.example.test\n");
            var f = new HostsForm(sb.ToString());
            f._find.Text = "127.0.0.1";
            f.Opened += (s, e) => Avalonia.Threading.Dispatcher.UIThread.Post(() => f.Find(true), Avalonia.Threading.DispatcherPriority.Background);
            return f;
        }
    }
}
