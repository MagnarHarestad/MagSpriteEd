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
    private DoubleBufferedListView _list = null!;
    private Label _d010Label = null!;
    private Label _groupLabel = null!;
    private NumericUpDown _xUpDown = null!, _yUpDown = null!;
    private NumericUpDown _spriteNumberUpDown = null!;
    private Button _playButton = null!;
    private NumericUpDown _fpsUpDown = null!;
    private ComboBox _zoomCombo = null!;
    private CheckBox _outlinesCheck = null!;
    private TrackBar _frameScrub = null!;
    private Label _frameLabel = null!;

    private readonly System.Windows.Forms.Timer _playTimer = new() { Interval = 180 };
    private int _frame;
    private CheckBox _pingPongCheck = null!;
    private int _playDir = 1;

    // Guards against event re-entrancy: RefreshList() selects a row, which
    // fires ListView.SelectedIndexChanged, which updates the X/Y/sprite#
    // editors, whose *own* Changed events would otherwise call RefreshList()
    // again mid-rebuild - a feedback loop that a fast mouse-drag (many
    // SpriteMoved events in quick succession) reliably turned into a
    // NullReferenceException inside the ListView. Every handler that only
    // reacts to a programmatic update checks this first.
    private bool _suppressEvents;

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
    private readonly List<(int frame, int[] x, int[] y, int[] source)> _posUndo = new();
    private readonly List<(int frame, int[] x, int[] y, int[] source)> _posRedo = new();
    private const int PosUndoCap = 100;

    // Individual colour alternation exactly as Fire_Frame writes to
    // $d027-$d02e: 8,10,8,10,10,8,10,8 (orange/light-red).
    private static readonly byte[] IndividualPaletteIndex = { 8, 10, 8, 10, 10, 8, 10, 8 };

    public event EventHandler? LoadBackdropRequested;

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
        if (disposing) _playTimer.Dispose();
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------------
    // UI construction
    // ---------------------------------------------------------------------
    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));

        _canvas = new ConstructCanvas
        {
            SpritePixel = (spriteIdx, row, col) =>
            {
                var bank = _bankProvider();
                int source = _spriteSource[_frame][spriteIdx];
                if (source < 0 || source >= bank.PieceCount) return 0;
                return bank.Get(source, row, col);
            },
            PaletteProvider = (v, spriteIdx) => v switch
            {
                1 => Color.FromArgb(129, 51, 43),
                2 => IndividualPaletteIndex[spriteIdx] == 8 ? Color.FromArgb(133, 76, 27) : Color.FromArgb(175, 101, 94),
                3 => Color.FromArgb(214, 225, 132),
                _ => Color.Transparent
            }
        };
        _canvas.ApplyZoomedSize();
        _canvas.SelectionChanged += () => { SyncListSelection(); UpdateSelectedEditors(); SelectedSpriteSourceChanged?.Invoke(); };
        _canvas.SpriteMoved += CommitCanvasPositionsToCurrentFrame;
        // These are the base Control.MouseDown/KeyDown events (raised via
        // base.OnMouseDown/base.OnKeyDown as the FIRST line of ConstructCanvas's
        // own overrides), so they fire before any position is actually
        // changed - the correct moment to snapshot for undo. MouseDown covers
        // the start of every drag; KeyDown covers arrow-key nudges and also
        // doubles as the Ctrl+Z/Ctrl+Y shortcut while the canvas has focus.
        _canvas.MouseDown += (_, _) => PushPositionUndo();
        _canvas.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Z && !e.Shift) { UndoPosition(); return; }
            if (e.Control && (e.KeyCode == Keys.Y || (e.KeyCode == Keys.Z && e.Shift))) { RedoPosition(); return; }
            if (_canvas.SelectedSprites.Count > 0 && e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
                PushPositionUndo();
        };

        var canvasScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(10, 10, 10), Padding = new Padding(12) };
        _canvas.Location = new Point(12, 12);
        canvasScroll.Controls.Add(_canvas);

        var side = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8), BackColor = Color.FromArgb(22, 22, 22) };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Width = 278 };

        flow.Controls.Add(MakeGroup("Backdrop", BuildBackdropRow()));
        flow.Controls.Add(MakeGroup("Timeline", BuildTimelineRow()));
        flow.Controls.Add(MakeGroup("Selected sprite", BuildSelectedSpriteRow()));

        _list = new DoubleBufferedListView { View = View.Details, FullRowSelect = true, MultiSelect = true, GridLines = true, HideSelection = false, Width = 258, Height = 190, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro };
        _list.Columns.Add("#", 24);
        _list.Columns.Add("Sprite#", 52);
        _list.Columns.Add("X", 40);
        _list.Columns.Add("Y", 40);
        _list.Columns.Add("MSB", 36);
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _canvas.SelectedSprites.Clear();
            foreach (int idx in _list.SelectedIndices) _canvas.SelectedSprites.Add(idx);
            _canvas.PrimarySelected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[_list.SelectedIndices.Count - 1] : -1;
            UpdateSelectedEditors();
            _canvas.Invalidate();
        };
        flow.Controls.Add(MakeGroup("Sprites (0-7, this frame) - Ctrl/Shift-click to group", _list));

        _d010Label = new Label { Text = "$d010 = %00000000 ($00)", AutoSize = true, ForeColor = Color.Gainsboro };
        flow.Controls.Add(MakeGroup("VIC registers (this frame)", _d010Label));

        flow.Controls.Add(MakeGroup("Actions", BuildActionsRow()));

        side.Controls.Add(flow);

        root.Controls.Add(canvasScroll, 0, 0);
        root.Controls.Add(side, 1, 0);
        Controls.Add(root);
    }

    private Control BuildBackdropRow()
    {
        var loadBtn = new Button { Text = "Load Backdrop (.kla)...", AutoSize = true, Margin = new Padding(2) };
        loadBtn.Click += (_, _) => LoadBackdropRequested?.Invoke(this, EventArgs.Empty);

        var zoomRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        zoomRow.Controls.Add(new Label { Text = "Zoom", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _zoomCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
        _zoomCombo.Items.AddRange(new object[] { "1x", "2x", "3x" });
        _zoomCombo.SelectedIndex = 1;
        _zoomCombo.SelectedIndexChanged += (_, _) =>
        {
            _canvas.Zoom = _zoomCombo.SelectedIndex + 1;
            _canvas.ApplyZoomedSize();
            _canvas.Invalidate();
        };
        zoomRow.Controls.Add(_zoomCombo);
        zoomRow.Size = zoomRow.PreferredSize;

        _outlinesCheck = new CheckBox { Text = "Show sprite outlines", ForeColor = Color.Gainsboro, AutoSize = true, Checked = true };
        _outlinesCheck.CheckedChanged += (_, _) => { _canvas.ShowOutlines = _outlinesCheck.Checked; _canvas.Invalidate(); };

        return Stack(loadBtn, zoomRow, _outlinesCheck);
    }

    private Control BuildTimelineRow()
    {
        _playButton = new Button { Text = "Play", Width = 60, Margin = new Padding(2) };
        _playButton.Click += (_, _) => TogglePlay();
        var prev = new Button { Text = "<", Width = 30, Margin = new Padding(2) };
        prev.Click += (_, _) => { StopPlay(); StepFrame(-1); };
        var next = new Button { Text = ">", Width = 30, Margin = new Padding(2) };
        next.Click += (_, _) => { StopPlay(); StepFrame(1); };
        var playRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        playRow.Controls.Add(_playButton); playRow.Controls.Add(prev); playRow.Controls.Add(next);
        playRow.Controls.Add(new Label { Text = "FPS", AutoSize = true, Padding = new Padding(6, 6, 2, 0) });
        _fpsUpDown = new NumericUpDown { Minimum = 1, Maximum = 50, Value = 17, Width = 50 };
        _fpsUpDown.ValueChanged += (_, _) => _playTimer.Interval = Math.Max(20, (int)(1000 / _fpsUpDown.Value));
        playRow.Controls.Add(_fpsUpDown);
        _pingPongCheck = new CheckBox
        {
            Text = "Ping-pong",
            ForeColor = Color.Gainsboro,
            AutoSize = true,
            Padding = new Padding(8, 4, 0, 0),
            // Also written into the "Export Optimized ASM" output as
            // var fire_opt_pingpong - Fire.s's Fire_Frame reads that to
            // pick ping-pong vs. simple forward-wrap playback on the C64
            // itself (see BuildOptimizedAsmExport), so this one checkbox
            // controls both the editor's own preview loop and the shipped
            // demo's animation.
        };
        _playDir = 1;
        playRow.Controls.Add(_pingPongCheck);
        playRow.Size = playRow.PreferredSize;

        _frameLabel = new Label { Text = "Frame 1/8", AutoSize = true, ForeColor = Color.Gainsboro };
        _frameScrub = new ClickToPositionTrackBar { Minimum = 0, Maximum = 7, Width = 250, TickStyle = TickStyle.BottomRight };
        _frameScrub.ValueChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            if (_frameScrub.Value == _frame) return;
            StopPlay();
            _frame = _frameScrub.Value;
            LoadFrameIntoCanvas();
        };

        return Stack(playRow, _frameLabel, _frameScrub);
    }

    private Control BuildSelectedSpriteRow()
    {
        _xUpDown = new NumericUpDown { Minimum = 0, Maximum = 511, Width = 60 };
        _yUpDown = new NumericUpDown { Minimum = 0, Maximum = 255, Width = 60 };
        // Undo is pushed once per editing session (on focus-in), not per tick -
        // otherwise holding the spinner arrow or typing a value would splinter
        // into one undo entry per keystroke/click.
        _xUpDown.Enter += (_, _) => { if (!_suppressEvents) PushPositionUndo(); };
        _yUpDown.Enter += (_, _) => { if (!_suppressEvents) PushPositionUndo(); };
        _xUpDown.ValueChanged += (_, _) => { if (_suppressEvents) return; if (_canvas.PrimarySelected >= 0) { _canvas.SpriteX[_canvas.PrimarySelected] = (int)_xUpDown.Value; _canvas.Invalidate(); CommitCanvasPositionsToCurrentFrame(); } };
        _yUpDown.ValueChanged += (_, _) => { if (_suppressEvents) return; if (_canvas.PrimarySelected >= 0) { _canvas.SpriteY[_canvas.PrimarySelected] = (int)_yUpDown.Value; _canvas.Invalidate(); CommitCanvasPositionsToCurrentFrame(); } };
        var xyRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        xyRow.Controls.Add(new Label { Text = "X", AutoSize = true, Padding = new Padding(0, 6, 2, 0) });
        xyRow.Controls.Add(_xUpDown);
        xyRow.Controls.Add(new Label { Text = "Y", AutoSize = true, Padding = new Padding(6, 6, 2, 0) });
        xyRow.Controls.Add(_yUpDown);
        xyRow.Size = xyRow.PreferredSize;

        // Sprite # is shown/edited as a CANONICAL (deduplicated) index, not
        // the raw piece index - otherwise identical content reused in
        // several places (e.g. a shared "cleared" sprite) would show a
        // different number each time instead of the same one. See
        // CurrentDedupMap().
        _spriteNumberUpDown = new NumericUpDown { Minimum = 0, Maximum = 255, Width = 60 };
        _spriteNumberUpDown.Enter += (_, _) => { if (!_suppressEvents) PushPositionUndo(); };
        _spriteNumberUpDown.ValueChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            if (_canvas.PrimarySelected >= 0)
            {
                EnsureArraysAllocated();
                var dedup = CurrentDedupMap();
                int canonical = (int)_spriteNumberUpDown.Value;
                int raw = canonical < dedup.CanonicalCount ? dedup.CanonicalToSlot[canonical] : 0;
                _spriteSource[_frame][_canvas.PrimarySelected] = raw;
                UpdateListRow(_canvas.PrimarySelected);
                _canvas.Invalidate();
                SelectedSpriteSourceChanged?.Invoke();
            }
        };
        var srcRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        srcRow.Controls.Add(new Label { Text = "Sprite #", AutoSize = true, Padding = new Padding(0, 6, 2, 0) });
        srcRow.Controls.Add(_spriteNumberUpDown);
        srcRow.Size = srcRow.PreferredSize;

        _groupLabel = new Label { Text = "No selection", AutoSize = true, ForeColor = Color.FromArgb(255, 200, 60) };

        var hint = new Label
        {
            Text = "Shortcuts:\n" +
                   "Drag - move selection\n" +
                   "Shift+drag/arrow - move x8\n" +
                   "Ctrl+click - add/remove from group\n" +
                   "Ctrl+Z / Ctrl+Y - undo/redo position",
            AutoSize = true,
            ForeColor = Color.DarkGray
        };

        return Stack(xyRow, srcRow, _groupLabel, hint);
    }

    private Control BuildActionsRow()
    {
        var copyNext = new Button { Text = "Copy positions -> next frame", AutoSize = true, Margin = new Padding(2) };
        copyNext.Click += (_, _) => CopyPositionsToNextFrame();
        var copyAll = new Button { Text = "Copy positions -> all frames", AutoSize = true, Margin = new Padding(2) };
        copyAll.Click += (_, _) => CopyPositionsToAllFrames();
        var resetBtn = new Button { Text = "Reset all frames to defaults", AutoSize = true, Margin = new Padding(2) };
        resetBtn.Click += (_, _) => { ResetAllFramesToDefaults(); LoadFrameIntoCanvas(); };
        return Stack(copyNext, copyAll, resetBtn);
    }

    private static Control Stack(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        foreach (var c in controls) { c.Margin = new Padding(2); flow.Controls.Add(c); }
        flow.Size = flow.PreferredSize;
        return flow;
    }

    private static GroupBox MakeGroup(string title, Control content)
    {
        var gb = new GroupBox { Text = title, ForeColor = Color.Gainsboro, AutoSize = true, Width = 266, Padding = new Padding(6) };
        content.Location = new Point(8, 20);
        gb.Controls.Add(content);
        gb.Height = content.Height + 34;
        return gb;
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
        _canvas.Invalidate();
        RefreshList();
        UpdateSelectedEditors();
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

    /// <summary>Re-syncs the canvas/list/dedup-dependent UI after the
    /// bank's PieceCount changes elsewhere (grow/shrink, undo, a fresh
    /// pixel edit) - the Timeline's own length is untouched, this only
    /// repaints and refreshes Sprite # dropdowns/labels that depend on the
    /// pool's current dedup map.</summary>
    public void RefreshAfterPoolChange() => LoadFrameIntoCanvas();

    /// <summary>Fires whenever the active frame changes, for whatever
    /// reason (Timeline nav, play, or GoToFrame) - MainForm mirrors this
    /// into its own _currentFrame so pixel editing always tracks whichever
    /// frame is on screen here.</summary>
    public event Action<int>? FrameChanged;

    private void CommitCanvasPositionsToCurrentFrame()
    {
        EnsureArraysAllocated();
        Array.Copy(_canvas.SpriteX, _posX[_frame], 8);
        Array.Copy(_canvas.SpriteY, _posY[_frame], 8);
        // Update only the row(s) that actually moved, in place, instead of
        // RefreshList()'s full Clear()+rebuild - this runs on every mouse-move
        // during a drag (now possibly for several sprites at once, dragged as
        // a group), and rebuilding the whole ListView that often is what made
        // it flicker.
        foreach (var s in _canvas.SelectedSprites) UpdateListRow(s);
        RecomputeD010Label();
        UpdateSelectedEditors();
    }

    // ---------------------------------------------------------------------
    // Position + sprite-source undo/redo - snapshots one frame's full
    // 8-sprite state.
    // ---------------------------------------------------------------------
    private void PushPositionUndo()
    {
        EnsureArraysAllocated();
        _posUndo.Add((_frame, (int[])_posX[_frame].Clone(), (int[])_posY[_frame].Clone(), (int[])_spriteSource[_frame].Clone()));
        while (_posUndo.Count > PosUndoCap) _posUndo.RemoveAt(0);
        _posRedo.Clear();
    }

    private void UndoPosition()
    {
        if (_posUndo.Count == 0) return;
        var last = _posUndo[^1];
        _posUndo.RemoveAt(_posUndo.Count - 1);
        EnsureArraysAllocated();
        if (last.frame >= _animFrameCount) return; // frame count shrank since this entry was pushed
        _posRedo.Add((last.frame, (int[])_posX[last.frame].Clone(), (int[])_posY[last.frame].Clone(), (int[])_spriteSource[last.frame].Clone()));
        Array.Copy(last.x, _posX[last.frame], 8);
        Array.Copy(last.y, _posY[last.frame], 8);
        Array.Copy(last.source, _spriteSource[last.frame], 8);
        _frame = last.frame;
        LoadFrameIntoCanvas();
    }

    private void RedoPosition()
    {
        if (_posRedo.Count == 0) return;
        var last = _posRedo[^1];
        _posRedo.RemoveAt(_posRedo.Count - 1);
        EnsureArraysAllocated();
        if (last.frame >= _animFrameCount) return;
        _posUndo.Add((last.frame, (int[])_posX[last.frame].Clone(), (int[])_posY[last.frame].Clone(), (int[])_spriteSource[last.frame].Clone()));
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

    private void CopyPositionsToNextFrame()
    {
        EnsureArraysAllocated();
        int next = (_frame + 1) % _animFrameCount;
        Array.Copy(_posX[_frame], _posX[next], 8);
        Array.Copy(_posY[_frame], _posY[next], 8);
        if (next == _frame) return;
        MessageBox.Show(this, $"Copied frame {_frame + 1}'s positions to frame {next + 1}.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Copies the current animation frame's FULL per-sprite state -
    /// position AND Sprite # source, for all 8 hardware sprites - into the
    /// next animation frame (wrapping at the end), then moves the Timeline
    /// there. Broader than CopyPositionsToNextFrame above, which only
    /// touches X/Y: this is what the toolbar's "Duplicate" button uses, so
    /// clicking it actually reproduces what's on screen right now, one
    /// frame later, instead of just its positions.</summary>
    public void DuplicateFrameToNext()
    {
        EnsureArraysAllocated();
        if (_animFrameCount < 2) return;
        int next = (_frame + 1) % _animFrameCount;
        Array.Copy(_posX[_frame], _posX[next], 8);
        Array.Copy(_posY[_frame], _posY[next], 8);
        Array.Copy(_spriteSource[_frame], _spriteSource[next], 8);
        GoToFrame(next);
    }

    private void CopyPositionsToAllFrames()
    {
        EnsureArraysAllocated();
        for (int f = 0; f < _animFrameCount; f++)
        {
            if (f == _frame) continue;
            Array.Copy(_posX[_frame], _posX[f], 8);
            Array.Copy(_posY[_frame], _posY[f], 8);
        }
        MessageBox.Show(this, $"Copied frame {_frame + 1}'s positions to all {_animFrameCount} frames (no more movement between frames).", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    // ---------------------------------------------------------------------
    // Selection / table sync
    // ---------------------------------------------------------------------
    private void SyncListSelection()
    {
        bool prev = _suppressEvents;
        _suppressEvents = true;
        try
        {
            _list.SelectedIndices.Clear();
            foreach (var s in _canvas.SelectedSprites)
                if (s >= 0 && s < _list.Items.Count) _list.Items[s].Selected = true;
            if (_canvas.PrimarySelected >= 0 && _canvas.PrimarySelected < _list.Items.Count)
                _list.Items[_canvas.PrimarySelected].EnsureVisible();
        }
        finally { _suppressEvents = prev; }
    }

    private void UpdateSelectedEditors()
    {
        int s = _canvas.PrimarySelected;
        bool has = s >= 0;
        _xUpDown.Enabled = _yUpDown.Enabled = _spriteNumberUpDown.Enabled = has;
        _groupLabel.Text = _canvas.SelectedSprites.Count switch
        {
            0 => "No selection",
            1 => $"Sprite {s} selected",
            _ => $"Group of {_canvas.SelectedSprites.Count} selected (primary: {s})"
        };
        if (!has) return;
        EnsureArraysAllocated();
        bool prev = _suppressEvents;
        _suppressEvents = true;
        try
        {
            _xUpDown.Value = Math.Max(_xUpDown.Minimum, Math.Min(_xUpDown.Maximum, _canvas.SpriteX[s]));
            _yUpDown.Value = Math.Max(_yUpDown.Minimum, Math.Min(_yUpDown.Maximum, _canvas.SpriteY[s]));
            var dedup = CurrentDedupMap();
            int raw = _spriteSource[_frame][s];
            int canonical = raw >= 0 && raw < dedup.SlotToCanonical.Length ? dedup.SlotToCanonical[raw] : 0;
            _spriteNumberUpDown.Maximum = Math.Max(0, dedup.CanonicalCount - 1);
            _spriteNumberUpDown.Value = Math.Max(0, Math.Min((int)_spriteNumberUpDown.Maximum, canonical));
        }
        finally { _suppressEvents = prev; }
    }

    /// <summary>The bank's current deduplication map - identical 12x21
    /// content at different piece indices collapses to one canonical
    /// index, so the UI's Sprite # is dense and jump-free, and reused
    /// content (e.g. a shared "cleared" sprite) always shows the same
    /// number. Recomputed on demand rather than cached, since it depends
    /// on live pixel content that can change from any edit.</summary>
    private SpriteBank.DedupMap CurrentDedupMap() => _bankProvider().BuildDedupMap();

    private void UpdateListRow(int s)
    {
        if (s < 0 || s >= _list.Items.Count) return;
        int x = _canvas.SpriteX[s];
        int y = _canvas.SpriteY[s];
        var dedup = CurrentDedupMap();
        int raw = _spriteSource[_frame][s];
        int canonical = raw >= 0 && raw < dedup.SlotToCanonical.Length ? dedup.SlotToCanonical[raw] : 0;
        var item = _list.Items[s];
        item.SubItems[1].Text = canonical.ToString();
        item.SubItems[2].Text = x.ToString();
        item.SubItems[3].Text = y.ToString();
        item.SubItems[4].Text = x > 255 ? "1" : "0";
    }

    private void RecomputeD010Label()
    {
        int msb = 0;
        for (int s = 0; s < 8; s++)
            if (_canvas.SpriteX[s] > 255) msb |= 1 << s;
        _d010Label.Text = $"$d010 = %{Convert.ToString(msb, 2).PadLeft(8, '0')} (${msb:X2})";
    }

    private void RefreshList()
    {
        bool prev = _suppressEvents;
        _suppressEvents = true;
        try
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            var dedup = CurrentDedupMap();
            for (int s = 0; s < 8; s++)
            {
                int raw = _spriteSource[_frame][s];
                int canonical = raw >= 0 && raw < dedup.SlotToCanonical.Length ? dedup.SlotToCanonical[raw] : 0;
                var item = new ListViewItem(new[]
                {
                    s.ToString(), canonical.ToString(),
                    _canvas.SpriteX[s].ToString(), _canvas.SpriteY[s].ToString(), _canvas.SpriteX[s] > 255 ? "1" : "0"
                });
                if (_canvas.SelectedSprites.Contains(s)) item.Selected = true;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            RecomputeD010Label();
        }
        finally { _suppressEvents = prev; }
    }

    /// <summary>Called by MainForm after every pixel edit so the composited
    /// preview stays live while drawing - the two panels share the same
    /// SpriteBank instance, but this panel never repaints on its own just
    /// because the pixel editor's data changed.</summary>
    public void RefreshSpriteArt() => _canvas.Invalidate();

    /// <summary>Read-only accessors so MainForm's positioned edit view can
    /// mirror this panel's live placement without duplicating its state.</summary>
    public (int x, int y) GetPosition(int frame, int spriteIndex)
    {
        EnsureArraysAllocated();
        int f = Math.Max(0, Math.Min(frame, _animFrameCount - 1));
        return (_posX[f][spriteIndex], _posY[f][spriteIndex]);
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
            UpdateListRow(spriteIndex);
            _canvas.Invalidate();
            if (spriteIndex == _canvas.PrimarySelected)
            {
                UpdateSelectedEditors();
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
        if (_playTimer.Enabled) StopPlay();
        else { _playTimer.Interval = Math.Max(20, (int)(1000 / _fpsUpDown.Value)); _playTimer.Start(); _playButton.Text = "Stop"; }
    }

    private void StopPlay() { _playTimer.Stop(); _playButton.Text = "Play"; }

    // ---------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------
    // Deduplicated ASM export - actually shrinks the compiled output when
    // there's repeated content (e.g. a "cleared" sprite reused across many
    // frames/pieces): only the distinct 12x21 pieces are emitted once each,
    // plus small per-frame tables (pointer index, $d010, X, Y) that
    // Fire_Frame looks up at runtime instead of the fixed constants/simple
    // arithmetic it uses today, and fire_opt_pingpong (the Timeline's own
    // Ping-pong checkbox) telling it whether to bounce back and forth or
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
        sb.AppendLine("// Deduplicated fire sprite composition exported from Tool\\SpriteEditor's Construct panel.");
        sb.AppendLine($"// {dedup.CanonicalCount} distinct 12x21 piece(s) stored (the pool has {bank.PieceCount} raw");
        sb.AppendLine("// slots, but only pieces some (frame, hardware sprite) actually references are exported -");
        sb.AppendLine("// unreferenced pool pieces left over from editing history cost nothing here) - identical");
        sb.AppendLine("// art, such as a shared \"cleared\" sprite reused across frames, is written once and");
        sb.AppendLine("// referenced by index everywhere it's used.");
        sb.AppendLine("// Included from Startup\\Fire.s's m_fire_sprites_optimized/m_fire_code_optimized macros");
        sb.AppendLine("// when FIRE_COMPOSITION_TABLES is defined at build time - do not hand-edit, re-export instead.");
        sb.AppendLine();
        sb.AppendLine($"var fire_opt_sprite_count = {dedup.CanonicalCount}");
        sb.AppendLine($"var fire_opt_frame_count = {_animFrameCount}");
        sb.AppendLine($"var fire_opt_pingpong = {(_pingPongCheck.Checked ? 1 : 0)}");
        sb.AppendLine();

        for (int i = 0; i < dedup.CanonicalCount; i++)
        {
            int piece = dedup.CanonicalToSlot[i];
            bank.EmitSpriteAsmBlock(sb, $"fire_opt_sprite_{i}", piece);
        }
        sb.AppendLine();

        // $e900: just past Startup\Fadeout.s's Fadeout_Tables ($e000,
        // ~2258 bytes, ending ~$e8d2) - these per-frame tables are plain
        // CPU-read data (unlike the sprite pieces above, which must stay
        // in the $4000 VIC bank), so they're free to live anywhere; moving
        // them out of the $6880-$d000 budget leaves more of it for
        // Lightning_Tables. At 25 bytes/frame (fire_ptr_table 8 + fire_
        // d010_table 1 + fire_pos_x_lo 8 + fire_pos_y 8) even 31 frames
        // (the animframe*8 byte-fit ceiling) is under 800 bytes, well
        // inside the ~6KB free before $ffff.
        sb.AppendLine("        org $e900, \"Fire_Composition_Tables\"");
        sb.AppendLine();

        sb.AppendLine("fire_ptr_table:");
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

        sb.AppendLine("fire_d010_table:");
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

        sb.AppendLine("fire_pos_x_lo:");
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

        sb.AppendLine("fire_pos_y:");
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

    public void ExportOptimizedAsm()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "Export optimized (deduplicated) ASM composition",
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

/// <summary>
/// A plain (non owner-draw) ListView doesn't expose DoubleBuffered
/// publicly, so Clear()+rebuild - or even per-item SubItem.Text updates -
/// can flicker without it. Unlike an owner-draw ListBox (whose items paint
/// via the native WM_DRAWITEM message, a separate path DoubleBuffered
/// doesn't intercept - discovered the hard way elsewhere in this project),
/// a plain ListView's own rendering does go through the buffered WM_PAINT
/// path, so just enabling the property here is enough on its own.
/// </summary>
internal sealed class DoubleBufferedListView : ListView
{
    public DoubleBufferedListView() => DoubleBuffered = true;
}

/// <summary>
/// A plain TrackBar's click-on-track behaviour pages by LargeChange toward
/// the click point instead of jumping straight to it - fine for a long
/// scrollbar-style range, wrong for a short per-frame scrubber where a
/// deliberate click should land on that exact frame. Setting Value to the
/// click's target position ourselves, before the native control processes
/// WM_LBUTTONDOWN, makes it see the down-click as landing on the thumb
/// (which we just moved there) and go straight into its normal drag-track
/// mode instead of paging.
/// </summary>
internal sealed class ClickToPositionTrackBar : TrackBar
{
    private const int WM_LBUTTONDOWN = 0x0201;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_LBUTTONDOWN && Orientation == Orientation.Horizontal && Maximum > Minimum)
        {
            int x = unchecked((short)(m.LParam.ToInt32() & 0xFFFF));
            Value = ValueFromX(x);
        }
        base.WndProc(ref m);
    }

    private int ValueFromX(int x)
    {
        const int thumbHalf = 8; // approx half-width of the native track thumb
        int usable = Math.Max(1, Width - thumbHalf * 2);
        double ratio = (double)(x - thumbHalf) / usable;
        ratio = Math.Max(0, Math.Min(1, ratio));
        return Minimum + (int)Math.Round(ratio * (Maximum - Minimum));
    }
}
