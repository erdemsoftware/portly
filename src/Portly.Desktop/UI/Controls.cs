using Th = Portly.Desktop.UI.Theme;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Portly.Desktop.UI
{
    /// <summary>
    /// Line icon drawn in code, same set as src\Portly\UI\Glyphs.cs: play stop services globe database terminal folder
    /// plus box list gear heart mail refresh more share update import hosts external warning.
    /// Uses the inherited Foreground unless Color is set.
    /// </summary>
    public class Glyph : Control
    {
        public static readonly StyledProperty<IBrush> ColorProperty = AvaloniaProperty.Register<Glyph, IBrush>(nameof(Color));
        public static readonly StyledProperty<string> NameProperty_ = AvaloniaProperty.Register<Glyph, string>("GlyphName");

        static Glyph() { AffectsRender<Glyph>(ColorProperty, NameProperty_, TextElement.ForegroundProperty); }

        public IBrush Color { get { return GetValue(ColorProperty); } set { SetValue(ColorProperty, value); } }

        /// <summary>Glyph name (Control.Name is taken by Avalonia).</summary>
        public string Name_ { get { return GetValue(NameProperty_); } set { SetValue(NameProperty_, value); } }

        public Glyph() { Width = 16; Height = 16; }

        public override void Render(DrawingContext ctx)
        {
            var brush = Color ?? TextElement.GetForeground(this) ?? Brushes.Gray;
            GlyphPainter.Draw(ctx, Name_, new Rect(Bounds.Size), brush);
        }
    }

    /// <summary>Drawing code for Glyph. Port of src\Portly\UI\Glyphs.cs (GDI+ -> Avalonia DrawingContext).</summary>
    public static class GlyphPainter
    {
        public static readonly string[] Names =
        {
            "play", "stop", "services", "globe", "database", "terminal", "folder", "plus", "box", "list", "gear", "heart",
            "mail", "refresh", "more", "share", "update", "import", "hosts", "external", "warning",
        };

        public static void Draw(DrawingContext g, string name, Rect r, IBrush brush)
        {
            if (string.IsNullOrEmpty(name)) return;
            double s = Math.Min(r.Width, r.Height);
            double x = r.X + (r.Width - s) / 2, y = r.Y + (r.Height - s) / 2;
            var pen = new Pen(brush, Math.Max(1.4, s / 11), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            Func<double, double> X = f => x + f * s;
            Func<double, double> Y = f => y + f * s;
            Func<double, double, Point> P = (fx, fy) => new Point(X(fx), Y(fy));
            Action<double, double, double, double> Line = (x1, y1, x2, y2) => g.DrawLine(pen, P(x1, y1), P(x2, y2));
            Action<double, double, double> Dot = (fx, fy, d) => g.DrawEllipse(brush, null, new Rect(X(fx), Y(fy), s * d, s * d));
            Action<double, double, double, double> Oval = (fx, fy, fw, fh) => g.DrawEllipse(null, pen, new Rect(X(fx), Y(fy), s * fw, s * fh));
            Action<Point[]> Open = pts => Lines(g, pen, false, pts);
            switch (name)
            {
                case "play":
                    Poly(g, brush, null, P(.28, .18), P(.82, .5), P(.28, .82));
                    break;
                case "stop":
                    g.DrawRectangle(brush, null, new Rect(X(.22), Y(.22), s * .56, s * .56), s * .1, s * .1);
                    break;
                case "services":
                    for (int i = 0; i < 3; i++)
                    {
                        g.DrawRectangle(null, pen, new Rect(X(.12), Y(.12 + i * .27), s * .76, s * .2), s * .06, s * .06);
                        Dot(.22, .18 + i * .27, .08);
                    }
                    break;
                case "globe":
                    Oval(.12, .12, .76, .76);
                    Oval(.34, .12, .32, .76);
                    Line(.14, .5, .86, .5);
                    break;
                case "database":
                    Oval(.2, .1, .6, .22);
                    Line(.2, .21, .2, .79);
                    Line(.8, .21, .8, .79);
                    Arc(g, pen, X(.2), Y(.39), s * .6, s * .22, 0, 180);
                    Arc(g, pen, X(.2), Y(.68), s * .6, s * .22, 0, 180);
                    break;
                case "terminal":
                    g.DrawRectangle(null, pen, new Rect(X(.1), Y(.16), s * .8, s * .68), s * .08, s * .08);
                    Open(new[] { P(.25, .38), P(.4, .5), P(.25, .62) });
                    Line(.48, .64, .7, .64);
                    break;
                case "folder":
                    Open(new[] { P(.1, .78), P(.1, .22), P(.38, .22), P(.46, .32), P(.9, .32), P(.9, .78), P(.1, .78) });
                    break;
                case "plus":
                    Line(.5, .18, .5, .82);
                    Line(.18, .5, .82, .5);
                    break;
                case "box":
                    Lines(g, pen, true, P(.5, .1), P(.88, .3), P(.88, .72), P(.5, .92), P(.12, .72), P(.12, .3));
                    Open(new[] { P(.12, .3), P(.5, .5), P(.88, .3) });
                    Line(.5, .5, .5, .92);
                    break;
                case "list":
                    for (int i = 0; i < 3; i++)
                    {
                        Dot(.12, .18 + i * .26, .09);
                        Line(.32, .22 + i * .26, .88, .22 + i * .26);
                    }
                    break;
                case "gear":
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4, c = Math.Cos(a), n = Math.Sin(a);
                        Line(.5 + c * .3, .5 + n * .3, .5 + c * .42, .5 + n * .42);
                    }
                    Oval(.24, .24, .52, .52);
                    Oval(.4, .4, .2, .2);
                    break;
                case "heart":
                    {
                        var geo = new StreamGeometry();
                        using (var c = geo.Open())
                        {
                            c.BeginFigure(P(.5, .86), true);
                            c.CubicBezierTo(P(.1, .6), P(.02, .3), P(.25, .18));
                            c.CubicBezierTo(P(.4, .1), P(.5, .22), P(.5, .3));
                            c.CubicBezierTo(P(.5, .22), P(.6, .1), P(.75, .18));
                            c.CubicBezierTo(P(.98, .3), P(.9, .6), P(.5, .86));
                            c.EndFigure(true);
                        }
                        g.DrawGeometry(brush, null, geo);
                    }
                    break;
                case "mail":
                    g.DrawRectangle(null, pen, new Rect(X(.1), Y(.22), s * .8, s * .56), s * .06, s * .06);
                    Open(new[] { P(.12, .26), P(.5, .54), P(.88, .26) });
                    break;
                case "refresh":
                    Arc(g, pen, X(.18), Y(.18), s * .64, s * .64, 300, 290);
                    Poly(g, brush, null, P(.86, .12), P(.86, .42), P(.58, .3));
                    break;
                case "more":
                    for (int i = 0; i < 3; i++) Dot(.16 + i * .28, .44, .12);
                    break;
                case "share":
                    Line(.3, .5, .72, .24);
                    Line(.3, .5, .72, .76);
                    Dot(.14, .34, .32);
                    Dot(.58, .1, .28);
                    Dot(.58, .62, .28);
                    break;
                case "update":
                    Line(.5, .15, .5, .68);
                    Open(new[] { P(.28, .46), P(.5, .68), P(.72, .46) });
                    Line(.2, .86, .8, .86);
                    break;
                case "import":
                    Open(new[] { P(.15, .55), P(.15, .85), P(.85, .85), P(.85, .55) });
                    Line(.5, .12, .5, .62);
                    Open(new[] { P(.3, .44), P(.5, .64), P(.7, .44) });
                    break;
                case "hosts":
                    Open(new[] { P(.22, .1), P(.62, .1), P(.8, .28), P(.8, .9), P(.22, .9), P(.22, .1) });
                    Open(new[] { P(.62, .1), P(.62, .28), P(.8, .28) });
                    Dot(.3, .4, .09);
                    Line(.47, .445, .7, .445);
                    Dot(.3, .58, .09);
                    Line(.47, .625, .7, .625);
                    Dot(.3, .76, .09);
                    Line(.47, .805, .62, .805);
                    break;
                case "external":
                    Open(new[] { P(.62, .15), P(.85, .15), P(.85, .38) });
                    Line(.85, .15, .48, .52);
                    Open(new[] { P(.7, .6), P(.7, .85), P(.15, .85), P(.15, .3), P(.4, .3) });
                    break;
                case "warning":
                    Lines(g, pen, true, P(.5, .12), P(.9, .84), P(.1, .84));
                    Line(.5, .38, .5, .6);
                    Dot(.46, .68, .08);
                    break;
            }
        }

        /// <summary>Filled and/or stroked closed polygon.</summary>
        public static void Poly(DrawingContext g, IBrush fill, IPen pen, params Point[] pts)
        {
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(pts[0], fill != null);
                for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i]);
                c.EndFigure(true);
            }
            g.DrawGeometry(fill, pen, geo);
        }

        /// <summary>Stroked polyline (open) or polygon (closed).</summary>
        public static void Lines(DrawingContext g, IPen pen, bool closed, params Point[] pts)
        {
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(pts[0], false);
                for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i]);
                c.EndFigure(closed);
            }
            g.DrawGeometry(null, pen, geo);
        }

        /// <summary>GDI+ DrawArc: ellipse in (x, y, w, h), start angle and sweep in degrees (clockwise, 0 = right).</summary>
        public static void Arc(DrawingContext g, IPen pen, double x, double y, double w, double h, double start, double sweep)
        {
            double rx = w / 2, ry = h / 2, cx = x + rx, cy = y + ry;
            double a0 = start * Math.PI / 180, a1 = (start + sweep) * Math.PI / 180;
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(new Point(cx + rx * Math.Cos(a0), cy + ry * Math.Sin(a0)), false);
                c.ArcTo(new Point(cx + rx * Math.Cos(a1), cy + ry * Math.Sin(a1)), new Size(rx, ry), 0, Math.Abs(sweep) > 180,
                    sweep >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
                c.EndFigure(false);
            }
            g.DrawGeometry(null, pen, geo);
        }

        internal static IBrush WithAlpha(IBrush b, byte alpha)
        {
            var c = b as ISolidColorBrush;
            return c == null ? b : new SolidColorBrush(Color.FromArgb(alpha, c.Color.R, c.Color.G, c.Color.B));
        }

        internal static Color Mix(Color c, Color to, double t)
        {
            return Color.FromRgb((byte)(c.R + (to.R - c.R) * t), (byte)(c.G + (to.G - c.G) * t), (byte)(c.B + (to.B - c.B) * t));
        }
    }

    /// <summary>Rounded status badge: tinted background, colored dot + text ("Running", "Stopped", "HTTPS" ...).</summary>
    public class Pill : Border
    {
        readonly Ellipse _dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _text = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };

        public Pill(string text = "", IBrush color = null, bool dot = true)
        {
            CornerRadius = new CornerRadius(11);
            Height = 22;
            Padding = new Thickness(8, 0);
            VerticalAlignment = VerticalAlignment.Center;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (dot) row.Children.Add(_dot);
            row.Children.Add(_text);
            Child = row;
            Set(text, color ?? Ui.Gray);
        }

        public void Set(string text, IBrush color)
        {
            var sb = color as ISolidColorBrush;
            var c = sb == null ? Colors.Gray : sb.Color;
            _text.Text = text;
            Background = new SolidColorBrush(Color.FromArgb((byte)(Th.IsDark ? 50 : 28), c.R, c.G, c.B));
            _text.Foreground = new SolidColorBrush(Th.IsDark ? GlyphPainter.Mix(c, Colors.White, .3) : GlyphPainter.Mix(c, Colors.Black, .15));
            _dot.Fill = color;
            Avalonia.Automation.AutomationProperties.SetName(this, text);
        }

        public string Text { get { return _text.Text; } }
    }

    /// <summary>
    /// Rounded square with the brand color (light diagonal gradient) and the first letters of a service / package
    /// (Apache "Ap", PHP "PH"), or a round avatar letter.
    /// </summary>
    public class BrandTile : Border
    {
        public BrandTile(string type, string title, double size = 40) : this(Th.Brand(type), title, size, false) { }

        BrandTile(IBrush brand, string title, double size, bool circle)
        {
            Width = Height = size;
            CornerRadius = new CornerRadius(circle ? size / 2 : size / 4);
            var c = ((ISolidColorBrush)brand).Color;
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(c, 0), new GradientStop(GlyphPainter.Mix(c, Colors.White, .15), 1) },
            };
            var initials = string.IsNullOrEmpty(title) ? "?" : title.Length >= 2 ? title.Substring(0, 2) : title;
            if (circle) initials = initials.Substring(0, 1).ToUpperInvariant();
            Child = new TextBlock
            {
                Text = initials, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = size * .35,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            Avalonia.Automation.AutomationProperties.SetName(this, title ?? "");
        }

        /// <summary>Round tile with a short text ("1", "2") in the given color (numbered steps).</summary>
        public static BrandTile Badge(string text, IBrush color, double size = 32) { return new BrandTile(color, text, size, true); }

        static readonly string[] AvatarColors = { "#2563EB", "#7C3AED", "#DB2777", "#EA580C", "#059669", "#0891B2", "#4F46E5", "#CA8A04" };

        /// <summary>Round tile with the first letter of a name and a color picked from the name.</summary>
        public static BrandTile Avatar(string name, double size = 38)
        {
            int h = 0;
            foreach (var c in name ?? "") h = h * 31 + c;
            var color = new SolidColorBrush(Color.Parse(AvatarColors[Math.Abs(h % AvatarColors.Length)]));
            return new BrandTile(color, string.IsNullOrEmpty(name) ? "?" : name, size, true);
        }
    }

    /// <summary>
    /// Padlock toggle on a service card: closed green = part of "Start all" / "Stop all", open red = left alone.
    /// A ToggleButton underneath, so keyboard and screen readers still work.
    /// </summary>
    public class LockToggle : ToggleButton
    {
        static LockToggle()
        {
            AffectsRender<LockToggle>(IsCheckedProperty, IsEnabledProperty, IsPointerOverProperty);
        }

        public LockToggle()
        {
            Width = Height = 26;
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            Template = new FuncControlTemplate<LockToggle>((t, ns) => new Border { Background = Brushes.Transparent });
            Classes.CollectionChanged += (s, e) => InvalidateVisual();
        }

        IBrush BackOf()
        {
            for (var v = this.GetVisualParent(); v != null; v = v.GetVisualParent())
            {
                var b = (v as Border)?.Background ?? (v as Panel)?.Background ?? (v as TemplatedControl)?.Background;
                var sb = b as ISolidColorBrush;
                if (sb != null && sb.Color.A > 0) return b;
            }
            return Ui.Brush("Card");
        }

        public override void Render(DrawingContext g)
        {
            double w = Bounds.Width, h = Bounds.Height;
            bool on = IsChecked == true;
            var color = on ? Ui.Green : Ui.Red;
            if (!IsEnabled) color = GlyphPainter.WithAlpha(color, 110);
            if (IsPointerOver && IsEnabled) g.DrawRectangle(Ui.Brush("Hover"), null, new Rect(0, 0, w, h), 6, 6);
            // body: lower half, rounded
            var body = new Rect(w * .18, h * .46, w * .64, h * .44);
            g.DrawRectangle(color, null, body, 3, 3);
            // keyhole
            g.DrawEllipse(BackOf(), null, new Rect(w / 2 - 2, body.Top + body.Height * .32, 4, 4));
            // shackle: closed sits in the body, open is lifted and swung to the right
            var pen = new Pen(color, 2, lineCap: PenLineCap.Round);
            double sx = w * .30, sw = w * .40, top = h * .10;
            if (on)
            {
                GlyphPainter.Arc(g, pen, sx, top, sw, h * .46, 180, 180);
                g.DrawLine(pen, new Point(sx, top + h * .23), new Point(sx, body.Top));
                g.DrawLine(pen, new Point(sx + sw, top + h * .23), new Point(sx + sw, body.Top));
            }
            else
            {
                double lift = h * .06, shift = w * .18;
                GlyphPainter.Arc(g, pen, sx + shift, top - lift, sw, h * .46, 180, 180);
                g.DrawLine(pen, new Point(sx + shift, top - lift + h * .23), new Point(sx + shift, top + h * .26));
                g.DrawLine(pen, new Point(sx + shift + sw, top - lift + h * .23), new Point(sx + shift + sw, body.Top));
            }
            if (Classes.Contains(":focus-visible"))
                g.DrawRectangle(null, new Pen(Ui.Accent, 1, DashStyle.Dot), new Rect(.5, .5, w - 1, h - 1), 4, 4);
        }
    }

    /// <summary>Every glyph at 16 and 32 px with its name, to check the drawing code (screenshot: --dialog GlyphGallery).</summary>
    internal class GlyphGallery : PortlyDialog
    {
        public GlyphGallery() : base("Glyphs", 720)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
            int n = 0;
            foreach (var name in GlyphPainter.Names)
            {
                var cell = new Border { Padding = new Thickness(8, 6), Margin = new Thickness(0, 0, 8, 8) };
                cell.Classes.Add("card");
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(new Glyph { Name_ = name, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new Glyph { Name_ = name, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new Glyph { Name_ = name, Width = 32, Height = 32, Color = Ui.Accent, VerticalAlignment = VerticalAlignment.Center });
                var cap = Ui.Label(name, "small"); cap.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(cap);
                cell.Child = row;
                Grid.SetColumn(cell, n % 3);
                Grid.SetRow(cell, n / 3);
                if (n % 3 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.Children.Add(cell);
                n++;
            }
            Body.Children.Add(grid);
            var extras = Ui.Row(new LockToggle { IsChecked = true }, new LockToggle { IsChecked = false }, new LockToggle { IsChecked = true, IsEnabled = false },
                new Pill("Running", Ui.Green), new Pill("Stopped", Ui.Gray), new Pill("PHP 8.5", Ui.Accent, false), new Pill("Port 80 busy", Ui.Orange),
                new BrandTile("apache", "Ap"), new BrandTile("php", "PH"), new BrandTile("mysql", "My"), BrandTile.Avatar("Erdem"));
            extras.Spacing = 10;
            Body.Children.Add(extras);
            Buttons.Children.Add(Ui.Button("OK", () => Close()));
        }

        public static Window Preview() { return new GlyphGallery(); }
    }
}
