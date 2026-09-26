using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Placement/composition panel, embedded directly in MainForm to the right
/// of the drawing area (previously a separate popup window - folded in so
/// the whole editor reads as one application). Shows the real backdrop
/// picture with the 8 hardware sprites on top, lets each be dragged into
/// place, and keeps a position PER ANIMATION FRAME (not one static spot)
/// so sprites can move across the animation to create flow.
///
/// Each hardware sprite's ART is also keyframed per animation frame, as a
/// single "Sprite #" - a direct index into the SpriteBank's flat pool of
/// 12x21 pieces. The Timeline's own length (how many animation frames
/// exist) is tracked here entirely independently of the bank's PieceCount
/// (how many distinct pieces of art exist) - any of the 8 hardware sprites
/// in any animation frame can reference any piece, reused freely (typing
/// the same Sprite # into two hardware sprites shows the same art on both,
/// including across different animation frames), so a fully custom, non-
/// symmetric composition is just as easy as the original two-flame layout,
/// and growing/shrinking one of the two counts never has to touch the other.
/// </summary>
public sealed class ConstructPanel : UserControl
{
    private readonly Func<SpriteBank> _bankProvider;
    private readonly Func<BackdropPicture?> _backdropProvider;

    private ConstructCanvas _canvas = null!;
    private Label _d010Label = null!;

    // Floating per-sprite inspector - replaces the old separate Sprites
    // table. A single instance, repositioned to hover next to whichever
    // hardware sprite is PrimarySelected (see RefreshInspector), instead of
    // a fixed-position panel listing all 8 at once.
    private SpriteInspector _inspector = null!;

    private Button _playButton = null!;
    private DarkNumberBox _fpsUpDown = null!;
    private DarkNumberBox _frameCountUpDown = null!;
    private FrameStrip _frameScrub = null!;
    private Label _frameLabel = null!;

    private readonly System.Windows.Forms.Timer _playTimer = new() { Interval = 180 };
    private readonly ToolTip _tips = new();
    private int _frame;
    private CheckBox _pingPongCheck = null!;
    private int _playDir = 1;

    // Guards against event re-entrancy when a control's Value/Selection is
    // set PROGRAMMATICALLY (e.g. RefreshInspector populating the X/Y
    // NumericUpDowns, or LoadFrameIntoCanvas moving the frame scrubber) -
    // without this, that own change would immediately fire the control's
    // ValueChanged/etc. handler right back into the same state update.
    // Every handler that only reacts to a programmatic update checks this first.
    private bool _suppressEvents;

    // Set once the user zooms Construct with the wheel - stops FitZoomToView
    // overriding their choice on every resize.
    private bool _userZoomed;

    // Per-frame keyframed state: [frame][sprite]. Position and sprite
    // source (which piece of the bank's flat pool to show) both vary per
    // animation frame, exactly like a real hand-authored sprite animation.
    // _animFrameCount is this panel's OWN Timeline length - entirely
    // independent of the bank's PieceCount (see class remarks above).
    private int[][] _posX = Array.Empty<int[]>();
    private int[][] _posY = Array.Empty<int[]>();
    private int[][] _spriteSource = Array.Empty<int[]>();
    private int _animFrameCount = 8;

    // Undo/redo for sprite POSITION + SOURCE together - separate from
    // MainForm's pixel-art undo stack, which only ever touched the
    // SpriteBank. Each entry snapshots one frame's full 8-sprite state,
    // pushed right before a drag, a keyboard nudge, or an X/Y/Sprite# field
    // edit changes it.
    private readonly List<(int frame, int[] x, int[] y, int[] source, long seq)> _posUndo = new();
    private readonly List<(int frame, int[] x, int[] y, int[] source, long seq)> _posRedo = new();
    private const int PosUndoCap = 100;

    // One frame's worth of copied position+source data - see
    // CopyCurrentFrameToClipboard/PasteFrameFromClipboard.
    private (int[] x, int[] y, int[] source)? _frameClipboard;

    // Individual colour alternation exactly as Fire_Frame writes to
    // $d027-$d02e: 8,10,8,10,10,8,10,8 (orange/light-red).
    private static readonly byte[] IndividualPaletteIndex = { 8, 10, 8, 10, 10, 8, 10, 8 };

    /// <summary>Shows/hides each sprite's bounding-box outline and index
    /// number in the composited preview - now a toolbar toggle in MainForm
    /// (moved out of the removed "Backdrop" panel) rather than a checkbox
    /// living in this control.</summary>
    public bool ShowSpriteOutlines
    {
        get => _canvas.ShowOutlines;
        set
        {
            if (_canvas.ShowOutlines == value) return;
            _canvas.ShowOutlines = value;
            _canvas.Invalidate();
        }
    }

    /// <summary>Opens the $d020 border in the preview so sprites placed in
    /// the border area stay visible - see ConstructCanvas.OpenBorder.</summary>
    public bool OpenBorder
    {
        get => _canvas.OpenBorder;
        set
        {
            if (_canvas.OpenBorder == value) return;
            _canvas.OpenBorder = value;
            _canvas.Invalidate();
        }
    }

    /// <summary>Shows/hides the checkerboard grid behind the sprites (only visible without a backdrop).</summary>
    public bool ShowGrid
    {
        get => _canvas.ShowGrid;
        set
        {
            if (_canvas.ShowGrid == value) return;
            _canvas.ShowGrid = value;
            _canvas.Invalidate();
        }
    }

    /// <summary>A plain left/right click or drag landed on a sprite pixel in
    /// Construct (spriteIndex, row, col, button) - MainForm applies the
    /// current drawing tool to that sprite's piece.</summary>
    public event Action<int, int, int, MouseButtons>? SpriteCellPainted;
    public event Action? PaintStrokeStarted;
    public event Action? PaintStrokeEnded;

    public ConstructPanel(Func<SpriteBank> bankProvider, Func<BackdropPicture?> backdropProvider)
    {
        _bankProvider = bankProvider;
        _backdropProvider = backdropProvider;

        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(18, 18, 18);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);

        BuildUi();
        ResetAllFramesToDefaults();
        LoadFrameIntoCanvas();

        _playTimer.Tick += (_, _) =>
        {
            int count = Math.Max(1, _animFrameCount);
            if (_pingPongCheck.Checked && count > 1)
            {
                _frame += _playDir;
                if (_frame >= count - 1) { _frame = count - 1; _playDir = -1; }
                else if (_frame <= 0) { _frame = 0; _playDir = 1; }
            }
            else
            {
                _frame = (_frame + 1) % count;
            }
            LoadFrameIntoCanvas();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _playTimer.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------------
    // UI construction
    // ---------------------------------------------------------------------
    private void BuildUi()
    {
        // One column: the composited canvas fills the top, everything else
        // (Timeline / Sprites+VIC) sits in one row along the
        // bottom instead of a narrow sidebar - see class remarks. That
        // sidebar's Backdrop panel is gone entirely: "Load Backdrop" now
        // lives on MainForm's own Menu, sprite-outline visibility is a
        // MainForm toolbar toggle (ShowSpriteOutlines), and Zoom is
        // ConstructCanvas's own scroll-wheel zoom.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _canvas = new ConstructCanvas
        {
            SpritePixel = (spriteIdx, row, col) =>
            {
                var bank = _bankProvider();
                int source = _spriteSource[_frame][spriteIdx];
                if (source < 0 || source >= bank.PieceCount) return 0;
                return bank.Get(source, row, col);
            },
            PaletteProvider = (v, spriteIdx) =>
            {
                var bank = _bankProvider();
                int source = _spriteSource[_frame][spriteIdx];
                int ind = source >= 0 && source < bank.PieceCount ? bank.IndividualColor(source) : SpriteBank.DefaultIndividualColor;
                return EditorPalette.ColorFor(v, ind);
            },
            IsSpriteHires = spriteIdx =>
            {
                var bank = _bankProvider();
                int source = _spriteSource[_frame][spriteIdx];
                return source >= 0 && source < bank.PieceCount && bank.IsHires(source);
            },
            SpriteLabel = spriteIdx => $"S{spriteIdx} - #{_spriteSource[_frame][spriteIdx]}"
        };
        _canvas.ApplyZoomedSize();
        _canvas.SelectionChanged += () => { RefreshInspector(); SelectedSpriteSourceChanged?.Invoke(); };
        _canvas.SpriteMoved += CommitCanvasPositionsToCurrentFrame;
        _canvas.ZoomChanged += () => { _userZoomed = true; RefreshInspector(); };
        _canvas.CellInteract += (s, row, col, button) => SpriteCellPainted?.Invoke(s, row, col, button);
        _canvas.PaintStrokeStarted += () => PaintStrokeStarted?.Invoke();
        _canvas.PaintStrokeEnded += () => PaintStrokeEnded?.Invoke();
        // These are the base Control.MouseDown/KeyDown events (raised via
        // base.OnMouseDown/base.OnKeyDown as the FIRST line of ConstructCanvas's
        // own overrides), so they fire before any position is actually
        // changed - the correct moment to snapshot for undo. Only Shift/Ctrl
        // clicks select and move sprites; a plain click draws (MainForm takes
        // the pixel undo snapshot for that) and middle only pans. KeyDown
        // covers arrow-key nudges. Ctrl+Z/Y is handled by MainForm, which
        // undoes whichever of a drawing or a placement change is newest.
        _canvas.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left &&
                ((ModifierKeys & (Keys.Shift | Keys.Control | Keys.Alt)) != 0 || _canvas.LabelChipAt(e.Location) >= 0))
                PushPositionUndo();
        };
        _canvas.KeyDown += (_, e) =>
        {
            if (!e.Control && _canvas.SelectedSprites.Count > 0 && e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
                PushPositionUndo();
        };

        var canvasScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(10, 10, 10), Padding = new Padding(6), Margin = new Padding(0) };
        _canvas.Location = new Point(6, 6);
        canvasScroll.Controls.Add(_canvas);
        // Auto-fit the zoom to the (large) Construct area until the user
        // zooms by hand with the wheel - from then on their zoom sticks.
        canvasScroll.SizeChanged += (_, _) => FitZoomToView(canvasScroll);

        BuildInspector();
        _canvas.Controls.Add(_inspector);

        // Bottom strip is now just the Timeline - the old "Sprites (0-7)"
        // table is gone, replaced by _inspector floating over whichever
        // sprite is selected (see BuildInspector/RefreshInspector), and the
        // "Actions" group was removed earlier. One flat row instead of
        // boxed GroupBoxes, so it reads as a single toolbar-like strip.
        var bottomRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.FromArgb(22, 22, 22)
        };
        BuildTimelineRow(bottomRow);

        root.Controls.Add(canvasScroll, 0, 0);
        root.Controls.Add(bottomRow, 0, 1);
        Controls.Add(root);
    }

    // Flat dark icon buttons in the media-player order: previous, play/pause, next.
    private static readonly Color TransportBack = Color.FromArgb(45, 45, 45);
    private static readonly Color TransportPlaying = Color.FromArgb(70, 130, 200);

    private Button MakeTransportButton(Image icon, string tooltip)
    {
        var btn = new Button
        {
            Image = icon,
            Size = new Size(32, 26),
            Margin = new Padding(1, 1, 1, 1),
            FlatStyle = FlatStyle.Flat,
            BackColor = TransportBack,
            TabStop = false
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 75);
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 65, 65);
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(85, 85, 85);
        _tips.SetToolTip(btn, tooltip);
        return btn;
    }

    /// <summary>Largest 0.5-step zoom at which the whole PAL frame fits the
    /// visible area, applied unless the user has zoomed by hand.</summary>
    private void FitZoomToView(Panel host)
    {
        if (_userZoomed) return;
        int availW = host.ClientSize.Width - host.Padding.Horizontal;
        int availH = host.ClientSize.Height - host.Padding.Vertical;
        if (availW <= 0 || availH <= 0) return;
        float fit = Math.Min((float)availW / ConstructCanvas.FrameWidth, (float)availH / ConstructCanvas.FrameHeight);
        float zoom = Math.Clamp((float)Math.Floor(fit * 2) / 2f, 1f, 8f);
        if (zoom == _canvas.Zoom) return;
        _canvas.Zoom = zoom;
        _canvas.ApplyZoomedSize();
        _canvas.Invalidate();
        RefreshInspector();
    }

    private void BuildTimelineRow(FlowLayoutPanel bottomRow)
    {
        var prev = MakeTransportButton(Icons.Prev(), "Previous frame");
        prev.Click += (_, _) => { StopPlay(); StepFrame(-1); };
        _playButton = MakeTransportButton(Icons.Play(), "Play");
        _playButton.Click += (_, _) => TogglePlay();
        var next = MakeTransportButton(Icons.Next(), "Next frame");
        next.Click += (_, _) => { StopPlay(); StepFrame(1); };
        next.Margin = new Padding(1, 1, 4, 1);
        bottomRow.Controls.Add(prev);
        bottomRow.Controls.Add(_playButton);
        bottomRow.Controls.Add(next);

        // Fixed width (sized for the widest possible text) so the strip next
        // to it doesn't shift as the frame number changes.
        _frameLabel = new Label { Text = "Frame 1/8", AutoSize = false, ForeColor = Color.Gainsboro, Margin = new Padding(10, 8, 4, 0), Height = 18 };
        _frameLabel.Width = TextRenderer.MeasureText("Frame 128/128", _frameLabel.Font).Width;
        bottomRow.Controls.Add(_frameLabel);
        _frameScrub = new FrameStrip { Maximum = 7, Width = 320, Margin = new Padding(2, 3, 8, 2) };
        _frameScrub.ValueChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            if (_frameScrub.Value == _frame) return;
            StopPlay();
            _frame = _frameScrub.Value;
            LoadFrameIntoCanvas();
        };
        bottomRow.Controls.Add(_frameScrub);

        // Timeline length applies as soon as the value changes. Typing
        // doesn't commit per keystroke - NumericUpDown only updates Value on
        // Enter, focus loss or a spinner click - so typing "16" never
        // briefly shrinks the Timeline to 1 frame on the "1".
        bottomRow.Controls.Add(new Label { Text = "Frames", AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(2, 8, 2, 0) });
        _frameCountUpDown = new DarkNumberBox { Minimum = 1, Maximum = 128, Value = 8, Width = 50, TextAlign = HorizontalAlignment.Center, Margin = new Padding(2, 3, 14, 2) };
        _frameCountUpDown.ValueChanged += (_, _) =>
        {
            if (_suppressEvents || (int)_frameCountUpDown.Value == _animFrameCount) return;
            StopPlay();
            SetAnimFrameCount((int)_frameCountUpDown.Value);
        };
        bottomRow.Controls.Add(_frameCountUpDown);

        bottomRow.Controls.Add(new Label { Text = "FPS", AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(2, 8, 2, 0) });
        _fpsUpDown = new DarkNumberBox { Minimum = 1, Maximum = 50, Value = 17, Width = 50, TextAlign = HorizontalAlignment.Center, Margin = new Padding(2, 3, 10, 2) };
        _fpsUpDown.ValueChanged += (_, _) => _playTimer.Interval = Math.Max(20, (int)(1000 / _fpsUpDown.Value));
        bottomRow.Controls.Add(_fpsUpDown);

        _pingPongCheck = new CheckBox
        {
            Text = "Ping-pong",
            ForeColor = Color.Gainsboro,
            AutoSize = true,
            Margin = new Padding(2, 6, 16, 2),
            // Also written into the "Export Animation" output as
            // var magspriteed_opt_pingpong - Fire.s's Fire_Frame reads that
            // to pick ping-pong vs. simple forward-wrap playback on the C64
            // itself (see BuildOptimizedAsmExport), so this one checkbox
            // controls both the editor's own preview loop and the shipped
            // demo's animation.
        };
        _playDir = 1;
        bottomRow.Controls.Add(_pingPongCheck);
        // VIC readouts go on a line of their own, so the first line is all
        // Timeline controls and the frame strip can take its spare width.
        bottomRow.SetFlowBreak(_pingPongCheck, true);

        _d010Label = new Label { Text = "$d010 = %00000000 ($00)", AutoSize = true, ForeColor = Color.FromArgb(140, 140, 140), Margin = new Padding(2, 8, 2, 0) };
        bottomRow.Controls.Add(_d010Label);

        bottomRow.ClientSizeChanged += (_, _) => FitFrameStripWidth(bottomRow);
    }

    /// <summary>Stretches the frame strip to fill whatever width the rest of
    /// the Timeline line (transport, Frames, FPS, Ping-pong) leaves over.</summary>
    private void FitFrameStripWidth(FlowLayoutPanel row)
    {
        int others = 0;
        foreach (Control c in row.Controls)
        {
            if (c == _frameScrub) { others += c.Margin.Horizontal; continue; }
            if (c == _d010Label) continue; // second line
            others += c.Width + c.Margin.Horizontal;
        }
        int want = row.ClientSize.Width - row.Padding.Horizontal - others - 2;
        want = Math.Max(160, want);
        if (_frameScrub.Width != want) _frameScrub.Width = want;
    }

    /// <summary>Builds the floating panel that hovers next to whichever
    /// hardware sprite is currently selected (see RefreshInspector) - the
    /// direct replacement for the old always-visible Sprites table: lets
    /// you step its Sprite # and edit X/Y right where the sprite is, rather
    /// than in a fixed list elsewhere on screen.</summary>
    private void BuildInspector()
    {
        _inspector = new SpriteInspector { Visible = false };
        _inspector.PieceStepped += StepPiece;
        _inspector.BeforeEdit += PushPositionUndo;
        _inspector.XChanged += x => SetPrimaryPosition(x, null);
        _inspector.YChanged += y => SetPrimaryPosition(null, y);
    }

    // H key (MainForm): keeps the info box hidden even while a sprite is selected.
    private bool _inspectorHidden;

    /// <summary>Toggles the info box under the selected sprite; returns true if now hidden.</summary>
    public bool ToggleInspectorHidden()
    {
        _inspectorHidden = !_inspectorHidden;
        RefreshInspector();
        return _inspectorHidden;
    }

    private void SetPrimaryPosition(int? x, int? y)
    {
        int s = _canvas.PrimarySelected;
        if (s < 0) return;
        if (x is { } nx) _canvas.SpriteX[s] = nx;
        if (y is { } ny) _canvas.SpriteY[s] = ny;
        _canvas.Invalidate();
        CommitCanvasPositionsToCurrentFrame();
    }

    /// <summary>Steps the primary-selected hardware sprite's Sprite # (its
    /// raw pool piece index, wrapping) - the inspector's "&lt; N &gt;"
    /// control.</summary>
    private void StepPiece(int delta)
    {
        int s = _canvas.PrimarySelected;
        if (s < 0) return;
        EnsureArraysAllocated();
        int count = Math.Max(1, _bankProvider().PieceCount);
        PushPositionUndo();
        int cur = _spriteSource[_frame][s];
        _spriteSource[_frame][s] = ((cur + delta) % count + count) % count;
        _canvas.Invalidate();
        RefreshInspector();
        SelectedSpriteSourceChanged?.Invoke();
    }

    /// <summary>Repositions and repopulates the floating inspector to track
    /// whichever hardware sprite is PrimarySelected (hidden when nothing is
    /// selected) - called on selection change, frame change, drag/nudge,
    /// zoom, and a Sprite # step.</summary>
    private void RefreshInspector()
    {
        int s = _canvas.PrimarySelected;
        if (s < 0 || s >= 8 || _inspectorHidden) { _inspector.Visible = false; return; }
        EnsureArraysAllocated();

        _inspector.SetValues(_spriteSource[_frame][s], _canvas.SpriteX[s], _canvas.SpriteY[s]);

        // Centred under the sprite (its label chip sits above the box, so
        // below keeps the two apart); flips above if it would run off the
        // bottom of the canvas.
        var rect = _canvas.SpriteRect(s);
        int x = rect.Left + (rect.Width - _inspector.Width) / 2;
        x = Math.Max(0, Math.Min(x, _canvas.Width - _inspector.Width));
        int y = rect.Bottom + 4;
        if (y + _inspector.Height > _canvas.Height) y = rect.Top - _inspector.Height - 4;
        y = Math.Max(0, Math.Min(y, _canvas.Height - _inspector.Height));
        _inspector.Location = new Point(x, y);
        _inspector.Visible = true;
        _inspector.BringToFront();
    }

    // ---------------------------------------------------------------------
    // Backdrop
    // ---------------------------------------------------------------------
    public void RefreshBackdrop()
    {
        _canvas.Backdrop = _backdropProvider()?.Image;
        _canvas.Invalidate();
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        RefreshBackdrop();
    }

    // ---------------------------------------------------------------------
    // Per-frame keyframed state (positions + sprite source) - sized to
    // _animFrameCount, this panel's OWN Timeline length, entirely
    // independent of the bank's PieceCount (see class remarks).
    // ---------------------------------------------------------------------
    private void EnsureArraysAllocated()
    {
        if (_posX.Length == _animFrameCount) return;
        GrowOrShrinkArraysTo(_animFrameCount);
    }

    private void GrowOrShrinkArraysTo(int fc)
    {
        var newX = new int[fc][];
        var newY = new int[fc][];
        var newSource = new int[fc][];
        int oldCount = _posX.Length;
        for (int f = 0; f < fc; f++)
        {
            newX[f] = new int[8];
            newY[f] = new int[8];
            newSource[f] = new int[8];
            if (f < oldCount)
            {
                Array.Copy(_posX[f], newX[f], 8);
                Array.Copy(_posY[f], newY[f], 8);
                Array.Copy(_spriteSource[f], newSource[f], 8);
            }
            else
            {
                SetDefaultFrame(f, newX[f], newY[f], newSource[f]);
            }
        }
        _posX = newX;
        _posY = newY;
        _spriteSource = newSource;
        _animFrameCount = fc;
        _frame = Math.Min(_frame, fc - 1);
        _frameScrub.Maximum = Math.Max(0, fc - 1);
    }

    /// <summary>Sets the Timeline's own length directly (MainForm's "Frames"
    /// count + Apply), independent of the bank's PieceCount - growing adds
    /// default-content frames at the end, shrinking just drops frames off
    /// the end (their Sprite # references simply become unreferenced, not
    /// deleted - the pool itself is untouched).</summary>
    public void SetAnimFrameCount(int count)
    {
        EnsureArraysAllocated();
        GrowOrShrinkArraysTo(Math.Max(1, count));
        LoadFrameIntoCanvas();
    }

    public int AnimFrameCount => _animFrameCount;

    // Fixed starting positions mirroring Startup\Fire.s's shipped constants:
    // fire_left_x=114, fire_right_x=206, fire_y=158 (screen-pixel targets
    // 90/182/108 with the project's +24/+50 sprite origin baked in), with a
    // 24px/21px offset per sprite within each flame's 2x2 footprint so the
    // default layout still reads as two flame blocks - any sprite can be
    // dragged anywhere afterwards, per frame. Sprite # defaults just cycle
    // through whatever the pool currently offers, giving some variety
    // between sprites/frames without assuming anything about pool size.
    private void SetDefaultFrame(int animFrame, int[] x, int[] y, int[] source)
    {
        int[] baseX = { 114, 114, 114, 114, 206, 206, 206, 206 };
        const int baseY = 158;
        int[] colOff = { 0, 24, 0, 24, 0, 24, 0, 24 };
        int[] rowOff = { 0, 0, 21, 21, 0, 0, 21, 21 };
        int poolCount = Math.Max(1, _bankProvider().PieceCount);
        for (int s = 0; s < 8; s++)
        {
            x[s] = baseX[s] + colOff[s];
            y[s] = baseY + rowOff[s];
            source[s] = (animFrame * 8 + s) % poolCount;
        }
    }

    private void ResetAllFramesToDefaults()
    {
        int fc = Math.Max(1, _animFrameCount);
        _posX = new int[fc][];
        _posY = new int[fc][];
        _spriteSource = new int[fc][];
        for (int f = 0; f < fc; f++)
        {
            _posX[f] = new int[8];
            _posY[f] = new int[8];
            _spriteSource[f] = new int[8];
            SetDefaultFrame(f, _posX[f], _posY[f], _spriteSource[f]);
        }
        _animFrameCount = fc;
        _frame = 0;
        _frameScrub.Maximum = Math.Max(0, fc - 1);
    }

    /// <summary>Resets the Timeline to `frameCount` fresh default frames -
    /// used when a whole new bank/project is loaded (New Blank/Procedural
    /// Bank, Load Sprite Bank), where an incremental resize doesn't make
    /// sense since none of the old keyframes are relevant any more.</summary>
    public void ResetForNewBank(int frameCount)
    {
        _animFrameCount = Math.Max(1, frameCount);
        ResetAllFramesToDefaults();
        LoadFrameIntoCanvas();
    }

    private void LoadFrameIntoCanvas()
    {
        EnsureArraysAllocated();
        Array.Copy(_posX[_frame], _canvas.SpriteX, 8);
        Array.Copy(_posY[_frame], _canvas.SpriteY, 8);
        if (_frameScrub.Value != _frame)
        {
            bool prev = _suppressEvents;
            _suppressEvents = true;
            try { _frameScrub.Value = _frame; } finally { _suppressEvents = prev; }
        }
        _frameLabel.Text = $"Frame {_frame + 1}/{_animFrameCount}";
        if (!_frameCountUpDown.ContainsFocus)
            _frameCountUpDown.Value = Math.Clamp(_animFrameCount, (int)_frameCountUpDown.Minimum, (int)_frameCountUpDown.Maximum);
        _canvas.Invalidate();
        RecomputeD010Label();
        RefreshInspector();
        FrameChanged?.Invoke(_frame);
        SelectedSpriteSourceChanged?.Invoke();
    }

    /// <summary>Jumps to a specific frame - used by MainForm, which now owns
    /// no frame-selection UI of its own (the FRAMES thumbnail strip was
    /// removed; this panel's Timeline is the only frame navigator).</summary>
    public void GoToFrame(int frame)
    {
        EnsureArraysAllocated();
        _frame = Math.Max(0, Math.Min(frame, _animFrameCount - 1));
        LoadFrameIntoCanvas();
    }

    /// <summary>Re-syncs the canvas/inspector after the bank's PieceCount
    /// changes elsewhere (grow/shrink, undo, a fresh pixel edit) - the
    /// Timeline's own length is untouched, this only repaints and refreshes
    /// the on-canvas Sprite # labels/inspector.</summary>
    public void RefreshAfterPoolChange() => LoadFrameIntoCanvas();

    /// <summary>Fires whenever the active frame changes, for whatever
    /// reason (Timeline nav, play, or GoToFrame) - MainForm mirrors this
    /// into its own _currentFrame so pixel editing always tracks whichever
    /// frame is on screen here.</summary>
    public event Action<int>? FrameChanged;

    /// <summary>Toolbar Glue: moves the selected sprite(s), as one
    /// block, flush against the nearest other sprite - see SpriteGlue for
    /// how the placement is chosen. Returns a status line for MainForm.</summary>
    public string GlueSelection()
    {
        EnsureArraysAllocated();
        var selected = new List<int>(_canvas.SelectedSprites);
        if (selected.Count == 0) return "Glue: select a sprite in Construct first (Shift+click).";
        if (selected.Count >= 8) return "Glue: every sprite is selected - there's nothing left to glue to.";

        var move = SpriteGlue.FindBestMove(_canvas.SpriteX, _canvas.SpriteY, selected);
        if (move is not { } m) return "Glue: no free spot next to another sprite fits.";

        string what = selected.Count == 1 ? $"Sprite {selected[0]}" : $"{selected.Count} sprites";
        string where = m.Side switch
        {
            SpriteGlue.Side.Left => "left of",
            SpriteGlue.Side.Right => "right of",
            SpriteGlue.Side.Above => "above",
            _ => "below"
        };
        if (m.Dx == 0 && m.Dy == 0) return $"Glue: {what} is already glued {where} sprite {m.Target}.";

        PushPositionUndo();
        foreach (int s in selected)
        {
            _canvas.SpriteX[s] += m.Dx;
            _canvas.SpriteY[s] += m.Dy;
        }
        _canvas.Invalidate();
        CommitCanvasPositionsToCurrentFrame();
        return $"Glued {what} {where} sprite {m.Target} (moved {m.Dx:+0;-0;0}, {m.Dy:+0;-0;0}).";
    }

    private void CommitCanvasPositionsToCurrentFrame()
    {
        EnsureArraysAllocated();
        Array.Copy(_canvas.SpriteX, _posX[_frame], 8);
        Array.Copy(_canvas.SpriteY, _posY[_frame], 8);
        RecomputeD010Label();
        RefreshInspector();
    }

    // ---------------------------------------------------------------------
    // Position + sprite-source undo/redo - snapshots one frame's full
    // 8-sprite state.
    // ---------------------------------------------------------------------
    private void PushPositionUndo()
    {
        EnsureArraysAllocated();
        _posUndo.Add((_frame, (int[])_posX[_frame].Clone(), (int[])_posY[_frame].Clone(), (int[])_spriteSource[_frame].Clone(), UndoClock.Next()));
        while (_posUndo.Count > PosUndoCap) _posUndo.RemoveAt(0);
        _posRedo.Clear();
        PositionEdited?.Invoke();
    }

    /// <summary>A new position/Sprite # edit was recorded - MainForm clears
    /// its own pixel redo stack on this, so the shared history stays linear.</summary>
    public event Action? PositionEdited;

    /// <summary>UndoClock stamps of the newest undo/redo entries (0 = empty) -
    /// MainForm compares these with its pixel stacks to undo/redo whichever
    /// change is most recent.</summary>
    public long PositionUndoTopSeq => _posUndo.Count > 0 ? _posUndo[^1].seq : 0;
    public long PositionRedoTopSeq => _posRedo.Count > 0 ? _posRedo[^1].seq : 0;

    public void ClearPositionRedo() => _posRedo.Clear();

    public void UndoPosition()
    {
        if (_posUndo.Count == 0) return;
        var last = _posUndo[^1];
        _posUndo.RemoveAt(_posUndo.Count - 1);
        EnsureArraysAllocated();
        if (last.frame >= _animFrameCount) return; // frame count shrank since this entry was pushed
        _posRedo.Add((last.frame, (int[])_posX[last.frame].Clone(), (int[])_posY[last.frame].Clone(), (int[])_spriteSource[last.frame].Clone(), last.seq));
        Array.Copy(last.x, _posX[last.frame], 8);
        Array.Copy(last.y, _posY[last.frame], 8);
        Array.Copy(last.source, _spriteSource[last.frame], 8);
        _frame = last.frame;
        LoadFrameIntoCanvas();
    }

    public void RedoPosition()
    {
        if (_posRedo.Count == 0) return;
        var last = _posRedo[^1];
        _posRedo.RemoveAt(_posRedo.Count - 1);
        EnsureArraysAllocated();
        if (last.frame >= _animFrameCount) return;
        _posUndo.Add((last.frame, (int[])_posX[last.frame].Clone(), (int[])_posY[last.frame].Clone(), (int[])_spriteSource[last.frame].Clone(), last.seq));
        Array.Copy(last.x, _posX[last.frame], 8);
        Array.Copy(last.y, _posY[last.frame], 8);
        Array.Copy(last.source, _spriteSource[last.frame], 8);
        _frame = last.frame;
        LoadFrameIntoCanvas();
    }

    private void StepFrame(int delta)
    {
        EnsureArraysAllocated();
        _frame = ((_frame + delta) % _animFrameCount + _animFrameCount) % _animFrameCount;
        LoadFrameIntoCanvas();
    }


    /// <summary>Snapshots the current animation frame's FULL per-sprite
    /// state - position AND Sprite # source, for all 8 hardware sprites -
    /// into an in-memory clipboard, for PasteFrameFromClipboard to drop
    /// into any (possibly different) frame later. Separate from MainForm's
    /// own pixel-art clipboard (_clipboard there), which only ever holds
    /// one piece's 12x21 art.</summary>
    public void CopyCurrentFrameToClipboard()
    {
        EnsureArraysAllocated();
        _frameClipboard = ((int[])_posX[_frame].Clone(), (int[])_posY[_frame].Clone(), (int[])_spriteSource[_frame].Clone());
    }

    public void PasteFrameFromClipboard()
    {
        if (_frameClipboard == null) return;
        EnsureArraysAllocated();
        PushPositionUndo();
        var (x, y, source) = _frameClipboard.Value;
        Array.Copy(x, _posX[_frame], 8);
        Array.Copy(y, _posY[_frame], 8);
        Array.Copy(source, _spriteSource[_frame], 8);
        LoadFrameIntoCanvas();
    }

    /// <summary>Inserts a new animation frame (Timeline step) at index `at`,
    /// as a duplicate of whatever was there before - so the sequence reads
    /// unchanged until the new frame is actually edited into an in-between
    /// pose - and shifts everything from `at` onward one step later. Grows
    /// the Timeline by one itself; never touches the bank/pool at all.</summary>
    public void InsertFrameCopy(int at)
    {
        EnsureArraysAllocated();
        int oldCount = _animFrameCount;
        GrowOrShrinkArraysTo(oldCount + 1);
        at = Math.Max(0, Math.Min(at, oldCount - 1));
        for (int i = _animFrameCount - 1; i > at; i--)
        {
            Array.Copy(_posX[i - 1], _posX[i], 8);
            Array.Copy(_posY[i - 1], _posY[i], 8);
            Array.Copy(_spriteSource[i - 1], _spriteSource[i], 8);
        }
        _frame = at;
        LoadFrameIntoCanvas();
    }

    /// <summary>Splices animation frame `at` out of the Timeline, shifting
    /// everything after it one step earlier and shrinking by one. Never
    /// touches the bank/pool at all - a piece this frame referenced simply
    /// becomes unreferenced (still there, available for reuse), never
    /// deleted, so there's no risk of losing art by deleting a frame.</summary>
    public void RemoveFrame(int at)
    {
        EnsureArraysAllocated();
        if (_animFrameCount <= 1) return;
        at = Math.Max(0, Math.Min(at, _animFrameCount - 1));
        for (int i = at; i < _animFrameCount - 1; i++)
        {
            Array.Copy(_posX[i + 1], _posX[i], 8);
            Array.Copy(_posY[i + 1], _posY[i], 8);
            Array.Copy(_spriteSource[i + 1], _spriteSource[i], 8);
        }
        GrowOrShrinkArraysTo(_animFrameCount - 1);
        _frame = Math.Min(at, _animFrameCount - 1);
        LoadFrameIntoCanvas();
    }

    private void RecomputeD010Label()
    {
        int msb = 0;
        for (int s = 0; s < 8; s++)
            if (_canvas.SpriteX[s] > 255) msb |= 1 << s;
        _d010Label.Text = $"$d010 = %{Convert.ToString(msb, 2).PadLeft(8, '0')} (${msb:X2})   " +
                          $"$d020 = ${EditorPalette.BorderIndex:X2}   $d021 = ${EditorPalette.BackgroundIndex:X2}";
    }

    /// <summary>Called by MainForm after every pixel edit so the composited
    /// preview stays live while drawing - the two panels share the same
    /// SpriteBank instance, but this panel never repaints on its own just
    /// because the pixel editor's data changed.</summary>
    public void RefreshSpriteArt() => _canvas.Invalidate();

    /// <summary>Repaints just the hardware sprites currently showing
    /// <paramref name="piece"/> - the cheap path for a single-pixel edit.</summary>
    public void RefreshSpriteArt(int piece)
    {
        EnsureArraysAllocated();
        for (int s = 0; s < 8; s++)
            if (_spriteSource[_frame][s] == piece) _canvas.InvalidateSprite(s);
    }

    /// <summary>Called by MainForm when any EditorPalette register changes -
    /// repaints with the new colours and updates the $d020/$d021 readout.</summary>
    public void RefreshVicColors()
    {
        RecomputeD010Label();
        _canvas.Invalidate();
    }

    /// <summary>The piece index into the bank's flat pool this hardware
    /// sprite shows at the given animation frame.</summary>
    public int GetSpriteSource(int frame, int spriteIndex)
    {
        EnsureArraysAllocated();
        int f = Math.Max(0, Math.Min(frame, _animFrameCount - 1));
        return _spriteSource[f][spriteIndex];
    }

    /// <summary>Repoints one hardware sprite's Sprite # source directly,
    /// without going through the canvas selection/NumericUpDown - used by
    /// MainForm's copy-on-write fork (see EnsureExclusiveSlot there) to
    /// redirect a (frame, sprite) pair at a freshly cloned piece right
    /// before a pixel edit would otherwise land on art shared with other
    /// frames/sprites.</summary>
    public void SetSpriteSource(int frame, int spriteIndex, int piece)
    {
        EnsureArraysAllocated();
        if (frame < 0 || frame >= _animFrameCount) return;
        _spriteSource[frame][spriteIndex] = piece;
        if (frame == _frame)
        {
            _canvas.Invalidate();
            if (spriteIndex == _canvas.PrimarySelected)
            {
                RefreshInspector();
                SelectedSpriteSourceChanged?.Invoke();
            }
        }
    }

    /// <summary>The primary-selected hardware sprite index (0-7), or -1 if
    /// none is selected - the same sprite Single Sprite View follows via
    /// SelectedSpriteSourceChanged/GetSelectedSpriteTarget.</summary>
    public int PrimarySelectedSprite => _canvas.PrimarySelected;

    /// <summary>Fires whenever the primary-selected hardware sprite, or what
    /// it resolves to (Sprite # edit, or the active Timeline frame changing
    /// which piece that Sprite # points at), changes - MainForm's Single
    /// Sprite View follows this to know which piece to edit.</summary>
    public event Action? SelectedSpriteSourceChanged;

    /// <summary>The piece index the primary-selected hardware sprite
    /// currently shows, or null if nothing is selected.</summary>
    public int? GetSelectedSpriteTarget()
    {
        if (_canvas.PrimarySelected < 0) return null;
        return GetSpriteSource(_frame, _canvas.PrimarySelected);
    }

    // ---------------------------------------------------------------------
    // Project persistence - in-memory DTO only, no file I/O here. MainForm's
    // ProjectFile composes this together with the SpriteBank's pixel data
    // and the backdrop into one project file. SpriteSource values are plain
    // piece indices, numerically unchanged by the flat-pool refactor (they
    // were already a flat bankFrame*4+quad index before), so this DTO and
    // ImportData/ExportData need no format migration of their own - only
    // SpriteBank.FromData's shape does (see there).
    // ---------------------------------------------------------------------
    public sealed class ConstructPanelData
    {
        public int FrameCount { get; set; }
        public int[][] PosX { get; set; } = Array.Empty<int[]>();
        public int[][] PosY { get; set; } = Array.Empty<int[]>();
        public int[][] SpriteSource { get; set; } = Array.Empty<int[]>();
    }

    public ConstructPanelData ExportData()
    {
        EnsureArraysAllocated();
        var data = new ConstructPanelData
        {
            FrameCount = _animFrameCount,
            PosX = new int[_animFrameCount][],
            PosY = new int[_animFrameCount][],
            SpriteSource = new int[_animFrameCount][]
        };
        for (int f = 0; f < _animFrameCount; f++)
        {
            data.PosX[f] = (int[])_posX[f].Clone();
            data.PosY[f] = (int[])_posY[f].Clone();
            data.SpriteSource[f] = (int[])_spriteSource[f].Clone();
        }
        return data;
    }

    public void ImportData(ConstructPanelData data)
    {
        int fc = Math.Max(1, data.FrameCount);
        var newX = new int[fc][];
        var newY = new int[fc][];
        var newSource = new int[fc][];
        for (int f = 0; f < fc; f++)
        {
            newX[f] = new int[8];
            newY[f] = new int[8];
            newSource[f] = new int[8];
            bool posValid = f < data.PosX.Length && f < data.PosY.Length
                            && data.PosX[f]?.Length == 8 && data.PosY[f]?.Length == 8;
            bool sourceValid = f < data.SpriteSource.Length && data.SpriteSource[f]?.Length == 8;

            if (posValid) { Array.Copy(data.PosX[f]!, newX[f], 8); Array.Copy(data.PosY[f]!, newY[f], 8); }
            if (sourceValid) Array.Copy(data.SpriteSource[f]!, newSource[f], 8);

            if (!posValid || !sourceValid)
            {
                // Older project files (pre "Sprite #") have positions but no
                // SpriteSource - fill in only whichever half is actually
                // missing, so a legacy file still restores its positions.
                var defX = new int[8]; var defY = new int[8]; var defSource = new int[8];
                SetDefaultFrame(f, defX, defY, defSource);
                if (!posValid) { newX[f] = defX; newY[f] = defY; }
                if (!sourceValid) newSource[f] = defSource;
            }
        }
        _posX = newX;
        _posY = newY;
        _spriteSource = newSource;
        _animFrameCount = fc;

        _frame = 0;
        _frameScrub.Maximum = Math.Max(0, fc - 1);
        _canvas.SelectedSprites.Clear();
        _canvas.PrimarySelected = -1;
        _posUndo.Clear();
        _posRedo.Clear();
        LoadFrameIntoCanvas();
    }

    // ---------------------------------------------------------------------
    // Playback
    // ---------------------------------------------------------------------
    private void TogglePlay()
    {
        if (_playTimer.Enabled) { StopPlay(); return; }
        _playTimer.Interval = Math.Max(20, (int)(1000 / _fpsUpDown.Value));
        _playTimer.Start();
        SetPlayButtonState(playing: true);
    }

    private void StopPlay()
    {
        _playTimer.Stop();
        SetPlayButtonState(playing: false);
    }

    private void SetPlayButtonState(bool playing)
    {
        _playButton.Image = playing ? Icons.Stop() : Icons.Play(); // Icons.Stop draws a pause glyph
        _playButton.BackColor = playing ? TransportPlaying : TransportBack;
        _tips.SetToolTip(_playButton, playing ? "Pause" : "Play");
    }

    // ---------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------
    // Deduplicated ASM export - actually shrinks the compiled output when
    // there's repeated content (e.g. a "cleared" sprite reused across many
    // frames/pieces): only the distinct 12x21 pieces are emitted once each,
    // plus small per-frame tables (pointer index, $d010, X, Y) that
    // Fire_Frame looks up at runtime instead of the fixed constants/simple
    // arithmetic it uses today, and magspriteed_opt_pingpong (the Timeline's
    // own Ping-pong checkbox) telling it whether to bounce back and forth or
    // just wrap forward. Requires Startup\Fire.s's
    // m_fire_sprites_optimized/m_fire_code_optimized macros, selected via
    // FIRE_COMPOSITION_TABLES at build time - the default build is
    // completely unaffected unless that's defined.
    // ---------------------------------------------------------------------
    /// <summary>Every raw piece index actually referenced by some (animation
    /// frame, hardware sprite) pair right now - used to keep the export's
    /// dedup restricted to pieces the Timeline genuinely uses, so pieces
    /// left over in the pool from editing history (a deleted frame's old
    /// Sprite #, a copy-on-write fork later edited away from, the pool
    /// simply resized bigger than anything ended up using) are dropped
    /// from the compiled output instead of each still being emitted as
    /// its own "unique" sprite.</summary>
    private HashSet<int> ReferencedPieces()
    {
        EnsureArraysAllocated();
        var referenced = new HashSet<int>();
        for (int f = 0; f < _animFrameCount; f++)
            for (int s = 0; s < 8; s++)
                referenced.Add(_spriteSource[f][s]);
        return referenced;
    }

    private string BuildOptimizedAsmExport()
    {
        EnsureArraysAllocated();
        var bank = _bankProvider();
        var dedup = bank.BuildDedupMap(ReferencedPieces());

        var sb = new StringBuilder();
        sb.AppendLine("// Deduplicated sprite composition exported from MagSpriteEd's Construct panel.");
        sb.AppendLine($"// {dedup.CanonicalCount} distinct 12x21 piece(s) stored (the pool has {bank.PieceCount} raw");
        sb.AppendLine("// slots, but only pieces some (frame, hardware sprite) actually references are exported -");
        sb.AppendLine("// unreferenced pool pieces left over from editing history cost nothing here) - identical");
        sb.AppendLine("// art, such as a shared \"cleared\" sprite reused across frames, is written once and");
        sb.AppendLine("// referenced by index everywhere it's used.");
        sb.AppendLine("// Included from Startup\\Fire.s's m_fire_sprites_optimized/m_fire_code_optimized macros");
        sb.AppendLine("// when FIRE_COMPOSITION_TABLES is defined at build time - do not hand-edit, re-export instead.");
        sb.AppendLine();
        sb.AppendLine($"var magspriteed_opt_sprite_count = {dedup.CanonicalCount}");
        sb.AppendLine($"var magspriteed_opt_frame_count = {_animFrameCount}");
        sb.AppendLine($"var magspriteed_opt_pingpong = {(_pingPongCheck.Checked ? 1 : 0)}");
        sb.AppendLine();

        // Per-canonical-sprite hires flag (see SpriteBank.IsHires) - 1 byte
        // per distinct piece above, in the same canonical order, for the
        // build to set/clear that sprite's $d01c multicolour bit whenever
        // magspriteed_ptr_table below points a hardware sprite at it.
        sb.AppendLine("magspriteed_opt_hires:");
        sb.Append("        .byte ");
        for (int i = 0; i < dedup.CanonicalCount; i++)
        {
            sb.Append(bank.IsHires(dedup.CanonicalToSlot[i]) ? 1 : 0);
            if (i < dedup.CanonicalCount - 1) sb.Append(',');
        }
        sb.AppendLine();
        sb.AppendLine();

        for (int i = 0; i < dedup.CanonicalCount; i++)
        {
            int piece = dedup.CanonicalToSlot[i];
            bank.EmitSpriteAsmBlock(sb, $"magspriteed_opt_sprite_{i}", piece);
        }
        sb.AppendLine();

        // $e900: just past Startup\Fadeout.s's Fadeout_Tables ($e000,
        // ~2258 bytes, ending ~$e8d2) - these per-frame tables are plain
        // CPU-read data (unlike the sprite pieces above, which must stay
        // in the $4000 VIC bank), so they're free to live anywhere; moving
        // them out of the $6880-$d000 budget leaves more of it for
        // Lightning_Tables. At 25 bytes/frame (magspriteed_ptr_table 8 +
        // magspriteed_d010_table 1 + magspriteed_pos_x_lo 8 + magspriteed_
        // pos_y 8) even 31 frames (the animframe*8 byte-fit ceiling) is
        // under 800 bytes, well inside the ~6KB free before $ffff.
        sb.AppendLine("        org $e900, \"MagSpriteEd_Composition_Tables\"");
        sb.AppendLine();

        sb.AppendLine("magspriteed_ptr_table:");
        for (int f = 0; f < _animFrameCount; f++)
        {
            sb.Append("        .byte ");
            for (int s = 0; s < 8; s++)
            {
                // raw is guaranteed to be in ReferencedPieces() (that set was
                // built from these exact values), so it always has a valid
                // canonical index here - never the -1 BuildDedupMap(subset)
                // leaves for pieces outside the requested set.
                int raw = _spriteSource[f][s];
                int canonical = dedup.SlotToCanonical[raw];
                sb.Append(canonical);
                if (s < 7) sb.Append(',');
            }
            sb.AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine("magspriteed_d010_table:");
        sb.Append("        .byte ");
        for (int f = 0; f < _animFrameCount; f++)
        {
            int msb = 0;
            for (int s = 0; s < 8; s++)
                if (_posX[f][s] > 255) msb |= 1 << s;
            sb.Append($"${msb:X2}");
            if (f < _animFrameCount - 1) sb.Append(',');
        }
        sb.AppendLine();
        sb.AppendLine();

        sb.AppendLine("magspriteed_pos_x_lo:");
        for (int f = 0; f < _animFrameCount; f++)
        {
            sb.Append("        .byte ");
            for (int s = 0; s < 8; s++)
            {
                sb.Append($"${_posX[f][s] & 0xFF:X2}");
                if (s < 7) sb.Append(',');
            }
            sb.AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine("magspriteed_pos_y:");
        for (int f = 0; f < _animFrameCount; f++)
        {
            sb.Append("        .byte ");
            for (int s = 0; s < 8; s++)
            {
                sb.Append($"${_posY[f][s] & 0xFF:X2}");
                if (s < 7) sb.Append(',');
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Menu > Export Animation: the whole Construct animation as
    /// ASM - the deduplicated sprites actually used, plus per-frame tables
    /// (Sprite # pointers, X/Y, $d010, hires flags, ping-pong).</summary>
    public void ExportAnimation()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "Export animation (deduplicated sprites + per-frame tables)",
            Filter = "Assembly source (*.s)|*.s|All files (*.*)|*.*",
            FileName = "MagSpriteEd_Composition_Data.s"
        };
        if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;
        var bank = _bankProvider();
        var dedup = bank.BuildDedupMap(ReferencedPieces());
        File.WriteAllText(sfd.FileName, BuildOptimizedAsmExport());
        MessageBox.Show(FindForm(),
            $"Exported {dedup.CanonicalCount} unique sprite(s) actually used by the Timeline " +
            $"(pool has {bank.PieceCount} raw slots total) to:\n{sfd.FileName}\n\n" +
            "Build with FIRE_COMPOSITION_TABLES defined (c6510 -d FIRE_COMPOSITION_TABLES ...) to use it.",
            "Exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
