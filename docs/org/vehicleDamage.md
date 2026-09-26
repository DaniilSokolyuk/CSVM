# The vehicle damage model, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), 2026-08-13, to settle which of two shipped numbers is
the patrol boat's hit points. That backlog item is retired on the strength of this page; its closing
commit is `git log --grep=BL-102`. Every claim below names the function it came from.

Everything here is a description of *behaviour and constants*. No decompiler output is reproduced;
the addresses are given so any claim can be re-checked at source.

**Scope.** One class covers everything the player can shoot down that flies or drives, and it is
selected by **how the instance was created, never by what it is**: a roster entry spawns a vehicle,
which runs the code on this page (player plane, AI plane, autogyro, patrol boat, truck). A compiled
anim def anchored to a placed world node makes a destructible, which runs the *other* system,
described in [`formats/destructibles.md`](../formats/destructibles.md). Nothing in the executable
special-cases a vehicle type; the split is entirely in the data. The patrol boat is authored into
both, which is what made its hit points look contradictory, and the last section works that through.

**Where the authored side lives.** [`formats/vehicle.md`](../formats/vehicle.md) has the
`armor`/`health` pair, `destroyable_parts` and `injure_anims`;
[`formats/ai-rosters.md`](../formats/ai-rosters.md) has the `aiv` roster slots this page shows being
consumed (`init_health`, the four zone pairs, `armor`).

## Function map

| Address | Role |
|---|---|
| `FUN_005abcf0` | The impact dispatcher: calls the handler the struck node registered at `+0xbc` |
| `0x004b9750` | The handler a vehicle registers there, a thunk onto `FUN_004b9770` |
| `FUN_004b9770` | Resolves the shooter from the round, files the radio and threat calls, then enters the wrapper |
| `FUN_004b9b30` | **The take-hit entry point, a WRAPPER that LOOPS** (found 2026-08-14, see the correction section) |
| `FUN_004b3950` | The struck-zone resolver: matches hit geometry against parts **with health remaining only** |
| `FUN_004b3b60` | The resolver's miss fallback: rand() over the first up-to-3 surviving parts |
| `FUN_004b7f30` | Clamped pool subtract: pool -= damage floored at 0, returns the leftover |
| `FUN_00479240` | The `vehicle.zrd` parser. `health` -> def`+0xb4`, `armor` -> def`+0xb8`, each written only if the key is present |
| `FUN_00475820` | Def -> instance: the whole-vehicle pair, the `destroyable_parts` array, the death-anim reference, and the def pointer itself |
| `FUN_00476250` | Spawn/reset: resolves model nodes, fills the per-part pools from the def, and applies the per-spawn jitter |
| `FUN_0047c210` | The roster spawn: applies an `aiv` block's `init_health`, `armor` and four zone pairs, then the difficulty scale |
| `FUN_0047bd90` | Set one named part's pools **and re-derive the whole-vehicle pair as the sum over parts** |
| `FUN_0047bf70` | The same for current values only (no maxima) |
| `FUN_004b9bc0` | Take a hit: routing, the steady-hand test, the damage voice lines, the death test |
| `FUN_004b7f80` | The spend: armour first, 1:1 overflow into health |
| `FUN_004b8070` | Spend against the whole-vehicle pools |
| `FUN_004b3bf0` | Spend against one part, then recompute the whole-vehicle pools from the parts |
| `FUN_004b80a0` / `FUN_004b8180` | Set armour / set health directly (repair, cheats); the health one rescales every part pool |
| `FUN_004b3800` | The def-level `injure_anims` driver, keyed on the **whole-vehicle** health fraction |
| `FUN_004b3d70` | The per-part `injure_anims` driver, keyed on **that part's** fraction |
| `FUN_004b1790` | The low-health test that arms the damage-state call (`FUN_004b1690`) |
| `FUN_004b1690` | The disabled-systems setter at `+0x2dc`: sets or clears one bit, re-points the engine sound slot when the whole mask changes state, and swaps the propeller anims on bit `0x2`'s own edges |
| `FUN_00478a00` | The vehicle-def constructor, which is where every def field with no parser token gets its value |
| `FUN_00477b70` | The `kind_of` copy constructor: a child def starts as a field-by-field copy of its parent |
| `FUN_004b82d0` | Death: plays the def's destroy anim, and everything that follows from being dead |
| `FUN_0048b920` | Ground impact: picks a crash anim from the per-material table and registers the same callback |
| `LAB_00480710` | The native completion callback a dying vehicle registers on its destroy/crash anim |
| `FUN_0047bab0` | Vehicle removal, the only path that frees a vehicle; defers while a death anim is live |
| `FUN_00520910` / `FUN_00521180` | Start an anim on a context node: clone a template, or rebind an instance |
| `FUN_004533c0` | The ambient/reinforcement plane pool, the one place a dead vehicle is recycled |
| `FUN_0048ad20` | The surface-vehicle terrain update, which carries a health drain of its own |
| `FUN_0041c470` | The debug info panel, which is what names these fields |

## The two ledgers

A vehicle instance carries a whole-vehicle pair and, optionally, a list of per-part pools.

| Instance offset | Field |
|---|---|
| `+0x2c4` | armour max |
| `+0x2c8` | armour current |
| `+0x2cc` | health max |
| `+0x2d0` | health current |
| `+0x64` | the airframe def it was built from |
| `+0x9c` / `+0xa0` | begin/end of the per-part array, records of `0x58` bytes |
| `+0x890` | one anim handle per def-level `injure_anims` entry |
| `+0x6d0` | the destroy anim reference, copied from def`+0x158` |

The naming is not inferred: `FUN_0041c470` prints these four as
`Armor: %.1f/%.1f Health: %.1f/%.1f` (`0061fdfc`), and each part as
`DP: %s Armor: %.1f/%.1f Health: %.1f/%.1f` (`00620400`) from the part fields below. An object with
no vehicle behind it gets a third format, `Armor: N/A Health: %.1f/%.1f` (`0061fe44`), read through
two virtuals rather than these offsets: that is the static-destructible case.

A part record (`0x58` bytes, `FUN_00476250` fills it, `FUN_0041c470` prints it):

| Part offset | Field |
|---|---|
| `+0x04` | the model node this zone is |
| `+0x0c` / `+0x10` | the got-hit anim and its live handle |
| `+0x18` / `+0x1c` | the destroy anim and its live handle |
| `+0x24` / `+0x28` | armour max / current |
| `+0x2c` / `+0x30` | health max / current |
| `+0x34` | the zone id a hit is matched against |
| `+0x3c` / `+0x40` | the part's own `injure_anims` list, `0x1c` per entry |
| `+0x4c` | one handle per entry of that list |

## The damaged state

A vehicle carries a **disabled-systems mask** at `+0x2dc`, a dword of independent bits, and a timer
beside it at `+0x2e0`. `FUN_004b1690` is its only setter: it ORs a bit in, ANDs it out, or (for the
value 4) writes the mask outright, and it is the mask's *edges* that do work. On bit `0x2` it runs
`FUN_004b15c0` or `FUN_004b1630`, which swap the vehicle's `spin_props_anim` for its
`stop_props_anim` and back ([ordnanceTypes.md](ordnanceTypes.md#what-the-masks-bit-2-edges-run));
on the whole mask leaving zero it stops the engine sound handle and re-runs the audio routine, and on
the mask returning to zero it puts `engine_sound` back on the slot and re-runs it. Twelve
instructions in the image reference
`+0x2dc`, all in the flight-model and damage ranges.

| Bit | Meaning | Raised by |
|---|---|---|
| `0x1` | the airframe is damaged | `FUN_004b1790`, below |
| `0x2` | engine out | the choker ([ordnanceTypes.md](ordnanceTypes.md), "The choker, settled"), with the `+0x2e0` timer |
| `0x4` | written as the whole mask, clearing the others with it | `FUN_004b1690`'s own value-4 arm |

**Bit `0x1` is a health-fraction test, and the fraction is a quarter.** `FUN_004b1790` runs on an
aircraft (mode class 0 or 4) whose bit is still clear, walks the per-part array, and takes the
**lowest health-only fraction** `part[+0x30] / part[+0x2c]` across the parts whose flag byte
`[part+0x21]` is set; on a vehicle with no parts it takes the whole-vehicle pair
`[+0x2d0] / [+0x2cc]` instead. If that fraction is strictly below the airframe def's `+0xbc` the bit
goes up. The two callers are the collision function `FUN_0048d2c0` and the take-hit routine
`FUN_004b9bc0`, so the test is re-run after every damage event and never on a timer.

⚠ **Armour is not in this fraction.** The pair divided is health max and health current; the part's
armour pool at `+0x24`/`+0x28` is untouched here. A reading that uses the combined pool trips the
state earlier than the original does on any zone that carries armour.

⚠ **What CLEARS bit `0x1` is undecoded.** `FUN_004b1790` only ever sets it, and guards on the bit
being clear already, so within that function the state is one-way. `FUN_004b1690` can clear any
bit, but no caller has been traced clearing this one. CSVM recomputes the test every frame instead
of latching, so a repair that lifts the worst zone back over a quarter returns the healthy engine
note immediately; whether the original does the same is the part to decode before relying on it.

⚠ **`def+0xbc` has no authored key.** `FUN_00479240` never writes it. The value comes from the def
constructor `FUN_00478a00`, which stores `0.25` at `0x00478b44`, and `FUN_00477b70` copies it down
each `kind_of` chain. Do not look for a `vehicle.zrd` key to tune: every def in the install carries
a quarter, and the test is strictly below it. What the bit then drives is the engine-audio swap
([formats/vehicle.md](../formats/vehicle.md), "What makes an airframe damaged").

## Where the numbers come from at spawn

Four things can write the pools, in this order.

**1. The airframe def.** `FUN_00475820` copies def`+0xb4` into health max and def`+0xb8` into armour
max, unconditionally, and copies the whole `destroyable_parts` array across. `FUN_00476250` then
fills each part's max and current from the def's own record, so a fresh vehicle starts at full on
both ledgers. Because `FUN_00479240` only writes def`+0xb4` when the def actually spells `health`,
a def chain that never spells it keeps the def constructor's value: `FUN_00478a00` stores **100.0**
into both `+0xb4` (health max) and `+0xb8` (armour max), and `FUN_00477b70` copies them down each
`kind_of` chain. That is why `FUN_004b3bf0`'s recompute never sees a zero max on a player plane.

**2. The roster block.** `FUN_0047c210` is the spawn used for an `aiv` entry. It applies
`init_health` (block`+0x28`) **only if greater than zero**, and `armor` (block`+0x2c`) if greater
than or equal to zero, which is exactly the "0.0 means use the airframe default" the roster's own
field list implies. It then hands the four zone pairs (block`+0xdc` onward: nose, tail, then the
literal names `leftwing` and `rightwing`) to `FUN_0047bd90` one at a time.

⚠ **`FUN_0047bd90` would change what "total health" means, and in the shipped data it never runs.**
It sets the named part's max and current, then re-derives the whole-vehicle armour and health, max
and current, as the **plain sum over all parts**. That would make the parts authoritative and the
whole-vehicle pair a derived total. Its first line rejects the call when both arguments are `-1`,
and all 414 shipped roster blocks carry `-1` in all eight zone slots, so the sum-derivation is dead
in this install and the def's own `health` stays authoritative.

**3. The difficulty scale.** Still in `FUN_0047c210`, and only when the spawned vehicle is
**hostile**: armour max and health max are multiplied by `1 + k * 0.125`
(`0x0047cc1b`-`0x0047cc2d`: `FILD` the integer `k`, `FMUL [0x0060802c]` = `0.125`,
`FADD [0x006032dc]` = `1.0`), and a `k` of zero skips the whole block (`0x0047cc13`). Current is
re-seeded from max afterwards. The same factor then multiplies each zone pair
(`0x0047cca4`-`0x0047cd15`), so the per-zone pools scale with the totals.

`k` comes from the difficulty query `FUN_00440710` at `0x0047cb32`-`0x0047cb4e`, and its three
values are read off the branch directly:

| Difficulty | Campaign label | `k` | Factor |
|---|---|---|---|
| 0 | Normal | `MOV EDI,0xfffffffe` = **-2** (`0x0047cb3b`) | **0.75** |
| 1 | Hard | 0, the block is skipped | 1.0 |
| 2 | Hardest | `MOV EDI,EAX` = **+2** (`0x0047cb4c`) | **1.25** |

⚠ **The low tier is `0.75`, not `0.875`.** This page previously read `k` as `-1` on the low tier,
and [`hangar.md`](hangar.md) previously gave the high tier as `1.125`; both are refuted by the
`0xfffffffe` immediate and the `CMP EAX,0x2` above. The spread is symmetric at two eighths either
side of 1.0, not one. The campaign selector's own labels are `IDS_DIFFICULTY` in
`rof/ui_strings.json` ids 109-111, Normal / Hard / Hardest in that order, so the default campaign
setting is difficulty 0 and enemies there carry **three quarters** of their authored pools.

⚠ **The gate is hostility, not inequality with the player's team.** `0x0047ca79`-`0x0047ca8b`
compares the spawned entity's side (`+0x8`) against the constant `1` that `FUN_004830c0` writes and
leaves `k` at zero when they are equal **or when either is zero**, so a neutral (side 0) is not
scaled either. ⚠ **The same `k` also shifts every one of that pilot's nine skill ratings**, in the
same function and behind the same gate, and the roster's `ace` flag exempts the ratings from it
while leaving this scale in force
([aiControlLaw.md](aiControlLaw.md#the-rating-the-interpolation-receives-is-not-the-authored-one)).
The difficulty setting therefore reaches two things at spawn, not one.

**4. The per-spawn jitter, aircraft only.** At the end of `FUN_00476250`, a vehicle whose name is
not `player`, in single player, and whose `mode` is `jet` (0) or `heli` (1), gets each
of armour max and health max multiplied by a uniform random factor in `[0.95, 1.05]`, current
re-seeded from max, and the same treatment applied to nine other def-derived numbers. **Ships (mode
3), ground vehicles (mode 2) and the `w*` wingman family (mode `wingman`, 4) are excluded**, so no
patrol boat, truck or shipped wingman is ever jittered and no player plane is either. The full
eleven-slot list, the gates and the class table are decoded in
[`flightModel.md`](flightModel.md)'s "The per-spawn jitter"; the whole-vehicle pair here is slots 1
and 2 of it, and **the per-part pools are not touched**. Implemented as
`PlaneStats.WithAiSpawnJitter`, applied at `FlightRoster.SpawnAi`.

## Taking a hit

⚠ *Corrected 2026-08-14: `FUN_004b9bc0` is NOT the entry point, `FUN_004b9b30` wraps it and
loops the unabsorbed leftover back through. The section below stands for one pass; the
correction section further down is the full contract.*

`FUN_004b9bc0` is the per-pass body. Ignoring the special cases it opens with (already dead, hit by
its own shooter, weapon classes that detonate or attach instead of damaging), it does this:

1. It takes a **pair** of damage numbers, armour damage and health damage, not one figure.
2. If the hit reports a zone id and the vehicle has parts, it looks for the part whose `+0x34`
   matches and spends against that part (`FUN_004b3bf0`). Otherwise it spends against the
   whole-vehicle pools (`FUN_004b8070`). Both routes go through the same spend helper.
3. The spend (`FUN_004b7f80`) takes armour first and overflows 1:1: the armour pool absorbs the
   fraction of the armour damage it can cover, and only the remaining fraction of the health damage
   reaches health. Armour covering the hit outright leaves health untouched. This is the ordering
   `CAP-19` observed at the controls, now confirmed in code.
4. **Part damage rewrites the whole-vehicle pools.** After spending on a part, `FUN_004b3bf0` sets
   whole-vehicle health current to `(sum of part health current / sum of part health max) * health
   max`, and armour the same way. The whole-vehicle pair is a running summary of the parts whenever
   parts exist, and it is what everything downstream reads.
5. **Death is one test and one test only: whole-vehicle health current at or below zero**, which
   sends it to `FUN_004b82d0`. *(Corrected 2026-08-14: under (4) every part exhausted is
   SUFFICIENT, not necessary, the wrapper loop's zone-less overflow can drain the pool with
   parts still alive.)*
6. Along the way it prints the AI's steady-hand test (`Absorbed %f damage; steady hand test
   failed. Evading.`, `0062b1e8`) and picks a radio line by comparing combined
   `armour + health` current against 0.7, 0.5 and 0.3 of combined max.

The reverse direction exists too: `FUN_004b8180` sets health directly and then scales **every**
part's current health by the new whole-vehicle fraction, so the two ledgers are kept consistent
from either end.

## Teams and friendly fire

**A round from one aircraft damages another whatever the two teams are.** The team ids gate the
target scan and the radio lines; nothing on the damage path gates the spend. Read 2026-08-14 to
settle whether the original refuses friendly damage or only friendly targeting: it refuses only the targeting.

The path from a struck polygon to a drained pool has no team test in it. The node that owns the
struck geometry carries a handler table at `node + 0xbc`, a list of `{context, callback}` pairs;
`FUN_005abcf0` calls `pair.callback(pair.context, weapon, hitNode, damagePair)` at `0x005abe68`. A
vehicle installs the thunk `0x004b9750`, with itself as the context, into slot 0 of its own node's
table when it spawns (`FUN_0047c210` at `0x0047c7d0`, through `FUN_005abbf0` and `FUN_005abb20`).
The thunk enters `FUN_004b9770`, which resolves the shooter and calls the wrapper
`FUN_004b9b30`, which calls the per-pass body `FUN_004b9bc0`. The spend is reached at `0x004ba0da`
(part-scoped) or `0x004ba102` (whole-vehicle), and the only things between the entry and those two
calls are the body's own early returns: the victim is already dead (`+0x91d`) or already in its
scripted destruct in a network game (`+0x91f`), a no-damage byte is set on the victim (`+0x920`) or
globally outside a network game (`DAT_0064f66e`), the shooter is the victim itself, the damage pair
is zero, or the weapon belongs to a class that detonates, attaches or blinds instead of damaging.
None of them reads a team.

**The team sits at instance `+0x08`,** and the engine has one recurring predicate over it: *the two
teams are equal, or either one is 0*. It is the same test the target scan rejects a candidate on
([aim-assist.md](aim-assist.md)) and the same test the spawn skips the enemy difficulty scale on
(`FUN_0047c210`, whose `local_14[2]` is this field). It appears three times on the damage path, and
each time it picks an announcement rather than an outcome:

- **`0x004b9d5e`–`0x004b9d7d`** computes the predicate over shooter and victim into a stack byte.
  Its **only** read is `0x004ba599`, where it is pushed as argument 2 of `FUN_0042e840`, and
  `FUN_0042e840` never reads argument 2 (its body touches `[EBP+8]`, `+0x10`, `+0x14`, `+0x18`,
  `+0x1c`, `+0x20` and `+0x24`, and nothing at `+0xc`). The value is computed and discarded. The
  branch that reads it is in any case the flash/sonic arm, which zeroes the damage pair for every
  victim regardless of team.
- **`0x004b98e0`** in `FUN_004b9770`: when the **local player's** round damages an aircraft the
  predicate calls friendly, that aircraft speaks combat-voice trigger 28 (`0x004b9961` sets `ECX`
  to the victim), if it owns a line for it. See [`formats/combat-voice.md`](../formats/combat-voice.md).
- **`0x004ba125`** at death: the gloat line is picked from the predicate over shooter and victim,
  then over victim and the local player. When shooter and victim come out friendly, **no line is
  chosen at all** and control falls straight through to `FUN_004b82d0`. A friendly kill is a silent
  kill, not a refused one.

⚠ **Do not read the test at `0x004b9d5e` as a friendly-fire rule.** It computes exactly the value
such a rule would need and hands it to a parameter nothing reads, so the one place the damage path
asks about teams decides nothing. `docs/verification.md` SRC-6 is the general form.

## Damage staging

Two independent drivers, both structured identically, both able to retract what they started.

**Def-level `injure_anims` (`FUN_004b3800`)** runs off `health current / health max` on the
**whole vehicle**, walking the def's list at def`+0x1a8` (entries of `0x1c` bytes, the anim
reference at `+0x18`). When the fraction drops to or below an entry's threshold and that entry has
no anim running, it starts one and stores the handle in the instance's `+0x890` array; when the
fraction rises back above the threshold and a handle is live, it stops the anim and clears the
handle. So the staging is reversible, not a latch, and repairing a vehicle visibly un-stages it.

⚠ **Armour is not in the fraction.** The divide is literally `[inst+0x2d0] / [inst+0x2cc]`, the
health pair only. The armour pair (`+0x2c4` / `+0x2c8`) is never read on this path, so a vehicle
with its armour stripped and its health untouched has crossed no def-level stage. This matters
because the combined armour+health progression is the right scale for the gauge (a hit walks one
down and then the other) and the wrong scale for these thresholds.

The entry layout, `0x1c` bytes:

| Offset | Field |
|---|---|
| `+0x00` | the threshold fraction |
| `+0x08` | the root-node reference, read only when `+0x0c` is set |
| `+0x0c` | non-zero selects a node-scoped context: `FUN_004d8cf0` resolves `+0x08` against the vehicle (falling back to `_C_exref` when `+0x08` is null). Zero plays against the vehicle's own node at inst`+0x0c` |
| `+0x18` | the anim reference, passed to `FUN_004edda0` |

That `+0x0c` field is the optional third element in
[`formats/vehicle.md`](../formats/vehicle.md)'s `[[fraction, animName, rootName], …]`. The player
airframes spell two elements, so their stages play against the vehicle root.

**Per-part `injure_anims` (`FUN_004b3d70`)** is the same loop against `part health current / part
health max` (`[part+0x30] / [part+0x2c]` — **also health-only**, and likewise not the combined
progression), over the part's own list at part`+0x3c` with handles at part`+0x4c`. It takes its
context straight from entry`+0x14` rather than resolving a root. It runs on every part-scoped hit.

Both are called from the spend paths, so a single bullet can move both levels at once. A part
reaching zero also plays that part's destroy anim (part`+0x1c`), which is separate from either list.

⚠ **The two levels are keyed on different pools and are not interchangeable.** The player's
def-level list carries `[0.85, player_fuelleak]` and `[0.10, player_smoketrail]`; both are
whole-vehicle-health stages, so `player_smoketrail` means "the hull is at 10%", not "some zone is at
10%". Driving the def-level list off a per-part fraction fires the whole-plane trail while the hull
is still near full. The backlog item this settled is retired; its closing commit is
`git log --grep=BL-246`. Our implementation had three deltas against this (scope, pool and
lifetime); all three are closed, and their record is `git log --grep=BL-384`.

### One start per downward crossing

The handle arrays (inst`+0x890`, part`+0x4c`) are the whole lifetime rule. An entry starts only when
its slot reads zero, and the slot is written with the instance `FUN_004edda0` returns. The slot is
cleared on the **upward** crossing alone (`threshold < fraction`, via `FUN_004ed480`), never when the
anim finishes by itself. So a stage fires exactly once per downward crossing and cannot fire again
until a repair lifts the fraction back over its threshold.

`FUN_004b3e20` (per-part) and `FUN_004b3910` (def-level) are the wipes: each stops every live anim in
its array and zeroes the slots. `FUN_004b8180`, the set-health path behind repairs and cheats, calls
the per-part wipe before re-running `FUN_004b3d70`, so a repair un-stages and then restages from the
new fractions.

Two consequences for a per-part list. Each part carries its own copy of a shared entry, with its own
slot, so an entry authored on all four player zones fires up to four times over a flight, once as
each zone first crosses. And because the fraction is health-only while `FUN_004b7f80` blocks health
damage outright until a part's armour is spent, a fully-armoured part crosses nothing at all: even a
0.99 entry waits for the armour pool. Decoded 2026-08-15 (`BL-297`).

### Which airframe a stage's anim binds to

`anim_root_name` is not a target selector. It names an offset *within* whatever root the caller
hands over, and in the common case it is discarded outright. `FUN_00520910` (clone a template def
into a live instance) and `FUN_00521180` (rebind an existing instance) both take a context **node
pointer** from the caller and branch on one pointer-identity test:

- **`def+0x6c == def+0x48`**, the authored anim root being the def's own root: `inst+0x48` and
  `inst+0x6c` both become the context node, and `name` (`+0x28`) and `anim_root_name` (`+0x4c`) are
  both `strcpy`'d from the context node's name. The authored string is overwritten.
- **`def+0x6c != def+0x48`**, the authored root being a proper descendant: `FUN_004efa70(contextNode,
  def+0x4c)` re-searches the new context's subtree for that name. A miss logs `"Animation node not
  found. Animation: %s; Node: %s"` (`0x00634220`), sets the state byte `+0xa0` to 5, and the
  animation does not play at all.

Which branch a def takes is fixed at load. `FUN_0051dcf0`, the `zrdr` reader, sets `+0x48` and
`+0x6c` to the same node on `ANIMATION_NAME`, then resolves `ANIMATION_ROOT_NAME` (keyword at
`0x00633f30`) through `FUN_004efaf0` at `0x0051e15a`, whose subtree search compares the root node
itself first. So a def whose `anim_root_name` equals its own `name` still holds `+0x6c == +0x48` and
takes the first branch forever. If the root name cannot be resolved at parse time the reader logs
`0x00633e64` and forces the same state by copying `+0x28` over `+0x4c`.

⚠ **A cross-airframe anim therefore needs no retarget.** `piratefighter-pfsmoketrail` is named
`piratefighter` and roots at `piratefighter`, so playing it on a `fury` rewrites both fields to
`fury` and binds the whole anim to that airframe. The mismatch is erased before anything can test
it. What the caller chooses is the context node, not the anim: for `injure_anims`, `FUN_004b3800`
passes the vehicle's own scene node (`inst+0xc`) when the entry's `+0x0c` is zero, and a named
sub-node via `FUN_004d8cf0` otherwise.

The whole player damage-stage family is authored this way, not just the smoke trail. Twenty defs
per chapter carry the NAME `player_pfighter` (`pdpanel1`-`8`, `plane_reset`, `player_fuelleak`,
`player_smoketrail`, `player_damage_trail`, `player_firetrail`, `random_remote_damage`,
`reset_bulletholes`, `bullet1`-`5`), and that name is the Devastator's own player-model root: a
node under the `player` wrapper in `extracted/planes/nodes.json`, holding the same
`geometry`/`cockpit1` children the other ten `player_*` roots hold. Every one of them spells
`anim_root_name` equal to its own `name`, and the extraction shows `anim_ptr == anim_root_ptr` on
each, which is the `def+0x6c == def+0x48` identity the branch tests. **So none of them is
Devastator-specific in effect**: played on any airframe, both fields are rewritten to that
airframe's node. Our rig runtime arrives at the same place from the other side, since a def whose
NAME resolves no anchor falls back to the caller's staging anchor, which is the plane model.

⚠ **`EffectCatalogue.AirframeScopedAnchors`' note is half right and should not be read as a claim
about the definitions.** No chapter gamez ships a `player_pfighter` node (zero occurrences across
all eight), so the constant's own job stands: the name stages no effect template, and it is
place-exempt because on the Devastator it resolves to the aircraft itself. But "inert on the other
ten airframes" is true only of the anchor, never of the def, and the `pdpanel*` / `player_fuelleak`
/ `player_damage_trail` stages already play on all eleven airframes for exactly that reason.

### Where a stage's effects land

Node names in a started anim are bound in `FUN_00521180`, which walks the anim's node tables and
resolves each reference through `FUN_004efa40`. An entry naming an explicit parent is searched under
that parent; everything else goes to `FUN_004efaf0`, which tries, in order, the instance's context
subtree (inst`+0x6c`), the context node (inst`+0x48`), the anim's two local tables (`FUN_004ee7e0`,
`FUN_004ee770`), and finally a global by-name lookup (`FUN_004d0280(7, name)`). The subtree search is
`FUN_004efa70`, a recursive name compare down `+0x56`/`+0x5c`.

⚠ **The context does not redirect a name, it only disambiguates one.** A name that is unique on the
airframe resolves to the same node whichever context started the anim, because a miss in the context
subtree falls through to the global lookup. The `pdpN` panel nodes are unique, so a stage naming
`pdp1` sparks at `pdp1` regardless of which part's list started it. There is no part-relative
retarget on this path. Decoded 2026-08-15 (`BL-297`).

A missing anchor is a **soft** failure, unlike a missing root. When a node reference resolves to
zero after the whole chain, `FUN_00521180` logs the same `0x00634220` message, stores zero in the
slot and carries on, so the anim still starts. What that costs depends on the event: the
`PUFFER_STATE` handler `FUN_004e7e40` reads its attach-node index at `event+0x34` and treats -200 as
"use the event's own coordinates" and -100 as "use the context root" (`inst+0x48`); any other index
with a zero pointer leaves `EAX` zero at `0x004e8277` and `FUN_00550370` writes NULL into the
puffer's parent slot `+0x74`. Combined with the global by-name fallback above, an anchor absent from
the airframe that is playing the anim binds to any node of that name anywhere in the loaded scene
before it reaches the NULL case. Decoded 2026-08-16 (`BL-385`).

### The AI stage anchors exist on ten of the eleven airframes

The two anims an AI `injure_anims` ladder names are authored against the Devastator, and they name
five node anchors between them: `piratefighter-pfsmoketrail` puts its `smokepuffer`/`firepuffer`
pair at `prop1`, and `player_pfighter-random_remote_damage` calls `small_fireball_follow`,
`short_fireball_follow` and `short_fire_follow` onto `railer1`, `lailer1`, `lft_elev` and `rt_elev`.
Because both defs take the total-retarget branch above, those five names are resolved against
whichever airframe the stage is playing on, which makes their presence per airframe the question.

Censused over `extracted/planes/nodes.json` by walking each airframe root's subtree, so the answer
is by node identity rather than by eye. Each airframe ships two models: the `player_*` root the
player flies (and the one CSVM builds for an AI plane too, since `PlaneStats.LoadForAi` keeps the
built model on the player chain) and the bare AI root the original spawns from an `aiv` roster.

| Airframe | `player_*` root | AI root | `prop1` | `railer1` | `lailer1` | `lft_elev` | `rt_elev` |
|---|---|---|---|---|---|---|---|
| Bloodhawk | `player_bhawk` | `bloodhawk` | yes | yes | yes | **no** | **no** |
| Devastator | `player_pfighter` | `piratefighter` | yes | yes | yes | yes | yes |
| Firebrand | `player_fbrand` | `firebrand` | yes | yes | yes | yes | yes |
| Brigand | `player_brigand` | `brigand` | yes | yes | yes | yes | yes |
| Fury | `player_fury` | `fury` | yes | yes | yes | yes | yes |
| Autogyro | `player_autogyro` | `autogyro` | yes | yes | yes | yes | yes |
| Avenger | `player_avenger` | `avenger` | yes | yes | yes | yes | yes |
| Kestrel | `player_kestrel` | `kestrel` | yes | yes | yes | yes | yes |
| Peacemaker | `player_peacemaker` | `peacemaker` | yes | yes | yes | yes | yes |
| Balmoral | `player_balmoral` | `balmoral` | yes | yes | yes | yes | yes |
| Warhawk | `player_warhawk` | `warhawk` | yes | yes | yes | yes | yes |

Both model families give the same answer on every airframe, so the census does not depend on which
model an AI plane is built from. **The Bloodhawk is the single exception, and only on the elevator
pair**: it spells its elevators `l_elev` and `r_elev`, children of `nose` rather than of `tail`, so
`lft_elev` and `rt_elev` resolve nothing on it. Two of `random_remote_damage`'s five cascade steps
therefore have no anchor on a Bloodhawk. `prop1` is present on all twenty-two roots, so
`pfsmoketrail` (the only stage a campaign wingman's one-entry ladder names) always resolves.

⚠ **In the original, a Bloodhawk's two anchorless steps do not go nowhere.** The global by-name
fallback above binds them to any node of that name in the loaded scene, and C3, C4 and C5 each ship
scenery aircraft carrying exactly these names: C3's three `britbalmoral_*`, C4's `anim_warhawk`,
`anim2_warhawk`, `anim2_brigand` and `anim2_autogyro`, and C5's `stihellhound_eg0`. A fireball
called onto a wounded Bloodhawk's `lft_elev` can therefore appear on a parked aircraft elsewhere in
the map. C1, C1B, C1C, C2 and C2B ship none of the five names in their gamez, so there the fallback
can only reach another live aircraft. **This is why "smoke appeared" is not evidence that a stage
anchored correctly.**

Our runtime cannot reproduce that particular mistake. The damage stages play through the per-plane
crash runtime, which is bound to the `FlightController`, so `NameResolver`'s widest tier is that one
aircraft's subtree rather than the world. The cost lands differently instead: `CallTargetSite`
falls back to the caller's anchor when a call's target node does not resolve, so on a Bloodhawk the
two fireballs fire at the airframe root. That is counted (`_retargetUnresolved`) and logged only
under `--debug-anim`. Censused for `BL-385`.

## Death

`FUN_004b82d0` starts the anim reference the def supplied at `+0x158` (instance `+0x6d0`), keeps its
handle at `+0x6d8`, drops the AI's target, marks the instance dead, and picks the kill message.
No vehicle def in `vehicle.zrd` carries a key naming that anim, so the reference is resolved by
name at load: the shipped `zrdr/patrol_boat_destroy.zrd` holds an `ANIMATION_DEFINITION` named
exactly `patrolboat`, and it is that def's sequence (parts thrown clear, `sinker` rolling and
sliding under, `ptboat_slick` fading in) that plays. For the player specifically, `FUN_00476250`
resolves an anim named `player` plus a set of `player_crash_*` variants into the same slot.

`FUN_004b82d0` also sets `+0x91f` when the vehicle's mode class (`+0x67c`) is 0 (jet) or 4
(wingman), registers a native completion callback on the anim it just started
(`FUN_004ee160(anim, &LAB_00480710, vehicle)` at `0x004b84c6`, or `LAB_00470900` in a network game),
and clears node flag `0x10000000` on `vehicle+0xc`. Ground impact runs the same shape from
`FUN_0048b920`, which picks a crash anim from the per-vehicle table `[+0x6e0 … +0x6e4]` **indexed by
the struck material's `+0x20`**, falls back to `+0x6d0` when that index is out of range or its slot
is null, and registers the same callback.

### The kill message

The line is posted only in a single-player game: `FUN_004b82d0` calls `FUN_00440ad0` and takes the
arm at `0x004b84a4` only when `*DAT_0064f750` is 0, so a `Network` session prints nothing. Which
line it is comes off the victim, in four cases, all formatted into the same stack:

| victim | message | format |
| --- | --- | --- |
| mode class (`+0x67c`) neither 0 (jet) nor 4 (wingman), tested at `0x004b850b` | 180 `MSG_DESTROYED`, taken at `0x004b8529` | `0x0062b168` `"%s %s"` |
| the player (`DAT_0071c298`) with a `PlayerName` set (`*DAT_0064f724`, read by `FUN_004409f0`) | 169 `MSG_SHOT_DOWN`, `0x004b8552` | `0x0062b170` `"%s %s"` |
| an aeroplane on the player's own team | 174 `MSG_WINGMAN_SHOT_DOWN`, `0x004b8589` | `0x0062b178` `"%s"` |
| any other aeroplane | 169 `MSG_SHOT_DOWN`, `0x004b85aa` | `0x0062b17c` `"%s %s"` |

The name in the three `"%s %s"` cases is the victim's own `+0x14` string (the block's slot-20
`title`, "Medusa Kestrel"), or `[0x00a20110]` where it has none, which formats as an empty string
and leaves the line with its leading space. `FUN_0059ce40(id)` is the lookup and the ids are
`extracted/messages.json` rows, not `langui` ones.

⚠ **`PlayerName` is a startup global, not the campaign's own field.** `FUN_004113b0` fills
`UIData +0x314` from the registry's remembered player at startup
([../formats/saved-games.md](../formats/saved-games.md), "Which player is current, across runs"),
so an Instant Action sortie flown after a campaign prints that campaign profile's name on the
player's own death, not the wingman line. CSVM feeds the kill line the flying campaign profile's
name, and outside a campaign `CampaignProfileStore.LastPlayedPilotName`, the profile last used;
a store that has seated nobody names nobody, which is the unset case the line already falls
through. The last-used record is machine state, so a `--det` run reads none of it.

The colour is a three-way fork on the victim's team at `0x004b85c9`-`0x004b8621`: a team above 1
takes `DAT_006eba60`, the player's own team takes `DAT_006eba64`, and anything else falls to
`FUN_004587d0`'s default `DAT_006eba5c`. ⚠ All three COLORREF globals read 0 in the image and
nothing in `crimson.exe` writes them, so the shipped colours are not recoverable statically. Only
the side rule is decoded.

`FUN_00458660` builds the stack with four lines (the `4` at `0x00458698`), slot 0 at `+0x1ead8` on
a `0x20b8` stride, in the `hudMsgBrief` font (Arial, height -10, width 6, weight 500, shadow on,
no colour of its own). `FUN_005c55f0` stamps each posted line with 5.0 s (`0x40a00000`) at the text
object's `+0x10`, and `+0xc` bit 0 is its live flag. `FUN_00458a10` places it: x is 0.5 of the
display width (`[0x006032e0]`) with the text object's centring flag `+0x1044` set (read at
`0x005c7e4f`), y is 0.2 of its height (`[0x006034fc]`), and each further slot sits 18 px lower
(`FUN_00458530`'s `+ 0x12`), all in the original's 480-line display pixels. `FUN_00458350` posts:
slot 0's line shifts down carrying its own remaining time and colour, an expired slot 0 shifts
nothing (the `+0xc` test at `0x00458361`), and a line identical to the one already in slot 0 skips
the shift and refreshes that slot instead (the `strcmp` at `0x00458380`, whose `JZ 0x0045847e`
jumps past the shift, not past the write). `FUN_004588e0` splits a line longer than 48 characters
(`0x31` at `0x004588f9`) at its last space, posting the tail first (`0x00458984`) and the head
second (`0x00458995`) so the pair reads top to bottom.

The same stack carries two notices that are not a death. `FUN_0048b920`, the ground impact above,
posts row 162 `MSG_CRASH` "Fatal Crash!" for the local player alone (`param_1 == DAT_0071c298`,
`FUN_0059ce40(0xa2, 0x40a00000, DAT_006eba64, DAT_006eba64)` into `FUN_004588e0`): in that player's
own colour, for the same 5.0 s, and on every impact its aircraft performs, since the post stands
ahead of the wreck-landing test at `+0x91f`. The mission clock's expiry posts rows 6002
`MSG_TIME_EXPIRED` and then 137 `MSG_MISSION_LOST` through `FUN_004587d0`, so both take the default
colour and "Mission LOST!" reads above "Time Expired"; the second is skipped on a mission already
won, and the whole arm is single-player only. That site is
[../formats/objectives.md](../formats/objectives.md), `MISSION_TIMER`.

`Flight/Hud/HudMessages.cs` carries all of this, with the 18 px pitch and the font's own 10 px cell
taken onto `HudMetrics`' 1440p reference by the factor of three between 480 and 1440 lines. Its
three colours are the remake's HUD palette standing in for the undecodable globals, marked TUNE.

⚠ **A live aircraft flown into the ground reaches `FUN_0048b920` alone**, which posts no kill line:
only the health-zero routine `FUN_004b82d0` words one, and `FUN_0048b920` calls neither it nor
anything that reaches it. CSVM reports both deaths through one `Downed` seam, so the kill line is
gated on the hull having been spent rather than on the report itself.

### What happens to the wreck

⚠ **Two anim slots, two moments.** The slot at `+0x6d0` is the DESTROY anim and starts the instant
health reaches zero; the table at `[+0x6e0 … +0x6e4]` is the GROUND-IMPACT anim and starts when the
falling wreck lands. Reading `ai_crash_*` as the death anim collapses the two and deletes the whole
fall: the aircraft vanishes on the kill instead of burning its way down. The destroy anim is
resolved by NAME, and the name is the airframe's own, so it is the self-named def every airframe
ships (`fury-fury`, `kestrel-kestrel`, `player-player`), never a `*_crash_*` one.

`fury-fury`'s `destroy_craft` sequence is the choreography: `large_fireball` with `air_mixed_exp_sg`
(the airburst), `large_firetrail` (the trail the wreck wears down), `chuteman` at **3.0 s** (the
pilot's parachute), eight `ObjectActiveState` events swapping healthy for destroyed, `Callback 16`,
`Callback 15`, then `CallSequence randomdestseq`, a second `large_fireball`/`plane_destroy_sg` pass
with `call_trailburst` and the `ObjectMotion` that actually flies the hull down. Its own
`destroyed_dirt`, `destroyed_water` and `bounce_effects` sequences carry the landing.
`has_callbacks` is **true** here and false on the three `ai_crash_*` defs. ⚠ It is not the tell for
which family a def belongs to: the player's three `player_crash_*` defs also carry it true, and each
authors a `Callback` of 12, a code the vehicle-death handler does not take. The authored VALUE is
what decides what a callback means, never the def it sits in.

**The two slots never both fire on one event, and which one carries the landing is authored per
family.** `randomdestseq`'s `ObjectMotion` names `MAIN_ROOT_NODE` with `impact_force`, gravity
-9.8, `do_intersections`, `run_time 20` and `bounce_sequence { default: bounce_effects, water:
destroyed_water }`, so on all eleven airframe defs the destroy anim takes the hull over and owns
the last of the fall and the ground explosion; from the handover on the vehicle no longer moves
itself, so `FUN_0048b920` sees no contact and the `ai_crash_*` table is left to what it is for
(that, and a wreck that reaches the ground inside the first three seconds), a LIVE aircraft flown into
terrain. `player-player` authors no hull `ObjectMotion` at all, only `piece1seq`..`piece4seq`,
each with its own `pNgrndhit` bounce, and its `Callback 15` is untimed, but it sits in
`destroy_craft`, which BOTH arms reach through a `WAIT_FOR_COMPLETION` call of `cpeject1`/
`cpeject2`. The handover therefore waits for the cockpit eject to finish, about five seconds, and
the player's hull flies itself until then exactly as an AI wreck does for its three; what falls
after it is the four pieces. A player wreck that reaches the ground inside that window does take
the crash table. `player-player_crash_dirt` leaving `destroyed`
active with `large_10sec_fire` on it is for the other case that def family serves, a live player
flown into terrain. Wiring both families to the kill would play the ground explosion twice.

**Nothing removes a destroyed vehicle.** There is no timeout, no distance cull, no count cap and no
recycling on the death path. The wreck stops being visible when the GROUND-IMPACT anim switches its
nodes off, and the object stays allocated, dead and hidden, until the mission tears down.

The `ai_crash_default` / `_dirt` / `_water` defs (the `ai_crash_` prefix is at `0x00627d40`, resolved
by `FUN_00478a00`; the player's `player_crash_` counterpart is `0x00627cf8`, resolved by
`FUN_00476250`) carry `has_callbacks: false` and no timed event at all: `ObjectActiveState` false on
`dontmove`, `markers`, `healthy` and `destroyed`, a sound, and one or two `CallAnimation` effects.
The aircraft is fully hidden on the frame the crash anim dispatches, and what remains visible
(`flydirt_plane`, `call_car_trails`, `plane_big_splash`) is separately-instanced anims anchored at
the crash point, owned by the anim system and expiring on their own sequences. ⚠ The **player** is
authored differently: `player-player_crash_dirt` leaves `destroyed` active and plays
`large_10sec_fire` on it, so a player dirt crash does leave a burning hulk. The difference is
entirely in the data; both take the same code path.

### The player's own destroy choreography

`player-player`'s `destroy` opens on `If NODE_ACTIVE 1`, and entry one of the def's own node list is
`player_autogyro` on all eight chapters, so the condition is an AIRFRAME TEST: only the autogyro
takes the arm that stops and sheds its rotor (`autogyro_stoprotor`, `autogyro_loserotor` at
`Event 0.15`), always with `cpeject1`. Every other airframe falls to `random_destroy`, whose own
`If RANDOM_WEIGHT 0.5` picks `cpeject1` or `cpeject2`. Both arms then reach `destroy_craft` through
that eject call's `WAIT_FOR_COMPLETION`, so the breakup waits for the pilot to leave.

`cpeject1`/`cpeject2` are the EXTERIOR bail-out: hide the airframe's seated `pilot` (under
`healthy/geometry/…/pilot_pos`), show `cpilot`, a parentless root of `planes.zbd` holding an
articulated pilot, `cpilot_parent > cpilot_drop > cp_torso`/`cp_head`/twelve limb nodes, reparent
it onto `pilot_pos`, and drive every joint from the def's own SI scripts. `cpeject1` adds
`snd_DA-Bail-A_id1_random` at 0.5 s and runs about five seconds; `cpeject2` is the shorter variant.
`cpejectstop` is the teardown pair, `ObjectDeleteChild` plus a hide.

`rem_pas`, called first in both arms, is the COCKPIT-INTERIOR crew: it hides the nine named
passenger characters (`p_waldo`, `p_spks`, `p_pick`, `p_jack`, `p_bjon`, `p_fas`, `p_ilsa`, `p_ub`,
`p_swan`) and detaches `apassengers` from `pass_st`. Its anim root is `apassengers`, a chapter-gamez
node, not an airframe one, so it has nothing to do with the multi-crew airframes and nothing to
render outside a cockpit view.

### A dead aircraft keeps flying itself until `Callback 15`

⚠ **`+0x91f` is what keeps a shot-down aircraft moving, and code 15 is what stops it.** The
per-vehicle update `FUN_004897c0` runs a vehicle when `+0x91d == 0 || +0x91f != 0`, so setting both
on death (`FUN_004b82d0`, for mode class 0 jet / 4 wingman) keeps the dead aircraft ACTIVE: the
movement dispatcher `FUN_00489ea0` carries the same test and still calls the aircraft integrator
`FUN_0048e580`, while the AI think (`FUN_0041f810`/`FUN_0041c270`) and the weapon loop are skipped
because each is gated on `+0x91f == 0`. `LAB_00480710`'s code-15 arm clears `+0x91f` at `0x00480774`,
and from the next frame the vehicle falls out of the update and stops moving itself.

That is why `Callback 16`, `Callback 15` and `CallSequence randomdestseq` sit in that order behind
the 3.0 s chute gate on all ten AI airframes: **16 samples the velocity the wreck has reached after
three seconds of falling on its own, 15 hands the hull over, and the `ObjectMotion` flies it from
there.** `player-player` authors the same three untimed, but reaches them only through a
`WAIT_FOR_COMPLETION` call of the cockpit eject, so a dead player's hull flies itself for the
eject's own length instead. ⚠ Nothing about this is `start: null` semantics, an absent `start` is `Animation 0.0`
and always passes, so the events behind a timed one fire at that timed event's time
(`FUN_004ecbb0`, and [anim-definitions.md](../formats/anim-definitions.md)'s "Event scheduling").
The fall before the parachute is the VEHICLE's, not the anim's.

`LAB_00480710`, the callback, handles three event codes. **16** pushes the vehicle's velocity into
the anim instance through `FUN_004ee0e0`, which is how a wreck inherits the aircraft's motion.
**15** clears `+0x91f` and calls `FUN_0047b9c0`, stopping the damage-stage anims and `start_anims`,
which is where an injure-ladder smoke trail ends. **0** is the delete arm: it frees the vehicle
(`FUN_004b0aa0` then `operator_delete` at `0x004807e3`) when node flag `0x8000000` is set, and
otherwise only sets flag `0x10000000` and returns.

**Every other code reaches the mission-script handler, and only for the player's own vehicle.**
`LAB_00480710`'s tail at `0x004807f6` compares the callback's vehicle against `DAT_0071c298` (the
player's) and forwards `FUN_0047e080(anim, vehicle, code)` when they match, so a code the three arms
above do not take is a no-op on every AI aircraft. `player-player`'s `Callback 3` is the case that
matters here: `FUN_0047e080`'s case 3 reads the camera manager `DAT_0064ef78`'s mode at `+0x14c`,
calls the mode setter `FUN_0042c280(0)` when it is not already 0, and clears the two view
accumulators `DAT_0064ef60`/`DAT_0064ef64`. It is a CAMERA command: leave whatever view the player
was in (modes 6 and 7 are the cockpit views) for the default external one, and drop the free-look
pan. Code 15's own arm does the same one line up, setting mode 8, the death camera.
`FlightController.Destroy` already cuts from Cockpit or Nose to CSVM's crash vantage immediately;
applying the later code 3 would incorrectly leave that crash view, so it stays counted.

Code 0 is never authored. The authored `Callback` handler is `FUN_004ec5e0` and passes the event's
own value; code 0 is emitted only by `FUN_004ebbb0`, which tears an anim instance down, and no
compiled anim def in the install authors a `Callback` of 0. The extraction holds 736 `Callback`
events over 28 distinct values, and 0 is not among them. Flag `0x8000000` likewise has exactly
one writer, `FUN_0047bab0` at `0x0047bc93`. So the free is a handshake between the callback and
`FUN_0047bab0`, the removal function, which frees immediately when `0x10000000` is set (no death
anim outstanding) and otherwise defers by setting `0x8000000`. **The trigger is always a call to
`FUN_0047bab0`, never the crash.**

Its callers are mission teardown (`FUN_00472c40` drains the whole vehicle list), the ambient
plane pool below, an objective trigger (`FUN_00465b40`) that explicitly skips dead vehicles, and the
player's change-aircraft command (`FUN_0047fd50`). A mission-roster aircraft is on none of those, so
its wreck persists for the mission. The live-vehicle count `DAT_0071dac0` is never compared against
a maximum.

`FUN_004533c0` is the one recycling mechanism: a fixed-size pool of ambient or reinforcement planes
(named `"%s_re%d"`, `0x00624d54`, spawned on a random bearing around the player at radius `pool+0x8`
and minimum altitude `pool+0xc`). It removes a slot's occupant when the vehicle is dead or further
than `pool+0x14` from the player, then respawns into the freed slot once the mission clock passes
`pool+0x24`, re-arming to `clock + pool+0x20`. Those bounds are per-pool data, not code constants.

The player and an AI plane run the same lifetime code, splitting only on which crash table was
resolved at load. The player-only branches in `FUN_004b82d0` and `FUN_0048b920` are announcements
and mode changes; the player's object is reset in place (`+0x91d` written back to zero in
`FUN_004735b0`, `FUN_00480480` and `FUN_0047fd50`) rather than freed. Decoded 2026-08-16 (`BL-385`).

`FUN_0048ad20`, the terrain-conform update for ground vehicles and ships, carries a health path of
its own: while one global flag is set it kills the vehicle outright, and while a second is set it
subtracts `0.5 * health max` per second and kills at zero. Both flags are cleared each pass by the
update itself and are set from inside the terrain query, so this reads as a terrain or obstacle
collision penalty. **What sets them is not decoded** and this page does not claim more than that
the drain exists and is expressed as a fraction of max per second.

## What this settles for the patrol boat

The question was which of two shipped numbers is the boat's hit points: `vehicle.zrd`'s
`patrolboat` (`armor 0`, `health 40`, stages at 0.60 and 0.30 firing `ptboat_50damage` and
`ptboat_75damage`) or an anim def's `HEALTH 20` with `ANIM_HEALTH` stages at 12 and 6. They agree on
the stage fractions and differ by exactly 2x on the total, which is what made it look like one
object described twice.

**Both are live, on different boats, and nothing in the executable knows a boat from a water
tower.** There are three shipped defs, not two, and which one an instance runs under is decided
entirely by how that instance was created:

| Def | Binds to | Model |
|---|---|---|
| `vehicle.zrd`'s `patrolboat` | an `aiv` roster entry, spawned as a vehicle | this page: 40 health, 0 armour, `injure_anims` at 0.60/0.30 |
| `C1/zrdr/patrol_boat.zrd`, `NAME ptboat*`, `HEALTH 20`, with a `DAMAGE_SEQUENCE` | placed world nodes matching the wildcard | the destructible model in [`formats/destructibles.md`](../formats/destructibles.md) |
| `zrdr/patrol_boat_destroy.zrd`, `NAME patrolboat`, `HEALTH 20`, no `DAMAGE_SEQUENCE` | the unparented prototype node | neither: it is the vehicle's death animation |

**The AI boat is a vehicle and reads 40.** `health 40` reaches health max through `FUN_00475820`;
`injure_anims` `[[0.6, ptboat_50damage], [0.3, ptboat_75damage]]` is the list `FUN_004b3800` walks
against the whole-vehicle fraction; death is the whole-vehicle test in `FUN_004b9bc0`. 19 `aiv`
blocks name a `patrolboat` (12 in C1/M05, 4 in **C1B/M03**, a mission the earlier survey missed, 2
in C5/M01, 1 in C2/M01), and every one carries `init_health 0.0`, `-1` in all eight zone slots and
`-1` for `armor`, so `FUN_0047c210` overrides nothing. Being a ship it takes the difficulty scale
(**35 / 40 / 50**) but not the aircraft jitter.

**The placed boat is a destructible and reads 20.** C1's refinery has three: `ptboat1`, `ptboat2`
and `ptboat3`, children of `refinery.flt` with real translates (nodes 3015 / 3029 / 3043), each
compiled into `C1/cam_anim` as `health 20.0`, `activation WeaponHit`, carrying the wildcard def's
`DAMAGE_SEQUENCE` (`ANIM_HEALTH 12` -> `sputter_black_smoke_obj`, `ANIM_HEALTH 6` ->
`sputter_fire_smoke_obj`, the generic shared effects rather than the boat-specific pair). These are
the boats visible in C1's freecam chapter, and they are shootable exactly like any other
destructible.

**The prototype is neither.** The `patrolboat` node in each chapter's `gamez` has no parent and an
identity transform (C1 2689, C1B 620, C2 4930, C3 1550, C5 8042; C5's `t_truck` at 7565 likewise),
so it is not in the scene graph and cannot be hit. What is compiled onto it, in exactly the four
missions that have roster boats, is exactly the anim list the **vehicle** def names:
`emit_ptsplash1` and `emit_ptsplash2` (its `start_anims`), `ptboat_50damage` and `ptboat_75damage`
(its `injure_anims`), and `patrolboat`/`healthy` (its death sequence). That correspondence is the
cleanest confirmation that the vehicle path is what consumes them; the `health 20.0` on the death
entry is a field inherited from the source def and nothing reads it.

⚠ **So the answer depends on which boat, and the earlier "the vehicle def governs the AI combatant,
the anim def governs placed scenery" reading was right.** For a scenery-only scope it is 20, from
the destructible; for mission play with a roster it is 40, from the vehicle. C1B's dock boat
(`patrolboat.flt` under `boat_at_dock`, node 669) is a third case: placed, but with no compiled def
of any kind, so it is inert geometry.

## The same question for aircraft

`vehicle.md` recorded that 11 AI defs resolve **both** a whole-vehicle `armor`/`health` pair and
their own `destroyable_parts`, and left it open which one an AI combatant spends. The answer is
both, in a fixed relationship: **the parts are the ledger and the whole-vehicle pair is a running
summary of them**, recomputed as a fraction of max on every part-scoped hit (`FUN_004b3bf0`), while
a hit that names no zone spends the summary directly. Death, the def-level staging and the AI's
damage reactions all read the summary. So a Bloodhawk's `armor 64 / health 64` is not an alternative
to its 4x20/20 zones; it is the scale the zones are expressed in.

The two other airframe-only behaviours are the ones above: enemy aircraft take the same difficulty
scale as the boat, and aircraft alone additionally take the +/-5% per-spawn jitter on both pools
(and on nine other def numbers), which is why two Bloodhawks on the same tier are not identical.

## What the remake builds on this

The remake represents multi-zone damage across its
three consumers: aircraft, zeppelins, and the static world destructibles. The decision, justified
against this decode:

**The two systems stay two systems, and `DestructibleRegistry` gains no zone concept.** The
original runs two disjoint damage models selected by how an instance was created (the scope note
above), and their semantics share nothing: a destructible is one scalar pool with absolute
`ANIM_HEALTH` thresholds and a one-way death; a vehicle is two ledgers with an armour pool, 1:1
overflow, fraction-keyed reversible staging, and spawn-time scaling. Folding zones into
`Mech3/DestructibleRegistry.cs` would graft the vehicle model onto a registry whose `(def, anchor)`
scalar instances and monotonic `DamageStage` are correct for what they cover. The registry keeps
the static destructibles only; vehicles keep their own damage component.

**(a) Aircraft: `Flight/Airframe/PlaneDamage.cs` is the vehicle ledger and grows the summary pair.** It
already holds the per-part half of this page's model (four zones nose/tail/left/right, each an
(armour, health) pair, armour first with 1:1 overflow, confirmed by CAP-19). What it lacks is the
decoded whole-vehicle summary: after a part-scoped spend, whole-vehicle current health and armour
are recomputed as the parts' fraction of their summed maxima times the whole-vehicle maxima, and a
hit that names no zone spends against the summary directly. An AI airframe's `armor`/`health` pair
is the scale that summary is expressed in, not a competing pool; player defs resolve none, so the
open thread above (the initialiser) stands. Kill threshold: **whole-vehicle health current at or
below zero**, *corrected 2026-08-14: NOT "every zone exhausted". The wrapper loop
(`FUN_004b9b30`, the correction section above) re-enters the unabsorbed leftover zone-less and
drains the whole pair directly, so the kill can arrive with zones still healthy; and a dead zone
is never struck, the resolver redirects the hit to a surviving zone. The whole pair is a real,
independent pool, not only a running summary.* ⚠ The remake today
kills on any `critical` part reaching zero (`FlightController.cs:823`, `:1957`); no code on the
decoded death path reads that flag (the open thread below), so the current rule is a recorded
divergence. D14 retires it when it lands the damage routing, keeping the flag parsed; if a part
kill is ever observed at the controls of the original, that observation reopens the flag question,
not this decision.

**(b) Zeppelins: an aggregator over per-part scalar pools, not a zoned registry instance.** The
decoded kill check walks the `healthy` node list, counts entries still active, and kills when
`survivors < num_healthy_required` ([`formats/mission-entities.md`](../formats/mission-entities.md);
default 1, clamped to the `healthy` list length; the survivor polarity must be asserted in a test,
because the inverse reading is an immortal zeppelin). Gasbags, engines and cannons are each their
own node with their own pool and destruction anim, which is exactly the registry's existing
`(def, anchor)` scalar shape once the sub-part defs anchor at all. The implementation: (1) the
deliberate `NameResolver.Anchors` change that lets zeppelin sub-part defs anchor; (2) pool seeding
from the mission record (`gasbags` hp 80-400, `cannon_health` hp 200) where authored; (3) a
per-zeppelin aggregator component, fed by the mission's `zeppelins.json` record, that counts
surviving `healthy` nodes and owns the kill, plus the engine-loss square root over the `engines`
list. No `(def, anchor, zone)` keying is needed anywhere, because each zone is already its own
instance; the zeppelin-level threshold lives in the aggregator, not the registry.

**(c) Static destructibles: unchanged.** Scalar `HEALTH`, `DAMAGE_SEQUENCE`, death at zero, per
[`formats/destructibles.md`](../formats/destructibles.md). This page's scope note is the reason:
the executable itself never unifies the two models, so a unified remake registry would be an
invention.

What each downstream item consumes:

- **F18** takes (b) whole: the anchor change, the seeding, the aggregator and its survivor
  threshold, and the `WeaponDef.DamagesZeppelin` gate (parsed, never consumed today) as the
  routing flag for what may hurt a gasbag.
- **D14** takes the hit contract from "Taking a hit" *as corrected 2026-08-14*: every hit is a
  pair (armour damage, health damage) plus an optional zone; zone hits spend the part then
  recompute the summary, THE LEFTOVER THEN RE-ENTERS ZONE-LESS and drains the whole pair
  directly; a dead zone redirects to a surviving one. The remake's zone supplier is
  `PlaneDamage.MapStruckPart` standing in for `FUN_004b3950`'s geometric half (untraced); the
  resolver's live-only rule and random-survivor fallback are ported exactly. D14 also retires
  the `critical` kill divergence above.
- **A2's spawned aircraft** seed the ledger exactly as "Where the numbers come from at spawn":
  the roster-named def chain's `armor`/`health`, and **no `destroyable_parts`, because no such
  chain authors any** (see the 2026-08-16 correction below; this bullet said "the `r*` def chain's
  `destroyable_parts` plus `armor`/`health`" and was wrong on both counts), then the roster's
  `init_health` (only if > 0) and `armor` (if >= 0). The eight per-zone roster slots are parsed for
  index alignment and ignored (`-1` on all 414 blocks; the sum-derivation path is dead in this install).
  The difficulty scale (0.75/1.0/1.25) and the aircraft-only per-spawn jitter (uniform 5 %) are
  decoded constants to apply when a difficulty setting exists, not TUNEs.
- **G21** keys its crash choreography off the vehicle death event (the whole-vehicle kill raising
  `FlightController.Downed`), never off `DestructibleRegistry`.

One contradiction with the plan and the design document, recorded rather than smoothed over: the
design's unified damage-zone model (every object divides into critical/non-critical zones and dies
when a threshold count of critical zones dies) is design-era. The shipped engine has **two kill
rules and no shared zone vocabulary**: vehicles die by sum-exhaustion of their zones, zeppelins by
the survivor count, and the design's own aircraft example (1 of {tail, nose, wings} critical) is
refuted by the decoded death path. The earlier "Damage zones" reading and its
architecture-constraints bullet proposing `(def, anchor, zone)` plus a threshold counter inside
the registry are superseded by this note.

## Correction (2026-08-14): the take-hit wrapper loop

The 2026-08-13 pass read `FUN_004b9bc0` as the take-hit entry point. It is not: `FUN_004b9b30`
wraps it, and the wrapper LOOPS. An at-the-controls report against the remake (an enemy Fury
absorbing nine HE rockets) prompted the re-read; everything below is instruction-level, same
method as the rest of this page. This section partially corrects "Taking a hit" step 5 and
"What the remake builds on this" below.

- **`FUN_004b9b30` loops the leftover.** First pass: a caller supplying no part id has the
  struck zone resolved by `FUN_004b3950`, and `FUN_004b9bc0` runs with it. The wrapper then sets
  part id := -1 and calls again while BOTH leftover damage values are still positive and
  whole-vehicle health (`+0x2d0`) is above zero. Every later pass is therefore the zone-less
  route, the whole-vehicle spend `FUN_004b8070`, with NO recompute behind it.
- **The spend writes its leftovers back.** `FUN_004b7f80` takes the damage pair in/out. The
  armour pool absorbs what it can of the armour damage (`FUN_004b7f30` clamps the pool at zero)
  and the unabsorbed armour damage is written back. Armour covering the armour damage outright
  ZEROES the health damage and ends the hit; so does armour standing against a hit that carries
  no armour damage at all. Otherwise the uncovered share of the health damage reaches the health
  pool, and the write-back is the full health magnitude minus what the pool absorbed, both the
  pool's overflow AND the armour-shielded share re-enter the loop, where they meet the whole
  pair.
- **A dead zone is never struck.** `FUN_004b3950` matches the hit geometry only against parts
  with health remaining, and its miss fallback `FUN_004b3b60` picks with `rand()` among the
  first up to three surviving parts. A hit aimed at a dead zone is REDIRECTED to a surviving
  one; only with no survivor does a hit run zone-less from the first pass, and by then the
  recompute has already written whole health to zero.
- **Net behaviour.** The struck zone absorbs what it can; the leftover drains the whole pair,
  gated once per pass by whatever whole armour the last recompute left standing. Concentrated
  one-bearing fire kills because the redirect walks the surviving zones down; a large warhead
  kills through the overflow with zones still healthy. Death stays the single test, whole
  health current at or below zero, but the overflow can reach it with parts alive, so
  "every part exhausted" was sufficient, never necessary.
- **The recompute quirk, kept.** A later part-scoped spend recomputes whole current from the
  parts (`FUN_004b3bf0`) and OVERWRITES earlier zone-less dents, a partial heal. The engine's
  own arithmetic does this; the remake reproduces it rather than fixing it
  (`PlaneDamageTests.ALaterPartSpendOverwritesAnEarlierOverflowDent`).
- **Whole-pair seeding in the remake.** Where the def chain authors `armor`/`health` (the AI
  defs) that pair is the whole maxima; player defs author none, so the remake seeds the pair as
  the sum over parts, `FUN_0047bd90`'s re-derivation is the decoded precedent for
  sum-over-parts as the whole pair. The player-initialiser open thread below stands; this is a
  documented stand-in, not a decode. Measured: player_bhawk seeds 80/80 (4×20/20), the Fury
  90/90 (25+25+20+20). ⚠ That 90/90 is the parts sum and nothing else: the AI `fury` def authors
  **72/72**, and 90/90 belongs to `bswingman`. The AI `bloodhawk` likewise authors 64/64 against
  the player parts' 80/80. Since 2026-08-16 an AI spawn reads its own def and gets the authored
  pair (`PlaneStats.LoadForAi`), so this sum-over-parts stand-in is the **player** path alone.

What this landed as: `Flight/Airframe/PlaneDamage.cs` carries the whole pair, the resolver redirect, the
verbatim spend and the wrapper loop; the `air-to-air` suite pins the one-bearing kill (80 rounds
of `wep_00`) and the rocket kill (a Fury falls to 5 head-on `wep_06` rockets against the
reported 9-rocket sponge); `PlaneDamageTests` pins the arithmetic including the overflow kill
with three zones healthy.

## Correction (2026-08-16): a spawned AI aircraft has no zones at all

The 2026-08-13 pass read the AI spawn path as seeding from the `r*` def chain. It does not, and the
error mattered: it made an AI aircraft look like a zoned vehicle with an authored summary pair,
when it is a **zone-less** one. A census of the shipped data settles it, method stated so it can be
re-run: resolve the `kind_of` chain of every def named by all 414 `aiv` blocks across the 53
`aiv.zrd.json` files, against all 75 defs in `vehicle.zrd.json`.

- **No roster block names an `r*` def.** Enemies are named by militia variants (`secfury`,
  `bhatwarhawk`, `habloodhawk`, `blakepeace`) or by a bare AI def directly (`devastator` on 36
  blocks, `bloodhawk`, `autogyro`, `balmoral`). The `r*` family is the **remote-player** family,
  `formats/vehicle.md` names it that, and it carries `thirdp`-only turrets to match.
- **Not one roster-named chain resolves `destroyable_parts`.** `secfury → fury → basic_airplane`
  carries `armor 72 / health 72` and the seven-entry injure ladder, and no zones anywhere.
- **The 38 militia variants author no damage key at all**, no pair, no parts, no ladder, so the
  bare AI def is the whole damage answer for any of them. The eleven `w*` wingman defs likewise.
  Only `bswingman` (90/90, a one-entry ladder) and `wingman` (100/100) differ, and both are
  campaign defs.
- **Exactly 37 of the 75 defs author any damage data**: the 11 player `p*` (4 zones, 2-entry
  ladder, no pair), the 11 `r*` (4 zones, 7-entry ladder), the 11 bare AI defs (a pair, a 7-entry
  ladder, 8 on the balmoral, and no zones), plus `bswingman`, `wingman`, `patrolboat` and
  `t_truck`.

So the whole-vehicle pair is not a summary over a zone ledger on an AI aircraft; it **is** the
ledger, and every hit takes `FUN_004b9bc0`'s zone-less route to `FUN_004b8070` from the first pass
rather than only after the wrapper loop's first iteration. The authored pairs: autogyro 60,
bloodhawk 64, peacemaker 68, fury 72, avenger 76, devastator 80, brigand 84, kestrel 84, firebrand
88, warhawk 96, balmoral 100.

What this landed as (BL-386): `PlaneStats.LoadForAi` resolves the AI def for the damage trio while
everything else, `DefName`, dynamics, turrets, the built model, stays on the player chain, since
`DefName` keys the eleven-entry stock-loadout table. The `ai-plane-defs` suite pins all eleven
airframes and a real spawn.

## Open threads

- **The `critical` flag** on a `destroyable_parts` entry is documented as "the plane is destroyed
  when this part reaches 0 HP", from the flag's name. No code on the death path reads a part flag:
  `FUN_004b82d0` has four callers and none of them is a per-part check, and `FUN_004b3bf0` only
  plays the part's destroy anim. Under the recompute, one zone at zero leaves the summary at 75%.
  Either the flag is consumed somewhere not yet found or the reading is wrong; it is not settled
  here.
- **Who supplies the zone id** a hit is matched against (`FUN_004b9bc0`'s parameter, against part
  `+0x34`) is on the hit-detection side. Partially resolved 2026-08-14: a caller passing -1 has
  it resolved by `FUN_004b3950` (live parts only, random-survivor fallback); that function's
  geometric matchers (`FUN_004b39d0`/`FUN_004b3aa0`) remain untraced.
- **What sets the surface-vehicle drain flags**, as above.
- **What `DAT_00654120` gates.** It branches the death and ground-impact paths at `0x004b845b`,
  `0x004b84d3` and `0x0048ba95`. Its only writer is `FUN_00450550` (sets 1, never cleared), and the
  airframe tuner readout in `FUN_00492040` runs only while it is 0, so the death path above is read
  as the normal-play path. The mode `FUN_00450550` enters was not identified. If it is instead 1 in
  ordinary flight, the **player** takes neither the destroy anim nor the callback registration; the
  AI behaviour is unchanged either way.
- **What resets the player's destroy-or-crash anim in ordinary single-player death.** Callback 12,
  the mission-over trigger, is the last event of the top-level `reset_state` list of four defs
  (`player-player` and the three `player_crash_*`), once each, right after
  `CallAnimation player_destruction_reset`; the eight occurrences per def in `cam_anim/player-*.json`
  are the eight chapter copies of the same authoring, not eight events. It fires when that anim is
  RESET while the death flag `+0x91d` is set and
  the wreck-falling flag `+0x91f` is clear (the guard at `0x0047e1fa`/`0x0047e208`); the multiplayer
  respawn path `FUN_00480480` clears both flags first, at `0x0048049e`..`0x004804b3`, which is why
  its own reset is swallowed. Which native call performs the reset on the single-player path was not
  identified; the candidates are `FUN_004ed480`'s callers and the `FUN_004ebbb0` teardown reached
  from `FUN_0048b920`. The ending itself is decoded in
  [`../formats/objectives.md`](../formats/objectives.md), "Win and loss".
- **Whether a dead, hidden vehicle is still ticked.** `FUN_004b82d0` calls `FUN_004cd2a0(node, 0)`,
  the crash sequence deactivates the nodes, and `FUN_0048c470` has a `crashed` early-out, but the
  per-frame consumers were not enumerated, so the standing cost of an accumulated wreck is unknown.
- **What a puffer with a NULL parent slot renders as** (model origin, world origin, or suppressed).
  The NULL store at `0x004e8277` / `FUN_00550370` is confirmed; the emitter-side consequence is not.
- **The compiled-archive anim loader's initialisation of `+0x48` / `+0x6c`.** The retarget rule above
  was read out of the `zrdr` reader `FUN_0051dcf0`. For defs whose `anim_root_name` equals their
  `name` both fields must resolve to the same prototype root either way, but that the compiled path
  enters with `+0x6c == +0x48` is inferred from the data, not read from its loader.
