using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Audio;

/// <summary>Where the engine slot stands against the airframe's damage. The original has no
/// sputter or restart cue of its own: the damage edge stops the slot, the slot stays silent while
/// the shared re-arm timer runs, and then the damaged loop starts at full level and holds.
/// Decode: docs/formats/vehicle.md, "The damaged engine's phases".</summary>
public enum EngineSlotPhase
{
    /// <summary>The plain or cockpit loop, per the view.</summary>
    Healthy,

    /// <summary>Damaged, and the slot is silent until the re-arm timer fires.</summary>
    Out,

    /// <summary>The <c>damaged_engine_sound</c> loop, at whatever pitch its definition accepts.</summary>
    Damaged,
}

/// <summary>Everything the engine slot's curves read this frame. The original's per-frame routine
/// takes the throttle lever plus two things off the airframe's own state, so this carries all three
/// rather than letting each audio path re-derive them.</summary>
/// <param name="Throttle">The lever, [0..1], which the curves' own control points are in terms of.</param>
/// <param name="TurnRate">rad/s about everything EXCEPT the nose axis.</param>
/// <param name="ClimbAttitude">The original's <c>a</c>: negative climbing, positive diving.</param>
/// <param name="Boosting">The nitro boost flag (<see cref="FlightModel.Boosting"/>).</param>
public readonly record struct EngineDrive(
    float Throttle, float TurnRate, float ClimbAttitude, bool Boosting = false);

/// <summary>
/// The engine-audio slot maths every aircraft shares: which definition sits on the engine slot,
/// and what pitch and gain the two loops take this frame. The original runs one per-frame function
/// for the player and for every AI vehicle, forking only on the distance cull and on the pitch
/// multiplier, so the player's <see cref="FlightAudio"/> and the positional
/// <see cref="Ai.AiEngineAudio"/> read their numbers from here rather than each keeping a copy.
/// The curves themselves are player.json globals carried on <see cref="PlaneStats"/>; the
/// definition names are the airframe's own vehicle.json keys (docs/formats/vehicle.md).
/// </summary>
public static class EngineAudioCurves
{
    /// <summary>Distance from the listener past which an AI aircraft's engine and whine are both
    /// stopped, and inside which they start again: 2000 world units, the square root of the
    /// executable's own `4000000.0`. Own-ship audio is never culled, so only
    /// <see cref="Ai.AiEngineAudio"/> reads this and the squared form below.</summary>
    internal const float CullDistance = 2000f;

    /// <summary>The comparison form: the cull test is on squared distance, as the original's is.</summary>
    internal const float CullDistanceSq = CullDistance * CullDistance;

    /// <summary>The health fraction the worst zone must fall BELOW before the airframe counts as
    /// damaged: the airframe def's field at VDEF+0xbc, whose only writer is the def constructor's
    /// 0.25 (no reader token reaches it, so every def in the install carries the same number).
    /// Decode: docs/formats/vehicle.md, "What makes an airframe damaged".</summary>
    internal const float DamagedEngineHealthFraction = 0.25f;

    // The re-arm delay's floor and spread: a threshold drawn as Min + Spread*u, u in [0, 1).
    // The original's own 3.0 + 2*rand()/32767 (docs/formats/vehicle.md, "an airframe damaged").
    private const float DamagedRearmMin = 3f;
    private const float DamagedRearmSpread = 2f;

    // The frequency handed to the voice is clamped to 4000..55200 Hz, and every install WAV is
    // 22050 Hz, so as a PitchScale the mixer's own bounds are these. Godot clamps nothing, and a
    // PitchScale of 0 stalls the stream instead of bottoming out.
    // Decode: docs/formats/sounds.md, "A definition is pitched only when it carries FREQUENCY".
    private const float MinPitch = 4000f / 22050f;
    private const float MaxPitch = 55200f / 22050f;

    // The two added terms and the clamp they sit under, all read out of the image rather than tuned
    // (docs/formats/vehicle.md carries each address). Volume and pitch differ only in the turn-rate
    // coefficient; the climb term is the same 0.15 on both.
    // ⚠ TurnRateIntoVolume is INERT on the shipped data and is not a dead constant to delete: the
    // engine volume curve is flat 1.0, so the remap multiplies the parameter it moves by a zero span.
    private const float TurnRateIntoVolume = 0.26f;
    private const float TurnRateIntoPitch = 0.25f;
    private const float ClimbAttitudeIntoParam = 0.15f;

    // The headroom above a curve's own 1.0 is authored, not slack: it is what the overshoot at a
    // hard pull and the boost pins below both live in.
    private const float ParamMax = 1.5f;

    // Boost REPLACES both parameters rather than adding to them.
    private const float BoostVolumeParam = 1.17f;
    private const float BoostPitchParam = 1.25f;

    // The gain the rattle loop plays at once its gate opens. The original's keep-alive call
    // hardcodes 1.0, level with the engine slot's flat volume curve; this port plays it 1.3x that,
    // a chosen departure judged at the controls, where the decoded level read audible but low.
    // Decode: docs/org/shakes.md, "The rattle SOUND is a gate at full level".
    private const float RattleGain = 1.3f;

    /// <summary>Whether the engine slot takes <c>damaged_engine_sound</c>: the original tests its
    /// whole disabled-systems mask for nonzero, and the two bits this engine models are the
    /// health-threshold one and engine-out. <paramref name="worstHealthFraction"/> is
    /// <see cref="PlaneDamage.WorstHealthFraction"/>, which already picks zones or the hull pair
    /// the way the original does.</summary>
    public static bool EngineDamaged(float worstHealthFraction, bool engineDead = false) =>
        engineDead || worstHealthFraction < DamagedEngineHealthFraction;

    /// <summary>Whether the definition on a slot accepts a frequency write at all. The original
    /// gives a definition's buffer the frequency control only when its entry carries
    /// <c>FREQUENCY</c>, so on one without it every pitch these curves compute is refused and the
    /// loop runs at its own sample rate. In the shipped install that silences the pitch on
    /// <c>snd_damagedengine</c> and on every <c>*_cp</c> cockpit loop.
    /// Decode: docs/formats/sounds.md, "A definition is pitched only when it carries FREQUENCY".</summary>
    public static bool SlotIsPitched(IReadOnlyDictionary<string, SoundDef>? defs, string? name) =>
        name != null && defs != null && defs.TryGetValue(name, out var def) && def.Frequency;

    /// <summary>The damaged swap's pitch multiplier, drawn once per swap and then held:
    /// <paramref name="u"/> is the original's <c>rand() / 32767</c> and the entry's own two floats
    /// bound it linearly. A CLEAR flag byte leaves the multiplier at 1 rather than drawing, which
    /// is why the flag is not a range of zero width. ⚠ Nobody hears this on the shipped entry:
    /// <c>snd_damagedengine</c> takes no frequency write (<see cref="SlotIsPitched"/>).</summary>
    public static float DamagedPitchMul(PlaneStats stats, float u) =>
        stats.DamagedEnginePitchRandom
            ? stats.DamagedEnginePitchLo + ((stats.DamagedEnginePitchHi - stats.DamagedEnginePitchLo) * u)
            : 1f;

    /// <summary>Ticks the shared re-arm timer one frame and says whether the damaged loop may
    /// start now. The accumulator as it stood BEFORE this frame is tested against a threshold
    /// redrawn every frame from <paramref name="u"/>, the frame's own draw in [0, 1): past it, it
    /// resets to zero and fires; otherwise it takes this frame's <paramref name="dt"/>.
    /// Decode: docs/formats/vehicle.md, "What makes an airframe damaged".</summary>
    public static bool AdvanceDamagedRearm(DamagedEngineTimer timer, float dt, float u)
    {
        if (timer.Elapsed <= DamagedRearmMin + (DamagedRearmSpread * u))
        {
            timer.Elapsed += dt;
            return false;
        }
        timer.Elapsed = 0f;
        return true;
    }

    /// <summary>One frame of the engine slot's damage phases (<see cref="EngineSlotPhase"/>).
    /// Healthy is taken the moment <paramref name="damaged"/> clears; a damaged slot whose loop
    /// is still sounding holds; anything else (the edge, the silence, a stopped loop) ticks the
    /// shared re-arm timer and starts the damaged loop when it fires. The caller stops the slot on
    /// the way into <see cref="EngineSlotPhase.Out"/> and starts it on the way out of it.
    /// Decode: docs/formats/vehicle.md, "The damaged engine's phases".</summary>
    public static EngineSlotPhase StepEnginePhase(EngineSlotPhase phase, bool damaged,
        bool loopSounding, DamagedEngineTimer timer, float dt, float u)
    {
        if (!damaged)
        {
            return EngineSlotPhase.Healthy;
        }
        if (phase == EngineSlotPhase.Damaged && loopSounding)
        {
            return EngineSlotPhase.Damaged;
        }
        return AdvanceDamagedRearm(timer, dt, u) ? EngineSlotPhase.Damaged : EngineSlotPhase.Out;
    }

    /// <summary>Whether a step from <paramref name="from"/> to <paramref name="next"/> starts the
    /// damaged loop this frame, which includes a damaged loop that had stopped (an AI past the
    /// cull) being started again once the timer fires.</summary>
    public static bool StartsDamagedLoop(EngineSlotPhase from, EngineSlotPhase next, bool loopSounding) =>
        next == EngineSlotPhase.Damaged && !(from == EngineSlotPhase.Damaged && loopSounding);

    /// <summary>The engine slot's definition and its pitch multiplier: damaged swaps onto
    /// <c>damaged_engine_sound</c> at a drawn multiplier; else <paramref name="cockpitView"/>
    /// (own-ship only, the full Cockpit view, since the original leaves the Nose view on the plain
    /// def) swaps onto <c>cockpit_engine_sound</c> at multiplier 1; else the plain
    /// <c>engine_sound</c>. Damaged wins because no def authors a damaged cockpit variant.</summary>
    public static (string Name, float PitchMul) EngineDefFor(
        PlaneStats stats, bool damaged, RandomNumberGenerator rng, bool cockpitView = false)
    {
        if (damaged && stats.DamagedEngineSound is { } damagedName)
        {
            return (damagedName, DamagedPitchMul(stats, stats.DamagedEnginePitchRandom ? rng.Randf() : 0f));
        }
        if (cockpitView && stats.CockpitEngineSound is { } cockpitName)
        {
            return (cockpitName, 1f);
        }
        return (stats.EngineSound, 1f);
    }

    /// <summary>The two quantities the engine slot reads off the airframe, in the original's own
    /// terms. Written once here because both are frame-convention traps: ours is nose −Z where the
    /// original's row 2 is −nose, so the climb term needs no negation and looks wrong.</summary>
    internal static EngineDrive DriveFrom(FlightModel model, bool boosting = false) => new(
        model.Throttle,
        // ⚠ Pitch and yaw ONLY. The original squares the two orientation rows perpendicular to the
        // nose and drops the nose-axis one, so a roll does not raise the note however fast it goes.
        Mathf.Sqrt((model.BodyRates.X * model.BodyRates.X) + (model.BodyRates.Y * model.BodyRates.Y)),
        // The original reads orientation row 2's Y, and row 2 is −nose; our nose is −Attitude.Z, so
        // its `a` IS Attitude.Z.Y with no sign flip. Negative climbing, as its own decode says.
        model.Attitude.Z.Y,
        boosting);

    /// <summary>Slot 0, the engine loop. Each curve's parameter is the throttle lever plus a
    /// turn-rate and a climb-attitude term, clamped to [0, 1.5] BEFORE the curve maps it
    /// (docs/formats/vehicle.md, "The engine slot's pitch and gain are not throttle alone"); the
    /// pitch then carries the swap multiplier and the mixer's clamp, or is 1 when
    /// <paramref name="pitchable"/> (<see cref="SlotIsPitched"/>) is clear. The volume is the curve
    /// alone: the caller still applies the definition's own VOLUME, its ramp and any mix gain.</summary>
    internal static (float Pitch, float Volume) Engine(
        PlaneStats stats, in EngineDrive drive, float pitchMul, bool pitchable)
    {
        float tVol = drive.Boosting ? BoostVolumeParam
            : Mathf.Clamp(stats.EngineVolume.Frac(drive.Throttle) + (TurnRateIntoVolume * drive.TurnRate)
                - (ClimbAttitudeIntoParam * drive.ClimbAttitude), 0f, ParamMax);
        float tPitch = drive.Boosting ? BoostPitchParam
            : Mathf.Clamp(stats.EnginePitch.Frac(drive.Throttle) + (TurnRateIntoPitch * drive.TurnRate)
                - (ClimbAttitudeIntoParam * drive.ClimbAttitude), 0f, ParamMax);
        return (pitchable
                    ? Mathf.Clamp(stats.EnginePitch.Remap(tPitch) * pitchMul, MinPitch, MaxPitch)
                    : 1f,
                stats.EngineVolume.Remap(tVol));
    }

    /// <summary>Slot 1, the overspeed whine: both curves run on speed / fd_speed, so the loop is
    /// silent below fd_speed and only opens up in a dive. No pitch multiplier reaches this slot.
    /// ⚠ No shipped airframe names a definition for it (see <see cref="PlaneStats.WhineSound"/>), so
    /// this is reached only if one ever does.</summary>
    internal static (float Pitch, float Volume) Whine(PlaneStats stats, float speedFrac) =>
        (Mathf.Max(MinPitch, stats.WhinePitch.Eval(speedFrac)), stats.WhineVolume.Eval(speedFrac));

    /// <summary>The airframe rattle's gain, on speed / fd_speed like the whine but as a GATE: full
    /// level from <see cref="PlaneStats.RattleSpeedGate"/> upward and silence below it, with no ramp
    /// between. The caller still applies the definition's own VOLUME and any mix gain, so the loop
    /// reaches the mix at the engine slot's own level once it opens.</summary>
    internal static float Rattle(PlaneStats stats, float speedFrac) =>
        speedFrac < stats.RattleSpeedGate ? 0f : RattleGain;

    /// <summary>The level the rattle plays at past its gate, for the suites that pin the gate's
    /// flat top and its relation to the engine slot.</summary>
    internal static float RattleLevel() => RattleGain;
}
