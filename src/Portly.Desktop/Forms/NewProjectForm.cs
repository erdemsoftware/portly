using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Services;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>New project: name + a template picked from tiles (PHP and JavaScript).</summary>
    public class NewProjectForm : PortlyDialog
    {
        readonly TextBox _name;
        readonly CheckBox _db, _open, _wpFull, _sub;
        readonly ComboBox _parent;
        readonly TextBlock _status, _url;
        readonly Button _create, _cancel;
        readonly List<Border> _tiles = new List<Border>();
        string _template = "blank";
        CancellationTokenSource _cts;

        public string CreatedName { get; private set; }
        public bool OpenAfter { get { return _open.IsChecked == true; } }

        /// <summary>Shows the dialog; returns the form when a project was created (read CreatedName / OpenAfter), otherwise null.</summary>
        public static async Task<NewProjectForm> Ask(Window owner, string template = null, string parent = null)
        {
            var f = new NewProjectForm(template, parent);
            return await f.ShowDialog<bool>(owner) ? f : null;
        }

        /// <param name="parent">Opened from a project's "Add sub-project": that project is already picked.</param>
        public NewProjectForm(string template = null, string parent = null) : base(I18n.T("project.title"), 640)
        {
            _name = new TextBox();
            _url = Ui.Label(" ", "muted small");
            _name.TextChanged += (s, e) => UpdateUrl();
            Body.Children.Add(Ui.Label(I18n.T("project.title"), "title"));
            Body.Children.Add(Ui.Column(4, Ui.Label(I18n.T("project.name"), "bold"), _name, _url));

            // sub-project: created in its own folder inside the chosen project (name.parent.tld)
            _sub = new CheckBox { Content = I18n.T("project.isSub") };
            _parent = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false, Margin = new Thickness(26, 0, 0, 0) };
            foreach (var p in Portly.Config.Sites.Scan().Where(x => x.Name.IndexOf('/') < 0 && x.Valid && !x.Archived).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                _parent.Items.Add(p.Name);
            if (_parent.Items.Count == 0) _sub.IsEnabled = false;
            else _parent.SelectedIndex = 0;
            if (parent != null && _parent.Items.Contains(parent))
            {
                _parent.SelectedItem = parent;
                _sub.IsChecked = true;
                _parent.IsEnabled = true;
            }
            _sub.IsCheckedChanged += (s, e) => { _parent.IsEnabled = _sub.IsChecked == true; UpdateUrl(); };
            _parent.SelectionChanged += (s, e) => UpdateUrl();
            Body.Children.Add(Ui.Column(6, _sub, _parent));

            var php = new WrapPanel { Orientation = Orientation.Horizontal };
            php.Children.Add(TemplateTile("blank", I18n.T("project.blankShort"), Ui.Gray));
            php.Children.Add(TemplateTile("wordpress", "WordPress", Color.Parse("#21759B")));
            foreach (var tt in ProjectCreator.TerminalTemplates.Where(x => x.Item3 == "composer"))
                php.Children.Add(TemplateTile(tt.Item1, tt.Item2, Brand(tt.Item1)));
            Body.Children.Add(Ui.Column(6, Ui.Section("PHP"), php));
            var js = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var tt in ProjectCreator.TerminalTemplates.Where(x => x.Item3 == "node"))
                js.Children.Add(TemplateTile(tt.Item1, tt.Item2, Brand(tt.Item1)));
            Body.Children.Add(Ui.Column(6, Ui.Section("JavaScript (Node.js)"), js));

            _wpFull = new CheckBox { Content = I18n.T("project.wpFull"), IsChecked = true };
            _db = new CheckBox { Content = I18n.T("project.createDb"), IsChecked = true };
            _open = new CheckBox { Content = I18n.T("project.openAfter"), IsChecked = true };
            Body.Children.Add(Ui.Column(2, _wpFull, _db, _open));

            _status = Ui.Label("", "muted");
            Body.Children.Add(_status);

            _create = Ui.Button(I18n.T("project.create"), Create, "primary", "plus");
            _create.IsDefault = true;
            _cancel = Ui.Button(I18n.T("common.cancel"), () => { if (_cts != null) _cts.Cancel(); else Close(false); });
            Buttons.Children.Add(_create);
            Buttons.Children.Add(_cancel);
            Closing += (s, e) => { if (_cts != null) _cts.Cancel(); };

            Select(template ?? "blank");
            UpdateUrl();
        }

        static Color Brand(string key)
        {
            switch (key)
            {
                case "laravel": return Color.Parse("#FF2D20");
                case "symfony": return Colors.Black;
                case "codeigniter": return Color.Parse("#DD4814");
                case "cakephp": return Color.Parse("#D33C43");
                case "drupal": return Color.Parse("#0678BE");
                case "vue": return Color.Parse("#42B883");
                case "react": return Color.Parse("#149ECA");
                case "svelte": return Color.Parse("#FF3E00");
                case "nuxt": return Color.Parse("#00DC82");
                case "next": return Colors.Black;
                case "angular": return Color.Parse("#DD0031");
                case "astro": return Color.Parse("#BC52EE");
                default: return UI.Theme.Current.Accent;
            }
        }

        Border TemplateTile(string key, string text, Color color) { return TemplateTile(key, text, new SolidColorBrush(color)); }

        Border TemplateTile(string key, string text, IBrush color)
        {
            var dot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = color, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(dot);
            content.Children.Add(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = content, Width = 138, Height = 42, MinWidth = 0, Padding = new Thickness(10, 0), HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Classes.Add("tile");
            Avalonia.Automation.AutomationProperties.SetName(b, text);
            b.Click += (s, e) => Select(key);
            // the ring around the tile marks the chosen template
            var ring = new Border { Tag = key, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 6), Child = b };
            _tiles.Add(ring);
            return ring;
        }

        void Select(string key)
        {
            _template = key;
            foreach (var r in _tiles) r.BorderBrush = (string)r.Tag == key ? Ui.Accent : Brushes.Transparent;
            var tt = ProjectCreator.TerminalTemplate(key);
            _wpFull.IsVisible = key == "wordpress";
            _db.IsEnabled = tt == null || tt.Item3 == "composer";
            _status.Text = tt != null && tt.Item3 == "node" ? I18n.T("project.nodeHint") : "";
        }

        void UpdateUrl()
        {
            var n = (_name.Text ?? "").Trim();
            var s = Settings.Current;
            _url.Text = n == "" ? I18n.T("project.urlHint", s.Tld) : (s.Ssl ? "https://" : "http://") + ProjectCreator.HostFor(n, ParentProject);
            _url.Foreground = n == "" || Portly.Config.Sites.IsValidName(n) ? Ui.Muted : Ui.Red;
        }

        /// <summary>The chosen main project when "This is a sub-project" is ticked, otherwise null.</summary>
        string ParentProject { get { return _sub != null && _sub.IsChecked == true ? _parent.SelectedItem as string : null; } }

        async Task Create()
        {
            var name = (_name.Text ?? "").Trim();
            var parent = ParentProject;
            if (_sub.IsChecked == true && parent == null) { await Dialogs.Warning(this, I18n.T("project.pickParent")); return; }
            var err = ProjectCreator.ValidateName(name, parent);
            if (err != null) { await Dialogs.Warning(this, err); return; }
            // a real domain: this PC will open the local copy instead of the live site (until it is switched on the card)
            var host = ProjectCreator.HostFor(name, parent);
            if (Portly.Config.Sites.IsPublicName(host) && !await Dialogs.Confirm(this, I18n.T("project.realDomain", host, host + "." + Settings.Current.Tld))) return;
            var template = _template;
            bool wantDb = _db.IsEnabled && _db.IsChecked == true;
            if (wantDb && ServiceManager.MySql.State != ServiceState.Running)
            {
                if (!await Dialogs.Confirm(this, I18n.T("project.mysqlOff"))) return;
                wantDb = false;
            }
            _create.IsEnabled = false;
            _cts = new CancellationTokenSource();
            try
            {
                var msg = await ProjectCreator.CreateAsync(name, template, wantDb, _wpFull.IsVisible && _wpFull.IsChecked == true, parent, s => Ui.Post(() => _status.Text = s), _cts.Token);
                CreatedName = parent == null ? name : parent + "/" + name;
                _cts = null;
                await Dialogs.Info(this, msg);
                Close(true);
            }
            catch (OperationCanceledException) { _status.Text = I18n.T("pkg.cancelled"); }
            catch (Exception ex) { await Dialogs.Warning(this, ex.Message); }
            finally
            {
                _cts = null;
                _create.IsEnabled = true;
            }
        }

        public static Window Preview()
        {
            var f = new NewProjectForm("laravel");
            f._name.Text = "blog";
            return f;
        }
    }
}
