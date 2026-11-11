using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.Forms;
using Portly.Desktop.UI;
using Portly.Packages;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop
{
    public partial class MainWindow
    {
        /// <summary>shared: open the site or "localhost" in the browser (https only when the CA is trusted).</summary>
        public void OpenLocalhost()
        {
            var s = Settings.Current;
            bool https = s.Ssl && Certs.IsCaTrusted();
            var port = https ? s.HttpsPort : s.HttpPort;
            var def = https ? 443 : 80;
            Ui.Open((https ? "https" : "http") + "://localhost" + (port == def ? "" : ":" + port) + "/");
        }

        public void OpenMailpit()
        {
            if (PackageTypes.ActiveDir(Settings.Current.MailTool) == null) { var _ = OpenPackages(Settings.Current.MailTool); return; }
            if (!ServiceManager.Mailpit.IsActive) SetStatus(I18n.T("main.mailNotRunning"));
            Ui.Open("http://127.0.0.1:" + Settings.Current.MailpitWebPort + "/");
        }

        /// <summary>
        /// Database tile: straight into phpMyAdmin (logged in when root has no password, else its login page);
        /// without phpMyAdmin the list of installed tools.
        /// </summary>
        public async Task OpenDatabaseQuick(Control anchor)
        {
            if (PackageTypes.ActiveDir("phpmyadmin") == null) { await OpenDatabaseTool(anchor); return; }
            ToolApps.RefreshPhpMyAdmin();   // the login mode follows the current root password
            Ui.Open(LocalUrl("/phpmyadmin/"));
        }

        /// <summary>shared: new project wizard (template "wordpress", "laravel" ...; parent for a sub-site).</summary>
        public async Task NewProject(string template = null, string parent = null)
        {
            ShowFromTray();
            var f = await NewProjectForm.Ask(this, template, parent);
            if (f == null) return;
            var created = f.CreatedName;
            var openAfter = f.OpenAfter;
            var _ = Task.Run(async () =>
            {
                await ServiceManager.ReloadWebAsync();
                if (openAfter && ServiceManager.Web.State == ServiceState.Running)
                {
                    var site = ServiceManager.CurrentSites.FirstOrDefault(x => x.Name == created && x.Valid);
                    if (site != null) Ui.Open(site.PreferredUrl);
                }
            });
        }

        public Task CreateDatabase(string suggested)
        {
            ShowFromTray();
            return DatabaseUi.NewDatabase(this, suggested);
        }

        public async Task BackupDatabases()
        {
            ShowFromTray();
            SetStatus(I18n.T("db.backingUp"));
            try
            {
                var file = await Task.Run(() => Database.BackupAll());
                await Dialogs.Info(this, I18n.T("db.backedUp", file));
            }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
        }

        public Task ChangeRootPassword()
        {
            ShowFromTray();
            return DatabaseUi.RootPassword(this);
        }

        public async Task TrustCa()
        {
            ShowFromTray();
            if (Certs.TrustCa()) await Dialogs.Info(this, I18n.T("ssl.trustedDone"));
        }

        /// <summary>After "Start all": offer to trust the Portly CA once (the services part calls this when the web server runs).</summary>
        async Task AskTrustCa()
        {
            if (!Settings.Current.Ssl || Certs.IsCaTrusted()) return;
            var ini = Settings.Current.Raw;
            if (ini.GetBool("general", "CaTrustAsked", false)) return;
            ini.Set("general", "CaTrustAsked", true);
            Settings.Current.Save();
            if (await Dialogs.Confirm(this, I18n.T("ssl.ask"))) await TrustCa();
        }

        public async Task OpenSettings()
        {
            ShowFromTray();
            var docRoot = Settings.Current.DocumentRoot;
            var r = await SettingsForm.Open(this);
            if (!r.Saved) return;
            if (docRoot != Settings.Current.DocumentRoot) WatchDocRoot();
            RebuildTheme();
            var imported = r.Imported;
            if (imported != null)
            {
                var msg = I18n.T("transfer.imported");
                if (imported.DocumentRootKept) msg += "\n\n" + I18n.T("transfer.docRootKept", Settings.Current.DocumentRoot);
                if (imported.Manual.Count > 0) msg += "\n\n" + I18n.T("transfer.manual", string.Join(", ", imported.Manual));
                if (imported.Missing.Count > 0) msg += "\n\n" + I18n.T("transfer.missing", imported.Missing.Count, I18n.T("pkg.installSelected"));
                await Dialogs.Info(this, msg);
                if (imported.Missing.Count > 0) await OpenPackages(null, false, imported.Missing);
            }
            await r.Applying;
            if (imported != null) await ServiceManager.ReloadWebAsync();
        }

        /// <summary>shared: package manager; type preselects a package type, firstRun shows the welcome set.</summary>
        public async Task OpenPackages(string type, bool firstRun = false, IEnumerable<PackageEntry> preselect = null)
        {
            ShowFromTray();
            await PackagesForm.Show(this, type, firstRun, preselect);
            RefreshVersions();
            UpdateBanner();
            await CheckVcRuntime(false);
            var _ = Task.Run(() => ServiceManager.RefreshSites());
        }

        /// <summary>shared: Windows only - Apache, PHP and MySQL need the Microsoft Visual C++ runtime.</summary>
        public async Task CheckVcRuntime(bool quiet)
        {
            if (!OS.IsWindows) return;
            int need = 0;
            foreach (var t in PackageTypes.All)
            {
                var f = PackageTypes.ActiveFolder(t.Id);
                if (f == "") continue;
                var e = Catalog.FindByFolder(t.Id, f);
                if (e != null) need = Math.Max(need, e.Vc);
                else if (f.Contains("VS18")) need = Math.Max(need, 50);
            }
            if (need == 0) return;
            var have = WinUtil.VcRuntimeMinor();
            if (have >= need) return;
            var ini = Settings.Current.Raw;
            if (quiet && ini.Get("general", "VcAskedFor", "") == need.ToString()) return;
            ini.Set("general", "VcAskedFor", need.ToString());
            Settings.Current.Save();
            if (!await Dialogs.Confirm(this, I18n.T("vc.ask"))) return;
            var file = Path.Combine(Paths.Downloads, "vc_redist.x64.exe");
            SetStatus(I18n.T("vc.downloading"));
            var _ = Task.Run(async () =>
            {
                try
                {
                    await Net.DownloadFileAsync(Catalog.VcRedistUrl, file, null, CancellationToken.None);
                    var code = WinUtil.RunElevated(file, "/install /passive /norestart");
                    Log.Info(I18n.T("vc.done", code));
                }
                catch (Exception ex) { Log.Error("VC++", ex); }
            });
        }

        /// <summary>Start-up: are the .exe/.dll files of the active versions still there? (existence + size only, fast)</summary>
        async Task QuickIntegrityCheck()
        {
            List<IntegrityProblem> problems;
            try { problems = await Task.Run(() => Integrity.QuickCheckActive()); } catch { return; }
            if (problems.Count == 0) return;
            foreach (var p in problems) Log.Warn(I18n.T("verify.problem", p.Count, p.Title));
            if (IsVisible) await ShowVerify(problems);
            else SetStatus(I18n.T("verify.problem", problems[0].Count, problems[0].Title));
        }

        /// <summary>shared: integrity check window; problems = null checks every active version.</summary>
        public async Task ShowVerify(List<IntegrityProblem> problems)
        {
            ShowFromTray();
            var repaired = await VerifyPackagesForm.ShowAsync(this, problems);
            if (repaired)
            {
                RefreshVersions();
                var _ = Task.Run(() => ServiceManager.RefreshSites());
            }
        }

        /// <summary>shared: Laragon / XAMPP import. interactive = say so when nothing was found.</summary>
        public Task OpenImport(bool interactive) { return OpenImport(interactive, null); }

        async Task OpenImport(bool interactive, List<ImportSource> known)
        {
            var sources = known ?? await Task.Run(() => Importer.Detect());
            if (sources.Count == 0)
            {
                if (interactive) await Dialogs.Info(this, I18n.T("import.none"));
                return;
            }
            if (!interactive && !await Dialogs.Confirm(this, I18n.T("import.offer", string.Join(", ", sources.Select(x => x.Name))))) return;
            ShowFromTray();
            if (await ImportForm.Ask(this, sources))
            {
                WatchDocRoot();
                await ServiceManager.ReloadWebAsync();
                RebuildTheme();
            }
        }

        public Task ShowDonate()
        {
            ShowFromTray();
            return DonateForm.Show(this);
        }

        /// <summary>Environment problems (OneDrive, non-ASCII paths, long paths, other PHP ...). Each is shown once unless asked.</summary>
        async Task ShowHealth(bool all)
        {
            List<HealthIssue> issues;
            try { issues = await Task.Run(() => HealthCheck.Run()); } catch { return; }
            foreach (var i in issues) Log.Warn(i.Text);
            var ini = Settings.Current.Raw;
            var shown = ini.Get("general", "HealthShown", "").Split('|').ToList();
            var show = all ? issues : issues.Where(i => !shown.Contains(i.Key)).ToList();
            if (show.Count == 0)
            {
                if (all) await Dialogs.Info(this, I18n.T("health.ok"));
                return;
            }
            if (!IsVisible) return;
            await HealthForm.Show(this, show);
            ini.Set("general", "HealthShown", string.Join("|", shown.Concat(show.Select(i => i.Key)).Distinct().Where(k => k != "")));
            Settings.Current.Save();
        }

        /// <summary>Start screen. Returns true when the user chose "Start all".</summary>
        async Task<bool> ShowWelcome(bool nothingInstalled)
        {
            var f = await WelcomeForm.Show(this);
            switch (f.Action)
            {
                case WelcomeAction.Packages: await OpenPackages(null, nothingInstalled); break;
                case WelcomeAction.Import: await OpenImport(true, f.Sources); break;
                case WelcomeAction.StartAll: return true;
            }
            return false;
        }
    }
}
