# `.rof`, the UI resource archive

The game's menu/hangar/paint-shop resources: artwork, GUI scripts, the widget layout CSV and
the scrapbook. Two archives ship in `GOSDATA\ASSETS\`:

| File | Size | Contents |
|---|---|---|
| `crimson.rof` | ~57 MB | 846 files in 21 directories, the whole UI resource set |
| `crimptch.rof` | 797 bytes | 1 file, a patch overlay that overrides the base archive at the same path |

mech3ax does not handle `.rof`; it is unrelated to the ZBD family. This project decodes it with
`crimptch.rof` as the Rosetta Stone, one file, one directory chain, small enough to read by
hand. The reader it belongs to ships as `GOSDATA\ASSETS\BINARIES\roffile.dll`, which is where
the format's name comes from.

**Consumed by the remake**: `src/Mech3/PatternLibrary.cs` reads the `.BM`
masks straight out of this extraction and `src/Mech3/PlanePainter.cs` composites them, which
is how aircraft get their liveries (see [paint.md](paint.md)).

**This is where the plane customisation UI lives**, the screens behind the `CustomPlane`
reference screenshots, and, most usefully, where the **paint region masks** are
(see "Paint region masks" below, and [paint.md](paint.md)).

The text those screens display is *not* here; it is a Win32 string table in
`BINARIES\langui.dll`, see [strings.md](strings.md).

## Container

All fields are little-endian `u32`. The file is a tree of directory nodes; the root node sits
at offset 0 and holds exactly one entry, `ASSETS`.

**Directory node:**

| Offset | Field | Meaning |
|---|---|---|
| `0x00` | `entry_count` | number of 24-byte entries that follow |
| `0x04` | `pool_len` | total bytes of the name pool after the entries |
| `0x08` | `entries[entry_count]` | 24 bytes each, below |
| `0x08 + 24*n` | `name_pool` | `pool_len` bytes of NUL-terminated names |

**Entry (24 bytes):**

| Offset | Field | Meaning |
|---|---|---|
| `0x00` | `offset` | absolute file offset: a child directory node, or the member's payload |
| `0x04` | `size_uncompressed` | 0 for directories |
| `0x08` | `size_compressed` | 0 for directories; bytes stored at `offset` |
| `0x0C` | `kind` | `0` stored, `1` directory, `2` deflated |
| `0x10` | `name_len` | length of the name including its NUL |
| `0x14` | `name_offset` | byte offset of the name **within this node's pool** |

Names are ASCII and always upper-case. `name_offset` indexes the pool of the node the entry
belongs to, not a global table, so a node is self-contained and can be parsed without state.

**Payloads.** `kind = 2` members are raw **zlib** streams (`78 01`, the game links zlib;
`DeflateStream` works after skipping the 2-byte zlib header). `kind = 0` members are stored
verbatim, used for data that would not compress, every shipped `.PNG` and `.JPG` is stored,
every `.TGA`, `.BM`, `.SCRIPT` and `.CSV` is deflated.

Verified across the whole archive: **all 846 members inflate to exactly their declared
`size_uncompressed`**, and the last member ends at byte 60,236,221, the archive's exact
length, so the layout is fully accounted for with no slack.

### The patch archive

`crimptch.rof` has the identical structure and carries a single member,
`ASSETS/SCRIPTS/AIRFRAME.SCRIPT`. It is an override: the engine reads the patch archive after
the base one, so the patched script wins. The extraction unpacks it to `_crimptch/` rather
than over the base extraction, so both versions are available to diff.

## Archive contents

| Count | Type | Notes |
|---|---|---|
| 313 | `.PNG` | UI artwork, stored uncompressed |
| 184 | `.BM` | custom texture format, below, the per-pattern plane skins |
| 143 | `.TGA` | fonts (`ARIAL8.TGA`, `FONT.TGA`), blueprints (`PX_0_BLUEPRINT` … `PX_10`, one per aircraft) |
| 107 | `.JPG` | scrapbook / briefing photography |
| 61 | `.SCRIPT` | the GUI scripts |
| 25 | `.TIF` | |
| 8 | `.WAV` | UI sounds (`MOUSECLICK`, `MOUSEOVER`, `MUSIC_SPLASH`, …) |
| 2 | `.CSV` | `LAYOUT.CSV` (widget layout), `SCRAPBOOK.CSV` |
| 2 | `.H` | `RESOURCE.H`, `RESRC1.H`, the string-ID map, see [strings.md](strings.md) |
| 1 | `.TXT` | `DEBUGINFO.TXT` |

`ASSETS/BINARIES/` exists in the tree but is **empty**, those files (`langui.dll`,
`language.dll`, `roffile.dll`, `ijl10.dll`) ship as loose files on disk at the same path.

### GUI scripts

`ASSETS/SCRIPTS/*.SCRIPT` is a C-like UI scripting language, `gui_create` / `gui_init` /
`gui_mailbox` blocks, `object` declarations bound to control classes, `callback(...)` into the
engine, and `mail(id, target)` message passing between screens. Identifiers are **obfuscated**
(single letters, `$$A$$` placeholders), but widget keys are plaintext and match `LAYOUT.CSV`
rows (`af_t_title`, `af_s_airframedesc`). The customisation flow is `PLANESELECTION`,
`PLANECONSTRUCTION`, `AIRFRAME`, `ARMOR`, `ENGINE`, `GUNS`, `HARDPOINTS`, `PAINT`,
`PLANENAME`, `PURCHASE`.

The campaign flow's scripts (`CAMPAIGN`, `PASSENGERCABIN`, `CAMPAIGNINTRO`, `FLIGHTCHECK`,
`ORDINANCELAYOUT`) are decoded behaviourally in
[campaign-screens.md](campaign-screens.md), which also maps every `@ctl@XX` script class onto its
`LAYOUT.CSV` type letter.

`LAYOUT.CSV` is a commented widget table: `ID=<type>,<art>,X,Y,Z,ResID,…`, where `ResID` is the
`IDS_*` string ID. Its header comments name the widget types (`B`utton, `T`ext, `S`crolltext,
`D`ropdown) but do not reproduce the shipped field orders. The file, its two macro mechanisms, the
per-type field orders and the decoded `menu_layout.json` extraction emits are in
[menu-layout.md](menu-layout.md).

## Bitmap textures

### PNG colour transfer

The screen-chrome PNGs are 8-bit RGBA but do **not** share one file gamma. For example,
`MM_B_CAMPAIGN.PNG` declares `gAMA = 0.45454`, while `MM_B_INSTANTACTION.PNG` declares
`0.22727`. Consumers that create a raw RGBA8 texture must normalize RGB to the UI's 0.45454
curve first; the latter requires a square-root conversion. Alpha is not colour data and stays
verbatim. `ComposedBoardView` applies this normalization after Godot decodes the file and before it
creates the texture; `PngImage` applies the same rule on its engine-free path.

The 184 `.BM` files are the per-pattern aircraft skins, in `ASSETS/GRAPHICS/<PATTERN>/`.
Fourteen pattern folders exist, the twelve `paint_pattern` names from [paint.md](paint.md)
plus `BROADWAY` and `ITSTAXI`. Filenames are the aircraft skin names (`BLO_WING.BM`,
`FUR_FUSALAGE1.BM`, `KES_FLAP.BM`), matching the `blo`/`fur`/`kes` prefixes in
[gamez.md](gamez.md). Not every pattern covers every aircraft: `FORTUNE` has all 62 skins,
`BLCKSWAN` only the 5 Fury ones.

**Header**, two `u16`, **height first**:

| Offset | Field |
|---|---|
| `0x00` | `height` |
| `0x02` | `width` |

The order was settled by comparing against the game's own textures of the same name in
`texture.zbd`: **170 of 173 same-named skins match on `(width, height)` = `(second, first)`**.

**Row order is BOTTOM-UP.** Rows are stored last-to-first relative to
the ZBD textures and to PNG, so a consumer must read source row `height-1-y` when writing row
`y`. Rendering the masks without this mirrors every livery along the texture's V axis, found
by the user in-game ("the stripes are on the wrong sides of the wings and tail", with the
Black Swan Fury, whose livery should read close to its unpainted skin, as the clearest tell).

Confirmed by structural (edge-map) cross-correlation of each `.BM` shading map against the
ZBD texture of the same name, over all four orientations and all 55 same-sized `FORTUNE`
pairs. Restricted to pairs that can actually discriminate (peak correlation > 0.25 and a
margin > 0.08 over the runner-up), **flipV wins 24 to 3**, and it holds every large margin,
`bal_fuslage` 0.68 vs 0.02, `bri_wingbottom` 0.70 vs −0.02, `pea_spinner` 0.78 vs 0.09,
`war_engine` 0.91 vs 0.59. The same test on the paint masks against the ZBD skins' own
body-hue regions agrees. The three dissenters (`fir_engine`, `dev_spinner`, `bri_reartop`)
are unexplained and listed under Open.

**Payload** is always exactly `width * height * 10` bytes, in planes (`n = width * height`):

| Range | Content |
|---|---|
| `[0, 3n)` | 24bpp RGB shading map |
| `[3n, 6n)` | three 8-bit paint-region weight masks, slot 1, 2, 3 |
| `[6n, 10n)` | 32bpp pattern/decal overlay with alpha |

### The shading map

Effectively greyscale: measured over the base plane of every pattern's most-saturated skin,
mean chroma is **2.5–5.0 / 255**. It carries the panel lines, rivets and baked shading, and no
paint. It is *not* the same image as the game's texture of the same name, `texture.zbd`'s
`blo_wing` is the blue-gray key texture described in [paint.md](paint.md), while the `.BM` is
a neutral luminance map at the same dimensions.

### Paint region masks

**The three 8-bit planes at `[3n, 6n)` are per-pixel blend weights, one per paint colour slot,
and they sum to 255.** This is the region table [paint.md](paint.md) records as absent from the
data, it was in the UI archive all along.

They are weights, not indices, which matters: the values cluster at 0 and 255 with a scatter of
intermediates, and those intermediates are **antialiased region borders**. An index encoding
could not express a half-and-half boundary texel, and the hand-authored hue-window
approach had to reintroduce soft membership precisely to stop borders speckling.

Verification, over all 184 skins: the three planes sum to 250–260 for **>97% of pixels in 100
files**, and for 89–96% in most of the rest (the shortfall is concentrated at borders and in
fully-unpainted skins). Rendering slot1/slot2/slot3 as R/G/B makes the patterns legible
directly, `HUGHES` resolves into clean rectangular blocks, `FORTUNE` into a curved swoosh
that matches the white swoosh on the red Fortune Hunters Bloodhawk in
`OriginalScreenshots/CustomPlane Paint1 Bloodhawk.png`.

So the original's colouring is, per texel:

```
shaded_paint = shading_map * (w1*colour1 + w2*colour2 + w3*colour3) / 255
```

with the overlay composited over the result. Regions with **no hue at all** are expressible
this way, which is what the hue-window approach structurally could not do, see "Known
divergences" in [paint.md](paint.md).

### The overlay layer

`[6n, 10n)` is 32bpp with a meaningful 4th channel, holding the pattern's decorative artwork
(stripes, flashes, squadron marks) as a layer to composite over the painted skin. It is
**all-zero for parts a pattern does not decorate**, `BLCKSWAN/FUR_WING.BM` has an empty
overlay, so a pattern can leave a part plain.

Channel order within the overlay is unconfirmed: the content inspected so far is greyscale, so
RGBA and BGRA are indistinguishable on it.

## Known limits

- **Overlay channel order and blend mode.** Greyscale content leaves RGBA vs BGRA
  undetermined. The remake composites it as straight alpha-over-RGB and renders correctly on
  everything inspected, which is consistent with but does not prove that reading.
- **Slot order.** *Confirmed *, file order **is** `paint_color1..3`. Rendering the
  Fortune Hunters Bloodhawk with all three plausible assignments against
  `OriginalScreenshots/CustomPlane Paint1 Bloodhawk.png` singled one out: only
  *(red, black, white)* puts black on the outer wing panels with the white swoosh between
  them. See [paint.md](paint.md) "Slot order, confirmed".
- **`BROADWAY` and `ITSTAXI`** are pattern folders with no matching `paint_pattern` in
  `vehicle.json`, probably story/cutscene liveries.
- **The three dimension mismatches** (of 173) between `.BM` header and `texture.zbd` were not
  chased; likely UI-only variants.
- **Three skins prefer unflipped rows.** `fir_engine` (0.95 vs 0.68), `dev_spinner` (0.99 vs
  0.83) and `bri_reartop` (0.57 vs 0.43) score higher unflipped in the row-order test above,
  against 24 that prefer flipped. All three are engine/spinner parts whose textures are close
  to V-symmetric, so this may be noise on an almost-tie rather than a real per-file
  difference; the remake flips globally and they render correctly.
- **Mipmaps are absent.** 10 bytes/pixel is exactly the four planes with nothing left over, so
  these are base-level only.

## Extraction

The extraction (`RofExtraction`) unpacks both archives into `extracted\rof\`, writing every member
at its archive path in upper case, and additionally decodes each `.BM` to `<NAME>.PNG` (shading
map) and `<NAME>_MASK.PNG` (R/G/B = slots 1/2/3). It also emits the string table, see
[strings.md](strings.md), and the decoded menu layout `menu_layout.json`, see
[menu-layout.md](menu-layout.md). Run `.\Extract.ps1`; `-Force` re-runs an up-to-date
extraction.

The steps live in `CSVM/src/Extraction/`: `RofArchive.cs` reads the container, `BmTexture.cs`
and `PngWriter.cs` write the two PNGs, and `RofExtraction.cs` runs the whole non-ZBD half. The
managed PNG writer stores no `sRGB`, `gAMA` or `pHYs` chunk.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
