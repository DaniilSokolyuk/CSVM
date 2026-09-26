# What each ordnance type does, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`). Every claim names the function it came from, so any of
them can be re-checked at source. No decompiler output is reproduced.

**Where the other halves live.** The authored side, meaning which keys a weapon entry carries and
what values this install ships, is [`formats/weapons.md`](../formats/weapons.md). The per-surface hit
effect table is [`weaponImpact.md`](weaponImpact.md). The AI's side of firing them is
[`aiPilot/aiWeapons.md`](aiPilot/aiWeapons.md). Our implementation is `WeaponDefs.cs` (which parses
every key below and acts on none of the special ones) and `Projectile.cs`.

**Scope.** This page covers the **flag map**, the **launch-side dispatch** (which types fire at all,
and what they spawn), **guidance**, the **proximity fuse**, the **shootable flyout**, and the
**hit-side dispatch** (what happens when a round reaches an aircraft).

## A second flags word, and four keys nothing authors

`FUN_004ba6f0`'s extension struct is not the only place a weapon's behaviour lives. The `.zrd`
dispatcher `FUN_005ad630` writes a **separate** flags word at weapon record `+0x74`, and the guidance
and spawn paths read that one, not the extension struct. The two are unrelated bit spaces: `0x08` in
the extension struct means `TORPEDO`, while `0x08` at `+0x74` means the weapon authored
`FLYOUT_HEALTH`.

| `+0x74` bit | Set by |
|---|---|
| `0x08` | `FLYOUT_HEALTH` is present |
| `0x80` | `EXPIRES` |
| `0x800` | `INSTANT` |
| `0x4000` | `MINE` |
| `0x8000` | `LOCK_ON` is present |
| `0x10000` | `LOCK_ON_LEAD` is present |
| `0x100000` | `REMOTE_DETONATE` |
| `0x200000` | `TETHER_GUIDED` |
| `0x2000000` | `DETONATE_AT_RANGE` |
| `0x8000000` | `RANDOM_DEVIATION` |

And the scalar slots the guidance path uses:

| Offset | Key |
|---|---|
| `+0x30` | `TURN_RATE` |
| `+0x34` | `PITCH_RATE` |
| `+0x58` | `LOCK_ON` |
| `+0x5c` | `TURN_SUSPEND_TIME` |
| `+0x7c`, `+0x80` | `LOCK_ON_LEAD` element 0 and element 1 |
| `+0x84` | derived from the two above |
| `+0x8c`, `+0x90` | the flyout's damage accumulator (initialised to 0) and `FLYOUT_HEALTH` |

⚠ **Ten of the keys this parser accepts are authored by nothing.** `PITCH_RATE`,
`TURN_SUSPEND_TIME`, `TETHER_GUIDED`, `REMOTE_DETONATE`, `INSTANT`, `MINE`, `RANDOM_DEVIATION`,
`MULTI_TARGET`, `EXPIRES` and `IMPACT_TYPE` appear in no entry of this install's `weapons.zrd.json`,
so they are absent from [`formats/weapons.md`](../formats/weapons.md)'s field census, which
enumerates what the data carries. They still shape the shipped behaviour by their **defaults**: a
`TURN_SUSPEND_TIME` of zero gives every guided round its turn authority from the first frame,
`INSTANT` being unset makes every round a travelling projectile rather than a one-step hitscan,
`MINE` being unset is why every blast has a falloff, and `RANDOM_DEVIATION` being unset is why no
shipped round wanders.

Six of the ten have traced behaviour; four were only ever seen parsed:

| Key | Where | What setting it would do |
|---|---|---|
| `TURN_SUSPEND_TIME` | `+0x5c` | Suspends guidance for N seconds after launch, then ramps turn authority as `(age − N) / LOCK_ON`. |
| `PITCH_RATE` | `+0x34` | Terrain avoidance: a guided round within 10 m of the ground adds `PITCH_RATE × 2/π` of "up" to its desired heading. |
| `TETHER_GUIDED` | `0x200000` | Swaps that pull-up for a ceiling clamp: the round may not climb past a global altitude. |
| `INSTANT` | `0x800` | The round resolves in one step instead of travelling. Hitscan. |
| `MINE` | `0x4000` | No acceleration; halves its whole path accumulator every frame, so it never reaches `RANGE`; its own motion routine; and its blast applies full damage with **no falloff** anywhere in the radius. |
| `RANDOM_DEVIATION` | `0x8000000` | A bounded random walk when the round has no target or its target is far. |
| `REMOTE_DETONATE` | `0x100000` | **Reader not traced.** |
| `MULTI_TARGET` | `0x20000` | **Reader not traced.** |
| `EXPIRES` | `0x80` | Suppresses the detonation a `LOCK_ON` round would otherwise get when it reaches `RANGE`, leaving it to vanish. |
| `IMPACT_TYPE` | `+0x190` | **Reader not traced.** |

⚠ The ten are the unauthored keys this page enumerated, **not** every key the parser accepts and
nothing authors. `FUN_005ad630` also reads `DETONATE_AT_RANGE`, `DETONATE_ON_WATER`, `HIT_OWNER`,
`RELATIVE_SPEED`, `SHOW_ON_RADAR`, `FIXED_ROTATE` and `RELOAD`, none of them present in this
install's data. `DETONATE_AT_RANGE` is the one that matters to a shipped behaviour, since it is half
of the range-expiry detonation rule below; the rest are listed so the next reader does not take the
count of ten for a complete census.

⚠ Four of those defaults are load-bearing, and one is easy to miss: **`PITCH_RATE` defaulting to 0
means no shipped round avoids terrain.** The avoidance code runs on every guided round; it just adds
zero. That is why rockets fly into hillsides rather than nosing over them.

**Why the set looks like this.** Read together, these ten describe mines, wire-guided rounds held
under a ceiling, multi-target seeking and hitscan weapons. That is a **MechWarrior** weapon roster
rather than an aerial-combat one, and this is the MechWarrior 3 engine: the extractor these decodes
lean on is `mech3ax`, and `CSVM/src/Mech3/` is named for it. The economical explanation is that these
keys are the other game's features carried in a shared codebase and never authored here.
⚠ That is an inference from the key set plus the engine lineage, not a decode. It is recorded because
it explains the shape of the data, and it should not be cited as evidence for anything.

## The weapon-extension struct

`FUN_004ba6f0` is the weapon-def flag parser. It `calloc`s one 0x38-byte block per weapon and hangs
it off the ZWEP record at `+0x210`; every consumer below reaches the weapon's behaviour through that
pointer. The block's layout, all of it written by that one function:

| Offset | Field |
|---|---|
| `+0x00` | the flags dword (below) |
| `+0x04` | `PRIORITY` |
| `+0x08` | `-cos(CANNON_SPREAD deg)`, the aim assist's acceptance cone ([`aim-assist.md`](aim-assist.md)) |
| `+0x0c` | `CLUSTER_SIZE` |
| `+0x10` | `CALIBER`, defaulting to 0 when the key is absent |
| `+0x14` | `FIRING_HEAT`, defaulting to 0; no consumer reads it |
| `+0x18` | a **shared `TIME` slot**, default 5.0 |
| `+0x1c` | `TANGLER`'s `RADIUS`, default 10.0 |
| `+0x20` | `DETONATION_DOT_PRODUCT` |
| `+0x24` | `LOOPED_SOUND_NAME`, `strncpy`'d to 19 bytes |

**`+0x18` is one slot, written by three different keys.** `BEEPER`'s `TIME`, `SMOKE_SCREEN`'s `TIME`
and `TANGLER`'s `TIME` all land there, each defaulting to 5.0 when its block omits the key. No
shipped weapon carries two of the three, so nothing collides, but they are the same field and a
reader cannot tell which key wrote it except by the flags.

## The flags dword

Every bit `FUN_004ba6f0` sets, in the order the function tests for them:

| Bit | Key |
|---|---|
| `0x08` | `TORPEDO` |
| `0x10` | `ROCKET` |
| `0x20` | `TARGETABLE` |
| `0x40` | `CANNON` |
| `0x200` | `SONIC` |
| `0x400` | `HIGH_EXPLOSIVE` |
| `0x800` | `FLASH` |
| `0x1000` | `DAMAGES_ZEPPELIN` |
| `0x2000` | `SHAKES_CAMERA` |
| `0x4000` | `BEEPER` |
| `0x8000` | `BEEPER_SEEKER` |
| `0x10000` | `SMOKE_SCREEN` |
| `0x20000` | `REAR` |
| `0x40000` | `TANGLER` |
| `0x80000` | `DETONATION_DOT_PRODUCT` is present |

`0x01`, `0x02`, `0x04`, `0x80` and `0x100` are never set by the parser. Note that the last row is a
**presence** bit, not a behaviour flag: the value itself goes to `+0x20`, and the bit only records
that the weapon authored the key.

There is no bit for `HIGH_EXPLOSIVE`'s counterpart, no bit for guidance, and no bit for `CRATER`.
Armour-piercing versus high-explosive is the `ARMOR_DAMAGE`/`HEALTH_DAMAGE` pair plus this one bit;
guidance is `TURN_RATE` with no flag at all.

Parsing `TANGLER` has a side effect the other flags do not: `FUN_004ba6f0` registers a handler on the
weapon record through `FUN_005aec90` (target `0x004ba660`, which Ghidra has not resolved as a
function). That handler lands at weapon `+0x20c` and is the per-weapon impact hook the detonation
path calls first; it builds the choker's cloud and silences the row's sound. See
[the detonation section](#detonation-the-impact-then-the-splash) and "The choker, settled".

## The launch-side dispatch

`FUN_004b6820` is the shot routine. It walks the shooter's weapon-slot list and acts on each slot
whose trigger byte is set. Everywhere in it, gun versus ordnance is decided by shifting the flags
dword right by 6 and taking bit 0, which is `CANNON`.

**The aim gate, and who it applies to.** Before anything is spawned, the mount's aim quality
(the `+0xa4` written by `FUN_004b7670`, see [`aiPilot/aiWeapons.md`](aiPilot/aiWeapons.md)) must
reach a threshold that differs by weapon class: **0.9962 for ordnance, 0.9848 for guns**, that is
cos 5 degrees against cos 10 degrees. Ordnance must be aimed more than twice as precisely as a gun
before it will leave the rail. The whole gate is **skipped when the shooter is the player**, and also
under a shooter flag at `+0x3e`.

**`REAR` inverts the gate and the spawn.** Already decoded in
[`aiPilot/aiWeapons.md`](aiPilot/aiWeapons.md), and repeated here only for what it adds: for a `REAR`
weapon (`0x20000`) the routine negates the mount's aim quality before testing it, so the weapon fires
when the target is **behind**, and it requires the shooter's `+0xba` byte, which that page reads as
being-pursued and which the hit handler sets when the aircraft absorbs damage, refreshing an expiry
8 seconds out. New here: `REAR` also flips the **spawn** direction (the launch axis is negated for an
ordinary weapon and taken as-is for a rear one) and flips the sign of a 10 m offset along that axis.
⚠ **`REAR` appears exactly once in the whole weapon table, on `wep_13`, the smoke screen.** The flare
`wep_15` is *called* `MSG_WEAP_REARARC_FLASH` and does **not** carry the flag. Its rear-arc character
comes from its own numbers instead: `VELOCITY [1.0]` so it barely moves, `LOCK_ON [2.5]` so it leaves
at the launching aircraft's speed and sheds it over 2.5 s, and `DETONATION_TIME [2.0]` so it goes off
two seconds after release. You drop it and fly away; it hangs where you left it and flashes at
whoever is still there. `IMPACT_PROXIMITY [500]` gives it an enormous radius, and `FLASH`'s
facing requirement is what keeps it from blinding you as you leave.

**`DAMAGES_ZEPPELIN` is a two-way gate, not a permission.** The routine asks the current target
whether it is a zeppelin (vtable slot `+0x1c`) and drops the trigger both when a non-`DAMAGES_ZEPPELIN`
weapon is pointed at a zeppelin **and** when a `DAMAGES_ZEPPELIN` weapon is pointed at anything else.
The torpedo is therefore restricted to zeppelins on this path, not merely permitted against them.

**`SMOKE_SCREEN` spawns no projectile.** Both the player and the AI branch test `0x10000` around the
spawn call. Without it, the round goes to the ordinary projectile spawn `FUN_005aef40` and is
registered by `FUN_00441830`. With it, neither runs: the routine allocates a 0x18-byte object through
`FUN_004b8d50`, handing it the shared `TIME` at ext `+0x18`, and pushes it onto a world list
(`DAT_0071dbbc`, counted by `DAT_0071dbc0`). The smoker is a world effect placed at the mount, not a
round in flight. ⚠ The weapon's `FIRE` row does **not** play on a smoke launch. The row's sound
(weapon `+0xc4` indexed by the `+0xe8` slot `FUN_005ac120` selects) and its animation list
(`+0x98`) are both spent inside `FUN_005aef40`, the spawn the `0x10000` branch skips; what the
launch does emit is the screen object's own emitter, created in `FUN_004b8d50` through the effect
system. The **ammo** decrement sits after the branch and is shared, so the smoker spends a round
like any other pylon weapon. Our side is `FlightController.ApplyFireOutcome`, which calls
`SmokeScreens.Lay` in place of the spawn.

**Firing a `LOCK_ON` weapon with no target acquires one.** Gated on `+0x74` bit `0x8000`, which is
`LOCK_ON` being present, and taken only when the player currently holds no target, the routine writes
an entry into a 20-slot ring buffer at `DAT_0071dbc8` and points the player's target block
(`+0xa4`..`+0xa6`, `+0x1ca`) at it.

**The firing shake is sized by `CALIBER`.** When the player fires, the routine calls `FUN_0042c070`
with kind **0** and `CALIBER` times a global at `DAT_0064ef78 + 0x3c`. That is the same feedback call
the hit handler uses with kinds 1, 2 and 3, which confirms from the other direction that
`SHAKES_CAMERA` is not the fire-path mechanism.

## Who aims ordnance, and who does not

The gun aim assist `FUN_004b6530` is reached from the **`CANNON` branch only**. Both ordnance
branches, the player's and the AI's, bypass it, so no ordnance round of any shooter is aim-assisted.
What separates the two is the direction each hands the spawn `FUN_005aef40`:

- **The AI's ordnance leaves along the mount's clamped aim, in world space.** The direction argument
  is the mount record's `+0x3c`, which `FUN_004b7670` writes at the end of every mount update as the
  clamped aim `+0x48`–`+0x50` rotated through the aircraft's orientation matrix. That aim tracks the
  lead solve within the authored `gun_pitch`/`gun_yaw` band
  ([`aiPilot/aiWeapons.md`](aiPilot/aiWeapons.md)), so an AI's rocket really does leave along a
  direction that follows the target.
- **The player's ordnance leaves along the aircraft's own basis axis**, negated, or taken as-is for a
  `REAR` weapon. The mount contributes the spawn **position** only; its aim is not read.

So the original gives the player no aim component on ordnance, and gives the AI one. Our side splits
the same way, in `FlightController.OrdnanceLaunchDir`. ⚠ No shipped player airframe cants a pylon
marker: every `pylonN` node's basis is square to its airframe, so the marker axis and the aircraft
axis coincide on the shipped fit and the split is a guard rather than a visible difference.

## Guidance

`FUN_005af960` is the per-round steering step, and `FUN_005af720`, the projectile tick, decides who
gets it. The gate is **`LOCK_ON` present (`+0x74` bit `0x8000`) and the round holding a target at
`+0x70`**. A round failing either condition never enters the steering step at all; it goes straight
to the motion step, `FUN_005afd50` normally or a plain constant-velocity integration when `+0x74` bit
`0x800` is set.

So "guided" has two independent gates in the original, and neither is a `GUIDED` flag: a weapon must
author `LOCK_ON` to be steered at all, and the round must have acquired something. `TURN_RATE` then
decides how hard it may turn, with the 0.001 sentinel meaning a `LOCK_ON` weapon that is steered but
turns almost imperceptibly.

**The desired direction** starts as the bearing from the round's position to its target
(`FUN_00538ca0` on `+0x48` and `+0x70`). If the weapon authored `LOCK_ON_LEAD` (`+0x74` bit
`0x10000`), the round's age `+0x668` is at least `+0x7c` and the round's second target field `+0x74`
(the pointer to the target's velocity) is set, `FUN_0053e56d` solves an intercept instead: it is the
constant-velocity intercept the aim assist also solves (in the same `u = 1/t` form, the answer
being `normalize(targetVel + (targetPos − roundPos) × u)`) on the round's **own** speed `+0x640` and the
target's velocity, and it returns 0 with no real root, which leaves the plain bearing standing.
While the age is under `+0x80` the routine `FUN_00538d70`-slerps from the bearing to the lead by
`(age − +0x7c) × +0x84`; from `+0x80` on it takes the lead outright. `+0x84` is written by the parse
(`FUN_005ad630`, `0x005ade1a`–`0x005ade52`) as `1 / (element1 − element0)`, only when the two
differ, after clamping element 0 up to at most element 1. So the lead comes in gradually rather than
at once. ⚠ No shipped entry reaches it: the three carriers (`wep_04`, `wep_25`, `wep_27`, at
`[4,8]`/`[5,10]`) all author `TURN_RATE 0.001` and `RANGE 900`, and each expires at that range
before its onset age even off a standing launcher (`wep_04` at about 3.46 s of its 4 s), so the
blend is real engine behaviour that this install's data never shows.

**The turn authority per frame** is

    maxTurn = turnScale * TURN_RATE * dt * ramp,  where
    ramp    = 0                                       while age < TURN_SUSPEND_TIME
            = (age - TURN_SUSPEND_TIME) / LOCK_ON     when TURN_SUSPEND_TIME > 0
            = 1                                       when TURN_SUSPEND_TIME is 0 (every shipped weapon)

in radians. `turnScale` is `DAT_00a1e1b8`, a per-shot global: the weapons init (`FUN_005ad4e0`)
writes it as 1.0 and the spawn (`FUN_005aef40`, `0x005af0cd`) copies it into the round and resets
it to 1.0 after every round it makes, and nothing else writes it, so it is 1 on every steering
frame in this binary. The `age` the ramp and the decay read is `+0x63c`, a clock the steering step
itself advances (and only while it is at or under `LOCK_ON`), not the round's age `+0x668`, so a
round that is not steered does not run it down. The routine takes the angle between the current
heading and the desired one, and if it exceeds `maxTurn` it slerps by exactly `maxTurn / angle` and
renormalises; otherwise it snaps to the desired direction outright.

**Turning costs speed.** After the turn, whenever that angle was above zero, the round's speed is
multiplied by `0.8 + 0.2 * cos(turned)`, where `turned` is the angle the heading actually swung this
frame: `maxTurn` when the turn was clamped, the whole angle when it snapped (`fcos` of `local_14` in
the clamped arm, of the acos result otherwise). It is applied per steering frame, never once per
turn, and because it is the cosine of one frame's swing the loss over a whole turn scales with the
frame's authority rather than with the turn: at 60 fps the seeker's 1.25 rad/s costs about 0.3%
over a 90° swing, and a coarser step costs more. Nothing in the authored data hints at this.

**Terrain avoidance is a weapon field.** When the round is within 10 m of the ground and a collision
probe comes back clear, the routine adds `PITCH_RATE * 2/pi` of "up" to the desired direction and
renormalises, so a guided round noses up to clear terrain. A `TETHER_GUIDED` weapon takes a different
branch: instead of the pull-up it **zeroes any climb component** once the round is near a global
altitude limit, which reads as a ceiling clamp rather than a floor.

### Every round inherits its launcher's velocity; only a motor round can lose it

This is the piece with the most consequence, and it is not visible in the data at all. It runs
through two different slots, and reading either one alone gives the wrong answer.

**A round with no motor is seeded with the launcher's velocity and keeps it.** The spawn splits at
`0x005af124`–`0x005af13e` on `ACCELERATION` (`+0x38`) being zero and `INSTANT` (`+0x74` bit `0x800`)
being absent. In that arm the speed `+0x640` takes `VELOCITY` (`0x005af147`), equal to the cap
already written at `0x005af0ea`, and `FUN_005389a0` builds the round's velocity vector `+0x60` at
`0x005af15a` as

    velocity = launcherVelocity + VELOCITY * direction

under no flag gate at all. The motion step moves the round by `dt * velocity` and rebuilds `+0x60`
only while the speed is below its cap, so a round seeded AT its cap is never rebuilt and carries the
launcher's velocity for its whole flight. **This is what every gun does**: all 31 `CANNON` entries
author neither `ACCELERATION` nor `LOCK_ON`, and nothing in this install authors `INSTANT`. The gun
aim assist's relative-velocity intercept and the pipper's `muzzle + 0.5 * (VELOCITY * nose +
planeVelocity)` ([`aim-assist.md`](aim-assist.md)) are both built on this round, and both are right.

**`+0x30` is the motor round's base vector, not the inheritance.** A weapon authoring
`ACCELERATION` takes the other arm and has its `+0x60` rebuilt every frame as
`+0x30 + speed * heading`, so what it inherits is whatever `+0x30` holds. That is where the
`LOCK_ON` gate sits: the spawn zeroes `+0x30`..`+0x38` at `0x005af5eb` and copies the **launcher's
velocity vector** in at `0x005af626`, only when the weapon carries `LOCK_ON` (`+0x74` bit `0x8000`,
tested at `0x005af616`). A motor round without `LOCK_ON` rebuilds from zero, keeping the launcher's
speed as the scalar added to its speed and cap (below) along its own heading, and none of the
vector. The four accelerating types are `wep_04`/`25`/`26`/`27`.

Then every frame, while the round is younger than `LOCK_ON` seconds, the guidance step sets

    velocity = heading * speed  +  ((LOCK_ON - age) / LOCK_ON) * inheritedLaunchVelocity

so the launcher's contribution is blended out **linearly to zero over `LOCK_ON` seconds**, leaving
the round travelling at its own authored `VELOCITY`. A round launched from a fast aircraft therefore
starts fast and visibly slows to its own cruise, and one launched from a slow aircraft does not.

⚠ This decay lives **inside** the steering step, so it inherits that step's gate: a `LOCK_ON` round
fired with **no target** is never stepped through `FUN_005af960` and so keeps its inherited launch
velocity indefinitely. In practice the shot routine hands a `LOCK_ON` weapon a target when the player
has none, so the usual case is the decaying one, but the two are not the same rule. CSVM
deliberately runs the decay for every `LOCK_ON` round, target or none, because it does not
reproduce that synthetic target; the divergence is recorded on `Projectile.cs` in
[`architecture.md`](../architecture.md).

`LOCK_ON` is doing three separate jobs, which is why the name misleads: it is the lead-guidance ramp
denominator, the launch-velocity decay window, and the flag that decides whether a **motor** round
inherits its launcher's velocity as a vector. [`formats/weapons.md`](../formats/weapons.md) describes it as lock-acquisition
time; that is the one job this decode did **not** find it doing.

## The motion step, and where a round dies

`FUN_005afd50` moves every round that is not being steered, and it holds most of the remaining
ballistics keys. In order:

- **Acceleration.** While the round's speed (`+0x640`) is below its cap (`+0x644`), speed increases
  by `ACCELERATION * dt` (weapon `+0x38`) and is clamped to the cap (the test at `0x005afd76`, the
  clamp at `0x005afd98`). A round already at or past its cap is left alone rather than clamped down.
  Nothing anywhere reduces speed except the steering step's turn penalty, so **there is no drag term
  in the original**.
- **What the cap is, and where a motor round starts.** Both are seeded in the spawn,
  `FUN_005aef40`. The cap takes the weapon's `VELOCITY` (`+0x2c`) at `0x005af0ea`. A weapon with no
  `ACCELERATION` (and without `INSTANT`) then has its speed seeded at `VELOCITY` too
  (`0x005af147`), **equal to its cap**, which is why nothing accelerates it. A weapon that does
  author `ACCELERATION` has its speed seeded at `1e-4` instead (`0x005af318`) and the **launcher's
  own speed** `|launcherVelocity|` added to both the cap (`0x005af356`) and the speed
  (`0x005af371`). So a motor round leaves at the speed of the aircraft that fired it and climbs to
  `VELOCITY` **above** that, rather than starting at `VELOCITY`: `wep_04` off a standing launcher
  takes its full 3 s to reach 450 m/s, and `wep_27`'s flak leaves an emplacement at nothing and is
  still short of its authored 850 when its 900 m `RANGE` runs out.
  ⚠ For a `LOCK_ON` motor round the launcher's speed therefore arrives twice: once as this scalar
  and once as the inherited vector at `+0x30`.
- **Gravity.** When the weapon authors `GRAVITY` (weapon `+0x54`), the round's vertical velocity
  loses `GRAVITY * dt` each frame. It is an acceleration in m/s², not a scale on world gravity. The
  reader exists and works; every entry in this install authors 0.0, which is why the shipped rounds
  fly flat. ⚠ The one path that would expose it is broken in the original: an accelerating round
  with a non-zero `GRAVITY` rebuilds its velocity as `velocity + heading * speed` each frame
  (`FUN_005389a0` called on `+0x60` rather than on the inherited vector at `+0x30`), which
  compounds. No shipped weapon reaches it.
- **First-frame guard.** On the frame a round is spawned, the integration delta uses `1e-6` instead
  of the real frame time, so a round never jumps a full step on its birth frame.
- **Wander.** Under `RANDOM_DEVIATION`, and only when the round has no target or its target is
  farther than `max(4 * DETONATION_DISTANCE, 400 m)`, three `rand()` draws build an angular
  acceleration of magnitude `5 * dt`, sign-flipped to keep pushing outward, integrated into an offset
  bounded at ±1.0, ±0.75, ±1.0 with a restoring `10 * dt`. An unguided or far-from-target round
  wobbles rather than flying a perfect line.
- **Hittable distance, which is what `RANGE_MINIMUM` actually is.** The key sets `+0x74` bit
  `0x80000000` and stores **both** its elements, element 0 at `+0x24` and element 1 at `+0x28`
  (`FUN_005ad630` at `0x005adfbb`), and a `FLYOUT_HEALTH` round carrying that bit gets its
  **intersect bit** only once it has travelled `+0x24`. The gate is a four-way conjunction at
  `0x005b01c4`: `(flags & 0x8) && (flags & 0x80000000) && +0x28 == 0 && travelled > +0x24`, calling
  `FUN_004cd210(node, 1)`. That routine (`Class.c:0x76f`) sets or clears node flag `0x10` at
  `node+0x24` and nothing else, and `0x10` is `INTERSECT_SURFACE`, the collision-participation bit
  every swept query tests ([`formats/gamez.md`](../formats/gamez.md), "flags.intersect_surface");
  visibility is bit `0x4`, written by `FUN_004cca30` (`gwNodeSetActive`) alone, which nothing on
  the projectile path calls. So `wep_14`'s `RANGE_MINIMUM [300, 0]` makes the torpedo unhittable
  for its first 300 m; it is drawn, with its trail, from the spawn frame. The spawn's tail
  (`FUN_005aef40`) clears the bit for a `RANGE_MINIMUM` carrier and leaves it set for any other
  `FLYOUT_HEALTH` weapon; a weapon with the health but no `RANGE_MINIMUM` therefore has no gate call
  at all. Element 1 is 0.0 in the sole authored entry, so the third clause holds throughout this
  install and what element 1 would otherwise mean is unknown.
  ⚠ [`formats/weapons.md`](../formats/weapons.md) once glossed the key as "minimum arming range",
  and this page once read `0x10` as a visibility flag; both are contradicted here. Nothing on this
  path gates arming and nothing on it hides the round; what the round shows over those 300 m is
  its `MODEL_ANIMATION` def's own timeline, below.
- **The ceiling.** `TETHER_GUIDED` clamps the vertical step so the round cannot climb past a global
  limit, confirming the reading in the guidance section.

**Three independent ways a round ends**, all resolved here by calling `FUN_005ac3a0`:

1. **Range.** Distance travelled accumulates in `+0x664`; once it reaches `RANGE` (weapon `+0x1c`)
   the round is done. `RANGE` **defaults to 500 m**, written into `+0x1c` by `FUN_005ad630`'s
   per-entry initialisation before the key is read, so the two entries authoring none (the smoke
   screen and the rear-arc flare) fly 500 m rather than forever.
2. **Timed fuse.** If the weapon authors `DETONATION_TIME` (weapon `+0x48`) and the round's age
   (`+0x668`) exceeds it, the round detonates. This is the rear-arc flare's 2.0 s. The field
   defaults to **−1.0**, and the test demands a positive value, so an unauthored fuse is off rather
   than instant.
3. **Target proximity.** A round with a target that comes within `DETONATION_DISTANCE` of it
   detonates. This is a **second, per-round fuse path** alongside the list sweep in `FUN_004b5fb0`:
   the sweep catches anything passing near any aircraft, this one catches the round's own target.

The three are not symmetric, and the branch structure is what says so. Range is tested first and
wins outright: neither fuse is consulted once the path is spent. Under range, the target fuse is
gated on `LOCK_ON` **and** a held target **and** a non-zero `+0x44`, and the timed fuse is what a
round failing any of those three falls through to (`LAB_005b029a`). All three positions are read
from the round's **un-advanced** position: the motion step computes the frame's delta and ends the
round before `FUN_005af720` applies it, so a round that ends this frame neither moves nor collides.

**Reaching `RANGE` does not always detonate.** The expiry branch takes `LAB_005b02ba`, which writes
the detonation position and calls `FUN_005ac3a0`, only when
`(LOCK_ON && !EXPIRES) || DETONATE_AT_RANGE`; otherwise it jumps to `LAB_005b0318` and the round is
simply removed. `EXPIRES` is `+0x74` bit `0x80` and `DETONATE_AT_RANGE` is bit `0x2000000`, both set
by `FUN_005ad630` and **neither authored by any entry in this install**, so the shipped rule is
exactly "carries `LOCK_ON`". The choker, the cannonball and the fake weapon therefore vanish at
their range where every other ordnance type goes off.

⚠ `MINE` does **not** accumulate half of each step. The halving is applied to the whole accumulator
every frame (`+0x664 *= 0.5` after the step is added), so the accumulator converges on one step's
length and never reaches `RANGE` at all. Nothing authors `MINE`, so the branch is unreachable; the
earlier "flies twice its nominal range" reading was wrong and is recorded here so it is not
re-derived.

## The proximity fuse

`FUN_004b5fb0` runs the fuse as part of the world tick. For each round in flight (`DAT_0064f78c`) it
walks **`VehicleList`** (the `std::list` at `0x0071dab8`, head node at `DAT_0071dabc`), skips anything
on the shooter's own side, and tests range against the round's weapon def `+0x44`. Nothing else is
a candidate: the loop never touches world geometry, which decodes what was previously a
recollection, that the original fuses on aircraft and never on terrain.

The side test compares the round wrapper's `+0x8` with the candidate's `+0x8`, the team. The
wrapper's copy is the shooter's own: `FUN_004b6820` hands `FUN_00441830` the address of the firing
vehicle's `+0x8` (`0x004b6e8c`, `0x004b7298`) and the wrapper's base constructor `FUN_00441b90`
stores it. The range test is point to point: `FUN_00538880` returns the squared distance between the
round's and the candidate's slot-0 positions, and the test passes below the squared distance at
`+0x44`. The first candidate in list order that passes both tests (and the dot test below) fires
the burst through `FUN_005ac3a0`, and the loop breaks.

CSVM keeps two departures. It detonates at the closest approach within the round's swept step
rather than at the first frame in range, since several rockets author a trigger distance equal to their
blast radius and a burst at first entry would deal nothing. An aircraft is measured to its airframe hulls and a surface hull to its
origin, the decoded point. Both branches skip the round's side as the original does, by plain id
equality with no neutral clause, and the aircraft branch also skips the shooter's own plane, which
only matters for a shooter whose team changed while its round was in flight.

The side is the one team space every combat object shares ([`targeting.md`](targeting.md) "The team
space"), so the skip follows each mode's team assignment. A campaign's humans and wingmen are team
1 and its AI take their roster or `SET_AI_TEAM` id; Instant Action's humans and wingmen are team 1
and its waves the enemy id; co-op splitscreen (`--coop`, and every co-op campaign) puts every human
on team 1. Dogfight (`--vs`) and plain splitscreen flight give each pilot its own default
(`AimAssist.TeamOfPilot`: pilot 0 is team 1, pilot N is `10 + N`), so two Dogfight pilots still
fuse on each other. The per-pilot ladder is a remake rule, since the original's network sides are
not decoded; the fuse reads whatever `+0x8` the mode wrote.

`VehicleList` holds aircraft and the AI ground/sea vehicles ([`aim-assist.md`](aim-assist.md) "The
four lists"), and **no zeppelin**. Its one inserter is `FUN_0047c210`, which allocates the 0xa20-byte
vehicle object and links it in through `FUN_005b3330` at `0x0047c543`; every other reference to
`0x0071dab8` is a name lookup (`FUN_004aff10`) or an iteration. A zeppelin is a different object:
`FUN_004bd110` reads `zeppelins.zrd`, allocates each one as a 0xfc-byte record (`FUN_004bd460`,
initialised by `FUN_004bd8d0`) and pushes it only onto the `ZeppelinList` vector at `0x0071df80`
(net-registered at `0x004bd223`). The mission script agrees: `SET_AI_TEAM` (`FUN_00469e20`) tries
`VehicleList` first and falls back to a separate `ZeppelinList` search (`FUN_004bd3e0`), naming the
two in one message, "can't find vehicle or zeppelin". The fuse's dot test also reads the candidate
at `+0x198`, past the end of a 0xfc-byte zeppelin record. **So the list sweep never fuses on a
zeppelin**, its hull, gasbags or cannons; a round passing close by flies on. The per-round
target-proximity path above is a separate question, since it reads the round's own held target
rather than a list.

When the weapon carries `DETONATION_DOT_PRODUCT` (`0x80000`), passing the range test is not enough.
The routine normalises the vector between the two and dots it against **the candidate's** orientation
axis (`+0x198`..`+0x1a0`), and requires that dot to reach the authored threshold at ext `+0x20`. Below
it, the candidate is skipped and the round flies on. The cone is measured against the target's
facing, not the round's. The vector is round minus candidate (the candidate's slot-0 position call
returns at `0x004b637c`, the round wrapper's at `0x004b6386`, and the round's is the minuend), and `+0x198` is the vehicle's
`m[2]`, the backward axis ([`flightModel.md`](flightModel.md)), so a positive threshold admits a
round in a cone behind the candidate. `FUN_004b9770`, the terminal-impact path, repeats the same test against the
victim's basis before it will resolve a hit.

## The beeper and the seeker, which are one weapon in two halves

`FUN_00441830` runs immediately after every spawn and is where a round acquires its per-type
behaviour. Three things happen there, each on its own flag:

- **`BEEPER_SEEKER`** (extension struct `0x8000`) installs `FUN_00441780` as the round's **per-frame
  retarget callback** at round `+0x688`.
- **`TARGETABLE`** (extension struct `0x20`) wraps the round in a 0x70-byte `TargetProjectile`
  (vtable `PTR_LAB_006063c4`), naming it from string id `0x2f6a`, and pushes it onto `DAT_0064f78c`.
- A weapon whose fuse distance (`+0x44`) exceeds 0.01 gets the same wrapper if it does not have one
  already, with the weapon stored at wrapper `+0x68`.

The wrapper's `+0x6c` byte is set to **1 only on the `TARGETABLE` path** and left 0 for a fuse-only
wrapper, which is exactly the admission byte [`aiPilot.md`](aiPilot.md) names for `TargetProjectile`.
So `TARGETABLE` is what makes a round appear in the target list, while `FLYOUT_HEALTH` is what lets
it absorb damage; the two flags do different halves of "shootable" and the torpedo carries both.

The name string id `0x2f6a` is a **literal**, not a read of the weapon's `DESC`: it is 12138,
`MSG_WEAP_AERIAL_TORPEDO`, "Aerial torpedo". Every wrapper the engine builds carries that one
string, which costs nothing here because the torpedo is the only `TARGETABLE` entry in the table.

⚠ This also corrects the fuse section above: `DAT_0064f78c` is **not** every round in flight. Only
rounds that are `TARGETABLE` or carry a fuse distance are ever wrapped into it, so the fuse loop
walks exactly the set that could fuse, and a plain round is invisible to it.

### The full beeper loop

1. A `BEEPER` round reaches an aircraft. `FUN_004b9bc0` zeroes its damage and, if the gate
   `FUN_004b8ce0` passes, calls `FUN_004b88a0`, which builds a tag object carrying the weapon's
   shared `TIME`, appends it to the tail of the tag list `DAT_0071dbac` (count `DAT_0071dbb0`) and
   stamps the original's clock `DAT_0071c470` into `DAT_0071dcc0+8` (`FUN_004b8d40`). The gate refuses
   three things, in this order: shooter and victim on the same team or either on team 0 (`+0x8`,
   the same test the aim assist runs); a victim that already carries a tag with a countdown above
   zero (`FUN_004b8ca0`), so a second beeper hit on a painted aircraft makes no tag and does **not**
   refresh the first, while an aircraft whose tag is in its tail takes a fresh one; and a clock
   equal to the stamp, so at most one tag is created per frame however many rounds of a
   `CLUSTER_SIZE [4]` salvo land in it. A hit on a dead victim never reaches the branch: the
   routine returns on `+0x91d` before the type dispatch.
2. `FUN_004b8ad0` counts every tag down by the frame delta through `FUN_004b89c0`. While the tag's
   effect object (`+0x4`) still exists, a tagged aircraft that dies or is removed
   (`+0x91d`/`+0x91f`) has the countdown slammed to **-1.0** before that frame's decrement. The frame
   that takes it to or below zero calls `FUN_004b8970`, which releases the effect and is where the
   tag stops being usable; the slam never fires again after that, so a death during the tail does
   not restart it. The countdown does not stop at zero: it keeps running, and the entry is deleted
   only once it sits at or below **-5.0**, a five-second tail after expiry (four after a slam).
3. A `BEEPER_SEEKER` round's callback `FUN_00441780` queries the tag list every frame through
   `FUN_004b8b50`, passing the round's **position** (`+0x48`) and **heading** (`+0x3c`). Tags with a
   countdown at or below zero are skipped.
4. The query keeps a running best and replaces it by the rule below.
5. The winner's position and velocity accessors (its vtable slots 0 and 1) are written into the
   round's target fields `+0x70` and `+0x74`, which are the same fields the guidance step reads,
   and **zeros are written when nothing is painted**. `FUN_005af720` calls the callback (`+0x688`)
   before it tests the steering gate on `+0x70`, so a seeker's target is the tag list's answer on
   every frame including its first: whatever target the shot routine handed it at spawn is
   overwritten before it is ever read, and a seeker with nothing painted holds nothing and is not
   steered. From there the round steers toward the pick at `TURN_RATE` like any other guided round.

### The query's selection rule

Read from the branch structure at `0x004b8c03`–`0x004b8c72`, with all four constants read out of the
image. **The dot's sign is inverted from the intuitive one**: the vector is taken from the tag toward
the round (`+0x48` minus the aircraft's position, normalised by `FUN_00422690`) and dotted with the
round's heading `+0x3c` (the unit direction the steering step multiplies by speed to make the
velocity, so it is the forward axis), so a tag **directly ahead scores near -1** and a lower dot is a
better-aligned candidate. **The distances are squared**: `FUN_00538880` returns a squared distance
and the routine keeps `1 / bestDistanceSq` (`FDIVR` at `0x004b8c67`) to form the ratio, so writing
`ratio` for `candidateDistanceSq / bestDistanceSq`:

- **No current best** → take the candidate.
- **Candidate better aligned** (`candDot < bestDot`) → take it if `ratio < 1.2`. A better-aligned tag
  is allowed to be up to 20% farther away in squared distance, which is about 9.5% as a length.
- **Candidate worse or equally aligned** (`candDot >= bestDot`) → take it only if it is strictly
  nearer (`ratio < 1.0`) **and** either `candDot <= 0.7` or `candDot < bestDot + 0.1`.

The four constants are `1.2` (`0x006040ac`), `1.0` (`0x006032dc`), `0.7` (`0x006035b0`) and `0.1`
(`0x006034a8`). Net effect: **range leads**. With the inverted dot, `candDot <= 0.7` is any tag less
than about 134° off the round's nose, so a nearer tag steals the pick outright unless it sits well
behind the round, and only then must it give up less than 0.1 of alignment. A better-aligned tag
that is farther steals only inside the 20% squared-distance window. The pick is a running best in
list order (creation order), not a total order: a near-worse tag and a far-better one inside that
window each replace the other, so that pair resolves to whichever was tagged later.

So the two weapons are one system: `wep_10` paints a target for its `TIME`, and `wep_11`, the sole
`BEEPER_SEEKER` and the sole weapon with a real `TURN_RATE` (1.25), homes on whatever is painted.
Neither half is useful alone, which is also why the Seeker is the only entry that needs guidance at
all.

## The shootable flyout

`FLYOUT_HEALTH`, not `TARGETABLE`, is what makes a round shootable. At the end of the spawn
`FUN_005aef40`:

- **Without** `FLYOUT_HEALTH` (`+0x74` bit `0x08` clear), the round's `+0x19c` and `+0x19d` are both
  set to **-1.0**, the not-shootable sentinel, and its scene node's `0x8000000` flag is cleared.
- **With** it, `+0x19c` takes the weapon's `+0x8c` (the zero the parser wrote, so a damage
  accumulator starting empty) and `+0x19d` takes `FLYOUT_HEALTH` itself. The node gets the
  `0x8000000` flag set, a `1` at node `+0xbc`, and a **back-pointer to the round at node `+0x40`**,
  which is what lets a collision against the node find the round it belongs to.

`TARGETABLE`'s half of the job, admitting the round to the target list, is `FUN_00441830` above.

The `0x8000000` node flag is not the whole story: the same tail also calls `FUN_004cd210` on the
round's node, which sets or clears node flag `0x10`, **the intersect bit** every swept query and
every cover ray tests. A round without `FLYOUT_HEALTH` has it cleared and is not in the collision
database at all; a round with it has it set, **unless** the weapon also carries `RANGE_MINIMUM` with
a zero second element (`+0x74` bit `0x80000000` and `+0x28`), in which case it is cleared at launch
and set later by the gate in `FUN_005afd50` (`0x005b01c4`). So the torpedo's 300 m gate is a
shootability gate and only that: the round is drawn throughout, and the steering step's own
terrain probe treats the bit the same way, saving it (`FUN_004cd260`), clearing it around the
probe and restoring it (`FUN_005af960`, the `flags & 8` arms).

### The launch look is the def's timeline

`FUN_005aef40` hides nothing. What it does with the visuals, in order: it aims and places the node
(`FUN_004d1a30`/`FUN_004d1d50`), then, when the per-shot slot at weapon `+0x150` is not −1, it
starts the `FLYOUT` row's `ANIMATION` (`+0xfc`, `FUN_004ee0b0`), `ANIMATION_ATTACHED` (`+0x10c`,
`FUN_004edf50`) and `MODEL_ANIMATION` (`+0x110`, `FUN_004edda0` on the round's own model clone at
round `+0x1c`, with `LAB_005ad320` as its callback), each stored on the round. The
`MODEL_ANIMATION` runs from that call on its own anim clock; nothing on the projectile path
switches its sequences, and the `RANGE_MINIMUM` gate above touches the intersect bit alone. So the
torpedo's launch look, its switch and its cruise look are all `torpedo_trail`
(`torpedo_effects.zrd.json`, compiled per chapter as `a_torpedo-torpedo_trail`), read off the
data:

| Anim time | Event | What is seen or heard |
|---|---|---|
| reset | `RESET_STATE`: `rightwing`, `leftwing`, `atprop` INACTIVE, `atpayload` scale 1 | the wings folded and the prop absent, so the round that leaves the rail is the same `a_torpedo` model as the pylon-mounted one with three nodes off, which reads as a different, wingless body |
| 0 s | `CALL_SEQUENCE torpuffer_trail1` | `torpuffertrail1`, a `DISTANCE_INTERVAL 0.2` puffer AT_NODE `a_torpedo (0, −0.2, 1.5)` cycling `fire_f01`…`fire_f06`: the orange rocket-flame ribbon |
| 3.5 s | `torpuffertrail1` INACTIVE at `ANIMATION_OFFSET 3.5` | the flame stops; its last puffs live up to 1 s more |
| 3.5 s | `CALL_SEQUENCE rightwing`, `leftwing` (`EVENT_OFFSET 3.5`) | both wings ACTIVE and swung from ±90° yaw to 0 over 5 s (`OBJECT_MOTION_FROM_TO`) |
| 3.5 s | `CALL_SEQUENCE torpuffer_blast` | `torpufferblast`, a `TIME_INTERVAL 0.001` puffer AT_NODE `a_torpedo (0, −0.2, 1.7)` on `smoke101`…`smoke103`, off 30 s later: the white puffs |
| 3.5 s | `CALL_SEQUENCE propstart` | `snd_propstart` at `atprop`; the prop ACTIVE 0.5 s later, spinning at 45°/s for 30 s |
| 3.5 s | `CALL_SEQUENCE growpayload` | `atpayload` scaled to (1.8, 1.5, 1.0) over 3 s |
| 3.75, 4.5, 5.25, 6.0 s | `SOUND snd_Atorp_armed` ×4 | the arming beeps |

The switch is therefore at **3.5 s on the anim clock**, not at 300 m; the two coincide only because
a torpedo leaving a launcher at cruise speed covers about 300 m in those seconds. The motor-start
sound and the beeps heard at the switch are the def's own `snd_propstart` and `snd_Atorp_armed`,
which is also why the `torpedo_trail` def carries them in its `static_sounds` table. The
`FLYOUT SOUND` slot (`snd_torpedo_loop`, parsed by `FUN_005ae990` into weapon `+0x124`/`+0x128`)
has **no reader**: every instruction naming either offset was enumerated and none is in the
projectile system, and the block's base `+0xf8` is formed only by the parser, so the loop is dead
in this build. `torpuffer_trail2` (a second fire trail off at 29 s) is defined and never called.

**How long the flight is, and how fast.** The spawn's `+0x38 == 0.0 && !(flags & 0x800)` test picks
the branch for a weapon without `ACCELERATION` and without `INSTANT` (bit `0x800` is `INSTANT`,
`FUN_005ad630` at its `s_INSTANT` read), which `wep_14` is: speed `+0x640` and cap `+0x644` are both
`VELOCITY` 60 (`0x005af147`), and the velocity vector at `+0x60` is `launcherVel + dir × 60`
(`FUN_005389a0(launcherVel, dir, 60, +0x60)`). With `LOCK_ON` the launcher vector is also copied to
`+0x30` and the steering step, which the shot routine's synthetic target guarantees runs, rebuilds
`+0x60` as `heading × 60 + inherited × (2.5 − age)/2.5`; the motion step's accelerate branch is
skipped (`speed < cap` is false), so nothing else touches the speed and the `TURN_RATE 0.001` turn
penalty is `1 − 5·10⁻¹²` per frame. The round ends at `RANGE` 1200 m of path (`+0x664`) by
detonating, since it carries `LOCK_ON` and no `EXPIRES`. Off a 50 m/s launcher that is 1200 m in
about 21 s (60 m/s plus the 62 m the decay adds), with the flame off at 3.5 s and 300 m passed at
about 4.5 s. ⚠ The at-the-controls reading of `CAP-23` (a 6 s launch phase and about 33 s of
flight) is not what this rule gives: the 3.5 s flame plus its 1 s of surviving puffs, the 5 s wing
swing and the beeps ending at 6.0 s make the launch phase read as roughly 6 s, but 33 s at 60 m/s is
about 2000 m, and no reader of `RANGE`, `+0x664` or the speed pair changes that. Whether the clip's
timing, its launcher's speed or an unread path (the tick's `+0x1a2` callback, the `DAT_009c6c44`
pre-hook) accounts for it is open, and this page does not tune to it.

### `PROJECTILE_BBOX` is a node flag, and its default is ON

`FUN_005ad630` at `0x005ae132`–`0x005ae155` reads the key into bit 0 of def `+0x78`: **absent sets
the bit**, present sets it to `value != 0`. `FUN_005aeca0` then passes that bit to `FUN_004cd2a0`,
which sets node flag `0x20` on the pooled round node. `wep_14` authors `PROJECTILE_BBOX [0]` and is
therefore the one entry in the table that turns the flag **off**, every other round carrying it by
default. The flag's consumer inside the collision database was not read, but the shape of the data
says what it is for: the one round anything can shoot at is the one round taken off the cheap path.

### How the flyout's health is spent, and what happens at zero

`FUN_005abcf0`, the projectile-hit resolver, tests the struck node's `+0xbc` for the `1` the spawn
wrote and follows the node's `+0x40` back-pointer to the round. It then spends the pair **armour
first, then health**. ⚠ That is the same ORDER an aircraft damage zone is spent in and not the same
arithmetic: `FUN_004b7f80`, the aircraft take-hit, carries an armour-shielded share that reduces the
health damage; the flyout branch is a plain clamp-and-subtract on each pool.

    flyout[+0x670] = max(0, flyout[+0x670] - incomingArmourDamage)
    if flyout[+0x670] == 0:
        flyout[+0x674] = max(0, flyout[+0x674] - incomingHealthDamage)

⚠ The armour pool starts at **zero**. The spawn seeds `+0x670` from weapon `+0x8c`, and `+0x8c` is
only ever written as the literal 0 by the parser, with no key feeding it. So a flyout's armour pool
is always empty and the first hit spends health directly. The two-pool structure is real but inert as
shipped.

The projectile tick `FUN_005af720` then checks `+0x674 == 0.0` **every frame, before anything else**,
and on zero destroys the round: it plays the weapon's `DESTROY_ANIMATION` (weapon `+0x188`) if one is
authored, or calls `FUN_005ac3a0` if not, and clears the alive flag. This is why the not-shootable
sentinel is **-1.0** rather than 0: a round without `FLYOUT_HEALTH` must never satisfy that equality.

Two consequences worth stating separately. **A shot-down round does not detonate**: `FUN_005ac3a0`
is the detonation, and it is the branch taken only when no `DESTROY_ANIMATION` is authored, which
`wep_14` is not. So the torpedo dies playing `torpedo_destroy_effect` and its 200/200 warhead is
never spent. And the test runs **after** the per-frame retarget callback at `+0x688` and **before**
the guidance gate and the motion step, so a round whose health reached zero neither steers nor moves
on the frame it dies.

## Detonation: the impact, then the splash

[`weaponImpact.md`](weaponImpact.md) covers the per-surface `IMPACT` **table**, meaning which effect
and sound a row binds and how rows are filled. It does not cover what a detonation *does*. That is
`FUN_005ac3a0`, and it has two halves.

### Half one, the direct impact (`FUN_005ac7a0`)

- **A per-weapon impact hook.** If the weapon has a callback at `+0x20c`, it runs first, with the
  weapon, the hit record and the round, and returns a suppression mask read three times further down:
  bit 1 silences the row's sound (`FUN_005ad100` is skipped), bit 2 suppresses the crater and
  quicksand carves and the row's `EFFECT` (row `+0x24`, spawned through `FUN_00525d90`), bit 4
  suppresses the row's `ANIMATION` slot alone. **Neither the direct damage nor the splash is under
  any bit**: `FUN_005abcf0` runs before the mask is first read, and the splash is the caller's
  (`FUN_005ac3a0`) second half. This is the slot `FUN_005aec90` writes, and the `TANGLER` parse is the
  one caller that installs one, `LAB_004ba660`, which closes the loose end noted at the top of this
  page. That hook builds the choker's **cloud** (below, "The choker, settled") and returns **1**, so
  a choker's `IMPACT` row plays no sound in the original; the mask's other two bits have no
  installer in the binary. CSVM: `WeaponDef.ImpactHook` (an enum, one installer) dispatched by
  `ProjectilePool.RunImpactHook`, the mask as `ImpactSuppression` fed to `ImpactOutcome.Resolve`.
- **Direct damage** is the authored pair scaled by a per-round factor at round `+0x678`, applied
  through `FUN_005abcf0` to the struck object alone.
- **The terrain carves** come next, and they are keyed on the `.zrd` dispatcher's word at weapon
  `+0x74`, not on the extension flags: `FUN_005ac690` under bit `0x10` (**`CRATER` present**, set by
  `FUN_005ad630` where it reads the key) and `FUN_005ac580` under bit `0x40000` (**`QUICKSAND`**,
  unauthored). Both gate on the struck node carrying `0x10000` and randomise a count and two scales
  from weapon fields `+0x198`/`+0x1c0`, `+0x1a4`/`+0x1cc` and `+0x1a8`/`+0x1d0`. ⚠ **That `0x10000`
  is `CAN_MODIFY`, and no node in the install carries it**, so neither carve ever runs in play and
  `FUN_005ac690` always returns false ([`craters.md`](craters.md), "No shipped detonation reaches
  the carve"). A crater that was actually carved would suppress **both** animation slots below
  unless the weapon authors `ANIMATION_ALWAYS` (`+0x74` bit `0x800000`; nothing in this install
  does), so that suppression is code with no reach and the six `CRATER` weapons always play their
  rows. The randomisation never fires either, because all six carriers author the block bare and
  every span is zero; what the carve would build, and what CSVM builds instead, is
  [`craters.md`](craters.md).
- **The row's own bindings** follow: the sound through `FUN_005ad100`; the `ANIMATION` (row `+0x4`)
  spawned through `FUN_004edc10` with a zero rotation, and only when `FUN_005abcf0` returned 0; and
  the `SURFACE_ANIMATION` (row `+0x1c`) spawned with an orientation built from the hit record:
  `FUN_00422690` normalises the record's normal (its first three floats), `FUN_0053fd40` builds the
  shortest-arc rotation from `DAT_006379c0`, which is `(0, 1, 0)`, onto it, and `FUN_00540260` turns
  that into the Euler triple the spawn takes. So a `SURFACE_ANIMATION` is world up rotated onto the
  struck normal, identity on flat ground, and a plain `ANIMATION` keeps its fixed axis whatever the
  surface. CSVM: `ProjectilePool.SurfaceUpBasis` on the slot `ImpactOutcome.SurfaceOriented` names,
  handed to `AnimRuntime.PlayEffectAt` and the gamez-model spawn as the template's basis.

### The upper ring in Enhanced Graphics

⚠ **Remake-only, and Enhanced Graphics only. The faithful presentation is the decoded rule above and
nothing here touches it.** The rocket burst's second ring is not a slot the `IMPACT` row names at
all: `he_ground_effect` reaches it through `CALL_ANIMATION call_he_ring1 AT_NODE he_ring, 0, 12, 0`,
twelve metres over the hit, while its ground ring (`call_he_ring`) is called at the hit itself. The
ring definitions carry scale and opacity and no rotation of any kind
([`../formats/weapon-effects.md`](../formats/weapon-effects.md)), so the original draws the upper
ring on the fixed world axis whatever the round's flight path, and `Crimson Skies 1.02 2026-07-31
23-27-53.mp4` shows exactly that. The two rings are not authored alike: the ground ring's model
(`he_ringer`) is flat in its node's XZ plane, while the upper ring's (`he_ringer1`, model box
±4.24 m in X and Y, ±0.5 m in Z) stands in its XY plane with its disc facing along Z. So the
original's upper ring is a standing disc facing world Z, and a ring seen edge-on reads as a bright
line rather than a ring, which is what the enhanced presentation changes and the faithful one keeps.

Under Enhanced Graphics the remake places that one callee with the ring's own disc normal (node Z,
`ProjectilePool.UpperRingDiscNormal`) rotated onto the reverse of the round's own flight direction,
so the ring faces back up the path the rocket came down. Rotating world up instead turns the disc a
quarter turn off the trail, since up lies in the disc's plane. Nothing
else moves: the ground ring, the fireball, the trail columns and the `SURFACE_ANIMATION` rule above
all keep their decoded placement, and the switch is the ordinary `graphics.mode` setting
(`GraphicsMode.Enhanced`), so the faithful path is bit-identical with or without this rule. CSVM:
`ProjectilePool.UpperRingOrient` decides and hands the basis through `EffectSink`;
`AnimRuntime.OrientedCallAnimNames` (from `EffectCatalogue.ImpactUpperRingAnimNames`) is the one
callee name it may re-base; the `impact-orientation` suite pins both presentations, measuring the
drawn mesh's disc normal against the reversed flight vector.

### Which row a burst reads

The surface id `FUN_005ac7a0` indexes the table with comes off the hit record it is handed
(`hit+0x20` is the struck material, `material+0x20` its id), and the two ways a round ends build
that record differently:

- **A direct strike reads the struck material.** `FUN_005b05c0`, reached from the swept ray in
  `FUN_005b03f0`, hands the ray's own hit record through, so an aircraft reads `player`(6) and a
  chapter mesh its `soil` id.
- **Every fused burst reads `default`(0).** Both fuse paths end in `FUN_005ac3a0`: the round's own
  fuse against its held target in `FUN_005afd50` (squared distance to the target at or under
  `+0x44`) and the vehicle-side sweep in `FUN_004b5fb0` (every live round whose `+0x44` exceeds
  0.01, against every VehicleList entry off the shooter's side, under the optional dot gate of flag
  `0x80000`). `FUN_005ac3a0` builds a synthetic hit record on its stack from the round's position and
  a material stub whose id field is zero, and the aircraft the round fused on is not on it; that
  aircraft takes its share through the splash gather like any other candidate. So a fuse burst
  draws at the round, never on the plane, and out of the weapon's `default` row whatever its
  `player` row authors. The range expiry and `DETONATION_TIME` end in the same call. The gate
  `DAT_00a1d7d4` on that path's splash is set to 1 at init (`FUN_005ad4e0`) and only the `MINE`
  walker `FUN_005b0970` toggles it around its own calls, so a fused burst splashes like a struck one.
- **An `ANIMATION` name with no definition draws nothing.** `FUN_005ae990` resolves each name at
  load through `FUN_00523820`, an exact-string scan of the anim-definition table, and stores the
  pointer in the row (`+0x4`; `+0x1c` for `SURFACE_ANIMATION`). An unknown name stores 0 and
  `FUN_005ac7a0` skips the spawn on a zero slot. Nothing falls back to `default` for a row the
  weapon named: the copy in `FUN_005ad630` fills only ids it names no block for. `wep_07`'s `player`
  row names `flak_effectplayer`, which no chapter defines, so a flak that physically strikes an
  aircraft plays the row's sound and no animation, while its fused bursts, the common case at a
  50 m fuse, draw `flak_effect` from `default`.

CSVM: `ProjectilePool.Impact` reads the struck id for a ray hit only and `default` for every
self-ended round; the fused aircraft rides along as the beeper's tag target and the blast's damage
anchor, never as the row.

### Half two, the splash (`FUN_005aca30` then `FUN_005acac0`)

Gated on `weapon +0x3c > 0`. `FUN_005aca30` runs a sphere query (`FUN_004cb420`) of radius
`round[+0x678] * IMPACT_PROXIMITY` and fills a hit buffer. `FUN_005acac0` then walks it and, per
entry:

    t      = 1 - dSurface² / IMPACT_PROXIMITY²
    armour = t * ARMOR_DAMAGE  * round[+0x678]
    health = t * HEALTH_DAMAGE * round[+0x678]

**The falloff is quadratic in distance, not linear**, because both terms of that ratio are squared
quantities (see [the squared-radius convention](#the-engine-stores-radii-squared)). It reaches zero
at the radius and scales both pools by the same factor. At half the radius a linear curve would give
0.5 and this gives **0.75**, so the original is markedly more generous close in and falls away faster
near the edge.

The per-entry `dSurface²` is computed inside the gather, `FUN_004cb420`, and it is the one place on
this path that takes a square root:

- **The distance is to the object's bounding-sphere surface, not its centre.** For each candidate
  node the gather builds the world-space corners of the node's box (`FUN_004cd960` then
  `FUN_004cb8b0`), and `FUN_004d8bd0` reduces those eight corners to their axis-aligned centre and a
  radius equal to half the box diagonal. `FUN_005388d0` (unlike `FUN_00538880`, this one does take
  the root) gives the plain centre-to-centre distance; the radius is subtracted, the result is
  clamped to zero, and the clamped value is squared into the hit entry at `+0x20`. So a target the
  burst engulfs, meaning the burst centre lies inside that sphere, records `0` and takes full
  damage, and everything else records the square of its surface distance.
- **The gate and the falloff use different radii.** The gather's sphere is
  `round[+0x678] × IMPACT_PROXIMITY` (the raw `+0x3c`), and a candidate is dropped when its
  `dSurface²` exceeds that radius squared. `FUN_005acac0` then divides by the unscaled `+0x40`.
  Since nothing observed writes a yield other than 1, the two radii coincide in shipped play.
- **The blast is occlusion-tested, for the nodes that opt in.** `FUN_005aca30` passes both the
  distance flag and the occlusion flag. Under the occlusion flag `FUN_004cb420` casts
  `FUN_004c8f70` from the burst centre to the candidate's sphere centre through the whole intersect
  database, with the candidate's own intersect bit (`node+0x24` bit `0x10`) cleared for the
  duration of the cast so it cannot shadow itself, and drops the candidate on any hit. The ROUND's
  own node (`round+0xc`) has that bit cleared for the whole gather (`FUN_005aca30`), so the round
  never shadows what it just burst against. ⚠ That node is the round, not its shooter: the shooter's
  aircraft is an ordinary candidate here, gathered like any other and able to shield another one.
  What keeps it from taking the damage is the guard on the hit side, below. ⚠ The cast runs only for a candidate carrying
  node flag `0x400000`; that bit is not in the GameZ node flags and no instruction in the binary sets
  it by an immediate, so which objects opt in is not decoded. CSVM tests every candidate.
- **One entry per collidable NODE, and the node is a leaf.** `FUN_004cb420` walks the spatial grid's
  cells and hands each object it finds to `FUN_004cb950`, which recurses: a node with `node+0x24` bit
  `0x40` clear is a group and contributes nothing itself, passing the walk to its children, while a
  node carrying both `0x40` and `0x100` is a leaf and writes exactly one entry, keyed on its own
  pointer at entry `+0x28`. Neither the gather nor `FUN_005acac0` dedupes further, so a model built
  from several collidable leaves takes several shares, one per leaf, all against the same HP pool.
  ⚠ This is NOT one entry per top-level object, and a reading that collapses a multi-part model to a
  single share contradicts the recursion. What the original has no counterpart for is CSVM's split of
  one leaf into a body per surface class and soil (`SceneBuilder.AttachCollision`), which is why the pool
  collapses those siblings (`WorldCollision.OwnerOf`) and nothing coarser.
- **At most 32 objects** are collected. The gather checks `count < 0x20` before testing each
  candidate and, once the buffer is full, logs "Database intersections array is full" for every
  further candidate in the walk and adds nothing. The buffer is reset per query, so the cap is per
  burst, and which 32 win is the order the spatial grid walk finds them in.

A weapon carrying `MINE` (`+0x74` bit `0x4000`) skips the falloff entirely and applies full damage to
everything inside the radius. `round[+0x678]` is a per-round yield multiplier scaling the radius and
both damage figures together, so it is one knob over the whole burst.

CSVM keeps the falloff, the engulf clamp, the occlusion cast and the 32 cap, and departs on three
points by choice (`Projectile.ApplyDamage`): the shooter's own aircraft is dropped at the gather
(`GatherAircraftCandidates`) rather than at the hit-side guard below, which reaches the same
damage figure of zero but also keeps it from occupying one of the 32 slots, from shielding another
candidate, and from taking the no-damage types' own effects; the surface distance is to the nearest point of the
target's own collision shape rather than to a bounding sphere, because a Godot collision body has no
per-node box and a chapter mesh's enclosing sphere would hand full damage to everything inside it;
and the 32 winners are the nearest 32, since the original's grid order is placement luck. The
occlusion ray does aim where the original's does, at the box centre: the bounds centre of the
candidate's struck collision shape (`ProjectilePool.BlastCentre`), never its node origin, which a
chapter mesh keeps at ground level.

### The engine stores radii squared

`FUN_005ad630` keeps two forms of `IMPACT_PROXIMITY` and a squared `DETONATION_DISTANCE`, and every
distance it compares them against comes from `FUN_00538880`, which returns a **squared** distance
with no square root:

| Offset | Holds |
|---|---|
| `+0x3c` | `IMPACT_PROXIMITY`, raw, used as the sphere-query radius |
| `+0x40` | `IMPACT_PROXIMITY²`, the falloff denominator |
| `+0x44` | `DETONATION_DISTANCE²`, the fuse trigger |
| `+0x48` | `DETONATION_TIME`, raw seconds |

So any reading of this engine that treats a `FUN_00538880` result as a plain distance, or `+0x40` and
`+0x44` as plain radii, gets the curve shape and the trigger range wrong. Both squarings are the same
`FLD v; FLD ST0; FMUL ST1; FSTP` idiom at `0x005adbb5` and `0x005add73`.

## The hit-side dispatch

`FUN_004b9bc0` is the routine that applies one weapon hit to one aircraft. It receives the victim,
the weapon record, the **squared distance** from the detonation, the struck zone id, the shooter, and
a two-float damage pair (armour, health). It has three callers, and which aircraft they hand it is
what decides who a burst affects: the vehicle's own hit callback `FUN_004b9770` for the struck
aircraft (its distance is `FUN_00538880` between the vehicle position and the detonation record's
burst point, so the aircraft's origin to the burst, squared), the splash walker `FUN_005acac0` once
per hit-buffer entry (its distance is the entry's `dSurface²`, so **every aircraft the sphere query
finds inside `IMPACT_PROXIMITY` is a victim**, whether the round struck one, fused on one, or burst on
the ground beside it, cover-tested and capped at 32 like the damage), and the choker cloud's walk
`FUN_004b9590` (below). It reads the flags dword from `weapon+0x210` and branches, and the branch
order is what decides which types can ever deal damage:

1. **Feedback magnitude.** A kind and a magnitude are computed for the player's per-hit feedback call
   `FUN_0042c070`, which runs only when the victim is the player. Kind 1 is a `CANNON` hit, sized by
   `CALIBER` times a global; kind 2 is anything else, sized by the larger of the two damage figures
   times a different global. `HIGH_EXPLOSIVE` applies a third global on top. Kind 3 is
   `SHAKES_CAMERA`, and it is the only one of the three that attenuates with distance, falling
   linearly to zero at the weapon record's `+0x40` (an effect-radius field; which authored key writes
   it is unread here).
   ⚠ The `HIGH_EXPLOSIVE` branch tests `distance <= 400.0` and then applies the **same** multiplier in
   both arms. The comparison is dead as shipped.
2. **`SONIC` or `FLASH`** (`0x200 | 0x800` tested together). `FUN_0042e840` computes an intensity
   from the squared distance, `IMPACT_PROXIMITY` squared on the spot (`+0x3c × +0x3c`), and the burst
   point read off the current detonation record (`FUN_005ad430() + 0x18`); if it is non-zero the
   victim is affected, and **the victim's kind decides how**. The player gets a screen flash through
   `FUN_0042e9d0` with a literal `1.0` first argument (the start delay), the intensity, the five-times
   figure as the duration, and the colour: white for `FLASH` and **red for `SONIC`**. An AI gets
   `FUN_004200d0`, which is a **stun**. Either way both damage figures are then zeroed. CSVM:
   `ProjectilePool.ApplyDisabling`, run from `Apply` on every burst of a `SONIC`/`FLASH` weapon over
   the same aircraft gather the splash uses, with the shooter kept as a candidate where the damage
   pass drops them; a human's pane through `ProjectilePool.WashSink` (`ScreenFlash.PlayBlend`), an
   AI through `FlightController.TryStunPilot`.
3. **`BEEPER`** (`0x4000`). Tests the shooter against the victim (`FUN_004b8ce0`), then tags the
   victim by handing `FUN_004b88a0` the shared `TIME` at `+0x18`. Zeroes both damage figures and
   returns.
4. **`TANGLER`** (`0x40000`). Zeroes both damage figures, then computes an engine-dead duration and
   calls `FUN_004b1690`. See below. There is no shooter guard ahead of it: the self-hit test comes
   after, on the ordinary path only.
5. **Everything else** is the ordinary damage path: a self-hit guard (a round whose shooter is its
   victim deals nothing), then the struck zone is found by walking the victim's zone list at stride
   0x58 and matching the zone id, and the pair is spent against that zone (`FUN_004b3bf0`) or against
   the whole vehicle when no zone matches (`FUN_004b8070`).

**The self-hit guard is the whole of the self-blast exemption, and it sits here rather than in the
gather.** It is `CMP EBX,ESI` at `0x004b9e3e`, shooter against victim, and on a match it writes 0
into both halves of the damage pair (`0x004b9e42`, `0x004b9e45`) and returns. The shooter reaches
it by value, not by geometry: `FUN_005acac0` hands each splash entry the round's own shooter at
`round+0x4`, `FUN_005abcf0` parks it in `DAT_00a1d7c8`, and the victim's hit callback reads it back
through `FUN_005ad440` (which is nothing but `return DAT_00a1d7c8`) and passes it here. So one guard
covers the direct hit, the fuse burst and the splash alike, for a gun and for a rocket, which is why
a pilot can fire a rocket out of a hardpoint sitting inside their own collision hull. ⚠ It stands
BELOW the four no-damage arms, so a shooter IS flashed, deafened, tagged and choked by their own
burst; only the damage pair is exempt. The one place the shooter is tested earlier is the mine's
detonation check (`FUN_005b0970` calls `FUN_005aca30` with the flag the other three callers pass as
1): a mine whose radius holds its own layer and nothing else does not go off at all.

**Four of the twelve types cannot damage anything.** `SONIC`, `FLASH`, `BEEPER` and `TANGLER` each
zero the damage pair before returning, so their authored `ARMOR_DAMAGE`/`HEALTH_DAMAGE` figures are
never spent. For `FLASH` (`wep_09`) and the flare (`wep_15`) that matches the authored `DAMAGE 0`;
`SONIC` (`wep_08`), the `BEEPER` (`wep_10`) and the choker (`wep_12`) author the pair itself, as
`ARMOR_DAMAGE 0.0` / `HEALTH_DAMAGE 0.0` in this install's `weapons.zrd.json`, so the zeroing changes
nothing here and would only bite an entry authoring a non-zero pair on one of these types. Dealing
no damage is not the same as doing nothing, though: three of the four disable the victim instead,
which is the section below.

## What the no-damage types do, to the player and to an AI

The player and an AI take **different** effects from the same weapon, decided at the point of impact.
The short version: an AI loses its controls, the player loses their view.

### `SONIC` and `FLASH`: an intensity, then a stun

`FUN_0042e840` produces one intensity in `[0, 1]` from the squared distance ratio
`d² / IMPACT_PROXIMITY²`:

    ratio     = min(1, d² / IMPACT_PROXIMITY²)
    intensity = 1                          while ratio < 0.6
              = 1 - (ratio - 0.6) * 2.5    from there to the radius

So the effect is at **full strength out to √0.6 ≈ 77% of the radius** and only fades over the last
quarter. It is a plateau, not a falloff.

**`FLASH` additionally requires the victim to be facing it**, and `SONIC` does not. For a `FLASH`,
the routine dots the unit vector toward the burst against the victim's forward axis and returns zero
if that dot is negative. Below 0.5 it scales the intensity by **twice the dot**, which meets the
unscaled value exactly at 0.5 and ramps to nothing at 0, so a burst 60° off the nose is untouched and
one at 66° keeps 80% of its strength. A flash going off behind you does nothing; a sonic
burst behind you works at full strength. That is the one place the two flags differ beyond the
screen colour.

The routine returns the intensity and **five times** the intensity. The player's screen flash takes
the first; `FUN_004200d0` takes the second as a **stun duration in seconds**, so a dead-centre hit
stuns for the full 5 s.

`FUN_004200d0` refuses a dead victim (`+0x91d`), the player, a victim with `+0xf8` set, and any
vehicle whose **dispatch class** `+0x67c` is not 0 or 4, that is, anything but a `jet` or a
`wingman` ([`aiPilot.md`](aiPilot.md); the AI *mode* at `+0x358` is not consulted, so a stunned
pilot is accepted). Otherwise it sets AI mode **4**, **zeroes the three stick channels raw and
copied** (`+0x100`/`+0x108`/`+0x10c` and `+0x114`/`+0x11c`/`+0x120`,
[`aiControlLaw.md`](aiControlLaw.md) "The channels"; the throttle lever at `+0x124` is left alone),
and writes the expiry to `+0xc0` as **clock + seconds, overwriting** whatever was there. There is
no max against the running expiry, so a stun landing on a stunned pilot replaces the clock in both
directions.

**`+0x978` is `stun_recovery_interval` and it is display-only here.** The field is the per-pilot
interpolation of the `stun_recovery_interval` pair (`DAT_0071c4c8`/`DAT_0071c4cc`, written by the
`ai_skill_parameters` loader `FUN_004735b0`, defaults 6.0/0.6, authored 4.8/0.6), stored at spawn by
`FUN_0047c210` (`0x0047d16a`) and defaulted to a flat 3.0 in `FUN_004aff80` (`0x004b0404`). Its
only other reader is `FUN_0041d9f0`'s failed sixth-sense test, which is `FUN_004200d0`'s third
caller and passes `+0x978` **as the seconds argument** (`0x0041df89`, "AI has been evaded. Sixth
sense test failed"). The debug `sprintf` inside `FUN_004200d0` prints `+0x978` regardless of what
was passed, so it is truthful for that caller and misleading for the other two: a sonic or flash
hit is stunned for five times the intensity and the smoke screen for `smokescreen_stun_interval`,
and the pilot's recovery interval multiplies neither.

So a sonic or flash round against an AI takes its hands off the controls for up to five seconds. It
is the strongest non-damaging effect in the weapon table. Our side: `AiPilot.Stun` through
`FlightController.TryStunPilot`, which holds the victim guards.

**The player's half is `FUN_0042e9d0`, and it is purely visual.** It is a full-screen colour wash:
`SONIC` red `(1, 0, 0)`, `FLASH` white `(1, 1, 1)`, at a **weight** equal to the intensity and for a
**duration equal to five times the intensity**, the same number the AI stun uses. Two derived timings
at 0.35 and 0.15 of the duration split it into phases; the tick (`FUN_0042eb80`) reads them as an
**attack** over the first 0.15 of the duration (the displayed weight climbs `dt / attack` of the peak
per frame, capped at the peak), a sustain at the peak, and a **release** over the last 0.35 (the
peak sheds `dt / release` of itself per frame, decaying toward 1/e of the sustain), then a hard cut
at the duration. Overlapping washes **blend** rather than replace: the running colour is mixed toward
the new one by the incoming weight (`(old·p + new·w) / (p' + w)`, normalised by the NEW weight) and
the weights combine as `p' = w + p − w·p`, so two flashes are worse than one but never saturate; a
re-hit resets the elapsed clock but not the displayed weight. The first argument is a **start
delay** during which nothing paints, read on the first hit only: the sonic/flash caller passes 1.0 s,
the smoke caller 0. An optional sound handle starts with it.

⚠ **Nothing on the player's path touches the controls.** The player branch calls only the screen
wash and returns; there is no input lockout, no state change, no stun. That asymmetry is the design:
the same round blinds a human and disables an AI, because blinding an AI would mean nothing and
locking a human's controls for five seconds would be intolerable.

### `TANGLER`: mechanical only

The choker sets the engine-dead bit and its timer, and nothing in the AI decision layer reads that
mask. Its readers are in the flight model (`FUN_0048fc40` and the `FUN_004b18a0` group), so a choked
AI is not told it has been choked; it simply flies an aircraft with no thrust. There is no stun, no
state change and no evasive reaction. The catch is a **cloud** the impact hook leaves at the burst,
not the round alone; see "The choker, settled".

### `BEEPER`: nothing at all to the victim

The tag is a world-list entry that seeker rounds query. The victim gets no effect, no state change
and no notification.

### `SMOKE_SCREEN` is a stun trap, not concealment

The smoke object built at launch is **not an occluder** and has nothing to do with line of sight or
targeting. Nothing queries it when picking or tracking a target. What it does, every frame while its
`TIME` runs, is walk the **aircraft list** and test each aircraft that is alive and is not the layer:

    within  smokescreen_stun_range          of the layer, and
    dot( unit(other - layer), layerAxis )  >  cos( smokescreen_stun_angle / 2 )

Anything passing both gets hit, and again the victim's kind decides how. **The player** gets a
`FUN_0042e9d0` wash in a grey-green `(0.2, 0.29, 0.145)` at weight 0.9, or 0.97 on the first hit,
lasting 2 s, with no start delay. **An AI** gets the same `FUN_004200d0` stun as a sonic round, for
`smokescreen_stun_interval` seconds, with no cooldown of any kind; since the stun leaves the AI in
state 4 and `FUN_004200d0` accepts state 4, it is refreshed every frame the AI remains in the cloud.
The AI branch skips a victim whose `+0xf8` byte is set, the same guard the sonic hit path applies.

**The wash's re-arm timer** (object `+0x14`, one slot per screen, which is one per player in a
single-player engine): the routine runs it down by the frame dt once per frame while it is above
zero, clamping at zero, and then washes the player only while the timer reads **below 0.5 s**,
restarting it at 2.0 s. So a player who stays inside is re-washed every **1.5 s**, not every 2, and
the 0.97 weight is used only when the timer had reached zero, which is the first hit or a return
after more than 2 s outside; every re-wash while inside is 0.9. Our side keeps the timer per victim
per screen (`SmokeScreenRule.StepWash`), Decision 2's per-viewer divergence.

**The pose is live, and the layer's death ends the screen.** Every frame the routine fetches the
layer's position through its own vtable and reads the axis off the layer's current matrix at
`+0x198`; nothing about the pose is captured at the lay, so a turning layer swings the whole 600 m
cone with it. The distance is a plain length: `FUN_00422690` normalises the layer-to-victim delta in
place and returns its length, compared raw and strictly against the range, and the dot is taken
against the unit vector it left behind. The screen's timer (object `+0x10`) runs down by the frame
dt at the top of every call; a layer whose `+0x91d` dead byte is set has its emitter stopped and its
timer forced to the `-1.0` sentinel on the spot, so the walk never runs for a dead layer. The walk
itself is also gated on the screen's emitter handle (`+8`) being live, which in the shipped game it
always is; our side runs the walk unconditionally.

**The screen's own smoke.** The 0x18-byte object is six slots: the layer at `+0`, the mount at `+4`,
the running emitter at `+8`, the expiry emitter at `+0xc`, the `TIME` at `+0x10` and the wash re-arm
at `+0x14`. `FUN_004b8d50` fills the first two from its arguments and then, through
`FUN_004edda0`/`FUN_004edc50`/`FUN_004ed8c0`, instances the effect record named
**`generate_smokescreen`** and **attaches it to a scene node**, the mount's node (mount `+4`) when
the launch passed a mount, else the layer's own (layer `+0xc`). Attachment is what makes the smoke
follow: nothing re-poses it per frame, it hangs on the aircraft's node and rides wherever the
aircraft goes. `FUN_004ee160` then stores a callback (`LAB_004b9230`) and the object as its
argument at emitter `+0x74`/`+0x78`. It is **not per-frame and it does not shape the smoke**: the
anim runtime invokes `+0x74` from the instance's release (`FUN_004ebbb0`) and from one event
handler (`FUN_004ec5e0`), and the callback only finds the screen on the world list
(`DAT_0071dbbc`), clears its `+8`/`+0x10`, and runs the same `FUN_004b8f80` →
`FUN_004b8dd0` canister test the run-out branch below runs. Nothing grows or scales the emitter
or its puffs outside the puffer system's own authored ramps. The two names come from `FUN_004b92c0`, which resolves
`generate_smokescreen` and `drop_smokescreen_canister` once at mission load (`FUN_00523820`, a linear
scan of the 0x110-byte effect records by name) into the pair at `DAT_0071dcc4`. They are **globals**,
not weapon fields: every screen in the game lays the same smoke whatever fired it, which is also why
the skipped `FIRE` row costs the launch nothing visible. `wep_13`'s `FIRE` row names the same
definition, so the two routes agree on what is drawn.

**One teardown, two ends.** `FUN_004b8f60` releases the emitter (`FUN_004ebbb0`) and clears both `+8`
and the re-arm slot, and `FUN_004b8fd0` reaches it down either branch: the dead-layer branch at the
top, and the run-out-`TIME` branch, which additionally asks `FUN_004b8f80` whether the mount has run
dry (mount `+0xc` count at or below zero with the `+0x14` byte clear) and, if it has, calls
`FUN_004b8dd0` to drop a **`drop_smokescreen_canister`** instance at the mount node's world pose
with 0.8 of its velocity, resetting `+0x10` to 20 s and installing `LAB_004b9280` before
`FUN_004b8ef0` releases that one too. A layer that dies gets no canister: its branch sets the `-1.0`
sentinel before either test.

Our side is the `ISmokeEmitter` seam on `SmokeScreens`: `Lay` starts one and `SimStep` stops it on
whichever end comes first, and `SmokeScreenEmitters` runs the definition's two DISTANCE_INTERVAL
`PUFFER_STATE`s at the layer's live pose, which is the same follow the original gets from the node
attachment. Two things the original has and we do not: the canister, whose 20 s tail belongs to the
pylon-runs-dry question rather than to the screen, and the definition's own 2.0 s
`ANIMATION_OFFSET` `INACTIVE` events, which would cut the emission a quarter of the way into the 8 s
screen. Our trail runs for the screen's whole `TIME`, the same reading `ProjectilePool` takes of the
rockets' flyout trails, which carry an `Animation 10.0` stop it likewise leaves to the round's life.
The reference footage (`CAP-23`, the Balmoral's smoker from the rear cockpit and two external
poses) agrees with the whole-`TIME` reading: one launch, and the cloud is still being laid seven
seconds later, which a 2 s emission with a 4 s lifetime cannot produce.

**What the cloud is, from the numbers.** `smokerpuff` is four puffs per 0.65 m of track (about
600 a second at flight speed, ~2,000 alive), each born a 0.3–0.5 m sprite that grows 85× over a
2.5–4 s life (25–42 m at the end), thrown 10 m/s astern in the layer's frame plus −17..18 m/s of
random on every axis, damped by `FRICTION 0.3` toward the wind, and coloured `255,180,100` for the
first 1.5 % of its life then `53,74,37` at alpha 0.8, a `NEAR_FADE 30,10` culling it within 30 m
of view depth and a `FADE_RANGE 500,700` fading it out beyond. `smokerpuff2` is the one-per-0.5 m,
1–1.3 s, 4× ribbon at the tail with 1–31 m/s astern and ±1 m/s across, no near band. Two things
in the port were dropping that authored cloud: the trail path spawned ONE puff per interval with no
`LOCAL_VELOCITY` (a quarter of the density, drifting on the world axes), and the `COLORS` ramp was
multiplied unlinearised, so the `53,74,37` green rendered `109,126,92` where the reference reads
`50,68,35`; both are fixed in `Puffer`/`EmitterRenderer` (see [puffer.md](puffer.md)). What remains
between the port and the reference at the same pose is the sprite's silhouette: the reference's
puffs read as round soft blobs and ours, thousands of `splashbase` quads stacked, close to an
opaque rounded square, since the texture's rim carries 2–5 % alpha that no single sprite shows and
a thousand do. Whether the original's rasteriser dropped that rim (a 4-bit alpha format, an alpha
test) is not decoded; nothing authored says so.

The three tunables are **not per-weapon**. `FUN_004735b0`, the `ai_skill_parameters` loader, writes
them from `player.zrd.json`, where their authored names state the mechanism outright:

| Key | Authored | Default if absent | Stored as |
|---|---|---|---|
| `smokescreen_stun_range` | 600 m | 200 m | raw |
| `smokescreen_stun_angle` | 170° | ≈73.7° (a stored cosine of 0.8) | `cos(angle × π/180 × 0.5)`, a **half**-angle cosine |
| `smokescreen_stun_interval` | 5 s | 3 s | raw, the AI stun duration |

600 m across a 170° cone is close to "everything behind the layer", which makes the smoker one of the
most powerful weapons in the table rather than a defensive screen. The layer axis is the third row of
the aircraft's world matrix (`+0x198`–`+0x1a0`), the **backward** axis: the launch-side dispatch
above negates that same row to spawn an ordinary weapon forward and takes it as-is for the `REAR`
smoker, so the cone opens behind the layer, where the smoke is laid. Our side reads it as the
negated `FlightController.NoseDirection`. On our side the mechanism is `Flight/Airframe/SmokeScreens.cs`
(`SmokeScreens.Lay` for the fire path, `SimStep` from the session, `SmokeScreenRule` for the
aircraft-free tests, `SmokeScreenTunables.Load` for the three keys).

## The choker, settled

The engine-dead duration is not the authored pair read straight off. `FUN_004b9bc0` computes

    duration = ENGINE_DEAD_max * (1 - dSquared / TANGLER_RADIUS),  floored at ENGINE_DEAD_min

and passes it to `FUN_004b1690`, which sets bit `2` of the victim's disabled-systems mask at `+0x2dc`
and raises the timer at `+0x2e0` to that duration if it is longer than what is already running. The
mask's rising edge calls `FUN_004b15c0` and its falling edge `FUN_004b1630`, which swap the
propeller presentation ("What the mask's bit-2 edges run", below).

⚠ **The units here do not match, and that is what the code does.** The numerator is a squared
distance from `FUN_00538880`, while `TANGLER`'s `RADIUS` is stored raw by `FUN_004ba6f0` (unlike
`IMPACT_PROXIMITY`, which the other parser squares). So with `wep_12`'s `RADIUS [35]` and
`ENGINE_DEAD [5,13]`, a dead-centre hit kills the engine for 13 s and the term reaches the 5 s floor
at **√21.5 ≈ 4.6 m**, not at 21.5 m. Whether that is a bug in the original or intended, the effective
full-strength zone is a few metres wide.

**`ENGINE_DEAD` is a pair of globals, not a per-weapon field.** `FUN_004ba6f0` writes the two values
into `DAT_0062b120` and `DAT_0062b124` rather than into the weapon's own struct, so the last-parsed
`TANGLER` weapon sets them for every choker in the install. The static image ships them at 2.0 and
10.0; loading `wep_12` overwrites them with 5 and 13.

**There is no airspeed clamp.** The choker branch does exactly two things: zero the damage, and set
the engine-dead bit with a timer. It does not touch velocity, drag, lift or any control authority.
The recollection recorded in [`formats/weapons.md`](../formats/weapons.md), that the choker drops the
airframe to stall speed essentially instantly, is **not** what this routine does, and there is no
second effect for it to be hiding in on the hit path. Whatever the felt instantaneity was, it is the
engine cutout plus the flight model, not an authored clamp.

**The catch is a cloud, and it lives for `TIME`.** The choker's impact hook (`LAB_004ba660`, the one
`FUN_005aec90` installs) does not choke anything itself. It allocates a 0x1c-byte object through
`FUN_004b94e0` holding the shooter, the weapon, the burst position (hit record `+0x48`), the
extension's `RADIUS` **squared** (`+0x1c`, squared once here) and the shared `TIME` slot (`+0x18`,
`wep_12` authors `2.0`), pushes it onto the world list `DAT_0071db9c`, and returns 1. Every frame
`FUN_004b96d0` walks that list: `FUN_004b9590` counts the cloud's time down by the frame delta and,
while any is left, walks the aircraft list `DAT_0071dabc` and hands every alive aircraft whose
`FUN_00538880` **origin** distance to the cloud centre is under the squared radius to `FUN_004b9bc0`
with that squared distance and a `1e-4` pair, which the `TANGLER` branch turns into the duration
above and `FUN_004b1690` into an extend-only timer. Three things follow. The `RADIUS` is both the
catch (squared, so 35 m is 35 m) and the duration's denominator (raw, the mismatch above), where the
round's own `DETONATION_DISTANCE` decides only where the cloud forms. An aircraft inside the cloud is
re-choked every frame for the cloud's 2 s, so its timer stands at the formula's value until the cloud
is gone and only then runs down. And nothing excludes the shooter: a pilot who flies through their
own cloud within its 2 s is choked like anyone else (the network guard in the hook decides who
creates the cloud, not who it catches). The direct-hit callback `FUN_004b9770` reaches the same
branch on the struck aircraft with its origin distance, so a direct hit chokes on the impact frame
and the cloud carries on from there. CSVM: `ProjectilePool.SpawnTanglerCloud`/`StepTanglerClouds`,
the timer through `FlightController.TryChokeEngine`, `CollectTanglerClouds` for a suite.

### What the mask's bit-2 edges run

`FUN_004b15c0` and `FUN_004b1630` are propeller functions, not engine ones. Each vehicle holds two
anim-instance slots, `+0x6c8` for the spinning prop and `+0x6cc` for the stopped one, and the pair
swaps one for the other on the `+0x38c` node handle the def's `start_anims` are also started on.

`FUN_004b15c0` releases whatever `+0x6c8` holds (`FUN_004ebbb0`), zeroes the slot, and, if `+0x6cc`
is empty and the airframe def carries `stop_props_anim` at `def+0x190`, starts that definition
(`FUN_004edda0`) into `+0x6cc` and installs `LAB_00480820` as its completion callback
(`FUN_004ee160`). That callback is four instructions: on event code 0, the code the anim runtime's
own release emits, it writes zero back into the vehicle's `+0x6cc`, so a finished wind-down leaves no
stale handle behind. `FUN_004b1630` is the mirror without a callback: release `+0x6cc`, and if
`+0x6c8` is empty and `def+0x18c` (`spin_props_anim`) is set, start that into `+0x6c8`. Both keys are
authored, parsed by `FUN_00479240` at `0x0047b13c` and `0x0047b163` from the strings at `0x006280dc`
and `0x006280ec` and resolved by name through `FUN_00523820`. Twenty-three defs author the pair: 21
name `spinprops`, the two autogyro chains name `agyro_rotors`, and all 23 name `stopprops`.

**A choke therefore sounds and looks like more than a definition swap.** `stopprops`
(`plane_props.zrd.json`) is a one-shot: a `SOUND` event on `snd_propstop`, `staticprop1` through
`staticprop3` activated and faded from 0 to 1 opacity over 2.0 s, and `prop1`/`prop1b` through
`prop3`/`prop3b` faded from 1 to 0 over 1.5 s and then deactivated. `snd_propstop` is
`propstop.wav`, `PURGEABLE` (not looped), `3D`, range 200 to 420, so
it plays positionally at the choked aircraft whoever is flying it. The blur discs cross-fade to a
still blade over a second and a half while slot 0 is silent: the same edge stops the engine loop, and
`snd_damagedengine` starts only once the definition's re-arm timer fires, 3 to 5 s later
([formats/vehicle.md](../formats/vehicle.md), "The damaged engine's phases").

**The restart is silent and instant.** `spinprops` carries no `SOUND` event and no opacity ramp: it
activates `propN`/`propNb`, deactivates `staticpropN` and `nitropropN`, and starts an endless
`XYZ_ROTATION` of `0,0,-220` on each `propN` and `0,0,60` on each `propNb`. The install's
`startprops`, which does carry `snd_propstart` and a three-stage spin-up ramp, is named by no def and
appears as no string in the image, so nothing in the retail game plays it.

**Neither function touches thrust, particles or the engine loop.** The thrust cut is the flight
model's own read of the mask bit (`0x0048fdd0`, [flightModel.md](flightModel.md)), the loop swap is
`FUN_004b18a0`'s, and the nitro refusal is `FUN_004b2110`'s. The name "engine stop" describes when
these two run, not what they do.

**Every site the pair fires from.** `FUN_004b15c0` (`stopprops`) has exactly two call sites.
`0x004b1723` is inside the mask setter `FUN_004b1690`, on bit 2 going from clear to set, which is the
choke itself; the branch is gated both on `(param_3 & 2) != 0` and on the bit actually changing.
`0x004b8440` is inside the death routine `FUN_004b82d0`, unconditional, after the dead byte at
`+0x91d` and before the def's destroy anim, so a killed aircraft winds its props down through the
same definition. `FUN_004b82d0` is reached from the collision death `FUN_0048ad20`, the shot-down
death `FUN_004b9bc0` and the two network deaths `FUN_00498bf0` and `FUN_004995a0`.

`FUN_004b1630` (`spinprops`) also has exactly two. `0x004b1739` is the falling edge in the same mask
setter, the choke timer running out. `0x0047b9ac` is the tail of `FUN_0047b790`, the `start_anims`
(re)start, which starts every `def+0x170` entry on the `+0x38c` node handle and then spins the discs
up.

**So the restart side runs at mission start and on a captured aeroplane, and the wind-down side runs
at neither.** `FUN_0047b790` has four callers. The vehicle build `FUN_00476250` ends with it, and
that build is reached through the body builder `FUN_0047c210` from the mission setup `FUN_004735b0`
(every aircraft the roster places), the spawn-by-name helper `FUN_0047b650`, the airframe swap's own
rebuild `FUN_0047fd50` behind cutscene codes 965, 966 and 967
([cutscenes.md](../formats/anim-definitions/cutscenes.md)), `FUN_0045a390` under the player reset
`FUN_0047f1f0`, and the multiplayer setup `FUN_00451bf0`. The un-hide arm of `FUN_004b0f40`
(`param_2 == 0`) calls it, which is the swap's reveal step. The player destruction reset
`FUN_00480480` calls it once the cutscene flag is clear and `FUN_00440ad0()` is nonzero. The
multiplayer remote update `FUN_00498170` calls it when a remote's `+0x6f1` respawn flag is set.

**A build clears the mask without running either function.** `FUN_00476250` calls
`FUN_004b1690(0, 4, 0)`, and the edge work is gated on `(param_3 & 2) != 0`, which `4` fails, so the
spin at build time comes from the `start_anims` tail rather than from the mask setter. The teardown
`FUN_004b1580` releases both slots without playing anything, and the anim teardown `FUN_0047b9c0`
reaches it.

**CSVM runs the pair on both of the choke's edges.** `FlightController.TryChokeEngine` plays
`stopprops` through `CrashRuntime` on the rising edge and `SimStep` plays `spinprops` when the
engine-out timer clears, the same `Play`/`Stop` call shape the nitro edges use, on the human rig and
the AI one alike, and `PlaneBuilder` keeps `staticpropN` in the flight build for it. `spinprops` runs
with its `OBJECT_MOTION` suppressed (`AnimRuntime.SuppressedMotionAnims`), because `PropAnimator`
already turns those discs procedurally at the same `-220`/`60` rates and two writers on one transform
is one too many; the restart also puts the discs' opacity back, which the wind-down took to zero and
the definition itself never writes. The stop call refuses while the stopped presentation already
holds the slot, which is the original's own `+0x6cc` occupancy test, and is what keeps a choked
aircraft's later crash from replaying the fade and re-firing `snd_propstop`. The sites that reach
`start_anims` need no new call here: mission start and the airframe swap both build their aircraft
through `HumanFlightAdapter.Assemble` or `AiFlightAssembler`, which replay the spawn choreography
already. CSVM plays `startprops` there where the original plays `spinprops`, a divergence in the
spawn sound rather than in the choke.

**The death runs it from the once-per-death shutdown, beside the cue.** `EndFlightSystems` is the
one site that ends a spent aircraft's flight systems, so `FlightAudio.OnEngineStop` and the
`stopprops` call stand on consecutive lines there and the wind-down's two halves cannot be raised
apart: the discs fade to the still blade wherever `snd_propstop` sounds, whether the aircraft was
shot down, rammed or flown into the world, and whether or not its airframe def binds a destroy def.
That is also the original's order, the death routine's own `stopprops` call standing ahead of the
def's destroy anim, so the destroy choreography deactivates the healthy hull's propeller nodes over
a wind-down that has already run rather than being undone by it. A wreck's ground contact raises
nothing further, its death having already spent the slot. `Respawn` takes `stopprops` off the slot
before it replays `startprops`, or a hull that went down with its propellers stopped would fly again
with the stop definition still fading `staticpropN` in under the start one fading it out. The suite
is `prop-slot-edges`.

## Two answers this routine gives to other items

**Blast knockback is authored, and it is ground-vehicle code no shipped def reaches.** Late in
`FUN_004b9bc0` (`0x004ba32c`) three gates stand in front of one call. The victim's `mode` class
`+0x67c` must be **2** (`tank`), the larger of the two damage figures must exceed **5.0**
(`0x006036bc`), and the hit record `FUN_005ad430` returns must carry two distinct points at `+0x00`
and `+0x0c`. Past them the routine normalises the first point minus the second and calls
`FUN_0048f5e0`, whose only caller this is, with that direction and two magnitudes:
`damage * vehicle_def[+0xa0] * 0.005` (`0x00608d58`) and `damage * vehicle_def[+0xa0] * 0.0333`
(`0x00608d5c`).

`vehicle_def[+0xa0]` is **the reciprocal of `mass`**, not a knockback constant. The def parser builds
it at `0x0047afae` as `1.0 / def[+0x9c]` immediately after reading the `mass` key (`0x0062807c`), so
the only per-airframe term in the two magnitudes is the vehicle's own mass, and a heavier vehicle is
moved less by the same damage. `basic_airplane` authors `mass 0.6` and the two surface vehicles
author `40.5` ([`../formats/vehicle.md`](../formats/vehicle.md)).

`FUN_0048f5e0` rotates the direction into the victim's own frame (`FUN_004d2080` then
`FUN_0053cb10`) and adds four terms, discarding the Y component. The pitch angle `+0x1f8` takes
`-z * first`, the roll angle `+0x200` takes `+x * first`, and the rate pair `+0x938` / `+0x940`
takes `-x * second` and `-z * second`. That pair is the surface-driving integrator's drive and steer
rates, damped in `FUN_0048f7d0` by the two `rate_damping` values at `def+0x90` / `def+0x94` and
driven there from the steering input at `+0x110`. So the effect tips a driving vehicle's attitude
and shoves what it is doing on the ground. It is not a linear impulse, and it reaches no aeroplane.

⚠ **Nothing in the shipped data reaches this branch, so a blast in the original moves nothing.**
`mode` `tank` is authored by no def, and the string `tank` does not occur in `vehicle.zrd` at all,
so `+0x67c` is never 2. Neither an aeroplane nor a world object is pushed by a burst, and CSVM
therefore applies no blast impulse of its own. Do not reintroduce one from these constants: they
are a ground-vehicle law, and the airframe term in them is a mass.

**Being hit has a direction cue with two variants.** Under a global gate (`DAT_0071c4e0`), the
routine computes the bearing to the hit source as an `atan2` converted to degrees and calls
`FUN_00481330` for a `CANNON` hit or `FUN_004813c0` for anything else, passing the damage magnitude
alongside.

## `TORPEDO` selects a force-feedback effect

Its one reader is `FUN_00480f50`, which drives the Immersion TouchSense force-feedback API
(`CImmCompoundEffect`). Gated on `ROCKET` (`0x10`), so guns are handled elsewhere, it picks one of
three launch effects and gives each its own direction and magnitude:

| Weapon | Effect slot | Direction | Magnitude |
|---|---|---|---|
| `TORPEDO` (`0x08`) | `+0x158` | 0 | 1.0 |
| `REAR` (`0x20000`) | `+0x154` | 180 | 0.58 |
| any other ordnance | `+0x150` | 0 | 0.79 |

So the torpedo is the heaviest thing you can launch and the stick says so, and a rear-firing weapon
kicks from behind. This is the only behaviour `TORPEDO` selects; everything else about the aerial
torpedo comes from its other keys.

CSVM does not reproduce this effect as authored: Godot's joystick vibration carries no direction, so
the front/rear split cannot be ported. What `TORPEDO` and `REAR` do carry into the remake is which
row of the pad rumble a launch takes, magnitude and length only, which is
`CSVM/src/Bindings/PadRumble.cs` over the survey in [`input.md`](input.md).

⚠ **This corrects an earlier claim on this page that `TORPEDO` had no reader at all.** That claim
rested on three instruction sweeps, and the gap between them was exactly this instruction form:
`TEST <memory>, 0x8` covered memory operands, `AND <memory>, 0x8` and `AND <register>, 0x8` covered
the `AND` forms, and **`TEST <register>, 0x8` was never swept**. The compiler loads the flags dword
into a register here and tests it there. A negative result about a flag is only as good as the
instruction forms behind it, and three sweeps that share a blind spot are not three independent
checks.

### What the torpedo actually does, then

Every part of `wep_14`'s flight is now accounted for by keys that **do** have readers:

| Key | Value | Effect, from the decode above |
|---|---|---|
| `TURN_RATE` | 0.001 | the dumbfire sentinel: guidance runs but turns it essentially not at all |
| `VELOCITY` | 60 m/s | its own cruise speed, and a slow one |
| `ACCELERATION` | 0 | no motor ramp |
| `LOCK_ON` | 2.5 s | inherits the launcher's velocity, decaying linearly to zero over 2.5 s |
| `RANGE_MINIMUM` | 300 m | the one entry carrying it: unhittable for its first 300 m, drawn throughout |
| `MODEL_ANIMATION` | `torpedo_trail` | the launch look, the 3.5 s switch and the cruise look, all on the def's own clock |
| `DETONATION_DISTANCE` | 1 m | fuse trigger distance, and its ticket into the fuse list |
| `IMPACT_PROXIMITY` | 30 m | blast radius |
| `TARGETABLE` + `FLYOUT_HEALTH 10` | | wrapped into the target list, with 10 HP to absorb |
| `DAMAGES_ZEPPELIN` | | restricted to zeppelins at launch, and the gasbag routing gate on impact |

So it leaves the rail at launcher speed plus 60 m/s, flies straight with no gravity drop, and sheds
the inherited component over 2.5 s down to a 60 m/s cruise. An aircraft at 120 m/s launches one that
**halves its speed** across those 2.5 s, which is the slowdown a player at the controls of the
original reports seeing after a torpedo drop.

## What a round collides with

`FUN_005b03f0` advances a round to its next hit and has two modes. If the spawn built a **precomputed
hit list** along the flight path (`+0x608`, the sorted list `FUN_005aef40` fills for a round whose
weapon takes that path), the step just consumes entries in order, comparing each entry's stored
squared distance against the distance travelled so far. Otherwise it runs a live swept query
(`FUN_004c8ec0`) from the round's current position. Either way the result goes to `FUN_005ad330`,
which is the hit resolve already documented in [`weaponImpact.md`](weaponImpact.md), including its
water special case. When both a list entry and a live hit are available, the **nearer** of the two
wins, compared by squared distance.

## Still open

Nothing bearing on ordnance behaviour. `FUN_004c8ec0` (the swept query) is a geometry-layer routine
shared with the collision system rather than weapon code, and was not opened. `FUN_004c8f70` (the
segment query the splash occlusion cast uses) was read far enough to say what it tests: it walks the
spatial grid along the segment and tests every node carrying both `ACTIVE` (`0x4`) and the intersect
bit (`0x10`), plus the terrain cells it crosses, so it is the same database every other ray in the
engine sees; its per-polygon test (`FUN_004c9a00`) was not opened.
- Which nodes carry the runtime flag `0x400000` that opts them into the splash occlusion cast (see
  "Half two, the splash").
- The per-round yield factor at `+0x678`, which scales the blast radius and both damage figures
  together. Nothing observed writes it other than `1`, so CSVM does not model it; a writer would
  make every blast quantity per-round rather than per-weapon.
- `FUN_004881e0` is the player's fire-input tick. It routes by `CANNON` (`0x40`) and `ROCKET`
  (`0x10`) only, feeding `CALIBER` to `FUN_004810d0` for guns and the whole weapon to `FUN_00480f50`
  for ordnance.

## Evidence & limits

- `FUN_004ba6f0` was read in full, so the flag map and the struct layout are complete for this build.
- `FUN_004b9bc0` was read in full. It handles hits **on an aircraft**; whether ground objects and
  zeppelins route through the same function is unread, so the "four types deal no damage" finding is
  stated for aircraft targets.
- `FUN_004b6820`, `FUN_004b5fb0`, `FUN_004b9770`, `FUN_005aef40`, `FUN_005af960`, `FUN_005af720`,
  `FUN_005abcf0`, `FUN_00441830`, `FUN_00441780`, `FUN_004b8b50`, `FUN_004b8ad0`, `FUN_004b89c0`,
  `FUN_004b7670`, `FUN_004b1690`, `FUN_005afd50`, `FUN_00480f50`, `FUN_005ac3a0`, `FUN_005ac7a0`,
  `FUN_005ac690`, `FUN_005ac580`, `FUN_005aca30`, `FUN_005acac0`, `FUN_0042e840`, `FUN_004200d0`,
  `FUN_0042e9d0`, `FUN_004b8d50`, `FUN_004b8fd0`, `FUN_004b8dd0`, `FUN_004b8f60`, `FUN_004b8f80`
  and `FUN_004b92c0` were read in full. `FUN_005aeca0`, `FUN_004cd210` and `FUN_004cd2a0` were read
  in full for the intersect bit and `PROJECTILE_BBOX`; the consumer of node flag `0x20` inside the
  collision database was not. `FUN_004cd210`'s bit was named against `FUN_004cca30`
  (`gwNodeSetActive`, bit `0x4`) and the gamez node-flag decode, so "intersect, not visibility"
  rests on both the toggle and the writer of the other bit. `FUN_005ae990` (the binding-block
  parser) was read in full for the `FLYOUT` layout at `+0xf8`, and the no-reader claim for its
  `SOUND` slots rests on an instruction sweep over the `+0x124`/`+0x128` operand forms plus the
  `LEA`s forming `+0xf8`; a reader reaching the slot through an unrelated base register would be
  outside that sweep.
- The impact hook `LAB_004ba660` was read from its disassembly (Ghidra has it as a label, not a
  function), and its cloud's builder `FUN_004b94e0`, list walker `FUN_004b96d0`, per-cloud step
  `FUN_004b9590` and list init/teardown `FUN_004b9440`/`FUN_004b9680` in full. `FUN_005ac7a0`'s
  `+0x74` bits `0x10` and `0x40000` were traced to `FUN_005ad630`'s `CRATER` and `QUICKSAND` reads,
  `0x800000` to its `ANIMATION_ALWAYS` read, and `DAT_006379c0` was read as `(0, 1, 0)`;
  `FUN_0053fd40` (the two-vector rotation) and `FUN_00422690` (normalise) were read in full.
- `FUN_004cb420` (the splash gather), `FUN_004d8bd0` (the box-to-sphere reduction),
  `FUN_005388d0` (the rooted distance) and `FUN_004cd210` (the intersect-bit toggle) were read in
  full for the surface-distance, occlusion and cap findings under "Half two, the splash".
- The three `smokescreen_stun_*` values were traced from their authored names in `player.zrd.json`
  through `FUN_004735b0`'s stores to their reads in `FUN_004b8fd0`, including the `× π/180 × 0.5`
  conversion, so the half-angle reading is decoded rather than inferred.
- The claim that no AI code reads the disabled-systems mask rests on an enumeration of every
  instruction referencing `+0x2dc`: twelve sites, all in the flight-model and damage ranges, none in
  the AI decision range. `FUN_0048fc40`, the flight model's reader, was not opened.
  `FUN_004b8b50`'s selection rule was taken from its disassembly rather than its decompilation,
  because the decompiler's rendering of the branch order there is misleading.
- `+0x3c`, `+0x40`, `+0x44` and `+0x48` were traced to their parse sites in `FUN_005ad630` and are
  confirmed, squarings included. `+0x1c` `RANGE`, `+0x38` `ACCELERATION` and `+0x54` `GRAVITY` are
  still named by their use in `FUN_005afd50` rather than traced, though all three agree with the
  value ranges [`formats/weapons.md`](../formats/weapons.md) reports.
- `FUN_00538880` returns a **squared** distance. Every comparison in this page that reads as a
  distance test is a squared-distance test, which is what makes the splash falloff quadratic and the
  `TANGLER` radius mismatch visible.
- The `+0x74` bit assignments and the guidance scalar offsets were read from `FUN_005ad630`'s parse
  sites one key at a time, each confirmed against the key string it is stored beside. `+0x84` is
  computed from `LOCK_ON_LEAD`'s two elements by an expression that was not read.
- `FUN_00538ca0` (bearing), `FUN_0053e56d` (the intercept solve), `FUN_00538d70` (slerp) and
  `FUN_004c7630` (the terrain probe) were not opened; their roles are inferred from arguments and
  from the arithmetic around the call.
- `FUN_0042c070` and `FUN_004b8ce0` were not opened; their roles above are inferred from their
  arguments and call sites, and are labelled as such. `FUN_004b15c0` and `FUN_004b1630` **were**
  opened, along with the callback `LAB_00480820`, the release `FUN_004ebbb0`, the callback
  installer `FUN_004ee160`, the death routine `FUN_004b82d0`, the spawn/reset `FUN_0047b790` and the
  slot teardown `FUN_004b1580`; their two def keys were traced from the parse sites at `0x0047b13c`
  and `0x0047b163` to the authored `spinprops`/`stopprops`/`agyro_rotors` names in the shipped data,
  so "What the mask's bit-2 edges run" is decoded rather than inferred from the names.
  `FUN_0048f5e0` **was** read in full, along with the surface integrator `FUN_0048f7d0` that damps
  the rate pair it writes and the def parser at `0x0047af60`..`0x0047afc6` that builds `+0xa0` from
  `mass`, so the knockback above is read rather than inferred. That `mode` `tank` ships nowhere
  rests on two independent checks, the parser's string table and the absence of `tank` from
  `vehicle.zrd`.
  `FUN_005aef40` **was** opened for the speed-cap seeding, and `FUN_005389a0` with it: it is
  `out = a + b * scale`, which is what makes the motion step's velocity rebuild
  `inherited + heading * speed`.
- Weapon-record offsets `+0x40` (an effect radius), `+0x44` (the fuse trigger distance) and `+0x74`
  (a second flags word) are named by use. Which authored key writes each one was not traced back
  through the `.zrd` parse, so the mapping to `IMPACT_PROXIMITY` and `DETONATION_DISTANCE` is
  inferred from the arithmetic they appear in.
- The globals indexed off `DAT_0064ef78` (the feedback multipliers at `+0x3c`, `+0x68`, `+0x94`,
  `+0x98`, `+0xc0`) were not resolved to values.
