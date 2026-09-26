# Mission objectives and stunt zones

Part of the [format documentation](README.md). This page describes the objective data behind
Stunt Flying: `ia.json` supplies the zone set, `targets.json` maps nodes to display-string keys,
and `messages.json` supplies localized text. `dzones.json` carries per-mission overrides that the
remake does not read. The current readers are `Messages.cs`, `MissionTargets.cs`, and
`StuntMission.cs`. The campaign missions' choreography script, `objectives.zrd`, which consumes
these danger zones and target-display flags from its own directives, is decoded in
[objectives.md](objectives.md).

## Contents

- [Stunt objective](#stunt-objective)
- [Zone overrides](#zone-overrides)
- [Target display keys](#target-display-keys)
- [Message table](#message-table)
- [Assembled marker text](#assembled-marker-text)
## Stunt objective

An instant-action Stunt Flying run's goal is to fly through a fixed set of **Danger Zones**
(bridges, tunnels, hangars, arches, hence "stunt"), timed, completable in any order. Each
zone is one gamez marker node whose display name comes from `targets.json`.

### `ia.json` `dzones`, the zone list

The mission's `ia.json` (see [spawns.md](spawns.md) for the rest of that file) carries a
top-level `dzones` key: a list of `[dzpathN, dzN]` string pairs, one per zone, in objective
order.

```
"dzones", [ ["dzpath1", "dz1"], ["dzpath2", "dz2"], … ]
```

- **`dzN`** is the completion marker, a gamez `Object3d` with `mesh_index -1` (no geometry),
  sitting directly under the identity `world1` root, so its `translation` is already
  world-space (C1 `dz1` = `(-5186.2, 141.2, -6500.6)`). It sits on the tunnel/bridge/hangar
  opening the player must fly through.
- **`dzpathN`** is an untextured, vertex-coloured **polyline ribbon** mesh under the world's
  `dzpaths` group, the AI/guide route through the zone (`dzN`'s point is a vertex of it).
  Never rendered in the original; the world build skips the whole `dzpaths` subtree (see
  [world-structure.md](world-structure.md)). The remake builds it only under `--debug-dzpaths`.
  Besides the route polyline it carries **two gate-outline polygons**, the entry and exit
  apertures (below).

#### A `dzpathN` mesh is always route + exactly two gates

**Measured over every `dzpathN` in this install: 80 of 80 meshes carry exactly three
polygons.** One is the route ribbon (3–124 vertices, up to 6.5 km long); the other two are
closed outline rings, and they are a *matched pair*, 64 of 80 agree in area within 10 %, 29
of them bit-equal. Their centroids sit a median 11.7 m apart along the route (0 m where the
aperture is a thin slit, up to 1.65 km where the zone is a long tunnel or valley run). C2/IA1's
`dzpath1` pair outlines the Seaplane Hangar's front aperture, 1.9 m from the `door`-leaf slit.

The original design specifies a Danger Zone as an **entry volume and an exit volume, both of
which must be crossed**, deliberately two, so that clipping one volume tangentially does not
score. The matched polygon pair is that entry/exit pair: the three-polygon shape is
data-confirmed, the entry/exit reading is design-informed and matches it exactly.

Which polygon index is which is *not* fixed, the route is usually index 0 but not always
(C4's `dzpath14` has the pair at indices 0 and 1). Classify by **material**: the two gate
outlines share one material and the route has the odd material; never use polygon index.

**A dzone's node is not always a `dzN` point marker**, it may name real world *geometry*:
C2/IA1's first dzone is `sghangar`, the Seaplane Hangar structure itself. Its gamez
`transform` is the no-transform string `"Initial"` (see [extraction.md](extraction.md)), so
the node's own origin resolves to the world origin, ~8 km from the building, the zone's
position must come from the subtree's mesh geometry, not the node transform. Every actual
`dzN` marker in this install (all 53, measured) is a childless `mesh_index -1` node, so the
two cases are cleanly distinguishable. The remake anchors such geometry zones on the
`door`-named leaf pair when present (the flown aperture, the hangar's `sgh_door1`/`sgh_door2`
leave a 20 m front slit), corroborated by `dzpath1`, whose second polygon outlines that front
aperture 1.9 m away.

**Read the list, not the node names.** `dzN` numbering is *not* contiguous and does not
enumerate every `dzN` in the gamez: C1B's dzones are `dz1, dz3, dz4, dz6, dz7`, and C1's
gamez contains a `dz6` that is **not** an objective. Always drive the zone set from the
`dzones` list.

Zone counts (this install): C1 5, C1B 5, C2 9, C3 4, C4 14, C5 17; **C1C and C2B have no
`dzones`** (their IA1 is a different instant-action type), a stunt run there is empty and
falls back to free flight.

### Completion test

**The original does have gate geometry**, the `dzpathN` entry/exit polygon pair above, and
its completion rule is a crossing of both. The remake reads the material-matched pair and requires
a segment crossing inside each polygon, in either order. `dzN` remains the HUD anchor; `DzRadius`
is retained for its existing non-scoring consumers. A tangential touch or a plane crossing outside
the polygon aperture does not score.

`help_label` distinguishes `MSG_OBJ_FLYTHROUGH` ("Fly Through") from `MSG_OBJ_FLYOVER` ("Fly
Over"); both use the same authored gate test (revisit only if a real mission reads wrong).

## Zone overrides

A **second, separate** file, in the mission's own zrdr archive, keyed on `dzpathN` rather than
`dzN` (23 files: story missions plus C5/IA1). It is what makes one chapter's fixed zone set
behave differently per mission. `CampaignDangerZones` reads all three keys: `disable` narrows the
armed set, and `objective_numbers` and `nosnapshot` decide which zone photographs into which
scrapbook row. Flat alternating `KEY, [values…]`; all three keys are optional.

| Key | Value | Meaning |
|---|---|---|
| `objective_numbers` | `[[dzpathN, n], …]` | the zone's objective **slot index** in this mission |
| `disable` | `[dzpathN, …]` | zones switched off for this mission |
| `nosnapshot` | `[dzpathN, …]` | zones that score but capture no scrapbook snapshot |

- **`objective_numbers`** values occupy a fixed **18–31** band across the whole install (14
  distinct values, contiguous within a mission), a reserved slot range for danger zones in the
  mission's objective list, not a zone id. C5/M01 uses all 14. The number is the zone's bit in the
  completed-objective mask and the objective half of the `Snap_<mission>_<objective>` scrapbook row
  it photographs into (`docs/formats/campaign-screens.md`, "The danger-zone slot"). ⚠ The debrief
  turns ids **18 to 30** into mask bits and stops there, so a zone numbered 31 scores and
  photographs but can never light a row.
- **`disable`** is how a story mission narrows the chapter's zone set: C1/M05 disables five of
  six, leaving one; C2/M05 and C4/M05 disable **every** zone, so those missions have none. It
  names `dzpathN`, so a zone is disabled by its path, not by its `dzN` marker.
- **`nosnapshot`** opts a zone out of the per-zone capture: crossing a listed zone still scores its
  objective number, but no photograph is written for it, which is why CM11/CM12 ship no `Snap_*_27`
  row for the zone they number 27. C5 sets it on 20 of 34 paths in every story mission (measured);
  C5/IA1's file carries `nosnapshot` alone.

## Target display keys

A mission's `targets.json` maps world-node names to their objective display text. It is a
**list of target entries**, and each entry is a **list of `[key, value]` pairs**, *not* the
flat-alternating `KEY, [values…]` reader shape (see the [shared conventions](README.md)); it
must be walked as pairs.

```
[
  [ ["description", "MSG_OBJ_TRAINTUNNEL_M"],
    ["nodes", ["dz3"]],
    ["category_label", "MSG_OBJ_DZ"],
    ["help_label", "MSG_OBJ_FLYTHROUGH"] ],
  …
]
```

| Key | Meaning |
|---|---|
| `description` | The target's own name key (`MSG_OBJ_TRAINTUNNEL_M`). |
| `nodes` | List of target(s) this entry labels (usually one; the `dzN` for a zone). A target is a bare world-node name or a nested `[parent, child]` path (`[wv_tailhook, peoplehook]`, `[piratezep, rock_zeppelin]`), the same shape and key `objectives.zrd`'s target directives use ([objectives.md](objectives.md)). |
| `category_label` | The target *type* key (`MSG_OBJ_DZ` = "Danger Zone"). Optional. |
| `help_label` | The *action* key (`MSG_OBJ_FLYTHROUGH` / `MSG_OBJ_FLYOVER` / `MSG_OBJ_REFPOINT`). |

The file is generic across mission types, the same schema labels dogfight zeppelins
(`MSG_TRGT_ZEP_ENEMY` / `MSG_OBJ_DISABLEENG`) and reference points (`ap_transmitter`
radio tower). The stunt loader reads only the entries whose node is a `dzN` from `dzones`.

**Where the file is found.** The engine opens `targets.zrd` through its reader search path
(`FUN_004a2be0` → `FUN_00579c60` → `FUN_00579710`), which every chapter's `init.gw` sets as
`common\zrdr`, `<chapter>\zrdr`, `<chapter>\zrdr\nets`, `<chapter>\<mission>\zrdr`
(`RdrSetPath`/`RdrAddPath`, [interp.md](interp.md)). One file is opened whole, never merged. 52
of the 53 missions ship their own; `C1C/M01` ships none and is labelled by `C1C/zrdr/targets.zrd`,
the one chapter-scope copy, which is where its `Worker's Voyage`, docking-hook and Pandora entries
live. The mission-scope and chapter-scope files never coexist in the shipped data, so which end of
the path wins is unobservable there.

## Message table

`MSG_*` keys resolve through the game's localized string table. **This is not a zrdr
reader**, it is a single top-level file (`extracted/messages.json`, default `--messages=`),
a plain JSON object, not a nested list:

```
{ "language_id": 1033,
  "entries": [ { "key": "MSG_OBJ_DZ", "id": 461, "value": "Danger Zone" }, … ] }
```

Look up by `key`. Relevant stunt values: `MSG_OBJ_DZ` = "Danger Zone", `MSG_OBJ_FLYTHROUGH` =
"Fly Through", `MSG_OBJ_FLYOVER` = "Fly Over", `MSG_OBJ_TRAINTUNNEL_M` = "Train Tunnel Mid",
`MSG_BRF_IASF_OBJ2` = "Fly through all the Danger Zones to win!" (the stunt intro line).
An unknown key resolves to itself (visible, not blank).

`id` is the row's Win32 STRINGTABLE id, and it is how the **exe itself** addresses these strings:
its message formatter `FUN_0059cd70` calls `LoadStringA(<messages module>, id, …)` and hands the
result to `FormatMessageA`, so a hard-coded numeric message id anywhere in the image is a row of
*this* table. The auto-land prompt is the worked example: `0xb5` = 181 = `MSG_PRESS_AUTOLAND`,
`0xb6` = 182 = `MSG_CLICK_AUTOLAND`
([anim-definitions/cutscenes.md](anim-definitions/cutscenes.md#the-prompts-own-wording)).
⚠ **The numbering is this table's alone.** `langui.dll`'s STRINGTABLE
([strings.md](strings.md)) is a different module with its own ids, and carries no row at either
181 or 182; a message id read out of the exe never resolves against it.

## Assembled marker text

The three sources combine into the original's marker string
(`OriginalScreenshots/C1 IA1 Cloudcoverage 1.png`):

```
<category_label> [<help_label>] - <description>   →   "Danger Zone [Fly Through] - Train Tunnel Mid"
```

The remake draws that string through the targeting HUD (`src/Flight/Hud/TargetHud.cs`), since a zone
is an objective on the pilot's own target cycle like any other: the category and help labels make
line 1, the description line 2, and off screen a screen-edge arrow carries the block plus the
relative clock bearing (`… 7 o'clock`), which is computed from the plane's heading rather than
stored in the data.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.