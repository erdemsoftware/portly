using System.IO;
using System.Windows.Forms;
using Portly.Core;
using Portly.Packages;
using Portly.Services;

namespace Portly.UI
{
    /// <summary>Main window pieces of the Tomcat feature: card menu items and the quick access tile.</summary>
    public static class TomcatUi
    {
        /// <summary>"Open Tomcat page" and "Open webapps folder" (a WAR file dropped there is deployed).</summary>
        public static void AddItems(ToolStripItemCollection items)
        {
            if (!TomcatService.Enabled) return;
            var page = new ToolStripMenuItem(I18n.T("tomcat.openPage")) { Enabled = ServiceManager.Tomcat.IsInstalled };
            page.Click += (s, e) => WinUtil.Open(TomcatService.Url);
            items.Add(page);
            var apps = new ToolStripMenuItem(I18n.T("tomcat.openWebapps")) { Enabled = ServiceManager.Tomcat.IsInstalled };
            apps.Click += (s, e) => OpenWebapps();
            items.Add(apps);
        }

        public static void OpenWebapps()
        {
            var dir = TomcatService.WebappsDir;
            if (dir == null) return;
            Directory.CreateDirectory(dir);
            WinUtil.OpenFolder(dir);
        }

        /// <summary>Log file for the card's "Open log" item.</summary>
        public static string LogFile { get { return Path.Combine(TomcatService.LogDir, "console.log"); } }

        /// <summary>Restart Tomcat after a Tomcat or Java version switch.</summary>
        public static bool NeedsRestart(string type) { return (type == "tomcat" || type == "java") && ServiceManager.Tomcat.IsActive; }
    }
}
