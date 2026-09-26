# AI patrol nets, `ne0NNNNN.zrd` + `neindex.zrd`

Part of the [format documentation](README.md). The chapter-scoped patrol graphs the
original's AI flies: waypoint sets with an **explicit edge list**, referenced by every
AI-consuming reader family. Engine reader: `CSVM/src/Mech3/AiNets.cs`; the
`--debug-ainets` overlay (F13) renders them, and `CSVM/src/Flight/Ai/AiNetFollower.cs`
flies them as a patrol behaviour (`--ai=<plane>:<net>`), traversal along the edge list, an anchored
trailer ridden (`BL-377`), stop points held and released. `CSVM/src/Session/World/ZeppelinRuntime.cs`
is the `COMPLETED_STOPPOINT` consumer; a reached node's danger-zone fields send an aircraft down
the named `dzpathN` ribbon on rails (`Flight/Modes/DangerZoneRibbon.cs`,
[`org/aiPilot.md`](../org/aiPilot.md) "The danger-zone run").

## Archive locations

Each chapter's zrdr scope (`<Cx>/zrdr.zbd`) carries:

- **`ne0NNNNN.zrd`**, one file per net; the filename encodes the net id
  (`ne000010` → id 10). **222 files across the 8 chapters**
  (C1 29, C1B 22, C1C 20, C2 34, C2B 18, C3 30, C4 29, C5 40).
- **`neindex.zrd`**, the chapter's id → name table. 222 names install-wide, a perfect
  1:1 with the files, both directions, per chapter.

## Net name table

```
[ [ first, id0, "Name0", id1, "Name1", … ] ]
```

One flat list: alternating id/name pairs after a leading number. ⚠ **The first element is
NOT the pair count**, it is an allocation figure ≥ the count (C1 opens with 46 over 29
pairs; C2 with 35 over 34). Reading it as a count truncates or over-reads the list; parse
pairs to the end of the list instead.

The **name** is the join key everything else uses: `egen.json` `vehicle.nets`,
`zeppelins.json` `net`, and `objectives.json` reference nets by name. `aiv.json` field 0
references them by **id**.

⚠ **An anchored trailer makes the whole net RIDE its target** (
`BL-377`, [`org/aiPilot.md`](../org/aiPilot.md) "The trailer"). `[nodeIndex, "name"]` is not
decoration: the named object is resolved at net build and every node position the engine hands out is
offset by it, so the graph is a PATTERN carried around a moving thing rather than a fixed route. 76 of the
222 nets are anchored this way, and their targets say what the mechanism is for: `piratezep` (25),
`workersvoyagezep` (11), **`player` (11)**, `cargozep2` (10), `sprucegoose` (5), `cargozep1` (3),
`mptrailer` (3), and one each of `train01`, `cargozep3`, `beowulfzep`, `dantezep`,
`passenger_trengine`, `tanker`, `britbalmoral_2`, `barracuda`. The anchor is the LAST node in
every shipped case, sitting off the ring the other nodes form.

⚠ **A player-anchored net is not a friendly thing.** The 11 are `M4ReinfAce` and `M2Ace` (C1),
`M4MedusaAce` / `M3BritAce` / `M5Bravo` / `M5Charlie` / `M5Postpick` (C3), `M2Blacke` (C4),
`M2STI` / `M3Bravo` / `M4Miles` (C5), four of them `*Ace*` boss flights. Census of every `aiv`
block that flies one: **12 blocks, 8 of them team 2 and 4 team 1.** The enemies are
`blakebloodhawk_8` (C1/M04, on `M4ReinfAce`), `bhatbrigand_1/2/3` (C4/M02) and
`stihellhound_5_1..4` (C5/M02); the friendlies are `devastator_1/2` (C3/M05 and C5/M03). One
enemy generator also names one (C5's `M4Miles`). So the mechanism is team-blind: it is how the
original puts a flight ON the player, whether that flight is escorting or hunting.

⚠ **Two anchored nets are flown by ZEPPELINS, and neither is self-referential.** C1C's `SwanZep1`
(flown by `blackswanzep`) rides `workersvoyagezep`, and C2B's `Gemini2` (flown by `geminizep`) rides
`piratezep`. That is one zeppelin's route carried around another zeppelin, a rendezvous or an
escort. Both records are `deactivated`, so a mission script is what would wake them and nothing
flies either today. Worth knowing because a zeppelin net anchored to its OWN node would pin the
zeppelin in place, and the shipped data never does that.

⚠ **Pair order is meaningful and is not id order.** The engine builds its whole net table by
walking this list forward and indexes the nets themselves by table position (`FUN_004311c0`,
[`org/aiPilot.md`](../org/aiPilot.md)), so "the chapter's first net" (what every Instant Action
actor is given) is the first PAIR here: 10 `M4ReinfAce` (C1), 29 `Patrolboat3` (C1B),
25 `M1Defense` (C1C), 1 elsewhere. C1B and C1C open on something other than their lowest id (11
on both), so a sorted-by-id read answers the wrong net on two of the eight chapters.
`AiNets.LoadIndexPairs` is the ordered read; `AiNets.Load` sorts by id and `LoadIndex` is a map.

### Net names are mission-scoped

Nets are stored per CHAPTER but authored per MISSION, and the name says which: the `M<N>` prefix
names the mission that uses the net (`M4ReinfAce`, `M2Train`, `M5Patrol1`). Censused across all 8
chapters by resolving every `aiv` block's field 0 back to its `neindex` name: **103 of
the 222 nets are referenced by an `aiv` block, and every one of them is used by a single mission,
the one its prefix names.** The remaining 119 are referenced by `egen`, `zeppelins` or `objectives`
instead, or by nothing at all.

Two qualifications, both from the same census:

- **C2's first two missions carry each other's prefix.** `C2/M01` flies `M2First`, `M2Second`,
  `M2Patrol1`, `M2Bravo1`, `M2Charlie1` and `M2Dummy`; `C2/M02` flies `M1Security`, `M1Police`,
  `M1Knights` and `M1FilmShot`. M03 and M05 match their prefixes normally. Unexplained; recorded
  because a name-based guess about which mission owns a C2 net will be wrong on those two.
- **A few shared nets are used by two neighbouring missions**, always a `*Dummy` or a `*Bravo`:
  C4's `M1Dummy` (M01 + M02, 10 blocks) and C2's `M2Bravo1` (M01 + M02).

⚠ **Instant Action ignores all of this and takes the chapter's first net regardless**, which is
therefore some mission's asset rather than a patrol area meant for free play. The per-chapter table
of what that is (a patrol boat's route on C1B, the pirate zeppelin's own course on C2B, a net no
mission uses on C5) is in [`instant-action.md`](instant-action.md).

## Net data

```
[ [ null, 10.0, f×9, NODES, EDGES, TRAILER ] ]
```

One record per file: 14 elements, or 13 on the 8 files that omit the trailer. Element 1
is `10.0` on every file; elements 2–10 are nine floats, `0.0` on 170 of the 222 files.

**Elements 2–10 are read as the net's own three volumes**, activation, attack and return, each as
radius, upper and lower altitude band, in that order: the nine consecutive vehicle fields the net
assignment writes them into ([`org/aiPilot.md`](../org/aiPilot.md) "Net assignment", net `+0x24`
… `+0x48`). The census fits the reading: 46 files author only element 8 (`700.0`, a return radius),
one authors element 2 (`3000.0`, an activation radius), and the C4 nets that author more put
positive values in the upper slots (6, 9) and negative ones in the lower slots (7, 10). `AiNet.Volumes`
carries them; a vehicle assigned the net takes each non-zero one, and its roster block's own volume
slots then outrank them. ⚠ A reading off the assignment's field order and the census, not off a
named deserialiser; nothing beyond the radii has a consumer.

**Element 1 is almost certainly the net's minimum arrival radius in metres.** `CCENet+0x28`
is the floor under every edge's capture radius (`FUN_00431a90`, decoded in
[`aiPilot.md`](../org/aiPilot.md)), and its constructor `FUN_004303d0` seats exactly `10.0f`
there while zeroing every neighbouring field. Whether element 1 is that field or the field is
simply never deserialised does not change the value in play: **the floor is 10 m on all 222
shipped nets**, which is what a port needs.

### NODES, `[x, y, z]` (+ four optional fields)

World-space positions in the standard frame (right-handed Y-up, metres,
[gotchas.md](gotchas.md)). **2,268 nodes** install-wide; 2–44 per net, median 8.

A node list may carry up to four more numbers past `z`: **81 nodes carry two and 12 carry four**.
They are **four independent optional fields**, not two competing tag shapes, and the deserialiser
reads them positionally behind one element-count test each (`FUN_004304a0`, reached from the net
table's build through the loader's vtable slot `+0x8`; it widens every node into a 24-byte runtime
record):

| Element | Node record | Meaning |
|---|---|---|
| 3 | `+0x0c` int, default 0 | **stop-point id**; 0 means "no stop point" |
| 4 | `+0x10` bool, default false | **halt flag**, a zeppelin arriving here stops |
| 5 | `+0x11` bool, default false | **danger-zone flag**, reaching here starts a `dzpath` run |
| 6 | `+0x14` int, default −1 | the N of the `dzpathN` that run flies; negative takes the nearest end of any active zone |

The two widths shipped are therefore fields 3–4 (a zeppelin stop point) and fields 3–6 with 3–4
zeroed (a danger-zone entry). Nothing ships a node authoring both.

#### Stop points: `COMPLETED_STOPPOINT` writes the halt flag

`COMPLETED_STOPPOINT` is **not a condition** and a mission never waits on one. It is an action a
mission runs when an objective **completes**, in the same block as `SET_AI_NET` and
`COMPLETED_ZEPCANNONS` (`FUN_0046a490`, the objective-completion pass). Its clause is
`[netName, id, flag]`, the loader stores three words per clause, a `strdup`'d name, an **int** and
a **bool** (`FUN_00466b70`), and the handler (`FUN_0046a0d0`) resolves the net by name, requires
`id > 0`, finds the node by id (`FUN_004319a0`) and writes the flag onto that node's `+0x10`
(`FUN_004319d0`). So the file's flag is only the net's **initial** state; the script owns it
afterwards.

Only the **zeppelin** follower reads it (`FUN_004bf9d0`), and it reads two nodes at once:

- the node it currently sits on halts → hold station: desired pitch 0, heading kept, throttle 0
  (`FUN_004bf500`);
- else the node ahead halts → approach it, at full speed until 250 m out and then linearly down to
  zero, holding position inside 30 m (`FUN_004bf360`);
- else fly the leg normally.

The aircraft follower (`FUN_0041d1f0`) never looks at the halt flag. It reads fields 5 and 6
instead: on arriving at a node whose danger-zone flag is set it starts `dzpath%d` from field 6, or
the nearest end of any active zone when field 6 is negative. The run is flown on rails, the pose
written from the ribbon's spline, and ends with the walk re-seated on the net's nearest node
([`org/aiPilot.md`](../org/aiPilot.md) "The danger-zone run"). ⚠ The tag is the ONLY way a
patrolling aircraft enters a zone: the roster's `daredevil_chance` is rolled by a hit pilot in
pursuit alone, so a tagged net decides which zones its fliers take and which they skip.

**The lookup takes the FIRST node carrying the id**, which is why a run of nodes may share one:
`C2B`'s `PirateZep1` puts id 5 on eight consecutive nodes, and `COMPLETED_STOPPOINT
["PirateZep1", 5, 1]` arms the head of that run.

Census against the shipped scripts: **23 `COMPLETED_STOPPOINT` clauses across 11 missions, naming
14 nets. Every one resolves to a node by id, none is dangling, and 20 of the 23 change the flag
the file authored**; the three that restate it (`C1B/M03`'s `Vostok1` id 1 and `Klondike1` id 6,
`C5/M03`'s `M3Cargo3` id 5) all write 0 over a 0. The scripts write `flag = 0` 19 times and
`flag = 1` 4 times, so releasing a docked airship is the common case and arming a fresh stop
mid-route is the rare one.

The tagged population: **40 nets of 222 carry tagged nodes**, 36 with stop points and 4 with
danger-zone entries, on disjoint nets. All 36 stop-point nets are zeppelin routes by name; 31 are
directly referenced by a `zeppelins.json` `net`, and the remaining five are unreferenced alternates
beside referenced twins (`M1Cargo` beside `M1CargoAlt`, `SwanZep2` beside `SwanZep1`, plus
`Gemini1`, `M5Bombrun`, `M4PZRetreat`). No fighter flies a stop point. Of the 81 tagged nodes,
**27 carry id 0 and 54 a real id; 49 ship armed and 32 clear**.

The four danger-zone nets are all AIRCRAFT nets: C2's `M3StuntCourse` #16 (C2/M03's six
`hafury` racers, team 0; seven tagged nodes naming `dzpath1, 2, 3, 10, 6, 7, 9` in walk order,
out of the mission's thirteen zones) and `M1FilmShot` #31 (C2/M02's `secfury_5/6`; `dzpath8, 2,
3`), C5's `M1Cabbie` #5 (`autogyro_1`; `dzpath33`) and `M4MilesRun` #41 (a generator's net;
`dzpath34`). Every tag carries a number; nothing ships the negative form.

**Ids are allocated per CHAPTER across files, not per net**, so they collide between nets and are
meaningful only together with the net name the clause carries. C5's three cargo routes show the
counter plainly: `M3Cargo1` uses 1 and 2, `M3Cargo2` 3 and 4, `M3Cargo3` 5 and 6.

Worked route, `C3/M01`, the mission this was decoded for. `piratezep` spawns at
`(-1401, 500, -1413)`, which is `M1PirateZep`'s node 0, and the net is an open 8-node path:

```
node 0  (spawn)   node 2  [1, 1]  ← armed, stop point 1   node 7  [0, 1]  ← armed, id 0
```

It flies out to node 2 and docks there. `OBJECTIVE12` and `OBJECTIVE13` each carry
`COMPLETED_STOPPOINT [["M1PirateZep", 1, 0]]`, so completing either releases it; it then runs the
rest of the path and halts for good at node 7, whose id 0 no clause can ever address. `C1/M04` is
the same shape one step earlier: its `piratezep` spawns ON node 0, which is itself stop point 1 and
armed, so that PANDORA starts docked and the script launches it.

⚠ **An armed node with id 0 is a terminal dock.** `id > 0` is the script side's own gate, so a
zeppelin that reaches one stays there for the rest of the mission. That is what ends an open path,
which is why the shipped routes do not need to be loops.

Two neighbouring script ops retarget net-followers at runtime, **`SET_AI_NET`** (accepts a vehicle
*or* a zeppelin) and **`SET_AI_TEAM`**, which is the design's "retreat is expressed as a net
change, not a special mode", confirmed.

### EDGES, `[i, j]` node-index pairs

**2,149 edges** install-wide; 1–43 per net. **This list is the connectivity**, the graph
branches (a node with two successors is normal) and is *not* necessarily a closed loop or
a sequential path. Never connect nodes in list order; only the edge list is the route
structure. All indices are in range on every shipped net (asserted by the golden test).

### TRAILER, the attach/follow target

Five shipped shapes:

| Shape | Files | Meaning |
|---|---|---|
| `[nodeIndex, "name"]` | 76 | target + the net node it attaches at (`[10, "player"]`, `[7, "piratezep"]`) |
| `[-1, "name"]` | 4 | a named target with **no** attach node (C2 net 21, C4 nets 6/24, C5 net 25) |
| `[nodeIndex]` | 1 | attach node, no name (C2 net 33: `[3]`) |
| `[-1]` | 133 | no target |
| *(absent, 13-element record)* | 8 | no target |

The observed shapes include `[-1]`, `[nodeIndex, "name"]`, and the middle two rows
are the shapes it missed. Target names seen: `player`, zeppelin node names
(`piratezep`, `dantezep`, …).

## Worked example

`C1/zrdr/ne000010.zrd` = id 10 = `M4ReinfAce`: 11 nodes at y = 400, 10 edges closing a
cycle over nodes 0–9 (`[0,1] … [8,9], [0,9]`), node 10 edgeless and off that cycle, trailer
`[10, "player"]`. Every node has degree 2, so the graph is one closed loop, but the GEOMETRY
crosses itself: it traces a **figure eight** roughly 550 × 1030 m, and the anchor sits at the
centre of one of the two lobes, 255 m from the cycle's centroid. C1's other player-anchored net,
`M2Ace` #23 (12 nodes at y = 350, anchor node 12), is the same shape at 585 × 1105 m; it belongs to
`C1/M02`, whose `objectives` is the only file that names it, and no `aiv` block flies it. ⚠ Do not
read "closed cycle" as "ring": a plane flying one of these passes close to its target through one
lobe and about a kilometre away through the other.

## Scope limit

- **`<Cx>/<mission>/zrdr/net.zrd` is a different, unnamed, edgeless file**: the mission's
  multiplayer spawn table, decoded in [net-spawns.md](net-spawns.md). It shares only the word
  "net" with these graphs, carries no edges, and is read by nothing outside a network match.
  Do not build patrol behaviour on it.
- The danger-zone route ribbons (`dzpathN` gamez meshes, [missions.md](missions.md)) are not
  nets: a net node's tag hands the flier to one, which it then flies as a spline on rails
  ([`org/aiPilot.md`](../org/aiPilot.md) "The danger-zone run"), and `--debug-dzpaths` draws it.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
