using System.Collections;
using System.IO;
using Avalonia.Controls;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Services;

namespace Portly.Desktop.Forms
{
    /// <summary>Main window pieces of the Tomcat feature: card menu items and the quick access tile.</summary>
    public static class TomcatUi
    {
        /// <summary>"Open Tomcat page" and "Open webapps folder" (a WAR file dropped there is deployed). Adds MenuItem controls.</summary>
        public static void AddItems(IList items)
        {
            if (!TomcatService.Enabled) return;
            var page = new MenuItem { Header = I18n.T("tomcat.openPage"), IsEnabled = ServiceManager.Tomcat.IsInstalled };
            page.Click += (s, e) => Ui.Open(TomcatService.Url);
            items.Add(page);
            var apps = new MenuItem { Header = I18n.T("tomcat.openWebapps"), IsEnabled = ServiceManager.Tomcat.IsInstalled };
            apps.Click += (s, e) => OpenWebapps();
            items.Add(apps);
        }

        public static void OpenWebapps()
        {
            var dir = TomcatService.WebappsDir;
            if (dir == null) return;
            Directory.CreateDirectory(dir);
            Ui.Open(dir);
        }

        /// <summary>Log file for the card's "Open log" item.</summary>
        public static string LogFile { get { return Path.Combine(TomcatService.LogDir, "console.log"); } }

        /// <summary>Restart Tomcat after a Tomcat or Java version switch.</summary>
        public static bool NeedsRestart(string type) { return (type == "tomcat" || type == "java") && ServiceManager.Tomcat.IsActive; }
    }
}
