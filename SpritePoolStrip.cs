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
    /// fixed width regardless of piece count. Uses the actual system
    /// vertical scrollbar width (not a guessed constant) so the host's
    /// AutoScroll panel never ends up a few pixels too narrow and grows an
    /// unwanted horizontal scrollbar alongside its vertical one.</summary>
    public static int PreferredWidth => ThumbWidth + Pad * 2 + SystemInformation.VerticalScrollBarWidth + 2;

    public int PieceCount { get; private set; }
    public int SelectedPiece { get; private set; } = -1;

    /// <summary>piece, row, col (0..11) -> raw 2-bit cell value (0..3).</summary>
    public Func<int, int, int, byte>? PixelProvider { get; set; }
    public Func<int, byte, Color>? PaletteProvider { get; set; }
    /// <summary>piece -> whether it's currently a hires (not multicolour)
    /// piece - see SpriteBank.IsHires. Each piece can independently be
    /// either, so this is checked per piece drawn, not once for the whole
    /// strip.</summary>
    public Func<int, bool>? IsHiresProvider { get; set; }

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

    /// <summary>Repaints just one piece's row instead of the whole strip.</summary>
    public void InvalidatePiece(int piece)
    {
        if (piece < 0 || piece >= PieceCount) return;
        Invalidate(new Rectangle(0, PieceTop(piece), Width, RowStride));
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

            bool hires = IsHiresProvider?.Invoke(p) ?? false;
            if (hires)
            {
                // Same total physical width either way (24 hires dots wide
                // on real hardware, same as 12 double-width multicolour
                // pixels) - just divided into twice as many, half-width,
                // square cells. Each raw cell's high bit is the left hires
                // pixel, low bit the right one (see SpriteBank.GetHiresPixel).
                int hiresCellSize = ThumbWidth / SpriteBank.HiresCols;
                var onBrush = BrushCache.Get(PaletteProvider?.Invoke(p, 2) ?? Color.Magenta);
                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < Cols; c++)
                    {
                        byte v = PixelProvider?.Invoke(p, r, c) ?? 0;
                        if ((v & 2) != 0) g.FillRectangle(onBrush, Pad + (c * 2) * hiresCellSize, thumbTop + r * ThumbCellHeight, hiresCellSize, ThumbCellHeight);
                        if ((v & 1) != 0) g.FillRectangle(onBrush, Pad + (c * 2 + 1) * hiresCellSize, thumbTop + r * ThumbCellHeight, hiresCellSize, ThumbCellHeight);
                    }
                }
            }
            else
            {
                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < Cols; c++)
                    {
                        byte v = PixelProvider?.Invoke(p, r, c) ?? 0;
                        if (v == 0) continue;
                        var b = BrushCache.Get(PaletteProvider?.Invoke(p, v) ?? Color.Magenta);
                        g.FillRectangle(b, Pad + c * ThumbCellWidth, thumbTop + r * ThumbCellHeight, ThumbCellWidth, ThumbCellHeight);
                    }
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
