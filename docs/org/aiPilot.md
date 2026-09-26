# The AI pilot: what a roster vehicle flies, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), 2026-08-15, settling `BL-364` and the decode half of
`BL-362`, and extended 2026-08-16 with the merge rule. Every claim below names the function or
address it came from, and the two data censuses name the files they counted.

Everything here is a description of *behaviour*. No decompiler output is reproduced; the addresses
are given so any claim can be re-checked at source.

**Where the other halves live.** The authored roster side is
[`formats/ai-rosters.md`](../formats/ai-rosters.md) (`netids`, `primary_target`, the skill vector);
the authored airframe side is [`formats/vehicle.md`](../formats/vehicle.md) (`mode`, `attack`,
`return_range`, `preferred_engagement_altitude`); the patrol graphs themselves are
[`formats/ai-nets.md`](../formats/ai-nets.md). The flight physics an AI shares with the player is
[`flightModel.md`](flightModel.md); the firing half (when an AI pulls the trigger, and the
`weapons` 5-tuple that governs it) is [`aiPilot/aiWeapons.md`](aiPilot/aiWeapons.md). CSVM's
implementation seam is `src/Flight/Ai/AiPilot.cs`, the driver that picks each mode's aim point and
parameter block; the steering law it hands them to is
`src/Flight/Ai/AiControlLaw.cs`, decoded in [`aiControlLaw.md`](aiControlLaw.md).

## The headline: there is no netless patrol

The engine has two AI flight behaviours for an aeroplane, and which one it runs is decided at spawn
by one authored key and one authored field:

- a **patrol-net follower**, which requires a net and has no fallback if it has none;
- a **formation escort**, which requires a `primary_target` and never looks at a net.

An aircraft with no patrol net does not fly a degenerate straight line and does not loiter. It
either flies a formation station on its leader, or it is a `jet` with no net, which the engine
builds dead.

⚠ **The shipped data does produce one netless `jet`, and the original builds it dead.** C1/M04's
`blakepeace_2_2` (team 2, group 4, enabled, `deactivated 0`) authors slot 0 as an empty list, every
volume as `0.0` and its spawn at the world origin. The spawn reader `FUN_0047c210` takes the id when
the list count is 1, draws `rand() % count` when it is higher, and otherwise writes `-1` to `+0x2e4`
(`0x0047c76d`); nothing on that path skips the block. What decides its fate is the vehicle build
`FUN_00476250`: it sets the dead byte `+0x91d` to 1 and the current edge `+0x2ec` to `-1` before net
assignment (`0x0047636b`), and clears the dead byte again (`0x004763df`) on exactly two paths, a
block whose `mode` is still `wingman` after the demotion test below, or a `netids >= 0` whose
assignment `FUN_00475fc0` left a real edge in `+0x2ec`. A `jet` with `netids == -1` takes neither,
so it is built dead: the world tick skips it (`+0x91d == 0 || +0x91f != 0`), targeting skips it,
`DEDG` does not count it, and `WAKEUP_ENEMIES` cannot revive it because the hidden bit `+0x945`
stays clear. The net follower `FUN_0041d1f0` would look `-1` up in the chapter net table
(`0x0041d20a`–`0x0041d228`), find no match and index record `-1`, 100 bytes before the table's first
record, with no guard, but it is never reached for this vehicle. Read live under the debugger on
C1/M04: the vehicle sits in `VehicleList` at (0, 0, 0) with `+0x91d = 1`, `+0x945 = 0`,
`+0x944 = 0`, `+0x2ec = -1` and its spawn speed still in `+0x934`, and the 100 bytes before the net
table hold a null node-array pointer, so the read would have faulted had it run. CSVM does not spawn
the block (`CampaignRoster.cs`, the plan's `Skipped` list).

## `mode`, the dynamics class

`vehicle.json` carries a per-def key **`mode`** (the string at `0x628084`), parsed by
`FUN_00479240` at `0x0047afe8`–`0x0047b081` into the def struct at `+0xa4`. `FUN_00475820`
(`0x00475abf`) copies it to the vehicle at `+0x67c` when the vehicle is built.

| `mode` | value | debug readout | flight update |
|---|---|---|---|
| `jet` | 0 | airplane | `FUN_0048e580`, the aeroplane integrator |
| `heli` | 1 | autogyro | `FUN_0048ffe0` |
| `tank` | 2 | ground vehicle | `FUN_0048a880` |
| `ship` | 3 | ship | `FUN_0048b480` |
| **`wingman`** | **4** | airplane | `FUN_0048e580`, the same as `jet` |
| `plane` | 5 | bomber | `FUN_0048b480` |

The physics dispatch is `FUN_00489ea0`; the readout names are the engine's own, from the debug
overlay `FUN_0041c470` (`0x0041c852`). **A `wingman` is a full aeroplane**, flying the same
integrator, aerodynamics and collision sweep as the player. Only its AI differs.

The shipped `vehicle.json` authors `mode` on four defs and inherits the rest through `kind_of`
(census of all 75 defs, 2026-08-15):

- **`jet`**: `basic_airplane` and therefore all 11 player defs, all 11 base AI aircraft, and all 39
  militia variants (`r*`, `bhat*`, `sti*`, `blake*`, `ha*`, `sec*`, `med*`, `brit*`, `rus*`, `bs*`,
  `germanhellhound`, `hkfirebrand`). ⚠ `autogyro` and `pautogyro` are `jet`, not `heli`.
- **`wingman`**: exactly 12 defs, being `wingman`, `bswingman`, and the eleven `w<plane>`
  (`wbloodhawk`, `wfirebrand`, `wbrigand`, `wfury`, `wautogyro`, `wavenger`, `wkestrel`,
  `wpeacemaker`, `wbalmoral`, `wwarhawk`, and `wingman` itself off `devastator`).
- **`ship`**: `patrolboat` and `t_truck`.
- Nothing ships `heli`, `tank` or `plane`.

A sibling key `mode_alt` parses to def `+0xa8` (`0x0047b0a6`) and is `0.0` on `basic_airplane`. No
consumer of it was identified.

## The per-frame AI update

The world tick `FUN_004897c0` walks the vehicle list and, for each vehicle that is present
(`+0x91d == 0`) and awake (`+0x944`), calls the AI update `FUN_0041c270`. That function forks in
this order:

1. Byte `+0xcc` set suppresses the AI entirely and the function returns.
2. `FUN_0041fe10` runs target selection and writes the selected target to `+0x948`.
3. If the selected target was lost and the current task is pursue, the task reverts to the vehicle's
   default task `+0x2f4` and a timer at `+0x300` is set to now plus `+0x308` (`0x0041c299`).
4. **`+0x67c == 4` dispatches to `FUN_0041e760`, the escort law, and nothing else runs.**
5. Otherwise the AI task `+0x2f0` selects the steering (`0x0041c2e6`): `1` is pursue
   (`FUN_0041d9f0`), `0` and `2` are both the patrol-net follower (`FUN_0041d1f0`), and any other
   value returns without steering.
6. After the net follower, a live selected target is handled on the vehicle's own `mode`
   (`0x0041c37c`). A `jet` or a `tank` promotes the task to pursue (`FUN_0041f040` sets
   `+0x2f0 = 1`) and its shot comes later, from inside the pursue behaviour. **`ship`, `plane` and
   `heli` are not promoted at all**: the per-mount lead solver `FUN_0041afe0` runs, and when it
   reports a firing solution on at least one mount the update calls the fire decision
   `FUN_0041f420(1, 1)` itself (`0x0041c348`). Those three classes never pursue. They shoot while
   still flying their net.

Firing is otherwise not one of the steering branches. All three behaviours call the fire decision
`FUN_0041f420` themselves, each with both weapon classes enabled, and it is decoded in
[`aiPilot/aiWeapons.md`](aiPilot/aiWeapons.md).

### What a `mode ship` vehicle runs

A patrol boat and a turret truck are ordinary members of the vehicle list `DAT_0071dabc`, and
nothing on the path from the world tick to the trigger tests for a surface hull:

- their weapon list is built from the def's own `weapons` block by `FUN_004b59b0`, the builder the
  def parser calls for every vehicle ([`aiPilot/aiWeapons.md`](aiPilot/aiWeapons.md)), so
  `patrolboat` and `t_truck` each carry one `wep_29` at 9000 rounds, a 0.3 s refire and a 1 to 500 m
  window;
- `FUN_00476250` gives every vehicle a gun mount whether or not the def authors gun limits, and
  neither def authors any; but both models carry a `turret` and a `gun` node, so the mount is the
  ANIMATED one, which brings its own elevation band and a slew whose lag the aim residual pays
  for ([`aiPilot/aiWeapons.md`](aiPilot/aiWeapons.md), "A `mode ship` vehicle's mount");
- `FUN_004897c0` calls the AI update for every awake vehicle, with no mode test at the call;
- the target scorer is the non-`jet` one, `FUN_00421950`, selected on `+0x67c` at `0x0041fe75`;
- the fire decision's quick-draw gate applies only when the *shooter's* mode is `jet` or `wingman`,
  so it cannot suppress a boat.

⚠ **`t_truck`'s standalone-turret entry in `ai.zrd` is a second gun, not the truck's only one.**
The truck carries the vehicle gun above like the boat, and its `t_truck**` emplacement is an
addition on top. So a patrol boat, which no `ai.zrd` `NODES` pattern matches, is **not** authored
silent, and the emplacement file settles nothing about a hull's own weapon.

### `attack_dwell` and `not_pursuit_dwell` are pursuit timers

`attack_dwell` parses to def `+0x5c` and `not_pursuit_dwell` to def `+0x60` (`0x00479da1`,
`0x00479dbc`), and `FUN_00475820` copies them to vehicle `+0x304` and `+0x308`. Both feed the one
timestamp `+0x300`, which paces a pursuit at both ends:

- `FUN_0041f040` refuses to promote, returning 0, while `DAT_0071c470 <= +0x300`
  (`0x0041f069`–`0x0041f07a`), unless the standing target at `+0x948` is the vehicle's assigned
  `primary_target` at `+0x2fc` (the two objects' `+0x4` compared at `0x0041f048`–`0x0041f05e`) or the
  debug flag `DAT_0064f66e` is set;
- on promoting it sets `+0x300` to now plus `+0x304` (`0x0041f106`), except that a `jet` or
  `wingman` promoting onto a target that is not a `TargetVehicle` gets a hardcoded 20.0 s instead
  (`0x006035e4`, `0x0041f0ea`);
- losing a pursued target sets `+0x300` to now plus `+0x308` (step 3 above);
- the pursue behaviour's tail reverts the task the moment `+0x300` passes (`0x0041e674`), so the
  same stamp that made the aeroplane wait before the chase is also what ends it, and the revert
  re-arms it to now plus `+0x308`, or plus a hardcoded 15.0 s (`0x00603560`) on the same
  `jet`/`wingman` non-`TargetVehicle` condition.

`basic_airplane` authors `attack_dwell 60` and `not_pursuit_dwell 5` and every aeroplane def
inherits them, so an unassigned chase runs a minute and the next one may start five seconds later.
⚠ **Neither field does anything on a `ship`, a `plane` or a `heli`**, since those classes never
reach `FUN_0041f040`. The boat's own copies of the same two values are inert. What paces a boat's
target churn is the sticky-target hold below.

The AI mode enum lives at `+0x358` and is a different thing from the task: 1 evasive maneuver,
2 approaching danger zone, 3 avoid crash, 4 stunned, 5 navigating danger zone, 0 otherwise.
⚠ **`patrol`, `pursue` and `lay off` are not stored states.** The debug readout derives them
(`FUN_0041c470`, the `AI mode:` string block at `0x0041c98b`): no selected target prints `patrol`; with a target,
`+0x2f0 == 1` prints `pursue` and anything else prints `lay off`. So "lay off" is literally *has a
target and is flying its net*, and "patrol" is *has no target*.

## Target acquisition: four candidate classes, not one

`FUN_0041fe10` is step 2 above. It picks the target, and `FUN_0041f9c0` is the sweep it delegates
to. The candidate pool is **four separate global lists**, each with its own `Target` subclass; the
RTTI names survive in the binary.

| Class | List | Constructor | vtable | Admission | Rank offset |
|---|---|---|---|---|---|
| `TargetVehicle` | `DAT_0071dabc` | `FUN_004a6330` | `0x0060886c` | `0x004a5b20`: `+0x91d` clear, `+0x944` set, hostile team | candidate `+0x340` |
| `TargetTurret` | `DAT_0071d914` | `FUN_004a6370` | `0x006088cc` | `0x004a5bd0`: `+0x6c` and `+0x6d` set, hostile team | scorer `+0x344` |
| `TargetStruct` | `DAT_0071d33c`–`DAT_0071d340` | inline | `0x0060364c` | `FUN_004a5b90`: `+0x8c` and `+0x90` set, hostile team; plus object `+0x8d` set and `+0x65` needs gasbag ordnance | scorer `+0x344` |
| `TargetProjectile` | `DAT_0064f78c` | `FUN_004a63b0` | `0x00608920` | object `+0x6c` set | none |

`FUN_0041f9c0` carries one running minimum across all four lists, so the pick is the global minimum;
the return chain resolves projectile, struct, turret, vehicle only because a later list records a
winner solely when it already beat the earlier ones. The Admission column is the `+0x34` virtual
[`targeting.md`](targeting.md) decodes as the hostility predicate, reached through `FUN_00422890`,
whose own second arm (reject a candidate below the query's Y) is inert here because the query
builder `FUN_00421e00` zeroes the enable byte at query `+0x14`. `TargetProjectile` is the consumer
of the `TARGETABLE` weapon flag.

### `target_bias` and `struct_bias` are the two class-dependent rank terms

There is **no class priority** in the sweep: one running minimum, and the only terms that depend on
what class a candidate belongs to are two `vehicle.zrd` fields the spawn path copies onto the
entity in `FUN_00475820`, `def+0x138` to `+0x340` and `def+0x13c` to `+0x344`
([`../formats/vehicle.md`](../formats/vehicle.md)). The vehicle constructor `FUN_004aff80` zeroes
both at `0x004b088e`/`0x004b0894`, so a def that authors neither spends nothing.

- **`target_bias`, candidate `+0x340`,** added when this entity is the candidate and only on the
  `TargetVehicle` arm. Shipped: `-300.0` on `player_pfighter`, `-100.0` on the twelve AI aeroplane
  defs, and unauthored on `patrolboat`, `t_truck` and every zeppelin.
- **`struct_bias`, scorer `+0x344`,** added to every turret and structure candidate and to no
  aircraft. Shipped: `-200.0` on eleven aeroplane defs, unauthored elsewhere.

⚠ **Both are negative, so under minimisation both ATTRACT.** An equidistant structure is therefore
ranked about 100 m AHEAD of an equidistant AI aeroplane by an aeroplane that authors both, and
about 100 m behind the player. Read `+0x344` as a structure preference, not as the handicap its
position in the arithmetic suggests. A turret's own picker spends neither: `FUN_004aabb0` pushes a
literal `0` for both the second and third arguments (`0x004aaf95` and `0x004aaf99`, EBX zeroed at
`0x004aaf93`), so `struct_bias` is an AI aircraft's term alone and a turret admits no gasbag.

### A dead vehicle's surviving parts stay candidates

⚠ **Nothing in the acquisition asks whether a part's parent vehicle is alive.** The only liveness
gate a structure candidate passes is `+0x8c`, the scene node's own active bit ANDed up the parent
chain, which `FUN_004a2730` recomputes every frame at `0x004a2840` from `FUN_004a32c0`
(`0x004a32c0`: return 0 the moment any level of the `+0x58` chain has bit 2 of `node+0x24` clear).
A zeppelin's death does not clear that bit on the airship node. C3/M04's `killpzep` carries exactly
one `ObjectActiveState`, `underneath` to false, and instead destroys six of the hull's twelve engine
destructibles outright by calling `destroy_pzleng11`/`21`/`31` and `destroy_pzreng11`/`21`/`31`,
each of which deactivates its own `healthy` node. The other six (`leng12`/`22`/`32`,
`reng12`/`22`/`32`, each its own `INACTIVEn` in the mission's objectives) keep their active bit and
stay in the struct list as ordinary candidates. So a dead airship's surviving engines are an ally's
legitimate targets in the original too, and the choreography, not the picker, is what removes the
rest.

⚠ **World structures are always swept, by every scorer class.** The fourth argument to
`FUN_0041f9c0`, which decides whether the struct list is walked at all, is the literal `1` pushed at
`0x00420002`, on no branch and under no test of the scorer's `+0x67c`. The push survives the
intervening `CALL FUN_00420070` because that one is `__fastcall` with its `this` in ECX and no stack
argument of its own, and the call site's `ADD ESP, 0x10` at `0x0042001f` counts the four dwords
(`1`, EAX, ECX, EBP) back off. So a `wingman` sweeps structures on the same terms as a `jet`, and
its only acquisition difference is the ignored `primary_target` below. Only the turret caller
conditions the argument, pushing `0` at `0x004aaf92`. Within the list, the gasbag members are the
conditional ones.

### The scorer, and which one runs

`rank = weight × 1200 + distance + objectiveBias`, minimised, as
[`ai-rosters.md`](../formats/ai-rosters.md) has it. Two implementations, selected on the scoring
vehicle's own `+0x67c` at `0x0041fe75`: `FUN_00421ad0` (vtable `0x00603544`) for `jet` and
`wingman`, `FUN_00421950` (vtable `0x0060354c`) for everything else. The debug overlay
`FUN_0041c470` duplicates both instruction for instruction, which is where the readout's formula
came from.

Weight starts at **1.0**, or **0.7** when the candidate is the player (`DAT_0071c298`). Then:

- **+0.4** when the candidate casts to `TargetVehicle` and its `+0x67c` is **4**, that is, when the
  candidate is a **`wingman`**. Minimised, so this is 480 m against it: the engine de-prioritises
  enemy wingmen. The readout's "one dynamics class" is this, and "dynamics" is the overlay's label
  for the `mode` field above.
- **−0.5** when the `Target`'s virtual at vtable `+0x1c` returns true. `TargetVehicle`,
  `TargetTurret` and `TargetProjectile` all bind `0x00422720`, a constant false. Only
  `TargetStruct` binds `0x004227a0`, which returns the object's `+0x65` flag, and the overlay calls
  that same virtual to print `Gasbag targeted: %s`. So the readout's "one structure case" is a
  **zeppelin gasbag**, worth 600 m in its favour, and nothing else in the game takes the term.

`FUN_00421ad0` adds three more terms that `FUN_00421950` does not have, so **a ground or sea AI
scores on base weight and the two class terms alone**. Let `d` be candidate minus self, in metres
and not normalised:

- `d` dotted with the scorer's own forward row (`+0x198`–`+0x1a0`): above **+0.5** adds **+0.2**,
  below **−0.5** adds **−0.2**, between them adds nothing. ⚠ This is an ahead/behind test with a
  half-metre deadband, **not** a cone, and **ahead is the unfavourable arm**. The engine prefers the
  target on its tail.
- `d.y > 0`, the candidate above, adds **+0.2**; otherwise **−0.2**.
- `d` dotted with the candidate's own forward (its vtable `+0x04`) negative, meaning it faces the
  scorer, adds **+0.2**; otherwise **−0.2**.

The admission test is a **cylinder**, not a radius: horizontal `d.x² + d.z²` at or under
`+0x328`, and `+0x32c ≤ d.y ≤ +0x330`. Outside it the score is `1e21` and the candidate is never
picked. `FUN_00421ad0` also returns `1e21` when the candidate object's `+0x04` field is 3 or more,
and `FUN_00422890` is a validity check whose failure scores the same.

⚠ **That cylinder is the ATTACK volume and it is the SCORER's own.** The base of all three loads
is `*(param_1 + 0x18)`, the scoring vehicle, so the volume belongs to the pilot doing the looking
and never to what is being looked at. The field map the net-assignment section below records puts
activation at `+0x318`/`+0x31c`/`+0x320` and attack at `+0x328`/`+0x32c`/`+0x330`.

### Every reader of the attack and activation triples

Each of the six fields was swept for by displacement across the whole image (648,780 instructions),
so these lists are complete for a direct `[reg + disp]` access; the only sites that take a triple's
ADDRESS instead are the two copy-and-clamp ones named below.

**The attack triple, `+0x328` / `+0x32c` / `+0x330`.** Three readers, and all three are the same
admission test:

| Where | Function | The condition it applies under |
|---|---|---|
| `0x00421b47`–`0x00421b67` | `FUN_00421ad0` | the `jet`/`wingman` scorer: outside the cylinder the rank is `1e21`, so the candidate is never picked |
| `0x004219ac`–`0x004219cc` | `FUN_00421950` | the scorer every OTHER `mode` runs, the same three fields, the same `1e21` |
| `0x0041cb5d`–`0x0041cb7d` and `0x0041cf2a`–`0x0041cf4a` | `FUN_0041c470` | the debug overlay, duplicating both scorers for the readout |

⚠ **The non-`jet` scorer admits on the attack cylinder too.** `FUN_00421950` drops the three
geometry terms, not the admission, so a `ship`, `plane`, `heli` or `tank` is bounded by the same
volume an aeroplane is.

Its writers are the constructor's zero (`FUN_004aff80`, `0x004b0106`), the def copy
(`FUN_00475820`, `0x00475a62`), the net assignment (`FUN_00475fc0`, `0x00476128`), the roster block
(`FUN_0047c210`, `0x0047c888`), and the script command **`SET_AI_ATTACK_RADIUS`** (`FUN_00469f70`,
`0x00469f93`), which stores r² into `+0x328` and ∓r into `+0x32c`/`+0x330`. "Where a hull's attack
triple comes from" below walks all five in the order a spawn runs them.

**The activation triple, `+0x318` / `+0x31c` / `+0x320`.** One reader that decides anything:

| Where | Function | The condition it applies under |
|---|---|---|
| `0x00489a28`–`0x00489a48` | `FUN_004897c0` | the world tick's AWAKE test, cylinder measured to the player |

⚠ **The activation volume is the simulation gate, not a targeting range.** The offset it tests is
the player's position (`DAT_0071c298`) minus this vehicle's, and what the test writes is the awake
byte `+0x944`: an asleep vehicle gets neither the AI update `FUN_0041c270` nor its physics that
frame. The cylinder is the last of five disjuncts. Byte `+0x91f` set wakes the vehicle outright;
otherwise `+0x354` must be zero, and then the AI-suppressed byte `+0xcc`, a default task `+0x2f4`
of 2, a `primary_target` at `+0x2fc` whose own vtable `+0x14` test fails, byte `+0x324`, or the
cylinder each wake it on their own.

Its writers are the constructor's zero (`FUN_004aff80`, `0x004b00ee`), the def copy
(`FUN_00475820`, `0x00475a4b`, through the copier at `0x004830e0`), the net assignment
(`FUN_00475fc0`, `0x004760d7`), the roster block (`FUN_0047c210`, `0x0047c81d`), the
`min_ai_active_dist` clamp, which reads the field back to compare (`FUN_00476250`, `0x004763fe`;
`FUN_0047c210`, `0x0047c922`), and the `DEDG` widening (`FUN_00465850`, `0x0046586c` read,
`0x0046587f` write, raise-only). **No script command writes it**: `SET_AI_ATTACK_RADIUS` has no
activation counterpart anywhere in the image.

⚠ **So a `DEDG` widening never reaches acquisition.** Both volumes ship at 2,000 m and the widening
raises the activation triple alone ([../formats/objectives.md](../formats/objectives.md)), so a
watched group is SIMULATED out to 9,000 m while every member still admits candidates only inside
its own 2,000 m attack cylinder. A net or a roster block that authors the two volumes differently
parts them the same way.

### Where a hull's attack triple comes from

`patrolboat` and `t_truck` author neither `attack` nor `kind_of`, so what their scorer admits on is
settled by the def record's own constructed default. Five writers touch
`+0x328`/`+0x32c`/`+0x330`, and a roster-block spawn runs them in this order:

| # | Where | Addresses | The condition it applies under | A shipped hull |
|---|---|---|---|---|
| 1 | vehicle constructor `FUN_004aff80` | `0x004b0106`, `0x004b010c`, `0x004b0112` | unconditional, `EBX` zeroed at `0x004affaf` | 0, then overwritten |
| 2 | def copy `FUN_00475820` | `0x00475a5f`–`0x00475a74` | unconditional, the def record's `+0x44`/`+0x48`/`+0x4c` straight across | **160000.0 / −400.0 / +400.0** |
| 3 | net assignment `FUN_00475fc0` | `0x00476128`, `0x00476143`, `0x0047615c` | per field, only where the net's own float is non-zero (`FCOMP` against `0x006032c8`) | no write |
| 4 | roster block `FUN_0047c210` | `0x0047c888`, `0x0047c8a6`, `0x0047c8c2` | the same per-field non-zero gate, after the def copy, so an authored slot wins | no write |
| 5 | `SET_AI_ATTACK_RADIUS` `FUN_00469f70` | `0x00469f93` r², `0x00469fa1` +r, `0x00469faf` −r | only where a mission script issues it, on the vehicle `FUN_004aff10` resolves by name | never reached |

The spawn order inside `FUN_0047c210` is `0x0047c51d` the constructor, `0x0047c550` the def copy,
`0x0047c77b` the `min_ai_active_dist` clamp, then the block's own volume writes from `0x0047c803`.
`FUN_00475fc0` is not called from the block spawn at all; its callers are `FUN_004a6610`
(`0x004a66eb`), `FUN_00476250` (`0x004763d2`) and `FUN_00475f30` (`0x00475fa9`).

**The 400 m is the def record's constructed default.** With no `kind_of`, `FUN_004735b0` takes the
no-parent branch at `0x00474c42` and builds the record through `FUN_00478a00`, which writes
`+0x44 = 160000.0` (`0x00478a69`), `+0x48 = −400.0` (`0x00478a70`) and `+0x4c = +400.0`
(`0x00478a77`). The def parser `FUN_00479240` overwrites those three only inside its `attack` token
branch (`0x00479cf5`–`0x00479d5a`, the token string at `0x627e30`), which a def authoring no
`attack` skips at the `JZ` at `0x00479d05`. A def that does name a parent inherits the parent's
triple through the copy constructor `FUN_00477b70` (`0x00477f34`, `0x00477f37`), which is the chain
walk `Mech3/VehicleDefs.AttackOf` performs.

Nothing in the shipped data overrides it. All 23 hull blocks in the campaign (C1/M05's twelve
`patrolboat_1..12`, C1B/M03's four, C2/M01's `patrolboat_eg0`, C5/M01's two boats and four
`t_truck_1..4`) author roster slots 8–19 as zero, and all fourteen nets those blocks reference
author elements 2–10 as zero. **So a shipped boat or truck ranks candidates inside 400 m**, not
inside the 2,500 m its `activation` authors. Its `weapons` window of 1 to 500 m is the looser gate
of the two and its far end never binds.

⚠ **`SET_AI_ATTACK_RADIUS` reaches a hull in the original and does not here.** `FUN_004aff10`
resolves the named vehicle whatever its `mode`, where `Session/CampaignDirector.SetAiAttackRadius`
reaches aircraft rigs carrying a `Pilot.Machine`. No shipped mission authors the directive, so the
hull half is deliberately unported rather than overlooked.

### The third volume, and where the leash is read

The return triple `+0x334` / `+0x338` / `+0x33c` also has exactly one reader: the tail of the pursue
behaviour `FUN_0041d9f0`, which runs on every frame the task is pursue and in every AI state but the
danger-zone approach. It is a CYLINDER about the anchor at `+0x348`–`+0x350`, offset by the vehicle's
own position at `+0x204`–`+0x20c`, and any one of five terms reverts the task:

| Term | Where | The test |
|---|---|---|
| dwell | `0x0041e674`–`0x0041e68b` | the timestamp `+0x300` has passed (`DAT_0071c470` is the clock), skipped while `DAT_0064f66e` is set |
| validity | `0x0041e68d`–`0x0041e69a` | the standing target's own virtual `+0x14` reports it gone |
| horizontal | `0x0041e69c`–`0x0041e6b5` | `dx² + dz²` exceeds `+0x334`, which holds r² |
| below | `0x0041e6b7`–`0x0041e6c5` | `dy` is under `+0x338`, which holds −r |
| above | `0x0041e6c7`–`0x0041e6d5` | `dy` is over `+0x33c`, which holds +r |

The revert at `0x0041e6d7` writes the default task `+0x2f4` into `+0x2f0` and re-arms `+0x300` to now
plus `+0x308` (`0x0041e72b`), or plus a hardcoded 15.0 (`0x00603560`, `0x0041e713`) for a
`jet`/`wingman` whose target is not a `TargetVehicle`. It leaves `+0x948` standing, so the next
frame's selection may hand the same target straight back, and the promotion's dwell refusal is what
keeps the aeroplane on its net until the re-armed stamp passes.
**None of the five reads the activation volume.** The parse fills all three fields from one token
(`0x00479de9`–`0x00479e09`, as `r²`, `+r`, `−r`), and only a second and third token part the band from
the radius (`0x00479e14`, `0x00479e22`); `basic_airplane` authors `return_range 1200` alone and every
def inherits it, so every aeroplane flies a 1,200 m radius with a ±1,200 m band.

**The anchor is the PURSUER's own pose, written exactly once per promotion.** `FUN_0041f040` reads
the vehicle's own world index at `+0x2e8` through the scene accessor `FUN_00432140` and stores that
position at `0x0041f0a6`. The per-frame update `FUN_0041c270` reaches the promotion only from the
patrol branch (task `0` or `2`), never while the task is already pursue, and the AI state at `+0x358`
is a separate field, so a stun, a climb-out or an evasive program leaves task and anchor standing.
A pursuit is leashed to where the chase began, not to the spawn and not to the last maneuver.

⚠ **The whole revert is skipped for an assigned `primary_target`.** The guard at
`0x0041e5a6`–`0x0041e5bc` jumps straight to the return at `0x0041e73d` when `+0x2fc` is set and the
standing target at `+0x948` has the same `+0x4` object, the same predicate the promotion's dwell gate
uses; the anchor offset (`0x0041e5c2`) and the five terms run only otherwise. A vehicle chasing the target its
roster block assigned it neither waits to promote nor ever reverts.

⚠ **The dwell pair caps every other chase at 60 seconds.** `basic_airplane` authors `attack_dwell 60`
and `not_pursuit_dwell 5`, inherited install-wide, so a promotion sets `+0x300` to now + 60 and the
first disjunct fires when that passes: an aeroplane chasing anything but its assigned target holds
the pursuit for a minute, reverts, and cannot promote again for five seconds. The geometry terms only
end it sooner. Subtracting `(1 − +0x8f4) × +0x304` at `0x0041e65c` shortens the deadline while the
quarry is the player and the player is chasing back.

### The promotion has no range, team or net gate of its own

`FUN_0041c270` calls `FUN_0041f040` on every frame the task is patrol (`0` or `2`), the selected
target at `+0x948` is live and `+0x67c` is not `ship`, `plane` or `heli` (`0x0041c37c`). The promotion
reads no distance, no team, no net flag and no volume: its one refusal is the dwell above, and its
only other input is what `FUN_0041fe10` selected. Every admission term (team, `rating_biases`, the
attack cylinder, the gasbag gate, the 20 s hold) therefore lives in the selection, and a target that
is selected is chased the frame the dwell allows it. The enemy and the friendly side run the same
path; nothing in either function reads the team beyond the scorer's own hostility test.

### Where the activation and attack ranges are read

The range a pursuit starts inside belongs to the selection, and the promotion trusts it. The three
functions on the path, in the order `FUN_0041c270` runs them each frame:

| Step | Function | What it reads |
|---|---|---|
| select | `FUN_0041fe10` | the assigned `primary_target` (`+0x2fc`) first, re-scored through the scorer every frame and kept while the score is under `1e20` (a `wingman` skips this arm); then the standing target re-scored while the hold `+0x94c` stands; then the sweep `FUN_0041f9c0` |
| score | `FUN_00421ad0` (`0x00421b47`), `FUN_00421950` (`0x004219ac`) | the attack cylinder `+0x328`/`+0x32c`/`+0x330`, outside which the rank is `1e21` and never picked |
| promote | `FUN_0041f040` | the dwell stamp and the primary match (`0x0041f048`–`0x0041f07a`), no position at all |

`min_ai_active_dist` is the global `DAT_0071c3ec`, written once by the level loader `FUN_004735b0`
(`0x00474139`/`0x00474141`) and read only by the two activation clamps (`FUN_00476250` at
`0x004763f4`, `FUN_0047c210` at `0x0047c922`). The activation triple it floors is read only by the
world tick's awake test (`0x00489a28`, the table above). Neither reaches `FUN_0041c270`,
`FUN_0041f040` or `FUN_0041d9f0`. So the attack radius gates a pursuit exactly once, as the
scorers' admission, and the primary arm is held to it because it runs through the same scorer.

CSVM applies that admission in `AiTargetRanking.Score` (a sphere of `AttackRange`, where the
original tests a cylinder), in `SelectBest` and `KeepsStandingTarget`, and in the primary pick of
`FlightController.SelectRankedTarget`. `FlightController.HoldsStandingTarget` keeps an assigned
primary only while it is inside `AttackRange`, the port of the primary arm's per-frame re-score.
`AiModeMachine` tests no range of its own.

### What CSVM ports of the dwell and the leash

`AiModeMachine` keeps the one stamp against its own clock (the sum of its `Update` steps) and reads
`attack_dwell`/`not_pursuit_dwell` through `PlaneStats` from the def chain, whose compiled defaults
are 60.0 and 3.0 (`FUN_00478a00`, `0x00478a93`/`0x00478a9a`) under `basic_airplane`'s authored 60
and 5. A promotion (and any other path that opens a new pursue task, an ordered one included) arms
the stamp to now + `attack_dwell`, or 20 s when `PursuitQuarry.IsVehicle` is false (a turret or a
structure; aircraft and hulls are the `TargetVehicle` class). `Patrol` refuses the promotion while
the stamp stands unless the quarry is the gunner's `PrimaryTargetName`. `Pursue`, `LayOff` and
`Evade` with a pursue task revert when the stamp passes or the pursuer leaves the return cylinder,
both skipped for the primary, and re-arm the stamp to now + `not_pursuit_dwell` (15 s for a
non-vehicle). A lost target re-arms `not_pursuit_dwell`. The 20/15 s condition is the pursuer's
`jet`/`wingman` mode in the original, which every shipped aeroplane def is, so the port tests the
quarry's class alone. Where it still diverges:

- The per-frame revert during an evasive program is deferred to the moment the program ends.
- The `player_attack_time_factor` term is not ported. `+0x8f4` is def `+0x294` (parsed at
  `0x0047b625`, token at `0x00628348`), constructor default 1.0 (`0x004b0313`), authored 0.8 on
  `basic_airplane`, and a roster block's `+0xd8` overrides it (`0x0047d431`/`0x0047d445`). When the
  quarry is the player and the player's own standing target is this vehicle, the deadline shrinks
  by `(1 − 0.8) × 60` = 12 s (`0x0041e65c`/`0x0041e662`).
- The other `+0x300` writers are not ported: the roster spawn's 0 to 10 s first wait (`FUN_00476250`,
  `0x004764b9`, called from `FUN_0047c210` at `0x0047c77b`, `rand()/32767 × 10.0`), the world tick's
  wake re-arm to `U(0,1) × +0x308` (`FUN_004897c0`, `0x00489b4f`), and the freeze-all/unfreeze-all
  pair `FUN_0041f250`/`FUN_0041f2e0` (now + 3e10 with `+0x354` set, then now + `U(0,2)` s). The
  constructor zeroes the stamp (`0x004b086a`), which the port matches.

### `rating_biases` returns rank units directly

`FUN_0041ae40` walks the bias list at scorer `+0x8a4`, entries of 0x14 bytes with the bias float at
`+0x10`, matching through `FUN_0041add0` and, for every candidate that is not a `TargetVehicle`,
walking the parent chain at `+0x54` / `+0x58` (below). It returns a value added raw to
`weight × 1200 + distance`:

| Authored bias | `TargetVehicle` | `TargetTurret` |
|---|---|---|
| no matching entry | `0` | `+37.5` |
| `≤ −1.0` | `FLT_MAX`, which every caller maps to `1e21` | same |
| `≥ 1.0` | `−100000` | `−99962.5` |
| otherwise | `bias × −750` | `bias × −750 + 37.5` |

⚠ **An authored `−1.0` is a hard exclusion, not a penalty.** The turret column is uniformly the
vehicle column plus 37.5, so a turret carries a flat 37.5 m handicap against an aircraft.

#### The parent walk belongs to every non-vehicle candidate, and it restarts the list

The name matched is `*(*(target + 4) + 0xc)`, the candidate object's world node, whose name is
inline at the node's own offset 0. `FUN_0041ae40` forks on one `__RTDynamicCast` to
`TargetVehicle`: an aircraft or a hull gets a single pass over the bias list and nothing more,
while **everything the cast rejects** takes the outer `do … while` that climbs `+0x54` (parent
count) / `+0x58` (parent array) and walks the whole list again at each level. So a `TargetStruct`
climbs the same chain a `TargetTurret` does; the flat `+37.5` is what the turret cast alone adds
afterwards. The loop restarts the list per level rather than trying two names per entry, so a
candidate's own name beats an owner's entry standing earlier in the authored order.

A docked airship reaches the AI as one candidate per part, each named `leng31` or `rturN`, and the
only place its own `cargozep1` appears is above them. That is why C4/M03's `bswingman_1` exclusion
and the Black Hat Warhawks' `["cargozep1", 1.0]` both work in the original and needed the chain
here.

CSVM ports the chain as `TargetPool.CollectOwners`: a zeppelin record's fanned `Owner` first, then
each world-tree node above the pool's anchor by its original gamez name, handed to
`AiTargetRanking.ObjectiveBiasFor`, which restarts the bias list per name. C4/M03's `cargozep1`
carries 21 damage pools, and the `structure-part-bias` suite reads the exclusion and the
always-target off every one of them.

### What reaches the struct list

A struct-list member is **one object per mission-structure node**: the loader `FUN_004a2e00` walks
`DAT_0071d35c`–`DAT_0071d360`, builds a 0x94-byte object per entry (`FUN_004a2570`), reads the
gasbag flag `+0x65` from node word `+0x28` bit 22 and sets the acquisition admission byte `+0x8d`.
That range is the list `FUN_004a2be0` fills, which is every scene node carrying **bit 31 of
`node+0x28`** and nothing else ([`targeting.md`](targeting.md)). The AI's structure pool is
therefore the authored mission-structure set, not every destructible standing in the world, and a
pool no flagged node owns is not a candidate for any pilot at any range.

A `targets.zrd`-only object is excluded from it twice. The loader's second loop, over
`DAT_0071d34c`–`DAT_0071d350`, builds the curated player-cycle entries, leaves `+0x8d` clear and has
`+0x90` cleared at `0x004a3000`; `FUN_004a5b90` requires `+0x8c`, `+0x90` and `+0x8d`, so either
omission alone keeps the entry out. The cycle the player tabs through is not the AI's pool.

CSVM's marker for the same set is an authored team on the destructible, since only a
mission-structure node's `field040` ownership slots and a zeppelin record write one:
`AimCandidateSet.AddMissionStructures` admits that set for the AI sweep, while the player's assist
and the turret path keep `AddStructures` over every destructible.

### The gasbag gate is ordnance, checked at admission

`FUN_00420070` walks the scorer's weapon list (`+0x268` to `+0x26c`, stride 0x30) for a weapon
whose def flags at `+0x210` carry bit **`0x1000`**, with ammo above zero and both cooldowns at or
under `DAT_0071c470`. Bit `0x1000` is `DAMAGES_ZEPPELIN`, set by the weapon parser `FUN_004ba6f0`
at `0x004ba960` from the keyword string at `0x0062b2ec`. The result is pushed as the third argument
at `0x00420011`, and it admits the `+0x65` members of the struct list.

So a pilot without gasbag ordnance is never offered a gasbag at all. It still sees the airship's
engines, turrets and cannons, which are ordinary members of the turret and struct lists.

### Three more rules the acquisition carries

1. **A `wingman` ignores its `primary_target`.** `0x0041feb0` skips the assigned-target branch when
   the scorer's own `+0x67c` is 4. This is the escort law's reading confirmed from a second site:
   for a `wingman` that field is a formation leader, not a target.
2. **An assigned `primary_target` wins outright** whenever it scores under `1e20`, without the pool
   being swept.
3. **A standing target is sticky for 20 seconds.** `FUN_004b0f20` sets `+0x94c` to now plus a
   hardcoded **20.0** every time a target is taken, so the hold is an engine constant and no def
   authors it. The test is `DAT_0071c470 < +0x94c`, now against the hold's expiry: while the hold
   stands, the standing target alone is re-scored and it is kept while it scores under `1e20`, and
   the pool is swept whole only once the hold has run out or that re-score fails. A re-take stamps
   the hold again, so a pilot that re-picks the same object holds it another twenty seconds.

⚠ **Deconfliction is a count, not a pool drop.** `FUN_0041fe10` decrements `target+0x8` and
`target->object+0x4` before scoring and restores them after (`0x0041fece`, `0x0041ff0b`), so
`object+0x4` is a live count of how many AI hold that target and the scorer simply excludes its own
contribution. There is no drop-and-reselect pass.

### What pursue does with a non-vehicle target

`FUN_0041d9f0` case 0 casts the standing target to `TargetVehicle` once, and the cast decides three
things. A victim that is a `jet` or `wingman` gets the aspect test against its backward row
(`+0x18`–`+0x20` of the thing at target vtable `+0xc`), the lead offset along that row when the test
fails, and the 400 m merge rule. Anything else, a turret or a structure, takes the on-axis arm
unconditionally: the aim point is the object's own position (target object vtable `+0x0`), the
gun-lead flag is set, and the desired velocity is the object's velocity virtual (object vtable
`+0x4`, the same slot the ranking's closing term reads), scaled by 0.9 on the normal arm. So a pilot
whose sweep picked a gasbag engine flies straight at the engine, and the zeppelin hull under it is
what the crash-avoidance ray then sees.

### What CSVM ports of this

`FlightController.SelectRankedTarget` sweeps `TargetVehicle`/`TargetTurret`/`TargetStruct` (the
gun aim assist's own three lists, the struct arm narrowed to the flagged set by
`AimCandidateSet.AddMissionStructures`) for one global minimum into `AiGunner.Target`, a standing
target of any class, which `AiPilot` reads through `PursuitQuarry.Of` as its pursuit quarry: the mode
machine promotes on it, and `FlyPursuit` takes the on-axis arm above for a non-aircraft one and
never arms the merge rule against it. The vehicle arm is the WHOLE `VehicleList` through
`ProjectilePool.CollectVehicleList`, so a surface hull is a candidate beside the aircraft, and the
terms that are properties of an aeroplane are read off the source's own type: an aircraft supplies
the `primary_target` name, the player and `wingman` flags and the `rating_biases` name, while a
hull supplies its own node name and nothing else. Nine missions author a `patrolboat*` or
`t_truck*` hard exclusion against hulls, which is what those pilots' `-1.0` entries then mean.
`AiTargetRanking.Score` is the decoded arithmetic term for
term: the `wingman` **+0.4** (an aircraft flying `AiPilot.Escort`, which is the netless `mode
wingman` fork), the half-metre deadband on the raw offset for ahead/behind, the altitude sign, the
closing term on the candidate's velocity, and the gasbag **−0.5**. The gasbag admission gate is
`FlightController.HasGasbagOrdnanceReady`, walking the pylons for a `DAMAGES_ZEPPELIN` weapon with
ammo whose two launch timers have run out, and the gasbag identity reaches `AiRocketeer.Solve`, so
a torpedo-armed pilot that picked a gasbag launches at it.

Both class terms are ported as they ship. `PlaneStats.AiTargetBias` and `PlaneStats.AiStructBias`
read `target_bias` and `struct_bias` off the def chain the vehicle spawns as, so an AI variant takes
its own and a flown aeroplane takes `player_airplane`'s **−300**, and `AiTargetRanking.Score` adds
whichever the class calls for in raw rank units beside the objective bias, signs untouched. A
structure candidate therefore carries the 200 m pull and a vehicle candidate its own 100 m one, in
the aeroplane picker and in a `mode ship` hull's gun alike. A turret's own picker spends neither,
which is what the original does.

The hold is ported too. `AiGunner.TakeTarget` stamps `AiGunner.TargetHoldSeconds` (the engine's
20.0) and keeps the winning `RankedTargetCandidate`, and `FlightController.HoldsStandingTarget`
re-scores that one candidate at the target's live position every tick, dropping it and sweeping the
pool whole the moment the rank fails or the hold runs out. A target written straight onto
`AiGunner.Target` by a mission order or an airframe swap carries no rank snapshot and keeps the
older rule that alive is enough, which is what an assigned `primary_target` gets in the original.

CSVM then departs from the original deliberately. `AiTargetRanking.AircraftFirst`, on by default and
settled once per launch from `--ai-targeting=` ([`../cli.md`](../cli.md)), withdraws every turret and
structure candidate while any aircraft still ranks, in `FlightController.SelectRankedTarget` and
`TurretController.AcquireTarget` both, so a wingman and the guns of the airship beside it go after
the same enemies and an ally fights a structure only with no aeroplane in reach. The same preference
runs inside the hold through `AiTargetRanking.KeepsStandingTarget`, so an enemy aeroplane coming
into reach takes an ally off a camp building at once rather than at the hold's end. A hull is neither
class and keeps its ranked place. `--ai-targeting=decoded` puts the single running minimum back and
the bare decoded hold with it, and the order is then the two biases' alone.
`Flight/Ai/SurfaceGunner` never takes the preference, since it
drops non-aircraft candidates anyway.

The admission volume comes out as the attack one in every picker. `AiTargetRanking.Score` refuses a
candidate past the `attackRange` it is handed, and `FlightController.SelectRankedTarget`, its
re-score `HoldsStandingTarget` and the withdrawal's reach test all hand it
`AiModeMachine.AttackRange`, so a member whose activation volume a `DEDG` widened keeps its own
attack radius for what it may pick up. `Flight/Ai/SurfaceGunner` is handed the radius
`SurfaceVehicleRuntime` resolves for the hull at spawn, the block's and net's attack slot over the
def's own `attack` over the 400 m def record default, which is the engine's own order (see "Where a
hull's attack triple comes from"). `AiModeMachine.ActivationRange` is left where the spawn seeds it
and reaches the ranking nowhere.

Unmodelled, named rather than guessed: the attack volume is scored as a sphere where the engine
tests a cylinder (`+0x328`, `+0x32c`–`+0x330`), so the altitude band is unported on both the
acquisition and the awake test (the return volume is the one triple CSVM does test as a cylinder,
see "The third volume, and where the leash is read"); the awake test itself, `FUN_004897c0`'s use of
the activation volume, has no CSVM counterpart at all, since every rig ticks every frame;
`TargetProjectile` is not
part of the acquisition sweep; and `FUN_00421ad0`'s `1e21` on a candidate object whose `+0x04` reads
3 or more.

## The chapter's net table, and what "the first net" means

`DAT_0064f610` points at a 16-byte header built by `FUN_004311c0`: an allocation figure at `+0x00`,
the entry count at `+0x04`, the array of entry pointers at `+0x08`, a flag at `+0x0c`. Each entry is
12 bytes: the net **id**, a strdup'd **name**, and the loaded net object. The count is
`(recordElements − 2) / 2` and the loop walks the parsed `neindex` record forward from element 1,
so **entry order is the file's pair order** (`FUN_00431300` is the matching teardown).

That order is what indexes the nets themselves: a lookup scans the table for a matching id and uses
its **position** to reach the net record at `*(DAT_0064f614 + 4) + index × 100`, 100 bytes per net.
Nothing sorts, so the table's first entry is simply the first pair in `neindex.zrd.json`, which is
**not** the lowest id: in this install C1B opens on id 29 (`Patrolboat3`) and C1C on id 25
(`M1Defense`), both against a lowest id of 11. Every consumer that takes "the first net" (Instant
Action's three branches below) means this entry.

`FUN_00475f30` is the by-name form (`SET_AI_NET`'s string arm): it walks the same table comparing
each entry's name, then calls `FUN_00475fc0` with the entry's id.

## The trailer: an anchored net rides its target

A net record's trailer is `[anchorNodeIndex, "name"]`. `FUN_004314e0`, which builds a `CCENet`
from the parsed record, stores the anchor index at net `+0x18` and resolves the NAME to an object
once, at build: `FUN_004d0280(7, name)`, the by-name lookup over the kind-7 registry, into net
`+0x1c`. A record with no name leaves `+0x1c` at 0.

Every node position the engine reads goes through `FUN_00432140(net, out, nodeIndex)`, which is a
thin wrapper over **`FUN_00432010`**, and that is where the trailer does its work. With a resolved
target and an anchor index other than −1:

```
target = the trailer object's world position   (FUN_004cf2c0, refreshed once per frame,
                                                cached against the frame stamp at net +0x00)
anchor = node[net +0x18]                        the net's own stored anchor position
out.x  = (node.x - anchor.x) + target.x
out.y  =  node.y                                ⚠ authored altitude, NEVER the target's
out.z  = (node.z - anchor.z) + target.z
```

So **the graph is a pattern carried horizontally by the named object**, keeping each node's
authored height. With no trailer, or an anchor of −1, the node's stored position is returned
verbatim. Because every consumer goes through this one function, the offset applies to the nearest
-node scan (`FUN_00431900`), the follower's current target and the edge geometry alike: nothing
sees the static coordinates.

⚠ **This is what "an AI escorts something" is in the shipped data.** 76 of 222 nets are anchored,
and the census of their targets is in [`../formats/ai-nets.md`](../formats/ai-nets.md): zeppelins,
a train, a tanker, and **`player` on 11 of them**. C1's `M4ReinfAce` (the chapter's FIRST net, so
the one every Instant Action actor is handed) is `[10, "player"]`: a single 10-node cycle, every
node degree 2, whose node 10 is the edgeless anchor. In the original, every Instant Action aircraft
in C1 flies that pattern carried around the player. Six of the eight chapters' first nets are
anchored this way, to the player (C1), a zeppelin (C1C, C2, C3) or a train (C4).

⚠ **The anchor is not the pattern's centre, and the pattern is not a ring.** Measured on both C1
player-anchored nets (2026-08-15, at the controls and confirmed against the node coordinates): the
cycle's geometry crosses itself, tracing a **figure eight** of two lobes, and the anchor node sits
at the centre of ONE lobe rather than at the centroid.

| Net | Nodes | y | Extent | Anchor vs centroid | Anchor vs near lobe's centre |
|---|---|---|---|---|---|
| `M4ReinfAce` #10 | 10 + anchor | 400 | ~550 × 1030 m | 255 m off | 39 m |
| `M2Ace` #23 | 12 + anchor | 350 | ~585 × 1105 m | 246 m off | ~110 m |

So the aircraft orbits the player closely through one lobe and swings ~1 km away through the
other, rather than circling at a constant radius. Both anchors sit off-centre in the same
direction, which is authoring, not coincidence.

⚠ One mission-specific special case sits at the top of `FUN_00432010` and is NOT the general rule:
if the trailer target is one of `britbalmoral_1/2/3` (`DAT_0071c4e4`/`e8`/`ec`, set by name in
`FUN_004735b0` for one mission only) the net re-binds to whichever of the three is still present.
An escort-target failover for that mission, nothing more.

⚠ A node with no edges is skipped by the nearest-node scan (`FUN_00431900` tests the node's own
degree at `+0x18`), which is how an anchor node parked off the ring never becomes a flight target
itself.

Implemented 2026-08-15 (`BL-377`): `AiNetFollower.NodePosition` applies the offset to every node
read and `Session/NetTrailerTargets` resolves the name, `player` to the player rig and anything else
to a world node. The one thing the binary cannot answer is whose position `player` means with a
split field, because the original has no splitscreen; the call (2026-08-15) is that split play
matches single player, so rig 0 is used and nothing else is invented.

## What the patrol executor aims at

`FUN_0041d1f0` case 0 is the aeroplane's patrol arm, and the aim point it hands the control law is
**not the node**. Built at `0x0041d30b`–`0x0041d4e0`, with `cur` the node the aircraft is flying
from (`obj+0x2e8`) and `next` the far end of the current edge (`DAT_0064e860`, both positions read
through `FUN_00432140` so the trailer offset is already in them):

```
leg   = normalise(next - cur)                    ; FUN_00422690
along = dot(pos - cur, leg)
off   = ((pos - cur) - along · leg) · 0.9        ; 0.9 at 0x0060355c
if |off|² > 40000:  off = off · (200 / |off|)    ; 40000 at 0x00603558, 200.0 (double) at 0x00603550
aim   = next + off
```

Then `FUN_0041b560(aim, velocity = DAT_0075d1b8, table 0x61fb68, emergency = 0, gunLead = 0)`, where
`DAT_0075d1b8` is three zero floats, so the aim point is stationary.

**The aim point carries 0.9 of the aircraft's own cross-track error, capped at 200 m.** An aeroplane
50 m left of the leg line is sent at a point 45 m left of the node, so the commanded course is very
nearly parallel to the leg and only the residual tenth of the offset converges on it. The
displacement moves with the aircraft's own drift, which is a washout on the position error.

⚠ **This is a tracking rule, not a damping rule, and it does not by itself steady the aeroplane.**
It was measured on the ported law: flying a 8 km leg entered 120 m off the line, the displacement
holds the aeroplane out on a parallel course as intended, and it does not change the roll against
the same flight aimed at the node. What steadies the aeroplane is in the law, and specifically in
the sign of `bz` ([`aiControlLaw.md`](aiControlLaw.md), step 4): the renormalisation is the astern
case, so an aircraft tracking something in front of it keeps its error's true magnitude.

⚠ The vertical component is carried too. `off` is a full 3-vector, so an aircraft above or below its
leg is aimed above or below the node by the same 0.9, and the 200 m cap is on the 3-D magnitude.

Case 3, avoid crash, aims at the aircraft's own position with **1000.0 added to Y only**
(`0x0041d2e2`) on the emergency table `0x61fb48`. There is no lateral component to it.

## Arrival is measured ALONG the leg, not as a distance to the node

After the law call, case 0 advances the walk on this test, with `next` the node being flown at and
`leg` the unit vector from `cur` to it:

```
if dot(pos - next, leg) <= -sqrt(edge+0x1c):  return   ; not arrived
FUN_0041d8f0(obj)                                      ; step to the next edge
```

**It fires as soon as the aeroplane draws abeam the node, however far off to the side it is.** A
capture SPHERE can be missed by an aircraft that cannot turn tightly enough, and then the walk is
stranded on a node it orbits forever; a plane perpendicular to the leg cannot be. The other branches
(pursue, maneuver) use a horizontal squared distance against the same `edge+0x1c` instead, so the
along-leg shape is the patrol arm's alone.

`edge+0x1c` is not authored. It is written once per edge at net load by `FUN_00431a90`, which also
fills the edge's delta and 3-D length (`+0x0c`…`+0x18`) before normalising the delta in place:

```
r = sqrt(dx² + dz²) · 0.1                   ; the leg's HORIZONTAL length, a tenth of it
if r < net+0x20:  r = net+0x20              ; CCENet+0x28, copied in by FUN_004314e0
edge+0x1c = r²
```

**A tenth of the horizontal leg length, floored at 10 m, stored squared.** The floor is `CCENet`'s
constructor default (`FUN_004303d0` writes `10.0f` to `+0x28`) and every shipped net leaves it
alone, element 1 of the net record is `10.0` on all 222 files. Altitude change along a leg does not
widen the capture, which is consistent with the follower's other, horizontal, arrival tests.

⚠ **Porting this does not by itself steady the aeroplane.** Measured on the anchored `M4ReinfAce`
against the invented 200 m horizontal radius it replaced, node advances in 90 s go 3 → 7 with the
player under way, so the stranding is real and this removes it, while the roll was unchanged. The
roll was the `bz` sign in [`aiControlLaw.md`](aiControlLaw.md), which is a separate fault in a
separate function; both were real and this one is the reason a walk could stall on a node.

## The patrol-net follower has no netless branch

`FUN_0041d1f0` resolves the net before it does anything else (`0x0041d1f9`–`0x0041d237`): it scans
the net-id table for the vehicle's `netids` value at `+0x2e4` and, **on no match, uses index −1**,
indexing 100 bytes before the first element of the net array and dereferencing the node and edge
pointers it finds there. There is no guard and no fallback path.

This is latent rather than reachable: a `jet` with no net is built dead by `FUN_00476250` (the
headline), so the follower never runs for it and the index −1 read is never executed by the retail
game. It is recorded here because it is the positive proof that "netless patrol" is not a behaviour
the engine has.

Net assignment is `FUN_00475fc0`. On `netids == -1` it returns immediately, leaving the task
untouched. On a valid net it:

- sets the current node `+0x2e8` to the nearest node to the spawn position (`FUN_00431900`) and the
  current edge `+0x2ec` (`FUN_00431e40`, below);
- sets the task `+0x2f0` to **2 when the net record's `+0x10` field is non-zero, else 0**;
- **overwrites the vehicle's three volumes from the net's own**, where the net authors a non-zero:
  activation from net `+0x24`² / `+0x28` / `+0x2c` into `+0x318` / `+0x31c` / `+0x320`, attack from
  net `+0x34`² / `+0x38` / `+0x3c` into `+0x328` / `+0x32c` / `+0x330`, and return from net `+0x40`²
  / `+0x44` / `+0x48` into `+0x334` / `+0x338` / `+0x33c`. Radii are stored squared.

⚠ **The roster block outranks the net on all nine.** The spawn runs the net assignment first
(`FUN_0047c210` calls `FUN_00476250` at `0x0047c77b`, and the net assignment is inside it) and only
then copies the block's own volumes from `+0x48`–`+0x6c` over the same nine fields, each guarded by
the same non-zero test, the last of them at `0x0047c91c`. So a net's volumes reach a vehicle only
where its roster block leaves that slot at 0.0, which is the campaign case. An Instant Action
block authors all nine at ±10000 m (`FUN_0045a240`), so there the net contributes none of them.

**No activation volume is smaller than `min_ai_active_dist`**, the `player.zrd.json` key read into
`_DAT_0071c3ec` by `FUN_004735b0` (`0x00474139`), **2000.0** in this install and 0 when the key is
absent. The clamp runs twice over the same three fields, once after the net assignment
(`0x004763f4`) and once after the block copy (`0x0047c922`): radius floored to `min_ai_active_dist`,
the low bound to −it and the high bound to +it. The attack and return volumes have no such floor.

⚠ **A net demotes a wingman.** `FUN_00476250` (`0x00476382`–`0x004763c3`): if `mode == 4` and
`netids >= 0`, the escort buffer at `+0x2f8` is freed and `+0x67c` is forced to **0**, after which
the vehicle takes the net like any other `jet`. The script-side net assignment `FUN_0049c920` ends
with `+0x67c = 0` on every path, including the failure path where the net id did not resolve.

So `mode wingman` is not "this aircraft escorts". It is **"this aircraft escorts when it has no
net"**, and the net wins whenever one is authored.

## Which edge a vehicle leaves a node on: the nose, never a draw

`FUN_00431e40(net, nodeIndex, excludeEdgeIndex, direction)` picks the edge, and it has exactly two
callers: the net assignment `FUN_00475fc0` at seat, with no edge excluded, and the walk step
`FUN_0041d8f0` after an arrival, excluding the edge just flown. Both pass the same direction, the
vehicle's `+0x198`–`+0x1a0` basis row with each float's sign bit flipped, which is its backward axis
negated and so its **nose**.

The pick walks the node's own edge list, skips the excluded edge, resolves each edge's far end,
normalises `farEnd − node` through `FUN_00422690`, dots it with the direction and keeps the maximum,
seeded at `−FLT_MAX` so the first of equal maxima wins. A node with no edges returns −1.

Three things follow. **The walk is deterministic**: nothing draws, so two vehicles seated on one node
facing one way leave it on one edge, which is how the campaign's grouped aircraft fly a shared net in
formation. **The seat is the node, not a destination**: `+0x2e8` holds the node the vehicle is flying
FROM and the aim point is the far end of that edge, so a vehicle offset to the side of the leg keeps
that offset through the cross-track carry below rather than converging on a node. And the direction
is the nose rather than the velocity, so a slipping or rolled aeroplane picks the same edge as a
coordinated one.

⚠ One arm sits ahead of all of that and no shipped net reaches it: when net `+0x10` is non-zero the
function returns the first non-negative entry of the node's `+0x20` table instead. `+0x10` is a
count of GOAL nodes copied from the parsed record (`FUN_004314e0`, record `+0x18`/`+0x1c`), and
`FUN_00431b70` fills every node's `+0x20` table with, per goal, the shortest-path distance and the
edge to take toward it: a net with goal nodes routes every vehicle to its first goal rather than by
the nose, and the net assignment seats such a vehicle on task 2. It is not the danger-zone tag
system ("The danger-zone run" below). The record element that would carry a goal list is absent
from all 222 shipped files (13 or 14 elements each), so `+0x10` is 0 everywhere in this install.

CSVM ports the pick as `AiNetFollower.PickOnward`, taking the nose through `AiNetFollower.Update`;
`AiPilot.FlyPatrol` passes `−model.Attitude.Z`. A caller with no facing keeps the older
nearest-node seat and a seeded draw, which is `ZeppelinMotion` alone, because a zeppelin is not a
vehicle in this engine at all and does not reach `FUN_00431e40`.

## The escort law, `FUN_0041e760`

Its leader is `primary_target` at `+0x2fc`, dereferenced through `+4` to the leader's vehicle with
no null check, so the mode requires an assigned target.

**Station offsets**, in the leader's own body frame (basis at `+0x180`, translation the leader's
position). Axis 2 of that basis is the leader's *backward* axis, derived from `FUN_00476250` setting
the forward vector to its negation (`0x00476339`):

| leader | offset | reading |
|---|---|---|
| the player | `DAT_0061fb88` = (6.0, 0.0, 18.0) | 6 m out, level, 18 m astern |
| another AI | `DAT_0061fb98` = (8.0, −2.0, −8.0) | 8 m out, 2 m low, 8 m ahead |

⚠ Escorting a non-player leader also forces the state to 2 every frame and sets byte `+0xdd`
(`0x0041e7b6`), which suppresses the radio call that the player-escort path plays. The effect is
that a wingman-of-a-wingman has no persistent state machine: it trails its selected target when it
has one and flies the station when it does not.

**The state machine** at `+0xd8`, five states, evaluated twice per frame (once to transition, once
to compute the station):

| state | station | leaves when |
|---|---|---|
| 0 | the target station, or the leader's position +200 m of altitude when there is no target or the LEADER is beyond **1800 m** | leader live, within **700 m**, own speed above **20.576 m/s** → 1 |
| 1 | the formation offset above | (set from 0, 2 or 4) |
| 2 | the target station | no target → 1; or `(3 × altitude error)² + horizontal range²` above **1200 m** squared for a player leader, **800 m** squared for an AI leader → 1 |
| 3 | re-join | within **50 m** of the station → 4; target acquired → 2 |
| 4 | the formation offset | within **50 m** → 1; target acquired → 2 |

Both distances in state 2's break-off are measured to the **leader**, not to the target, and the
range term is the horizontal one alone (`FUN_00538920`, x and z); every other test on this page is
a 3-D distance (`FUN_00538880`).

Nothing inside this function enters state 3, and **nothing inside it leaves state 1**: the column
above lists that state's entries, not an exit. A wingman on a player leader therefore joins once
and holds the formation offset for the rest of the mission, target or no target. The engaging
state is reachable only through the every-frame forcing an AI leader applies (above), which is why
a wingman-of-a-wingman is the one that alternates.

Nothing OUTSIDE the function moves the state either. `+0xd8` is written at six sites, all of them
inside `FUN_0041e760` (`0x0041e7b0`, `0x0041e8d1`, `0x0041e9f6`, `0x0041ea21`, `0x0041ea37`,
`0x0041ea62`) plus the constructor's zero at `0x004b05a3`; no other function in the image writes a
vehicle's escort state at all.

### A wingman still selects a target, and still shoots

Holding the formation is not the same as ignoring the war, which is why a `mode wingman` block
authoring `rating_biases` is coherent.

**It acquires.** `FUN_0041c270` calls the acquisition `FUN_0041fe10` at the top of every AI frame
before it forks on `mode`, so a wingman runs it exactly as a jet does: the same ranked pick
(`FUN_0041f9c0`) against the same bias table at `+0x344`. Two arms of that function are keyed on
`mode` being 0 or 4, which is the wingman's own class: the scorer it picks (`PTR_FUN_00603544`
rather than `PTR_FUN_0060354c`), and the arm that would prefer `primary_target` as the target,
which is gated on `mode != 4` so a wingman never shoots at its own leader.

**It fires.** The escort law's tail, after it has handed the station to the steering law, is
`if (+0x948 != 0) { … FUN_0041afe0(); FUN_0041f420(1,1); }`, the same fire routine the pursuit path
in `FUN_0041c270` ends on. The gate ahead of it is `FUN_00460be0`'s firing solution against the
gun group at `+0x950`, refused when the solution's dot against the aircraft's own forward row falls
below −0.9, so a wingman fires whenever the target it selected happens to lie ahead of the station
it is flying. The engagement a player sees from a wingman is that, not a pursuit.

### The release that is not wired up

`FUN_0049c880` walks every vehicle: alive (`+0x91d == 0`), `mode` 4, a non-null `+0x2fc` whose
leader answers its vtable `+0x38` test, and hands each one to `FUN_0049c920`. That function assigns
the chapter's first net (`+0x2e4`, with `+0x2e8`/`+0x2ec` from the node and edge lookups), sets the
AI state at `+0x2f0` from the net record, and clears `mode` to 0. A vehicle it has touched is an
ordinary netted AI: `FUN_0041c270` stops forking to the escort law for it and runs the patrol and
pursue machine instead, which is the only "wingman leaves formation and fights" mechanism in the
image.

⚠ **It is unreachable.** `FUN_0049c880` has no callers and no 4-byte pointer to it anywhere in the
image, so nothing in the shipped build can run it. Whatever it was for (the shape reads as a
release order given to the flight), the shipped game never gives it.

**The target station** (states 0 and 2) ramps with the *target's* speed and is placed along the
NEGATION of the target's backward axis, i.e. that far AHEAD of it along its own facing, a cut-off
point rather than a trail:

```
d = 106.68                                   for v <= 20.576 m/s
d = 106.68 + (v - 20.576) * 1.8516719        for 20.576 < v < 102.880005 m/s
d = 259.08                                   for v >= 102.880005 m/s
```

Those are imperial figures in metric storage: 350 ft at 46 mph ramping to 850 ft at 230 mph.

⚠ Pursue computes the same ramp from the same immediates and applies it with the OPPOSITE sign
(`FUN_0041d9f0`: the aim point is the victim's position plus `d ×` its backward axis, so pursue
stations itself that far BEHIND its victim). The two laws differ in that one sign alone. The aim
velocity handed to the steering law goes with the station: the target's own on states 0 and 2, the
LEADER's on states 1 and 4, and zero on the "fly at the leader" arm of state 0.

**Separation.** Inside **80 m** of the leader (6400 m² compared before the square root), the station
is pushed away from the leader along the leader-to-follower vector scaled by `80 / distance`.
⚠ The player station is 18.97 m from its leader, so this push ALWAYS fires there: the commanded
point alternates between the station itself and a point about 99 m out along the current
leader-to-wingman line, and the hold that results is a weave around the leader rather than a
parade-tight join.

**Two AI modes short-circuit the law.** Avoid crash (`+0x358 == 3`) steers at the aircraft's own
position plus **1000 m** of altitude, and stunned (`+0x358 == 4`) returns immediately with no input
at all. The net follower's avoid-crash case does the same 1000 m climb-out with its own parameter
block, so **avoid crash is "aim 1000 m above yourself" in both laws**. The escort law flies its own
climb-out on `DAT_0061fb28`, the wingman table, where the net follower uses `DAT_0061fb48`.

### What the escort's climb-out is, exactly

The arm is `0x0041e7bd`–`0x0041e814`, read out instruction by instruction because CSVM's escort was
flying an invented displacement here:

| what | where | value |
|---|---|---|
| aim point | `0x0041e7c8`–`0x0041e7d3` reads `+0x204` (own x, y, z) | own position, **X and Z untouched** |
| the climb | `0x0041e7e0` adds `DAT_00603464` to the Y term | `0x447a0000` = **1000.0** |
| aim velocity | `0x0041e7ee` pushes `0x0075d1b8` | the engine's zero vector |
| parameter table | `0x0041e7e6` pushes `0x0061fb28` | the **wingman** table, 0.4/1.5 |
| `emergency` | `0x0041e7d8` pushes 1 | set |
| `gunLead` | `0x0041e7d6` pushes 0 | clear |

The net follower's case 3 is the same six values at `0x0041d2b4`–`0x0041d301`, differing in the
table alone (`0x0061fb48`, 0.6/1.3). So **there is no lateral term in either law**: the climb-out
is a wings-level pull-up, and what separates a wingman's from a netted aeroplane's is the throttle
band, nothing else.

**Duration and exit.** The arm `return`s at `0x0041e814` without reading or writing the escort
state at `+0xd8`, and no other site writes that byte outside `FUN_0041e760` (six sites, all listed
above). So the climb-out ends exactly when `FUN_0041f810` stops setting `+0x358` to 3, which is the
first clear ray, and the wingman resumes the formation state it already held. **It returns to
station; it does not re-join**, and state 3 (`Rejoining`) stays unreachable.

⚠ **A wingman's own ray sees the leader it is formating on**, and this is the original's behaviour
rather than a port defect. The ray excludes only the caster, the station sits astern of a player
leader, and the separation push makes the hold a weave along the leader-to-wingman line, so a
wingman trailing inside 4.5 seconds of travel is looking straight at its leader. Measured on
C3/M01: the campaign wingman sits 94.5 m dead astern and arms on `player1/airframe` at a 511 m
reach nine seconds after the intro.

### What CSVM ports of this (D34)

`src/Flight/Ai/AiEscort.cs` is the law: the five-state machine, both station offsets, the ramp, the
break-off test and the separation push, pure over a leader/target snapshot. `AiPilot.Escort` holds
it and, when its leader is in play, dispatches to it INSTEAD of pursue, lay off, patrol, evade and
a running maneuver, keeping only stunned and avoid crash ahead of it, which is the original's own
fork order. The station is flown through `AiControlLaw` on `AiLawParams.Wingman`, and so is the
climb-out: `AiPilot.ClimbOutAim(pos)` is the decoded vertical aim above, `AiPilot.FlyClimbOut`
picks it for an escorting pilot and the invented lateral break for every other one, and the escort
state is untouched across the whole episode so the wingman resumes its station on release.

Not ported: the radio call the join plays (`DAT_0071c3b0`) and the re-acquire sweep state 2 runs
when its target is lost (`FUN_0041f9c0` again, with its own 3600 m test and second cue,
`DAT_0071c3b4`); the fire decision the law ends on, which in CSVM is the host's `AiGunner` pass;
and the null-leader dereference, which CSVM answers by falling back to the pilot's standing orders.

The acquisition and the guns come out the same way. `AiPilot.Escort` short-circuits the STEERING
dispatch only; `FlightController`'s gunner pass runs on every AI tick regardless, so a CSVM wingman
ranks targets against its own `rating_biases` and fires from the station exactly as the law's tail
does. The `wingman-engage` suite measures it on a flown leg: the wingman holds one bandit as its
target throughout, opens fire, and its escort state never leaves the formation.

**The spawner.** A campaign session spawns the mission's `aiv` roster through
`Session/Campaign/CampaignRoster.cs` (the plan) and `CampaignDirector.BuildRoster` (the placement). The fork
above is applied per block from the def's `mode` (`Mech3/VehicleDefs.cs`, resolved through
`kind_of`) and the block's `netids`: a netless `mode wingman` block gets `AiPilot.Escort` on the rig
its `primary_target` names (the literal `player` is the first human), resolved in a second pass once
every rig exists; any authored net becomes `AiPilot.Patrol` on that net and no escort. The net's
volumes (record elements 2–10) are applied first and the block's own slots 8–19 over them, each
field on the non-zero test, then the activation radius is floored at `min_ai_active_dist`, which is
the order decoded under "Net assignment". A `deactivated` block is built inert and `WAKEUP_ENEMIES`
re-activates it. The `campaign-roster` suite pins all of it over C1/M04.

⚠ **A leader flying faster than 250 mph cannot be formated on at all.** The steering law caps an
AI's desired speed at `AiControlLaw.SpeedCeiling` (111.76 m/s) whatever the airframe can do, so a
wingman handed a faster leader falls behind for the rest of the mission. That is the original's
ceiling, not a port artifact, and it is why the in-engine check flies its leader at a cruise lever.

## Crash avoidance is a STATE, not an altitude rule

Decoded 2026-08-15 to answer "the nets are authored at 400 m, what stops an AI flying into high
ground?". Nothing in the net does: node altitudes are absolute and authored, and neither
`FUN_00432010` nor the follower `FUN_0041d1f0` samples terrain. The answer is a separate
per-plane check that flips the AI substate at vehicle `+0x358`.

`FUN_0041f810`, reading the vehicle's own Y at `+0x208`:

| Y | What happens |
|---|---|
| below `DAT_0071c3f0` (**20.0**) | state `+0x358` = **3** outright, no ray and no throttle |
| `DAT_0071c3f0` … `DAT_0071c3f4` (**8000.0**) | cast a ray, hit sets state 3, a clear ray clears it |
| above `DAT_0071c3f4` | no check, and a state of 3 is CLEARED to 0 every frame |

Both bounds are hard immediates written at level setup by `FUN_004735b0`: `0x45fa0000` = 8000.0 at
`0x00474151` and `0x41a00000` = 20.0 at `0x0047415b`. They are not per-mission and not authored.
The nose-speed floor 4.4700 (`DAT_0071c3f8`, `0x408f0d84`) is written in the same run.

The ray is the vector the vehicle's virtual `+0x04` accessor returns, scaled by **4.5**, passed to
`FUN_004c8f70`. ⚠ That accessor is velocity by shape and by use, which makes the ray about 4.5
seconds of travel, but the identification is inferred rather than read off a name; the 4.5 itself
is exact. The check is throttled per plane by `+0xf0` against the game clock `DAT_0071c470` to
**clock + 0.5…1.0 s**, the fraction drawn as `rand() × 3.051851e-05` (that is `rand()/32767`), so
it is not a per-frame cast. The throttle gate returns before the cast and before the clear, so
state 3 persists between due checks; the caller resets `+0xf0` to the current clock when a vehicle
first becomes active, so a plane that pops in checks on its first tick rather than waiting out a
fresh interval.

⚠ **A clear ray releases the state in the same call.** There is no consecutive-clear counter and no
hold: the ray misses, control falls through to `if (state == 3) state = 0`, and the plane is back on
its route on the next tick. The climb-out is exactly as long as the obstacle keeps arming it.

State 3 is consumed by the follower's own switch on `+0x358` (`FUN_0041d1f0` case 3, and the same
case in the `+0x67c == 1` arm): the steering target becomes **the plane's own position with
Y + 1000**, flown through parameter block `DAT_0061fb48` rather than patrol's `DAT_0061fb68`, i.e.
a throttle band of 0.6–1.3 instead of 0.8–1.1. So the climb-out is both a different target and a
hotter lever, and it ends when the check stops setting the state.

Two neighbouring facts about the same floor. `FUN_0041b560`, the shared steering law, reads
`DAT_0071c3f0` directly (`0x0041b5c5`, `0x0041b5d5`), so the floor is enforced inside the control
law as well as by this check. And `FUN_004216e0`, the danger-zone approach (reached from the
follower `FUN_0041d1f0` at `0x0041d4f2` and the merge rule `FUN_0041d9f0` at `0x0041da75`),
bypasses both bounds: it stashes `DAT_0071c3f0` and writes −FLT_MAX (`0x004216f5`), with a paired
per-vehicle ceiling at `+0x314` written +FLT_MAX, and restores both at `0x00421775`.

⚠ **That bypass spans one steering solve, not a mode and not a duration.** The two writes bracket
the single `FUN_0041b560` call at `0x0042176a`, and the restore is three instructions after it
returns, so the clamp is off for one aim-point evaluation. The state-5 write `+0x358 = 5` sits at
`0x0042180f`, well past the restore, so this is not a mode-5 disable and there is no window in
which a second aircraft could read an opened floor: the global is back at 20.0 before anything else
in the frame runs. The bypass also never reaches `FUN_0041f810`, which the frame loop calls from
its own site and which always sees the restored 20.0. Nothing in the original suspends the floor
for the duration of anything, and the only writer of the global outside level setup is this pair.

`AiPilot.FlyDangerZoneApproach` is the port: it is the one caller that passes
`AiControlLaw.Steer`'s `openAltitudeBand`, so a ribbon entry point under 20 m or over the
airframe's `flight_ceiling` is flown to rather than clamped away from, and every other caller keeps
the band closed. The shipped ribbons do not exercise it: the `campaign-racers` suite still carries
C2/M03's six racers through the same seven zones with the world's colliders up, so the exemption
binds only where a mission authors a zone outside the band.

### The maneuver veto, the fourth reader of the two bounds

`FUN_004201a0`, the evasive-maneuver selector, reads both bounds once per CANDIDATE, against a
point that candidate is predicted to reach rather than against the aeroplane's own position. For
each library entry it copies the step list (`FUN_00421f50`), composes the entry frame onto every
step (`FUN_0045f7c0`, the vehicle quaternion at `+0x150` for a `relative` entry and the level
heading frame off `+0x180` otherwise), and sums each step's own forward (`FUN_0045f860` folding
`FUN_0045fd80`, which rotates the unit −Z at `DAT_00607b40` by the step and adds). That sum times
**107.2896** (`0x006036a0`, 240 mph in m/s) added to the position at `+0x204` is the predicted end
point, and its Y goes through the same three bands `FUN_0041f810` uses:

| predicted end Y | What happens |
|---|---|
| below `DAT_0071c3f0` (**20.0**) | the candidate is dropped outright, `0x00420405` |
| `DAT_0071c3f0` … `DAT_0071c3f4` (**8000.0**) | sweep the predicted path, a hit drops it, `0x0042042d` |
| above `DAT_0071c3f4` | the candidate is kept, with no sweep at all |

The sweep is `FUN_0045f8a0`: the same chain walked one segment at a time, `FUN_004c8f70` between
each consecutive pair, with the aeroplane's own node deactivated for the duration exactly as the
crash check deactivates it. This is why the original never STARTS a program that would fly it into
the ground, and it is a different mechanism at a different moment from the reactive climb-out.

⚠ **A coin flip mirrors the program before the prediction.** `rand() & 1` at `0x0042031e` runs
`FUN_0045f840`, which applies `FUN_0045f770` to every copied step: the step's own X axis is
reflected against the unit X at `DAT_006379b0`, which on each pure axis works out as "negate the
yaw and the roll, keep the pitch". The forward vector's vertical component is the same either way,
so the ALTITUDE test reads identically on both sides of the flip and only the swept ground track
differs. CSVM does not draw the flip (`ManeuverExecutor` deliberately bakes no side in), so its
veto matches the original on altitude and is half of it on obstacles.

`AiModeMachine.PickManeuver` runs the three bands through `ManeuverExecutor.PredictedPath`, which
composes the same steps onto the same entry frame `Next` flies, and sweeps the path through the
same `ProbeBlocked` delegate the crash check uses. ⚠ The machine predicts from the position and
attitude `Update` was last handed, so a rig that never steps the machine vetoes against the origin.

### Which state wins

The precedence is in the caller, `FUN_004897c0`, not in either function, and it runs in this order
per vehicle per frame:

```
if (mode is 0, 1 or 4) {                             // the dynamics class gate, 0x00489a90
    if (state < 4)                    FUN_0041f810(v);   // the crash check
    if (state == 4 && clock >= +0xc0) state = 0;         // stun expiry
    if (+0x9bc != 0 && state < 2)     state = 2;         // a danger-zone run re-arms its approach
}
FUN_0041c270(v);                                     // the AI brain, and so the net follower
```

Three rules fall out of the inner block. The crash check runs in states 0 to 3 only, so **stunned
(4) and the rail run (5) suppress it entirely**. It writes 3 without consulting the prior state, so
it **pre-empts an approach**. And the approach is re-armed from state 0 or 1 only, so once crash
avoidance holds state 3 the run record waits until the climb-out releases ("The danger-zone run"
below).

### Only three dynamics classes get crash avoidance at all

The whole block above sits behind a test of `+0x67c`, the def's `mode` key (`jet` 0, `heli` 1,
`tank` 2, `ship` 3, `wingman` 4, `plane` 5; see [`flightModel.md`](flightModel.md)). At
`0x00489a90` the class is read and compared against 0, 4 and 1; anything else jumps to
`0x00489b12`, which is the `FUN_0041c270` call itself. So a vehicle of any other class **still
steers its net every frame and simply never runs the crash check**, never expires a stun and never
starts a maneuver. Since `FUN_0041f810` is the only writer of state 3, the follower's own
avoid-crash case is dead code for those classes.

**Zeppelins therefore have no crash avoidance, and could not have it in this install.** They are
not registry vehicles at all: `extracted/zrdr/vehicle.zrd.json` names no zeppelin, blimp or airship
type, so a zeppelin is a gamez scene node driven by `mis_anim` rather than an object in the vehicle
list this tick walks ([`flightModel.md`](flightModel.md), "Zeppelins"). It has no `+0x67c` to pass
the gate with and no `+0x358` to hold a state in. The one direction that does work is the other
one: the zeppelin node carries `active` and `intersect_surface`, which is exactly what
`FUN_004c8f70` tests, so **an aeroplane's crash-avoidance ray sees a zeppelin** and climbs out for
it the way it does for a hillside.

⚠ **The floor is a flat world-Y value, not a terrain follow.** 20 m saves a plane over water and
flat ground and does nothing over a 600 m ridge; the raycast is the only terrain-aware part.

**What the ray can hit: other aircraft included** (decoded 2026-08-15, asked as "does crash
avoidance see other planes?"). `FUN_004c8f70` tests terrain plus every scene node whose flag word
at `+0x24` carries `ACTIVE` (0x4) and `INTERSECT_SURFACE` (0x10) (see
[`flightModel.md`](flightModel.md), "Emitters"); it has no vehicle filter. Plane models qualify:
3151 of 3317 nodes in `extracted/planes/nodes.json` carry both flags, including the
`player_pfighter` and `piratefighter` roots. The check itself proves the point: before casting,
`FUN_0041f810` reads its own node's flag word, deactivates the node through `FUN_004cca30`
(`gwNodeSetActive`, named by its own error string at `0x0062cd28`), casts, and restores the
previous active state (`0x41f91e`/`0x41f982`), so the one aircraft excluded from the ray is the
caster. The collision sweep `FUN_0048d7f0` wraps the same query in the same self-exclusion
(`0x48d9c7`/`0x48da3e`), corroborating that aircraft are expected hits. An AI plane whose
4.5-second velocity ray passes through another aircraft therefore enters state 3 and flies the
same 1000 m climb-out as for terrain.

Three bounds on how effective that is in a head-on: the ray is a zero-width line against the other
plane's node volumes, so a small or crossing target can slip between checks; the cadence is one
cast per 0.5…1.0 s per plane, and head-on closure at fighter speeds spends the whole 4.5 s
lookahead in roughly two seconds; and both parties answer with the same straight-ahead climb, so a
mutual detection can still merge. Head-ons in the original are rare, not impossible.

CSVM's probe is `FlightController.AvoidCrashBlocksLine`, masking `CollisionLayers.WorldAndAircraft`
with the caster's own body excluded, which is this rule. `AiModeMachine` carries the rest of the
band structure: `ProbeLookaheadS` 4.5 s, `ProbeIntervalMinS`/`ProbeIntervalMaxS` 0.5…1.0 s drawn
per plane from its seeded rng, `AltitudeFloorM` 20, `ProbeCeilingM` 8000, `ClimbOutM` 1000, one ray
along the aeroplane's own velocity, and release on the first clear ray. The same two bounds serve
`AiModeMachine.EndsSafely`, the maneuver veto above, which is a separate reader at a separate
moment and must not be folded into the reactive arm: this one refuses to start a doomed program,
that one rescues an aeroplane already in trouble. What remains invented is `ProbeMinLookaheadM`,
marked as such, plus `AiPilot.ClimbOutBreakM` below, which a netted pilot flies and an escorting one
does not.

### Retired: a second, deck-slanted probe ray

An invented `ProbeDeckM` cast a second ray 40 m below the lookahead point so that "shallow terrain
under a level flight path still registers". The original casts one ray and no more, and the second
one arms where the decoded ray is clear: on C3/M01 it broke the campaign wingman off its station
twice on a low pass over water, at 90 m and again at 80 m, each time on a `col_water` the primary
ray missed. Removing it leaves that pass with no break-off at all and turns the wingman's recovery
monotonic (separation 294.6 m falling to 236.4 m, altitude difference 174.8 m to 50.5 m, where with
the deck ray it bottomed at 244 m / 55 m and climbed back to 305 m / 132 m). The tail of the
detection argument above applies: detection frequency is measurably not what produces CSVM's
mid-airs, so a probe that fires less often is not a collision risk on that evidence.

### Measured: the third bound is the binding one

A spectated 5-versus-5 Bloodhawk dogfight in C1 (a hand-authored `--ia` file, `--debug-spectate
--ai-attack=5 --det --frames=9000`, about 150 s of sim per run) was run to settle which of the
three bounds above actually produces the mid-air collisions CSVM sees. ⚠ `--det` does not pin this
scenario across processes, so each run is a sample; the figures below are per-run averages.

| probe | runs | mid-airs / run | of which head-on | arms / run | arms on an aircraft / run |
|---|---|---|---|---|---|
| the decoded zero-width ray | 8 | 3.63 | ~5 in 6 | 31.0 | 13.8 |
| a swept 10 m sphere | 6 | 3.50 | ~3 in 4 | 79.7 | 66.7 |

Two results. **The ray does see aeroplanes in flight**: 13.8 arms per run on another Bloodhawk's
airframe, against 17 on terrain. And **widening it changes nothing that matters**: the sweep
detects an aeroplane 4.8× as often and the collision rate does not move (3.50 against 3.63, inside
a run-to-run spread of 3 to 4). Detection is not the bottleneck, so the sweep was reverted.

What remains is the third bound, which the decode already names: **both parties answer a detection
with the same straight-ahead 1000 m climb**, so two aeroplanes that both see each other both pull
up along converging tracks and merge anyway. The collisions are overwhelmingly head-on, measured
at impact as the angle between the two velocity vectors, 175°–178° apart on most of them.

### Measured: breaking the climb-out's symmetry is what helps

Same rig, and this time the two arms differ in ONE constant, `AiPilot.ClimbOutBreakM`, so
everything else about the build is identical:

| climb-out | runs | mid-airs / run | of which head-on / run |
|---|---|---|---|
| straight up, the original's | 8 | 3.75 | 3.00 |
| 1000 m up **and 1000 m right of own track** | 14 | **2.21** | 2.14 |

A 41 % cut, Welch t = 2.8 on 13 degrees of freedom, p ≈ 0.014. Right rather than a coin flip is
the point: two aeroplanes meeting head-on that each break right diverge every time, where a random
side still puts them on the same one half the time. It is taken off the ground track and not the
airframe's own right axis, so a rolled or inverted pilot breaks the same way as a level one.

⚠ This is INVENTED and marked so in the code. The original displaces nothing; its climb-out aim
point is the aeroplane's own position with Y + 1000 and no lateral term at all.

⚠ And it is not a cure: 2.2 mid-airs per 150 s of a ten-plane furball is still a lot. It is the
symmetry that was costing the most, not the detection.

⚠ The whole scenario is one the original never produces. Ten netted aircraft all pursuing each
other in one volume is Instant Action as CSVM builds it; the shipped game's actors fly the
chapter's first net (above) and converge rarely.

### Measured: the decoded cadence costs no collisions

Adopting the original's band structure (the 20 m and 8000 m arms, the 0.5…1.0 s per-plane cadence
in place of a flat invented 0.25 s, and release on the first clear ray in place of a four-round
clear streak) probes far less often, so it was A/B'd on the same rig, the two arms differing only
in that change:

| probe rule | runs | mid-airs / run | arms / run | arms on an aircraft / run |
|---|---|---|---|---|
| the invented 0.25 s cadence, 4-round release | 8 | 2.13 | 28.3 | 11.0 |
| the decoded bands, cadence and release | 8 | 1.75 | 24.0 | 4.4 |

Mid-airs do not move: Welch t = 0.97, p ≈ 0.36, so nothing here separates the arms. Detections on
another aeroplane fall by 60 % and the collision rate does not follow, which is the same result the
swept-sphere arm gave from the other direction. Detection frequency is not what produces these
collisions.

## The danger-zone run: modes 2 and 5, and what "on rails" is

The two AI states the readout names `approaching danger zone` (2) and `navigating danger zone`
(5) are one mechanism, the `DZPathList` of `dzpath.cpp`: an aeroplane locks onto a `dzpathN`
route ribbon and is CARRIED along it with its pose written from the ribbon, the flight model
switched off, until it runs off the far end and returns to its net. The run record is an
0x18-byte block at vehicle `+0x9bc`: the zone, a reversed flag, a lane, the current segment and
the metres into it, and a done flag.

### The ribbon is a spline in metres, with lanes

`FUN_004459f0` builds a zone per `dzpath`-prefixed gamez node (`FUN_00445ef0`), in a list of
0x50-byte entries at `DAT_0064fb64`. Per node: the mesh's polygons whose material is the `dzone`
material (`DAT_0064fb70`, by name) are the gate outlines; the other polygon is the route, and its
vertices IN POLYGON ORDER become the control points. Consecutive vertices are joined by a cubic
whose end tangents are the neighbouring chords averaged (the single chord at either end), and
every coefficient is then divided by the chord length's powers, so the parameter of a segment runs
from 0 to that length in metres (`+0x14`: 0x34-byte segments, the length then `a,b,c,d` per
axis). `FUN_004465b0` evaluates a point, `FUN_00446670` the tangent, `FUN_00446710` the second
derivative, `FUN_00446790`/`FUN_00446850` an end's point and its tangent INTO the ribbon.

The zone's lane table (`+0x24`, 0x10 bytes per lane: an occupancy count and an offset) holds a
zero lane plus one per CHILD node of the ribbon, at the child's local translation. A run takes the
least-occupied lane (`FUN_00446560`) and every point it flies is displaced by that offset. No
shipped `dzpath` node carries a child, so every install lane table is the zero lane alone.

Per zone: `+0x44` is a difficulty, the node's flag word `+0x28 >> 23`; `+0x48` is the active byte,
cleared by `dzones.zrd`'s `disable` list (`FUN_00445da0`) and by the script's zone on/off op;
`+0x49` is cleared by `nosnapshot`; `+0x4c` is the objective slot from `objective_numbers`.

The difficulty read is the whole word shifted right as an unsigned quantity, with no mask and no
sign extension: `MOV ECX,[EBX+0x28]` then `SHR ECX,0x17` then `MOV [EDX+0x44],ECX` at
`0x00445f7d`–`0x00445f8e`, so bits 23 to 31 and nothing else. The pick then refuses a zone on the
signed test `natural_touch < difficulty` (`0x004210e0`'s unforced arm), which admits an exact
match. **Every one of the install's 80 `dzpath` nodes authors no flag word at all**, six in C1,
seven in C1B, thirteen in C2, five in C3, fifteen in C4 and thirty-four in C5, with none in C1C or
C2B, so every shipped zone is difficulty 0 and the term admits every pilot. The field is read
anyway: the authored bits are the only thing allowed to set it.

### Two entries, and which one the shipped data uses

**The node tag** (`FUN_0041d1f0`, right after the walk step `FUN_0041d8f0`): the node just
REACHED has its `+0x11` byte set, so its `+0x14` index names the ribbon. An index of 0 or more is
formatted as `dzpath%d` and handed to `FUN_00421500`; a negative one calls `FUN_004210e0` in its
forced arm. Neither arm rolls anything, tests a range or reads a difficulty:

- `FUN_00421500(name)` resolves the zone by name (`FUN_00445ce0`), requires it active and to have
  a lane table, and enters from whichever end is nearer to the aeroplane (`reversed` when the far
  vertex is closer). A refused entry sets a 5 s retry stamp (`+0x8a0`) and nothing else.
- the forced arm of `FUN_004210e0` takes the nearest end of ANY active zone, at any range.

**The proximity roll** (`FUN_004210e0`'s unforced arm) is a different thing and a narrow one. Its
gate is the head of the combat driver `FUN_0041d9f0`, five tests before anything else that driver
does, each falling through to `0x0041da43`: the global suspend flag `DAT_0064f66e` is clear
(`0x0041d9f9`), the vehicle class `+0x67c` is 0, a `jet` (`0x0041da07`), the state `+0x358` is 0
(`0x0041da11`), the hit byte `+0xba` is set (`0x0041da1b`), and the mission clock `DAT_0071c470`
has reached the retry stamp `+0x8a0` (`0x0041da25`, `FCOMP` then `TEST AH,1`, so the call is taken
on clock at or past stamp). Then `FUN_004210e0(0, 0)` at `0x0041da3e`. The hit byte is the damage
handler's, set on a FAILED steady-hand test (`FUN_004b9bc0`, "Absorbed %f damage, steady hand test
failed") and cleared by pursue when the player is no longer behind it, so the roll is an evasion:
a hit pilot being chased dives into a nearby zone.

The unforced arm rolls `rand() × 3.051851e-05 < daredevil_chance` (`+0x954`, written from the def
at `0x0047ce4e`, constructor default 0.2 at `0x004b03c2`), logging "Dare devil test passed.
Looking for danger zones." or "Dare devil test failed. Not looking for danger zones.". On a pass
it walks every zone whose active byte `+0x48` is set, whose difficulty `+0x44` is at or under the
pilot's `natural_touch` (`+0x958`, written at `0x0047ce92`, default 4 at `0x004b03cc`; "Choosing
danger zone. Natural touch test failed") and which has a free lane (`FUN_00446510`), reads both
ends through `FUN_00446790`/`FUN_00446850`, and takes the end within **500 m** (the squared test
`local_4c <= 250000.0`) whose into-ribbon tangent best lines up with the direction from the
aeroplane to it. Every path that does not enter stamps `+0x8a0 = DAT_0071c470 + 5.0` at
`LAB_00421443`. The only other caller is the debug console's `force_dz` (`FUN_0043d640`), on the
player's target.

The suspend flag is not a danger-zone term and reads 0 for the whole of a played mission: the
mission load clears it (`0x00464693`) as does the new-game path (`0x004654fa`), the debug
console's `suspend` command writes it (`0x0043db4d`), and the state core raises it only once a
deadline of 1,000,000,000 ticks stamped at load has passed (`0x004a0252`, the constant at
`DAT_00622bb4`).

Both entries end the same way: the run record is written to `+0x9bc`, the state to **2**, and the
standing target `+0x948` is released. `FUN_004897c0` then re-arms state 2 from a non-null
`+0x9bc` every frame the state is below 2, which is how a climb-out or a stun hands back to the
approach rather than to patrol (the "queued maneuver" reading of that line under "Which state
wins" was wrong: the maneuver starter `FUN_004201a0` writes state 1 and `+0x9a4`, never
`+0x9bc`).

⚠ **Which racers fly which zones in C2/M03.** The four tagged nets install-wide are C2's
`M3StuntCourse` #16 (`hafury_1`…`_6`, the CM13 racers, team 0) and `M1FilmShot` #31 (C2/M02's
`secfury_5/6`), and C5's `M1Cabbie` #5 (`autogyro_1`) and `M4MilesRun` #41 (a generator's net).
`M3StuntCourse` tags seven of its 38 nodes: `dzpath1, 2, 3, 10, 6, 7, 9` in walk order, out of the
mission's thirteen zones. The racers have no target and never pursue, so the proximity roll never
runs for them: **in the original the racers fly exactly those seven zones and skip the other
six**, and a port that sends them through all thirteen would be inventing.

⚠ **A generator's launch takes a tagged node like any roster aircraft.** The tag arm sits inside the
follower's mode-0 branch and reads the reached node's `+0x11` and `+0x14` alone; `FUN_00421500` then
wants the zone to resolve by name, its active byte set and a lane free. Read end to end, none of the
functions on that path (`FUN_0041d1f0`, `FUN_0041d8f0`, `FUN_00431e40`, `FUN_00421500`) touches a
field only a launch carries: the generator back-pointer `+0x94`, the surface take-off path
`+0xc8`/`+0xcc`/`+0xd0` and the drop's collision grace `+0xac`/`+0xb4` are written by
`FUN_00451bf0` and read nowhere here, and the net assignment forces a `wingman` to mode 0 in any
case. So how an aeroplane reached the air neither enables nor blocks a run. C5/M04 is the shipped
case: the Dante's bay launches Miles onto `M4MilesStage`, OBJECTIVE60's `SET_AI_NET` moves him to
`M4MilesRun`, and the mission's `dzones.zrd` authors no `disable` list, so `dzpath34` is armed when
he reaches the node that names it.

⚠ **Which way round a net is walked is nowhere authored.** A net assignment seats the walk on the
node nearest the aeroplane (`FUN_00431900`) and takes the edge whose leg best lines up with its
NOSE (`FUN_00431e40`), and `SET_AI_NET` is that same assignment (`FUN_00475f30` into
`FUN_00475fc0`), so the direction is whatever heading the aeroplane holds when the net is handed to
it. `M4MilesRun` is an open eight-node chain carrying its tag on node 2, and a dead end turns the
walk around, because the edge pick skips the excluded edge but keeps its own default index of 0 and
so hands a degree-1 node back the edge just flown. The tagged node is therefore reached from either
side, and a seat anywhere on that chain ends in the `dzpath34` run rather than away from it.

A bay launch's own first edge is not picked by its nose at all. The spawner places and rotates
the node and only then assigns the net (`FUN_0047c210`: the rotation through `FUN_004d1a30`, then
`FUN_00476250`), so the seat sees the generator's authored `rotation`, `[-90, 0, 0]` on all 17
zeppelin hosts, which is nose straight down; `FUN_00451bf0` rewrites the euler with the host's own
heading only afterwards. Every shipped net is level within itself, so every candidate leg dots to
zero against a vertical nose and the first-listed edge of the seat node wins, the maximum being
seeded at `−FLT_MAX` under a strict compare.

### Mode 2, the approach (`FUN_004216e0`)

The aim point is the run's CURRENT point (segment, metres, lane), flown through the steering law
on the emergency table `DAT_0061fb48` (0.6/1.3) with no aim velocity, and with the altitude floor
`DAT_0071c3f0` and the vehicle ceiling `+0x314` opened for that one solve (written `0x004216f5`
and `0x004216ff`, restored `0x00421775` and `0x0042177b`, around the single `FUN_0041b560` call at
`0x0042176a`). ⚠ The state-5 write is at `0x0042180f`, after the restore, so nothing here suspends
the floor for any other aircraft or for any span longer than that one solve; whether a mission-wide
suspension would have been the more interesting design is a question about a mechanism the original
does not have. When the 3-D distance
to that point falls under 105 m (11025 m²) the state becomes **5** and the rail state is seeded:
`+0x35c` the offset from the rail point, `+0x374` the rail point, `+0x368` the aeroplane's
velocity minus `speed × tangent`, `+0x380` the lock time, and the velocity zeroed. There is no
timeout: an aeroplane that cannot reach the point keeps circling it.

### Mode 5, the rail (`FUN_00490590`)

While the state is 5, `FUN_004897c0` calls this INSTEAD of the physics dispatch
(`0x00489b98`). Per frame:

- direction `d` = the tangent at the cursor, negated on a reversed run, normalised;
- the target attitude is the quaternion carrying (0,0,−1) onto `d`, then rolled so that "up"
  points along the second derivative's component perpendicular to `d`: the wings bank fully into
  the bend, and a straight stretch keeps world up. The attitude at `+0x150` is blended toward it by
  `min(1, 1.3 × dt)` of the remaining rotation per frame, `0.5 × dt` on the run's last segment;
- speed walks toward `69.2912 − 4.4704 × d.y` m/s (155 mph, less 10 mph per unit of climb) at
  22.352 m/s², and the velocity is `speed × d`;
- the rail point `+0x374` follows the cursor through `exp(−10 dt)`, the residual offset `+0x35c`
  integrates its own velocity `+0x368`, and both shrink in magnitude by
  `min(1, 0.6 × (now − lock)) × 111.76 × dt`; the position is rail point plus offset, moved through
  the collision sweep `FUN_0048d7f0` like any other step;
- the cursor advances `dt × speed` metres in five sub-steps, each divided by the local tangent
  length, stepping across segment ends; running off the exit end sets the done flag.

On done: the lane count is decremented, the record freed, the state set to **0**, `+0x8a0`
stamped now + 5 s, and the net walk RE-SEATED: the nearest node (`FUN_00431900`) and the nose
edge pick (`FUN_00431e40`) excluding the edge the walk was on when the run began. The stun and
crash checks are both gated on state < 4 / < 2 in `FUN_004897c0`, so nothing interrupts a rail
run but a stun write, after which `+0x9bc` re-arms the approach at the cursor's current point.

⚠ **The `PR-DngrZn` line belongs to the gate count, not to the AI's run.** The zone module's own
completion routine `FUN_00446990` broadcasts combat-voice trigger 15 through `FUN_004b86a0(0xf)`
at `0x004469ff`, inside the branch that needs more than one gate crossed (`0x004469dc`) and right
after the loop that clears each gate's crossed byte `+0x10` (`FUN_00446930` sets it). The same
branch flags matching `DANGER_ZONES_COMPLETED` objective names, increments the completion count
`_DAT_0071d328` and arms the snapshot `DAT_0064fb78`. `FUN_0048e580` reaches that routine for the
local player's vehicle alone (`CMP EDI,[0x0071c298]` at `0x0048ea1f`), so the line is the flight
praising the player's run through the gates, and an AI's own rail run never speaks it.

### What CSVM ports of this

`Flight/Modes/DangerZoneRibbon.cs` is the spline, the run cursor and the rail integrator with every
constant above; `Flight/Modes/DangerZoneRibbons.cs` reads every `dzpathN` of the chapter gamez by the
route-versus-gate material rule, takes each zone's difficulty off its node's flag word and applies
`dzones.zrd`'s `disable` list. `AiNetFollower`
reports the node it just reached (`ArrivedNode`), `AiPilot` takes the node-tag entry into
`AiModeMachine.ApproachingDangerZone`, locks at 105 m into `NavigatingDangerZone` and publishes
`RailPose`, which `FlightController.SimStep` applies in place of the model step; the exit
re-seats the follower through `AiNetFollower.Reseat`, which is handed the leg the walk was on at
the entry and refuses it, the exclusion above. Measured on C2/M03 with the world's colliders up
(the `campaign-racers` suite, 208 s of sim): all six racers fly `dzpath1, 2, 3, 10, 6, 7, 9` in
the net's tag order, each once, through approach, lock and exit.

The proximity roll is ported beside the tag entry. `AiModeMachine.RollDaredevil` is the roll and
the 5 s stamp, with the original's own log strings; `DangerZoneRibbons.ProximityPick` is the zone
walk, the difficulty and free-lane admission and the 500 m best-facing end; `AiPilot` calls them
from the combat modes with `Evading` up, which is the remake's `+0xba`, and both entries then run
through one `StartDangerZoneRun`. The class gate lives where the chance is assembled
(`AiFlightAssembler`, a non-`jet` gets a chance of 0), so a wingman escort keeps its station under
fire. The trigger 15 line rides the player's own completion report:
`CampaignDirector.NotifyDangerZoneCompleted` raises `WorldInputs.DangerZoneSpoken`, which
`GameSession` turns into `AiVoiceRuntime.DangerZoneCompleted` for the flown aeroplane.

⚠ **The exclusion is what carries a racer out of a zone.** Without it the seat pick after a run
is free to take the leg back toward the tagged node, and `dzpath3`'s exit sets the aeroplane
down where that leg is the best-aligned edge under its nose: the racer flies back, reaches the
tag again and re-locks the zone it just flew, forever. The retry stamp is no help here, because
`FUN_00421500` never reads `+0x8a0`; the tag arm has no cooldown at all.

⚠ **The rail runs under the original's collision, and that collision is one point for an AI.**
`FUN_00490590` moves the rail position through the sweep `FUN_0048d7f0` like any step, and a
positive severity reaches `FUN_0048d2c0` and its AI doom rule, so nothing exempts a rail run
from a wall. What lets the racers through `dzpath2`'s `dbase` arch (a 9.7 m slot at the rail's
18 to 20 m height, the route vertex `(-6035.9, 18, -3825)` inside it) is the contact shape:
the sweep carries the def's `collision` probes, and every AI def resolves `basic_airplane`'s
single origin probe (docs/formats/vehicle.md "Collision probes"), so the wings never touch the
posts. The rail's roll is the second derivative's lateral part normalised whatever its size,
which on that near-straight stretch wanders between 6° and 63° and never reaches the knife
edge the slot would need; the original does not need one. A hull-swept AI rams the arch's
front face with a wingtip at `(-6038, 23, -3848)` every time, which is what the
`campaign-racers` suite holds against with the world's colliders up.

A generator's launch is handed the same ribbons when it is booked into the roster
(`CampaignDirector.RegisterGeneratorLaunch`), since the world phase that hands them out has already
run by the time a bay launches: without that, C5/M04's Miles could not take `dzpath34`, which is the
install's one tagged node on a generator's net. The `generator-launch-danger-zone` suite launches him
off the Dante over the mission's built world, seats him on `M4MilesRun` the way OBJECTIVE60 does and
reads the entry off his pilot.

Not ported: the suspend flag the roll's gate reads first (nothing in CSVM suspends a mission that
way), the target release at the lock (CSVM's gunner target is the host's), the altitude-floor
bypass on the approach solve, the lane table past the zero lane (no shipped node has one), and the
vertical nose a
bay launch seats its net with (CSVM seats a launched follower on its first update, from the
aeroplane's live nose, so a drop's first leg is the best-aligned one rather than the first-listed).

## The merge rule: what pursue does when two aircraft close nose to nose

Decoded 2026-08-16 to answer "is there a proximity check against other planes?". There is no
separate one. Beyond the ray above, the ONLY aircraft-aware term in the whole AI is a block inside
pursue, `FUN_0041d9f0` at `0x0041e130`–`0x0041e297`. The net follower `FUN_0041d1f0` has none at
all (its only correction is a cross-track nudge, the perpendicular error × 0.9 clamped to 200 m),
and `FUN_0041afe0`, which pursue calls every frame, is the gunnery lead solve.

The block arms on four conditions, all of which must hold. Let `u` be the unit vector from the
pursuer to its victim:

- the victim casts to `TargetVehicle` and its `+0x67c` is 0 or 4, i.e. it is a `jet` or a
  `wingman`. A ground or sea target never arms this;
- range under **400 m**;
- `dot(ownVelocity, u) > 0.8 × ownSpeed`, the pursuer is flying at the victim, within about 37°;
- `dot(victimVelocity, u) < −0.8 × victimSpeed`, the victim is flying back at the pursuer, within
  the same cone.

What it then does is **not** a break-off:

- the `desiredVelocity` argument handed to the steering law, which is otherwise the victim's own
  velocity, becomes `u × 31.292799` (70 mph). Since the law clamps its speed demand to ±26.8224 m/s
  around the current speed and floors it at 22.352 m/s, this collapses the demand into the pass;
- the aim point's Y gains `sqrt(u.x² + u.z²) × f`, where `f` is `−0.3` at or below 22.352 m/s
  (50 mph), `+0.3` at or above 40.2336 m/s (90 mph), and `(ownSpeed − 31.292799) × 0.033554047`
  between them. ⚠ `u` is already normalised here, so the whole term is worth **0.3 m at most**. It
  is a real branch and it does effectively nothing.

So the engine's answer to a head-on is to throttle back and hold the line. The aim point itself is
unchanged, and it is the victim's own position whenever the aspect test at `0x0041dd49` reads
head-on or tail-chase (`|dot(u, victimBackwardAxis)|` over the pilot's `quick_draw_angle` cosine at
vehicle `+0x960`, default `0.70697`, cos 45°).

⚠ **A second arm exists and is a player-only special case**, the **reversed arm**: pursue's aim
direction is negated, so the pilot steers away from its victim rather than at it, and the block it
passes the steering law is `DAT_0061fb68` instead of `DAT_0061fb08` (see the parameter table below).
It is gated by the global `DAT_0064ee4d`, so at most one aircraft per frame may take it, plus the
roster byte at vehicle `+0x989` (written by `FUN_0047c210` at `0x0047ca5a`, defaulted to 1 by the
vehicle constructor `FUN_004aff80`) and the per-vehicle flag `+0xba`. Inside 400 m the merge block
gives this arm its own response instead of the two above: aim 500 m along the victim's own forward
axis, past it, and demand `−31.292799 ×` that axis. A jousting pass at the player, not avoidance.
CSVM does not port it.

⚠ The aspect test takes **both** of the victim's cones, since it compares a magnitude. A pursuer
sitting on its victim's tail reads the same as one merging with it, and both aim the law at the
victim itself; only a beam aspect flies to the lead point. Nothing downstream tells the two apart
except the reversed arm's own gate.

Ported 2026-08-16: `AiPilot.IsOnGunAxis` (the aspect test, both cones), `AiPilot.IsMerging` and
`AiPilot.MergeVerticalBias`, all applied in `FlyPursuit`.

## The steering law both behaviours call

`FUN_0041b560(this, stationPoint, desiredVelocity, params, emergencyFlag, leadFlag)` turns a target
point into stick and throttle. It is the original's AI control law; the law itself is decoded in
[`aiControlLaw.md`](aiControlLaw.md) and ported as `AiControlLaw`. What this page settles is which
of its **four** 8-float parameter blocks each behaviour passes.

| block | first two | used by |
|---|---|---|
| `DAT_0061fb08` | 0.3, 1.3 | pursue, `FUN_0041d9f0`'s normal arm |
| `DAT_0061fb28` | 0.4, 1.5 | the escort law, and its avoid-crash climb-out |
| `DAT_0061fb48` | 0.6, 1.3 | the net follower's avoid-crash climb-out |
| `DAT_0061fb68` | 0.8, 1.1 | the net follower's normal patrol, and pursue's reversed arm (above) |

⚠ `DAT_0061fb08` is the one block that does not share the others' tail: it carries
`0.08, 0.01, 0.2, 0.025, 0.9, 0.0`. Pursue also scales its own desired-velocity vector by **0.9**
whenever it passes this block.

Shared tail on the other three: `0.06, 0.06, 0.15, 0.025, 0.35, 0.0`. Entries 0 and 1 are the throttle
floor and ceiling, which the law walks toward at **0.35 per second** and then clamps; entries 2 and
3 are the stick thresholds below which the second control axis is
added; entries 4 and 5 weight the along-track and across-track components of the intercept; entry 6
is the bank-authority threshold. The law also clamps its own speed demand to a ±**26.8224 m/s**
(60 mph) band around the current speed, and floors it at **22.352 m/s** (50 mph).

## `netids` is a list, and the engine picks one at random

The roster spawn `FUN_0047c210` reads the count at block `+0x10` and the array at block `+0x14`
(`0x0047c733`–`0x0047c76d`):

- count 1 takes that id;
- count above 1 takes **`rand() % count`**, drawn once at spawn;
- count 0 or less writes `-1`.

This install authors a scalar per block, so the draw never fires in the campaign, but the reader is
the list reader and the editor comment quoted in [`ai-rosters.md`](../formats/ai-rosters.md) is
right to call it a list.

Activation is separate from all of this. `FUN_004b0f40(vehicle, deactivated)` is the
activate/deactivate primitive, clearing or setting `+0x945`, `+0x91d`, `+0x91e` and `+0x91f`, and is
called from the spawn with the roster's `deactivated` field. On activation, a vehicle that has a net
is snapped to that net's nearest node (`FUN_00432010`).

## `preferred_engagement_altitude` is a maneuver-selection weight

The roster's `pref_engage_alt` (the editor comment's name; `vehicle.json` spells it
`preferred_engagement_altitude`) is block `+0x98`. `FUN_0047c210` (`0x0047d44b`) copies it to the
vehicle at `+0x98c`, falling back to the airframe def's own value when the block leaves it at
`-1.0`. `basic_airplane` authors **300.0** and every aircraft def inherits it; the vehicle
constructor's own default is also 300.0 (`0x004b0434`).

Its **one reader in the image** is `FUN_004201a0` at `0x004204da`, the evasive-maneuver selector: a
candidate maneuver's weight gains **+1.0** when the aircraft's altitude (`+0x208`) sits on the wrong
side of the preferred altitude. It is a bias on the maneuver library draw and **not** an altitude
order. Nothing steers toward it.

## What the shipped rosters actually do

Census of all 53 `aiv.zrd.json` files, 414 blocks, 2026-08-15:

| | blocks | `netids` |
|---|---|---|
| `player` | 53 | −1 |
| `wingman_N` | 50 | −1 |
| `bswingman_N` | 3 | −1 |
| everything else | 308 | a real net id, every one |

**Every enemy, boat and truck in the campaign is netted, and the only netless AI in the campaign is
the player's wingmen.** Their node names are `wingman_N` and `bswingman_N`, which are exactly the
two non-`w<plane>` defs that author `mode wingman`. The two halves agree: netless is the wingman
case, and the wingman case is the escort law.

## Instant Action gives every actor a net

⚠ This corrects [`instant-action.md`](../formats/instant-action.md), which recorded that an Instant
Action actor leaves `netids` at its `-1` default.

`FUN_0045a390` builds each actor's roster block on the stack and, in **all three** of its branches
(the wingmen loop, the ace, and the wave loop), writes:

```
block +0x10 = 1                                   the netids count
block +0x14 = malloc(4), holding the first entry of the chapter net-id table DAT_0064f610
```

at `0x0045a8b4` (the wingmen), `0x0045ab18` (the ace) and `0x0045ae85` (the waves). Each reads the
table's **entry 0** and takes that entry's id, so every Instant Action actor is handed the id of
the first pair in the chapter's `neindex` (see "The chapter's net table" above): net 10
(`M4ReinfAce`) in C1, 29 (`Patrolboat3`) in C1B, 25 (`M1Defense`) in C1C, and net 1 in each of the
remaining five.

Their volumes are `FUN_0045a240`'s ±10000 m, not the net's: the block wins the overwrite (above).

Two consequences follow from the demotion rule above, read off the code path and consistent with
the original at the controls:

- Instant Action **wingmen are demoted from `wingman` to `jet` at spawn** and walk that net like
  everything else. The `w<plane>` defs contribute their pilot and airframe values, not their mode.
- The Instant Action escort chain that `instant-action.md` decodes from `primary_target`
  (0/1/3 on the player, 2/4 on 1/3) is therefore **not** flown as a formation in that mode. It
  survives as a target assignment.

What the original's wingmen do instead is the acquisition: on C1's CM02 they enter pursue at the
start and attack the zeppelin's turrets and engines, which is the sweep picking a `TargetTurret` or
`TargetStruct` inside the ±10000 m volumes and pursue flying at it ("What pursue does with a
non-vehicle target" above). The net is only what they fly when the sweep finds nothing. The escort
law is a campaign behaviour and the wrong fix for a wingman that leaves the fight.

## Function map

| Address | What |
|---|---|
| `FUN_004897c0` | the world tick: iterates vehicles, gates on `+0x91d` / `+0x944`, calls the AI update and the physics |
| `FUN_00489ea0` | physics dispatch on `mode` |
| `FUN_0041c270` | the per-frame AI update and its behaviour fork |
| `FUN_0041fe10` | target selection, writes `+0x948` |
| `FUN_0041d1f0` | the patrol-net follower |
| `FUN_0041d9f0` | pursue, including the 400 m merge rule at `0x0041e130` |
| `FUN_0041e760` | the formation escort law |
| `FUN_0041f040` | the promotion to pursue: the `+0x300` dwell refusal, the anchor, the dwell re-arm |
| `FUN_0041b560` | the shared steering law: point in, stick and throttle out |
| `FUN_004311c0` | builds the chapter net table from `neindex`, in file order (`FUN_00431300` frees it) |
| `FUN_004314e0` | builds one `CCENet` from its record: nodes, edges, volumes, and the trailer's name→object resolve |
| `FUN_00431a90` | per edge at net load: the delta, its 3-D length, and the arrival radius squared into `edge+0x1c` |
| `FUN_004303d0` | the `CCENet` constructor, whose `+0x28` default is the 10 m arrival-radius floor |
| `FUN_0041d8f0` | steps the walk to the next edge once the arrival test fires, excluding the edge just flown |
| `FUN_00431e40` | which edge leaves a node: the one whose leg best lines up with the vehicle's nose |
| `FUN_00432010` | node position with the trailer offset applied: the "this net rides that object" rule |
| `FUN_00432140` | node position by index, the wrapper every consumer calls |
| `FUN_00431900` | nearest node to a point, skipping edgeless nodes |
| `FUN_00475fc0` | net assignment (`SET_AI_NET`), including the volume overwrite |
| `FUN_00475f30` | the by-name net assignment: table scan on the entry name, then `FUN_00475fc0` |
| `FUN_004735b0` | the `player.zrd.json` loader, including `min_ai_active_dist` |
| `FUN_0049c920` | script-side net assignment, always forces `mode` to `jet` |
| `FUN_0049c880` | releases every wingman onto a net through the above; UNREFERENCED in the image |
| `FUN_00475820` | def to vehicle copy, including `mode` |
| `FUN_00476250` | post-spawn vehicle init, including the wingman demotion |
| `FUN_0047c210` | the roster spawn: `netids` draw, `preferred_engagement_altitude`, activation |
| `FUN_004b0f40` | activate / deactivate |
| `FUN_00479240` | the `vehicle.json` def parser, including the `mode` string table |
| `FUN_004201a0` | evasive-maneuver selection, the one reader of `preferred_engagement_altitude` |
| `FUN_0041c470` | the debug overlay that names the modes and recomputes the target ranking |
| `FUN_0041fe10` | target acquisition: primary target, sticky standing target, else the sweep |
| `FUN_0041f9c0` | the sweep over the four candidate lists, and the pick |
| `FUN_00421ad0` | the scorer for `jet` and `wingman`, with the three ±0.2 terms |
| `FUN_00421950` | the scorer for every other `mode`, base weight and the two class terms only |
| `FUN_0041ae40` | the `rating_biases` lookup, returning rank units |
| `FUN_00420070` | the `DAMAGES_ZEPPELIN` ordnance check that admits gasbag candidates |
| `FUN_0041f420` | the fire decision every behaviour calls, [`aiPilot/aiWeapons.md`](aiPilot/aiWeapons.md) |
| `FUN_004459f0` | builds the `DZPathList`: one zone per `dzpath` gamez node (`FUN_00445ef0`) |
| `FUN_00445da0` | applies `dzones.zrd`'s `disable`, `nosnapshot` and `objective_numbers` to the zones |
| `FUN_00421500` | the node-tag entry by name: nearer end, run record, state 2 |
| `FUN_004210e0` | the pick over all zones: forced (nearest end) from a negative tag, rolled (daredevil, 500 m) from pursue |
| `FUN_004216e0` | mode 2, the approach: the run's current point on the emergency table, lock at 105 m |
| `FUN_00490590` | mode 5, the rail: pose written off the ribbon in place of the physics, exit re-seats the net |
| `FUN_004465b0` / `FUN_00446670` / `FUN_00446710` | a ribbon point, tangent and second derivative at (segment, metres, lane) |
| `FUN_00431b70` | the goal-node routing table no shipped net carries (`+0x10`/`+0x20`) |

## Open

- `FUN_0041b560` is described by its parameter table only. The law itself (how it converts a station
  point into bank, pitch and rudder) is a separate decode, and it is what would replace
  `AiPilot`'s placeholder.
- `mode_alt` has no identified consumer.
- Whether the original's Instant Action wingmen visibly hold station is untested. The code path says
  they do not.
