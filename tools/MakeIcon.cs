// Generates app.ico (multi-size, PNG-compressed entries) for HW Inspector.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float m = s * 0.04f;
            var bg = new RectangleF(m, m, s - 2 * m, s - 2 * m);
            using (var path = Round(bg, s * 0.2f))
            using (var br = new LinearGradientBrush(bg, Color.FromArgb(255, 34, 52, 84), Color.FromArgb(255, 14, 20, 32), 90f))
                g.FillPath(br, path);

            // pins
            float chip = s * 0.46f, c0 = (s - chip) / 2;
            int pins = s >= 48 ? 4 : s >= 24 ? 3 : 2;
            float pinLen = s * 0.1f, pinW = Math.Max(1f, s * 0.045f);
            using (var pb = new SolidBrush(Color.FromArgb(255, 120, 200, 255)))
            {
                for (int i = 0; i < pins; i++)
                {
                    float t = c0 + chip * (i + 1) / (pins + 1) - pinW / 2;
                    g.FillRectangle(pb, t, c0 - pinLen, pinW, pinLen);
                    g.FillRectangle(pb, t, c0 + chip, pinW, pinLen);
                    g.FillRectangle(pb, c0 - pinLen, t, pinLen, pinW);
                    g.FillRectangle(pb, c0 + chip, t, pinLen, pinW);
                }
            }
            // chip body
            var body = new RectangleF(c0, c0, chip, chip);
            using (var path = Round(body, s * 0.06f))
            using (var br = new LinearGradientBrush(body, Color.FromArgb(255, 70, 190, 255), Color.FromArgb(255, 30, 110, 220), 45f))
                g.FillPath(br, path);
            // die
            float die = chip * 0.5f, d0 = (s - die) / 2;
            var dieR = new RectangleF(d0, d0, die, die);
            using (var path = Round(dieR, s * 0.035f))
            using (var br = new SolidBrush(Color.FromArgb(255, 16, 26, 44)))
                g.FillPath(br, path);
            if (s >= 32)
            {
                using (var pen = new Pen(Color.FromArgb(255, 120, 230, 255), Math.Max(1.5f, s * 0.035f)))
                {
                    float a = d0 + die * 0.25f, b = d0 + die * 0.75f, mid = d0 + die * 0.5f;
                    g.DrawLines(pen, new[] { new PointF(a, mid), new PointF(d0 + die * 0.42f, mid), new PointF(d0 + die * 0.52f, a + die * 0.05f),
                                             new PointF(d0 + die * 0.62f, b - die * 0.05f), new PointF(d0 + die * 0.68f, mid), new PointF(b, mid) });
                }
            }
        }
        return bmp;
    }

    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
            using (var b = Draw(s))
            using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
        using (var fs = File.Create(outPath))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
        using (var b = Draw(256)) b.Save(Path.ChangeExtension(outPath, ".png"), ImageFormat.Png);
    }
}
