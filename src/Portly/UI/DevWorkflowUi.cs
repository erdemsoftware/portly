using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>Site card badges and site menu items of the DevWorkflow features (keeps MainForm edits to two calls).</summary>
    public static class DevWorkflowUi
    {
        /// <summary>"2 jobs" and "portly.yml" pills for a site card.</summary>
        public static List<Control> SiteBadges(Site site, IWin32Window owner, Action refresh)
        {
            var list = new List<Control>();
            if (!site.Valid) return list;
            if (Features.IsOn("jobs"))
            {
                int n = Jobs.ActiveCount(site.Name);
                if (n > 0)
                {
                    var pill = new Pill { Text = I18n.T("jobs.badge", n), Color = Theme.Current.Green, Cursor = Cursors.Hand };
                    pill.Click += (s, e) => { using (var f = new JobsForm(site)) f.ShowDialog(owner); refresh(); };
                    new ToolTip().SetToolTip(pill, I18n.T("jobs.badgeTip", n));
                    list.Add(pill);
                }
            }
            if (ProjectFile.IsPending(site))
            {
                var pill = new Pill { Text = "portly.yml", Color = Theme.Current.Orange, Cursor = Cursors.Hand };
                pill.Click += (s, e) => ApplyProjectFile(site, owner, refresh);
                new ToolTip().SetToolTip(pill, I18n.T("pf.notice"));
                list.Add(pill);
            }
            return list;
        }

        static void ApplyProjectFile(Site site, IWin32Window owner, Action refresh)
        {
            using (var f = new ProjectFileForm(site)) f.ShowDialog(owner);
            refresh();
        }

        public static void AddSiteMenuItems(ToolStripItemCollection items, Site site, IWin32Window owner, Action refresh)
        {
            if (Features.IsOn("logs"))
                items.Add(I18n.T("logs.menu"), null, (s, e) => { using (var f = new LogsForm(site)) f.ShowDialog(owner); });
            if (Features.IsOn("jobs") && site.Valid)
                items.Add(I18n.T("jobs.menu"), null, (s, e) => { using (var f = new JobsForm(site)) f.ShowDialog(owner); refresh(); });
            if (Features.IsOn("dumps") && site.Valid)
                items.Add(I18n.T("profiler.menu"), null, (s, e) => { using (var f = new ProfilerForm(site)) f.ShowDialog(owner); });
            if (Features.IsOn("projectFile") && site.Valid)
            {
                if (ProjectFile.Find(site) != null)
                    items.Add(I18n.T("pf.menuApply"), null, (s, e) => ApplyProjectFile(site, owner, refresh));
                items.Add(I18n.T("pf.menuCreate"), null, (s, e) =>
                {
                    var existing = ProjectFile.Find(site);
                    if (existing != null && !Ui.Confirm(owner, I18n.T("pf.overwrite", existing))) return;
                    try
                    {
                        var file = ProjectFile.Write(site);
                        WinUtil.Edit(file);
                        refresh();
                    }
                    catch (Exception ex) { Ui.Error(owner, ex.Message); }
                });
            }
        }
    }
}
