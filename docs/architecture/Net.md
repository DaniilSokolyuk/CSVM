# Net

The network seam: what carries bytes between peers, and the in-process carrier the suites run on.
No type here names an engine type or a socket, which is what lets one session run over the
loopback in a plain unit test and over a real carrier in a match; the boundary is asserted over
compiled metadata by `CSVM.Tests/NetNamespaceDependencyTests.cs`. Nothing about the world crosses
this seam, and the message vocabulary sits entirely above it.

One `## src/...` entry per module, body at most 8 lines.

Traps do not live here; the rule is in `docs/architecture.md`.

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
