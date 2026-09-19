# Multiplayer spawn, decoded from `crimson.exe`

Where the original puts a pilot in a network match: the opening placement off the mission's
`net.zrd` table, and the different rule a respawn takes. The table's own file format is
[`formats/net-spawns.md`](../formats/net-spawns.md); this page is what the executable does with it.
What the same match counts and how it ends is
[`multiplayer-scoring.md`](multiplayer-scoring.md), and every message it puts on the wire is
[`multiplayer-messages.md`](multiplayer-messages.md).

All of it lives in `remote.cpp` (the source path string at `00628f50`): `FUN_00495310` is the
session init and `FUN_004969b0` is the placement it ends with.

## The two placements are different rules

`FUN_004969b0(aircraft)` branches on the aircraft's byte at `+0x6f0`, which the session init sets
to 1 immediately before calling it and which the placement clears at `00496c04`. So the flag means
"this is the opening placement", consumed once per match.

| Condition | Position | Heading |
|---|---|---|
| flag set, **or** fewer than two pilots in the match | the `net.zrd` entry for the pilot's slot | the entry's own heading |
| flag clear and two or more pilots | the centroid of the other live aircraft, displaced on a bearing | that bearing's complement |

**The opening placement is the table.** The slot is the pilot's index, plus `team << 4` when the
match has teams, and the list is walked from ordinal 1 with the first entry as the fallback
(`00496bae`…`00496bd9`). The entry's heading goes in as yaw only, pitch and roll zero.

**A respawn does not read the table at all.** It sums the other live aircraft's `+0x204`/`+0x20c`
(x and z), divides by their count, and adds a displacement of `cos`/`sin` of
`pilotIndex × 0.7853982` (the quarter-turn constant at `00608168`) times a radius global. Altitude
comes from a terrain query (`FUN_004c76e0`): the sampled height plus a margin when it is above
900 m, and a flat 900 m when the query fails or the ground is lower (`00496b52`…`00496b75`).
⚠ The radius and the margin (`00628f08`, `00628f0c`) are written at runtime by two other sites
each, so their initialised values are not the values in play and are not recorded here.

The bearing stepping by exactly 45 degrees per pilot index is the second independent sign that the
match holds at most **eight** pilots; the first is the per-pilot colour table at `00628eb4`, which
has exactly eight entries and is indexed by the same field.

## The opening spawn does not use PLAYER_INIT

Every other mode reaches the player placement `FUN_0047f740` through `FUN_0047f1f0`, which takes
the mission's throttle and speed from `objectives.zrd`'s `PLAYER_INIT`
([`formats/spawns.md`](../formats/spawns.md)). The multiplayer opening placement calls
`FUN_0047f740` itself with two immediates instead:

| Argument | Value | Meaning |
|---|---|---|
| speed | `0x41cd999a` (`00496be6`) | **25.7 m/s** (57 mph), negated onto the nose axis exactly as the story spawn's is |
| throttle | `0x3f59999a` (`00496be1`) | **0.85**, written to the lever at aircraft `+0x124` |

A multiplayer mission still ships a `PLAYER_INIT` record, and it is still applied on mission load;
the session init then re-places the pilot over it. So the record's spawn is visible in the data and
is not the pose a match starts from.

## Which mode uses teams

`FUN_004136e0` turns the lobby's game-type setting into the mode global at `0071c194`, and then
sets the team flag at `0071d89c` to "the mode is not 1":

| Lobby setting | Mode | Teams |
|---|---|---|
| 1, with no teams formed | 1 | no, the slot is the pilot index alone |
| 1, with teams formed | 2 | yes |
| 0 | 3 | yes |
| 2 | 4 | yes |

Mode 4 is capture the flag: it is the mode the respawn's own extra branch adjusts for, and it is
the one the `snd_CTF*` sound keys (`00628f94` onward) and the `score_return_flag` /
`score_enemy_flag` score keys (`00627754`, `00627768`) belong to. Team ids are handed out from 1,
which is what leaves block 0 of the table to the un-teamed match.

## The per-pilot colour table

`00628eb4` holds eight dwords and then zeros from `00628ed4`. It is indexed by the pilot's own
index with no bound check at `00495893` (`MOV ECX, dword ptr [EDX*0x4 + 0x628eb4]`, `EDX` from the
aircraft's `+0x3c`) and again at `00497ae6`, and each entry is stored to the aircraft at `+0x1060`.
The stored bytes are `81 2d 2d 00`, `2d 2d 81 00`, `2d 81 2d 00`, `81 81 2d 00`, `81 2d 64 00`,
`66 81 2d 00`, `45 7c 81 00`, `66 2d 81 00`. Which channel the consumer takes first is not decoded:
a search for a reader of `+0x1060` finds only those two writers.

The original's pilot index is 1-based here, so its eighth pilot reads the first zero dword past the
table. `Net/NetSeats.cs` does not reproduce that: every seat gets a colour, seats 0 to 7 from the
eight dwords read as red, green, blue, and seats 8 to 15 from the channel-wise complement of seat
minus 8. Both readings are TUNE (`BL-1017`).

## What the remake takes

The remake's Dogfight is the un-teamed match, so it takes the opening rule and the opening
throttle and speed (`SpawnPicker.LoadSpawnList` and `SpawnPicker.StartState`, over
`SpawnPoints.LoadNetFreeForAll`). It does **not** take the respawn rule: a centroid displacement
puts a returning pilot next to the pack, which is the camping problem
`Flight/VersusSpawnRotation.cs` exists to solve, and that rotation stays as it is. The stunt
race's abreast starting grid is selected only when a race exists and never touches a Dogfight:
four dogfighters 60 m apart on one heading is a head-on merge every round.
