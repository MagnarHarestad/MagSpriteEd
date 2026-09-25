using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MagSpriteEd;

/// <summary>
/// In-memory model of the sprite art: a flat pool of PieceCount
/// independent 12x21 C64 multicolour sprites, each stored as one byte per
/// pixel with values 0..3:
///   0 = transparent, 1 = MC1 (%01, shared $d025 - flame base),
///   2 = individual (%10, per-sprite $d027-$d02e), 3 = MC2 (%11, shared $d026 - flame tip).
/// There is no 2x2-block grouping any more - that was retired once every
/// hardware sprite's Sprite # became an independent per-animation-frame
/// keyframe (see ConstructPanel): grouping raw storage in fours forced the
/// pool's capacity to scale in lockstep with the Timeline's own frame
/// count, which was never actually required once art could be freely
/// shared/reused across frames. The legacy LoadFromCompiled import still
/// reads files in that four-per-frame layout purely as an on-disk format
/// convention - nothing about live editing depends on it.
/// </summary>
public sealed class SpriteBank
{
    public const int QuadCols = 12;
    public const int QuadRows = 21;
    public const int SpriteBytes = 64; // 63 pixel-row bytes + 1 pad byte

    // Legacy 2x2-block layout, used only by the compiled-bank import
    // (LoadFromCompiled) and old project migration, which still group four
    // pieces per "frame" as a file-format convention inherited from the
    // original m_fire_sprites procedural generator.
    private const int LegacyBlockCols = 24;
    private const int LegacyBlockRows = 42;
    private static readonly (int colOff, int rowOff)[] LegacyQuadOffsets =
        { (0, 0), (LegacyBlockCols / 2, 0), (0, LegacyBlockRows / 2), (LegacyBlockCols / 2, LegacyBlockRows / 2) };

    public int PieceCount { get; private set; }

    private byte[][,] _pixels; // [piece][row, col], row 0..20, col 0..11

    // Per-piece hires flag - see IsHires/SetHires and the GetHiresPixel/
    // SetHiresPixel accessors below. Purely an alternate INTERPRETATION of
    // the exact same raw storage above: on real VIC hardware a multicolour
    // sprite's raw bytes are identical in size/layout to a hires sprite's
    // (3 bytes/row either way) - only whether the chip reads 2 bits as one
    // 4-colour pixel or 1 bit as one 2-colour pixel differs, controlled by
    // $d01c per hardware sprite. So flipping this flag never touches
    // _pixels at all, and toggling it back and forth is always lossless -
    // whatever was drawn under the old interpretation is simply read back
    // under the new one (see GetHiresPixel's bit packing).
    private bool[] _hires;

    // Per-piece "Individual" colour (real C64 palette index) - purely how
    // the editor DISPLAYS that piece (on hardware it's a per-sprite $d027+
    // register), never part of the exported pixel data.
    public const int DefaultIndividualColor = 8;
    private int[] _individualColor;

    public SpriteBank(int pieceCount)
    {
        if (pieceCount < 1) throw new ArgumentOutOfRangeException(nameof(pieceCount));
        PieceCount = pieceCount;
        _pixels = new byte[pieceCount][,];
        _hires = new bool[pieceCount];
        _individualColor = Enumerable.Repeat(DefaultIndividualColor, pieceCount).ToArray();
        for (int p = 0; p < pieceCount; p++)
            _pixels[p] = new byte[QuadRows, QuadCols];
    }

    public byte Get(int piece, int row, int col) => _pixels[piece][row, col];

    public void Set(int piece, int row, int col, byte value)
    {
        if (value > 3) value = 3;
        _pixels[piece][row, col] = value;
    }

    public int IndividualColor(int piece) => _individualColor[piece];
    public void SetIndividualColor(int piece, int c64Index) => _individualColor[piece] = c64Index;

    public bool IsHires(int piece) => _hires[piece];
    public void SetHires(int piece, bool hires) => _hires[piece] = hires;

    // ---------------------------------------------------------------------
    // Hires bit accessors - 24 single-bit columns (0=transparent, 1=the
    // sprite's one colour, from the same per-sprite $d027-$d02e register
    // "Individual" already uses) instead of 12 double-width 2-bit columns.
    // Each existing 0-3 cell packs its 2 raw bits as two independent hires
    // pixels: the high bit (value 2) is the spatially LEFT one, the low bit
    // (value 1) the RIGHT one - the exact same bit order EmitSprite/
    // LoadFromCompiled already pack MSB-first, so no export/import code
    // needs to change: a hires piece's raw bytes are already correct the
    // moment its pixels are drawn this way.
    // ---------------------------------------------------------------------
    public const int HiresCols = QuadCols * 2;

    public byte GetHiresPixel(int piece, int row, int col24)
    {
        byte cell = _pixels[piece][row, col24 / 2];
        bool left = (col24 & 1) == 0;
        return (byte)(left ? (cell >> 1) & 1 : cell & 1);
    }

    public void SetHiresPixel(int piece, int row, int col24, byte value)
    {
        int col12 = col24 / 2;
        bool left = (col24 & 1) == 0;
        byte cell = _pixels[piece][row, col12];
        byte bit = (byte)(value & 1);
        cell = left ? (byte)((cell & 0b01) | (bit << 1)) : (byte)((cell & 0b10) | bit);
        _pixels[piece][row, col12] = cell;
    }

    /// <summary>
    /// Builds a bank from a PNG sprite sheet. The image is a grid of 24x21
    /// multicolour sprites (each 12x21 cell is drawn 2 px wide, as C64 MC
    /// pixels are), read left-to-right then top-to-bottom. Black (or fully
    /// transparent) is transparent; the up-to-3 other colours become
    /// MC1/Individual/MC2 in order of first appearance. slotColors[1..3]
    /// returns the nearest real C64 palette index for each slot.
    /// </summary>
    public static SpriteBank LoadFromPng(string path, out int[] slotColors, out string log)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        const int SheetSpriteW = QuadCols * 2;
        if (bmp.Width % SheetSpriteW != 0 || bmp.Height % QuadRows != 0)
            throw new InvalidDataException($"PNG is {bmp.Width}x{bmp.Height}; width must be a multiple of {SheetSpriteW} and height a multiple of {QuadRows}.");

        int perRow = bmp.Width / SheetSpriteW, rows = bmp.Height / QuadRows;
        var slotOf = new Dictionary<int, byte>();
        var slotRgb = new int[4];
        var bank = new SpriteBank(perRow * rows);
        for (int p = 0; p < bank.PieceCount; p++)
        {
            int ox = p % perRow * SheetSpriteW, oy = p / perRow * QuadRows;
            for (int r = 0; r < QuadRows; r++)
                for (int c = 0; c < QuadCols; c++)
                {
                    var a = bmp.GetPixel(ox + c * 2, oy + r);
                    var b = bmp.GetPixel(ox + c * 2 + 1, oy + r);
                    if (a.ToArgb() != b.ToArgb())
                        throw new InvalidDataException($"Sprite {p}: pixels at ({ox + c * 2},{oy + r}) are not doubled horizontally (multicolour pixels must be 2 px wide).");
                    if (a.A < 128 || (a.R | a.G | a.B) == 0) continue;
                    int rgb = a.ToArgb() & 0xFFFFFF;
                    if (!slotOf.TryGetValue(rgb, out byte slot))
                    {
                        if (slotOf.Count == 3)
                            throw new InvalidDataException("PNG uses more than 3 non-transparent colours (plus black).");
                        slot = (byte)(slotOf.Count + 1);
                        slotOf[rgb] = slot;
                        slotRgb[slot] = rgb;
                    }
                    bank.Set(p, r, c, slot);
                }
        }

        slotColors = new int[4];
        for (int s = 1; s <= 3; s++)
        {
            int best = 0, bestD = int.MaxValue;
            for (int i = 0; i < 16; i++)
            {
                var pc = BackdropPicture.Palette[i];
                int dr = pc.R - ((slotRgb[s] >> 16) & 255), dg = pc.G - ((slotRgb[s] >> 8) & 255), db = pc.B - (slotRgb[s] & 255);
                int d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            slotColors[s] = best;
        }
        log = $"Loaded {bank.PieceCount} sprites from {Path.GetFileName(path)} ({slotOf.Count} colours).";
        return bank;
    }

    public byte[,] Piece(int piece) => _pixels[piece];

    public byte[,] ClonePiece(int piece)
    {
        var src = _pixels[piece];
        var dst = new byte[QuadRows, QuadCols];
        Array.Copy(src, dst, src.Length);
        return dst;
    }

    public void RestorePiece(int piece, byte[,] snapshot)
    {
        Array.Copy(snapshot, _pixels[piece], snapshot.Length);
    }

    public void ClearPiece(int piece) => _pixels[piece] = new byte[QuadRows, QuadCols];

    public void Resize(int newPieceCount)
    {
        if (newPieceCount < 1) newPieceCount = 1;
        var newPixels = new byte[newPieceCount][,];
        var newHires = new bool[newPieceCount];
        var newInd = new int[newPieceCount];
        for (int p = 0; p < newPieceCount; p++)
        {
            newPixels[p] = p < PieceCount ? _pixels[p] : new byte[QuadRows, QuadCols];
            newHires[p] = p < PieceCount && _hires[p];
            newInd[p] = p < PieceCount ? _individualColor[p] : DefaultIndividualColor;
        }
        _pixels = newPixels;
        _hires = newHires;
        _individualColor = newInd;
        PieceCount = newPieceCount;
    }

    // ---------------------------------------------------------------------
    // Procedural seed - adapted from Fire.s's lua pixel() function to one
    // independent 12x21 piece (the original operated on a 24x42 2x2 block,
    // sharing one phase across all four quadrants so the block read as one
    // coherent swirl slice - pieces no longer have that grouping, so this
    // just gives each piece its own phased flame silhouette). A starting
    // point for hand-editing, not a byte-exact match to the old shipped
    // shape any more.
    // ---------------------------------------------------------------------
    public static byte ProceduralPixel(int col, int row, double phase)
    {
        double shear = Math.Sin(row * 0.22 + phase) * 1.6;
        double sc = FloorMod(col - shear, QuadCols);
        double h = (0.4 + 0.32 * Math.Sin(sc * 0.7 + phase)) * QuadRows;
        double rowFromBase = (QuadRows - 1) - row;
        if (rowFromBase > h) return 0;
        double frac = h > 0 ? rowFromBase / h : 0;
        if (frac > 0.72) return 3;
        if (frac > 0.32) return 2;
        return 1;
    }

    private static double FloorMod(double a, double m)
    {
        double r = a % m;
        return r < 0 ? r + m : r;
    }

    public void GenerateProceduralPiece(int piece)
    {
        // The flame silhouette is inherently a multicolour pattern (it
        // uses all 3 non-transparent values) - resetting "to procedural"
        // always returns a piece to that default, rather than leaving it
        // hires and reinterpreting these bytes as scrambled hires pixels.
        _hires[piece] = false;
        double phase = piece * (2 * Math.PI / Math.Max(1, PieceCount));
        var grid = _pixels[piece];
        for (int row = 0; row < QuadRows; row++)
            for (int col = 0; col < QuadCols; col++)
                grid[row, col] = ProceduralPixel(col, row, phase);
    }

    public void GenerateProceduralAll()
    {
        for (int p = 0; p < PieceCount; p++)
            GenerateProceduralPiece(p);
    }

    // ---------------------------------------------------------------------
    // Load the real compiled sprite bank: a .prg (raw bytes + 2-byte load
    // address) plus the matching .sym (VICE-format "add_label <hex> .<name>"
    // lines) produced by the same c6510 build. Locates every
    // magspriteed_frame{N}_{tl,tr,bl,br} label (or the legacy fire_frame{N}_*
    // naming a build from before this tool's rename still uses) and decodes
    // its 64-byte block back into a piece at index N*4+{0,1,2,3}, using the
    // exact inverse of emit_sprite's packing
    // (byte = c0<<6 | c1<<4 | c2<<2 | c3, MSB-first, 3 bytes/row).
    // ---------------------------------------------------------------------
    public static SpriteBank LoadFromCompiled(string prgPath, string symPath, out string log)
    {
        var mem = new byte[65536];
        var bytes = File.ReadAllBytes(prgPath);
        if (bytes.Length < 3) throw new InvalidDataException("PRG file is too small to contain a load address and data.");
        int loadAddr = bytes[0] | (bytes[1] << 8);
        int dataLen = bytes.Length - 2;
        for (int i = 0; i < dataLen; i++)
            mem[(loadAddr + i) & 0xFFFF] = bytes[2 + i];

        var symbols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(symPath))
        {
            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) continue;
            if (parts[0] != "add_label") continue;
            if (parts[2].Length < 2 || parts[2][0] != '.') continue;
            if (!int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int addr)) continue;
            symbols[parts[2].Substring(1)] = addr;
        }

        // Accepts both this tool's current export prefix and the legacy
        // "fire_" prefix a build compiled before the rename still has baked
        // into its .sym, so an older compiled bank still imports correctly.
        var re = new Regex(@"^(?:magspriteed|fire)_frame(\d+)_(tl|tr|bl|br)$", RegexOptions.IgnoreCase);
        var found = new Dictionary<(int frame, string quad), int>();
        int maxFrame = -1;
        foreach (var kv in symbols)
        {
            var m = re.Match(kv.Key);
            if (!m.Success) continue;
            int f = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string quad = m.Groups[2].Value.ToLowerInvariant();
            found[(f, quad)] = kv.Value;
            if (f > maxFrame) maxFrame = f;
        }

        if (maxFrame < 0)
            throw new InvalidOperationException(
                "No magspriteed_frameN_{tl,tr,bl,br} (or legacy fire_frameN_*) symbols found in " +
                Path.GetFileName(symPath) + " - is this the .sym produced alongside " +
                Path.GetFileName(prgPath) + "?");

        int pieceCount = (maxFrame + 1) * 4;
        var bank = new SpriteBank(pieceCount);
        int missing = 0;
        string[] quads = { "tl", "tr", "bl", "br" };
        foreach (int f in Enumerable.Range(0, maxFrame + 1))
        {
            for (int qi = 0; qi < 4; qi++)
            {
                string quad = quads[qi];
                if (!found.TryGetValue((f, quad), out int addr)) { missing++; continue; }
                int piece = f * 4 + qi;
                for (int row = 0; row < QuadRows; row++)
                {
                    int rowBase = addr + row * 3;
                    for (int b = 0; b < 3; b++)
                    {
                        byte bv = mem[(rowBase + b) & 0xFFFF];
                        for (int i = 0; i < 4; i++)
                        {
                            int col = b * 4 + i;
                            byte val = (byte)((bv >> (6 - 2 * i)) & 3);
                            bank._pixels[piece][row, col] = val;
                        }
                    }
                }
            }
        }

        log = $"Loaded {pieceCount} sprite piece(s) ({maxFrame + 1} legacy frames x 4) from " +
              $"{Path.GetFileName(prgPath)} @ {Path.GetFileName(symPath)} ({pieceCount * SpriteBytes} bytes)";
        if (missing > 0)
            log += $"  -- {missing} sprite label(s) not found, left blank";
        return bank;
    }

    // ---------------------------------------------------------------------
    // Sprite packing in the C64's own format: 21 rows of 3 bytes (4 two-bit
    // multicolour pixels per byte, MSB first - the same bytes are 8 hires
    // pixels per byte for a hires piece), then 1 pad byte = 64 bytes.
    // ---------------------------------------------------------------------
    private byte[,] PieceOrBlank(int piece) => piece < PieceCount ? _pixels[piece] : new byte[QuadRows, QuadCols];

    private void EmitSprite(StringBuilder sb, string label, int piece)
    {
        sb.AppendLine(label + ":");
        var grid = PieceOrBlank(piece);
        for (int row = 0; row < QuadRows; row++)
        {
            var rowBytes = new byte[3];
            for (int b = 0; b < 3; b++)
            {
                int v = 0;
                for (int i = 0; i < 4; i++)
                {
                    int col = b * 4 + i;
                    byte p = grid[row, col];
                    v = v * 4 + p;
                }
                rowBytes[b] = (byte)v;
            }
            sb.AppendLine($"        .byte ${rowBytes[0]:X2},${rowBytes[1]:X2},${rowBytes[2]:X2}");
        }
        sb.AppendLine("        .byte 0   ; pad to 64");
    }

    /// <summary>Every sprite in the pool, in pool order, 64 bytes each - the
    /// raw C64 sprite format (Menu > Export Spritebank). Sprite n starts at
    /// offset n*64, so with the data at a 64-byte-aligned address its
    /// sprite pointer is simply base/64 + n.</summary>
    public byte[] ToBinary()
    {
        var data = new byte[PieceCount * SpriteBytes];
        int p = 0;
        for (int piece = 0; piece < PieceCount; piece++)
            p = PackSprite(data, p, piece);
        return data;
    }

    private int PackSprite(byte[] data, int p, int piece)
    {
        var grid = PieceOrBlank(piece);
        for (int row = 0; row < QuadRows; row++)
        {
            for (int b = 0; b < 3; b++)
            {
                int v = 0;
                for (int i = 0; i < 4; i++)
                {
                    int col = b * 4 + i;
                    v = v * 4 + grid[row, col];
                }
                data[p++] = (byte)v;
            }
        }
        data[p++] = 0; // pad to 64
        return p;
    }

    // ---------------------------------------------------------------------
    // Deduplication - identical 12x21 content stored at different piece
    // indices is recognized as one logical piece. Used both to give the
    // Construct UI a dense, jump-free "Sprite #" (see ConstructPanel) and
    // to shrink the exported ASM/binary output by emitting each distinct
    // piece only once (see ConstructPanel.ExportAnimation).
    // ---------------------------------------------------------------------
    public sealed class DedupMap
    {
        /// <summary>[piece index] -> canonical index.</summary>
        public int[] SlotToCanonical = Array.Empty<int>();
        /// <summary>canonical index -> the FIRST piece index with that content.</summary>
        public int[] CanonicalToSlot = Array.Empty<int>();
        public int CanonicalCount => CanonicalToSlot.Length;
    }

    private byte[] PieceContent(int piece)
    {
        var grid = _pixels[piece];
        var data = new byte[QuadRows * QuadCols];
        int i = 0;
        for (int r = 0; r < QuadRows; r++)
            for (int c = 0; c < QuadCols; c++)
                data[i++] = grid[r, c];
        return data;
    }

    public DedupMap BuildDedupMap() => BuildDedupMap(Enumerable.Range(0, PieceCount));

    /// <summary>Deduplicates only the given piece indices, ignoring
    /// everything else in the pool - used by ConstructPanel's optimized
    /// export so pieces nothing currently references (orphaned pool
    /// growth - a deleted frame's old Sprite #, a copy-on-write fork that
    /// got edited away from again, the pool simply being resized larger
    /// than the Timeline ever ended up using) are dropped from the
    /// compiled output entirely, instead of still being emitted as their
    /// own "unique" sprite just because content-only dedup over the WHOLE
    /// pool has no way to know they're unused. SlotToCanonical is sized to
    /// PieceCount as usual; entries for pieces outside `onlyPieces` are
    /// left at -1 (never a valid canonical index) rather than silently
    /// aliasing canonical 0, so a caller that forgets to restrict its own
    /// lookups to the same set fails loudly instead of exporting wrong art.</summary>
    public DedupMap BuildDedupMap(IEnumerable<int> onlyPieces)
    {
        var slotToCanonical = new int[PieceCount];
        Array.Fill(slotToCanonical, -1);
        var canonicalToSlot = new List<int>();
        var seen = new Dictionary<string, int>();

        foreach (int piece in onlyPieces)
        {
            if (slotToCanonical[piece] != -1) continue; // onlyPieces may contain duplicates
            string key = Convert.ToBase64String(PieceContent(piece));
            if (seen.TryGetValue(key, out int canon))
            {
                slotToCanonical[piece] = canon;
            }
            else
            {
                int newCanon = canonicalToSlot.Count;
                canonicalToSlot.Add(piece);
                seen[key] = newCanon;
                slotToCanonical[piece] = newCanon;
            }
        }
        return new DedupMap { SlotToCanonical = slotToCanonical, CanonicalToSlot = canonicalToSlot.ToArray() };
    }

    /// <summary>Emits one piece's ASM block under a caller-chosen label -
    /// the same packing ToBinary() uses, as .byte rows - for ConstructPanel's
    /// Export Animation.</summary>
    public void EmitSpriteAsmBlock(StringBuilder sb, string label, int piece) => EmitSprite(sb, label, piece);

    // ---------------------------------------------------------------------
    // Project persistence - in-memory DTO only. MainForm's ProjectFile owns
    // the actual file I/O, so a saved project can also carry the Construct
    // panel's sprite placements and backdrop alongside this pixel data.
    // Keeps both the current flat-pool shape (PieceCount/Pieces) and the
    // old 2x2-block shape (FrameCount/Frames, pre-decoupling project files)
    // so FromData can migrate an old project instead of silently discarding
    // it - see FromData.
    // ---------------------------------------------------------------------
    public sealed class SpriteBankData
    {
        public int PieceCount { get; set; }
        public byte[][][] Pieces { get; set; } = Array.Empty<byte[][]>();

        // Per-piece hires flag (see SpriteBank.IsHires). Absent/shorter than
        // PieceCount in an older project file - FromData below just leaves
        // those pieces at their default false (multicolour), matching what
        // every piece already effectively was before this flag existed.
        public bool[] Hires { get; set; } = Array.Empty<bool>();

        // Per-piece Individual colour (C64 palette index); absent in older files.
        public int[] IndividualColors { get; set; } = Array.Empty<int>();

        // Legacy (pre flat-pool) shape - only ever populated by an OLD
        // project file being deserialized, never written by ExportData.
        public int FrameCount { get; set; }
        public byte[][][] Frames { get; set; } = Array.Empty<byte[][]>();
    }

    public SpriteBankData ExportData()
    {
        var data = new SpriteBankData { PieceCount = PieceCount, Pieces = new byte[PieceCount][][], Hires = new bool[PieceCount], IndividualColors = new int[PieceCount] };
        for (int p = 0; p < PieceCount; p++)
        {
            data.Pieces[p] = new byte[QuadRows][];
            for (int r = 0; r < QuadRows; r++)
            {
                data.Pieces[p][r] = new byte[QuadCols];
                for (int c = 0; c < QuadCols; c++)
                    data.Pieces[p][r][c] = _pixels[p][r, c];
            }
            data.Hires[p] = _hires[p];
            data.IndividualColors[p] = _individualColor[p];
        }
        return data;
    }

    public static SpriteBank FromData(SpriteBankData data)
    {
        if (data.Pieces.Length > 0 || data.PieceCount > 0)
        {
            var bank = new SpriteBank(Math.Max(1, data.PieceCount));
            for (int p = 0; p < data.PieceCount && p < data.Pieces.Length; p++)
                for (int r = 0; r < QuadRows; r++)
                    for (int c = 0; c < QuadCols; c++)
                        bank._pixels[p][r, c] = data.Pieces[p][r][c];
            for (int p = 0; p < data.PieceCount && p < data.Hires.Length; p++)
                bank._hires[p] = data.Hires[p];
            for (int p = 0; p < data.PieceCount && p < data.IndividualColors.Length; p++)
                bank._individualColor[p] = data.IndividualColors[p];
            return bank;
        }

        // Old project file (saved before the flat-pool refactor): Frames[f]
        // is a 42x24 2x2 block - split each into 4 pieces at index
        // f*4+{tl,tr,bl,br}, the exact same numbering "Sprite #"/SpriteSource
        // already used as a flat bankFrame*4+quad index, so
        // ConstructPanelData needs no migration at all, only this shape does.
        int frameCount = Math.Max(1, data.FrameCount);
        var migrated = new SpriteBank(frameCount * 4);
        for (int f = 0; f < frameCount && f < data.Frames.Length; f++)
        {
            for (int qi = 0; qi < 4; qi++)
            {
                var (colOff, rowOff) = LegacyQuadOffsets[qi];
                int piece = f * 4 + qi;
                for (int r = 0; r < QuadRows; r++)
                    for (int c = 0; c < QuadCols; c++)
                        migrated._pixels[piece][r, c] = data.Frames[f][rowOff + r][colOff + c];
            }
        }
        return migrated;
    }
}
