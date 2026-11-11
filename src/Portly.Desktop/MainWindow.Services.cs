using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Portly.Core;
using Portly.Desktop.UI;
using Portly.Services;

namespace Portly.Desktop
{
    public partial class MainWindow
    {
        sealed class Row
        {
            public Service Svc;
            public TextBlock Detail, Error;
            public Pill Status;
            public ToggleSwitch Toggle;
        }

        readonly Dictionary<Service, Row> _rows = new Dictionary<Service, Row>();
        TextBlock _servicesSub;
        Button _startAll;
        bool _updating, _busyAll;

        Control BuildServicesPage()
        {
            _rows.Clear();
            var page = new StackPanel { Spacing = 14 };
            _startAll = Ui.Button(I18n.T("main.startAll"), ToggleAll, "primary", "play");
            _startAll.MinWidth = 170;
            page.Children.Add(Ui.PageHeader(I18n.T("nav.services"), out _servicesSub, _startAll));

            page.Children.Add(Ui.Section(I18n.T("main.quickAccess")));
            var tiles = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 10 };
            tiles.Children.Add(Tile(I18n.T("main.web"), "globe", () => Ui.Open("http://localhost/")));
            tiles.Children.Add(Tile(I18n.T("main.database"), "database", null));
            tiles.Children.Add(Tile(I18n.T("main.mail"), "mail", null));
            tiles.Children.Add(Tile(I18n.T("main.terminal"), "terminal", null));
            tiles.Children.Add(Tile(I18n.T("main.root"), "folder", () => Ui.Open(Settings.Current.DocumentRoot)));
            tiles.Children.Add(Tile(I18n.T("main.newProject"), "plus", async () => await NewProject()));
            page.Children.Add(tiles);

            foreach (var svc in ServiceManager.Rows) page.Children.Add(ServiceCard(svc));
            return page;
        }

        static Button Tile(string text, string glyph, Func<Task> click)
        {
            var b = Ui.Button(text, click, "tile", glyph);
            return b;
        }

        static Button Tile(string text, string glyph, Action click)
        {
            return Tile(text, glyph, click == null ? null : (Func<Task>)(() => { click(); return Task.CompletedTask; }));
        }

        Control ServiceCard(Service svc)
        {
            var row = new Row { Svc = svc };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 14 };
            grid.Children.Add(new BrandTile(svc.PackageType, svc.Title));

            row.Detail = Ui.Label("", "muted small");
            row.Error = Ui.Label("", "error small");
            row.Error.IsVisible = false;
            var text = Ui.Column(2, Ui.Label(svc.Title, "subtitle"), row.Detail, row.Error);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            row.Status = new Pill();
            Grid.SetColumn(row.Status, 2);
            grid.Children.Add(row.Status);

            row.Toggle = new ToggleSwitch { OnContent = null, OffContent = null, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            Avalonia.Automation.AutomationProperties.SetName(row.Toggle, svc.Title);
            row.Toggle.IsCheckedChanged += async (s, e) => await OnToggle(row);
            Grid.SetColumn(row.Toggle, 3);
            grid.Children.Add(row.Toggle);

            _rows[svc] = row;
            UpdateRow(svc);
            return Ui.Card(grid);
        }

        async Task OnToggle(Row row)
        {
            if (_updating) return;
            bool on = row.Toggle.IsChecked == true;
            try
            {
                if (on) await ServiceManager.StartAsync(row.Svc);
                else await ServiceManager.StopAsync(row.Svc);
            }
            catch (Exception ex) { Log.Error("Toggle " + row.Svc.Id, ex); }
            UpdateRow(row.Svc);
        }

        /// <summary>shared: version lists on the cards after packages were installed / removed.</summary>
        public void RefreshVersions() { }

        /// <summary>shared: check the services' ports before starting; false = the user cancelled.</summary>
        public Task<bool> ResolvePorts(IEnumerable<Service> services) { return Task.FromResult(true); }

        /// <summary>shared: "Fix" link on a port error.</summary>
        public Task FixPorts(IEnumerable<Service> services) { return Task.CompletedTask; }

        /// <summary>shared: Start all / Stop all (button, tray menu).</summary>
        public async Task ToggleAll()
        {
            if (_busyAll) return;
            _busyAll = true;
            try
            {
                if (ServiceManager.AnyBulkActive) await ServiceManager.StopAllAsync();
                else await ServiceManager.StartAllAsync();
            }
            finally
            {
                _busyAll = false;
                UpdateGlobal();
            }
        }

        /// <summary>shared: refresh one service card (state, version, error).</summary>
        public void UpdateRow(Service svc)
        {
            Row row;
            if (svc == null || !_rows.TryGetValue(svc, out row)) return;
            _updating = true;
            try
            {
                var parts = new List<string>();
                parts.Add(svc.IsInstalled ? svc.VersionLabel : I18n.T("svc.notInstalled"));
                if (svc.PortText != "") parts.Add(svc.PortText);
                row.Detail.Text = string.Join("  ·  ", parts);

                switch (svc.State)
                {
                    case ServiceState.Running: row.Status.Set(I18n.T("svc.running"), Ui.Green); break;
                    case ServiceState.Starting: row.Status.Set(I18n.T("svc.starting"), Ui.Orange); break;
                    case ServiceState.Stopping: row.Status.Set(I18n.T("svc.stopping"), Ui.Orange); break;
                    case ServiceState.Error: row.Status.Set(I18n.T("svc.error"), Ui.Red); break;
                    default: row.Status.Set(I18n.T("svc.stopped"), Ui.Gray); break;
                }
                row.Error.Text = svc.State == ServiceState.Error ? svc.LastError : "";
                row.Error.IsVisible = svc.State == ServiceState.Error && !string.IsNullOrEmpty(svc.LastError);
                row.Toggle.IsChecked = svc.IsActive;
                row.Toggle.IsEnabled = svc.IsInstalled && svc.State != ServiceState.Starting && svc.State != ServiceState.Stopping;
            }
            finally { _updating = false; }
        }

        /// <summary>shared: header subtitle and the Start all / Stop all button.</summary>
        public void UpdateGlobal()
        {
            if (_servicesSub == null) return;
            int running = _rows.Keys.Count(s => s.State == ServiceState.Running);
            int installed = _rows.Keys.Count(s => s.IsInstalled);
            _servicesSub.Text = running + " / " + installed + " " + I18n.T("svc.running").ToLowerInvariant();
            var stop = ServiceManager.AnyBulkActive;
            var label = I18n.T(stop ? "main.stopAll" : "main.startAll");
            _startAll.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { new Glyph { Name_ = stop ? "stop" : "play", VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } },
            };
            Avalonia.Automation.AutomationProperties.SetName(_startAll, label);
        }
    }
}
