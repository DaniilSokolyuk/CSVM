# Net

The network seam: what carries bytes between peers, and the in-process carrier the suites run on.
No type here names an engine type beyond Godot's plain math structs, nor a socket, which is what
lets one session run over the loopback in a plain unit test and over a real carrier in a match;
the boundary is asserted over compiled metadata by `CSVM.Tests/NetNamespaceDependencyTests.cs`. Nothing about the world crosses
this seam, and the message vocabulary sits entirely above it.

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
reliable stream's deadlines are held monotonic per sender so jitter cannot reorder it, and a
sequenced payload at or below the newest already delivered on its channel is discarded. Read
`CSVM.Tests/LoopbackTransportTests.cs` for the contract in assertions.

## src/Net/NetMessages.cs
The vocabulary: `NetMessageType` (one word per message), the death, spawn and match-end enums
taken from the original's own values, and the ten message structs. Each is a value type
implementing `INetMessage<TSelf>`, which carries its type word and its `INetTransport.cs`
reliability class as static abstracts, so a sender reads the class off the type without
constructing anything. `NetMessage` holds what they share: the four-byte header, the no-seat
marker, the aircraft-state width budget, `ReliabilityOf`, `IsOriginalId`, and `TryReadHeader`,
the one call a receiver makes before it knows which deserialiser to run. Read
`NetMessageWriter.cs` next.

## src/Net/NetMessageWriter.cs
The two cursors every serialiser and deserialiser runs on, `NetMessageWriter` and
`NetMessageReader`, kept in one file because they are one pair and drift apart if they are not.
Little-endian primitives over the caller's span, plus the two quantised forms the layouts need: a
unit-range field as a 16-bit integer, and a fixed-width UTF-8 field that truncates on a whole
character. The writer opens with the header and patches the total length in on `Close`; the
reader reads the header in its constructor, so `Type`, `Length` and `Valid` answer before any
payload byte is touched.
