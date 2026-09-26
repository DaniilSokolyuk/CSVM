using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The <c>level_off_rate</c> auto-level torque is decoded and unreachable: the vehicle parser's
/// token table accepts the key, the torque reads it out of the def, and no shipped def authors it,
/// so the term is zero on every airframe (docs/org/flightModel.md, "The `level_off_rate`
/// auto-level"). These pins are what that class rests on, the data census, and the behaviour a
/// non-zero term would show. A def that started authoring the key, or an auto-level term invented
/// in the plant, fails here.
/// </summary>
public class LevelOffRateAbsenceTests
{
    private const float Dt = 1f / 60f;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The census: every <c>dynamics</c> block in the shipped data, the ten keys each one
    /// authors, and the eleventh the parser accepts that none of them does. <c>return_rate</c> is
    /// the able-to-fail control, a reader that stopped seeing the blocks at all would report zero
    /// of both, and it is the sibling centring rate, so it is the key an auto-level would sit
    /// beside.</summary>
    [ExtractedDataFact]
    public void NoShippedDynamicsBlockAuthorsTheLevelOffRate()
    {
        var root = Zrdr.LoadFile(ZrdrPath, "vehicle.json")[0] as List<object?>;
        Assert.NotNull(root);
        int blocks = 0, withReturnRate = 0, withLevelOff = 0;
        for (int i = 0; i + 1 < root!.Count; i += 2)
        {
            if (root[i + 1] is not List<object?> props)
                continue;
            if (ZrdrDict.FromAlternating(props).Dict("dynamics") is not { } keys)
                continue;
            blocks++;
            if (keys.TryFloat("return_rate", out _))
                withReturnRate++;
            if (keys.Has("level_off_rate"))
                withLevelOff++;
        }

        Assert.True(blocks > 0, "no dynamics block was read at all, so this census measures nothing");
        Assert.Equal(blocks, withReturnRate);
        Assert.Equal(0, withLevelOff);
    }

    /// <summary>The behaviour half: no roll rate appears in a banked aircraft with both sticks
    /// centred, which is exactly the state the original's auto-level is gated on. Rolling the body
    /// up-vector onto world up is mostly a roll-axis rotation, so a ported term would show here
    /// first. ⚠ The bank ANGLE is not the measure: the decoded bank coupling's yaw and pitch
    /// reorient the airframe every frame of a banked cruise, which moves a wing-height reading
    /// without any roll torque behind it.</summary>
    [ExtractedDataFact]
    public void ABankedAircraftWithCentredSticksGrowsNoRollRate()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        // The AI force path: the weathervane and the stall nose-drop are both player-only, so this
        // arm carries no attitude-restoring term at all and an auto-level would stand alone in it.
        var m = new FlightModel(stats, aiForcePath: true);
        var banked = new Basis(Vector3.Back, Mathf.DegToRad(60f));
        m.Reset(new Vector3(0f, 1000f, 0f), banked, stats.FdSpeed * 0.8f, 1f);
        float worst = 0f;
        for (int i = 0; i < 300; i++)
        {
            m.Step(new FlightInput { Throttle = 1f }, Dt);
            worst = Mathf.Max(worst, Mathf.Abs(m.BodyRates.Z));
        }

        Assert.True(worst < 1e-4f,
            $"a roll rate of {worst:0.0000000} rad/s appeared with no roll command and no shipped term to make it");
    }
}
