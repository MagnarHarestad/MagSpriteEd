using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Slim dark vertical scrollbar - the native one is light-themed and
/// sits on the wrong side for the sprite pool (right up against the edit
/// canvas). Scrolls any content taller than its viewport: the owner sets
/// Maximum (content height) and LargeChange (viewport height) and follows
/// ValueChanged. Drag the thumb, click the track to page, or use the wheel.
/// </summary>
internal sealed class DarkScrollBar : Control
{
    private const int MinThumb = 24;

    private int _value, _maximum, _largeChange = 1;
    private bool _dragging, _hover;
    private int _dragStartY, _dragStartValue;

    public event Action? ValueChanged;

    /// <summary>Pixels scrolled per wheel notch.</summary>
    public int WheelStep { get; set; } = 60;

    public DarkScrollBar()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(22, 22, 22);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Cursor = Cursors.Hand;
    }

    /// <summary>Total content height.</summary>
    public int Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(0, value); Value = _value; Invalidate(); }
    }

    /// <summary>Visible (viewport) height.</summary>
    public int LargeChange
    {
        get => _largeChange;
        set { _largeChange = Math.Max(1, value); Value = _value; Invalidate(); }
    }

    private int MaxValue => Math.Max(0, _maximum - _largeChange);

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, 0, MaxValue);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke();
        }
    }

    public void ScrollBy(int delta) => Value += delta;

    /// <summary>Applies a mouse-wheel notch delta (e.g. from the content
    /// being scrolled, which receives the wheel while hovered).</summary>
    public void Wheel(int wheelDelta) => ScrollBy(-wheelDelta / 120 * WheelStep);

    private bool CanScroll => _maximum > _largeChange;

    private Rectangle ThumbRect()
    {
        int track = Height - 4;
        int thumbH = Math.Max(MinThumb, (int)((long)track * _largeChange / Math.Max(1, _maximum)));
        thumbH = Math.Min(thumbH, track);
        int travel = track - thumbH;
        int y = 2 + (MaxValue == 0 ? 0 : (int)((long)travel * _value / MaxValue));
        return new Rectangle(2, y, Width - 4, thumbH);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (!CanScroll) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = ThumbRect();
        var color = _dragging ? Color.FromArgb(150, 150, 150) : _hover ? Color.FromArgb(115, 115, 115) : Color.FromArgb(80, 80, 80);
        using var path = RoundedRect(r, Math.Min(4, r.Width / 2));
        g.FillPath(BrushCache.Get(color), path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !CanScroll) return;
        var thumb = ThumbRect();
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragStartY = e.Y;
            _dragStartValue = _value;
            Capture = true;
            Invalidate();
        }
        else
        {
            // Page toward the click.
            ScrollBy(e.Y < thumb.Top ? -_largeChange : _largeChange);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hover = ThumbRect().Contains(e.Location);
        if (hover != _hover) { _hover = hover; Invalidate(); }
        if (!_dragging) return;
        var thumb = ThumbRect();
        int travel = Height - 4 - thumb.Height;
        if (travel <= 0) return;
        Value = _dragStartValue + (int)((long)(e.Y - _dragStartY) * MaxValue / travel);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover) { _hover = false; Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Wheel(e.Delta);
    }
}
