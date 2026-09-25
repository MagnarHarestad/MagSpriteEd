using System.Reflection;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Turns on double buffering for the plain layout containers (Panel,
/// TableLayoutPanel, FlowLayoutPanel, UserControl) under a root control.
/// WinForms only exposes DoubleBuffered as a protected property, so the
/// stock containers never get it - they erase and repaint straight to the
/// screen, which is what flickers and drags during a window resize or a
/// scroll. Custom-painted controls set it themselves already.
/// </summary>
internal static class DoubleBuffering
{
    private static readonly PropertyInfo? DoubleBufferedProperty =
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void EnableForContainers(Control root)
    {
        if (root is Panel or UserControl) // Panel covers TableLayoutPanel/FlowLayoutPanel
            DoubleBufferedProperty?.SetValue(root, true);
        foreach (Control child in root.Controls)
            EnableForContainers(child);
    }
}
