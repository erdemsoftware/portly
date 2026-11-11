using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Tools;

namespace Portly.Desktop.Forms
{
    /// <summary>Move projects and databases from Laragon, XAMPP and similar.</summary>
    public class ImportForm : PortlyDialog
    {
        readonly List<ImportSource> _sources;
        readonly ComboBox _source;
        readonly TextBlock _info, _status;
        readonly CheckBox _projects, _databases;
        readonly RadioButton _inPlace, _copy;
        readonly StackPanel _dbs = new StackPanel { Spacing = 2 };
        readonly List<CheckBox> _dbChecks = new List<CheckBox>();
        List<Importer.DbInfo> _dbList;
        readonly TextBox _password;
        readonly Button _list, _start, _close;
        readonly ProgressBar _progress;
        bool _busy;
        public bool ProjectsChanged { get; private set; }

        /// <summary>Shows the dialog; returns true when projects were moved (the projects list must be reloaded).</summary>
        public static async Task<bool> Ask(Window owner, List<ImportSource> sources)
        {
            var f = new ImportForm(sources);
            await f.ShowDialog(owner);
            return f.ProjectsChanged;
        }

        public ImportForm(List<ImportSource> sources) : base(I18n.T("import.title"), 620)
        {
            _sources = sources;
            Body.Spacing = 6;
            Body.Children.Add(Ui.Label(I18n.T("import.title"), "title"));
            Body.Children.Add(Ui.Label(I18n.T("import.intro"), "muted"));

            _source = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var s in sources) _source.Items.Add(s);
            Body.Children.Add(_source);
            _info = Ui.Label("", "muted small");
            Body.Children.Add(_info);

            _projects = new CheckBox { IsChecked = true, Margin = new Thickness(0, 10, 0, 0), Content = Ui.Label(I18n.T("import.projects"), "bold") };
            _inPlace = new RadioButton { GroupName = "importMode", Content = I18n.T("import.inPlace"), IsChecked = true, Margin = new Thickness(26, 0, 0, 0) };
            _copy = new RadioButton { GroupName = "importMode", Content = I18n.T("import.copy", Settings.Current.DocumentRoot), Margin = new Thickness(26, 0, 0, 0) };
            Body.Children.Add(_projects);
            Body.Children.Add(_inPlace);
            Body.Children.Add(_copy);
            _projects.IsCheckedChanged += (s, e) => { _inPlace.IsEnabled = _copy.IsEnabled = _projects.IsChecked == true; };

            _databases = new CheckBox { IsChecked = true, Margin = new Thickness(0, 10, 0, 0), Content = Ui.Label(I18n.T("import.databases"), "bold") };
            Body.Children.Add(_databases);
            _password = new TextBox { Width = 160, PasswordChar = '•' };
            _list = Ui.Button(I18n.T("import.list"), LoadDatabases, null, "database");
            var pw = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 0, 0, 0) };
            foreach (Control c in new Control[] { Ui.Label(I18n.T("import.password")), _password, _list })
            {
                c.VerticalAlignment = VerticalAlignment.Center;
                c.Margin = new Thickness(0, 0, 8, 4);
                pw.Children.Add(c);
            }
            Body.Children.Add(pw);
            var box = Ui.Card(new ScrollViewer { Content = _dbs }, new Thickness(10, 8));
            box.Height = 150;
            box.Margin = new Thickness(26, 4, 0, 0);
            Body.Children.Add(box);
            var note = Ui.Label(I18n.T("import.dbNote"), "muted small");
            note.Margin = new Thickness(26, 2, 0, 0);
            Body.Children.Add(note);
            _databases.IsCheckedChanged += (s, e) => { _dbs.IsEnabled = _password.IsEnabled = _list.IsEnabled = _databases.IsChecked == true; };

            _progress = new ProgressBar { IsIndeterminate = true, Height = 6, IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };
            Body.Children.Add(_progress);
            _status = Ui.Label("", null, Ui.Muted);
            Body.Children.Add(_status);

            _start = Ui.Button(I18n.T("import.start"), Run, "primary", "update");
            _close = Ui.Button(I18n.T("common.close"), () => { if (!_busy) Close(); });
            Buttons.Children.Add(_start);
            Buttons.Children.Add(_close);
            Closing += (s, e) => { if (_busy) e.Cancel = true; };

            _source.SelectionChanged += (s, e) => ShowSource();
            _source.SelectedIndex = 0;
        }

        ImportSource Src { get { return _source.SelectedItem as ImportSource; } }

        void ShowSource()
        {
            var s = Src;
            if (s == null) return;
            _info.Text = I18n.T("import.info", s.ProjectsDir, s.ProjectCount, s.MysqlBin == null ? "-" : "MySQL " + s.MysqlVersion, s.MysqlData ?? "-");
            _databases.IsEnabled = s.MysqlBin != null;
            if (s.MysqlBin == null) _databases.IsChecked = false;
            ClearDbs();
        }

        void ClearDbs()
        {
            _dbs.Children.Clear();
            _dbChecks.Clear();
            _dbList = null;
        }

        void SetDbs(List<Importer.DbInfo> list)
        {
            ClearDbs();
            foreach (var d in list)
            {
                var c = new CheckBox { Content = new TextBlock { Text = d.Name + "   (" + Ui.FormatBytes(d.Bytes) + ")" }, IsChecked = true };
                _dbChecks.Add(c);
                _dbs.Children.Add(c);
            }
            _dbList = list;
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            _progress.IsVisible = busy;
            _start.IsEnabled = _list.IsEnabled = _source.IsEnabled = !busy;
        }

        void Status(string text) { Ui.Post(() => _status.Text = text); }

        async Task LoadDatabases()
        {
            SetBusy(true);
            try
            {
                var list = await Importer.ListDatabasesAsync(Src, _password.Text, Status);
                SetDbs(list);
                _status.Text = I18n.T("import.found", list.Count);
            }
            catch (Exception ex) { _status.Text = ""; await Dialogs.Warning(this, I18n.T("import.listFailed") + "\n\n" + ex.Message); }
            SetBusy(false);
        }

        async Task Run()
        {
            var src = Src;
            if (_databases.IsChecked == true && _dbList == null) await LoadDatabases();
            var all = _dbList ?? new List<Importer.DbInfo>();
            var names = new List<string>();
            for (int i = 0; i < _dbChecks.Count; i++) if (_dbChecks[i].IsChecked == true) names.Add(all[i].Name);
            if (_projects.IsChecked != true && names.Count == 0) return;
            SetBusy(true);
            var messages = new List<string>();
            try
            {
                if (_projects.IsChecked == true)
                {
                    if (_inPlace.IsChecked == true) { Importer.UseProjectsInPlace(src); messages.Add(I18n.T("import.projectsLinked", src.ProjectsDir)); }
                    else
                    {
                        var n = await Task.Run(() => Importer.CopyProjects(src, Status, CancellationToken.None));
                        messages.Add(I18n.T("import.projectsCopied", n, Settings.Current.DocumentRoot));
                    }
                    ProjectsChanged = true;
                }
                if (_databases.IsChecked == true && names.Count > 0)
                    messages.Add(await Importer.ImportDatabasesAsync(src, _password.Text, names, Status, CancellationToken.None));
                _status.Text = "";
                SetBusy(false);
                await Dialogs.Info(this, string.Join("\n", messages));
                Close(true);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                _status.Text = "";
                await Dialogs.Warning(this, ex.Message);
            }
        }

        public static Window Preview()
        {
            var laragon = new ImportSource
            {
                Kind = "laragon", Product = "Laragon", Root = @"C:\laragon", ProjectsDir = @"C:\laragon\www", ProjectCount = 14,
                MysqlBin = @"C:\laragon\bin\mysql\mysql-8.0.30-winx64\bin", MysqlData = @"C:\laragon\data\mysql-8.0", MysqlVersion = "8.0.30",
            };
            var xampp = new ImportSource { Kind = "xampp", Product = "XAMPP", Root = @"C:\xampp", ProjectsDir = @"C:\xampp\htdocs", ProjectCount = 5 };
            var f = new ImportForm(new List<ImportSource> { laragon, xampp });
            f.SetDbs(new List<Importer.DbInfo>
            {
                new Importer.DbInfo { Name = "shop", Bytes = 48 * 1024 * 1024 },
                new Importer.DbInfo { Name = "wp_blog", Bytes = 12 * 1024 * 1024 },
                new Importer.DbInfo { Name = "crm", Bytes = 230 * 1024 * 1024 },
            });
            return f;
        }
    }
}
