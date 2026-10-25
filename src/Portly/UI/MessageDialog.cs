using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Portly.Core;

namespace Portly.UI
{
    public enum MessageKind { Info, Warning, Question }

    /// <summary>
    /// Portly's own message window (instead of the plain Windows message box): theme colors, a round icon,
    /// the first paragraph as the heading, "- " lines as bullets, clickable addresses and Portly buttons.
    /// </summary>
    public class MessageDialog : Form
    {
        static readonly Regex UrlRx = new Regex(@"https?://[^\s""'<>]+[^\s""'<>.,;:!?)]", RegexOptions.IgnoreCase);
        readonly string _text;

        MessageDialog(string text, MessageKind kind, bool question)
        {
            _text = text ?? "";
            Ui.Apply(this);
            Text = "Portly";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(22, 20, 22, 16);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 3, Dock = DockStyle.Fill };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var icon = new KindIcon(kind) { Size = Ui.S(40, 40), Margin = Ui.P(0, 0, 16, 0) };
            t.Controls.Add(icon, 0, 0);
            t.SetRowSpan(icon, 2);

            // first paragraph = heading, the rest = body
            var parts = _text.Replace("\r\n", "\n").Split(new[] { "\n\n" }, 2, StringSplitOptions.None);
            var heading = BodyLabel(parts[0].Trim(), !question);
            heading.Font = Ui.Subtitle;
            heading.Margin = Ui.P(0, 2, 0, 0);
            t.Controls.Add(heading, 1, 0);
            if (parts.Length > 1 && parts[1].Trim() != "")
            {
                var body = BodyLabel(parts[1].Trim(), !question); // a question shows example addresses: no links
                body.Margin = Ui.P(0, 10, 0, 0);
                t.Controls.Add(body, 1, 1);
            }

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 20, 0, 0) };
            if (question)
            {
                var no = Ui.Button(I18n.T("common.no"), null);
                no.DialogResult = DialogResult.No;
                no.Margin = Padding.Empty;
                no.MinimumSize = Ui.S(88, 0);
                var yes = Ui.Button(I18n.T("common.yes"), null, true);
                yes.DialogResult = DialogResult.Yes;
                yes.Margin = Ui.P(0, 0, 8, 0);
                yes.MinimumSize = Ui.S(88, 0);
                buttons.Controls.Add(no);
                buttons.Controls.Add(yes);
                CancelButton = no;
                // like before: Enter answers "No" until the user picks "Yes" (safe for delete questions)
                Shown += (s, e) => no.Focus();
            }
            else
            {
                var ok = Ui.Button(I18n.T("common.ok"), null, true);
                ok.DialogResult = DialogResult.OK;
                ok.Margin = Padding.Empty;
                ok.MinimumSize = Ui.S(88, 0);
                buttons.Controls.Add(ok);
                AcceptButton = ok;
                CancelButton = ok;
            }
            t.Controls.Add(buttons, 0, 2);
            t.SetColumnSpan(buttons, 2);
            Controls.Add(t);

            // Ctrl+C copies the message, like the Windows message box
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C && !(ActiveControl is TextBoxBase)) { try { Clipboard.SetText(_text); } catch { } e.Handled = true; }
            };
        }

        /// <summary>The body text: "- " lines become bullets, addresses become links.</summary>
        static Control BodyLabel(string text, bool links)
        {
            text = string.Join("\n", text.Split('\n').Select(l => l.StartsWith("- ") ? "•  " + l.Substring(2) : l));
            var link = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(Ui.S(440), 0),
                Font = Ui.Font,
                ForeColor = Ui.Text,
                LinkColor = Ui.Accent,
                ActiveLinkColor = Ui.AccentDark,
                VisitedLinkColor = Ui.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = Color.Transparent,
                UseMnemonic = false,
            };
            link.Links.Clear();
            if (links) foreach (Match m in UrlRx.Matches(text)) link.Links.Add(m.Index, m.Length, m.Value);
            link.LinkClicked += (s, e) => { var u = e.Link.LinkData as string; if (u != null) WinUtil.Open(u); };
            return link;
        }

        static DialogResult Show(IWin32Window owner, string text, MessageKind kind, bool question)
        {
            // called from a background task: show it on the owner's UI thread (the Windows message box allowed any thread)
            var ctl = owner as Control;
            if (ctl != null && ctl.InvokeRequired && !ctl.IsDisposed)
                return (DialogResult)ctl.Invoke((Func<DialogResult>)(() => Show(owner, text, kind, question)));
            using (var d = new MessageDialog(text, kind, question))
            {
                var ownerForm = owner as Form;
                if (ownerForm != null && ownerForm.Visible && ownerForm.WindowState != FormWindowState.Minimized)
                {
                    d.StartPosition = FormStartPosition.CenterParent;
                    return d.ShowDialog(owner);
                }
                // Portly is in the tray: on top of the other windows, in the middle of the screen
                d.StartPosition = FormStartPosition.CenterScreen;
                d.TopMost = true;
                return d.ShowDialog();
            }
        }

        public static void Info(IWin32Window owner, string text) { Show(owner, text, MessageKind.Info, false); }
        public static void Warning(IWin32Window owner, string text) { Show(owner, text, MessageKind.Warning, false); }
        public static bool Confirm(IWin32Window owner, string text) { return Show(owner, text, MessageKind.Question, true) == DialogResult.Yes; }

        /// <summary>Round colored badge with "i", "!" or "?".</summary>
        class KindIcon : Control
        {
            readonly MessageKind _kind;
            public KindIcon(MessageKind kind)
            {
                _kind = kind;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var c = _kind == MessageKind.Warning ? Ui.Orange : Ui.Accent;
                var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (var soft = new SolidBrush(Color.FromArgb(40, c))) g.FillEllipse(soft, r);
                var inner = RectangleF.Inflate(r, -Width * 0.14f, -Height * 0.14f);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, inner);
                var mark = _kind == MessageKind.Warning ? "!" : _kind == MessageKind.Question ? "?" : "i";
                var f = Ui.ScaledFont("Segoe UI Semibold", 12f); // cached by Ui: never disposed here
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(mark, f, Brushes.White, inner, sf);
            }
        }
    }
}
