# Milestone 6, Multiplayer

**ACTIVE PLAN** (written 2026-09-19). It sits in `docs/`, which by this repo's convention makes it
a live plan; PROJECT_CONTEXT.md's "Current status" names it. When every item lands, the closing
commit deletes this file, records the completion in its message, and clears the "Current status"
pointer; any live prose linking this file by path is unlinked in the same commit.

The last big milestone: play over a network. The original's transport (DirectPlay over IPX, TCP,
modem and serial) is gone, but its game-level protocol survives in `crimson.exe` and is partly
decoded, so this plan is a new carrier for an old protocol rather than a clean-room design. Each
client simulates and broadcasts its own aircraft, one player hosts and owns everything shared (the
match clock, scores, spawns, the AI, the zeppelins, the turrets, the destructibles, the campaign's
mission director), and every discrete event crosses the wire as a typed message in the shape the
executable already uses. Two modes ship: the Dogfight deathmatch that splitscreen already plays, and
campaign co-op in the shape the local splitscreen campaign already has, the host's campaign with
guests flying as the human field. The plan draws one item from `backlog.md`, `BL-951` (the local
multiplayer door and join board), which was not re-verified still-open in the session that wrote
this plan.

Out of scope, deliberately: the flag and zeppelin match modes (decoded on the scoring side only,
they follow Dogfight once the carrier works), a dedicated headless host (a listen server is the
original's model and the least work; Godot's `--headless` keeps the option open), per-guest campaign
profiles and progression (co-op is the host's campaign), a Steam store listing (a distribution and
legal decision, kept open by the transport flag rather than taken here), deterministic lockstep
(Godot's collision queries and physics server are not deterministic across machines), and
server-authoritative prediction (exists to stop cheating, which a hobby community does not need,
and feels worse for aircraft than the owner-simulated model).

## Milestone goal

- Two to eight players fly one Dogfight match over the network, each on their own machine, with
  the decoded scoring, spawn rotation and match end.
- A host runs a campaign mission and guests fly beside them as the human field, seeing the same
  objectives, cutscenes, wingmen and enemies, and the mission ends for everyone when it ends for the
  host.
- The game code above the transport does not know which transport carries it: ENet with direct IP
  ships, an in-process loopback runs the test suite, and a Steam transport can be added behind the
  same interface without touching a session.
- Two sessions, one hosting and one joining, run inside one process under `RunTests.ps1`, with an
  injected latency and loss model, so every replication rule has a suite before ENet is ever opened.

**Nothing about the world ever crosses the wire.** Every peer builds the same world from the same
extraction, so the only bytes sent are pilot states, fire and hit events, AI spawns and deaths,
destructible deaths, mission director transitions, the match clock and the seat roster. A design
that finds itself replicating a mesh, a node or an animation has left this plan.

## Decisions (2026-09-19)

| # | Question | Decision |
|---|---|---|
| 1 | Which network shape | **Owner-authoritative aircraft, host-authoritative match and world**, the shape the original uses (a death is reported by the dying pilot's own client, `docs/org/multiplayer-scoring.md`) and the one that needs no cross-machine determinism |
| 2 | Is campaign co-op in scope | **Yes**, as the host's campaign with guests, the same shape as the local splitscreen campaign co-op; not per-guest profiles or progression |
| 3 | Which modes first | **Dogfight and co-op**, the flag and zeppelin modes later |
| 4 | Hosting model | **Listen server** (one player hosts); a dedicated headless host later |
| 5 | Player ceiling | **8, behind a constant that is 16-safe**; the executable has no coded cap, 8 is the shipped lobby value and the data holds 16 |
| 6 | Steam | **Not decided here.** The transport goes behind a flag so a Steam build (Steam Networking Sockets, relay, lobbies, invites) can be added without touching the session; the store listing is a separate legal and distribution decision |
| 7 | Where the seam goes | **Two interfaces above the transport**: a remote-airframe arm beside `IFlightInputSource` on the aircraft side and a transport interface on the session side; a remote human is a pose that arrives late, never a stick that arrives late |
| 8 | Hit authority | **Shooter's client decides the hit, the victim applies the damage and reports its own death, the host scores**, the decoded original's order, no lag compensation. The hit itself is a **reliable** message, confirmed after A2 found the original batches hits inside its unreliable aircraft-state packet: a lost hit would be a lost kill |
| 9 | Topology | **Star, the host relays.** A guest connects to the host only; the host forwards every guest's aircraft state and events to the other guests, so one port and one UPnP mapping serve a match and a guest-to-guest packet costs one extra hop. No mesh between guests |

## ⚠ Read this before implementing anything

| # | The wrong claim | How it died |
|---|---|---|
| 1 | ~~`docs/org/multiplayer-scoring.md` names offset `+0x3c` of the pilot record as the team field~~ **This row was itself wrong; A2's re-read settled it** | Two records carry a team field and the row conflated them. `FUN_00498bf0`'s same-team arm compares `+0x3c` of the **remote** record, the `0x1090`-byte object `FUN_00499d80` looks up over `DAT_0071c7a4`, which `FUN_00495310` fills from the pilot record's `+0x08` team object (`FUN_0046f3c0`) when teams are on and from the pilot index when they are off; `FUN_00499a50`'s team-chat arm compares the same field, and the colour table `00628eb4` is indexed by it. `+0x08` of the *pilot* record (`FUN_00413db0`, `FUN_0046ea40`) is the team object it is derived from. The scoring doc now names the record with the offset |
| 2 | The original caps a match at a coded number of players | No constant bound exists. The pilot list is an STL list (head `0071c150`, count `0071c154`, walked by `FUN_0046f110`) and the count is never compared against a maximum. The only gate is DirectPlay's `dwMaxPlayers`, filled from the lobby screen variable `nMaxPlayers` (string `006192e0`, global `00642f08`), which the code only ever resets to 0 |

| Confidence | Items | What that means for you |
|---|---|---|
| **Traced to an exact mechanism in code, with the data that proves it** | B12, B13, B14 (scoring, spawn placement and match end are decoded in `docs/org/`), A4 (ceiling, from this plan's own decode) | Confirm the trace, then implement. |
| **Direction sound, magnitude a judgement call** | B11 (send rate, interpolation buffer, extrapolation window) | The *what* is settled; the *how much* is TUNE, add it to `backlog.md`'s TUNE list, don't invent it as fact. |
| **Leads only, no mechanism yet** | A1, A2, A3, A5, B15, C21 to C24, D31, D32 | Budget for investigation; this may end in a disproof. |

**⚠ Worktree hazard.** `git stash` is repo-global and shared across worktrees, never use it in a
worktree session here; use a local commit or a file copy.

## What the data actually ships

**The pilot record**, 0x7c bytes, allocated by `FUN_00414640` (the DirectPlay create-player
callback registered at `004148a5`); the remake's per-seat network record should carry the same
fields, every one of which has a known consumer:

| Offset | Field | Consumer |
|---|---|---|
| 0x00 | vtable `006033e0` | |
| 0x04 | DirectPlay id, the match key | `FUN_0046f110` |
| 0x08 | team id | `FUN_00413db0`, `FUN_0046ea40` |
| 0x0c, 0x0d | flags, "is local" | `FUN_00414640` |
| 0x10 | callsign | `00495310` |
| 0x14 | aircraft pointer | `00495310` |
| 0x18 | pilot index, **1-based** (`FUN_004148a0` seeds the counter `0061f5e0` at 1) | colour table, `net.zrd` slot |
| 0x1c | signed score | `docs/org/multiplayer-scoring.md` |
| 0x20 | second scoreboard number | `FUN_0046ea40` |
| 0x2c | 0x30-byte scoreboard row | `FUN_004136e0` |
| 0x34 to 0x77 | 0x44-byte plane and livery configuration | `FUN_00497990` |
| 0x78 | join-payload extra | `FUN_00414640` |

**The shipped ceiling and the headroom.** The per-pilot colour table at `00628eb4` holds eight
dwords (`812d2d, 2d2d81, 2d812d, 81812d, 812d64, 66812d, 457c81, 662d81`, zeros from `00628ed4`),
indexed unchecked at `00495893` and `00497ae6`; because the index is 1-based, the eighth pilot reads
past the table and flies black, so only seven get an authored colour. The respawn bearing steps 45
degrees per index (`docs/org/multiplayer-spawn.md`). The data allows 16: the spawn tables are
quantised at 16 entries per block (`team << 4` at `00496bba`, `docs/formats/net-spawns.md`), the
lobby's player array is 16 entries (`00645390`, stride 0x20, name at +4, colour at +0x1c) and its
team array 16 (`00645590`, stride 0x10), both filled with no bound check; the score list is 32 rows
(`00645690`, stride 0x2c) and the games list 128 (`00643190`, stride 0x44). Packets are not a
limit: types 0x13 and 0x27 are allocated from the live counts with 16-bit length fields, and ids
are 32-bit DPIDs. Raising the remake past 8 costs the colour table, the 45-degree fan, the 16-entry
spawn block assumption and the four lobby arrays.

**The message shape.** A death is message type `0x12`, built by `FUN_00498a90` and handled by
`FUN_00498bf0`, carrying a killer id at `+4` and a cause at `+0xc`; the cause table and the three
Dogfight scoring events (suicide, kill, turret kill) are in `docs/org/multiplayer-scoring.md`. The
per-mission spawn table `net.zrd` (45 files install-wide, four floats per node) is read by
`SpawnPoints.LoadNetFreeForAll` in `CSVM/src/Flight/SpawnPoints.cs`, and what the executable does
with a picked entry is `docs/org/multiplayer-spawn.md`.

**The seams the code already has.** `CSVM/src/Flight/IFlightInputSource.cs:10` is the one place a
sim step reads pilot intent, resolved once at `FlightController.cs:1006` from three arms (scripted,
AI, keyboard). `FlightController` keeps `_simPrev`, `_simCurr` and `_renderPose`
(`FlightController.cs:1260`), the slot an interpolated remote pose lands in.
`CSVM/src/Session/SessionSimulation.cs:8` names every simulation phase as a method on
`ISessionSimulationRuntime`, which makes the host-or-guest ownership rule mechanical. The
splitscreen seat index is already the player id through `VersusMatch.PlayerCount`
(`GameSession.cs:2613`, `GameSession.cs:2673`) and `SpawnPicker.cs:92`'s `playerCount`. The seeded
`Rng` streams (`Rng.Stream`) mean one seed handed out at join makes every draw agree.

## Ground rules

- **Original-game data drives everything.** Read the reader/compiled JSON before writing a handler;
  never guess a value. Inventing content is the trap this project falls into most often.
- **Evidence is a lead to verify, not a finding to implement.** Confirm every claim against the
  data/code before building on it; **a correct disproof that lands no code is a success here**, not a
  failure. Mark each item's Evidence with its confidence (traced-to-code / direction-sound-magnitude-
  TUNE / lead-only).
- **`PROJECT_CONTEXT.md` + the module's entry in `docs/architecture/<Namespace>.md` (plus its index
  bullet in `docs/architecture.md`) / `docs/formats/` are updated in the same turn** as each landed
  item; a landed item gets its record in the landing commit's message and is **deleted** from
  `backlog.md` (not marked FIXED there). New decodes land with their `docs/formats/` page.
- **Read `docs/verification.md` before measuring anything**, the instruments here mislead; cite the
  rule that bites per item.
- **Verify against a full 8-chapter `--freecam --chapter=<X>` regression** (zero errors, same
  mesh/node counts unless the change is meant to add coverage) plus a targeted capture at the
  location the report came from.
- **Read the module's entry in `docs/architecture/<Namespace>.md` (found through the index in
  `docs/architecture.md`) before modifying it,** then the comments on the members you touch; dead
  ends are in the landing commits (`git log --grep=<ID>`), so search those before re-chasing one.

## Checklist

Statuses: ☐ open · ◐ in progress · ☑ done · ❌ closed/disproven. **Keep this in sync as items land.**

### Wave A, the seam, in-process and network-free

1. ☑ The transport interface and the loopback transport with an injected latency and loss model
2. ☑ The message vocabulary: typed messages, reliability classes, serialisation, modelled on the decoded set
3. ☑ The remote-airframe arm: a `FlightController` fed a received pose instead of a flight model
4. ☑ Network seats: seat identity without a pane, the 8-behind-16 ceiling, the seed and match-clock handoff
5. ☑ The two-session harness: a host and a guest `GameSession` in one process under `RunTests.ps1`

### Wave B, Dogfight over the wire

11. ☑ Aircraft state replication: own aircraft broadcast at a fixed rate, remote aircraft interpolated
12. ☑ Fire, hit, damage and death events in the decoded order, scored by the host
13. ☐ Host-owned spawn and respawn from `net.zrd` and the rotation, applied by guests
14. ☐ Match state: clock, limits, end and scoreboard replicated
15. ☑ The ENet transport, host and join by direct IP with UPnP, and the multiplayer door's join board (`BL-951`)

### Wave C, campaign co-op

21. ☐ The host-owned mission director: objective graph transitions, cutscene codes and wingman spawns as events
22. ☐ Host-owned AI and world: aircraft, zeppelins, turrets, generators, vehicles and destructibles as spawn, state and death events
23. ☐ Guests as the human field: `CampaignHumanField` and the objective rules see remote humans, the scripted P1 stays the host
24. ☐ The co-op session flow: cabin and briefing on the host, guests joining into the mission, mission end and debrief on every peer

### Wave D, hardening

31. ☐ Latency and loss soaks, desync instruments and a `--debug-net` readout
32. ☑ The Steam transport flag: a build-time gate with a stub, so the seam is proven before any SDK arrives

## Dependency and parallelism notes

A1 and A2 block everything else and can run in parallel with each other; A3, A4 and A5 need both
and can then run in parallel (A3 owns `CSVM/src/Flight/`, A4 owns the seat and session-spec side of
`CSVM/src/Session/`, A5 owns `CSVM/src/Testing/` and the runner). Wave B is a chain, B11 → B12 → B13
→ B14, because each replicates over the previous one's channel; B15 needs only A1 and A2 and can run
beside B11 to B14 as long as it stays out of `GameSession.cs`. Wave C needs B12 and B14 and is a
chain, C21 → C22 → C23 → C24. D31 needs Wave B; D32 needs only A1. File contention: B11, B12, B14,
C21 and C22 all edit `GameSession.cs` and `SessionSimulation.cs`, never run two of them in parallel
worktrees; give each concurrent agent one namespace and name the files it may not touch.

---

# Wave A, the seam, in-process and network-free

## A1 ☑ The transport interface and the loopback transport with an injected latency and loss model

**Landed.** A new `CSVM/src/Net/` namespace holds the seam and one carrier.
`CSVM/src/Net/INetTransport.cs` is the transport interface plus the two types it is spoken in:
`NetReliability` (Unreliable, UnreliableSequenced, Reliable), `INetTransportListener`
(`OnPeerConnected`, `OnPeerDisconnected`, `OnPayload(peer, channel, ReadOnlySpan<byte>)`) and
`INetTransport` itself (`LocalPeer`, `Peers`, `Bind`, `Send(peer, payload, reliability, channel)`,
`Disconnect`, `Step(dt)`). Payloads are byte spans and no member names a message type, so A2's
vocabulary sits entirely above the seam. `CSVM/src/Net/LoopbackConditions.cs` is one direction's
wire conditions as a validated value (latency, symmetric jitter half-width, loss probability) whose
two draws come from a caller-supplied `Random`. `CSVM/src/Net/LoopbackTransport.cs` is `Mesh(n,
conditions, rng)`, n transports linked to each other in one process through delivery queues, with
`SetConditions(peer, conditions)` to change one direction mid-run and `Step(dt)` as the only place a
payload is ever delivered. The guarantees are enforced, not imitated: loss is drawn only for the two
unreliable classes, a reliable stream's deadlines are held monotonic per sender so jitter cannot
reorder it, and a sequenced payload at or below the newest already delivered on its channel is
discarded on arrival. `CSVM.Tests/LoopbackTransportTests.cs` (9 cases) and
`CSVM.Tests/NetNamespaceDependencyTests.cs` cover it, the latter asserting over compiled metadata
that no `CSVM.Net` type references anything under `Godot` or `System.Net`. `docs/architecture/Net.md`
and its three index bullets in `docs/architecture.md` are new; `PROJECT_CONTEXT.md`'s namespace map
gains `src/Net/`. Nothing under `CSVM/src/Flight/` or `CSVM/src/Session/` was touched: A1 is the
mechanism, and the session wiring is A5's.

**Verified.** On the run branch with A1 to A5 merged, the full battery (`RunTests.ps1
-GoldenWorkers 2`) reads build clean, 4830 units passed with 2 skipped, 377 of 378 engine suites
passed with engine errors clean, and 19 of 19 golden shots hash-identical. The one engine failure,
`instant-action-end` on a per-frame movement minimum through the 3 s win hold, passed alone on the
same tree and is a load flake under six shards beside a sibling's runs; nothing under
`CSVM/src/Net/` is on its path. `LoopbackTransportTests` (9) and `NetNamespaceDependencyTests`
pass on the merged tree.

**Original approach (kept for reference).**

**Goal.** A session can send and receive typed messages to and from named peers without knowing
what carries them, and a test can run two sessions against each other in one process with chosen
latency, jitter and loss.

**Evidence (confidence: lead-only).** Nothing under `CSVM/` touches the network today;
`docs/PLAN-public-release.md` greps `System.Net`, `ENetMultiplayerPeer` and `MultiplayerApi` to
prove it, and that grep becomes a claim to retire when this lands. The interface shape (a peer id
list, send unreliable, send reliable, a receive callback) is this plan's design, not a decode.
The grep survives A1 unchanged: the namespace opens no socket and names no Godot type, which
`NetNamespaceDependencyTests` now asserts mechanically rather than by grep. B15 is still the item
that makes it false.

**Approach.** One interface in a new `CSVM/src/Net/` namespace with three members: the peer roster,
`Send(peer, message, reliability)` and a receive hook. `LoopbackTransport` connects two instances
in-process through queues with a per-direction latency, jitter and loss model injected at
construction, so a suite can make a packet arrive late, out of order or not at all on demand. No
Godot type in the interface; the ENet implementation (B15) is the only file that names one. Register
the transport as a session input through `FlightRosterInputs`'s pattern of grouped construction
facts rather than widening `SessionSpec`. As landed, the interface carries six members rather than
three: `Bind`, `Disconnect` and `Step` join the roster and `Send`, because a listener has to be
attached somewhere, a session leaving a match has to hang up, and delivery has to be driven by the
caller for a suite to own its timing. The `FlightRosterInputs` registration is A5's, since A1
touches no session.

**Model recommendation.** High. The exact members of this seam are what A3, A5, B11 to B15 and D32
all build against, so a shape settled wrong here is re-cut through every later item.

**Verify.** `CSVM.Tests/LoopbackTransportTests.cs`: a bound listener is told about the peers the
mesh already gave it; nothing is delivered before a step or before its latency; loss takes both
unreliable classes and never a reliable payload; a reliable stream keeps its send order under
jitter and loss at certainty; a payload overtaken on its channel is dropped when sequenced and
delivered out of order when plain unreliable; sequencing is per channel; both directions carry and
a hang-up empties both rosters; one seed replays a lossy, jittered, reordering run exactly. Plus
`CSVM.Tests/NetNamespaceDependencyTests.cs` for the no-engine, no-socket boundary.

**⚠ Traps.** Do not build the interface on `MultiplayerApi` or `MultiplayerSynchronizer`; they
replicate node properties and carry no interpolation, and they would put a Godot type in every
session. Do not reach for `[Rpc]` on session code for the same reason. The loopback's loss model
must apply to unreliable messages only; a "reliable" message that the loopback drops is a bug in
the test, not a scenario.

## A2 ☑ The message vocabulary: typed messages, reliability classes, serialisation, modelled on the decoded set

**Landed.** `CSVM/src/Net/NetMessages.cs` holds ten message structs over a shared four-byte header
(`ushort type`, `ushort totalLength`, the original's own framing). Each is a value type implementing
`INetMessage<TSelf>`, whose `static abstract Type` and `Reliability` let a sender read the class off
the type without constructing anything; `NetMessage.ReliabilityOf` is the same table as a switch for
a type word that arrives off the wire. `CSVM/src/Net/NetMessageWriter.cs` holds the two cursors every
serialiser runs on, `NetMessageWriter` and `NetMessageReader`, little-endian over `Span<byte>` with
no reflection, plus the quantised unit field and the fixed-width UTF-8 field the layouts need. Seven
ids follow the original (`0x0F` aircraft state, `0x10` fire, `0x12` death, `0x13` score, `0x17` match
state, `0x22` hit, `0x27` seat roster); three are minted above the original's `0x27` ceiling (`0x40`
damage, `0x41` spawn, `0x42` director transition), and `NetMessage.IsOriginalId` says which is which
in code. The original's whole table, with builders, handlers, payload widths and guarantees, is now
`docs/org/multiplayer-messages.md`, linked from the scoring and spawn pages.

**Verified.** On the run branch with A1 to A5 merged, the full battery reads 4830 units passed,
377 of 378 engine suites passed (the one failure a load flake in `instant-action-end` that passed
alone; see A1) and 19 of 19 golden shots hash-identical. `NetMessagesTests` (the round trip of
every struct, the handshake included) passes on the merged tree, and the team-offset correction
in the warning table was re-read from the decompiled comparison in the same-team arm before the
item was landed.

**Model recommendation.** High. The wire layout is the one artefact every later item in Waves B, C
and D reads back, and a field packed wrong here surfaces as a physics or scoring bug three items
away.

**Verify.** `CSVM.Tests/NetMessagesTests.cs`, 22 tests tagged `Tier=Quick`: a round trip per message
type, the quantisation clamp, the variable-length roster at zero, one and over-capacity seat counts,
callsign truncation, header routing, and four rejection cases (wrong type word, truncated buffer,
declared length mismatched to the entry count, header shorter than four bytes). The size budget is
`NetMessage.AircraftStateBudget = 48`, asserted against the 44 bytes the layout actually needs, so
the four bytes of headroom are a named constant and not a fact about today's fields.

**⚠ Traps.** The decode is done and the id table is in `docs/org/multiplayer-messages.md`. Three
findings change what later items may assume. First, the original batches its hit reports into the
*unreliable* `0x0F` aircraft-state packet (12 bytes per hit, a 4-bit count, queued by `FUN_004987d0`
and dropped by `FUN_00498760`), so its hits are droppable; the remake keeps Decision 8's reliable
hit instead, and B-wave scoring must not cite the original as authority for a droppable hit. Second,
types `0x02`, `0x03`, `0x05`, `0x06`, `0x07`, `0x0D` and `0x0E` never cross the wire at all:
`FUN_005b2820` and `FUN_005b24a0` synthesise them on the stack from DirectPlay system messages, so
a remake transport owes them nothing. Third, the plan's ⚠ row 1 below is **wrong**, and the scoring
doc was right about the offset: `FUN_00498bf0`'s cause-1 arm compares `+0x3c` of the *remote* record
(the `0x1090`-byte object `FUN_00499d80` looks up), not of the pilot record, and `FUN_00495310`
fills that field from the pilot record's `+0x08` team object. Both offsets are real and they name
different records; the scoring doc's line now says which.

**Original approach (kept for reference).**

**Goal.** Every byte that crosses the wire has a named type, a declared reliability class and a
serialiser with a test, and the set is small enough to list on one page.

**Evidence (confidence: lead-only for the set, traced for its model).** The original's death report
(type `0x12`, killer at `+4`, cause at `+0xc`, `docs/org/multiplayer-scoring.md`) is the template
for every discrete event. Types `0x13` and `0x27` are allocated from live counts with 16-bit length
fields, so the original's roster and score messages are variable-length; the full message table is
not decoded and is a TODO on the decode side, not a blocker.

**Approach.** A `NetSession` module owns the vocabulary: aircraft state (unreliable, sequenced),
fire (unreliable, sequenced), hit and damage (reliable), death (reliable, the `0x12` shape), spawn
and respawn (reliable), score and match state (reliable), seat roster and seed (reliable), mission
director transition (reliable, Wave C). Hand-packed structs with a sequence number on the unreliable
ones; no reflection-based serialiser. The vocabulary lives in `CSVM/src/Net/`, and no other
namespace names a message type's wire layout. The landed shape splits that one module into the
vocabulary and the writer/reader pair, since A1 owns the transport in the same namespace.

## A3 ☑ The remote-airframe arm: a `FlightController` fed a received pose instead of a flight model

**Landed.** `CSVM/src/Net/RemotePoseBuffer.cs` is one remote aircraft's received history: samples
go in stamped with the buffer's own clock (`Receive` at `Now`, `Add` at an explicit time for a
test), a sample at or below the newest sequence is dropped with a wrap-safe comparison, and a read
answers the state `BufferDelaySeconds` behind the render time. Two samples straddling that target
interpolate (position, velocity, throttle and the sender's three surface deflections by lerp, the
attitude by slerp over normalised quaternions, since the wire's quantised ones are not unit);
a target past the newest rides that sample's velocity for at most `ExtrapolationCapSeconds` and
then holds; a target before the oldest holds the oldest. `RemotePose.Feed` reports which of the
three cases (`Interpolating`, `Extrapolating`, `Starved`) produced the answer, so B11's instrument
counts them without re-deriving the decision. Both constants are accepted at the harness
conditions on B11's measurement. The arm on the controller is the buffer itself: `FlightController.RemotePoses`, carried
through `FlightControllerBuild` and copied in `Bind` before the input arm is resolved, and
`RemoteOwned => RemotePoses != null`, so a seat cannot be half remote. Owned remotely, the sim step
runs `StepRemotePose` in place of the whole live-flight branch and writes the model's pose,
velocity, lever and boost from the sample; `_simPrev`/`_simCurr`/`_renderPose` are then set by the
same two lines as before, so `WorldPosition`, `WorldVelocity`, `NoseDirection`, `Attitude` and the
render interpolation are untouched. Being hit, damage visuals, engine and weapon audio, HUD
markers, the shake and the crash rig all stay live.

**Verified.** On the run branch with A1 to A5 merged, the full battery reads 4830 units passed,
377 of 378 engine suites passed and 19 of 19 golden shots hash-identical, so the gating moved no
pinned pixel of a locally flown aircraft. The one engine failure is the `instant-action-end` load
flake described under A1; it passed alone. `RemotePoseBufferTests` (12) and the `remote-airframe`
suite pass on the merged tree, the suite's locally flown control answering the gun trigger, the
respawn button and the model step that the remote rig refuses.

**Model recommendation.** High. The gating is a list of "must not run" members rather than a new
code path, and the failure mode of a missed one (a remote aeroplane bouncing off terrain it never
touched on its owner's machine) is invisible until two sessions fly.

**Verify.** `CSVM.Tests/RemotePoseBufferTests.cs`, 12 tests tagged `Tier=Quick`: straddling
interpolation, a gap in the stream, a sample at and below the newest sequence, the 65535 wrap,
extrapolation at half the cap, the hold past it, a read before the oldest, an empty buffer, the
buffer's own clock, `Clear`, the bounded ring, and a non-unit quaternion slerped. The engine suite
is `remote-airframe`: two identical real rigs, one handed a buffer and one not, where the remote
one's pose tracks a scripted 120 m/s stream to under 0.5 m and 0.02 rad through the delay, its
velocity and the sender's stick arrive intact, a stopped stream holds it one cap past the last
sample and three further seconds move it under 0.01 m, a non-cannon hit spends its damage ledger
without moving it, and neither the gun trigger nor a held respawn reaches it. Each of the last
three has the locally flown rig as its able-to-fail control, which flies, fires and respawns.

**⚠ Traps.** `AircraftStateMessage` carries no timestamp, only a `ushort` sequence, so the buffer
keeps its own clock and the session owes it one `Advance` per step (the controller does this) plus
one `Receive` per message. A remote controller must not run the ground-blow probe, the nearest-human
fill, terrain contact resolution or the under-map backstop, or it will move a pose only its owner
may write. Guns and rockets stay untouched but unreachable from this machine's trigger; B12's fire
events are what fires them. A respawn clears the buffer, because the samples before it describe an
aeroplane that is no longer there.

**Original approach (kept for reference).**

**Goal.** A `FlightController` built for a remote human flies from received state samples,
interpolated between the last two and extrapolated past the newest, while everything hung on it
(guns, rockets, damage visuals, engine audio, HUD markers, collision hulls) works unchanged.

**Evidence (confidence: traced for the seam, lead-only for the arm).**
`CSVM/src/Flight/IFlightInputSource.cs:10` is the one input seam and its comment says the arm
cannot change after `Bind`. `FlightController.cs:1006` resolves the three existing arms;
`FlightController.cs:1260` and `FlightController.cs:1405` are where `_simPrev`, `_simCurr` and
`_renderPose` are set from the model, the slot a received pose replaces. `PilotInputSource` shows
the arm pattern (`IFlightInputSource.cs:46`).

**Approach.** Not a fourth `IFlightInputSource`: a remote human is a pose that arrives late, not a
stick that arrives late, and feeding remote sticks into the local model only works under lockstep.
Instead a controller-level ownership flag set through the build DTO, alongside `Pilot` and the
supplied input source, that makes the sim step skip `FlightModel` and set `_simCurr` from an
interpolation buffer the network fills. The buffer holds timestamped samples; interpolation between
the two straddling the render time minus the buffer delay, extrapolation along the last velocity
when the newest sample is older than that. Keep the arm inside `FlightController` so the readers of
`WorldPosition`, `WorldVelocity`, `NoseDirection` and `Attitude` (`FlightController.cs:937` to
`:950`) need no change. The landed shape carries the ownership on the buffer reference itself
rather than a separate flag, so the two cannot disagree.

## A4 ☑ Network seats: seat identity without a pane, the 8-behind-16 ceiling, the seed and match-clock handoff

**Landed.** Four modules in `CSVM/src/Net/`, and the session wiring that reads them.
`NetSeat.cs` is the per-seat record shaped like the decoded pilot record (`PeerId`, `SeatIndex`,
`TeamId`, `IsLocal`, `Callsign`, `PlaneNode`, `Livery`, signed `Score`, and `Color` off the table).
`NetSeats.cs` holds `MaxPlayers = 8` behind `SeatCapacity = 16`, the colour table (seats 0 to 7 the
eight dwords at `00628eb4` read as red, green, blue; seats 8 to 15 the channel-wise complement of
seat minus 8, both TUNE, `BL-1017`), and `Validate`, which requires seats numbered from zero with
no gap and at least one flown here. `NetHandshake.cs` is the host's seed and its session clock at
send; `NetClockSlew.cs` is the guest-side application of the clock half, an offset walked to each
fresh reading over `ConvergeSeconds` at no more than `MaxRateOffset` of real time, snapping past
`SnapSeconds` and counting it (all three TUNE, `BL-1018`). No wire layout is declared: these are
the records a session hands to and takes from A2's vocabulary.

In the session, `LauncherContext` gained optional `NetSeats` and `NetHandshake`. `GameSession`
takes the handshake's seed as its master before `Rng.Reset` runs, opens a `NetClockSlew` from its
clock and advances it once per frame, and builds `_seatRigs` beside `_rigs`: the panes at their own
seat indices plus one pane-less `PlayerRig` per guest, ordered by seat. `_rigs` stays the pane list
every camera-anchored system reads, and the six seat-indexed sites (the roster's `RigCount` and
`BuildPlayers`, `VersusMatch`, `VersusSpawnRotation`, the `lastKiller` ledger and the scoring loop)
read `_seatRigs`, so `SpawnPicker`, `VersusMatch` and `VersusSpawnRotation` index remote seats with
no change of their own. `HumanRosterBindings` carries the roster as `NetSeats`, and
`HumanFlightAdapter` skips the pane, HUD parent, camera, own-ship audio, pads, keyboard, pause key,
target selection, stunt zones, versus HUD, danger-zone eye, speed cue and own-airframe layer for a
seat that is not local, keeps everything else, hides the canvases `_Ready` builds through the
existing `SetPilotHudVisible(false)`, and takes the roster's airframe pick over the launch flags.
`FlightController.cs` and `SessionSimulation.cs` were not touched.

`CSVM.Tests/NetSeatTests.cs` (11 cases) and `CSVM.Tests/NetClockSlewTests.cs` (11 cases) are the
unit coverage; `CSVM/src/Testing/NetSeatSuites.cs`'s `net-seats` builds a three-seat match on the
`MP1` net table with one pane and two guests and asserts the roster order, the spawn walk, the
score rows, the rotation's ledger and the absent pane furniture, against the local seat in the same
build as its able-to-fail control. Docs: four `docs/architecture/Net.md` entries and their index
bullets, the `GameSession.cs`, `FlightRosterInputs.cs` and `HumanFlightAdapter.cs` entries in
`docs/architecture/Session.md`, and the colour table's decode in `docs/org/multiplayer-spawn.md`.

**Verified.** On the run branch with A1 to A5 merged, the full battery reads 4830 units passed,
377 of 378 engine suites passed and 19 of 19 golden shots hash-identical, so the seat-rig list
beside the pane list moved no pinned pixel of a single-pane or split-pane launch. The one engine
failure is the `instant-action-end` load flake described under A1; it passed alone. `NetSeatTests`
(11), `NetClockSlewTests` (11) and the `net-seats` suite pass on the merged tree.

**Original approach (kept for reference).**

**Goal.** A remote guest occupies a seat number with no pane, every seat-indexed system (spawns,
scores, markers, colours) works on it unchanged, the player ceiling is one constant, and every peer
draws the same seeded streams against the same clock.

**Evidence (confidence: traced).** The seat index is already the player id: `VersusMatch.PlayerCount`
bounds killer and victim ids at `GameSession.cs:2613` and `:2673`, `SpawnPicker.cs:92` places
players `0 … playerCount-1`, and `FlightRoster.BuildPlayers` commits the human field in ascending
player order (`docs/architecture/Session.md`, its entry). The ceiling decode is this plan's "What the
data actually ships": no coded cap, 8 shipped, 16 in the data. `Rng.Stream` is seeded per stream.
The session clock is `GameSession`'s (`docs/architecture/Session.md`, its entry).

**Approach.** A seat record in `CSVM/src/Net/` carrying the decoded pilot record's fields (peer id,
team, local flag, callsign, plane and livery choice, index, score) and a `MaxPlayers` constant set to
8 with the seat-indexed tables sized for 16, so raising it is the constant plus the colour and
respawn-fan entries. The host sends the roster, its seed and its clock at join; a guest slews its
session clock to the host's rather than snapping it. Seats without a pane skip the HUD, camera and
audio build in `HumanFlightAdapter` but keep the rest.

**Model recommendation.** High. The seat record and the seat-versus-pane split are what B11 to B14
and C23 all index against, and the edit lands in `GameSession.cs`, where a wrong list at one of the
six seat-indexed sites is a silent wrong answer rather than a build error.

**Verify.** `CSVM.Tests/NetSeatTests.cs`: the ceiling stands behind a wider table; the first eight
colours are the authored dwords; every seat has a distinct colour and the derived eight are the
stated complement; a seat outside the table throws rather than wrapping; a roster is refused when
it is empty, past the ceiling, gapped, repeated, or flown by nobody here.
`CSVM.Tests/NetClockSlewTests.cs`: the handshake offset is in force before anything is observed; a
fresh reading moves nothing on its own frame; the walk arrives inside one window and never passes
its target; the rate stays inside its bound; a negative error runs the offset the other way; a
reading past the threshold is applied at once and counted, one inside it is not; the newest reading
replaces the one being walked to; a settled slew costs nothing. The `net-seats` engine suite builds
the production roster seam with one pane and two guests on the `MP1` net table and asserts all
three seats commit in seat order, each opens on its own distinct table entry, the board keeps a row
for a guest's kill, the rotation's opening ledger holds the guest's entry, the roster's airframe
pick is what the guest flies, and every guest is built with no HUD in a pane, no pad, no keyboard,
no pause key, no target selection and no camera-anchored cue, with the local seat in the same build
as the able-to-fail control.

**⚠ Traps.** The original's pilot index is 1-based and its eighth pilot flies black; the remake's
seats are 0-based and every seat gets a colour, so do not copy the table's off-by-one when porting
the colour dwords. Do not raise the ceiling past 8 in this plan; the respawn fan and the co-op
missions are authored for fewer.

## A5 ☑ The two-session harness: a host and a guest `GameSession` in one process under `RunTests.ps1`

**Landed.** One module in `CSVM/src/Net/`, the session wiring that opens it, and the suite that
runs two whole sessions on it. `NetSession.cs` is a session's own end of the wire: it holds an
`INetTransport`, is the listener bound to it, sends a typed message under the class the type
declares (`Send`, `Broadcast`), routes an arrival by its type word to a handler registered through
`On<T>`, and counts `Sent`, `Received`, `DroppedUnknown` and `Malformed`. `Step(dt)` is the only
thing it does on its own. The one meaning it carries is the join: a host answers each peer with
the handshake and then the roster, and a guest applies both, rebuilding its seats whenever either
half lands. `On<T>` refuses the join's two types, so a later feature cannot unhook it. The one
edit to A2's files is `HandshakeMessage` in `NetMessages.cs`, minted at `0x43`: the master seed,
the host's clock, and the seat the joining peer was given, which the roster cannot carry because
its entries hold no peer id.

`LauncherContext` gained `NetTransport`, `NetHost` and `NetAirframes`. A session given a transport
opens its `NetSession` in the constructor, before its world, so a host can answer a join it has
not built for yet. A guest's start is therefore two phases: `AwaitNetJoin`, the first statement of
`StartSession`, pumps the wire up to `NetJoinSteps` of simulated link time and applies the host's
seed, roster and clock ahead of `Rng.Reset` and of `BuildSeatRigs`. Both step paths
(`_PhysicsProcess` and `DriveParentSimulation`) step the wire immediately before the simulation
step, so a payload is applied on the step after it arrived. `SessionSimulation.cs` and everything
under `CSVM/src/Flight/` were not touched, and with no transport in the context every added call
is a null-guarded no-op.

`CSVM/src/Testing/NetSessionSuites.cs`'s `net-two-session` builds a host and a guest `GameSession`
from one extraction in one process, each under its own `SubViewport` with its own `World3D`, over
a two-transport loopback mesh at 30 ms latency, 10 ms jitter and 25 per cent loss. It asserts the
guest built on the host's seed rather than the one it was launched with, the roster crossed seat
for seat with the local flags complementary, both worlds walked every seat onto the same net-table
entry (the base is drawn from the seeded stream, so a disagreed seed moves it), the join is two
reliable payloads and nothing else, a handler registered by type takes its message from inside the
guest's own step, and the two worlds stand in separate physics spaces. `CSVM.Tests/NetSessionTests.cs`
(9 cases) is the engine-free coverage of the join, the dispatch and the counters, and
`NetMessagesTests.cs` gained the handshake's round trip. Docs: the `NetSession.cs` entry in
`docs/architecture/Net.md` with its index bullet, the `GameSession.cs` and `Launcher.cs` entries in
`docs/architecture/Session.md`, the harness in `docs/architecture/Testing.md`, and `0x43` in
`docs/org/multiplayer-messages.md`'s minted list.

**Verified.** On the run branch with A1 to A5 merged, the full battery reads 4830 units passed,
377 of 378 engine suites passed and 19 of 19 golden shots hash-identical. The one engine failure
is the `instant-action-end` load flake described under A1; it passed alone. `net-two-session`
passes on the merged tree in 6.7 s with engine errors clean, reporting the second session at a
2.5 s build and 204 MiB of static memory beside the first's 3.9 s and 237 MiB (warm against cold,
so the pair bounds the second session rather than comparing like with like). `NetSessionTests` (9)
passes.

**Original approach (kept for reference).**

**Goal.** A suite can host a session, join a second session to it through the loopback transport,
step both, and assert on what each sees, on the hidden desktop the runner already uses.

**Evidence (confidence: lead-only).** `GameSession` is constructed from `(SessionSpec,
LauncherContext)` and owns its own world root and clock (`docs/architecture/Session.md`, its entry),
so two in one tree is plausible; whether two worlds of one chapter fit in memory beside each other,
and whether the resources `Launcher` keeps outside a session (camera, sun, audio) tolerate two, is
unmeasured.

**Approach.** A `Testing/` suite that builds two `GameSession`s from one extraction with the second
told it is a guest, wires them through two `LoopbackTransport`s, and steps them in lockstep from the
test. Reuse the `--run-tests=` entry and `RunProbe.ps1`'s hidden desktop; never a foreground window.

**Model recommendation.** High. This is the rig every replication item is measured on, and its
failure mode is a suite that passes while proving nothing, which is not visible from its verdict.

**Verify.** The `net-two-session` engine suite above, run under `RunTests.ps1` on the hidden
desktop. The second session costs a 2.4 s build and about 205 MiB of static memory beside the
first's 3.9 s and 237 MiB, measured warm against cold in the same process, so the pair bounds the
second session rather than comparing like with like (PERF-7). The whole suite is 6.5 s.

**⚠ Traps.** Suites run in one frame and physics can miss enabled shapes on that frame; a collider
claim needs a live session, not the harness (`docs/verification.md`). Two sessions in one tree share
the physics server; keep them in separate physics spaces or the guest's cosmetic hulls will collide
with the host's world.

# Wave B, Dogfight over the wire

## B11 ☑ Aircraft state replication: own aircraft broadcast at a fixed rate, remote aircraft interpolated

**Landed.** `CSVM/src/Net/AircraftStateCadence.cs` owns the send half and nothing else:
`SendStepInterval` (3 simulation steps, 20 Hz at the fixed step, accepted as measured) and a sequence
counter per seat, so the session's own edit stays in the step path. `StepHumanAircraft` now steps
every entry of `_seatRigs` rather than every pane, which is what makes a seat flown elsewhere run
`FlightController.StepRemotePose` and advance its buffer; outside a network match the two lists hold
the same rigs. At the end of that phase `BroadcastAircraftState` puts each seat flown here on the
wire as A2's `AircraftStateMessage`: the SIM pose, never the render pose, with position, attitude,
velocity, throttle, the three surface deflections and the nitro flag. The message already carried
every field inside its 48-byte budget, so neither the struct nor `docs/org/multiplayer-messages.md`
changed. The one handler is registered at the build, right after `BuildSeatRigs`, and hands an
arrival to that seat's `RemotePoses`, which exists only on a seat flown elsewhere, so an aeroplane
flown here can never have its pose overruled by the wire; the buffer drops a stale or reordered
sequence itself. `HumanFlightAdapter` builds that buffer for a remote seat, since its presence IS
the ownership.

**Verified.** <pending orchestrator run> The `net-aircraft-replication` engine suite (6.5 s, the
same order as `net-two-session`) flies both owners a scripted climbing right-hand roll for 240 sim
steps over a 30 ms link with 10 ms of jitter and 25 % loss, then measures each owner's own path
against the path the far peer showed for it, fitting the lag in twentieths of a step before reading
the residual (METHOD-32). The guest shows the host's aeroplane at **0.52 m mean and 1.08 m worst**
position error at a fitted 107 ms; the host shows the guest's at 0.38 m and 1.58 m at 117 ms, over
a 326 m flight with 69 degrees of cumulative turn. Over four mesh seeds the spread is 0.22 to
0.52 m mean, 1.08 to 1.79 m worst and 100 to 121 ms of lag, with 176 to 194 of 210 sampled steps
answered by interpolating and none starved, so the regression bars are set at 1.5 m mean and 5 m
worst from those readings; what a player will accept is still unmeasured and no bar claims it.
The same metric run against the OTHER aeroplane's path reads 2119.6 m, which is the able-to-fail
control (METHOD-14). Taking the conditions apart at the same rate, jitter alone reads 0.49 m mean
and loss alone 0.03 m, so the residual is the arrival stamping and not the send rate: a sample
carries no send time and the buffer stamps it on arrival. Five quick unit tests cover the cadence,
its per-seat ladders and the 16-bit wrap the receiving buffer has to accept.

**Model recommendation.** High. The wiring is three small edits, but each one is a place where the
wrong choice is invisible until two machines fly: stepping panes instead of seats leaves remote
aeroplanes frozen with every test still green, and a handler that does not check for a buffer lets
the wire write over a locally simulated pose. The measurement is the larger part of the work, and
it needs the lag fitted out before the residual means anything.

**⚠ Traps.** Read the sim-clock cadence, not the physics tick, when measuring: the sim clock on the
user's rig has run at half wall time in late campaign runs. Do not replicate the render pose; the
sample is the sim pose. `NetSession.Broadcast` reaches this peer's own peers, so on a listen server
with three or more machines a guest's samples reach the host alone until the host relays them
(Decision 9, the relay B12 registers over this handler); with two peers, which is what the suite
measures, that gap is invisible.

**Original approach (kept for reference).**

**Goal.** Every peer sees every other aircraft where its owner has it, smoothly, with the lag hidden
behind an interpolation buffer and not behind stutter.

**Evidence (confidence: direction-sound, magnitudes TUNE).** Owner-simulated aircraft with
interpolation are the flight-game norm and the original's model. The send rate, the buffer delay and
the extrapolation window are TUNE, to be measured on the harness under the loss model, not set by
feel.

**Approach.** In `StepHumanAircraft` (`SessionSimulation.cs:17`): own seats simulate as today and
enqueue a state sample every N sim steps; remote seats' controllers take the arm from A3. The sample
is position, attitude, velocity, throttle, control-surface deflections for the animator, and the
nitro flag, with a sequence number; a stale sequence is dropped. TUNE entries for the three numbers
go to `backlog.md`.

## B12 ☑ Fire, hit, damage and death events in the decoded order, scored by the host

**Landed.** `GameSession.WireNetCombat` is the whole wiring, registered at the build beside B11's
state handler, and every part of it is inert in a session with no seats on a wire. A seat flown here
announces each round it spawns through `FlightController.WeaponFired` (the gun and rocket spawns, and
a carried turret's rounds through `TurretController`), and the session puts it on the wire as the
unreliable sequenced `FireMessage`; every peer spawns that round locally from the event, so
`StepProjectiles` and `StepIncomingFire` still see nothing but their own machine's projectiles. A
strike is offered to the rig's new `HitRouter` before the local damage runs, which is where Decision
8's fork sits: the machine flying the shooter's seat decides the hit, and the host stands in for
every round no seat fired (AI pilots and world emplacements), so exactly one machine ever claims a
strike and no damage is charged twice. When the victim is flown elsewhere the decider sends the
reliable `HitMessage`, now 28 bytes carrying the struck shape and the impact in the victim's body
space, and the victim's owner applies it through the same `TakeProjectileHit` a local round takes.
That owner reports its own death as `DeathMessage` with killer and cause, the host alone runs
`VersusMatch` and broadcasts the outcome as `ScoreMessage`, and a guest writes that board through
`VersusMatch.ApplyScore` instead of scoring anything itself. A death with no seat to charge is
`Suicide` and a turret's kill carries the turret cause, as the decoded table has them.

**Decision 9, the relay.** `NetSession` carries it. `RelayToOthers` forwards an arrival to every peer
but the one it came from, `RelayToSeatOwner` forwards it to the single peer that flies the seat the
message names, and both forward the arrival's own bytes on the channel it arrived on, so a relayed
message keeps its original sender's seat and never the host's. Only a host may register one, and the
`Relayed` counter makes each forward countable from a suite. Channels are what make the star work:
sequenced discard is per sender and channel and a relayed sample arrives under the host's peer id, so
`NetChannels.ForSeat` gives every seat its own channel for its unreliable stream while `Events`
carries everything reliable. The score is not relayed, because only the host ever writes it.

**The two calls A3 left open.** A remote wreck runs its fall locally on every machine: the death
crosses as one event and each peer plays the crash rig it already has, which keeps the fall smooth
under loss and costs no further wire. A carried turret on an aeroplane flown elsewhere does not run
its gunner here at all; its rounds arrive as the owner's fire events like any other shot, so two
machines can never aim the same barrel at different targets.

**Verified.** <pending orchestrator run>

**Model recommendation.** High. The message shapes are small, but the authority fork is where this
goes wrong invisibly: a hit decided on both ends charges the damage twice, a relay that rewrites the
sender's seat scores the kill to the host, and both of those still look correct with two machines and
fail only with three. The channel rule has the same shape, since two guests sharing one unreliable
channel discard each other by sequence number and the symptom reads as packet loss.

**⚠ Traps.** Shooter-side hits favour the shooter and are what players expect; do not add lag
compensation, the original has none. The `DamageAt` log line prints only its first 12 hits, so its
absence is not evidence. A round spends armour before health, so a single strike on a pristine
airframe moves `WholeArmor` alone and an assertion reading health alone cannot fail.

**Original approach (kept for reference).**

**Goal.** A shot fired on one machine is seen on every machine, a hit the shooter's client decides
lands as damage on the victim's client, the victim reports its own death with killer and cause, and
the host scores it by the decoded tables.

**Evidence (confidence: traced).** `docs/org/multiplayer-scoring.md`: one signed score per pilot
through `FUN_0046e1b0`; Dogfight honours three events (suicide -1, kill +1, turret kill +1); a death
is message `0x12` from the dying client with killer at `+4` and cause at `+0xc`; the cause table and
what each is charged as are in the doc. `VersusMatch` already holds the local tally
(`GameSession.cs:2613`, `:2673`). ⚠ The team field the same-team arm compares is `+0x3c` of the
remote record, not of the pilot record (see row 1 of the table above, and
`docs/org/multiplayer-messages.md`).

**Approach.** Fire events unreliable and sequenced (a missed gun burst is cosmetic); hit events
reliable from the shooter to the victim's owner; damage applied on the owner as today; the death
event reliable from the owner to the host in the `0x12` shape; the host runs `VersusMatch`'s
scoring and broadcasts the score. Projectiles stay local on every peer, spawned from fire events, so
`StepProjectiles` and `StepIncomingFire` need no wire.

## B13 ☐ Host-owned spawn and respawn from `net.zrd` and the rotation, applied by guests

**Goal.** The host picks every spawn and respawn from the mission's `net.zrd` through the existing
rotation, and every guest places the aircraft where the host said.

**Evidence (confidence: traced).** `SpawnPoints.LoadNetFreeForAll` reads the table
(`docs/formats/net-spawns.md`); `docs/org/multiplayer-spawn.md` is what the executable does with a
pick; `VersusSpawnRotation` (`CSVM/src/Flight/VersusSpawnRotation.cs`) is the remake's rotation
with one living seat per point and the roomiest-entry respawn; the 16-entry block quantisation is
in this plan's data survey.

**Approach.** Rotation runs only on the host; a reliable spawn event carries the seat and the
picked entry; guests call the same placement path the owner does. A guest's own respawn is
requested from the host, not taken.

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: a harness suite asserting both peers place every seat on the same entry over a
sequence of deaths; `VersusSpawnSuites` is the pattern>

**⚠ Traps.** A guest must not run the rotation locally "to save a round trip"; two rotations
diverge on the first death.

## B14 ☐ Match state: clock, limits, end and scoreboard replicated

**Goal.** Every peer shows the same clock, the same scores and the same end, and the wrap-up board
holds on every machine when the host's match ends.

**Evidence (confidence: traced).** `docs/org/multiplayer-scoring.md` "How a match ends" and "The two
limits are exclusive"; `VersusBoard` and `VersusHud` are the local presentation; the ending hold is
`StepEndingHold` (`SessionSimulation.cs:13`).

**Approach.** The host is the only writer of match state; a reliable match-state message carries
the clock, the limits and the ending; `StepVersus` on a guest applies rather than advances. The
scoreboard is derived from replicated scores, not sent.

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: a harness suite ending a match on the kill limit and on the time limit and
asserting the hold on both peers>

**⚠ Traps.** <TODO: none known yet>

## B15 ☑ The ENet transport, host and join by direct IP with UPnP, and the multiplayer door's join board (`BL-951`)

**Landed (the transport).** `CSVM/src/Net/EnetTransport.cs` implements A1's `INetTransport` over
Godot's `ENetMultiplayerPeer`, and is the only file under `CSVM/` that names a Godot networking
type. The peer wrapper was taken over `ENetConnection` for two reasons: its three transfer modes
are exactly the three reliability classes (`Unreliable` is an unsequenced packet, `UnreliableOrdered`
is ENet's sequenced unreliable one, which itself discards a payload older than the newest delivered
on its channel, and `Reliable` is reliable ordered), and it settles peer ids across the whole mesh,
which `NetSeat.PeerId` needs and which a raw `ENetConnection` would have made the transport invent
a handshake for. Two static constructors: `Host(port, maxPeers, bindAddress = "*")` opens a listen
server and is peer 1, `Join(address, port)` starts a join whose success arrives as
`OnPeerConnected(1)` on a later step. `Send` picks the mode from `NetReliability` and passes the
caller's channel through, with `MaxChannel = 8` asked for at connect so a channel above zero has a
negotiated ENet channel to ride. `Step` makes one poll, from which every join, departure and
payload is reported, so the seam's "nothing arrives between steps" rule holds here as on the
loopback. `LinkState` (`Connecting`, `Up`, `Down`) is the join board's readout, named here so no
caller learns Godot's own enum; `Close`/`Dispose` releases the socket.
`CSVM/src/Net/UpnpPortMap.cs` is the optional door in the host's router: `Map(port, description)`
returns `Mapped`, `NoGateway`, `Refused` or `TimedOut` with the external address when one was
learned, `Unmap(port)` takes it down, neither throws, and both block for the gateway search, so
they run when hosting opens and closes rather than on a frame or in a step.
`CSVM/src/Testing/EnetTransportSuites.cs`'s `enet-transport` hosts and joins on `127.0.0.1` inside
the test process and asserts the join on both ends, the ban on delivery without a step, a reliable
round trip whole and on its own channel, a sequenced burst never delivered out of order, an
unreliable payload, and a hang-up that empties both rosters and swallows the sends after it.
`CSVM.Tests/EnetTransportTests.cs` pins the class-to-delivery mapping and the argument refusals;
`CSVM.Tests/NetNamespaceDependencyTests.cs` now exempts the two carriers by full name from the
no-engine-type rule and holds the networking half of that exemption to `EnetTransport` in a second
fact. The transport also carries `INetLink` (`LinkState`, `PendingPayloads`) and a hold-and-replay:
a socket with no listener holds what lands and replays it on `Bind`, which is what lets a guest's
handshake survive the gap between the join landing on the board and the session existing.

**Landed (the door).** `CSVM/src/UI/Menu/NetPlayFeature.cs` is the door as a shared `IMenuFeature`,
naming neither an engine type nor a carrier. It owns the port and the address a board edits, the
socket `OpenHost`/`OpenJoin` open, and the readouts a board draws (`Stage`, `Peers`, `Link`,
`PortMap`, `Fault`, `HostStarted`). Both carrier factories and both port-mapping calls arrive as
delegates, so the launcher passes `EnetTransport` and `UpnpPortMap` while a suite passes a loopback
mesh and no router. A join lands on `Step`, never in the press, and gives up after
`JoinTimeoutSeconds`. The UPnP ask runs on its own thread from where hosting opens, so no frame
waits on the gateway search; the unmap does not, since a mapping left behind is a door standing
open in the player's router. `BuildLaunch` hands the open wire out as a `MenuNetLaunch` and keeps
nothing but the mapping, which `Close` gives back.

**Landed (the board and the wiring).** `CSVM/src/UI/LaunchMenu.cs` draws the board: a Multiplayer
row at the end of the Mode screen opens a five-row Network screen (port, address, Host a match,
Join by address, Continue) whose status line reports the port, the link state, the joined count and
the external address once the router has named one. `PlayerSetupFeature.Refusal`/`CanLaunch` and
the static `LaunchMenu.CanLaunch` take an optional `networked` flag, so a networked Dogfight no
longer asks for a second local pilot. `CSVM/src/Session/Launcher.cs` registers the door with the
real carrier and router calls, carries `LaunchExit.Net` into the one `LauncherContext` it builds
(`NetTransport`, `NetHost`, `NetSeats` and `NetAirframes` over
`UI/PlanePickerRoster.StockAirframes`, which A5's suite builds its seats the same way from), and
closes the launch at `ReturnToMenu` and at the quit. `CSVM/src/SessionSpec.cs` parses
`--net-host[=port|address:port]` and `--net-join=address[:port]` through public `ParseHost` and
`ParseJoin`, and a CLI guest holds at the launch until the link stands or 30 seconds pass.
`GameSession.cs` is untouched: the session already takes the wire through `LauncherContext`.

**Landed (the smoke).** `CSVM/src/Testing/NetEnetSessionSuites.cs`'s `net-enet-join` puts a host
and a guest `GameSession` over two real ENet sockets on `127.0.0.1`, walking the port until one
opens, and asserts the seed, the roster and the local seats a guest takes from the handshake plus
the payload counters after a lockstep. Its able-to-fail control is the hold-and-replay: the guest's
socket is stepped unbound until it holds at least two payloads, and binding the session replays
them. `CSVM/src/Testing/MenuNetPlaySuites.cs`'s `menu-net-door` drives the board as a player does,
including the control that an open door refuses to move its own port row.
`CSVM.Tests/NetPlayFeatureTests.cs` pins the door itself over a loopback mesh in ten facts.
⚠ The honest limit: this is one process. A second game process cannot be driven from the hidden
desktop without putting a window on somebody's screen, so what crosses here is the ENet carrier
over real UDP, not the process boundary. Two machines, and a NAT between them, remain unmeasured.

**Landed (the release documents).** `.github/SECURITY.md` now states the surface as it is (a UDP
listener behind an explicit host action, nothing listening otherwise, no server contacted) and puts
the listener and its port mapping in scope. `docs/PLAN-public-release.md`'s grep list is narrowed
to the terms that are still absent and names the two files the ENet and UPnP types are confined to.

**Verified.** <pending orchestrator run>

**Owed.** A host and a guest agree on the map, the match rules and the aircraft by hand: nothing is
exchanged before the session is built, so each end picks its own and a disagreement is silent, and
a host's roster gives every remote seat the local pilot's airframe (`BL-1022`). The Original
presentation has no board over the shared door (`BL-1021`). `BL-951`'s local join board is
untouched and stays open. LAN and WAN play, and the firewall and router behaviour that comes with
them, need two machines and a friend.

**Original approach (kept for reference).**

**Goal.** One player hosts from the menu, another joins by address, and both land in a Dogfight
that plays as it does on the harness.

**Evidence (confidence: lead-only).** Godot's `ENetMultiplayerPeer` carries reliable, unreliable and
unreliable-ordered channels over UDP and its `UPNP` class maps a port on the host's router; both
untested here. `BL-951` (`backlog.md`, the local multiplayer door and join board) describes the
board this item widens with network seats; it was not re-verified still-open in the scoping
session. Re-verified open: `git log --grep=BL-951` finds only the retag that moved it from
`[Next: decide]` to `[Next: code]` and the merge carrying it, no closing commit, and
`git log -S"JoiningOpen"` and `-S"ClaimP1Pad"` show the scattered per-screen join the item
describes still in place. The network door landed here is a separate board and leaves it open.

**Approach.** `EnetTransport` implements A1's interface and is the only file under `CSVM/` naming a
Godot networking type. The menu door opens the join board from `BL-951` with a host and a join
action; a joined guest appears as a seat on every peer's board. UPnP is attempted and reported, never
required.

**Model recommendation.** A top-tier model. The item spans four namespaces at once (the carrier,
the shared feature, the board and the launcher) under two namespace-dependency scans that reject
the obvious shortcuts, and the timing bug it had to find, a guest's handshake arriving before its
session exists, is visible only by reading the session's join spin against the transport's step.

**Verify.** The transport half is the `enet-transport` engine suite, both ends in one process over
`127.0.0.1`, which settles that ENet hosts and joins itself inside a single Godot process
(connected in under 10 ms, whole suite 0.23 s). A stale sequenced payload cannot be provoked there,
because a loopback socket never reorders, so the discard rule stays asserted on the loopback
carrier while the ENet side asserts the delivery class that implements it. Still owed: the second
headless instance joined over loopback IP, and the LAN and WAN checks at the controls, with a
friend.

**⚠ Traps.** The public-release plan's "no network code" grep and `SECURITY.md`'s surface statement
are both false as of the transport half; the second half amends them in its landing commit. Do not
add a relay or NAT traversal here; that is the Steam or EOS decision this plan leaves open. A
wildcard bind is what makes Windows ask about the firewall, so anything scripted binds the loopback
address instead.

# Wave C, campaign co-op

## C21 ☐ The host-owned mission director: objective graph transitions, cutscene codes and wingman spawns as events

**Goal.** A guest sees every objective transition, cutscene, letterbox, radio line and wingman
spawn the host's `CampaignDirector` produces, at the same moment on the shared clock.

**Evidence (confidence: lead-only).** `CampaignDirector` runs the objective graph, the escort
repair, the music and the danger-zone tracker, and `BindCallbackHost` takes the cutscene callback
slot (`docs/architecture/Session.md`, its entry); the graph's transition set is `ObjectiveGraph`.
Which transitions have side effects a guest must replay, versus ones a guest can derive from
replicated state, is unmapped.

**Approach.** The director runs unchanged on the host. Every graph transition and every callback
code becomes a reliable event a guest's director applies in "follow" mode: it does not evaluate
rules, it replays transitions. Cutscenes then play locally on the guest from the code, as they do
today.

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: a harness suite running one campaign mission with a scripted host flight and
asserting the guest's objective state and cutscene codes match the host's log; the mission to use is
unchosen>

**⚠ Traps.** The scripted `player` token, roster leaders and anchored net trailers are a separate
P1 identity (`CampaignHumanField`'s entry); the host is P1 and a guest never is.

## C22 ☐ Host-owned AI and world: aircraft, zeppelins, turrets, generators, vehicles and destructibles as spawn, state and death events

**Goal.** Every AI aircraft, zeppelin, turret, generator wave, surface vehicle and destructible is
where the host has it on every guest, and dies when the host says.

**Evidence (confidence: lead-only).** The phases are named on `ISessionSimulationRuntime`
(`SessionSimulation.cs:18` to `:24`); which of them a guest can run cosmetically from a seed and a
clock (authored patrol nets, generator cycles, scripted path vehicles) versus which need state
samples (engaging AI aircraft, zeppelin cannons) is unmapped and is this item's first task.

**Approach.** Per phase, one of two labels: replay-from-seed (the guest runs the same deterministic
choreography from the shared seed and clock and takes only spawn and death events) or
state-replicated (the host samples the entity like an aircraft and the guest interpolates). AI
aircraft are state-replicated; destructible deaths are events; the rest is decided by the mapping.

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: per phase, a harness assertion that the guest's entity set matches the host's
after a scripted mission segment>

**⚠ Traps.** AI reaction rolls (`AiModeMachine`'s steady-hand and sixth-sense) draw from an `Rng`
stream; a guest that runs the AI locally from the same seed still diverges on the first
world-dependent branch, so "same seed" is not "same AI". Replicate the AI's state, do not re-run it.

## C23 ☐ Guests as the human field: `CampaignHumanField` and the objective rules see remote humans, the scripted P1 stays the host

**Goal.** An objective that waits for a human's arrival, counts live humans in a captured group, or
tracks a wreck, sees a guest as it sees a splitscreen partner.

**Evidence (confidence: traced for the rules, lead-only for the wiring).** `CampaignHumanField`
is engine-free over `HumanState` (position, captured group, wreck state), produced only by
`CampaignDirector.World.SnapshotHumans`; `Travelers` uses the nearest human, `LiveInGroup` counts
non-wrecked humans; rules are pinned by `CSVM.Tests/CampaignHumanFieldTests.cs`
(`docs/architecture/Session.md`, its entry).

**Approach.** `SnapshotHumans` on the host includes remote seats' interpolated poses and their
replicated wreck and capture state; the rules need no change. Capture and wreck state on a guest's
aircraft are the guest's to report (its own death is its own `0x12`).

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: a `CampaignHumanFieldTests` extension with a remote human, plus a harness run of a
mission whose objective waits on arrival>

**⚠ Traps.** <TODO: none known yet>

## C24 ☐ The co-op session flow: cabin and briefing on the host, guests joining into the mission, mission end and debrief on every peer

**Goal.** The host walks the cabin, briefing and flight check as today, guests join before launch
and fly, and when the mission ends the host's profile records the attempt while every guest returns
to the board.

**Evidence (confidence: lead-only).** Mission end records the attempt, folds the persist log into
the profile and holds before the cabin behind `LeavingFade` (`CampaignDirector`'s entry); the local
splitscreen campaign co-op already launches guests as the human field through `GameSession`'s
grid selection (`docs/architecture/Session.md:78`). What a guest sees during the host's briefing is
undecided.

**Approach.** The host's `SessionSpec` carries the campaign position as today; the launch message
to guests carries chapter, mission and roster, and guests build the same session with no profile.
Mission end: the host records, guests hold and return. <TODO: decide what a guest sees while the
host is in the cabin and briefing; a waiting board is the least work>

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: at the controls, two machines, one mission end to end>

**⚠ Traps.** A guest has no profile; every path that writes one (`CampaignProfileStore`,
`CampaignSnapshot`'s photographs, the memento) must be a no-op on a guest, not a crash.

# Wave D, hardening

## D31 ☐ Latency and loss soaks, desync instruments and a `--debug-net` readout

**Goal.** A soak on the harness at chosen latency and loss reports position error, event order
violations and dropped-message counts, and a live session can show the same numbers on screen.

**Evidence (confidence: lead-only).** The harness (A5) and the loopback loss model (A1) are the
instruments; the acceptable numbers are unmeasured.

**Approach.** A soak suite over a scripted Dogfight and one co-op mission at a small matrix of
latency and loss; a `--debug-net` flag in the pattern of `--debug-anim` that prints the counters to
the HUD and the log.

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: the soak's own thresholds, once measured>

**⚠ Traps.** The file sink takes only `Log.*` lines and debug lines are flag-gated; absence of a
line is not evidence.

## D32 ☑ The Steam transport flag: a build-time gate with a stub, so the seam is proven before any SDK arrives

**Landed.** `CSVM/src/Net/SteamTransport.cs` is the Steam carrier's place in the seam: it
implements `INetTransport`, and its `Host`, `Join` and private constructor all throw
`InvalidOperationException` reading "not built with the Steamworks SDK", with one arm naming the
missing define and the other naming the missing SDK. `SteamBuild` is the `CSVM_STEAM` define,
set by the `CsvmSteam` MSBuild property added to `CSVM/CSVM.csproj` (appended to
`DefineConstants`, never assigned over the Godot SDK's own symbols).
`CSVM/src/Net/NetCarrier.cs` is the one place a carrier is chosen: `Host`, `Join`, `PortMap` and
`PortUnmap`, selecting ENet or Steam off that define, with the router door null for a carrier
that needs none. `CSVM/src/Session/Launcher.cs` registers the door through it and opens
`--net-host`/`--net-join` through it, and its command-line link wait now reads `INetLink` and
`IDisposable` instead of casting to `EnetTransport`, so nothing in the launcher names a carrier.
`CSVM.Tests/NetNamespaceDependencyTests.cs` exempts the stub exactly as the two carriers are
exempted and adds a third fact: over the whole assembly, no type outside `CSVM.Net.SteamTransport`
may name `Steamworks.`, `Godot.Steam` or `GodotSteam`. `CSVM.Tests/SteamTransportTests.cs` is the
unit fact on the throw and the selection, written so both flavours run it. The `enet-transport`
suite gained one check that this build's selection is the ENet socket. Docs: new entries in
`docs/architecture/Net.md` with their index bullets, the carrier clause in
`docs/architecture/Session.md`'s `Launcher.cs` and `docs/architecture/UI.md`'s `NetPlayFeature.cs`
entries, and the build flavour in `docs/tooling.md`. No CLI flag was added, so `docs/cli.md` is
untouched.

**Verified.** <pending orchestrator run>

**Original approach (kept for reference).**

**Goal.** A build with the Steam flag set constructs a Steam transport stub through A1's interface
and everything above it is unchanged, proving the seam before the Steamworks SDK, which cannot be
committed to this repo under its licence, is ever added.

**Evidence (confidence: lead-only).** GodotSteam (GDExtension) and Facepunch.Steamworks (C#) both
reach Steam Networking Sockets, the relay and lobbies from Godot; neither is evaluated here, and
naming them is the whole of what this item says about them.

**Approach.** A `CSVM_STEAM` define, a `SteamTransport` that throws "not built" at construction
without the SDK, and the transport selection in one place. No SDK, no app id, no store page in this
plan.

**Model recommendation.** Medium. The work is one stub, one selection point and a build property;
the judgement in it is where the seam is cut, and A1 already cut it.

**Verify.** Both flavours build clean with zero warnings and pass the unit suite:
`dotnet build CSVM/CSVM.sln` and `dotnet build CSVM/CSVM.sln -p:CsvmSteam=true`, then
`.\RunTests.ps1 -SkipEngine -SkipGoldens` and `dotnet test CSVM.Tests/CSVM.Tests.csproj
-p:CsvmSteam=true`. The define is proven to reach the compiler by the folded message arm: the
Steam-flavour `CSVM.dll` carries "CSVM_STEAM is defined but no SDK is linked" and the default one
carries the other arm. The three network suites (`enet-transport`, `net-enet-join`,
`menu-net-door`) run on the default flavour.

**⚠ Traps.** The Steam listing itself is a legal and distribution decision (trademark, the "requires
your own copy" model OpenTTD uses, Valve's review) and is not this plan's to take. `SteamBuild` is
a `const`, so a consumer inlines it: a stale `CSVM.Tests` build against a freshly reflavoured
`CSVM.dll` would report the old flavour, which is why the flavoured unit run rebuilds both.
