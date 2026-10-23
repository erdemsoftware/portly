using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>
    /// Feature "projectFile": shows exactly what a portly.yml changes before anything happens.
    /// Commands (jobs, hooks) are listed apart and only applied with the "Allow commands from this file" tick.
    /// </summary>
    public class ProjectFileForm : Form
    {
        public bool Applied { get; private set; }

        public ProjectFileForm(Site site)
        {
            Ui.Apply(this);
            Text = I18n.T("pf.title", site.Name);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(20);

            var file = ProjectFile.Find(site);
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            int w = Ui.S(580);
            Func<string, Font, Color, Label> label = (text, font, color) =>
            {
                var l = Ui.Label(text, font, color);
                l.MaximumSize = new Size(w, 0);
                t.Controls.Add(l);
                return l;
            };
            label(file ?? "", Ui.Small, Ui.Muted);

            ProjectSpec spec = null;
            string error = null;
            try { spec = ProjectFile.Load(file); }
            catch (YamlException ex) { error = I18n.T("pf.syntax", ex.Line, ex.Message); }
            catch (Exception ex) { error = ex.Message; }

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 14, 0, 0) };
            var close = Ui.Button(I18n.T("common.close"), (s, e) => Close());
            buttons.Controls.Add(close);
            CancelButton = close;

            if (spec == null)
            {
                label(error, Ui.Font, Ui.Red);
                t.Controls.Add(buttons);
                Controls.Add(t);
                return;
            }

            var steps = ProjectFile.Plan(site, spec);
            label(I18n.T("pf.changes"), Ui.Bold, Ui.Text).Margin = Ui.P(0, 10, 0, 4);
            foreach (var st in steps.Where(x => !x.IsCommand))
                label((st.Warning ? "!  " : "•  ") + st.Text, Ui.Font, st.Warning ? Ui.Orange : Ui.Text);
            if (steps.All(x => x.IsCommand)) label(I18n.T("pf.noChanges"), Ui.Font, Ui.Muted);

            CheckBox allow = null;
            if (spec.HasCommands)
            {
                label(I18n.T("pf.commands"), Ui.Bold, Ui.Text).Margin = Ui.P(0, 14, 0, 4);
                foreach (var st in steps.Where(x => x.IsCommand)) label("›  " + st.Text, Ui.Mono, Ui.Text);
                label(I18n.T("pf.commandsWarn"), Ui.Small, Ui.Muted);
                allow = new CheckBox { Text = I18n.T("pf.allowCommands"), AutoSize = true, Margin = Ui.P(0, 6, 0, 0) };
                t.Controls.Add(allow);
            }

            var result = label("", Ui.Font, Ui.Text);
            ModernButton apply = null;
            apply = Ui.Button(I18n.T("pf.apply"), async (s, e) =>
            {
                apply.Enabled = false;
                close.Enabled = false;
                try
                {
                    var done = await ProjectFile.ApplyAsync(site, file, spec, allow != null && allow.Checked, msg => result.Text = msg);
                    result.Text = string.Join("\n", done);
                    Applied = true;
                    await ServiceManager.ReloadWebAsync();
                }
                catch (Exception ex) { result.Text = ex.Message; apply.Enabled = true; }
                finally { close.Enabled = true; }
            }, true);
            apply.Margin = Ui.P(0, 0, 8, 0);
            buttons.Controls.Add(apply);
            t.Controls.Add(buttons);
            Controls.Add(t);
        }
    }
}
