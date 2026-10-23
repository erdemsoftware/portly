using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Portly.Core;

namespace Portly.UI
{
    public enum ButtonKind { Secondary, Primary, Ghost, Nav, Tile, Danger }

    /// <summary>
    /// Rounded, theme-aware button. Still a real Win32 BUTTON, so keyboard, screen readers and automation keep working.
    /// </summary>
    public class ModernButton : Button
    {
        bool _hover, _down;
        ButtonKind _kind;
        public string Glyph;
        public bool Active;            // selected navigation item
        public Color? CustomColor;     // e.g. donate (heart) color
        public int Radius = 8;

        public ButtonKind Kind { get { return _kind; } set { _kind = value; Invalidate(); } }

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            TextChanged += (s, e) => { AccessibleName = Text; if (AutoSize) PerformLayout(); };
            // a disabled button gets no mouse events: drop a stale hover look and the hand cursor
            EnabledChanged += (s, e) => { _hover = _down = false; Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); };
        }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>
        /// UI Automation sees a real Button with the Invoke pattern (screen readers can press it), also for
        /// glyph-only buttons and the selected navigation item.
        /// </summary>
        protected override AccessibleObject CreateAccessibilityInstance() { return new ModernButtonAccessible(this); }

        UiaProvider _uia;
        protected override void WndProc(ref Message m) { if (!UiaProvider.Handle(this, UiaProvider.Kind.Button, ref _uia, ref m)) base.WndProc(ref m); }

        class ModernButtonAccessible : ButtonBaseAccessibleObject
        {
            readonly ModernButton _b;
            public ModernButtonAccessible(ModernButton b) : base(b) { _b = b; }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override string Name
            {
                get
                {
                    if (!string.IsNullOrEmpty(_b.AccessibleName)) return _b.AccessibleName;
                    return string.IsNullOrEmpty(_b.Text) ? _b.Glyph : _b.Text.Replace("&", "");
                }
                set { base.Name = value; }
            }
            public override string DefaultAction { get { return _b.AccessibleDefaultActionDescription ?? I18n.T("a11y.press"); } }
            public override AccessibleStates State
            {
                get
                {
                    var s = base.State;
                    if (_b.Active) s |= AccessibleStates.Selected;
                    return s;
                }
            }
            public override void DoDefaultAction() { if (_b.Enabled && _b.Visible) _b.PerformClick(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        int GlyphSize { get { return _kind == ButtonKind.Tile ? Ui.S(24) : Ui.S(16); } }

        public override Size GetPreferredSize(Size proposed)
        {
            using (var g = CreateGraphics())
            {
                var t = TextRenderer.MeasureText(g, Text ?? "", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine);
                if (_kind == ButtonKind.Tile)
                    return new Size(Math.Max(Ui.S(104), t.Width + Ui.S(20)), Ui.S(76));
                int w = t.Width + Padding.Horizontal + (Glyph != null ? GlyphSize + (Text != "" ? Ui.S(8) : 0) : 0);
                return new Size(Math.Max(w, MinimumSize.Width), Math.Max(Math.Max(t.Height, GlyphSize) + Padding.Vertical, MinimumSize.Height));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var parentBg = Ui.BackOf(Parent);
            g.Clear(parentBg);

            Color fill, fore, border = Color.Empty;
            switch (_kind)
            {
                case ButtonKind.Primary:
                    fill = CustomColor ?? (_hover || _down ? p.AccentHover : p.Accent);
                    if (CustomColor.HasValue && (_hover || _down)) fill = ControlPaint.Dark(CustomColor.Value, .1f);
                    fore = p.AccentText;
                    break;
                case ButtonKind.Ghost:
                    fill = _hover || _down ? p.Hover : parentBg;
                    fore = CustomColor ?? p.Text;
                    break;
                case ButtonKind.Nav:
                    fill = Active ? p.SidebarActive : (_hover ? p.Hover : parentBg);
                    fore = Active ? p.SidebarActiveText : (CustomColor ?? p.Text);
                    break;
                case ButtonKind.Danger:
                    fill = _hover ? ControlPaint.Dark(p.Red, .1f) : p.Red;
                    fore = Color.White;
                    break;
                default: // Secondary, Tile
                    fill = _hover || _down ? p.Hover : p.Card;
                    fore = p.Text;
                    border = p.Border;
                    break;
            }
            if (!Enabled && _kind != ButtonKind.Nav)
            {
                // clearly "not clickable": flat grey whatever the kind (a faded blue still looks like a button to press)
                fill = _kind == ButtonKind.Ghost ? parentBg : Mix(parentBg, p.Text, Theme.IsDark ? .07f : .05f);
                fore = Mix(parentBg, p.Text, .38f);
                border = _kind == ButtonKind.Ghost ? Color.Empty : Mix(parentBg, p.Text, .12f);
            }

            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var b = new SolidBrush(fill)) Glyphs.FillRound(g, b, rect, Ui.S(Radius));
            if (border != Color.Empty) using (var pen = new Pen(border)) Glyphs.DrawRound(g, pen, rect, Ui.S(Radius));
            if (Focused && ShowFocusCues)
            {
                // the ring must stand out on every fill: accent on light fills, a white inner ring on accent / red
                bool strongFill = _kind == ButtonKind.Primary || _kind == ButtonKind.Danger;
                float w = Math.Max(1, Ui.S(2)), inset = strongFill ? w + 1.5f : 1.5f;
                using (var pen = new Pen(strongFill ? Color.White : p.Accent, w))
                    Glyphs.DrawRound(g, pen, new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1), Math.Max(2, Ui.S(Radius) - (int)inset + 1));
            }

            var glyphColor = _kind == ButtonKind.Tile && Enabled ? (CustomColor ?? p.Accent) : fore;
            if (_kind == ButtonKind.Tile)
            {
                int gs = GlyphSize;
                if (Glyph != null) Glyphs.Draw(g, Glyph, new RectangleF((Width - gs) / 2f, Ui.S(14), gs, gs), glyphColor);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(4, Ui.S(44), Width - 8, Ui.S(22)), fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                return;
            }
            var ts = TextRenderer.MeasureText(g, Text ?? "", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine);
            int gw = Glyph != null ? GlyphSize : 0;
            int gap = Glyph != null && !string.IsNullOrEmpty(Text) ? Ui.S(8) : 0;
            int total = gw + gap + ts.Width;
            int x = _kind == ButtonKind.Nav ? Padding.Left : Math.Max(Padding.Left / 2, (Width - total) / 2);
            if (Glyph != null) Glyphs.Draw(g, Glyph, new RectangleF(x, (Height - gw) / 2f, gw, gw), glyphColor);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gw + gap, 0, Width - x - gw - gap, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Panel with rounded corners and a 1px border (a "card").</summary>
    public class Card : Panel
    {
        public int Radius = 10;
        Color _border = Color.Empty;
        public Color BorderColor { get { return _border == Color.Empty ? Theme.Current.Border : _border; } set { _border = value; Invalidate(); } }

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Current.Card;
            AccessibleRole = AccessibleRole.Grouping;
        }

        UiaProvider _uia;
        protected override void WndProc(ref Message m) { if (!UiaProvider.Handle(this, UiaProvider.Kind.Group, ref _uia, ref m)) base.WndProc(ref m); }

        /// <summary>A card is a named group for screen readers: its name is the first label inside (e.g. "Apache").</summary>
        protected override AccessibleObject CreateAccessibilityInstance() { return new CardAccessible(this); }

        class CardAccessible : ControlAccessibleObject
        {
            readonly Card _c;
            public CardAccessible(Card c) : base(c) { _c = c; }
            public override AccessibleRole Role { get { return AccessibleRole.Grouping; } }
            public override string Name
            {
                get
                {
                    if (!string.IsNullOrEmpty(_c.AccessibleName)) return _c.AccessibleName;
                    foreach (Control child in _c.Controls)
                        if (child is Label && !(child is LinkLabel) && !string.IsNullOrEmpty(child.Text)) return child.Text;
                    return base.Name;
                }
                set { base.Name = value; }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            var outside = Ui.BackOf(Parent);
            g.Clear(outside);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var b = new SolidBrush(BackColor)) Glyphs.FillRound(g, b, r, Ui.S(Radius));
            using (var p = new Pen(BorderColor)) Glyphs.DrawRound(g, p, r, Ui.S(Radius));
        }
    }

    /// <summary>On/off switch. AutoCheck is off: the owner decides what a click does (start / stop).</summary>
    public class ToggleSwitch : CheckBox
    {
        bool _busy;
        public bool Busy { get { return _busy; } set { _busy = value; Invalidate(); } }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            AutoCheck = false;
            AutoSize = false;
            Size = Ui.S(44, 24);
            Cursor = Cursors.Hand;
            Text = "";
            AccessibleRole = AccessibleRole.CheckButton;
        }

        /// <summary>UI Automation: CheckBox with the Toggle pattern; Toggle() runs the owner's Click (start / stop).</summary>
        protected override AccessibleObject CreateAccessibilityInstance() { return new ToggleAccessible(this); }

        UiaProvider _uia;
        protected override void WndProc(ref Message m) { if (!UiaProvider.Handle(this, UiaProvider.Kind.Toggle, ref _uia, ref m)) base.WndProc(ref m); }

        /// <summary>Same as a mouse click (the owner's Click handler starts / stops), for keyboard-less automation.</summary>
        public void PerformToggle() { OnClick(EventArgs.Empty); }

        class ToggleAccessible : CheckBoxAccessibleObject
        {
            readonly ToggleSwitch _t;
            public ToggleAccessible(ToggleSwitch t) : base(t) { _t = t; }
            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
            public override string Description { get { return _t.AccessibleDescription ?? (_t.Busy ? I18n.T("a11y.busy") : null); } }
            public override string DefaultAction { get { return I18n.T("a11y.toggle"); } }
            public override AccessibleStates State
            {
                get
                {
                    var s = base.State;
                    if (_t.Busy) s |= AccessibleStates.Busy;
                    return s;
                }
            }
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(Ui.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(1, 1, Width - 3, Height - 3);
            var color = _busy ? p.Orange : Checked ? p.Green : (Theme.IsDark ? Color.FromArgb(70, 76, 88) : Color.FromArgb(208, 213, 221));
            if (!Enabled) color = Color.FromArgb(110, color);
            using (var b = new SolidBrush(color)) Glyphs.FillRound(g, b, track, track.Height / 2);
            float d = track.Height - Ui.S(6);
            float kx = Checked ? track.Right - d - Ui.S(3) : track.X + Ui.S(3);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx, track.Y + Ui.S(3), d, d);
            if (Focused && ShowFocusCues) using (var pen = new Pen(p.Accent, Math.Max(2, Ui.S(2)))) Glyphs.DrawRound(g, pen, track, track.Height / 2);
        }
    }

    /// <summary>Small rounded badge, e.g. status ("Running") or a PHP version.</summary>
    public class Pill : Control
    {
        Color _color;
        public Color Color { get { return _color; } set { _color = value; Invalidate(); } }
        public bool Dot = true;

        public Pill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Font = Ui.Small;
            Height = Ui.S(22);
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
            TextChanged += (s, e) => { FitWidth(); AccessibilityNotifyClients(AccessibleEvents.NameChange, -1); };
        }

        /// <summary>Read as text ("Running"), not as an empty pane.</summary>
        protected override AccessibleObject CreateAccessibilityInstance() { return new TextAccessible(this); }

        UiaProvider _uia;
        protected override void WndProc(ref Message m) { if (!UiaProvider.Handle(this, UiaProvider.Kind.Text, ref _uia, ref m)) base.WndProc(ref m); }

        internal class TextAccessible : ControlAccessibleObject
        {
            readonly Control _c;
            public TextAccessible(Control c) : base(c) { _c = c; }
            public override AccessibleRole Role { get { return _c.AccessibleRole == AccessibleRole.Default ? AccessibleRole.StaticText : _c.AccessibleRole; } }
            public override string Name { get { return string.IsNullOrEmpty(_c.AccessibleName) ? _c.Text : _c.AccessibleName; } set { base.Name = value; } }
        }

        void FitWidth()
        {
            var t = TextRenderer.MeasureText(Text ?? "", Font);
            Width = t.Width + Ui.S(Dot ? 26 : 16);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var b = new SolidBrush(Color.FromArgb(Theme.IsDark ? 50 : 28, _color))) Glyphs.FillRound(g, b, r, r.Height / 2);
            int x = Ui.S(8);
            if (Dot)
            {
                float d = Ui.S(7);
                using (var b = new SolidBrush(_color)) g.FillEllipse(b, x, (Height - d) / 2f, d, d);
                x += Ui.S(11);
            }
            var fore = Theme.IsDark ? ControlPaint.Light(_color, .3f) : ControlPaint.Dark(_color, .15f);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Rounded square with brand color and short text ("PHP", "My" ...) or a round avatar letter.</summary>
    public class BrandTile : Control
    {
        public Color Color = Color.SteelBlue;
        public bool Circle;

        public BrandTile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Size = Ui.S(40, 40);
            Font = Ui.ScaledFont("Segoe UI", 10f, FontStyle.Bold);
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new Pill.TextAccessible(this); }

        UiaProvider _uia;
        protected override void WndProc(ref Message m) { if (!UiaProvider.Handle(this, UiaProvider.Kind.Image, ref _uia, ref m)) base.WndProc(ref m); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var b = new LinearGradientBrush(r, ControlPaint.Light(Color, .15f), Color, 45f))
            {
                if (Circle) g.FillEllipse(b, r);
                else Glyphs.FillRound(g, b, r, Ui.S(10));
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        /// <summary>Known service colors.</summary>
        public static BrandTile For(string id)
        {
            switch (id)
            {
                case "apache": return new BrandTile { Text = "A", Color = ColorTranslator.FromHtml("#D22128") };
                case "nginx": return new BrandTile { Text = "N", Color = ColorTranslator.FromHtml("#009639") };
                case "php": return new BrandTile { Text = "php", Color = ColorTranslator.FromHtml("#777BB4"), Font = Ui.ScaledFont("Segoe UI", 8.5f, FontStyle.Bold) };
                case "mysql": return new BrandTile { Text = "My", Color = ColorTranslator.FromHtml("#00758F") };
                case "redis": return new BrandTile { Text = "R", Color = ColorTranslator.FromHtml("#DC382D") };
                case "mailpit": return new BrandTile { Text = "M", Color = ColorTranslator.FromHtml("#0EA5E9") };
                case "mariadb": return new BrandTile { Text = "Ma", Color = ColorTranslator.FromHtml("#003545") };
                case "postgresql": return new BrandTile { Text = "Pg", Color = ColorTranslator.FromHtml("#336791") };
                case "mongodb": return new BrandTile { Text = "Mo", Color = ColorTranslator.FromHtml("#13AA52") };
                case "memcached": return new BrandTile { Text = "Mc", Color = ColorTranslator.FromHtml("#5B8F2E") };
                case "mailhog": return new BrandTile { Text = "MH", Color = ColorTranslator.FromHtml("#8B5CF6") };
                case "smtp4dev": return new BrandTile { Text = "S4", Color = ColorTranslator.FromHtml("#0F766E") };
                default: return new BrandTile { Text = "?", Color = Color.SlateGray };
            }
        }

        static readonly string[] AvatarColors = { "#2563EB", "#7C3AED", "#DB2777", "#EA580C", "#059669", "#0891B2", "#4F46E5", "#CA8A04" };

        public static BrandTile Avatar(string name)
        {
            int h = 0;
            foreach (var c in name ?? "") h = h * 31 + c;
            var color = ColorTranslator.FromHtml(AvatarColors[Math.Abs(h % AvatarColors.Length)]);
            var letter = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            return new BrandTile { Text = letter, Color = color, Circle = true, Size = Ui.S(38, 38) };
        }
    }
}

namespace Portly.UI
{
    /// <summary>Drop-down list drawn with theme colors (the native one keeps a light border and arrow in dark mode).</summary>
    public class ThemedCombo : ComboBox
    {
        bool _hover;

        public ThemedCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            ItemHeight = Ui.S(22);
            BackColor = Theme.Current.Input;
            ForeColor = Theme.Current.Text;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var p = Theme.Current;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool selected = (e.State & DrawItemState.Selected) != 0 && !edit;
            using (var b = new SolidBrush(selected ? p.SidebarActive : p.Input)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
            {
                var text = GetItemText(Items[e.Index]);
                var r = new Rectangle(e.Bounds.X + Ui.S(edit ? 4 : 6), e.Bounds.Y, e.Bounds.Width - Ui.S(8), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, text, Font, r, selected ? p.SidebarActiveText : p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x000F || !IsHandleCreated) return; // WM_PAINT: redraw border and arrow in theme colors
            var p = Theme.Current;
            using (var g = Graphics.FromHwnd(Handle))
            {
                int aw = SystemInformation.VerticalScrollBarWidth;
                // right-to-left windows put the drop-down button on the left
                bool rtl = RightToLeft == RightToLeft.Yes;
                var arrow = rtl ? new Rectangle(1, 1, aw, Height - 2) : new Rectangle(Width - aw - 1, 1, aw, Height - 2);
                using (var b = new SolidBrush(p.Input)) g.FillRectangle(b, arrow);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float cx = arrow.X + arrow.Width / 2f, cy = Height / 2f, d = Ui.S(4);
                using (var pen = new Pen(Enabled ? p.Muted : p.Border, Math.Max(1.4f, Ui.S(2) * .8f)))
                    g.DrawLines(pen, new[] { new PointF(cx - d, cy - d / 2), new PointF(cx, cy + d / 2), new PointF(cx + d, cy - d / 2) });
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                using (var pen = new Pen(Focused || _hover ? p.Accent : p.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}
