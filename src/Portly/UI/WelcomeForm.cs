using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Core;
using Portly.Packages;
using Portly.Tools;

namespace Portly.UI
{
    public enum WelcomeAction { None, Packages, Import, StartAll }

    /// <summary>
    /// Start screen: shown on every start until "Don't show again" is ticked.
    /// 1. install packages  2. import from another stack  3. start.
    /// </summary>
    public class WelcomeForm : Form
    {
        public WelcomeAction Action { get; private set; }
        public List<ImportSource> Sources { get; private set; }
        readonly CheckBox _dontShow;
        readonly Label _importText;
        readonly ModernButton _importBtn;

        public WelcomeForm()
        {
            Ui.Apply(this);
            Text = "Portly";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(24);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.P(0, 0, 0, 6) };
            head.Controls.Add(new PictureBox { Image = Ui.AppImage(48), Size = Ui.S(48, 48), SizeMode = PictureBoxSizeMode.Zoom, Margin = Ui.P(0, 0, 14, 0) });
            var names = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            names.Controls.Add(Ui.Label(I18n.T("welcome.title"), Ui.Title));
            var sub = Ui.Label(I18n.T("welcome.subtitle"), Ui.Font, Ui.Muted);
            sub.MaximumSize = new Size(Ui.S(470), 0);
            names.Controls.Add(sub);
            head.Controls.Add(names);
            t.Controls.Add(head);

            // 1. packages
            var installed = PackageTypes.All.Where(x => PackageTypes.Installed(x.Id).Count > 0).Select(x => x.Name).ToList();
            var pkText = installed.Count == 0 ? I18n.T("welcome.packagesNone") : I18n.T("welcome.packagesSome", string.Join(", ", installed));
            t.Controls.Add(Step("1", I18n.T("welcome.packagesTitle"), pkText, installed.Count == 0,
                Ui.Button(installed.Count == 0 ? I18n.T("welcome.installRecommended") : I18n.T("main.packages"), (s, e) => Done(WelcomeAction.Packages), installed.Count == 0, "box")));

            // 2. import (detected in the background)
            _importBtn = Ui.Button(I18n.T("import.start"), (s, e) => Done(WelcomeAction.Import), false, "import");
            _importBtn.Enabled = false;
            var importCard = Step("2", I18n.T("welcome.importTitle"), I18n.T("welcome.importSearching"), false, _importBtn);
            _importText = (Label)importCard.Tag;
            t.Controls.Add(importCard);

            // 3. start
            var root = Settings.Current.DocumentRoot;
            t.Controls.Add(Step("3", I18n.T("welcome.startTitle"), I18n.T("welcome.startText", root, Settings.Current.Tld), installed.Count > 0,
                Ui.Button(I18n.T("main.startAll"), (s, e) => Done(WelcomeAction.StartAll), installed.Count > 0, "play")));

            var bottom = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = Ui.P(0, 14, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            _dontShow = new CheckBox { Text = I18n.T("welcome.dontShow"), AutoSize = true };
            left.Controls.Add(_dontShow);
            var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.P(0, 6, 0, 0) };
            links.Controls.Add(Ui.Link(I18n.T("welcome.guide"), () => WinUtil.Open(AppInfo.GitHubUrl + "/blob/main/docs/04-kullanim.md")));
            links.Controls.Add(Ui.Link(I18n.T("main.donate"), () => WinUtil.Open(AppInfo.DonateUrl)));
            left.Controls.Add(links);
            bottom.Controls.Add(left, 0, 0);
            var close = Ui.Button(I18n.T("common.close"), (s, e) => Done(WelcomeAction.None));
            close.Margin = Padding.Empty;
            bottom.Controls.Add(close, 1, 0);
            t.Controls.Add(bottom);
            Controls.Add(t);
            CancelButton = close;

            Shown += async (s, e) => await DetectSources();
        }

        /// <summary>A numbered step card. The description label is returned in Tag.</summary>
        static Card Step(string number, string title, string text, bool highlight, ModernButton action)
        {
            var card = new Card { Width = Ui.S(560), Height = Ui.S(92), Margin = Ui.P(0, 10, 0, 0), Radius = 12 };
            if (highlight) card.BorderColor = Color.FromArgb(160, Ui.Accent);
            var badge = new BrandTile { Text = number, Color = highlight ? Ui.Accent : Ui.Gray, Circle = true, Size = Ui.S(32, 32), Location = new Point(Ui.S(16), Ui.S(16)) };
            var tl = Ui.Label(title, Ui.Subtitle);
            tl.Location = new Point(Ui.S(60), Ui.S(14));
            var dl = new Label { Text = text, AutoSize = false, AutoEllipsis = true, ForeColor = Ui.Muted, BackColor = Color.Transparent, Location = new Point(Ui.S(61), Ui.S(40)), Size = new Size(Ui.S(330), Ui.S(44)) };
            action.AutoSize = false;
            action.Size = new Size(Ui.S(150), Ui.S(36));
            action.Location = new Point(card.Width - Ui.S(166), Ui.S(28));
            card.Controls.AddRange(new Control[] { badge, tl, dl, action });
            card.Tag = dl;
            return card;
        }

        async Task DetectSources()
        {
            try { Sources = await Task.Run(() => Importer.Detect()); }
            catch { Sources = new List<ImportSource>(); }
            if (IsDisposed) return;
            if (Sources.Count == 0) _importText.Text = I18n.T("welcome.importNone");
            else
            {
                _importText.Text = I18n.T("welcome.importFound", string.Join(", ", Sources.Select(x => x.Product).Distinct()));
                _importBtn.Enabled = true;
                _importBtn.Kind = ButtonKind.Primary;
            }
        }

        void Done(WelcomeAction action)
        {
            Action = action;
            if (_dontShow.Checked) { Settings.Current.ShowWelcome = false; Settings.Current.Save(); }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dontShow.Checked && Settings.Current.ShowWelcome) { Settings.Current.ShowWelcome = false; Settings.Current.Save(); }
            base.OnFormClosing(e);
        }
    }
}
