using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
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
        FileSystemWatcher _watcher;
        DispatcherTimer _debounce, _backupTimer, _recheckTimer;

        // =====================================================================
        // Start-up
        // =====================================================================
        async Task RunStartup()
        {
            try { await Startup(); }
            catch (Exception ex) { Log.Error("Startup", ex); }
        }

        async Task Startup()
        {
            Log.Info(I18n.T("log.welcome", AppInfo.VersionLabel, AppInfo.EditionName, OsInfo.DisplayName));
            await Task.Run(() => ServiceManager.RefreshSites());
            WatchDocRoot();
            if (OS.IsWindows)
            {
                Terminal.ApplyUserPath();
                if (ServiceMode.IsController) ServiceMode.StartPolling(); // states come from the Portly Windows service
                await Task.Run(() => ShareFirewall.Prepare(ShareStore.All().Where(x => x.Lan && x.LanAuto && x.LanPort > 0).Select(x => x.LanPort).ToList()));
            }
            StartAutoShares();

            var s = Settings.Current;
            if (OS.IsWindows && !s.OsWarningShown)
            {
                s.OsWarningShown = true;
                s.Save();
                if (!AppInfo.IsLegacy && !OsInfo.IsWin10OrLater) await Dialogs.Warning(this, I18n.T("os.useLegacy", OsInfo.DisplayName));
                if (AppInfo.IsLegacy && OsInfo.IsWin10OrLater) await Dialogs.Info(this, I18n.T("os.useModern"));
            }
            if (Paths.HasSpaceInRoot) Log.Warn(I18n.T("warn.spaceInPath"));
            if (OS.IsWindows && RunningFromTemp() && await Dialogs.Confirm(this, I18n.T("warn.runningFromTemp")))
            {
                if (MoveToCPortly()) return;
            }

            bool nothingInstalled = PackageTypes.All.All(t => PackageTypes.Installed(t.Id).Count == 0);
            bool startFromWelcome = false;
            if (s.ShowWelcome && !_startHidden)
                startFromWelcome = await ShowWelcome(nothingInstalled);
            else if (nothingInstalled && !s.FirstRunDone)
                await OpenPackages(null, true);
            s.FirstRunDone = true;
            s.Save();
            if (!nothingInstalled) await CheckVcRuntime(true);

            // automatic database backup (off by default): checked every 10 minutes
            _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _backupTimer.Tick += (o, e) => Task.Run(() =>
            {
                try { Database.AutoBackupIfDue(); } catch (Exception ex) { Log.Warn("Auto backup: " + ex.Message); }
            });
            _backupTimer.Start();

            await ShowHealth(false);
            await QuickIntegrityCheck();
            var checks = BackgroundChecks(false, false);
            // a Portly that stays in the tray for days: look again every 3 hours (the checks themselves are rate limited)
            _recheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(3) };
            _recheckTimer.Tick += async (o, e) => { try { await BackgroundChecks(false, false); } catch (Exception ex) { Log.Warn(ex.Message); } };
            _recheckTimer.Start();
            if (_startServices || s.StartServicesOnLaunch || startFromWelcome) await ToggleAll();
            await checks;
        }

        /// <summary>Started from inside a zip (Explorer extracts to %TEMP%): everything installed would be lost.</summary>
        static bool RunningFromTemp()
        {
            var temp = Path.GetTempPath().TrimEnd('\\', '/');
            return Paths.AppDir.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Windows only: copy the program files to C:\Portly and start from there. False when it failed.</summary>
        bool MoveToCPortly()
        {
            var target = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Portly");
            try
            {
                Directory.CreateDirectory(target);
                foreach (var f in Directory.GetFiles(Paths.AppDir))
                    File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
                foreach (var d in new[] { "lang", "templates", "runtimes" })
                {
                    var from = Path.Combine(Paths.AppDir, d);
                    if (!Directory.Exists(from)) continue;
                    foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(target, f.Substring(Paths.AppDir.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(f, dest, true);
                    }
                }
                Process.Start(new ProcessStartInfo(Path.Combine(target, Path.GetFileName(Paths.Exe))) { WorkingDirectory = target });
                _stopped = true;
                ExitApp();
                return true;
            }
            catch (Exception ex)
            {
                var _ = Dialogs.Warning(this, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Local network shares marked "start when Portly starts": listening right away on their own port (until the web
        /// server runs, the device gets a "start the web server" page).
        /// </summary>
        void StartAutoShares()
        {
            foreach (var e in ShareStore.All().Where(x => x.Lan && x.LanAuto))
            {
                var site = ServiceManager.CurrentSites.FirstOrDefault(x => x.Valid && string.Equals(x.Host, e.Host, StringComparison.OrdinalIgnoreCase));
                if (site == null) continue;
                try { LanShare.Start(site, true); }
                catch (Exception ex) { Log.Warn(I18n.T("lan.autoFailed", e.Host, ex.Message)); }
            }
        }

        // =====================================================================
        // Updates
        // =====================================================================
        /// <summary>shared: "update available" / notices in the sidebar.</summary>
        public void UpdateBanner()
        {
            if (_updateBtn == null) return;
            bool appUpdate = Updater.Available != null;
            if (_updateTop != null)
            {
                if (appUpdate) SetButton(_updateTop, I18n.T("update.short", Updater.Available.Version), "update");
                _updateTop.IsVisible = appUpdate;
            }
            if (appUpdate)
            {
                SetButton(_updateBtn, I18n.T("update.short", Updater.Available.Version), "update");
                _updateBtn.Classes.Set("primary", true);
                _updateBtn.IsVisible = true;
                return;
            }
            if (_notices != null) _notices.Reload();
            var pending = AppInfo.UpdatesSupported ? Catalog.PendingUpdates().Count : 0;
            SetButton(_updateBtn, I18n.T("update.packages", pending), "update");
            _updateBtn.Classes.Set("primary", false);
            _updateBtn.IsVisible = pending > 0;
        }

        /// <summary>The update button (sidebar and top of the services page): install the new Portly, or open the packages.</summary>
        async Task OnBannerClick()
        {
            var u = Updater.Available;
            if (u == null) { await OpenPackages(null); return; }
            var msg = I18n.T("update.confirm", u.Version, AppInfo.VersionText) + (string.IsNullOrEmpty(u.Notes) ? "" : "\n\n" + u.Notes);
            if (!await Dialogs.Confirm(this, msg)) return;
            // .app, AppImage, tar.gz, .deb or the Windows zip; when this install cannot update itself, say why and open the release page
            var plan = Updater.Plan(u);
            if (!plan.CanInstall)
            {
                await Dialogs.Warning(this, plan.Reason ?? I18n.TOr("update.noAsset", "This release has no download for {0}. Get it from the releases page.", OS.Name + "-" + OS.Arch));
                Ui.Open(AppInfo.GitHubUrl + "/releases/latest");
                return;
            }
            _updateBtn.IsEnabled = false;
            try
            {
                await Updater.InstallUpdateAsync(u, p => Ui.Post(() => SetStatus(I18n.T("update.downloading", p.Percent < 0 ? Ui.FormatBytes(p.Bytes) : p.Percent + "%"))), CancellationToken.None);
                bool wasRunning = ServiceManager.AnyActive;
                await ServiceManager.StopAllAsync();
                Updater.Restart(wasRunning);
                _stopped = true;
                ExitApp();
            }
            catch (Exception ex)
            {
                _updateBtn.IsEnabled = true;
                await Dialogs.Warning(this, I18n.T("update.failed", ex.Message));
            }
        }

        async Task BackgroundChecks(bool force, bool interactive)
        {
            await Certs.RefreshBundleAsync();
            if (!AppInfo.UpdatesSupported) return;
            await Donate.RefreshAsync();
            var s = Settings.Current;
            // the app itself every 12 hours; new package versions at the first start of a day
            bool appDue = force, pkgDue = force;
            if (!force && s.CheckUpdates)
            {
                DateTime last;
                appDue = !(DateTime.TryParse(s.LastUpdateCheck, out last) && last > DateTime.Now.AddHours(-12));
                pkgDue = UpdateNotices.DailyCheckDue;
            }
            if (!appDue && !pkgDue) { Ui.Post(UpdateBanner); return; }
            try
            {
                await Catalog.RefreshAsync();
                if (interactive && Catalog.TrustError != null) await Dialogs.Warning(this, Catalog.TrustError);
                UpdateNotices.MarkChecked();
                var u = appDue ? await Updater.CheckAsync() : Updater.Available;
                UpdateBanner();
                if (interactive && u == null && UpdateNotices.Current.Count == 0) await Dialogs.Info(this, I18n.T("update.none", AppInfo.VersionLabel));
                if (interactive && UpdateNotices.Current.Count > 0) { ShowFromTray(); ShowPage("services"); }
                if (u != null && !IsVisible) Alert(I18n.T("update.banner", u.Version));
            }
            catch (Exception ex)
            {
                Log.Warn(I18n.T("update.checkFailed", ex.Message));
                if (interactive) await Dialogs.Warning(this, I18n.T("update.checkFailed", ex.Message));
            }
        }

        // =====================================================================
        // Document root watcher
        // =====================================================================
        void WatchDocRoot()
        {
            if (Program.IsScreenshot) return;
            try
            {
                if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
                var root = Settings.Current.DocumentRoot;
                Directory.CreateDirectory(root);
                // two levels deep (group folders), but only folder changes and project marker files count:
                // saving a file inside a project never restarts the web server
                _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName, InternalBufferSize = 32768 };
                Action<string, bool> consider = (fullPath, isDir) =>
                {
                    var rel = fullPath.Length > root.Length ? fullPath.Substring(root.Length).Trim('\\', '/') : "";
                    int depth = rel.Split('\\', '/').Length;
                    var file = Path.GetFileName(fullPath).ToLowerInvariant();
                    bool marker = file == "index.php" || file == "index.html" || file == "index.htm" || file == "composer.json" || file == "package.json" || file == "wp-config.php" || file == "public";
                    if ((isDir && depth <= 2) || (marker && depth <= 3))
                        Ui.Post(() => { _debounce.Stop(); _debounce.Start(); });
                };
                FileSystemEventHandler h = (s, e) => consider(e.FullPath, Directory.Exists(e.FullPath) || e.ChangeType == WatcherChangeTypes.Deleted && !Path.HasExtension(e.FullPath));
                _watcher.Created += h;
                _watcher.Deleted += h;
                _watcher.Renamed += (s, e) => { consider(e.FullPath, Directory.Exists(e.FullPath)); consider(e.OldFullPath, Directory.Exists(e.FullPath)); };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Warn("Watcher: " + ex.Message); }
        }

        static string SitesKey(IEnumerable<Site> sites)
        {
            return string.Join("|", sites.Select(x => x.Host + ">" + x.DocRoot + ">" + x.Valid + ">" + x.IsGroup));
        }

        async Task OnDocRootChanged()
        {
            // nothing that matters for the web server changed (e.g. a file inside a project)
            var before = SitesKey(ServiceManager.CurrentSites);
            var now = await Task.Run(() => SitesKey(Sites.Scan()));
            if (before == now) return;
            if (Settings.Current.AutoVhosts && ServiceManager.Web.State == ServiceState.Running)
            {
                Log.Info(I18n.T("log.sitesChanged"));
                await ServiceManager.ReloadWebAsync();
            }
            else await Task.Run(() => ServiceManager.RefreshSites());
        }
    }
}
