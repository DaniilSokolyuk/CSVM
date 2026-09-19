# Multiplayer scoring and match end, decoded from `crimson.exe`

What a network match counts and how it stops. The placement side of the same match is
[`multiplayer-spawn.md`](multiplayer-spawn.md), and every message the match puts on the wire is
[`multiplayer-messages.md`](multiplayer-messages.md).

## One signed score per pilot

Every scoring event goes through `FUN_0046e1b0(pilotId, delta)`, which adds `delta` to the pilot
record's `+0x1c` and returns the new total. There is no separate kill counter in the match record:
the number the scoreboard ranks, and the number the kill target is compared against, is that one
signed running total, so a penalty moves a pilot **away** from winning.

The per-event amounts are config keys read by `FUN_004735b0`, each with a hard-coded fallback the
shipped data does not have to supply:

| Key | Global | Default |
|---|---|---|
| `score_suicide` | `0071c824` | **-1** (stored at `00473fc8`) |
| `score_kill` | `0071c850` | **+1** (stored at `00473ff2`) |
| `score_turret_kill` | `0071c7b0` | +1 |
| `score_zep` | `0071c808` | +100 |
| `score_zep_kill` | `0071d1d8` | +1 |
| `score_gas_kill` | `0071c84c` | +10 |
| `score_my_gas_kill` | `0071c7ec` | **-10** |
| `score_return_flag` | `0071c7fc` | +1 |
| `score_enemy_flag` | `0071c804` | +5 |

`FUN_0046ecd0` dispatches an event id to one of three per-mode tables on the mode field at
`+0x5c`. **Dogfight (modes 1 and 2) reaches `FUN_0046ed40`, which honours exactly three events**:
1 (`score_suicide`), 2 (`score_kill`) and 6 (`score_turret_kill`). The flag and zeppelin events
belong to the other two tables and cannot fire in a dogfight.

## Who the death is charged to

A death is reported by the dying pilot's own client, as message type `0x12` built by
`FUN_00498a90`, and handled by `FUN_00498bf0`. The packet carries a killer id at `+4` and a cause
at `+0xc`:

| Cause | Built when | Scored |
|---|---|---|
| 1 | the killer argument is non-zero | `score_kill` to the killer, **or `score_suicide` to the killer** when killer and victim share the team slot at **remote record `+0x3c`**, the `0x1090`-byte record `FUN_00499d80` looks up, not the pilot record |
| 2 | no killer at all | `score_suicide` to the pilot who died |
| 3, 4 | a zeppelin part or a turret owner | that owner's event |

⚠ **There is no last-damager memory anywhere on this path.** The credited killer is the attacker of
the one damage event that took the hull to zero, read straight off the damage call's own argument
(`004ba1c6`…`004ba1de`, `killer = ((weapon[+0x10] == 0x10) - 1) & attacker`, so one damage class
reports as self-inflicted even though an attacker dealt it). The other reporter,
`FUN_0048b920`'s explode path, passes killer `0` unconditionally, and a one-shot latch at
`0071d1dd` means whichever fires first owns the death. So a pilot who is shot up, breaks off and
then flies into the ground scores a **suicide**, and the pilot who damaged them gets nothing.

## How a match ends

`FUN_004996d0(reason)` broadcasts the end (message `0x17`) and `FUN_00499730` turns the reason into
the end-screen line:

| Reason | Raised by | Meaning |
|---|---|---|
| 1 | `00496e28`, the network tick, when the remaining-time query `FUN_0046c580` drops below 1.0 | the clock ran out |
| 2 | `00499343` / `004993ac`, inside the score broadcast, when a pilot's `+0x1c` reaches the target | somebody hit the score target |
| 3 | `0049afc7` | a mode-specific objective |
| 4 | `0049900f` / `004996a2`, after a death or a drop, when `FUN_004999f0` finds no two live pilots on different sides | nobody left to fight |

⚠ **A time-out has no overtime, no sudden death and no tiebreak.** Reason 1 sets the ended flag,
prints its line and runs the five-second wait at `0071c19c`; nothing computes a winner, and two
pilots tied at the top are simply tied on the board. The one thing that follows a time-out is the
end screen.

## The two limits are exclusive

`FUN_004136e0` reads the lobby's limit kind from `00642f94` and arms exactly one of them: kind 0
writes the time limit to `0071c180` and sets the score-limit-off byte `0071c1a2`; kind 1 writes the
score target to `0071c17c` and sets the time-limit-off byte `0071c1a1`. Each check is gated on the
other's byte, so the original never runs a match that can end either way. The remake's Dogfight
arms both rows at once, which is a remake decision and not a reading of this code.

## What the remake takes

`Flight/VersusMatch.cs` keeps the same signed score: `KillScore` (+1) per kill, `SuicideScore`
(-1) for a death with no killer, the kill-target row compared against the score rather than against
raw kills, and standings ranked by score with a tie at the top rendered as a draw. Kills and deaths
stay as separate display counters, which the original keeps in the pilot's career record rather
than in the match. The team-kill arm has no counterpart: the remake's Dogfight is free-for-all.
