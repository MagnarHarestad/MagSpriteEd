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
/// shared/reused across frames. The two "quad"-shaped legacy paths below
/// (LoadFromCompiled / ToAsm / ToBinary) still read and write files in that
/// four-per-frame layout purely as an on-disk format convention - nothing
/// about live editing depends on it.
/// </summary>
public sealed class SpriteBank
{
    public const int QuadCols = 12;
    public const int QuadRows = 21;
    public const int SpriteBytes = 64; // 63 pixel-row bytes + 1 pad byte

    // Legacy 2x2-block layout, used only by the compiled-bank import/export
    // paths below (LoadFromCompiled/ToAsm/ToBinary), which still group four
    // pieces per "frame" as a file-format convention inherited from the
    // original m_fire_sprites procedural generator.
    private const int LegacyBlockCols = 24;
    private const int LegacyBlockRows = 42;
    private static readonly (int colOff, int rowOff)[] LegacyQuadOffsets =
        { (0, 0), (LegacyBlockCols / 2, 0), (0, LegacyBlockRows / 2), (LegacyBlockCols / 2, LegacyBlockRows / 2) };

    public int PieceCount { get; private set; }

    private byte[][,] _pixels; // [piece][row, col], row 0..20, col 0..11

    public SpriteBank(int pieceCount)
    {
        if (pieceCount < 1) throw new ArgumentOutOfRangeException(nameof(pieceCount));
        PieceCount = pieceCount;
        _pixels = new byte[pieceCount][,];
        for (int p = 0; p < pieceCount; p++)
            _pixels[p] = new byte[QuadRows, QuadCols];
    }

    public byte Get(int piece, int row, int col) => _pixels[piece][row, col];

    public void Set(int piece, int row, int col, byte value)
    {
        if (value > 3) value = 3;
        _pixels[piece][row, col] = value;
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
        for (int p = 0; p < newPieceCount; p++)
            newPixels[p] = p < PieceCount ? _pixels[p] : new byte[QuadRows, QuadCols];
        _pixels = newPixels;
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
    // Export as assembly text in the same block layout the original Fire.s
    // build expects (label, 21 rows of 3 hex bytes, then the pad byte),
    // grouping the flat pool into fours under magspriteed_frameN_{tl,tr,bl,br}
    // labels. Pads with a blank piece if PieceCount isn't a multiple of 4
    // (nothing about live editing keeps it one any more).
    // ---------------------------------------------------------------------
    public string ToAsm()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Hand-authored MagSpriteEd sprite bank.");
        sb.AppendLine("// Generated by MagSpriteEd (File > Export ASM) - do not hand-edit,");
        sb.AppendLine("// re-export from the editor instead. Included from Startup\\Fire.s's");
        sb.AppendLine("// m_fire_sprites_manual macro when FIRE_SPRITES_MANUAL is defined at build time.");
        sb.AppendLine();
        int legacyFrames = (PieceCount + 3) / 4;
        string[] quadNames = { "tl", "tr", "bl", "br" };
        for (int f = 0; f < legacyFrames; f++)
            for (int qi = 0; qi < 4; qi++)
                EmitSprite(sb, $"magspriteed_frame{f}_{quadNames[qi]}", f * 4 + qi);
        return sb.ToString();
    }

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

    public byte[] ToBinary()
    {
        int legacyFrames = (PieceCount + 3) / 4;
        var data = new byte[legacyFrames * 4 * SpriteBytes];
        int p = 0;
        for (int piece = 0; piece < legacyFrames * 4; piece++)
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
    // piece only once (see ExportOptimizedAsm).
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
    /// the same packing ToAsm() uses, exposed for the deduplicated exporter
    /// in ConstructPanel.</summary>
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

        // Legacy (pre flat-pool) shape - only ever populated by an OLD
        // project file being deserialized, never written by ExportData.
        public int FrameCount { get; set; }
        public byte[][][] Frames { get; set; } = Array.Empty<byte[][]>();
    }

    public SpriteBankData ExportData()
    {
        var data = new SpriteBankData { PieceCount = PieceCount, Pieces = new byte[PieceCount][][] };
        for (int p = 0; p < PieceCount; p++)
        {
            data.Pieces[p] = new byte[QuadRows][];
            for (int r = 0; r < QuadRows; r++)
            {
                data.Pieces[p][r] = new byte[QuadCols];
                for (int c = 0; c < QuadCols; c++)
                    data.Pieces[p][r][c] = _pixels[p][r, c];
            }
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
