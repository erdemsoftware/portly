using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Portly.Core;
using Portly.Desktop;
using Portly.Desktop.Forms;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;

namespace Portly.UiTests
{
    static partial class Program
    {
        /// <summary>A package row of the packages dialog: the border that carries the row (its Tag), found by the texts in it.</summary>
        static Border PackageRow(Window dlg, string name, string version)
        {
            return All<Border>(dlg).FirstOrDefault(b => b.Tag != null && b.Tag.GetType().Name == "Line" && TextOf(b).Contains(name) && TextOf(b).Contains(version));
        }

        /// <summary>Removes Mailpit and installs it again, both through the packages dialog; the services page follows.</summary>
        static void Flow_Packages()
        {
            if (!_webReady) { Skip("packages dialog install", "packages not installed"); return; }
            var mail = ServiceManager.Mailpit;
            var entry = Catalog.Recommended("mailpit") ?? Catalog.ForType("mailpit").First();
            var folderDir = Path.Combine(Paths.BinOf("mailpit"), entry.Folder);
            GoTo("services");
            Check("Mailpit card: installed, no Install button", mail.IsInstalled && CardShows(mail, "svc.stopped"));

            Click(NeedButton(Win, I18n.T("main.packages")));
            var dlg = WaitDialog<PackagesForm>(20);
            Check("Packages: opens", dlg != null);
            if (dlg == null) return;
            var row = PackageRow(dlg, "Mailpit", entry.Version);
            Check("Packages: the Mailpit row is listed as installed", row != null && TextOf(row).Contains(I18n.T("pkg.installedActive")), row == null ? "no row" : TextOf(row));
            if (row == null) return;

            // remove: select the row, Remove, answer Yes
            Click(row);
            Shot("packages-selected", dlg);
            Click(NeedButton(dlg, I18n.T("pkg.remove")));
            var confirm = WaitMessage(15);
            Check("Packages: Remove asks first", confirm != null && TextOf(confirm).Contains("Mailpit"), confirm == null ? "no dialog" : TextOf(confirm));
            if (confirm == null) return;
            Click(NeedButton(confirm, I18n.T("common.yes")));
            bool removed = WaitFor(() => !Directory.Exists(folderDir) && PackageTypes.Installed("mailpit").Count == 0, 60);
            Pump(300);
            Shot("packages-removed", dlg);
            Check("Packages: Mailpit is removed", removed, Directory.Exists(folderDir) ? "folder still there" : "still listed");

            // install again through the dialog: tick the row, Install selected
            row = PackageRow(dlg, "Mailpit", entry.Version);
            Check("Packages: the row is there again, not installed", row != null && !TextOf(row).Contains(I18n.T("pkg.installed")), row == null ? "no row" : TextOf(row));
            if (row == null) return;
            var tick = All<CheckBox>(row).First();
            Click(tick);
            Check("Packages: the row is ticked", tick.IsChecked == true);
            var install = NeedButton(dlg, I18n.T("pkg.installSelected"));
            Check("Packages: Install selected is enabled", install.IsEffectivelyEnabled);
            Click(install);
            bool installed = WaitFor(() => PackageTypes.Installed("mailpit").Contains(entry.Folder) && !All<ProgressBar>(dlg).Any(p => p.IsVisible), 600);
            Pump(500);
            Shot("packages-installed", dlg);
            Check("Packages: Mailpit " + entry.Version + " is installed from the dialog", installed, TextOf(dlg).Length > 200 ? "" : TextOf(dlg));
            var closeBtn = FindButton(dlg, I18n.T("common.close"));
            if (closeBtn != null && closeBtn.IsEffectivelyEnabled) Click(closeBtn); else CloseDialog(dlg);
            Check("Packages: closes", WaitFor(() => !OpenWindows().Contains(dlg), 20, false));
            Pump(500);

            // the services page follows: Mailpit is startable again
            Shot("services-after-install");
            Check("services: the Mailpit card is installed again", mail.IsInstalled && CardShows(mail, "svc.stopped"), PillOf(mail));
            var toggle = FindToggle(Win, mail.Title);
            if (toggle == null || !toggle.IsEffectivelyVisible) { Check("services: the Mailpit switch is back", false); return; }
            Click(toggle);
            bool on = WaitFor(() => mail.State == ServiceState.Running && CardShows(mail, "svc.running"), 60);
            Check("services: the reinstalled Mailpit starts from its switch", on, mail.State + " " + mail.LastError);
            if (on)
            {
                var info = FetchRetryUrl("http://127.0.0.1:18025/api/v1/info", 20);
                Check("services: the reinstalled Mailpit answers", !info.StartsWith("["), info);
                Click(FindToggle(Win, mail.Title));
                Check("services: and stops again", WaitFor(() => mail.State == ServiceState.Stopped, 60));
            }
        }
    }
}
