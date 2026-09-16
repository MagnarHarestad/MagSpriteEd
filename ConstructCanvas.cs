using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FireSpriteEditor;

/// <summary>
/// Composites the backdrop picture with the 8 hardware sprites at their
/// CURRENT frame's positions, and lets the user drag/nudge them. Frame-
/// agnostic by design - ConstructPanel swaps SpriteX/SpriteY in and out as
/// the active frame changes, so this control only ever sees "the current
/// frame's 8 positions".
///
/// Supports grouping: Ctrl+click toggles a sprite in/out of the selection,
/// and clicking (without Ctrl) an already-selected member of a multi-sprite
/// selection keeps the whole group selected instead of collapsing it to
/// one - that's what lets a drag started on any member move the whole
/// group together, preserving everyone's relative offsets.
/// </summary>
internal sealed class ConstructCanvas : Control
{
    private const int SpriteScreenW = 24;
    private const int SpriteScreenH = 21;

    public int Zoom { get; set; } = 2;
    public Bitmap? Backdrop { get; set; }
    /// <summary>When false, hides the per-sprite bounding-box border and
    /// index number entirely - a clean look at just the composited art,
    /// e.g. to check how it actually reads over the backdrop.</summary>
    public bool ShowOutlines { get; set; } = true;

    /// <summary>(spriteIndex, row 0..20, col 0..11) -> pixel value 0..3.</summary>
    public Func<int, int, int, byte>? SpritePixel { get; set; }
    /// <summary>(pixel value, spriteIndex) -> preview colour.</summary>
    public Func<byte, int, Color>? PaletteProvider { get; set; }

    public readonly int[] SpriteX = new int[8];
    public readonly int[] SpriteY = new int[8];

    /// <summary>All sprites currently part of the selection/group (possibly just one).</summary>
    public readonly HashSet<int> SelectedSprites = new();
    /// <summary>The most recently clicked member - whose X/Y/quad/offset the side panel edits.</summary>
    public int PrimarySelected { get; set; } = -1;

    /// <summary>Fired whenever the selection set or primary changes.</summary>
    public event Action? SelectionChanged;
    /// <summary>Fired whenever any selected sprite's position changes (drag or nudge).</summary>
    public event Action? SpriteMoved;

    private bool _dragging;
    private int _dragStartMouseX, _dragStartMouseY;
    private readonly Dictionary<int, (int x, int y)> _dragStart = new();

    public ConstructCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.Black;
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void ApplyZoomedSize() => Size = new Size(BackdropPicture.Width * Zoom, BackdropPicture.Height * Zoom);

    private Rectangle SpriteRect(int spriteIndex) =>
        new((SpriteX[spriteIndex] - 24) * Zoom, (SpriteY[spriteIndex] - 50) * Zoom, SpriteScreenW * Zoom, SpriteScreenH * Zoom);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;

        if (Backdrop != null)
            g.DrawImage(Backdrop, new Rectangle(0, 0, BackdropPicture.Width * Zoom, BackdropPicture.Height * Zoom));
        else
        {
            using var b = new SolidBrush(Color.FromArgb(24, 24, 24));
            g.FillRectangle(b, ClientRectangle);
            using var checker = new SolidBrush(Color.FromArgb(34, 34, 34));
            for (int y = 0; y < Height; y += 16 * Zoom)
                for (int x = 0; x < Width; x += 16 * Zoom)
                    if (((x / (16 * Zoom)) + (y / (16 * Zoom))) % 2 == 0)
                        g.FillRectangle(checker, x, y, 16 * Zoom, 16 * Zoom);
        }

        for (int s = 0; s < 8; s++) DrawSprite(g, s);
    }

    private void DrawSprite(Graphics g, int spriteIndex)
    {
        int screenX = SpriteX[spriteIndex] - 24;
        int screenY = SpriteY[spriteIndex] - 50;

        if (SpritePixel != null && PaletteProvider != null)
        {
            for (int r = 0; r < 21; r++)
            {
                for (int c = 0; c < 12; c++)
                {
                    byte v = SpritePixel(spriteIndex, r, c);
                    if (v == 0) continue;
                    using var brush = new SolidBrush(PaletteProvider(v, spriteIndex));
                    g.FillRectangle(brush, (screenX + c * 2) * Zoom, (screenY + r) * Zoom, 2 * Zoom, Zoom);
                }
            }
        }

        if (!ShowOutlines) return;

        bool isPrimary = spriteIndex == PrimarySelected;
        bool isSelected = SelectedSprites.Contains(spriteIndex);
        Color color = isPrimary ? Color.White : isSelected ? Color.FromArgb(255, 200, 60) : Color.FromArgb(120, 180, 255);
        var rect = SpriteRect(spriteIndex);
        using var pen = new Pen(color, isSelected ? 2 : 1);
        g.DrawRectangle(pen, rect);
        using var idxBrush = new SolidBrush(color);
        g.DrawString(spriteIndex.ToString(), Font, idxBrush, rect.X + 2, rect.Y + 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // Focus() below can make the containing AutoScroll panel
        // "helpfully" scroll this control into view (WinForms' own
        // focus-follow behaviour, ScrollControlIntoView) - only ever
        // noticeable on the very first click, since afterwards the panel
        // already considers the canvas in view. This control's own
        // position within that panel is fixed (see ConstructPanel.BuildUi),
        // so undo any such nudge rather than let the whole composited view
        // (backdrop + every sprite) visibly shift by a pixel or two.
        var scrollParent = Parent as ScrollableControl;
        Point? before = scrollParent?.AutoScrollPosition;
        Focus();
        if (scrollParent != null && before is { } p && scrollParent.AutoScrollPosition != p)
            scrollParent.AutoScrollPosition = new Point(-p.X, -p.Y);

        bool ctrl = (ModifierKeys & Keys.Control) != 0;

        for (int s = 7; s >= 0; s--)
        {
            if (!SpriteRect(s).Contains(e.Location)) continue;

            if (ctrl)
            {
                if (!SelectedSprites.Remove(s)) SelectedSprites.Add(s);
            }
            else if (!SelectedSprites.Contains(s))
            {
                // Fresh click on a sprite outside the current group starts a new single selection.
                SelectedSprites.Clear();
                SelectedSprites.Add(s);
            }
            // else: clicking an already-selected member of a multi-selection keeps
            // the whole group selected, so the drag below moves everyone together.
            PrimarySelected = s;

            if (SelectedSprites.Contains(s))
            {
                _dragging = true;
                _dragStartMouseX = e.X; _dragStartMouseY = e.Y;
                _dragStart.Clear();
                foreach (var sel in SelectedSprites) _dragStart[sel] = (SpriteX[sel], SpriteY[sel]);
            }
            SelectionChanged?.Invoke();
            Invalidate();
            return;
        }

        if (!ctrl)
        {
            SelectedSprites.Clear();
            PrimarySelected = -1;
            SelectionChanged?.Invoke();
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || SelectedSprites.Count == 0) return;
        int dx = (e.X - _dragStartMouseX) / Zoom;
        int dy = (e.Y - _dragStartMouseY) / Zoom;
        foreach (var s in SelectedSprites)
        {
            var (sx, sy) = _dragStart[s];
            SpriteX[s] = Clamp(sx + dx, 0, 511);
            SpriteY[s] = Clamp(sy + dy, 0, 255);
        }
        Invalidate();
        SpriteMoved?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down ||
        keyData is (Keys.Left | Keys.Shift) or (Keys.Right | Keys.Shift) or (Keys.Up | Keys.Shift) or (Keys.Down | Keys.Shift) ||
        base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (SelectedSprites.Count == 0) return;
        int step = e.Shift ? 8 : 1;
        int dx = 0, dy = 0;
        switch (e.KeyCode)
        {
            case Keys.Left: dx = -step; break;
            case Keys.Right: dx = step; break;
            case Keys.Up: dy = -step; break;
            case Keys.Down: dy = step; break;
            default: return;
        }
        foreach (var s in SelectedSprites)
        {
            SpriteX[s] = Clamp(SpriteX[s] + dx, 0, 511);
            SpriteY[s] = Clamp(SpriteY[s] + dy, 0, 255);
        }
        Invalidate();
        SpriteMoved?.Invoke();
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
