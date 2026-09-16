using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FireSpriteEditor;

/// <summary>
/// Shows the backdrop with all 8 hardware sprites at their Construct-panel
/// positions, zoomed in, so sprite pixels can be edited "in place" instead
/// of only on an isolated flat 24x42 block. Pan by holding the middle
/// mouse button and dragging; zoom with the wheel. The backdrop itself is
/// never editable - only a click that lands on a sprite raises
/// CellInteract, exactly mirroring PixelGridControl's contract but with
/// (spriteIndex, row, col) instead of a flat (row, col), since which
/// SpriteBank frame/quadrant a click actually means depends on which
/// sprite it landed on.
/// </summary>
internal sealed class PositionedEditCanvas : Control
{
    private const int SpriteScreenW = 24;
    private const int SpriteScreenH = 21;

    public float Zoom { get; set; } = 4f;
    public Bitmap? Backdrop { get; set; }

    /// <summary>spriteIndex -> (x, y) in VIC sprite-register space.</summary>
    public Func<int, (int x, int y)>? PositionProvider { get; set; }
    /// <summary>(spriteIndex, row 0..20, col 0..11) -> pixel value 0..3.</summary>
    public Func<int, int, int, byte>? SpritePixel { get; set; }
    /// <summary>(pixel value, spriteIndex) -> colour.</summary>
    public Func<byte, int, Color>? PaletteProvider { get; set; }

    /// <summary>spriteIndex, row, col, mouse button - only raised for a hit on a sprite.</summary>
    public event Action<int, int, int, MouseButtons>? CellInteract;

    private float _panX = 40, _panY = 20;
    private bool _panning;
    private int _panStartMouseX, _panStartMouseY;
    private float _panStartX, _panStartY;

    private int _lastRow = -1, _lastCol = -1, _lastSprite = -1;

    public PositionedEditCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(10, 10, 10);
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
    }

    private Rectangle SpriteRect(int spriteIndex)
    {
        if (PositionProvider == null) return Rectangle.Empty;
        var (x, y) = PositionProvider(spriteIndex);
        float screenX = (x - 24) * Zoom + _panX;
        float screenY = (y - 50) * Zoom + _panY;
        return new Rectangle((int)screenX, (int)screenY, (int)(SpriteScreenW * Zoom), (int)(SpriteScreenH * Zoom));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;

        using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

        if (Backdrop != null)
            g.DrawImage(Backdrop, new RectangleF(_panX, _panY, BackdropPicture.Width * Zoom, BackdropPicture.Height * Zoom));

        if (SpritePixel == null || PaletteProvider == null || PositionProvider == null) return;

        for (int s = 0; s < 8; s++)
        {
            var rect = SpriteRect(s);
            var (x, y) = PositionProvider(s);
            float screenX = (x - 24) * Zoom + _panX;
            float screenY = (y - 50) * Zoom + _panY;
            for (int r = 0; r < 21; r++)
            {
                for (int c = 0; c < 12; c++)
                {
                    byte v = SpritePixel(s, r, c);
                    if (v == 0) continue;
                    using var brush = new SolidBrush(PaletteProvider(v, s));
                    g.FillRectangle(brush, screenX + c * 2 * Zoom, screenY + r * Zoom, 2 * Zoom, Zoom);
                }
            }
            using var pen = new Pen(Color.FromArgb(120, 180, 255), 1);
            g.DrawRectangle(pen, rect);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Middle)
        {
            _panning = true;
            _panStartMouseX = e.X; _panStartMouseY = e.Y;
            _panStartX = _panX; _panStartY = _panY;
            return;
        }
        HandlePaintHit(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning)
        {
            _panX = _panStartX + (e.X - _panStartMouseX);
            _panY = _panStartY + (e.Y - _panStartMouseY);
            Invalidate();
            return;
        }
        if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            HandlePaintHit(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle) _panning = false;
        _lastRow = -1; _lastCol = -1; _lastSprite = -1;
    }

    private void HandlePaintHit(MouseEventArgs e)
    {
        for (int s = 7; s >= 0; s--)
        {
            var rect = SpriteRect(s);
            if (!rect.Contains(e.Location)) continue;
            int col = Math.Clamp((int)((e.X - rect.X) / Zoom / 2), 0, 11);
            int row = Math.Clamp((int)((e.Y - rect.Y) / Zoom), 0, 20);
            if (s == _lastSprite && row == _lastRow && col == _lastCol) return;
            _lastSprite = s; _lastRow = row; _lastCol = col;
            CellInteract?.Invoke(s, row, col, e.Button);
            return;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float old = Zoom;
        Zoom = Math.Clamp(Zoom + (e.Delta > 0 ? 0.5f : -0.5f), 1f, 10f);
        // Keep the point under the cursor stable while zooming.
        _panX = e.X - (e.X - _panX) * (Zoom / old);
        _panY = e.Y - (e.Y - _panY) * (Zoom / old);
        Invalidate();
    }
}
