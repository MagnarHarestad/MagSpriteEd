using System;
using System.Drawing;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Timeline frame picker: one box per animation frame, side by side. Click
/// a box to go to exactly that frame, drag across to scrub, or use the
/// mouse wheel to step one frame at a time. Hit-testing uses the same cell
/// maths as the drawing, so where you click is always where it lands.
/// Replaces a native TrackBar, whose own click handling (page-stepping
/// toward the click, a thumb whose size had to be guessed) made clicks
/// between ticks land on the wrong frame.
/// Same Maximum/Value/ValueChanged shape as TrackBar (Minimum is always 0).
/// </summary>
internal sealed class FrameStrip : Control
{
    private int _maximum;
    private int _value;
    private int _hover = -1;
    private bool _dragging;

    public event EventHandler? ValueChanged;

    public FrameStrip()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Height = 24;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 7.5f);
    }

    /// <summary>Last frame index (frame count - 1). Clamps Value if it shrinks.</summary>
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(0, value);
            if (_value > _maximum) Value = _maximum;
            else Invalidate();
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, 0, _maximum);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int Count => _maximum + 1;
    private float CellWidth => (float)(Width - 1) / Count;

    private int IndexAt(int x) => Math.Clamp((int)(x / CellWidth), 0, _maximum);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(22, 22, 22));
        float w = CellWidth;
        int h = Height - 1;

        for (int i = 0; i < Count; i++)
        {
            var cell = new RectangleF(i * w, 0, w, h);
            Color fill = i == _value ? Color.FromArgb(70, 130, 200)
                       : i == _hover ? Color.FromArgb(75, 75, 75)
                       : i % 2 == 0 ? Color.FromArgb(48, 48, 48) : Color.FromArgb(40, 40, 40);
            g.FillRectangle(BrushCache.Get(fill), cell);

            // Frame numbers (1-based, matching the "Frame N/M" label) where they fit.
            string label = (i + 1).ToString();
            var size = g.MeasureString(label, Font);
            if (size.Width + 2 <= w)
            {
                var textColor = i == _value ? Color.White : Color.FromArgb(170, 170, 170);
                g.DrawString(label, Font, BrushCache.Get(textColor),
                    cell.X + (w - size.Width) / 2, (h - size.Height) / 2);
            }
        }

        // Cell dividers, only while boxes are wide enough to read as separate.
        if (w >= 4)
        {
            using var divider = new Pen(Color.FromArgb(22, 22, 22));
            for (int i = 1; i < Count; i++)
                g.DrawLine(divider, i * w, 0, i * w, h);
        }

        using var border = new Pen(Color.FromArgb(90, 90, 90));
        g.DrawRectangle(border, 0, 0, Width - 1, h);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _dragging = true;
        Value = IndexAt(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && (e.Button & MouseButtons.Left) != 0) Value = IndexAt(e.X);
        int hover = IndexAt(e.X);
        if (hover != _hover) { _hover = hover; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) _dragging = false;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        Value += e.Delta > 0 ? -1 : 1; // wheel down = next frame
    }
}
