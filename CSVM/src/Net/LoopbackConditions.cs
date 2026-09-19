using System;

namespace CSVM.Net;

/// <summary>
/// One direction's wire conditions for a <see cref="LoopbackTransport"/> link: a fixed latency, a
/// symmetric jitter about it, and a loss probability. Every draw comes from a caller-supplied
/// <see cref="Random"/> rather than an ambient one, so a suite that seeds the generator replays
/// the same network exactly. Loss is offered here for all three reliability classes; which ones
/// it may actually touch is the transport's rule, not this value's.
/// </summary>
public readonly struct LoopbackConditions
{
    /// <summary>No delay, no jitter, no loss: the link a correctness test runs on.</summary>
    public static readonly LoopbackConditions Perfect = default;

    public LoopbackConditions(double latency, double jitter, double loss)
    {
        if (latency < 0.0 || double.IsNaN(latency))
        {
            throw new ArgumentOutOfRangeException(nameof(latency), latency, "latency is seconds and cannot be negative");
        }

        if (jitter < 0.0 || double.IsNaN(jitter))
        {
            throw new ArgumentOutOfRangeException(nameof(jitter), jitter, "jitter is a symmetric half-width in seconds");
        }

        if (loss < 0.0 || loss > 1.0 || double.IsNaN(loss))
        {
            throw new ArgumentOutOfRangeException(nameof(loss), loss, "loss is a probability in [0, 1]");
        }

        Latency = latency;
        Jitter = jitter;
        Loss = loss;
    }

    /// <summary>Seconds every payload waits before it is due.</summary>
    public double Latency { get; }

    /// <summary>The half-width, in seconds, of the uniform spread about <see cref="Latency"/>.</summary>
    public double Jitter { get; }

    /// <summary>The chance in [0, 1] that a payload is thrown away at the sender.</summary>
    public double Loss { get; }

    /// <summary>How long this payload waits, never less than zero however wide the jitter.</summary>
    public double Delay(Random rng)
    {
        double spread = Jitter > 0.0 ? ((rng.NextDouble() * 2.0) - 1.0) * Jitter : 0.0;
        return Math.Max(0.0, Latency + spread);
    }

    /// <summary>Whether this payload is lost. Drawn only for the classes the transport allows loss
    /// on, so a reliable stream costs no draw and cannot shift a seeded run.</summary>
    public bool Drops(Random rng) => Loss > 0.0 && rng.NextDouble() < Loss;
}
