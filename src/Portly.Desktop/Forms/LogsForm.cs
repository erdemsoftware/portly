using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>
    /// Live tail of one log file: shows the last ~2 MB, polls once a second for new lines, notices when the file
    /// is cleared or rotated, and keeps only the entries that belong to the site in shared logs.
    /// Only the last MaxShown characters are drawn (a text control cannot lay out megabytes quickly).
    /// </summary>
    public class LogView : UserControl
    {
        readonly SelectableTextBlock _box;
        readonly ScrollViewer _scroll;
        readonly TextBlock _info;
        readonly DispatcherTimer _timer;
        Func<string> _resolve;
        Func<string, bool> _match;
        LogFilter _filter;
        string _file, _textFilter = "";
        long _pos;
        readonly StringBuilder _all = new StringBuilder();
        const int MaxChars = 2 * 1024 * 1024;
        const int MaxShown = 400 * 1024;

        public string CurrentFile { get { return _file; } }

        public LogView()
        {
            _box = new SelectableTextBlock { TextWrapping = TextWrapping.NoWrap, FontSize = 12.5, Foreground = Ui.Brush("ConsoleText") };
            _box.Classes.Add("mono");
            _scroll = new ScrollViewer { Content = _box, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            var frame = new Border { Child = _scroll };
            frame.Classes.Add("console");
            frame.Padding = new Thickness(12, 10, 6, 10);
            _info = Ui.Label("", "muted small");
            _info.TextWrapping = TextWrapping.NoWrap;
            _info.TextTrimming = TextTrimming.CharacterEllipsis;
            _info.Margin = new Thickness(2, 6, 0, 0);
            var dock = new DockPanel();
            DockPanel.SetDock(_info, Dock.Bottom);
            dock.Children.Add(_info);
            dock.Children.Add(frame);
            Content = dock;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Tick();
            DetachedFromVisualTree += (s, e) => _timer.Stop();
            AttachedToVisualTree += (s, e) => { if (_resolve != null) _timer.Start(); };
        }

        public void Show(Func<string> resolve, Func<string, bool> match)
        {
            _resolve = resolve;
            _match = match;
            Reload();
            _timer.Start();
        }

        public string TextFilter
        {
            get { return _textFilter; }
            set { _textFilter = value ?? ""; Render(); }
        }

        public void Reload()
        {
            if (_resolve == null) return;
            _file = _resolve();
            _filter = new LogFilter(_match);
            long end;
            var tail = SiteLogs.ReadTail(_file, SiteLogs.TailBytes, out end);
            _pos = end;
            _all.Clear();
            _all.Append(_filter.Feed(tail, true));
            Render();
        }

        void Tick()
        {
            if (_resolve == null) return;
            var f = _resolve();
            if (!string.Equals(f, _file, StringComparison.OrdinalIgnoreCase)) { Reload(); return; }
            long end;
            var text = SiteLogs.ReadFrom(_file, _pos, out end);
            if (text == null) { if (_pos > 0 || File.Exists(_file)) Reload(); else UpdateInfo(); return; }
            if (text == "") return;
            _pos = end;
            var add = _filter.Feed(text);
            if (add == "") { UpdateInfo(); return; }
            _all.Append(add);
            if (_all.Length > MaxChars + MaxChars / 4) { _all.Remove(0, _all.Length - MaxChars); Render(); return; }
            var visible = Filtered(add);
            if (visible != "")
            {
                bool atEnd = AtEnd();
                var now = (_box.Text ?? "") + visible.Replace("\r", "");
                if (now.Length > MaxShown + MaxShown / 4) { Render(); return; }
                _box.Text = now;
                if (atEnd) ScrollToEnd();
            }
            UpdateInfo();
        }

        bool AtEnd() { return _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 4; }

        void ScrollToEnd() { Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background); }

        string Filtered(string text)
        {
            if (_textFilter == "") return text;
            var sb = new StringBuilder();
            foreach (var line in text.Split('\n'))
                if (line.IndexOf(_textFilter, StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(line).Append('\n');
            return sb.ToString();
        }

        void Render()
        {
            var text = Filtered(_all.ToString()).Replace("\r", "");
            if (text.Length > MaxShown)
            {
                text = text.Substring(text.Length - MaxShown);
                int nl = text.IndexOf('\n');
                if (nl >= 0) text = text.Substring(nl + 1);
            }
            _box.Text = text;
            ScrollToEnd();
            UpdateInfo();
        }

        void UpdateInfo()
        {
            if (_file == null) { _info.Text = ""; return; }
            var fi = new FileInfo(_file);
            _info.Text = fi.Exists ? _file + "  ·  " + Ui.FormatBytes(fi.Length) + (_match != null ? "  ·  " + I18n.T("logs.filtered") : "") : I18n.T("logs.noFile", _file);
        }
    }

    /// <summary>Feature "logs": site menu > Logs... One tab per log the site has, live, with a text filter.</summary>
    public class LogsForm : PortlyDialog
    {
        readonly Site _site;
        readonly List<LogSource> _sources;
        readonly LogView _view;
        readonly List<Button> _tabs = new List<Button>();
        LogSource _current;

        public LogsForm(Site site, string startKey = null) : this(site, SiteLogs.For(site), startKey) { }

        LogsForm(Site site, List<LogSource> sources, string startKey) : base(I18n.T("logs.title", site.Name), 980)
        {
            _site = site;
            _sources = sources;

            var tabs = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var src in _sources)
            {
                var s = src;
                var b = Ui.Button(s.Title, () => Select(s), "nav");
                b.Padding = new Thickness(12, 5);
                b.Margin = new Thickness(0, 0, 6, 6);
                b.Tag = s;
                tabs.Children.Add(b);
                _tabs.Add(b);
            }

            var filter = new TextBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            debounce.Tick += (o, e) => { debounce.Stop(); _view.TextFilter = filter.Text.Trim(); };
            filter.TextChanged += (o, e) => { debounce.Stop(); debounce.Start(); };
            var flabel = Ui.Label(I18n.T("logs.filter"));
            flabel.VerticalAlignment = VerticalAlignment.Center;
            var bar = Ui.Row(flabel, filter,
                Ui.Button(I18n.T("logs.clear"), ClearCurrent),
                Ui.Button(I18n.T("log.folder"), OpenFolder, null, "folder"),
                Ui.Button(I18n.T("logs.openFile"), () => { if (_view.CurrentFile != null && File.Exists(_view.CurrentFile)) DevUi.Edit(_view.CurrentFile); }));
            bar.Margin = new Thickness(0, 4, 0, 10);

            _view = new LogView();
            var grid = new DockPanel();
            DockPanel.SetDock(tabs, Dock.Top);
            DockPanel.SetDock(bar, Dock.Top);
            grid.Children.Add(tabs);
            grid.Children.Add(bar);
            grid.Children.Add(_view);
            DevUi.Fill(this, grid, 980, 640, 640, 400);

            var first = _sources.FirstOrDefault(x => x.Key == startKey) ?? _sources.FirstOrDefault(x => !x.Shared && File.Exists(x.Resolve())) ?? _sources.FirstOrDefault();
            Opened += (o, e) => { if (first != null) Select(first); };
        }

        void Select(LogSource s)
        {
            _current = s;
            foreach (var b in _tabs)
            {
                if (b.Tag == s) { if (!b.Classes.Contains("selected")) b.Classes.Add("selected"); }
                else b.Classes.Remove("selected");
            }
            _view.Show(s.Resolve, s.Match);
        }

        async System.Threading.Tasks.Task ClearCurrent()
        {
            if (_current == null) return;
            var file = _current.Resolve();
            if (!File.Exists(file)) return;
            var msg = _current.Shared ? I18n.T("logs.clearShared", Path.GetFileName(file)) : I18n.T("logs.clearConfirm", file);
            if (!await Dialogs.Confirm(this, msg)) return;
            try { SiteLogs.Truncate(file); _view.Reload(); }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
        }

        void OpenFolder()
        {
            var f = _view.CurrentFile;
            var dir = f != null ? Path.GetDirectoryName(f) : Paths.Logs;
            if (!Directory.Exists(dir)) dir = _site.Folder;
            DevUi.OpenFolder(dir);
        }

        public static Window Preview()
        {
            var dir = DevUi.Temp("logs");
            var site = new Site { Name = "shop", Host = "shop.portly", Folder = dir, DocRoot = dir, Valid = true };
            var php = Path.Combine(dir, "php_errors.log");
            File.WriteAllText(php,
                "[11-Nov-2026 09:41:07 UTC] PHP Warning:  Undefined variable $cart in " + dir + "\\cart.php on line 42\n" +
                "[11-Nov-2026 09:41:09 UTC] PHP Fatal error:  Uncaught TypeError: count(): Argument #1 ($value) must be of type Countable|array, null given in " + dir + "\\cart.php:57\n" +
                "Stack trace:\n#0 " + dir + "\\index.php(12): Cart->total()\n#1 {main}\n  thrown in " + dir + "\\cart.php on line 57\n" +
                "[11-Nov-2026 09:43:21 UTC] PHP Notice:  session_start(): Ignoring session_start() because a session is already active in " + dir + "\\bootstrap.php on line 8\n");
            var laravel = Path.Combine(dir, "laravel.log");
            File.WriteAllText(laravel,
                "[2026-11-11 09:40:12] local.INFO: Order #1042 created {\"user\":7,\"total\":129.9}\n" +
                "[2026-11-11 09:40:15] local.WARNING: Stock low for sku TS-001\n" +
                "[2026-11-11 09:44:02] local.ERROR: SQLSTATE[HY000] [2002] Connection refused\n");
            var sources = new List<LogSource>
            {
                new LogSource { Key = "php", Title = I18n.T("logs.php"), Resolve = () => php, Match = e => true },
                new LogSource { Key = "web", Title = I18n.T("logs.web", "Apache"), Resolve = () => Path.Combine(dir, "apache-error.log"), Match = e => true },
                new LogSource { Key = "laravel", Title = "Laravel", Resolve = () => laravel },
                new LogSource { Key = "job-queue", Title = I18n.T("logs.job", "queue"), Resolve = () => Path.Combine(dir, "queue.log") },
            };
            return new LogsForm(site, sources, "php");
        }
    }
}
