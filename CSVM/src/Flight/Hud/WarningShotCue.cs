namespace CSVM.Flight.Hud;

/// <summary>
/// The decoded incoming-fire shield (<c>FUN_004b1340</c>, player.json <c>warning_shot_max</c> /
/// <c>_dissipation</c> / <c>_interval</c>). Gun rounds that land on the pilot's own aeroplane are
/// DISCARDED while the accumulator is armed, and answered with <c>bullet_warning_sg</c> instead of
/// the ricochet; sustained fire fills it to <c>max</c>, which disarms it, and from then on the same
/// rounds spend their damage and ring <c>bullet_hit_sg</c>. A quiet interval drains it and re-arms.
/// It counts TIME, not rounds: an interval that closed with at least one hit adds its own elapsed
/// length, so the shipped <c>max</c> of 2 s is two intervals of fire. That same interval is what
/// the canopy cue hangs off, which is why <see cref="Tick"/> answers with the hit count.
/// Engine-free so the rule unit-tests without a live node, the same split
/// <see cref="CanopyHoleCue"/> uses. Decode: <c>docs/org/weaponFire.md</c>.</summary>
public sealed class WarningShotCue
{
    private readonly float _max;
    private readonly float _decay;
    private readonly float _interval;

    private float _intensity;
    private float _sinceInterval;
    private int _hits;
    private bool _armed = true;

    /// <param name="max">Seconds of fire it saturates at (<c>warning_shot_max</c>).</param>
    /// <param name="decay">Intensity shed per second of quiet: the RECIPROCAL of the authored
    /// <c>warning_shot_dissipation</c>, taken at parse time in <see cref="Airframe.PlaneStats"/> exactly as
    /// the original takes it.</param>
    /// <param name="interval">Seconds the hit count accrues over
    /// (<c>warning_shot_interval</c>).</param>
    public WarningShotCue(float max, float decay, float interval)
    {
        _max = max;
        _decay = decay;
        _interval = interval;
    }

    /// <summary>The live accumulator, 0..max, seconds of fire taken, less what quiet has drained.
    /// Nothing in the original reads it beyond the saturation test; it is exposed for the log
    /// breadcrumb and the tests.</summary>
    public float Intensity => _intensity;

    /// <summary>Whether a gun round landing right now has its damage discarded and answers with the
    /// warning cue (<c>+0x91c</c>, which a fresh airframe starts with SET at
    /// <c>0x004b032b</c>).</summary>
    public bool Absorbs => _armed;

    /// <summary>One gun round landed on this aeroplane. Counted before the absorb decision, as the
    /// original counts it (<c>0x004b9e83</c> stands ahead of the flag test at
    /// <c>0x004b9e91</c>), so an absorbed round still drives both the accumulator and the canopy
    /// cadence.</summary>
    public void RegisterHit() => _hits++;

    /// <summary>Ages the interval and answers with the hit count the interval closed with, or 0
    /// while it has not closed or closed quiet. An interval that closed with hits charges the
    /// accumulator by its own elapsed length and disarms the shield at <c>max</c>; a quiet one
    /// drains it and re-arms the shield the moment it sits below <c>max</c> again, however much
    /// charge is left.</summary>
    public int Tick(float dt)
    {
        _sinceInterval += dt;
        if (_sinceInterval < _interval)
            return 0;
        float elapsed = _sinceInterval;
        int hits = _hits;
        _sinceInterval = 0f;
        _hits = 0;
        if (hits != 0)
        {
            _intensity += elapsed;
            if (_intensity >= _max)
            {
                _intensity = _max;
                _armed = false;
            }
            return hits;
        }
        if (_intensity > 0f)
        {
            _intensity -= elapsed * _decay;
            // The zero clamp sits INSIDE the re-arm branch in the original (0x004b1408 through
            // 0x004b1422), not beside it: with a positive decay the two can never disagree, and
            // this is the shipped nesting rather than a tidied equivalent.
            if (_intensity < _max)
            {
                _armed = true;
                if (_intensity < 0f)
                    _intensity = 0f;
            }
        }
        return 0;
    }

    /// <summary>Back to a fresh airframe's state: no charge, shield armed. What the vehicle
    /// constructor writes at <c>0x004b0319</c>..<c>0x004b032b</c>.</summary>
    public void Reset()
    {
        _intensity = 0f;
        _sinceInterval = 0f;
        _hits = 0;
        _armed = true;
    }
}
