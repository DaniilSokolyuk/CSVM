# Instant Action wrap-up

Part of: [Instant Action](../instant-action.md).

## The wrap-up screen

`IA_WRAPUP.SCRIPT` and `LAYOUT.CSV` wire the post-mission board. The `langui` table carries **six**
consecutive title strings:

| Id | Symbol | Text |
|---|---|---|
| 1133 | `IDS_IAWU_TITLE` | Instant Action |
| 1134 | `IDS_IAWU_TIME_TITLE` | Time to Complete Mission |
| 1135 | `IDS_IAWU_DESTROYED_TITLE` | Enemies Shot Down |
| 1136 | `IDS_IAWU_ZONES_TITLE` | Danger Zones Completed |
| 1137 | `IDS_IAWU_SHOTS_TITLE` | Shot % |
| 1138 | `IDS_IAWU_KILLS_TITLE` | Total Kills |

**⚠ Only four of those six are actually wired to a row, "Total Kills" is not.** `LAYOUT.CSV`
declares exactly four `IAWU_LINE*` brushstroke panes and four value texts (`IAWU_T_TIME`,
`IAWU_T_DESTROYED`, `IAWU_T_ZONES`, `IAWU_T_SHOTS`), plus the screen's own title
(`IAWU_T_TITLE=T,IDS_IAWU_TITLE,…`); there is no fifth data line, no fifth value text, and no
`IAWU_T_KILLS`-shaped row anywhere in the CSV. `IA_WRAPUP.SCRIPT` matches: `gui_init` fills exactly
four strings from one callback (`callback($$E$$, 2352, GT, HT, IT, JT)`) and assigns them to the
four value texts; no fifth variable or object exists. `RESOURCE.H` confirms the format-string side:
there are only four `IDS_IAWU_*` **format** ids (1185 `TIME`, 1186 `KILLED`, 1187 `DANGERZONES`,
1188 `PERCENTAGE`) feeding those four rows, no `IDS_IAWU_KILLS` format id exists at all. Of the six
title strings, `IDS_IAWU_KILLS_TITLE` alone has no reference anywhere outside `RESOURCE.H`'s own
`#define`, every other title, including the screen heading `IDS_IAWU_TITLE`, is referenced by a
`LAYOUT.CSV` row.

So the shipped wrap-up screen shows **four** rows, Time to Complete Mission, Enemies Shot Down,
Danger Zones Completed, Shot %, not five. "Total Kills" is a defined-but-unwired string, the same
class of finding as `strings.md`'s spyglass/padlock case: present in the table, absent from the
screen that would have used it. **This corrects the milestone goal's and A5/G14's "five rows"
framing**, G14 should render four rows, and A5's counter decode only needs to answer for those
four (Shot %'s numerator/denominator remains the open question).

| Row | Format id | Format |
|---|---|---|
| Time to Complete Mission | `IDS_IAWU_TIME` (1185) | `%02d:%02d` |
| Enemies Shot Down | `IDS_IAWU_KILLED` (1186) | `%d` |
| Danger Zones Completed | `IDS_IAWU_DANGERZONES` (1187) | `%d` |
| Shot % | `IDS_IAWU_PERCENTAGE` (1188) | `%d%%` |

### What the four numbers count

 `gui_init` makes exactly one engine call, `callback($$E$$, 2352, GT, HT,
IT, JT)`, and its arm in `crimson.exe` is `0x0040c644`-`0x0040c749`. That arm formats all four
strings and writes them back through the four out-pointers, so the whole board is one function
reading one record.

**The wrap-up reads a frozen snapshot, not the live counters.** `FUN_00443090` is the mission-end
handler: when the mode global `DAT_0071bb80` is **3** (Instant Action, set by the launcher
`FUN_004174d0`) it calls `FUN_00419700`, which calls `FUN_00419630(&DAT_0064ad8c)` to copy the live
counters into the snapshot block at `0x0064ad8c`. Every other mode takes `FUN_004194e0` and a
different layout, which is why the snapshot has two shapes and only the mode-3 one is a set of four
scalars.

The live counters are all fields of **one object at `0x0071d2a0`**, zeroed together by
`FUN_004a22a0` off the mission-load path:

| Object offset | Snapshot offset | Field |
|---|---|---|
| `+0x00`…`+0x2b` | `+0x08` (summed) | kill table A, one dword per aircraft type 0 to 10 |
| `+0x2c` | not read | kills of anything that is not one of the eleven aircraft |
| `+0x30`…`+0x5b` | `+0x08` (summed) | kill table B, same indexing |
| `+0x5c` | `+0x20` | cannon rounds the local player fired |
| `+0x60` | `+0x22` | cannon rounds the local player hit with |
| `+0x88` | `+0x14` | danger zones completed |

**Time to Complete Mission** is snapshot `+4`, `ftol(clock × 1000.0)` over the mission clock at
`0x0071b468`, so it is a straight elapsed time in milliseconds. The row renders
`minutes = ms / 60000` and `seconds = (ms / 1000) % 60` (reciprocal multiplies `0x10624dd3 >> 6`
and `0x45e7b273 >> 14`), both truncating rather than rounding.

**Enemies Shot Down** is the sum of **both** per-aircraft kill tables over types 0 to 10. A kill is
recorded in `FUN_004b9bc0` only when the victim's team (`+0x08`) is **greater than 1**, which is the
decoded team space again, so a friendly or neutral kill never counts. Since the original applies
friendly damage (below), a wingman you shoot down is a kill that this row will not show.

⚠ **Two things are silently excluded.** The victim's category field `+0x67c` must be 0 or 4 to reach
the per-aircraft tables at all; everything else lands in the object's `+0x2c` bucket, and the
wrap-up never reads `+0x2c`. And `FUN_00426e30`, which turns the victim's def name into an aircraft
index, returns **11** for a name it does not recognise, which is one past the summed range. So
ground targets, zeppelins and anything off the eleven-aircraft list do not appear in "Enemies Shot
Down". Which of the two tables a kill lands in is decided by a byte on the victim (`+0x988`, copied
at spawn from roster-block byte `+0xa4`); because this row sums both, that split does not change it
and was not chased.

**Danger Zones Completed** is the object's `+0x88`, incremented by `FUN_00446990`. A zone completes
when **more than one** of its gates has been flown (gate records from `+0x34` to `+0x38`, stride
`0x14`, with a passed byte at `+0x10`), and the increment then happens only if the zone's own latch
byte `+0x40` is still clear. The latch is set immediately afterwards.

⚠ **So this counts distinct zones completed, once each. Flying the same zone a second time does not
increment it.** The row is also present on every mission type; on a mission with no zones nothing
completes one, so it renders `0` rather than being hidden.

**Shot %** is `ftol(100.0 × snapshot+0x22 / snapshot+0x20)`, that is **hits over rounds fired**, and
both halves carry the **same** filter:

- The denominator increments in `FUN_004b6820`, the per-station fire loop, once per round that is
  actually created (`FUN_005aef40` returned non-zero) and only when the firing vehicle is the local
  player (`DAT_0071c298`).
- The numerator increments at three sites (`0x004ba04e` in `FUN_004b9bc0`, `0x004bad0c` in
  `FUN_004bab50`, `0x004c08c1` in `FUN_004c0880`, covering three kinds of thing hit), each guarded
  by the shooter being the local player and by `TEST byte ptr [weaponDef], 0x40`.
- Bit `0x40` is the **`CANNON`** flag. `FUN_004ba6f0` is the weapon-flags parser and its
  `OR dword ptr [ESI], 0x40` at `0x004ba9ba` follows the `CANNON` key string at `0x0062b320`
  ([weapons.md](../weapons.md) lists `CANNON` on 31 entries).
- The fire side carries the same filter even though no `0x40` immediate appears in `FUN_004b6820`:
  the function hoists the bit to `(weaponFlags >> 6) & 1` at the top of each station and the
  increment sits inside that arm. The ordnance arms of the same function create their rounds
  through `FUN_005aef40` without ever touching the counter.

⚠ **So Shot % is cannon hits over cannon rounds fired, both by the local player, and ordnance is
excluded from both halves.** Of the two readings this could have had, it is the one that excludes
ordnance, and it is symmetric. Corroboration that the pair belongs together: `FUN_00499490` packs
exactly these two globals into one network message.

Two edges G14 has to decide about rather than inherit. **Nothing guards a zero denominator**: fire
no cannon round and the x87 divide yields infinity, which `ftol` turns into `0x80000000`, so the row
would print a large negative number. That is read from the instructions, not observed in the
original, and it should be handled deliberately. And **both counters are incremented as 32-bit
dwords but snapshotted as 16-bit words**, so past 65535 the row wraps.

### The hold after the ending

**The original flies on for 3.0 s after the goal is reached, then freezes for 1.0 s, then fades
for 2.0 s, and only then shows this screen.** Six seconds pass between the win and the wrap-up, and
the first three of them are a live world.

All four Instant Action modes share one mission object at `0x0071b480` and one per-frame end
machine. The countdown is the float at `mission+0xc40`, in seconds.

| Stage | Length | Read from |
|---|---|---|
| The live world runs on | **3.0 s** | `PUSH 0x40400000` at `0x0045be4c`, handed to `FUN_00463c30` at `0x0045be58`, which seeds `+0xc40` |
| The frozen frame | **1000 ms** | the `Sleep` in `FUN_004a0af0` (`this = 0x0071d540`), called from `0x0046ba6b` |
| The fade to this screen | **2.0 s** | `_DAT_006272b8` (`0x40000000`), passed by `FUN_00419700` at `0x0041971c` |

- **The win arms it.** `FUN_0045b9d0` is the Instant Action mode tick, dispatched on the mode id in
  `DAT_00718cd8`. Every arm leaves through `LAB_0045be40`, which calls `FUN_00463c10(1)` (the won
  flag, `+0xc58`) and then `FUN_00463c30(1, 3.0f)`, setting the over flag `+0xc54` and seeding the
  countdown. `FUN_004696f0` seeds the same 3.0 s at mission init (`0x004696ff`).
- **The world is not held while it runs.** `FUN_004a0220`, the flying state's world tick, decrements
  `+0xc40` by the frame dt `DAT_009ad744` at `0x004a02a7`, and `FUN_004a0a80` ticks the whole world
  *before* testing the countdown at `0x004a0ad2`. Only three sites read the over flag
  (`FUN_0046a490`, `FUN_004a0220`, `FUN_004a0a80`) and none of them is in the physics or input path,
  so the aeroplanes fly, the wreck falls and the camera moves for the whole 3.0 s.
- **What the countdown reaching zero does.** `FUN_0046ba10` fires when `+0xc40 <= 0.0` and no
  cutscene is running (`+0x6ec == 0`): the 1.0 s `Sleep` and framebuffer capture, then, absent a
  `WIN_ANIM` / `LOSS_ANIM` (`+0x6e4` / `+0x6e8`), `FUN_00443090` at `0x0046bac0`. Game type 3 there
  takes `FUN_00419700`, which writes `DAT_0064b348 = 6`, the wrap-up variant of the shared results
  state that `FUN_00416ad0` branches on at `0x00416b14`, and arms the 2.0 s fade.
- **The countdown's other seeds, which are not this hold.** `FUN_0046a490` re-seeds 3.0 s at
  `0x0046af91` and `0x0046afc4`, and 0.1 s (`0x3dcccccd`) at `0x0046af8a` and `0x0046afbd` when an
  objective carrying the `INSTANTWIN` / `INSTANTLOSS` keyword completed on that very frame. It also
  seeds 3.0 s at `0x0046a524` when the mission's time limit expires, with neither flag set.
  ⚠ `INSTANT` there means an instant mission end, not Instant Action.

⚠ **A death is not held at all.** The player's own end path never touches `+0xc40`:
`FUN_0047e080` case `0xc`, guarded at `0x0047e1fa`/`0x0047e208` on the player's `+0x91d` set and
`+0x91f` clear, calls `FUN_004a0af0(0)` and goes straight to `FUN_00443090`, since the death-cutscene
global `DAT_0071bb68` read at `0x0047e263` is never written anywhere in the image. What delays the
screen there is the crash animation itself, whose RESET is what reaches this case
([objectives.md](../objectives.md), "The mission-end path").

⚠ **The respawn path is a separate arm and must not be confused with the hold.** The
`player_destruction_reset` effect (`FUN_00480480`) calls `FUN_00463c30(0, 0x40400000)` at
`0x004804d0`, which CLEARS the over flag and re-arms the countdown for a later ending. It carries no
delay of its own.

**What CSVM does with this.** `InstantActionRuntime.WrapupHoldS` is the decoded 3.0 s and the world
runs through it. A win leaves the stick and the throttle live, so the player flies the ending out as
the original does, and only the discrete commands (the two triggers, the weapon selectors, respawn)
are swallowed (`FlightController.ControlHold`). The result was settled at the ending, so a hull lost
inside the hold spends no life, takes no pane and leaves the board reporting the win, with the wreck
falling for the rest of the hold. A death takes the same hold with the seat held whole, the stick
neutral over the lever the pilot left, standing in for the crash animation the original waits out.
The 1.0 s freeze and the 2.0 s fade are not reproduced: the ending simply takes the screen when the
hold ends.

Where that ending goes depends on the presentation. Built-in keeps its own board inside the flight
(`src/UI/Screens/IaWrapupBoard.cs`, a stunt run's photographs drawn as a strip under its splits), which
is also where Restart, Exit and photo mode live. Original
leaves the world instead and lands on this page in the menu shell, `src/UI/Menu/Original/`
`OriginalWrapupScreen.cs` over `src/UI/Screens/InstantActionWrapupPage.cs`: the magazine spread, the notepad
carrying the heading and the four decoded rows at their authored positions, and CONTINUE back to the
Instant Action screen in place of the board's Restart. Three pieces of the page are the remake's own,
on space the shipped page leaves empty. Yellow post-its under the four rows carry the lines the
shipped page has no row for: the context line naming the chapter and the mission type, and a stunt
run's split table, shared over a second and third post-it to the left when one will not hold them.
The table's total is left off, since the stunt clock and the mission clock both run from the start
to the ending and the time row already shows that figure. A stunt run's Danger Zone photographs
stand as white-bordered prints in marker order in the same band, to the left of the post-its and
right against them: the grid with the fewest rows whose pictures come within 15% of the widest the
room allows, capped at the built-in strip's 164-pixel thumbnail width, so a short run reads as one
strip and a seventeen-zone run over three post-its shrinks into the room left of the last one. A
photograph still on its way when the page opens draws as an empty print and fills when it lands.
A box above CONTINUE carries the outcome,
ticked on a win and empty on a loss; the original never lost an Instant Action, so the empty box has
no reference art. The four numbers travel as `IaWrapupSnapshot`, read once at the ending
(`src/UI/Menu/MenuReturnDestination.cs`) and never re-read, since the session that counted them is
freed before the page draws. The photographs join it at the handover rather than the ending: the
run completes on its last zone's gate pair, and the camera latches a marker on entering its 15 m
sphere, which can come after the ending or on the ending's own frame after the run's test.

### What the launcher maps

`FUN_004174d0` is the Instant Action launcher and carries two dropdown-to-internal maps worth
having. The mission-type dropdown index becomes the internal id **0 → 0, 1 → 1, 2 → 4, 3 → 2**,
which reproduces the ace / squadron / stunt / zeppelin ordering settled from the parser side above,
now confirmed from the launcher as well. The environment dropdown index becomes a chapter id
**0 → 1, 1 → 5, 2 → 6, 3 → 8, 4 → 2, 5 → 7, 6 → 4**, and the chapter is then loaded as mission **1**
(`FUN_004638f0(chapterId, 1)`), matching `<chapter>/IA1/`.

No chapter folder name exists anywhere in `crimson.exe` (searched: no `c1b`, `c1c` or `c2b`
string), so the executable never spells out which folder an id names. The ids are the eight chapter
folders in alphabetical order, C1 = 1 through C5 = 8, which resolves the map to **C1, C2B, C3, C5,
C1B, C4, C2** and leaves id 3 (C1C) unreferenced. That is the mapping `CSVM/src/UI/Screens/LaunchMenu.cs`
has carried all along, decoded before this plan and restated in its `Chapters` comment, so this
read is a second source for it rather than a new finding. It corrects the "Environment → chapter"
table above, which A5 had wrong on two rows.

## Friendly fire

**The original applies friendly damage.** A round from one aircraft damages another whatever the
two teams are: the path from impact to the drained pool carries no team test, and the team ids gate
the target scan and the radio lines instead. A wingman on the player's side can therefore be shot
down by the player or by another wingman, and an Instant Action flight needs no damage gate of its
own. The decode is [`org/vehicleDamage.md`](../../org/vehicleDamage.md)'s "Teams and friendly fire"
section; the team space it reads (0 neutral, 1 the player's side, 2 and up enemy) is
on [turrets.md](../turrets.md).
