using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Portly.Config;
using Portly.Core;
using Portly.Desktop;
using Portly.Desktop.UI;
using Portly.Services;

namespace Portly.UiTests
{
    static partial class Program
    {
        static List<string> _childrenBefore = new List<string>();
        static readonly List<string> AllLog = new List<string>();

        static Service[] Running { get { return new Service[] { ServiceManager.Web, ServiceManager.Php, ServiceManager.MySql, ServiceManager.Mailpit }; } }

        static void GoTo(string page)
        {
            DismissPopups();
            var key = "nav." + page;
            var b = All<Button>(Win).FirstOrDefault(x => NameOf(x) == I18n.T(key) && x.Classes.Contains("nav"));
            if (b == null) throw new Exception("sidebar button " + key + " not found");
            Click(b);
        }

        static string StatusKey(Service svc)
        {
            switch (svc.State)
            {
                case ServiceState.Running: return "svc.running";
                case ServiceState.Starting: return "svc.starting";
                case ServiceState.Stopping: return "svc.stopping";
                case ServiceState.Error: return "svc.error";
                default: return svc.IsInstalled ? "svc.stopped" : "svc.notInstalled";
            }
        }

        static Border CardOf(Service svc)
        {
            var toggle = FindToggle(Win, svc.Title);
            if (toggle == null) return null;
            return toggle.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card"));
        }

        /// <summary>The status pill text a card shows right now ("Running", "Stopped" ...).</summary>
        static string PillOf(Service svc)
        {
            var card = CardOf(svc);
            if (card == null) return null;
            var states = new[] { "svc.running", "svc.starting", "svc.stopping", "svc.error", "svc.stopped", "svc.notInstalled" }.Select(k => I18n.T(k)).ToList();
            var pill = All<Pill>(card).FirstOrDefault(p => states.Contains(p.Text));
            return pill == null ? null : pill.Text;
        }

        static bool CardShows(Service svc, string key) { return PillOf(svc) == I18n.T(key); }

        static bool AllRunning() { return Running.All(s => s.State == ServiceState.Running); }

        static string StateText() { return string.Join(", ", Running.Select(s => s.Title + " " + s.State + (string.IsNullOrEmpty(s.LastError) ? "" : " (" + s.LastError + ")"))); }

        static string LogText()
        {
            var box = All<SelectableTextBlock>(Win).FirstOrDefault(t => t.Classes.Contains("mono"));
            return box == null ? null : string.Concat(box.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        }

        static int LogCount(string contains)
        {
            lock (AllLog) return AllLog.Count(l => l.Contains(contains));
        }

        // ------------------------------------------------------------------ Start all

        static void Flow_Start()
        {
            GoTo("services");
            Shot("services-before-start");
            if (!_webReady) { Skip("start all", "packages not installed"); return; }
            _childrenBefore = LeftoverProcesses();
            var startAll = NeedButton(Win, I18n.T("main.startAll"));
            Check("services: every installed card shows Stopped first", Running.All(s => CardShows(s, "svc.stopped")), string.Join(", ", Running.Select(s => s.Title + "=" + PillOf(s))));
            // Mailpit is not part of "Start all" until its padlock on the card is closed (the default is: web server and database only)
            var lockBtn = All<LockToggle>(Win).FirstOrDefault(l => NameOf(l) == I18n.T("svc.bulk") + " - " + ServiceManager.Mailpit.Title);
            Check("Mailpit card has its Start-all padlock", lockBtn != null);
            if (lockBtn != null)
            {
                bool was = Settings.Current.IsEnabled("mailpit");
                if (!was) Click(lockBtn);
                Check("Mailpit padlock: now part of Start all", Settings.Current.IsEnabled("mailpit"));
            }
            Click(startAll);
            bool ok = WaitFor(() => AllRunning() || Running.Any(s => s.State == ServiceState.Error), 300);
            Pump(300);
            Shot("services-started");
            Check("Start all: web server, PHP, MySQL and Mailpit run", AllRunning(), StateText());
            Check("Start all: the cards show the Running pill", Running.All(s => CardShows(s, "svc.running")), string.Join(", ", Running.Select(s => s.Title + "=" + PillOf(s))));
            Check("Start all: the button now says Stop all", FindButton(Win, I18n.T("main.stopAll")) != null);
            Check("Start all: the header counts running services", TextOf(Win).Contains(I18n.T("main.statusRunning", 3, 3)) || TextOf(Win).Contains(I18n.T("main.statusRunning", 4, 4)), TextOf(Win).Length > 0 ? "" : "no text");
            if (!AllRunning()) return;

            var body = FetchRetry("e2e.localhost", b => b.Contains(_phpVersion), 30);
            Check("e2e.localhost answers with PHP " + _phpVersion, body.Contains(_phpVersion) && _phpVersion != "", body.Length > 200 ? body.Substring(0, 200) : body);
        }

        // ------------------------------------------------------------------ single toggles and restart

        static void Flow_Services()
        {
            if (!AllRunning()) { Skip("services toggles", "services are not running"); return; }
            GoTo("services");
            var mail = ServiceManager.Mailpit;

            // Mailpit off with its own switch
            var toggle = FindToggle(Win, mail.Title);
            Check("Mailpit switch is on", toggle != null && toggle.IsChecked == true);
            Click(toggle);
            bool stopped = WaitFor(() => mail.State == ServiceState.Stopped && CardShows(mail, "svc.stopped"), 60);
            Shot("mailpit-off");
            Check("Mailpit switched off: stopped, pill says Stopped", stopped, mail.State + " / " + PillOf(mail));
            Check("Mailpit switched off: its port is free, the others still run", !ProcessUtil.IsPortInUse(18025) && ServiceManager.MySql.State == ServiceState.Running && ServiceManager.Web.State == ServiceState.Running);

            // and on again
            toggle = FindToggle(Win, mail.Title);
            Click(toggle);
            bool started = WaitFor(() => mail.State == ServiceState.Running && CardShows(mail, "svc.running"), 60);
            Check("Mailpit switched on: running, pill says Running", started, mail.State + " / " + PillOf(mail) + " " + mail.LastError);
            var info = FetchRetryUrl("http://127.0.0.1:18025/api/v1/info", 20);
            Check("Mailpit answers on its web port again", !info.StartsWith("["), info.Length > 100 ? info.Substring(0, 100) : info);

            // Restart on the database card
            var my = ServiceManager.MySql;
            var restart = NeedButton(Win, I18n.T("svc.restart") + " - " + my.Title);
            int stopsBefore = LogCount(I18n.T("log.stopped", "").Trim()), startsBefore = LogCount(I18n.T("log.started", "").Trim());
            Check("MySQL restart button is enabled while it runs", restart.IsEffectivelyEnabled);
            Click(restart);
            bool again = WaitFor(() => LogCount(I18n.T("log.started", "").Trim()) > startsBefore && my.State == ServiceState.Running && CardShows(my, "svc.running"), 180);
            Shot("mysql-restarted");
            Check("MySQL restarted from its card: stopped and started again", again && LogCount(I18n.T("log.stopped", "").Trim()) > stopsBefore, my.State + " " + my.LastError);
            // "running" comes before the root password check at the end of Start; the button returns when Start has finished
            Check("MySQL restart: the restart button is usable again", WaitFor(() => NeedButton(Win, I18n.T("svc.restart") + " - " + my.Title).IsEffectivelyEnabled, 30));
            var r = ProcessUtil.Run(Path.Combine(PackageTypes_ActiveDir("mysql"), "bin", OS.Exe("mysql")), "--protocol=TCP -h127.0.0.1 -P13306 -uroot -N -B -e \"select 1\"", null, null, 60000);
            Check("MySQL answers select 1 after the restart", r.Ok && r.Output.Split('\n').Any(l => l.Trim() == "1"), r.Output);
        }

        static string PackageTypes_ActiveDir(string type) { return Portly.Packages.PackageTypes.ActiveDir(type); }

        static string FetchRetryUrl(string url, int seconds)
        {
            var until = DateTime.Now.AddSeconds(seconds);
            string body = "";
            do
            {
                var r = System.Threading.Tasks.Task.Run(() => { System.Net.HttpStatusCode c; var b = Fetch(url, null, out c); return Tuple.Create(c, b); }).GetAwaiter().GetResult();
                if (r.Item1 == System.Net.HttpStatusCode.OK) return r.Item2;
                body = "[" + (int)r.Item1 + "] " + r.Item2;
                Pump(400);
            } while (DateTime.Now < until);
            return body;
        }

        // ------------------------------------------------------------------ navigation

        static bool PageTitleShown(string key)
        {
            return All<TextBlock>(Win).Any(t => t.Classes.Contains("title") && t.Text == I18n.T(key) && t.IsEffectivelyVisible);
        }

        static void Flow_Navigation()
        {
            GoTo("sites");
            Shot("page-projects");
            Check("sidebar Projects: the projects page is shown", PageTitleShown("nav.sites") && !PageTitleShown("nav.services"));
            Check("sidebar Projects: the button is marked selected", All<Button>(Win).Any(b => NameOf(b) == I18n.T("nav.sites") && b.Classes.Contains("selected")));
            GoTo("log");
            Shot("page-log");
            Check("sidebar Log: the log page is shown", PageTitleShown("nav.log") && !PageTitleShown("nav.sites"));
            if (_webReady)
            {
                Pump(200);
                var log = LogText() ?? "";
                var tail = I18n.T("log.started", "\u0001");
                tail = tail.Substring(tail.IndexOf('\u0001') + 1);
                int lines = log.Split('\n').Count(l => l.EndsWith(tail));
                Check("log page: shows the service start lines", lines >= 2, lines + " lines ending with \"" + tail + "\"; log has " + log.Length + " characters");
            }
            GoTo("services");
            Shot("page-services");
            Check("sidebar Services: the services page is shown", PageTitleShown("nav.services") && !PageTitleShown("nav.log"));
        }

        // ------------------------------------------------------------------ Stop all

        static void Flow_Stop()
        {
            if (!_webReady) { Skip("stop all", "packages not installed"); return; }
            GoTo("services");
            var stopAll = NeedButton(Win, I18n.T("main.stopAll"));
            Click(stopAll);
            bool ok = WaitFor(() => Running.All(s => s.State == ServiceState.Stopped) && FindButton(Win, I18n.T("main.startAll")) != null, 120);
            Pump(300);
            Shot("services-stopped");
            Check("Stop all: every service is stopped", Running.All(s => s.State == ServiceState.Stopped), StateText());
            Check("Stop all: the cards show the Stopped pill", Running.All(s => CardShows(s, "svc.stopped")), string.Join(", ", Running.Select(s => s.Title + "=" + PillOf(s))));
            Check("Stop all: the button says Start all again", FindButton(Win, I18n.T("main.startAll")) != null);
            List<string> left = null;
            for (int i = 0; i < 40; i++)
            {
                left = LeftoverProcesses().Where(x => !_childrenBefore.Contains(x)).ToList();
                if (left.Count == 0) break;
                Pump(250);
            }
            Check("Stop all: no child process left", left.Count == 0, string.Join(", ", left));
            Check("Stop all: test ports are free again", !new[] { 18080, 18443, 13306, 18025, 11025, 19100 }.Any(ProcessUtil.IsPortInUse));
        }
    }
}
