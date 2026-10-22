using System.Drawing;
using System.Windows.Forms;
using Portly.Core;

namespace Portly.UI
{
    public class AboutForm : Form
    {
        void ShowDonate() { using (var f = new DonateForm()) f.ShowDialog(this); }

        public AboutForm()
        {
            Ui.Apply(this);
            Text = I18n.T("about.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(22);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            head.Controls.Add(new PictureBox { Image = Ui.AppImage(48), Size = Ui.S(48, 48), SizeMode = PictureBoxSizeMode.Zoom, Margin = Ui.P(0, 0, 12, 0) });
            var names = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            names.Controls.Add(Ui.Label("Portly " + AppInfo.VersionText, Ui.Title));
            names.Controls.Add(Ui.Label(AppInfo.EditionName + "  ·  " + I18n.T("about.by"), Ui.Font, Ui.Muted));
            head.Controls.Add(names);
            t.Controls.Add(head);

            var desc = Ui.Label(I18n.T("about.description"));
            desc.MaximumSize = new Size(Ui.S(420), 0);
            desc.Margin = Ui.P(0, 14, 0, 6);
            t.Controls.Add(desc);
            if (AppInfo.IsLegacy)
            {
                var legacy = Ui.Label(I18n.T("about.legacy"), Ui.Small, Ui.Muted);
                legacy.MaximumSize = new Size(Ui.S(420), 0);
                t.Controls.Add(legacy);
            }

            var links = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Ui.S(440), 0), Margin = Ui.P(0, 8, 0, 8) };
            links.Controls.Add(Ui.Link("GitHub", () => WinUtil.Open(AppInfo.GitHubUrl)));
            links.Controls.Add(Ui.Link("erdemsoftware.com", () => WinUtil.Open(AppInfo.WebsiteUrl)));
            links.Controls.Add(Ui.Link(I18n.T("about.translate"), () => WinUtil.Open(AppInfo.TranslateUrl)));
            links.Controls.Add(Ui.Link(I18n.T("about.issue"), () => WinUtil.Open(AppInfo.IssuesUrl)));
            t.Controls.Add(links);

            var donateBox = new Card { AutoSize = true, BackColor = Color.FromArgb(Theme.IsDark ? 40 : 255, Theme.IsDark ? 24 : 241, Theme.IsDark ? 30 : 242), BorderColor = Color.FromArgb(90, Ui.Heart), Padding = Ui.P(14), Margin = Ui.P(0, 4, 0, 10), MaximumSize = new Size(Ui.S(440), 0) };
            var donateFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
            var dText = Ui.Label(I18n.T("about.donateText"));
            dText.MaximumSize = new Size(Ui.S(410), 0);
            donateFlow.Controls.Add(dText);
            var dBtn = Ui.Button(I18n.T("main.donate"), (s, e) => ShowDonate(), true, "heart");
            dBtn.CustomColor = Ui.Heart;
            dBtn.Margin = Ui.P(0, 8, 0, 0);
            donateFlow.Controls.Add(dBtn);
            donateBox.Controls.Add(donateFlow);
            t.Controls.Add(donateBox);

            var legal = Ui.Label(I18n.T("about.license") + "\n" + I18n.T("about.thirdParty"), Ui.Small, Ui.Muted);
            legal.MaximumSize = new Size(Ui.S(440), 0);
            t.Controls.Add(legal);

            var close = Ui.Button(I18n.T("common.close"), (s, e) => Close());
            close.Anchor = AnchorStyles.Right;
            close.Margin = Ui.P(0, 12, 0, 0);
            t.Controls.Add(close);
            CancelButton = close;
            Controls.Add(t);
        }
    }
}
