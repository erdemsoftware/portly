using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Config;
using Portly.Core;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>Feature "jobs": site menu > Background jobs... List with state, start / stop / restart, edit, live log.</summary>
    public class JobsForm : Form
    {
        readonly Site _site;
        readonly ListView _list;
        readonly LogView _log;
        readonly Action _onChanged;
        List<JobDef> _defs;

        public JobsForm(Site site)
        {
            _site = site;
            Ui.Apply(this);
            Text = I18n.T("jobs.title", site.Name);
            Size = Ui.S(960, 660);
            MinimumSize = Ui.S(700, 480);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Padding = Ui.P(16, 12, 16, 12);

            var hint = Ui.Label(I18n.T("jobs.hint"), Ui.Small, Ui.Muted);
            hint.Dock = DockStyle.Top;
            hint.AutoSize = false;
            hint.Height = Ui.S(40);

            _list = new ListView { Dock = DockStyle.Top, Height = Ui.S(190), View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = BorderStyle.FixedSingle };
            _list.Columns.Add(I18n.T("jobs.col.name"), Ui.S(120));
            _list.Columns.Add(I18n.T("jobs.col.command"), Ui.S(360));
            _list.Columns.Add(I18n.T("jobs.col.state"), Ui.S(120));
            _list.Columns.Add(I18n.T("jobs.col.info"), Ui.S(260));
            _list.SelectedIndexChanged += (s, e) => ShowLog();
            _list.DoubleClick += (s, e) => Edit();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Padding = Ui.P(0, 8, 0, 8) };
            bar.Controls.Add(Ui.Button(I18n.T("jobs.start"), async (s, e) => await Act(d => { Jobs.Start(_site, d); return Task.FromResult(0); }), true, "play"));
            bar.Controls.Add(Ui.Button(I18n.T("jobs.stop"), async (s, e) => await Act(d => Jobs.StopAsync(_site.Name, d.Name)), false, "stop"));
            bar.Controls.Add(Ui.Button(I18n.T("jobs.restart"), async (s, e) => await Act(d => Jobs.RestartAsync(_site, d)), false, "refresh"));
            bar.Controls.Add(new Panel { Width = Ui.S(16), Height = 1, BackColor = Color.Transparent });
            bar.Controls.Add(Ui.Button(I18n.T("jobs.add"), (s, e) => Add(), false, "plus"));
            bar.Controls.Add(Ui.Button(I18n.T("jobs.edit"), (s, e) => Edit()));
            bar.Controls.Add(Ui.Button(I18n.T("jobs.remove"), async (s, e) => await Remove()));
            bar.Controls.Add(Ui.Button(I18n.T("log.folder"), (s, e) => WinUtil.OpenFolder(Jobs.LogDir), false, "folder"));

            _log = new LogView { Dock = DockStyle.Fill };
            Controls.Add(_log);
            Controls.Add(bar);
            Controls.Add(_list);
            Controls.Add(hint);
            _log.BringToFront();

            _onChanged = () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)Fill); } catch { } };
            Jobs.Changed += _onChanged;
            FormClosed += (s, e) => Jobs.Changed -= _onChanged;
            Load += (s, e) => { Fill(); if (_list.Items.Count > 0) _list.Items[0].Selected = true; };
        }

        void Fill()
        {
            var selected = Selected();
            _defs = Jobs.Load(_site.Name);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var d in _defs)
            {
                var job = Jobs.Get(_site.Name, d.Name);
                var state = job == null ? JobState.Stopped : job.State;
                var cmd = d.Command + (d.Schedule ? "   (" + I18n.T("jobs.everyMinute") + ")" : "");
                var item = new ListViewItem(new[] { d.Name, cmd, I18n.T("jobs.state." + state.ToString().ToLowerInvariant()), job == null ? (d.Autostart ? I18n.T("jobs.autostart") : "") : job.Info }) { Tag = d };
                item.ForeColor = state == JobState.Running ? Ui.Green : state == JobState.Failed ? Ui.Red : state == JobState.Waiting ? Ui.Orange : Ui.Text;
                _list.Items.Add(item);
                if (selected != null && selected.Name == d.Name) item.Selected = true;
            }
            if (_defs.Count == 0) _list.Items.Add(new ListViewItem(new[] { "", I18n.T("jobs.none"), "", "" }) { ForeColor = Ui.Muted });
            _list.EndUpdate();
        }

        JobDef Selected()
        {
            return _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as JobDef : null;
        }

        void ShowLog()
        {
            var d = Selected();
            if (d == null) return;
            var name = d.Name;
            _log.Show(() => Jobs.LogFileFor(_site.Name, name), null);
        }

        async Task Act(Func<JobDef, Task> action)
        {
            var d = Selected();
            if (d == null) return;
            if (!Features.IsOn("jobs")) { Ui.Error(this, I18n.T("jobs.featureOff")); return; }
            try { await action(d); } catch (Exception ex) { Ui.Error(this, ex.Message); }
            Fill();
        }

        void Add()
        {
            var def = JobEditForm.Ask(this, null, _defs.Select(x => x.Name).ToList());
            if (def == null) return;
            Jobs.Upsert(_site.Name, def);
            Fill();
        }

        void Edit()
        {
            var d = Selected();
            if (d == null) return;
            var def = JobEditForm.Ask(this, d, _defs.Where(x => x.Name != d.Name).Select(x => x.Name).ToList());
            if (def == null) return;
            var list = Jobs.Load(_site.Name);
            list.RemoveAll(x => x.Name == d.Name);
            list.Add(def);
            Jobs.Save(_site.Name, list);
            Fill();
        }

        async Task Remove()
        {
            var d = Selected();
            if (d == null) return;
            if (!Ui.Confirm(this, I18n.T("jobs.removeConfirm", d.Name))) return;
            await Jobs.RemoveAsync(_site.Name, d.Name);
            var list = Jobs.Load(_site.Name);
            list.RemoveAll(x => x.Name == d.Name);
            Jobs.Save(_site.Name, list);
            Fill();
        }
    }

    /// <summary>Add / edit one job: preset, name, command, options.</summary>
    public class JobEditForm : Form
    {
        public static JobDef Ask(IWin32Window owner, JobDef existing, List<string> takenNames)
        {
            using (var f = new JobEditForm(existing, takenNames))
                return f.ShowDialog(owner) == DialogResult.OK ? f._result : null;
        }

        JobDef _result;

        JobEditForm(JobDef existing, List<string> taken)
        {
            Ui.Apply(this);
            Text = existing == null ? I18n.T("jobs.add") : I18n.T("jobs.edit");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(18);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(460), Margin = Ui.P(0, 2, 0, 10) };
            foreach (var p in Jobs.Presets) preset.Items.Add(new Item(I18n.T(p.TitleKey), p.Key));
            var name = new TextBox { Width = Ui.S(220), Margin = Ui.P(0, 2, 0, 10) };
            var cmd = new TextBox { Width = Ui.S(460), Margin = Ui.P(0, 2, 0, 4) };
            var presetHint = Ui.Label("", Ui.Small, Ui.Muted);
            presetHint.MaximumSize = new Size(Ui.S(460), 0);
            var autostart = new CheckBox { Text = I18n.T("jobs.opt.autostart"), AutoSize = true, Margin = Ui.P(0, 8, 0, 2) };
            var restart = new CheckBox { Text = I18n.T("jobs.opt.restart"), AutoSize = true, Checked = true, Margin = Ui.P(0, 2, 0, 2) };
            var schedule = new CheckBox { Text = I18n.T("jobs.opt.schedule"), AutoSize = true, Margin = Ui.P(0, 2, 0, 2) };
            schedule.CheckedChanged += (s, e) => restart.Enabled = !schedule.Checked;
            var error = Ui.Label("", Ui.Small, Ui.Red);

            preset.SelectedIndexChanged += (s, e) =>
            {
                var p = Jobs.Presets.First(x => x.Key == ((Item)preset.SelectedItem).Value);
                if (existing != null && preset.Tag == null) { preset.Tag = 1; return; }
                if (p.Key != "custom") { name.Text = p.Name; cmd.Text = p.Command; schedule.Checked = p.Schedule; }
                presetHint.Text = I18n.T("jobs.presetHint." + p.Key);
            };

            t.Controls.Add(Ui.Label(I18n.T("jobs.preset")));
            t.Controls.Add(preset);
            t.Controls.Add(Ui.Label(I18n.T("jobs.col.name")));
            t.Controls.Add(name);
            t.Controls.Add(Ui.Label(I18n.T("jobs.col.command")));
            t.Controls.Add(cmd);
            t.Controls.Add(presetHint);
            t.Controls.Add(autostart);
            t.Controls.Add(restart);
            t.Controls.Add(schedule);
            t.Controls.Add(error);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 12, 0, 0) };
            var cancel = Ui.Button(I18n.T("common.cancel"), null);
            cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.Button(I18n.T("common.save"), (s, e) =>
            {
                var n = name.Text.Trim().ToLowerInvariant();
                if (!Jobs.NameRx.IsMatch(n)) { error.Text = I18n.T("jobs.badName"); return; }
                if (taken.Contains(n)) { error.Text = I18n.T("jobs.nameTaken", n); return; }
                if (cmd.Text.Trim() == "") { error.Text = I18n.T("jobs.noCommand"); return; }
                _result = new JobDef { Name = n, Command = cmd.Text.Trim(), Autostart = autostart.Checked, Restart = restart.Checked && !schedule.Checked, Schedule = schedule.Checked };
                DialogResult = DialogResult.OK;
            }, true);
            ok.Margin = Ui.P(0, 0, 8, 0);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            t.Controls.Add(buttons);
            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;

            if (existing != null)
            {
                name.Text = existing.Name;
                cmd.Text = existing.Command;
                autostart.Checked = existing.Autostart;
                restart.Checked = existing.Restart;
                schedule.Checked = existing.Schedule;
                var match = Jobs.Presets.FirstOrDefault(p => p.Key != "custom" && p.Command == existing.Command);
                preset.SelectedIndex = Jobs.Presets.IndexOf(match ?? Jobs.Presets.Last());
            }
            else preset.SelectedIndex = 0;
        }
    }
}
