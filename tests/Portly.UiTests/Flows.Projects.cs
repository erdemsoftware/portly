using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Portly.Config;
using Portly.Core;
using Portly.Desktop;
using Portly.Desktop.Forms;
using Portly.Desktop.UI;
using Portly.Services;

namespace Portly.UiTests
{
    static partial class Program
    {
        /// <summary>The card of a project on the projects page (the border that holds its name).</summary>
        static Border SiteCardOf(string name)
        {
            return All<TextBlock>(Win).Where(t => t.Classes.Contains("subtitle") && t.Text == name)
                .Select(t => t.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card")))
                .FirstOrDefault(b => b != null);
        }

        static Button MoreButtonOf(Border card)
        {
            return All<Button>(card).FirstOrDefault(b => NameOf(b) == I18n.T("sites.more"));
        }

        static void OpenSiteMenu(string site)
        {
            var card = SiteCardOf(site);
            if (card == null) throw new Exception("project card \"" + site + "\" not found");
            Click(MoreButtonOf(card));
        }

        static void Flow_Projects()
        {
            GoTo("sites");
            var site = Path.Combine(Paths.Www, "uitest");
            if (Directory.Exists(site)) Directory.Delete(site, true);
            ServiceManager.RefreshSites();
            Pump(200);
            Check("projects: the e2e project has a card", SiteCardOf("e2e") != null);
            Shot("projects-before");

            // ---- New project dialog, driven field by field
            Click(NeedButton(Win, I18n.T("main.newProject")));
            var form = WaitDialog<NewProjectForm>();
            Check("New project dialog opens", form != null);
            if (form == null) return;
            Shot("newproject-open", form);
            var name = All<TextBox>(form).First();
            Type(name, "uitest");
            Check("New project: the address follows the name", TextOf(form).Contains("uitest.localhost"), TextOf(form));
            Click(NeedButton(form, I18n.T("project.blankShort")));
            var open = FindCheck(form, I18n.T("project.openAfter"));
            if (open != null && open.IsChecked == true) Click(open);   // no browser from the test
            Check("New project: \"open in the browser\" is off", open != null && open.IsChecked != true);
            Shot("newproject-filled", form);
            Click(NeedButton(form, I18n.T("project.create")));
            var done = WaitMessage(120);
            Check("New project: a result message is shown", done != null);
            string msgText = done == null ? "" : TextOf(done);
            if (done != null)
            {
                Check("New project: the message says it was created", msgText.Contains(Head(I18n.T("project.created", "uitest"))), msgText);
                if (_webReady && ServiceManager.MySql.State == ServiceState.Running)
                    Check("New project: the message says the database was created", msgText.Contains(ProjectCreatorDb("uitest")) && msgText.Split((char)10).Length >= 2, msgText);
                Shot("newproject-done", done);
                Click(NeedButton(done, I18n.T("common.ok")));
            }
            Check("New project: the dialog closes", WaitFor(() => OpenWindows().Count == 0, 20, false));
            Check("New project: the folder and index.php exist", File.Exists(Path.Combine(site, "index.php")));

            // ---- the card appears and the address answers
            bool card = WaitFor(() => SiteCardOf("uitest") != null && ServiceManager.CurrentSites.Any(x => x.Name == "uitest" && x.Valid), 90);
            Pump(300);
            Shot("projects-after-create");
            Check("projects: the new card appears", card);
            var cardCtl = SiteCardOf("uitest");
            Check("projects: the card shows the http address", cardCtl != null && TextOf(cardCtl).Contains("uitest.localhost"), cardCtl == null ? "" : TextOf(cardCtl));
            if (_webReady) WaitFor(() => ServiceManager.Web.State == ServiceState.Running && ServiceManager.Php.State == ServiceState.Running, 90);
            if (_webReady && ServiceManager.Web.State == ServiceState.Running)
            {
                var db = Portly.Packages.PackageTypes.ActiveDir("mysql");
                var q = ProcessUtil.Run(Path.Combine(db, "bin", OS.Exe("mysql")), "--protocol=TCP -h127.0.0.1 -P13306 -uroot -N -B -e \"show databases like 'uitest'\"", null, null, 60000);
                Check("New project: the database exists in MySQL", q.Ok && q.Output.Contains("uitest"), q.Output);
                var body = FetchRetry("uitest.localhost", b => b.Contains("uitest") && b.Contains(_phpVersion), 60);
                Check("projects: http://uitest.localhost answers with the page and PHP " + _phpVersion, body.Contains("uitest") && body.Contains(_phpVersion), body.Length > 200 ? body.Substring(0, 200) : body);
            }
            else Skip("projects: the new address answers", "web server not running");

            // ---- delete through the card menu
            OpenSiteMenu("uitest");
            Shot("projects-card-menu");
            ClickMenuItem(I18n.T("sites.delete"));
            var confirm = WaitMessage(15);
            Check("delete: a confirmation dialog asks first", confirm != null && TextOf(confirm).Contains("uitest"), confirm == null ? "no dialog" : TextOf(confirm));
            if (confirm == null) return;
            Shot("projects-delete-confirm", confirm);
            Click(NeedButton(confirm, I18n.T("common.yes")));
            // the folder goes to the trash (Recycle Bin / Finder / gio or the fallback trash folder); a warning means it could not
            bool gone = WaitFor(() => !Directory.Exists(site) && SiteCardOf("uitest") == null, 90);
            var warning = OpenWindows().OfType<MessageDialog>().FirstOrDefault();
            if (!gone && warning != null)
            {
                var wt = TextOf(warning);
                Shot("projects-delete-warning", warning);
                Click(NeedButton(warning, I18n.T("common.ok")));
                Check("delete: the folder was moved to the trash", false, wt);
                return;
            }
            Shot("projects-after-delete");
            Check("delete: the folder is gone and the card disappears", gone, Directory.Exists(site) ? "folder still there" : "card still shown");
            Check("delete: the project is no longer served", WaitFor(() => ServiceManager.CurrentSites.All(x => x.Name != "uitest"), 30));
        }

        static string ProjectCreatorDb(string name) { return Portly.Tools.ProjectCreator.DbNameFor(name); }
    }
}
