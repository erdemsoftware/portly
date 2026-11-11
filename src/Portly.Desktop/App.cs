using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Portly.Core;
using Portly.Desktop.UI;

namespace Portly.Desktop
{
    public class App : Application
    {
        public override void Initialize()
        {
            Name = "Portly";   // macOS menu bar and dock
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://Portly/")) { Source = new Uri("avares://Portly/UI/Styles.axaml") });
            Theme.Apply(this, Settings.Current.Theme);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop != null)
            {
                // the tray keeps Portly alive without a window; Exit (tray menu, closing without "keep running") ends it
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var main = new MainWindow(Program.StartHidden, Program.StartServices);
                if (!Program.StartHidden) desktop.MainWindow = main;
                // quit from the dock menu / logout (macOS), Shutdown(): the services stop before the windows close
                desktop.ShutdownRequested += (s, e) => main.StopOnShutdown();
                desktop.Exit += (s, e) => main.StopOnShutdown();
                Dispatcher.UIThread.UnhandledException += (s, e) =>
                {
                    Log.Error("Unexpected error", e.Exception);
                    e.Handled = true;
                    Ui.Post(async () =>
                    {
                        try { await Dialogs.Warning(main, I18n.T("err.unexpected") + "\n\n" + e.Exception.Message); } catch { }
                    });
                };
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
