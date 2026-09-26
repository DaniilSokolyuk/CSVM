namespace CSVM.Utils;

/// <summary>
/// The ramp behind the cover a session starts under: opaque while the world is still assembling,
/// then up from a dark tone over <see cref="FadeSeconds"/> once the first frame the player is
/// meant to see is ready. Engine free, so the whole rule is unit testable; <c>UI/Screens/SessionStartFade</c>
/// paints it. The original goes from its load screen straight into the cutscene or the flight and
/// comes up from dark rather than black, with no frame of the world building in between.
/// </summary>
public sealed class StartCover
{
    /// <summary>How long the cover takes to go from opaque to gone. About a second at the
    /// controls, in the shape of the mission-end fade's two-second leaving ramp. TUNE: the
    /// original's start ramp is undecoded, so this is a length judged against footage, not a
    /// figure read out of the program.</summary>
    public const float FadeSeconds = 1f;

    /// <summary>The tone the cover fades up from, a near-black neutral with a faint blue cast
    /// rather than pure black, which is what the original starts a mission on. TUNE: picked to
    /// read as dark without reading as a dead screen, not decoded.</summary>
    public const float ToneR = 0.07f;

    /// <summary>The tone's green channel. See <see cref="ToneR"/>.</summary>
    public const float ToneG = 0.07f;

    /// <summary>The tone's blue channel, lifted over the other two for the cast. See
    /// <see cref="ToneR"/>.</summary>
    public const float ToneB = 0.09f;

    /// <summary>How long the cover may hold before it fades whatever the session says. A
    /// remake-only safety valve: a build that never reports a first frame would otherwise leave
    /// the player looking at an opaque screen with no way out.</summary>
    public const float MaxHoldSeconds = 10f;

    /// <summary>The largest step one frame may contribute. ⚠ Do not remove the clamp: a build is
    /// one synchronous block, so the frame that closes over it carries the whole build as its
    /// delta, and an unclamped step would spend the entire fade (and the hold cap) in that one
    /// frame, which is exactly the frame the cover exists to hide.</summary>
    public const float MaxStepSeconds = 0.1f;

    private readonly bool _enabled;
    private bool _released;
    private float _held;
    private float _faded;

    /// <summary>Builds the ramp for one session start. Under <c>--det</c> nothing is covered at
    /// all: a covered frame would paint over the world the pinned goldens hash and over every
    /// scripted <c>--screenshot</c>, which counts its frames from the first world frame.</summary>
    public StartCover(bool det) => _enabled = !det;

    /// <summary>Does this ramp cover anything at all, ever? False under <c>--det</c>.</summary>
    public bool Enabled => _enabled;

    /// <summary>Has the hold ended and the fade started?</summary>
    public bool Released => _released;

    /// <summary>Did <see cref="MaxHoldSeconds"/> end the hold rather than the session being
    /// ready? The one state worth a log line, since it means a session never reported its first
    /// frame.</summary>
    public bool TimedOut { get; private set; }

    /// <summary>How long the cover held before the fade started.</summary>
    public float HeldSeconds => _held;

    /// <summary>The cover's alpha this frame: 1 while it holds, ramping linearly to 0 across the
    /// fade, and 0 for the whole of a <c>--det</c> run.</summary>
    public float Alpha
    {
        get
        {
            if (!_enabled)
            {
                return 0f;
            }

            if (!_released)
            {
                return 1f;
            }

            float left = 1f - (_faded / FadeSeconds);
            return left < 0f ? 0f : left;
        }
    }

    /// <summary>Is the cover done with, so its owner can drop it?</summary>
    public bool Finished => !_enabled || (_released && _faded >= FadeSeconds);

    /// <summary>One frame. <paramref name="ready"/> is the session's own answer to whether the
    /// frame the player is meant to see is ready; the frame it first reads true still draws the
    /// cover opaque, so the fade starts on that frame rather than after it.</summary>
    public void Advance(float dt, bool ready)
    {
        if (!_enabled || dt <= 0f)
        {
            return;
        }

        if (dt > MaxStepSeconds)
        {
            dt = MaxStepSeconds;
        }

        if (!_released)
        {
            _held += dt;
            TimedOut = !ready && _held >= MaxHoldSeconds;
            _released = ready || TimedOut;
            return;
        }

        _faded += dt;
    }
}
