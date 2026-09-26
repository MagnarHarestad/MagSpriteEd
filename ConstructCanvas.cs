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

    /// <summary>Previews the VIC open-border trick: the $d020 border is not
    /// drawn, so sprites placed out in the border area stay visible (over
    /// $d021, as the opened border shows). A faint dashed line still marks
    /// where the normal display window ends.</summary>
    public bool OpenBorder { get; set; }

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
    private Point _lastDragMouse;
    // Alt was used for glue in the current/last drag - its key-up is then
    // swallowed so Windows doesn't enter menu mode and eat the next click.
    private bool _altUsedInDrag;
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

    // Cached static background layer (border fill, $d021, backdrop picture or
    // checker grid). Redrawing it from scratch on every paint - hundreds of
    // alpha-blended checker cells, or a scaled backdrop, over a canvas that
    // can be thousands of pixels wide at high zoom - is what made dragging
    // and panning lag. It's rebuilt only when something it depends on
    // changes (see BackgroundKey); each paint just blits the visible part.
    private Bitmap? _background;
    private (float zoom, Size size, Bitmap? backdrop, bool grid, bool open, int border, int bg) _backgroundKey;

    private (float, Size, Bitmap?, bool, bool, int, int) BackgroundKey() =>
        (Zoom, ClientSize, Backdrop, ShowGrid, OpenBorder, EditorPalette.BorderColor.ToArgb(), EditorPalette.BackgroundColor.ToArgb());

    private Bitmap GetBackground(RectangleF display)
    {
        var key = BackgroundKey();
        if (_background != null && key == _backgroundKey) return _background;

        _background?.Dispose();
        var bmp = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;

            // With the border opened, the whole frame is background.
            g.FillRectangle(BrushCache.Get(OpenBorder ? EditorPalette.BackgroundColor : EditorPalette.BorderColor), 0, 0, bmp.Width, bmp.Height);
            g.FillRectangle(BrushCache.Get(EditorPalette.BackgroundColor), display);

            if (Backdrop != null)
                g.DrawImage(Backdrop, display);
            else if (ShowGrid)
            {
                // Editing aid only: a faint checker over $d021 (not replacing it),
                // aligned to the 16-pixel character-pair grid of the display.
                g.SetClip(display);
                var checker = BrushCache.Get(Color.FromArgb(28, 255, 255, 255));
                float cell = 16 * Zoom;
                for (int cy = 0; cy * cell < display.Height; cy++)
                    for (int cx = 0; cx * cell < display.Width; cx++)
                        if ((cx + cy) % 2 == 0)
                            g.FillRectangle(checker, display.X + cx * cell, display.Y + cy * cell, cell, cell);
            }
        }
        _background = bmp;
        _backgroundKey = key;
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _background?.Dispose(); _background = null;
            _labelFont?.Dispose(); _labelFont = null;
        }
        base.Dispose(disposing);
    }

    // Label chip font, created once instead of per sprite per paint.
    private Font? _labelFont;
    private Font LabelFont => _labelFont ??= new Font(Font.FontFamily, 7.5f);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _labelFont?.Dispose();
        _labelFont = null;
        _labelSizes.Clear();
    }

    // Measured label chip text sizes, keyed by label ("S3 - #6"). Lets
    // LabelChipRect work outside of paint (for click hit-testing) and give
    // exactly the same box the paint drew.
    private readonly Dictionary<string, SizeF> _labelSizes = new();

    private SizeF MeasureLabel(Graphics? g, string label)
    {
        if (_labelSizes.TryGetValue(label, out var size)) return size;
        if (g != null) size = g.MeasureString(label, LabelFont);
        else { using var cg = CreateGraphics(); size = cg.MeasureString(label, LabelFont); }
        _labelSizes[label] = size;
        return size;
    }

    /// <summary>The "S3 - #6" label chip sitting just above a sprite's box.
    /// g is the paint Graphics when drawing, null when hit-testing.</summary>
    private RectangleF LabelChipRect(Graphics? g, int spriteIndex)
    {
        var rect = SpriteRect(spriteIndex);
        string label = SpriteLabel?.Invoke(spriteIndex) ?? spriteIndex.ToString();
        var size = MeasureLabel(g, label);
        return new RectangleF(rect.X, rect.Y - size.Height, size.Width + 6, size.Height + 1);
    }

    /// <summary>The sprite whose label chip is under p, or -1 (topmost
    /// first, matching draw order). Chips only exist while outlines show.
    /// Clicking a chip grabs that sprite for moving - see OnMouseDown.</summary>
    public int LabelChipAt(Point p)
    {
        if (!ShowOutlines) return -1;
        for (int s = 7; s >= 0; s--)
            if (LabelChipRect(null, s).Contains(p)) return s;
        return -1;
    }

    /// <summary>Everything a sprite draws: its box, plus the label chip
    /// above it (which can be wider than the box at low zoom) and the 2px
    /// selection pen. Used both to skip sprites outside a paint's clip and
    /// to invalidate only what a move actually changed.</summary>
    private Rectangle DirtyRect(int spriteIndex)
    {
        var r = SpriteRect(spriteIndex);
        return Rectangle.FromLTRB(r.Left - 3, r.Top - 18, Math.Max(r.Right, r.Left + 80) + 3, r.Bottom + 3);
    }

    /// <summary>Repaints one sprite's area only (e.g. after a pixel edit).</summary>
    public void InvalidateSprite(int spriteIndex) => Invalidate(DirtyRect(spriteIndex));

    /// <summary>Moves the given sprites by (dx, dy), clamped to the valid
    /// register range, repainting only the areas they left and entered
    /// instead of the whole (possibly very large) canvas.</summary>
    private void MoveSprites(IEnumerable<int> sprites, Func<int, (int x, int y)> newPosition)
    {
        Region? dirty = null;
        foreach (var s in sprites)
        {
            var (x, y) = newPosition(s);
            x = Clamp(x, 0, 511);
            y = Clamp(y, 0, 255);
            if (x == SpriteX[s] && y == SpriteY[s]) continue;
            dirty ??= new Region(Rectangle.Empty);
            dirty.Union(DirtyRect(s));
            SpriteX[s] = x;
            SpriteY[s] = y;
            dirty.Union(DirtyRect(s));
        }
        if (dirty == null) return; // nothing actually moved
        Invalidate(dirty);
        dirty.Dispose();
        SpriteMoved?.Invoke();
    }

    // The whole client area is painted in OnPaint (the cached background
    // covers it), so skip the default background erase - it only adds a
    // second full-size fill per paint.
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        var display = new RectangleF(BorderLeft * Zoom, BorderTop * Zoom, BackdropPicture.Width * Zoom, BackdropPicture.Height * Zoom);

        // Same layering as the VIC-II: $d021 background, then the bitmap
        // (its "00" pixels are transparent so $d021 shows through), then
        // sprites, then the $d020 border ON TOP of the sprites - a sprite
        // moved into the border is hidden there, exactly as on the C64.
        // The first two layers come from the cached background; only the
        // part inside the clip rectangle is copied, 1:1 with no scaling.
        var clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(GetBackground(display), clip, clip, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;

        // Only sprites overlapping the area being repainted.
        for (int s = 0; s < 8; s++)
            if (DirtyRect(s).IntersectsWith(clip)) DrawSpritePixels(g, s);

        if (OpenBorder)
        {
            // The display-window edge is an editor outline like the sprite
            // boxes, so the same Show/Hide Outlines toggle controls it.
            if (ShowOutlines)
            {
                using var edge = new Pen(Color.FromArgb(110, 255, 255, 255)) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(edge, display.X, display.Y, display.Width - 1, display.Height - 1);
            }
        }
        else
        {
            var border = BrushCache.Get(EditorPalette.BorderColor);
            g.FillRectangle(border, 0, 0, Width, display.Top);
            g.FillRectangle(border, 0, display.Bottom, Width, Height - display.Bottom);
            g.FillRectangle(border, 0, display.Top, display.Left, display.Height);
            g.FillRectangle(border, display.Right, display.Top, Width - display.Right, display.Height);
        }

        // Outlines and labels are an editor overlay, drawn last so a sprite
        // hidden in the border can still be seen and grabbed.
        for (int s = 0; s < 8; s++)
            if (DirtyRect(s).IntersectsWith(clip)) DrawSpriteOverlay(g, s);
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
        // Clicking the chip grabs the sprite for moving (see OnMouseDown).
        string label = SpriteLabel?.Invoke(spriteIndex) ?? spriteIndex.ToString();
        var chipRect = LabelChipRect(g, spriteIndex);
        g.FillRectangle(BrushCache.Get(color), chipRect);
        g.DrawString(label, LabelFont, BrushCache.Get(Color.FromArgb(20, 20, 20)), chipRect.X + 3, chipRect.Y);
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
        // Left-clicking a sprite's label chip grabs it like Shift+click.
        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        bool shift = (ModifierKeys & Keys.Shift) != 0;

        if (e.Button == MouseButtons.Left)
        {
            int chip = LabelChipAt(e.Location);
            if (chip >= 0)
            {
                SelectSprite(chip, e, ctrl);
                return;
            }
        }

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
            SelectSprite(s, e, ctrl);
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

    /// <summary>Selects sprite s (Ctrl toggles it in/out of the group
    /// instead) and starts a move drag if it ended up selected.</summary>
    private void SelectSprite(int s, MouseEventArgs e, bool ctrl)
    {
        if (ctrl)
        {
            if (!SelectedSprites.Remove(s)) SelectedSprites.Add(s);
        }
        else if (!SelectedSprites.Contains(s))
        {
            // Grabbing a sprite outside the current group starts a new single selection.
            SelectedSprites.Clear();
            SelectedSprites.Add(s);
        }
        // else: grabbing an already-selected member of a multi-selection keeps
        // the whole group selected, so the drag below moves everyone together.
        PrimarySelected = s;

        if (SelectedSprites.Contains(s))
        {
            _dragging = true;
            _altUsedInDrag = false;
            _lastDragMouse = e.Location;
            _dragStartMouseX = e.X; _dragStartMouseY = e.Y;
            _dragStart.Clear();
            foreach (var sel in SelectedSprites) _dragStart[sel] = (SpriteX[sel], SpriteY[sel]);
        }
        SelectionChanged?.Invoke();
        Invalidate();
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
        if (!_dragging)
        {
            // Hint that a label chip can be grabbed.
            var cursor = LabelChipAt(e.Location) >= 0 ? Cursors.SizeAll : Cursors.Default;
            if (Cursor != cursor) Cursor = cursor;
            return;
        }
        UpdateDrag(e.Location);
    }

    /// <summary>Positions the dragged selection for the given mouse point.
    /// While Alt is held it snaps, as one block, to the nearest spot glued
    /// flush against another sprite (the same placement Shift+G picks - see
    /// SpriteGlue) measured from where the plain drag would put it, so the
    /// glue follows the mouse; without Alt it moves freely.</summary>
    private void UpdateDrag(Point mouse)
    {
        if (!_dragging || SelectedSprites.Count == 0) return;
        _lastDragMouse = mouse;
        int dx = (int)((mouse.X - _dragStartMouseX) / Zoom);
        int dy = (int)((mouse.Y - _dragStartMouseY) / Zoom);

        if ((ModifierKeys & Keys.Alt) != 0 && SelectedSprites.Count < 8)
        {
            _altUsedInDrag = true;
            var xs = (int[])SpriteX.Clone();
            var ys = (int[])SpriteY.Clone();
            foreach (var s in SelectedSprites)
            {
                xs[s] = Clamp(_dragStart[s].x + dx, 0, 511);
                ys[s] = Clamp(_dragStart[s].y + dy, 0, 255);
            }
            var glue = SpriteGlue.FindBestMove(xs, ys, SelectedSprites);
            int gx = glue?.Dx ?? 0, gy = glue?.Dy ?? 0;
            MoveSprites(SelectedSprites, s => (xs[s] + gx, ys[s] + gy));
            return;
        }
        MoveSprites(SelectedSprites, s => (_dragStart[s].x + dx, _dragStart[s].y + dy));
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
        // Alt pressed mid-drag: snap to glue right away, without waiting for
        // the mouse to move.
        if (e.KeyCode == Keys.Menu && _dragging)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            UpdateDrag(_lastDragMouse);
            return;
        }
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
        MoveSprites(SelectedSprites, s => (SpriteX[s] + dx, SpriteY[s] + dy));
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode != Keys.Menu || !(_dragging || _altUsedInDrag)) return;
        // Handled key-up keeps Windows from entering menu mode on a lone Alt.
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (_dragging) UpdateDrag(_lastDragMouse); // back to free movement
        else _altUsedInDrag = false;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
