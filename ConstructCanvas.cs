using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Composites the backdrop picture with the 8 hardware sprites at their
/// CURRENT frame's positions. Frame-agnostic by design - ConstructPanel
/// swaps SpriteX/SpriteY in and out as the active frame changes, so this
/// control only ever sees "the current frame's 8 positions".
///
/// Mouse: plain left/right drag draws on / erases the sprite under the
/// cursor (raised as CellInteract - MainForm applies the edit), Shift+click
/// selects a sprite and Shift+drag moves the selection, Ctrl+click toggles
/// a sprite in/out of the group. Shift-clicking an already-selected member
/// of a multi-sprite selection keeps the whole group selected, so the drag
/// moves everyone together. Middle-drag pans, the wheel zooms.
/// </summary>
internal sealed class ConstructCanvas : Control
{
    private const int SpriteScreenW = 24;
    private const int SpriteScreenH = 21;

    // Pixel-exact PAL screen: the 320x200 display window with the border
    // VICE shows in its "normal" border mode (384x272 visible). The display
    // window starts at sprite coordinate X=24 / Y=50, so the visible frame's
    // top-left is sprite X=-8 / Y=15.
    public const int BorderLeft = 32;
    public const int BorderRight = 32;
    public const int BorderTop = 35;
    public const int BorderBottom = 37;
    public const int FrameWidth = BorderLeft + BackdropPicture.Width + BorderRight;    // 384
    public const int FrameHeight = BorderTop + BackdropPicture.Height + BorderBottom;  // 272
    private const int DisplaySpriteX = 24;
    private const int DisplaySpriteY = 50;

    public float Zoom { get; set; } = 2f;
    public Bitmap? Backdrop { get; set; }
    /// <summary>When false, hides the per-sprite bounding-box border and
    /// index number entirely - a clean look at just the composited art,
    /// e.g. to check how it actually reads over the backdrop.</summary>
    public bool ShowOutlines { get; set; } = true;

    /// <summary>Faint checkerboard over the $d021 background when no backdrop picture is loaded.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>(spriteIndex, row 0..20, col 0..11) -> raw 2-bit cell value 0..3.</summary>
    public Func<int, int, int, byte>? SpritePixel { get; set; }
    /// <summary>(pixel value, spriteIndex) -> preview colour.</summary>
    public Func<byte, int, Color>? PaletteProvider { get; set; }
    /// <summary>spriteIndex -> whether the piece it currently shows is
    /// hires rather than multicolour - see SpriteBank.IsHires.</summary>
    public Func<int, bool>? IsSpriteHires { get; set; }
    /// <summary>spriteIndex -> the label chip text drawn above its bounding
    /// box (e.g. "S3 - #6"), replacing the old separate Sprites table -
    /// falls back to the plain index if unset.</summary>
    public Func<int, string>? SpriteLabel { get; set; }

    /// <summary>Fired after a scroll-wheel zoom actually changes Zoom -
    /// lets ConstructPanel reposition its floating per-sprite inspector,
    /// which tracks a sprite's on-screen rect.</summary>
    public event Action? ZoomChanged;

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

    /// <summary>spriteIndex, row, col, button - a plain (no-modifier) left
    /// or right click/drag landed on that sprite pixel. col is 0..11 for a
    /// multicolour sprite, 0..23 for a hires one (see IsSpriteHires).</summary>
    public event Action<int, int, int, MouseButtons>? CellInteract;
    /// <summary>Bracket one paint drag, so the owner can take one undo
    /// snapshot per piece per stroke rather than per pixel.</summary>
    public event Action? PaintStrokeStarted;
    public event Action? PaintStrokeEnded;

    private bool _painting;
    private MouseButtons _paintButton;
    private (int sprite, int row, int col) _lastPaint = (-1, -1, -1);

    private bool _dragging;
    private int _dragStartMouseX, _dragStartMouseY;
    private readonly Dictionary<int, (int x, int y)> _dragStart = new();

    // Middle-button pan. Tracked in
    // SCREEN coordinates: scrolling the parent moves this control itself,
    // so control-relative mouse positions would shift under the drag.
    private bool _panning;
    private Point _panStartScreen;
    private Point _panStartScroll;

    public ConstructCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.Black;
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer, true);
    }

    private const float MinZoom = 1f;
    private const float MaxZoom = 8f;
    private const float ZoomStep = 0.5f;

    public void ApplyZoomedSize() => Size = new Size((int)Math.Ceiling(FrameWidth * Zoom), (int)Math.Ceiling(FrameHeight * Zoom));

    /// <summary>Sprite coordinate -> frame pixel (unzoomed), border included.</summary>
    private static int FrameX(int spriteX) => spriteX - DisplaySpriteX + BorderLeft;
    private static int FrameY(int spriteY) => spriteY - DisplaySpriteY + BorderTop;

    /// <summary>Public so ConstructPanel's floating per-sprite inspector can
    /// anchor itself to a sprite's current on-screen box.</summary>
    internal Rectangle SpriteRect(int spriteIndex) =>
        new((int)(FrameX(SpriteX[spriteIndex]) * Zoom), (int)(FrameY(SpriteY[spriteIndex]) * Zoom), (int)(SpriteScreenW * Zoom), (int)(SpriteScreenH * Zoom));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;

        var display = new RectangleF(BorderLeft * Zoom, BorderTop * Zoom, BackdropPicture.Width * Zoom, BackdropPicture.Height * Zoom);

        // Same layering as the VIC-II: $d021 background, then the bitmap
        // (its "00" pixels are transparent so $d021 shows through), then
        // sprites, then the $d020 border ON TOP of the sprites - a sprite
        // moved into the border is hidden there, exactly as on the C64.
        g.FillRectangle(BrushCache.Get(EditorPalette.BorderColor), ClientRectangle);
        g.FillRectangle(BrushCache.Get(EditorPalette.BackgroundColor), display);

        if (Backdrop != null)
            g.DrawImage(Backdrop, display);
        else if (ShowGrid)
        {
            // Editing aid only: a faint checker over $d021 (not replacing it),
            // aligned to the 16-pixel character-pair grid of the display.
            var state = g.Save();
            g.SetClip(display);
            var checker = BrushCache.Get(Color.FromArgb(28, 255, 255, 255));
            float cell = 16 * Zoom;
            for (int cy = 0; cy * cell < display.Height; cy++)
                for (int cx = 0; cx * cell < display.Width; cx++)
                    if ((cx + cy) % 2 == 0)
                        g.FillRectangle(checker, display.X + cx * cell, display.Y + cy * cell, cell, cell);
            g.Restore(state);
        }

        for (int s = 0; s < 8; s++) DrawSpritePixels(g, s);

        var border = BrushCache.Get(EditorPalette.BorderColor);
        g.FillRectangle(border, 0, 0, Width, display.Top);
        g.FillRectangle(border, 0, display.Bottom, Width, Height - display.Bottom);
        g.FillRectangle(border, 0, display.Top, display.Left, display.Height);
        g.FillRectangle(border, display.Right, display.Top, Width - display.Right, display.Height);

        // Outlines and labels are an editor overlay, drawn last so a sprite
        // hidden in the border can still be seen and grabbed.
        for (int s = 0; s < 8; s++) DrawSpriteOverlay(g, s);
    }

    private void DrawSpritePixels(Graphics g, int spriteIndex)
    {
        int screenX = FrameX(SpriteX[spriteIndex]);
        int screenY = FrameY(SpriteY[spriteIndex]);

        if (SpritePixel != null && PaletteProvider != null)
        {
            bool hires = IsSpriteHires?.Invoke(spriteIndex) ?? false;
            for (int r = 0; r < 21; r++)
            {
                for (int c = 0; c < 12; c++)
                {
                    byte v = SpritePixel(spriteIndex, r, c);
                    if (hires)
                    {
                        // Each raw cell packs 2 independent hires pixels -
                        // high bit left, low bit right (see SpriteBank's
                        // GetHiresPixel remarks) - both drawn in the same
                        // "Individual" colour hires sprites actually use on
                        // real hardware (one colour register per sprite,
                        // no shared MC1/MC2).
                        if ((v & 2) != 0)
                        {
                            var b1 = BrushCache.Get(PaletteProvider(2, spriteIndex));
                            g.FillRectangle(b1, (screenX + c * 2) * Zoom, (screenY + r) * Zoom, Zoom, Zoom);
                        }
                        if ((v & 1) != 0)
                        {
                            var b2 = BrushCache.Get(PaletteProvider(2, spriteIndex));
                            g.FillRectangle(b2, (screenX + c * 2 + 1) * Zoom, (screenY + r) * Zoom, Zoom, Zoom);
                        }
                    }
                    else
                    {
                        if (v == 0) continue;
                        var brush = BrushCache.Get(PaletteProvider(v, spriteIndex));
                        g.FillRectangle(brush, (screenX + c * 2) * Zoom, (screenY + r) * Zoom, 2 * Zoom, Zoom);
                    }
                }
            }
        }
    }

    private void DrawSpriteOverlay(Graphics g, int spriteIndex)
    {
        if (!ShowOutlines) return;

        bool isPrimary = spriteIndex == PrimarySelected;
        bool isSelected = SelectedSprites.Contains(spriteIndex);
        Color color = isPrimary ? Color.White : isSelected ? Color.FromArgb(255, 200, 60) : Color.FromArgb(120, 180, 255);
        var rect = SpriteRect(spriteIndex);
        using var pen = new Pen(color, isSelected ? 2 : 1);
        g.DrawRectangle(pen, rect);

        // Label chip sits right above the box instead of a number drawn
        // inside it - readable at a glance, and it's what replaces the old
        // separate Sprites table: shows which pool piece this hardware
        // sprite currently plays, right where the sprite actually is.
        string label = SpriteLabel?.Invoke(spriteIndex) ?? spriteIndex.ToString();
        using var labelFont = new Font(Font.FontFamily, 7.5f);
        var textSize = g.MeasureString(label, labelFont);
        var chipRect = new RectangleF(rect.X, rect.Y - textSize.Height, textSize.Width + 6, textSize.Height + 1);
        g.FillRectangle(BrushCache.Get(color), chipRect);
        g.DrawString(label, labelFont, BrushCache.Get(Color.FromArgb(20, 20, 20)), chipRect.X + 3, chipRect.Y);
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

        if (e.Button == MouseButtons.Middle)
        {
            if (scrollParent == null) return;
            _panning = true;
            _panStartScreen = Cursor.Position;
            _panStartScroll = new Point(-scrollParent.AutoScrollPosition.X, -scrollParent.AutoScrollPosition.Y);
            Cursor = Cursors.SizeAll;
            return;
        }

        // Plain left/right = draw on the sprite under the cursor; Shift =
        // select (and drag to move); Ctrl = add/remove from the group.
        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        bool shift = (ModifierKeys & Keys.Shift) != 0;

        if (e.Button == MouseButtons.Left && (ctrl || shift))
        {
            SelectAt(e, ctrl);
            return;
        }
        if (ctrl || shift) return;

        if (e.Button is MouseButtons.Left or MouseButtons.Right)
        {
            _painting = true;
            _paintButton = e.Button;
            _lastPaint = (-1, -1, -1);
            PaintStrokeStarted?.Invoke();
            PaintAt(e.Location, e.Button);
        }
    }

    private void SelectAt(MouseEventArgs e, bool ctrl)
    {
        for (int s = 7; s >= 0; s--)
        {
            if (!SpriteRect(s).Contains(e.Location)) continue;

            if (ctrl)
            {
                if (!SelectedSprites.Remove(s)) SelectedSprites.Add(s);
            }
            else if (!SelectedSprites.Contains(s))
            {
                // Shift-click on a sprite outside the current group starts a new single selection.
                SelectedSprites.Clear();
                SelectedSprites.Add(s);
            }
            // else: Shift-clicking an already-selected member of a multi-selection keeps
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

    /// <summary>Raises CellInteract for the sprite pixel under the cursor,
    /// once per cell. Topmost sprite wins (same order they're drawn in).
    /// Nothing is painted in the border: it covers the sprites there, so
    /// you couldn't see what you drew.</summary>
    private void PaintAt(Point p, MouseButtons button)
    {
        float fx = p.X / Zoom, fy = p.Y / Zoom;
        if (fx < BorderLeft || fx >= BorderLeft + BackdropPicture.Width ||
            fy < BorderTop || fy >= BorderTop + BackdropPicture.Height)
            return;

        for (int s = 7; s >= 0; s--)
        {
            float sx = fx - FrameX(SpriteX[s]);
            float sy = fy - FrameY(SpriteY[s]);
            if (sx < 0 || sx >= SpriteScreenW || sy < 0 || sy >= SpriteScreenH) continue;

            bool hires = IsSpriteHires?.Invoke(s) ?? false;
            int col = hires ? (int)sx : (int)(sx / 2);
            int row = (int)sy;
            if ((s, row, col) == _lastPaint) return;
            _lastPaint = (s, row, col);
            CellInteract?.Invoke(s, row, col, button);
            return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning)
        {
            if (Parent is ScrollableControl scroll)
            {
                var now = Cursor.Position;
                scroll.AutoScrollPosition = new Point(
                    _panStartScroll.X - (now.X - _panStartScreen.X),
                    _panStartScroll.Y - (now.Y - _panStartScreen.Y));
            }
            return;
        }
        if (_painting)
        {
            if ((e.Button & _paintButton) != 0) PaintAt(e.Location, _paintButton);
            return;
        }
        if (!_dragging || SelectedSprites.Count == 0) return;
        int dx = (int)((e.X - _dragStartMouseX) / Zoom);
        int dy = (int)((e.Y - _dragStartMouseY) / Zoom);
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
        if (e.Button == MouseButtons.Middle && _panning)
        {
            _panning = false;
            Cursor = Cursors.Default;
            return;
        }
        if (_painting && e.Button == _paintButton)
        {
            _painting = false;
            PaintStrokeEnded?.Invoke();
        }
        _dragging = false;
    }

    /// <summary>Scroll to zoom. This control has no pan offset of its own:
    /// it just resizes, and the containing AutoScroll panel (ConstructPanel's
    /// canvasScroll) handles bringing whatever's now off-screen back into
    /// view.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // Marks the wheel message as consumed so the containing AutoScroll
        // panel doesn't ALSO scroll its content on the same wheel tick -
        // OnMouseWheel is actually handed a HandledMouseEventArgs even
        // though its own signature only promises the plain base type.
        if (e is HandledMouseEventArgs handled) handled.Handled = true;

        float old = Zoom;
        float newZoom = Math.Clamp(Zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep), MinZoom, MaxZoom);
        if (newZoom == old) return;
        Zoom = newZoom;
        ApplyZoomedSize();

        // Keep the point under the cursor stable: shift the parent's scroll
        // position by how far that point moved.
        if (Parent is ScrollableControl scroll)
        {
            float ratio = newZoom / old;
            int shiftX = (int)Math.Round(e.X * (ratio - 1f));
            int shiftY = (int)Math.Round(e.Y * (ratio - 1f));
            scroll.AutoScrollPosition = new Point(-scroll.AutoScrollPosition.X + shiftX, -scroll.AutoScrollPosition.Y + shiftY);
        }
        Invalidate();
        ZoomChanged?.Invoke();
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
