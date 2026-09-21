using System.Collections.Generic;
using System.Drawing;

namespace MagSpriteEd;

/// <summary>
/// Shared SolidBrush cache for the per-cell pixel painting in the canvases
/// and thumbnails - allocating and disposing a brush for every cell of every
/// sprite on every repaint made drawing (especially while dragging) sluggish.
/// The colour set is tiny (16 C64 colours plus a few UI greys), so brushes
/// are simply kept for the life of the app. UI thread only.
/// </summary>
internal static class BrushCache
{
    private static readonly Dictionary<int, SolidBrush> Brushes = new();

    public static SolidBrush Get(Color color)
    {
        int key = color.ToArgb();
        if (!Brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(color);
            Brushes[key] = brush;
        }
        return brush;
    }
}
