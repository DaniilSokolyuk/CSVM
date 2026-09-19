using System;
using Godot;

namespace CSVM.Net;

/// <summary>Which of the three cases a <see cref="RemotePoseBuffer"/> answer came out of, so an
/// instrument can count them without re-deriving the decision. Interpolating is the healthy case.
/// Extrapolating means the newest sample is older than the render time, so the answer rides that
/// sample's velocity. Starved means the buffer cannot cover the render time at all, and holds the
/// nearest sample it has.</summary>
public enum RemotePoseFeed
{
    /// <summary>The buffer holds the nearest sample rather than guessing further.</summary>
    Starved,

    /// <summary>Two samples straddle the render time and the answer is between them.</summary>
    Interpolating,

    /// <summary>The newest sample is older than the render time; the answer rides its velocity.</summary>
    Extrapolating,
}

/// <summary>One aircraft's state at one moment, as the buffer reconstructs it from the samples
/// around that moment. The surface deflections carry the sender's own stick, the animator's feed,
/// and are not re-derived from the pose.</summary>
public readonly record struct RemotePose(
    RemotePoseFeed Feed,
    Vector3 Position,
    Quaternion Attitude,
    Vector3 Velocity,
    float Throttle,
    float Aileron,
    float Elevator,
    float Rudder,
    bool Nitro);

/// <summary>
/// The received history of one remote aircraft, and the pose to draw it at now. Samples go in
/// with the local time they arrived at. A read asks for a render time and gets the state
/// <see cref="BufferDelaySeconds"/> behind it, so ordinary jitter lands between two samples that
/// are already here. Engine-free and clock-free: the owner advances <see cref="Now"/> by its own
/// step. That is what lets a suite drive a whole flight with no session in the process. A sample
/// at or below the newest sequence is dropped, wrap included, so a reordered delivery cannot walk
/// the aircraft backwards.
/// </summary>
public sealed class RemotePoseBuffer
{
    /// <summary>How far behind the render time the buffer reads, seconds. Big enough to hide the
    /// gap between two sends plus the jitter on them. Small enough that the aircraft is not shown
    /// meaningfully in its own past. TUNE: measured on the two-session harness under the loss
    /// model, not set by feel.</summary>
    public const float BufferDelaySeconds = 0.1f;

    /// <summary>How far past its newest sample the buffer will ride a velocity, seconds. Past it
    /// the answer holds rather than flying an aircraft nobody is steering any more. TUNE: measured
    /// beside <see cref="BufferDelaySeconds"/> on the same harness.</summary>
    public const float ExtrapolationCapSeconds = 0.25f;

    /// <summary>How many samples are kept. At the send rates in question this is seconds of
    /// history. That is far more than a read reaches back for, so the oldest is evicted and not
    /// missed.</summary>
    public const int Capacity = 32;

    private readonly (double Time, AircraftStateMessage Sample)[] _entries =
        new (double, AircraftStateMessage)[Capacity];

    private int _start;          // ring index of the oldest entry
    private int _count;
    private ushort _newestSequence;
    private bool _seenAny;       // whether _newestSequence means anything yet

    /// <summary>The buffer's own receive clock, in seconds, advanced by its owner's sim step. An
    /// arrival is stamped with it and a read is asked for it. Both sides then share one scale,
    /// with no session clock reachable from here.</summary>
    public double Now { get; private set; }

    /// <summary>How many samples are held, at most <see cref="Capacity"/>.</summary>
    public int Count => _count;

    /// <summary>The newest sequence accepted, meaningless until one has been.</summary>
    public ushort NewestSequence => _newestSequence;

    /// <summary>Moves <see cref="Now"/> on by one step of the owner's clock.</summary>
    public void Advance(float dt) => Now += dt;

    /// <summary>Takes one sample that arrived just now. False when it was dropped as stale.</summary>
    public bool Receive(in AircraftStateMessage sample) => Add(sample, Now);

    /// <summary>Takes one sample stamped at <paramref name="time"/> on the buffer's own scale.
    /// False when its sequence is at or below the newest accepted, which is a duplicate or a
    /// reordered delivery and is dropped rather than applied. A stamp behind the newest held is
    /// pulled up to it, so the history a read walks stays in order.</summary>
    public bool Add(in AircraftStateMessage sample, double time)
    {
        if (_seenAny && !IsNewer(sample.Sequence, _newestSequence))
            return false;

        if (_count > 0)
        {
            double newestTime = _entries[Index(_count - 1)].Time;
            if (time < newestTime)
                time = newestTime;
        }

        int slot;
        if (_count < Capacity)
        {
            slot = Index(_count);
            _count++;
        }
        else
        {
            slot = _start;
            _start = (_start + 1) % Capacity;
        }

        _entries[slot] = (time, sample);
        _newestSequence = sample.Sequence;
        _seenAny = true;
        return true;
    }

    /// <summary>Forgets every sample and the sequence with them. That is what a placement the wire
    /// did not cause (a spawn, a respawn) owes. The samples before it describe an aircraft that is
    /// no longer where they say.</summary>
    public void Clear()
    {
        _start = 0;
        _count = 0;
        _seenAny = false;
        _newestSequence = 0;
    }

    /// <summary>The state to draw at <see cref="Now"/>.</summary>
    public bool TrySample(out RemotePose pose) => TrySample(Now, out pose);

    /// <summary>The state to draw at <paramref name="renderTime"/>, which the buffer answers
    /// <see cref="BufferDelaySeconds"/> behind. False only when there is no sample at all to build
    /// an answer from. Every other case answers, and <see cref="RemotePose.Feed"/> says which of
    /// the three it was.</summary>
    public bool TrySample(double renderTime, out RemotePose pose)
    {
        pose = default;
        if (_count == 0)
            return false;

        double target = renderTime - BufferDelaySeconds;
        var oldest = _entries[_start];
        if (target <= oldest.Time)
        {
            // Nothing that old is held, so there is nothing to interpolate from: the aircraft
            // waits at its oldest known state rather than being guessed backwards.
            pose = At(oldest.Sample, RemotePoseFeed.Starved);
            return true;
        }

        var newest = _entries[Index(_count - 1)];
        if (target >= newest.Time)
        {
            double age = target - newest.Time;
            bool capped = age > ExtrapolationCapSeconds;
            float flown = (float)(capped ? ExtrapolationCapSeconds : age);
            var held = At(newest.Sample,
                capped ? RemotePoseFeed.Starved : RemotePoseFeed.Extrapolating);
            pose = held with { Position = held.Position + (held.Velocity * flown) };
            return true;
        }

        // Backwards from the newest: the first entry at or before the target is the older half of
        // the straddling pair. Every entry after that one is later than the target.
        for (int i = _count - 1; i >= 1; i--)
        {
            var a = _entries[Index(i - 1)];
            if (a.Time > target)
                continue;
            var b = _entries[Index(i)];
            double span = b.Time - a.Time;
            float t = span > 0.0 ? (float)((target - a.Time) / span) : 1f;
            pose = Between(a.Sample, b.Sample, t);
            return true;
        }

        pose = At(newest.Sample, RemotePoseFeed.Starved);
        return true;
    }

    // Wrap-safe "is a newer than b": the sequence is 16 bits and rolls over. A plain comparison
    // would reject the whole wrap and freeze the aircraft. Half the range is the horizon.
    private static bool IsNewer(ushort a, ushort b)
    {
        ushort gap = (ushort)(a - b);
        return gap != 0 && gap < 0x8000;
    }

    // Quantised components come off the wire slightly off unit length, and Godot's Slerp refuses a
    // quaternion that is not normalised. A zero one is the unset default, which is identity here.
    private static Quaternion Unit(Quaternion q) =>
        q.LengthSquared() > 1e-6f ? q.Normalized() : Quaternion.Identity;

    private static RemotePose At(in AircraftStateMessage sample, RemotePoseFeed feed) =>
        new(feed, sample.Position, Unit(sample.Attitude), sample.Velocity,
            sample.Throttle, sample.Aileron, sample.Elevator, sample.Rudder, sample.Nitro);

    private static RemotePose Between(in AircraftStateMessage a, in AircraftStateMessage b, float t) =>
        new(RemotePoseFeed.Interpolating,
            a.Position.Lerp(b.Position, t),
            Unit(a.Attitude).Slerp(Unit(b.Attitude), t),
            a.Velocity.Lerp(b.Velocity, t),
            Mathf.Lerp(a.Throttle, b.Throttle, t),
            Mathf.Lerp(a.Aileron, b.Aileron, t),
            Mathf.Lerp(a.Elevator, b.Elevator, t),
            Mathf.Lerp(a.Rudder, b.Rudder, t),
            t >= 0.5f ? b.Nitro : a.Nitro);

    private int Index(int offset) => (_start + offset) % Capacity;
}
