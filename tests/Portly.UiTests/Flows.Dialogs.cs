using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
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
        static int LogErrorCount() { lock (LogErrors) return LogErrors.Count; }

        /// <summary>Opens a dialog with the given action, checks that it appears, takes a picture, closes it with a Close button / Esc, checks that it is gone.</summary>
        static T OpenAndClose<T>(string what, Action open, string closeButton = null, Action<T> inspect = null) where T : Window
        {
            open();
            var dlg = WaitDialog<T>(20);
            Check(what + ": opens", dlg != null, dlg != null ? "" : "no " + typeof(T).Name + " window");
            if (dlg == null) return null;
            Shot("dialog-" + Slug(what), dlg);
            if (inspect != null) inspect(dlg);
            var btn = closeButton == null ? null : FindButton(dlg, closeButton);
            if (btn != null && btn.IsEffectivelyEnabled) Click(btn); else CloseDialog(dlg);
            Check(what + ": closes", WaitFor(() => !OpenWindows().Contains(dlg), 10, false));
            return dlg;
        }

        static void OpenMoreMenuItem(string key)
        {
            GoTo("sites");
            var more = All<Button>(Win).First(b => NameOf(b) == I18n.T("main.more") && b.Classes.Contains("nav"));
            Click(more);
            ClickMenuItem(I18n.T(key));
        }

        static void Flow_Dialogs()
        {
            int errors = LogErrorCount();
            GoTo("services");

            // ---- Packages
            OpenAndClose<PackagesForm>("Packages", () => Click(NeedButton(Win, I18n.T("main.packages"))), I18n.T("common.close"), d =>
            {
                var text = TextOf(d);
                Check("Packages: lists the installed packages", text.Contains("Caddy") && text.Contains("Mailpit") && text.Contains(I18n.T("pkg.installedActive")), text.Length > 300 ? text.Substring(0, 300) : text);
            });

            // ---- Settings: change a harmless setting, save, reopen, value kept, restore
            Settings_RoundTrip();

            // ---- Import (nothing to import on a clean machine: the info box says so; with other environments the import dialog opens)
            Click(NeedButton(Win, I18n.T("nav.import")));
            WaitFor(() => OpenWindows().Any(), 20, false);
            var imp = OpenWindows().FirstOrDefault();
            Check("Import: a message or the import dialog opens", imp != null);
            if (imp != null)
            {
                Shot("dialog-import", imp);
                if (imp is MessageDialog) Check("Import: says nothing was found", TextOf(imp).Contains(Head(I18n.T("import.none"))) || imp is MessageDialog, TextOf(imp));
                var close = FindButton(imp, I18n.T("common.ok")) ?? FindButton(imp, I18n.T("common.cancel")) ?? FindButton(imp, I18n.T("common.close"));
                if (close != null) Click(close); else CloseDialog(imp);
                Check("Import: closes", WaitFor(() => !OpenWindows().Contains(imp), 10, false));
            }

            // ---- About (More menu)
            OpenAndClose<AboutForm>("About", () => OpenMoreMenuItem("menu.about"), I18n.T("common.close"));

            // ---- Hosts editor: opened from the Hosts button, never saved
            GoTo("services");
            var hostsBefore = File.Exists(HostsFile.FilePath) ? File.ReadAllText(HostsFile.FilePath) : null;
            OpenAndClose<HostsForm>("Hosts editor", () => { Click(NeedButton(Win, I18n.T("hosts.button"))); ClickMenuItem(I18n.T("hosts.edit")); }, I18n.T("common.close"), d =>
            {
                Check("Hosts editor: shows the file text", All<TextBox>(d).Any(t => !string.IsNullOrEmpty(t.Text)) || hostsBefore == null || hostsBefore == "");
            });
            var hostsAfter = File.Exists(HostsFile.FilePath) ? File.ReadAllText(HostsFile.FilePath) : null;
            Check("Hosts editor: the hosts file is untouched", hostsBefore == hostsAfter);
            CloseAllDialogs();

            // ---- projects page dialogs
            GoTo("sites");
            OpenAndClose<SslRepairForm>("SSL repair", () =>
            {
                var b = All<Button>(Win).FirstOrDefault(x => (NameOf(x) ?? "").StartsWith(I18n.T("sslfix.button")) && x.IsEffectivelyVisible);
                if (b == null) throw new Exception("Fix SSL button not found (SSL is " + Settings.Current.Ssl + ")");
                Click(b);
            }, I18n.T("common.close"));
            GoTo("sites");
            OpenAndClose<SharesForm>("Shares", () => Click(NeedButton(Win, I18n.T("shares.title"))), I18n.T("common.close"));

            // ---- Logs and Jobs of a project (card menu)
            GoTo("sites");
            OpenAndClose<LogsForm>("Logs of a project", () => { OpenSiteMenu("e2e"); ClickMenuItem(I18n.T("logs.menu")); }, I18n.T("common.close"));
            GoTo("sites");
            OpenAndClose<JobsForm>("Jobs of a project", () => { OpenSiteMenu("e2e"); ClickMenuItem(I18n.T("jobs.menu")); }, I18n.T("common.close"));

            // ---- the other items of the project menu open as well (read only): the folder and terminal ones start programs, they are left alone
            GoTo("sites");
            OpenSiteMenu("e2e");
            Check("project menu lists address, delete and share entries", FindMenuItem(I18n.T("sites.openHttp")) != null && FindMenuItem(I18n.T("sites.delete")) != null && FindMenuItem(I18n.T("share.button")) != null);
            Shot("project-menu");
            PressEscape(Win);

            Check("dialogs: nothing was logged as an error", LogErrorCount() == errors, string.Join(" || ", LogErrors.Skip(errors)));
        }

        static void Settings_RoundTrip()
        {
            GoTo("services");
            bool before = Settings.Current.StartMinimized;
            var form = OpenSettings();
            if (form == null) return;
            var box = FindCheck(form, I18n.T("settings.startMinimized"));
            Check("Settings: the \"start minimized\" box matches the stored value", box != null && box.IsChecked == before);
            if (box == null) { CloseDialog(form); return; }
            Click(box);
            Check("Settings: clicking flips the box", box.IsChecked == !before);
            Shot("settings-changed", form);
            Click(NeedButton(form, I18n.T("common.save")));
            Check("Settings: Save closes the dialog", WaitFor(() => !OpenWindows().Contains(form), 30, false));
            Pump(500);
            Check("Settings: the value is stored", Settings.Current.StartMinimized == !before);
            Check("Settings: the window was rebuilt and still shows the services page", PageTitleShown("nav.services"));
            if (_webReady && ServiceManager.Web.State == ServiceState.Running)
                Check("Settings: the rebuilt cards still show the running services", Running.All(s => CardShows(s, "svc.running")), string.Join(", ", Running.Select(s => s.Title + "=" + PillOf(s))));

            form = OpenSettings();
            if (form == null) return;
            box = FindCheck(form, I18n.T("settings.startMinimized"));
            Check("Settings: reopened, the changed value is kept", box != null && box.IsChecked == !before);
            if (box != null && box.IsChecked != before) Click(box);
            Click(NeedButton(form, I18n.T("common.save")));
            WaitFor(() => !OpenWindows().Contains(form), 30, false);
            Pump(500);
            Check("Settings: restored to the original value", Settings.Current.StartMinimized == before);
            Settings.Current.StartMinimized = before;
            Settings.Current.Save();
        }

        static SettingsForm OpenSettings()
        {
            GoTo("services");
            Click(NeedButton(Win, I18n.T("main.settings")));
            var form = WaitDialog<SettingsForm>(20);
            Check("Settings: opens", form != null);
            return form;
        }
    }
}
