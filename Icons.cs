using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MagSpriteEd;

/// <summary>
/// Small toolbar icons, drawn programmatically via GDI+ rather than loaded
/// from files - avoids needing to download and bundle image assets (which
/// would need per-file approval and a reliable source), and avoids betting
/// on exact Unicode codepoints in an icon font being correct without a way
/// to visually check right now. Plain geometric glyphs in a single stroke
/// colour, sized for a 20x20 toolbar button.
/// </summary>
internal static class Icons
{
    public const int Size = 20;
    private static readonly Color Stroke = Color.Gainsboro;

    private static Bitmap Make(Action<Graphics> draw)
    {
        var bmp = new Bitmap(Size, Size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        draw(g);
        return bmp;
    }

    public static Bitmap Menu() => Make(g =>
    {
        using var pen = new Pen(Stroke, 2f);
        g.DrawLine(pen, 3, 5, 17, 5);
        g.DrawLine(pen, 3, 10, 17, 10);
        g.DrawLine(pen, 3, 15, 17, 15);
    });

    public static Bitmap Pencil() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, new PointF[] { new(4, 16), new(13, 7), new(16, 4), new(18, 6), new(15, 9), new(6, 18), new(3, 19), new(4, 16) });
    });

    public static Bitmap Bucket() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f) { LineJoin = LineJoin.Round };
        g.DrawPolygon(pen, new PointF[] { new(5, 9), new(11, 3), new(17, 9), new(13, 17), new(8, 17) });
        g.DrawLine(pen, 5, 9, 17, 9);
        using var b = new SolidBrush(Stroke);
        g.FillEllipse(b, 9, 15, 3, 3);
    });

    public static Bitmap Line() => Make(g =>
    {
        using var pen = new Pen(Stroke, 2f);
        g.DrawLine(pen, 4, 16, 16, 4);
        using var b = new SolidBrush(Stroke);
        g.FillEllipse(b, 2, 14, 4, 4);
        g.FillEllipse(b, 14, 2, 4, 4);
    });

    public static Bitmap Mirror() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.4f) { DashStyle = DashStyle.Dot };
        g.DrawLine(pen, 10, 2, 10, 18);
        using var b = new SolidBrush(Stroke);
        g.FillPolygon(b, new PointF[] { new(3, 10), new(8, 6), new(8, 14) });
        g.FillPolygon(b, new PointF[] { new(17, 10), new(12, 6), new(12, 14) });
    });

    public static Bitmap Clear() => Make(g =>
    {
        using var pen = new Pen(Stroke, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, 5, 5, 15, 15);
        g.DrawLine(pen, 15, 5, 5, 15);
    });

    public static Bitmap FlipH() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.4f) { DashStyle = DashStyle.Dot };
        g.DrawLine(pen, 10, 2, 10, 18);
        using var arrowL = new Pen(Stroke, 2f) { EndCap = LineCap.ArrowAnchor };
        g.DrawLine(arrowL, 9, 10, 3, 10);
        using var arrowR = new Pen(Stroke, 2f) { EndCap = LineCap.ArrowAnchor };
        g.DrawLine(arrowR, 11, 10, 17, 10);
    });

    public static Bitmap FlipV() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.4f) { DashStyle = DashStyle.Dot };
        g.DrawLine(pen, 2, 10, 18, 10);
        using var arrowU = new Pen(Stroke, 2f) { EndCap = LineCap.ArrowAnchor };
        g.DrawLine(arrowU, 10, 9, 10, 3);
        using var arrowD = new Pen(Stroke, 2f) { EndCap = LineCap.ArrowAnchor };
        g.DrawLine(arrowD, 10, 11, 10, 17);
    });

    public static Bitmap Duplicate() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawRectangle(pen, 3, 5, 10, 10);
        g.DrawRectangle(pen, 7, 3, 10, 10);
    });

    public static Bitmap FrameInsert() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawRectangle(pen, 2, 4, 11, 13);
        using var plus = new Pen(Stroke, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(plus, 14, 6, 14, 14);
        g.DrawLine(plus, 10, 10, 18, 10);
    });

    public static Bitmap FrameDelete() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawRectangle(pen, 2, 4, 11, 13);
        using var minus = new Pen(Stroke, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(minus, 10, 10, 18, 10);
    });

    public static Bitmap Copy() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawRectangle(pen, 3, 6, 9, 11);
        g.DrawRectangle(pen, 7, 2, 9, 11);
    });

    public static Bitmap Paste() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawRectangle(pen, 4, 4, 12, 14);
        g.DrawRectangle(pen, 7, 2, 6, 3);
    });

    public static Bitmap Wand() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, 4, 16, 14, 6);
        using var b = new SolidBrush(Stroke);
        g.FillPolygon(b, StarPoints(16, 4, 3));
        g.FillEllipse(b, 5, 14, 2, 2);
    });

    private static PointF[] StarPoints(float cx, float cy, float r) => new PointF[]
    {
        new(cx, cy - r), new(cx + r * 0.3f, cy - r * 0.3f), new(cx + r, cy),
        new(cx + r * 0.3f, cy + r * 0.3f), new(cx, cy + r), new(cx - r * 0.3f, cy + r * 0.3f),
        new(cx - r, cy), new(cx - r * 0.3f, cy - r * 0.3f)
    };

    public static Bitmap Undo() => Make(g =>
    {
        using var pen = new Pen(Stroke, 2f) { EndCap = LineCap.ArrowAnchor, StartCap = LineCap.Round };
        g.DrawArc(pen, 4, 4, 12, 12, 90, 200);
    });

    public static Bitmap Redo() => Make(g =>
    {
        using var pen = new Pen(Stroke, 2f) { EndCap = LineCap.ArrowAnchor, StartCap = LineCap.Round };
        g.DrawArc(pen, 4, 4, 12, 12, 90, -200);
    });

    public static Bitmap Play() => Make(g =>
    {
        using var b = new SolidBrush(Stroke);
        g.FillPolygon(b, new PointF[] { new(5, 3), new(5, 17), new(17, 10) });
    });

    public static Bitmap Stop() => Make(g =>
    {
        using var b = new SolidBrush(Stroke);
        g.FillRectangle(b, 5, 5, 4, 10);
        g.FillRectangle(b, 11, 5, 4, 10);
    });

    public static Bitmap Prev() => Make(g =>
    {
        using var b = new SolidBrush(Stroke);
        g.FillRectangle(b, 3, 4, 2, 12);
        g.FillPolygon(b, new PointF[] { new(16, 4), new(16, 16), new(6, 10) });
    });

    public static Bitmap Next() => Make(g =>
    {
        using var b = new SolidBrush(Stroke);
        g.FillRectangle(b, 15, 4, 2, 12);
        g.FillPolygon(b, new PointF[] { new(4, 4), new(4, 16), new(14, 10) });
    });

    public static Bitmap Apply() => Make(g =>
    {
        using var pen = new Pen(Stroke, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new PointF[] { new(3, 10), new(8, 15), new(17, 4) });
    });

    public static Bitmap Swatch(Color c) => Make(g =>
    {
        using var b = new SolidBrush(c);
        g.FillRectangle(b, 2, 2, 16, 16);
        using var pen = new Pen(Color.FromArgb(90, 90, 90), 1f);
        g.DrawRectangle(pen, 2, 2, 15, 15);
    });

    public static Bitmap Picture() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawRectangle(pen, 2, 4, 16, 12);
        g.DrawEllipse(pen, 5, 7, 3, 3);
        g.DrawLines(pen, new PointF[] { new(4, 15), new(10, 9), new(13, 12), new(16, 9), new(17, 10) });
    });

    public static Bitmap New() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawRectangle(pen, 4, 2, 10, 16);
        using var pen2 = new Pen(Stroke, 2f);
        g.DrawLine(pen2, 9, 7, 9, 13);
        g.DrawLine(pen2, 6, 10, 12, 10);
    });

    public static Bitmap FolderOpen() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawLines(pen, new PointF[]
        {
            new(2, 6), new(8, 6), new(9, 4), new(14, 4), new(15, 6),
            new(19, 8), new(17, 16), new(2, 16), new(2, 6)
        });
        g.DrawLine(pen, 2, 6, 5, 8);
        g.DrawLine(pen, 5, 8, 19, 8);
    });

    public static Bitmap Save() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawRectangle(pen, 3, 3, 14, 14);
        g.DrawRectangle(pen, 6, 3, 8, 5);
        g.DrawRectangle(pen, 5, 11, 10, 6);
    });

    public static Bitmap Export() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.8f) { EndCap = LineCap.ArrowAnchor };
        g.DrawLine(pen, 10, 15, 10, 3);
        using var pen2 = new Pen(Stroke, 1.6f);
        g.DrawLine(pen2, 3, 17, 17, 17);
    });

    public static Bitmap Target() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawEllipse(pen, 3, 3, 14, 14);
        g.DrawEllipse(pen, 7, 7, 6, 6);
        using var pen2 = new Pen(Stroke, 1.4f);
        g.DrawLine(pen2, 10, 0, 10, 4);
        g.DrawLine(pen2, 10, 16, 10, 20);
        g.DrawLine(pen2, 0, 10, 4, 10);
        g.DrawLine(pen2, 16, 10, 20, 10);
    });

    public static Bitmap SingleSprite() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawRectangle(pen, 5, 2, 10, 16);
        using var b = new SolidBrush(Stroke);
        g.FillRectangle(b, 7, 5, 2, 2);
        g.FillRectangle(b, 11, 5, 2, 2);
        g.FillRectangle(b, 7, 9, 2, 2);
        g.FillRectangle(b, 11, 9, 2, 2);
        g.FillRectangle(b, 7, 13, 2, 2);
        g.FillRectangle(b, 11, 13, 2, 2);
    });

    public static Bitmap Info() => Make(g =>
    {
        using var pen = new Pen(Stroke, 1.6f);
        g.DrawEllipse(pen, 3, 3, 14, 14);
        using var b = new SolidBrush(Stroke);
        g.FillRectangle(b, 9, 8, 2, 7);
        g.FillEllipse(b, 9, 5, 2, 2);
    });
}
