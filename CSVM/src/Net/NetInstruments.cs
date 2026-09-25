using System;
using System.Globalization;

namespace CSVM.Net;

/// <summary>The six <see cref="NetInstruments"/> counters at one moment. Subtracting an earlier
/// reading gives what one stretch of a run added, which is how a soak reads one cell of its
/// matrix.</summary>
public readonly record struct NetInstrumentReading(
    int StateGaps, int FireGaps, int StaleArrivals, int LateHits, int LateFire, int OrderViolations)
{
    /// <summary>Everything that should have arrived and did not.</summary>
    public int Dropped => StateGaps + FireGaps;

    /// <summary>Everything that arrived after the moment it described.</summary>
    public int Late => StaleArrivals + LateHits + LateFire;

    public static NetInstrumentReading operator -(NetInstrumentReading a, NetInstrumentReading b) =>
        new(a.StateGaps - b.StateGaps, a.FireGaps - b.FireGaps, a.StaleArrivals - b.StaleArrivals,
            a.LateHits - b.LateHits, a.LateFire - b.LateFire, a.OrderViolations - b.OrderViolations);
}

/// <summary>
/// The desync counters one machine keeps over its own traffic, fed every payload that arrives and
/// every one this machine says itself. Engine-free and clock-free, so the soak suite, a unit test
/// and the <c>--debug-net</c> readout read one set of numbers. Each counter is judged from this
/// machine's view alone; position error needs the owner's own path and is the harness's to
/// measure. The rules are the order a Dogfight's events follow, listed in this module's entry in
/// <c>docs/architecture/Net.md</c>.
/// </summary>
public sealed class NetInstruments
{
    private const int StateStream = 0;
    private const int FireStream = 1;

    private readonly ushort[,] _newest = new ushort[2, NetSeats.SeatCapacity];
    private readonly bool[,] _streaming = new bool[2, NetSeats.SeatCapacity];
    private readonly bool[] _down = new bool[NetSeats.SeatCapacity];
    private readonly bool[] _known = new bool[NetSeats.SeatCapacity];
    private readonly bool[] _scored = new bool[NetSeats.SeatCapacity];
    private readonly int[] _scoredDeaths = new int[NetSeats.SeatCapacity];
    private readonly int[] _unscoredDeaths = new int[NetSeats.SeatCapacity];

    /// <summary>Aircraft-state samples that never reached this machine, read off the gaps in each
    /// seat's sequence. A lost sample and one the carrier discarded as overtaken read alike.
    /// </summary>
    public int StateGaps { get; private set; }

    /// <summary>Fire events that never reached this machine, read the same way off each seat's
    /// fire sequence.</summary>
    public int FireGaps { get; private set; }

    /// <summary>Sequenced payloads that arrived at or below the newest already seen from their
    /// seat. The carriers discard these, so anything above zero is a carrier breaking its class.
    /// </summary>
    public int StaleArrivals { get; private set; }

    /// <summary>Hit claims that arrived for a seat already reported dead and not yet placed again.
    /// The shooter aimed at a copy the death had not reached.</summary>
    public int LateHits { get; private set; }

    /// <summary>Fire events that arrived from a seat already reported dead and not yet placed
    /// again, sent before the death and delivered after it.</summary>
    public int LateFire { get; private set; }

    /// <summary>Reliable events seen out of their causal order. One is a score line moving a seat's
    /// deaths past those reported for it. The other is a respawn for a seat nobody reported dead.
    /// </summary>
    public int OrderViolations { get; private set; }

    /// <summary>Everything that should have arrived and did not.</summary>
    public int Dropped => StateGaps + FireGaps;

    /// <summary>Everything that arrived after the moment it described.</summary>
    public int Late => StaleArrivals + LateHits + LateFire;

    /// <summary>The counters as they stand, for a delta or a log line.</summary>
    public NetInstrumentReading Reading =>
        new(StateGaps, FireGaps, StaleArrivals, LateHits, LateFire, OrderViolations);

    /// <summary>The one line the <c>--debug-net</c> readout shows and logs: the session's own
    /// counters, these, and the tally of every remote aeroplane's buffer on this machine.
    /// </summary>
    public static string Describe(NetSession net, RemotePoseTally poses)
    {
        ArgumentNullException.ThrowIfNull(net);
        var r = net.Instruments.Reading;
        return string.Create(CultureInfo.InvariantCulture,
            $"net {(net.IsHost ? "host" : "guest")} seat {net.LocalSeat} peers {net.Peers.Count}: sent {net.Sent} recv {net.Received} relayed {net.Relayed} unknown {net.DroppedUnknown} malformed {net.Malformed} | dropped state {r.StateGaps} fire {r.FireGaps} | late stale {r.StaleArrivals} hits {r.LateHits} fire {r.LateFire} | order {r.OrderViolations} | poses interp {poses.Interpolating} extrap {poses.Extrapolating} starved {poses.Starved} jumps {poses.Jumps} | extrap err {poses.MeanExtrapolationError:0.00} m mean {poses.WorstExtrapolationError:0.00} m worst");
    }

    /// <summary>One payload the transport delivered here, read before any handler runs so an
    /// unclaimed type still counts. A payload that does not parse is the session's to count.
    /// </summary>
    public void Arrived(ReadOnlySpan<byte> payload)
    {
        if (!NetMessage.TryReadHeader(payload, out var type, out _))
        {
            return;
        }

        switch (type)
        {
            case NetMessageType.AircraftState when AircraftStateMessage.TryRead(payload, out var state):
                Stream(StateStream, state.Seat, state.Sequence);
                break;
            case NetMessageType.Fire when FireMessage.TryRead(payload, out var fire):
                if (IsSeat(fire.Seat) && _down[fire.Seat])
                {
                    LateFire++;
                }

                Stream(FireStream, fire.Seat, fire.Sequence);
                break;
            case NetMessageType.Hit when HitMessage.TryRead(payload, out var hit):
                if (IsSeat(hit.VictimSeat) && _down[hit.VictimSeat])
                {
                    LateHits++;
                }

                break;
            default:
                Lifecycle(type, payload);
                break;
        }
    }

    /// <summary>One payload this machine sent itself. Only a seat's life counts here: a death this
    /// machine reported, a spawn it granted, a score it wrote. Its own streams are the far end's
    /// to judge.</summary>
    public void Said(ReadOnlySpan<byte> payload)
    {
        if (NetMessage.TryReadHeader(payload, out var type, out _))
        {
            Lifecycle(type, payload);
        }
    }

    private static bool IsSeat(byte seat) => seat < NetSeats.SeatCapacity;

    // A seat's first sample only sets where its ladder stands. A guest that joins a running match
    // meets it mid-count, and nothing before that was ever addressed to it.
    private void Stream(int stream, byte seat, ushort sequence)
    {
        if (!IsSeat(seat))
        {
            return;
        }

        if (!_streaming[stream, seat])
        {
            _streaming[stream, seat] = true;
            _newest[stream, seat] = sequence;
            return;
        }

        // Wrap-safe, the same half-range horizon the pose buffer uses.
        ushort gap = (ushort)(sequence - _newest[stream, seat]);
        if (gap == 0 || gap >= 0x8000)
        {
            StaleArrivals++;
            return;
        }

        if (stream == StateStream)
        {
            StateGaps += gap - 1;
        }
        else
        {
            FireGaps += gap - 1;
        }

        _newest[stream, seat] = sequence;
    }

    private void Lifecycle(NetMessageType type, ReadOnlySpan<byte> payload)
    {
        switch (type)
        {
            case NetMessageType.Death when DeathMessage.TryRead(payload, out var death)
                                          && IsSeat(death.VictimSeat):
                _down[death.VictimSeat] = true;
                _known[death.VictimSeat] = true;
                _unscoredDeaths[death.VictimSeat]++;
                break;
            case NetMessageType.Spawn when SpawnMessage.TryRead(payload, out var spawn)
                                          && IsSeat(spawn.Seat):
                // A seat this machine has heard nothing about may have died before it joined.
                if (spawn.Kind == NetSpawnKind.Respawn && _known[spawn.Seat] && !_down[spawn.Seat])
                {
                    OrderViolations++;
                }

                _down[spawn.Seat] = false;
                _known[spawn.Seat] = true;
                break;
            case NetMessageType.Score when ScoreMessage.TryRead(payload, out var score)
                                          && IsSeat(score.Seat):
                Scored(score.Seat, score.Deaths);
                break;
        }
    }

    // A seat's first line, and a line whose deaths went down (a rematch), only set the baseline.
    // Past it, every death a line adds must already have been reported here.
    private void Scored(byte seat, int deaths)
    {
        if (!_scored[seat] || deaths < _scoredDeaths[seat])
        {
            _scored[seat] = true;
            _scoredDeaths[seat] = deaths;
            _unscoredDeaths[seat] = 0;
            return;
        }

        int added = deaths - _scoredDeaths[seat];
        if (added > _unscoredDeaths[seat])
        {
            OrderViolations += added - _unscoredDeaths[seat];
            _unscoredDeaths[seat] = 0;
        }
        else
        {
            _unscoredDeaths[seat] -= added;
        }

        _scoredDeaths[seat] = deaths;
    }
}
