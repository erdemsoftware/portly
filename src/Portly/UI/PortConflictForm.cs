using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Portly.Core;
using Portly.Services;

namespace Portly.UI
{
    public enum PortChoice { Cancel, UseFreePorts, CloseOwners }

    /// <summary>Shown when ports are taken: who holds them, and two ways out.</summary>
    public class PortConflictForm : Form
    {
        public PortChoice Choice { get; private set; }

        public PortConflictForm(List<PortConflict> conflicts)
        {
            Ui.Apply(this);
            Text = I18n.T("port.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(20);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.P(0, 0, 0, 6) };
            var icon = new PictureBox { Size = Ui.S(28, 28), Margin = Ui.P(0, 0, 10, 0) };
            icon.Paint += (s, e) => Glyphs.Draw(e.Graphics, "warning", new RectangleF(0, 0, icon.Width, icon.Height), Ui.Orange);
            head.Controls.Add(icon);
            head.Controls.Add(Ui.Label(I18n.T("port.title"), Ui.Subtitle));
            t.Controls.Add(head);
            var intro = Ui.Label(I18n.T("port.intro"), Ui.Font, Ui.Muted);
            intro.MaximumSize = new Size(Ui.S(520), 0);
            t.Controls.Add(intro);

            foreach (var c in conflicts)
            {
                var card = new Card { Width = Ui.S(520), Height = Ui.S(c.Owner.IsSystem ? 112 : 92), Margin = Ui.P(0, 8, 0, 0) };
                var title = Ui.Label(I18n.T("port.row", c.Service.Title + " · " + c.Label, c.Port), Ui.Bold);
                title.Location = new Point(Ui.S(14), Ui.S(10));
                var who = Ui.Label(I18n.T("port.usedBy", c.Owner.Pid == 0 ? I18n.T("port.unknownProc") : c.Owner.ToString()), Ui.Font, Ui.Text);
                who.Location = new Point(Ui.S(14), Ui.S(34));
                var path = Ui.Label(c.Owner.IsSystem ? I18n.T("port.system") : c.Owner.Path, Ui.Small, c.Owner.IsSystem ? Ui.Orange : Ui.Muted);
                path.AutoSize = false;
                path.AutoEllipsis = true;
                path.Location = new Point(Ui.S(14), Ui.S(54));
                path.Size = new Size(Ui.S(490), Ui.S(c.Owner.IsSystem ? 36 : 18));
                var sug = Ui.Label(c.Suggested > 0 ? I18n.T("port.suggest", c.Suggested) : I18n.T("port.noFree"), Ui.Small, Ui.Green);
                sug.Location = new Point(Ui.S(14), Ui.S(c.Owner.IsSystem ? 90 : 72));
                card.Controls.AddRange(new Control[] { title, who, path, sug });
                t.Controls.Add(card);
            }

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 16, 0, 0) };
            var cancel = Ui.Button(I18n.T("common.cancel"), (s, e) => { Choice = PortChoice.Cancel; Close(); });
            cancel.Margin = Padding.Empty;
            var free = Ui.Button(I18n.T("port.useFree"), (s, e) => { Choice = PortChoice.UseFreePorts; Close(); }, true);
            free.Enabled = conflicts.All(c => c.Suggested > 0);
            var kill = Ui.Button(I18n.T("port.closeApps"), (s, e) =>
            {
                var names = string.Join(", ", conflicts.Select(c => c.Owner.ToString()).Distinct());
                if (!Ui.Confirm(this, I18n.T("port.closeConfirm", names))) return;
                Choice = PortChoice.CloseOwners;
                Close();
            });
            kill.Enabled = conflicts.All(c => !c.Owner.IsSystem && c.Owner.Pid > 0);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(free);
            buttons.Controls.Add(kill);
            t.Controls.Add(buttons);
            Controls.Add(t);
            CancelButton = cancel;
            AcceptButton = free;
        }
    }
}
