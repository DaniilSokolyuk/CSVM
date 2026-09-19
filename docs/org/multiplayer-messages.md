# Multiplayer messages, decoded from `crimson.exe`

Every message the original puts on the wire: its type word, who builds it, who handles it, what
the payload holds, and whether DirectPlay is asked to guarantee it. What the same match counts
and how it ends is [`multiplayer-scoring.md`](multiplayer-scoring.md); where it puts a pilot is
[`multiplayer-spawn.md`](multiplayer-spawn.md).

The match half lives in `remote.cpp` (the source path string at `00628f50`) and the lobby half in
the `00413xxx` block; the DirectPlay wrapper under them is the `005b2xxx`/`005b4xxx` block.

## The framing is two words

Every packet opens with a 16-bit type at `+0` and a 16-bit total length, header included, at
`+2`. Nothing else is common: there is no sequence number, no checksum and no sender id in the
frame, because DirectPlay hands the handler the sender's player id as its own argument.

`FUN_005b4850(fromId, packet)` is the dispatcher. It walks the handler list headed at
`DAT_009c7874` and calls **every** entry whose registered type word equals the packet's first
word (`005b4851`…`005b4874`). A type with no entry is dropped without a word, and two entries for
one type both run. `FUN_005b4720(type, handler, 2)` adds one, `FUN_005b47a0(type, handler)`
removes it.

`FUN_00495310` registers the whole match set at session init and `FUN_004966c0` removes exactly
the same set at teardown, which is why a type that is live in a match is dead in the menus.

## The send call, and what "guaranteed" means here

`FUN_005b2640(data, size, guaranteed, toPlayer)` is the only send in the program. It builds the
flag word as `(guaranteed != 0)`, which is `DPSEND_GUARANTEED`, and adds `DPSEND_ASYNC` (`0x200`)
when `DAT_009c7869` is set, then calls `IDirectPlay4::SendEx` through vtable slot `+0xc4`.
`toPlayer` of 0 is `DPID_ALLPLAYERS`, so a 0 in the table below is a broadcast.

`DAT_009c7869` is read out of the session caps in `FUN_005b4930`: the flag is the caps bit
`0x10000`, and when it is clear the program puts up a "NOT Using Asynchronous Sends" message box
and sends synchronously. So asynchrony is a service-provider property, not a per-message choice;
the guarantee is the per-message choice, and it is what the remake's reliability classes model.

## The match message table

Sizes are the total the builder writes into the length word. "Guar." is the third argument at the
send call, and "To" is the fourth.

| Type | Builder | Handler | Bytes | Guar. | To | What it carries |
|---|---|---|---|---|---|---|
| `0x0d` | none | `LAB_00496ca0` | 4 | n/a | n/a | a peer left, synthesised locally (see below) |
| `0x0e` | none | `LAB_004978f0` | 4 | n/a | n/a | a peer arrived, synthesised locally |
| `0x0f` | `FUN_00496ee0` | `FUN_00497950` | `0x20` + `0xc` per hit, `0x2c` when a shot rides along | **no** | one peer | the aircraft state, below |
| `0x0f` | `FUN_004975d0` | `FUN_00497950` | `0x20` | yes | 0 | the same shape sent once at spawn, which is the one guaranteed state packet |
| `0x10` | `FUN_004988e0` | `LAB_00498950` | `0x1c` | yes | 0 | a weapon event: a word at `+4`, the two dwords at aircraft `+0x728`/`+0x72c`, and a three-float vector from `+0x294` |
| `0x11` | `FUN_0049bc00` | `LAB_0049bc80` | 4 + payload | yes | 0 | a console or log line relayed to every peer, gated on `DAT_0071d23d` |
| `0x12` | `FUN_00498a90` | `FUN_00498bf0` | `0x10` | yes | 0 | the death report, below |
| `0x13` | `FUN_00499270` | `FUN_004993f0` | `0xc` + 8 per row | yes | 0 | the whole score table: player count at `+4`, team count at `+8`, then one `(id, score)` pair per row |
| `0x14` | `FUN_00499490` | `FUN_00499530` | `0x10` | yes | 0 | the career counters: kills at `+4`, deaths at `+6`, then two dwords from `0071d2fc`/`0071d300` |
| `0x15` | `FUN_00499a50` | `LAB_00499b30` | 6 + text | see note | 0 or one peer | chat. An all-chat goes out **unguaranteed** to everybody; a team chat is sent guaranteed, once per teammate, to each peer whose `+0x18` matches the sender's team slot |
| `0x17` | `FUN_004996d0` | `FUN_00499730` | `0xc` | yes | 0 | the match end: a float clock at `+4` and the reason at `+8` |
| `0x18` | `FUN_00413170` | lobby | `0x18` + text | mode-dependent | 0, one peer, or each peer | a lobby notice with a string; the third argument selects broadcast, per-team or single |
| `0x1a` | `FUN_00413090`, `FUN_00413f70`, `FUN_004142c0`, `FUN_00414340`, `FUN_004143c0` | lobby | `0xc`, or 4 + payload from the last | yes | 0 | a lobby setting change; the subtype word at `+4` is 8, 8, 2, 1 and variable in builder order |
| `0x1c` | `FUN_0049a050` | `FUN_0049a170` | `0xc` | yes | the host (`DAT_0071d228`) | an objective or flag request, which only the host answers |
| `0x1d` | `FUN_0049a240` | `FUN_0049a300` | 8 + `0xc` per row | yes | 0 | the objective table: a count at `+4`, then `(id, holder, state)` per row |
| `0x1e` | `FUN_0049adf0` | `FUN_0049b0b0` | 6 + `0x1c` per zeppelin + `0x10` per event | **no** | 0 | the zeppelin state, below |
| `0x1f` | `FUN_0049b320` | `FUN_0049b3e0` | `0xc` | yes | 0 | a turret or part event raised by the local aircraft, packed as `part & 3 \| (kind & 7) << 2` at `+4` and an owner id at `+8` |
| `0x20` | `FUN_0049b210` | `FUN_0049b2c0` | `0xc` | yes | 0 | the same shape raised for a remote owner |
| `0x21` | none found | `LAB_004990d0` | | | | receive-only in this build |
| `0x22` | `FUN_00499110` | `LAB_00499190` | `0x10` | yes | 0 | a damage attribution: victim id at `+4`, attacker id at `+8`, a dword from the weapon record `+0x10` at `+0xc` |
| `0x23` | `FUN_0049bd00` | `LAB_0049bd70` | `0xc` | yes | one peer | the ping: two `GetTickCount` stamps, sent only to a peer whose id is at or above ours, so one side of each pair pings |
| `0x24`, `0x25` | none found | `LAB_0049bde0` | | | | receive-only in this build, both on one handler |
| `0x27` | `FUN_004135f0` | lobby | 8 + `0x20` per player + a tail | yes | 0 | the lobby roster, below |

⚠ **Types `0x02`, `0x03`, `0x05`, `0x06`, `0x07`, `0x0d` and `0x0e` never cross the wire.**
`FUN_005b2820` and `FUN_005b24a0` build them on the stack from DirectPlay's own system messages
and hand them straight to the dispatcher (the type and length pairs are written as immediates at
`005b2898`, `005b29ad`, `005b29db`, `005b2aa8`, `005b2ad0`, `005b2ad9` and `005b2618`). They are
4 or 8 bytes and they are local notifications, so a remake has no counterpart to serialise: the
transport's own connect and disconnect callbacks are the counterpart.

## The aircraft state packet

`FUN_00496ee0(force)` walks the remote list and sends one packet **per peer**, unguaranteed:

| Offset | Field |
|---|---|
| `+0x00` | type `0x0f`, then the total length |
| `+0x04`, `+0x08`, `+0x0c` | position, three dwords straight off the aircraft at `+0x204` |
| `+0x10` | orientation, three angles packed into one dword as `yaw << 0x15 \| pitch << 0xb \| roll` |
| `+0x14` | motion, packed the same way as `a << 0x14 \| b << 10 \| c` |
| `+0x18` | `GetTickCount` at build time |
| `+0x1c` | throttle in the low byte, a 7-bit field at bits 8..14, and one flag at bit 15 |
| `+0x1e` | a 3-bit shot count at bits 4..6 and a 4-bit hit count at bits 0..3 |
| `+0x20` | when the shot count is above zero, three dwords of shot state |
| tail | the queued hit records, 12 bytes each |

⚠ **The original batches its hit reports onto this unguaranteed packet.** A hit is queued by
`FUN_004987d0` as a 12-byte record onto the list at remote record `+0x1088` (count at `+0x108c`),
appended here, and dropped by `FUN_00498760` the moment the packet is handed to the send. Nothing
resends it. The 4-bit count field also means the sixteenth queued hit of a tick is lost before it
is ever sent. The remake does not copy this: Decision 8 makes the hit its own reliable message,
which is a remake decision and not a reading of this code.

The send rate is per peer and adaptive. `FUN_00497850(peerCount, distance)` returns the interval,
stored at remote `+0x106c`, and a peer is skipped entirely until its interval has passed unless
the caller forces it. The same walk pings a peer (type `0x23`) when its own 10-second timer at
`+0x1068` has expired.

## The death report

`FUN_00498a90(killer, param, source)` builds the 16-byte `0x12` once per death, behind the
one-shot latch `DAT_0071d1dd`, and calls the handler locally as well as sending it:

| Offset | Field |
|---|---|
| `+0x00` | type `0x12`, then length `0x10` |
| `+0x04` | the credited id, which is a remote record id and not a pilot id |
| `+0x08` | the caller's second argument, passed through untouched |
| `+0x0c` | the cause word, 1 to 4 |

The cause is chosen in the builder: a non-zero killer gives 1; no killer and no source gives 2
with the victim's own id at `+4`; a source that `FUN_0049b4a0` resolves gives 3; a source that
`FUN_00499de0` resolves gives 4. What each cause scores is in
[`multiplayer-scoring.md`](multiplayer-scoring.md).

### The team comparison is on the remote record, not the pilot record

`FUN_00498bf0`'s cause-1 arm compares `piVar5[0xf] == piVar3[0xf]`, and both pointers come from
`FUN_00499d80`, the lookup over the remote list headed at `DAT_0071c7a4`. That record is the
`0x1090`-byte object `FUN_00499c90` constructs, so the field is **remote record `+0x3c`**, and the
pilot record's `+0x08` is not read anywhere on this path.

What fills `+0x3c` is `FUN_00495310`: the pilot's own index (pilot record `+0x18`) when
`DAT_0071d89c` says the mode has no teams, and `FUN_0046f3c0(pilotRecord + 0x08)`'s `+0x18`, the
team's index, when it does. The colour table at `00628eb4` is indexed by the same slot, and
[`../formats/net-spawns.md`](../formats/net-spawns.md) reads it as the team half of the spawn slot
at `00496bba`. The team chat arm of `FUN_00499a50` compares the same field.

So the friendly-fire arm is correct as written and needs no team lookup: in an un-teamed match the
slot holds distinct per-pilot indices, two pilots never match, and the arm cannot fire.

## The zeppelin state packet

`FUN_0049adf0` sends type `0x1e` unguaranteed on a fixed **0.5-second** cadence (`_DAT_0071d238`
is re-armed to `now + 0.5` after each send). It carries a zeppelin count in the byte at `+4`, then
`0x1c` per zeppelin (position, three fields from the object, and a packed word holding a 4-bit
event count), then `0x10` per pending event. Unlike the aircraft state it is a broadcast, and like
the aircraft state its event tail is dropped once sent.

## The lobby roster

`FUN_004135f0` sends type `0x27`, the widest message in the protocol and the only one the remake
borrows an id from for a roster. A player count sits at `+4`, then `0x20` per player: the player
index, a second dword, an 18-character name copied with `strncpy` and zero-terminated at `+0x1a`,
a dword at `+0x1c`, and a variable tail of that player's own list. It is sent guaranteed to
everybody, whole, every time anything changes; there is no delta form.

`0x27` is the highest type word the program uses, which is why the remake mints its own ids above
it.

## What the remake takes

`Net/NetMessages.cs` keeps the two-word header and the original's id for every event that has a
counterpart here: `0x0f` aircraft state, `0x10` fire, `0x12` death with its four causes, `0x13`
score, `0x17` match state, `0x22` hit and `0x27` seat roster. Damage, spawn, the mission
director transition and the join handshake have no counterpart, so they are minted at `0x40`,
`0x41`, `0x42` and `0x43`, above the ceiling above. The handshake carries the master seed, the
host's clock and the seat the joining peer was given; the original needs none of the three,
because it draws from no shared stream and hands out no seat.

The remake does not take the batching: its hit and damage messages are reliable and separate, its
score message is one seat rather than the whole table, and its roster carries the match seed,
which the original has no need of because it never draws from a shared stream. The packed angle
and motion dwords are not taken either; the remake spends 8 bytes on a quantised quaternion and
12 on a float velocity, which is the trade `Net/NetMessages.cs`'s width budget exists to hold.
