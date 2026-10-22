using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Portly.Core;

namespace Portly.UI
{
    /// <summary>Shared look: colors, fonts, DPI scaling and small control factories.</summary>
    public static class Ui
    {
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentDark = Color.FromArgb(29, 78, 216);
        public static readonly Color Bg = Color.FromArgb(245, 247, 251);
        public static readonly Color Card = Color.White;
        public static readonly Color Line = Color.FromArgb(228, 231, 236);
        public static readonly Color Text = Color.FromArgb(28, 36, 51);
        public static readonly Color Muted = Color.FromArgb(102, 112, 133);
        public static readonly Color Green = Color.FromArgb(22, 163, 74);
        public static readonly Color Orange = Color.FromArgb(245, 158, 11);
        public static readonly Color Red = Color.FromArgb(220, 38, 38);
        public static readonly Color Gray = Color.FromArgb(152, 162, 179);
        public static readonly Color Heart = Color.FromArgb(225, 29, 72);

        public static readonly Font Font = new Font("Segoe UI", 9f);
        public static readonly Font Bold = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font Small = new Font("Segoe UI", 8.25f);
        public static readonly Font Title = new Font("Segoe UI", 15f, FontStyle.Bold);
        public static readonly Font Subtitle = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 9f);

        static float _scale = -1;
        public static float Scale
        {
            get
            {
                if (_scale < 0)
                {
                    try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) _scale = g.DpiX / 96f; }
                    catch { _scale = 1f; }
                }
                return _scale;
            }
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
                    try
                    {
                        using (var s = typeof(Ui).Assembly.GetManifestResourceStream("portly.ico")) _icon = new Icon(s);
                    }
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

        public static Button Button(string text, EventHandler click, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Card,
                ForeColor = primary ? Color.White : Text,
                Padding = P(10, 4, 10, 4),
                Margin = P(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                MinimumSize = new Size(0, S(30)),
                AccessibleRole = AccessibleRole.PushButton,
                AccessibleName = text,
            };
            b.TextChanged += (s, e) => b.AccessibleName = b.Text;
            b.FlatAppearance.BorderColor = primary ? AccentDark : Line;
            b.FlatAppearance.MouseOverBackColor = primary ? AccentDark : Color.FromArgb(238, 242, 255);
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
                LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = Color.Transparent,
                Margin = P(0, 4, 10, 4),
            };
            l.LinkClicked += (s, e) => click();
            return l;
        }

        public static void Apply(Form f)
        {
            f.Font = Font;
            f.AutoScaleMode = AutoScaleMode.None;
            f.BackColor = Bg;
            f.ForeColor = Text;
            f.Icon = AppIcon;
        }

        // ---------------- dialogs ----------------
        public static void Info(IWin32Window owner, string text) { MessageBox.Show(owner, text, "Portly", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        public static void Error(IWin32Window owner, string text) { MessageBox.Show(owner, text, "Portly", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        public static bool Confirm(IWin32Window owner, string text) { return MessageBox.Show(owner, text, "Portly", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes; }

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
                f.Padding = P(14);
                var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
                layout.Controls.Add(Label(label));
                var box = new TextBox { Text = value, Width = S(320), UseSystemPasswordChar = password, Margin = P(0, 2, 0, 8) };
                layout.Controls.Add(box);
                if (password)
                {
                    var show = new CheckBox { Text = I18n.T("common.show"), AutoSize = true };
                    show.CheckedChanged += (s, e) => box.UseSystemPasswordChar = !show.Checked;
                    layout.Controls.Add(show);
                }
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = P(0, 8, 0, 0) };
                var cancel = Button(I18n.T("common.cancel"), null);
                cancel.DialogResult = DialogResult.Cancel;
                var ok = Button(I18n.T("common.ok"), null, true);
                ok.DialogResult = DialogResult.OK;
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

    /// <summary>Round status light.</summary>
    public class StatusDot : Control
    {
        Color _color = Ui.Gray;
        public Color DotColor { get { return _color; } set { _color = value; Invalidate(); } }

        public StatusDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = Ui.S(14, 14);
            Margin = Ui.P(0, 8, 6, 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Math.Min(Width, Height) - 2;
            using (var b = new SolidBrush(_color)) e.Graphics.FillEllipse(b, 1, 1, d, d);
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
