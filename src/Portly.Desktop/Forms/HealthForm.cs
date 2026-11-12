using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>System check results in one window: each problem on its own card, with its fix button when there is one.</summary>
    public class HealthForm : PortlyDialog
    {
        public static Task Show(Window owner, List<HealthIssue> issues) { return new HealthForm(issues).ShowDialog(owner); }

        public HealthForm(List<HealthIssue> issues) : base(I18n.T("health.menu"), 580)
        {
            Body.Children.Add(Ui.Label(I18n.T("health.title"), "bold"));
            foreach (var i in issues)
            {
                var issue = i;
                var card = Ui.Column(10, Ui.Selectable(issue.Text));
                if (issue.Fix != null)
                {
                    Button fix = null;
                    fix = Ui.Button(issue.FixLabel, async () =>
                    {
                        try
                        {
                            if (await Task.Run(() => issue.Fix()) == 0) { fix.Content = "✓ " + issue.FixLabel; fix.IsEnabled = false; }
                        }
                        catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
                    }, "primary");
                    fix.HorizontalAlignment = HorizontalAlignment.Left;
                    card.Children.Add(fix);
                }
                Body.Children.Add(Ui.Card(card, new Avalonia.Thickness(16, 14)));
            }
            Buttons.Children.Add(Ui.Button(I18n.T("common.close"), () => Close()));
        }

        public static Window Preview()
        {
            return new HealthForm(new List<HealthIssue>
            {
                new HealthIssue { Key = "onedrive", Text = I18n.T("health.onedrive", @"C:\Users\demo\OneDrive\Portly") },
                new HealthIssue { Key = "nonascii", Text = I18n.T("health.nonAscii", @"C:\Users\Müşteri") },
                new HealthIssue { Key = "phprc", Text = I18n.T("health.phprc", @"C:\php\php.ini"), FixLabel = "Remove PHPRC", Fix = () => 0 },
            });
        }
    }
}
