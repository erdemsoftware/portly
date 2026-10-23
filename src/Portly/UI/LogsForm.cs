using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>
    /// Live tail of one log file: shows the last ~2 MB, polls once a second for new lines, notices when the file
    /// is cleared or rotated, and keeps only the entries that belong to the site in shared logs.
    /// </summary>
    public class LogView : Panel
    {
        readonly RichTextBox _box;
        readonly Label _info;
        readonly Timer _timer;
        Func<string> _resolve;
        Func<string, bool> _match;
        LogFilter _filter;
        string _file, _textFilter = "";
        long _pos;
        readonly StringBuilder _all = new StringBuilder();
        const int MaxChars = 2 * 1024 * 1024;

        public string CurrentFile { get { return _file; } }

        public LogView()
        {
            var p = Theme.Current;
            BackColor = Color.Transparent;
            var frame = new Card { Dock = DockStyle.Fill, BackColor = p.ConsoleBg, BorderColor = p.ConsoleBg, Padding = Ui.P(12, 10, 6, 10), Radius = 10 };
            _box = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = p.ConsoleBg, ForeColor = p.ConsoleText, Font = Ui.Mono, Tag = "console", DetectUrls = false, WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both };
            frame.Controls.Add(_box);
            _info = Ui.Label("", Ui.Small, Ui.Muted);
            _info.Dock = DockStyle.Bottom;
            _info.AutoSize = false;
            _info.AutoEllipsis = true;
            _info.Height = Ui.S(22);
            _info.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(frame);
            Controls.Add(_info);
            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Tick();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Stop(); _timer.Dispose(); }
            base.Dispose(disposing);
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
            if (_resolve == null || IsDisposed) return;
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
                bool atEnd = _box.SelectionStart >= _box.TextLength - 1;
                _box.AppendText(visible.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
                if (atEnd) { _box.SelectionStart = _box.TextLength; _box.ScrollToCaret(); }
            }
            UpdateInfo();
        }

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
            _box.Text = Filtered(_all.ToString()).Replace("\n", "\r\n");
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
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
    public class LogsForm : Form
    {
        readonly Site _site;
        readonly List<LogSource> _sources;
        readonly LogView _view;
        readonly List<ModernButton> _tabs = new List<ModernButton>();
        LogSource _current;

        public LogsForm(Site site, string startKey = null)
        {
            _site = site;
            Ui.Apply(this);
            Text = I18n.T("logs.title", site.Name);
            Size = Ui.S(980, 640);
            MinimumSize = Ui.S(640, 400);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Padding = Ui.P(16, 12, 16, 12);

            _sources = SiteLogs.For(site);
            var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Padding = Ui.P(0, 0, 0, 6) };
            foreach (var src in _sources)
            {
                var s = src;
                var b = new ModernButton { Text = s.Title, Kind = ButtonKind.Nav, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Ui.P(12, 4, 12, 4), MinimumSize = new Size(0, Ui.S(32)), Margin = Ui.P(0, 0, 6, 0), Font = Ui.Font, Tag = s };
                b.Click += (o, e) => Select(s);
                tabs.Controls.Add(b);
                _tabs.Add(b);
            }

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Padding = Ui.P(0, 4, 0, 8) };
            bar.Controls.Add(Ui.Label(I18n.T("logs.filter")));
            var filter = new TextBox { Width = Ui.S(260), Margin = Ui.P(0, 4, 12, 0) };
            var debounce = new Timer { Interval = 300 };
            debounce.Tick += (o, e) => { debounce.Stop(); _view.TextFilter = filter.Text.Trim(); };
            filter.TextChanged += (o, e) => { debounce.Stop(); debounce.Start(); };
            bar.Controls.Add(filter);
            bar.Controls.Add(Ui.Button(I18n.T("logs.clear"), (o, e) => ClearCurrent()));
            bar.Controls.Add(Ui.Button(I18n.T("log.folder"), (o, e) => OpenFolder(), false, "folder"));
            bar.Controls.Add(Ui.Button(I18n.T("logs.openFile"), (o, e) => { if (_view.CurrentFile != null && File.Exists(_view.CurrentFile)) WinUtil.Edit(_view.CurrentFile); }));

            _view = new LogView { Dock = DockStyle.Fill };
            Controls.Add(_view);
            Controls.Add(bar);
            Controls.Add(tabs);
            _view.BringToFront();

            var first = _sources.FirstOrDefault(x => x.Key == startKey) ?? _sources.FirstOrDefault(x => !x.Shared && File.Exists(x.Resolve())) ?? _sources.FirstOrDefault();
            Load += (o, e) => { if (first != null) Select(first); };
        }

        void Select(LogSource s)
        {
            _current = s;
            foreach (var b in _tabs) { b.Active = b.Tag == s; b.Invalidate(); }
            _view.Show(s.Resolve, s.Match);
        }

        void ClearCurrent()
        {
            if (_current == null) return;
            var file = _current.Resolve();
            if (!File.Exists(file)) return;
            var msg = _current.Shared ? I18n.T("logs.clearShared", Path.GetFileName(file)) : I18n.T("logs.clearConfirm", file);
            if (!Ui.Confirm(this, msg)) return;
            try { SiteLogs.Truncate(file); _view.Reload(); }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        void OpenFolder()
        {
            var f = _view.CurrentFile;
            var dir = f != null ? Path.GetDirectoryName(f) : Paths.Logs;
            if (!Directory.Exists(dir)) dir = _site.Folder;
            WinUtil.OpenFolder(dir);
        }
    }
}
