# UI text - `langui.dll` string table

Part of the [format documentation](README.md). `langui.dll` holds numeric Win32 STRINGTABLE
resources for shell UI text. Mission `messages.json` uses symbolic `MSG_*` keys instead; this page
covers its bindable-command table because that data is player-facing UI text.

## Contents

- [DLLs](#dlls)
- [Resource format](#resource-format)
- [Font prefix](#font-prefix)
- [Placeholders](#placeholders)
- [Symbol names](#symbol-names)
- [ID map](#id-map)
- [Bindable commands](#bindable-commands)
- [Known limit](#known-limit)
- [Extraction](#extraction)
## DLLs

| File | Strings | What it is |
|---|---|---|
| `langui.dll` | 1,247 | The localisable UI text. This is the one that matters. |
| `language.dll` | 36 | GameOS engine runtime strings, joystick/sound/renderer error messages, plus the locale name (`English`), its default font (`Arial.ttf`) and LCID (`1033`). Not UI text. |

Both are resource-only DLLs, so a localised build swaps them. The extraction emits both,
tagged with their source in a `dll` field.

## Resource format

Standard Win32, no game-specific quirks, the work is just walking the PE resource directory:

- Resource type `RT_STRING` (6).
- Strings are packed **16 per resource block**; a block's resource id is `(string_id >> 4) + 1`
  and the string's slot within it is `string_id & 15`.
- Within a block, each of the 16 slots is a `u16` length followed by that many **UTF-16LE**
  code units. A length of 0 marks an unused slot, blocks are not required to be full.

## Font prefix

Most strings begin with a bracketed font tag naming the font to render them in,
`[COUR9]Ford Hoplite`, `[CSB9I]The Hughes Bloodhawk is …`. It is markup, not content: strip it
before display. 1,020 of the 1,247 strings carry one; 227 have none and fall back to the
default.

The font table is **self-documenting inside the same string table**. ID 9 is a comment
explaining the convention ("*The FIRST font in the list is the default font. Any string that
doesn't have a specific [FONTID] in it will use this font*"), and IDs 10 onward each declare
one font as `[TAG]/font=<TAG.ttf>`. The most-used tags are `COUR9` (153 strings), `CSB9I` (90),
`AB14I` (88) and the `TREB*` Trebuchet family.

⚠ **29 rows carry an empty tag, `[]`, and the extraction keeps its closing bracket.** They are the
default names and a few sentinels (`IDS_DEFAULT` `]Stock`, `IDS_DEFAULTPLAYERNAME`
`]Nathan Zachary`, `IDS_PILOTPLANENAME` `]Gypsy Magic`, `IDS_KB_JOYSTICK` `]Joystick %1!d!`), so a
reader that shows one of them strips a leading `]` as well as a whole tag.

A handful of strings also carry inline `<B>…<b>` bold markup.

⚠ **The archive ships none of the `.ttf` files the table names, so a tag is read as the Windows face
its letters abbreviate.** `AB` Book Antiqua (bold), `BEL` Bell MT, `CENT` Century, `COP` Copperplate
Gothic Bold, `COUR` Courier New, `CSB` Century Schoolbook, `FREE` Freestyle Script, `IMP` Impact,
`PEP` Pepita MT, `STEN` Stencil, `TNR` Times New Roman, `TREB` Trebuchet MS, `VIN` Viner Hand ITC;
the digits are the point size and a trailing `B` or `I` bolds or slants it. The original's capture
of the scrapbook zoom confirms Viner Hand, Courier New, Impact, Times New Roman, Century and Bell MT
by eye; `AB` is read from its letters alone. A size is points at 96 dpi (an `IMP36` headline's
capital stands about 39 of the 800x600 board's pixels), and a wrapped block's lines are pitched at
that same pixel size, much tighter than the faces' own line metrics.

## Placeholders

Substitutions use the positional `FormatMessage` form, `%1!s!`, `%2!d!`, not bare printf:

> `Your %1!s! is worth <B>$%2!d!<b>. Are you sure you want to sell it?`

Positional indices mean a translation can reorder the arguments, which is the point of the
form.

## Symbol names

`ASSETS/SCRIPTS/RESOURCE.H`, inside the `.rof`, is the Visual Studio-generated header for
`LangUI.rc` and maps symbolic names to IDs:

```
#define IDS_PX_B_AIRFRAME               1004
#define IDS_AIRFRAMEDESCRIPTION         3040
```

It covers **327 of the 1,283 extracted rows**, the ones the GUI scripts and `LAYOUT.CSV`
reference by name. The rest, including most of the aircraft description block, are addressed
numerically and have no symbol. The extraction joins the two, leaving `symbol` null where
none exists.

## ID map

⚠ **These ids are `langui.dll`'s alone.** A numeric message id hard-coded in `crimson.exe` addresses
the `messages.json` table instead ([missions.md](missions.md#message-table)), which is a separate
module with its own numbering; the two overlap in range and mean different things.

Blocks are contiguous and stable, which is what makes the unnamed ranges usable:

| IDs | Contents |
|---|---|
| 9–~60 | The font table (above) |
| 100–199 | Common UI labels (`OK`, `Cancel`) |
| 200–299 | Validation and confirmation messages |
| 500–599 | Pilot names and skill ratings |
| 700–799 | Purchase / sell prompts |
| 1000–1099 | Hangar and plane-customisation labels |
| 1100–1199 | Options screens (graphics, audio, controls) |
| 1200–1219 | Mission-results / scrapbook UI |
| **1220–1224** | **Campaign act names**, `IDS_MISSIONAREA`: Hawaii, Northwest, Hollywood, Rocky Mountains, Manhattan |
| 1225–1299 | Purchase, inventory and key-binding labels |
| **3000–3010** | **Aircraft full names**, "Hughes Bloodhawk", "Curtiss-Wright J2 Fury" |
| **3020–3030** | **Aircraft short names**, "Bloodhawk", "Fury" |
| **3040–3050** | **Aircraft descriptions**, the customisation screen's flavour text |
| 3060–3079 | Gun mount position names (`Nose Turret`, `Outer Wing Guns`) |
| 3100–3199 | Engine names (`Ford v-8`, `Junkers Jumo 230B`, `Bristol Mercury VI`) |
| 3200–3299 | Gun and ammunition names |
| 3300–3399 | Engine / component descriptions |
| 3400–3424 | Rocket names and descriptions |
| 3425–3438 | Militia long names (`IDS_PAINTLONGNAME`) |
| **3450–3473** | **Campaign mission long names**, `IDS_MISSIONLONGNAME`, one per mission in story order, each prefixed with its act ("Hawaii - The Lost Treasure of Sir Francis Drake") |
| **3480–3503** | **Campaign mission short names**, `IDS_MISSIONSHORTNAME`, the same 24 in the same order |
| 3600–3618 | Instant Action mission names (`IDS_IA_CONTENTS`) |
| 3650–3697 | Instant Action environments, mission types, militias, difficulties |
| 3700–3799 | Squadron names (`Hoplites`, `Hellhounds`) |
| 10000–10599 | Multiplayer: lobby, game types, chat notices |
| 20000+ | Key names for the controls screen |

Both campaign mission blocks are indexed by the mission's story position, so the long name is
`3450 + seq` and the short name `3480 + seq`, with `seq` as
[campaign-sequence.md](campaign-sequence.md) defines it. `RESOURCE.H` also declares
`IDS_MISSIONDATE` (3510) and `IDS_MISSIONOBJECTIVES` (3540), but the shipped `langui.dll` has no
strings at either id; treat those symbols as unused.

The three aircraft blocks are **parallel and in the same order**, so
`full_name = 3000 + i`, `short_name = 3020 + i`, `description = 3040 + i` for the same
aircraft `i`, eleven entries, matching the eleven `player_*` aircraft in `planes.zbd`
(see [gamez.md](gamez.md)). Order is Hoplite, Hellhound, Balmoral, Bloodhawk, Brigand,
Devastator, Firebrand, Fury, Kestrel, Peacemaker, Warhawk, the Hoplite and Hellhound first,
then the remaining nine alphabetically.

## Bindable commands

**This section is about the *other* string table**, `messages.json`, the `MSG_*` key table
documented in [missions.md](missions.md), because that is where the game keeps the labels for
its **controls-configuration screen**, and that list is the authoritative inventory of what the
retail game let a player do. **74 bindable commands under seven headings** (measured), plus the
device-name vocabulary the binding UI prints. **Recorded here as the feature-parity target for
the remake: this is the whole player-facing command set, straight from the shipped build.**

Two near neighbours are *not* commands and are excluded from the 74: `MSG_WINGMAN_SHOT_DOWN`
("Wingman was shot down") is a notification and `MSG_DLG_CONTROLS` ("controls") a dialog title,
both read like bindings by name.

**Trust this list over any other source.** It is what the shipped binary offered to bind. It has
already overturned one wrong conclusion: an asset-name sweep found no `spyglass` or `padlock`
file anywhere in `rimage`, `rof` or the chapter textures and concluded both features were cut
before release, they shipped, and the string table says so plainly. See
[verification.md](../verification.md) on why an absent filename proves nothing.

### The seven headings (ids 3005–3011)

`MSG_MOVEMENT_CONTROLS` "Movement" · `MSG_WEAPON_CONTROLS` "Weapons" · `MSG_THROTTLE_CONTROLS`
"Throttle" · `MSG_TARGETING_CONTROLS` "Targeting" · `MSG_VIEW1_CONTROLS` "Views 1" ·
`MSG_VIEW2_CONTROLS` "Views 2" · `MSG_OTHER_CONTROLS` "Other".

### Flight, throttle and weapons

| Key | Label |
|---|---|
| `MSG_NOSE_UP` / `MSG_NOSE_DOWN` | Point Nose Up / Down |
| `MSG_ROLL_LEFT` / `MSG_ROLL_RIGHT` | Roll Left / Right |
| `MSG_RUDDER_LEFT` / `MSG_RUDDER_RIGHT` | Turn Left / Right |
| `MSG_LEVEL_TOG` | Level Off |
| `MSG_INC_THROTTLE` / `MSG_DEC_THROTTLE` | Throttle Up / Down |
| `MSG_THROTTLE_0`…`MSG_THROTTLE_8` | Throttle *n*/8, **nine direct-set bindings**, an eight-notch quadrant |
| `MSG_CMD_TRIGGER` | Fire Guns |
| `MSG_FIRE_MISSILE` | Fire Rockets |
| `MSG_CMD_CYCLE_MODE` | Cycle Weapons |
| `MSG_CMD_CANNON_NEXT` / `_PREV` | Cycle guns counterclockwise / clockwise |
| `MSG_CMD_MISSILE_NEXT` / `_PREV` | Cycle rockets counterclockwise / clockwise |
| `MSG_MISSILE_NEXT` | Next Rocket |
| `MSG_CMD_NITROUS` | Use Nitro-Booster |
| `MSG_JETTISON_FUEL` | Jettison fuel |

**Gun and rocket selection is rotational, not linear**, the labels are "clockwise" and
"counterclockwise", i.e. the selector walks the mounts around the airframe, not up and down a
list. The remake carries both directions of each class on the original's own four keys.

### Targeting, three groups, four verbs

The full suite shipped: `MSG_CMD_TARGET_{NEAREST,NEXT,PREVIOUS}_{ENEMY,ALLY,GROUND}` (nine
commands), plus `MSG_CMD_TARGET_UNDER_RETICULE` "Select Target Nearest Crosshairs" and
`MSG_CMD_TARGET_NOTHING` "Target Nothing". The three groups are labelled "Next Enemy/Objective",
"Next Ally" and "Next Non-Aircraft", ground vehicles, structures and, per the design, Danger
Zones.

### Views, the spyglass and padlock both shipped

| Key | Label |
|---|---|
| `MSG_CAM2_TOG` | **Toggle Spyglass**, a *camera* command (camera 2), not a targeting one |
| `MSG_CMD_PADLOCK_SNAP` | Access Snap Look Mode |
| `MSG_CMD_PADLOCK_STICK` | Access Smooth Look Mode |
| `MSG_CMD_PADLOCK_WATCH` | Track Target |
| `MSG_PADLOCK_M`/`_U`/`_D`/`_L`/`_R`/`_UL`/`_UR`/`_DL`/`_DR` | the nine hat-position look directions (Look Forward / Up / Back / Left / Right / Up-Left / Up-Right / Up-Left-Rear / Up-Right-Rear) |
| `MSG_LOOK_FORWARD` | Cycle Cockpit Views |
| `MSG_LOOK_FLYBY` | Access Chase View |
| `MSG_LOOK_DOWN` / `_BACK` / `_LEFT` / `_RIGHT` | External Camera Down / Back / Left / Right |
| `MSG_CAM_ZOOM_IN` / `_OUT` | External Camera Zoom In / Out |

All three design-document padlock modes are present, and the nine `MSG_PADLOCK_*` directions are
the hat-switch grid the design describes. `player.json`'s `autohead_turn_time` / `_max` /
`_min_pitch` are this camera's turn-lead time, lead cap and elevation floor (see [vehicle.md](vehicle.md#player-global-blocks)).

### Other

| Key | Label |
|---|---|
| `MSG_CMD_PAUSE_GAME` | **Pause/Quit/Objectives**, the objectives display shipped, folded onto the pause key |
| `MSG_CMD_BAIL_OUT` | Bail Out |
| `MSG_CMD_LAUNCH_AUTO_LAND` | Auto-Dock. The in-flight prompt is `MSG_PRESS_AUTOLAND` ("Press %1 to autodock") for a key or a joystick button and `MSG_CLICK_AUTOLAND` ("Click %1 to autodock") for a mouse one |
| `MSG_CMD_INTERP` | Comm Interp |
| `MSG_CMD_KEYMAP_DISP` | View Help |
| `MSG_CMD_DISPLAY_SCORES` | Display Scores (Multiplayer Only) |
| `MSG_CHAT_ALL` / `MSG_CHAT_TEAM` | Chat to Everyone / Team |
| `MSG_CMD_ZONE_TOGGLE` / `MSG_CMD_COLLISION_TOGGLE` | Zone toggle / Collisions, developer switches left in the shipped table |
| `MSG_WINGMAN_ENGAGE` / `_FIRE` / `_BKT_LEFT` / `_BKT_RIGHT` | Engage/Disengage, Wingman Fire, Bracket left / right |

### Binding vocabulary

`MSG_KEYA` "Key A" and `MSG_KEYB` "Key B" confirm **two keyboard bindings per command**;
`MSG_JOYBTN` "Joy Btn" and `MSG_MOUSEBTN` "Mouse Btn" are the other two columns.
`MSG_JBTN_1`…`_10` name ten joystick buttons, `MSG_MBTN_LEFT`/`_RIGHT`/`_MIDDLE` three mouse
buttons, and a `MSG_KEY_*` block (ids 15004–15128) names every keyboard key, Japanese IME keys
included.

## Known limit

- **The leading `]` marker.** A few strings start with `]` (`]Nathan Zachary`, `]0`), some
  in-band flag the shell strips, not yet identified. It appears on pilot names and on the
  4000-block counters.
- **Aircraft index → `player_*` name.** The 3000-block order matches neither the `planes.zbd`
  node order nor the launchscreen roster, and its first two entries break the otherwise
  alphabetical run; a consumer should map by name rather than by index.

## Extraction

The extraction (`UiStringTable`) writes `extracted\rof\ui_strings.json`, one row per string with
`id`, `symbol` (from `RESOURCE.H`, or null), `font` (the parsed `[FONTID]`), `text` (tag
stripped) and `dll`. It needs the `.rof` extracted first for `RESOURCE.H`, which the same run
does.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.