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
    // height, matching PositionedEditCanvas's own 2x-wide pixel rectangles.
    private const int MinEditCellHeight = 10;
    private const int MaxEditCellHeight = 36;

    private SpriteBank _bank = CreateDefaultBank();

    // Which animation/Timeline frame is active (drives Construct's
    // positions and the Positioned view) - decoupled from both which
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

    private readonly List<(int piece, byte[,] snapshot)> _undo = new();
    private readonly List<(int piece, byte[,] snapshot)> _redo = new();
    private const int UndoCap = 100;

    // Line tool state (flat editor only - the positioned view simplifies
    // Line to paint-per-cell, see PositionedCanvas_CellInteract)
    private int _lineStartRow = -1, _lineStartCol = -1;
    private int _hoverRow = -1, _hoverCol = -1;

    private ToolStrip _toolbar = null!;
    private PixelGridControl _canvas = null!;
    private Panel _canvasScroll = null!;
    private SpritePoolStrip _spritePoolStrip = null!;
    private Panel _spritePoolScroll = null!;
    private PositionedEditCanvas _positionedCanvas = null!;
    private ToolStripButton _positionedModeBtn = null!;
    private readonly HashSet<int> _positionedStrokePieces = new();

    private ConstructPanel _constructPanel = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripStatusLabel _fileLabel = null!;
    private NumericUpDown _frameCountUpDown = null!;
    private NumericUpDown _poolCountUpDown = null!;

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
        Load += (_, _) => { FitCanvasToScrollArea(); FitPoolStripWidth(); };
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

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 960));

        // ---- Left: edit canvas - Single Sprite View (one 12x21 piece,
        // highly zoomed in - the default) or Positioned view (zoomed, in
        // place over the backdrop, panned with middle-drag) ----
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
        _canvasScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(12, 12, 12), Padding = new Padding(20) };
        _canvasScroll.Controls.Add(_canvas);
        _canvas.Location = new Point(20, 20);
        // Re-fit whenever the host area's size actually changes (window
        // resize, Positioned view toggling off and giving this back the
        // full width, etc.) - keeps the canvas at the largest zoom that
        // still needs no horizontal scrollbar.
        _canvasScroll.SizeChanged += (_, _) => FitCanvasToScrollArea();

        _positionedCanvas = new PositionedEditCanvas
        {
            Dock = DockStyle.Fill,
            Visible = false,
            PositionProvider = s => _constructPanel.GetPosition(_currentFrame, s),
            SpritePixel = (s, row, col) =>
            {
                int source = _constructPanel.GetSpriteSource(_currentFrame, s);
                return source >= 0 && source < _bank.PieceCount ? _bank.Get(source, row, col) : (byte)0;
            },
            PaletteProvider = (v, s) =>
            {
                int source = _constructPanel.GetSpriteSource(_currentFrame, s);
                int ind = source >= 0 && source < _bank.PieceCount ? _bank.IndividualColor(source) : SpriteBank.DefaultIndividualColor;
                return EditorPalette.ColorFor(v, ind);
            },
            IsSpriteHires = s =>
            {
                int source = _constructPanel.GetSpriteSource(_currentFrame, s);
                return source >= 0 && source < _bank.PieceCount && _bank.IsHires(source);
            }
        };

        var centerHost = new Panel { Dock = DockStyle.Fill };
        centerHost.Controls.Add(_positionedCanvas);
        centerHost.Controls.Add(_canvasScroll);

        // ---- Far left: vertical strip of every pool piece as a small
        // thumbnail (Single Sprite View only - hidden in Positioned view,
        // see SetPositionedMode). Click one to make it Single Sprite View's
        // edit target; thumbnails update live while drawing via RefreshAll. ----
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
        _spritePoolScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(12, 12, 12),
            Padding = new Padding(2)
        };
        _spritePoolScroll.Controls.Add(_spritePoolStrip);
        // ClientSizeChanged (not SizeChanged) also catches the case that
        // actually caused the leftover horizontal scrollbar: the panel's
        // own outer bounds don't change when SetPieceCount grows the strip
        // tall enough to need a vertical scrollbar, but its CLIENT area
        // does (shrinks by the scrollbar's width) - and a hardcoded once-
        // guessed reservation for that width (SystemInformation's, which
        // can be a few pixels off from what actually gets drawn depending
        // on theme/DPI) isn't reliable enough on its own. Re-fitting the
        // strip's own Width to whatever's actually available, every time
        // that available space can change, is what actually guarantees no
        // horizontal scrollbar ever shows up unless the piece truly
        // doesn't fit any more.
        _spritePoolScroll.ClientSizeChanged += (_, _) => FitPoolStripWidth();

        var editArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor
        };
        editArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SpritePoolStrip.PreferredWidth + 4));
        editArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editArea.Controls.Add(_spritePoolScroll, 0, 0);
        editArea.Controls.Add(centerHost, 1, 0);

        // ---- Right: embedded Construct panel (placement/composition over
        // the backdrop) - also the sole frame navigator now; the FRAMES
        // thumbnail strip was removed in favour of its Timeline. Loading a
        // backdrop is only ever done via this Menu's own item now (see
        // BuildToolbar) - ConstructPanel no longer has its own button. ----
        _constructPanel = new ConstructPanel(() => _bank, () => _backdrop);
        _constructPanel.FrameChanged += frame =>
        {
            _currentFrame = Math.Max(0, Math.Min(frame, _constructPanel.AnimFrameCount - 1));
            RefreshAll();
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
                _editPiece = piece;
                _editPieceOwnerSprite = _constructPanel.PrimarySelectedSprite;
                _spritePoolStrip.SetSelected(piece);
                ScrollPoolStripToSelection();
                RefreshAll();
            }
        };

        root.Controls.Add(editArea, 0, 0);
        root.Controls.Add(_constructPanel, 1, 0);

        Controls.Add(root);
        Controls.Add(status);
        Controls.Add(_toolbar);

        _spritePoolStrip.SetPieceCount(_bank.PieceCount);
        _spritePoolStrip.SetSelected(_editPiece);
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
        int viewTop = -_spritePoolScroll.AutoScrollPosition.Y;
        int viewHeight = _spritePoolScroll.ClientSize.Height;
        if (top < viewTop) _spritePoolScroll.AutoScrollPosition = new Point(0, top);
        else if (bottom > viewTop + viewHeight) _spritePoolScroll.AutoScrollPosition = new Point(0, bottom - viewHeight);
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
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("New Procedural Bank", Icons.Wand(), (_, _) => NewProceduralBank()));
        menuBtn.DropDownItems.Add(new ToolStripSeparator());
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Load Sprite Bank (.prg + .sym)...", Icons.FolderOpen(), (_, _) => DeferDialogAction(LoadSpriteBankFromCompiled)));
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Load Sprite Bank (.png)...", Icons.FolderOpen(), (_, _) => DeferDialogAction(LoadSpriteBankFromPng)));
        menuBtn.DropDownItems.Add(new ToolStripSeparator());
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Load Project (.json)...", Icons.FolderOpen(), (_, _) => DeferDialogAction(LoadProject)));
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Save Project (.json)...", Icons.Save(), (_, _) => DeferDialogAction(SaveProject)));
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Save Project", Icons.Save(), (_, _) => SaveProjectQuick()));
        menuBtn.DropDownItems.Add(new ToolStripSeparator());
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Export ASM (MagSpriteEd_Sprites_Data.s)...", Icons.Export(), (_, _) => DeferDialogAction(ExportAsm)));
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Export Binary (.bin)...", Icons.Export(), (_, _) => DeferDialogAction(ExportBinary)));
        menuBtn.DropDownItems.Add(new ToolStripMenuItem("Export Optimized ASM (deduplicated)...", Icons.Export(), (_, _) => DeferDialogAction(_constructPanel.ExportOptimizedAsm)));
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
        byte[] visualOrder = { 0, 1, 3, 2 }; // Background, MC1, MC2, Individual
        foreach (var v in visualOrder) _toolbar.Items.Add(_swatchButtons[v]);

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
        _toolbar.Items.Add(_borderBtn);

        EditorPalette.Changed += () =>
        {
            UpdateSwatchIcons();
            _canvas.Invalidate();
            _positionedCanvas.Invalidate();
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

        _positionedModeBtn = new ToolStripButton
        {
            Image = Icons.Target(),
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = "Positioned view: edit sprites in place over the backdrop, as arranged in Construct (middle-drag to pan, wheel to zoom)",
            CheckOnClick = true
        };
        _positionedModeBtn.CheckedChanged += (_, _) => SetPositionedMode(_positionedModeBtn.Checked);
        _toolbar.Items.Add(_positionedModeBtn);


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
        AddToolbarButton(Icons.Undo(), "Undo", (_, _) => Undo());
        AddToolbarButton(Icons.Redo(), "Redo", (_, _) => Redo());

        _toolbar.Items.Add(new ToolStripSeparator());

        // -- Sprite pool size: how many distinct 12x21 art pieces exist,
        // entirely independent of the Frame count below. (Swapped ahead of
        // Frames, per request.) --
        _toolbar.Items.Add(new ToolStripLabel("Pool") { ForeColor = Color.Gainsboro });
        _poolCountUpDown = new NumericUpDown { Minimum = 1, Maximum = 512, Value = _bank.PieceCount };
        _toolbar.Items.Add(new ToolStripControlHost(_poolCountUpDown) { AutoSize = false, Width = 50 });
        AddToolbarButton(Icons.Apply(), "Apply sprite pool size (how many distinct pieces of art exist)", (_, _) =>
        {
            if (!TryApplyPoolSize((int)_poolCountUpDown.Value)) { _poolCountUpDown.Value = _bank.PieceCount; return; }
            _editPiece = Math.Min(_editPiece, _bank.PieceCount - 1);
            _spritePoolStrip.SetPieceCount(_bank.PieceCount);
            _constructPanel.RefreshAfterPoolChange();
            RefreshAll();
            RefreshStatus($"Sprite pool size set to {_bank.PieceCount}.");
        });

        _toolbar.Items.Add(new ToolStripSeparator());

        // -- Frame count (Timeline length - independent of the sprite pool
        // above; see ConstructPanel's class remarks). The frame-step
        // buttons come first, then the "Frames" label + value field, then
        // Apply last (per request) - it still just reads
        // _frameCountUpDown.Value live at click time, so it works
        // regardless of where any of these are drawn relative to it. --
        AddToolbarButton(Icons.FrameInsert(), "Add 1 new frame step at the current Timeline position (duplicate of it)", (_, _) => AddFrameStepAtCurrent());
        AddToolbarButton(Icons.FrameDelete(), "Delete the current animation frame step", (_, _) => DeleteCurrentFrameStep());
        AddToolbarButton(Icons.Duplicate(), "Copy this animation frame's sprites data (positions + Sprite #s) to Clipboard", (_, _) => _constructPanel.CopyCurrentFrameToClipboard());
        AddToolbarButton(Icons.Paste(), "Paste sprites data from Clipboard into the current animation frame", (_, _) =>
        {
            _constructPanel.PasteFrameFromClipboard();
            RefreshAll();
        });
        _toolbar.Items.Add(new ToolStripLabel("Frames") { ForeColor = Color.Gainsboro });
        _frameCountUpDown = new NumericUpDown { Minimum = 1, Maximum = 128, Value = 8 };
        _toolbar.Items.Add(new ToolStripControlHost(_frameCountUpDown) { AutoSize = false, Width = 46 });
        AddToolbarButton(Icons.Apply(), "Apply frame count (Timeline length)", (_, _) =>
        {
            _constructPanel.SetAnimFrameCount((int)_frameCountUpDown.Value);
            _currentFrame = Math.Min(_currentFrame, _constructPanel.AnimFrameCount - 1);
            SelectFrame(_currentFrame);
            RefreshAll();
            RefreshStatus($"Frame count set to {_constructPanel.AnimFrameCount}.");
        });

        _toolbar.Items.Add(new ToolStripSeparator());

        // -- Sprite outline visibility in Construct's composited preview -
        // moved here from the removed "Backdrop" panel's own checkbox. --
        var outlinesBtn = new ToolStripButton
        {
            Image = Icons.Outline(),
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = "Show sprite outlines (bounding box + index) in Construct's preview",
            CheckOnClick = true,
            Checked = true
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

    private void UpdateSwatchIcons()
    {
        Color[] colors = { EditorPalette.BackgroundColor, PaletteColor(1), PaletteColor(2), PaletteColor(3) };
        for (int i = 0; i < 4; i++) _swatchButtons[i].Image = Icons.Swatch(colors[i]);
        _borderBtn.Image = Icons.BorderSwatch(EditorPalette.BorderColor, EditorPalette.BackgroundColor);
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
    /// the same "unit" size drives both column counts. Called on resize,
    /// when Positioned view hands the area back, and whenever _editPiece or
    /// its hires flag changes (see RefreshAll/SyncHiresButton).</summary>
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

    /// <summary>Keeps the pool strip's own Width exactly matching whatever
    /// horizontal space _spritePoolScroll's client area actually has right
    /// now, so it never ends up a few pixels wider than what's genuinely
    /// available (which is what triggers an unwanted horizontal
    /// scrollbar) - see the ClientSizeChanged wiring in BuildUi.</summary>
    private void FitPoolStripWidth()
    {
        int availW = _spritePoolScroll.ClientSize.Width - _spritePoolScroll.Padding.Horizontal;
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

    private void SetPositionedMode(bool positioned)
    {
        _canvasScroll.Visible = !positioned;
        _spritePoolScroll.Visible = !positioned;
        _positionedCanvas.Visible = positioned;
        // The icon shows what clicking again will switch TO, not the
        // current state - so it flips to "Single Sprite View" once you're
        // in Positioned view, and back to "Positioned view" otherwise.
        _positionedModeBtn.Image = positioned ? Icons.SingleSprite() : Icons.Target();
        _positionedModeBtn.ToolTipText = positioned
            ? "Single Sprite View: edit one 12x21 sprite piece, zoomed in"
            : "Positioned view: edit sprites in place over the backdrop, as arranged in Construct (middle-drag to pan, wheel to zoom)";
        if (positioned)
        {
            _positionedCanvas.Backdrop = _backdrop?.Image;
            _positionedCanvas.Invalidate();
        }
        else
        {
            // Positioned view can have edited pieces the strip never
            // repainted while hidden (it only tracks _editPiece, not
            // whichever piece a Positioned-view sprite happened to show) -
            // refresh every thumbnail once on the way back in.
            _spritePoolStrip.Invalidate();
            ScrollPoolStripToSelection();
            FitCanvasToScrollArea();
        }
    }

    private void WireEvents()
    {
        _canvas.CellInteract += Canvas_CellInteract;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseUp += Canvas_MouseUp;

        _positionedCanvas.CellInteract += PositionedCanvas_CellInteract;
        _positionedCanvas.MouseDown += (_, e) => { if (e.Button is MouseButtons.Left or MouseButtons.Right) _positionedStrokePieces.Clear(); };
        _positionedCanvas.MouseUp += (_, _) => _positionedStrokePieces.Clear();
    }

    // ---------------------------------------------------------------------
    // App-wide keyboard shortcuts. KeyPreview routes every KeyDown through
    // here FIRST regardless of which control has focus - ConstructPanel's
    // own canvas already handles Ctrl+Z/Y for POSITION undo when it has
    // focus, so this only takes over when focus is anywhere else (leaving
    // e.Handled unset lets the child control's own handler still run).
    // ---------------------------------------------------------------------
    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        bool inConstruct = _constructPanel.ContainsFocus;

        if (e.Control && e.KeyCode == Keys.Z && !e.Shift)
        {
            if (inConstruct) return; // let ConstructCanvas's own Ctrl+Z (position undo) handle it
            Undo();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Control && (e.KeyCode == Keys.Y || (e.KeyCode == Keys.Z && e.Shift)))
        {
            if (inConstruct) return;
            Redo();
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
        return c is NumericUpDown or TextBoxBase or ComboBox;
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
                    RefreshAfterPixelEdit(_editPiece);
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
    // Positioned canvas interaction - same tools, but the piece to edit is
    // resolved from wherever the click landed rather than read directly
    // off _editPiece, since each of the 8 sprites can show a different
    // piece than whatever Single Sprite View currently has selected.
    // ---------------------------------------------------------------------
    private void PositionedCanvas_CellInteract(int spriteIndex, int row, int col, MouseButtons button)
    {
        if (button != MouseButtons.Left && button != MouseButtons.Right) return;

        int source = _constructPanel.GetSpriteSource(_currentFrame, spriteIndex);
        source = EnsureExclusiveSlot(_currentFrame, spriteIndex, source);
        bool hires = _bank.IsHires(source);

        // One undo snapshot per distinct piece touched in this stroke (a
        // drag can cross from one sprite into another, possibly resolving
        // to a different piece each time), not one per cell.
        if (_positionedStrokePieces.Add(source)) PushUndo(source);

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
                // mode - PositionedEditCanvas's own hit-testing checks the
                // same IsSpriteHires flag.
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
        else RefreshAfterPixelEdit(source);
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
    // Undo / redo (pixel art)
    // ---------------------------------------------------------------------
    private void PushUndo(int? piece = null)
    {
        int p = piece ?? _editPiece;
        _undo.Add((p, _bank.ClonePiece(p)));
        TrimUndo();
        _redo.Clear();
    }

    private void PushUndoAll()
    {
        // Snapshot every piece as one grouped undo step is unnecessary here -
        // "Procedural (all)" is easy to reverse by re-loading/undoing per
        // piece is not exact, so just snapshot everything and rely on
        // Undo per-piece afterwards for anything else.
        for (int p = 0; p < _bank.PieceCount; p++)
        {
            _undo.Add((p, _bank.ClonePiece(p)));
        }
        TrimUndo();
        _redo.Clear();
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
        _redo.Add((last.piece, _bank.ClonePiece(last.piece)));
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
        _undo.Add((last.piece, _bank.ClonePiece(last.piece)));
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
        _positionedCanvas.Invalidate();
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
    private void RefreshAfterPixelEdit(int piece)
    {
        _canvas.Invalidate();
        _positionedCanvas.Invalidate();
        _constructPanel.RefreshSpriteArt();
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

    private void NewProceduralBank()
    {
        if (!ConfirmDiscard()) return;
        _bank = CreateDefaultBank();
        _undo.Clear(); _redo.Clear();
        _lastPrgPath = null; _lastSymPath = null; _lastProjectPath = null;
        _editPiece = 0;
        _editPieceOwnerSprite = -1;
        _poolCountUpDown.Value = _bank.PieceCount;
        _spritePoolStrip.SetPieceCount(_bank.PieceCount);
        _constructPanel.ResetForNewBank(8);
        SelectFrame(0);
        RefreshAll();
        RefreshStatus("New procedural bank: 8 sprite pieces, 8 frames.");
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

    private void LoadSpriteBankFromCompiled()
    {
        string root = GuessProjectRoot();
        using var ofd = new OpenFileDialog
        {
            Title = "Load compiled sprite bank - select program.prg",
            Filter = "PRG files (*.prg)|*.prg|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(root) ? root : Environment.CurrentDirectory
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        string prgPath = ofd.FileName;
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

    private void LoadSpriteBankFromPng()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Load sprite bank - select a PNG sprite sheet",
            Filter = "PNG images (*.png)|*.png|All files (*.*)|*.*"
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _bank = SpriteBank.LoadFromPng(ofd.FileName, out int[] slotColors, out string log);
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

    private void ExportAsm()
    {
        string root = GuessProjectRoot();
        string startupDir = Path.Combine(root, "Startup");
        using var sfd = new SaveFileDialog
        {
            Title = "Export ASM - overwrites MagSpriteEd_Sprites_Data.s",
            Filter = "Assembly source (*.s)|*.s|All files (*.*)|*.*",
            FileName = "MagSpriteEd_Sprites_Data.s",
            InitialDirectory = Directory.Exists(startupDir) ? startupDir : root
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(sfd.FileName, _bank.ToAsm());
            RefreshStatus("Exported ASM to " + sfd.FileName +
                "  -- build with FIRE_SPRITES_MANUAL defined (c6510 -d FIRE_SPRITES_MANUAL ...) to use it.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportBinary()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "Export raw binary",
            Filter = "Binary (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = "magspriteed_sprites.bin"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(sfd.FileName, _bank.ToBinary());
            RefreshStatus("Exported binary to " + sfd.FileName);
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
        _positionedCanvas.Backdrop = _backdrop?.Image;
        _positionedCanvas.Invalidate();
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
