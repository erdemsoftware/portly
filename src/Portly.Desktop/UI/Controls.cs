using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

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
        public static void Draw(DrawingContext g, string name, Rect r, IBrush brush)
        {
            if (string.IsNullOrEmpty(name)) return;
            double s = Math.Min(r.Width, r.Height);
            double x = r.X + (r.Width - s) / 2, y = r.Y + (r.Height - s) / 2;
            var pen = new Pen(brush, Math.Max(1.4, s / 11), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            Func<double, double> X = f => x + f * s;
            Func<double, double> Y = f => y + f * s;
            switch (name)
            {
                case "play":
                    Poly(g, brush, null, new Point(X(.28), Y(.18)), new Point(X(.82), Y(.5)), new Point(X(.28), Y(.82)));
                    break;
                case "stop":
                    g.DrawRectangle(brush, null, new Rect(X(.22), Y(.22), s * .56, s * .56), s * .1, s * .1);
                    break;
                default:
                    // placeholder until every glyph is ported: a small circle
                    g.DrawEllipse(null, pen, new Point(X(.5), Y(.5)), s * .3, s * .3);
                    break;
            }
        }

        public static void Poly(DrawingContext g, IBrush fill, IPen pen, params Point[] pts)
        {
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(pts[0], fill != null);
                for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i]);
                c.EndFigure(fill != null);
            }
            g.DrawGeometry(fill, pen, geo);
        }
    }

    /// <summary>Rounded status badge: colored dot + text ("Running", "Stopped", "HTTPS" ...).</summary>
    public class Pill : Border
    {
        readonly Ellipse _dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _text = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };

        public Pill(string text = "", IBrush color = null, bool dot = true)
        {
            CornerRadius = new CornerRadius(999);
            Padding = new Thickness(10, 3);
            VerticalAlignment = VerticalAlignment.Center;
            Background = Ui.Brush("Hover");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (dot) row.Children.Add(_dot);
            row.Children.Add(_text);
            Child = row;
            Set(text, color ?? Ui.Gray);
        }

        public void Set(string text, IBrush color)
        {
            _text.Text = text;
            _text.Foreground = color;
            _dot.Fill = color;
            Avalonia.Automation.AutomationProperties.SetName(this, text);
        }

        public string Text { get { return _text.Text; } }
    }

    /// <summary>Colored square with the first letters of a service / package (Apache "Ap", PHP "PH").</summary>
    public class BrandTile : Border
    {
        public BrandTile(string type, string title, double size = 40)
        {
            Width = Height = size;
            CornerRadius = new CornerRadius(size / 4);
            Background = UI.Theme.Brand(type);
            var initials = string.IsNullOrEmpty(title) ? "?" : title.Length >= 2 ? title.Substring(0, 2) : title;
            Child = new TextBlock
            {
                Text = initials, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = size * .35,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
    }

    /// <summary>
    /// Padlock toggle on a service card: locked (green) = part of "Start all" / "Stop all", open (red) = left alone.
    /// </summary>
    public class LockToggle : ToggleButton
    {
        public LockToggle()
        {
            Classes.Add("ghost");
            Padding = new Thickness(6);
            IsCheckedChanged += (s, e) => Refresh();
            Refresh();
        }

        void Refresh()
        {
            bool on = IsChecked == true;
            Content = new TextBlock { Text = on ? "🔒" : "🔓", FontSize = 15, Foreground = on ? Ui.Green : Ui.Red };
        }
    }
}
