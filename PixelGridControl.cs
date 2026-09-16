using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Renders a Rows x Cols grid of indexed pixels (0..3) at a fixed cell size
/// and reports which cell the mouse is over. Cells are CellWidth x
/// CellHeight, not necessarily square: a real C64 multicolour sprite pixel
/// is twice as wide as it is tall (each occupies 2 hires dot-widths but only
/// 1 scanline), so CellWidth is normally 2x CellHeight - see MainForm's
/// EditCellWidth/EditCellHeight. Owns no sprite data itself - MainForm
/// supplies PixelProvider/PaletteProvider and applies edits.
/// </summary>
internal sealed class PixelGridControl : Control
{
    public int Rows { get; }
    public int Cols { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public bool Interactive { get; set; } = true;

    /// <summary>Quadrant boundary column/row (0 to disable), drawn as a bold guide line.</summary>
    public int BoldCol { get; set; } = -1;
    public int BoldRow { get; set; } = -1;

    public Func<int, int, byte>? PixelProvider { get; set; }
    public Func<byte, Color>? PaletteProvider { get; set; }

    /// <summary>row, col, button - fired on mouse-down and on drag into a new cell.</summary>
    public event Action<int, int, MouseButtons>? CellInteract;

    private int _lastRow = -1, _lastCol = -1;

    public PixelGridControl(int rows, int cols, int cellWidth, int cellHeight)
    {
        Rows = rows;
        Cols = cols;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Size = new Size(cols * cellWidth + 1, rows * cellHeight + 1);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                byte v = PixelProvider?.Invoke(r, c) ?? 0;
                Color col = v == 0 ? CheckerColor(r, c) : (PaletteProvider?.Invoke(v) ?? Color.Magenta);
                using var b = new SolidBrush(col);
                g.FillRectangle(b, c * CellWidth, r * CellHeight, CellWidth, CellHeight);
            }
        }

        if (CellHeight >= 6)
        {
            using var gridPen = new Pen(Color.FromArgb(70, 70, 70));
            using var boldPen = new Pen(Color.FromArgb(230, 230, 230));
            for (int c = 0; c <= Cols; c++)
            {
                int x = c * CellWidth;
                g.DrawLine(c == BoldCol ? boldPen : gridPen, x, 0, x, Rows * CellHeight);
            }
            for (int r = 0; r <= Rows; r++)
            {
                int y = r * CellHeight;
                g.DrawLine(r == BoldRow ? boldPen : gridPen, 0, y, Cols * CellWidth, y);
            }
        }

        using var border = new Pen(Color.FromArgb(140, 140, 140));
        g.DrawRectangle(border, 0, 0, Cols * CellWidth, Rows * CellHeight);
    }

    private static Color CheckerColor(int r, int c) =>
        ((r / 2 + c / 2) % 2 == 0) ? Color.FromArgb(38, 38, 38) : Color.FromArgb(52, 52, 52);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _lastRow = -1; _lastCol = -1;
        if (Interactive) HandleMouse(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Interactive && e.Button != MouseButtons.None) HandleMouse(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _lastRow = -1; _lastCol = -1;
    }

    private void HandleMouse(MouseEventArgs e)
    {
        int col = e.X / CellWidth;
        int row = e.Y / CellHeight;
        if (col < 0 || col >= Cols || row < 0 || row >= Rows) return;
        if (row == _lastRow && col == _lastCol) return;
        _lastRow = row; _lastCol = col;
        CellInteract?.Invoke(row, col, e.Button);
    }
}
