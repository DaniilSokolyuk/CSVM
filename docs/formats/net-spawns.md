# Multiplayer spawn table, `net.zrd`

Part of the [format documentation](README.md). The per-mission table the original places
multiplayer pilots on at the start of a match. One file per mission folder in the mission's own
zrdr archive (`<Cx>/<mission>/zrdr.zbd`), **45 install-wide**. Engine reader:
`SpawnPoints.LoadNetFreeForAll` in `CSVM/src/Flight/Modes/SpawnPoints.cs`; what the executable then does
with a picked entry is [`org/multiplayer-spawn.md`](../org/multiplayer-spawn.md).

⚠ **Despite the name this is not a patrol net.** It carries no edge list, no stop points and no
trailer, and its only consumer places a pilot once. The chapter-scoped `ne0NNNNN.zrd` graphs in
[ai-nets.md](ai-nets.md) are a different format with a different scope.

## Contents

- [Record](#record)
- [Why the node counts are quantised](#why-the-node-counts-are-quantised)
- [What the 45 files hold](#what-the-45-files-hold)
- [Block shapes](#block-shapes)
- [What the remake reads](#what-the-remake-reads)

## Record

```
[ [ [x, y, z, heading°], … ] ]
```

One flat group of four-float nodes, each **the same record `ia.json`'s `spawn_points` entries
use** ([spawns.md](spawns.md)): a world position in the standard frame (right-handed Y-up, metres,
[gotchas.md](gotchas.md)) and a yaw in degrees. Nothing else is authored, and nothing else is read.

The field meanings come from the one reader and the one consumer:

- **The reader is `FUN_00495310`**, the multiplayer session init in `remote.cpp` (the source path
  string at `00628f50`). It is the single xref to the string `net.zrd` at `00628f18`
  (`0049555e`). For each node it allocates a **16-byte** record, copies the node's first four
  elements into it, and appends it to the global list at `0071c81c` (count at `0071c820`). So the
  runtime record is four floats and no more.
- **The consumer is `FUN_004969b0`**, called from that same init with the player aircraft. It
  passes the record's own base address to the player placement `FUN_0047f740` as the position
  vector (written to aircraft `+0x204`/`+0x208`/`+0x20c`), and multiplies element 3 by
  `0.017453292519943295` (the double at `006040e8`, degrees to radians) into the **yaw** slot of
  the rotation triple it hands the same call (`00496bde`, `00496bf0`), leaving pitch and roll at
  zero.

There is no team field, no type field and no radius field in the record: the team is not stored
per entry, it is the **block** an entry sits in.

## Why the node counts are quantised

`FUN_004969b0` addresses the table by a slot number rather than searching it
(`00496bae`…`00496bd9`):

```
slot = playerIndex                      ; aircraft's remote record +0x38
if (teams are in play)                  ; the byte at 0071d89c
    slot += team << 4                   ; +0x3c, shifted by 4 at 00496bba
```

then walks the list numbering entries from 1 and takes the first entry whose ordinal equals the
slot, falling back to the **first** entry when nothing matches.

**The shift by 4 is the quantisation: a table is 16 entries per block.** Block 0 is the un-teamed
free-for-all, and each further block belongs to one team, team ids being handed out from 1
(`FUN_004136e0`). The shipped counts follow exactly:

| Mission | Nodes | Blocks |
|---|---|---|
| `MP1` (8 files) | **80** | free-for-all + 4 teams |
| `MP2` (5 files), `MP3` (8 files) | **48** | free-for-all + 2 teams |
| campaign `M0x` | 8, or an empty file | never read, see below |

So the "quantisation" is not a rounding of an authored count; it is the address arithmetic. A map
that supports four-team play has to author five blocks, and one that supports two has to author
three.

## What the 45 files hold

**23 distinct payloads**, which is what the file set looks like once the copies are collapsed:

| Payload group | Files | What it is |
|---|---|---|
| per-map multiplayer tables | 21 files, **20 payloads** | one authored table per multiplayer map; `C1C/MP1` and `C2B/MP1` are identical to each other |
| the campaign placeholder | **14 files, 1 payload** | `C1/M02`, `C1/M04`, `C1B/M03`, `C1C/M01`, `C2/M01`, `C2/M02`, `C2/M03`, `C2/M05`, `C2B/M04` and all five `C3` missions carry the same 8 nodes |
| an empty table | **9 files, 1 payload** | all five `C4` missions and `C5/M01`–`M04` ship `[null]` |
| `C1/M05` | 1 file, 1 payload | 8 nodes of its own |

⚠ **The campaign copies are dead data and must not be read as spawns.** Their only consumer is the
multiplayer session init, which a campaign mission never runs. The shared 14-file payload says so
on its own terms as well: its nodes sit at altitudes 0 to 34 m, and three of its eight headings are
`-225`, `301` and `235`, outside the range any heading consumer accepts. It is an authoring
template left in the folders, and the later chapters dropped even that.

## Block shapes

The two block kinds are visibly different, which is what a free-for-all versus a team start should
look like.

**A free-for-all block can be 16 individually placed points.** `C1/MP1`'s spans about 5.4 km by
5.2 km of the map, altitudes 125 to 371 m, and every entry carries its own heading.

**A team block is a staging stack**: a few ground positions within a few hundred metres of each
other, repeated up a ladder of altitudes, the whole block on one heading. `C1/MP1`'s four team
blocks are each 4 positions at 4 altitudes 100 m apart, on headings `-130`, `177`, `-80` and `-57`.
`C1/MP3`'s two team blocks are the same shape; its free-for-all block is a stack too, 2 positions
at 8 altitudes, and its whole table is on heading `-100`.

`C1/MP2` puts its free-for-all block and its first team block over the **same** ground positions
and separates them only by altitude ladder (200/225/250/275 m against 200/300/400/500 m), so the
two block kinds are not always in different places.

## What the remake reads

`SpawnPoints.LoadNetFreeForAll` returns **block 0 only**, up to `SpawnPoints.NetBlock` (16)
entries, and `SpawnPicker.LoadSpawnList` takes them only for a Dogfight launch (`--vs`) on a
mission that ships no `ia.json`. That is the original's own gate: the un-teamed match is the one
that reads block 0, and no other mode reads the file at all. The team blocks have no consumer here
because the remake's Dogfight is free-for-all.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims
they support. Two further limits:

- **Whether the pilot's own index is 0- or 1-based is not settled statically.** The walk numbers
  entries from 1 and falls back to the first entry, so a 0-based index would put pilots 0 and 1 on
  the same entry and leave the upper half of each block unused. Eight is the ceiling either way:
  the per-pilot colour table at `00628eb4` holds exactly 8 entries and the respawn bearing steps by
  45 degrees per index. It does not change the block structure, and the remake walks the block from
  its own base index rather than reproducing the original's ordinal arithmetic.
- The census above is of this install's 45 files. A different release could author a map with a
  different number of team blocks; read the count, do not assume 80 or 48.
