using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MagSpriteEd;

public sealed class MainForm : Form
{
    // The flat canvas zooms to fill whatever space _canvasScroll actually
    // has (see FitCanvasToScrollArea) rather than a fixed pixel size, so it
    // never needs a horizontal scrollbar at a normal window size - these
    // just bound how far that auto-fit can zoom in a tiny or huge window.
    // A real C64 multicolour sprite pixel is twice as wide as it is tall
    // (it occupies 2 hires dot-widths but only 1 scanline), so the cell
    // width FitCanvasToScrollArea picks is always exactly double its
    // height, matching Construct's own 2x-wide pixel rectangles.
    private const int MinEditCellHeight = 10;
    private const int MaxEditCellHeight = 36;

    private SpriteBank _bank = CreateDefaultBank();

    // Which animation/Timeline frame is active (drives Construct's
    // positions) - decoupled from both which
    // SpriteBank piece Single Sprite View is editing (_editPiece) AND from
    // the bank's own PieceCount (the Timeline's length and the pool's size
    // are two entirely independent numbers - see ConstructPanel).
    private int _currentFrame;

    // Single Sprite View's edit target: one piece index into the bank's
    // flat pool. Set either by clicking a hardware sprite in Construct (see
    // SelectedSpriteSourceChanged below) or by clicking a thumbnail in the
    // pool strip to its left (see _spritePoolStrip.PieceClicked); defaults
    // to the pool's first piece before anything's been clicked.
    private int _editPiece;

    // The hardware sprite (Construct's PrimarySelectedSprite) _editPiece is
    // currently "owned by", for EnsureExclusiveEditTarget's copy-on-write
    // check - cached at selection time rather than re-queried live, because
    // it must be -1 (no owner - never auto-fork) when _editPiece was picked
    // directly from the pool strip instead of following Construct's
    // selection, even though Construct's own PrimarySelectedSprite may
    // still point at something else entirely at that moment.
    private int _editPieceOwnerSprite = -1;

    private enum Tool { Pencil, Fill, Line }
    private Tool _tool = Tool.Pencil;
    private byte _selectedColor = 1;
    private bool _mirror;

    private readonly List<(int piece, byte[,] snapshot, long seq)> _undo = new();
    private readonly List<(int piece, byte[,] snapshot, long seq)> _redo = new();
    private const int UndoCap = 100;

    // Line tool state (flat editor only - drawing in Construct simplifies
    // Line to paint-per-cell, see ConstructCanvas_CellInteract)
    private int _lineStartRow = -1, _lineStartCol = -1;
    private int _hoverRow = -1, _hoverCol = -1;

    private ToolStrip _toolbar = null!;
    private PixelGridControl _canvas = null!;
    private Panel _canvasScroll = null!;
    private SpritePoolStrip _spritePoolStrip = null!;
    private Panel _spritePoolScroll = null!;
    private DarkScrollBar _poolScrollBar = null!;
    private TableLayoutPanel _root = null!;
    // Gap around the Single Sprite View grid (was 20px on every side).
    private const int EditPad = 6;
    // Max share of the window width the Single Sprite View grid may take.
    private const float EditGridWidthShare = 0.24f;
    private const int PoolScrollBarWidth = 12;
    // Pieces already snapshotted for undo in the current Construct paint stroke.
    private readonly HashSet<int> _constructStrokePieces = new();

    private ConstructPanel _constructPanel = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripStatusLabel _fileLabel = null!;
    private DarkNumberBox _poolCountUpDown = null!;

    private ToolStripButton[] _swatchButtons = null!;
    private ToolStripButton _pencilBtn = null!, _fillBtn = null!, _lineBtn = null!, _mirrorBtn = null!;
    private ToolStripButton _hiresBtn = null!;
    private ToolStripButton _borderBtn = null!;
    // Guards _hiresBtn's CheckedChanged while SyncHiresButton programmatically
    // updates it to match whatever piece _editPiece just became - otherwise
    // that would immediately write the just-READ flag back via SetHires,
    // harmless but pointless, and would fight a real user click's own
    // pending Checked value in some reentrant edge cases.
    private bool _suppressHiresEvent;

    private byte[,]? _clipboard;

    private string? _lastPrgPath;
    private string? _lastSymPath;
    private string? _lastProjectPath;

    private BackdropPicture? _backdrop;

    private static readonly byte[] IndividualPaletteIndex = { 8, 10, 8, 10, 10, 8, 10, 8 };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(this);
    }

    public MainForm()
    {
        Text = "MagSpriteEd v1.0";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1300, 760);
        Size = new Size(1680, 900);
        BackColor = Color.FromArgb(18, 18, 18);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);
        KeyPreview = true; // app-wide shortcuts (Ctrl+Z/Y, 0-3 for colour) - see MainForm_KeyDown

        // AppIcon.ico is also set as <ApplicationIcon> in the .csproj (the
        // .exe file's own Win32 resource icon, seen in Explorer) - loading
        // it here too from the embedded copy sets the actual RUNNING
        // window/taskbar icon, which WinForms doesn't inherit from that
        // Win32 resource on its own.
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("MagSpriteEd.AppIcon.ico"))
        {
            if (iconStream != null) Icon = new Icon(iconStream);
        }

        BuildUi();
        WireEvents();
        KeyDown += MainForm_KeyDown;
        FitCanvasToScrollArea();
        FitPoolStripWidth();
        // Layout right after construction can under-report available space
        // (DPI scaling, the window not having done its first real layout
        // pass yet) - re-fit once more once the form has actually loaded.
        Load += (_, _) => { FitEditColumnWidth(); FitCanvasToScrollArea(); FitPoolStripWidth(); };
        SelectFrame(0);
        RefreshStatus("Ready - procedurally-generated bank loaded (matches the original demo's shipped output).");
    }

    private static SpriteBank CreateDefaultBank()
    {
        var bank = new SpriteBank(8);
        bank.GenerateProceduralAll();
        return bank;
    }

    // ---------------------------------------------------------------------
    // UI construction
    // ---------------------------------------------------------------------
    private void BuildUi()
    {
        BuildToolbar();

        var status = new StatusStrip { BackColor = Color.FromArgb(30, 30, 30) };
        _statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gainsboro };
        _fileLabel = new ToolStripStatusLabel { ForeColor = Color.DarkGray };
        status.Items.Add(_statusLabel);
        status.Items.Add(_fileLabel);

        // Left column is sized to exactly fit the pool strip + edit grid
        // (see FitEditColumnWidth); Construct takes all remaining width, so
        // no dead space is left between the three views.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = BackColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 600));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _root = root;
        root.SizeChanged += (_, _) => { if (_canvasScroll != null) FitEditColumnWidth(); };

        // ---- Left: edit canvas - Single Sprite View (one 12x21 piece,
        // highly zoomed in). Drawing in place over the backdrop happens in
        // Construct itself (see ConstructCanvas_CellInteract). ----
        _canvas = new PixelGridControl(SpriteBank.QuadRows, SpriteBank.QuadCols, MaxEditCellHeight * 2, MaxEditCellHeight)
        {
            // Hires "on" is remapped to value 2 (Individual) purely for
            // colour lookup - PaletteColor/EditorPalette have no separate
            // concept of a hires colour, since a hires sprite's one colour
            // IS that same per-sprite Individual register on real hardware.
            PixelProvider = (r, c) => _bank.IsHires(_editPiece)
                ? (_bank.GetHiresPixel(_editPiece, r, c) != 0 ? (byte)2 : (byte)0)
                : _bank.Get(_editPiece, r, c),
            PaletteProvider = v => EditorPalette.ColorFor(v, _bank.IndividualColor(_editPiece)),
            BackgroundProvider = () => EditorPalette.BackgroundColor
        };
        _canvasScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(12, 12, 12), Padding = new Padding(EditPad), Margin = new Padding(0) };
        _canvasScroll.Controls.Add(_canvas);
        _canvas.Location = new Point(20, 20);
        // Re-fit whenever the host area's size actually changes (window
        // resize etc.) - keeps the canvas at the largest zoom that still
        // needs no horizontal scrollbar.
        _canvasScroll.SizeChanged += (_, _) => { FitEditColumnWidth(); FitCanvasToScrollArea(); };

        // ---- Far left: vertical strip of every pool piece as a small
        // thumbnail. Click one to make it Single Sprite View's edit target;
        // thumbnails update live while drawing via RefreshAll. ----
        _spritePoolStrip = new SpritePoolStrip
        {
            PixelProvider = (piece, r, c) => _bank.Get(piece, r, c),
            PaletteProvider = (piece, v) => EditorPalette.ColorFor(v, _bank.IndividualColor(piece)),
            IsHiresProvider = piece => _bank.IsHires(piece)
        };
        _spritePoolStrip.PieceClicked += piece =>
        {
            _editPiece = piece;
            _editPieceOwnerSprite = -1; // no known Construct owner - never auto-fork this selection
            _spritePoolStrip.SetSelected(piece);
            RefreshAll();
        };
        // Clipping viewport for the strip - no AutoScroll (whose native
        // scrollbar is light-themed and sat right up against the edit
        // canvas): _poolScrollBar, in its own column at the far left,
        // scrolls it by moving the strip's Top (see UpdatePoolScroll).
        _spritePoolScroll = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(12, 12, 12),
            Margin = new Padding(0)
        };
        _spritePoolScroll.Controls.Add(_spritePoolStrip);
        _spritePoolScroll.ClientSizeChanged += (_, _) => { FitPoolStripWidth(); UpdatePoolScroll(); };
        _spritePoolStrip.SizeChanged += (_, _) => UpdatePoolScroll();
        _spritePoolStrip.MouseWheel += (_, e) => _poolScrollBar.Wheel(e.Delta);
        _spritePoolScroll.MouseWheel += (_, e) => _poolScrollBar.Wheel(e.Delta);

        _poolScrollBar = new DarkScrollBar { Dock = DockStyle.Fill, Margin = new Padding(0) };
        _poolScrollBar.ValueChanged += () => _spritePoolStrip.Top = -_poolScrollBar.Value;

        // Pool size lives at the foot of the strip it controls, instead of
        // on the main toolbar. No Apply button - it applies on change.
        var poolFooter = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.FromArgb(22, 22, 22),
            Padding = new Padding(4, 4, 2, 4),
            Margin = new Padding(0)
        };
        poolFooter.Controls.Add(new Label { Text = "Pool", AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(2, 7, 2, 0) });
        _poolCountUpDown = new DarkNumberBox { Minimum = 1, Maximum = 512, Value = _bank.PieceCount, Width = 52, TextAlign = HorizontalAlignment.Center, Margin = new Padding(2, 3, 2, 2) };
        // Applies as soon as the value changes (Enter, focus loss or a
        // spinner click - not per keystroke, so typing "16" never passes
        // through a 1-piece pool).
        _poolCountUpDown.ValueChanged += (_, _) => ApplyPoolSize();
        poolFooter.Controls.Add(_poolCountUpDown);

        // [scrollbar | thumbnails] over [Pool footer] - the scrollbar is the
        // far-left edge of the window, not wedged between strip and canvas.
        var poolColumn = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.FromArgb(12, 12, 12)
        };
        poolColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, PoolScrollBarWidth));
        poolColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        poolColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        poolColumn.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        poolColumn.Controls.Add(_poolScrollBar, 0, 0);
        poolColumn.Controls.Add(_spritePoolScroll, 1, 0);
        poolColumn.Controls.Add(poolFooter, 0, 1);
        poolColumn.SetColumnSpan(poolFooter, 2);

        var editArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = BackColor
        };
        editArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, PoolScrollBarWidth + SpritePoolStrip.PreferredWidth));
        editArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editArea.Controls.Add(poolColumn, 0, 0);
        editArea.Controls.Add(_canvasScroll, 1, 0);

        // ---- Right: embedded Construct panel (placement/composition over
        // the backdrop) - also the sole frame navigator now; the FRAMES
        // thumbnail strip was removed in favour of its Timeline. Loading a
        // backdrop is only ever done via this Menu's own item now (see
        // BuildToolbar) - ConstructPanel no longer has its own button. ----
        _constructPanel = new ConstructPanel(() => _bank, () => _backdrop);
        // Drawing directly on a sprite in Construct (plain left/right drag).
        _constructPanel.SpriteCellPainted += ConstructCanvas_CellInteract;
        _constructPanel.PaintStrokeStarted += () => _constructStrokePieces.Clear();
        _constructPanel.PaintStrokeEnded += () => _constructStrokePieces.Clear();
        // A new placement edit makes any pixel redo stale - keeps the shared history linear.
        _constructPanel.PositionEdited += () => _redo.Clear();
        // Fires on every Timeline step, including 17+ times a second during
        // playback - so only the status line (frame count) is refreshed here.
        // Construct repaints itself, and if the step changes which piece the
        // selected sprite shows, SelectedSpriteSourceChanged below handles it.
        _constructPanel.FrameChanged += frame =>
        {
            _currentFrame = Math.Max(0, Math.Min(frame, _constructPanel.AnimFrameCount - 1));
            RefreshStatus(null);
        };
        // Clicking a hardware sprite in Construct is how you pick what
        // Single Sprite View edits - it follows whichever sprite is
        // primary-selected there (and re-resolves if its Sprite # or the
        // active Timeline frame changes which piece that points at).
        _constructPanel.SelectedSpriteSourceChanged += () =>
        {
            var target = _constructPanel.GetSelectedSpriteTarget();
            if (target is { } piece)
            {
                // Raised on every frame change too - skip the full refresh
                // when the edit target hasn't actually moved.
                if (piece == _editPiece && _editPieceOwnerSprite == _constructPanel.PrimarySelectedSprite) return;
                _editPiece = piece;
                _editPieceOwnerSprite = _constructPanel.PrimarySelectedSprite;
                _spritePoolStrip.SetSelected(piece);
                ScrollPoolStripToSelection();
                RefreshAll();
            }
        };

        _constructPanel.Margin = new Padding(0);
        root.Controls.Add(editArea, 0, 0);
        root.Controls.Add(_constructPanel, 1, 0);

        Controls.Add(root);
        Controls.Add(status);
        Controls.Add(_toolbar);

        _spritePoolStrip.SetPieceCount(_bank.PieceCount);
        _spritePoolStrip.SetSelected(_editPiece);

        // Every layout container, including Construct's, buffered - smooth
        // window resizes and scrolling instead of erase-then-draw flicker.
        DoubleBuffering.EnableForContainers(this);
    }

    /// <summary>Scrolls the pool strip's host panel just enough to bring the
    /// currently-selected piece's thumbnail fully into view - used whenever
    /// selection changes via Construct rather than a direct click in the
    /// strip itself (a direct click is already visible, since that's what
    /// was just clicked).</summary>
    private void ScrollPoolStripToSelection()
    {
        if (_editPiece < 0 || _editPiece >= _spritePoolStrip.PieceCount) return;
        int top = _spritePoolStrip.PieceTop(_editPiece);
        int bottom = _spritePoolStrip.PieceBottom(_editPiece);
        int viewTop = _poolScrollBar.Value;
        int viewHeight = _spritePoolScroll.ClientSize.Height;
        if (top < viewTop) _poolScrollBar.Value = top;
        else if (bottom > viewTop + viewHeight) _poolScrollBar.Value = bottom - viewHeight;
    }

    /// <summary>Re-syncs the pool scrollbar's range with the strip's height
    /// and the viewport's - called whenever either changes.</summary>
    private void UpdatePoolScroll()
    {
        _poolScrollBar.LargeChange = Math.Max(1, _spritePoolScroll.ClientSize.Height);
        _poolScrollBar.Maximum = _spritePoolStrip.Height;
        _spritePoolStrip.Top = -_poolScrollBar.Value;
    }

    private void ApplyPoolSize()
    {
        if ((int)_poolCountUpDown.Value == _bank.PieceCount) return;
        if (!TryApplyPoolSize((int)_poolCountUpDown.Value)) { _poolCountUpDown.Value = _bank.PieceCount; return; }
        _editPiece = Math.Min(_editPiece, _bank.PieceCount - 1);
        _spritePoolStrip.SetPieceCount(_bank.PieceCount);
        _constructPanel.RefreshAfterPoolChange();
        RefreshAll();
        RefreshStatus($"Sprite pool size set to {_bank.PieceCount}.");
    }

    // ---------------------------------------------------------------------
    // Icon toolbar - replaces the old "File" text menu and the right-edge
    // tool panel (Palette/Tool/Frame/Frame Count/History groups) with a
    // single row of icon buttons. Icons are drawn programmatically (see
    // Icons.cs) rather than loaded from files.
    // ---------------------------------------------------------------------
    /// <summary>Runs a dialog-showing action on the next message-loop tick
    /// instead of synchronously inside a menu item's own Click handler.
    /// Clicking a dropdown menu item that opens a modal dialog leaves a
    /// stray mouse-up in the message queue from the SAME click that opened
    /// the dropdown; once the dialog closes, Windows can deliver that
    /// leftover message to whatever control is now under the cursor -
    /// which, since the Menu dropdown extends down over the canvas, is the
    /// pixel editor, so dismissing a load/export dialog could paint one
    /// stray pixel right where the menu item was (only noticeable in
    /// Single Sprite View, since that's the one that draws on a plain
    /// click). Deferring past this click's own message lets the queue
    /// settle first.</summary>
    private void DeferDialogAction(Action action) => BeginInvoke(action);

    private void ShowAbout()
    {
        using var dlg = new AboutDialog();
        dlg.ShowDialog(this);
    }

    private void BuildToolbar()
    {
        _toolbar = new ToolStrip
        {
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.Gainsboro,
            Renderer = new DarkMenuRenderer(),
            GripStyle = ToolStripGripStyle.Hidden,
            ImageScalingSize = new Size(Icons.Size, Icons.Size),
            Dock = DockStyle.Top
        };

        // -- Menu (was the "File" text menu - now a hamburger icon) --
        var menuBtn = new ToolStripDropDownButton
        {
            Image = Icons.Menu(),
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = "Menu"
        };
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("About MagSpriteEd...", Icons.Info(), (_, _) => DeferDialogAction(ShowAbout)));
        menuBtn.DropDownItems.Add(new ToolStripSeparator());
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("New Blank Bank", Icons.New(), (_, _) => NewBlankBank()));
        menuBtn.DropDownItems.Add(new ToolStripSeparator());
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Load Sprite Bank (.prg + .sym / .png)...", Icons.FolderOpen(), (_, _) => DeferDialogAction(LoadSpriteBank)));
        menuBtn.DropDownItems.Add(new ToolStripSeparator());
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Load Project...", Icons.FolderOpen(), (_, _) => DeferDialogAction(LoadProject)));
        // Ctrl+S / Ctrl+Shift+S are handled in MainForm_KeyDown; the
        // display strings just show them in the menu.
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Save Project", Icons.Save(), (_, _) => SaveProjectQuick()) { ShortcutKeyDisplayString = "Ctrl+S" });
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Save Project As...", Icons.Save(), (_, _) => DeferDialogAction(SaveProject)) { ShortcutKeyDisplayString = "Ctrl+Shift+S" });
        menuBtn.DropDownItems.Add(new ToolStripSeparator());
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Export Spritebank...", Icons.Export(), (_, _) => DeferDialogAction(ExportSpritebank)));
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Export Animation...", Icons.Export(), (_, _) => DeferDialogAction(_constructPanel.ExportAnimation)));
        menuBtn.DropDownItems.Add(new ToolStripSeparator());
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Load Backdrop Picture (.kla)...", Icons.Picture(), (_, _) => DeferDialogAction(LoadBackdropPicture)));
        var gridItem = new ToolStripMenuItem("Show Grid in Construct") { CheckOnClick = true, Checked = true };
        gridItem.CheckedChanged += (_, _) => _constructPanel.ShowGrid = gridItem.Checked;
        menuBtn.DropDownItems.Add(gridItem);
        menuBtn.DropDownItems.Add(new ToolStripSeparator());
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (_, _) => Close()));
        // Dropdown items don't inherit colours from the owning button/ToolStrip -
        // see DarkMenuRenderer's header for why this is needed at all.
        foreach (ToolStripItem item in menuBtn.DropDownItems)
            item.ForeColor = Color.Gainsboro;
        _toolbar.Items.Add(menuBtn);

        _toolbar.Items.Add(new ToolStripSeparator());

        // -- Palette: kept closest to the menu button, per request. Right-
        // click any swatch to repoint that slot at any of the 16 real C64
        // colours via EditorPalette. _swatchButtons stays indexed by the
        // underlying colour VALUE (0=Background,1=MC1,2=Individual,3=MC2 -
        // used everywhere else, e.g. SetColor/UpdateSwatchIcons), while
        // visualOrder controls the left-to-right layout independently - MC2
        // shown before Individual, swapped from the value order, per request.
        // Background is $d021: sprite pixel value 0 is transparent and shows
        // it through, so painting "Background" is how you erase.
        string[] names =
        {
            "Background $d021 (shortcut 0) - right-click to change colour",
            "MC1 $d025 (shortcut 1) - right-click to change colour",
            "Individual $d027+ (shortcut 3) - right-click to change this sprite's colour",
            "MC2 $d026 (shortcut 2) - right-click to change colour"
        };
        _swatchButtons = new ToolStripButton[4];
        for (byte v = 0; v < 4; v++)
        {
            byte idx = v;
            var btn = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ToolTipText = names[v],
                Checked = v == _selectedColor
            };
            btn.Click += (_, _) => SetColor(idx);
            btn.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowPalettePopup(idx, btn); };
            _swatchButtons[v] = btn;
        }

        // -- $d020 border: not a drawing colour, so any click opens the palette. --
        _borderBtn = new ToolStripButton
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = "Border $d020 - click to change colour"
        };
        _borderBtn.MouseUp += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
                ShowColorPopup(_borderBtn, EditorPalette.SetBorder);
        };
        _toolbar.Items.Add(_borderBtn); // left of Background

        byte[] visualOrder = { 0, 1, 3, 2 }; // Background, MC1, MC2, Individual
        foreach (var v in visualOrder) _toolbar.Items.Add(_swatchButtons[v]);

        EditorPalette.Changed += () =>
        {
            UpdateSwatchIcons();
            _canvas.Invalidate();
            _spritePoolStrip.Invalidate();
            _constructPanel.RefreshVicColors();
        };
        UpdateSwatchIcons();

        // -- Multicolour/hires toggle for the piece Single Sprite View is
        // currently editing - see SpriteBank.IsHires. CheckOnClick's own
        // CheckedChanged fires AFTER Checked is already updated, so the
        // handler just reads it straight off the button. ---
        _hiresBtn = new ToolStripButton
        {
            Image = Icons.Multicolor(),
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            CheckOnClick = true,
            ToolTipText = "Multicolour sprite (click to switch this sprite to hires)"
        };
        _hiresBtn.CheckedChanged += (_, _) =>
        {
            _hiresBtn.Image = _hiresBtn.Checked ? Icons.Hires() : Icons.Multicolor();
            _hiresBtn.ToolTipText = _hiresBtn.Checked
                ? "Hires sprite - single colour, double horizontal resolution (click to switch this sprite to multicolour)"
                : "Multicolour sprite (click to switch this sprite to hires)";
            if (_suppressHiresEvent) return;
            _bank.SetHires(_editPiece, _hiresBtn.Checked);
            FitCanvasToScrollArea();
            RefreshAll();
        };
        _toolbar.Items.Add(_hiresBtn);

        _toolbar.Items.Add(new ToolStripSeparator());

        // -- Tool selection --
        _pencilBtn = MakeToolButton(Icons.Pencil(), "Pencil (left = paint, right = erase)", () => SetTool(Tool.Pencil));
        _fillBtn = MakeToolButton(Icons.Bucket(), "Flood fill (left = paint, right = erase)", () => SetTool(Tool.Fill));
        _lineBtn = MakeToolButton(Icons.Line(), "Line (left = paint, right = erase)", () => SetTool(Tool.Line));
        _pencilBtn.Checked = true;
        _mirrorBtn = new ToolStripButton { Image = Icons.Mirror(), DisplayStyle = ToolStripItemDisplayStyle.Image, ToolTipText = "Mirror left/right while painting", CheckOnClick = true };
        _mirrorBtn.CheckedChanged += (_, _) => _mirror = _mirrorBtn.Checked;
        _toolbar.Items.Add(_pencilBtn);
        _toolbar.Items.Add(_fillBtn);
        _toolbar.Items.Add(_lineBtn);
        _toolbar.Items.Add(_mirrorBtn);


        _toolbar.Items.Add(new ToolStripSeparator());

        // -- Frame operations --
        AddToolbarButton(Icons.Clear(), "Clear this sprite", (_, _) => { EnsureExclusiveEditTarget(); PushUndo(); ClearPiece(); RefreshAll(); });
        AddToolbarButton(Icons.FlipH(), "Flip this sprite horizontal", (_, _) => { EnsureExclusiveEditTarget(); PushUndo(); FlipHorizontalPiece(); RefreshAll(); });
        AddToolbarButton(Icons.FlipV(), "Flip this sprite vertical", (_, _) => { EnsureExclusiveEditTarget(); PushUndo(); FlipVerticalPiece(); RefreshAll(); });
        AddToolbarButton(Icons.Copy(), "Copy this sprite", (_, _) =>
        {
            var grid = _bank.Piece(_editPiece);
            var clip = new byte[SpriteBank.QuadRows, SpriteBank.QuadCols];
            Array.Copy(grid, clip, grid.Length);
            _clipboard = clip;
        });
        AddToolbarButton(Icons.Paste(), "Paste sprite (into whichever sprite is currently being edited)", (_, _) =>
        {
            if (_clipboard == null) return;
            EnsureExclusiveEditTarget();
            PushUndo();
            var grid = _bank.Piece(_editPiece);
            Array.Copy(_clipboard, grid, _clipboard.Length);
            RefreshAll();
        });
        _toolbar.Items.Add(new ToolStripSeparator());

        // -- Undo / redo (pixel art) --
        AddToolbarButton(Icons.Undo(), "Undo last change - drawing or sprite placement (Ctrl+Z)", (_, _) => UndoLatest());
        AddToolbarButton(Icons.Redo(), "Redo (Ctrl+Y)", (_, _) => RedoLatest());

        _toolbar.Items.Add(new ToolStripSeparator());

        // -- Frame steps. The "Frames" count field + Apply live in
        // Construct's own Timeline row, next to the frame strip. --
        AddToolbarButton(Icons.FrameInsert(), "Add 1 new frame step at the current Timeline position (duplicate of it)", (_, _) => AddFrameStepAtCurrent());
        AddToolbarButton(Icons.FrameDelete(), "Delete the current animation frame step", (_, _) => DeleteCurrentFrameStep());
        AddToolbarButton(Icons.Duplicate(), "Copy this animation frame's sprites data (positions + Sprite #s) to Clipboard", (_, _) => _constructPanel.CopyCurrentFrameToClipboard());
        AddToolbarButton(Icons.Paste(), "Paste sprites data from Clipboard into the current animation frame", (_, _) =>
        {
            _constructPanel.PasteFrameFromClipboard();
            RefreshAll();
        });

        _toolbar.Items.Add(new ToolStripSeparator());

        // -- Open border: preview the VIC open-border trick, so sprites in
        // the $d020 border area are shown instead of hidden by it. --
        var openBorderBtn = new ToolStripButton
        {
            Image = Icons.OpenBorder(),
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = "Open border - show sprites placed in the $d020 border area (VIC open-border trick)",
            CheckOnClick = true
        };
        openBorderBtn.CheckedChanged += (_, _) => _constructPanel.OpenBorder = openBorderBtn.Checked;
        _toolbar.Items.Add(openBorderBtn);

        // -- Glue: snap the selected sprite(s) flush against the nearest other
        // sprite. In Construct, Alt+click on a sprite does the same. --
        AddToolbarButton(Icons.Glue(), "Glue selected sprite(s) to the nearest sprite (or Alt+click a sprite in Construct)", (_, _) => GlueSelection());

        // -- Sprite outline visibility (far right of the toolbar) in Construct's composited preview -
        // moved here from the removed "Backdrop" panel's own checkbox. --
        var outlinesBtn = new ToolStripButton
        {
            Image = Icons.Outline(),
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = "Show/Hide Outlines",
            CheckOnClick = true,
            Checked = true,
            // Pinned to the toolbar's right edge, apart from the other icons.
            Alignment = ToolStripItemAlignment.Right
        };
        outlinesBtn.CheckedChanged += (_, _) => _constructPanel.ShowSpriteOutlines = outlinesBtn.Checked;
        _toolbar.Items.Add(outlinesBtn);
    }

    /// <summary>Inserts a new animation frame right at the current Timeline
    /// position - starting as a duplicate of whatever was there, so nothing
    /// visibly changes until it's actually edited - and shifts every later
    /// frame one step along. Never touches the sprite pool at all: the
    /// Timeline's length and the pool's size are fully independent.</summary>
    private void AddFrameStepAtCurrent()
    {
        int insertAt = _currentFrame;
        _constructPanel.InsertFrameCopy(insertAt);
        _currentFrame = insertAt;
        RefreshAll();
        RefreshStatus($"Inserted a new frame step at position {insertAt + 1} (duplicate of the previous content) - {_constructPanel.AnimFrameCount} frame(s) total.");
    }

    /// <summary>Removes the current animation frame step, shifting every
    /// later frame one step earlier. Never touches the sprite pool - a
    /// piece this frame referenced simply becomes unreferenced (still
    /// there for reuse), so there's no risk of losing art by deleting a
    /// frame any more.</summary>
    private void DeleteCurrentFrameStep()
    {
        if (_constructPanel.AnimFrameCount <= 1)
        {
            MessageBox.Show(this, "Can't delete the only remaining frame.", "Delete frame", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        int deleteAt = _currentFrame;
        _constructPanel.RemoveFrame(deleteAt);
        _currentFrame = Math.Min(_currentFrame, _constructPanel.AnimFrameCount - 1);
        SelectFrame(_currentFrame);
        RefreshAll();
        RefreshStatus($"Deleted frame step {deleteAt + 1} - {_constructPanel.AnimFrameCount} frame(s) remain.");
    }

    private ToolStripButton MakeToolButton(Image icon, string tooltip, Action onClick)
    {
        var btn = new ToolStripButton { Image = icon, DisplayStyle = ToolStripItemDisplayStyle.Image, ToolTipText = tooltip };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void AddToolbarButton(Image icon, string tooltip, EventHandler onClick)
    {
        var btn = new ToolStripButton { Image = icon, DisplayStyle = ToolStripItemDisplayStyle.Image, ToolTipText = tooltip };
        btn.Click += onClick;
        _toolbar.Items.Add(btn);
    }

    private void SetTool(Tool t)
    {
        _tool = t;
        _pencilBtn.Checked = t == Tool.Pencil;
        _fillBtn.Checked = t == Tool.Fill;
        _lineBtn.Checked = t == Tool.Line;
        RefreshStatus(null);
    }

    private void SetColor(byte c)
    {
        _selectedColor = c;
        for (int i = 0; i < _swatchButtons.Length; i++) _swatchButtons[i].Checked = i == c;
        RefreshStatus(null);
    }

    // Colours the swatch icons were last built for - RefreshAll runs often,
    // and rebuilding 5 bitmaps (plus a toolbar relayout/repaint) every time
    // for unchanged colours was pure waste.
    private readonly int[] _swatchIconColors = { -1, -1, -1, -1, -1, -1 };

    private void UpdateSwatchIcons()
    {
        Color[] colors = { EditorPalette.BackgroundColor, PaletteColor(1), PaletteColor(2), PaletteColor(3) };
        for (int i = 0; i < 4; i++)
        {
            int argb = colors[i].ToArgb();
            if (_swatchIconColors[i] == argb) continue;
            _swatchIconColors[i] = argb;
            var old = _swatchButtons[i].Image;
            _swatchButtons[i].Image = Icons.Swatch(colors[i]);
            old?.Dispose();
        }
        int border = EditorPalette.BorderColor.ToArgb(), background = EditorPalette.BackgroundColor.ToArgb();
        if (_swatchIconColors[4] != border || _swatchIconColors[5] != background)
        {
            _swatchIconColors[4] = border;
            _swatchIconColors[5] = background;
            var old = _borderBtn.Image;
            _borderBtn.Image = Icons.BorderSwatch(EditorPalette.BorderColor, EditorPalette.BackgroundColor);
            old?.Dispose();
        }
    }

    private void ShowPalettePopup(byte slot, ToolStripButton anchor)
    {
        ShowColorPopup(anchor, c64Index =>
        {
            if (slot == 2)
            {
                // Individual is per sprite: only the piece being edited changes.
                _bank.SetIndividualColor(_editPiece, c64Index);
                EditorPalette.RaiseChanged();
            }
            else EditorPalette.Set(slot, c64Index);
        });
    }

    private void ShowColorPopup(ToolStripItem anchor, Action<int> picked)
    {
        var popup = new Palette16Popup();
        var screenPos = _toolbar.PointToScreen(anchor.Bounds.Location);
        popup.Location = new Point(screenPos.X, screenPos.Y + anchor.Bounds.Height + 2);
        popup.ColorPicked += picked;
        popup.Show(this);
    }

    /// <summary>Picks the largest cell size (bounded by Min/MaxEditCellHeight)
    /// that still fits the whole grid into _canvasScroll's current client
    /// area without needing a horizontal scrollbar, and reconfigures the
    /// canvas for whichever of multicolour (12 double-width columns) or
    /// hires (24 square columns) _editPiece currently is - both cover the
    /// exact same total physical width (24 hires-dot-widths either way), so
    /// the same "unit" size drives both column counts. Called on resize and
    /// whenever _editPiece or its hires flag changes (see RefreshAll/
    /// SyncHiresButton).</summary>
    /// <summary>Sizes the left column to exactly the pool strip plus the
    /// edit grid, so the Construct column gets every remaining pixel. The
    /// grid is capped at EditGridWidthShare of the window width - most
    /// drawing happens in Construct, so it gets the bigger area.</summary>
    private void FitEditColumnWidth()
    {
        int availH = _canvasScroll.ClientSize.Height - _canvasScroll.Padding.Vertical;
        if (availH <= 0 || _root.ClientSize.Width <= 0) return;
        int byHeight = availH / SpriteBank.QuadRows;
        int byWidth = (int)(_root.ClientSize.Width * EditGridWidthShare) / SpriteBank.HiresCols;
        int unit = Math.Clamp(Math.Min(byHeight, byWidth), MinEditCellHeight, MaxEditCellHeight);
        int gridW = unit * SpriteBank.HiresCols + 1;
        int want = PoolScrollBarWidth + SpritePoolStrip.PreferredWidth + gridW + _canvasScroll.Padding.Horizontal;
        if (unit * SpriteBank.QuadRows + 1 > availH) want += SystemInformation.VerticalScrollBarWidth;
        want = Math.Max(200, Math.Min(want, _root.ClientSize.Width - 400));
        if (Math.Abs(_root.ColumnStyles[0].Width - want) > 0.5f) _root.ColumnStyles[0].Width = want;
    }

    private void FitCanvasToScrollArea()
    {
        int availW = _canvasScroll.ClientSize.Width - _canvasScroll.Padding.Horizontal;
        int availH = _canvasScroll.ClientSize.Height - _canvasScroll.Padding.Vertical;
        if (availW <= 0 || availH <= 0) return;
        int byWidth = availW / SpriteBank.HiresCols;
        int byHeight = availH / SpriteBank.QuadRows;
        int unit = Math.Clamp(Math.Min(byWidth, byHeight), MinEditCellHeight, MaxEditCellHeight);
        if (_bank.IsHires(_editPiece)) _canvas.Reconfigure(SpriteBank.HiresCols, unit, unit);
        else _canvas.Reconfigure(SpriteBank.QuadCols, unit * 2, unit);
        _canvas.Location = new Point(_canvasScroll.Padding.Left, _canvasScroll.Padding.Top);
    }

    /// <summary>Keeps the pool strip's Width matching its viewport's.</summary>
    private void FitPoolStripWidth()
    {
        int availW = _spritePoolScroll.ClientSize.Width;
        if (availW <= 0) return;
        _spritePoolStrip.Width = availW;
    }

    /// <summary>Reflects _editPiece's current hires flag onto the toolbar
    /// toggle without re-triggering its own CheckedChanged side effects
    /// (which would just write the same flag back and re-fit again -
    /// harmless, but pointless work on every single pixel edit since
    /// RefreshAll calls this unconditionally).</summary>
    private void SyncHiresButton()
    {
        bool hires = _bank.IsHires(_editPiece);
        if (_hiresBtn.Checked == hires) return;
        _suppressHiresEvent = true;
        try { _hiresBtn.Checked = hires; }
        finally { _suppressHiresEvent = false; }
        _hiresBtn.Image = hires ? Icons.Hires() : Icons.Multicolor();
        _hiresBtn.ToolTipText = hires
            ? "Hires sprite - single colour, double horizontal resolution (click to switch this sprite to multicolour)"
            : "Multicolour sprite (click to switch this sprite to hires)";
    }

    private void WireEvents()
    {
        _canvas.CellInteract += Canvas_CellInteract;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseUp += Canvas_MouseUp;
    }

    // ---------------------------------------------------------------------
    // App-wide keyboard shortcuts. KeyPreview routes every KeyDown through
    // here FIRST regardless of which control has focus; setting e.Handled
    // stops the focused control from seeing it too.
    // ---------------------------------------------------------------------
    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        bool undoKey = e.Control && e.KeyCode == Keys.Z && !e.Shift;
        bool redoKey = e.Control && (e.KeyCode == Keys.Y || (e.KeyCode == Keys.Z && e.Shift));
        // Typing in a number field (X/Y, Frames, Pool...) keeps its own text undo.
        if ((undoKey || redoKey) && !IsTextEntryFocused())
        {
            if (undoKey) UndoLatest(); else RedoLatest();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Ctrl+S: save to the current project file (asks the first time);
        // Ctrl+Shift+S: always ask where to save.
        if (e.Control && !e.Alt && e.KeyCode == Keys.S)
        {
            if (e.Shift) SaveProject(); else SaveProjectQuick();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Colour shortcuts (0=Background,1=MC1,2=MC2,3=Individual - matching
        // the toolbar's left-to-right order, not the underlying byte value)
        // only when not typing into a text field (X/Y/Sprite#/Frame count/etc.).
        if (!e.Control && !e.Alt && IsColorShortcutKey(e.KeyCode, out byte color) && !IsTextEntryFocused())
        {
            SetColor(color);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void GlueSelection() => RefreshStatus(_constructPanel.GlueSelection());

    private static bool IsColorShortcutKey(Keys key, out byte color)
    {
        // Toolbar order is Background, MC1, MC2, Individual; underlying
        // byte values are Background=0, MC1=1, Individual=2, MC2=3.
        switch (key)
        {
            case Keys.D0: case Keys.NumPad0: color = 0; return true; // Background
            case Keys.D1: case Keys.NumPad1: color = 1; return true; // MC1
            case Keys.D2: case Keys.NumPad2: color = 3; return true; // MC2
            case Keys.D3: case Keys.NumPad3: color = 2; return true; // Individual
            default: color = 0; return false;
        }
    }

    private static Control GetDeepestActiveControl(Control root)
    {
        Control current = root;
        while (current is ContainerControl cc && cc.ActiveControl != null && cc.ActiveControl != current)
            current = cc.ActiveControl;
        return current;
    }

    private bool IsTextEntryFocused()
    {
        var c = GetDeepestActiveControl(this);
        return c is NumericUpDown or DarkNumberBox or TextBoxBase or ComboBox;
    }

    // ---------------------------------------------------------------------
    // Flat canvas interaction (Single Sprite View) - row/col are directly
    // within the one 12x21 piece being edited (_editPiece); no offset math
    // needed any more, a piece IS the whole editing canvas.
    // ---------------------------------------------------------------------
    private void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
        EnsureExclusiveEditTarget();
        PushUndo();
        RefreshAll(); // once per stroke: picks up any copy-on-write fork
        int col = e.X / _canvas.CellWidth;
        int row = e.Y / _canvas.CellHeight;
        _lineStartRow = row;
        _lineStartCol = col;
        _hoverRow = row;
        _hoverCol = col;
    }

    private void Canvas_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_tool == Tool.Line && _lineStartRow >= 0)
        {
            byte color = (e.Button == MouseButtons.Right) ? (byte)0 : _selectedColor;
            DrawLine(_lineStartRow, _lineStartCol, _hoverRow, _hoverCol, color);
            RefreshAll();
        }
        _lineStartRow = -1;
        _lineStartCol = -1;
    }

    /// <summary>Writes one pixel through whichever of SpriteBank's two
    /// accessors matches the piece's current mode - multicolour's Set
    /// takes the palette value (0-3) directly, hires's SetHiresPixel just
    /// wants "on or off" (any non-transparent palette value counts as on),
    /// since a hires sprite only ever has the one colour to begin with.</summary>
    private void SetPixel(int piece, bool hires, int row, int col, byte paletteColor)
    {
        if (hires) _bank.SetHiresPixel(piece, row, col, paletteColor != 0 ? (byte)1 : (byte)0);
        else _bank.Set(piece, row, col, paletteColor);
    }

    private void Canvas_CellInteract(int row, int col, MouseButtons button)
    {
        if (button != MouseButtons.Left && button != MouseButtons.Right) return;
        _hoverRow = row;
        _hoverCol = col;
        bool hires = _bank.IsHires(_editPiece);

        switch (_tool)
        {
            case Tool.Pencil:
                {
                    byte color = (button == MouseButtons.Right) ? (byte)0 : _selectedColor;
                    SetPixel(_editPiece, hires, row, col, color);
                    if (_mirror)
                    {
                        int maxCol = hires ? SpriteBank.HiresCols : SpriteBank.QuadCols;
                        SetPixel(_editPiece, hires, row, maxCol - 1 - col, color);
                    }
                    RefreshAfterPixelEdit(_editPiece, row, col);
                    break;
                }
            case Tool.Fill:
                {
                    byte color = (button == MouseButtons.Right) ? (byte)0 : _selectedColor;
                    FloodFill(_editPiece, hires, row, col, color);
                    RefreshAll();
                    break;
                }
            case Tool.Line:
                // handled on mouse-up so the whole drag defines one straight segment
                break;
        }
    }

    // ---------------------------------------------------------------------
    // Drawing directly in Construct - same tools as Single Sprite View, but
    // the piece to edit is resolved from wherever the click landed rather
    // than read off _editPiece, since each of the 8 sprites can show a
    // different piece than whatever Single Sprite View has selected.
    // ---------------------------------------------------------------------
    private void ConstructCanvas_CellInteract(int spriteIndex, int row, int col, MouseButtons button)
    {
        if (button != MouseButtons.Left && button != MouseButtons.Right) return;

        int source = _constructPanel.GetSpriteSource(_currentFrame, spriteIndex);
        source = EnsureExclusiveSlot(_currentFrame, spriteIndex, source);
        bool hires = _bank.IsHires(source);

        // One undo snapshot per distinct piece touched in this stroke (a
        // drag can cross from one sprite into another, possibly resolving
        // to a different piece each time), not one per cell.
        if (_constructStrokePieces.Add(source)) PushUndo(source);

        byte color = button == MouseButtons.Right ? (byte)0 : _selectedColor;
        switch (_tool)
        {
            case Tool.Pencil:
            case Tool.Line:
                // Line is simplified to paint-per-cell here rather than
                // tracking its own start/end drag a second time - keeps this
                // view's hit-testing/undo logic from having to duplicate the
                // flat editor's separate line-preview machinery. col already
                // arrives in the right 0..11/0..23 range for this piece's
                // mode - ConstructCanvas's own hit-testing checks the same
                // IsSpriteHires flag.
                SetPixel(source, hires, row, col, color);
                if (_mirror)
                {
                    int maxCol = hires ? SpriteBank.HiresCols : SpriteBank.QuadCols;
                    SetPixel(source, hires, row, maxCol - 1 - col, color);
                }
                break;
            case Tool.Fill:
                FloodFill(source, hires, row, col, color);
                break;
        }
        if (_tool == Tool.Fill) RefreshAll();
        else RefreshAfterPixelEdit(source, row, col);
    }

    /// <summary>Flood fill within one piece - piece/localRow/localCol pick
    /// which art piece and where within it (0..11 for multicolour, 0..23
    /// for hires - see SetPixel). Never spills into another piece, since
    /// each is now its own independent canvas.</summary>
    private void FloodFill(int piece, bool hires, int localRow, int localCol, byte newColor)
    {
        if (hires) FloodFillHires(piece, localRow, localCol, newColor != 0 ? (byte)1 : (byte)0);
        else FloodFillMulticolor(piece, localRow, localCol, newColor);
    }

    private void FloodFillMulticolor(int piece, int localRow, int localCol, byte newColor)
    {
        var grid = _bank.Piece(piece);
        byte target = grid[localRow, localCol];
        if (target == newColor) return;
        var stack = new Stack<(int r, int c)>();
        stack.Push((localRow, localCol));
        while (stack.Count > 0)
        {
            var (r, c) = stack.Pop();
            if (r < 0 || r >= SpriteBank.QuadRows || c < 0 || c >= SpriteBank.QuadCols) continue;
            if (grid[r, c] != target) continue;
            grid[r, c] = newColor;
            stack.Push((r + 1, c));
            stack.Push((r - 1, c));
            stack.Push((r, c + 1));
            stack.Push((r, c - 1));
        }
    }

    private void FloodFillHires(int piece, int localRow, int localCol, byte newValue)
    {
        byte target = _bank.GetHiresPixel(piece, localRow, localCol);
        if (target == newValue) return;
        var stack = new Stack<(int r, int c)>();
        stack.Push((localRow, localCol));
        while (stack.Count > 0)
        {
            var (r, c) = stack.Pop();
            if (r < 0 || r >= SpriteBank.QuadRows || c < 0 || c >= SpriteBank.HiresCols) continue;
            if (_bank.GetHiresPixel(piece, r, c) != target) continue;
            _bank.SetHiresPixel(piece, r, c, newValue);
            stack.Push((r + 1, c));
            stack.Push((r - 1, c));
            stack.Push((r, c + 1));
            stack.Push((r, c - 1));
        }
    }

    private void DrawLine(int r0, int c0, int r1, int c1, byte color)
    {
        bool hires = _bank.IsHires(_editPiece);
        int maxCol = hires ? SpriteBank.HiresCols : SpriteBank.QuadCols;
        int dr = Math.Abs(r1 - r0), dc = Math.Abs(c1 - c0);
        int sr = r0 < r1 ? 1 : -1, sc = c0 < c1 ? 1 : -1;
        int err = dr - dc;
        int r = r0, c = c0;
        while (true)
        {
            if (r >= 0 && r < SpriteBank.QuadRows && c >= 0 && c < maxCol)
            {
                SetPixel(_editPiece, hires, r, c, color);
                if (_mirror) SetPixel(_editPiece, hires, r, maxCol - 1 - c, color);
            }
            if (r == r1 && c == c1) break;
            int e2 = 2 * err;
            if (e2 > -dc) { err -= dc; r += sr; }
            if (e2 < dr) { err += dr; c += sc; }
        }
    }

    private void ClearPiece() => _bank.ClearPiece(_editPiece);

    private void FlipHorizontalPiece()
    {
        // Hires needs a bit-level mirror across all 24 columns, not a
        // whole-cell swap: each raw cell packs 2 DIFFERENT hires columns
        // (see SpriteBank.GetHiresPixel), so swapping whole cells would
        // pair up the wrong two columns instead of properly mirroring.
        if (_bank.IsHires(_editPiece))
        {
            for (int r = 0; r < SpriteBank.QuadRows; r++)
                for (int c = 0; c < SpriteBank.HiresCols / 2; c++)
                {
                    int c2 = SpriteBank.HiresCols - 1 - c;
                    byte a = _bank.GetHiresPixel(_editPiece, r, c);
                    byte b = _bank.GetHiresPixel(_editPiece, r, c2);
                    _bank.SetHiresPixel(_editPiece, r, c, b);
                    _bank.SetHiresPixel(_editPiece, r, c2, a);
                }
            return;
        }
        var grid = _bank.Piece(_editPiece);
        for (int r = 0; r < SpriteBank.QuadRows; r++)
            for (int c = 0; c < SpriteBank.QuadCols / 2; c++)
            {
                int c2 = SpriteBank.QuadCols - 1 - c;
                (grid[r, c], grid[r, c2]) = (grid[r, c2], grid[r, c]);
            }
    }

    private void FlipVerticalPiece()
    {
        var grid = _bank.Piece(_editPiece);
        for (int r = 0; r < SpriteBank.QuadRows / 2; r++)
            for (int c = 0; c < SpriteBank.QuadCols; c++)
            {
                int r2 = SpriteBank.QuadRows - 1 - r;
                (grid[r, c], grid[r2, c]) = (grid[r2, c], grid[r, c]);
            }
    }

    // ---------------------------------------------------------------------
    // Copy-on-write for pixel edits. A piece is real, shared storage -
    // several (animation frame, hardware sprite) pairs can point their
    // Sprite # at the very same piece (most commonly right after
    // "Duplicate", which starts the new frame out re-using the previous
    // frame's art). Drawing directly into that piece would silently
    // repaint every OTHER frame/sprite that still references it too. So
    // before any pixel-editing operation touches _editPiece (or a
    // Positioned-view sprite's own source), EnsureExclusiveEditTarget/
    // EnsureExclusiveSlot checks whether the piece about to be drawn into
    // is still referenced elsewhere; if it is, its current content is
    // cloned into a free piece and only THIS (frame, sprite) reference is
    // repointed there first - the edit then lands on the fork, leaving
    // whoever else was using the original art untouched.
    // ---------------------------------------------------------------------
    private void EnsureExclusiveEditTarget()
    {
        // Uses the OWNER cached at selection time (_editPieceOwnerSprite),
        // not a live re-query of Construct's current primary selection -
        // it's -1 (no owner, never auto-fork) whenever _editPiece was picked
        // directly from the pool strip, even if Construct still happens to
        // have some unrelated hardware sprite selected. See its field doc.
        int exclusive = EnsureExclusiveSlot(_currentFrame, _editPieceOwnerSprite, _editPiece);
        // SetSpriteSource (inside EnsureExclusiveSlot) already updates
        // _editPiece via the SelectedSpriteSourceChanged event when
        // hwSprite is Construct's primary selection, but set it directly
        // too so this is correct even if that wiring ever changes.
        _editPiece = exclusive;
    }

    private int EnsureExclusiveSlot(int animFrame, int hwSprite, int piece)
    {
        if (hwSprite < 0) return piece; // no known (frame,sprite) owner to protect
        if (!IsSlotReferenced(piece, animFrame, hwSprite)) return piece; // already exclusive

        int newPiece = AllocateFreeSlot();
        CopySlotContent(piece, newPiece);
        _constructPanel.SetSpriteSource(animFrame, hwSprite, newPiece);
        return newPiece;
    }

    /// <summary>Copies one piece's 12x21 pixel content to another - shared
    /// by EnsureExclusiveSlot's fork and TryApplyPoolSize's shrink-time
    /// relocation below.</summary>
    private void CopySlotContent(int fromPiece, int toPiece)
    {
        for (int r = 0; r < SpriteBank.QuadRows; r++)
            for (int c = 0; c < SpriteBank.QuadCols; c++)
                _bank.Set(toPiece, r, c, _bank.Get(fromPiece, r, c));
        // Otherwise a fork/relocation of a hires piece would silently land
        // on a fresh (always-multicolour) slot and reinterpret its bytes
        // under the wrong mode the moment you start drawing on it.
        _bank.SetHires(toPiece, _bank.IsHires(fromPiece));
    }

    /// <summary>True if any (animation frame, hardware sprite) pair other
    /// than (exceptFrame, exceptSprite) has its Sprite # pointed at
    /// piece.</summary>
    private bool IsSlotReferenced(int piece, int exceptFrame = -1, int exceptSprite = -1)
    {
        int animCount = _constructPanel.AnimFrameCount;
        for (int f = 0; f < animCount; f++)
            for (int s = 0; s < 8; s++)
            {
                if (f == exceptFrame && s == exceptSprite) continue;
                if (_constructPanel.GetSpriteSource(f, s) == piece) return true;
            }
        return false;
    }

    /// <summary>Picks a piece nothing currently references, growing the
    /// pool by one if every existing piece is in use. Pool growth never
    /// touches the Timeline (animation frame count) at all any more - the
    /// two are fully independent, so there's no more "which frame gets the
    /// new piece" bookkeeping to do here.</summary>
    private int AllocateFreeSlot()
    {
        for (int piece = 0; piece < _bank.PieceCount; piece++)
            if (!IsSlotReferenced(piece)) return piece;

        int newPiece = _bank.PieceCount;
        _bank.Resize(_bank.PieceCount + 1);
        _spritePoolStrip.SetPieceCount(_bank.PieceCount);
        RefreshStatus($"Sprite pool grew to {_bank.PieceCount} piece(s) to make room for a forked sprite - every existing piece was already in use.");
        return newPiece;
    }

    /// <summary>Shrinks (or grows) the sprite pool safely: a plain
    /// _bank.Resize() truncates storage past the new piece count, but any
    /// (frame, sprite) Sprite # that still points at one of those
    /// truncated pieces - very possible once EnsureExclusiveSlot has been
    /// forking sprites into whatever pieces had room - would silently lose
    /// that art. Walks every reference first, relocates anything that
    /// would be orphaned into a free piece that survives the shrink, and
    /// only then resizes - or refuses (telling the user how many pieces
    /// are actually needed) if there isn't room to keep everything
    /// currently in use.</summary>
    private bool TryApplyPoolSize(int newPieceCount)
    {
        if (newPieceCount >= _bank.PieceCount)
        {
            _bank.Resize(newPieceCount);
            return true;
        }

        int animCount = _constructPanel.AnimFrameCount;
        var usedSlots = new HashSet<int>();
        for (int f = 0; f < animCount; f++)
            for (int s = 0; s < 8; s++)
                usedSlots.Add(_constructPanel.GetSpriteSource(f, s));

        var orphaned = new List<int>();
        var freeInRange = new List<int>();
        for (int piece = 0; piece < newPieceCount; piece++)
            if (!usedSlots.Contains(piece)) freeInRange.Add(piece);
        foreach (var piece in usedSlots)
            if (piece >= newPieceCount) orphaned.Add(piece);
        orphaned.Sort();

        if (orphaned.Count > freeInRange.Count)
        {
            MessageBox.Show(this,
                $"Can't shrink to {newPieceCount} sprite piece(s) without losing art: {usedSlots.Count} distinct " +
                "pieces are actually referenced somewhere in the Timeline right now. You need at least " +
                $"{usedSlots.Count} to keep everything currently in use.",
                "Can't shrink that far", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var relocation = new Dictionary<int, int>();
        for (int i = 0; i < orphaned.Count; i++)
        {
            CopySlotContent(orphaned[i], freeInRange[i]);
            relocation[orphaned[i]] = freeInRange[i];
        }
        for (int f = 0; f < animCount; f++)
            for (int s = 0; s < 8; s++)
            {
                int src = _constructPanel.GetSpriteSource(f, s);
                if (relocation.TryGetValue(src, out int newPiece))
                    _constructPanel.SetSpriteSource(f, s, newPiece);
            }

        _bank.Resize(newPieceCount);
        return true;
    }

    // ---------------------------------------------------------------------
    // Undo / redo. Two stacks - pixel edits here, position/Sprite # edits in
    // ConstructPanel - stamped from one shared UndoClock, so UndoLatest/
    // RedoLatest (Ctrl+Z/Y and the toolbar buttons) step through ONE
    // time-ordered history, whether the change was drawing or placement.
    // ---------------------------------------------------------------------
    private void UndoLatest()
    {
        long pixel = _undo.Count > 0 ? _undo[^1].seq : 0;
        long position = _constructPanel.PositionUndoTopSeq;
        if (pixel == 0 && position == 0) { RefreshStatus("Nothing to undo."); return; }
        if (pixel > position) Undo();
        else { _constructPanel.UndoPosition(); RefreshStatus("Undid last sprite placement change."); }
    }

    private void RedoLatest()
    {
        // The most recently UNDONE change has the lowest stamp of the two
        // redo tops (undo walks backwards through time).
        long pixel = _redo.Count > 0 ? _redo[^1].seq : 0;
        long position = _constructPanel.PositionRedoTopSeq;
        if (pixel == 0 && position == 0) { RefreshStatus("Nothing to redo."); return; }
        if (position == 0 || (pixel != 0 && pixel < position)) Redo();
        else { _constructPanel.RedoPosition(); RefreshStatus("Redid sprite placement change."); }
    }

    private void PushUndo(int? piece = null)
    {
        int p = piece ?? _editPiece;
        _undo.Add((p, _bank.ClonePiece(p), UndoClock.Next()));
        TrimUndo();
        _redo.Clear();
        _constructPanel.ClearPositionRedo();
    }

    private void TrimUndo()
    {
        while (_undo.Count > UndoCap) _undo.RemoveAt(0);
    }

    private void Undo()
    {
        if (_undo.Count == 0) { RefreshStatus("Nothing to undo."); return; }
        var last = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((last.piece, _bank.ClonePiece(last.piece), last.seq));
        _bank.RestorePiece(last.piece, last.snapshot);
        _editPiece = last.piece;
        RefreshAll();
        RefreshStatus("Undid last edit on sprite #" + last.piece + ".");
    }

    private void Redo()
    {
        if (_redo.Count == 0) { RefreshStatus("Nothing to redo."); return; }
        var last = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((last.piece, _bank.ClonePiece(last.piece), last.seq));
        _bank.RestorePiece(last.piece, last.snapshot);
        _editPiece = last.piece;
        RefreshAll();
        RefreshStatus("Redid edit on sprite #" + last.piece + ".");
    }

    // ---------------------------------------------------------------------
    // Frame selection / redraw - ConstructPanel's Timeline is now the only
    // frame navigator; SelectFrame just asks it to jump, and its
    // FrameChanged event (wired in BuildUi) mirrors the result back here.
    // ---------------------------------------------------------------------
    private void SelectFrame(int frame) => _constructPanel.GoToFrame(frame);

    private void RefreshAll()
    {
        UpdateSwatchIcons();
        SyncHiresButton();
        FitCanvasToScrollArea();
        _canvas.Invalidate();
        RefreshStatus(null);
        // The Construct panel shares this same SpriteBank instance, but it's
        // a separate Control - it never repaints just because this canvas's
        // data changed, so nudge it explicitly to keep the composited
        // preview live while drawing.
        _constructPanel.RefreshSpriteArt();
        // Same deal for the pool strip - and its own SetPieceCount is only
        // called where the pool size can actually change, so this is purely
        // "redraw whatever's currently on screen, with the current selection".
        _spritePoolStrip.SetSelected(_editPiece);
        _spritePoolStrip.Invalidate();
    }

    /// <summary>Cheap refresh for a plain pixel edit inside one piece: skips
    /// the re-fit/toolbar-sync work RefreshAll does and only repaints the
    /// pool thumbnail of the piece that changed.</summary>
    private void RefreshAfterPixelEdit(int piece, int row, int col)
    {
        // Only what that pixel can have changed: its cell (and the mirrored
        // one) in the edit grid - if the grid is showing this piece at all -
        // the Construct sprites that show this piece, and its thumbnail.
        if (piece == _editPiece)
        {
            _canvas.InvalidateCell(row, col);
            if (_mirror) _canvas.InvalidateCell(row, _canvas.Cols - 1 - col);
        }
        _constructPanel.RefreshSpriteArt(piece);
        _spritePoolStrip.InvalidatePiece(piece);
    }

    private void RefreshStatus(string? message)
    {
        int pieces = _bank.PieceCount;
        int bytes = pieces * SpriteBank.SpriteBytes;
        _statusLabel.Text = message ?? $"Editing sprite #{_editPiece}   pool: {pieces} piece(s), {bytes} bytes   frames: {_constructPanel.AnimFrameCount}   Tool: {_tool}   Colour: {PaletteName(_selectedColor)}";
        _fileLabel.Text = _lastPrgPath != null ? "Loaded: " + Path.GetFileName(_lastPrgPath)
                          : _lastProjectPath != null ? "Project: " + Path.GetFileName(_lastProjectPath)
                          : "(unsaved)";
    }

    private static string PaletteName(byte v) => v switch
    {
        0 => "Background",
        1 => "MC1",
        2 => "Individual",
        3 => "MC2",
        _ => "?"
    };

    // Reads from EditorPalette so a right-click colour change on the
    // toolbar swatches (see ShowPalettePopup) is reflected everywhere this
    // is used - the flat canvas, its thumbnails-in-waiting, and the
    // swatches' own icons.
    private Color PaletteColor(byte v) => EditorPalette.ColorFor(v, _bank.IndividualColor(_editPiece));

    // ---------------------------------------------------------------------
    // File operations
    // ---------------------------------------------------------------------
    private void NewBlankBank()
    {
        if (!ConfirmDiscard()) return;
        _bank = new SpriteBank(8);
        _undo.Clear(); _redo.Clear();
        _lastPrgPath = null; _lastSymPath = null; _lastProjectPath = null;
        _editPiece = 0;
        _editPieceOwnerSprite = -1;
        _poolCountUpDown.Value = _bank.PieceCount;
        _spritePoolStrip.SetPieceCount(_bank.PieceCount);
        _constructPanel.ResetForNewBank(8);
        SelectFrame(0);
        RefreshAll();
        RefreshStatus("New blank bank: 8 sprite pieces, 8 frames.");
    }

    private bool ConfirmDiscard()
    {
        if (_undo.Count == 0) return true;
        var result = MessageBox.Show(this, "Discard current edits?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        return result == DialogResult.Yes;
    }

    private static string GuessProjectRoot()
    {
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            if (File.Exists(Path.Combine(candidate, "Source.s"))) return candidate;
        }
        catch { /* fall through */ }
        return Environment.CurrentDirectory;
    }

    /// <summary>One loader for every sprite bank format - picked by the
    /// chosen file's extension: a compiled .prg (with its matching .sym,
    /// found next to it or asked for; picking the .sym itself works too) or
    /// a .png sprite sheet (see SpriteBank.LoadFromPng).</summary>
    private void LoadSpriteBank()
    {
        string root = GuessProjectRoot();
        using var ofd = new OpenFileDialog
        {
            Title = "Load sprite bank",
            Filter = "Sprite banks (*.prg;*.sym;*.png)|*.prg;*.sym;*.png|" +
                     "Compiled bank (*.prg + matching .sym)|*.prg;*.sym|" +
                     "PNG sprite sheet (*.png)|*.png|" +
                     "All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(root) ? root : Environment.CurrentDirectory
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        string ext = Path.GetExtension(ofd.FileName).ToLowerInvariant();
        switch (ext)
        {
            case ".png": LoadSpriteBankFromPng(ofd.FileName); break;
            case ".prg": LoadSpriteBankFromCompiled(ofd.FileName); break;
            case ".sym": LoadSpriteBankFromCompiled(Path.ChangeExtension(ofd.FileName, ".prg")); break;
            default:
                MessageBox.Show(this, $"Unsupported sprite bank format \"{ext}\" - choose a .prg (+ .sym) or a .png.",
                    "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                break;
        }
    }

    private void LoadSpriteBankFromCompiled(string prgPath)
    {
        if (!File.Exists(prgPath))
        {
            MessageBox.Show(this, $"Couldn't find the program file:\n{prgPath}", "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string symPath = Path.ChangeExtension(prgPath, ".sym");
        if (!File.Exists(symPath))
        {
            using var sfd = new OpenFileDialog
            {
                Title = "Select the matching .sym file",
                Filter = "SYM files (*.sym)|*.sym|All files (*.*)|*.*",
                InitialDirectory = Path.GetDirectoryName(prgPath)
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            symPath = sfd.FileName;
        }

        try
        {
            _bank = SpriteBank.LoadFromCompiled(prgPath, symPath, out string log);
            _lastPrgPath = prgPath;
            _lastSymPath = symPath;
            AfterBankLoaded(log);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadSpriteBankFromPng(string pngPath)
    {
        try
        {
            _bank = SpriteBank.LoadFromPng(pngPath, out int[] slotColors, out string log);
            _lastPrgPath = null;
            _lastSymPath = null;
            EditorPalette.Set(1, slotColors[1]);
            EditorPalette.Set(3, slotColors[3]);
            for (int p = 0; p < _bank.PieceCount; p++) _bank.SetIndividualColor(p, slotColors[2]);
            AfterBankLoaded(log);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AfterBankLoaded(string log)
    {
        _lastProjectPath = null;
        _undo.Clear(); _redo.Clear();
        _editPiece = 0;
        _editPieceOwnerSprite = -1;
        _poolCountUpDown.Value = _bank.PieceCount;
        _spritePoolStrip.SetPieceCount(_bank.PieceCount);
        int legacyFrames = Math.Max(1, _bank.PieceCount / 4);
        _constructPanel.ResetForNewBank(legacyFrames);
        SelectFrame(0);
        RefreshAll();
        RefreshStatus(log);
    }

    private void LoadProject()
    {
        using var ofd = new OpenFileDialog { Title = "Load project", Filter = "Sprite editor project (*.json)|*.json" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var result = ProjectFile.Load(ofd.FileName);
            _bank = result.Bank;
            _backdrop?.Image.Dispose();
            _backdrop = result.Backdrop;
            if (result.Palette is { } pal)
                EditorPalette.SetAll(pal.Background, pal.Border, pal.Mc1, pal.Mc2);
            else if (result.Backdrop != null)
                // Pre-v4 project: no saved registers. The backdrop used to be
                // drawn with its own bg byte baked in, so keep it looking the same.
                EditorPalette.Set(0, result.Backdrop.BackgroundIndex);
            _lastProjectPath = ofd.FileName;
            _lastPrgPath = null; _lastSymPath = null;
            _undo.Clear(); _redo.Clear();
            _editPiece = 0;
            _editPieceOwnerSprite = -1;
            _poolCountUpDown.Value = _bank.PieceCount;
            _spritePoolStrip.SetPieceCount(_bank.PieceCount);
            if (result.Construct != null) _constructPanel.ImportData(result.Construct);
            else _constructPanel.ResetForNewBank(Math.Max(1, _bank.PieceCount / 4));
            SelectFrame(0);
            RefreshBackdropViews();
            RefreshAll();
            RefreshStatus("Loaded project " + Path.GetFileName(ofd.FileName) +
                (result.Backdrop != null ? " (with backdrop and sprite placements)" : " (no backdrop saved)"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveProject()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "Save project",
            Filter = "Sprite editor project (*.json)|*.json",
            FileName = "magspriteed_project.json"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ProjectFile.Save(sfd.FileName, _bank, _constructPanel, _backdrop);
            _lastProjectPath = sfd.FileName;
            RefreshStatus("Saved project " + Path.GetFileName(sfd.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveProjectQuick()
    {
        if (_lastProjectPath == null) { SaveProject(); return; }
        try
        {
            ProjectFile.Save(_lastProjectPath, _bank, _constructPanel, _backdrop);
            RefreshStatus("Saved project " + Path.GetFileName(_lastProjectPath));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Menu > Export Spritebank: every sprite in the pool as one
    /// raw binary, 64 bytes per sprite in C64 hardware format (see
    /// SpriteBank.ToBinary) - ready to incbin at a 64-byte-aligned address.</summary>
    private void ExportSpritebank()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "Export spritebank (C64 sprite format)",
            Filter = "Binary (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = "magspriteed_sprites.bin"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(sfd.FileName, _bank.ToBinary());
            RefreshStatus($"Exported {_bank.PieceCount} sprite(s), {_bank.PieceCount * SpriteBank.SpriteBytes} bytes, to {sfd.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadBackdropPicture()
    {
        string root = GuessProjectRoot();
        string gfxDir = Path.Combine(root, "GFX", "Bitmap");
        using var ofd = new OpenFileDialog
        {
            Title = "Load backdrop picture (Koala format)",
            Filter = "Koala picture (*.kla;*.koa)|*.kla;*.koa|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(gfxDir) ? gfxDir : root
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var loaded = BackdropPicture.Load(ofd.FileName);
            _backdrop?.Image.Dispose();
            _backdrop = loaded;
            EditorPalette.Set(0, loaded.BackgroundIndex); // a Koala loader writes the file's bg byte to $d021
            RefreshStatus("Loaded backdrop " + Path.GetFileName(ofd.FileName) + $" ($d021 = ${loaded.BackgroundIndex:X2})");
            RefreshBackdropViews();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshBackdropViews()
    {
        _constructPanel.RefreshBackdrop();
    }
}

/// <summary>Dark-themed renderer shared by the toolbar and its dropdown menu.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    // Belt-and-braces: force readable text regardless of each item's own
    // ForeColor, so a menu item added without one never renders black text
    // on this renderer's dark background again.
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Color.Gainsboro : Color.FromArgb(110, 110, 110);
        base.OnRenderItemText(e);
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    // The ToolStrip/MenuStrip's own bar background - without these,
    // ToolStripProfessionalRenderer paints its default light Office-blue
    // gradient here regardless of the control's own BackColor.
    public override Color ToolStripGradientBegin => Color.FromArgb(30, 30, 30);
    public override Color ToolStripGradientMiddle => Color.FromArgb(30, 30, 30);
    public override Color ToolStripGradientEnd => Color.FromArgb(30, 30, 30);
    public override Color SeparatorDark => Color.FromArgb(60, 60, 60);
    public override Color SeparatorLight => Color.FromArgb(60, 60, 60);

    public override Color MenuItemSelected => Color.FromArgb(60, 60, 60);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(60, 60, 60);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(60, 60, 60);
    public override Color MenuItemBorder => Color.FromArgb(90, 90, 90);
    public override Color MenuBorder => Color.FromArgb(50, 50, 50);
    public override Color ToolStripDropDownBackground => Color.FromArgb(30, 30, 30);
    public override Color ImageMarginGradientBegin => Color.FromArgb(30, 30, 30);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(30, 30, 30);
    public override Color ImageMarginGradientEnd => Color.FromArgb(30, 30, 30);

    // Toolbar button hover/press/checked states.
    public override Color ButtonSelectedHighlight => Color.FromArgb(60, 60, 60);
    public override Color ButtonSelectedHighlightBorder => Color.FromArgb(90, 90, 90);
    public override Color ButtonPressedHighlight => Color.FromArgb(80, 80, 80);
    public override Color ButtonPressedHighlightBorder => Color.FromArgb(110, 110, 110);
    public override Color ButtonCheckedHighlight => Color.FromArgb(70, 90, 120);
    public override Color ButtonCheckedHighlightBorder => Color.FromArgb(120, 160, 210);
}
