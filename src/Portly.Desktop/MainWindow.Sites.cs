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
    }
}
