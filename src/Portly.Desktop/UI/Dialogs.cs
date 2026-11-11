using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Portly.Core;

namespace Portly.Desktop.UI
{
    /// <summary>
    /// Base for every Portly dialog window: palette, icon, centered on the owner, Esc closes,
    /// content area + right-aligned button bar. Subclasses fill Body and Buttons.
    /// Show with: await dlg.ShowDialog(owner) (or ShowDialog&lt;T&gt; with Close(result)).
    /// Each dialog may offer "public static Window Preview()" for screenshot mode (--dialog TypeName).
    /// </summary>
    public class PortlyDialog : Window
    {
        protected readonly StackPanel Body = new StackPanel { Spacing = 12 };
        protected readonly StackPanel Buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        readonly ScrollViewer _scroll;

        public PortlyDialog(string title, double width = 560)
        {
            Title = title;
            Icon = Ui.AppIcon;
            Width = width;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 820;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = I18n.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            _scroll = new ScrollViewer { Content = Body, Padding = new Thickness(24, 20, 24, 8) };
            var bar = new Border { Child = Buttons, Padding = new Thickness(24, 12, 24, 18) };
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom);
            dock.Children.Add(bar);
            dock.Children.Add(_scroll);
            Content = dock;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        /// <summary>Resizable dialog with a fixed size (log viewers, editors).</summary>
        protected void MakeResizable(double height)
        {
            SizeToContent = SizeToContent.Manual;
            Height = height;
            CanResize = true;
        }
    }

    public enum MessageKind { Info, Warning, Question }

    /// <summary>
    /// Message / question / text input inside Portly's own window style (never the OS message box).
    /// The first paragraph of the text is the heading, the rest the body.
    /// A question focuses "No" first, like the Windows app, so Enter never confirms a delete by accident.
    /// </summary>
    public class MessageDialog : PortlyDialog
    {
        MessageDialog(string text, MessageKind kind, bool question) : base("Portly", 460)
        {
            var parts = (text ?? "").Replace("\r\n", "\n").Split(new[] { "\n\n" }, 2, StringSplitOptions.None);
            var icon = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Top,
                Background = kind == MessageKind.Warning ? Ui.Orange : Ui.Accent,
                Child = new TextBlock
                {
                    Text = kind == MessageKind.Warning ? "!" : kind == MessageKind.Question ? "?" : "i",
                    Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 20,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var texts = Ui.Column(10, Ui.Selectable(parts[0].Trim(), "subtitle"));
            if (parts.Length > 1 && parts[1].Trim() != "") texts.Children.Add(Ui.Selectable(parts[1].Trim()));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
            grid.Children.Add(icon);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            Body.Children.Add(grid);

            if (question)
            {
                var yes = Ui.Button(I18n.T("common.yes"), () => Close(true), "primary");
                var no = Ui.Button(I18n.T("common.no"), () => Close(false));
                yes.MinWidth = no.MinWidth = 88;
                Buttons.Children.Add(yes);
                Buttons.Children.Add(no);
                Opened += (s, e) => no.Focus();
            }
            else
            {
                var ok = Ui.Button(I18n.T("common.ok"), () => Close(true), "primary");
                ok.MinWidth = 88;
                Buttons.Children.Add(ok);
                Opened += (s, e) => ok.Focus();
            }
        }

        public static async Task<bool> Show(Window owner, string text, MessageKind kind, bool question)
        {
            var d = new MessageDialog(text, kind, question);
            if (owner == null || !owner.IsVisible)
            {
                var tcs = new TaskCompletionSource<bool>();
                d.Closed += (s, e) => tcs.TrySetResult(false);
                d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                d.Show();
                return await tcs.Task;
            }
            return await d.ShowDialog<bool>(owner);
        }

        public static Window Preview() { return new MessageDialog("Delete the database \"shop\"?\n\nThis cannot be undone.", MessageKind.Question, true); }
    }

    public static class Dialogs
    {
        public static Task Info(Window owner, string text) { return MessageDialog.Show(owner, text, MessageKind.Info, false); }
        public static Task Warning(Window owner, string text) { return MessageDialog.Show(owner, text, MessageKind.Warning, false); }
        public static Task<bool> Confirm(Window owner, string text) { return MessageDialog.Show(owner, text, MessageKind.Question, true); }

        /// <summary>One line of text from the user; null when cancelled.</summary>
        public static async Task<string> Prompt(Window owner, string title, string label, string value = "", bool password = false)
        {
            var d = new PromptDialog(title, label, value, password);
            return owner == null ? null : await d.ShowDialog<string>(owner);
        }

        sealed class PromptDialog : PortlyDialog
        {
            public PromptDialog(string title, string label, string value, bool password) : base(title, 440)
            {
                var box = new TextBox { Text = value ?? "", PasswordChar = password ? '•' : default(char) };
                Body.Children.Add(Ui.Field(label, box));
                var ok = Ui.Button(I18n.T("common.ok"), () => Close(box.Text), "primary");
                Buttons.Children.Add(ok);
                Buttons.Children.Add(Ui.Button(I18n.T("common.cancel"), () => Close(null)));
                box.KeyDown += (s, e) => { if (e.Key == Key.Enter) Close(box.Text); };
                Opened += (s, e) => { box.Focus(); box.SelectAll(); };
            }
        }
    }
}
