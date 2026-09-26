using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// The original's nitro boost lifecycle, engine-free: a 30-unit tank burned at 4/s while boosting
/// and refilled at 1/s always, a one-shot engage from a tank at or above 99 % that no input can
/// stop, the 5 % cutoff, the engine-out refusal and the boost/decay animation edges. The force
/// couplings live in <see cref="FlightModel"/> (<c>BoostLever</c>, <c>BoostDragFactor</c>) and are
/// reached through <see cref="FlightInput.Boost"/>. Decode with addresses: docs/org/flightModel.md,
/// "Nitro". ⚠ Every constant here is executable-resident and censused by
/// FlightConstantInventoryTests; do not balance them by feel.
/// </summary>
public sealed class NitroSystem
{
    // The four tank slots the vehicle constructor writes; no data key reaches them.
    public const float Capacity = 30f;
    public const float BurnRate = 4f;
    public const float RechargeRate = 1f;
    // The human arm engages only from this fraction of the tank, and every arm cuts off below the
    // other. The AI arm has no engage fraction at all.
    public const float EngageFraction = 0.99f;
    public const float CutoffFraction = 0.05f;
    // The boost animation lives at least this long after an engage before the decay replaces it.
    public const float MinBoostAnimSeconds = 1f;
    // How long the keyed loop sound outlives its last refresh.
    public const float LoopKeyedSeconds = 0.1f;

    private Anim _anim;
    private bool _active;
    private float _timer;

    private enum Anim { None, Boost, Decay }

    /// <summary>The injector: the hangar engine pick's nitrous bit for a human, the roster
    /// <c>nitro</c> slot for an AI. Nothing engages without it.</summary>
    public bool Installed { get; set; }

    /// <summary>Tank contents in the original's units, spawning full.</summary>
    public float Charge { get; private set; } = Capacity;

    public float ChargeFraction => Charge / Capacity;

    /// <summary>The boost flag the force path reads.</summary>
    public bool Boosting { get; private set; }

    /// <summary>The boost animation (and its loop sound) is alive.</summary>
    public bool BoostAnimAlive { get; private set; }

    /// <summary>The decay animation is playing; a re-engage is refused until
    /// <see cref="DecayFinished"/>.</summary>
    public bool DecayAnimPlaying => _anim == Anim.Decay;

    /// <summary>Set on the tick the boost engaged (shake, force feedback, boost def start);
    /// cleared by the next <see cref="BeginStep"/>.</summary>
    public bool EngagedThisTick { get; private set; }

    /// <summary>Set on the tick the boost animation stopped and the decay def should start;
    /// cleared by the next <see cref="BeginStep"/>.</summary>
    public bool ReleasedThisTick { get; private set; }

    /// <summary>Set on a tick whose command arm reached the state machine at all while the boost
    /// animation was alive: the original refreshes its keyed <c>snd_nitro</c> loop for
    /// <see cref="LoopKeyedSeconds"/> there, INSIDE the method, so the loop sounds only while
    /// something keeps calling it. That is why an AI's loop is a blip at the engage and a second of
    /// sound after the maneuver rather than a sustain: nothing calls the method during the
    /// maneuver. Cleared by the next <see cref="BeginStep"/>.</summary>
    public bool LoopRefreshedThisTick { get; private set; }

    /// <summary>Opens a step: clears both tick edges so this step's own arms can raise them.
    /// ⚠ Never clear them anywhere later in a step. An edge cleared between the arm that raises it
    /// and the consumer that reads it deletes the engage outright, costing the boost its animation,
    /// its shake and its loop sound while the aircraft still accelerates. Why the flags exist at
    /// all, and what the original does instead: docs/org/flightModel.md, "Nitro".</summary>
    public void BeginStep()
    {
        EngagedThisTick = false;
        ReleasedThisTick = false;
        LoopRefreshedThisTick = false;
    }

    /// <summary>The human command arm, once per tick before <see cref="Advance"/>: the injector
    /// and the cutoff drop the boost, a tank under the engage line keeps whatever state it is in,
    /// and a tank at or above it engages on a held command.</summary>
    public void HumanCommand(bool commandHeld, bool engineOut, float dt)
    {
        if (!Installed || Charge < CutoffFraction * Capacity)
            Set(false, engineOut, dt);
        else if (Charge < EngageFraction * Capacity)
        {
            if (Boosting)
                Set(true, engineOut, dt);
        }
        else if (commandHeld)
            Set(true, engineOut, dt);
    }

    /// <summary>The AI arm: a nitro-flagged maneuver starting (true) or the per-frame release
    /// outside one (false). Needs the injector like the human arm; has no engage line.</summary>
    public void AiSet(bool want, bool engineOut, float dt)
    {
        if (want && !Installed)
            return;
        Set(want, engineOut, dt);
    }

    /// <summary>The per-vehicle tank update: burn while boosting, refill always, clamp, and the
    /// cutoff that ends a burn. Runs after the command arm, as the original's per-vehicle update
    /// runs after its input handler, and leaves both tick edges alone: its own cutoff raises the
    /// release edge, and the command arm's engage edge has not been read yet.</summary>
    public void Advance(float dt, bool engineOut)
    {
        if (Boosting)
            Charge -= dt * BurnRate;
        Charge = Mathf.Clamp(Charge + dt * RechargeRate, 0f, Capacity);
        if (Charge < CutoffFraction * Capacity)
            Set(false, engineOut, dt);
    }

    /// <summary>Losing the engine drops the boost at once.</summary>
    public void EngineLost(float dt) => Set(false, true, dt);

    /// <summary>The decay def completed (or there is none to play).</summary>
    public void DecayFinished()
    {
        if (_anim == Anim.Decay)
            _anim = Anim.None;
    }

    /// <summary>Spawn/respawn: both flags and the animation state clear, the tank refills. The
    /// injector is the build's and is not touched.</summary>
    public void Reset()
    {
        _anim = Anim.None;
        _active = false;
        _timer = 0f;
        Charge = Capacity;
        Boosting = false;
        BoostAnimAlive = false;
        EngagedThisTick = false;
        ReleasedThisTick = false;
        LoopRefreshedThisTick = false;
    }

    // The original's SetNitro, in its order: refuse on engine out, refuse while the decay plays,
    // then the flag, the engage arm, the release arm.
    private void Set(bool want, bool engineOut, float dt)
    {
        if (want && engineOut)
            return;
        if (want && _anim != Anim.None && !_active)
            want = false;
        _timer += dt;
        Boosting = want;
        if (!want)
            _active = false;
        else if (_anim == Anim.None)
        {
            _active = true;
            _timer = 0f;
            _anim = Anim.Boost;
            BoostAnimAlive = true;
            EngagedThisTick = true;
        }

        if (_anim != Anim.None && !_active && BoostAnimAlive && _timer > MinBoostAnimSeconds)
        {
            BoostAnimAlive = false;
            _anim = Anim.Decay;
            ReleasedThisTick = true;
        }

        // The loop refresh is the last thing the original's method does, after the release arm has
        // had its say, so the tick that stops the boost animation refreshes nothing.
        if (BoostAnimAlive)
            LoopRefreshedThisTick = true;
    }
}
