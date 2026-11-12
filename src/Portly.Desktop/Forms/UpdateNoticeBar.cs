using Th = Portly.Desktop.UI.Theme;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// <summary>
    /// "PHP 8.6.0 is out - Install and use / Later / Skip this version" cards above the service list.
    /// New branches get one card each; patches of installed branches are grouped ("Update all (3)").
    /// No dialog at start-up: the cards just appear when there is news.
    /// </summary>
    public class UpdateNoticeBar : StackPanel
    {
        /// <summary>Reload / restart what uses the type after its active version changed.</summary>
        public Func<string, Task> ApplyVersionChange;
        public Action<string> OpenPackages;
        /// <summary>Versions changed: refresh the service cards.</summary>
        public Action Changed;
        bool _busy;
        readonly List<Button> _buttons = new List<Button>();

        public UpdateNoticeBar()
        {
            Spacing = 8;
            Margin = new Thickness(0, 0, 0, 4);
            IsVisible = false;
        }

        /// <summary>Recompute the notices from the local catalog and redraw.</summary>
        public void Reload()
        {
            if (_busy) return;
            UpdateNotices.Recompute();
            Build(UpdateNotices.Current);
        }

        void Build(List<UpdateNotice> notices)
        {
            Children.Clear();
            _buttons.Clear();
            foreach (var n in notices.Where(x => x.NewBranch))
            {
                var notice = n;
                TextBlock text = null;
                var main = Ui.Button(I18n.T(n.InstallOnly ? "notice.install" : "notice.installUse"), async () => await Run(new[] { notice }, text), "primary", "update");
                text = AddCard(I18n.T("notice.newBranch", n.TypeName, n.Entry.Version), main,
                    Ui.Button(I18n.T("notice.later"), () => Dismiss(new[] { notice }, false)),
                    Ui.Button(I18n.T("notice.skip"), () => Dismiss(new[] { notice }, true)));
            }
            var patches = notices.Where(x => !x.NewBranch).ToList();
            if (patches.Count > 0)
            {
                TextBlock text = null;
                var main = Ui.Button(patches.Count == 1 ? I18n.T("notice.update") : I18n.T("notice.updateAll", patches.Count), async () => await Run(patches, text), "primary", "update");
                var msg = patches.Count == 1
                    ? I18n.T("notice.patch", patches[0].TypeName, PackageTypes.Label(patches[0].OldFolder), patches[0].Entry.Version)
                    : I18n.T("notice.patches", string.Join(", ", patches.Select(p => p.TypeName + " " + p.Entry.Version)));
                text = AddCard(msg, main,
                    Ui.Button(I18n.T("notice.later"), () => Dismiss(patches, false)),
                    Ui.Button(I18n.T(patches.Count == 1 ? "notice.skip" : "notice.skipAll"), () => Dismiss(patches, true)));
            }
            IsVisible = Children.Count > 0;
        }

        TextBlock AddCard(string message, params Button[] buttons)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            var text = Ui.Label(message, "bold");
            text.TextWrapping = TextWrapping.NoWrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(text, message);
            grid.Children.Add(text);
            var row = Ui.Row(buttons);
            row.Spacing = 6;
            row.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(row, 1);
            grid.Children.Add(row);
            _buttons.AddRange(buttons);
            var card = Ui.Card(grid, new Thickness(16, 10));
            card.BorderBrush = new SolidColorBrush(Th.Current.Accent, 0.55);
            Children.Add(card);
            return text;
        }

        sealed class PreviewDialog : PortlyDialog
        {
            public PreviewDialog() : base("Notices", 900)
            {
                var bar = new UpdateNoticeBar();
                bar.Build(new List<UpdateNotice>
                {
                    new UpdateNotice { Type = "php", NewBranch = true, Entry = new PackageEntry { Type = "php", Version = "8.6.0", Folder = "php-8.6.0" } },
                    new UpdateNotice { Type = "apache", NewBranch = false, OldFolder = "apache-2.4.68", Entry = new PackageEntry { Type = "apache", Version = "2.4.69", Folder = "apache-2.4.69" } },
                });
                Body.Children.Add(bar);
                Buttons.IsVisible = false;
            }
        }

        public static Window Preview() { return new PreviewDialog(); }

        void Dismiss(IEnumerable<UpdateNotice> list, bool skip)
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
        async Task Run(IList<UpdateNotice> list, TextBlock text)
        {
            if (_busy) return;
            _busy = true;
            foreach (var b in _buttons) b.IsEnabled = false;
            var oldFolders = new List<UpdateNotice>();
            var errors = new List<string>();
            int i = 0;
            foreach (var n in list)
            {
                i++;
                var label = n.TypeName + " " + n.Entry.Version;
                int index = i;
                Action<InstallProgress> report = p => Ui.Post(() =>
                {
                    var pct = p.Stage == "download" ? " " + (p.Percent >= 0 ? p.Percent + "%" : Ui.FormatBytes(p.Bytes)) : "";
                    text.Text = (list.Count > 1 ? "[" + index + "/" + list.Count + "] " : "") + label + " - " + I18n.T("pkg.stage." + p.Stage) + pct;
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
                            if (OS.IsWindows) Terminal.ApplyUserPath();
                            if (ApplyVersionChange != null) await ApplyVersionChange(n.Type);
                        }
                    }
                    else
                    {
                        bool wasActive = string.Equals(PackageTypes.ActiveFolder(n.Type), n.OldFolder, StringComparison.OrdinalIgnoreCase);
                        await Installer.UpdateAsync(n.OldFolder, n.Entry, report, CancellationToken.None);
                        if (wasActive)
                        {
                            if (OS.IsWindows) Terminal.ApplyUserPath();
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
            var owner = Ui.OwnerOf(this);
            if (errors.Count > 0) await Dialogs.Warning(owner, I18n.T("notice.failed", string.Join("\n", errors)));
            var dbBranch = list.FirstOrDefault(n => n.NewBranch && n.InstallOnly && n.Entry.IsInstalled);
            if (dbBranch != null)
            {
                await Dialogs.Info(owner, I18n.T("notice.dbInstalled", dbBranch.TypeName + " " + dbBranch.Entry.Version));
                if (OpenPackages != null) OpenPackages(dbBranch.Type);
            }
            await OfferRemoveOld(owner, oldFolders);
            Reload();
        }

        static Task OfferRemoveOld(Window owner, List<UpdateNotice> updated)
        {
            return OfferRemoveOld(owner, updated.Where(n => n.Entry.IsInstalled).Select(n => Tuple.Create(n.Type, n.OldFolder)));
        }

        /// <summary>After a patch update: the previous build of the branch is usually no longer needed (type, old folder).</summary>
        public static async Task OfferRemoveOld(Window owner, IEnumerable<Tuple<string, string>> replaced)
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
            if (!await Dialogs.Confirm(owner, I18n.T("notice.removeOld", names, Ui.FormatBytes(size)))) return;
            foreach (var x in removable)
            {
                try { Installer.Remove(x.Type, x.OldFolder); }
                catch (Exception ex) { Log.Error(x.OldFolder, ex); }
            }
        }
    }
}
