using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// One decoded rotational step, evaluated independently of <see cref="FlightModel"/> for all
/// three control axes. The original exponentiates <c>dt * omega</c> into a quaternion whose
/// scalar/vector pair is <c>(cos |v|, sin |v| * normalize(v))</c>; converting that quaternion to
/// a basis therefore rotates by <c>2 * |v|</c>.
/// </summary>
public class FlightAxisReplayTests
{
    private const float Dt = 1f / 60f;
    private const float Mph = 0.44704f;
    private const float Speed = 50f * Mph;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DecodedQuaternionStepMatchesPitchYawAndRoll(int axisIndex)
    {
        var stats = DevastatorDynamics();
        var model = new FlightModel(stats);
        model.Reset(Vector3.Zero, Basis.Identity, Speed, 0f);

        var axis = Axis(axisIndex);
        var input = axisIndex switch
        {
            0 => new FlightInput { Pitch = 1f },
            1 => new FlightInput { Yaw = 1f },
            _ => new FlightInput { Roll = 1f },
        };

        float torque = axisIndex switch
        {
            0 => stats.PitchTorque,
            1 => stats.RudderTorque,
            _ => stats.RollTorque,
        };
        float reciprocalInertia = axisIndex switch
        {
            0 => stats.RecInertia.X,
            1 => stats.RecInertia.Y,
            _ => stats.RecInertia.Z,
        };
        float authority = 1f;
        float accumulated = torque * authority * Dt;
        float dampedMomentum = accumulated * Mathf.Exp(-Dt * stats.AngMomentumDamp);
        float bodyRate = dampedMomentum * reciprocalInertia;
        float decodedAngle = 2f * bodyRate * Dt;
        var decodedAttitude = Basis.Identity.Rotated(axis, decodedAngle);

        model.Step(input, Dt);

        float actualRate = Component(model.BodyRates, axisIndex);
        Assert.True(Mathf.IsEqualApprox(actualRate, bodyRate, 1e-6f),
            $"{Name(axisIndex)} angular state: actual {actualRate:R}, decoded {bodyRate:R}; "
            + $"torque {torque:R}, authority {authority:R}, momentum {dampedMomentum:R}");

        Assert.True(BasisNear(model.Attitude, decodedAttitude, 1e-6f),
            $"{Name(axisIndex)} attitude: actual angle {AttitudeAngle(model.Attitude):R}, "
            + $"decoded quaternion angle {decodedAngle:R}; angular state matched at {bodyRate:R}");

        var halfAngleAttitude = Basis.Identity.Rotated(axis, bodyRate * Dt);
        Assert.False(BasisNear(model.Attitude, halfAngleAttitude, 1e-6f),
            $"{Name(axisIndex)} able-to-fail control: the plant still used half the decoded angle");
    }

    private static PlaneStats DevastatorDynamics() => new()
    {
        PitchTorque = 3.3f,
        RudderTorque = 2f,
        RollTorque = 6.8f,
        ReturnRate = 0f,
        AngMomentumDamp = 5f,
        RecInertia = new Vector3(0.85f, 1f, 0.8f),
        FdSpeed = 113f,
        VehWeight = 1f,
        RefArea = 515f,
        DragFactor = 0.62f,
        TurnFadeIn = 10f * Mph,
        TurnFadeOut = 50f * Mph,
        YawLowSpeed = 0.0625f,
        YawHighSpeed = 0.17f,
        YawFadeIn = 10f * Mph,
        YawMax = Speed,
        YawFadeOut = 400f * Mph,
    };

    private static Vector3 Axis(int index) => index switch
    {
        0 => Vector3.Right,
        1 => Vector3.Up,
        _ => Vector3.Back,
    };

    private static float Component(Vector3 value, int index) => index switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };

    private static string Name(int index) => index switch
    {
        0 => "pitch",
        1 => "yaw",
        _ => "roll",
    };

    private static float AttitudeAngle(Basis attitude)
    {
        float trace = attitude.X.X + attitude.Y.Y + attitude.Z.Z;
        return Mathf.Acos(Mathf.Clamp((trace - 1f) * 0.5f, -1f, 1f));
    }

    private static bool BasisNear(Basis actual, Basis expected, float tolerance) =>
        actual.X.DistanceTo(expected.X) <= tolerance
        && actual.Y.DistanceTo(expected.Y) <= tolerance
        && actual.Z.DistanceTo(expected.Z) <= tolerance;
}
