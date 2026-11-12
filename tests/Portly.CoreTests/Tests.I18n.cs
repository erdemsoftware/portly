using System.IO;
using System.Linq;
using Portly.Core;

namespace Portly.CoreTests
{
    static partial class Program
    {
        static void Suite_I18nPlatform()
        {
            I18n.Load("tr");
            var theme = I18n.T("settings.themeSystem");
            Check("platform wording", OS.IsWindows ? theme == "Windows ile aynı" : theme == "Sistemle aynı", theme);
            Check("plain keys unchanged", I18n.T("svc.running") == "Çalışıyor");
            I18n.Load("en");
            if (!OS.IsWindows)
            {
                var shown = new[] { "settings.langAuto", "settings.runAtStartup", "settings.themeSystem", "sslfix.caOk", "db.passwordChanged" };
                Check("no Windows in macOS / Linux wording", shown.All(k => !I18n.T(k).Contains("Windows")), string.Join(" | ", shown.Select(k => I18n.T(k))));
            }
        }
    }
}
