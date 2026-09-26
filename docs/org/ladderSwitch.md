# The rope-ladder switch, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), settling `BL-526`, the CM07 rope ladder that never
deployed. Every claim names the function or address it came from; no decompiler output is
reproduced. The port is `src/Session/Campaign/LadderSwitch.cs` (the rule and the state machine) and
`src/Session/Campaign/LadderSwitchRuntime.cs` (the world binding).

**Where the neighbours live.** The two authored definitions the switch starts, `drop_ladder` and
`retract_ladder` in `C1/M02/zrdr/ladder.zrd`, and the pickup sensor table `pickups.zrd` are
described in [`../formats/anim-definitions/cutscenes.md`](../formats/anim-definitions/cutscenes.md).
The orientation matrix the attitude test reads is decoded in [`flightModel.md`](flightModel.md)
"Bank coupling". This page is what the original does with those: the per-frame rule, the state
machine and the callback that settles it.

⚠ **This page is a decode, not a proposal.** Where it disagrees with an observation at the
controls or a screenshot, the decode wins and the disagreement is a note.

## Nothing authored ever calls the ladder

No `.zrd` in `C1/M02` or in the shared chapter `landings.zrd` authors a `CALL_ANIMATION` of
`drop_ladder`; the name appears only as its own definition and as a `STOP_ANIMATION` /
`INVALIDATE_ANIMATION` target in the pickup-completion definition. The string `"drop_ladder"`
(`0x00627b64`) has one code reference in the whole image, in the mission initialiser
`FUN_004735b0`, and `"retract_ladder"` (`0x00627b70`) sits beside it. The ladder is driven by a
native object, not by the animation data.

`FUN_004735b0` is the ordinary per-mission initialiser (`FUN_00464680`, the mission load, calls
it under the `StructsMissionInit` progress label for every mission), not a C1/M02-only function.
What is C1/M02-specific is the authored data the object resolves: every other mission builds the
same switch, finds no definitions and no sensors, and the switch idles.

## The switch object

`FUN_004735b0` allocates a 20-byte object through `FUN_004456f0` and stores it in the global
`DAT_0071c324` (`0x00475517`); the mission teardown `FUN_00472c40` frees it (`0x00472c96`). Its
layout:

| offset | meaning |
|---|---|
| `+0x0` | vtable `0x00608084` |
| `+0x4` | state: 0 retracted, 1 deployed, 2 deploying, 3 retracting |
| `+0x8` | the `drop_ladder` definition, resolved by name through `FUN_00523820`, set by `FUN_00445500` |
| `+0xc` | the `retract_ladder` definition, resolved the same way, set by `FUN_00445540` |
| `+0x10` | the `ladder_pos` node (`FUN_00445760` on the string at `0x00623cc0`) |

The two setters also register the switch as each definition's `CALLBACK` host: `FUN_00445500`
calls `FUN_004ee160(def, LAB_004454e0, this)` and `FUN_00445540` calls
`FUN_004ee160(def, LAB_004454f0, this)`. `FUN_004ee160` writes the callback function and its
context into the definition at `+0x74`/`+0x78`, the same per-definition registration slot every
other host uses. The thunks at `0x004454e0` and `0x004454f0` forward the raised code into vtable
slots 0 and 1.

The vtable at `0x00608084`:

| slot | routine | does |
|---|---|---|
| 0 | `0x00445660` | if the code is `0x7b` (123), `FUN_004454c0`: state = 1 (deployed) |
| 1 | `0x00445680` | if the code is `0x7b`, `FUN_004454d0`: state = 0 (retracted) |
| 2 | `0x004456a0` | start the drop: reads the `ladder_pos` node's position (`FUN_004cf200`) and starts the `+0x8` definition through `FUN_004edc10` with that position as its operand |
| 3 | `0x004455c0` | start the retract: `FUN_004edda0(def, 0, 0, 0, 0)` on the `+0xc` definition |

So the `CALLBACK [VALUE 123]` both definitions author (2.1 s into `drop_ladder`, 1.9 s into
`retract_ladder`) is the switch's own settle signal, and code 123 has no meaning in the
mission-script host because it was never meant for it.

### The two transitions

`FUN_004455e0` is "deploy" and `FUN_00445620` is "retract"; both are `__fastcall` on the switch:

```
deploy:  state 1            -> nothing
         state 0, no def    -> state = 1
         state 0, def       -> slot 2 (start drop); on success state = 2
         state 2 or 3       -> nothing
retract: state 0            -> nothing
         state 1, no def    -> state = 0
         state 1, def       -> slot 3 (start retract); on success state = 3
         state 2 or 3       -> nothing
```

A transient state holds until the definition's callback lands it. Losing the gate while the drop
is still playing therefore does not cancel the drop; the retract starts on the first tick after
the settle in which the gate is still lost.

## The per-frame rule

The tail of the world tick `FUN_004897c0` (`0x00489dc3`-`0x00489e21`) runs once per frame inside
the tick's outer guard (no cutscene owning the world, `DAT_0071c50c == 0`), with the player object
`DAT_0071c298` present and alive (`+0x91d == 0`) and the switch allocated:

```
if 0.707 < player[+0x190]                       ; attitude gate
   and FUN_00471690(list 0x71c268, player pos)   ; sensor gate
then deploy(switch)
else retract(switch)
```

**The attitude test.** `+0x190` is the Y component of row 1 of the aircraft's orientation matrix
at `+0x180`, and row 1 is the aircraft's own up axis in world coordinates ([`flightModel.md`](flightModel.md)
"Bank coupling", where the same component is `wingUp = m[1][1]`). The immediate at `0x006080ec`
is `0.707`. The gate is therefore "the aircraft's up axis is within 45 degrees of world up",
which bank and pitch both count against; heading plays no part. It is not measured against the
sensor or the caboose.

**The sensor test.** `FUN_00471690` is a membership walk over the global list at `0x0071c268`,
entries 8 bytes apart, `this` hard-coded at `0x00489dee`. Each entry is `{node, radius²}`, and
`FUN_004717d0` passes an entry when the node's flag word `+0x24` carries bit `0x4` (active) and
the squared distance (`FUN_00538880`) from the node's world position (`FUN_004cf2c0`) to the
player's position is at most the stored `radius²`. The position handed in is the player object's
first virtual, called at `0x00489deb`.

**Where the list comes from.** `FUN_00471830`, called by the mission load right after the
initialiser (`0x004648b5`, `this = 0x71c268`), reads `pickups.zrd` (`0x006273fc`): for each record
after the first it resolves the node named at `+0xc` by name (`FUN_004d0280(7, name)`) and, if it
exists, appends `{node, radius × radius}` (the multiply is in `FUN_00471770`). The mission
teardown `FUN_00464970` empties the list (`FUN_004716d0`). Nothing else in the image reads the
list: `pickups.zrd` exists for this switch. C1/M02's table is one row, `ladder_pickup_sensor`,
100 m.

## Consequences for the port

- The switch is a per-mission native object evaluated every tick, never an animation event: a
  `CALLBACK` handler for code 123 in the mission-script host would be wrong, and the codes
  around it play no role
  ([formats/anim-definitions/cutscenes.md](../formats/anim-definitions/cutscenes.md)).
- The gate is two tests, in this order: `up.Y > 0.707` on the aircraft's own attitude, then
  inside any active `pickups.zrd` sensor. There is no speed band, no heading test and no
  objective gate; the sensor node's own activation (the pickup rig staging it on the caboose)
  is the only mission-side control.
- A mission without the definitions still flips the state, silently. The port keeps that so the
  switch needs no per-mission table.
- `retract_ladder` is only ever started from state 1, so a drop that is still playing is never
  cut short by the switch; the pickup-completion definition's `STOP_ANIMATION [drop_ladder]` is
  what ends it after the hookup.

## The ladder through the pickup

The switch's own rule stops at the cutscene boundary (the tick tail runs only while no cutscene
owns the world), so what happens to the ladder during the pickup is authored, not native. The
approach's definition `ladder_pickup_sensor`/`lookat_copilotpkup` (`C1/M02/zrdr/copilot_pkup.zrd`)
carries one untimed sequence, and its order is the whole answer:

1. `STOP_ANIMATION` on `drop_ladder`, `waveloop`, `get_up`, `hit_the_deck`.
2. `INVALIDATE_ANIMATION` on `waveloop`, `get_up`, `hit_the_deck`.
3. `CALL_ANIMATION [cabpkup_ladder]`, then `CALL_ANIMATION [cabpkup_player]`, neither waiting.
4. `INVALIDATE_ANIMATION` on `caboosewave`, `cabpkup_ladder`, `cabpkup_player`, `drop_ladder`,
   `retract_ladder`.
5. `CALL_ANIMATION [got_the_pilot]`, `[trucktodepot1]`, then `[caboosepickup]` with
   `WAIT_FOR_COMPLETION`, and `INVALIDATE_ANIMATION [caboosepickup]` after it.

The invalidations in step 4 land immediately after the calls in step 3 and with no wait between
them, so they mean "never runnable again" rather than "stop now": the instances started a moment
earlier run on.

**The climb replaces the wind sway, it does not play over it.** `drop_ladder` calls
`gen_drop_ladder` with `WAIT_FOR_COMPLETION`, and it is inside `gen_drop_ladder` that each rung's
`ladder_drop` script is followed by a `looprN` sequence holding `ladder_loop` in a `LOOP_COUNT -1`.
Stopping `drop_ladder` at step 1 is what ends that loop. `cabpkup_ladder` then re-parents the very
same `rope_ladder` object from the aircraft's `ladder_pos` to the `caboose`, zeroes `ladder_roll`,
and drives the ladder root plus all six rungs from its own `OBJECT_MOTION_SI_SCRIPT` `ALL_NAMES`
record (node index 2 and 4 through 9 against script slots 0 through 6), ending by setting
`rope_ladder` inactive. No rung is left for a wind loop to move, and the ladder hangs in the
train's frame rather than the aircraft's for the length of the climb.

A ladder that stops swaying when the pickup cutscene starts is therefore the original's own
behaviour, and the question to ask of a build is whether the climb's own motion is playing, not
whether the sway survived. `landings-train-pickup-ride` measures both, as a point out along
`rung1` read in the ladder root's frame: the rungs hinge about their own origins, so a
position-only read of a rung sees nothing at all.

## Not built

**The `ladder_roll` counter-rotation.** `FUN_004735b0` also resolves a node named `ladder_roll`
(`0x00627530`) into `DAT_0071c320`. While the switch's state is non-zero, the same tick tail
(`0x00489d42`-`0x00489dba`) decomposes the player's orientation matrix into Euler angles
(`FUN_0053df30`), rebuilds a rotation from them with the middle angle zeroed
(`FUN_0053f610(0, e0, e2)`), converts it back (`FUN_0053f8d0`, `FUN_0053fa40`, `FUN_0053df30`)
and writes the result as the node's rotation (`FUN_004d1a30`). Read as: the ladder's hanging
frame is counter-rotated by the aircraft's pitch and roll so it hangs plumb, heading left alone.
In C1/M02 the node is authored inside the `rope_ladder` actor
(`mis_anim/rope_ladder-cabpkup_ladder.json`). The port does not do this yet; the rungs' authored
`ladder_drop.zan` motion plays under the aircraft's own frame.

**The `ladder_pickup` mission flag.** `FUN_004a3a60`, the mission-record parser, sets a byte at
record `+0x22` when the record carries `ladder_pickup` (`0x006296b0`). Its reader is not traced
in this pass; it is not part of the switch above.

**What starts `pickup_timing`.** Not this list, and not the player: the chapter's `train.zrd`
definition `train_on_track`, a `NEW_GAME_START` anim, opens with `CALL_ANIMATION [pickup_timing]`
before its track loops, so the timing runs from mission load on the train's own clock
([`../formats/anim-definitions/cutscenes.md`](../formats/anim-definitions/cutscenes.md)). The port
runs it the same way and starts nothing off the sensor.

## Function reference

| function | role |
|---|---|
| `FUN_004735b0` | per-mission initialiser: allocates the switch, resolves both definitions and the `ladder_roll` node |
| `FUN_004456f0` | switch constructor: state 0, both definitions null, resolves `ladder_pos` |
| `FUN_00445500` / `FUN_00445540` | set the drop / retract definition and register the switch as its callback host |
| `FUN_004455e0` / `FUN_00445620` | the deploy / retract transitions |
| `FUN_004454c0` / `FUN_004454d0` | the settle writes (state 1 / state 0) behind vtable slots 0 and 1 |
| `FUN_004897c0` | the world tick; its tail is the per-frame rule |
| `FUN_00471690` / `FUN_004717d0` | the sensor membership walk and its per-entry active-and-inside test |
| `FUN_00471830` | builds the sensor list from `pickups.zrd` at mission load |
| `FUN_00471770` | stores one entry as `{node, radius²}` |
| `FUN_004716d0` | empties the list at mission teardown |
| `FUN_004ee160` | per-definition callback host registration |
