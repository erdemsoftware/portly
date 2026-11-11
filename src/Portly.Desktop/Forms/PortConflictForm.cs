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

namespace Portly.Desktop.Forms
{
    public enum PortChoice { Cancel, UseFreePorts, CloseOwners }

    /// <summary>Shown when ports are taken: who holds them, and two ways out.</summary>
    public class PortConflictForm : PortlyDialog
    {
        public PortChoice Choice { get; private set; }

        public static async Task<PortChoice> Show(Window owner, List<PortConflict> conflicts)
        {
            var f = new PortConflictForm(conflicts);
            await f.ShowDialog(owner);
            return f.Choice;
        }

        public PortConflictForm(List<PortConflict> conflicts) : base(I18n.T("port.title"), 580)
        {
            var head = Ui.Row(new Glyph { Name_ = "warning", Color = Ui.Orange, Width = 28, Height = 28 }, Ui.Label(I18n.T("port.title"), "subtitle"));
            head.Spacing = 10;
            Body.Children.Add(head);
            Body.Children.Add(Ui.Label(I18n.T("port.intro"), "muted"));

            foreach (var c in conflicts)
            {
                var title = Ui.Label(I18n.T("port.row", c.Service.Title + " · " + c.Label, c.Port), "bold");
                var who = Ui.Label(I18n.T("port.usedBy", c.Owner.Pid == 0 ? I18n.T("port.unknownProc") : c.Owner.ToString()));
                var path = Ui.Selectable(c.Owner.IsSystem ? I18n.T("port.system") : c.Owner.Path, "small");
                path.Foreground = c.Owner.IsSystem ? Ui.Orange : Ui.Muted;
                if (!c.Owner.IsSystem) { path.TextWrapping = TextWrapping.NoWrap; path.TextTrimming = TextTrimming.CharacterEllipsis; }
                var sug = Ui.Label(c.Suggested > 0 ? I18n.T("port.suggest", c.Suggested) : I18n.T("port.noFree"), "small", Ui.Green);
                Body.Children.Add(Ui.Card(Ui.Column(4, title, who, path, sug), new Thickness(14, 10)));
            }

            var kill = Ui.Button(I18n.T("port.closeApps"), async () =>
            {
                var names = string.Join(", ", conflicts.Select(c => c.Owner.ToString()).Distinct());
                if (!await Dialogs.Confirm(this, I18n.T("port.closeConfirm", names))) return;
                Choice = PortChoice.CloseOwners;
                Close();
            });
            kill.IsEnabled = conflicts.All(c => !c.Owner.IsSystem && c.Owner.Pid > 0);
            var free = Ui.Button(I18n.T("port.useFree"), () => { Choice = PortChoice.UseFreePorts; Close(); }, "primary");
            free.IsEnabled = conflicts.All(c => c.Suggested > 0);
            free.IsDefault = true;
            var cancel = Ui.Button(I18n.T("common.cancel"), () => { Choice = PortChoice.Cancel; Close(); });
            Buttons.Children.Add(kill);
            Buttons.Children.Add(free);
            Buttons.Children.Add(cancel);
        }

        public static Window Preview()
        {
            return new PortConflictForm(new List<PortConflict>
            {
                new PortConflict
                {
                    Service = ServiceManager.Apache, Key = "HttpPort", Label = "HTTP", Port = 80, Suggested = 8080,
                    Owner = new PortOwnerInfo { Pid = 5120, Name = "nginx.exe", Path = @"C:\tools\nginx-1.27\nginx.exe" },
                },
                new PortConflict
                {
                    Service = ServiceManager.Apache, Key = "HttpsPort", Label = "HTTPS", Port = 443, Suggested = 8443,
                    Owner = new PortOwnerInfo { Pid = 4, Name = "System" },
                },
            });
        }
    }
}
