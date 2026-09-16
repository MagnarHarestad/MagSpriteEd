# MagSpriteEd

A Windows desktop tool for hand-authoring animated Commodore 64 multicolour
hardware-sprite art: draw a pool of 12x21 sprites, arrange up to 8 hardware
sprites per animation frame over a real C64 backdrop picture, and export
everything as 6502 assembly or a raw binary blob ready to `.byte`-include
into a build.

It was originally built as the in-house sprite tool for a C64 demo's fire
effect (`PlasmaBollLightningBolts3D`), which is why some naming - the
`fire_frameN_{tl,tr,bl,br}` export labels, the `FIRE_SPRITES_MANUAL` /
`FIRE_COMPOSITION_TABLES` build flags - still reflects that origin. The app
itself has no dependency on that project being present: point it at any
compiled `.prg`/`.sym` pair or start from a blank/procedural bank, and it
works entirely standalone.

## Features

- **Pixel editor** - Pencil, flood fill, and line tools for a 12x21
  multicolour sprite (2 bits/pixel: transparent, MC1, Individual, MC2), with
  optional left/right mirroring while you draw.
- **Flat sprite pool** - Any number of independent 12x21 art pieces, fully
  decoupled from how many animation frames exist. Any hardware sprite in any
  frame can reference any piece, so art is reused freely instead of being
  locked to one frame.
- **Construct panel** - Places all 8 hardware sprites over a real Koala
  (`.kla`/`.koa`) backdrop picture. Position (X/Y) and which pool piece each
  sprite shows ("Sprite #") are both keyframed per animation frame, with a
  scrubbable timeline, playback (adjustable FPS, ping-pong), and zoom.
- **Positioned View** - Edit sprite pixels directly in place over the
  backdrop, at whatever position/zoom Construct has set up, instead of only
  in the flat single-sprite view.
- **Copy-on-write editing** - Drawing into a sprite that's shared by more
  than one (frame, hardware sprite) pair automatically forks it into a free
  pool slot first, so you never accidentally repaint art used elsewhere.
- **Undo/redo** - Separate stacks for pixel edits and for
  position/composition changes.
- **Procedural generator** - Seeds the whole pool (or a single piece) with a
  parametric flame shape as a starting point.
- **Project files (`.json`)** - Save/load the sprite bank, the full Construct
  composition, and the backdrop picture (embedded as bytes, so the project
  stays self-contained even if the source `.kla` moves).
- **Import from a compiled build** - Load an existing sprite bank straight
  out of a `.prg` + VICE-format `.sym` pair by locating its
  `fire_frameN_{tl,tr,bl,br}` symbols.
- **Export**
  - ASM source (`MagSpriteEd_Sprites_Data.s`-style), one label per sprite piece.
  - Deduplicated/optimized ASM plus composition tables (positions and
    per-frame Sprite # arrays), for a build that reads the animation back
    out of tables instead of hand-written code.
  - Raw binary (`.bin`).

## Requirements

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download) to build and run

## Build & run

```bash
dotnet build -c Release
dotnet run -c Release
```

or open `MagSpriteEd.csproj` in Visual Studio / Rider and run from there.
The produced executable is `MagSpriteEd.exe`.

## Usage notes

- **Pool vs. Frames**: the toolbar's "Pool" count is how many distinct
  12x21 art pieces exist; "Frames" is how many animation steps the Timeline
  has. They're independent - resizing one never touches the other.
- **Colour shortcuts**: `0`-`3` pick Transparent/MC1/MC2/Individual (matches
  the toolbar's left-to-right order); `Ctrl+Z` / `Ctrl+Y` undo/redo.
- **Palette**: right-click the MC1/MC2/Individual swatches to preview the
  drawing canvas in any of the 16 real C64 colours - this only affects how
  the editor displays those slots, never the exported pixel data.
- **Backdrop**: File menu > "Load Backdrop Picture (.kla)..." accepts any
  Koala-format picture (2-byte load address + 8000 bytes bitmap + 1000
  bytes screen RAM + 1000 bytes colour RAM + background colour byte).
- Exported ASM assumes an assembler/build using `c6510`-style `.byte`
  directives and the label conventions noted above; adjust the including
  source to match if your build pipeline differs.

## License

[MIT](LICENSE)
