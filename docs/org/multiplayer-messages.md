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

The builder runs on the host alone (`FUN_005b4210`, my id `[0x9c7860]` against the host's
`[0x9c7864]`), once the clock `[0x9ad748]` reaches `_DAT_0071d238`; the half second is the float at
`0x006032e0`, re-armed at `0x0049b00b`. It walks the zeppelin vector `0x71df84`..`0x71df88` in
order, so a zeppelin is named by its index in that vector, and sends through
`FUN_005b2640(buf, len, 0, 0)`: unguaranteed, to everyone. One zeppelin's `0x1c` bytes:

| Offset | Field | Source |
|---|---|---|
| `+0x00` | position, three floats | the hull node's world position, `[[zep+0x1c]+0x38]+0x54` |
| `+0x0c` | speed, float | `zep+0xa4`, the throttle `FUN_004bf240` writes |
| `+0x10` | pitch, float radians | `zep+0x30` |
| `+0x14` | yaw, float radians | `zep+0x2c` |
| `+0x18` | part states, 2 bits per part | `FUN_004c0c20` over the list at `zep+0x5c` |
| `+0x1a` | event count in bits 0..3; above it per-part bits of the list at `zep+0x7c` | `[0x71c838] & 0xf`, `FUN_004c0d80` |

Each event is `0x10` bytes, a target point as three floats and a `u16` part index at `+0xc`, taken
from the global list `0x71c834` that `FUN_0049b030` fills when a zeppelin cannon shoots. After the
send, when `[0x71c190]` is 0 and a zeppelin's dead flag `zep+6` is set, the builder ends the
zeppelin match: `FUN_0046ecd0(index + 1, 3)`, `FUN_004996d0(3)` and the score table `FUN_00499270`.

The receiver, `FUN_0049b0b0`, writes zeppelin `i` of the packet into entry `i` of its own vector with
no check against its length. The position goes to a target at `zep+0xe4` and the speed to `zep+0xa4`.
The facing becomes a unit forward at `zep+0xf0`: `(-sin yaw cos pitch, sin pitch, -cos yaw cos pitch)`.
`FUN_004c0cb0` applies the part states (1 and 2 through `FUN_004455e0`, 0 and 3 through
`FUN_00445620`). Each event goes to `FUN_004c0d00(part, point)`, which fires `wep_28` (the string at
`0x62b828`) from that part toward the point through `FUN_005aef40`. The high bits of `+0x1a` are not
read.

A guest does not step its own path. `FUN_004bf9d0` asks `FUN_00470550`, which answers false on the
host and otherwise runs the chase:

- `k = FUN_0053e2e0(2 dt)`, which is `e^(-2 dt)` from a table of `exp(-i/51)` (the constants at
  `0x60912c` and `0x609128`, clamped at the 5.0 of `0x6036bc`);
- the hull position becomes `k * position + (1 - k) * target` (`FUN_00538c50`), written to `zep+0x20`;
- the forward from its own yaw and pitch is blended the same way toward `zep+0xf0` and normalised
  (`FUN_00538d20`, `FUN_00422690`); pitch is `asin(forward.y)` and yaw `atan2(-forward.x, -forward.z)`;
- the target is carried on by `speed * dt` along the blended forward, so between packets the target
  is dead-reckoned;
- `FUN_004bf930` writes the pose.

The guest's velocity query `FUN_004bf7f0` answers speed times the received forward. On a straight
leg a guest's hull trails the host's by the speed times the link's latency, plus the speed over the
chase rate, `v / 2` metres.

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
director transition, the join handshake and a seat's ask to be spawned again have no
counterpart, so they are minted at `0x40`, `0x41`, `0x42`, `0x43` and `0x44`, above the ceiling
above. The host-owned world's four (AI state, AI fire, a guest's hit claim on an AI, and a world
event) are minted at `0x45` to `0x48`, below, the clock ping at `0x49`, the lobby's session advert at `0x4A`, the zeppelin path at `0x4B`, a generator's AI launch at `0x4C` and the surface-vehicle patrol at `0x4D`. The handshake carries the master seed, the host's clock and the seat the joining peer was
given; the original needs none of the three, because it draws from no shared stream and hands
out no seat. The ask carries a seat and nothing else: the original's client takes its own
respawn, while here the host owns every placement and answers the ask with a spawn event.

`0x17` is the one original id the remake widens. The original's twelve bytes carry a clock and a
reason, which is all a client that runs its own countdown needs. The remake's twenty carry the
remaining time, both limits, the reason and the host's session clock, because a guest here runs
no countdown of its own: it is told the clock, and that field is also the reading its
`NetClockSlew` takes an offset from, since the periodic tick is the only message a running match
repeats. Both limits ride even though the original arms exactly one, which costs four bytes a
second and leaves an exclusive lobby nothing to change on the wire.

The clock ping is minted at `0x49` rather than taking `0x23`, though it has the original's shape:
twelve bytes, two stamps, one peer, a ten-second timer (`Net/NetClockPing.cs`). The stamps
differ in kind. The original's are `GetTickCount` milliseconds and it pings to learn a peer's
latency, one side of each pair asking; the remake's are session-clock seconds, and only a guest
asks, because the host's clock is the one being read. It is unreliable where the original's is
guaranteed, since a retransmitted question would measure the retransmission as link. The guest
reads the host's clock forward by half the round trip, and the first answer replaces the offset
the handshake opened on. It is also the periodic clock reading a campaign session has, where no
match state ticks.

Fire (`0x10`) is unreliable and unsequenced where the original's is guaranteed, and rides a
channel per seat apart from that seat's state (`Net/NetChannels.cs`). Sequenced beside state, a
burst sent between two samples was discarded as overtaken whenever jitter swapped them, and
rounds fired on one step would discard each other the same way. Unsequenced, a burst is spawned
whichever order it lands in; a reliable burst would stall behind a retransmission.

The remake does not take the batching: its hit and damage messages are reliable and separate, its
score message is one seat rather than the whole table, and its roster carries the match seed,
which the original has no need of because it never draws from a shared stream. The packed angle
and motion dwords are not taken either; the remake spends 8 bytes on a quantised quaternion and
12 on a float velocity, which is the trade `Net/NetMessages.cs`'s width budget exists to hold.

## The mission director

`0x42` carries one event of the host's objectives graph as a code, an id and the host's clock,
reliable, in the order the graph raised it. The host is the only sender; a guest's graph is
replicated and changes only by replaying these (`Session/NetDirectorLink.cs`). The message is 16
bytes: the header, a `u16` code, two bytes of padding, an `i32` id, and an `f32` `HostClock`, the
host's session time when its graph raised the event. The codes are `NetDirectorEvent`:

| Code | Event | Id |
|---|---|---|
| 1 to 7 | Woke, Napped, Completed, Killed, Slept, Expired, Hidden | objective number in the low 16 bits, the objective whose completion caused it in the high 16 (0 for none) |
| 8 | Settled: a completion and every chain it ran are done | objective number |
| 9 | The mission countdown expired | 0 |
| 10 | Ending decided | outcome, plus `0x100` when the objectives-won or objectives-lost sound played |
| 11 | Mission ended, after the host's wrap-up | outcome |

A code the guest does not know is dropped. `Hidden` exists because `HIDE_OBJ` retires an objective
without the completion bookkeeping, and a guest that did not hear it would complete that objective
later by a rule of its own. Nap lengths are not sent: the guest reads them off the same script,
from the source objective's `NAP_OBJECTIVE_WHEN_I_COMPLETE` or the objective's own nap.

What a guest replays, and what it derives from what it replayed:

- **Replayed, presentation.** Each transition runs the same bookkeeping on the guest as on the
  host: `WAKE_ANIM` and `SLEEP_ANIM`, the wake, completed, class-complete and ending sound groups,
  `STOP_QUEUED_SOUNDS`, the objective and other target lists, the help labels, the countdown's
  reset, adjust and end actions, and the display rows. The countdown's display runs locally
  between events, pinned at zero, and only the host's code 9 expires it.
- **Replayed for now, world.** `WAKEUP_ENEMIES`, `WAKEUP_TURRETS`, `WAKEUP_ZEP_TURRETS`,
  `WAKEUP_GENERATOR`, `SET_AI_TEAM`, `SET_AI_NET`, `SET_AI_ATTACK_RADIUS`,
  `COMPLETED_ZEPCANNONS`, `COMPLETED_STOPPOINT` and `START_TAXI` run through the guest's own world
  seam. A guest's AI aircraft is a replicated airframe (below), so a warp, a net or a team set on
  it is overwritten by the host's next sample. `WAKEUP_ENEMIES` does not wake it: the host's wake
  arrives as a presence event (below), which is what takes it out of `Inert` so the samples show.
  `WAKEUP_GENERATOR` credits the guest's own cycles, whose aircraft launches are refused (below).
  Turrets still act on these locally. A guest's zeppelin or surface vehicle takes the net but walks
  nothing, since its path is the host's samples.
- **Drawn by the host, world.** `WARP_VEHICLE` picks one of its waypoints on the world stream, and
  its only authored use hides an aircraft that stays `Inert`, which the host sends no samples for.
  The host sends the pick as world event 4, and a guest's directive draws nothing: it places the
  aircraft on the host's pick, when the directive runs or when the pick arrives, whichever is later.
  `DEDG`'s engagement widening is a side effect of testing a condition, so a guest never runs it.
- **Derived, cutscene codes.** The presentation codes (20, 2, 11, 1, 10, 913 and 914, 666 and 667,
  951, 86) are raised on the guest by its own animation runtime, playing the definitions its
  replayed `WAKE_ANIM` or the shared start list started. Sending them as well would apply each one
  twice.
- **Refused on a guest.** Code 13, the docking's mission completion, is refused by a replicated
  graph; the host's own code 13 decides the ending and codes 10 and 11 carry it. The condition
  hooks (a player lost, a danger zone completed) and a direct wake are refused the same way.
- **Not the director's.** 801 to 803 (a Black Hat launch), 968 (a wingman taken out) and 800 (a
  generator's credit) change AI world state and still run locally on each end. A Black Hat launch
  reactivates a block built dormant with its ordinal, so on a guest it waits for the host's presence
  event like `WAKEUP_ENEMIES`; 800's credit launches nothing there of its own. 965 to 967 (the
  airframe swap) belong to the episode's owner. Definitions started by a player's position (the
  landing approach rows, the `PlayerRange` conditions, the ladder switch) are not graph events.
  The escorting wingman is a roster block spawned at build, not a director event.

A guest applies each event on arrival and then catches up on it (`Session/NetDirectorCatchUp.cs`).
The lateness is the guest's shared clock minus the stamp, never negative. What the event started
is advanced by that much:
- the objective's private timer, its nap and a countdown it set;
- the cutscene instances it started and their motions, stepped at the authored frame so their
  timed events and codes fire in order (a code the host raised during that time is raised on
  arrival);
- a one-shot, started that far into its clip, or skipped when the clip is already over;
- a radio call, whose start delay is shortened by the lateness and which, past it, joins at the
  line and offset the host's is at.
Particle emitters, light animations and a music cue start at their own beginning on arrival. None
of them has a position to seek, and none is timed against the rest.
The shared clock is `Net/NetClockSlew.cs`'s. Its one-way readings are read forward by half the
round trip `0x49` measures (below), so a lateness read against it is the whole time the event
spent on the link, not only the excess over the average.
A guest's mission end holds the world and builds the result without writing a profile, a
photograph or an award. A guest that joins late has missed every earlier event.

## The host-owned world

The host flies every AI aircraft and decides every world hit; a guest replicates the state and
replays nothing that draws from the AI stream (`Session/NetWorldLink.cs`). The same seed is not the
same AI: the mode machine rolls on the AI stream every step, so two ends running one AI would part
on the first roll that landed differently. An AI is named on the wire by its admission ordinal, its
index in the roster's append-only AI list, which both ends grow in the same order for the aircraft
built with the world. The one source that grows it later is a generator's aircraft launch, and a
guest builds those only from the host's `0x4C`, so the two lists cannot part on a launch timed
differently or credited on one end alone. Black Hat launches and `WAKEUP_ENEMIES` add nothing to
the list: they reactivate blocks built dormant, whose ordinals already stand.

| Id | Message | Class | Carries |
|---|---|---|---|
| `0x45` | AI state | unreliable, on the event channel | ordinal, per-AI sequence, pose, velocity, lever, surfaces, nitro (48 bytes) |
| `0x46` | AI fire | unreliable | ordinal, weapon index, muzzle, aim (28 bytes) |
| `0x47` | AI hit | reliable, guest to host | ordinal, shooter seat, weapon, damage share, part, impact in the AI's body space (28 bytes) |
| `0x48` | World event | reliable, host to all | code, subject, argument, value (16 bytes) |
| `0x4B` | Zeppelin state | unreliable, host to all | placement index, per-zeppelin sequence, position, speed, pitch, yaw (32 bytes) |
| `0x4C` | AI spawn | reliable, host to all | admission ordinal, launch counter, generator index, net index, flags, lever, position, drop direction, velocity (44 bytes) |
| `0x4D` | Surface vehicle state | unreliable, host to all | spawn index, per-hull sequence, name hash, position, speed, yaw (32 bytes) |

`0x4C` is one generator aircraft launch. The host admits the aircraft as it launches and sends the
ordinal it got; the guest builds the launch only when that ordinal is its own next one, which
reliable ordered delivery makes the normal case, and drops it otherwise. The guest builds with the
host's net pick, pose, launch velocity and lever, and forms the name from the host's launch counter,
so the mission script names the same aircraft on both ends. A guest's own cycles still run their
timers and doors, but its spawner refuses every aircraft launch they come due for. No take-off run
is started on the guest, since the host's samples fly the copy along it. A surface hull launch is
not carried here.

`0x4B` is one zeppelin of the original's `0x1e`, its first `0x18` bytes in the original's order, on the
same half second. A zeppelin is named by its placement index, which both ends build from the same
records. The part-state word and the cannon-shot tail are not carried: a part's death arrives as a
pool event, and each end's cannons still fire on their own. A guest runs the original's chase
(`Flight/ZeppelinReplica.cs`) in place of its follower, so the two ends cannot part on a branch
pick. A hull the host holds, has not woken or has lost is not sent, and the guest's copy stays where
the last sample left it.

`0x4D` has no counterpart in the original, which has no campaign across a link and so no patrol
boat to send. It takes `0x4B`'s half second and its chase, with the pitch dropped because a hull
rides the water. A hull is named by its index in the surface runtime's spawn list and by the name
hash `0x48`'s code 3 uses; a guest checks the hash and searches by it when a generator's launch has
shifted the index. A patrol replayed on each end diverges without a branch draw: the wake arrives
late, a `SET_AI_NET` starts its route from wherever the hull stands, and a launch spawns on each
end's own timer. The roster and `SET_AI_NET` nets of C1B/M03 and C2/M01 carry no node with three
or more neighbours, and C2/M01's launch net carries one, so the branch pick is the smallest of the
four.

AI state is plain unreliable rather than sequenced because every AI shares one channel, and a
transport sequence would drop one AI's sample against another's; each AI's own pose buffer drops a
stale one by the per-AI sequence. It rides the seat stream's cadence. The world event codes are
`NetWorldEvent`: 1 an AI downed (the argument is the killer's seat or -1), 2 an AI's hull fraction,
3 a destructible pool's health (the subject is its registration
index, the argument a hash of its definition and anchor names, which the guest checks before
applying and searches by when the index has shifted), 4 a `WARP_VEHICLE` pick (the subject is the
drawn waypoint index, the argument the hash of the warped vehicle's name), 5 an AI's presence (the argument is 1 for in
play and 0 for deactivated). The host sends 5 whenever an AI's `Inert` changes outside a cutscene
park, which covers a script wake, a Black Hat launch and a wingman taken out. A cutscene park (913)
is not sent, because each end's own cutscene parks its own copy.

Code 3 goes out at once for a stage change or a kill. A hit that lowers a pool without either is
held and sent on the seat stream's next tick, one sample per pool however many hits landed, because
three guest-visible rules read the health between stages: the target bar's fraction
(`Flight/TargetPool.cs`), a surface hull's injure ladder (`Session/SurfaceVehicle.cs`), and every
`ANIM_HEALTH` condition. A sample waiting when a stage change goes out is dropped, since the stage
event carries the same health. Chip samples ride the reliable class with the stage events: a guest
only ever lowers a pool, so an old sample arriving late changes nothing, and a lost last one would
leave the guest's copy high until the next hit.

A hit on an AI is decided once: by the host for its own rounds and for every round no seat fired,
and by a guest for its own seat's rounds, which it claims with `0x47`. A world pool is spent only
on the host, which simulates every round, a guest's included, from the fire events; a guest's
world runtime reports a struck pool as hit and spends nothing. A ram on an aeroplane flown
elsewhere spends nothing on it either, since its owner's own sweep resolves that half.

Each simulation phase, as a guest runs it:

| Phase | On a guest |
|---|---|
| Ending hold, landing approaches, radio, smoke screens, beeper tags, incoming fire | Local presentation or per-pane rules, no world authority. |
| Capture AI aircraft | Local membership; new AI are admitted by ordinal before any is stepped. |
| Projectiles | Local on every end, spawned from fire events; the guest's rounds spend nothing on the world or on an AI. |
| Human aircraft | Replicated per seat (`0x0f`, `0x10`, `0x22`, `0x40`, `0x12`). |
| Captured AI aircraft | **Replicated**: each AI flies from `0x45` samples; fire arrives as `0x46`, hull and death as `0x48`. |
| Zeppelins | **Replicated path**: each hull chases the host's `0x4B` samples. Part and cannon deaths arrive as pool events; the broadside still fires locally. |
| Turret emplacements | Replayed locally and cosmetic: a guest's turret round spends nothing, the host's decides. Deaths arrive as pool events. |
| Generators | **Host-owned launches**: each aircraft launch arrives as `0x4C` at the host's ordinal. The guest's cycles and doors run on its own timers and are cosmetic; their aircraft launches are refused. |
| Surface vehicles | **Replicated patrol**: each hull chases the host's `0x4D` samples. A hull's death arrives as a pool event; its gun still fires locally. |
| Instant action | Not run in a network match. |
| Campaign | The director replay above. |
| AI voice | Derived locally; a replicated AI runs no mode machine, so its mode-driven call-outs are silent. |
| Versus | The match state above. |

## The lobby

Before any session binds the carrier, a `Net/NetLobby.cs` stands on it. A host sends one message
there, to each peer as it connects and again whenever the offer changes. A guest's lobby keeps the
latest and never passes it to the session, so the session's own vocabulary never sees it.

| Id | Message | Class | Carries |
|---|---|---|---|
| `0x4A` | Session advert | reliable, host to each guest | session kind (Dogfight or campaign co-op), campaign mission sequence or none, player count, host name in 16 bytes (24 bytes) |
