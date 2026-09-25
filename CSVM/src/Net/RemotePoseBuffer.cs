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

/// <summary>What one or more <see cref="RemotePoseBuffer"/>s took and answered. It holds samples
/// accepted and dropped as stale, and the owner's reads by feed. It also holds each accepted
/// sample's extrapolation error against the one before it. A sample no flight could reach is a
/// jump, a placement the wire carried, counted instead of measured. <see cref="Plus"/> sums the
/// remote aeroplanes on a machine.</summary>
public readonly record struct RemotePoseTally(
    int Accepted,
    int Stale,
    int Interpolating,
    int Extrapolating,
    int Starved,
    int ErrorSamples,
    double ErrorSum,
    float WorstExtrapolationError,
    int Jumps)
{
    /// <summary>Every owner read counted, whatever its feed.</summary>
    public int Answers => Interpolating + Extrapolating + Starved;

    /// <summary>The mean extrapolation error in metres, zero before two samples have landed.
    /// </summary>
    public float MeanExtrapolationError => ErrorSamples > 0 ? (float)(ErrorSum / ErrorSamples) : 0f;

    /// <summary>The two tallies as one, the worst error being the worse of the two.</summary>
    public RemotePoseTally Plus(in RemotePoseTally other) => new(
        Accepted + other.Accepted, Stale + other.Stale, Interpolating + other.Interpolating,
        Extrapolating + other.Extrapolating, Starved + other.Starved,
        ErrorSamples + other.ErrorSamples, ErrorSum + other.ErrorSum,
        Math.Max(WorstExtrapolationError, other.WorstExtrapolationError), Jumps + other.Jumps);
}

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
    /// meaningfully in its own past. Accepted at the two-session harness's link conditions, where
    /// no read starves.</summary>
    public const float BufferDelaySeconds = 0.1f;

    /// <summary>How far past its newest sample the buffer will ride a velocity, seconds. Past it
    /// the answer holds rather than flying an aircraft nobody is steering any more. Accepted beside
    /// <see cref="BufferDelaySeconds"/> on the same harness.</summary>
    public const float ExtrapolationCapSeconds = 0.25f;

    /// <summary>How many samples are kept. At the send rates in question this is seconds of
    /// history. That is far more than a read reaches back for, so the oldest is evicted and not
    /// missed.</summary>
    public const int Capacity = 32;

    private readonly (double Time, AircraftStateMessage Sample)[] _entries =
        new (double, AircraftStateMessage)[Capacity];

    private readonly int[] _answers = new int[3];
    private readonly float _sampleSeconds;
    private int _start;          // ring index of the oldest entry
    private int _count;
    private ushort _newestSequence;
    private bool _seenAny;       // whether _newestSequence means anything yet
    private int _accepted;
    private int _stale;
    private int _errorCount;
    private double _errorSum;
    private float _errorWorst;
    private int _jumps;

    /// <summary>A buffer whose sender puts one sample on the wire every
    /// <paramref name="sampleSeconds"/>. That turns a sequence gap into flown time for the
    /// extrapolation error.</summary>
    public RemotePoseBuffer(float sampleSeconds = AircraftStateCadence.SampleSeconds)
    {
        _sampleSeconds = sampleSeconds;
    }

    /// <summary>The buffer's own receive clock, in seconds, advanced by its owner's sim step. An
    /// arrival is stamped with it and a read is asked for it. Both sides then share one scale,
    /// with no session clock reachable from here.</summary>
    public double Now { get; private set; }

    /// <summary>How many samples are held, at most <see cref="Capacity"/>.</summary>
    public int Count => _count;

    /// <summary>The newest sequence accepted, meaningless until one has been.</summary>
    public ushort NewestSequence => _newestSequence;

    /// <summary>What this buffer has taken and answered since it was built or last reset.
    /// <see cref="Clear"/> keeps it, since a respawn is not a new link.</summary>
    public RemotePoseTally Tally => new(
        _accepted, _stale, _answers[(int)RemotePoseFeed.Interpolating],
        _answers[(int)RemotePoseFeed.Extrapolating], _answers[(int)RemotePoseFeed.Starved],
        _errorCount, _errorSum, _errorWorst, _jumps);

    /// <summary>Zeroes <see cref="Tally"/> and nothing else, so a soak can read one stretch of a
    /// run on its own.</summary>
    public void ResetTally()
    {
        Array.Clear(_answers);
        _accepted = 0;
        _stale = 0;
        _errorCount = 0;
        _errorSum = 0.0;
        _errorWorst = 0f;
        _jumps = 0;
    }

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
        {
            _stale++;
            return false;
        }

        _accepted++;
        if (_count > 0)
        {
            var newest = _entries[Index(_count - 1)];
            if (time < newest.Time)
                time = newest.Time;
            Measure(newest.Sample, sample);
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

    /// <summary>The state to draw at <see cref="Now"/>, the owner's one read per step, so this is
    /// the read <see cref="Tally"/> counts by feed. A read at any other time counts nothing.
    /// </summary>
    public bool TrySample(out RemotePose pose)
    {
        if (!TrySample(Now, out pose))
            return false;
        _answers[(int)pose.Feed]++;
        return true;
    }

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

    // How far a sample landed from where the one before it predicted, flown on its velocity across
    // the gap. That is the error a forced extrapolation would show. Flying, the aeroplane lands
    // within twice its reach of that point. A sample past it was placed there (a respawn ahead of
    // its spawn event), so it is a jump, not an error.
    private void Measure(in AircraftStateMessage before, in AircraftStateMessage after)
    {
        float flown = (ushort)(after.Sequence - before.Sequence) * _sampleSeconds;
        float error = (before.Position + (before.Velocity * flown)).DistanceTo(after.Position);
        float reach = Math.Max(before.Velocity.Length(), after.Velocity.Length()) * flown;
        if (error > 2f * reach)
        {
            _jumps++;
            return;
        }

        _errorCount++;
        _errorSum += error;
        _errorWorst = Math.Max(_errorWorst, error);
    }

    private int Index(int offset) => (_start + offset) % Capacity;
}
