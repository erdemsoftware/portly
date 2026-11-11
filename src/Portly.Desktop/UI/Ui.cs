using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Portly.Core;

namespace Portly.Desktop.UI
{
    /// <summary>
    /// Building blocks shared by every page and dialog. Same names as src\Portly\UI\Ui.cs where possible,
    /// so WinForms code ports line by line. Dialogs are async here (Avalonia has no blocking ShowDialog).
    /// </summary>
    public static class Ui
    {
        // ---------------- colors ----------------
        /// <summary>A palette brush by name: "Accent", "Muted", "Red", "Card", "Line" ... (see Palette).</summary>
        public static IBrush Brush(string name)
        {
            object v;
            if (Application.Current != null && Application.Current.TryGetResource(name, Application.Current.ActualThemeVariant, out v) && v is IBrush)
                return (IBrush)v;
            var f = typeof(Palette).GetField(name);
            return f != null ? new SolidColorBrush((Color)f.GetValue(Theme.Current)) : Brushes.Magenta;
        }

        public static IBrush Accent { get { return Brush("Accent"); } }
        public static IBrush Text { get { return Brush("Text"); } }
        public static IBrush Muted { get { return Brush("Muted"); } }
        public static IBrush Line { get { return Brush("Line"); } }
        public static IBrush Green { get { return Brush("Green"); } }
        public static IBrush Orange { get { return Brush("Orange"); } }
        public static IBrush Red { get { return Brush("Red"); } }
        public static IBrush Gray { get { return Brush("Gray"); } }
        public static IBrush Heart { get { return Brush("Heart"); } }

        // ---------------- text ----------------
        /// <summary>Upper case by the UI language: Turkish "Hızlı erişim" -> "HIZLI ERİŞİM".</summary>
        public static string Upper(string text)
        {
            try { return text.ToUpper(new CultureInfo(I18n.Code)); }
            catch { return text.ToUpperInvariant(); }
        }

        /// <summary>Text with style classes: "title", "subtitle", "bold", "muted", "small", "section", "error", "mono".</summary>
        public static TextBlock Label(string text, string classes = null, IBrush color = null)
        {
            var t = new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap };
            if (!string.IsNullOrEmpty(classes)) t.Classes.AddRange(classes.Split(' '));
            if (color != null) t.Foreground = color;
            return t;
        }

        /// <summary>Upper-case muted heading above a group ("QUICK ACCESS").</summary>
        public static TextBlock Section(string text) { return Label(Upper(text), "section"); }

        /// <summary>Text the user can select and copy (paths, commands, errors).</summary>
        public static SelectableTextBlock Selectable(string text, string classes = null)
        {
            var t = new SelectableTextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap };
            if (!string.IsNullOrEmpty(classes)) t.Classes.AddRange(classes.Split(' '));
            return t;
        }

        // ---------------- buttons ----------------
        /// <summary>
        /// Button with optional glyph (see Glyph.Names). click may be async; exceptions are logged and shown.
        /// kind: null (outlined), "primary", "ghost", "danger".
        /// </summary>
        public static Button Button(string text, Func<Task> click, string kind = null, string glyph = null)
        {
            var b = new Button { Content = ButtonContent(text, glyph) };
            if (!string.IsNullOrEmpty(kind)) b.Classes.AddRange(kind.Split(' '));
            if (!string.IsNullOrEmpty(text)) Avalonia.Automation.AutomationProperties.SetName(b, text);
            if (click != null) b.Click += async (s, e) => await Run(click, b);
            return b;
        }

        public static Button Button(string text, Action click, string kind = null, string glyph = null)
        {
            return Button(text, click == null ? null : (Func<Task>)(() => { click(); return Task.CompletedTask; }), kind, glyph);
        }

        /// <summary>Square ghost button with only a glyph; the text becomes tooltip and screen-reader name.</summary>
        public static Button IconButton(string glyph, string text, Func<Task> click)
        {
            var b = Button(null, click, "ghost", glyph);
            b.Padding = new Thickness(7);
            ToolTip.SetTip(b, text);
            Avalonia.Automation.AutomationProperties.SetName(b, text);
            return b;
        }

        public static Button IconButton(string glyph, string text, Action click)
        {
            return IconButton(glyph, text, () => { click(); return Task.CompletedTask; });
        }

        static object ButtonContent(string text, string glyph)
        {
            if (string.IsNullOrEmpty(glyph)) return text;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(new Glyph { Name_ = glyph, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrEmpty(text)) row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        /// <summary>Text link (WinForms LinkLabel).</summary>
        public static Button Link(string text, Action click)
        {
            var b = new Button { Content = text };
            b.Classes.Add("link");
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        /// <summary>Run a click handler: one at a time per button, errors go to the log and a warning dialog.</summary>
        public static async Task Run(Func<Task> action, Control source = null)
        {
            if (source != null && source.Tag as string == "busy") return;
            if (source != null) source.Tag = "busy";
            try { await action(); }
            catch (Exception ex)
            {
                Log.Error("UI action", ex);
                await Dialogs.Warning(OwnerOf(source), I18n.T("err.unexpected") + "\n\n" + ex.Message);
            }
            finally { if (source != null) source.Tag = null; }
        }

        // ---------------- layout ----------------
        public static Border Card(Control child, Thickness? padding = null)
        {
            var b = new Border { Child = child };
            b.Classes.Add("card");
            if (padding.HasValue) b.Padding = padding.Value;
            return b;
        }

        public static StackPanel Row(params Control[] children)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var c in children) if (c != null) p.Children.Add(c);
            return p;
        }

        public static StackPanel Column(double spacing, params Control[] children)
        {
            var p = new StackPanel { Spacing = spacing };
            foreach (var c in children) if (c != null) p.Children.Add(c);
            return p;
        }

        /// <summary>Page title, muted subtitle under it, action buttons on the far side.</summary>
        public static Control PageHeader(string title, out TextBlock subtitle, params Control[] actions)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
            subtitle = Label("", "muted");
            subtitle.TextWrapping = TextWrapping.NoWrap;
            subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            grid.Children.Add(Column(2, Label(title, "title"), subtitle));
            var row = Row(actions);
            row.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(row, 1);
            grid.Children.Add(row);
            return grid;
        }

        /// <summary>Label above an input, as in the settings forms.</summary>
        public static Control Field(string label, Control input, string hint = null)
        {
            var c = Column(4, Label(label, "bold"), input);
            if (!string.IsNullOrEmpty(hint)) c.Children.Add(Label(hint, "muted small"));
            return c;
        }

        // ---------------- misc ----------------
        public static string FormatBytes(long b) { return Units.Bytes(b); }

        static Bitmap _appImage;

        public static Bitmap AppImage
        {
            get
            {
                if (_appImage != null) return _appImage;
                using (var s = typeof(Ui).Assembly.GetManifestResourceStream("portly.png"))
                    return _appImage = s == null ? null : new Bitmap(s);
            }
        }

        public static WindowIcon AppIcon { get { return AppImage == null ? null : new WindowIcon(AppImage); } }

        /// <summary>Window that owns a control (for dialogs).</summary>
        public static Window OwnerOf(Control c)
        {
            return c == null ? MainWindow.Instance : (TopLevel.GetTopLevel(c) as Window ?? MainWindow.Instance);
        }

        /// <summary>Open a folder, file or web address with the system's default app.</summary>
        public static void Open(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            try
            {
                if (OS.IsWindows) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                else if (OS.IsMac) Process.Start("open", new[] { target });
                else Process.Start("xdg-open", new[] { target });
            }
            catch (Exception ex) { Log.Warn("Open " + target + ": " + ex.Message); }
        }

        /// <summary>Copy text to the clipboard of the window that owns the control.</summary>
        public static async Task Copy(Control from, string text)
        {
            var top = TopLevel.GetTopLevel(from) ?? MainWindow.Instance;
            if (top != null && top.Clipboard != null) await top.Clipboard.SetTextAsync(text);
        }

        /// <summary>Run on the UI thread (events from services arrive on other threads).</summary>
        public static void Post(Action a) { Dispatcher.UIThread.Post(a); }
    }
}
