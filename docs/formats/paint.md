# Paint schemes, colours and decals

How the original colours an aircraft. Shipped skin textures are **not** the final paint: they
are neutral shading maps carrying region keys, and the engine writes the scheme's colours into
them at load time. The scheme itself (pattern + three colours + three decals) comes from
`vehicle.json` for AI aircraft, `ia.json` for instant-action aces, and a saved `.pln` file for
the player's customised plane.

This is why a naively-rendered Bloodhawk comes out **desaturated blue-gray** while the original
flies a **red** one: the remake draws the unpainted key texture.

Extracted data and the user's reference screenshots show
(`OriginalScreenshots/CustomPlane Paint1 Bloodhawk.png` = the in-game paint UI,
`OriginalScreenshots/Kestrel.png` = a painted plane in flight). **Implemented**,
see "Implementing this in the remake" at the bottom for what the remake actually does and
where it knowingly diverges.

The engine half (which slot each key parses into, how an override falls back to the def per field,
how three components become one packed dword) is decoded in [`org/paint.md`](../org/paint.md).

## The scheme record

Seven fields, appearing under three different key prefixes depending on where the scheme lives.
Same schema each time:

| Field | Type | Meaning |
|---|---|---|
| `paint_pattern` | string | Named scheme (`hughes`, `blackhat`, `medusas`, …) |
| `paint_color1` .. `3` | RGB triple, **integer 0–255** | The three paint colours |
| `paint_decal1` .. `3` | integer | Index into the numbered decal set (below) |

Prefixes:

- **`paint_*`**, in `vehicle.json`, on a vehicle def. Carried through `kind_of` inheritance
  like every other property (see [vehicle.md](vehicle.md)).
- **`ace_*`**, in a mission's `ia.json` (`ace_pattern`, `ace_color1`, `ace_decal2`, …),
  alongside `ace_name` / `ace_plane` / `ace_stats`. The instant-action ace's personal scheme.
- The player's customised plane stores the same thing binary, see "Saved custom planes".

The colour triples are **always integer 0–255** here, never the normalized-float encoding
that `weather.json` mixes in (see the README's dual-encoding note), no `>1` test needed.

### Schemes shipped in `vehicle.json`

Twelve named patterns across the AI roster. `devastator` and `wingman` carry a `paint_pattern`
with **no colours or decals**, the Fortune Hunters player scheme, whose colours evidently come
from elsewhere (the pattern's own defaults; see "Open").

| Pattern | Used by | color1 | color2 | color3 | decal1/2/3 |
|---|---|---|---|---|---|
| `player_fortune` | `devastator`, `wingman` | - | - | - | - |
| `hughes` | `habloodhawk`, `hakestrel`, `hafury` | 243,194,0 | 0,0,0 | 255,255,255 | 21/11/11 |
| `blackhat` | `bhatbrigand`, `bhatwarhawk`, `bhatgyro` | 177,130,66 | 119,74,43 | 66,39,15 | 21/2/2 |
| `blake` | `blakebloodhawk`, `blakepeace` | 149,163,195 | 89,114,159 | 233,228,240 | 21/3/3 |
| `british` | `britpeace`, `britbalmoral` | 177,130,66 | 48,47,39 | 255,255,255 | 21/4/4 |
| `blckswan` | `bsfury` | 23,23,21 | 48,47,39 | 196,193,186 | 21/5/5 |
| `cccp` | `rusdevastator` | 57,64,68 | 223,0,41 | 245,211,0 | 21/6/6 |
| `hollywd` | `hkfirebrand` | 108,102,169 | 67,36,121 | 212,202,225 | 21/10/9 |
| `medusas` | `medkestrel`, `medbrigand` | 95,125,143 | 41,14,21 | 141,137,93 | 21/14/14 |
| `sactrust` | `stihellhound` | 52,38,107 | 243,194,0 | 23,23,21 | 21/15/15 |
| `german` | `germanhellhound` | 96,115,126 | 0,0,0 | 48,47,39 | 21/13/13 |
| `studio` | `secfury`, `secgyro` | 32,90,167 | 255,255,255 | 0,0,0 | 21/11/11 |

Note `_2`/`_3`/`_5` suffixed defs (`blakepeace_2`, `bhatbrigand_5`) repeat their base def's
scheme verbatim, they are per-chapter roster duplicates, not scheme variants.

## The decal set

Each chapter's `texture.zbd` carries a **gapless numbered set 00–49** (identical in every
chapter, verified C1–C5), 64×64 with `Full` alpha, each with a half-size 32×32 `_1` LOD twin
(index 49 has none). **`paint_decalN` indexes this set by the texture's zero-padded numeric
prefix**, that is the whole mapping.

| Range | Contents |
|---|---|
| 00–20 | Squadron / militia / nation logos: `00bbomber_logo1`, `02blackhat_logo1`, `03blake_logo1`, `04british_logo1`, `05bswan_logo1`, `06cccp_logo1`, `07fhunter_logo1`, `09hknights_logo1`, `11hughes_logo1`, `13luftwaffe_logo1`, `14medusa_logo1`, `15sacredtrust_logo1`, `16colorado_logo1` … `20dixie_logo1` |
| 21–49 | Nose art: `21ace_star`, `22aintyerhoney`, `31fury`, `36joker`, `43skypirate`, `48gypsy`, `49ace_star2`, … |

The mapping cross-checks exactly against every pattern's name in the table above:
`blackhat`→2 = `02blackhat_logo1`, `blake`→3, `british`→4, `blckswan`→5 = `05bswan_logo1`,
`cccp`→6, `hughes`→11, `german`→13 = `13luftwaffe_logo1`, `medusas`→14, `sactrust`→15 =
`15sacredtrust_logo1`, `hollywd`→10/9 = the two `hknights` (Hollywood Knights) logos.

Every AI def uses `paint_decal1 = 21` (`21ace_star`), consistent with slot 1 = **nose** drawing
from the 21–49 nose-art range while slots 2/3 = **tail** and **wing** draw from the 00–20 logo
range. The paint UI's three dropdowns are labelled Nose / Tail / Wing in that order.

### Where a decal lands on the model

Every player aircraft in `planes.zbd` carries **16×16 placeholder** decal textures named
`<prefix>_noselogo`, `<prefix>_taillogo`, `<prefix>_winglogo`, one material each, on dedicated
polygons, across the eleven aircraft prefixes `blo` `kes` `fur` `pea` `war` `bri` `bal` `hel`
`agyro` `fir` `dev`. The engine swaps the chosen 64×64 decal texture onto these slots at load
time; the shipped 16×16 image is a placeholder, not artwork to render. Placeholder and decal
are both `alpha=Full`, so the swap does not disturb a renderer's alpha classification.

**Not every aircraft has all three.** The Firebrand ships no `fir_noselogo` (only
`fir_taillogo` / `fir_winglogo`), and keying the remake's aircraft
detection on the nose slot alone left the Firebrand unpainted. Detect on any of the three.

Index → texture is unambiguous: in both C1 and C5 exactly 50 textures match "two digits
followed by a non-digit, not an `_1` LOD twin", one per index 00–49, with no collisions
anywhere else in the archive (verified).

## Base skins are unpainted key textures

The per-plane skin textures (`blo_wing`, `blo_fin`, `blo_fusalagetop`, `kes_fuselage`, …) are
**shading/luminance maps with paint-region keys**, not finished paint.

Evidence, on the Bloodhawk:

- `blo_fusalagetop`'s 234-entry palette is a near-monotone luminance ramp from `(0,0,0)` to
  `(189,186,197)` with a slight blue cast. Its dominant pixel colour is `(74,81,99)`, a
  desaturated blue-gray. There is no red anywhere in the file, yet the original's Fortune
  Hunters Bloodhawk is red.
- `blo_fin` and `blo_wing` split into **two visually coherent regions**: a blue-gray body and a
  yellow-olive sweeping stripe. Rendering the texture region-coloured reproduces exactly the
  body / swoosh-flash split visible on the painted plane in the reference screenshots, the
  white swoosh on the red Bloodhawk, the black-and-white wing striping on the red Kestrel.
- Neutral (unsaturated) areas are unpainted structure, cowl metal, canopy frames, panel lines.

### The palette is not organised into reserved ramps

paint region", based on `blo_fin`'s indices **0–31** being a clean 32-step blue-gray ramp
covering the fin body. **That generalisation is wrong**, and an implementation must not key
on palette index:

- On `blo_wing`, the same aircraft, the blue body region is scattered across the palette
  (indices 0, 6–7, 9, 13–14, 16–17, 20, 24–25, 28–31, …), not banded.
- Every plane palette inspected is simply **sorted by luminance**, the ordinary output of a
  median-cut/octree quantizer. `blo_fin`'s tidy 0–31 run is a coincidence of that sort: the
  blue body happens to be the darkest thing in that particular texture.
- `manifest.json` carries only `Local` palettes; there are **no `global_palettes`** in any
  chapter's `texture.zbd` or in `rimage.zbd` (all eight chapters checked), so there is no
  shared paint palette either.

So the engine cannot be doing an index-range palette swap. Whatever table it uses to decide
"this texel is paint slot 2" is keyed on something else and is not in the ZBD data.

> **Found, it is in `crimson.rof`.** The region table exists after all, in the UI
> resource archive rather than the ZBD set: each `ASSETS/GRAPHICS/<PATTERN>/<SKIN>.BM` carries
> a greyscale shading map plus **three 8-bit per-pixel weight masks, one per paint colour slot,
> summing to 255**. That is a direct answer to "how the engine identifies a region", and it is
> per *pattern*, which also answers "what a pattern actually varies" below. Full decode in
> [rof.md](rof.md); the extraction writes each mask out as `<SKIN>_mask.png` (R/G/B = slots
> 1/2/3). **The remake was reworked onto these masks on**, the hue-window sections
> below describe the source-skin analysis; the runtime uses the mask-based mapping described in this page.

### What the regions actually look like

Region hue is **not** a stable slot key across aircraft, each aircraft's skins are authored
around **one dominant body hue**, and it differs per plane. Measured over every skin of each
aircraft (saturated-texel hue histogram, C1; the skins are byte-identical in all eight
chapters, verified, so this is chapter-independent):

| Prefix | Aircraft | Body hue | Secondary | Saturated |
|---|---|---|---|---|
| `blo` | Bloodhawk | 217° blue | 58° olive swoosh | 54% |
| `kes` | Kestrel | 199° blue | 58° olive flap | 43% |
| `pea` | Peacemaker | 219° blue | 55° olive | 44% |
| `bri` | Brigand | 200° blue | - | 43% |
| `war` | Warhawk | 31° orange | - | 94% |
| `bal` | Balmoral | 35° orange | - | 80% |
| `hel` | Hellhound | 250° purple | 48° amber | 83% |
| `fir` | Firebrand | 252° purple (wide, 230–270°) | - | 90% |
| `dev` | Devastator | 1° red | - | 52% |
| `agyro` | Autogyro | 58° olive | - | 88% |
| `fur` | Fury | **none** | - | 3% |

The Devastator shipping red and the Warhawk orange suggests some skins were authored at their
story colour. **The Fury is the outlier that disproves any pure hue-keying theory**: its skins
are a near-black greyscale shading map (median value 0.00 on `fur_wing` and `fur_fusalage1`)
with no saturated texels at all, yet `secfury` flies a studio-blue one in the original. So the
engine's region table cannot be derived from the texture's colours alone, it is external data
we do not have.

Note the yellow/olive secondary hue is partly **propeller spinners**, not paint: `dev_spinner`,
`fir_spinner`, `hel_spinner` and `pea_spinner` are ~100% saturated at 50–60°.

## Saved custom planes

The player's customised aircraft are plain files (no archive) in the install's `Planes/`
directory, named by the plane's in-game name (`Blue Streak`, `Jumping Jane`), **204 bytes**
each. The file is a verbatim dump of the engine's in-memory record: `FUN_0041a7b0` runs
`sprintf("Planes\%s", record+0x04)`, `mkdir("Planes")`, then `fwrite(record, 0xcc, 1)`.
The records live in an array at `0x0064b78c`, stride 0xcc; index 25 (`0x0064cb78`) is the
build-in-progress scratch copy every hangar screen edits, and case 22 of the screen-flow
callback dispatcher `FUN_00407670` fills the derived fields and calls the writer.

Little-endian 32-bit fields, offsets confirmed against the screen callbacks that write them:

| Offset | Field | Evidence |
|---|---|---|
| 0x04 | name, NUL-padded string | the writer's `sprintf` |
| 0x2c | airframe id | spawn descriptor slot 0 in `FUN_00417090`; first `%d` of the icon filename `PX_Icon_%d_%d_%d.tga` (`0x61f370`) |
| 0x34, 0x38 | left/right wing hardpoint count, 0-4 | callback 2245 handler at `0x0040ad0f`; the dropdown (callback 2244 at `0x0040b81d`) offers five rows, langui 1165/1168/1169 |
| 0x40 | paint pattern index 0-13 (13 remaps to 11 for the icon) | handlers at `0x0040d523`/`0x0040d54d`, second `%d` of the icon filename |
| 0x44-0x4c | per-slot colour index 0-26 into the swatch table at `0x0061dd48` (see below) | callback 2237 get/set at `0x0040d3e2` |
| 0x50-0x58 | per-slot shade-variant index into that colour's ramp; a colour pick resets it to the colour's own default variant | callback 2236 get/set at `0x0040d437`; the reset at `0x0040d402` |
| 0x5c, 0x60, 0x64 | the three decal indices 0-49 (nose, tail, wing). The paint screen's 5-wide decal grid stores `row*5 + col`, which IS the flat index | callback 2239 at `0x0040d5b7`; `FUN_0041a320` consumes them by indexing the decal-name table at `0x0061da20` directly |
| 0x68 | the three paint colours as RGBA bytes (`df 00 29 00` = `(223,0,41)`, ...), a DERIVED cache: `FUN_00406840`, its only writer, recomputes it from the colour+shade indices, including at `0x0040b5cb` just before every save | the resolver `FUN_004067c0` |
| 0x74-0x80 | four armour values, stored premultiplied by 5 | callback 2247 handler at `0x0040ac3d` divides by 5 on read |
| 0x84 | per-gun-slot family bit byte (adds 5 to the dropdown row) | callback 2249 handler at `0x0040bea2` |
| 0x88-0x94 | four gun ids, 5 = empty | callback 2249 |
| 0x98-0xa4 | per-gun-slot 4 = empty, else 0; derived at commit | case 22 of `FUN_00407670` |
| 0xa8-0xc4 | eight per-pylon display cells: commit writes 1 if position < that wing's hardpoint count else 11; callback 2245's write refills them `rand()%20 > 10` per cell while the count is non-zero | case 22; `0x0040ad4f` |

### How the remake stores one

The 204-byte format is import-only. CSVM writes its own JSON, one file per plane under
`user://Planes/`, named by the plane's name (`CustomPlaneStore`, schema `version` 2): the chosen
fields above and nothing the original derives at commit.

That file carries one field the original's record has no room for, an optional `loadout` block:

```json
"loadout": { "ammo": [2, 4, -1, -1], "ordnance": [3, 0, 0, 0, 11, 0, 0, 0] }
```

`ammo` is four gun slots and `ordnance` eight pylon cells, in the campaign profile's own encoding
(`saved-games.md`, "Where the ammunition and ordnance picks live"): an ammunition index 0-3 or the
no-gun marker 4, and a one-based rocket-table row with 0 meaning never picked. `-1` in `ammo` is the
same "never picked", which the profile record has no need of because its own field starts at 0.

The campaign's EXPORT is the only writer, and the block is written only for a plane that carries
one, so a plane built in the hangar persists exactly the file earlier builds wrote. It is a
**version 2** file either way: a reader without the block skips it like any unknown property and a
reader with it defaults the fields when it is absent, so raising the schema version would only have
made older builds refuse a plane they read perfectly well.

The same file carries a second optional field, the export gate the original states in langui 1139,
702 and 1256:

```json
"awaitingExport": true
```

The original keeps the campaign's aircraft out of Instant Action and multiplayer until the player
presses EXPORT on one, and this is how CSVM says which side of that crossing a stored plane is on.
It is written only while the plane is still waiting: a build funded by the campaign wallet and a
mission-awarded aircraft are saved with it, EXPORT clears it, and a build from either wallet-free
door never carries it. **The absence of the field means exported**, which is what keeps every plane
written before the field existed in the pickers. It is a **version 2** file for the same reason the
`loadout` block is.

### The paint screen's preview art

The paint screen's aircraft preview is not the flying model's `.BM` composition. Each
`PX_ICON_<airframe>_<pattern>_<n>.TGA` set (358x335, 32-bit) is a four-LAYER stack, the `_0..3`
suffix a layer index: layer 0 is the detail plate (panel lines, canopy, prop, guns, with its
coverage in the alpha channel) and layers 1-3 are the three colour slots' region masks, white
RGB with the region in the **alpha**. The composite is: masks 1-3 alpha-over in slot order,
each carrying its slot's resolved colour, then the plate over the result, no shading
multiply and no weight normalisation, so a fully-masked texel is exactly its resolved colour.
Sets ship for exactly the pairs the availability mask allows (only itstaxi/Hoplite has none).
Measured against the reference screenshots, the Fury's Fortune Hunters composite agrees to a
mean error of 4.4/255 per channel with region interiors exact. The decal picker's icons are
`PX_P_DECALS.TGA`, 66x3300: fifty 66x66 tiles top to bottom in decal-index order.

### The swatch table and the pattern defaults

The paint screen's colours are index pairs, never free RGB. The swatch table at `0x0061dd48`
(27 rows, stride 0x23, terminated by a zero count byte) holds per row: base R,G,B; a
shade-variant count; the default variant index; then the variants as R,G,B triples, a
dark-to-light ramp. `FUN_004067c0(record, slot, variant, flag)` resolves (colour, shade) to
RGB; the colour dropdown (callback 2229 at `0x0040d30e`) and the three shade dropdowns
(2232-2234 at `0x0040d388`) render chips from it. The full table is transcribed verbatim to
`CSVM/data/hangar_swatches.json`. The shipped scheme colours corroborate it: `blackhat`'s
(177,130,66) sits in the tan row's ramp, `blckswan`'s pair in the grey ramp.

The pattern table at `0x0061daf0` (14 entries, stride 0x28) is, per entry: a ushort
**airframe availability mask** at +0x00 (bit i = airframe i may wear it), three default
colour indices at +0x04..+0x0c, three default shade indices at +0x10..+0x18, and unread
padding. Setting the pattern (callback 2238's SET at `0x0040d4ba`) copies the entry's six
defaults over the record's colour and shade indices and touches nothing else, which is why
the original loads a pattern's own colours on selection. The pattern dropdown's labels are
langui `3425 + patternIndex`.

⚠ **The shipped table carries a one-dword shift in entries 10 and 12** (`german`,
`broadway`): their first dword is 0 and the real mask/defaults follow one slot late. A naive
walk that stops at the first zero mask would end at entry 10; play disproves that reading
(the Hoplite offers four patterns including `itstaxi` = 13, and a genuine save carries
`studio` = 11), so the walk evidently tolerates it. The corrected masks, de-shifted for
10/12 and matching every external source (the schemes' vehicle.json users, the shipped icon
sets, the fixtures): 0x411, 0x80, 0x208, 0x204, 0x7ff, 0x40, 0x188, 0x110, 0x20, 0x402,
0x2, 0x81, 0x200, 0x1.

Two facts pinned by reading seven genuine saves (the `CSVM.Tests/fixtures/planes204` fixture
set): the paint UI's darkest shade saves as **(25,25,25)**, so every scheme-table slot listed
as (0,0,0) round-trips through a save as 25/25/25 (the four Fury fixtures, each named for a
shipped scheme, match their scheme's triple in every other slot; their saves also pin pattern
indices blckswan = 1, fortune = 4, hughes = 6, studio = 11). And the family-bit byte at +0x84
can carry stray bits above the four gun slots (one fixture holds 0xc3 with only slots 0-1
occupied), so a reader must test bit n for slot n and never the whole byte.

**Pattern index 0-13 is a row of the engine's own 14-entry name table** at `0x0060301c`, the same
table `FUN_00401e80` builds `assets\graphics\<pattern>\` paths from ([`org/hangar.md`](../org/hangar.md)),
so the index names the archive folder directly:

| 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `blackhat` | `blckswan` | `blake` | `british` | `fortune` | `hollywd` | `hughes` | `medusas` | `cccp` | `sactrust` | `german` | `studio` | `broadway` | `itstaxi` |

The four indices the fixture saves pin (blckswan 1, fortune 4, hughes 6, studio 11) all land on
their own name, and each airframe's shipped `PX_ICON_<af>_<pattern>_*` sets are exactly the
pattern list `PatternsFor` gives that airframe's skin prefix (`itstaxi` aside, which ships no
icons at all). That cross-check is also what maps airframe id to skin prefix: 0 `agyro`, 1 `hel`, 2 `bal`,
3 `blo`, 4 `bri`, 5 `dev`, 6 `fir`, 7 `fur`, 8 `kes`, 9 `pea`, 10 `war`.

The saved-plane name index is a separate structure: 33-byte name records at `0x648534`,
capacity 24 (bound `0x64884c`), filled by the `Planes\*.*` directory scan `FUN_00415000`
(screen-flow callback 1024, case 0x400 of `FUN_00407670`; widget callback 2099 counts the
same table through a filter). Importing a player's saved planes needs only the table above;
nothing depends on it yet.

## Implementing this in the remake

**Reworked onto the original's own masks**, replacing the hue-window workaround
below. `src/Mech3/PatternLibrary.cs` reads the `.BM` region masks out of the extracted UI
archive, `src/Mech3/PlanePainter.cs` composites them, `src/Mech3/PaintScheme.cs` holds the
record, and the result reaches the renderer through a `SceneBuilder` texture-substitution hook
that `PlaneBuilder` drives per aircraft.

Per texel, exactly the formula in [rof.md](rof.md):

```
shaded_paint = shading * (w1*colour1 + w2*colour2 + w3*colour3) / 255
```

then the pattern's overlay composited over it (4th channel as alpha). Two details
matter: the shading map is the `.BM`'s own base plane, **not** the ZBD skin of the same
name; and `.BM` rows are stored **bottom-up**, so each source row is read from `h-1-y` (see
[rof.md](rof.md), without the flip every livery is mirrored along V, which is how the bug
first surfaced in-game). Parts a pattern ships no `.BM`
for keep their shipped ZBD texture; the ZBD skin's alpha channel is carried onto the composite
where the two agree on dimensions, so `SceneBuilder`'s blend-vs-scissor choice stays valid.

Decals are unchanged: `<prefix>_noselogo`/`_taillogo`/`_winglogo` swap for the numbered decal
via `TextureArchive.FindByDecalIndex`.

### Which `.BM` a model texture is painted from

The original does not derive one name from the other. `FUN_00401e80` walks a static per-airframe
table of (`.BM` file, model texture to replace) pairs, reached through the pointer array at
`0x0060329c` (one entry per airframe id, the Devastator being id 5 at `0x0060315c`), and its
strings occupy `0x00619470` to `0x00619b93`. Read whole, the eleven tables hold 62 pairs, and
**exactly one of them differs on the two sides**: the Devastator's fuselage sides are
`dev_fusalage1.bm` → `dev_fusalage` (`0x006197dc` / `0x006197f0`). Every other pair is
name-identical, which is why a literal lookup is right everywhere else.

`PlanePainter.SkinNameFor` is that one divergence, as a single alias rather than the whole table
transcribed. ⚠ Neither name is a mistake to correct: the ZBD texture really is `dev_fusalage` and
every pattern folder really ships `DEV_FUSALAGE1.BM`, so renaming either side breaks the other
consumer. `dev_fusalagetop` appears in no pair and no pattern ships a `.BM` for it, so the
fuselage spine keeping its shipped ZBD skin is the original's behaviour; it is also a separate
64x64 texture whose UVs tile to about U = 4.45, so the side atlas would not fit it.

### Patterns are per aircraft

A pattern covers only the planes it ships skins for, which is why the original's paint UI
offers a different list per aircraft. Measured over the extraction:

| Aircraft | Patterns |
|---|---|
| Bloodhawk `blo` | FORTUNE, BLAKE, HUGHES |
| Kestrel `kes` | FORTUNE, HUGHES, MEDUSAS |
| Fury `fur` | FORTUNE, BLCKSWAN, HUGHES, STUDIO |
| Peacemaker `pea` | FORTUNE, BLAKE, BRITISH, BROADWAY |
| Warhawk `war` | FORTUNE, BLACKHAT, SACTRUST |
| Brigand `bri` | FORTUNE, BLACKHAT, MEDUSAS |
| Balmoral `bal` | FORTUNE, BRITISH |
| Hellhound `hel` | FORTUNE, GERMAN, SACTRUST |
| Autogyro `agyro` | FORTUNE, BLACKHAT, ITSTAXI, STUDIO |
| Firebrand `fir` | FORTUNE, HOLLYWD |
| Devastator `dev` | FORTUNE, CCCP |

`FORTUNE` is the only pattern covering all eleven. The four the Fury carries match the user's
four `CustomPlane Paint Fury Pattern *.png` reference screenshots exactly, which is the
confirmation that this list *is* what the paint UI offers.

`PatternLibrary.PatternsFor(prefix)` is that list; random liveries draw from it, the livery
lab steps it, and `--paint=` validates against it (naming a pattern the aircraft lacks logs
the aircraft's own set and paints decals only).

### Slot order, confirmed

Slot 1 = identity/body, slot 2 = dark trim, slot 3 = light trim, the order the masks ship in
matches `paint_color1..3`. Settled by rendering the Fortune Hunters Bloodhawk with all three
plausible assignments against `CustomPlane Paint1 Bloodhawk.png`: only *(red, black, white)*
puts black on the outer wing panels and the white swoosh between them, as the reference shows.
White in both trims loses the black wing entirely; black in slot 3 paints the swoosh instead
of the panel.

The executable corroborates the ordering discipline but not the region reading: `FUN_00479240`
parses the three colours into fixed consecutive slots and `FUN_0047c210` resolves and packs them
positionally, so nothing between `vehicle.json` and the scheme record permutes them
([`org/paint.md`](../org/paint.md)). Which region each slot reaches is decided past the message
dispatch that decode stops at, so the assignment above still rests on the render test and the
Colour/Shade reading.

That also pins down **`player_fortune`'s missing colours** as `223,0,41 / 0,0,0 / 255,255,255`,
the same shape every shipped scheme has (`hughes` is yellow/black/white). The red comes from
the paint UI's swatch and a saved `.pln` at 0x68; the trims are read off the reference. Still
an inference from artwork, not a value found in a file.

### What the rework fixed

Every divergence the hue-window version carried was a consequence of guessing regions from
colour, and all of them are gone:

- **Achromatic regions paint.** The Bloodhawk's outer wing panels are black under Fortune
  Hunters, as in the original, a hue window could not see a region with no hue.
- **The Fury paints.** Its ZBD skin is featureless near-black, but its `.BM` masks are
  complete; all four of its patterns render correctly, including the Studio Security
  blue-and-white checkerboard.
- **Slot order is data, not area rank.**
- **Borders come antialiased** in the masks themselves, so the soft hue falloff that existed
  only to stop borders speckling is gone.
- **It is faster.** A flat multiply-add per texel uses a flat multiply-add per texel instead of HSV conversion plus a
  percentile pass; painting four aircraft is now within measurement noise of not painting.

## Known limits

- **The "Shade" column is the brightness of the chosen colour.** The UI's Colour dropdown
  picks the hue family and Shade picks how light or dark it is; their product is the single RGB
  that ends up in `paint_colorN`. There is no fourth stored field and nothing extra to model: a
  scheme really is three colours, and the masks giving each slot exactly one colour is
  consistent, not a contradiction. The swatch table above is that Colour/Shade space made
  concrete, and the hangar's paint screen drives the original's own two dropdowns over it; the
  **livery lab** keeps its RGB sliders,
  which span the same space and more. The pattern table's six per-pattern defaults and the
  50 decal names are transcribed to `CSVM/data/hangar_patterns.json` beside the swatches.
  Two shade-level divergences between a pattern's hangar defaults and its `vehicle.json` AI
  scheme are authored, not errors: `hollywd` slot 2 defaults to (70,40,132) where the AI
  scheme carries (67,36,121), and `cccp` slot 3 to (243,194,0) against (245,211,0).

  This also **independently confirms `player_fortune`'s colours**, which had been derived only
  by rendering. `CustomPlane Paint1 Bloodhawk.png` shows Colour/Shade of *red/red*,
  *white/**black***, *white/white*, resolving to **red, black, white**, exactly the triple
  that the three-way render test singled out. Two unrelated routes, same answer.
- **Overlay channel order** is still unconfirmed (see rof.md); treating the 4th channel as
  alpha over RGB renders correctly on the content inspected, which is greyscale and so cannot
  distinguish RGBA from BGRA.
- **`player_fortune`'s colours** are inferred from artwork (above), not located in a file.
- **`BROADWAY` and `ITSTAXI`** ship masks (Peacemaker and Autogyro) but no `paint_pattern`
  names them, so they have no canonical colours; the remake offers them with whatever colours
  are current.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
