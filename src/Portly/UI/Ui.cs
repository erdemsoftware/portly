using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Portly.Core;

namespace Portly.UI
{
    /// <summary>Shared look: theme colors, fonts, DPI scaling and small control factories.</summary>
    public static class Ui
    {
        // colors come from the active theme (light / dark)
        public static Color Accent { get { return Theme.Current.Accent; } }
        public static Color AccentDark { get { return Theme.Current.AccentHover; } }
        public static Color Bg { get { return Theme.Current.Bg; } }
        public static Color Card { get { return Theme.Current.Card; } }
        public static Color Line { get { return Theme.Current.Border; } }
        public static Color Text { get { return Theme.Current.Text; } }
        public static Color Muted { get { return Theme.Current.Muted; } }
        public static Color Green { get { return Theme.Current.Green; } }
        public static Color Orange { get { return Theme.Current.Orange; } }
        public static Color Red { get { return Theme.Current.Red; } }
        public static Color Gray { get { return Theme.Current.Gray; } }
        public static Color Heart { get { return Theme.Current.Heart; } }

        // Fonts follow the DPI of the monitor the main window is on (see SetDpi). Sizes are in points at the
        // system DPI, so on a monitor with another DPI they are multiplied by Dpi / SystemDpi.
        public static Font Font { get { return ScaledFont("Segoe UI", 9f); } }
        public static Font Bold { get { return ScaledFont("Segoe UI Semibold", 9.5f); } }
        public static Font Small { get { return ScaledFont("Segoe UI", 8.25f); } }
        public static Font Title { get { return ScaledFont("Segoe UI Semibold", 16f); } }
        public static Font Brand { get { return ScaledFont("Segoe UI Semibold", 14f); } }
        public static Font Subtitle { get { return ScaledFont("Segoe UI Semibold", 10.5f); } }
        public static Font Mono { get { return ScaledFont("Consolas", 9.5f); } }

        /// <summary>A font of the given point size at the current DPI (for custom controls).</summary>
        public static Font ScaledFont(string family, float points, FontStyle style = FontStyle.Regular)
        {
            var key = family + "|" + points + "|" + (int)style + "|" + Dpi;
            Font f;
            lock (FontCache)
            {
                if (!FontCache.TryGetValue(key, out f))
                    FontCache[key] = f = new Font(family, points * Dpi / SystemDpi, style);
            }
            return f;
        }

        // fonts stay alive for the whole run: controls of older windows may still use them
        static readonly System.Collections.Generic.Dictionary<string, Font> FontCache = new System.Collections.Generic.Dictionary<string, Font>();

        static int _systemDpi;
        /// <summary>DPI that GDI uses for point sizes (the primary monitor's when Portly started).</summary>
        public static int SystemDpi
        {
            get
            {
                if (_systemDpi == 0)
                {
                    try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) _systemDpi = (int)Math.Round(g.DpiX); }
                    catch { _systemDpi = 96; }
                    if (_systemDpi <= 0) _systemDpi = 96;
                }
                return _systemDpi;
            }
        }

        static int _dpi;
        /// <summary>DPI of the monitor the main window is on (per-monitor V2 aware on Windows 10 1703+).</summary>
        public static int Dpi { get { return _dpi == 0 ? SystemDpi : _dpi; } }
        public static float Scale { get { return Dpi / 96f; } }

        /// <summary>Switch sizes and fonts to another monitor's DPI. Returns false when nothing changed.</summary>
        public static bool SetDpi(int dpi)
        {
            if (dpi <= 0 || dpi == Dpi) return false;
            _dpi = dpi;
            return true;
        }

        /// <summary>The first solid background color up the parent chain (for painting outside rounded corners).</summary>
        public static Color BackOf(Control c)
        {
            for (var p = c; p != null; p = p.Parent)
                if (p.BackColor.A == 255) return p.BackColor;
            return Theme.Current.Bg;
        }

        /// <summary>Upper case in the UI language (Turkish i -> İ).</summary>
        public static string Upper(string text)
        {
            try { return text.ToUpper(new System.Globalization.CultureInfo(I18n.Code)); }
            catch { return text.ToUpperInvariant(); }
        }

        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static Size S(int w, int h) { return new Size(S(w), S(h)); }
        public static Padding P(int all) { return new Padding(S(all)); }
        public static Padding P(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }

        static Icon _icon;
        public static Icon AppIcon
        {
            get
            {
                if (_icon == null)
                {
                    try { using (var s = typeof(Ui).Assembly.GetManifestResourceStream("portly.ico")) _icon = new Icon(s); }
                    catch { _icon = SystemIcons.Application; }
                }
                return _icon;
            }
        }

        public static Bitmap AppImage(int size)
        {
            using (var ico = new Icon(AppIcon, size, size)) return ico.ToBitmap();
        }

        /// <summary>Tray icon with a small status dot.</summary>
        public static Icon TrayIcon(Color? dot)
        {
            var size = SystemInformation.SmallIconSize;
            var bmp = AppImage(size.Width);
            if (dot.HasValue)
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    int d = Math.Max(6, size.Width / 2 - 1);
                    var r = new Rectangle(size.Width - d, size.Height - d, d - 1, d - 1);
                    using (var b = new SolidBrush(dot.Value)) g.FillEllipse(b, r);
                    using (var p = new Pen(Color.White, Math.Max(1f, size.Width / 16f))) g.DrawEllipse(p, r);
                }
            }
            var h = bmp.GetHicon();
            bmp.Dispose();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        public static ModernButton Button(string text, EventHandler click, bool primary = false, string glyph = null)
        {
            var b = new ModernButton
            {
                Text = text,
                Kind = primary ? ButtonKind.Primary : ButtonKind.Secondary,
                Glyph = glyph,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = P(14, 6, 14, 6),
                Margin = P(0, 0, 8, 0),
                MinimumSize = new Size(0, S(34)),
                Font = Font,
                AccessibleName = text,
            };
            if (click != null) b.Click += click;
            return b;
        }

        public static Label Label(string text, Font font = null, Color? color = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = font ?? Font,
                ForeColor = color ?? Text,
                BackColor = Color.Transparent,
                Margin = P(0, 4, 6, 4),
            };
        }

        public static LinkLabel Link(string text, Action click)
        {
            var l = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                LinkColor = Accent,
                ActiveLinkColor = AccentDark,
                VisitedLinkColor = Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = Color.Transparent,
                Margin = P(0, 4, 10, 4),
            };
            l.LinkClicked += (s, e) => click();
            return l;
        }

        /// <summary>Theme a whole form: background, text, inputs, dark title bar.</summary>
        public static void Apply(Form f)
        {
            f.Font = Font;
            f.AutoScaleMode = AutoScaleMode.None;
            f.BackColor = Bg;
            f.ForeColor = Text;
            f.Icon = AppIcon;
            ApplyDirection(f);
            f.HandleCreated += (s, e) => Theme.ApplyTitleBar(f);
            f.Load += (s, e) => StyleTree(f);
            // A dialog moved to a monitor with another scale is rescaled by WinForms (per-monitor V2: bounds and
            // fonts). Ui.Scale stays with the main window, which rebuilds itself (MainForm.OnDpiChanged).
            // Esc closes every dialog, also those without a Cancel button
            f.KeyPreview = true;
            f.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape || e.Handled || !f.Modal || f.CancelButton != null) return;
                var combo = f.ActiveControl as ComboBox;
                if (combo != null && combo.DroppedDown) return;
                e.Handled = true;
                f.Close();
            };
        }

        /// <summary>Arabic, Persian, Urdu: mirror the whole window (layout, text alignment, icons).</summary>
        public static void ApplyDirection(Form f)
        {
            f.RightToLeft = I18n.IsRtl ? RightToLeft.Yes : RightToLeft.No;
            f.RightToLeftLayout = I18n.IsRtl;
        }

        /// <summary>Give native inputs theme colors (TextBox, ComboBox, ListView, NumericUpDown ...).</summary>
        public static void StyleTree(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is TextBox || c is ComboBox || c is NumericUpDown || c is ListView || c is RichTextBox || c is ListBox)
                {
                    if (c.Tag as string != "console") { c.BackColor = Theme.Current.Input; c.ForeColor = Text; }
                    var cb = c as ComboBox;
                    if (cb != null && Theme.IsDark) cb.FlatStyle = FlatStyle.Flat;
                    var tb = c as TextBox;
                    if (tb != null && Theme.IsDark) tb.BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is CheckBox && !(c is ToggleSwitch) || c is RadioButton)
                    c.ForeColor = Text;
                StyleTree(c);
            }
        }

        // ---------------- dialogs ----------------
        // Portly's own themed window (MessageDialog), not the plain Windows message box
        public static void Info(IWin32Window owner, string text) { MessageDialog.Info(owner, text); }
        public static void Error(IWin32Window owner, string text) { MessageDialog.Warning(owner, text); }
        public static bool Confirm(IWin32Window owner, string text) { return MessageDialog.Confirm(owner, text); }

        /// <summary>Simple text input. Returns null when cancelled.</summary>
        public static string Prompt(IWin32Window owner, string title, string label, string value = "", bool password = false)
        {
            using (var f = new Form())
            {
                Apply(f);
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.AutoSize = true;
                f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                f.Padding = P(16);
                var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
                layout.Controls.Add(Label(label));
                var box = new TextBox { Text = value, Width = S(340), UseSystemPasswordChar = password, Margin = P(0, 2, 0, 8) };
                layout.Controls.Add(box);
                if (password)
                {
                    var show = new CheckBox { Text = I18n.T("common.show"), AutoSize = true };
                    show.CheckedChanged += (s, e) => box.UseSystemPasswordChar = !show.Checked;
                    layout.Controls.Add(show);
                }
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = P(0, 10, 0, 0) };
                var cancel = Button(I18n.T("common.cancel"), null);
                cancel.DialogResult = DialogResult.Cancel;
                var ok = Button(I18n.T("common.ok"), null, true);
                ok.DialogResult = DialogResult.OK;
                ok.Margin = P(0, 0, 8, 0);
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                layout.Controls.Add(buttons);
                f.Controls.Add(layout);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }

        public static string FormatBytes(long b)
        {
            if (b < 1024 * 1024) return (b / 1024) + " KB";
            return (b / 1048576.0).ToString("0.0") + " MB";
        }
    }

    /// <summary>Combo item with display text and a value.</summary>
    public class Item
    {
        public string Text;
        public string Value;
        public Item(string text, string value) { Text = text; Value = value; }
        public override string ToString() { return Text; }
    }
}
