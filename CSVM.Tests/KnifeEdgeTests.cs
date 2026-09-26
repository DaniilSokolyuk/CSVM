using System.IO;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Tooling;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The knife-edge: no dedicated sag term and no verticality scale on the nose-chase either, the
/// decoded bank→yaw coupling and the weathervane produce the drift on their own. Decode:
/// docs/org/flightModel.md's "lift_accel_rate is a lag toward a target velocity".
/// ⚠ Do not reintroduce a nose-sag term; see that section for why it regresses the onset.
/// The probe recipe lives in <see cref="Probes.KnifeEdge"/>, not restated here.
/// </summary>
public class KnifeEdgeTests
{
    private const float Dt = 1f / 60f;

    private static readonly string[] AllPlanes =
    {
        "player_bhawk", "player_pfighter", "player_fury", "player_warhawk", "player_autogyro",
        "player_avenger", "player_balmoral", "player_brigand", "player_fbrand", "player_kestrel",
        "player_peacemaker",
    };

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>Wings level, nose 45° up, flight path along the nose, stick centred: every torque
    /// in the model is identically zero there, so one step must leave the attitude exactly where
    /// it was. Pins that a retired sag term keyed on wing verticality (docs/org/flightModel.md)
    /// cannot leak back in and rotate the nose here.</summary>
    [Fact]
    public void NothingRotatesTheAttitudeInAWingsLevelPullWithTheStickCentred()
    {
        var m = new FlightModel(Bhawk());
        var entry = Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(45f));
        m.Reset(Vector3.Zero, entry, 135f, 1f);
        var before = m.Attitude;
        m.Step(new FlightInput { Throttle = 1f }, Dt);

        float moved = Mathf.RadToDeg((-m.Attitude.Z).AngleTo(-before.Z));
        Assert.True(moved < 1e-4f,
            $"a wings-level nose-up aircraft with its path on the nose must not rotate at all "
            + $"(nose moved {moved:0.0000}°), a sag term keyed on wing verticality leaks here");
    }

    /// <summary>The discriminating signature: the original's knife-edge drifts for the whole 36 s and
    /// never finds an equilibrium. A bounded sag reaches its bound inside a second, which puts
    /// essentially none of the total sag in the last third of the hold; a genuine drift puts about a
    /// third of it there. The autogyro remains drifting but can settle late; the fixed-wing
    /// airframes retain the filmed Bloodhawk's unbounded shape.</summary>
    [ExtractedDataFact]
    public void TheKnifeEdgeKeepsDriftingOnEveryAirframe()
    {
        foreach (string plane in AllPlanes)
        {
            var r = Probes.KnifeEdge(ZrdrPath, plane);
            Assert.True(r.Error == null, $"{plane}: {r.Error ?? "-"}");
            foreach (var run in r.Runs)
            {
                Assert.True(run.DriftDegS > 0.2,
                    $"{plane} @{run.EntryMph:0} mph: nose drift {run.DriftDegS:0.00} °/s, the "
                    + "knife-edge must keep sagging (the original drifts 0.69–0.89 °/s)");
                Assert.True(plane == "player_autogyro" || run.SettledFrac > 0.12,
                    $"{plane} @{run.EntryMph:0} mph: only {run.SettledFrac:0.00} of the sag arrived "
                    + "in the last third, that is a bounded sag settling, not the original's drift");
            }
        }
    }

    /// <summary>α at the knife-edge stays inside the <c>liftAOAs</c> window on every airframe: the
    /// blend may engage past the low edge (peaks 0.77–5.68°, tightest on the Bloodhawk at 143 mph)
    /// but never saturates to fully nose-faked airflow. The Balmoral stays under the low edge,
    /// which keeps refuting the invented "0.1° inside the ramp" figure no instrument reproduces;
    /// see docs/org/flightModel.md.</summary>
    [ExtractedDataFact]
    public void KnifeEdgeAlphaStaysInsideTheLiftAoaWindowOnEveryAirframe()
    {
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            double lowDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(stats.LiftAoaCosLo, -1f, 1f)));
            double highDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(stats.LiftAoaCosHi, -1f, 1f)));
            var r = Probes.KnifeEdge(ZrdrPath, plane);
            Assert.True(r.Error == null, $"{plane}: {r.Error ?? "-"}");
            foreach (var run in r.Runs)
            {
                Assert.True(run.AlphaPeak < highDeg,
                    $"{plane} @{run.EntryMph:0} mph: α peaks at {run.AlphaPeak:0.00}° against a "
                    + $"liftAOAs upper edge of {highDeg:0.00}°, and a knife-edge must never reach "
                    + "fully nose-faked airflow");
                if (plane == "player_balmoral")
                {
                    Assert.True(run.AlphaPeak < lowDeg,
                        $"player_balmoral @{run.EntryMph:0} mph: α peaks at {run.AlphaPeak:0.00}° "
                        + $"against the {lowDeg:0.00}° low edge; the refuted claim that it "
                        + "knife-edges inside the ramp must not come true quietly");
                }
            }
        }
    }

    // The Bloodhawk's real dynamics, the placeholder `PlaneStats()` defaults are the
    // executable's fallback aircraft and carry a different inertia and damping.
    private static PlaneStats Bhawk() => new()
    {
        PitchTorque = 3.3f,
        RollTorque = 7.5f,
        RudderTorque = 2f,
        ReturnRate = 3f,
        AngMomentumDamp = 5f,
        RecInertia = new Vector3(1.18f, 1f, 1.1f),
        FdSpeed = 135f,
        VehWeight = 1900f,
        RefArea = 330f,
        DragFactor = 0.37f,
    };
}
