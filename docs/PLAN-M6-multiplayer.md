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
| 8 | Hit authority | **Shooter's client decides the hit, the victim applies the damage and reports its own death, the host scores**, the decoded original's order, no lag compensation |

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
3. ☐ The remote-airframe arm: a `FlightController` fed a received pose instead of a flight model
4. ☐ Network seats: seat identity without a pane, the 8-behind-16 ceiling, the seed and match-clock handoff
5. ☐ The two-session harness: a host and a guest `GameSession` in one process under `RunTests.ps1`

### Wave B, Dogfight over the wire

11. ☐ Aircraft state replication: own aircraft broadcast at a fixed rate, remote aircraft interpolated
12. ☐ Fire, hit, damage and death events in the decoded order, scored by the host
13. ☐ Host-owned spawn and respawn from `net.zrd` and the rotation, applied by guests
14. ☐ Match state: clock, limits, end and scoreboard replicated
15. ☐ The ENet transport, host and join by direct IP with UPnP, and the multiplayer door's join board (`BL-951`)

### Wave C, campaign co-op

21. ☐ The host-owned mission director: objective graph transitions, cutscene codes and wingman spawns as events
22. ☐ Host-owned AI and world: aircraft, zeppelins, turrets, generators, vehicles and destructibles as spawn, state and death events
23. ☐ Guests as the human field: `CampaignHumanField` and the objective rules see remote humans, the scripted P1 stays the host
24. ☐ The co-op session flow: cabin and briefing on the host, guests joining into the mission, mission end and debrief on every peer

### Wave D, hardening

31. ☐ Latency and loss soaks, desync instruments and a `--debug-net` readout
32. ☐ The Steam transport flag: a build-time gate with a stub, so the seam is proven before any SDK arrives

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

**Verified.** <pending orchestrator run>

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

**Verified.** <pending orchestrator run>

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

## A3 ☐ The remote-airframe arm: a `FlightController` fed a received pose instead of a flight model

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
`:950`) need no change.

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: a suite feeding a scripted sample stream and asserting the interpolated pose;
a live check that a remote aircraft's guns, damage stages and crash rig fire on a pose-driven
controller; the exact probes are unwritten>

**⚠ Traps.** The ground-blow probe and the nearest-human distance are filled by the caller after
`Read` (`IFlightInputSource.cs:8`); a pose-driven controller must still not run them, or a remote
aircraft will bounce off terrain it never touched on its owner's machine. Collision on a remote
aircraft is cosmetic and for being hit; it must never move the pose.

## A4 ☐ Network seats: seat identity without a pane, the 8-behind-16 ceiling, the seed and match-clock handoff

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

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: a suite building a session with pane-less seats and asserting the roster, the
spawn placement and the score tables index them; a clock-slew suite; the exact assertions are
unwritten>

**⚠ Traps.** The original's pilot index is 1-based and its eighth pilot flies black; the remake's
seats are 0-based and every seat gets a colour, so do not copy the table's off-by-one when porting
the colour dwords. Do not raise the ceiling past 8 in this plan; the respawn fan and the co-op
missions are authored for fewer.

## A5 ☐ The two-session harness: a host and a guest `GameSession` in one process under `RunTests.ps1`

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

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: the harness's own smoke, two sessions built and stepped with zero errors; the
memory and resource check is unmeasured>

**⚠ Traps.** Suites run in one frame and physics can miss enabled shapes on that frame; a collider
claim needs a live session, not the harness (`docs/verification.md`). Two sessions in one tree share
the physics server; keep them in separate physics spaces or the guest's cosmetic hulls will collide
with the host's world.

# Wave B, Dogfight over the wire

## B11 ☐ Aircraft state replication: own aircraft broadcast at a fixed rate, remote aircraft interpolated

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

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: on the harness, the position error between the owner's pose and the remote's
interpolated pose over a scripted flight at chosen latency and loss; the acceptable error is
unmeasured>

**⚠ Traps.** Read the sim-clock cadence, not the physics tick, when measuring: the sim clock on the
user's rig has run at half wall time in late campaign runs. Do not replicate the render pose; the
sample is the sim pose.

## B12 ☐ Fire, hit, damage and death events in the decoded order, scored by the host

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

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: a harness suite where the guest kills the host and the host kills the guest,
asserting the score on both, plus the suicide and turret-kill causes; the exact assertions are
unwritten>

**⚠ Traps.** Shooter-side hits favour the shooter and are what players expect; do not add lag
compensation, the original has none. The `DamageAt` log line prints only its first 12 hits, so its
absence is not evidence.

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

## B15 ☐ The ENet transport, host and join by direct IP with UPnP, and the multiplayer door's join board (`BL-951`)

**Goal.** One player hosts from the menu, another joins by address, and both land in a Dogfight
that plays as it does on the harness.

**Evidence (confidence: lead-only).** Godot's `ENetMultiplayerPeer` carries reliable, unreliable and
unreliable-ordered channels over UDP and its `UPNP` class maps a port on the host's router; both
untested here. `BL-951` (`backlog.md`, the local multiplayer door and join board) describes the
board this item widens with network seats; it was not re-verified still-open in the scoping
session. <TODO: re-verify `BL-951` still-open against `git log --grep=BL-951` and
`UI/Menu/Original/OriginalSeats.cs`>

**Approach.** `EnetTransport` implements A1's interface and is the only file under `CSVM/` naming a
Godot networking type. The menu door opens the join board from `BL-951` with a host and a join
action; a joined guest appears as a seat on every peer's board. UPnP is attempted and reported, never
required.

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: a smoke with a second headless instance on the same machine, joined over
loopback IP; the LAN and WAN checks are at the controls, with a friend>

**⚠ Traps.** The public-release plan's "no network code" grep and `SECURITY.md`'s surface statement
both become false with this item; amend both in the landing commit. Do not add a relay or NAT
traversal here; that is the Steam or EOS decision this plan leaves open.

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

## D32 ☐ The Steam transport flag: a build-time gate with a stub, so the seam is proven before any SDK arrives

**Goal.** A build with the Steam flag set constructs a Steam transport stub through A1's interface
and everything above it is unchanged, proving the seam before the Steamworks SDK, which cannot be
committed to this repo under its licence, is ever added.

**Evidence (confidence: lead-only).** GodotSteam (GDExtension) and Facepunch.Steamworks (C#) both
reach Steam Networking Sockets, the relay and lobbies from Godot; neither is evaluated here.

**Approach.** A `CSVM_STEAM` define, a `SteamTransport` that throws "not built" at construction
without the SDK, and the transport selection in one place. No SDK, no app id, no store page in this
plan.

**Model recommendation.** <TODO: not settled in the scoping session>

**Verify.** <TODO: both build flavours compile and the test suite passes on both>

**⚠ Traps.** The Steam listing itself is a legal and distribution decision (trademark, the "requires
your own copy" model OpenTTD uses, Valve's review) and is not this plan's to take.
