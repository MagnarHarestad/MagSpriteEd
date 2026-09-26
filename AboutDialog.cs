using System.Drawing;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Menu > About - credits and a single reference sheet for every keyboard/
/// mouse shortcut scattered across the toolbar tooltips, the Construct
/// panel's own hint label (removed in favour of this) and MainForm's
/// KeyDown handling, gathered in one place instead of only discoverable by
/// hovering each toolbar button individually.
/// </summary>
internal sealed class AboutDialog : Form
{
    protected override void OnHandleCreated(System.EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(this);
    }

    public AboutDialog()
    {
        Text = "About MagSpriteEd";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(18, 18, 18);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(700, 720);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var titleLabel = new Label
        {
            AutoSize = true,
            Text = "MagSpriteEd",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.White
        };
        var subtitleLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
            Text =
                "Hand-author animated Commodore 64 multicolour hardware-sprite art:\r\n" +
                "draw a pool of 12x21 sprites, arrange up to 8 hardware sprites per\r\n" +
                "animation frame over a real C64 backdrop, and export 6502 assembly or binary.\r\n" +
                "\r\n" +
                "HOW TO USE\r\n" +
                "  1. Start a bank: New Blank Bank, or load one from a\r\n" +
                "     .prg + .sym pair, a sprite-sheet .png (24x21 sprites, 2 px wide\r\n" +
                "     pixels, black = transparent, max 3 colours) or a saved project (.json).\r\n" +
                "  2. Draw pool pieces in the Single Sprite View, or right on the sprites in Construct.\r\n" +
                "  3. In Construct, load a .kla backdrop, set the number of frames, and choose\r\n" +
                "     each hardware sprite's Sprite # and X/Y per frame. Play to preview.\r\n" +
                "  4. Save the project, then Export Spritebank (binary, all sprites) or\r\n" +
                "     Export Animation (ASM: used sprites + per-frame tables)."
        };
        var creditLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
            ForeColor = Color.FromArgb(255, 200, 60),
            Text = "Developed by Magnar Harestad of Censor Design."
        };
        var headerStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Top
        };
        headerStack.Controls.Add(titleLabel);
        headerStack.Controls.Add(subtitleLabel);
        headerStack.Controls.Add(creditLabel);
        layout.Controls.Add(headerStack, 0, 0);

        var shortcuts = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9f),
            Margin = new Padding(0, 12, 0, 12),
            Text = BuildShortcutsText()
        };
        layout.Controls.Add(shortcuts, 0, 1);

        var closeBtn = new Button
        {
            Text = "Close",
            Width = 90,
            Anchor = AnchorStyles.Right,
            DialogResult = DialogResult.OK
        };
        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Top
        };
        buttonRow.Controls.Add(closeBtn);
        layout.Controls.Add(buttonRow, 0, 2);

        Controls.Add(layout);
        AcceptButton = closeBtn;
        CancelButton = closeBtn;

        // A read-only TextBox select-alls itself when it gets initial focus; focus Close instead.
        Shown += (_, _) =>
        {
            shortcuts.Select(0, 0);
            closeBtn.Focus();
        };
    }

    private static string BuildShortcutsText() =>
        "DRAWING (Single Sprite View / directly on sprites in Construct)\r\n" +
        "  Left click / drag        Paint with the selected colour\r\n" +
        "  Right click / drag       Erase (paint Background)\r\n" +
        "  0 / 1 / 2 / 3            Select colour (Background / MC1 / MC2 / Individual)\r\n" +
        "  Ctrl+Z / Ctrl+Y          Undo / redo the last change - drawing or sprite\r\n" +
        "                           placement, in the order you made them\r\n" +
        "  Ctrl+S / Ctrl+Shift+S    Save project / Save project as (choose a file)\r\n" +
        "  Right-click a swatch     Change that swatch's real C64 colour - Background\r\n" +
        "                           ($d021), MC1 and MC2 are shared by all sprites,\r\n" +
        "                           Individual applies only to the sprite being edited\r\n" +
        "  Border swatch            Click to change the $d020 border colour Construct\r\n" +
        "                           draws around the screen (it covers sprites, as on a C64)\r\n" +
        "\r\n" +
        "SPRITE POOL LIST (left of Single Sprite View)\r\n" +
        "  Click a thumbnail        Edit that pool piece\r\n" +
        "\r\n" +
        "CONSTRUCT PANEL\r\n" +
        "  Left / right drag        Draw on / erase the sprite under the cursor\r\n" +
        "                           (not in the border - it covers the sprites there)\r\n" +
        "  Shift+click              Select a sprite (Shift on empty space deselects)\r\n" +
        "  Shift+drag               Move the selected sprite(s)\r\n" +
        "  Drag a label (S3 - #6)   Same as Shift+drag: grab and move that sprite\r\n" +
        "  Alt+click a sprite       Select it and glue it flush against the nearest\r\n" +
        "                           sprite (smallest move, edges aligned, no overlap)\r\n" +
        "  Hold Alt while dragging  Keep snapping to glued spots as you move; let go\r\n" +
        "                           of Alt to move freely\r\n" +
        "  Ctrl+click               Add/remove a sprite from the selection group\r\n" +
        "  Arrow keys               Nudge the selected sprite(s) by 1 pixel\r\n" +
        "  Shift+arrow              Nudge by 8 pixels\r\n" +
        "  Mouse wheel              Zoom the composited preview in / out\r\n" +
        "  Middle-click drag        Pan the view\r\n" +
        "  Sprite info box          Shown under the selected sprite:\r\n" +
        "                           < / > (or wheel) step its Sprite # through the pool;\r\n" +
        "                           X / Y: drag left/right to change (Shift = x8),\r\n" +
        "                           wheel = +/-1, or click to type a value\r\n" +
        "  H                        Hide / show the sprite info box\r\n" +
        "  Menu > Show Grid         Toggles a faint checkerboard when no backdrop is loaded\r\n" +
        "\r\n" +
        "TOOLS\r\n" +
        "  Pencil / Fill / Line     Left = paint, right = erase\r\n" +
        "  Mirror                   Mirrors every stroke left/right while enabled\r\n" +
        "  Show/Hide Outlines       Toggles the bounding box + label Construct draws over\r\n" +
        "                           each sprite, and the dashed display-window edge shown\r\n" +
        "                           while the border is open\r\n" +
        "  Copy/Paste frame icons   Copy this animation frame's full sprite composition\r\n" +
        "                           (positions + Sprite #s, all 8 sprites) to an in-memory\r\n" +
        "                           clipboard, and paste it into any other frame\r\n" +
        "  Multicolour/Hires        Toggles the CURRENTLY EDITED sprite between 4-colour\r\n" +
        "                           double-width pixels and 1-colour double-resolution\r\n" +
        "                           pixels - a per-sprite setting, like a real C64's $d01c";
}
