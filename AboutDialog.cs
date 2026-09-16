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
        ClientSize = new Size(480, 520);

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
            MaximumSize = new Size(440, 0),
            Margin = new Padding(0, 4, 0, 0),
            Text =
                "A tool for hand-authoring animated Commodore 64 multicolour\r\n" +
                "hardware-sprite art: draw a pool of 12x21 sprites, arrange up to\r\n" +
                "8 hardware sprites per animation frame over a real C64 backdrop\r\n" +
                "picture, and export as 6502 assembly or a raw binary blob."
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
            ScrollBars = ScrollBars.Vertical,
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
    }

    private static string BuildShortcutsText() =>
        "DRAWING (Single Sprite View / Positioned View)\r\n" +
        "  Left click / drag        Paint with the selected colour\r\n" +
        "  Right click / drag       Erase (paint Transparent)\r\n" +
        "  0 / 1 / 2 / 3            Select colour (Transparent / MC1 / MC2 / Individual)\r\n" +
        "  Ctrl+Z / Ctrl+Y          Undo / redo pixel edit\r\n" +
        "  Right-click a swatch     Change that swatch's real C64 colour (MC1/MC2/Individual)\r\n" +
        "\r\n" +
        "SPRITE POOL LIST (left of Single Sprite View)\r\n" +
        "  Click a thumbnail        Edit that pool piece\r\n" +
        "\r\n" +
        "POSITIONED VIEW\r\n" +
        "  Middle-click drag        Pan the view\r\n" +
        "  Mouse wheel              Zoom in / out\r\n" +
        "\r\n" +
        "CONSTRUCT PANEL (sprite placement)\r\n" +
        "  Drag                     Move the selected sprite(s)\r\n" +
        "  Shift+drag / Shift+arrow Move by 8 pixels instead of 1\r\n" +
        "  Arrow keys               Nudge the selected sprite(s) by 1 pixel\r\n" +
        "  Ctrl+click               Add/remove a sprite from the selection group\r\n" +
        "  Ctrl+Z / Ctrl+Y          Undo / redo position and Sprite # changes\r\n" +
        "  Double-click a list cell Edit Sprite# / X / Y directly in the sprite table\r\n" +
        "\r\n" +
        "TOOLS\r\n" +
        "  Pencil / Fill / Line     Left = paint, right = erase\r\n" +
        "  Mirror                   Mirrors every stroke left/right while enabled\r\n" +
        "  Multicolour/Hires        Toggles the CURRENTLY EDITED sprite between 4-colour\r\n" +
        "                           double-width pixels and 1-colour double-resolution\r\n" +
        "                           pixels - a per-sprite setting, like a real C64's $d01c";
}
