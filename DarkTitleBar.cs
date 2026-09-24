using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MagSpriteEd;

/// <summary>
/// Black title bar with white caption text, matching the app's dark UI.
/// The title bar is drawn by Windows (DWM), not WinForms, so BackColor
/// can't reach it - it has to be requested per window once its handle
/// exists (call from OnHandleCreated).
/// </summary>
internal static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 before 20H1
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Windows 10 20H1+ / 11
    private const int DWMWA_CAPTION_COLOR = 35;               // Windows 11 only
    private const int DWMWA_TEXT_COLOR = 36;                  // Windows 11 only

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Form form)
    {
        IntPtr hwnd = form.Handle;
        // Dark mode gives a near-black bar with white text on Windows 10 and 11.
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
        // Windows 11 can go further: exact colours (COLORREF = 0x00BBGGRR).
        // Windows 10 rejects these attributes, which is harmless.
        int black = 0x00000000, white = 0x00FFFFFF;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref black, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref white, sizeof(int));
    }
}
