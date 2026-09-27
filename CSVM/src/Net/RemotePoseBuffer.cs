using System;
using Godot;

namespace CSVM.Net;

/// <summary>Which of the three cases a <see cref="RemotePoseBuffer"/> answer came out of, so an
/// instrument can count them without re-deriving the decision. Interpolating is the healthy case.
/// Extrapolating means the newest sample is older than the playout time, so the answer rides that
/// sample's velocity. Starved means the buffer cannot cover the playout time at all, and holds the
/// nearest sample it has.</summary>
public enum RemotePoseFeed
{
    /// <summary>The buffer holds the nearest sample rather than guessing further.</summary>
    Starved,

    /// <summary>Two samples straddle the playout time and the answer is between them.</summary>
    Interpolating,

    /// <summary>The newest sample is older than the playout time; the answer rides its velocity.</summary>
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
/// The received history of one remote aircraft, and the pose to draw it at now. A sample is placed
/// on the sender's own timeline by its sequence, never by when it landed. A playout clock walks
/// that timeline <see cref="BufferDelaySeconds"/> behind the newest arrival. Its rate is the
/// sender's clock against this one, fitted over the held arrivals, and nudged gently by the
/// smoothed lead error. Engine-free and clock-free: the owner advances <see cref="Now"/> by its own
/// step. A sample at or below the newest sequence is dropped, wrap included.
/// </summary>
public sealed class RemotePoseBuffer
{
    /// <summary>How far behind the newest arrival the playout reads, sender seconds. Big enough to
    /// hide the gap between two sends plus the jitter on them. Small enough that the aircraft is not
    /// shown meaningfully in its own past.</summary>
    public const float BufferDelaySeconds = 0.1f;

    /// <summary>How far past its newest sample the playout will ride a velocity, seconds. Past it
    /// the playout clock stops and the answer holds, rather than flying an aircraft nobody is
    /// steering any more.</summary>
    public const float ExtrapolationCapSeconds = 0.25f;

    /// <summary>How many samples are kept: about three seconds at the send rate, which is the
    /// window the clock rate is fitted over.</summary>
    public const int Capacity = 64;

    /// <summary>How long a fitted window of arrivals must span before its rate replaces the one in
    /// use. A shorter one is mostly arrival jitter.</summary>
    public const double RateWindowSeconds = 0.5;

    /// <summary>The time constant the lead error is smoothed over, seconds. TUNE.</summary>
    public const double ErrorSmoothingSeconds = 0.25;

    /// <summary>The playout's speed correction per second of smoothed lead error. With
    /// <see cref="ErrorSmoothingSeconds"/> this is a critically damped loop. TUNE.</summary>
    public const double ErrorGain = 1.0;

    /// <summary>The largest share of the fitted rate the lead error may add or take away. TUNE.
    /// </summary>
    public const double MaxCorrection = 0.1;

    /// <summary>A lead error past this many seconds re-anchors the playout outright. Only a
    /// discontinuity reaches it: a stream resumed after its sender stopped counting, or a stall
    /// longer than the history.</summary>
    public const double SnapSeconds = 1.0;

    private readonly (double Sent, AircraftStateMessage Sample)[] _entries =
        new (double, AircraftStateMessage)[Capacity];

    // The arrivals the clock rate is fitted over. Kept apart from the samples because a respawn
    // empties those, and the link's clock does not change with it.
    private readonly (double Arrival, double Sent)[] _arrivals = new (double, double)[Capacity];

    private readonly int[] _answers = new int[3];
    private readonly float _sampleSeconds;
    private int _start;          // ring index of the oldest entry
    private int _count;
    private int _fitStart;       // ring index of the oldest arrival
    private int _fitCount;
    private ushort _newestSequence;
    private bool _seenAny;       // whether the stale check applies; a Clear lifts it
    private bool _timeline;      // whether _newestSequence, _newestSent and _playout mean anything
    private double _newestSent;  // the newest sample's place on the sender's timeline
    private double _newestArrival;
    private double _playout;     // the sender time a read answers for
    private double _rate = 1.0;  // sender seconds per second of this buffer's clock
    private double _lead;        // smoothed lead error, seconds; positive means the playout lags
    private int _accepted;
    private int _stale;
    private int _errorCount;
    private double _errorSum;
    private float _errorWorst;
    private int _jumps;

    /// <summary>A buffer whose sender puts one sample on the wire every
    /// <paramref name="sampleSeconds"/>. That places each sample on the sender's timeline, and
    /// turns a sequence gap into flown time.</summary>
    public RemotePoseBuffer(float sampleSeconds = AircraftStateCadence.SampleSeconds)
    {
        _sampleSeconds = sampleSeconds;
    }

    /// <summary>The buffer's own receive clock, in seconds, advanced by its owner's sim step. An
    /// arrival is stamped with it, and the playout clock is walked by it.</summary>
    public double Now { get; private set; }

    /// <summary>How many samples are held, at most <see cref="Capacity"/>.</summary>
    public int Count => _count;

    /// <summary>The newest sequence accepted, meaningless until one has been.</summary>
    public ushort NewestSequence => _newestSequence;

    /// <summary>The sender time the owner's read answers for. The first sample taken sits at its
    /// sequence times the sample interval, and every later one follows on by its sequence gap,
    /// across a <see cref="Clear"/> too.</summary>
    public double PlayoutTime => _playout;

    /// <summary>Sender seconds the playout walks per second of <see cref="Now"/>, the lead
    /// correction included.</summary>
    public double PlayoutRate => _rate + Math.Clamp(ErrorGain * _lead, -MaxCorrection * _rate, MaxCorrection * _rate);

    /// <summary>How many times the playout was re-anchored rather than walked.</summary>
    public int Resyncs { get; private set; }

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

    /// <summary>Moves <see cref="Now"/> on by one step of the owner's clock, and the playout with it
    /// at <see cref="PlayoutRate"/>. The playout never passes the newest sample by more than
    /// <see cref="ExtrapolationCapSeconds"/>, and never runs backwards.</summary>
    public void Advance(float dt)
    {
        Now += dt;
        if (!_timeline || dt <= 0f)
            return;
        double ceiling = _newestSent + ExtrapolationCapSeconds;
        double next = Math.Min(_playout + (dt * PlayoutRate), ceiling);
        _playout = Math.Max(_playout, next);
    }

    /// <summary>Takes one sample that arrived just now. False when it was dropped as stale.</summary>
    public bool Receive(in AircraftStateMessage sample) => Add(sample, Now);

    /// <summary>Takes one sample that arrived at <paramref name="time"/> on the buffer's own scale.
    /// False when its sequence is at or below the newest accepted, which is a duplicate or a
    /// reordered delivery and is dropped rather than applied. The arrival feeds only the playout
    /// clock; where the sample sits is its sequence's business.</summary>
    public bool Add(in AircraftStateMessage sample, double time)
    {
        if (_seenAny && !IsNewer(sample.Sequence, _newestSequence))
        {
            _stale++;
            return false;
        }

        _accepted++;
        bool first = !_timeline;
        double sent = sample.Sequence * (double)_sampleSeconds;
        if (!first)
        {
            // Signed, because after a Clear the stale check is off and an older sequence is taken.
            if (time < _newestArrival)
                time = _newestArrival;
            sent = _newestSent + ((short)(ushort)(sample.Sequence - _newestSequence) * (double)_sampleSeconds);
        }

        if (_count > 0)
            Measure(_entries[Index(_count - 1)].Sample, sample);
        _entries[Push(ref _start, ref _count)] = (sent, sample);
        _newestSequence = sample.Sequence;
        _newestSent = sent;
        _newestArrival = time;
        _seenAny = true;
        _timeline = true;
        Steer(sent, time, first);
        return true;
    }

    /// <summary>Forgets every sample and lifts the stale check. That is what a placement the wire
    /// did not cause (a spawn, a respawn) owes. The samples before it describe an aircraft that is
    /// no longer where they say. The timeline, the playout clock and the fitted rate are kept. The
    /// sender's sequence runs on through a respawn, and the link is the same one.
    /// </summary>
    public void Clear()
    {
        _start = 0;
        _count = 0;
        _seenAny = false;
    }

    /// <summary>The state to draw at <see cref="PlayoutTime"/>, the owner's one read per step, so
    /// this is the read <see cref="Tally"/> counts by feed. A read at any other time counts nothing.
    /// </summary>
    public bool TrySample(out RemotePose pose)
    {
        if (!TrySample(_playout, out pose))
            return false;
        _answers[(int)pose.Feed]++;
        return true;
    }

    /// <summary>The state at <paramref name="playoutTime"/> on the sender's timeline, the scale
    /// <see cref="PlayoutTime"/> reads on. False only when there is no sample at all to build an
    /// answer from. Every other case answers, and <see cref="RemotePose.Feed"/> says which of the
    /// three it was.</summary>
    public bool TrySample(double playoutTime, out RemotePose pose)
    {
        pose = default;
        if (_count == 0)
            return false;

        double target = playoutTime;
        var oldest = _entries[_start];
        if (target <= oldest.Sent)
        {
            // Nothing that old is held, so there is nothing to interpolate from: the aircraft
            // waits at its oldest known state rather than being guessed backwards.
            pose = At(oldest.Sample, RemotePoseFeed.Starved);
            return true;
        }

        var newest = _entries[Index(_count - 1)];
        if (target >= newest.Sent)
        {
            double age = target - newest.Sent;
            bool capped = age >= ExtrapolationCapSeconds;
            float flown = (float)Math.Min(age, ExtrapolationCapSeconds);
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
            if (a.Sent > target)
                continue;
            var b = _entries[Index(i)];
            double span = b.Sent - a.Sent;
            float t = span > 0.0 ? (float)((target - a.Sent) / span) : 1f;
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

    // The slot a new entry goes in at the end of a ring, dropping the oldest once it is full.
    private static int Push(ref int start, ref int count)
    {
        if (count < Capacity)
            return (start + count++) % Capacity;
        int slot = start;
        start = (start + 1) % Capacity;
        return slot;
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

    // One accepted sample's say over the playout clock. Its lead error is how far the playout
    // stood from the delay behind it when it landed. A first sample, or one past the snap
    // threshold, anchors the playout outright and restarts the rate fit from itself.
    private void Steer(double sent, double arrival, bool first)
    {
        double since = Math.Max(0.0, Now - arrival);
        double error = sent - BufferDelaySeconds - (_playout - (since * PlayoutRate));
        if (first || Math.Abs(error) > SnapSeconds)
        {
            if (!first)
                Resyncs++;
            _playout = sent - BufferDelaySeconds + (since * _rate);
            _fitCount = 0;
            _lead = 0.0;
            _arrivals[Push(ref _fitStart, ref _fitCount)] = (arrival, sent);
            return;
        }

        double blend = Math.Min(1.0, _sampleSeconds / ErrorSmoothingSeconds);
        _lead += (error - _lead) * blend;
        _arrivals[Push(ref _fitStart, ref _fitCount)] = (arrival, sent);
        FitRate();
    }

    // The sender's clock against this one: the least-squares slope of sender time over arrival
    // across the arrivals since the last anchor. Arrival jitter averages out over the window,
    // which one pair of samples could never do.
    private void FitRate()
    {
        int n = _fitCount;
        var oldest = _arrivals[_fitStart];
        var newest = _arrivals[(_fitStart + n - 1) % Capacity];
        if (n < 4 || newest.Arrival - oldest.Arrival < RateWindowSeconds)
            return;

        double meanA = 0.0, meanS = 0.0;
        for (int i = 0; i < n; i++)
        {
            var e = _arrivals[(_fitStart + i) % Capacity];
            meanA += e.Arrival / n;
            meanS += e.Sent / n;
        }

        double sxx = 0.0, sxy = 0.0;
        for (int i = 0; i < n; i++)
        {
            var e = _arrivals[(_fitStart + i) % Capacity];
            sxx += (e.Arrival - meanA) * (e.Arrival - meanA);
            sxy += (e.Arrival - meanA) * (e.Sent - meanS);
        }

        if (sxx > 0.0)
            _rate = Math.Clamp(sxy / sxx, 0.2, 5.0);
    }

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
