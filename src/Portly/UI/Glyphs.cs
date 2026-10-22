using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Portly.UI
{
    /// <summary>
    /// Small line icons drawn with GDI+ (no icon font needed, so they look the same on Windows 7 and 11).
    /// </summary>
    public static class Glyphs
    {
        public static void Draw(Graphics g, string name, RectangleF r, Color c)
        {
            if (string.IsNullOrEmpty(name)) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(r.Width, r.Height);
            float x = r.X + (r.Width - s) / 2, y = r.Y + (r.Height - s) / 2;
            float w = Math.Max(1.4f, s / 11f);
            using (var p = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(c))
            {
                Func<float, float> X = f => x + f * s;
                Func<float, float> Y = f => y + f * s;
                switch (name)
                {
                    case "play":
                        g.FillPolygon(b, new[] { new PointF(X(.28f), Y(.18f)), new PointF(X(.82f), Y(.5f)), new PointF(X(.28f), Y(.82f)) });
                        break;
                    case "stop":
                        FillRound(g, b, new RectangleF(X(.22f), Y(.22f), s * .56f, s * .56f), s * .1f);
                        break;
                    case "services":
                        for (int i = 0; i < 3; i++)
                        {
                            var rr = new RectangleF(X(.12f), Y(.12f + i * .27f), s * .76f, s * .2f);
                            DrawRound(g, p, rr, s * .06f);
                            g.FillEllipse(b, X(.22f), Y(.18f + i * .27f), s * .08f, s * .08f);
                        }
                        break;
                    case "globe":
                        g.DrawEllipse(p, X(.12f), Y(.12f), s * .76f, s * .76f);
                        g.DrawEllipse(p, X(.34f), Y(.12f), s * .32f, s * .76f);
                        g.DrawLine(p, X(.14f), Y(.5f), X(.86f), Y(.5f));
                        break;
                    case "database":
                        g.DrawEllipse(p, X(.2f), Y(.1f), s * .6f, s * .22f);
                        g.DrawLine(p, X(.2f), Y(.21f), X(.2f), Y(.79f));
                        g.DrawLine(p, X(.8f), Y(.21f), X(.8f), Y(.79f));
                        g.DrawArc(p, X(.2f), Y(.39f), s * .6f, s * .22f, 0, 180);
                        g.DrawArc(p, X(.2f), Y(.68f), s * .6f, s * .22f, 0, 180);
                        break;
                    case "terminal":
                        DrawRound(g, p, new RectangleF(X(.1f), Y(.16f), s * .8f, s * .68f), s * .08f);
                        g.DrawLines(p, new[] { new PointF(X(.25f), Y(.38f)), new PointF(X(.4f), Y(.5f)), new PointF(X(.25f), Y(.62f)) });
                        g.DrawLine(p, X(.48f), Y(.64f), X(.7f), Y(.64f));
                        break;
                    case "folder":
                        g.DrawLines(p, new[] { new PointF(X(.1f), Y(.78f)), new PointF(X(.1f), Y(.22f)), new PointF(X(.38f), Y(.22f)), new PointF(X(.46f), Y(.32f)), new PointF(X(.9f), Y(.32f)), new PointF(X(.9f), Y(.78f)), new PointF(X(.1f), Y(.78f)) });
                        break;
                    case "plus":
                        g.DrawLine(p, X(.5f), Y(.18f), X(.5f), Y(.82f));
                        g.DrawLine(p, X(.18f), Y(.5f), X(.82f), Y(.5f));
                        break;
                    case "box":
                        g.DrawPolygon(p, new[] { new PointF(X(.5f), Y(.1f)), new PointF(X(.88f), Y(.3f)), new PointF(X(.88f), Y(.72f)), new PointF(X(.5f), Y(.92f)), new PointF(X(.12f), Y(.72f)), new PointF(X(.12f), Y(.3f)) });
                        g.DrawLines(p, new[] { new PointF(X(.12f), Y(.3f)), new PointF(X(.5f), Y(.5f)), new PointF(X(.88f), Y(.3f)) });
                        g.DrawLine(p, X(.5f), Y(.5f), X(.5f), Y(.92f));
                        break;
                    case "list":
                        for (int i = 0; i < 3; i++)
                        {
                            g.FillEllipse(b, X(.12f), Y(.22f + i * .26f) - s * .04f, s * .09f, s * .09f);
                            g.DrawLine(p, X(.32f), Y(.22f + i * .26f), X(.88f), Y(.22f + i * .26f));
                        }
                        break;
                    case "gear":
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(p, X(.5f + (float)Math.Cos(a) * .3f), Y(.5f + (float)Math.Sin(a) * .3f), X(.5f + (float)Math.Cos(a) * .42f), Y(.5f + (float)Math.Sin(a) * .42f));
                        }
                        g.DrawEllipse(p, X(.24f), Y(.24f), s * .52f, s * .52f);
                        g.DrawEllipse(p, X(.4f), Y(.4f), s * .2f, s * .2f);
                        break;
                    case "heart":
                        using (var path = new GraphicsPath())
                        {
                            path.AddBezier(X(.5f), Y(.86f), X(.1f), Y(.6f), X(.02f), Y(.3f), X(.25f), Y(.18f));
                            path.AddBezier(X(.25f), Y(.18f), X(.4f), Y(.1f), X(.5f), Y(.22f), X(.5f), Y(.3f));
                            path.AddBezier(X(.5f), Y(.3f), X(.5f), Y(.22f), X(.6f), Y(.1f), X(.75f), Y(.18f));
                            path.AddBezier(X(.75f), Y(.18f), X(.98f), Y(.3f), X(.9f), Y(.6f), X(.5f), Y(.86f));
                            g.FillPath(b, path);
                        }
                        break;
                    case "mail":
                        DrawRound(g, p, new RectangleF(X(.1f), Y(.22f), s * .8f, s * .56f), s * .06f);
                        g.DrawLines(p, new[] { new PointF(X(.12f), Y(.26f)), new PointF(X(.5f), Y(.54f)), new PointF(X(.88f), Y(.26f)) });
                        break;
                    case "refresh":
                        g.DrawArc(p, X(.18f), Y(.18f), s * .64f, s * .64f, 300, 290);
                        g.FillPolygon(b, new[] { new PointF(X(.86f), Y(.12f)), new PointF(X(.86f), Y(.42f)), new PointF(X(.58f), Y(.3f)) });
                        break;
                    case "more":
                        for (int i = 0; i < 3; i++) g.FillEllipse(b, X(.16f + i * .28f), Y(.44f), s * .12f, s * .12f);
                        break;
                    case "update":
                        g.DrawLine(p, X(.5f), Y(.15f), X(.5f), Y(.68f));
                        g.DrawLines(p, new[] { new PointF(X(.28f), Y(.46f)), new PointF(X(.5f), Y(.68f)), new PointF(X(.72f), Y(.46f)) });
                        g.DrawLine(p, X(.2f), Y(.86f), X(.8f), Y(.86f));
                        break;
                    case "import":
                        g.DrawLines(p, new[] { new PointF(X(.15f), Y(.55f)), new PointF(X(.15f), Y(.85f)), new PointF(X(.85f), Y(.85f)), new PointF(X(.85f), Y(.55f)) });
                        g.DrawLine(p, X(.5f), Y(.12f), X(.5f), Y(.62f));
                        g.DrawLines(p, new[] { new PointF(X(.3f), Y(.44f)), new PointF(X(.5f), Y(.64f)), new PointF(X(.7f), Y(.44f)) });
                        break;
                    case "external":
                        g.DrawLines(p, new[] { new PointF(X(.62f), Y(.15f)), new PointF(X(.85f), Y(.15f)), new PointF(X(.85f), Y(.38f)) });
                        g.DrawLine(p, X(.85f), Y(.15f), X(.48f), Y(.52f));
                        g.DrawLines(p, new[] { new PointF(X(.7f), Y(.6f)), new PointF(X(.7f), Y(.85f)), new PointF(X(.15f), Y(.85f)), new PointF(X(.15f), Y(.3f)), new PointF(X(.4f), Y(.3f)) });
                        break;
                    case "warning":
                        g.DrawPolygon(p, new[] { new PointF(X(.5f), Y(.12f)), new PointF(X(.9f), Y(.84f)), new PointF(X(.1f), Y(.84f)) });
                        g.DrawLine(p, X(.5f), Y(.38f), X(.5f), Y(.6f));
                        g.FillEllipse(b, X(.46f), Y(.68f), s * .08f, s * .08f);
                        break;
                }
            }
            g.SmoothingMode = old;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Brush b, RectangleF r, float radius)
        {
            using (var path = Round(r, radius)) g.FillPath(b, path);
        }

        public static void DrawRound(Graphics g, Pen p, RectangleF r, float radius)
        {
            using (var path = Round(r, radius)) g.DrawPath(p, path);
        }
    }
}
