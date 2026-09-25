using System;
using System.Drawing;

namespace MagSpriteEd;

/// <summary>
/// The C64 colour registers every view renders with - right-click a
/// toolbar swatch to repoint one at any of the 16 real C64 colours:
///   Background = $d021 (what sprite pixel value 0 shows through to, and
///                what a Koala picture's "00" pixels are),
///   Border     = $d020 (Construct's frame around the 320x200 display),
///   MC1 / MC2  = $d025 / $d026, shared by every sprite,
///   Individual = $d027-$d02e, per sprite - lives on each SpriteBank piece
///                (IndividualColor), not here; IndividualIndex is only the
///                default a new piece starts with.
/// Never affects the exports (ToBinary / Export Animation) - those only ever deal in the 0-3
/// pixel-type values, never actual colours.
/// </summary>
internal static class EditorPalette
{
    public static int BackgroundIndex = 0;  // $d021
    public static int BorderIndex = 0;      // $d020
    public static int Mc1Index = 2;         // default: real C64 index 2 (red) - matches Fire_Frame's $d025
    public static int IndividualIndex = 8;  // default: real C64 index 8 (orange) - one of Fire_Frame's two $d027-$d02e values
    public static int Mc2Index = 7;         // default: real C64 index 7 (yellow) - matches Fire_Frame's $d026

    public static event Action? Changed;

    public static Color BackgroundColor => BackdropPicture.Palette[BackgroundIndex];
    public static Color BorderColor => BackdropPicture.Palette[BorderIndex];

    /// <summary>Sprite pixel colour. Value 0 stays Transparent - on the C64 a
    /// sprite's 0 pixels show whatever is behind them (the bitmap or $d021),
    /// so callers that want the background colour itself use BackgroundColor.
    /// Individual (value 2) is per-piece: pass that piece's own C64 colour index.</summary>
    public static Color ColorFor(byte pixelValue, int individualIndex) => pixelValue switch
    {
        1 => BackdropPicture.Palette[Mc1Index],
        2 => BackdropPicture.Palette[individualIndex],
        3 => BackdropPicture.Palette[Mc2Index],
        _ => Color.Transparent
    };

    public static void RaiseChanged() => Changed?.Invoke();

    /// <summary>slot: 0=Background, 1=MC1, 2=Individual, 3=MC2 - the same
    /// numbering as the pixel values the toolbar swatches select.</summary>
    public static void Set(byte slot, int c64ColorIndex)
    {
        switch (slot)
        {
            case 0: BackgroundIndex = c64ColorIndex; break;
            case 1: Mc1Index = c64ColorIndex; break;
            case 2: IndividualIndex = c64ColorIndex; break;
            case 3: Mc2Index = c64ColorIndex; break;
            default: return;
        }
        Changed?.Invoke();
    }

    public static void SetBorder(int c64ColorIndex)
    {
        BorderIndex = c64ColorIndex;
        Changed?.Invoke();
    }

    /// <summary>Restores every shared register at once (e.g. from a saved
    /// project) with a single Changed notification.</summary>
    public static void SetAll(int background, int border, int mc1, int mc2)
    {
        BackgroundIndex = background & 0x0F;
        BorderIndex = border & 0x0F;
        Mc1Index = mc1 & 0x0F;
        Mc2Index = mc2 & 0x0F;
        Changed?.Invoke();
    }
}
