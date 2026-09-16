using System;
using System.Drawing;
using System.Windows.Forms;

namespace FireSpriteEditor;

/// <summary>
/// Small borderless popup showing all 16 real C64 colours in a 4x4 grid,
/// for picking a replacement colour for one of the flat editor's palette
/// slots. Closes itself on selection, on Escape, or when it loses focus
/// (e.g. the user clicks elsewhere).
/// </summary>
internal sealed class Palette16Popup : Form
{
    public event Action<int>? ColorPicked;

    private const int SwatchSize = 26;
    private const int Pad = 6;

    public Palette16Popup()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(24, 24, 24);
        Size = new Size(Pad * 2 + 4 * SwatchSize, Pad * 2 + 4 * SwatchSize);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Deactivate += (_, _) => Close();

        for (int i = 0; i < 16; i++)
        {
            int idx = i;
            int col = i % 4, row = i / 4;
            var sw = new Panel
            {
                BackColor = BackdropPicture.Palette[i],
                Size = new Size(SwatchSize - 3, SwatchSize - 3),
                Location = new Point(Pad + col * SwatchSize, Pad + row * SwatchSize),
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Hand
            };
            var tip = new ToolTip();
            tip.SetToolTip(sw, $"C64 colour {i}");
            sw.Click += (_, _) => { ColorPicked?.Invoke(idx); Close(); };
            Controls.Add(sw);
        }
    }
}
