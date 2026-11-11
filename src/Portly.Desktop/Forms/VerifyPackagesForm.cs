using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>
    /// Tools > Verify packages (antivirus / quarantine): compares the installed files with the list written at
    /// install time, shows Windows Defender detections under the Portly folder, repairs and offers a Defender scan.
    /// </summary>
    public class VerifyPackagesForm : PortlyDialog
    {
        readonly TextBox _out;
        readonly TextBlock _summary;
        readonly ProgressBar _progress;
        readonly Button _full, _repair, _exclude, _history, _scan, _close;
        List<IntegrityProblem> _problems = new List<IntegrityProblem>();
        CancellationTokenSource _cts;
        bool _busy;
        /// <summary>Something was repaired: the caller restarts / refreshes.</summary>
        public bool Repaired;

        public VerifyPackagesForm(List<IntegrityProblem> found = null) : base(I18n.T("verify.title"), 760)
        {
            MakeResizable(540);
            MinWidth = 600;
            MinHeight = 420;

            _summary = Ui.Label("", "bold");
            var intro = Ui.Label(I18n.T("verify.intro"), "muted");
            var top = Ui.Column(8, intro, _summary);
            top.Margin = new Thickness(0, 0, 0, 10);

            _out = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), Foreground = Ui.Brush("ConsoleText"),
            };
            _out.Classes.Add("mono");
            ScrollViewer.SetHorizontalScrollBarVisibility(_out, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_out, ScrollBarVisibility.Auto);
            var console = new Border { Child = _out };
            console.Classes.Add("console");

            _progress = new ProgressBar { Height = 6, IsIndeterminate = true, IsVisible = false };
            _full = Ui.Button(I18n.T("verify.full"), async () => await FullCheck(), "primary");
            _repair = Ui.Button(I18n.T("verify.repair"), async () => await Repair(), null, "update");
            _exclude = Ui.Button(I18n.T("verify.exclude"), async () => await AddExclusion());
            _history = Ui.Button(I18n.T("verify.history"), async () => await ShowHistory());
            _scan = Ui.Button(I18n.T("verify.scan"), async () => await Scan());
            _close = Ui.Button(I18n.T("common.close"), () => { if (_busy) { if (_cts != null) _cts.Cancel(); } else Close(); });
            var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
            foreach (var b in new[] { _full, _repair, _exclude, _history, _scan, _close }) row.Children.Add(b);
            // Windows Defender features
            _exclude.IsVisible = OS.IsWindows;
            _scan.IsVisible = OS.IsWindows && Antivirus.MpCmdRun != null && OsInfo.IsWin10OrLater;
            _history.IsVisible = OS.IsWindows && OsInfo.IsWin10OrLater;
            var bottom = Ui.Column(8, _progress, row);
            bottom.Margin = new Thickness(0, 10, 0, 0);

            var dock = new DockPanel { Margin = new Thickness(20, 16, 20, 18) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(bottom);
            dock.Children.Add(console);
            Content = dock;

            if (found != null) ShowProblems(found, -1, 0);
            else _summary.Text = I18n.T("verify.notRun");
            UpdateButtons();
            Opened += (s, e) => { if (_problems.Count > 0) _repair.Focus(); else _full.Focus(); };
        }

        /// <summary>Opens the window. True when something was repaired (the caller restarts / refreshes).</summary>
        public static async Task<bool> ShowAsync(Window owner, List<IntegrityProblem> found = null)
        {
            var d = new VerifyPackagesForm(found);
            await d.ShowDialog(owner);
            return d.Repaired;
        }

        public static Window Preview()
        {
            var p1 = new IntegrityProblem { Type = "php", Folder = "php-8.4.12", Dir = @"C:\Portly\bin\php\php-8.4.12-nts-Win32-vs17-x64" };
            p1.Missing.Add("php.exe");
            p1.Missing.Add(@"ext\php_curl.dll");
            p1.Changed.Add("php8ts.dll");
            var p2 = new IntegrityProblem { Type = "apache", Folder = "httpd-2.4.66", Dir = @"C:\Portly\bin\apache\httpd-2.4.66" };
            p2.Missing.Add(@"bin\httpd.exe");
            return new VerifyPackagesForm(new List<IntegrityProblem> { p1, p2 });
        }

        void UpdateButtons()
        {
            _full.IsEnabled = _history.IsEnabled = _scan.IsEnabled = _exclude.IsEnabled = !_busy;
            _repair.IsEnabled = !_busy && _problems.Count > 0;
            _close.Content = _busy ? I18n.T("common.cancel") : I18n.T("common.close");
            _progress.IsVisible = _busy;
        }

        void ShowProblems(List<IntegrityProblem> problems, int checkedCount, int withoutList)
        {
            _problems = problems;
            var sb = new StringBuilder();
            if (problems.Count == 0)
                _summary.Text = checkedCount >= 0 ? I18n.T("verify.allOk", checkedCount) : I18n.T("verify.notRun");
            else
                _summary.Text = string.Join("\n", problems.Select(p => I18n.T("verify.problem", p.Count, p.Title)));
            _summary.Foreground = problems.Count == 0 ? Ui.Green : Ui.Red;
            foreach (var p in problems)
            {
                sb.AppendLine(p.Title + "  (" + p.Dir + ")");
                foreach (var m in p.Missing.Take(40)) sb.AppendLine("  " + I18n.T("verify.missing") + ": " + m);
                foreach (var c in p.Changed.Take(40)) sb.AppendLine("  " + I18n.T("verify.changed") + ": " + c);
                if (p.Count > 80) sb.AppendLine("  ...");
                sb.AppendLine();
            }
            if (withoutList > 0) sb.AppendLine(I18n.T("verify.noList", withoutList));
            _out.Text = sb.ToString();
        }

        async Task FullCheck()
        {
            _busy = true; _cts = new CancellationTokenSource(); UpdateButtons();
            try
            {
                int count = 0, without = 0;
                var token = _cts.Token;
                var list = await Task.Run(() => Integrity.FullCheck(t => Ui.Post(() => _summary.Text = I18n.T("verify.checking", t)), token, out count, out without));
                ShowProblems(list, count, without);
            }
            catch (OperationCanceledException) { _summary.Text = I18n.T("pkg.cancelled"); }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
            _busy = false; UpdateButtons();
        }

        async Task Repair()
        {
            if (_problems.Count == 0) return;
            _busy = true; _cts = new CancellationTokenSource(); UpdateButtons();
            var failed = new List<string>();
            foreach (var p in _problems.ToList())
            {
                try
                {
                    await Integrity.RepairAsync(p, ip => Ui.Post(() =>
                        _summary.Text = p.Title + " - " + I18n.T("pkg.stage." + ip.Stage) + (ip.Percent >= 0 && ip.Stage == "download" ? " " + ip.Percent + "%" : "")), _cts.Token);
                    Repaired = true;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { failed.Add(p.Title + ": " + ex.Message); Log.Error(p.Title, ex); }
            }
            // check again what is still wrong
            var still = await Task.Run(() => _problems.Select(p => Integrity.Check(p.Dir, false)).Where(p => p != null && p.Count > 0).ToList());
            ShowProblems(still, -1, 0);
            if (still.Count == 0) { _summary.Text = I18n.T("verify.repairedAll"); _summary.Foreground = Ui.Green; }
            if (failed.Count > 0) await Dialogs.Warning(this, string.Join("\n", failed));
            else if (still.Count > 0) await Dialogs.Warning(this, I18n.T("verify.stillMissing"));
            _busy = false; UpdateButtons();
        }

        async Task AddExclusion()
        {
            if (!await Dialogs.Confirm(this, I18n.T("health.defenderConfirm", Paths.Root, Settings.Current.DocumentRoot))) return;
            if (HealthCheck.AddDefenderExclusion() == 0) await Dialogs.Info(this, I18n.T("health.defenderDone"));
        }

        async Task ShowHistory()
        {
            _busy = true; UpdateButtons();
            _summary.Text = I18n.T("verify.historyReading");
            var list = await Task.Run(() => Antivirus.DefenderHistory(Paths.Root));
            var ci = await Task.Run(() => Antivirus.CodeIntegrityBlocks(TimeSpan.FromDays(7)));
            var sb = new StringBuilder();
            if (list.Count == 0 && ci.Count == 0) sb.AppendLine(I18n.T("verify.historyNone"));
            foreach (var d in list) sb.AppendLine(d.Time + "  " + (d.Threat ?? "") + "  " + d.Path);
            if (ci.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(I18n.T("verify.ciBlocked"));
                foreach (var f in ci) sb.AppendLine("  " + f);
            }
            _out.Text = sb.ToString();
            _summary.Text = list.Count + ci.Count == 0 ? I18n.T("verify.historyNone") : I18n.T("verify.historyFound", list.Count + ci.Count);
            _summary.Foreground = list.Count + ci.Count == 0 ? Ui.Green : Ui.Orange;
            _busy = false; UpdateButtons();
        }

        async Task Scan()
        {
            if (!await Dialogs.Confirm(this, I18n.T("verify.scanConfirm", Paths.Root))) return;
            _busy = true; UpdateButtons();
            _summary.Text = I18n.T("verify.scanning", Paths.Root);
            _summary.Foreground = Ui.Text;
            var r = await Task.Run(() => Antivirus.ScanFolder(Paths.Root));
            _out.Text = (r.Output ?? "").Trim();
            _summary.Text = r.ExitCode == 0 ? I18n.T("verify.scanClean") : r.ExitCode == 2 ? I18n.T("verify.scanThreats") : I18n.T("verify.scanFailed", r.ExitCode);
            _summary.Foreground = r.ExitCode == 0 ? Ui.Green : Ui.Red;
            _busy = false; UpdateButtons();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (_busy) { e.Cancel = true; if (_cts != null) _cts.Cancel(); return; }
            base.OnClosing(e);
        }
    }
}
