using System;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>Plays one library maneuver's timed step program as <see cref="FlightInput"/> values,
/// <c>Next(model, dt)</c> each sim step until <see cref="Done"/>, consumed the way
/// <see cref="AiPilot"/> is (docs/architecture.md). Steps are TARGET ATTITUDES in degrees, not
/// stick deflections or rates, composed onto the entry frame. A positive-duration step is held
/// for its time; a zero-duration step advances once the attitude is captured, or after
/// <see cref="ZeroDurationTimeoutS"/>. The tracking law is placeholder/invented, same status as
/// <see cref="AiPilot"/>'s.</summary>
public sealed class ManeuverExecutor
{
    /// <summary>"Attitude reached" for a zero-duration step, degrees of total rotation error.</summary>
    public const float StepToleranceDeg = 15f;

    /// <summary>Invented safety net: a zero-duration step advances after this long even when
    /// its attitude was never captured. Not an original value.</summary>
    public const float ZeroDurationTimeoutS = 6f;

    /// <summary>How far <see cref="PredictedPath"/> carries the aircraft per step, metres, decoded
    /// (<c>FUN_004201a0</c>, the float at <c>0x006036a0</c> and the same value pushed at
    /// <c>0x0042045a</c>): 240 mph in m/s. A selection-time estimate only, never flown.</summary>
    public const float PredictedStepM = 107.2896f;

    /// <summary>Throttle flown for the whole program. Mutable like <see cref="AiPilot"/>'s
    /// orders; the shipped data carries no per-step throttle.</summary>
    public float Throttle = 1f;

    // Placeholder tracking gains (AiPilot's style): stick per radian of body-frame attitude
    // error, with anticipated body rate subtracted so the capture does not overshoot.
    private const float Gain = 1.5f;
    private const float RateLead = 0.35f;

    private Basis _reference = Basis.Identity;
    private bool _begun;
    private float _stepElapsed;

    public ManeuverExecutor(Maneuver maneuver)
    {
        if (maneuver.IsStub)
            throw new ArgumentException($"'{maneuver.Name}' is a stub (no steps) and cannot be flown");
        Maneuver = maneuver;
    }

    public Maneuver Maneuver { get; }

    /// <summary>The step currently being flown; == <c>Maneuver.Steps.Count</c> once done.</summary>
    public int StepIndex { get; private set; }

    /// <summary>True once every step has run. <see cref="Next"/> then holds level in the entry
    /// frame; the owner (D11's state machine) is expected to switch input sources.</summary>
    public bool Done => StepIndex >= Maneuver.Steps.Count;

    /// <summary>The program's predicted flight path from <paramref name="pos"/> at
    /// <paramref name="attitude"/>: one <see cref="PredictedStepM"/> segment per step along that
    /// step's own target forward, chained, the last point being the program's predicted end. This
    /// is what the maneuver library's altitude veto reads, so it composes the same steps onto the
    /// same entry frame <see cref="Next"/> flies rather than modelling the aircraft.</summary>
    public static Vector3[] PredictedPath(Maneuver maneuver, Vector3 pos, Basis attitude)
    {
        var reference = ReferenceFrame(maneuver, attitude);
        var path = new Vector3[maneuver.Steps.Count];
        var at = pos;
        for (int i = 0; i < path.Length; i++)
        {
            at -= TargetBasis(reference, maneuver.Steps[i]).Z * PredictedStepM;
            path[i] = at;
        }
        return path;
    }

    /// <summary>One sim step's stick and throttle. The first call captures the entry frame:
    /// the level entry-heading frame, or the full entry attitude when the maneuver is
    /// <c>relative</c>.</summary>
    public FlightInput Next(FlightModel model, float dt)
    {
        if (!_begun)
        {
            _begun = true;
            _reference = ReferenceFrame(Maneuver, model.Attitude);
        }

        var error = Vector3.Zero;
        while (!Done)
        {
            error = BodyFrameError(model.Attitude, TargetBasis(_reference, Maneuver.Steps[StepIndex]));
            var step = Maneuver.Steps[StepIndex];
            // Half a frame of slack so an accumulated float clock advances on the nearest
            // frame rather than one late (60 × 1/60f sums just under 1.0).
            bool advance = step.DurationS > 0f
                ? _stepElapsed + 0.5f * dt >= step.DurationS
                : Mathf.RadToDeg(error.Length()) <= StepToleranceDeg
                    || _stepElapsed + 0.5f * dt >= ZeroDurationTimeoutS;
            if (!advance)
                break;
            StepIndex++;
            _stepElapsed = 0f;
        }
        _stepElapsed += dt;

        if (Done)
            error = BodyFrameError(model.Attitude, _reference);

        return new FlightInput
        {
            Pitch = Mathf.Clamp((error.X - model.BodyRates.X * RateLead) * Gain, -1f, 1f),
            Yaw = Mathf.Clamp((error.Y - model.BodyRates.Y * RateLead) * Gain, -1f, 1f),
            Roll = Mathf.Clamp((error.Z - model.BodyRates.Z * RateLead) * Gain, -1f, 1f),
            Throttle = Mathf.Clamp(Throttle, 0f, 1f),
        };
    }

    // The frame the steps compose onto: the full entry attitude for a `relative` program, the
    // level entry-heading frame otherwise, which is the original's own pair of branches.
    private static Basis ReferenceFrame(Maneuver maneuver, Basis attitude)
    {
        var att = attitude.Orthonormalized();
        return maneuver.Relative
            ? att
            : new Basis(Vector3.Up, Mathf.DegToRad(AiPilot.HeadingDegOf(-att.Z)));
    }

    // The rotation from the current attitude to the target as a body-frame
    // axis·angle vector (radians), whose components line up with the stick axes.
    private static Vector3 BodyFrameError(Basis attitude, Basis target)
    {
        var q = (attitude.Orthonormalized().Inverse() * target).GetRotationQuaternion();
        if (q.W < 0f)
            q = -q; // the short way round
        float angle = 2f * Mathf.Acos(Mathf.Clamp(q.W, -1f, 1f));
        var axis = new Vector3(q.X, q.Y, q.Z);
        float len = axis.Length();
        return len < 1e-6f ? Vector3.Zero : axis * (angle / len);
    }

    // The step's target attitude in the world frame: entry frame · yaw · pitch · roll
    // (degrees; body axes, pitch +up about X, yaw +left about Y, roll +left about Z).
    // ⚠ Do not bake a side in here. The original mirrors the whole program on a coin flip at
    // selection, which negates every step's yaw and roll at once (docs/org/aiPilot.md).
    private static Basis TargetBasis(Basis reference, ManeuverStep step) =>
        reference
        * new Basis(Vector3.Up, Mathf.DegToRad(step.YawDeg))
        * new Basis(Vector3.Right, Mathf.DegToRad(step.PitchDeg))
        * new Basis(Vector3.Back, Mathf.DegToRad(step.RollDeg));
}
