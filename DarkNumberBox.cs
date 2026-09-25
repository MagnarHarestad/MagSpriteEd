using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Dark-themed replacement for NumericUpDown, whose native edit box and
/// spinner buttons are always light. Same surface the app used from
/// NumericUpDown - Minimum/Maximum/Value (decimal), ValueChanged, TextAlign -
/// so call sites didn't change shape.
///
/// Value commits on Enter, focus loss, a spinner click, the wheel or the
/// Up/Down keys - never per keystroke, so typing "16" doesn't pass through 1.
/// Escape reverts the text. Holding a spinner half repeats.
/// </summary>
internal sealed class DarkNumberBox : UserControl
{
    private const int SpinWidth = 14;

    private static readonly Color Back = Color.FromArgb(38, 38, 38);
    private static readonly Color BorderIdle = Color.FromArgb(70, 70, 70);
    private static readonly Color BorderHover = Color.FromArgb(100, 100, 100);
    private static readonly Color BorderFocus = Color.FromArgb(70, 130, 200);
    private static readonly Color SpinHover = Color.FromArgb(60, 60, 60);
    private static readonly Color Arrow = Color.FromArgb(170, 170, 170);

    private readonly TextBox _text;
    private readonly System.Windows.Forms.Timer _repeat = new();
    private decimal _value, _minimum, _maximum = 100;
    private int _repeatDir;
    private int _hoverHalf; // -1 = down arrow, 1 = up arrow, 0 = none
    private bool _hover;

    public event EventHandler? ValueChanged;

    public DarkNumberBox()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Back;
        ForeColor = Color.Gainsboro;

        _text = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Back,
            ForeColor = Color.Gainsboro,
            TextAlign = HorizontalAlignment.Center,
            Text = "0"
        };
        _text.KeyDown += Text_KeyDown;
        _text.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && e.KeyChar != '-') e.Handled = true; };
        _text.Leave += (_, _) => Commit();
        _text.GotFocus += (_, _) => { Invalidate(); BeginInvoke(_text.SelectAll); };
        _text.LostFocus += (_, _) => Invalidate();
        _text.MouseWheel += (_, e) => Wheel(e.Delta);
        Controls.Add(_text);

        _repeat.Tick += (_, _) => { _repeat.Interval = 60; Step(_repeatDir); };
        Size = new Size(50, 24);
        LayoutText();
    }

    public decimal Minimum
    {
        get => _minimum;
        set { _minimum = value; if (_maximum < value) _maximum = value; Value = _value; }
    }

    public decimal Maximum
    {
        get => _maximum;
        set { _maximum = value; if (_minimum > value) _minimum = value; Value = _value; }
    }

    public decimal Value
    {
        get => _value;
        set
        {
            decimal v = Math.Clamp(value, _minimum, _maximum);
            _text.Text = ((int)v).ToString();
            if (v == _value) return;
            _value = v;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public HorizontalAlignment TextAlign
    {
        get => _text.TextAlign;
        set => _text.TextAlign = value;
    }

    private Rectangle SpinRect => new(Width - SpinWidth - 1, 1, SpinWidth, Height - 2);

    private void LayoutText()
    {
        if (_text == null) return; // base constructor can resize before _text exists
        int textH = _text.PreferredHeight;
        // +1: a borderless TextBox's PreferredHeight carries extra space
        // below the glyphs, so plain centring sits the digits a pixel high.
        _text.SetBounds(4, Math.Max(1, (Height - textH) / 2 + 1), Math.Max(10, Width - SpinWidth - 8), textH);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutText();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_text == null) return;
        _text.Font = Font;
        LayoutText();
    }

    private void Commit()
    {
        if (decimal.TryParse(_text.Text, out var parsed)) Value = parsed;
        else _text.Text = ((int)_value).ToString();
    }

    private void Step(int dir)
    {
        Commit();
        Value = _value + dir;
        _text.SelectAll();
    }

    private void Wheel(int delta)
    {
        if (delta != 0) Step(delta > 0 ? 1 : -1);
    }

    private void Text_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up: Step(1); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.Down: Step(-1); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.PageUp: Step(10); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.PageDown: Step(-10); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.Enter: Commit(); _text.SelectAll(); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.Escape: _text.Text = ((int)_value).ToString(); _text.SelectAll(); e.Handled = e.SuppressKeyPress = true; break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Back);

        var spin = SpinRect;
        int mid = spin.Top + spin.Height / 2;
        if (_hoverHalf != 0)
        {
            var half = _hoverHalf > 0
                ? new Rectangle(spin.X, spin.Top, spin.Width, mid - spin.Top)
                : new Rectangle(spin.X, mid, spin.Width, spin.Bottom - mid);
            g.FillRectangle(BrushCache.Get(SpinHover), half);
        }
        using (var sep = new Pen(BorderIdle))
            g.DrawLine(sep, spin.X, spin.Top + 3, spin.X, spin.Bottom - 3);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cx = spin.X + spin.Width / 2 + 1;
        var arrow = BrushCache.Get(Arrow);
        g.FillPolygon(arrow, new PointF[] { new(cx - 3.5f, mid - 2.5f), new(cx + 3.5f, mid - 2.5f), new(cx, mid - 6.5f) });
        g.FillPolygon(arrow, new PointF[] { new(cx - 3.5f, mid + 2.5f), new(cx + 3.5f, mid + 2.5f), new(cx, mid + 6.5f) });
        g.SmoothingMode = SmoothingMode.None;

        var border = ContainsFocus ? BorderFocus : _hover ? BorderHover : BorderIdle;
        using var pen = new Pen(border);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private int HalfAt(Point p)
    {
        var spin = SpinRect;
        if (!spin.Contains(p)) return 0;
        return p.Y < spin.Top + spin.Height / 2 ? 1 : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int half = HalfAt(e.Location);
        if (half != _hoverHalf || !_hover) { _hoverHalf = half; _hover = true; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverHalf = 0;
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _text.Focus();
        if (e.Button != MouseButtons.Left) return;
        int half = HalfAt(e.Location);
        if (half == 0) return;
        Step(half);
        _repeatDir = half;
        _repeat.Interval = 400;
        _repeat.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _repeat.Stop();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Wheel(e.Delta);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _repeat.Dispose();
        base.Dispose(disposing);
    }
}
