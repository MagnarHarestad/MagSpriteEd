using System;
using System.Drawing;

namespace MagSpriteEd;

/// <summary>
/// Customizable colour assignment for the flat pixel editor's 3 real
/// colour slots (MC1/Individual/MC2) - right-click a toolbar swatch to
/// repoint it at any of the 16 real C64 colours. Deliberately scoped to
/// the drawing workflow only: Construct and the Positioned view keep
/// rendering the actual hardware-accurate values Fire_Frame writes
/// ($d025=red, $d026=yellow, $d027-$d02e alternating 8/10), so there's
/// always one place showing ground truth even while this is set to
/// something else for comfort while drawing. Never affects ToAsm()/
/// ToBinary() - those only ever deal in the 0-3 pixel-type values, never
/// actual colours.
/// </summary>
internal static class EditorPalette
{
    public static int Mc1Index = 2;         // default: real C64 index 2 (red) - matches Fire_Frame's $d025
    public static int IndividualIndex = 8;  // default: real C64 index 8 (orange) - one of Fire_Frame's two $d027-$d02e values
    public static int Mc2Index = 7;         // default: real C64 index 7 (yellow) - matches Fire_Frame's $d026

    public static event Action? Changed;

    public static Color ColorFor(byte pixelValue) => pixelValue switch
    {
        1 => BackdropPicture.Palette[Mc1Index],
        2 => BackdropPicture.Palette[IndividualIndex],
        3 => BackdropPicture.Palette[Mc2Index],
        _ => Color.Transparent
    };

    /// <summary>slot: 1=MC1, 2=Individual, 3=MC2 (0=Transparent isn't a real colour register).</summary>
    public static void Set(byte slot, int c64ColorIndex)
    {
        switch (slot)
        {
            case 1: Mc1Index = c64ColorIndex; break;
            case 2: IndividualIndex = c64ColorIndex; break;
            case 3: Mc2Index = c64ColorIndex; break;
            default: return;
        }
        Changed?.Invoke();
    }
}
