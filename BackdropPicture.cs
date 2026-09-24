using System;
using System.Drawing;
using System.IO;

namespace MagSpriteEd;

/// <summary>
/// Decodes a Koala Painter (.kla/.koa) picture into a 320x200 bitmap, for
/// use as the backdrop in the Construct window. Layout matches
/// Startup\Load_Stuff.s's m_LoadKoala exactly (2-byte load address, then
/// 8000 bytes bitmap / 1000 bytes screen RAM / 1000 bytes colour RAM /
/// 1 byte background colour, read via incprg offsets 0/8000/9000) - the
/// same file this project's build itself loads into addr.picture.bitmap
/// /screenram/colorram ($4000/$6000/$6400).
/// </summary>
public sealed class BackdropPicture
{
    public const int Width = 320;
    public const int Height = 200;

    // Real C64 palette (same table used by the project's own verification
    // renders), indexed 0-15.
    public static readonly Color[] Palette =
    {
        Color.FromArgb(0, 0, 0),       Color.FromArgb(255, 255, 255), Color.FromArgb(129, 51, 43),  Color.FromArgb(112, 190, 201),
        Color.FromArgb(127, 57, 141),  Color.FromArgb(95, 171, 72),   Color.FromArgb(60, 42, 146),   Color.FromArgb(214, 225, 132),
        Color.FromArgb(133, 76, 27),   Color.FromArgb(85, 56, 0),     Color.FromArgb(175, 101, 94),  Color.FromArgb(80, 80, 80),
        Color.FromArgb(120, 120, 120), Color.FromArgb(159, 224, 128), Color.FromArgb(115, 99, 213),  Color.FromArgb(159, 159, 159)
    };

    /// <summary>The decoded 320x200 picture. Pixels using the "00" bit pair
    /// are left fully transparent rather than baked to the file's background
    /// colour - on the C64 those pixels ARE $d021, so views fill the display
    /// area with EditorPalette.BackgroundColor first and draw this over it.</summary>
    public Bitmap Image { get; }
    public string SourcePath { get; }
    /// <summary>The raw .kla file bytes - kept so a saved project can embed
    /// the backdrop directly (it's only ~10KB) instead of just referencing
    /// a path that might not exist when the project is reopened.</summary>
    public byte[] RawBytes { get; }
    /// <summary>The file's own background colour byte (0-15) - what a real
    /// Koala loader would write to $d021.</summary>
    public int BackgroundIndex { get; }

    private BackdropPicture(Bitmap image, string sourcePath, byte[] rawBytes, int backgroundIndex)
    {
        Image = image;
        SourcePath = sourcePath;
        RawBytes = rawBytes;
        BackgroundIndex = backgroundIndex;
    }

    public static BackdropPicture Load(string path) => Decode(File.ReadAllBytes(path), path);

    /// <summary>Decodes a backdrop already held in memory - used when
    /// restoring one embedded in a saved project file.</summary>
    public static BackdropPicture LoadFromBytes(byte[] raw, string sourcePath) => Decode(raw, sourcePath);

    private static BackdropPicture Decode(byte[] raw, string sourcePath)
    {
        const int off = 2; // 2-byte load address header
        if (raw.Length < off + 8000 + 1000 + 1000)
            throw new InvalidDataException(
                $"'{Path.GetFileName(sourcePath)}' is too small to be a Koala-format picture " +
                $"({raw.Length} bytes, need at least {off + 8000 + 1000 + 1000}).");

        var bitmap = new byte[8000];
        var screen = new byte[1000];
        var color = new byte[1000];
        Array.Copy(raw, off, bitmap, 0, 8000);
        Array.Copy(raw, off + 8000, screen, 0, 1000);
        Array.Copy(raw, off + 9000, color, 0, 1000);
        byte bg = raw.Length >= off + 10000 + 1 ? raw[off + 10000] : (byte)0;

        var bmp = new Bitmap(Width, Height);
        for (int cy = 0; cy < 25; cy++)
        {
            for (int cx = 0; cx < 40; cx++)
            {
                byte s = screen[cy * 40 + cx];
                byte c = (byte)(color[cy * 40 + cx] & 0x0F);
                byte c01 = (byte)((s >> 4) & 0x0F);
                byte c10 = (byte)(s & 0x0F);
                for (int r = 0; r < 8; r++)
                {
                    int y = cy * 8 + r;
                    if (y >= Height) continue;
                    byte b = bitmap[cy * 320 + cx * 8 + r];
                    for (int p = 0; p < 4; p++)
                    {
                        int v = (b >> (6 - 2 * p)) & 3;
                        Color col = v == 0 ? Color.Transparent : Palette[v == 1 ? c01 : v == 2 ? c10 : c];
                        int x = 2 * (cx * 4 + p);
                        if (x < Width) bmp.SetPixel(x, y, col);
                        if (x + 1 < Width) bmp.SetPixel(x + 1, y, col);
                    }
                }
            }
        }
        return new BackdropPicture(bmp, sourcePath, raw, bg & 0x0F);
    }
}
