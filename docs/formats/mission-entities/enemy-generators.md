# Enemy generators, `egen.json`

Part of [Mission entities](../mission-entities.md) in the [format documentation](../README.md).

## `egen.json`, enemy generators

An **enemy generator** spawns AI aircraft into a live mission from a host entity. The host is
either a zeppelin (fighters dropped out of its hangar) or a fixed installation, this install
has ground airfields `eairg31`/`eairg32`, a ship `eshipg31`, and a submarine `barracuda`.

| Key | On | Meaning |
|---|---|---|
| `node` | 23/23 | the host world node |
| `vehicle` | 23/23 | nested: `params` (a designer label in the `aiv.json` HEADER's `(slotId, label)` pairs, e.g. `Eairg31_params`; authored on 15 of 23, see the typo note below), `nets` (one or more AI net names), `choose_nets` (`cyclic` throughout) |
| `capacity` | 23/23 | `0` throughout, and **dead data**: the loader reads it only behind a global that is statically zero, so every generator starts with a zero launch budget whatever is authored and launches only what a script, a film or a wave credits. See [Capacity rule and limit](#capacity-rule-and-limit) |
| `max_active` | 23/23 | concurrent live spawns (1/4/5/6/10) |
| `wave_size` | 23/23 | planes per wave (1, once 3) |
| `wave_period` | 23/23 | seconds between waves (1–20) |
| `ind_period` | 23/23 | seconds between individuals inside a wave (0.5–10) |
| `zeppelin` | 17/23 | `[1]`, marks the zeppelin-hangar variant |
| `open_anim` / `close_anim` | 17/23 | the hangar-door animations, run before and after a wave |
| `origin` | 17/23 | the node the fighters appear at, `cargobay` on 16 of 17 |
| `rotation` | 17/23 | `[-90, 0, 0]` throughout, the drop attitude. Read as **three** angles (all converted to radians), not the single value the data suggests |
| `min_altitude` | 17/23 | **100–300 m: the launch gate** |
| `healthy` | 1/23 | the node whose destruction stops the generator (`subhealthy`, the submarine) |
| `moving_path` | 1/23 | bare flag, it makes the take-off path **host-relative**, see [Launching from a surface host](#launching-from-a-surface-host) |

**`min_altitude` is confirmed by the design document by name.** The design specifies that a
zeppelin drops fighters through its hangar door and so must be high enough to do it; that the
generator gets an extra altitude parameter for this; that generation is *held* until the
altitude is reached; and it names the file, `egen.zrd`. The `open_anim` → spawn →
`close_anim` sequence is spelled out there too. This is the one place in this page where the
design document and the shipped data agree field-for-field.

33 of the 53 `egen.json` files are an empty `[null]`, most multiplayer maps have no generator.

**One authored `params` label is a shipped typo.** The linkage is by exact label: an egen
`vehicle.params` value names a designer label in the same mission's `aiv.json` header. 14 of the
15 authored labels resolve; C1/M04's egen says `Eairg32_params` while the header spells it
`Earig32_params` (a transposition), so that generator's roster lookup cannot succeed as authored.
Asserted in `CSVM.Tests/EnemyGeneratorsTests.cs`.

What the engine does with it is decoded. The record parser (`FUN_00452850`) compares the `params`
string against every label in the roster table and stores the matching vehicle record at `+0x3c`;
no match leaves it null. The launch (`FUN_00451bf0`, called from the cycle at `FUN_00452640`) then
takes the template-less branch: it copies the generator's `vehicle.type` string (`+0x44`, empty
because no shipped file authors the key) and asks `FUN_0047b650` for a vehicle def of that name,
which walks the def table with `_stricmp` and returns null for the empty name. The launch still
returns 1, so the cycle books it: `capacityRemaining--`, `active++`, the timer resets, the wave
counter advances and the global `%s_eg%d` ordinal increments. Nothing is built, and the `active`
slot is never freed because no vehicle exists to die, so `eairg32` (`max_active 4`) blocks itself
after four empty launches. The other branch, the first block of the def or the sibling generator's
block, does not exist in the code. CSVM matches this: the launch builds nothing, the ordinal
advances and the slot stays taken (`Session/Roster/AiGeneratorRuntime.cs`); the `player_bhawk` stand-in
is only for a generator that authors no `params` key at all.

## The generator cycle

Decoded from the binary. One generator holds a timer, a next-event threshold, a per-wave counter
and a door state; each tick advances the timer by the frame delta (and stops entirely while the
game is paused).

```
if host is dead                    -> disable this generator permanently
blocked = (wave_size - spawnedThisWave) + active   > max_active
       or (wave_size - spawnedThisWave)            > capacityRemaining
       or (min_altitude set and host altitude < min_altitude)

if blocked:      if door OPEN and timer >= 4 -> close door        # hold, do not cancel
else:
  door OPEN   and timer >= 4 and timer + 8 < nextEvent -> close door
  door CLOSED and timer >= nextEvent - 4               -> open door
  door OPEN   and timer >= nextEvent                   -> SPAWN
```

The capitals matter: `OPEN` and `CLOSED` are two of the door's **four** states, and a door in
motion is in neither, so none of those three rules fires while one is travelling. See
[The door is a four-state machine](#the-door-is-a-four-state-machine).

On a successful spawn: `capacityRemaining--`, `active++`, timer resets to 0, and the wave counter
advances. If the wave is now complete the counter resets and
`nextEvent = ind_period + wave_period`; otherwise `nextEvent = ind_period`.

Three things that reading pins down:

- **`ind_period` and `wave_period` compose, they do not alternate.** `ind_period` is the gap between
  individuals *within* a wave; the gap *between* waves is `ind_period + wave_period`, not
  `wave_period` alone.
- **The door timings are hardcoded, not data.** The door opens **4 s before** a due spawn, stays
  open at least 4 s, and only closes early if the next spawn is more than 8 s away, so a
  fast-cycling generator simply leaves its hangar open.
- **The altitude gate holds, it does not cancel**, exactly as the design document says. The wave
  counter and the timer are untouched while blocked; only the door closes. The gate is skipped
  entirely when `min_altitude` is unset (a `-1.0` sentinel).

One value the decode does not pin: the FIRST `nextEvent` threshold after load. The remake's
implementation (`Session/Roster/GeneratorCycle.cs`) assumes the full inter-wave gap
(`ind_period + wave_period`), the conservative reading, and says so where F20 will revisit it.

### The door is a four-state machine

The door is its own small object on the generator record at `+0xa4`: a handler table, a state at
`+0xa8`, and the resolved open and close definitions at `+0xac`/`+0xb0`. The record parser
(`FUN_00452850`) installs each definition through `FUN_00445500`/`FUN_00445540`, and those two also
register the definition's **completion callback** (`FUN_004ee160` writes it at the instance's
`+0x74`/`+0x78`, the trampolines at `004454e0`/`004454f0`). The four states are `0` CLOSED, `1`
OPEN, `2` OPENING and `3` CLOSING.

`FUN_004455e0` is the open request. From OPEN it succeeds without doing anything. From CLOSED it
splits: with **no** open definition it sets OPEN there and then, and with one it starts the
animation and goes to **OPENING**, where it stays until the animation's completion callback runs
the handler that writes OPEN (`FUN_004454c0`). `FUN_00445620` mirrors it into CLOSING and
`FUN_004454d0`. Neither request does anything from the two moving states, so a door mid-close
cannot be caught and reopened.

**So a generator with authored hangar doors cannot launch on the step its door starts opening.**
The spawn rule tests the door for OPEN, and the open request just before it can only reach OPENING;
the aircraft leaves when the panels have finished travelling, which for the zeppelin
`hangerdoors` definitions is their authored 5 s, one second past the 4 s lead. The generators
that launch on the same step are exactly those with no open definition to run: `barracuda`, whose
defaulted `barra_openda` name resolves to nothing, opens instantly and launches at its threshold.
The same reading says a wave whose door had to reopen is a second late throughout, not only after
a long hold.

**The host's death disables the generator.** For a fixed installation that is the `healthy` node
going inactive; for a zeppelin it is the zeppelin's own destroyed flag, record byte +6, which the
survivor check (`FUN_004bf0b0` → `FUN_004bd780`) sets on the tick survivors drop below
`num_healthy_required`, together with a wreck timer of 3 s (`+8`) after which the hull starts its
sink. The cycle (`FUN_00452640`) reads the flag at the top of every tick, and the objective credit
(`FUN_00469af0`, `WAKEUP_GENERATOR`) only adds to the remaining capacity, so a credit landing after
the kill launches nothing. ⚠ **CSVM deviates here on purpose: a killed host's bay launches for the
wreck timer's 3 s, then disables** (`GeneratorCycle.HostDeathGraceSeconds`), and a launch already
waiting out its door lead completes past that, since the lead alone outruns the 3 s. C5/M04 authors the
race: OBJECTIVE10 completes on three gasbags inactive and naps OBJECTIVE11, the credit for Miles's
launch, 0.5 s later, while the Dante dies on the fourth gasbag. A torpedo salvo kills the fourth
inside that half second, the decoded rule disables the bay before the credit arrives, Miles never
launches, and OBJECTIVE38's `DEDG [5, 0]` fires the loss over an empty group. The designed path
avoids it only because OBJECTIVE11's `all_dtzep_gasbags` spaces its pops 3 s apart. Two further load-time
rejections: a generator whose `node` cannot be resolved is **dropped**, and so is one where **none**
of its `vehicle.nets` names resolve, a generator with no valid net does not load inert, it does not
load at all.

**A non-zeppelin generator also needs a take-off path, and is dropped without one.** `FUN_004518d0`
names it from the host node with `sprintf("%.2s%.3s")`, the first two characters and the last three,
so `eairg31` asks for the host-relative subtree `eag31_aipath` and `barracuda` for `bauda_aipath`;
fewer than two waypoints under it, or a host name shorter than five characters, and the generator
does not load. The launch then places the aircraft on waypoint 0 (+0.2 m Y), faces it down the leg
into waypoint 1, gives it zero velocity and a full throttle lever, and drives it along the path.
This is the same movement law the roster's `taxiPath` uses, entered from a second site
([org/flightModel.md](../../org/flightModel.md), "The scripted-path follower").

Smaller loader findings: `open_anim`/`close_anim` **default from the node name** when unauthored.
The loader (`FUN_00452850`) formats `sprintf("%.5s_open%.2s", node, node + len - 2)`, the first
five characters, `_open`, the last two, so `eairg31` asks for `eairg_open31`; it then formats
`close%.2s` into a second buffer but looks the FIRST buffer up again, so an unauthored close
resolves the open definition, and the close call replays the open. Three unauthored hosts ship
the def that default names (C1's `eairg31`/`eairg32`, C2/M01's `eshipg31`, each a `cam_anim`
def rooted on the host sliding its `ldoor`/`rdoor` 8 m over 4 s, the door lead); C3/M03's
`barracuda` asks for `barra_openda` and gets nothing;
`choose_nets` parses only its first letter and accepts **`random`** as well as the `cyclic` every
file authors; and the `vehicle` block additionally accepts **`primary_target`** and **`title`**,
neither authored in this install.

What the door names resolve to: every authored `open_anim`/
`close_anim` is a **compiled `mis_anim` definition**, root `hangerdoors` under the host
zeppelin, activation OnCall, whose sequences `OBJECT_MOTION_FROM_TO` the hull's `door_left`/
`door_right` nodes 0 → ±90° about Z over **5 s** (close is the reverse), with the open's first
event activating `cargobay`. The ground hangars' defaulted defs above are the same shape at a
smaller scale. Two placement facts that bite: the chapter can
carry several `hangerdoors` namesakes (C1 has three, the zeppelin's and two ground hangars'),
so a door call must be scoped to the host's subtree; and the `cargobay` origin node sits ON the
bay floor inside the hull. The remake releases fighters exactly there: the decoded 1.5 s carrier
grace suppresses collision while the drop clears the hull. C1/IA1's mission setup deactivates its zeppelin at load (`support\c1\ia1.gw`). The mission builder
then activates the zeppelin selected by `zeppelin_type` only for `zeppelin_run`; a `dogfight_squadron`
session leaves it inactive. The same builder path arms the hull's dormant gun rings
([turrets.md](../turrets.md#waking-a-whole-subtree)).

## Launching from a surface host

A generator without the `zeppelin` key does not drop fighters out of a hangar; it runs them off a
**take-off path** authored in the host's own subtree. `FUN_00451bf0` is the launch, `FUN_004518d0`
the host bind, and `FUN_00451440` the path build.

**The path name comes from the host node name**, `sprintf("%.2s%.3s", node, node + len - 3)`: the
first two characters plus the last three. So `barracuda` asks for `bauda`, `eairg31` for `eag31`,
`eshipg31` for `esg31`. A host name shorter than five characters rejects the generator outright.
The points are the nodes `<base>_aip0`, `<base>_aip1`, … under a `<base>_aipath` group, searched
inside the host's subtree and read in order until one is missing. **A path shorter than two points
is a load rejection**, alongside the unresolved-host and no-net drops above.

All three surface hosts in this install ship one, and each reads as a runway:

| Host | Path | Points, in the host's frame |
|---|---|---|
| `barracuda` (C3) | `bauda_aip0..3` | `(0, 2.5, 8.5)` → `(0.5, 3.757, -24.674)` → `(0.5, 6.375, -97.127)` → `(0.5, 10.775, -121.984)` |
| `eairg31` (C1) | `eag31_aip0..4` | `(0, 0, -2)` → `(0, 0, -11)` → `(48, 0, -122)` → `(40, 0, -210)` → `(40, 0, -258)` |
| `eairg32` (C1) | `eag32_aip0..4` | `(0, 0, -2)` → `(0, 0, -11)` → `(-16, 0, -114)` → `(8, 0, -178)` → `(40, 0, -210)` |

The submarine's climbs from 2.5 m to 10.8 m over 130 m of deck and open water; the two airfields'
stay flat and curve, which is a ground roll. Note that the sub's run points along its local −Z
while its `sub_movement` drive travels local +Z: the Barracuda arrives in the bay and launches
back out over the water it came from.

**`moving_path` decides whose frame the points are kept in.** The flag clears a byte that the path
builder tests: set (the default, no flag) bakes each point to world coordinates once at load; the
flag, authored only on `barracuda`, keeps the host node on the path record so the points are
stored host-relative and re-transformed by the host's live matrix at every launch. That is what
lets a generator ride a hull that is still driving. Because the `_aip` nodes sit inside the host's
subtree, reading their live global position gives the same answer without repeating the matrix
work.

**The launch state**, from the multi-point branch of `FUN_00451bf0`:

- position: path point 0 through the host's live matrix, **plus 0.2 m in Y**;
- attitude: the angles from point 0 to point 1, rotated by the same matrix, so the path's own
  climb supplies the pitch;
- velocity: **zero**, against the zeppelin drop's inherited carrier velocity;
- throttle: the field pair at `+0x124`/`+0x128` set to **1.0**, where the drop sets them to 0.1;
- the path is kept on the aircraft at `+0xc8` with the flag at `+0xcc` and the leg index zeroed
  at `+0xd0`, which is the take-off run it then flies under the scripted-path follower
  (`FUN_0048a110`, docs/org/flightModel.md "The scripted-path follower": 40 mph along the run,
  accelerating and climbing out on the final leg, the flag cleared at the final leg's 300 m point,
  past a short strip's last waypoint), and which
  also suppresses the net-nearest-node snap an ordinary activation makes (`FUN_004b0f40`), so the
  run places the aircraft and the net takes over where the run ends.

There is no altitude gate and no spawn-height offset on this path. `min_altitude` is a zeppelin
key and does not appear on a surface record.

**The host node's own matrix never places a surface launch.** `FUN_00451bf0` reads the host's
world matrix (`FUN_004cf200`, the node's accumulated transform) only on the zeppelin branch
(`generator+1` set), where the drop point is the `origin` node; a surface host takes waypoint 0
of its path on every launch, and the branch that would read the host with no path is unreachable
because the loader has already dropped a host whose path is shorter than two points. So a
model-less group host is no special case: C2/M01's `eshipg31` is an identity-transform group
under `generators` under the terrain tile `g36347`, its geometry `ship_gen.flt` and its
`esg31_aip0..5` points carrying the world translation themselves (`(-5888.8, 0.02, -4408.0)` to
`(-6141.7, -0.04, -4435.7)`, a 253 m run west along the water), and the launch lands on
`esg31_aip0` in the original and here alike. The bbox centre is not read by anything in the launch.

**A ship generator launches a hull.** `Eshipg31_params` names `patrolboat_eg0`, a `mode ship` def
(`patrolboat`, docs/formats/vehicle.md), and the same spawner `FUN_0047c210` builds it and sets
the path flag on it, so the boat runs `esg31_aip0..5` under the scripted-path follower and joins
`M2Patrol1` where the path ends; the mission's `SET_AI_NET [patrolboat_egN, M2GoosePatrol]`
clauses then walk each launch between nets. CSVM: `GeneratorLaunch.Surface` in
`Session/Campaign/CampaignRoster.cs`, built by `Session/World/SurfaceVehicleRuntime.cs` and launched down the
path by `Session/Roster/AiGeneratorRuntime.cs`; never the CLI airframe in a hull's place.

## A launch flies its net like a roster aircraft

Nothing on the net-following path reads a field only a launch carries, so a generated aeroplane takes
a tagged node's `dzpath` run exactly as a roster one does, and the direction it walks its net is not
authored anywhere: each assignment seats the walk on the nearest node and takes the edge best lined
up with the nose, `SET_AI_NET` included ([org/aiPilot.md](../../org/aiPilot.md), "The danger-zone
run"). C5/M04 is the shipped case, the install's only tagged node on a generator's net: the Dante's
bay launches Miles onto `M4MilesStage`, and OBJECTIVE60 moves him onto `M4MilesRun`, whose node 2
names `dzpath34`. The spawner's own ordering has one consequence for the launch pose: the net is
assigned while the aircraft still wears the generator's authored `rotation`, so a drop's seat is made
with the nose pointing straight down.

## Launch names

Every launch is renamed, `sprintf("%s_eg%d", base, counter)`. The base is the roster block's own
name truncated at its **last** underscore, so C3/M03's `britpeace_5` template launches as
`britpeace_eg0`; a generator with no roster block uses its authored vehicle name whole. The
counter is a single mission-global value: zeroed when the egen file loads and advanced by every
successful launch of any of the mission's generators, so C3/M03's four Barracuda launches are
`britpeace_eg0` through `britpeace_eg3`. The spawner swaps the template block's name string for
the launch, spawns from it, and restores the block's own name afterwards, so the block stays
reusable while each launched aircraft carries a distinct identity.

## Capacity rule and limit

`capacity` is `0` on all 23 generators, and the engine never reads it. The loader
(`FUN_00452850`) takes the authored value only when the global `DAT_0071bb34` is non-zero; that
global is statically zero and has no writer in the executable (its other readers are the turret
file loaders `FUN_004ac170`/`FUN_004ac480`, which return nothing for the same reason, and
`FUN_00453330`). Otherwise it writes `+0x7c` as 0, and `capacityRemaining` (`+0x80`) is copied
from it. Every generator therefore starts with a zero launch budget, and the cycle's rule

```
(wave_size - spawnedThisWave) > capacityRemaining     ->  blocked
```

holds it until something adds credit. **A generator nothing credits never launches.** Four sites
add credit, and they are the whole list:

| Site | What it adds |
|---|---|
| `FUN_00469af0`, the objective wake-up apply | `WAKEUP_GENERATOR name n` adds `n` (default 1) to the named generator ([objectives.md](../objectives.md)) |
| `FUN_0045b9d0`, the Instant Action wave director | each newly-current wave's member count, to the objective zeppelin's generator ([instant-action.md](../instant-action.md)) |
| `FUN_0047e080`, the mission-script host | cutscene `CALLBACK 800` adds 5 to the generator named `cargozep1`, the name hardcoded; authored once in the shipped data, as the first event of C4/M03's `cg_beauty_shot` ([cutscenes.md](../anim-definitions/cutscenes.md)) |
| `FUN_0043d640`, the console | `kick <generator>` adds 1 |

C3/M03 is the script case: the patrol phase completes before the script credits `barracuda` by 4;
its `vehicle.params BarracudaPlanes` then selects the authored-disabled `britpeace_5` AIV block,
so launches are Peacemakers configured from that template and appear at the submarine's live pose.
C4/M03 is the film case: the mission authors no `WAKEUP_GENERATOR`, so its `cargozep1` generator
(`Cargo_params`, the disabled `bsfury_1` block on net `M3Allies`; `max_active 10`, `ind_period 3`,
`wave_period 1`) sits idle from load until the docking film `cg_hookup_player` calls the
hangar-view `cg_beauty_shot`, whose first event is code 800. It then launches its five credited
Furies one every 4 s through the opened hangar doors: the freed crews of the briefing's "Dock and
free the crews". Before that the mission's only allied aircraft is the Black Swan's own
(`bswingman_1`). Instant Action's `zeppelin_run` credits per wave the same way: nothing launches
before the first credit, and one wave's worth may launch after it.

## The launch hook carries a staged aircraft

Codes 801 to 803 belong to CM19's launch hook rather than to a generator, and the hook is the one
mechanism in the shipped data that puts an aircraft into the air off a definition. What rides the
hook down is not the roster aeroplane the code wakes: it is a **staged prop**, a separate display
model the mission parks on the hook before the first launch.

Four such props exist in the install, all in C4: `anim2_warhawk`, `anim2_brigand` and
`anim2_autogyro` for the launch hook, and `anim_warhawk` for the Black Swan's own hook. Each is an
`Object3d` node with its `active` bit **clear**, no parent, and a model subtree, so the `world1`
walk never reaches it and it draws nothing until a definition moves it.

`startanims.zrd`'s `NEW_GAME_START` list is what wakes them: it names `bm_warhawk_state`,
`ai_warhawk_state`, `ai_brigand_state` and `ai_autogyro_state`, each anchored on its own prop. A
state definition switches the prop (off for the three launch-hook ones, on for `anim_warhawk`),
calls the matching `*_hook_startup` on it, and calls its `*_place` definition. A place definition
is two or three events:

```
OBJECT_ADD_CHILD        [bmhookpoint, anim2_warhawk]
OBJECT_TRANSLATE_STATE  [anim2_warhawk, (0, -2.8, 0.2)]
OBJECT_ACTIVE_STATE     [warlaunchhook, INACTIVE]
```

Three nodes in C4's gamez are called `bmhookpoint`, one under each of `warhawk_bmhook`,
`player_bmhook` and `warlaunchhook`, so a name match alone picks the wrong hook two times in three.
The definition's own symbol table is what decides: the original binds every node reference once at
load and looks it up by index at run time, with no name comparison on the event path
([sequences.md](../../org/sequences.md)).

**The add-child is a plain relink.** Dispatch slot 15, `004eab20` in the 47-slot table at
`DAT_00727de0`, reads the parent and child node pointers out of the instance's own table, returns
without doing anything when the child already stands among the parent's children, and otherwise
calls `FUN_004cd500`. It tests neither the child's active bit nor whether the node is placed, so
moving an unplaced, switched-off root into the visible tree is ordinary rather than a special case.

`launch_warhawk` is then the whole of the launch's appearance: it switches the prop on, switches
`warlaunchhook` on, fades the hook in over 0.75 s, runs the hook's first SI script for the ride
down, switches the prop off, raises `CALLBACK 801`, and 2 s later runs the second script and
switches the hook off again. So **the hook and the aeroplane on it are visible only during a
launch**, and the roster Black Hat that 801 wakes takes over from the prop at the bottom of the
ride. The Black Swan's own hook is the exception: `bm_warhawk_place` leaves `warhawk_bmhook`
switched on and `bm_warhawk_state` switches `anim_warhawk` on, so that display aeroplane hangs
there from the first frame of the mission.

## CSVM handling

CSVM resolves a surface host's take-off path at load, drops the generator when it is shorter than
two points, and launches on the decoded pose: point 0 plus 0.2 m, nose on point 1, at rest with
the throttle open. An aircraft then flies the take-off **run** under the scripted-path follower
and is handed to the flight model at the final leg's 300 m point, climbing; a hull runs the same
points and joins its
net where they end (`Flight/Ai/SurfaceVehicle.cs`).

**The door hold is kept, its length is not.** The remake's cycle carries a two-state door and no
animation clock, so a spawn released past its threshold with the hangar shut opens the door and
then waits the hardcoded 4 s lead, where the original waits out whatever its open definition takes
(5 s on the zeppelins). The split by definition is reproduced: `AiGeneratorRuntime` plays the
transition, and a door whose animation starts nothing releases the hold in the same step
(`GeneratorCycle.ReleaseDoorHold`), so `barracuda` launches at its threshold as decoded. Two
consequences of the four-state decode are not reproduced: a wave whose door reopens on schedule
still launches on its threshold rather than a second late, and a door caught mid-close reopens
where the original refuses. Pinned by `GeneratorCycleTests` over the four shapes that leave a cycle
overdue behind a shut door, and by the `generator-callback-credit` and `zeppelin-launch` suites.

**CSVM runs the credit rule as decoded.** `Session/Roster/GeneratorCycle.cs` never reads the authored
`capacity`: every cycle starts at zero remaining and blocks while the wave's remainder exceeds it,
so an uncredited generator holds its timer and keeps its doors shut for the whole mission.
`AiGeneratorRuntime.GrantWaveCapacity(host, n)` is the one credit, fed by the objective apply
(`WAKEUP_GENERATOR`, with `vehicle.params` resolving the disabled AIV template for each fresh
spawn), by the `zeppelin_run` director's one wave's member count at each wave change, and by
cutscene callback 800, which the runtime answers from its place in the `CALLBACK` host chain as
five launches on the generator named `cargozep1`, the original's literal. `--wake-generators`
grants a script's whole `WAKEUP_GENERATOR` credit at build, the logged headless stand-in for
playing up to the objective. Codes 801 to 803 are no generator's: they belong to CM19's launch
hook, and `CampaignDirector` answers them from its own link ahead of this one
([cutscenes.md](../anim-definitions/cutscenes.md)). Pinned by the `generator-callback-credit`
suite over C4/M03 and `GeneratorCycleTests`.

**CSVM stands the staged props up at world build.** The remake's world walk builds placed content
only, so a prop no walk reaches has no node for the place definition's `OBJECT_ADD_CHILD` to move
and the hook flies empty. `WorldSession.BuildStagedProps` builds each one beside the cutscene
composition frames and switches it off, leaving the definitions to park and reveal it;
`GameZ.IsStagedProp` is the test (an unplaced `Object3d` with its active bit clear and geometry
under it) and the event's parent must be placed content. Install-wide the rule fires on the four
C4 nodes above and nothing else, since an unplaced root that ships **switched on** is a call
template served by the library-root pool instead. Pinned by the `campaign-launch-hook-prop` suite,
which reads which of the three `bmhookpoint` nodes each prop landed under and drives
`launch_warhawk` for the reveal.
