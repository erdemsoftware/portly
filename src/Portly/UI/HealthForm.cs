using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Portly.Core;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>System check results in one window: each problem on its own card, with its fix button when there is one.</summary>
    public class HealthForm : Form
    {
        public HealthForm(List<HealthIssue> issues)
        {
            Ui.Apply(this);
            Text = I18n.T("health.menu");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(22);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var title = Ui.Label(I18n.T("health.title"), Ui.Bold);
            title.MaximumSize = new Size(Ui.S(520), 0);
            title.Margin = Ui.P(0, 0, 0, 10);
            t.Controls.Add(title);

            foreach (var i in issues)
            {
                var issue = i;
                var card = new Card { Width = Ui.S(520), Margin = Ui.P(0, 0, 0, 8), Radius = 12 };
                var text = Ui.Label(issue.Text);
                text.MaximumSize = new Size(Ui.S(488), 0);
                text.Location = new Point(Ui.S(16), Ui.S(14));
                card.Controls.Add(text);
                int bottom = text.Location.Y + text.PreferredSize.Height;
                if (issue.Fix != null)
                {
                    ModernButton fix = null;
                    fix = Ui.Button(issue.FixLabel, (s, e) =>
                    {
                        try { if (issue.Fix() == 0) { fix.Text = "✓ " + issue.FixLabel; fix.Enabled = false; } }
                        catch (System.Exception ex) { Ui.Error(this, ex.Message); }
                    }, true);
                    fix.Location = new Point(Ui.S(16), bottom + Ui.S(10));
                    card.Controls.Add(fix);
                    bottom = fix.Location.Y + fix.PreferredSize.Height;
                }
                card.Height = bottom + Ui.S(14);
                t.Controls.Add(card);
            }

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 8, 0, 0) };
            var close = Ui.Button(I18n.T("common.close"), (s, e) => Close());
            close.Margin = Padding.Empty;
            buttons.Controls.Add(close);
            t.Controls.Add(buttons);
            Controls.Add(t);
            CancelButton = close;
        }
    }
}
