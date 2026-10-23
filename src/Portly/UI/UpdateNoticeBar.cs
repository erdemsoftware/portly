using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Core;
using Portly.Packages;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>
    /// "PHP 8.6.0 is out — Install and use / Later / Skip this version" cards above the service list (modern edition).
    /// New branches get one card each; patches of installed branches are grouped ("Update all (3)").
    /// No dialog at start-up: the cards just appear when there is news.
    /// </summary>
    public class UpdateNoticeBar : TableLayoutPanel
    {
        /// <summary>Reload / restart what uses the type after its active version changed (MainForm.ApplyVersionChange).</summary>
        public Func<string, Task> ApplyVersionChange;
        public Action<string> OpenPackages;
        /// <summary>Versions changed: refresh the service cards.</summary>
        public Action Changed;
        bool _busy;

        public UpdateNoticeBar()
        {
            Dock = DockStyle.Top;
            AutoSize = true;
            ColumnCount = 1;
            BackColor = Color.Transparent;
            Padding = Ui.P(0, 0, 0, 4);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Visible = false;
        }

        /// <summary>Recompute the notices from the local catalog and redraw.</summary>
        public void Reload()
        {
            if (_busy) return;
            UpdateNotices.Recompute();
            Build();
        }

        void Build()
        {
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
            Controls.Clear();
            RowStyles.Clear();
            RowCount = 0;
            var notices = UpdateNotices.Current;
            foreach (var n in notices.Where(x => x.NewBranch))
            {
                var notice = n;
                Label text = null;
                var main = Ui.Button(I18n.T(n.InstallOnly ? "notice.install" : "notice.installUse"), null, true, "update");
                main.Click += async (s, e) => await Run(new[] { notice }, text);
                text = AddCard(I18n.T("notice.newBranch", n.TypeName, n.Entry.Version), main,
                    Ui.Button(I18n.T("notice.later"), (s, e) => Hide(new[] { notice }, false)),
                    Ui.Button(I18n.T("notice.skip"), (s, e) => Hide(new[] { notice }, true)));
            }
            var patches = notices.Where(x => !x.NewBranch).ToList();
            if (patches.Count > 0)
            {
                Label text = null;
                var main = Ui.Button(patches.Count == 1 ? I18n.T("notice.update") : I18n.T("notice.updateAll", patches.Count), null, true, "update");
                main.Click += async (s, e) => await Run(patches, text);
                var msg = patches.Count == 1
                    ? I18n.T("notice.patch", patches[0].TypeName, PackageTypes.Label(patches[0].OldFolder), patches[0].Entry.Version)
                    : I18n.T("notice.patches", string.Join(", ", patches.Select(p => p.TypeName + " " + p.Entry.Version)));
                text = AddCard(msg, main,
                    Ui.Button(I18n.T("notice.later"), (s, e) => Hide(patches, false)),
                    Ui.Button(I18n.T(patches.Count == 1 ? "notice.skip" : "notice.skipAll"), (s, e) => Hide(patches, true)));
            }
            Visible = Controls.Count > 0;
            ResumeLayout(true);
            Ui.StyleTree(this);
        }

        Label AddCard(string message, params ModernButton[] buttons)
        {
            var card = new Card { Height = Ui.S(58), Dock = DockStyle.Fill, Margin = Ui.P(0, 0, 0, 8), Radius = 12, BorderColor = Color.FromArgb(140, Theme.Current.Accent) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Padding = Ui.P(0, 11, 8, 0) };
            foreach (var b in buttons) { b.Margin = Ui.P(6, 0, 0, 0); flow.Controls.Add(b); }
            var text = new Label
            {
                Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Font = Ui.Bold, ForeColor = Ui.Text,
                BackColor = Color.Transparent, Padding = Ui.P(16, 0, 8, 0), Text = message,
            };
            new ToolTip().SetToolTip(text, message);
            card.Controls.Add(text);
            card.Controls.Add(flow);
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(card, 0, RowCount++);
            return text;
        }

        void Hide(IEnumerable<UpdateNotice> list, bool skip)
        {
            foreach (var n in list)
            {
                if (skip) Settings.Current.Skip(n.Type, n.Entry.Version);
                else Settings.LaterThisSession.Add(n.Key);
            }
            if (skip) Settings.Current.Save();
            Reload();
        }

        /// <summary>Download, install and (unless it is a database branch) switch to the new version.</summary>
        async Task Run(IList<UpdateNotice> list, Label text)
        {
            if (_busy) return;
            _busy = true;
            foreach (Control c in Controls) foreach (var b in c.Controls.OfType<FlowLayoutPanel>().SelectMany(f => f.Controls.OfType<ModernButton>())) b.Enabled = false;
            var oldFolders = new List<UpdateNotice>();
            var errors = new List<string>();
            int i = 0;
            foreach (var n in list)
            {
                i++;
                var label = n.TypeName + " " + n.Entry.Version;
                Action<InstallProgress> report = p => BeginInvokeSafe(() =>
                {
                    var pct = p.Stage == "download" ? " " + (p.Percent >= 0 ? p.Percent + "%" : Ui.FormatBytes(p.Bytes)) : "";
                    text.Text = (list.Count > 1 ? "[" + i + "/" + list.Count + "] " : "") + label + " - " + I18n.T("pkg.stage." + p.Stage) + pct;
                });
                try
                {
                    if (n.NewBranch)
                    {
                        await Installer.InstallAsync(n.Entry, report, CancellationToken.None);
                        if (!n.InstallOnly)
                        {
                            Settings.Current.SetVersion(n.Type, n.Entry.Folder);
                            Settings.Current.Save();
                            Log.Info(I18n.T("log.switched", n.TypeName, n.Entry.Version));
                            Terminal.ApplyUserPath();
                            if (ApplyVersionChange != null) await ApplyVersionChange(n.Type);
                        }
                    }
                    else
                    {
                        bool wasActive = string.Equals(PackageTypes.ActiveFolder(n.Type), n.OldFolder, StringComparison.OrdinalIgnoreCase);
                        await Installer.UpdateAsync(n.OldFolder, n.Entry, report, CancellationToken.None);
                        if (wasActive)
                        {
                            Terminal.ApplyUserPath();
                            if (ApplyVersionChange != null) await ApplyVersionChange(n.Type);
                        }
                        oldFolders.Add(n);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(label, ex);
                    errors.Add(label + ": " + ex.Message);
                }
            }
            _busy = false;
            if (Changed != null) Changed();
            var owner = FindForm();
            if (errors.Count > 0) Ui.Error(owner, I18n.T("notice.failed", string.Join("\n", errors)));
            var dbBranch = list.FirstOrDefault(n => n.NewBranch && n.InstallOnly && n.Entry.IsInstalled);
            if (dbBranch != null)
            {
                Ui.Info(owner, I18n.T("notice.dbInstalled", dbBranch.TypeName + " " + dbBranch.Entry.Version));
                if (OpenPackages != null) OpenPackages(dbBranch.Type);
            }
            OfferRemoveOld(owner, oldFolders);
            Reload();
        }

        static void OfferRemoveOld(IWin32Window owner, List<UpdateNotice> updated)
        {
            OfferRemoveOld(owner, updated.Where(n => n.Entry.IsInstalled).Select(n => Tuple.Create(n.Type, n.OldFolder)));
        }

        /// <summary>After a patch update: the previous build of the branch is usually no longer needed (type, old folder).</summary>
        public static void OfferRemoveOld(IWin32Window owner, IEnumerable<Tuple<string, string>> replaced)
        {
            var sitePhp = Cleanup.SitePhpFolders();
            var removable = replaced
                .Select(t => new { Type = t.Item1, OldFolder = t.Item2, Dir = Path.Combine(Paths.BinOf(t.Item1), t.Item2) })
                .Where(n => !string.Equals(PackageTypes.ActiveFolder(n.Type), n.OldFolder, StringComparison.OrdinalIgnoreCase))
                .Where(n => !(n.Type == "php" && sitePhp.Contains(n.OldFolder)))
                .Where(x => Directory.Exists(x.Dir) && !Cleanup.IsInUse(x.Dir))
                .ToList();
            if (removable.Count == 0) return;
            long size = removable.Sum(x => Cleanup.FolderSize(x.Dir));
            var names = string.Join(", ", removable.Select(x => PackageTypes.Get(x.Type).Name + " " + PackageTypes.Label(x.OldFolder)));
            if (!Ui.Confirm(owner, I18n.T("notice.removeOld", names, Ui.FormatBytes(size)))) return;
            foreach (var x in removable)
            {
                try { Installer.Remove(x.Type, x.OldFolder); }
                catch (Exception ex) { Log.Error(x.OldFolder, ex); }
            }
        }

        void BeginInvokeSafe(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }
    }
}
