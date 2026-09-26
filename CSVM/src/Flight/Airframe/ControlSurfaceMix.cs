using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>Which of the original's six control-surface angle slots a node belongs to. The two
/// rudder slots share one target and one smoother, so they are one entry here.</summary>
public enum SurfaceSlot
{
    /// <summary>The <c>l_aileronN</c> nodes.</summary>
    AileronLeft,

    /// <summary>The <c>r_aileronN</c> nodes.</summary>
    AileronRight,

    /// <summary>The <c>l_elevatorN</c> nodes.</summary>
    ElevatorLeft,

    /// <summary>The <c>r_elevatorN</c> nodes.</summary>
    ElevatorRight,

    /// <summary>The <c>l_rudderN</c> and <c>r_rudderN</c> nodes.</summary>
    Rudder,
}

/// <summary>
/// The original's control-surface angle solver, in radians and free of any scene node: three stick
/// channels mix into six slot targets, each clamped and then smoothed exponentially at 2/s. The
/// elevators carry a differential roll term as well as the common pitch one, so they act as small
/// ailerons; the rudder slots take the reverse-authority factor and no clamp. Decode with
/// addresses: docs/org/flightModel.md, "The original's control-surface animation".
/// ⚠ <see cref="Advance"/>'s <c>animate</c> argument is the original's player-only guard. When it
/// is false the slots are not written at all, which leaves the surfaces frozen where they stand,
/// and that is what an AI aircraft flies with. Widened to every human pilot for splitscreen.
/// </summary>
public struct ControlSurfaceMix
{
    /// <summary>Aileron gain and clamp, radians per unit of roll (28.6°).</summary>
    public const float AileronRad = 0.5f;

    /// <summary>Elevator common-mode gain and clamp, radians per unit of pitch (34.4°).</summary>
    public const float ElevatorRad = 0.6f;

    /// <summary>Elevator differential gain, radians per unit of roll.</summary>
    public const float ElevatorRollRad = 0.18f;

    /// <summary>Rudder gain, radians per unit of yaw (35°), before reverse authority.</summary>
    public const float RudderRad = 0.61086524f;

    /// <summary>Exponential smoothing rate toward each slot's target, per second.</summary>
    public const float SmoothPerSec = 2f;

    private float _aileronLeft;
    private float _aileronRight;
    private float _elevatorLeft;
    private float _elevatorRight;
    private float _rudder;

    /// <summary>The current smoothed angle of one slot, in radians.</summary>
    public readonly float this[SurfaceSlot slot] => slot switch
    {
        SurfaceSlot.AileronLeft => _aileronLeft,
        SurfaceSlot.AileronRight => _aileronRight,
        SurfaceSlot.ElevatorLeft => _elevatorLeft,
        SurfaceSlot.ElevatorRight => _elevatorRight,
        _ => _rudder,
    };

    /// <summary>The angle one slot settles at for a held stick, in radians: the smoother's fixed
    /// point, and the value the clamps act on.</summary>
    public static float TargetFor(SurfaceSlot slot, FlightInput input, float reverseAuthority) =>
        slot switch
        {
            SurfaceSlot.AileronLeft =>
                Mathf.Clamp(-AileronRad * input.Roll, -AileronRad, AileronRad),
            SurfaceSlot.AileronRight =>
                Mathf.Clamp(AileronRad * input.Roll, -AileronRad, AileronRad),
            SurfaceSlot.ElevatorLeft => Mathf.Clamp(
                (-ElevatorRad * input.Pitch) - (ElevatorRollRad * input.Roll),
                -ElevatorRad, ElevatorRad),
            SurfaceSlot.ElevatorRight => Mathf.Clamp(
                (-ElevatorRad * input.Pitch) + (ElevatorRollRad * input.Roll),
                -ElevatorRad, ElevatorRad),
            _ => -RudderRad * input.Yaw * reverseAuthority,
        };

    /// <summary>Steps every slot one frame toward its target.</summary>
    /// <param name="animate">The player-only guard: false leaves every slot untouched.</param>
    public void Advance(float dt, FlightInput input, float reverseAuthority, bool animate)
    {
        if (!animate)
            return;

        // exp(-rate·dt), so the shape is the same at any frame rate and no step can overshoot.
        float decay = Mathf.Exp(-SmoothPerSec * dt);
        Approach(ref _aileronLeft, TargetFor(SurfaceSlot.AileronLeft, input, reverseAuthority), decay);
        Approach(ref _aileronRight, TargetFor(SurfaceSlot.AileronRight, input, reverseAuthority), decay);
        Approach(ref _elevatorLeft, TargetFor(SurfaceSlot.ElevatorLeft, input, reverseAuthority), decay);
        Approach(ref _elevatorRight, TargetFor(SurfaceSlot.ElevatorRight, input, reverseAuthority), decay);
        Approach(ref _rudder, TargetFor(SurfaceSlot.Rudder, input, reverseAuthority), decay);
    }

    /// <summary>Snaps every slot back to neutral.</summary>
    public void Reset() => this = default;

    private static void Approach(ref float value, float target, float decay) =>
        value = target + ((value - target) * decay);
}
