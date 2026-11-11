using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>
    /// Feature "projectFile": shows exactly what a portly.yml changes before anything happens.
    /// Commands (jobs, hooks) are listed apart and only applied with the "Allow commands from this file" tick.
    /// </summary>
    public class ProjectFileForm : PortlyDialog
    {
        public bool Applied { get; private set; }

        public ProjectFileForm(Site site) : base(I18n.T("pf.title", site.Name), 620)
        {
            var file = ProjectFile.Find(site);
            Body.Spacing = 4;
            Body.Children.Add(Ui.Selectable(file ?? "", "muted small"));

            ProjectSpec spec = null;
            string error = null;
            try { spec = ProjectFile.Load(file); }
            catch (YamlException ex) { error = I18n.T("pf.syntax", ex.Line, ex.Message); }
            catch (Exception ex) { error = ex.Message; }

            var close = Ui.Button(I18n.T("common.close"), () => Close());

            if (spec == null)
            {
                Body.Children.Add(Ui.Label(error, "error"));
                Buttons.Children.Add(close);
                return;
            }

            var steps = ProjectFile.Plan(site, spec);
            var changes = Ui.Label(I18n.T("pf.changes"), "bold");
            changes.Margin = new Thickness(0, 10, 0, 2);
            Body.Children.Add(changes);
            foreach (var st in steps.Where(x => !x.IsCommand))
                Body.Children.Add(Ui.Label((st.Warning ? "!  " : "•  ") + st.Text, null, st.Warning ? Ui.Orange : null));
            if (steps.All(x => x.IsCommand)) Body.Children.Add(Ui.Label(I18n.T("pf.noChanges"), "muted"));

            CheckBox allow = null;
            if (spec.HasCommands)
            {
                var cmds = Ui.Label(I18n.T("pf.commands"), "bold");
                cmds.Margin = new Thickness(0, 12, 0, 2);
                Body.Children.Add(cmds);
                foreach (var st in steps.Where(x => x.IsCommand)) Body.Children.Add(Ui.Selectable("›  " + st.Text, "mono"));
                Body.Children.Add(Ui.Label(I18n.T("pf.commandsWarn"), "muted small"));
                allow = new CheckBox { Content = I18n.T("pf.allowCommands"), Margin = new Thickness(0, 6, 0, 0) };
                Body.Children.Add(allow);
            }

            var result = Ui.Selectable("");
            Body.Children.Add(result);
            Button apply = null;
            apply = Ui.Button(I18n.T("pf.apply"), async () =>
            {
                apply.IsEnabled = false;
                close.IsEnabled = false;
                try
                {
                    var done = await ProjectFile.ApplyAsync(site, file, spec, allow != null && allow.IsChecked == true, msg => Ui.Post(() => result.Text = msg));
                    result.Text = string.Join("\n", done);
                    Applied = true;
                    await ServiceManager.ReloadWebAsync();
                }
                catch (Exception ex) { result.Text = ex.Message; apply.IsEnabled = true; }
                finally { close.IsEnabled = true; }
            }, "primary");
            Buttons.Children.Add(apply);
            Buttons.Children.Add(close);
        }

        public static Window Preview()
        {
            var dir = DevUi.Temp("projectfile");
            File.WriteAllText(Path.Combine(dir, "portly.yml"), """
version: 1
php: "8.3"
node: "22"
root: public
domains: [shop.portly, "*.api.shop.portly"]
services: [mysql, redis, mailpit]
database: { name: shop, collation: utf8mb4_unicode_ci }
jobs:
  - name: queue
    command: "php artisan queue:work --tries=3"
    autostart: true
  - name: schedule
    command: "php artisan schedule:run"
    schedule: true
hooks:
  after-start: "composer install"
""");
            var site = new Site { Name = "shop", Host = "shop.portly", Folder = dir, DocRoot = dir, Valid = true };
            return new ProjectFileForm(site);
        }
    }
}
