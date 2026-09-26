# Mission objectives choreography

Part of the [format documentation](README.md). This page decodes `objectives.zrd`, the
per-mission choreography script that drives campaign missions: objective wake/sleep timing,
sound cues, target-list edits, enemy and generator wake-ups, and mission win/loss. Every
directive's semantics below is traced against `crimson.exe` (the parser at `FUN_00466b70`,
the per-frame tick at `FUN_0046a490`, the wake executor at `FUN_00469af0`); no claim rests on
a keyword's name alone. [missions.md](missions.md) covers the danger-zone and target-display
surface this page's `DANGER_ZONES_*` and `*_TARGET` directives touch;
[instant-action.md](instant-action.md) covers the sibling mission director that runs when this
script is nearly empty.

**Census.** All **53** `objectives.zrd.json` files in the install were censused for this page:
24 campaign missions (`C*/M*`), 8 Instant Action (`C*/IA1`), 21 multiplayer (`C*/MP*`),
carrying **1,338** `OBJECTIVEn` blocks in total. Every directive that census found appears in
the reference below; the shipped-but-dead directives are listed under
[Data anomalies](#data-anomalies). The worked example is `extracted\C2\M01\zrdr\objectives.zrd.json`
(82 objectives, the Hollywood "Spruce Goose" mission); a second, shorter walk covers CM05's
zeppelin-damage fuse in `extracted\C3\M04`.

## Contents

- [Conceptual model](#conceptual-model)
- [File-level keys](#file-level-keys)
- [The objective block](#the-objective-block)
  - [Objective states and timing: BEGIN_DORMANT](#objective-states-and-timing-begin_dormant)
  - [Completion conditions](#completion-conditions)
  - [Wake actions](#wake-actions)
  - [Completion actions](#completion-actions)
  - [Chaining directives](#chaining-directives)
  - [IDENTITY and the objectives display](#identity-and-the-objectives-display)
  - [Win and loss](#win-and-loss)
    - [The fourth ending: the player's own death](#the-fourth-ending-the-players-own-death)
- [Keywords the parser accepts that no mission authors](#keywords-the-parser-accepts-that-no-mission-authors)
- [Reader rules and edge cases](#reader-rules-and-edge-cases)
- [Data anomalies](#data-anomalies)
- [Worked example: C2/M01](#worked-example-c2m01)
- [Worked example: CM05's zeppelin-damage fuse](#worked-example-cm05s-zeppelin-damage-fuse)
- [Evidence & limits](#evidence--limits)

## Conceptual model

The file is a flat zrdr list: a handful of file-level keys, then `OBJECTIVE1`, `OBJECTIVE2`, …
blocks. The parser (`FUN_00466b70`) loads `OBJECTIVE%d` for d = 1, 2, 3, … and **stops at the
first number that is missing**, so numbering must be contiguous from 1. Each block becomes a
0x5e4-byte record in one array; every cross-reference between objectives is by number
(converted to 0-based internally).

An objective is a small state machine, ticked every frame by `FUN_0046a490`:

| State | Meaning |
|---|---|
| 0 dormant | waiting for its `BEGIN_DORMANT` delay or for another objective to wake it |
| 1 awake | its completion conditions are being evaluated |
| 2 napping | asleep on a timer; wakes itself when the timer runs out |
| 3 retired | completed, slept permanently, or expired; only an explicit wake revives it |

Waking runs the objective's **wake actions** (reactivate enemies, arm turrets, top up a
generator, start an animation, play a sound group). While awake, its **completion conditions**
are tested; an objective with no conditions completes on its first eligible tick. Completing
runs its **completion actions** (sounds, AI team/net changes, target-list edits, vehicle warps)
and its **chaining directives** (wake, kill, nap other objectives), and can end the mission
(`INSTANTWIN`/`INSTANTLOSS`). A completed objective never runs again unless another objective's
`NAP_OBJECTIVE_WHEN_I_COMPLETE` clears its completed flag.

**At most one objective completes per tick.** The completion scan starts from a round-robin
index (`DAT_0071c128`, advanced every tick) and stops at the first completion, so simultaneous
completions resolve over consecutive frames in rotating order.

## File-level keys

| Key | Args | Semantics (all traced in `FUN_00466b70` unless noted) |
|---|---|---|
| `MISSION_TIMER` | `[seconds]`, optional `"NOLOSS"` | Sets the mission countdown clock's initial value. The clock is idle until something starts it (see `RESET_TIMER` below); when running, its expiry ends the mission with a 3 s wrap-up delay and an on-screen notice (`FUN_0046a490` head, message rows 6002 `MSG_TIME_EXPIRED` and 137 `MSG_MISSION_LOST`). With `NOLOSS` the clock pins at 0 instead of expiring (`FUN_0046c640`). Every shipped file authors `0.0`, so the clock only matters where an objective adjusts it. |
| `PLAYER_INIT` | `[int, [x,y,z], [pitch,yaw,roll], float, float]` | Player start block. Position is meters; the rotation triple is degrees, stored premultiplied by 0.017453292 (radians). The leading int, the fourth value (0.8 in every file) and the fifth (stored x0.1) are parsed to mission record +0x6c0..+0x6e0; **their consumer is untraced**, so what the last two mean at spawn is a named gap. |
| `RESTORE_ANIMS`, `EXECUTE_ANIMS`, `INVALIDATE_ANIMS` | list | Three animation lists. **Authored `null` in all 51 files that carry them**, so the campaign never exercises them. The parser accepts plain anim names and a `[node, "prefix#"]` wildcard form (`#` matches the numeric tail, resolved against the node's own anim list); the `EXECUTE` list is run at load. |
| `PRIMARY/SECONDARY/TERTIARY_COMPLETE_SOUND` | `[group]` | Sound group played whenever an objective of that `IDENTITY` class completes. Authored once (C1/M02) with the placeholder `your_sound_here`, which resolves to no group. |
| `MISSION_WON_SOUND`, `MISSION_LOST_SOUND` | `[group]` | Played when the mission-end flag is set won/lost (`FUN_00463c30`). Same single placeholder authoring. |
| `OBJECTIVES_WON_SOUND`, `OBJECTIVES_LOST_SOUND` | `[group]` | Parsed (record +0xc70/+0xc74), played when the all-`WON`/all-`LOST` completion rule fires (see [Win and loss](#win-and-loss)). Authored nowhere. |
| `WIN_ANIM`, `LOSS_ANIM` | `[name]` | Parsed to record +0x6e4/+0x6e8. Authored nowhere; consumer untraced. |
| `READ_TIME`, `REVIEW_DELAY`, `OBJECTIVE_DELAY`, `PLAY_LOSS_ANIM_WHEN_MAVERICK_ON_RUNWAY` | `[int]` | Authored only in C5/MP1 and C5/MP2. **None of these strings exists anywhere in `crimson.exe`** (byte-pattern search), so the shipped engine reads none of them. |

## The objective block

Directives inside an `OBJECTIVEn` block are found by exact-name lookup (`FUN_00579ff0`), so
order inside the block does not matter and unknown words are silently ignored. Where a
directive takes "up to 10" entries, that is the parser's hard array cap.

### Objective states and timing: BEGIN_DORMANT

`BEGIN_DORMANT [t]` starts the objective dormant. `t` is compared against the mission's
elapsed clock: the objective wakes automatically at mission time `t` seconds, and `t = -1`
means it never wakes on its own and waits for a `WAKE_OBJECTIVE_WHEN_I_COMPLETE` from another
objective. Absent `BEGIN_DORMANT`, the objective starts awake.

The parser accepts up to three more floats (`FUN_00466b70` at 0x467a30..0x467b80), none
authored by any shipped file:

- 2nd float: awake-duration. After that many seconds awake, the objective naps and fires its
  `WAKE_OBJECTIVE_WHEN_I_SLEEP` list.
- 3rd float: nap-duration used when the objective naps.
- 4th float: absolute mission-time deadline; reaching it while awake retires the objective and
  fires its `WAKE_OBJECTIVE_WHEN_I_SLEEP` list.

`TICK_DEPENDS_ON_OBJ [n]` gates the whole objective: it is only ticked, tested, or completed
while objective `n` is **awake** (state 1), not merely alive (`FUN_0046a490` gate at
0x46a5e6). The nap countdown (state 2, `+0x5cc` accumulating against `+0x5d8`) sits behind
that gate too, so a nap another objective puts a gated objective into is **held**, not dropped:
`FUN_0046b160` sets state 2, zeroes the timer and clears the completed flag regardless of the
gate, and the timer only starts counting once `n` is awake. C1/M04's 18 and 19 (both gated on
29, which 28's `DEDG [2, 2]` wakes) are the shipped case: their naps of 20 wait for 29, and the
Paladin Blake squad arrives 30 s or 90 s after that, whichever route the radio tower chose.

A gate objective is therefore one an author means never to complete while the gate is wanted, and
a gate whose own condition reads true is a chain that stops for good. C1/M04's 29 is `INACTIVE1
[piratezep]` over the hull the mission is built around, so the gate stands exactly as long as that
hull is switched on: the mission's own intro switches it off for its last shot and switches it
back on in the `RESET_STATE` the handoff runs, and a session that leaves it off reads 29 complete
on the tick it wakes, closing 18 and 19 permanently. The same node gates 40, which 42 depends on,
so the docking chain 42/43/44 rests on the hull twice over.

### Completion conditions

An awake objective completes when **any one** of its condition families reports true
(`FUN_0046a490`, the OR-chain at 0x46a8c0). An objective authoring none of them completes
immediately on its first eligible tick.

| Directive | Args | Completes when (traced) |
|---|---|---|
| `INACTIVE1` … `INACTIVE18` | `[node]` or `[node, child, ...]` | Each entry names a gamez node, optionally walking down named children (`FUN_00469180`; `["piratezep","leng22","healthy"]` is the path piratezep / leng22 / healthy). The condition counts entries whose resolved node has lost its active bit (bit 2 of node +0x24, the same `gwNodeSetActive` bit [instant-action.md](instant-action.md) documents); it reports true when the count reaches `INACTIVE_COMPLETION_COUNT` (`FUN_00469a60`). Destroying a destructible deactivates its `healthy` model, which is why every "destroy X" objective is written as `INACTIVEn [x, healthy]`. The parser accepts `INACTIVE1` through `INACTIVE100`; the data's maximum is 18. |
| `INACTIVE_COMPLETION_COUNT` | `[k]` | Required count for the `INACTIVEn` family. Defaults to all entries. |
| `ANIM_STATE` | `ANIM { NAME [a], STATE [s] }` repeated, optional `COMPLETION_COUNT [k]` | Counts entries whose animation's current runtime state (`FUN_004ed530`) equals the authored one; true at `k` matches, default all (`FUN_004697a0`). States parse as `RUNNING` = 2, `EXECUTED` = 3, `INVALID` = 4 (`FUN_004691d0`); they are the anim runtime's playing / has-run / invalidated states (see [anim-definitions.md](anim-definitions.md)). |
| `DANGER_ZONES_COMPLETED` | `[zone, ...]` | Names danger zones ([missions.md](missions.md)). When the player completes a zone, the zone module walks every **awake** objective and flags matching names (`FUN_00446990` at 0x446a72); the condition is true when `DANGER_ZONES_COMPLETION_COUNT` names are flagged, default all (`FUN_00469ab0`). Zones completed while the objective is dormant or napping do not count for it. |
| `DANGER_ZONES_COMPLETION_COUNT` | `[k]` | Required count for the zone list. |
| `DEDG` | `[group, max]`, parser accepts optional 3rd string `generator` | True when the number of live vehicles whose AI group equals `group` (vehicle +0x388, the roster `group` field) is at or below `max` (`FUN_00465910` / `FUN_004658d0`); an optional generator name adds that generator's remaining capacity (+0x80) to the live count, so unspawned members block completion. `DEDG [2,0]` is "group 2 wiped out"; `[1,2]` is "group 1 down to two". "Live" is the dead byte `+0x91d` being clear (`FUN_00465850`), and the activate/deactivate primitive `FUN_004b0f40` sets that byte together with `+0x945`, so a roster member shipped `deactivated` is not counted until `WAKEUP_ENEMIES` puts it in play; C2/M01 relies on this, its `DEDG [1, 2]` gates falling to two while seven group-1 blocks are still parked. A cutscene's AI park (code 913, `FUN_0041f250`) is different: it sets the hold flag `+0x354`, pushes the next-think time out and deactivates the scene node, and never touches `+0x91d`, so a parked vehicle still counts; C3/M05 relies on that, its wing walk parking the last Balmoral for 19 s under a `DEDG [5, 0]` that naps the instant loss (CSVM: `FlightController.Deactivated`, inert without `Parked`). Side effect: every counted member's engagement volume is widened each tick to 9,000 m radius (stored squared, as 8.1e7) and ±9,000 m altitude (`FUN_00465850`), so a DEDG-watched group never disengages by distance. Each of the three fields is a floor the write only raises, and the walk sits behind both the awake gate and the OR-chain's short circuit, so a napped or retired objective stops widening and nothing ever narrows the volume back. A clause whose group is 0 or whose max is negative widens nothing, since `FUN_00465910` returns before the walk. CSVM applies the radius (`Session/Campaign/CampaignRoster.cs`'s `WidenForDedg`, reached through the graph's world seam); the altitude bands have no consumer there. The name is not expanded anywhere in the binary; the mechanics above are the full decoded meaning. |
| `TRAVELERS` | `[who, "APPROACHING", where, radius, count]`, optional `"DELETE_ON_SUCCESS"` | Proximity condition (`FUN_00465b40`). `who` is either a gamez node name (the shipped files mostly use `player`) or an integer AI group. `where` is a node name or a literal `[x,y,z]`. Node form: true when the subject node is inside (`APPROACHING`) or outside (any other word; nothing else is authored) `radius` meters of the reference; with `DELETE_ON_SUCCESS` the vehicle standing on the node is deleted, or the node deactivated, as the condition fires. Group form: each tick, every live group member inside (or outside) the radius adds 1 to a running tally, `DELETE_ON_SUCCESS` deletes the counted members (never the player), and the condition is true when the tally reaches `count` (default 1). Without deletion a loitering member re-counts every tick. Radius is stored squared; `count` sits in the 5th slot. See ["TRAVELERS and the roster"](#travelers-and-the-roster) for what a name may address. |
| `COUNTER … TEST_COMPLETE` | see below | Parsed, authored nowhere; see [unauthored keywords](#keywords-the-parser-accepts-that-no-mission-authors). |

`COMPLETED_ZEPCANNONS` and `COMPLETED_STOPPOINT`, despite the names, are **completion
actions**, not conditions; they are listed under [Completion actions](#completion-actions).

### TRAVELERS and the roster

A node-form `TRAVELERS` names its subject and its reference by **vehicle name**, and a vehicle
name is not always a world node. An `aiv` roster block is spawned from the roster, and its name
reaches the world's node index only where the chapter gamez happens to carry a library root of
that same name; `C4/M02`'s `bhatgyro_1` does not, because the root it is built from is
`bhatgyro`. A resolver that walks the node index alone therefore cannot answer where that
aircraft is, and the condition is unanswerable rather than false.

That distinction decides `C4/M02`. Each of its four search locations wakes a spot check
(`OBJECTIVE31` to `OBJECTIVE34`, `TRAVELERS player APPROACHING bhatgyro_1 500`) and naps the
"he is not here" radio line that kills that spot check two seconds later, so each location is a
two-second window. Those four spot checks are the **only** objectives that wake `OBJECTIVE24`,
the mission's PRIMARY 1. A reference that never resolves is a mission that cannot be finished.

⚠ **Deactivation is not consulted on the node form.** `C4/M02` warps Blacke at five seconds and
spots him while he is still deactivated; `WAKEUP_ENEMIES` puts him in the air only from
`OBJECTIVE17`, which is downstream of the spot check. The group form's liveness rule is `DEDG`'s
and does not carry here.

### Wake actions

Run by `FUN_00469af0` at the moment the objective wakes, whether by timer or by another
objective.

| Directive | Args | Effect (traced in `FUN_00469af0`) |
|---|---|---|
| `WAKEUP_ENEMIES` | `[name, ...]` up to 10 | Each name is looked up as a vehicle (`FUN_004aff10`) or zeppelin (`FUN_004bd3e0`) and, if it is deactivated (vehicle byte +0x945, zeppelin byte +0x5), reactivated. This is the partner of the `aiv` roster's `deactivated` flag ([ai-rosters.md](ai-rosters.md)). |
| `WAKEUP_TURRETS` | `[name, ...]` up to 10 | Sets turret byte +0x6e (`ACTIVATED`, [turrets.md](turrets.md)) on every turret whose name matches. A `*` in the pattern matches exactly one digit (`FUN_004a97b0`), so `aagun**` arms aagun01 through aagun99. |
| `WAKEUP_ZEP_TURRETS` | `[node, ...]` up to 10 | Resolves each name to a gamez node and arms every turret in that node's subtree (`FUN_004bef70(node, 1)`, the same primitive Instant Action uses on its objective zeppelin). |
| `WAKEUP_GENERATOR` | `[name]` or `[name, n]` | Adds `n` (default 1) to the named generator's remaining launch capacity (+0x80, [mission-entities/enemy-generators.md](mission-entities/enemy-generators.md)). The generator's own launch cycle then releases the aircraft. |
| `WAKE_ANIM` | `[anim]` or `[anim, node]` | Starts the named animation definition, optionally at a node (`FUN_004edda0`). C1/M04's first objective opens the `hangar3` ground hangar with it; a generator's own hangar door is not this verb's but the generator cycle's ([mission-entities/enemy-generators.md](mission-entities/enemy-generators.md)). |
| `WAKEUP_SOUND_GROUP` | `[group]` | Plays the named sound group ([sounds.md](sounds.md)) through `FUN_0046cc70`: normally immediate; three groups registered from `player.zrd`'s `pre_battle_sound` / `in_battle_sound` / `won_battle_sound` keys get stateful treatment instead, and the shipped `player.zrd` binds only `in_battle_sound`, to `music_battle_sg`, which is latched so battle music starts once. `music_prebattle_sg` and the stinger groups the data names are ordinary groups on this path. |
| `RESET_TIMER` | `[seconds]` | If the objective wakes out of dormancy: sets the mission countdown to `seconds` and **starts** it (`FUN_0046c510` + `FUN_0046c5a0`). Authored nowhere. |
| `COUNTER … ON_WAKEUP` | | Parsed, authored nowhere. |

The wake also resets the objective's private timer and, for `HIDE_OBJ` (authored nowhere),
marks another objective completed without running its effects.

### Completion actions

Run once, inside the tick that detects completion (`FUN_0046a490`, 0x46a90c onward).
`COMPLETED_SOUND_GROUP [group]` goes to the objective layer's cue dispatcher (`FUN_0046cc50` →
`FUN_0046caf0`) with a **1 s start delay**, the same delay `WAKEUP_SOUND_GROUP`'s ordinary path
passes `FUN_00593590`.

⚠ **The dispatcher's 15 s interval is a music guard, not a speech one.** `FUN_0046caf0` gates it on
`FUN_00480460`, which tests bit 3 of the game's own sound-flag word; the keyword table that builds
that word (0x628744, consumed at 0x4802e0) is `NOROGUE` 1, `WINGMAN` 2, `VOICE` 4, **`MUSIC` 8**,
`SFX` 0x10, `OPTIONAL` 0x40. A music cue raised while the previous one's 15 s hold is unexpired is
**refused** (`return 0`), not delayed, and a music cue is dispatched with a 0 s delay. A voice line
is subject to no such interval: its only wait rule is its own `QUEUE` tolerance
([sounds.md](sounds.md)). ⚠ Do not read this interval as spacing between spoken lines; it silences
short-tolerance chatter that the original plays.

Then, in order:

| Directive | Args | Effect (traced) |
|---|---|---|
| `WARP_VEHICLE` | `[vehicle, [x,y,z,heading], ...]`, entries may append a 5th string | Picks one waypoint **at random** (`rand() % count`) from the list. A plain entry writes the vehicle's position and its euler attitude and nothing else (`FUN_00493fb0`); an entry with the 5th string instead names a **scripted path**, and puts the vehicle on it at waypoint 0 facing the leg into waypoint 1, with the path flag `+0xcc` set and the freeze flag `+0xd4` CLEARED, so it leaves moving (`FUN_004940d0`, the same placement the spawner makes but released, see [flightModel.md](../org/flightModel.md), "The scripted-path follower"). The forward velocity belongs to the caller (`FUN_0046a490` at `0x0046a8f2`), not to either placement: `min(plane_speed_max, fd_speed)` along the placed nose, written **only** for a vehicle that did not end up path-driven. Authored once (C4/M02), where it is what hides Blacke in one of four places. |
| `SET_AI_TEAM` | `[[name, team], ...]` up to 10 | Sets the vehicle's team through its vtable (dropping its current target) or the zeppelin's team fields +0xdc/+0xe0 with a turret-side refresh (`FUN_00469e20`, log string `SET_AI_TEAM: setting vehicle %s to team %d`). |
| `SET_AI_NET` | `[[name, net], ...]` up to 10 | Reassigns the vehicle (`FUN_00475f30`) or zeppelin (`FUN_004bd7a0`) to the named patrol net ([ai-nets.md](ai-nets.md)). This is how C2/M01 walks its patrol boats through successive nets. |
| `SET_AI_ATTACK_RADIUS` | `[[name, r], ...]` | Parsed and executable (`FUN_00469f70` writes the vehicle's radius-squared / ±band volume at +0x328), authored nowhere. |
| `COMPLETED_ZEPCANNONS` | `[[zep, flag], ...]` up to 10 | Writes `flag` to zeppelin record byte +0xc (`FUN_0046a0b0`), the broadside engage flag: the zeppelin update `FUN_004bf9d0` runs the cannon fire pass only while it is set and retracts ready cannons while it is clear ([mission-entities.md](mission-entities.md) "Broadside firing"). Authored in C2B/M04, C4/M05 and C5/M04 only. |
| `COMPLETED_STOPPOINT` | `[[net, stop, flag], ...]` up to 10 | Looks the name up in the patrol-net table, requires `stop > 0`, finds the FIRST node carrying that stop-point id (`FUN_004319a0`) and writes `flag` onto that node's halt byte (`FUN_0046a0d0` / `FUN_004319d0`). `flag = 0` releases a docked zeppelin, `flag = 1` arms a fresh stop mid-route; the file's own flag is only the starting state. All 23 shipped clauses resolve to a node ([ai-nets.md](ai-nets.md) stop points). |
| `ADD_OTHER_TARGET` / `REMOVE_OTHER_TARGET` | targets: a bare name, or a `[parent, child, ...]` path | Sets/clears byte +0x4c on the named vehicle, turret, or object (`FUN_0046a1b0`/`FUN_0046a1f0`): the `other_target` display flag `targets.zrd` also sets at load ([missions.md](missions.md)). |
| `ADD_OBJECTIVE_TARGET` / `REMOVE_OBJECTIVE_TARGET` | targets: a bare name, or a `[parent, child, ...]` path | Sets/clears byte +0x4d, the mission-objective target flag (the byte [instant-action.md](instant-action.md) documents on the IA objective zeppelin). A nested list is ONE target, a node path walked from the outer name inward, the shape `INACTIVEn` uses: every shipped nesting is a path (`[piratezep, rock_zeppelin]`, `[zcrane1, healthy]`, `[cargozep2, ctur1]`, C5/M01's three-deep `[rfspt4, healthy, spprt]`), and a bare name beside one (`[[player_bmhook, bm_hook], bhf_hangar]`) is a second target. Read as independent names, `[piratezep, rock_zeppelin]` flags the hull root and every ground `rock_zeppelin` too. The read is from the data's shape; the binary's walk of the list is not traced. |
| `START_TAXI` | `[name, ...]` up to 10 | Clears vehicle byte +0xd4 (`FUN_0046a2b0`), releasing the vehicle onto its authored `taxiPath` ([ai-rosters.md](ai-rosters.md)). |
| `SET_HELP_LABEL` | `[target, MSG_key]`, the target a bare name or a `[parent, child]` path | Resolves the message key to its langui id and writes id + text into the target's help-label fields (+0x48/+0x38, `FUN_0046a2d0`), the label the HUD shows for a selected target. |
| `STOP_QUEUED_SOUNDS` | `[name, ...]` up to 10 | Removes the named sounds from the radio queue if they have not started (`FUN_005920f0`). Used to cancel now-moot reminder chatter. |
| `ADJUST_TIMER_WHEN_I_COMPLETE` | `[op, seconds]`, op `SET` or `ADJUST` | Sets or adds to the mission countdown (`FUN_0046c510`/`FUN_0046c550`). Parsed (also as unauthored alias `TIMER_ADJUST`), authored nowhere. |
| `END_TIMER` | flag | Stops the mission countdown (`FUN_0046c5c0`). Authored nowhere. |
| `COUNTER … ON_COMPLETE` | | Parsed, authored nowhere. |

### Chaining directives

All references are objective numbers as written in the file (1-based); the parser stores them
0-based. Lists are read until the parser's slot caps (30 wake, 15 kill, 15 sleep targets).

| Directive | Args | Effect (traced) |
|---|---|---|
| `WAKE_OBJECTIVE_WHEN_I_COMPLETE` | `[n, ...]` | On completion, wakes each listed objective, running its wake actions (`FUN_00469af0`). `WAKE_OBJECTIVE` is an exact parser alias. A dead or completed target is skipped. ⚠ If a listed target is **already awake**, the executor resets that target's private timer and returns, leaving the rest of the list unprocessed (`FUN_00469af0` early return at 0x469b3c). No shipped chain is written to hit this, but a runtime must reproduce or knowingly diverge from it. |
| `KILL_OBJECTIVE_WHEN_I_COMPLETE` | `[n, ...]` | Marks each target dead (`FUN_0046b130` clears alive + awake). A killed objective never ticks, completes, or wakes again. Used to retire the losing branches of a fork (for example the "barge escaped" objective once the barge dies). |
| `NAP_OBJECTIVE_WHEN_I_COMPLETE` | `[n, seconds]` | Single target. Puts it in the napping state with a wake-back timer of `seconds` **and clears its completed flag**, so a completed objective can run again (`FUN_0046a490` at 0x46ad24). This is the repeat mechanism behind every timed reminder loop. Omitting `seconds` warns (`NAP_OBJECTIVE_WHEN_I_COMPLETE has no nap_time specified.`) and defaults to 0.3 s. |
| `SLEEP_OBJECTIVE_WHEN_I_COMPLETE` | `[n, ...]` | Retires each target (state 3) without killing it; a later wake revives it. Retiring this way also plays the target's `SLEEP_ANIM` if it has one. Parsed, authored nowhere. |
| `WAKE_OBJECTIVE_WHEN_I_SLEEP` | `[n, ...]` | Fired when this objective naps via its own awake-duration or expires at its deadline (the unauthored `BEGIN_DORMANT` extras). Parsed, authored nowhere. |

### IDENTITY and the objectives display

`IDENTITY [class, priority]` or `IDENTITY [class, priority, MSG_key]` marks the objective as a
player-visible mission objective. `class` is `PRIMARY` = 1, `SECONDARY` = 2, `TERTIARY` = 3
(case-insensitive, `_stricmp` at 0x468786).

The mission runtime reads only class and priority: on completion it appends `priority` to the
per-class completed list on the targets module (`FUN_004a2350`) and plays the class's
`*_COMPLETE_SOUND`. The **display** list is built separately: `FUN_004acc20` re-reads the same
`objectives.zrd`, collects every `IDENTITY`, and builds one row per **unique priority** with
the `MSG_key`'s text as the row label, sorted; completion marks the row whose stored priority
matches (`FUN_004ad240`). Consequences, all decoded:

- `priority` is the row key. It must be unique across the whole mission or two objectives
  share one display row; the shipped data keeps primaries at 1..9 and secondaries from 11 up.
- It is also the sort key and the value reported to the per-class completion list; it is not a
  weight and has no effect on objective logic.
- **It is the bit index in the recorded completed-objective mask.** At mission end `FUN_004194e0`
  ORs `1 << priority` for every completed row, over bit 0, which is the mission-won flag; a
  priority of 0 is skipped. So the mask is indexed by the numbers a mission authors, not by row
  position, and the campaign's reward table matches its objective-bit column against them
  ([saved-games.md](saved-games.md), "The mission-result array").
- **It is not the objective's `OBJECTIVEn` number.** The original never needs that number here,
  since the completing objective carries its own priority into the match, and most missions author
  the two differently: `C3/M04`'s priority 1 row is `OBJECTIVE15`, and its `OBJECTIVE1` is a
  conditionless two-second wake. An engine whose runtime answers "is objective n complete" therefore
  marks a row by the number of the block the row was read from, never by the row's priority.
- The `MSG_key` third argument is display text only; the mission parser never reads it. An
  `IDENTITY` without it (authored, for example `["SECONDARY", 11]`) produces a row with empty
  text that still tracks completion.

The rows themselves are laid out by `escape.zrd`'s `OBJECTIVESLIST` primitive, which the pause
screen and the loading dialog share: a `parchment` bitmap at `[555, 6]`, the title
`MSG_BRF_DLG_OBJECTIVES` in `ObjListTitle`, and the list itself at `[580, 50]` in `ObjList`,
which `fonts.zrd` gives as "Andy Bold" at 14 px, italic, in near-black `[16, 16, 16]`. Completion
adds nothing but a mark: the list's `CHECKMARK` element is the `obj_check1` bitmap (25x24, a red
brush stroke) with `CENTER [1]`, so it is drawn centred on the marked row's own origin and overlaps
its leading characters. `FUN_004ad240` only sets the row's completed byte (+0x10 of the 0x1c-byte
row, whose +0x14 is the priority it matches on); the row's text, colour and place do not change,
and the parchment keeps every row it started with.

### Win and loss

- `INSTANTWIN` (bare flag): completing this objective sets the mission won flag (mission
  +0xc58) and the wrap-up runs after 0.1 s instead of the usual 3 s.
- `INSTANTLOSS`: same for the lost flag (+0xc5c).
- `WON` / `LOST` (parsed, authored nowhere): mark the objective as a victory/defeat condition;
  the mission is won when **all** `WON`-marked objectives are complete, lost when all
  `LOST`-marked ones are (`FUN_0046a490` tail), playing `OBJECTIVES_WON_SOUND` /
  `OBJECTIVES_LOST_SOUND`.
- The mission countdown expiring is a third ending (see `MISSION_TIMER`).
- A cutscene's completion code 13 sets the same won flag from outside this module and runs the
  mission-end path itself, with no wrap-up
  ([anim-definitions/cutscenes.md](anim-definitions/cutscenes.md)). Where a mission carries both,
  the code always wins: it is raised by the docking definition's last sequence, so that definition
  is still `RUNNING` when it lands and the `ANIM_STATE ... EXECUTED` objective watching it cannot
  have completed yet. C3/M05 is the worked case, its `OBJECTIVE19` (`ANIM_STATE
  hooked_to_klondike EXECUTED`, `INSTANTWIN`) reaching its condition one frame after the mission
  is already over.

#### The mission-end path, and what the player sees after it

All four endings converge on `FUN_00443090`, and it does three things and no more.

1. **Silences the world.** `FUN_00594040` walks every live sound instance and collects the ones
   still playing, `FUN_00594580` stops each (`FUN_00593dc0`) and `FUN_00594680` frees the list.
2. **Records the attempt.** `FUN_004194e0` (or `FUN_00419700` for game type 3) builds the
   completed-objective bitmask, writes the result slot through `FUN_00419630`, and asks for the
   results state `DAT_0071d57c`.
3. **Asks for it through the fade, not directly.** `FUN_0046fb60` parks the requested state in
   `_DAT_0071c210`, arms the "Fade State" object at `DAT_0071c200` with a duration and pushes THAT
   onto the state machine at `DAT_0071d3a0`. The duration is `_DAT_006272b8`, the fade's own
   default, and it is **2.0 s** (`FUN_00470000` seeds every fade with it).

The Fade State is the whole answer to "what plays after the ending". Its entry
(`FUN_0046fc80` -> `FUN_0059e2e0`) COPIES THE CURRENT FRAMEBUFFER into a surface of its own; its
tick (`FUN_0046fcc0` -> `FUN_0046fe10`) blits that copy every frame at a level ramping from 0 to 1
at `1/duration` per second and presents it itself; its exit (`FUN_0046fd60`) frees the copy. The
flying state is off the top of the machine for all of it, and `FUN_00443090`'s own
`DAT_0071d290 = 5000` parks that state's presenter for 5000 frames on top of that, where every
ordinary cutscene callback writes 1.

So nothing of the film plays on after an ending. **The last live frame is the frame the ending
landed on**, and the player looks at that frame fading out for 2.0 s before the next screen. In
C3/M05 that catches the docking 0.06 s before `bal_wing_foldup`'s authored end, with the wings
folded but a few degrees short of their authored angle, and 0.85 s before `stopprops` would have
finished spinning the Balmoral's propellers down. Neither is ever seen.

**The frame a code-13 ending lands on is the film's, not the pilot's.** The docking's completion
code is a case of the cutscene callback host at `0x0047e2ba`
([anim-definitions/cutscenes.md](anim-definitions/cutscenes.md)), and it does four things in this
order:

1. `FUN_00494b20` **puts the player back in flight**: it re-reads the vehicle's position and
   orientation off its node into `+0x1f8`..`+0x20c`, writes that pose into every saved camera slot
   in the list from `+0x6a4` to `+0x6a8` (stride 0x24), takes the three view offsets at
   `+0x6b0`..`+0x6b8` from `DAT_0075d1b8`, and CLEARS the flags callback 11 set (`+0x91d`, `+0x91e`)
   along with the wreck flag `+0x91f`.
2. `DAT_0071d290 = 1` at `0x0047e2c6`, one frame of the presenter park.
3. `FUN_00463c10(1)`, the won flag.
4. `FUN_00443090`, the mission-end path above.

Step 2 is what decides which camera the fade runs over. The flying state's tick reads that park at
`0x004a09f0`, **after** its own render call at `0x004a09e1`: a non-zero value is decremented and the
frame is never handed to the present at `0x005a8550`, and only zero reaches it. So the single frame
that would show the world cut back to the pilot's own view is rendered and thrown away. The Fade
State's copy is taken before that anyway: `FUN_005b60d0` calls the pushed object's entry (vtable+8)
synchronously before returning, so the framebuffer `FUN_0059e2e0` copies inside step 4 is still the
last frame presented, which is the film's. `FUN_00443090`'s own `DAT_0071d290 = 5000` then keeps the
flying state from ever presenting again, and from the next frame that state is off the top of the
machine regardless.

**A mission that ends inside a docking film fades out on the film's last frame.** The cut back to
the cockpit is real (the flight flags are cleared and the vehicle pose re-synced) but it exists for
exactly one unpresented frame. Nothing about this is particular to a win: the fade copies whatever
was on screen, so an ending raised from the anim side while any film plays fades that film's frame,
and an ending in free flight fades the pilot's own view.

Every shipped campaign mission authors `INSTANTWIN`/`INSTANTLOSS` objectives to end on (the
missions finishing on a hook or a drop reach code 13 first); the
`WON`/`LOST` aggregate rule and the timer are unexercised by the data. **Four of the 21 campaign
missions author no loss at all** (`C3/M01`, `C3/M02`, `C4/M02`, `C5/M03`: zero `INSTANTLOSS`, zero
`LOST`), which is the shape of the fourth ending below: those missions are losable only by dying.

#### The fourth ending: the player's own death

The player losing the aircraft is a mission ending, and it is **not** any of the three above. It
never reaches this module: it closes a gate that stops the module instead, and drives the debrief
from the crash animation.

- **The gate.** `FUN_004a0220` (the state core) tests `byte [player+0x91d]` twice. The second test,
  at `0x004a09b6`, skips the whole objectives tick `FUN_0046a490` while the player is dead. In
  single player (`FUN_00440ad0() == 0`) nothing in this module runs after that: no completion scan,
  no countdown (`FUN_0046c5f0`'s only two callers are inside the gated tick and a network-only
  branch), no cue drain (`FUN_0046cdf0`), no delayed cues (`FUN_0046c870`), no target-list
  executors. The first test, at `0x004a0281`, guards the wrap-up countdown at `+0xc40`, which is
  never armed because `FUN_00463c30` is never called.
- **What closes it.** `+0x91d` is set by `FUN_004b82d0` (whole-vehicle health at or below zero,
  which also sets the wreck-falling flag `+0x91f` for mode classes 0 and 4) and by `FUN_0048b920`
  (terrain or water impact, setting `+0x91d` at `0x0048ba35` and CLEARING `+0x91f`). A healthy
  aircraft flown into a hill therefore closes the same gate as being shot down. `FUN_0048b920` also
  stops the mission clock directly (`FUN_0046c5c0`).
- **What ends the mission.** The player's destroy-or-crash animation being RESET while `+0x91d` is
  set and `+0x91f` is clear: `Callback 12`, the last event of the top-level `reset_state` list of
  `player-player` and the three `player_crash_*` defs. It reaches `LAB_00480710`'s tail at
  `0x004807f6` (player only), `FUN_0047e080` case `0xc` behind the guard at
  `0x0047e1fa`/`0x0047e208`, then `FUN_00443090` and the debrief `FUN_004194e0`. No argument and no
  enum: the four endings converge on `FUN_00443090` and the outcome is read there from the won flag
  `+0xc58` alone (`FUN_00463be0`). The lost flag `+0xc5c` only ever picks a sound and a wrap-up
  delay inside the gated tick, so **a mission already won when the player dies is still won**, and a
  death plays no `MISSION_LOST_SOUND` or `OBJECTIVES_LOST_SOUND` at all. What does play is the
  message-table 0xa9 kill line (`MSG_SHOT_DOWN`, [../org/vehicleDamage.md](../org/vehicleDamage.md)
  "The kill message") with combat-voice triggers 20/21 forced on the victim
  (`FUN_004b82d0`), and 0xa2 (`MSG_CRASH`) on impact (`FUN_0048b920`); both flush the radio queue through
  `FUN_00591f40(1)`.
- **Delay.** There is no countdown on this path. The gap between the kill and the debrief is the
  wreck's own fall to the ground plus whatever performs the animation reset. The only hard-coded
  wait is a 1000 ms `Sleep` in `FUN_004a0af0`, the freeze-frame capture reached from case `0xc`.
- **Limits.** Which native call performs the reset on the ordinary single-player path is not
  identified (`../org/vehicleDamage.md`), so the exact instant of the ending is bounded by the guard
  rather than read off the trigger. Multiplayer respawn (`FUN_00480480`) clears both flags before
  resetting the anim, which is why its own `Callback 12` is swallowed.
- **Persistence.** `Status.dat` is written on every ending (`FUN_0046b240`, unconditional);
  `Mission.NNN`/`Persist.NNN` are written only when `+0xc58` is set (`FUN_0046b450`). A death writes
  no world state (see [saved-games.md](saved-games.md)).

## Keywords the parser accepts that no mission authors

Collected from the keyword table at 0x625eac..0x626c34 and the parse paths above, so a reader
knows they are engine surface, not data: file-level `OBJECTIVES_WON_SOUND`,
`OBJECTIVES_LOST_SOUND`, `WIN_ANIM`, `LOSS_ANIM`, `NOLOSS`; per-objective `WAKE_OBJECTIVE`,
`SLEEP_OBJECTIVE_WHEN_I_COMPLETE`, `WAKE_OBJECTIVE_WHEN_I_SLEEP`, `HIDE_OBJ`, `END_TIMER`,
`RESET_TIMER`, `TIMER_ADJUST`, `ADJUST_TIMER_WHEN_I_COMPLETE`, `SET_AI_ATTACK_RADIUS`,
`SLEEP_ANIM`, `WON`, `LOST`, the 2nd..4th `BEGIN_DORMANT` floats, `INACTIVE19`..`INACTIVE100`,
and the `COUNTER` block (`ON_WAKEUP` / `ON_COMPLETE` / `TEST_COMPLETE` actions over named
counters with ops `SET`, `ADD`, `SUBTRACT`, `TEST_GT/GE/LT/LE/EQ/NE`, `FUN_00469440`).

## Reader rules and edge cases

- **Exact-match, order-free lookup.** Directives are found by exact string compare
  (`FUN_00579ff0`); misspellings and stray words are silently dead (see
  [Data anomalies](#data-anomalies)). The search recurses into nested lists, so a data string
  that collides with a keyword could false-match; no shipped file does.
- **Contiguous numbering.** Parsing stops at the first missing `OBJECTIVE%d`. An `OBJECTIVEn`
  authored with a `null` body (C2/M01's `OBJECTIVE64`) still parses: it starts awake with no
  conditions and completes as a no-op on its first eligible tick.
- **One completion per tick**, rotating start index (see conceptual model).
- **Napping clears completion.** `NAP_OBJECTIVE_WHEN_I_COMPLETE` is the only path that clears
  another objective's completed flag; wake alone never re-runs a completed objective.
- **The wake executor's early return** on an already-awake target truncates the remaining wake
  list (see chaining table).
- **Sound-group names resolve at parse time** (`FUN_00596120`); an unknown group (including
  the `your_sound_here` placeholders) stores null and plays nothing.
- **Instant Action and MP files are stubs.** Every `IA1` file is `MISSION_TIMER` +
  `PLAYER_INIT` + three null anim lists and no objectives; the IA director
  ([instant-action.md](instant-action.md)) supplies the mission logic. MP files add at most
  four keys the executable never reads.

## Data anomalies

Census findings that look like vocabulary but are not, each verified dead against the parser's
exact-match lookup:

- `WAKEUP_OBJECTIVE_WHEN_I_COMPLETE` (C1B/M03, objectives 13 and 14): not in the keyword
  table; both directives never fire in the shipped game. The intended spelling is
  `WAKE_OBJECTIVE_WHEN_I_COMPLETE`.
- `SET_AI_` (C5/M04, objective 19): truncated `SET_AI_NET` over five wingman/devastator
  entries; dead.
- `Change`, `to`, `mobile`, `net` (C4/M01, objective 24): a stray in-file comment, four bare
  words the lookup never queries.
- `WAKE_ANIM [anim, [node, child]]` (C4/M05, three gasbag-torpedo anims): the parser reads
  the second argument only when it is a plain string, so the `[cargozep3, gasbagN]` node pair
  is ignored and the anim starts with no AT_NODE.

A faithful runtime must not "fix" these: the original mission behaves as if they were absent.

## Worked example: C2/M01

The Hollywood mission's 82 objectives, walked end to end on paper against the semantics above.
The mission: fly to the Spruce Goose, sink four tug-and-barge convoys before they reach it,
survive an ambush, and escort the Goose out.

- **Openers.** OBJ1 (dormant 2 s), OBJ2 (10 s), OBJ3 (20 s) are pure radio: each wakes,
  plays its `WAKEUP_SOUND_GROUP`, and, conditionless, completes on its next eligible tick.
  OBJ3's completion naps OBJ5 for 90 s.
- **Primary 1** (OBJ4, `IDENTITY [PRIMARY, 1, MSG_BRF_HWM2_OBJ1]`, awake from start):
  completes when `kkgate/healthy` goes inactive, that is when the harbor gate is destroyed.
  It retargets the display (`REMOVE_OBJECTIVE_TARGET propane`, `ADD_OBJECTIVE_TARGET
  sprucegoose`), plays the success group, naps OBJ8 in 5 s, and kills OBJ5/6/7, the 60 s
  "you still need to hit the gate" reminder loop (OBJ5 naps 6, 6 naps 7, each replaying
  Betty's line).
- **Barge waves.** OBJ8 (woken above) klaxons, adds `tugandbarge01` as target, wakes OBJ13's
  chain: each of OBJ13/17/21/25 is `INACTIVE1 [tugandbargeN, thlthy]`, "that convoy's healthy
  hull is gone". Completing one swaps the displayed target to the next convoy, wakes the next
  wave's escort/reminder objectives, and kills the previous reminder trio (OBJ14-16, 18-20,
  22-24, 26-28, all 60 s `snd_..Betty_11` nag loops built from mutual naps). OBJ25 is
  `IDENTITY [PRIMARY, 2, MSG_BRF_HWM2_OBJ2]`; its completion wakes OBJ78, which naps OBJ32
  (the "primary 2 done" sound pair OBJ31/32) after 1 s.
- **Convoy scoring forks.** OBJ33-36 (`INACTIVE1 [tugandbargeN, thlthy]`, awake from start)
  kill the line objectives OBJ29-32 on the same convoy deaths that OBJ75-78 (woken through the
  tracker chain) nap those objectives to make them fire. The same event both revives and
  kills each line, several ticks apart along two paths, so which wins rides on the
  one-completion-per-tick rotation. The decoded semantics expose this as an authoring race,
  not something to smooth over in a reimplementation.
- **Path anims.** OBJ9/11/41 (`ANIM_STATE ... COMPLETION_COUNT 1` over `pathN_continue` /
  `pathN_accelerate` RUNNING) detect a convoy getting under way and wake follow-ups: OBJ9
  wakes OBJ10 (`WAKEUP_GENERATOR eshipg31 +3` and Tex's warning) and naps OBJ53, the
  patrol-boat sequence. OBJ37-40 watch `pathN_decelerate` RUNNING (a convoy stopping) and
  play Betty's notice, killing their own alternates.
- **Patrol boats.** OBJ53-57 and 58-63 are a timed cascade of naps whose payloads are
  `SET_AI_NET [patrolboat_egN, M2GoosePatrol]`: over about two minutes each boat is moved
  onto the Goose-guarding net. OBJ70 (`SET_AI_NET ... M2PatrolStop`, all six) parks them; it
  is napped by OBJ45.
- **The Goose flies.** OBJ69 (`ANIM_STATE fly_the_goose RUNNING`) naps OBJ42 15 s later;
  OBJ42 wakes OBJ65 (`DEDG [1, 2]`, group 1 down to two, Steele's line, wakes OBJ67, naps
  OBJ43 in 5 s). OBJ67 wakes the Hollywood Knight ambush (`WAKEUP_ENEMIES hkfirebrand_*`),
  their net assignment OBJ71, and the prebattle music cue OBJ82. OBJ66 (woken from OBJ12's
  `DEDG [1, 2]` branch) wakes the security Furys and music cue OBJ81. OBJ43
  (`IDENTITY [SECONDARY, 11]`, `DEDG [2, 0]`, group 2 wiped) plays the secondary-success
  group, wakes the stinger OBJ80, and stops Steele's queued chatter.
- **Endgame.** OBJ50 (`INACTIVE_COMPLETION_COUNT 4` over `g_engine1..8`, awake from start):
  four of the Goose's eight engines dead completes it, waking OBJ79 (which kills every
  reminder loop), killing the whole win path (OBJ46/47 among its kill list), and napping
  OBJ51, `INSTANTLOSS`, 30 s later. So losing four engines starts a 30 s fuse to mission
  loss. OBJ72/73/74 (`INACTIVE_COMPLETION_COUNT 1/2/3` over the same
  engines) play Betty's escalating damage warnings. OBJ46 (`IDENTITY [PRIMARY, 3,
  MSG_BRF_HWM2_OBJ3]`, `DEDG [1, 0]`, woken by OBJ68 after the ambush `DEDG [2, 0]`):
  destroying every group-1 enemy completes primary 3, wakes OBJ48 (`music_missionsuccess_sg`)
  and naps OBJ47, `INSTANTWIN`, 25 s later. OBJ45 wakes OBJ49 (`music_primaryobj_sg`).
- **Round-trip check.** Every one of the 82 objectives is accounted for: 1-3 self-wake on
  their `BEGIN_DORMANT` timers; 4, 9, 11, 33-41, 50, 69, 72-74 start awake; every other
  number appears in the wake/nap/kill chains above. Objective 64 is a null no-op block, and
  52 is dormant-forever and never woken (dead content, present but unreachable, which the
  state machine handles as authored).

The walk exercises every directive family the mission uses and contradicts none of the traced
semantics.

## Worked example: CM05's zeppelin-damage fuse

CM05 (`C3/M04`) authors the same fuse shape on the Pandora, and it answers a question the
mission raises at the controls: what actually fails the mission when Brigands are under the
hull. The answer is the airship's engine nacelles, counted, and nothing else.

- **The rungs.** OBJ6 (awake from start), OBJ7, OBJ8 and OBJ9 (each `BEGIN_DORMANT -1`) carry
  the same twelve `INACTIVEn [piratezep, <engine>, healthy]` paths over the hull's twelve
  engine nacelles, with `INACTIVE_COMPLETION_COUNT` 1, 2, 4 and 6. Each completion plays its
  own radio line (`snd_HI4LowDmg`, two `Sparks` lines, `snd_HI4HvyDmg`) and naps the next rung
  1 s later, so the chain climbs one rung per nacelle count reached.
- **The fuse.** OBJ9's completion, six nacelles out, releases the hull's dock stop point
  (`COMPLETED_STOPPOINT [M4PirateZep, 1, 0]`), wakes OBJ19, and naps OBJ10 for 45 s. OBJ10 is
  `INSTANTLOSS` with `BEGIN_DORMANT -1` and **no completion condition**, so it completes on its
  first tick awake: the nap running out is the loss, 45 s after the sixth nacelle dies.
- **The only route in.** No other directive in the file names objective 10, and OBJ10 is the
  mission's only `INSTANTLOSS`. So the loss counts nacelles, never Brigands, a zone, a timer or
  damage on the player, and five nacelles out leave the mission running indefinitely.
- **Whether one attacker is enough.** Each nacelle is its own 40 HP `WeaponHit` destructible
  whose death switches the `healthy` model off, which is the bit the rungs read: 240 HP of
  nacelle between a lone attacker and the fuse. All five `medbrigand_*` roster blocks carry a
  +0.5 `rating_biases` weight onto `piratezep` (the Medusa ace is weighted -1.0 off it), and
  nothing caps attackers, repairs a nacelle or re-arms a rung. One Brigand left alone therefore
  reaches the sixth nacelle given time, and the player's own rounds into the nacelles count the
  same way. A burning gasbag section is the second route to the same count, since each bay's
  death takes that bay's engines with it.

CSVM's runtime evaluates this as authored, pinned over the mission's own built world by the
`campaign-pandora-loss` suite: twelve nacelles switched on behind their own 40 HP pools, five
out leaving OBJ10 dormant with the mission running, the sixth napping it for 45 s, and the
outcome turning Lost 45.00 s later.

## Evidence & limits

Traced in `crimson.exe`: parser `FUN_00466b70` (keyword table 0x625eac..0x626c34), tick
`FUN_0046a490`, wake executor `FUN_00469af0`, condition tests `FUN_00469a60` / `FUN_00469ab0`
/ `FUN_004697a0` / `FUN_00465910` / `FUN_00465b40`, the completion executors named per row,
display readers `FUN_004acc20` / `FUN_004ad240` / `FUN_004a2350`, and the danger-zone notifier
`FUN_00446990`. Census over all 53 files ran against the extracted JSON in this repo's
`extracted\` tree.

Named gaps, each marked at its point of use: the consumers of `PLAYER_INIT`'s first, fourth
and fifth values; `WIN_ANIM` /
`LOSS_ANIM`'s consumer; and the radio queue's internal mode/delay parameters
(`FUN_0046caf0`), whose pre/in/won-battle special routing is decoded but whose exact fade
behaviour is not.
