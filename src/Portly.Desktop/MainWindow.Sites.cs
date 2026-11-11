using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Portly.Core;
using Portly.Desktop.UI;

namespace Portly.Desktop
{
    public partial class MainWindow
    {
        TextBlock _sitesSub;

        Control BuildSitesPage()
        {
            var page = new StackPanel { Spacing = 14 };
            page.Children.Add(Ui.PageHeader(I18n.T("nav.sites"), out _sitesSub));
            return page;
        }

        /// <summary>shared: rebuild the project cards (sites changed, share started, web server restarted ...).</summary>
        public void FillSites() { }

        /// <summary>shared: HTTPS repair for sites whose certificate is not trusted.</summary>
        public Task OpenSslRepair() { return Task.CompletedTask; }

        /// <summary>shared: list of active shares.</summary>
        public Task OpenShares() { return Task.CompletedTask; }

        /// <summary>shared: hosts button with its menu (services and projects page headers).</summary>
        public Button HostsButton(bool withText = false) { return Ui.Button(withText ? I18n.T("hosts.title") : null, (Action)null, null, "hosts"); }
    }
}
