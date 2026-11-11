using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Portly.Core;

namespace Portly.Desktop
{
    public class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            var pref = Settings.Current.Theme;
            RequestedThemeVariant = pref == "dark" ? ThemeVariant.Dark : pref == "light" ? ThemeVariant.Light : ThemeVariant.Default;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop != null) desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }
}
