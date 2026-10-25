using System;
using System.Drawing;
using System.Windows.Forms;
using Portly.Core;

namespace Portly.UI
{
    /// <summary>Donation options: open a payment page, or copy a crypto address / IBAN.</summary>
    public class DonateForm : Form
    {
        public DonateForm()
        {
            Ui.Apply(this);
            Text = I18n.T("donate.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(22);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var heart = new PictureBox { Size = Ui.S(30, 30), Margin = Ui.P(0, 0, 10, 0) };
            heart.Paint += (s, e) => Glyphs.Draw(e.Graphics, "heart", new RectangleF(0, 0, heart.Width, heart.Height), Ui.Heart);
            head.Controls.Add(heart);
            head.Controls.Add(Ui.Label(I18n.T("donate.title"), Ui.Title));
            t.Controls.Add(head);
            var intro = Ui.Label(I18n.T("about.donateText"), Ui.Font, Ui.Muted);
            intro.MaximumSize = new Size(Ui.S(500), 0);
            intro.Margin = Ui.P(0, 6, 0, 10);
            t.Controls.Add(intro);

            var methods = Donate.Load();
            if (methods.Count == 0)
            {
                var none = Ui.Label(I18n.T("donate.none"));
                none.MaximumSize = new Size(Ui.S(500), 0);
                t.Controls.Add(none);
                t.Controls.Add(Ui.Link("GitHub", () => WinUtil.Open(AppInfo.GitHubUrl)));
            }
            // the list scrolls when there are many methods
            var list = new ScrollFlow { FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = Ui.S(530), Margin = Padding.Empty };
            int listHeight = 0;
            foreach (var m in methods)
            {
                var method = m;
                var card = new Card { Width = Ui.S(500), Height = Ui.S(method.Kind == "copy" ? 86 : 66), Margin = Ui.P(0, 0, 0, 8), Radius = 12 };
                var name = Ui.Label(method.Name, Ui.Bold);
                name.Location = new Point(Ui.S(16), Ui.S(12));
                var note = Ui.Label(method.Note, Ui.Small, Ui.Muted);
                note.Location = new Point(Ui.S(16), Ui.S(36));
                card.Controls.Add(name);
                card.Controls.Add(note);
                ModernButton action;
                if (method.Kind == "copy")
                {
                    var value = new TextBox { Text = method.Value, ReadOnly = true, Font = Ui.Mono, BorderStyle = BorderStyle.None, BackColor = Ui.Card, ForeColor = Ui.Text, Location = new Point(Ui.S(16), Ui.S(58)), Width = Ui.S(360) };
                    card.Controls.Add(value);
                    action = Ui.Button(I18n.T("donate.copy"), null, false, null);
                    action.Click += (s, e) =>
                    {
                        try { Clipboard.SetText(method.Value); action.Text = I18n.T("donate.copied"); } catch { }
                    };
                }
                else action = Ui.Button(I18n.T("donate.open"), (s, e) => WinUtil.Open(method.Value), true, "external");
                action.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                action.AutoSize = false;
                action.Size = new Size(Ui.S(110), Ui.S(34));
                action.Location = new Point(card.Width - Ui.S(126), Ui.S(16));
                card.Controls.Add(action);
                list.Controls.Add(card);
                listHeight += card.Height + card.Margin.Vertical;
            }
            if (methods.Count > 0)
            {
                list.Height = Math.Min(listHeight + Ui.S(4), Ui.S(470));
                t.Controls.Add(list);
            }

            var close = Ui.Button(I18n.T("common.close"), (s, e) => Close());
            close.Anchor = AnchorStyles.Right;
            close.Margin = Ui.P(0, 10, 0, 0);
            t.Controls.Add(close);
            CancelButton = close;
            Controls.Add(t);
        }
    }
}
