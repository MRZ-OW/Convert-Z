using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ConvertZ
{
    public static class IconArt
    {
        public static readonly Color From = Color.FromArgb(0x7B, 0x5C, 0xFF);
        public static readonly Color To = Color.FromArgb(0x2E, 0xC5, 0xFF);
        public static readonly Color Done = Color.FromArgb(0x3D, 0xDC, 0x97);

        public static Bitmap App(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float s = size;
                float pad = size <= 16 ? 0.5f : s * 0.04f;
                var rect = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
                using (var path = Rounded(rect, s * 0.22f))
                using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), From, To))
                    g.FillPath(brush, path);

                float cx = s / 2, cy = s / 2, r = s * 0.27f;
                float w = Math.Max(1.6f, s * 0.095f);
                using (var pen = new Pen(Color.White, w) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
                {
                    var arc = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
                    g.DrawArc(pen, arc, 200, 125);
                    g.DrawArc(pen, arc, 20, 125);
                }
                using (var white = new SolidBrush(Color.White))
                {
                    Arrowhead(g, white, cx, cy, r, 325, w);
                    Arrowhead(g, white, cx, cy, r, 145, w);
                }
            }
            return bmp;
        }

        static void Arrowhead(Graphics g, Brush b, float cx, float cy, float r, float angle, float w)
        {
            double a = angle * Math.PI / 180;
            float px = cx + (float)(r * Math.Cos(a)), py = cy + (float)(r * Math.Sin(a));
            float tx = (float)-Math.Sin(a), ty = (float)Math.Cos(a);
            float nx = (float)Math.Cos(a), ny = (float)Math.Sin(a);
            float len = w * 2.1f, half = w * 1.55f;
            var tip = new PointF(px + tx * len, py + ty * len);
            var p1 = new PointF(px + nx * half, py + ny * half);
            var p2 = new PointF(px - nx * half, py - ny * half);
            g.FillPolygon(b, new[] { tip, p1, p2 });
        }

        public static Bitmap Progress(int size, double fraction, float spin)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float s = size;
                using (var disc = new SolidBrush(Color.FromArgb(235, 0x1C, 0x1F, 0x2B)))
                    g.FillEllipse(disc, 0.5f, 0.5f, s - 1, s - 1);

                float w = Math.Max(2.2f, s * 0.2f);
                float inset = w / 2 + Math.Max(1f, s * 0.07f);
                var arc = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
                using (var track = new Pen(Color.FromArgb(70, 255, 255, 255), w))
                    g.DrawEllipse(track, arc);

                using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), From, To))
                using (var pen = new Pen(brush, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    if (fraction < 0)
                        g.DrawArc(pen, arc, spin - 90, 100);
                    else if (fraction >= 0.999)
                        g.DrawEllipse(pen, arc);
                    else if (fraction > 0.005)
                        g.DrawArc(pen, arc, -90, (float)(360 * fraction));
                }

                float d = s - 2 * inset - w - Math.Max(1f, s * 0.06f) * 2;
                if (d >= 3)
                    using (var c = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), From, To))
                        g.FillEllipse(c, (s - d) / 2, (s - d) / 2, d, d);
            }
            return bmp;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
