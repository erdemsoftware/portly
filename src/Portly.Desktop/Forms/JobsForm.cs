using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Portly.Config;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>Feature "jobs": site menu > Background jobs... List with state, start / stop / restart, edit, live log.</summary>
    public class JobsForm : PortlyDialog
    {
        readonly Site _site;
        readonly GridList _list;
        readonly LogView _log;
        readonly Action _onChanged;
        List<JobDef> _defs;
        string _shown;
        bool _preview;
        List<Job> _sample;

        public JobsForm(Site site) : base(I18n.T("jobs.title", site.Name), 960)
        {
            _site = site;

            var hint = Ui.Label(I18n.T("jobs.hint"), "muted small");
            _list = new GridList("140,2.4*,110,*", new[] { I18n.T("jobs.col.name"), I18n.T("jobs.col.command"), I18n.T("jobs.col.state"), I18n.T("jobs.col.info") });
            _list.Height = 190;
            _list.Margin = new Thickness(0, 8, 0, 0);
            _list.Box.SelectionChanged += (s, e) => ShowLog();
            _list.Box.DoubleTapped += async (s, e) => await Ui.Run(Edit, _list);

            var bar = DevUi.Bar(
                Ui.Button(I18n.T("jobs.start"), () => Act(d => { Jobs.Start(_site, d); return Task.CompletedTask; }), "primary", "play"),
                Ui.Button(I18n.T("jobs.stop"), () => Act(d => Jobs.StopAsync(_site.Name, d.Name)), null, "stop"),
                Ui.Button(I18n.T("jobs.restart"), () => Act(d => Jobs.RestartAsync(_site, d)), null, "refresh"),
                new Border { Width = 8 },
                Ui.Button(I18n.T("jobs.add"), Add, null, "plus"),
                Ui.Button(I18n.T("jobs.edit"), Edit),
                Ui.Button(I18n.T("jobs.remove"), Remove),
                Ui.Button(I18n.T("log.folder"), () => DevUi.OpenFolder(Jobs.LogDir), null, "folder"));

            _log = new LogView();
            var grid = new DockPanel();
            DockPanel.SetDock(hint, Dock.Top);
            DockPanel.SetDock(_list, Dock.Top);
            DockPanel.SetDock(bar, Dock.Top);
            grid.Children.Add(hint);
            grid.Children.Add(_list);
            grid.Children.Add(bar);
            grid.Children.Add(_log);
            DevUi.Fill(this, grid, 960, 660, 700, 480);

            _onChanged = () => Ui.Post(Fill);
            Jobs.Changed += _onChanged;
            Closed += (s, e) => Jobs.Changed -= _onChanged;
            Opened += (s, e) => { if (_preview) return; Fill(); if (_list.Box.ItemCount > 0) _list.Box.SelectedIndex = 0; };
        }

        List<JobDef> LoadDefs() { return _sample != null ? _sample.Select(j => j.Def).ToList() : Jobs.Load(_site.Name); }

        Job GetJob(string name) { return _sample != null ? _sample.FirstOrDefault(j => j.Def.Name == name) : Jobs.Get(_site.Name, name); }

        void Fill()
        {
            var selected = Selected();
            _defs = LoadDefs();
            _list.Clear();
            foreach (var d in _defs)
            {
                var job = GetJob(d.Name);
                var state = job == null ? JobState.Stopped : job.State;
                var cmd = d.Command + (d.Schedule ? "   (" + I18n.T("jobs.everyMinute") + ")" : "");
                var color = state == JobState.Running ? Ui.Green : state == JobState.Failed ? Ui.Red : state == JobState.Waiting ? Ui.Orange : null;
                var item = _list.Add(d, new[] { d.Name, cmd, I18n.T("jobs.state." + state.ToString().ToLowerInvariant()), job == null ? (d.Autostart ? I18n.T("jobs.autostart") : "") : job.Info }, color);
                if (selected != null && selected.Name == d.Name) _list.Box.SelectedItem = item;
            }
            if (_defs.Count == 0)
            {
                var item = _list.Add(null, new[] { "", I18n.T("jobs.none"), "", "" }, Ui.Muted);
                item.IsHitTestVisible = false;
            }
        }

        JobDef Selected() { return _list.Selected as JobDef; }

        void ShowLog()
        {
            var d = Selected();
            if (d == null) return;
            var name = d.Name;
            if (_shown == name) return;
            _shown = name;
            _log.Show(() => Jobs.LogFileFor(_site.Name, name), null);
        }

        async Task Act(Func<JobDef, Task> action)
        {
            var d = Selected();
            if (d == null) return;
            if (!Features.IsOn("jobs")) { await Dialogs.Warning(this, I18n.T("jobs.featureOff")); return; }
            try { await action(d); } catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
            Fill();
        }

        async Task Add()
        {
            var def = await JobEditForm.Ask(this, null, _defs.Select(x => x.Name).ToList());
            if (def == null) return;
            Jobs.Upsert(_site.Name, def);
            Fill();
        }

        async Task Edit()
        {
            var d = Selected();
            if (d == null) return;
            var def = await JobEditForm.Ask(this, d, _defs.Where(x => x.Name != d.Name).Select(x => x.Name).ToList());
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
            if (!await Dialogs.Confirm(this, I18n.T("jobs.removeConfirm", d.Name))) return;
            await Jobs.RemoveAsync(_site.Name, d.Name);
            var list = Jobs.Load(_site.Name);
            list.RemoveAll(x => x.Name == d.Name);
            Jobs.Save(_site.Name, list);
            Fill();
        }

        public static Window Preview()
        {
            var dir = DevUi.Temp("jobs");
            var site = new Site { Name = "shop", Host = "shop.portly", Folder = dir, DocRoot = dir, Valid = true };
            var f = new JobsForm(site);
            f._preview = true;
            f._sample = new List<Job>
            {
                new Job { Site = "shop", Def = new JobDef { Name = "queue", Command = "php artisan queue:work --tries=3", Autostart = true }, State = JobState.Running, Info = "pid 14288" },
                new Job { Site = "shop", Def = new JobDef { Name = "schedule", Command = "php artisan schedule:run", Schedule = true }, State = JobState.Waiting, Info = "next in 38 s" },
                new Job { Site = "shop", Def = new JobDef { Name = "vite", Command = "npm run dev" }, State = JobState.Stopped, Info = "" },
                new Job { Site = "shop", Def = new JobDef { Name = "reverb", Command = "php artisan reverb:start" }, State = JobState.Failed, Info = "exit code 1" },
            };
            f.Fill();
            f._list.Box.SelectedIndex = 0;
            return f;
        }
    }

    /// <summary>Add / edit one job: preset, name, command, options.</summary>
    public class JobEditForm : PortlyDialog
    {
        public static Task<JobDef> Ask(Window owner, JobDef existing, List<string> takenNames)
        {
            return new JobEditForm(existing, takenNames).ShowDialog<JobDef>(owner);
        }

        public static Window Preview()
        {
            return new JobEditForm(new JobDef { Name = "queue", Command = "php artisan queue:work --tries=3", Autostart = true, Restart = true }, new List<string>());
        }

        JobEditForm(JobDef existing, List<string> taken) : base(existing == null ? I18n.T("jobs.add") : I18n.T("jobs.edit"), 520)
        {
            var preset = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var p in Jobs.Presets) preset.Items.Add(I18n.T(p.TitleKey));
            var name = new TextBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
            var cmd = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var presetHint = Ui.Label("", "muted small");
            var autostart = new CheckBox { Content = I18n.T("jobs.opt.autostart") };
            var restart = new CheckBox { Content = I18n.T("jobs.opt.restart"), IsChecked = true };
            var schedule = new CheckBox { Content = I18n.T("jobs.opt.schedule") };
            schedule.IsCheckedChanged += (s, e) => restart.IsEnabled = schedule.IsChecked != true;
            var error = Ui.Label("", "small", Ui.Red);
            bool skipFirst = existing != null;

            preset.SelectionChanged += (s, e) =>
            {
                if (preset.SelectedIndex < 0) return;
                var p = Jobs.Presets[preset.SelectedIndex];
                if (skipFirst) { skipFirst = false; presetHint.Text = I18n.T("jobs.presetHint." + p.Key); return; }
                if (p.Key != "custom") { name.Text = p.Name; cmd.Text = p.Command; schedule.IsChecked = p.Schedule; }
                presetHint.Text = I18n.T("jobs.presetHint." + p.Key);
            };

            Body.Children.Add(Ui.Field(I18n.T("jobs.preset"), preset));
            Body.Children.Add(Ui.Field(I18n.T("jobs.col.name"), name));
            Body.Children.Add(Ui.Column(4, Ui.Label(I18n.T("jobs.col.command"), "bold"), cmd, presetHint));
            Body.Children.Add(Ui.Column(2, autostart, restart, schedule));
            Body.Children.Add(error);

            var ok = Ui.Button(I18n.T("common.save"), () =>
            {
                var n = (name.Text ?? "").Trim().ToLowerInvariant();
                var c = (cmd.Text ?? "").Trim();
                if (!Jobs.NameRx.IsMatch(n)) { error.Text = I18n.T("jobs.badName"); return; }
                if (taken.Contains(n)) { error.Text = I18n.T("jobs.nameTaken", n); return; }
                if (c == "") { error.Text = I18n.T("jobs.noCommand"); return; }
                bool sch = schedule.IsChecked == true;
                Close(new JobDef { Name = n, Command = c, Autostart = autostart.IsChecked == true, Restart = restart.IsChecked == true && !sch, Schedule = sch });
            }, "primary");
            ok.IsDefault = true;
            Buttons.Children.Add(ok);
            Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(null)));

            if (existing != null)
            {
                name.Text = existing.Name;
                cmd.Text = existing.Command;
                autostart.IsChecked = existing.Autostart;
                restart.IsChecked = existing.Restart;
                schedule.IsChecked = existing.Schedule;
                var match = Jobs.Presets.FirstOrDefault(p => p.Key != "custom" && p.Command == existing.Command);
                preset.SelectedIndex = Jobs.Presets.IndexOf(match ?? Jobs.Presets.Last());
            }
            else preset.SelectedIndex = 0;
        }
    }
}
