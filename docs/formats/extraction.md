# Extraction status, what comes out of a retail install

Which of Crimson Skies' archive types can be read, how far each has been validated, and what
the output looks like. Everything here was validated against **this project's retail install**
(`unzbd cs <mode>`, round-tripped with `rezbd cs <mode>` and compared by sha256).

**Extraction is complete: every archive type this install ships is supported, and every one of
them round-trips byte-identically in the fork.** If you only need the practical facts, they are:

- The engine extracts both halves (`CSVM/src/Extraction/`): the ZBD archives through unzbd, then
  the non-ZBD half (`.rof` UI archives, the DLL string tables, the cinemas) into `extracted/rof/`.
  Output lands under `extracted/`, mirroring the game's own folder structure. Every game-named
  path under `extracted/rof/` (archive members, the `.BM` PNGs, the cinemas) is written upper
  case by `RofTree`, whatever case the install or the data spells it, and every reader maps a
  name the same way, so a case-sensitive disk finds each file with no directory scan. The files
  the extraction authors (`menu_layout.json`, `ui_strings.json`, `_crimptch/`) keep their own
  names. The ZBD half's outputs take `ZbdTree`'s case, the retail install's own: chapter and
  mission folders upper case (`C1B/M03`), archive names lower case (`zrdr.zip`,
  `rtexture12.zip`). The campaign sequence names the folders `c1b`/`m03`, so every chapter and
  mission path resolves through `SessionPaths`, which maps it the same way. A player runs it
  from the in-game Extract screen, a developer with `Extract.ps1` (repo root) or the headless
  `--extract=<install>` flag ([../tooling.md](../tooling.md), [../cli.md](../cli.md)).
- The extractor runs the **fork** build (`tools/mech3ax/target/release/unzbd.exe`),
  not the pinned v0.6.1 binary. `--unzbd=<path>` (`Extract.ps1 -Unzbd <path>`) rolls back to
  v0.6.1 with **no code change**, because the Godot loaders read either output shape (see
  "Supported extraction shapes" below).
- mech3ax's own README support matrix is **outdated** for Crimson Skies, actual support, even at
  v0.6.1, is far better than it advertises.

## Support matrix

| Format | Status |
|---|---|
| `texture.zbd` / `rtexture*.zbd` / `rimage.zbd` | ✅ extracts to PNGs; round-trip **byte-identical** (C1 verified) |
| `soundsh.zbd` / `soundsl.zbd` | ✅ extracts to WAVs |
| `zrdr.zbd` (reader / mission config) | ✅ extracts to JSON (ai, engines, Briefing, …) |
| `interp.zbd` | ✅ extracts to JSON (engine boot scripts) |
| `gamez.zbd` (world geometry) | ✅ extracts (metadata / textures / materials / meshes / nodes JSON); round-trip **byte-identical** with the pinned v0.6.1 binary (C1 + C5 verified) **and with the fork** (all 8 chapters). The fork's JSON *shape* differs, see the `planes.zbd` row |
| `planes.zbd` (aircraft models) | ✅ extracts. It is a GameZ-format file (the boot script loads it via `GameZReadZBDFile`), so it uses `gamez` mode. With the pinned v0.6.1 binary the round-trip differed by 72 bytes / 6 MB, swapped `\0`/`.` garbage past the null terminator in fixed-width texture-name fields; **the fork round-trips byte-identically**. The bug was a general `Ascii` asymmetry, not CS-specific: `to_str_suffix` restores the period at the *first* zero, but `from_str_suffix` converted the *last* one |
| `cam_anim.zbd` / `mis_anim.zbd` | ✅ **in the fork only** (not in the pinned v0.6.1 binary): `unzbd cs anim` / `rezbd cs anim` work end-to-end since, test.py `--- ALL OK ---`, **all 61 archives of this install byte-identical through the real zip pipeline**. Extracted like every other type, and **consumed by the Godot project** (`CompiledAnim.cs` → `AnimProgram.cs` → `AnimRuntime.cs`) |
| `GOSDATA/ASSETS/*.rof` | ✅ **not a ZBD, not mech3ax**, decoded and extracted by this project (`RofArchive`). 846 members, all inflating to their exact declared size. Holds the customisation UI and the per-pattern **paint region masks** ([rof.md](rof.md)) |
| `BINARIES/langui.dll` | ✅ Win32 STRINGTABLE, extracted by this project (`PeStringTable`), 1,247 UI strings including the aircraft names and descriptions ([strings.md](strings.md)) |
| `GOSDATA/ASSETS/GRAPHICS/MPG/*.mpg` | ✅ **copied verbatim, never converted**, by the extraction (`MovieCopy`), under upper-case names. The ten movies are not archive members: the `.rof` carries `ASSETS\GRAPHICS\MPG` as a directory entry with nothing behind it and the files sit loose in the install, so the step copies them and CSVM decodes MPEG-1 at runtime ([cinemas.md](cinemas.md)) |

## Animation archives

`cam_anim.zbd`/`mis_anim.zbd` were **net-new** work in the fork, upstream mech3ax has no CS anim
support at all. Every `AnimDef` field, support array and sequence event is **semantically decoded**
into mech3ax's shared API types (PM-delegating `EventCs` dispatch; the CS-only e46 `SOUND_ADJUST`
and e47 si-script `ALL_NAMES` are preserved raw; `hangar3_doors` was verified field-for-field
against its zrdr JSON source), and **every SI script frame-decodes**, all 1,090 of them. Rotate
cubics are half-angle offsets composed `exp(v)⊗base`; splines are kept as raw bytes, matching
upstream's MW/PM handling.

CS-only container data rides in optional `AnimMetadata` fields (`base_files`, raw `ptrs`). One
JSON-layer quirk, six NaN deltas in C5/M02's `carneypkup_cam.zan`, is preserved bit-exact via
`delta_raw`.

The schema itself is documented in [anim-definitions.md](anim-definitions.md).

## Supported extraction shapes

The fork's extraction JSON is **intentionally not shape-compatible** with v0.6.1's. Same data,
different spelling:

| v0.6.1 ("legacy") | fork ("unified") |
|---|---|
| `{"Object3d":{…}}` wrapper | flat node with the variant under `data` |
| `mesh_index` / `children` / `transformation` | `model_index` / `child_indices` / `transform` |
| `matrix.a…i` | `original.r00…r22` (no-transform case is the string `"Initial"`) |
| `meshes.json` | `models.json` |
| polygon `unk04` / `triangle_strip` | `priority` / `tri_strip` |
| mesh light `extra` | `vertices` |
| `textures.json` = `{original, renamed}`, plus `texture_ptrs` | `textures.json` = `{name}`, materials carry `texture_index` |
| partition cells `nodes[].index` | `values[].node_index` |
| zrdr entry naming **replaces** the source extension: `vehicle.zrd` → `vehicle.json` | **appends** to it: `vehicle.zrd` → `vehicle.zrd.json` |

Content is unaffected by the zrdr naming difference, all 222 readers of the shared archive
were verified semantically identical across the two extractions. Consumers must accept both
spellings when looking an entry up by name.

`GameZ.cs`, `TextureArchive.cs` and `Zrdr.cs` read **both**, which is what makes the v0.6.1
rollback a data-only operation. Two traps in the unified shape are worth knowing before touching
that code, and are recorded in `docs/architecture.md`: its own `index` field is 1-based and
duplicated (v0.6.1's `node_index`) while `child_indices` are **not** in that space, they
stay flat list positions; and unified `scale` is unit everywhere measured, so it is deliberately
ignored.

## Aircraft extraction data

Confirmed usable: `nodes.json` holds **3,317 nodes**, including full hierarchies for
`player_bhawk`, `player_peacemaker`, `player_kestrel`, `player_autogyro`, `player_avenger`,
`player_balmoral` and `player_fury`, with control surfaces (ailerons / elevators), props, gear,
firepoints and cockpits.

Note that **plane skin pixels live in each chapter's `texture.zbd`, not in `planes.zbd`**, and that
the shipped skins are unpainted key textures, see [paint.md](paint.md).

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
