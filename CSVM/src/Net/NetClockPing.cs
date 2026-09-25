using System;

namespace CSVM.Net;

/// <summary>
/// The round trip a guest's <see cref="NetClockSlew"/> reads the link latency from, and the
/// periodic reading of the host's clock every session kind gets from it. A guest asks the host
/// what its clock reads; the host answers at once with the question's own stamp. The guest then
/// knows how long the question and its answer took together, and reads the host's clock forward
/// by half of that. Modelled on the original's <c>0x23</c> ping, whose timer and stamps are in
/// <c>docs/org/multiplayer-messages.md</c>. Engine-free: every clock is handed in, and every step
/// is the caller's.
/// </summary>
public sealed class NetClockPing
{
    /// <summary>Simulation steps between two answered questions: ten seconds at the fixed step,
    /// the original's own per-peer ping timer.</summary>
    public const int IntervalSteps = 600;

    /// <summary>Simulation steps a question waits for its answer before it is asked again. A
    /// question is unreliable, so a lost one would otherwise cost a whole interval. TUNE.</summary>
    public const int RetrySteps = 60;

    private readonly NetSession _net;
    private readonly NetClockSlew? _slew;
    private readonly Func<double> _clock;
    private int _wait;
    private double _newestAsked = double.NegativeInfinity;

    private NetClockPing(NetSession net, NetClockSlew? slew, Func<double> clock)
    {
        _net = net;
        _slew = slew;
        _clock = clock;
    }

    /// <summary>Questions this guest has asked. A host's end asks none.</summary>
    public int Asked { get; private set; }

    /// <summary>On a guest, answers taken into its slew; an answer overtaken by a newer one is not
    /// counted, since the newer one already said more. On the host, questions answered, which is
    /// the arrivals its relay must leave alone.</summary>
    public int Answered { get; private set; }

    /// <summary>Makes <paramref name="net"/>, a host, answer every question with
    /// <paramref name="hostClock"/> as it reads at arrival. Nothing is sent until a question
    /// arrives, and <see cref="Step"/> on the end this returns does nothing.</summary>
    public static NetClockPing Answer(NetSession net, Func<double> hostClock)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(hostClock);
        if (!net.IsHost)
        {
            throw new InvalidOperationException("only the host answers a clock question");
        }

        var ping = new NetClockPing(net, null, hostClock);
        net.On<ClockPingMessage>((peer, ask) =>
        {
            net.Send(peer, ask with { HostClock = (float)hostClock() }, NetChannels.Events);
            ping.Answered++;
        });
        return ping;
    }

    /// <summary>Makes <paramref name="net"/>, a guest, ask the host about its clock on
    /// <see cref="Step"/>. Every answer goes to <paramref name="slew"/>.
    /// <paramref name="guestClock"/> is this guest's own session clock, the one the slew's offset
    /// is added to.</summary>
    public static NetClockPing Follow(NetSession net, NetClockSlew slew, Func<double> guestClock)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(slew);
        ArgumentNullException.ThrowIfNull(guestClock);
        if (net.IsHost)
        {
            throw new InvalidOperationException("a host's clock is the shared one and asks nobody");
        }

        var ping = new NetClockPing(net, slew, guestClock);
        net.On<ClockPingMessage>((_, answer) => ping.Take(answer));
        return ping;
    }

    /// <summary>One simulation step. A guest's first one asks, so it holds a measured round trip
    /// one link's worth after it starts flying.</summary>
    public void Step()
    {
        if (_slew is null || _net.HostPeer == NetSession.NoPeer || _wait-- > 0)
        {
            return;
        }

        _net.Send(_net.HostPeer, new ClockPingMessage((float)_clock()), NetChannels.Events);
        Asked++;
        _wait = RetrySteps - 1;
    }

    // An answer older than one already taken was overtaken in flight, and one stamped later than
    // this clock reads was never asked here. Neither is a round trip. A stamp is a float, which
    // may round past a clock that has not moved since, so that one reading is taken as now.
    private void Take(in ClockPingMessage answer)
    {
        double now = _clock();
        if (answer.AskedClock > now && answer.AskedClock != (float)now)
        {
            return;
        }

        double asked = Math.Min(answer.AskedClock, now);
        if (asked <= _newestAsked)
        {
            return;
        }

        _newestAsked = asked;
        _slew!.ObserveRoundTrip(asked, answer.HostClock, now);
        Answered++;
        _wait = IntervalSteps - 1;
    }
}
