using System;
using System.IO;
using System.Text.Json;

namespace MagSpriteEd;

/// <summary>
/// Combined project file: the sprite bank's pixel art, the Construct
/// panel's per-frame sprite placements/composition (quadrant, frame
/// offset, keyframed X/Y), and the backdrop picture actually in use -
/// embedded as raw bytes (a .kla is only ~10KB) rather than just a path,
/// so the project still opens correctly if the source file moves or is
/// on another machine. Load/Save Project always saves and restores the
/// whole editing session, not just the pixel art.
/// </summary>
public sealed class ProjectFile
{
    // Version 3: SpriteBank switched from FrameCount/Frames (2x2-block
    // grouped) to PieceCount/Pieces (flat pool) - see SpriteBank.FromData,
    // which migrates an older file's Frames shape on load. Construct's own
    // data (positions/SpriteSource) is unaffected and needs no migration.
    // Version 4: adds Palette (the shared C64 colour registers). Absent in
    // older files - see MainForm.LoadProject's fallback.
    public int Version { get; set; } = 4;
    public SpriteBank.SpriteBankData? Bank { get; set; }
    public ConstructPanel.ConstructPanelData? Construct { get; set; }
    public string? BackdropSourcePath { get; set; }
    public string? BackdropBase64 { get; set; }
    public PaletteData? Palette { get; set; }

    /// <summary>The shared VIC colour registers (C64 colour indexes 0-15).
    /// Individual ($d027+) is per piece and saved with the bank instead.</summary>
    public sealed class PaletteData
    {
        public int Background { get; set; } // $d021
        public int Border { get; set; }     // $d020
        public int Mc1 { get; set; }        // $d025
        public int Mc2 { get; set; }        // $d026
    }

    public static void Save(string path, SpriteBank bank, ConstructPanel constructPanel, BackdropPicture? backdrop)
    {
        var file = new ProjectFile
        {
            Bank = bank.ExportData(),
            Construct = constructPanel.ExportData(),
            BackdropSourcePath = backdrop?.SourcePath,
            BackdropBase64 = backdrop != null ? Convert.ToBase64String(backdrop.RawBytes) : null,
            Palette = new PaletteData
            {
                Background = EditorPalette.BackgroundIndex,
                Border = EditorPalette.BorderIndex,
                Mc1 = EditorPalette.Mc1Index,
                Mc2 = EditorPalette.Mc2Index
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file));
    }

    public readonly struct LoadResult
    {
        public readonly SpriteBank Bank;
        public readonly ConstructPanel.ConstructPanelData? Construct;
        public readonly BackdropPicture? Backdrop;
        public readonly PaletteData? Palette;

        public LoadResult(SpriteBank bank, ConstructPanel.ConstructPanelData? construct, BackdropPicture? backdrop, PaletteData? palette)
        {
            Bank = bank;
            Construct = construct;
            Backdrop = backdrop;
            Palette = palette;
        }
    }

    public static LoadResult Load(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<ProjectFile>(json)
                   ?? throw new InvalidOperationException("Invalid project file: " + path);
        var bank = SpriteBank.FromData(file.Bank ?? throw new InvalidOperationException("Project file has no sprite bank data."));

        BackdropPicture? backdrop = null;
        if (!string.IsNullOrEmpty(file.BackdropBase64))
        {
            var raw = Convert.FromBase64String(file.BackdropBase64);
            backdrop = BackdropPicture.LoadFromBytes(raw, file.BackdropSourcePath ?? "(embedded)");
        }
        return new LoadResult(bank, file.Construct, backdrop, file.Palette);
    }
}
