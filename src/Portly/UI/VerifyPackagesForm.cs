using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Core;
using Portly.Packages;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>
    /// Tools > Verify packages (antivirus / quarantine): compares the installed files with the list written at
    /// install time, shows Windows Defender detections under the Portly folder, repairs and offers a Defender scan.
    /// </summary>
    public class VerifyPackagesForm : Form
    {
        readonly TextBox _out;
        readonly Label _summary;
        readonly ProgressBar _progress;
        readonly ModernButton _full, _repair, _exclude, _history, _scan, _close;
        List<IntegrityProblem> _problems = new List<IntegrityProblem>();
        CancellationTokenSource _cts;
        bool _busy;
        /// <summary>Something was repaired: the caller restarts / refreshes.</summary>
        public bool Repaired;

        public VerifyPackagesForm(List<IntegrityProblem> found = null)
        {
            Ui.Apply(this);
            Text = I18n.T("verify.title");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = Ui.S(720, 520);
            MinimumSize = Ui.S(600, 420);

            var top = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = Ui.P(16, 14, 16, 6), BackColor = Ui.Bg };
            _summary = new Label { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(Ui.S(670), 0), Font = Ui.Bold, Padding = Ui.P(0, 6, 0, 0) };
            var intro = new Label { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(Ui.S(670), 0), ForeColor = Ui.Muted, Text = I18n.T("verify.intro") };
            top.Controls.Add(_summary);
            top.Controls.Add(intro);

            _out = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Ui.Mono };
            var host = new Panel { Dock = DockStyle.Fill, Padding = Ui.P(16, 4, 16, 4), BackColor = Ui.Bg };
            host.Controls.Add(_out);

            var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = Ui.P(16, 6, 16, 12), BackColor = Ui.Bg };
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = Ui.S(6), Style = ProgressBarStyle.Marquee, Visible = false };
            var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = Ui.P(0, 8, 0, 0) };
            _full = Ui.Button(I18n.T("verify.full"), async (s, e) => await FullCheck(), true);
            _repair = Ui.Button(I18n.T("verify.repair"), async (s, e) => await Repair(), false, "update");
            _exclude = Ui.Button(I18n.T("verify.exclude"), (s, e) => AddExclusion());
            _history = Ui.Button(I18n.T("verify.history"), async (s, e) => await ShowHistory());
            _scan = Ui.Button(I18n.T("verify.scan"), async (s, e) => await Scan());
            _close = Ui.Button(I18n.T("common.close"), (s, e) => { if (_busy) { if (_cts != null) _cts.Cancel(); } else Close(); });
            row1.Controls.AddRange(new Control[] { _full, _repair, _exclude, _history, _scan, _close });
            _scan.Visible = Antivirus.MpCmdRun != null && OsInfo.IsWin10OrLater;
            _history.Visible = OsInfo.IsWin10OrLater;
            bottom.Controls.Add(row1);
            bottom.Controls.Add(_progress);

            Controls.Add(host);
            Controls.Add(bottom);
            Controls.Add(top);
            host.BringToFront();

            if (found != null) Show(found, -1, 0);
            else _summary.Text = I18n.T("verify.notRun");
            UpdateButtons();
        }

        void UpdateButtons()
        {
            _full.Enabled = _history.Enabled = _scan.Enabled = _exclude.Enabled = !_busy;
            _repair.Enabled = !_busy && _problems.Count > 0;
            _close.Text = _busy ? I18n.T("common.cancel") : I18n.T("common.close");
            _progress.Visible = _busy;
        }

        void Show(List<IntegrityProblem> problems, int checkedCount, int withoutList)
        {
            _problems = problems;
            var sb = new StringBuilder();
            if (problems.Count == 0)
                _summary.Text = checkedCount >= 0 ? I18n.T("verify.allOk", checkedCount) : I18n.T("verify.notRun");
            else
                _summary.Text = string.Join("\n", problems.Select(p => I18n.T("verify.problem", p.Count, p.Title)));
            _summary.ForeColor = problems.Count == 0 ? Ui.Green : Ui.Red;
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
                var list = await Task.Run(() => Integrity.FullCheck(t => BeginInvoke((Action)(() => _summary.Text = I18n.T("verify.checking", t))), token, out count, out without));
                Show(list, count, without);
            }
            catch (OperationCanceledException) { _summary.Text = I18n.T("pkg.cancelled"); }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
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
                    await Integrity.RepairAsync(p, ip => BeginInvoke((Action)(() =>
                        _summary.Text = p.Title + " - " + I18n.T("pkg.stage." + ip.Stage) + (ip.Percent >= 0 && ip.Stage == "download" ? " " + ip.Percent + "%" : ""))), _cts.Token);
                    Repaired = true;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { failed.Add(p.Title + ": " + ex.Message); Log.Error(p.Title, ex); }
            }
            // check again what is still wrong
            var still = await Task.Run(() => _problems.Select(p => Integrity.Check(p.Dir, false)).Where(p => p != null && p.Count > 0).ToList());
            Show(still, -1, 0);
            if (still.Count == 0) { _summary.Text = I18n.T("verify.repairedAll"); _summary.ForeColor = Ui.Green; }
            if (failed.Count > 0) Ui.Error(this, string.Join("\n", failed));
            else if (still.Count > 0) Ui.Error(this, I18n.T("verify.stillMissing"));
            _busy = false; UpdateButtons();
        }

        void AddExclusion()
        {
            if (!Ui.Confirm(this, I18n.T("health.defenderConfirm", Paths.Root, Settings.Current.DocumentRoot))) return;
            if (HealthCheck.AddDefenderExclusion() == 0) Ui.Info(this, I18n.T("health.defenderDone"));
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
            _summary.ForeColor = list.Count + ci.Count == 0 ? Ui.Green : Ui.Orange;
            _busy = false; UpdateButtons();
        }

        async Task Scan()
        {
            if (!Ui.Confirm(this, I18n.T("verify.scanConfirm", Paths.Root))) return;
            _busy = true; UpdateButtons();
            _summary.Text = I18n.T("verify.scanning", Paths.Root);
            _summary.ForeColor = Ui.Text;
            var r = await Task.Run(() => Antivirus.ScanFolder(Paths.Root));
            _out.Text = (r.Output ?? "").Trim();
            _summary.Text = r.ExitCode == 0 ? I18n.T("verify.scanClean") : r.ExitCode == 2 ? I18n.T("verify.scanThreats") : I18n.T("verify.scanFailed", r.ExitCode);
            _summary.ForeColor = r.ExitCode == 0 ? Ui.Green : Ui.Red;
            _busy = false; UpdateButtons();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy) { e.Cancel = true; if (_cts != null) _cts.Cancel(); return; }
            base.OnFormClosing(e);
        }
    }
}
