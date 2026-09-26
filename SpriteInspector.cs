using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// The floating info box under the selected sprite in Construct: one
/// compact, rounded, custom-drawn pill with three sections -
///   Sprite #  ‹ #12 ›   chevrons step the pool piece; wheel steps too
///   X  89               scrub fields: drag left/right to change (Shift =
///   Y  102              steps of 8), wheel = ±1, click to type a value
/// Owns no sprite data: ConstructPanel pushes values in with SetValues and
/// applies what the events report.
/// </summary>
internal sealed class SpriteInspector : Control
{
    private const int Pad = 4, ChevronW = 20, PieceW = 38, LabelW = 16, ValueW = 34, FieldPad = 6, Radius = 7;

    private static readonly Color Back = Color.FromArgb(32, 32, 36);
    private static readonly Color Edge = Color.FromArgb(78, 78, 88);
    private static readonly Color Divider = Color.FromArgb(58, 58, 66);
    private static readonly Color HoverFill = Color.FromArgb(50, 50, 58);
    private static readonly Color Accent = Color.FromArgb(70, 130, 200);
    private static readonly Color ValueText = Color.FromArgb(225, 225, 230);
    private static readonly Color LabelText = Color.FromArgb(135, 135, 150);

    private enum Part { None, PieceLeft, Piece, PieceRight, X, Y }

    private int _piece, _x, _y;
    private const int MaxX = 511, MaxY = 255;
    private Part _hover = Part.None;

    // Scrub state for an X/Y drag.
    private Part _scrubPart = Part.None;
    private int _scrubStartMouse, _scrubStartValue;
    private bool _scrubMoved;

    // Wheel bursts on the same field count as one undo step.
    private Part _lastWheelPart = Part.None;
    private long _lastWheelTicks;

    private readonly TextBox _editor;
    private Part _editing = Part.None;

    private readonly Font _valueFont = new("Segoe UI", 9f);
    private readonly Font _labelFont = new("Segoe UI", 8f);

    /// <summary>‹ / › clicked or wheel over the Sprite # section (-1 / +1).</summary>
    public event Action<int>? PieceStepped;
    /// <summary>Raised once before an X/Y edit starts (drag, typed value, or
    /// a burst of wheel steps) - the owner snapshots undo here.</summary>
    public event Action? BeforeEdit;
    public event Action<int>? XChanged;
    public event Action<int>? YChanged;

    public SpriteInspector()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Back;
        Size = new Size(Pad + ChevronW + PieceW + ChevronW + 1 + FieldWidth + 1 + FieldWidth + Pad, 28);

        _editor = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = HoverFill,
            ForeColor = ValueText,
            Font = _valueFont,
            TextAlign = HorizontalAlignment.Center,
            Visible = false
        };
        _editor.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
        _editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { EndEdit(commit: true); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { EndEdit(commit: false); e.Handled = e.SuppressKeyPress = true; }
        };
        _editor.Leave += (_, _) => EndEdit(commit: true);
        Controls.Add(_editor);
    }

    private static int FieldWidth => FieldPad + LabelW + ValueW + FieldPad;

    /// <summary>Shows the given values without raising any events. A field
    /// that's being typed into keeps the user's text.</summary>
    public void SetValues(int piece, int x, int y)
    {
        if (piece == _piece && x == _x && y == _y) return;
        _piece = piece; _x = x; _y = y;
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // Real rounded corners: the parts outside the pill are cut away, so
        // the Construct canvas shows through there.
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), Radius);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    // ---- Layout ----
    private Rectangle PartRect(Part p)
    {
        int h = Height - 2 * 3, top = 3;
        int pieceLeft = Pad;
        int fieldX = Pad + ChevronW + PieceW + ChevronW + 1;
        int fieldY = fieldX + FieldWidth + 1;
        return p switch
        {
            Part.PieceLeft => new Rectangle(pieceLeft, top, ChevronW, h),
            Part.Piece => new Rectangle(pieceLeft + ChevronW, top, PieceW, h),
            Part.PieceRight => new Rectangle(pieceLeft + ChevronW + PieceW, top, ChevronW, h),
            Part.X => new Rectangle(fieldX, top, FieldWidth, h),
            Part.Y => new Rectangle(fieldY, top, FieldWidth, h),
            _ => Rectangle.Empty
        };
    }

    private Rectangle ValueRect(Part field)
    {
        var r = PartRect(field);
        return new Rectangle(r.X + FieldPad + LabelW, r.Y, ValueW, r.Height);
    }

    private Part PartAt(Point p)
    {
        foreach (var part in new[] { Part.PieceLeft, Part.Piece, Part.PieceRight, Part.X, Part.Y })
            if (PartRect(part).Contains(p)) return part;
        return Part.None;
    }

    // ---- Painting ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Hover / active background for a section.
        Part active = _scrubPart != Part.None ? _scrubPart : _editing != Part.None ? _editing : _hover;
        if (active != Part.None)
        {
            using var hp = RoundedRect(PartRect(active), 4);
            g.FillPath(BrushCache.Get(HoverFill), hp);
        }

        // Sprite # section: ‹ #12 ›
        DrawChevron(g, PartRect(Part.PieceLeft), left: true, _hover == Part.PieceLeft);
        DrawChevron(g, PartRect(Part.PieceRight), left: false, _hover == Part.PieceRight);
        DrawCentered(g, "#" + _piece, _valueFont, ValueText, PartRect(Part.Piece));

        // Dividers
        using (var div = new Pen(Divider))
        {
            int dx1 = PartRect(Part.X).X - 1, dx2 = PartRect(Part.Y).X - 1;
            g.DrawLine(div, dx1, 7, dx1, Height - 7);
            g.DrawLine(div, dx2, 7, dx2, Height - 7);
        }

        DrawField(g, Part.X, "X", _x);
        DrawField(g, Part.Y, "Y", _y);

        // Outline - accent while a value is being changed.
        bool busy = _scrubPart != Part.None || _editing != Part.None;
        using var edge = new Pen(busy ? Accent : Edge);
        using var outline = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        g.DrawPath(edge, outline);
    }

    private void DrawField(Graphics g, Part field, string label, int value)
    {
        var r = PartRect(field);
        DrawCentered(g, label, _labelFont, LabelText, new Rectangle(r.X + FieldPad, r.Y, LabelW, r.Height));
        if (_editing != field)
            DrawCentered(g, value.ToString(), _valueFont, ValueText, ValueRect(field));
    }

    private static void DrawChevron(Graphics g, Rectangle r, bool left, bool hover)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        using var pen = new Pen(hover ? Color.White : LabelText, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        int d = left ? 1 : -1;
        g.DrawLines(pen, new PointF[] { new(cx + 2 * d, cy - 5), new(cx - 2 * d, cy), new(cx + 2 * d, cy + 5) });
    }

    private static void DrawCentered(Graphics g, string text, Font font, Color color, Rectangle r) =>
        TextRenderer.DrawText(g, text, font, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- Mouse ----
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_scrubPart != Part.None && (e.Button & MouseButtons.Left) != 0)
        {
            int moved = Cursor.Position.X - _scrubStartMouse; // screen coords: this box follows the sprite as X changes
            if (!_scrubMoved && Math.Abs(moved) < 3) return; // still a click
            if (!_scrubMoved) { _scrubMoved = true; BeforeEdit?.Invoke(); }
            int step = (ModifierKeys & Keys.Shift) != 0 ? 8 : 1;
            SetField(_scrubPart, _scrubStartValue + moved / 2 * step);
            return;
        }

        var part = PartAt(e.Location);
        if (part != _hover) { _hover = part; Invalidate(); }
        Cursor = part is Part.X or Part.Y ? Cursors.SizeWE
               : part is Part.PieceLeft or Part.PieceRight ? Cursors.Hand
               : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != Part.None) { _hover = Part.None; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_editing != Part.None) EndEdit(commit: true);
        switch (PartAt(e.Location))
        {
            case Part.PieceLeft: PieceStepped?.Invoke(-1); break;
            case Part.PieceRight: PieceStepped?.Invoke(1); break;
            case Part.X:
            case Part.Y:
                _scrubPart = PartAt(e.Location);
                _scrubStartMouse = Cursor.Position.X;
                _scrubStartValue = _scrubPart == Part.X ? _x : _y;
                _scrubMoved = false;
                Invalidate();
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_scrubPart == Part.None) return;
        var part = _scrubPart;
        bool wasClick = !_scrubMoved;
        _scrubPart = Part.None;
        Invalidate();
        if (wasClick) BeginEdit(part); // a plain click types a value instead
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        int dir = e.Delta > 0 ? 1 : -1;
        var part = PartAt(e.Location);
        if (part is Part.PieceLeft or Part.Piece or Part.PieceRight)
        {
            PieceStepped?.Invoke(dir);
            return;
        }
        if (part is not (Part.X or Part.Y)) return;

        long now = Environment.TickCount64;
        if (part != _lastWheelPart || now - _lastWheelTicks > 700) BeforeEdit?.Invoke();
        _lastWheelPart = part;
        _lastWheelTicks = now;
        int step = (ModifierKeys & Keys.Shift) != 0 ? 8 : 1;
        SetField(part, (part == Part.X ? _x : _y) + dir * step);
    }

    private void SetField(Part field, int value)
    {
        if (field == Part.X)
        {
            value = Math.Clamp(value, 0, MaxX);
            if (value == _x) return;
            _x = value;
            XChanged?.Invoke(value);
        }
        else
        {
            value = Math.Clamp(value, 0, MaxY);
            if (value == _y) return;
            _y = value;
            YChanged?.Invoke(value);
        }
        Invalidate();
    }

    // ---- Typed values ----
    private void BeginEdit(Part field)
    {
        _editing = field;
        var r = ValueRect(field);
        int h = _editor.PreferredHeight;
        _editor.SetBounds(r.X, r.Y + (r.Height - h) / 2 + 1, r.Width, h);
        _editor.Text = (field == Part.X ? _x : _y).ToString();
        _editor.Visible = true;
        _editor.Focus();
        _editor.SelectAll();
        Invalidate();
    }

    private void EndEdit(bool commit)
    {
        if (_editing == Part.None) return;
        var field = _editing;
        _editing = Part.None;
        _editor.Visible = false;
        if (commit && int.TryParse(_editor.Text, out int v) && v != (field == Part.X ? _x : _y))
        {
            BeforeEdit?.Invoke();
            SetField(field, v);
        }
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) EndEdit(commit: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _valueFont.Dispose(); _labelFont.Dispose(); Region?.Dispose(); }
        base.Dispose(disposing);
    }
}
