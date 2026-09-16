using System;
using System.IO;
using System.Text.Json;

namespace FireSpriteEditor;

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
    public int Version { get; set; } = 3;
    public SpriteBank.SpriteBankData? Bank { get; set; }
    public ConstructPanel.ConstructPanelData? Construct { get; set; }
    public string? BackdropSourcePath { get; set; }
    public string? BackdropBase64 { get; set; }

    public static void Save(string path, SpriteBank bank, ConstructPanel constructPanel, BackdropPicture? backdrop)
    {
        var file = new ProjectFile
        {
            Bank = bank.ExportData(),
            Construct = constructPanel.ExportData(),
            BackdropSourcePath = backdrop?.SourcePath,
            BackdropBase64 = backdrop != null ? Convert.ToBase64String(backdrop.RawBytes) : null
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file));
    }

    public readonly struct LoadResult
    {
        public readonly SpriteBank Bank;
        public readonly ConstructPanel.ConstructPanelData? Construct;
        public readonly BackdropPicture? Backdrop;

        public LoadResult(SpriteBank bank, ConstructPanel.ConstructPanelData? construct, BackdropPicture? backdrop)
        {
            Bank = bank;
            Construct = construct;
            Backdrop = backdrop;
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
        return new LoadResult(bank, file.Construct, backdrop);
    }
}
