using System;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The shared clock's round trip over a loopback with injected latency. A host and a guest session
/// run in lockstep, the host's clock a fixed lead ahead of the guest's. The guest's host time is
/// read against the host's real clock, with the round trip and without it. A match-state tick
/// feeds one-way readings in both runs, as a live match does.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class NetClockPingTests
{
    private const double Dt = 1.0 / 60.0;

    // Not a whole number of steps, so no delivery lands on a rounding crumb of the step clock.
    private const double Latency = 0.105;

    private const double HostLead = 50.0;
    private const int TickSteps = 60;
    private const int RunSteps = 600;

    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    // The lockstep order costs the answer's leg one step against the question's, so half a step
    // is the harness's own asymmetry. The bound is one step.
    [Fact]
    public void With_the_round_trip_the_guest_reads_the_hosts_clock_within_a_step()
    {
        var run = Run(new LoopbackConditions(Latency, 0.0, 0.0), ping: true);

        Assert.InRange(Math.Abs(run.Error), 0.0, Dt);
        Assert.InRange(run.Slew.RoundTrip, 2.0 * Latency, (2.0 * Latency) + (2.0 * Dt));
        Assert.True(run.Ping!.Answered >= 1, $"{run.Ping.Answered} answers taken");
        Assert.Equal(run.Ping.Asked, run.Answerer!.Answered);
    }

    // ABLE-TO-FAIL CONTROL. The same link and the same ticks with no question asked. Every reading
    // is one-way, so the offset holds the host's clock minus the latency.
    [Fact]
    public void Without_it_the_guest_reads_the_hosts_clock_one_latency_behind()
    {
        var run = Run(new LoopbackConditions(Latency, 0.0, 0.0), ping: false);

        Assert.InRange(run.Error, -Latency - (2.0 * Dt), -Latency + Dt);
        Assert.Equal(0, run.Slew.RoundTrips);
    }

    // A lost question is asked again after RetrySteps rather than a whole interval later, and the
    // jitter's uneven legs stay inside two steps.
    [Fact]
    public void A_lossy_jittery_link_still_lands_within_two_steps()
    {
        var run = Run(new LoopbackConditions(Latency, 0.01, 0.25), ping: true);

        Assert.InRange(Math.Abs(run.Error), 0.0, 2.0 * Dt);
        Assert.True(run.Ping!.Answered >= 1 && run.Ping.Asked >= run.Ping.Answered,
            $"asked {run.Ping.Asked}, answered {run.Ping.Answered}");
    }

    [Fact]
    public void Only_a_host_answers_and_only_a_guest_asks()
    {
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(3));
        var host = NetSession.Host(mesh[0], Roster(), 7UL, null, Airframes);
        var guest = NetSession.Guest(mesh[1], Airframes);

        Assert.Throws<InvalidOperationException>(() => NetClockPing.Answer(guest, () => 0.0));
        Assert.Throws<InvalidOperationException>(() => NetClockPing.Follow(host, new NetClockSlew(0.0), () => 0.0));

        int sent = host.Sent;
        var answerer = NetClockPing.Answer(host, () => 0.0);
        answerer.Step();
        Assert.Equal(sent, host.Sent);
        Assert.Equal(0, answerer.Asked);
    }

    private static Result Run(LoopbackConditions link, bool ping)
    {
        var mesh = LoopbackTransport.Mesh(2, link, new Random(4127));
        double t = 0.0;
        var host = NetSession.Host(mesh[0], Roster(), 7UL, () => HostLead + t, Airframes);
        var guest = NetSession.Guest(mesh[1], Airframes);
        for (int i = 0; i < 600 && !guest.Joined; i++)
        {
            host.Step(Dt);
            guest.Step(Dt);
            t += Dt;
        }

        Assert.True(guest.Joined);

        // The guest's session clock reads t, so its opening offset is the handshake's one-way one.
        var slew = new NetClockSlew(guest.Handshake.HostClock - t);
        guest.On<MatchStateMessage>((_, state) => slew.Observe(state.HostClock, t));
        NetClockPing? asker = null;
        NetClockPing? answerer = null;
        if (ping)
        {
            answerer = NetClockPing.Answer(host, () => HostLead + t);
            asker = NetClockPing.Follow(guest, slew, () => t);
        }

        for (int step = 0; step < RunSteps; step++)
        {
            host.Step(Dt);
            guest.Step(Dt);
            asker?.Step();
            if (step % TickSteps == 0)
            {
                host.Broadcast(new MatchStateMessage(0f, 0f, 0, NetMatchEnd.Running, (float)(HostLead + t)));
            }

            slew.Advance(Dt);
            t += Dt;
        }

        return new Result(slew.HostTime(t) - (HostLead + t), slew, asker, answerer);
    }

    private static NetSeat[] Roster() => new NetSeat[]
    {
        new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
        new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
    };

    private sealed record Result(double Error, NetClockSlew Slew, NetClockPing? Ping, NetClockPing? Answerer);
}
