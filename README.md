# MagSpriteEd

A Windows desktop tool for hand-authoring animated Commodore 64 multicolour
hardware-sprite art: draw a pool of 12x21 sprites, arrange up to 8 hardware
sprites per animation frame over a real C64 backdrop picture, and export
everything as 6502 assembly or a raw binary blob ready to `.byte`-include
into a build.

It was originally built as the in-house sprite tool for a C64 demo's fire
effect (`PlasmaBollLightningBolts3D`), which is why the `FIRE_SPRITES_MANUAL` /
`FIRE_COMPOSITION_TABLES` build flags mentioned below still reflect that
origin - they're the actual flags that demo's own assembly source expects,
not something this tool can rename on its own. The app itself has no
dependency on that project being present, though: point it at any compiled
`.prg`/`.sym` pair or start from a blank bank, and it works
entirely standalone. Its own export labels use a `magspriteed_frameN_*`
naming convention; loading a `.prg`/`.sym` compiled before this tool's
rename (still using the legacy `fire_frameN_*` labels) is also supported.

## Features

- **Pixel editor** - Pencil, flood fill, and line tools for a 12x21
  multicolour sprite (2 bits/pixel: background, MC1, Individual, MC2), with
  optional left/right mirroring while you draw.
- **Multicolour/hires per sprite** - Any pool piece can independently be
  switched to hires (1 colour, double horizontal resolution - 24x21 instead
  of 12x21), matching the real VIC's per-sprite $d01c bit. Toggled from the
  toolbar for whichever piece Single Sprite View currently has open.
- **Flat sprite pool** - Any number of independent 12x21 art pieces, fully
  decoupled from how many animation frames exist. Any hardware sprite in any
  frame can reference any piece, so art is reused freely instead of being
  locked to one frame.
- **Construct panel** - Places all 8 hardware sprites over a real Koala
  (`.kla`/`.koa`) backdrop picture. Position (X/Y) and which pool piece each
  sprite shows ("Sprite #") are both keyframed per animation frame, with a
  scrubbable timeline, playback (adjustable FPS, ping-pong), and zoom.
- **Draw in Construct** - Paint sprite pixels directly in place over the
  backdrop, not only in the zoomed single-sprite view. Plain drag draws,
  Shift+click selects, Shift+drag (or dragging a sprite's label) moves,
  Ctrl+click groups.
- **Copy-on-write editing** - Drawing into a sprite that's shared by more
  than one (frame, hardware sprite) pair automatically forks it into a free
  pool slot first, so you never accidentally repaint art used elsewhere.
- **Undo/redo** - One time-ordered history covering both pixel edits and
  position/composition changes.
- **Procedural starting bank** - The app opens with a pool seeded with a
  parametric flame shape as a starting point (Menu > New Blank Bank clears it).
- **Project files (`.json`)** - Save/load the sprite bank, the full Construct
  composition, and the backdrop picture (embedded as bytes, so the project
  stays self-contained even if the source `.kla` moves).
- **Import from a compiled build** - Load an existing sprite bank straight
  out of a `.prg` + VICE-format `.sym` pair by locating its
  `magspriteed_frameN_{tl,tr,bl,br}` (or legacy `fire_frameN_{tl,tr,bl,br}`)
  symbols.
- **Export**
  - **Export Spritebank** - every sprite in the pool as one raw binary
    (`.bin`), 64 bytes per sprite in the C64's own sprite format (21 rows
    x 3 bytes + 1 pad byte), in pool order.
  - **Export Animation** - the whole Construct animation as ASM: only the
    sprites the Timeline actually uses, deduplicated, plus per-frame tables
    (Sprite # pointers, X/Y positions, `$d010`, a hires/multicolour flag per
    sprite, and ping-pong), for a build that plays the animation back out of
    tables instead of hand-written code.

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
  12x21 art pieces exist; "Frames" (next to Construct's frame strip) is how
  many animation steps the Timeline has. They're independent - resizing one
  never touches the other.
- **Colour shortcuts**: `0`-`3` pick Background/MC1/MC2/Individual (matches
  the toolbar's left-to-right order); `Ctrl+Z` / `Ctrl+Y` undo/redo.
- **Palette**: right-click a swatch to set that register to any of the 16
  real C64 colours - Background ($d021), MC1 ($d025), MC2 ($d026), or the
  edited sprite's Individual colour. The Border swatch sets $d020. These
  only affect how the editor displays things (and are saved in the
  project), never the exported pixel data.
- **Backdrop**: File menu > "Load Backdrop Picture (.kla)..." accepts any
  Koala-format picture (2-byte load address + 8000 bytes bitmap + 1000
  bytes screen RAM + 1000 bytes colour RAM + background colour byte).
  Loading one sets $d021 from its background byte.
- **Construct screen**: drawn pixel-exact to a PAL C64 - the 320x200
  display inside a 32/32/35/37-pixel $d020 border (VICE's normal 384x272
  view), with sprites hidden behind the border as on real hardware.
- Exported ASM assumes an assembler/build using `c6510`-style `.byte`
  directives and the label conventions noted above; adjust the including
  source to match if your build pipeline differs.

## License

[MIT](LICENSE)
