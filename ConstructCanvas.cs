using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MagSpriteEd;

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

    public float Zoom { get; set; } = 2f;
    public Bitmap? Backdrop { get; set; }
    /// <summary>When false, hides the per-sprite bounding-box border and
    /// index number entirely - a clean look at just the composited art,
    /// e.g. to check how it actually reads over the backdrop.</summary>
    public bool ShowOutlines { get; set; } = true;

    /// <summary>Checkerboard grid shown behind the sprites when no backdrop picture is loaded.</summary>
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

    private bool _dragging;
    private int _dragStartMouseX, _dragStartMouseY;
    private readonly Dictionary<int, (int x, int y)> _dragStart = new();

    // Middle-button pan (same gesture as PositionedEditCanvas). Tracked in
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

    public void ApplyZoomedSize() => Size = new Size((int)Math.Ceiling(BackdropPicture.Width * Zoom), (int)Math.Ceiling(BackdropPicture.Height * Zoom));

    /// <summary>Public so ConstructPanel's floating per-sprite inspector can
    /// anchor itself to a sprite's current on-screen box.</summary>
    internal Rectangle SpriteRect(int spriteIndex) =>
        new((int)((SpriteX[spriteIndex] - 24) * Zoom), (int)((SpriteY[spriteIndex] - 50) * Zoom), (int)(SpriteScreenW * Zoom), (int)(SpriteScreenH * Zoom));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;

        if (Backdrop != null)
            g.DrawImage(Backdrop, new RectangleF(0, 0, BackdropPicture.Width * Zoom, BackdropPicture.Height * Zoom));
        else
        {
            using var b = new SolidBrush(Color.FromArgb(24, 24, 24));
            g.FillRectangle(b, ClientRectangle);
            if (ShowGrid)
            {
                using var checker = new SolidBrush(Color.FromArgb(34, 34, 34));
                float cell = 16 * Zoom;
                for (int cy = 0; cy * cell < Height; cy++)
                    for (int cx = 0; cx * cell < Width; cx++)
                        if ((cx + cy) % 2 == 0)
                            g.FillRectangle(checker, cx * cell, cy * cell, cell, cell);
            }
        }

        for (int s = 0; s < 8; s++) DrawSprite(g, s);
    }

    private void DrawSprite(Graphics g, int spriteIndex)
    {
        int screenX = SpriteX[spriteIndex] - 24;
        int screenY = SpriteY[spriteIndex] - 50;

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
        _dragging = false;
    }

    /// <summary>Replaces the old Zoom combo box that lived in the removed
    /// Backdrop panel - scroll to zoom, same as PositionedEditCanvas
    /// already does. This control has no pan offset of its own (unlike
    /// PositionedEditCanvas): it just resizes, and the containing AutoScroll
    /// panel (ConstructPanel's canvasScroll) handles bringing whatever's now
    /// off-screen back into view.</summary>
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
