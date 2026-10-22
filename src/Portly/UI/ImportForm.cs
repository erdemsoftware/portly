using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Portly.Core;
using Portly.Tools;

namespace Portly.UI
{
    /// <summary>Move projects and databases from Laragon or XAMPP.</summary>
    public class ImportForm : Form
    {
        readonly List<ImportSource> _sources;
        readonly ThemedCombo _source;
        readonly Label _info, _status;
        readonly CheckBox _projects, _databases;
        readonly RadioButton _inPlace, _copy;
        readonly CheckedListBox _dbs;
        readonly TextBox _password;
        readonly ModernButton _list, _start, _close;
        readonly ProgressBar _progress;
        bool _busy;
        public bool ProjectsChanged { get; private set; }

        public ImportForm(List<ImportSource> sources)
        {
            _sources = sources;
            Ui.Apply(this);
            Text = I18n.T("import.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = Ui.P(20);

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            t.Controls.Add(Ui.Label(I18n.T("import.title"), Ui.Title));
            var intro = Ui.Label(I18n.T("import.intro"), Ui.Font, Ui.Muted);
            intro.MaximumSize = new Size(Ui.S(520), 0);
            t.Controls.Add(intro);

            _source = new ThemedCombo { Width = Ui.S(400), Margin = Ui.P(0, 10, 0, 4) };
            foreach (var s in sources) _source.Items.Add(s);
            t.Controls.Add(_source);
            _info = Ui.Label("", Ui.Small, Ui.Muted);
            _info.MaximumSize = new Size(Ui.S(520), 0);
            t.Controls.Add(_info);

            _projects = new CheckBox { Text = I18n.T("import.projects"), AutoSize = true, Checked = true, Font = Ui.Bold, Margin = Ui.P(0, 12, 0, 2) };
            t.Controls.Add(_projects);
            _inPlace = new RadioButton { Text = I18n.T("import.inPlace"), AutoSize = true, Checked = true, Margin = Ui.P(20, 2, 0, 0) };
            _copy = new RadioButton { Text = I18n.T("import.copy", Settings.Current.DocumentRoot), AutoSize = true, Margin = Ui.P(20, 2, 0, 0) };
            t.Controls.Add(_inPlace);
            t.Controls.Add(_copy);
            _projects.CheckedChanged += (s, e) => { _inPlace.Enabled = _copy.Enabled = _projects.Checked; };

            _databases = new CheckBox { Text = I18n.T("import.databases"), AutoSize = true, Checked = true, Font = Ui.Bold, Margin = Ui.P(0, 12, 0, 2) };
            t.Controls.Add(_databases);
            var pw = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.P(20, 2, 0, 0) };
            pw.Controls.Add(Ui.Label(I18n.T("import.password")));
            _password = new TextBox { Width = Ui.S(150), UseSystemPasswordChar = true };
            pw.Controls.Add(_password);
            _list = Ui.Button(I18n.T("import.list"), async (s, e) => await LoadDatabases(), false, "database");
            _list.Margin = Ui.P(8, 0, 0, 0);
            pw.Controls.Add(_list);
            t.Controls.Add(pw);
            _dbs = new CheckedListBox { Width = Ui.S(500), Height = Ui.S(150), CheckOnClick = true, Margin = Ui.P(20, 6, 0, 0), BorderStyle = BorderStyle.FixedSingle };
            t.Controls.Add(_dbs);
            var note = Ui.Label(I18n.T("import.dbNote"), Ui.Small, Ui.Muted);
            note.MaximumSize = new Size(Ui.S(500), 0);
            note.Margin = Ui.P(20, 4, 0, 0);
            t.Controls.Add(note);
            _databases.CheckedChanged += (s, e) => { _dbs.Enabled = _password.Enabled = _list.Enabled = _databases.Checked; };

            _progress = new ProgressBar { Width = Ui.S(520), Height = Ui.S(6), Style = ProgressBarStyle.Marquee, Visible = false, Margin = Ui.P(0, 12, 0, 0) };
            t.Controls.Add(_progress);
            _status = Ui.Label("", Ui.Font, Ui.Muted);
            _status.MaximumSize = new Size(Ui.S(520), 0);
            t.Controls.Add(_status);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.P(0, 12, 0, 0) };
            _close = Ui.Button(I18n.T("common.close"), (s, e) => { if (!_busy) Close(); });
            _close.Margin = Padding.Empty;
            _start = Ui.Button(I18n.T("import.start"), async (s, e) => await Run(), true, "update");
            buttons.Controls.Add(_close);
            buttons.Controls.Add(_start);
            t.Controls.Add(buttons);
            Controls.Add(t);

            _source.SelectedIndexChanged += (s, e) => ShowSource();
            _source.SelectedIndex = 0;
        }

        ImportSource Src { get { return (ImportSource)_source.SelectedItem; } }

        void ShowSource()
        {
            var s = Src;
            _info.Text = I18n.T("import.info", s.ProjectsDir, s.ProjectCount, s.MysqlBin == null ? "-" : "MySQL " + s.MysqlVersion, s.MysqlData ?? "-");
            _databases.Enabled = s.MysqlBin != null;
            if (s.MysqlBin == null) _databases.Checked = false;
            _dbs.Items.Clear();
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            _progress.Visible = busy;
            _start.Enabled = _list.Enabled = _source.Enabled = !busy;
        }

        void Status(string text) { if (!IsDisposed) BeginInvoke((Action)(() => _status.Text = text)); }

        async Task LoadDatabases()
        {
            SetBusy(true);
            try
            {
                var list = await Importer.ListDatabasesAsync(Src, _password.Text, Status);
                _dbs.Items.Clear();
                foreach (var d in list) _dbs.Items.Add(d.Name + "   (" + Ui.FormatBytes(d.Bytes) + ")", true);
                _dbs.Tag = list;
                _status.Text = I18n.T("import.found", list.Count);
            }
            catch (Exception ex) { _status.Text = ""; Ui.Error(this, I18n.T("import.listFailed") + "\n\n" + ex.Message); }
            SetBusy(false);
        }

        async Task Run()
        {
            var src = Src;
            if (_databases.Checked && _dbs.Tag == null) await LoadDatabases();
            var all = _dbs.Tag as List<Importer.DbInfo> ?? new List<Importer.DbInfo>();
            var names = new List<string>();
            for (int i = 0; i < _dbs.Items.Count; i++) if (_dbs.GetItemChecked(i)) names.Add(all[i].Name);
            if (!_projects.Checked && names.Count == 0) return;
            SetBusy(true);
            var messages = new List<string>();
            try
            {
                if (_projects.Checked)
                {
                    if (_inPlace.Checked) { Importer.UseProjectsInPlace(src); messages.Add(I18n.T("import.projectsLinked", src.ProjectsDir)); }
                    else
                    {
                        var n = await Task.Run(() => Importer.CopyProjects(src, Status, CancellationToken.None));
                        messages.Add(I18n.T("import.projectsCopied", n, Settings.Current.DocumentRoot));
                    }
                    ProjectsChanged = true;
                }
                if (_databases.Checked && names.Count > 0)
                    messages.Add(await Importer.ImportDatabasesAsync(src, _password.Text, names, Status, CancellationToken.None));
                _status.Text = "";
                Ui.Info(this, string.Join("\n", messages));
                DialogResult = DialogResult.OK;
                SetBusy(false);
                Close();
            }
            catch (Exception ex)
            {
                SetBusy(false);
                _status.Text = "";
                Ui.Error(this, ex.Message);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }
}
