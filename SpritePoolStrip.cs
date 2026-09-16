using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Vertical list of every piece in the SpriteBank's flat pool, each drawn as
/// a small thumbnail - lets Single Sprite View show what else is in the
/// bank alongside the zoomed edit canvas, live-updating as you draw, and
/// jump straight to editing any of them. Purely a picker: it owns no pixel
/// data itself, just renders whatever MainForm's PixelProvider/
/// PaletteProvider report and reports clicks back via PieceClicked.
///
/// Sized to sit inside an AutoScroll host Panel (like PixelGridControl does
/// in MainForm's _canvasScroll) - this control's own Height grows with the
/// piece count, and OnPaint only actually draws whatever thumbnails fall
/// within the current clip rectangle, so drawing cost tracks how many rows
/// are actually visible on screen, not the total pool size.
/// </summary>
internal sealed class SpritePoolStrip : Control
{
    private const int Cols = SpriteBank.QuadCols;
    private const int Rows = SpriteBank.QuadRows;
    // A real C64 multicolour pixel is twice as wide as it is tall (see
    // PixelGridControl's own remarks) - ThumbCellWidth is exactly double
    // ThumbCellHeight so these thumbnails match the big edit canvas's shape.
    private const int ThumbCellHeight = 6;
    private const int ThumbCellWidth = ThumbCellHeight * 2;
    private const int LabelHeight = 14;
    private const int Pad = 8;
    private const int Gap = 10;

    private const int ThumbWidth = Cols * ThumbCellWidth;
    private const int ThumbHeight = Rows * ThumbCellHeight;
    private const int RowStride = LabelHeight + ThumbHeight + Gap;

    /// <summary>Width this control wants - the host lays it out at this
    /// fixed width (plus room for the scrollbar) regardless of piece count.</summary>
    public const int PreferredWidth = ThumbWidth + Pad * 2 + 22;

    public int PieceCount { get; private set; }
    public int SelectedPiece { get; private set; } = -1;

    /// <summary>piece, row, col -> pixel value (0..3).</summary>
    public Func<int, int, int, byte>? PixelProvider { get; set; }
    public Func<byte, Color>? PaletteProvider { get; set; }

    /// <summary>Raised on left-click of a thumbnail, with its piece index.</summary>
    public event Action<int>? PieceClicked;

    public SpritePoolStrip()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(12, 12, 12);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Width = PreferredWidth;
    }

    public void SetPieceCount(int count)
    {
        if (count == PieceCount) return;
        PieceCount = count;
        Height = Math.Max(1, Pad + count * RowStride);
        Invalidate();
    }

    public void SetSelected(int piece)
    {
        if (piece == SelectedPiece) return;
        SelectedPiece = piece;
        Invalidate();
    }

    /// <summary>Top/bottom Y of a piece's whole row (label + thumbnail), in
    /// this control's own coordinates - used by the host panel to scroll a
    /// newly-selected piece into view.</summary>
    public int PieceTop(int piece) => Pad + piece * RowStride;
    public int PieceBottom(int piece) => PieceTop(piece) + RowStride - Gap;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        if (PieceCount == 0 || RowStride <= 0) return;
        var clip = e.ClipRectangle;
        int first = Math.Max(0, (clip.Top - Pad) / RowStride);
        int last = Math.Min(PieceCount - 1, (clip.Bottom - Pad) / RowStride);

        using var font = new Font(Font.FontFamily, 7.5f);
        for (int p = first; p <= last; p++)
        {
            int top = PieceTop(p);
            bool selected = p == SelectedPiece;

            using (var textBrush = new SolidBrush(selected ? Color.White : Color.Gainsboro))
                g.DrawString("#" + p, font, textBrush, Pad, top);

            int thumbTop = top + LabelHeight;
            // Flat background rather than PixelGridControl's checkerboard -
            // at this small a size a checker pattern reads as visual noise
            // (almost a grid in its own right), and these are meant to be
            // read as a plain colour silhouette, not edited directly.
            using (var bg = new SolidBrush(Color.FromArgb(30, 30, 30)))
                g.FillRectangle(bg, Pad, thumbTop, ThumbWidth, ThumbHeight);
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    byte v = PixelProvider?.Invoke(p, r, c) ?? 0;
                    if (v == 0) continue;
                    using var b = new SolidBrush(PaletteProvider?.Invoke(v) ?? Color.Magenta);
                    g.FillRectangle(b, Pad + c * ThumbCellWidth, thumbTop + r * ThumbCellHeight, ThumbCellWidth, ThumbCellHeight);
                }
            }

            using var border = new Pen(selected ? Color.FromArgb(120, 160, 210) : Color.FromArgb(90, 90, 90), selected ? 2 : 1);
            g.DrawRectangle(border, Pad - 1, thumbTop - 1, ThumbWidth + 1, ThumbHeight + 1);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || RowStride <= 0) return;
        int p = (e.Y - Pad) / RowStride;
        if (p < 0 || p >= PieceCount) return;
        PieceClicked?.Invoke(p);
    }
}
