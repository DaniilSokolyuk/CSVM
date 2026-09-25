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
| 5 | Player ceiling | **16, one constant with every seat-indexed table built to the same width**; the executable has no coded cap, the shipped lobby shows `Players (1 of 16)` and the data holds 16. The authored colour table and the 45-degree respawn fan serve eight, so seats 8 to 15 take derived colours (`BL-1017`) and a wrapped fan. Revised from 8 when the lobby screenshots arrived |
| 6 | Steam | **Not decided here.** The transport goes behind a flag so a Steam build (Steam Networking Sockets, relay, lobbies, invites) can be added without touching the session; the store listing is a separate legal and distribution decision |
| 7 | Where the seam goes | **Two interfaces above the transport**: a remote-airframe arm beside `IFlightInputSource` on the aircraft side and a transport interface on the session side; a remote human is a pose that arrives late, never a stick that arrives late |
| 8 | Hit authority | **Shooter's client decides the hit, the victim applies the damage and reports its own death, the host scores**, the decoded original's order, no lag compensation. The hit itself is a **reliable** message, confirmed after A2 found the original batches hits inside its unreliable aircraft-state packet: a lost hit would be a lost kill |
| 9 | Topology | **Star, the host relays.** A guest connects to the host only; the host forwards every guest's aircraft state and events to the other guests, so one port and one UPnP mapping serve a match and a guest-to-guest packet costs one extra hop. No mesh between guests |
| 10 | The co-op door | **The cabin hosts, the Connection screen joins, the Connection screen's Host opens the lobby; all in the Original presentation**, which is the default (the Built-in menu is disabled by default and keeps C25's boards). A campaign host presses a Host Co-op button in the Original cabin's button column, drawn in the original's button style; while hosting it reads Close Network, the NETWORK OPEN band shows the address and guest count, and a player chip per guest shows its Ready mark. Close Network, or leaving the cabin for the main menu, returns every guest to the Connection screen with "Host closed the game". The game name is `<profile>'s campaign`; there is no password this milestone. A guest joins from the original's Multiplayer Connection screen, by LAN broadcast search into the LAN TCP/IP Games list or by Internet IP address. Co-op caps at four humans (`n/4`), the campaign's P1 to P4 human field; Dogfight keeps Decision 5's 16. The Connection screen's Host opens a rebuilt Multiplayer Lobby for the original's modes, of which Dogfight is the only live one; campaign co-op has no lobby and needs no Dogfight door. Items F51, F52 and C24 |

## ⚠ Read this before implementing anything

| # | The wrong claim | How it died |
|---|---|---|
| 1 | ~~`docs/org/multiplayer-scoring.md` names offset `+0x3c` of the pilot record as the team field~~ **This row was itself wrong; A2's re-read settled it** | Two records carry a team field and the row conflated them. `FUN_00498bf0`'s same-team arm compares `+0x3c` of the **remote** record, the `0x1090`-byte object `FUN_00499d80` looks up over `DAT_0071c7a4`, which `FUN_00495310` fills from the pilot record's `+0x08` team object (`FUN_0046f3c0`) when teams are on and from the pilot index when they are off; `FUN_00499a50`'s team-chat arm compares the same field, and the colour table `00628eb4` is indexed by it. `+0x08` of the *pilot* record (`FUN_00413db0`, `FUN_0046ea40`) is the team object it is derived from. The scoring doc now names the record with the offset |
| 2 | The original caps a match at a coded number of players | No constant bound exists. The pilot list is an STL list (head `0071c150`, count `0071c154`, walked by `FUN_0046f110`) and the count is never compared against a maximum. The only gate is DirectPlay's `dwMaxPlayers`, filled from the lobby screen variable `nMaxPlayers` (string `006192e0`, global `00642f08`), which the code only ever resets to 0 |

| Confidence | Items | What that means for you |
|---|---|---|
| **Traced to an exact mechanism in code, with the data that proves it** | B12, B13, B14 (scoring, spawn placement and match end are decoded in `docs/org/`), A4 (ceiling, from this plan's own decode) | Confirm the trace, then implement. |
| **Direction sound, magnitude a judgement call** | B11 (send rate, interpolation buffer, extrapolation window), D33 (the lease length) | The *what* is settled; the *how much* is TUNE, add it to `backlog.md`'s TUNE list, don't invent it as fact. |
| **Leads only, no mechanism yet** | A1, A2, A3, A5, B15, C21 to C24, D31, D32, F51, F52 | Budget for investigation; this may end in a disproof. |

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

**The shipped lobby, as seen.** `OriginalScreenshots/Multiplayer Lobby Mission Options.png`,
`... Select Plane.png`, `... Select Ammo Guns.png` and `... Select Ammo Rockets.png` (the main
checkout only, git-ignored) show the 1.02 lobby: the roster reads `Players (1 of 16)`, which is
why Decision 5 is 16 rather than the 8 first assumed; each pilot has a Ready box; the host's Mission Options
page holds the environment, the mission type, Victory Conditions as one of Time (mins, default
10) or Score (default 40), Restrict Number of Teams with a range (default 2 to 2), Limited Lives,
Auto Respawn, Allow Custom Planes and Outlaw Components; Select Plane and Select Ammo (a shell
per gun calibre, a rocket per hardpoint, eight rows) are per pilot; the fourth tab is Game
Scores. `Multiplayer Connection.png` and `Multiplayer Connection Screen.png` are the Connection
page (MSN Gaming Zone, LAN IPX, LAN TCP/IP, Internet by IP address, Modem-to-Modem) and the LAN
games list. B14 reads the two victory conditions and the Game Scores tab from here; F51 builds
the Connection page and games list, and F52 (`BL-1022`) the lobby.

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
13. ☑ Host-owned spawn and respawn from `net.zrd` and the rotation, applied by guests
14. ☑ Match state: clock, limits, end and scoreboard replicated
15. ☑ The ENet transport, host and join by direct IP with UPnP, and the multiplayer door's join board (`BL-951`)

### Wave C, campaign co-op

21. ☑ The host-owned mission director: objective graph transitions, cutscene codes and wingman spawns as events
22. ☑ Host-owned AI and world: aircraft, zeppelins, turrets, generators, vehicles and destructibles as spawn, state and death events
23. ☑ Guests as the human field: `CampaignHumanField` and the objective rules see remote humans, the scripted P1 stays the host
24. ☐ The co-op session flow: guests follow the host's cabin and briefing, pick from the host's hangar, Ready before launch, and share the debrief
25. ☑ The co-op door: the campaign flow opens to the network, guests join from the Network board and wait for the host's launch
26. ☑ Host-decided positional starts and the airframe swap: landing approaches, the ladder switch, `PlayerRange` and codes 965 to 967 for a guest

### Wave D, hardening

31. ☑ Latency and loss soaks, desync instruments and a `--debug-net` readout
32. ☑ The Steam transport flag: a build-time gate with a stub, so the seam is proven before any SDK arrives
33. ☐ The router mapping on a finite lease with a stale mapping cleared, and a fuzz of every message reader

### Wave E, the rest of the host-owned world

41. ☑ Host-owned AI spawns: generator launches, Black Hat launches and `WAKEUP_*` as spawn events carrying the host's admission ordinal
42. ☑ Zeppelin paths from the host: the path position as a periodic state message, in the original's `0x1e` shape
43. ☑ Surface vehicles from the host: patrols and `WARP_VEHICLE` placed by the host, not replayed from a diverging draw
44. ☑ Destructible chip damage: a pool's health between stages mirrored on every guest

### Wave F, the Original presentation's multiplayer screens

51. ☐ The network doors in the Original presentation: the cabin's Host Co-op button, and the Connection page with LAN discovery and the games list (`BL-1021`)
52. ☐ The Multiplayer Lobby rebuilt in the original's layout, with Dogfight live (`BL-1022`)

## Dependency and parallelism notes

A1 and A2 block everything else and can run in parallel with each other; A3, A4 and A5 need both
and can then run in parallel (A3 owns `CSVM/src/Flight/`, A4 owns the seat and session-spec side of
`CSVM/src/Session/`, A5 owns `CSVM/src/Testing/` and the runner). Wave B is a chain, B11 → B12 → B13
→ B14, because each replicates over the previous one's channel; B15 needs only A1 and A2 and can run
beside B11 to B14 as long as it stays out of `GameSession.cs`. Wave C needs B12 and B14 and is a
chain, C21 → C22 → C23 → C24. D31 needs Wave B; D32 needs only A1. File contention: B11, B12, B14,
C21 and C22 all edit `GameSession.cs` and `SessionSimulation.cs`, never run two of them in parallel
worktrees; give each concurrent agent one namespace and name the files it may not touch.
C25 needs only B15 and owns the UI side (`LaunchMenu`, `CampaignFlow`, the Network screen), so it
can run beside C23; C24 needs C25, since C25's door is how a guest reaches C24's launch. C26 needs
C23 and edits `GameSession.cs`, so it never runs beside C24 or E41.
Wave E needs C22 and extends its `NetWorldLink`; E41 edits `GameSession.cs`'s AI capture phase and
runs alone against C23, C24 and any other `GameSession.cs` item. E42 and E43 own their own world
runtimes and can run beside each other; E44 owns `AnimRuntime`'s spend and the `0x48` world event.
AI voice on a guest is not a plan item; it is GitHub issue #22.

The remaining order is D33 and F51 first, then C24 and F52 one after the other, in either order.
- **D33** needs nothing open. It owns `Net/UpnpPortMap.cs`, a new engine-free lease policy beside
  it and a new unit fuzz file, and touches `UI/Menu/NetPlayFeature.cs` only for the lease renewal.
  It can run beside F51 if F51 lands its `NetPlayFeature.cs` edits after D33's, or the second to
  land merges that one file by hand; it never touches `GameSession.cs`.
- **F51** needs C25. It owns the Original shell's multiplayer entry (`OriginalShell.cs`, a new
  Connection screen module under `UI/Menu/Original/`), the cabin's button column
  (`OriginalCampaignScreen.cs`), a new LAN discovery carrier under `Net/`, and edits
  `NetPlayFeature.cs`, `CoopDoorText.cs`, `NetMessages.cs` (the advert), `Session/Launcher.cs`
  (registering the discovery carrier), `OriginalCoverageTests.cs`, `MenuOriginalSuites.cs`,
  `NetNamespaceDependencyTests.cs`, `.github/SECURITY.md` and `docs/PLAN-public-release.md`. It does
  not touch `GameSession.cs`.
- **C24** needs F51 (a guest reaches the co-op flow through F51's doors), C25 and C26. It edits
  `GameSession.cs`, `Session/Launcher.cs`, `CampaignDirector.cs`, `OriginalCampaignScreen.cs`,
  `OriginalPresentation.cs`, `CampaignFeature.cs`, `NetPlayFeature.cs` and `NetMessages.cs`.
- **F52** needs F51 (the Connection page's Host opens it, and a Dogfight guest lands in it). It
  edits `GameSession.cs` (the lives rules), `Session/Launcher.cs` (the roster from the lobby's
  picks), `NetSeats.cs`, `NetMessages.cs`, `NetPlayFeature.cs` and `OriginalShell.cs`.
- C24 and F52 share `GameSession.cs`, `Launcher.cs`, `NetMessages.cs` and `NetPlayFeature.cs`, and
  both need a Ready roster (Ready per guest, Launch greyed until all are Ready); never run them in
  parallel worktrees. Whichever lands first builds the Ready roster presentation-neutral, and the
  second reuses it. Every new message id is the next free one at the time it is minted (`0x4F` is
  the next free now, `NetWorldEvent` code 6), so two items minting at once collide.

## At the milestone's landing

These leave the milestone's scope and are filed as `backlog.md` items in the closing commit, each
id minted through `New-ItemId.ps1`:
- Capture the Flag, with team play (Restrict Number of Teams, Create Team), which it needs.
- Zeppelin vs.
- Custom planes over the network: a co-op guest flying the host's custom designs or its own, and the
  lobby's Allow Custom Planes and Outlaw Components.

GitHub issue #24 already holds the lobby's Boot and an optional host password.

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
`NetSeats.cs` holds `MaxPlayers = 16` with `SeatCapacity = 16` (raised from 8 under Decision 5's revision), the colour table (seats 0 to 7 the
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

**Verified.** On the run branch with Wave B whole (B11 to B15 and D32 merged), the full battery
(`RunTests.ps1 -GoldenWorkers 2`) reads build clean, 4883 units passed with 2 skipped, 386 of 386
engine suites passed with engine errors clean in all six shards, and 19 of 19 golden shots
hash-identical. The `net-aircraft-replication` engine suite (6.5 s, the
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
unreliable `FireMessage` on its seat's fire channel; every peer spawns that round locally from the event, so
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

**Verified.** On the run branch with Wave B whole, the full battery reads build clean, 4883 units
passed with 2 skipped, 386 of 386 engine suites passed with engine errors clean, and 19 of 19
golden shots hash-identical; `net-combat-events` and `net-relay-star` pass in the battery's
shards and alone. Two things the battery taught: the three-session star suite holds three worlds
in one process, which overflowed the global shader instance buffer inside a shard of sixty suites
until it was raised fourfold in `project.godot`; and B15's port-mapping unit fact starved twice
under the parallel unit run and now waits on a wall-clock deadline. The relay is asserted on the
loopback star only, not over ENet sockets; `DamageMessage`'s stage and flag words are sent as
zero; death causes 3 and 4 score correctly but nothing raises them yet.

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

## B13 ☑ Host-owned spawn and respawn from `net.zrd` and the rotation, applied by guests

**Landed.** One rule decides where a pilot appears, and it is written in the `GameSession.cs`
entry of `docs/architecture/Session.md`: the OPENING placement is the shared seed's own walk over
the mission's `net.zrd` table and crosses no wire at all, while every later return is GRANTED by
the host. `GameSession.WireNetSpawns` is the wiring, beside B12's `WireNetCombat` and inert with
no seats on a wire. `_versusSpawns` is now built on the host alone, so a guest holds no rotation
to diverge with. A downed seat that is flown here asks through `AskSpawn`: on the host that is a
direct call to `GrantSpawn`, on a guest one reliable `SpawnRequestMessage` (minted at `0x44`,
seat and nothing else) sent once per death under `_spawnAsked`. `GrantSpawn` runs the rotation
against the living field, broadcasts `SpawnMessage(seat, kind, entry)` and applies it locally
through the same `TakeSpawn` every guest runs, which resolves the entry against the spawn list
the session was placed from and calls `FlightController.RespawnAt`. A rematch grants the whole
field its opening entries the same way rather than each machine respawning locally.

`FlightController` gained the two hooks that make one placement path serve both: `RespawnRequest`
withholds the return entirely, so a due crash timer or the respawn key asks instead of respawning
and the aeroplane stays down until the answer lands, and `RespawnAt` takes a pose it was handed
rather than asking `RespawnPlacement`, so a granted spawn cannot run a rotation of its own. Two
bugs fell out of the reading: `VersusRespawn` built its living field over `_rigs`, the pane list,
so a host flying one pane would have rotated around its own aeroplane alone with every guest
invisible to the spacing rule, and it now reads `_seatRigs` through the shared `LivingField`.

With the field at sixteen the rule needs no fan: the free-for-all block holds sixteen entries and
the match admits sixteen pilots, so every seat still opens on a point of its own, and a list
shorter than the field is answered by the rotation relaxing its one-living-seat-per-point rule
rather than by computing a bearing. The original's 45-degree centroid fan stays unimplemented,
which `docs/org/multiplayer-spawn.md` now states for the networked case as well.

**Verified.** On the run branch with Wave B whole, the full battery reads build clean, 4883 units
passed with 2 skipped, 386 of 386 engine suites passed with engine errors clean, and 19 of 19
golden shots hash-identical; `net-spawn-rotation` passes in its shard and alone. The suite was
landed under the 16-seat ceiling and asserts a table point per seat against the constant. Sixteen
seats have been validated by the roster rules, not flown: no suite yet builds a sixteen-seat
match. On the agent's fork: `dotnet build` clean with zero warnings,
4877 units passed with 2 skipped, and `net-spawn-rotation`, `net-combat-events`,
`net-relay-star`, `net-two-session`, `net-seats`, `versus-spawn-rotation` and
`versus-spawn-net-table` each pass on their own.

**Verify.** `net-spawn-rotation` in `CSVM/src/Testing/NetCombatSuites.cs`, three sessions in one
process with the guest-to-guest link cut so an ask reaches the host alone. Before any peer has
stepped it reads every seat's opening entry on all three and finds them equal with no grant
applied anywhere, which is the opening-is-the-seed rule proved rather than asserted. Then three
deaths on two different machines, each stepped only until every peer has taken one more grant:
the granted entry agrees across all three, is a real table entry, is never the one the seat was
downed at, and the aeroplane stands on it on the machine that flies it. The able-to-fail control
has the host hand out an entry its own rotation refuses, the one a living seat holds, and both
guests obey it; a guest rotating for itself could not land there. Weight 9.9 in
`analysis/engine-suite-weights.json`.

**Original approach (kept for reference).** Rotation runs only on the host; a reliable spawn event
carries the seat and the picked entry; guests call the same placement path the owner does. A
guest's own respawn is requested from the host, not taken.

**Evidence (confidence: traced).** `SpawnPoints.LoadNetFreeForAll` reads the table
(`docs/formats/net-spawns.md`); `docs/org/multiplayer-spawn.md` is what the executable does with a
pick; `VersusSpawnRotation` (`CSVM/src/Flight/VersusSpawnRotation.cs`) is the remake's rotation
with one living seat per point and the roomiest-entry respawn; the 16-entry block quantisation is
in this plan's data survey.

**Model recommendation.** <TODO: not settled in the scoping session>

**⚠ Traps.** A guest must not run the rotation locally "to save a round trip"; two rotations
diverge on the first death. A granted spawn must not be applied through plain `Respawn`: on the
host that seat still carries a rotation, and asking it again moves the aeroplane off the point
the rest of the field was just told about. An agreement between peers must be read off the
granted entry and not off three positions, because a seat flown elsewhere stands where its
owner's latest pose puts it and an unreliable pose sent before the death can arrive after the
grant.

## B14 ☑ Match state: clock, limits, end and scoreboard replicated

**Landed.** The host is the only writer of the match, and `GameSession.WireNetMatch` is the wiring,
beside B12's `WireNetCombat` and B13's `WireNetSpawns` and inert with no seats on a wire. On a host
it builds a `Net/MatchStateCadence`; on a guest it calls `VersusMatch.Replicate()` and registers the
handler. A replicated match refuses to write itself: `Advance` moves no clock, no score completes
it, and `ApplyState(killTarget, timeLimit, remainingSeconds, ended)` is the only thing that ends or
re-arms one. `StepVersus` now runs `StepVersusMatch`, which advances the clock on the host and
sends, and does neither on a guest. The scoreboard is not on the wire at all: every machine derives
its standings from the per-seat scores B12 already sends.

**The send rate.** Change-driven plus a clock tick, never per step. The changes are the ending
(sent from `ScoreDeath` and from `StepVersusMatch`) and a rematch, and `MatchStateCadence` ticks
every `TickStepInterval = 60` steps, one second at the fixed step, which is the rate the versus
HUD's whole-second readout can show a difference at. TUNE under `BL-1025`. The first step ticks, so
a guest holds the host's limits inside one step of its build and nothing is sent from the wire-up
itself, which keeps the join the two payloads `net-two-session` counts. A round of an hour costs 60
ticks a minute at 20 bytes.

**The clock and the slew.** Yes, the tick feeds it. `MatchStateMessage` gains `float HostClock`,
the host's session clock at send, widening the original's `0x17` from 16 to 20 bytes (the one
original id the remake widens, recorded in `docs/org/multiplayer-messages.md`). A guest's handler
calls `NetClockSlew.Observe(state.HostClock, ownClock)` before applying the state, so the periodic
tick is what `BL-1018` is judged on. A float costs 2.4e-4 s of step at the hour mark, far under the
slew's own `SettledSeconds`. Live reading from the harness: over a match the guests' slew reads a
target of **6.000 s and `Snaps == 1`** on both, against the 6.000 s the host's clock was wound on
by. ⚠ Both readings are 6 s and not a walk because a suite drives `_PhysicsProcess` alone and
`GameClock.Time` advances in `BeginFrame`, so the only clock that moves is the one the suite calls
`_Process` on (`docs/verification.md` INSTR-92).
`Observe` reads each tick forward by half the round trip `NetClockPing` measures (C21's
**Landed (the round trip).**), so the tick no longer holds the offset one latency short. In the
harness the frozen clocks give a round trip of zero and the 6.000 s reading stands.

**The rematch** (owed by B13) is the host's alone: a guest's R returns without touching anything,
which leaves a guest at a wrap-up board pressing a key that does nothing and is `BL-1026`. The
host's rematch sends the running state BEFORE the zeroed scores, then grants every opening spawn.

**Both limits still ride** even though the original arms exactly one (`FUN_004136e0`): the remake's
Dogfight arms both, which is a remake decision the scoring doc already records, and carrying both
rows leaves an exclusive lobby nothing to change on the wire.

**Verified.** On the run branch with Wave B whole, the full battery reads build clean, 4883 units
passed with 2 skipped, 386 of 386 engine suites passed with engine errors clean, and 19 of 19
golden shots hash-identical; `net-match-state` passes in its shard and alone. The 1 Hz tick
(`BL-1025`) and the slew window (`BL-1018`, first live reading: target 6.000 s, Snaps 1) are
still to be judged on a real link with the HUD clock in view. On the agent's fork: `dotnet build`
clean with zero warnings,
4883 units passed with 2 skipped, `RunTests -Quick` green, and `net-match-state`,
`net-two-session`, `net-combat-events`, `net-relay-star`, `net-spawn-rotation` and
`net-aircraft-replication` each pass on their own. No golden was re-pinned: nothing that draws
changed.

**Verify.** `net-match-state` in `CSVM/src/Testing/NetCombatSuites.cs`, three sessions in one
process with the guest-to-guest link cut and the two guests launched on limits of their own
(9 kills / 9 minutes against the host's 2 / 1), so a limit a guest shows is one that crossed the
wire. Seven phases: both guests hold the host's two limits and only the host holds a match it may
write; the guests stepped without the host move no clock at all, against the control of the host
moving its own over the same steps, and the host's clock then reaches both, at most one tick
behind; the slew reading above, against the control of the same tick with both clocks at zero
leaving the offset at zero; a guest that counts the target into its own match ends nothing and
raises no board, against the control of the same calls into a match nobody replicates ending it;
two real deaths take seat 1 to the kill limit and all three machines report the match over, the
board holding, the reason `ScoreTarget` and one identical scoreboard; the guest's rematch key
changes nothing anywhere and the host's zeroes every score on every machine; and the host's clock
wound to half a second short of its limit then stepped over it ends the round again, with the
reason `TimeLimit` this time. Weight 10.0 in `analysis/engine-suite-weights.json`. Off-engine:
`CSVM.Tests/MatchStateCadenceTests.cs` and four cases in `VersusMatchTests`.

**Original approach (kept for reference).** The host is the only writer of match state; a reliable
match-state message carries the clock, the limits and the ending; `StepVersus` on a guest applies
rather than advances. The scoreboard is derived from replicated scores, not sent.

**Evidence (confidence: traced).** `docs/org/multiplayer-scoring.md` "How a match ends" and "The two
limits are exclusive"; `VersusBoard` and `VersusHud` are the local presentation; the ending hold is
`StepEndingHold` (`SessionSimulation.cs:13`).

**⚠ Traps.** A guest must never advance the clock or arm a limit itself, and a guest that reaches
the end locally must not hold early: both are one rule, `VersusMatch.Replicated`, because a guest
that completed its own match would raise its board on a different frame from everybody else and
freeze a world the host is still flying. ⚠ The ending must be sent AFTER the scores that settled
the round and never from `MatchCompleted`, which fires before them: `ApplyScore` is a no-op on a
completed match, so a guest told the end first drops the last kill and its board names a different
winner. ⚠ Nothing may be sent from `WireNetMatch` itself; the join is counted as exactly two
payloads. ⚠ A guest's rematch must not restart anything locally, or it flies a round nobody else
is in.

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
caller's channel through, with `ChannelCount` (the events channel plus one per seat) asked for at
connect so every seat's channel has a negotiated ENet channel to ride. `Step` makes one poll, from which every join, departure and
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

**Verified.** On the run branch with Wave B whole, the full battery reads build clean, 4883 units
passed with 2 skipped, 386 of 386 engine suites passed with engine errors clean, and 19 of 19
golden shots hash-identical; `enet-transport`, `net-enet-join` and `menu-net-door` pass in their
shards and alone. Two orchestrator fixes on the merged tree: the join check of `net-enet-join`
predated B11's aircraft stream and now reads the two reliable join payloads as a floor plus a
growth check after the lockstep; and `NetPlayFeatureTests`' port-mapping fact waited a fixed 500
steps for a thread-pool task, starved twice under the parallel unit run, and now waits on a 20 s
wall-clock deadline. The Decision 5 revision found the socket negotiating 8 channels while seat 7
sent on channel 8; `ChannelCount` is now one per seat plus the events channel and the suite
proves the highest one carries. The evidence stops at one process: two real ENet sockets on
127.0.0.1, never two machines, a router or a NAT.

**Owed.** A host and a guest agree on the map, the match rules and the aircraft by hand: nothing is
exchanged before the session is built, so each end picks its own and a disagreement is silent, and
a host's roster gives every remote seat the local pilot's airframe (`BL-1022`, F52). The Original
presentation has no board over the shared door (`BL-1021`, F51). `BL-951`'s local join board is
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

## C21 ☑ The host-owned mission director: objective graph transitions, cutscene codes and wingman spawns as events

**Landed.** The host's objectives graph runs its own rules and a guest's follows it event by event,
over the existing `0x42` `DirectorTransitionMessage` (no new message id; the next free id is still
`0x45`).
`CSVM/src/Session/ObjectiveGraph.cs` gains the follow mode, modelled on `VersusMatch.Replicate`:
`Replicate()` sets `Replicated`, after which `Step` only advances `Elapsed` and counts the
countdown's display down (pinned at zero), and the graph changes only through `ApplyTransition`,
`ApplySettled`, `ApplyTimerExpired`, `ApplyEnding` and `ApplyEnded`, each running the same
bookkeeping the host's transition ran (wake actions, completion actions and chaining effects, nap
length read off the same script, sleep animation, display rows). `NotifyPlayerLost`,
`NotifyDockingComplete`, `NotifyDangerZoneCompleted` and `Wake` are refused on a replicated graph.
Two host-side additions make the event stream complete: `HIDE_OBJ` now raises a `Hidden`
transition (it retired an objective silently, so a guest would later complete it by its own
rule), and the ending sounds moved into `End`, which raises the new `EndingDecided` with a
`MissionEnding(Outcome, ObjectivesSound)`.
`CSVM/src/Net/NetMessages.cs` adds `NetDirectorEvent`, the eleven codes and their id layouts.
`CSVM/src/Session/NetDirectorLink.cs` is new: `Publish(net, graph)` subscribes to the host's
`Transitioned`, `Completed` (sent as `Settled`, after the chain), `TimerExpired`, `EndingDecided`
and `MissionEnded` and broadcasts each on `NetChannels.Events`; `Follow(net, graph)` replicates the
graph and registers the one handler that replays arrivals.
`CSVM/src/Session/CampaignDirector.cs`: a replicated graph's end builds the `Result` and the
leaving hold but writes no profile, persist log, photograph or award.
`CSVM/src/Session/GameSession.cs`: `WireNetDirector` stamps the host's events with its session
clock and hands a guest's arrivals to a `NetDirectorCatchUp` over the world runtime and the shared
clock (`NetClockSlew.HostTime`). It runs after the callback host is bound and,
only when a net session, net seats and a campaign graph all exist, publishes on the host and
follows on a guest. It sends nothing at join, so a `--vs` guest still leaves `0x42` unclaimed.
Tests: `CSVM.Tests/NetDirectorLinkTests.cs` (5 cases, Quick) and the engine suite
`net-director-follow` in `CSVM/src/Testing/NetDirectorSuites.cs` (weight 12.0).
Docs: `docs/org/multiplayer-messages.md` "The mission director" holds the code and id table and
the replay and derive mapping; the Session and Net architecture entries and one index bullet.

**Landed (the shared-clock catch-up).** Every director event carries the host's clock, and a
guest that learns of one late starts what it started as far along as the host's copy is.
`DirectorTransitionMessage` grows a `float HostClock` after the id and is 16 bytes (a 12-byte
payload is refused). `NetDirectorLink.Publish(net, graph, hostClock)` stamps every event it sends;
`Follow(net, graph, catchUp)` hands arrivals to the new `CSVM/src/Session/NetDirectorCatchUp.cs`,
which reads the lateness as the guest's shared clock minus the stamp (never negative), applies the
event with it, and advances what it started:
- `ObjectiveGraph.ApplyTransition(..., late)`: a replayed wake starts the private timer at the
  lateness and a `RESET_TIMER` or `ADJUST_TIMER SET` countdown that far down; a replayed nap starts
  that far into its length. A replicated `Step` now also runs the private timers and naps (each
  pinned at zero), and `TimerOf`/`NapRemainingOf` read them.
- `AnimRuntime.CollectLateStarts`/`CatchUp(seconds)`: the instances the event started, and their
  motions, are stepped on their own at the authored frame, so their timed events and callback codes
  fire in order; `ClockOf(name)` reads a playback position.
- `WorldSounds.LateBy`: a one-shot fired during the catch-up starts that far into its clip, and one
  already over is skipped (`SkippedLate`). `MissionRadio.LateBy`: a late call's start delay is
  shortened by the lateness, and past it the call joins at the line and offset the host is at
  (`CSVM/src/Mech3/LateStart.cs`, engine-free), skipping lines already said.
Not advanced, and no fixed lead for them: particle emitters and light animations started by a
late cutscene begin at their own start (they have no position to seek, and a lead would delay the
host's own playback for every event), and a music cue starts on arrival (the stream is chosen,
not timed, and a 100 ms late start of a looping bed is not audible as desync). So no TUNE lead
constant and no backlog entry.
Tests: `NetMessagesTests` (the 16-byte round trip and the old width refused) and
`NetDirectorLinkTests` (lateness arithmetic, `LateStart.Into`, and a late wake with its control).
`net-director-follow` now runs three built worlds over a 100 ms link: the host, a guest with the
catch-up, and a control guest with it off (weight 16.0).

**The mapping.** Replayed by the guest, presentation: `WAKE_ANIM`/`SLEEP_ANIM`, every sound group
(wake, completed, class complete, objectives and mission won or lost), `STOP_QUEUED_SOUNDS`, target
lists, help labels, the countdown's reset, adjust and end actions, and display rows. Derived by the
guest: every presentation cutscene code (20, 2, 11, 1, 10, 913/914, 666/667, 951, 86), which its own
animation runtime raises from the definitions its replayed `WAKE_ANIM` or the shared start list
started; sending them as well would apply each twice. Refused on the guest: code 13 (the host's
code 13 decides the ending, and codes 10 and 11 carry it), the condition hooks and a direct wake.
Host-owned world, **C22 marker**: `WAKEUP_ENEMIES`, `WAKEUP_TURRETS`, `WAKEUP_ZEP_TURRETS`,
`WAKEUP_GENERATOR`, `WARP_VEHICLE` (its random draw diverges), `SET_AI_TEAM`, `SET_AI_NET`,
`SET_AI_ATTACK_RADIUS`, `COMPLETED_ZEPCANNONS`, `COMPLETED_STOPPOINT` and `START_TAXI` are replayed
through the guest's own world seam as an interim local simulation, and callback codes 801 to 803
(Black Hat launch), 968 (wingman removed) and 800 (generator credit) still run locally on each
end. **Wingman spawns are C22's:** `wingman_1` is a roster block spawned from the profile at build,
not a director event, so nothing here sends it. **C23 marker**: 965 to 967 (the airframe swap)
belong to the episode's owner, and definitions started by a player's position (landing approach
rows, `PlayerRange` conditions, the ladder switch) are not graph events. **C24 marker**: the
guest's own profile record, and a late joiner, who has missed every earlier event.

**Verified.** The complete battery on the merged tree (the catch-up and its session hook over C22,
C21, D31 and Wave B): build clean, 4910 units passed with 2 skipped, 389 of 389 engine suites
passed with engine errors clean in all six shards, 19 of 19 golden shots hash-identical, so the
catch-up's `AnimRuntime`, `MotionSet`, `WorldSounds` and `MissionRadio` changes moved no pinned
picture. `net-director-follow` runs three built C5/M02 worlds over a 100 ms lossy loopback: the
guest decides nothing alone, refuses the docking code, replays the host's transitions in order,
derives the ending cutscene's codes from its own playback, ends Won with the host and records no
attempt on its own profile. At the end of the frame the late ending wake arrived in, its cutscene
and timer stand 0 ms from the host's, and the control guest with the catch-up off trails by 83.3 ms
on both. That run supplied an exact shared clock; the round trip below replaces it with the live
slew.

**Landed (the round trip).** `CSVM/src/Net/NetClockPing.cs` is the original's `0x23` ping in the
remake's terms, minted at `0x49` (`ClockPingMessage`, 12 bytes, two session-clock stamps,
unreliable): a guest asks the host's clock on its first step, again 600 steps (the original's ten
seconds) after an answer and 60 steps after an unanswered question (TUNE), and the host answers at
once on the events channel. `NetClockSlew.ObserveRoundTrip` keeps the newest round trip, reads
the answer forward by half of it, and applies the first one at once as the end of the opening
alignment, since an event replayed while a latency's error was still being walked off keeps that
error in its timer. Every later one-way reading (`Observe`, a match's `0x17` tick) is read forward
by the same half, so a tick no longer pulls the offset back by the latency. `GameSession.WireNetClock`
wires both ends for every session kind after the join, so a campaign session has a periodic clock
reading; nothing is sent from the join itself. `NetClockPingTests` is the able-to-fail check: over
a 105 ms loopback the guest's host time ends 9.1 ms off with the ping and 116.7 ms off without it
(the latency rounded up to whole steps), and 17.4 ms off under jitter and 25 per cent loss.
`net-director-follow` now opens each guest's slew a latency short, as a one-way handshake does,
and feeds it from the ping over the lossy link: the round trip measures 200 ms, host time stands
16.7 ms off at the sample, and the cutscene and timer gaps are 16.7 ms, inside the one-step bar.
`net-relay-star` leaves the host's answered questions out of its forwarded-once count, and
`net-soak`'s drop truth leaves out the events channel's losses (`LoopbackTransport.LostOn`),
where no sequence stream rides.

**Verified.** <pending orchestrator run>

**Owed.**
- A late joiner (C24) would read every missed event as seconds or minutes late; the catch-up is
  not capped for that case.
- No launch path yet runs a campaign mission with a net seat, so `WireNetDirector` is exercised
  only by the suites, which drive two directors directly. The co-op campaign flow is C24's, and
  the door into it C25's.
- The world directives above still replay locally on a guest where C22 left markers; those are
  Wave E.

**Model recommendation.** Opus. The work is the graph's bookkeeping order (a completion's chain,
nap lengths from a source, the silent `HIDE_OBJ`, where the ending sounds play), where a missed
side effect shows only as a guest whose state drifts later.

**Verify.** `net-director-follow` in `CSVM/src/Testing/NetDirectorSuites.cs`, over C5/M02: two
built worlds, two `CampaignDirector`s and two `CutsceneController`s joined by a 30 ms, 25 per
cent lossy loopback. C5/M02 was chosen because it opens with a timer-driven completion carrying a
world action and ends on an objective-started cutscene whose callee raises codes 11, 2 and 13, so
one short run covers a transition, a world directive and the cutscene codes. The guest alone for
3 s raises no transition and refuses the docking code, against the control of the host running
three over the same time by its own rules; then the host wakes the ending, and the guest replays
the host's transitions in order and in state per objective and row, its own playback raises the
host's code sequence (13 included) with no code sent, only the host's graph accepts code 13, both
end Won with the same ending sounds, both directors hold the result, and only the host's profile
records the attempt. Off-engine: `CSVM.Tests/NetDirectorLinkTests.cs`, including C1/M04's real
chain replayed in state and in world actions with zero condition queries on the guest.

**Original approach (kept for reference).**

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

**⚠ Traps.** The scripted `player` token, roster leaders and anchored net trailers are a separate
P1 identity (`CampaignHumanField`'s entry); the host is P1 and a guest never is.

## C22 ☑ Host-owned AI and world: aircraft, zeppelins, turrets, generators, vehicles and destructibles as spawn, state and death events

**Landed.** The mapping plus AI aircraft and destructible deaths; the other world phases are
sharpened markers below.
- `CSVM/src/Net/NetMessages.cs`: ids `0x45` AI state, `0x46` AI fire, `0x47` AI hit, `0x48` world
  event, their reliability classes, and `NetWorldEvent` (1 AI downed, 2 AI hull, 3 destructible
  health).
- `CSVM/src/Net/NetWorldMessages.cs` (new): the four structs. `AiStateMessage.AsAircraftState`
  feeds the existing `RemotePoseBuffer`.
- `CSVM/src/Session/NetWorldLink.cs` (new): the host broadcasts each AI's pose at the seat cadence,
  its fire, hull and death, and every destructible stage change and kill; a guest flies each AI
  from samples (`RemotePoses`), spawns its rounds from fire events, claims its own seat's hits on an
  AI with `0x47`, and applies pool health. AIs are named by admission ordinal in the roster's
  append-only list.
- `CSVM/src/Session/GameSession.cs`: `WireNetWorld` after `WireNetDirector`; `NetWorld` for the
  suites; the AI capture phase admits new AI and the AI step phase sends.
- `CSVM/src/Mech3/AnimRuntime.cs`: `DamageAt`'s spend moved into `SpendHealth`, which raises
  `DestructibleDamaged` on a stage change or kill; `DamageReplicated` makes a guest's `DamageAt`
  report a hit and spend nothing; `ApplyReplicatedHealth` is the guest's only spend.
- `CSVM/src/Flight/FlightController.cs`: a remote-owned rig runs no AI gunner and takes no collision
  hit (its owner's sweep resolves its half). Without the second, a guest's ram destroyed its copy
  of a host AI before the first sample arrived.
- `CSVM/src/Testing/NetWorldSuites.cs` (new): suite `net-ai-world`; weight in
  `analysis/engine-suite-weights.json`.
- `CSVM.Tests/NetMessagesTests.cs`: round trips and classes for the four messages.
- Docs: `docs/org/multiplayer-messages.md` "The host-owned world" (message table, decide-once
  rules, the per-phase table); the Net, Session and Mech3 architecture entries and index bullets.

**The mapping.** Every `ISessionSimulationRuntime` phase, as a guest runs it. State-replicated:
captured AI aircraft (pose, fire, hull, death) and human aircraft (B11/B12). Events: destructible
stage changes and deaths, for every pool (buildings, turrets, zeppelin parts and cannons, generator
and vehicle hulls). Local and not authoritative: projectiles (spawned from fire events, spending
nothing on a guest), the ending hold, landing approaches, radio, smoke screens, beeper tags and
incoming fire. Replayed locally as an interim, **markers**: zeppelin paths, turret aim and fire
(cosmetic on a guest), generator cycles and surface-vehicle patrols. Derived: AI voice (silent for a
replicated AI, which runs no mode machine). Not run: instant action. The campaign and versus
phases are C21's and B14's.

**Markers (not landed).**
- Host-owned AI spawns: closed by E41 (generator launches as `0x4C` at the host's ordinal; Black
  Hat launches and `WAKEUP_ENEMIES` as presence events on the ordinals they already hold).
- Zeppelin paths: the original sends the zeppelin's path position as `0x1e` every 0.5 s; the remake
  replays the path locally from the shared seed and clock.
- Surface-vehicle patrols and `WARP_VEHICLE` (its random draw diverges): replayed locally.
- AI voice: a replicated AI's mode-driven call-outs are silent on a guest.
- Destructible chip damage: closed by E44 (health between stages as code 3 samples, coalesced per
  pool on the seat cadence).

**Verified.** The complete battery on the merged tree (C22 over C21, D31 and Wave B): build clean,
4906 units passed with 2 skipped, 389 of 389 engine suites passed with engine errors clean in all
six shards, 19 of 19 golden shots hash-identical, so the moved `AnimRuntime` spend and the
remote-owned `FlightController` gates changed no pinned picture. `net-ai-world` tracks a guest's
copy of a host AI at 0.27 m mean against its host path (60.0 m against the other AI, so the
comparison can fail) and lands 6 host AI rounds on the guest. The limit: the loopback only, four
phases left as markers, AI voice silent on a guest, and `AiState` bandwidth unmeasured.

**Owed.**
- Whether a silent AI voice on a guest is acceptable until the voice is sent or derived from state.
- The markers above are Wave E (E41 to E44); the AI voice marker is GitHub issue #22.
- A late joiner has missed every earlier world event (C24's).
- AI state costs 48 bytes at 20 Hz per AI; a busy campaign mission may want a lower rate for distant
  AI, which D31's soaks can measure.

**Model recommendation.** Opus. The work is deciding, per hit and per spend, which end decides it
once; a wrong answer shows only as a double death or a guest whose copy dies early.

**Verify.** `net-ai-world` in `CSVM/src/Testing/NetWorldSuites.cs`: a versus launch on MP1 with two
AI fighters, two sessions over a loopback of 30 ms latency, 10 ms jitter and 25 per cent loss. Admission (both ends
admit the same AI by ordinal, the guest's as remote-owned); tracking (each guest AI within 3 m of
its own host AI's path, against a control over 5x that on the other AI's path and a placement gap
over 50 m); fire (host AI rounds appear on the guest); hits in both directions with controls; deaths
(a lethal ram on the guest's copy is a no-op, the host's forced crash reaches the guest); and
destructibles (a guest `DamageAt` spends nothing, a host kill propagates, the spared pool stands).
Off-engine: `CSVM.Tests/NetMessagesTests.cs`.

**Original approach (kept for reference).**

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

## C23 ☑ Guests as the human field: `CampaignHumanField` and the objective rules see remote humans, the scripted P1 stays the host

**Landed.** The host's human field is every seat, and a guest's death reaches it; the rules are
unchanged. The positional triggers and the airframe swap are sharpened markers below.
- `CSVM/src/Session/GameSession.cs`: `HumanAircraft()` (the director's `Humans` input) reads
  `_seatRigs` instead of the panes, so a guest flown elsewhere is a human at its interpolated pose
  and its wreck is the one its own `0x12` plays; `HumanField` exposes that list to the suites.
  `WireNetCombat` sends a seat's death report from `Downed` in every non-versus mission (a match
  keeps its own handler, which also keeps the last killer), since the campaign had none.
  `BeginCampaignSpectate` and `LockCandidateAircraft` read the seats, so a downed pane can follow a
  guest and a lock can hold one.
- `CSVM/src/Testing/NetHumanFieldSuites.cs` (new): suite `net-human-field`; weight in
  `analysis/engine-suite-weights.json`.
- `CSVM.Tests/CampaignHumanFieldTests.cs`: a remote human fed through a `RemotePoseBuffer`.
- Docs: the `GameSession.cs` and `CampaignHumanField.cs` entries in `docs/architecture/Session.md`.
- No new message, and `CampaignDirector.cs`, `ObjectiveGraph.cs` and `NetDirectorLink.cs` are
  untouched: `SnapshotHumans` already read whatever list the session hands it.

**The wiring contract.** The host decides every objective off its own field, which is its panes plus
one pane-less rig per guest. A guest's pose is the host's `RemotePoseBuffer` sample (one buffer
delay, 0.1 s, plus the link behind the owner); its `Crashed` flag turns true only when the guest's
own death report arrives and `TakeRemoteDeath` plays the wreck on the host; the co-op loss
(`StepPlayerLost`) then waits for that wreck to land like any other. The scripted player
(`PlayerAircraft`, P1, authored `player` tokens, roster leaders) is the host's seat 0.

**Markers (not landed), C21's sharpened.**
- Positional starts are local to each machine: landing approach rows and the ladder switch are
  bound to `_rigs`, and `PlayerPositionsSnapshot` (the anim runtime's `If PlayerRange` gate) reads
  the panes. A guest who flies a landing row or a capture volume starts that definition, and with it
  the 965 to 967 airframe swap (`CutsceneController.EpisodeOwner`, `GameSession.SwapPlayerAirframe`,
  `FlightRoster.RunSwap`), only on its own machine: the host's copy keeps the old airframe and no
  captured group, so a `DEDG` or `LiveInGroup` read on the host never counts that guest. The fix is a
  host-decided start: the host evaluates every seat against the approach volumes and sends the start
  and the owning seat (the next free id, `0x49`), and the swap replicates as the owner's new airframe
  and group. Binding the approaches to `_seatRigs` alone is wrong, because a swap would rebuild the
  host's copy of a guest and drop its `RemotePoses` feed.
- A 967 swap builds a new controller that `WireNetCombat` never wired, so the swapped pilot's fire,
  damage and death reports stop crossing.
- On a guest, `PlayerAircraft` and an unclaimed episode's `ScriptedPlayer` are the guest's own pane,
  not the host's P1, so a locally run definition poses the wrong aeroplane there.
- `If PlayerRange` washes are cosmetic and a per-machine read of the viewer's own pane is right for
  them; a gate that raises a callback code would need the host's field instead.

**Verified.** The complete battery on the merged tree (C23 over the catch-up, C22, C21, D31 and
Wave B): build clean, 4911 units passed with 2 skipped, 390 of 390 engine suites passed with engine
errors clean in all six shards, 19 of 19 golden shots hash-identical. `net-human-field` puts the
host's copy of a guest 0.00 m from an arrival point with P1 4020 m out: OBJECTIVE5 completes on the
whole-field director and not on the panes-only control, and the guest's death report crosses in
50 ms and reads Lost 0.38 s later. The suite fails with the non-match death report removed or the
field read over the panes. The limit: the loopback only, and the positional starts and the airframe
swap stay local to each machine (C26).

**Owed.**
- The markers above are C26.
- `NetSeats.Validate` does not require the host at seat 0; every seat-0-is-P1 read assumes it.

**Original approach (kept for reference).**

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

**Model recommendation.** Opus. The rules needed no change; the work is finding which lists are
panes and which are seats, and which report never fired outside a match.

**Verify.** `net-human-field` in `CSVM/src/Testing/NetHumanFieldSuites.cs`: a host and a guest
session in free flight on C3/MP1 over a loopback of 30 ms latency, 10 ms jitter and 25 per cent
loss, with two directors on the host running C3/M01's shipped script, one over the host's field and
one over its panes alone as the control. The field holds both seats in order with the guest's
remote-owned. OBJECTIVE5's dormant `player` TRAVELERS (1100 m, re-read off the data) completes
when the guest pins itself at the point on its own machine while P1 waits 4 km out, and the control
completes nothing. The host's forced crash loses the control's mission while the field's flies on;
the guest's forced crash crosses as `0x12`, the host's copy becomes a wreck in the field, and the
field's mission ends Lost. Able to fail: with the non-match death report removed the copy never
crashes in 30 s, and with the field read over the panes five checks fail. Off-engine:
`CampaignHumanFieldTests` feeds an owner flying into a 100 m radius through a `RemotePoseBuffer`
and asserts the host's read arrives one buffer delay (within a sample and a step) after the owner.

**⚠ Traps.**
- The host must hold seat 0: `PlayerAircraft` is `_rigs[0]`, and on a guest that is the guest.
- A remote human arrives in the field one buffer delay plus the link after its owner arrived, so a
  harness check on arrival must step past that, not read the step the owner crossed.
- A match reports a seat's death from its own `Downed` handler; wiring the non-match report there
  too would send every versus death twice.
- The co-op loss waits for the guest's wreck to land on the host, which the host falls from its
  last buffered pose. The guest's replicated graph takes Lost from the host, never from its own
  wreck.

## C24 ☐ The co-op session flow: guests follow the host's cabin and briefing, pick from the host's hangar, Ready before launch, and share the debrief

**Goal.** A guest who joined a co-op host through F51's doors follows the host from the cabin into
the same briefing, picks a plane and ammo from the host's hangar, presses Ready, flies the mission
as the human field, and sees the debrief for the host's outcome. The host's save records the
campaign; a guest's save is never touched. Built in the Original presentation (Decision 10).

**What exists to build on.**
- C25: `SessionAdvertMessage` (`0x4A`) and `NetSessionKind`, `NetLobby` (the carrier's first
  listener, which holds every non-advert payload until a session binds it), `NetSeats.Field` (the
  host's roster with one seat per guest), `NetPlayFeature`'s co-op door (`OpenCoopHost`, `Offer`,
  `IsCoopHost`, `IsCoopGuest`, `Advert`, `HostStarted`), `CoopDoorText`'s band and status lines, the
  Built-in waiting board, and C25's **wiring contract** (below in its section): the host takes
  `_net.BuildLaunch()` in the mission launch and carries it on `CampaignMissionExit`, and a guest's
  launch trigger is `HostStarted`.
- C21 to C23: the host-owned director, world and human field; C26: host-decided landing rows, the
  ladder switch and the 965 to 967 swap for a guest, whose Owed list names this item as the first
  whole `GameSession` co-op campaign path. E41 to E44: the rest of the host-owned world.
- F51: the cabin's Host Co-op button, the guest chips and the Connection page a guest joins from
  and returns to.
- The Original campaign screens: `OriginalCampaignScreen.cs` (`ActivateCabin`, `ActivateBriefing`,
  `EnterBriefing`), `CampaignPlaneSelectionPage.cs`, `CampaignAmmoPage.cs`, and the debrief return
  (`OriginalPresentation`'s `DebriefReturn`, which opens the scrapbook through `ShowScrapbook`).

**Evidence (confidence: lead-only).** Mission end records the attempt, folds the persist log into
the profile and holds before the cabin behind `LeavingFade` (`CampaignDirector`'s entry); the local
splitscreen campaign co-op already launches guests as the human field through `GameSession`'s
grid selection (`docs/architecture/Session.md:78`). The original's debrief is the scrapbook
(`docs/org/debrief.md`): mission end opens the book, whose Replay Mission restarts in place and
whose return goes to the cabin.

**Approach.** The flow, as confirmed:
- **Following.** A joined guest lands in the host's cabin with every button greyed, and follows
  the host's navigation into the same briefing and flight check. Plane construction, the memento
  and previous missions stay host-only. The host sends its screen and mission on each change (a
  reliable message, the next free id at the time).
- **Loadout.** A guest picks plane and ammo from the host's hangar (the host's unlocked stock
  planes) with the host's ammo choices; the host sends the pick list and a guest answers with an
  index into it. No plane definition crosses the network (see "Stock planes only").
- **Ready and Launch.** A guest presses Ready and can take it back until launch. The host's Launch
  is greyed until every connected guest is Ready; the host has no override. The host backing out of
  the briefing, or changing mission, clears every Ready and the guests follow. A guest who
  disconnects drops out of the count. The player chips show Ready, as the original lobby's list
  does. The launch then follows C25's wiring contract.
- **Late joiners.** A guest who joins while the host is in a mission (the advert's `In mission`)
  waits in the cabin until the next briefing. No guest ever joins a mission in flight, which also
  settles E41's owed late joiner.
- **Mission end.** The host decides the outcome. Every peer sees the debrief for it with its own
  kills and damage; the shared result (objectives, mission cash) is the host's. Continue, Retry and
  Quit to cabin are the host's buttons; guests see them greyed and follow. A retry goes back through
  plane and ammo selection and Ready. A guest may disconnect at the debrief, back to the Connection
  screen.
- **Disconnects.** A dropped guest's plane leaves the mission with a `<name> left` message and the
  mission goes on; that guest's kills so far count, and the guest may rejoin as a late joiner. A
  dropped host ends the session for every guest with "Host left the game", back to the Connection
  screen. No host migration.
- **Saves.** Campaign progress lives only in the host's save. A guest builds its session with no
  profile, and every profile writer is a no-op on it.

The Built-in presentation keeps C25's boards; the rules above live in presentation-neutral features
(`CampaignFeature`, `NetPlayFeature`) so the Built-in boards can follow them later, but this item
draws only the Original screens.

**Stock planes only.** A guest simulates its own aeroplane (Decision 1), so its machine must build
the picked airframe, and every peer must build each guest's copy. A stock plane is an index both
ends resolve alike; a custom design exists only in the host's save. A guest's list is therefore the
host's unlocked stock planes. Flying a custom design as a guest, the host's or the guest's own, is a
refinement filed at the milestone's landing, since it needs a plane build to cross the network.

**Model recommendation.** Opus. It crosses the Original campaign screens, the launcher, the
session's profile-free guest path and a new message family, and every screen is a look judgement.

**Verify.**
- A new menu engine suite (`menu-original-coop-flow`) over a loopback pair with a stub router: the
  guest's cabin buttons are greyed and a press moves nothing; the host's Next Mission takes the
  guest into the same briefing; the guest picks the host's second plane and a non-default ammo; the
  host's Launch is refused while one guest is not Ready (the able-to-fail control) and allowed once
  it is; the host backing out clears the Ready; the launch builds both ends' `CampaignMissionExit`,
  the guest's with no profile.
- A two-session engine suite (`net-coop-mission`) over a lossy loopback: a guest `GameSession`
  flies a campaign mission with no profile; the host's outcome reaches the guest's debrief with the
  host's objectives and cash and the guest's own kills; the host's Retry returns both to selection;
  a guest dropped mid-mission leaves the host's field with `<name> left` while the mission steps
  on; a dropped host sends the guest to the Connection screen with "Host left the game".
- The guest's profile directory is byte-identical before and after the whole run (a write to it
  fails the check).
- Montages of the guest's cabin, the briefing with Ready chips and both debriefs for the user.
- At the controls: two machines, one mission end to end, with a friend.

**⚠ Traps.**
- A guest has no profile; every path that writes one (`CampaignProfileStore`,
  `CampaignSnapshot`'s photographs, the memento) must be a no-op on a guest, not a crash.
- The scrapbook debrief reads the profile's record of the mission; a guest's debrief draws from
  the host's result and its own counters instead.
- Continue, Retry and Quit to cabin do not map one to one onto the original's scrapbook (its
  forward path, Replay Mission, the return to the cabin); name the mapping in the landing commit.
- A guest's own `CampaignFeature` must not advance the mission; the host's navigation is the only
  source.

## C25 ☑ The co-op door: the campaign flow opens to the network, guests join from the Network board and wait for the host's launch

**Landed.**
- `CSVM/src/Net/NetMessages.cs`: `SessionAdvertMessage` at `0x4A` (24 bytes, reliable) and
  `NetSessionKind` (Dogfight, campaign co-op). It carries the kind, the campaign mission sequence
  (chapter and mission within it are derived), the player count and the host's name.
- `CSVM/src/Net/NetLobby.cs` (new): the carrier's first listener. The session reply named in the
  approach cannot carry the kind, because no session exists until the launch and a carrier binds
  once. The lobby binds at open, sends the host's advert on connect and on change, keeps a guest's
  latest advert, and holds every other payload until the launched session binds it.
- `CSVM/src/Net/NetSeats.cs`: `Field`, a host's roster from its local planes plus one `guest N`
  seat per peer on the wire.
- `CSVM/src/UI/Menu/NetPlayFeature.cs`: every open wraps its carrier in a lobby; `OpenCoopHost`,
  `Offer`, `IsCoopHost`, `IsCoopGuest`, `Advert`, `Advertising`; `Step` re-advertises;
  `HostStarted` is now the lobby's held count.
- `CSVM/src/UI/Menu/CoopDoorText.cs` (new): the band, session name and status lines.
  `CSVM/src/UI/Menu/NetDoorAid.cs` (new): loopback doors for the screenshot aids.
- `CSVM/src/UI/LaunchMenu.cs`: player 1's L / Y on a campaign board opens and closes the door
  (refused while the Multiplayer board holds it open); the cabin footer names the press; the band
  sits under the chip strip, and remote guests count there as `Pn net` chips and against the local
  join ceiling without a pane. Cancelling the campaign and flying a mission close the door. A
  co-op guest's Continue leads to the Network screen's waiting mode, whose one row leaves. Aids
  `campaign-coop[:guests]`, `network-coopjoin`, `network-coopwait`.
- `CSVM/src/Testing/MenuCoopDoorSuites.cs` (new): suite `menu-coop-door`, weighted in
  `analysis/engine-suite-weights.json`. `MenuSuiteHost` takes a suite's door. Units: `NetLobbyTests`, `CoopDoorTextTests`, and extensions to `NetMessagesTests`
  and `NetPlayFeatureTests`.
- Docs: `Net.md`, `UI.md`, `Testing.md`, the index, `org/multiplayer-messages.md` (the lobby
  table), `org/menu-inventory.md` (the three aids).

**Verified.** The complete battery on the merged tree (C25 over C23, the catch-up, C22, C21, D31
and Wave B): build clean, 4924 units passed with 2 skipped, 391 of 391 engine suites passed with
engine errors clean in all six shards, 19 of 19 golden shots hash-identical. `menu-coop-door` and
`menu-net-door` pass; the door's able-to-fail control was checked by a reverted mutation on the
agent's fork. The limit: the loopback only, the router mapping unexercised against a real router,
and the launch past the waiting board is C24's.

**Owed.**
- The user's look judgement of `.scratch/m6/C25/montage-coop-door.png` (cabin shut, cabin open with
  two guests, the guest's join board, the waiting board) and `cabin-network-solo.png`.
- C24's wiring, below. `FlyCampaignMission` closes the door today, since no mission carries a wire.
- No `GameSession` hook was needed.
- The Original presentation's version of these screens (Decision 10): F51 builds the cabin's Host
  Co-op button with its band and guest chips and the Connection page a guest joins from, and C24
  replaces the waiting board with the guest following the host's cabin. The co-op cap is four
  humans, while this door admits up to `NetSeats.MaxPlayers` less the local seats; F51 lowers it.

**The wiring contract (for C24).**
- The host: in `FlyCampaignMission`, when `_net.IsCoopHost`, take `_net.BuildLaunch()` instead of
  `CloseCoopDoor()` and carry it on `CampaignMissionExit` (a new `MenuNetLaunch? Net`). The roster
  is `NetSeats.Field(transport.LocalPeer, planes, transport.Peers, planes[0])`; the session binds
  the `NetLobby` the launch carries, and `NetSession.Host` answers every guest at once.
- The guest: the waiting board polls `NetPlayFeature.HostStarted`, which turns true when the host's
  join answer lands in the lobby. That is the launch trigger; build the guest's session from
  `BuildLaunch()` there. The held answer and roster replay into it when it binds.
- Adverts never reach a session. The launcher closes the door (`Close`, which unmaps) at the
  flight's end, as the Dogfight's net launch does.
- The advert's mission is `CampaignFlow.MissionSeq` when a mission is picked, else the profile's
  next mission; the guest's mission name is langui `3450 + seq`.

**Verify.** Units: `NetLobbyTests` (5), `CoopDoorTextTests` (3), `NetMessagesTests` and
`NetPlayFeatureTests` extended. Engine suite `menu-coop-door` stands a host seated in the cabin and
a guest on the Multiplayer board over the loopback with a stub router. It asserts that L / Y opens
the carrier and the mapping and a second press unmaps (the able-to-fail control), that the guest's
board names the campaign session, and that Continue lands on the waiting board. It asserts the
host's chips read `P1`, `P2 net` with one local seat, and that `NetSeats.Field` gives the guest seat
1 in a session built over the released lobbies. Leaving both ends unmaps the port. Montages cover
the toggle, the join board and the waiting board.

**Original approach (kept for reference).**

**Goal.** From the Remake menus, a host opens their campaign to the network and a friend on another
machine joins it, without a command line; the joined guest shows in the host's campaign boards as a
player and waits until the host launches a mission.

**Evidence (confidence: traced).** Decision 10. The launch menu's Campaign row opens `CampaignFlow`,
whose composed boards draw a `P1 P2 P3 P4` chip strip in `SplitScreen.PlayerColor` once more than
one local player has joined (`CSVM/src/UI/LaunchMenu.cs`). B15 built the Network screen, its join
board by direct IP with UPnP, and the `--net-host`/`--net-join` paths through `NetCarrier`
(`docs/architecture/Net.md`). The original has no campaign co-op, so no screen of it to copy; the
Original presentation has no multiplayer door at all (`BL-1021`).

**Approach.** A toggle in the campaign flow (the Remake presentation only) opens the carrier through
`NetCarrier.Host` and its router mapping and shows the address to give a friend. The Network join
board accepts a co-op host: the session advertises its kind (a campaign co-op, with chapter and
mission) in its join reply, and a guest that joins one lands on a waiting board instead of the
original modes' lobby. Remote guests take net seats (A4) and appear in the chip strip as joined
players. The launch itself, the briefing a guest sees and the mission end are C24's.

**Model recommendation.** Opus. It crosses the UI, the seat roster and the join handshake, and the
screens are judged by the user.

**Verify (as scoped).** A two-session harness run that opens a campaign to the network, joins a
guest over the loopback, and asserts the guest holds a net seat, the host's chip strip counts it,
and the guest's screen is the waiting board; plus montages of the toggle, the join board and the
waiting board for the user.

**⚠ Traps.** Local and remote joiners share the chip strip and the seat ceiling of 16; a remote guest
must not take a local controller's pane. The toggle opens a port, so closing the campaign flow or
leaving the menu must close the carrier and remove the router mapping. Screens are look judgements:
montage them for the user, never park them on a measurement.

## C26 ☑ Host-decided positional starts and the airframe swap: landing approaches, the ladder switch, `PlayerRange` and codes 965 to 967 for a guest

**Landed.**
- `CSVM/src/Net/NetMessages.cs`: `PositionalStart = 0x004E`, the `NetPositionalStart` kinds
  (landing row, ladder holder, auto-land held) and its `ReliabilityOf` arm.
- `CSVM/src/Net/NetPositionalMessages.cs` (new): `PositionalStartMessage`, 12 bytes, reliable
  (kind, seat, flags with bit 0 held, pad, `i32` row index in the bound table).
- `CSVM/src/Session/NetPositionalStartLink.cs` (new): on the host, `Started` and `HolderChanged` go
  out as broadcasts, and a guest's `AutoLandHeld` sets `RemoteAutoLand` on the host's copy of that
  seat, accepted only from the peer owning it. On a guest, both runtimes are replicated, a landing
  row is started for the named seat's rig through `StartRow`, the holder is taken through
  `TakeHolder`, and `Step` reports its own seats' held button on change.
- `CSVM/src/Session/LandingApproachRuntime.cs`: `Started` (row id, rig), `Replicate`/`Replicated`,
  `StartRow`, `Pressing`. A replicated tick offers only `auto` rows to its own (not remote-owned)
  humans and holds a press until the prompt goes away; it starts nothing itself.
- `CSVM/src/Session/LadderSwitchRuntime.cs`: `HolderChanged`, `Replicate`/`Replicated`, `TakeHolder`;
  a replicated switch steps on the host's holder.
- `CSVM/src/Flight/FlightController.cs`: `RemoteAutoLand`, which a remote-owned copy's
  `AutoLandPressed` answers with.
- `CSVM/src/Session/CutsceneController.cs`: `BindRigs` takes an optional scripted player; on a
  network guest an unclaimed episode belongs to the host's seat 0.
- `CSVM/src/Session/GameSession.cs`: the landing trigger and ladder switch bind over `_seatRigs` at
  both bind sites; `WireNetPositionalStarts` opens the link after `WireNetWorld`, stepped after the
  trigger's tick; the anim runtime's range reads take `FieldPositionsSnapshot` (every seat's
  aeroplane in a network session, the panes otherwise; the per-viewer consumers keep the panes);
  `WireNetCombat`'s per-seat body is `WireSeatCombat`, which `SwapPlayerAirframe` runs again on the
  owner's seat; `Cutscene` and `SkipSwapRewire` (the rewire control) for the suites.
- `CSVM/src/Testing/NetPositionalStartSuites.cs` (new): suites `net-positional-start` and
  `net-swap-rewire`, weighted in `analysis/engine-suite-weights.json` (8.0 s and 7.1 s).
  `CoopEpisodeOwnerSuites.StageAi`/`BuildRoster` (now with seats and a world root) and
  `LandingApproachSuites.TopAncestorOf` are internal for it.
- `CSVM.Tests/NetMessagesTests.cs`: `PositionalStartRoundTripsEveryKind` (3 cases).
- Docs: `org/multiplayer-messages.md` (the `0x4E` table, a Positional starts section, the phase row
  and the director's "not the director's" bullet), `architecture/Net.md`, `Session.md`, `Flight.md`,
  and two index bullets in `architecture.md`.
- Message id `0x4E` used. No `NetWorldEvent` code added.

**Verified.** The complete battery on the merged tree (C26 over E44, E41, E43, E42 and everything
before it): build clean, 4950 units passed with 0 failed and 2 skipped, 397 engine suites passed with
engine errors clean, and 19 goldens hash-identical. `net-positional-start` and `net-swap-rewire` pass
inside it.

Agent-side evidence: build clean with 0 warnings; units 4950 passed with 2 skipped; `-Quick` passed
(578 units, 13 engine suites). `net-positional-start` passes over two built C3/M05 worlds on a 30 ms,
25 per cent lossy loopback: with the host's trigger over its panes alone nothing starts on either end
(the control), and over the whole field the host starts `ww_balmoral1` for seat 1 and the guest
replays it 0.18 s later, both episodes owned by seat 1; the capture's 967 rebuilds the guest's seat on
`pbalmoral` on both ends (the host's copy still remote-owned, 0.00 m from the guest's aeroplane),
both in group 5 with the captured Balmoral inert, and the host's own aeroplane untouched.
`net-swap-rewire` passes: with the rewire withheld the host builds 0 of the swapped guest's rounds and
the copy has no hit route (the control); rewired, 8 fired and 8 rebuilt, and the guest's death
crosses in 17 ms. The related suites (`campaign-coop-episode-owner`, `campaign-coop-approach-row`,
`campaign-airframe-swap`, `campaign-cutscene-ownership`, seven `landings-*`, `net-combat-events`,
`net-director-follow`, `net-human-field`) pass, 16 of 16 with the two new ones.

**The wiring contract.** The host's landing trigger and ladder switch read `_seatRigs`, so a guest's
copy qualifies at its interpolated pose. Every row start crosses as `0x4E` kind 1 with the seat whose
flying started it; a guest starts the same bound row for that seat's rig, so `EpisodeOwner` is that
seat on every end, and the 965 to 967 swap runs on every end from each end's own playback. The
owner's machine rebuilds its own aeroplane; every other end rebuilds a remote-owned copy (a fresh
`RemotePoseBuffer`, fed because the sample lookup is by seat on arrival) and re-wires it for combat.
An `auto` row's button crosses as kind 3 and is held until the prompt goes away. The ladder holder
crosses as kind 2. A guest's scripted player is seat 0.

**Owed.**
- `PlayerRange` gates are evaluated on each end over the whole field, not sent by the host, so a
  range-started definition can start a link delay apart on the two ends. No shipped range gate that
  raises a code has been exercised across the link.
- The suites' capture row is CM02's manual row, so the auto-land crossing (kind 3) is covered by the
  message test and not by an engine suite; the first story mission's `auto` row is the candidate.
- A ladder holder is not re-sent when the host re-binds the switch after the roster graft; a guest
  that re-binds at a different time starts from no holder until the next change.
- The director's `PlayerAircraft` stays the guest's own pane on a guest: its uses are the music
  scan, the scored shooter and the kill credit, which are per viewer. Roster leaders resolved from it
  lead AI the host flies, so the guest's choice is not visible.
- The whole path through a `GameSession` co-op campaign (a guest session flying a capture mission)
  waits on C24, since a campaign session needs a profile; the suites drive the runtimes over built
  worlds and two `GameSession` free flights.

**Original approach (kept for reference).**

**Goal.** A guest who flies into a landing approach or a capture volume is captured on every
machine: the host starts the definition, the swap runs for that guest everywhere, and the host's
`DEDG` and `LiveInGroup` reads count the guest.

**Evidence (confidence: traced).** C23's markers: approach rows and the ladder switch are bound to
`_rigs` (the panes) and `PlayerPositionsSnapshot` reads the panes, so the start and the 965 to 967
swap (`CutsceneController.EpisodeOwner`, `GameSession.SwapPlayerAirframe`, `FlightRoster.RunSwap`)
run only on the guest's own machine. A 967 swap builds a controller `WireNetCombat` never wired, so
the swapped pilot's fire, damage and death stop crossing.

**Approach.** The host evaluates every seat against the approach volumes and sends the start with
the owning seat (the next free message id); the swap replicates as the owner's new airframe and
group, and every rebuilt controller is re-wired for combat. `If PlayerRange` washes stay per machine
(cosmetic); a gate that raises a callback code reads the host's field.

**Model recommendation.** Opus. The swap rebuilds controllers under live net feeds, and a wrong order
drops a seat's `RemotePoses` feed or its combat wiring without an error.

**Verify.** Engine suite `net-positional-start`: two built C3/M05 worlds over a lossy loopback, a
guest flying CM02's Balmoral capture row; the host's copy swaps and joins the captured group, the
guest's own aeroplane likewise, with a control (the host's trigger over its panes alone) that starts
nothing. Engine suite `net-swap-rewire`: two `GameSession`s swap the guest's seat, and the swapped
guest's fire and death still cross, with a control (the rewire withheld) where they do not. Unit:
`PositionalStartRoundTripsEveryKind`.

**⚠ Traps.** Binding the approaches to `_seatRigs` alone is wrong: a swap would rebuild the host's
copy of a guest and drop its `RemotePoses` feed. On a guest, `PlayerAircraft` and an unclaimed
episode's `ScriptedPlayer` are the guest's own pane, not the host's P1.

# Wave D, hardening

## D31 ☑ Latency and loss soaks, desync instruments and a `--debug-net` readout

**Landed.** `CSVM/src/Net/NetInstruments.cs` is one machine's desync counters over its own
traffic, engine-free: state and fire samples dropped (read off the gaps in each seat's sequence,
the first sample only setting the baseline), stale arrivals, hits and bursts landing for a seat
already reported dead, and reliable events out of their causal order (a score line adding deaths
nobody reported, a respawn for a known seat nobody reported dead; a first score line and a
rematch only set the baseline). `NetInstrumentReading` subtracts, so a soak reads one stretch, and
`Describe` writes the one readout line with the invariant culture.
`CSVM/src/Net/NetSession.cs` owns an `Instruments`, feeds it every arrival after the header
check and before relay and handler, and every send once: `Broadcast` now writes the payload once
and counts it once for the instruments however many peers take it, still one `Sent` per peer.
`CSVM/src/Net/LoopbackTransport.cs` counts `Lost` (sender side) and `DiscardedStale` (receiver
side), the carrier's truth the inferred gap count is checked against.
`CSVM/src/Net/RemotePoseBuffer.cs` keeps a `RemotePoseTally`: samples accepted and stale, the
owner's reads by feed (the `TrySample(out)` overload alone counts, so a suite's probe read does
not), and each accepted sample's extrapolation error against the one before it flown on its
velocity across the sequence gap, the one position error a machine reads without the owner. A
miss past twice the sample's reach is a jump (a placement, such as a respawn sample overtaking its
spawn event) and is counted apart. `CSVM/src/Net/AircraftStateCadence.cs` gained `SampleSeconds`.
`CSVM/src/Testing/NetSoakSuites.cs` is `net-soak`: a host and a guest on one seeded mesh fly four
cells (clean; 50 ms, 10 ms jitter, 5 per cent loss; 100 ms, 20 ms, 10 per cent; 200 ms, 40 ms,
20 per cent), each a 240-step curve with both guns held, a kill each way with the shooter
claiming hits until the death reaches it, and both aeroplanes back in play. Each cell notes
position error against the owner's own path (`NetSessionSuites.Track`, now internal), the tally,
both machines' instrument deltas and the carriers' own loss and discard counts. It asserts zero
order violations and zero stale arrivals everywhere, the position bars below, a clean cell that
drops nothing and starves no read in flight, the inferred drop count equal to the carriers'
`Lost` plus `DiscardedStale` both ways after a clean flush, and two able-to-fail controls (the
worst cell drops and its deaths reach the shooter later than the clean cell's; an injected
respawn for a flying seat moves the order counter by exactly one). `NetCombatSuites`' `Ambient`
and `Ends` are internal so the soak reuses the rig.
`--debug-net` (`CSVM/src/SessionSpec.cs`) makes `CSVM/src/Session/Launcher.cs` build a
`CSVM/src/UI/NetReadout.cs` and, once a wall second, log `Describe`'s line under `core` and show
it in the top-left corner on `HudLayers.Debug`, the tallies summed over every seat's buffer. It
reads `GameSession.NetLink` and `SeatRigs`, which already exist, so no hook in `GameSession` was
needed. Units: `CSVM.Tests/NetInstrumentsTests.cs` (new), and additions to
`RemotePoseBufferTests`, `LoopbackTransportTests` and `SessionSpecTests`. The suite's weight is
in `analysis/engine-suite-weights.json`. Docs: `docs/architecture/Net.md` (a new entry, four
entries changed), `docs/architecture/UI.md`, the index bullets in `docs/architecture.md`, and
`docs/cli.md`. Backlog: `BL-1041`.

**Verified.** The complete battery on the merged tree (C21 and D31 over Wave B): build clean, 4903
units passed with 2 skipped, 388 of 388 engine suites passed with engine errors clean in all six
shards, 19 of 19 golden shots hash-identical. `net-soak` passes all four link cells with zero order
violations and zero stale arrivals, each machine's inferred drop count equal to what the loopback
lost or discarded, and both able-to-fail controls moving as expected. The limit: every number is
the loopback's model of a link, the position-error bars are TUNE (`BL-1041`), and the
`--debug-net` corner readout has not been seen on screen, only its text line is unit-tested.

**Landed (fire on its own channel).** Fire rode its seat's state channel, so jitter discarded
gunfire behind a newer state sample: at 50 ms and 5 per cent the carriers discarded 10 payloads
beside 12 lost, and 11 fire events of 54 rounds went missing. `NetChannels.ForFire` now gives
each seat a fire channel above the whole state range, `NetChannels.Count` is the layout's width
and `EnetTransport.ChannelCount`, and `GameSession.SendFire` names the fire channel.
`FireMessage` is plain unreliable rather than sequenced: rounds fired on one step race each other
under jitter, a sequenced class would drop the one landing second, and a reliable one would stall
a burst behind a retransmission. `NetInstruments` counts a burst that fills a fire gap inside the
last 64 as `ReorderedFire` instead of stale. AI fire already rode the events channel unsequenced
and is unchanged. After: the 50 ms cell discards nothing and misses 2 fire events of 54 (5 per
cent loss would take about 2.7); the 100 ms cell discards nothing; the 200 ms cell's 11 to 13
discards are state against state, since 40 ms of jitter exceeds the 50 ms sample spacing. No
fire is drawn out of order in any cell. `net-soak` asserts the 50 ms cell's zero discards, and
`enet-transport` sends on the top fire channel.

**Verified.** <pending orchestrator run>

**Owed.** The readout itself has not been seen on a real two-machine
link: no suite drives `Launcher.TickNetReadout`, so the corner text and the log line are owed a
look at the controls.

**Co-op mission soak (sharpened marker, no code).** The same cells over one co-op mission, once
Wave C lands: a host and a guest in `CampaignDirector`'s shared mission, reading the same
instruments plus the director's transitions (`DirectorTransitionMessage`) as a sixth reliable
stream whose order is checked the way the score line's is. It reuses `NetSoakSuites`' `Pair`,
cells and `Judge` unchanged; what it adds is a scripted objective each way and a check that both
machines reach the same transition sequence.

**Original approach (kept for reference).**

**Goal.** A soak on the harness at chosen latency and loss reports position error, event order
violations and dropped-message counts, and a live session can show the same numbers on screen.

**Evidence (confidence: lead-only).** The harness (A5) and the loopback loss model (A1) are the
instruments; the acceptable numbers are unmeasured.

**Approach.** A soak suite over a scripted Dogfight and one co-op mission at a small matrix of
latency and loss; a `--debug-net` flag in the pattern of `--debug-anim` that prints the counters to
the HUD and the log.

**Model recommendation.** High. The counters are only worth something if each rule is right
about causal order across the star's relay (a broadcast counted once, a late joiner's baseline,
a rematch), and the soak's controls have to be able to fail; a cheaper tier would ship counters
that read zero for the wrong reason.

**Verify.** `net-soak` alone and the Net suites it shares code with. Measured on the seeded run
(worse direction, mean/worst metres of position error after the fitted lag): clean 0.01/0.01,
50 ms/5 per cent 0.57/0.89, 100 ms/10 per cent 0.81/3.03, 200 ms/20 per cent 1.43/4.67. The bars
are 0.25/0.5, 1.5/3, 2/6 and 3.5/10, two to three times the measurement. They are TUNE
(`BL-1041`): what a player accepts is unmeasured, so a bar is a regression tripwire. Exact on
every link: zero order violations, zero stale arrivals, and the inferred drop count equal to the
carriers' own. The clean cell drops nothing and starves no read in flight.

**⚠ Traps.** The file sink takes only `Log.*` lines and debug lines are flag-gated; absence of a
line is not evidence. After a respawn every buffer starts empty and starves for the render delay
on any link (10 reads a cell here), so a starve count read over a whole match is not a link
measure. A respawn sample can overtake its reliable spawn event on a lossy link, which the
buffer's error counts as a jump rather than a thousand-metre miss.

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

**Verified.** On the run branch with Wave B whole, the full battery reads build clean, 4883 units
passed with 2 skipped, 386 of 386 engine suites passed with engine errors clean, and 19 of 19
golden shots hash-identical; `enet-transport` carries the selection check in its shard and alone.
At landing both flavours built clean and the Steam-flavour units matched the default flavour's
count. No Steam network was opened, because no SDK is linked; what is proven is that the seam
takes a second carrier and that selecting it changes nothing above the seam.

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

## D33 ☐ The router mapping on a finite lease with a stale mapping cleared, and a fuzz of every message reader

**Goal.** A host's router never keeps a CSVM port mapping longer than the host needs it, even when
the game dies without closing, and no payload a peer can send makes a reader throw or allocate
without bound. A separate item rather than a part of F51, because it touches neither the menus nor
the session and can land beside anything but F51's `NetPlayFeature.cs` edits.

**What exists to build on.** `CSVM/src/Net/UpnpPortMap.cs`: `Map(port, "CSVM")` calls
`AddPortMapping(port, port, description, "UDP", 0)`, where lease 0 is permanent, and `Unmap(port)`
runs only when hosting closes, so a crash or a killed process leaves the mapping in the router for
good. `NetPlayFeature` runs the map on its own thread when hosting opens and the unmap when it
closes (B15). The readers: every message's static `TryRead` (15 in `Net/NetMessages.cs`, 7 in
`Net/NetWorldMessages.cs`, 1 in `Net/NetPositionalMessages.cs`), behind `NetMessageReader`, and the
listeners that call them (`NetLobby.OnPayload`, `NetSession`'s dispatch and the session links).

**Evidence (confidence: direction sound, lease length TUNE).** Godot's UPnP client takes a lease
duration in `AddPortMapping` and offers no enumeration of a gateway's mappings, so a stale mapping
can only be removed by port. Some IGD v1 gateways refuse any lease but 0 (UPnP error 725,
OnlyPermanentLeasesSupported), unverified against a real router here.

**Approach.**
- **Lease.** `Map` asks a finite lease (a TUNE value, one hour as a starting point) and
  `NetPlayFeature` renews it on its mapping thread before it expires while hosting stays open. A
  gateway that refuses a finite lease falls back to lease 0 with the unmap on close, and says so in
  the log. The lease and renewal rules sit in an engine-free policy class beside `UpnpPortMap`, so
  units test them over a fake gateway.
- **Stale mapping.** Before adding, delete any mapping on the same port (a `CSVM` mapping left by an
  earlier run), and remember the last mapped port in the user's settings so a run that hosts on a
  different port removes the old one first.
- **Fuzz.** A unit fuzz over every message reader, enumerated by reflection over the
  `INetMessage<T>` implementations so a message added later is covered without an edit: a seeded
  random payload of every length up to twice the largest message, every truncated prefix of a valid
  encoding, a valid body under a wrong length word or a wrong type word, and a valid header over
  random bytes. Each must return false (or a value it re-encodes identically) without throwing, and
  the allocation per call stays under a fixed bound (`GC.GetAllocatedBytesForCurrentThread`). The
  same payloads go through `NetLobby.OnPayload` and a loopback `NetSession`'s dispatch, which must
  neither throw nor index a seat out of range. F51's discovery reply and F52's lobby messages join
  through the same enumeration.

**Model recommendation.** Medium. Two contained changes with a clear contract; the only judgement
is the lease fallback.

**Verify.**
- Units over a fake gateway: a finite lease is asked (the able-to-fail control: today's lease 0
  fails it); the renewal falls before the lease runs out; a 725 refusal falls back to lease 0; the
  stale delete precedes the add; a changed port removes the remembered one.
- Unit `NetMessageFuzzTests`: the enumeration finds every `NetMessageType` that has a reader (a
  count check, so a message the reflection misses fails the test), and every case above passes.
- The existing `enet-transport`, `net-enet-join`, `menu-net-door` and `menu-coop-door` suites still
  pass.

**⚠ Traps.** Renewal must not run on a frame or in a step; the gateway search blocks (B15). A lease
that expires mid-match closes the door on every guest outside the router, so the renewal margin
must cover a slow gateway search. Godot's client cannot ask a gateway who holds a mapping, so a
delete by port can remove another program's mapping on that port; delete only the port this run is
about to map and the one the settings remember, never a range.

# Wave E, the rest of the host-owned world

C22 made AI aircraft and destructible stages host-owned and left four world phases replayed locally
on a guest. Each item below takes one of them to the host. The phase table is in
`docs/org/multiplayer-messages.md`, "The host-owned world"; C22's **Markers** list states each gap.

## E41 ☑ Host-owned AI spawns: generator launches, Black Hat launches and `WAKEUP_*` as spawn events carrying the host's admission ordinal

**Landed.** The trace narrowed the item. A Black Hat launch (801 to 803, `FirstDormantOf` then
`ActivateDormantRoster`) and `WAKEUP_ENEMIES` add no AI: they reactivate roster blocks built inert
at build time, which already hold their ordinals on both ends. What they can part is presence, not
the ordinal. The one run-time source that grows the AI list in a network session is a generator's
aircraft launch (`AiGeneratorRuntime.Spawn` into `FlightRoster.SpawnAi`), since Instant Action
does not run there. By file:
- `CSVM/src/Net/NetMessages.cs`, `CSVM/src/Net/NetWorldMessages.cs`: `AiSpawnMessage`, id
  **`0x4C`**, 44 bytes, reliable on `NetChannels.Events`, host to all: admission ordinal, launch
  counter, live generator index, net index, flags, lever, position, drop direction, velocity.
  `NetWorldEvent.AiPresence = 5` on the existing `0x48` (4 is E43's).
- `CSVM/src/Session/AiGeneratorRuntime.cs`: `GeneratorAircraftLaunch`; `AircraftLaunched` after
  each aircraft launch; `Replicate`, `RefusesOwnAircraft` and `RefusedLaunches`;
  `LaunchReplicated`, which builds the host's launch with its net, pose, velocity, lever and launch
  counter and starts no take-off run; `LaunchedVehicle.Refusal`, on which `Spawn` hands the cycle's
  slot back.
- `CSVM/src/Session/GameSession.cs`: the generator spawner returns `Refusal` for an airframe or
  roster-template launch while `RefusesOwnAircraft` holds (surface hulls untouched);
  `WireNetWorld` calls `FollowGenerators`; `Generators` accessor for the harness.
- `CSVM/src/Session/NetWorldLink.cs`: `FollowGenerators`. The host admits the launched aircraft at
  once and broadcasts its ordinal; a guest builds it only when that ordinal is its next, else drops
  it (`SpawnsSent`, `SpawnsTaken`, `SpawnsRefused`). The host sends `AiPresence` on every
  `InertChanged` outside a cutscene park; a guest applies it.
- `CSVM/src/Session/CampaignDirector.cs`: `ActivateDormantRoster` leaves a remote-owned copy
  inert, so a guest's own `WAKEUP_ENEMIES` and 801 to 803 wait for the host's presence event.
- `CSVM/src/Testing/NetAiSpawnSuites.cs` (new): `net-ai-spawn`; its weight in
  `analysis/engine-suite-weights.json`.
- Tests: an `0x4C` round trip in `NetMessagesTests`.
- Docs: `docs/org/multiplayer-messages.md` (id list, `0x4C` row and prose, code 5, the director's
  wake and 801 to 803 bullets, the Generators phase row), the Session and Net entries and index.

**Verified.** The complete battery on the merged tree (E41 over E43, E42 and everything before it,
with `WireNetWorld` calling `FollowVehicles` and then `FollowGenerators`): build clean, 4947 units
passed with 0 failed and 2 skipped, 395 engine suites passed with engine errors clean, and 19
goldens hash-identical. `net-ai-spawn` passes inside it.

**Owed.**
- The guest's own campaign wake refusal is not driven by a suite: `net-ai-spawn` runs `--fly`, with
  no director. Presence is exercised by setting `Inert` on the host directly.
- A guest's generator cycles and doors still run on its own timers and credits, which is cosmetic.
- A late joiner has missed every earlier launch and presence event (C24's).

**Verify.** `.\RunTests.ps1 -Suite net-ai-spawn -SkipUnits -SkipGoldens`: a host and a guest session
on C1/M02 with `--generators` over a 30 ms, 10 ms jitter, 25 per cent loss loopback. The host
credits eairg32 and, once that has launched, eairg31; the guest credits eairg31 alone, so its own
cycle would launch first at the other airfield. The guest must refuse its own launch, build the
host's two at the host's ordinals, first-seen within 20 m of the host's launch point (the two points
stand about 120 m apart), with the host's names, then track each host path (0.08 and 0.13 m mean,
about 118 m against the other). A sample for an unadmitted ordinal admits nothing; the host's
`Inert` reaches the guest both ways, and a cutscene park does not. Units: `NetMessagesTests`.

**Original approach (kept for reference).**

**Goal.** Every AI a generator launches, a Black Hat launch spawns (callback codes 801 to 803) or a
`WAKEUP_*` directive wakes exists on every guest under the same admission ordinal as on the host.

**Evidence (confidence: traced).** `NetWorldLink` names an AI by its admission ordinal in the
roster's append-only list, and both ends must grow that list in the same order. A guest's generator
and Black Hat launches and its `WAKEUP_*` directives run on its own timers (C21 replays the
directives locally as an interim), so the ordinals can shift against the host's; an AI with no host
counterpart then holds at its spawn, and a shifted one tracks the wrong host AI.

**Approach.** The host sends a reliable spawn event carrying the ordinal, the roster block and the
spawn pose; a guest's own launch and wake paths are refused while replicated, and it admits AI only
from the event, in the shape of B13's `GrantSpawn`.

**Model recommendation.** Opus. The ordinal contract is shared by every world message, and a wrong
order shows only as an AI tracking another's path.

**⚠ Traps.** The spawn event must arrive before the first `0x45` sample for that ordinal; a sample
for an unknown ordinal is held or dropped, never admitted.

## E42 ☑ Zeppelin paths from the host: the path position as a periodic state message, in the original's `0x1e` shape

**Landed.** The original's `0x1e` is decoded in full in `docs/org/multiplayer-messages.md` ("The
zeppelin state packet"): builder `FUN_0049adf0`, host only, every 0.5 s (the float at `0x006032e0`),
unguaranteed to all; `0x1c` bytes per zeppelin (position, speed `zep+0xa4`, pitch `zep+0x30`, yaw
`zep+0x2c`, a part-state word, an event count) and a cannon-shot tail; receiver `FUN_0049b0b0` sets a
target, and the guest law `FUN_00470550` replaces the path step with an `e^(-2 dt)` chase onto a
dead-reckoned target. By file:
- `CSVM/src/Flight/ZeppelinReplica.cs` (new): that guest law, with a per-zeppelin sequence that drops
  a stale sample, `Reseat` for a scripted motion's hand-back, and the two decoded constants
  (`ChaseRatePerS = 2`, `SendSeconds = 0.5`).
- `CSVM/src/Flight/ZeppelinMotion.cs`: `Follow`, which takes a pose and speed from outside the law.
- `CSVM/src/Session/ZeppelinRuntime.cs`: `Replicate`, `ReplicaAt`, `TakePath` and `TryReadPath`; a
  hull with a replica steps the replica instead of its follower and steer law.
- `CSVM/src/Net/NetMessages.cs`, `CSVM/src/Net/NetWorldMessages.cs`: `ZeppelinStateMessage`, id
  **`0x4B`**, 32 bytes, plain unreliable on `NetChannels.Events`, the original record's first `0x18`
  bytes in its order, named by placement index.
- `CSVM/src/Session/NetWorldLink.cs`: `FollowZeppelins(ZeppelinRuntime)`; the host's `StepSends`
  samples every moving hull each `ZeppelinSendSteps` (30 steps); a guest replicates and takes them.
- `CSVM/src/Testing/NetZeppelinSuites.cs` (new): the `net-zeppelin-path` suite.
- Tests: `ZeppelinReplicaTests` (6) and a `0x4B` round trip in `NetMessagesTests`.

**Verified.** The complete battery on the merged tree (E42 over C25, C23, the catch-up, C22, C21,
D31 and Wave B, with `GameSession.WireNetWorld` calling `FollowZeppelins` whenever the session has
zeppelins): build clean, 4943 units passed with 0 failed and 2 skipped, 392 engine suites passed with
engine errors clean, and 19 goldens hash-identical. `net-zeppelin-path` passes inside it.

**Owed.**
- The lag is the original's: a guest's hull trails the host's by speed times (latency + 0.5 s). No
  clock-slew compensation is applied, since the original applies none.
- The part-state word and the cannon-shot tail are not carried: part deaths already arrive as pool
  events, and each end's broadside still fires on its own. Whether a guest's broadside should be
  driven from the host is open.
- A late joiner's hull holds at its placement until the first sample, at most half a second.

**Model recommendation.** High. The decode is a chain of six functions and the receiver law has to
be read off the disassembly; the code itself is small.

**Verify.** `.\RunTests.ps1 -Suite net-zeppelin-path -SkipUnits -SkipGoldens`: C3/M01's two
zeppelins through net-soak's four link cells (clean, 50, 100 and 200 ms with 5, 10 and 20 per cent
loss, mesh seed 7717), 28 s read per cell. The guest's hull must stay within speed times (latency +
jitter + 0.5 s chase lag + 0.5 s turn slack) of the host's; a replicated guest never fed is the
control and must exceed that bar. A guest flying its own follower is reported beside it. Units:
`ZeppelinReplicaTests`, `NetMessagesTests`.

**Original approach (kept for reference).**

**Goal.** A zeppelin is where the host has it on every guest, whatever the guest's frame rate or
join time.

**Evidence (confidence: decoded for the original, traced for the remake).** The original sends a
zeppelin's path position as `0x1e` every 0.5 s (`docs/org/multiplayer-messages.md`). The remake
replays the path locally from the shared seed and clock.

**Approach.** The host sends each zeppelin's path parameter at the decoded period; a guest steers its
path runtime to it instead of advancing it alone.

**⚠ Traps.** Zeppelin parts and cannons are already destructible pools under C22; a path message must
not re-spawn or re-arm them.

## E43 ☑ Surface vehicles from the host: patrols and `WARP_VEHICLE` placed by the host, not replayed from a diverging draw

**Landed.** A guest's surface hulls chase the host's patrol samples, and a guest director's
`WARP_VEHICLE` takes the host's draw instead of making its own. By file:
- `CSVM/src/Net/NetMessages.cs`, `CSVM/src/Net/NetWorldMessages.cs`: `SurfaceVehicleStateMessage`,
  id **`0x4D`** (not `0x4C`, which E41 may take), 32 bytes, plain unreliable on
  `NetChannels.Events`: spawn index, per-hull sequence, name hash, position, speed, yaw. World
  event code **4** `VehicleWarped`: subject the drawn index, argument the vehicle's name hash.
- `CSVM/src/Session/SurfaceVehicle.cs`: `Replicate`, `TakeSample`, `TryReadPatrol` and
  `Replicated`. A replicated hull steps a `ZeppelinReplica` (no pitch) instead of its follower, and a
  later route assignment keeps the net without drawing a route.
- `CSVM/src/Session/SurfaceVehicleRuntime.cs`: `Replicate`, which also covers hulls spawned later.
- `CSVM/src/Session/CampaignDirector.cs`: `WarpDrawn` (the host's pick), `TakeWarpsFromHost` and
  `TakeHostWarp`. A guest's directive queues until the pick arrives, or places at once when the pick
  came first; the placement body is `PlaceWarp`, shared by both.
- `CSVM/src/Session/NetWorldLink.cs`: `FollowVehicles(SurfaceVehicleRuntime?, CampaignDirector?)`,
  `SurfaceSendSteps` (the zeppelin's 30 steps), `NameKey` (the FNV-1a that `PoolKey` now calls), and
  the counters `SurfaceSamplesSent` and `SurfaceSamplesTaken`.
- `CSVM/src/Testing/NetSurfaceVehicleSuites.cs` (new): `net-surface-patrol`.
- `CSVM/src/Testing/CampaignBlackeSearchSuites.cs`: `net-blacke-warp`.
- Tests: three in `NetMessagesTests` (the `0x4D` round trip, code 4, `NameKey`).
- `docs/org/multiplayer-messages.md`: the `0x4D` row and its paragraph, code 4, the phase row, and
  `WARP_VEHICLE` moved out of the replayed list.

**Verified.** The complete battery on the merged tree (E43 over E42 and everything before it, with
`GameSession.WireNetWorld` calling `FollowVehicles(_surfaceVehicles, _campaign)` after the zeppelin
hook): build clean, 4946 units passed with 0 failed and 2 skipped, 394 engine suites passed with
engine errors clean, and 19 goldens hash-identical. `net-surface-patrol` and `net-blacke-warp` pass
inside it.

**Approach.** Replication for patrols, a host draw for the warp. The survey in `net-surface-patrol`
finds no branch node on any roster or `SET_AI_NET` hull net of C1B/M03 or C2/M01, and one on C2/M01's
launch net, so the branch pick is not the main risk. A replayed patrol still diverges from timing: a
late wake, a `SET_AI_NET` route that starts wherever the hull stands, a launch on each end's own timer,
and a late join. Samples remove all four, at the cost of the chase lag. `WARP_VEHICLE` only ever
targets an aircraft rig (C4/M02 hides Blacke, who stays `Inert`, so no `0x45` would move him), so it
needs the pick and not a hull message.

**Owed.**
- A generator launch still spawns a hull on each end's own timer, so hull indices can shift. The
  name hash finds the hull, but a launch the guest has not made has no hull to take the sample. That
  is solved when E41 moves the launch to the host.
- A late joiner's hulls hold where they were built until the first sample, at most half a second. A
  late joiner has missed the warp event.
- A guest hull's gun still fires locally, as the zeppelin broadside does.
- A path-named warp point is released locally on each end, the same as `START_TAXI`. No shipped
  mission authors one.

**Model recommendation.** Medium. The patrol side copies E42's pattern, and the work is in reading
which draws exist and where the warp's directive and its pick can arrive in either order.

**Verify.** `.\RunTests.ps1 -Suite net-surface-patrol -SkipUnits -SkipGoldens`: C1B/M03's four
boats through net-soak's four link cells. The guest's hull must stay within speed times (latency +
jitter + 0.5 s chase + 0.5 s slack) of the host's, and an unfed replicated guest must exceed that
bar. `.\RunTests.ps1 -Suite net-blacke-warp -SkipUnits -SkipGoldens`: C4/M02 over a 100 ms, 10 per
cent loopback. Two guests, one early and one late, draw nothing and land on the host's waypoint, and
a control drawing for itself lands elsewhere. Units: `NetMessagesTests`.

**Original approach (kept for reference).**

**Goal.** Every surface vehicle patrols and warps where the host's does.

**Evidence (confidence: traced).** Surface-vehicle patrols are replayed locally on a guest, and
`WARP_VEHICLE` takes a random draw that diverges between machines (C22's markers).

**Approach.** Map whether a patrol is deterministic from the seed and clock once `WARP_VEHICLE` is
taken from the host, or needs state samples like an AI aircraft.

**⚠ Traps.** "Same seed" is not "same world" once any draw depends on world state; decide per vehicle
path, as C22 did per phase.

## E44 ☑ Destructible chip damage: a pool's health between stages mirrored on every guest

**Landed.** A host hit that lowers a pool without a stage change or a kill now reaches every guest
as a `0x48` code 3 sample, the same event a stage change already used, so no new message id or
world event code was needed. Three guest-visible rules read the health between stages, so the item
was not closable by proof: the target bar's fraction (`Flight/TargetPool.cs`), a surface hull's
injure ladder, which plays its anims at health fractions independent of the stages
(`Session/SurfaceVehicle.cs` `StepInjureLadder`), and every `ANIM_HEALTH` condition
(`AnimRuntime.HealthOf`). By file:
- `CSVM/src/Mech3/AnimRuntime.cs`: `DestructibleChipped`, raised by `SpendHealth` when a spend
  lowers health without moving the stage or killing; `DestructibleDamaged` is unchanged.
- `CSVM/src/Session/NetWorldLink.cs`: the host marks each chipped pool once, and `StepSends`
  flushes them on the seat stream's cadence (`AircraftStateCadence.SendStepInterval`, three steps),
  one sample per pool however many hits landed. A stage change or kill sends at once and drops the
  pool's waiting chip, since it carries the same health. `ChipSamplesSent` counts them. A guest
  applies the sample through the existing `ApplyReplicatedHealth`, never `DamageAt`, so it cannot
  spend twice.
- `CSVM/src/Testing/NetWorldSuites.cs`: `net-ai-world` gains a chip reading.
- Docs: `docs/org/multiplayer-messages.md` (code 3's meaning and a paragraph on the chip samples),
  the Mech3 and Session architecture entries and the `NetWorldLink` index bullet.

**Verified.** The complete battery on the merged tree (E44 over E41, E43, E42 and everything before
it): build clean, 4947 units passed with 0 failed and 2 skipped, 395 engine suites passed with engine
errors clean, and 19 goldens hash-identical. `net-ai-world` passes inside it with its chip reading.

**Approach.** A health sample per damaged pool, coalesced per seat-cadence tick, on the reliable
class. Reliable because a guest only lowers a pool: an old sample arriving late changes nothing, and
a lost last sample would leave the guest's copy high until the next hit. The cost is 16 bytes per
chipped pool per tick at most, 20 Hz only while that pool is under fire.

**Model recommendation.** Medium. The spend path and the event code already existed; the work was
the reader census and the coalescing.

**Verify.** `.\RunTests.ps1 -Suite net-ai-world -SkipUnits -SkipGoldens`: after the kill reading, the
host lands four hits of 0.5 per cent of the spared pool within one step. Checks: the host's stage is
unchanged and its health lowered; the able-to-fail control that the guest's copy still reads full
before any step runs; after 30 steps over the 30 ms, 25 per cent lossy loopback, the guest's health
equals the host's with the same stage; and exactly one chip sample was sent for the four hits.

**Owed.**
- The bandwidth of chip samples under a real firefight is unmeasured; D31's soaks can measure it if
  a busy mission shows it.

**Original approach (kept for reference).**

**Goal.** A destructible's health between its damage stages is the same on every peer, so a guest's
targeting and hit feedback read the host's value.

**Evidence (confidence: traced).** C22 sends only stage changes and deaths (`0x48` world event,
`NetWorldEvent` 3), so a pool's health between stages is not mirrored.

**Approach.** A health sample per damaged pool, coalesced per tick, against the bandwidth D31
measures; or proof that no guest-visible rule reads chip health, which closes the item.

**Verify.** A harness assertion that a pool's health on the guest equals the host's after a burst
that does not cross a stage.

**⚠ Traps.** `ApplyReplicatedHealth` is a guest's only spend; a chip sample must not go through
`DamageAt`, or a guest spends twice.

# Wave F, the Original presentation's multiplayer screens

Decision 10 puts every network door in the Original presentation, which is the default. The
original's 22 multiplayer GUI scripts in `crimson.rof` have no `LAYOUT.CSV` section: they use the
`CC` widget library and assign geometry inline (`docs/org/menu-inventory.md`), so decoding
`LAYOUT.CSV` reaches none of them, and the layout of every screen below comes from the
`OriginalScreenshots/Multiplayer *.png` shots. Reading the scripts' inline geometry out of
`crimson.rof` is a lead that would replace screenshot measurement, not a prerequisite. The art
these screens use is `MP_`-prefixed in the extraction (`MP_B_CheckBox8States.png` is cited in
`docs/formats/menu-layout.md`); inventory what exists before drawing any piece as remake chrome.

## F51 ☐ The network doors in the Original presentation: the cabin's Host Co-op button, and the Connection page with LAN discovery and the games list (`BL-1021`)

**Goal.** A campaign host opens co-op from the Original cabin, and a guest finds it on the LAN (or
types the host's address) from the original's Multiplayer Connection screen, joins, and is handed
to C24's flow. The Connection page's Host opens F52's lobby. Closes `BL-1021`, the Original
presentation's missing multiplayer door.

**What exists to build on.**
- C25's door: `NetPlayFeature` (`OpenCoopHost`, `Offer`, `Advert`, `Advertising`, `IsCoopHost`,
  `IsCoopGuest`, `HostStarted`, `PortMap`, `Close`), `NetLobby`, `SessionAdvertMessage` (`0x4A`,
  24 bytes: kind, mission sequence, player count and a 16-byte host name), and `CoopDoorText`
  (`HostBand` is the NETWORK OPEN band; `SessionName` and `WaitingStatus` the guest's words).
- B15's carrier and router calls: `EnetTransport`, `UpnpPortMap`, `NetPlayFeature.DefaultPort`
  (47500), registered in `Session/Launcher.cs`.
- The Original shell: `OriginalShell.cs` draws `MM_B_MULTIPLAYER` (layout row
  `MM_B_MULTIPLAYER=B,PM_B_MultiPlayer.png,280,380,...,MultiPlayerMain`) disabled; the edge is
  `Edge.Disabled` in `CSVM.Tests/OriginalCoverageTests.cs`, and `MenuOriginalSuites.cs` asserts it
  takes no input. `docs/org/menu-inventory.md` records it as the one disabled top-level plaque.
- The Original cabin: `OriginalCampaignScreen.cs`'s `ActivateCabin` switches on the `BoardButton`
  rows (Next Mission, Previous Missions, Plane Construction, Return to Main Menu, Change Memento).
  The remake-only Dogfight door under the Free Flight door (`OriginalSeats.cs`) is the precedent
  for a remake-only row drawn in the original's button style.
- The original's screens: `Multiplayer Connection.png` (MSN Gaming Zone, LAN IPX, LAN TCP/IP,
  Internet with an IP Address field, Modem-to-Modem, a Build Custom Plane panel, Host, Connect,
  Exit Multiplayer) and `Multiplayer Connection Screen.png` (the LAN TCP/IP Games list: Game Name,
  # of Players, Mission Type, Mission Environment and Status columns each with a Sort by radio, an
  auto-refresh toggle, Create Game, Join Game, Exit, and a "Searching ..." dialog with Cancel).
- No LAN discovery exists: nothing under `CSVM/src` opens a UDP broadcast socket.

**Evidence (confidence: lead-only).** The screens are seen, not decoded. Godot's `PacketPeerUdp`
can broadcast (`SetBroadcastEnabled`), untested here.

**Approach.**
- **The cabin door.** A Host Co-op button in the Original cabin's button column, offered only on a
  campaign cabin and never while the door is open for a Dogfight. It opens the co-op host through
  `NetPlayFeature` as C25's Built-in toggle does; while hosting it reads Close Network, the
  NETWORK OPEN band shows the address and guest count, and a player chip per guest shows its Ready
  mark once C24 or F52 has built the Ready roster. Close Network, or leaving the cabin for the main
  menu, sends every guest a reliable close notice (the next free message id at the time; `0x4F` is
  free now) before the carrier closes, so the guest's Connection screen can say "Host closed the
  game" where a dropped link says "Host left the game". The game name is `<profile>'s campaign`.
  The co-op cap becomes four humans, local and remote together, in `NetPlayFeature` so the
  Built-in door's call in `LaunchMenu.cs` inherits it.
- **The Connection page.** `MM_B_MULTIPLAYER` goes live and opens it. LAN TCP/IP and Internet are
  live; MSN Gaming Zone, LAN IPX and Modem-to-Modem are drawn greyed, and so is Build Custom Plane,
  since guests fly stock planes this milestone. Host opens F52's lobby as the Dogfight host (greyed
  until F52 lands). Connect with LAN TCP/IP opens the games list; with Internet it joins the typed
  address directly.
- **LAN discovery.** A guest's search broadcasts a small query on a fixed discovery port; an open
  door (co-op or Dogfight) answers with its advert and its game port. The carrier is a new file
  under `Net/` and the only other one naming a Godot networking type; the parsing is engine-free.
- **The games list.** One row per answer: Game Name (`<host>'s campaign` for co-op); # of Players
  as `n/4` for co-op and `n/16` for Dogfight; Mission Type (`Campaign co-op` or `Dogfight`); Mission
  Environment (the mission's name, langui `3450 + seq`, falling back to the mission's shortcode,
  such as `C2/M03`, when the name does not fit the column; the map for a Dogfight); Status
  (`Waiting` in the cabin or briefing, `In mission`, which is still joinable and waits in the cabin,
  or `Full`). Status and the cap are new advert fields. The five Sort by radios and the auto-refresh
  toggle work; Join Game is greyed until a row is picked; Create Game is Host.
- **Where a guest lands.** A co-op join goes to C24's guest cabin; until C24 lands, to a shell
  dialog over `CoopDoorText.WaitingStatus` with a Leave button. A Dogfight join goes to F52's lobby.
- **The release documents.** `.github/SECURITY.md`'s surface gains the discovery responder, which
  listens only while a door is open, and the guest's broadcast; `docs/PLAN-public-release.md`'s grep
  list names the third file holding a networking type.

**Model recommendation.** Opus. A new socket under the namespace dependency scans, the Original
shell's first live multiplayer edge, and screens the user judges against the original.

**Verify.**
- Units: `CoopDoorTextTests` extended (the game name, `n/4`, the shortcode fallback when the name
  overflows the column, the three statuses); a discovery protocol test (query and reply round trip,
  a truncated or foreign datagram rejected); `NetMessagesTests` for the extended advert and the close
  notice; `OriginalCoverageTests`' `MM_B_MULTIPLAYER` edge now live (its old `Disabled` entry fails
  the test); `NetNamespaceDependencyTests` exempting the discovery carrier by full name alone.
- A new engine suite (`menu-original-connection`) over a loopback pair with a stub router: the
  Multiplayer plaque opens the Connection page (the rewritten check in `MenuOriginalSuites.cs`
  fails on today's disabled plaque); the cabin's Host Co-op opens the carrier and the mapping and
  Close Network unmaps (the able-to-fail control); the games list shows the host's row with all five
  columns; Join Game lands the guest; Close Network puts the guest on the Connection page with "Host
  closed the game", and a carrier dropped without the notice gives "Host left the game"; a fifth
  human is refused as `Full`.
- An engine suite (`lan-discovery`) runs the real discovery socket on `127.0.0.1` by unicast, since
  a broadcast on the loopback proves nothing on Windows; the broadcast itself is at the controls.
- Montages of the cabin door (shut, open with two guests), the Connection page and the games list
  beside the original's shots, for the user's look judgement.
- At the controls: two machines on one LAN, the search finding the host.

**⚠ Traps.**
- A discovery reply must not be larger than the query that asked for it, or the responder is a
  reflection amplifier; pad the query to the reply's size.
- The advert's host name is 16 UTF-8 bytes, so a long profile name truncates; cut on a character
  boundary.
- The Connection page must keep stepping `NetPlayFeature` every menu frame, or a join never lands
  (`BL-1021`'s own trap).
- A wildcard bind is what makes Windows ask about the firewall; the suites bind the loopback.
- `docs/org/menu-inventory.md` counts `MM_B_MULTIPLAYER` as the one disabled plaque in several
  places; update every count in the landing commit.

## F52 ☐ The Multiplayer Lobby rebuilt in the original's layout, with Dogfight live (`BL-1022`)

**Goal.** The Connection page's Host opens the original's Multiplayer Lobby, where the host sets
the match and every pilot picks a plane and ammo, presses Ready, chats, and launches a network
Dogfight in which every end flies the same map, rules and airframes. Closes `BL-1022`: nothing is
agreed before the session starts today, so each end picks its own map and aircraft, a disagreement
is silent, and a host's roster gives every remote seat the local pilot's airframe.

**What exists to build on.**
- `BL-1022`'s fix shape: the host announces the chapter and the match rules and each guest answers
  with its airframe index into `UI/PlanePickerRoster.StockAirframes`, which both ends read in one
  order, applied before the session is built; the index is the wire contract.
- B14's match state: `MatchStateMessage` (the time limit and score target) and the scoreboard, which the
  Game Scores tab shows. `Launcher.cs`'s net roster (`NetSeats`, `NetAirframes` over
  `StockAirframes`) and `LaunchExit.Net`.
- The decoded lobby messages (`docs/org/multiplayer-messages.md`): `0x1a` a setting change with a
  subtype word, `0x27` the lobby roster, `0x15` chat, `0x18` a lobby notice.
- F51's Connection page, which opens this lobby for the host and lands a Dogfight guest here.
- No lives rule exists: `AircraftLifecycle.AutoRespawnAfter` is the only respawn setting, and
  nothing counts lives.
- The four `OriginalScreenshots/Multiplayer Lobby *.png` shots and `Multiplayer Lobby Select Ammo
  Rockets.png`.

**Evidence (confidence: lead-only).** The layout is seen, not decoded (Wave F's preamble). The
defaults read off the Mission Options shot (Time 10 minutes, Score 40, teams 2 to 2). What Limited
Lives counts and how the original stops a pilot with none left are not decoded; read the `0x1a`
subtypes and the lobby's setting variables before building the rule.

**Approach.** All four tabs rebuilt in the original's layout, with a presentation-neutral lobby
model under them.
- **Mission Options, live:** Mission Environment (the maps Dogfight supports); Mission Type set to
  Dogfight, with Capture the Flag and the zeppelin modes listed but greyed, and the type's
  description text from the original's strings; Victory Conditions (Time in minutes or Score, B14's
  limits); Lives (Limited Lives and Auto Respawn).
- **Greyed:** Allow Custom Planes and Outlaw Components (everyone flies stock planes), Restrict
  Number of Teams and Create Team (free-for-all Dogfight), and Boot (GitHub issue #24).
- **Select Plane:** Default Planes live over the stock list with the stats and the gun and
  hardpoint census the shot shows, read from the airframe; Custom Planes greyed.
- **Select Ammo:** the Guns and Rockets tabs, a shell type per gun calibre and a rocket type per
  hardpoint. The pick rides with the plane pick into every peer's roster.
- **Game Scores:** B14's scoreboard of the last match.
- **The roster and Launch:** `Players (n of 16)` with a Ready box per pilot; Launch waits for every
  pilot's Ready (the Ready roster C24 also needs; see the dependency notes).
- **Chat:** Send puts one line on the wire as one reliable message, bounded in length, relayed by
  the host (Decision 9).
- **Leave Game** returns to the Connection page.
- **The wire:** host-to-all options, a guest's pick, Ready and chat, as new messages (the next free
  ids at the time), modelled on the decoded lobby set. The launch carries the agreed map, rules and
  every seat's airframe and ammo on `LaunchExit.Net`, and the launcher builds the net roster from
  the picks, not the local pilot's airframe.

**Model recommendation.** Opus. Four screens built from screenshots alone, a new message family,
the launcher's roster, and a lives rule in the versus match that has to be decoded first.

**Verify.**
- Units for the lobby model: an option set by a guest is refused; a greyed option cannot be set;
  Launch is refused until every pilot is Ready and a disconnect drops out of the count; round trips
  in `NetMessagesTests`; D33's fuzz covers the new readers through its enumeration.
- A new engine suite (`menu-original-lobby`) over a loopback pair: the host sets a map, Time 5 and
  Limited Lives and the guest reads them; the guest picks the second stock plane and a non-default
  shell; the launched host's roster builds the guest's seat on that airframe (the able-to-fail
  control: with the pick withheld it takes the local pilot's airframe, today's `BL-1022`); one chat
  line arrives once on each end; Leave Game lands on the Connection page.
- A two-session engine suite for the lives rule: with Limited Lives, a pilot's respawns stop after
  the last life on both ends, and with Auto Respawn off, a respawn waits for the pilot.
- Montages of the four tabs beside the original's shots, for the user's look judgement.
- At the controls: two machines, one Dogfight from the lobby to the match end.

**⚠ Traps.**
- The stock airframe index is the contract; a reordering of `StockAirframes` is a wire break.
- Where every peer lands after the match (the lobby's Game Scores tab, as the original suggests, or
  the Connection page) is not settled; ask the user before building it.
- The name `NetLobby` is taken by C25's carrier listener; the lobby screen's model needs another
  name.
- A guest's option controls are greyed, not hidden, as in the original, and a guest's edit never
  reaches the wire.
