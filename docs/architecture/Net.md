# Net

The network seam: what carries bytes between peers, the in-process carrier the suites run on, the
ENet carrier a match ships over, and the one place a build picks between them. Only the ENet
carrier and the port mapping name an engine type beyond Godot's plain math structs, and nothing
here names a socket API, which is what lets one session run over the loopback in a plain unit test
and over ENet in a match; the boundary and its exemptions are asserted over compiled metadata by
`CSVM.Tests/NetNamespaceDependencyTests.cs`, which also holds every Steam name to the Steam
carrier. Nothing about the world crosses this seam, and the message vocabulary sits entirely above
it.

One `## src/...` entry per module, body at most 8 lines.

Traps do not live here; the rule is in `docs/architecture.md`. What the original sends, with its
type ids, payloads and guarantees, is in [../org/multiplayer-messages.md](../org/multiplayer-messages.md).

## src/Net/INetTransport.cs
The seam itself, and the two types it is spoken in. `NetReliability` is the three classes a
payload can be sent under, `INetTransportListener` is what a transport tells its owner (a peer
joined, a peer left, a payload landed), and `INetTransport` is the carrier: the peer roster,
`Send` of a byte span with its class and channel, `Bind` of the one listener, `Disconnect`, and
`Step`, which is the only place a payload is ever delivered. A session holds the interface and
constructs neither implementation itself. Read `LoopbackTransport.cs` for the carrier the suites
use.

## src/Net/LoopbackConditions.cs
One direction's wire conditions as a value: a latency, a symmetric jitter half-width about it, and
a loss probability, all in seconds and all validated at construction. `Delay` and `Drops` are the
two draws, taken from a caller-supplied `Random` rather than an ambient one, so a seeded suite
replays the same network exactly and a reliable stream costs no loss draw. Which classes loss may
touch is the transport's rule, not this value's.

## src/Net/LoopbackTransport.cs
Two or more transports wired to each other in one process through delivery queues, one
`LoopbackConditions` per direction, changeable mid-run through `SetConditions` so a suite makes a
reorder instead of waiting for the jitter to draw one. `Mesh` builds the set and links it; nothing
arrives until `Step` advances that end's own clock, which is what gives a test delivery time.
The guarantees are enforced, not imitated: loss is drawn only for the unreliable classes, a
reliable stream's deadlines are held monotonic per sender, and a sequenced payload at or below
the newest delivered on its channel is discarded. `Lost` (sender) and `DiscardedStale` (receiver)
count both, the truth `NetInstruments`' gap count is checked against. Read `LoopbackTransportTests.cs`.

## src/Net/EnetTransport.cs
The shipped carrier: the seam over Godot's ENet peer, UDP under ENet's own three delivery classes,
and the one type under `CSVM/` allowed to name a Godot networking type. `Host` opens a listen
server, `Join` reports success as the host joining the roster, and both ends address each other by
the id ENet assigns, the host being 1. Every roster change and payload comes out of `Step`, the
single poll it makes, so "nothing arrives between steps" holds here as on the loopback.
`ChannelCount` is the events channel plus one per seat, asked for by both ends, since ENet fixes
it at the handshake and refuses a send past it. `INetLink` is where a board and a launcher read
the socket, and a socket with no listener holds what lands and replays it on `Bind`.

## src/Net/SteamTransport.cs
The Steam carrier's place in the seam with nothing behind it: the Steamworks SDK cannot be
committed here under its licence, so every way in throws "not built with the Steamworks SDK" and
says which of the two cases the build is. `SteamBuild` is the `CSVM_STEAM` define, set by the
`CsvmSteam` build property, and the only thing that define changes. The throw is an
`InvalidOperationException`, the kind the door and the launcher already catch from a socket that
will not open, so a Steam build reaches a board as a line of text rather than a crash. Read
`NetCarrier.cs` for where it is chosen.

## src/Net/NetCarrier.cs
Which carrier a match runs over, chosen once: the menu door's registration in
`Session/Launcher.cs` and the command line's own open both come through `Host` and `Join` here, so
a build changes carrier without an edit above the seam. `UsesSteam` is the switch and `Name` is
the word for a log line. `PortMap` and `PortUnmap` are the router door a direct-IP host asks for,
and are null for a carrier that is reachable without one, which a door shows as no mapping.
⚠ Nothing above the seam branches on the carrier.

## src/Net/UpnpPortMap.cs
A best-effort port mapping through Godot's UPnP client, so a host behind a router is reachable
from outside it. `Map` returns one of four outcomes a board can show (mapped, no gateway, refused,
timed out) with the external address when one was learned, `Unmap` takes it back down, and neither
throws: a refused mapping costs a host nothing but a line on the board, and a guest on the same
network still joins. Both calls block for the length of the gateway search, so they belong at the
moment hosting opens and closes, never on a frame and never in a transport step.

## src/Net/NetLobby.cs
A carrier's first listener, standing between the socket a menu opens and the session that later
binds it, since a carrier binds only once. It is itself the `INetTransport` the session binds. A
host's `Advertise` sends a `SessionAdvertMessage` to every peer on connect and on each change; a
guest keeps the latest arrival in `Advert` and never passes one on. Any other payload is held (up
to `HeldPayloads`) until a session binds, then replayed behind the peer announcement, so `Held` is
how a guest's board learns that the host's session has answered. Read `NetLobbyTests.cs`.

## src/Net/NetMessages.cs
The vocabulary: `NetMessageType` (one word per message), the death, spawn and match-end enums
taken from the original's own values, `NetDirectorEvent` (the director message's codes and id
layouts), `NetWorldEvent` (the world event's codes), `NetSessionKind`, and the message structs, the host's spawn grant, a seat's ask and the lobby's `SessionAdvertMessage` among them. Each is a value type implementing `INetMessage<TSelf>`,
which carries its type word and its `INetTransport.cs` reliability class as static abstracts, so
a sender reads the class off the type without constructing anything. `NetMessage` holds what they
share: the four-byte header, the no-seat and no-spawn-entry markers, the aircraft-state width
budget, `ReliabilityOf`, `IsOriginalId`, and `TryReadHeader`, the one call a receiver makes
before it knows which deserialiser to run. Read `NetMessageWriter.cs` next.

## src/Net/NetWorldMessages.cs
The host-owned world's four messages, beside the vocabulary rather than in it: `AiStateMessage`
(an AI's pose by admission ordinal, plain unreliable because every AI shares one channel, with
`AsAircraftState` for the pose buffer), `AiFireMessage`, `AiHitMessage` (a guest's claim on an AI,
to the host alone) and `WorldEventMessage`, whose `NetWorldEvent` code says what its subject,
argument and value carry. Their ids and the phase mapping they serve are
[../org/multiplayer-messages.md](../org/multiplayer-messages.md)'s.

## src/Net/NetMessageWriter.cs
The two cursors every serialiser and deserialiser runs on, `NetMessageWriter` and
`NetMessageReader`, kept in one file because they are one pair and drift apart if they are not.
Little-endian primitives over the caller's span, plus the two quantised forms the layouts need: a
unit-range field as a 16-bit integer, and a fixed-width UTF-8 field that truncates on a whole
character. The writer opens with the header and patches the total length in on `Close`; the
reader reads the header in its constructor, so `Type`, `Length` and `Valid` answer before any
payload byte is touched.
## src/Net/NetClockSlew.cs
How a guest holds its session clock against the host's, as one offset that is walked rather than
written: `HostTime(guest) = guest + Offset`, and a fresh `Observe` sets a target the offset
converges on over `ConvergeSeconds`, bounded by `MaxRateOffset` of real time, never overshooting.
A reading further out than `SnapSeconds` is applied at once and counted in `Snaps`, which is the
signal that the window is wrong rather than the link. Engine-free and clock-free: it is handed
every time it is told about, so `GameSession` needs no clock write and a unit suite drives it
whole. The three constants are TUNE (`BL-1018`).

## src/Net/NetHandshake.cs
What a host hands a joining guest before either flies: the master seed and the host's session
clock at send. The seed reaches `GameSession`'s constructor through `LauncherContext`, where it
replaces the launch's own master before `Rng.Reset` runs, which is what makes both peers draw the
same liveries, the same spawn walk and the same dice. The clock becomes the opening offset of the
guest's `NetClockSlew`. The record is what a session hands to and takes from the wire; the bytes
that carry it are the message vocabulary's.

## src/Net/NetSeat.cs
One pilot's place in a match, shaped like the record the original allocates per player: the peer it
is addressed by, its team, whether this machine flies it, its callsign, its airframe and paint, its
seat index and its signed score. `Color` reads the seat's own entry in `NetSeats`. The seat index is
the whole identity: a remote pilot indexes spawns, scores, markers and colours exactly as a
splitscreen pane does, which is why the session orders its rigs by it. Read
`docs/architecture/Session.md`'s `GameSession.cs` entry for where a seat becomes an aeroplane
without a pane.

## src/Net/NetSeats.cs
The roster's rules: `MaxPlayers = 16` pilots admitted, the count the original's lobby shows and
its data holds, every seat-indexed table built `SeatCapacity = 16` wide, each seat's identity
colour, and `Validate`, which requires seats numbered from zero with no gap and at least one flown here. `Field` builds a host's roster from its local planes and the peers on its wire.
Seats 0 to 7 take the original's authored dwords at `00628eb4` in order (the remake's index is
0-based where the original's was 1-based and its eighth pilot read past the table); seats 8 to 15
take the channel-wise complement of seat minus 8. The channel order and the derived eight are TUNE
(`BL-1017`).

## src/Net/RemotePoseBuffer.cs
One remote aircraft's received history, and the pose to draw it at now: `AircraftStateMessage`
samples go in stamped with the buffer's own clock, and a read gets the state
`BufferDelaySeconds` behind, interpolated between the two samples straddling it. Past the newest
sample the answer rides that sample's velocity for at most `ExtrapolationCapSeconds` and then
holds, and `RemotePoseFeed` names which case each answer came from. A sample at or below the
newest sequence is dropped, wrap included. `Tally` counts the owner's reads by feed, the stale
drops, and each sample's miss against the one before it flown on its velocity (the one position
error a machine reads without the owner); a miss past twice the sample's reach is a jump, counted apart.

## src/Net/AircraftStateCadence.cs
The send half of aircraft replication, and the only thing in it that is not the session's own
step: when an owner puts its aeroplane on the wire, counted in simulation steps, and what
sequence each sample carries, counted per seat because a receiver decides staleness by it. What a
sample holds is the session's to fill and what happens to it is `RemotePoseBuffer.cs`'s, so this
module knows neither. `SendStepInterval` is accepted as measured; at the fixed step it puts two send
intervals inside the buffer's own read-behind, which is what lets one lost sample still leave a
pair to read between. `SampleSeconds` is that interval in seconds, what a sequence gap is worth.

## src/Net/MatchStateCadence.cs
When a host repeats the match state, counted in simulation steps. It exists only for the clock:
every change that matters (the limits at the build, the rematch, the ending) is sent where it
happens, and the tick is what refreshes the remaining time and gives `NetClockSlew` the one
reading a running match repeats. `TickStepInterval` is 60, a second at the fixed step, which is
the rate the versus HUD's whole-second readout can show a difference at. TUNE (`BL-1025`). The
first step ticks, so a guest holds the host's limits inside one step of its build. What the
message carries is `GameSession`'s to fill. Read `docs/architecture/Session.md`'s entry for it.

## src/Net/NetChannels.cs
Which channel a message rides. Sequenced discard is per sender and channel, and a relayed sample
carries the host's peer id rather than its sender's, so two guests sharing one channel would
discard each other by sequence number: `ForSeat` gives every seat its own, and `Events` carries
the join and everything reliable, where nothing is discarded. A seat past the roster's ceiling
falls back to `Events`, which costs ordering rather than delivery.

## src/Net/NetSession.cs
The one object a session owns to talk to its peers: it holds the transport, implements the
listener, sends a typed message under the class the type declares, and routes an arrival to
the handler registered on its type word. The only meaning it knows is the join, a host answering each peer with the handshake (which names
the seat) and then the roster; `On` refuses those two types. The star's relay is here too:
`SendToSeat` addresses a seat through whoever owns it, and a host's `RelayToOthers` and
`RelayToSeatOwner` forward an arrival's own bytes, never back to its sender. `Sent`, `Received`,
`Relayed`, `DroppedUnknown` and `Malformed` are the counters a suite reads; `Instruments` is fed
every arrival before its handler and every send once, a broadcast included.

## src/Net/NetInstruments.cs
One machine's desync counters over its own traffic, engine-free, read by `net-soak` and the
`--debug-net` readout. Rules: a seat's first state or fire sample sets its ladder, and each later
gap counts as dropped; an arrival at or below the newest is stale; a hit or a burst for a seat
reported dead and not placed again is late; a score line adding deaths nobody reported, or a
respawn for a known seat nobody reported dead, is out of order. A seat's first score line and a
line whose deaths fall (a rematch) only set the baseline. Position error needs the owner's path
and is the soak's to measure. `Describe` writes the one readout line.
