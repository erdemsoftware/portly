using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Portly.Core;
using Portly.Services;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>New project: name + a template picked from tiles (PHP and JavaScript).</summary>
    public class NewProjectForm : Form
    {
        readonly TextBox _name;
        readonly CheckBox _db, _open, _wpFull;
        readonly Label _status, _url;
        readonly ModernButton _create, _cancel;
        readonly List<ModernButton> _tiles = new List<ModernButton>();
        string _template = "blank";
        CancellationTokenSource _cts;

        public string CreatedName { get; private set; }
        public bool OpenAfter { get { return _open.Checked; } }

        public NewProjectForm(string template = null)
        {
            Ui.Apply(this);
            Text = I18n.T("project.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(20);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            t.Controls.Add(Ui.Label(I18n.T("project.title"), Ui.Title));
            t.Controls.Add(Ui.Label(I18n.T("project.name"), Ui.Bold));
            _name = new TextBox { Width = Ui.S(400), Margin = Ui.P(0, 2, 0, 2) };
            t.Controls.Add(_name);
            _url = Ui.Label(" ", Ui.Small, Ui.Muted);
            t.Controls.Add(_url);
            _name.TextChanged += (s, e) => UpdateUrl();

            t.Controls.Add(Section("PHP"));
            var php = Flow();
            php.Controls.Add(TemplateTile("blank", I18n.T("project.blankShort"), Ui.Gray));
            php.Controls.Add(TemplateTile("wordpress", "WordPress", ColorTranslator.FromHtml("#21759B")));
            foreach (var tt in ProjectCreator.TerminalTemplates.Where(x => x.Item3 == "composer"))
                php.Controls.Add(TemplateTile(tt.Item1, tt.Item2, Brand(tt.Item1)));
            t.Controls.Add(php);
            t.Controls.Add(Section("JavaScript (Node.js)"));
            var js = Flow();
            foreach (var tt in ProjectCreator.TerminalTemplates.Where(x => x.Item3 == "node"))
                js.Controls.Add(TemplateTile(tt.Item1, tt.Item2, Brand(tt.Item1)));
            t.Controls.Add(js);

            _wpFull = new CheckBox { Text = I18n.T("project.wpFull"), AutoSize = true, Checked = true, Margin = Ui.P(0, 10, 0, 0) };
            _db = new CheckBox { Text = I18n.T("project.createDb"), AutoSize = true, Checked = true, Margin = Ui.P(0, 4, 0, 0) };
            _open = new CheckBox { Text = I18n.T("project.openAfter"), AutoSize = true, Checked = true };
            t.Controls.Add(_wpFull);
            t.Controls.Add(_db);
            t.Controls.Add(_open);

            _status = new Label { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(Ui.S(560), 0), Margin = Ui.P(0, 10, 0, 0) };
            t.Controls.Add(_status);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 12, 0, 0) };
            _cancel = Ui.Button(I18n.T("common.cancel"), (s, e) => { if (_cts != null) _cts.Cancel(); else Close(); });
            _cancel.Margin = Padding.Empty;
            _create = Ui.Button(I18n.T("project.create"), (s, e) => Create(), true, "plus");
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_create);
            t.Controls.Add(buttons);
            Controls.Add(t);
            AcceptButton = _create;

            Select(template ?? "blank");
            UpdateUrl();
        }

        static Label Section(string text)
        {
            var l = Ui.Label(Ui.Upper(text), Ui.Small, Ui.Muted);
            l.Margin = Ui.P(0, 12, 0, 4);
            return l;
        }

        static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(Ui.S(600), 0), WrapContents = true, Margin = Padding.Empty };
        }

        static Color Brand(string key)
        {
            switch (key)
            {
                case "laravel": return ColorTranslator.FromHtml("#FF2D20");
                case "symfony": return Color.Black;
                case "codeigniter": return ColorTranslator.FromHtml("#DD4814");
                case "cakephp": return ColorTranslator.FromHtml("#D33C43");
                case "drupal": return ColorTranslator.FromHtml("#0678BE");
                case "vue": return ColorTranslator.FromHtml("#42B883");
                case "react": return ColorTranslator.FromHtml("#149ECA");
                case "svelte": return ColorTranslator.FromHtml("#FF3E00");
                case "nuxt": return ColorTranslator.FromHtml("#00DC82");
                case "next": return Color.Black;
                case "angular": return ColorTranslator.FromHtml("#DD0031");
                case "astro": return ColorTranslator.FromHtml("#BC52EE");
                default: return Ui.Accent;
            }
        }

        ModernButton TemplateTile(string key, string text, Color color)
        {
            var b = new ModernButton
            {
                Text = text,
                Kind = ButtonKind.Secondary,
                AutoSize = false,
                Size = Ui.S(112, 40),
                Margin = Ui.P(0, 0, 8, 8),
                Font = Ui.Bold,
                Tag = key,
                CustomColor = color,
            };
            b.Click += (s, e) => Select(key);
            _tiles.Add(b);
            return b;
        }

        void Select(string key)
        {
            _template = key;
            foreach (var b in _tiles)
            {
                bool on = (string)b.Tag == key;
                b.Kind = on ? ButtonKind.Primary : ButtonKind.Secondary;
                b.Invalidate();
            }
            var tt = ProjectCreator.TerminalTemplate(key);
            _wpFull.Visible = key == "wordpress";
            _db.Enabled = tt == null || tt.Item3 == "composer";
            _status.Text = tt != null && tt.Item3 == "node" ? I18n.T("project.nodeHint") : "";
        }

        void UpdateUrl()
        {
            var n = _name.Text.Trim();
            var s = Settings.Current;
            _url.Text = n == "" ? I18n.T("project.urlHint", s.Tld) : (s.Ssl ? "https://" : "http://") + n.ToLowerInvariant() + "." + s.Tld;
            _url.ForeColor = n == "" || Config.Sites.IsValidName(n) ? Ui.Muted : Ui.Red;
        }

        async void Create()
        {
            var name = _name.Text.Trim();
            var err = ProjectCreator.ValidateName(name);
            if (err != null) { Ui.Error(this, err); return; }
            var template = _template;
            bool wantDb = _db.Enabled && _db.Checked;
            if (wantDb && ServiceManager.MySql.State != ServiceState.Running)
            {
                if (!Ui.Confirm(this, I18n.T("project.mysqlOff"))) return;
                wantDb = false;
            }
            _create.Enabled = false;
            _cts = new CancellationTokenSource();
            try
            {
                var msg = await ProjectCreator.CreateAsync(name, template, wantDb, _wpFull.Visible && _wpFull.Checked, s => BeginInvoke((Action)(() => _status.Text = s)), _cts.Token);
                CreatedName = name;
                Ui.Info(this, msg);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException) { _status.Text = I18n.T("pkg.cancelled"); }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
            finally
            {
                _cts = null;
                if (!IsDisposed) _create.Enabled = true;
            }
        }
    }
}
