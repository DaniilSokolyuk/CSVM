using System.IO;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The AI actor seam's input driver, engine-free: <see cref="AiPilot"/> over a real
/// <see cref="FlightModel"/> on the shipped Bloodhawk stats, on the fixed sim dt. Pins that the
/// control law holds a level course, converges onto an ordered heading, and that its orders are
/// mutable mid-flight: a retarget and a new altitude issued between steps are flown to with no
/// respawn or rebuild. Deliberately not re-pinned when the placeholder law was replaced by the
/// original's (<see cref="AiControlLaw"/>); its own arithmetic is pinned by
/// <see cref="AiControlLawTests"/> against the decode instead.
/// </summary>
public class AiPilotTests
{
    private const float Dt = 1f / 60f;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [ExtractedDataFact]
    public void HoldsCourseFliesOrderedTurnsAndTakesMidFlightRetargets()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        var model = new FlightModel(stats);
        model.Reset(new Vector3(0f, 400f, 0f), Basis.Identity, 80f, 0.85f); // nose -Z = heading 0
        var pilot = new AiPilot { TargetHeadingDeg = 0f, TargetAltitude = 400f };

        void Fly(float seconds)
        {
            for (int i = 0; i < (int)(seconds / Dt); i++)
                model.Step(pilot.Next(model, Dt), Dt);
        }

        float HeadingErrDeg()
        {
            var nose = -model.Attitude.Z;
            float headingDeg = AiPilot.HeadingDegOf(nose);
            return Mathf.Wrap(pilot.TargetHeadingDeg - headingDeg, -180f, 180f);
        }

        // Straight and level: 20 s on the spawn course stays on heading and altitude.
        Fly(20f);
        Assert.True(Mathf.Abs(HeadingErrDeg()) < 5f, $"level hold drifted {HeadingErrDeg():0.0}° off course");
        Assert.InRange(model.Position.Y, 340f, 460f);
        Assert.True(model.Speed > 40f, $"level hold bled speed to {model.Speed:0.0} m/s");

        // An ordered 90° turn converges and recaptures level flight.
        pilot.TargetHeadingDeg = 90f;
        Fly(30f);
        Assert.True(Mathf.Abs(HeadingErrDeg()) < 6f, $"turn to 090 ended {HeadingErrDeg():0.0}° off");
        Assert.InRange(model.Position.Y, 320f, 480f);

        // Orders are mutable between steps: retarget across the compass AND re-order the
        // altitude on the same pilot, mid-flight, the mission-script surface's requirement.
        pilot.TargetHeadingDeg = 225f;
        pilot.TargetAltitude = 500f;
        Fly(45f);
        Assert.True(Mathf.Abs(HeadingErrDeg()) < 6f, $"retarget to 225 ended {HeadingErrDeg():0.0}° off");
        Assert.InRange(model.Position.Y, 440f, 560f);
        Assert.True(model.Speed > 40f, $"the flight ended stalled at {model.Speed:0.0} m/s");
    }

    /// <summary>Flown: the ported aim makes an AI TRACK its leg rather than chase its node. Entered
    /// 120 m off a long straight leg it stays out there, converging by the decoded tenth, where the
    /// same flight aimed at the node pulls in hard, the able-to-fail control.
    /// ⚠ It does NOT settle the roll. Both flights still wallow (peak bank ~90°, mean ~48°), so
    /// `BL-387` survives this and its cause is in the plant, not in the aim point.</summary>
    [ExtractedDataFact]
    public void ThePatrolAimTracksTheLegInsteadOfChasingTheNode()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");

        // 40 s of a straight leg due north, entered 120 m to the west of it and already on
        // heading, so the only thing to work off is the cross-track error.
        float CrossTrackAfter40s(bool aimAtNode)
        {
            var model = new FlightModel(stats);
            model.Reset(new Vector3(-120f, 400f, 0f), Basis.Identity, 80f, 0.85f);
            var legStart = new Vector3(0f, 400f, 0f);
            var node = new Vector3(0f, 400f, -8000f);
            float throttle = 0.85f;
            for (int i = 0; i < (int)(40f / Dt); i++)
            {
                var aim = aimAtNode ? node : AiPilot.PatrolAim(model.Position, legStart, node);
                var input = AiControlLaw.Steer(model, aim, Vector3.Zero, AiLawParams.Cruise,
                    throttle, Dt);
                throttle = input.Throttle;
                model.Step(input, Dt);
            }
            return Mathf.Abs(model.Position.X);
        }

        float chasing = CrossTrackAfter40s(aimAtNode: true);
        float tracking = CrossTrackAfter40s(aimAtNode: false);
        Assert.True(chasing < 70f, $"the control did not converge on its node: {chasing:0} m off");
        Assert.True(tracking > 80f,
            $"the ported aim converged like a node chase: {tracking:0} m off against {chasing:0} m");
    }

    /// <summary>`SteeringPatrol` reports what the last step actually did, which is what F13's
    /// leashes colour on: a bare-orders pilot is not flying a net however its mode reads, and a
    /// net-following one is: the distinction between "holds that node" and "is flying at it".</summary>
    [ExtractedDataFact]
    public void SteeringPatrolReportsWhetherTheLastStepFlewTheNet()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        var model = new FlightModel(stats);
        model.Reset(new Vector3(0f, 400f, 0f), Basis.Identity, 80f, 0.85f);

        var pilot = new AiPilot { TargetHeadingDeg = 0f, TargetAltitude = 400f };
        Assert.False(pilot.SteeringPatrol);   // nothing has stepped yet
        pilot.Next(model, Dt);
        Assert.False(pilot.SteeringPatrol);   // bare orders: there is no net to fly

        var net = new CSVM.Mech3.AiNet
        {
            Id = 3,
            Name = "TestRing",
            Nodes = new[]
            {
                new CSVM.Mech3.AiNetNode(new Vector3(0f, 400f, -2000f), System.Array.Empty<float>()),
                new CSVM.Mech3.AiNetNode(new Vector3(2000f, 400f, -2000f), System.Array.Empty<float>()),
            },
            Edges = new[] { (0, 1) },
        };
        pilot.Patrol = new AiNetFollower(net, new System.Random(1));
        pilot.Next(model, Dt);
        Assert.True(pilot.SteeringPatrol);
        // Seated on node 0, the nearest, and flying its one edge: the leash points at the far end.
        Assert.Equal(0, pilot.Patrol.LegStartIndex);
        Assert.Equal(1, pilot.Patrol.CurrentIndex);

        // Steering something else clears it on the very next step, even though the follower
        // still holds its node, which is the case a leash must draw dimmed rather than bright.
        pilot.Patrol = null;
        pilot.Next(model, Dt);
        Assert.False(pilot.SteeringPatrol);
    }

    /// <summary>A wingman whose leader has left play and whose quarry is dead falls to the netless
    /// arm of <see cref="AiPilot.Next"/>, which PROJECTS the two order fields without writing them,
    /// so it holds the bearing its last pursuit wrote and dives toward the altitude that aeroplane
    /// died at. Nothing inside the pilot re-derives them: the host hands it the lost leader's own
    /// net (<c>CampaignDirector.TakeLostLeadersNets</c>, flown in <c>wingman-lost-leader</c> where a
    /// leader can really leave play), and that seat is what re-derives both, pinned here.</summary>
    [ExtractedDataFact]
    public void ALeaderlessNetlessWingmanHoldsItsPursuitBearingUntilItInheritsANet()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        const float staleBearingDeg = 215f;   // where the quarry was when the last pursuit step ran
        const float staleAltitudeM = 90f;     // and the altitude it was shot down at

        // The state CM05 leaves `wingman_2` in once `devastator_1` is destroyed and its own
        // target dies: an escort block with no leader in play and no live quarry. A null leader
        // fails `Escort is { Leader.InPlay: true }` exactly as a crashed one does.
        AiPilot Leaderless() => new()
        {
            Escort = new AiEscort(),
            TargetHeadingDeg = staleBearingDeg,
            TargetAltitude = staleAltitudeM,
        };

        var model = new FlightModel(stats);
        model.Reset(new Vector3(0f, 400f, 0f), Basis.Identity, 80f, 0.85f);
        var pilot = Leaderless();
        for (int i = 0; i < (int)(10f / Dt); i++)
            model.Step(pilot.Next(model, Dt), Dt);

        Assert.Equal(staleBearingDeg, pilot.TargetHeadingDeg, 3);
        Assert.Equal(staleAltitudeM, pilot.TargetAltitude, 3);
        Assert.True(model.Position.Y < 300f,
            $"the held orders did not fly it down toward the dead quarry: {model.Position.Y:0} m");

        // The seat the host makes on the leader's own net, which is all `SeatOnNet` does to the
        // pilot. Both order fields are re-derived on the very first step after it.
        var net = new CSVM.Mech3.AiNet
        {
            Id = 11,
            Name = "LostLeadersNet",
            Nodes = new[]
            {
                new CSVM.Mech3.AiNetNode(new Vector3(0f, 500f, -2000f), System.Array.Empty<float>()),
                new CSVM.Mech3.AiNetNode(new Vector3(2000f, 500f, -2000f), System.Array.Empty<float>()),
            },
            Edges = new[] { (0, 1) },
        };
        pilot.Escort = null;
        pilot.Patrol = new AiNetFollower(net, new System.Random(1));
        pilot.Next(model, Dt);

        Assert.True(Mathf.Abs(Mathf.Wrap(pilot.TargetHeadingDeg - staleBearingDeg, -180f, 180f)) > 1f,
            $"the seated pilot still holds the dead quarry's bearing, {pilot.TargetHeadingDeg:0.0}°");
        Assert.True(Mathf.Abs(pilot.TargetAltitude - staleAltitudeM) > 1f,
            $"…and still holds the altitude it died at, {pilot.TargetAltitude:0} m");
        Assert.True(pilot.SteeringPatrol, "the seated pilot is not flying the net it was given");
    }

    /// <summary>The stun mask (<c>FUN_004200d0</c>): a stunned pilot hands back neutral stick and
    /// rudder with the throttle lever left where it was, for exactly the seconds asked, and then
    /// steers again; a second stun while stunned overwrites the clock. With a mode machine the
    /// stun lives in its Stunned mode; without one the pilot keeps its own countdown, and the
    /// mask is the same either way. Both are checked here.</summary>
    [ExtractedDataFact]
    public void AStunnedPilotHandsBackNeutralSticksForTheDurationAndThenResumes()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");

        foreach (bool withMachine in new[] { false, true })
        {
            var model = new FlightModel(stats);
            model.Reset(new Vector3(0f, 400f, 0f), Basis.Identity, 80f, 0.85f);
            // Ordered 90° off the nose, so an unmasked step always deflects a control.
            var pilot = new AiPilot { TargetHeadingDeg = 90f, TargetAltitude = 400f };
            if (withMachine)
                pilot.Machine = new AiModeMachine(new System.Random(3));

            var steering = pilot.Next(model, Dt);
            Assert.True(Mathf.Abs(steering.Roll) + Mathf.Abs(steering.Pitch) + Mathf.Abs(steering.Yaw) > 0.01f,
                $"the unstunned pilot ({(withMachine ? "machine" : "bare")}) did not deflect a control");
            float lever = pilot.Throttle;

            Assert.False(pilot.IsStunned);
            pilot.Stun(1f);
            Assert.True(pilot.IsStunned);
            Assert.Equal(1f, pilot.StunRemainingS, 3);

            // A whole second of neutral sticks, the lever untouched, the model still flying.
            for (int i = 0; i < 59; i++)
            {
                var input = pilot.Next(model, Dt);
                Assert.Equal(0f, input.Roll);
                Assert.Equal(0f, input.Pitch);
                Assert.Equal(0f, input.Yaw);
                Assert.Equal(lever, input.Throttle, 5);
                Assert.True(pilot.IsStunned, $"stun ended early at step {i}");
                model.Step(input, Dt);
            }

            // Re-stunned mid-way: the clock is overwritten, not accumulated.
            pilot.Stun(0.5f);
            Assert.Equal(0.5f, pilot.StunRemainingS, 3);
            for (int i = 0; i < 31; i++)
                model.Step(pilot.Next(model, Dt), Dt);
            Assert.False(pilot.IsStunned, $"the {(withMachine ? "machine" : "bare")} pilot stayed stunned past the overwritten clock");
            Assert.Equal(0f, pilot.StunRemainingS);

            // And it steers again on its own.
            var resumed = pilot.Next(model, Dt);
            Assert.True(Mathf.Abs(resumed.Roll) + Mathf.Abs(resumed.Pitch) + Mathf.Abs(resumed.Yaw) > 0.01f,
                "the recovered pilot did not take the controls back");

            // The respawn reset clears a running stun outright.
            pilot.Stun(5f);
            Assert.True(pilot.IsStunned);
            pilot.ClearStun();
            Assert.False(pilot.IsStunned);
            Assert.Equal(0f, pilot.StunRemainingS);

            // Zero seconds is not a stun.
            pilot.Stun(0f);
            Assert.False(pilot.IsStunned);
        }
    }

    /// <summary>The climb-out breaks to the right of the aeroplane's own GROUND TRACK, which is
    /// what makes two aircraft that both detect each other diverge instead of both pulling
    /// straight up along converging paths. Taken off the track and not the airframe, so roll and
    /// pitch never flip which way a pilot breaks.</summary>
    [Fact]
    public void TheClimbOutBreaksRightOfItsOwnGroundTrack()
    {
        var pos = new Vector3(0f, 400f, 0f);

        // Flying north (nose −Z): right is +X. Flying east (+X): right is +Z.
        var north = AiPilot.ClimbOutAim(pos, new Vector3(0f, 0f, -100f));
        Assert.True(north.X > 0f, $"a northbound break went to X={north.X:0}");
        Assert.Equal(0f, north.Z, 3);
        var east = AiPilot.ClimbOutAim(pos, new Vector3(100f, 0f, 0f));
        Assert.True(east.Z > 0f, $"an eastbound break went to Z={east.Z:0}");

        // It is still a climb, and the vertical part is the original's own 1000 m.
        Assert.Equal(1400f, north.Y, 3);

        // A steep climb or dive keeps its horizontal track, so the break does not flip.
        var climbing = AiPilot.ClimbOutAim(pos, new Vector3(0f, 90f, -40f));
        Assert.True(climbing.X > 0f, "a climbing aeroplane broke the wrong way");
        var diving = AiPilot.ClimbOutAim(pos, new Vector3(0f, -90f, -40f));
        Assert.True(diving.X > 0f, "a diving aeroplane broke the wrong way");

        // Two aircraft meeting head-on break to OPPOSITE sides of the shared line, which is the
        // whole point: the same rule applied by both parties separates them.
        var a = AiPilot.ClimbOutAim(pos, new Vector3(0f, 0f, -100f));
        var b = AiPilot.ClimbOutAim(pos, new Vector3(0f, 0f, 100f));
        Assert.True(a.X > 0f && b.X < 0f, $"both broke to X={a.X:0} and X={b.X:0}");

        // A hover with no horizontal track still yields a usable aim point rather than a NaN.
        var still = AiPilot.ClimbOutAim(pos, Vector3.Zero);
        Assert.True(still.IsFinite() && still.Y > pos.Y);
    }

    /// <summary>The decoded climb-out an escort flies has NO lateral term at all: the aim point is
    /// the aeroplane's own position with 1000 m added to Y, whatever way it is pointing
    /// (<c>FUN_0041e760</c> at <c>0x0041e7c8</c>). It is the same point the net follower's own
    /// case 3 builds; only the parameter table differs between the two laws.</summary>
    [Fact]
    public void TheDecodedClimbOutIsPurelyVertical()
    {
        var pos = new Vector3(-1378f, 109f, -1706f);
        var aim = AiPilot.ClimbOutAim(pos);

        Assert.Equal(pos.X, aim.X, 3);
        Assert.Equal(pos.Z, aim.Z, 3);
        Assert.Equal(pos.Y + 1000f, aim.Y, 3);

        // The break the netted overload adds is exactly what this one must not have.
        var broken = AiPilot.ClimbOutAim(pos, new Vector3(0f, 0f, -100f));
        Assert.True(broken.X > pos.X + 500f, $"the netted break went to X={broken.X:0}");
    }

    /// <summary>The patrol aim carries 0.9 of the aeroplane's own cross-track error onto the node,
    /// capped at 200 m (<c>FUN_0041d1f0</c> case 0). What that buys is a commanded course nearly
    /// PARALLEL to the leg: the residual convergence is a tenth of the offset, which is small
    /// enough that the law's sign-relay roll stops banking hard each way (`BL-387`).</summary>
    [Fact]
    public void ThePatrolAimCarriesMostOfItsOwnCrossTrackErrorOntoTheNode()
    {
        // A leg due north (−Z) from the origin, and an aeroplane 100 m to the west of it.
        var legStart = new Vector3(0f, 400f, 0f);
        var node = new Vector3(0f, 400f, -2000f);
        var pos = new Vector3(-100f, 400f, -1000f);

        var aim = AiPilot.PatrolAim(pos, legStart, node);
        Assert.Equal(-90f, aim.X, 3);          // 0.9 of the 100 m offset, on the aeroplane's side
        Assert.Equal(node.Z, aim.Z, 3);        // along-leg distance is untouched
        Assert.Equal(node.Y, aim.Y, 3);

        // The residual is what converges, and it points back at the leg rather than away from it.
        Assert.True(Mathf.Abs(aim.X - pos.X) < Mathf.Abs(node.X - pos.X),
            "the aim point sits further from the aeroplane than the node does");

        // On the leg, the aim IS the node; there is nothing to carry.
        var onLeg = AiPilot.PatrolAim(new Vector3(0f, 400f, -1000f), legStart, node);
        Assert.Equal(node.X, onLeg.X, 3);
        Assert.Equal(node.Y, onLeg.Y, 3);

        // Past the cap the displacement is 200 m, not 0.9 of a kilometre.
        var far = AiPilot.PatrolAim(new Vector3(-1000f, 400f, -1000f), legStart, node);
        Assert.Equal(-200f, far.X, 3);

        // The vertical component is carried the same way: off is a full 3-vector.
        var high = AiPilot.PatrolAim(new Vector3(0f, 500f, -1000f), legStart, node);
        Assert.Equal(490f, high.Y, 3);

        // A degenerate leg has no cross-track direction, and must not divide by its own zero.
        var degenerate = AiPilot.PatrolAim(pos, node, node);
        Assert.Equal(node, degenerate);
    }

    /// <summary>The aspect test (<c>FUN_0041d9f0</c> at <c>0x0041dd49</c>) takes BOTH of the
    /// victim's cones: a victim coming at us and a victim we sit behind read alike, and only a
    /// beam aspect leaves the pursuer flying to the lead point. The cone is the pilot's own
    /// <c>quick_draw_angle</c>, so a better pilot goes to a firing solution from further off the
    /// axis.</summary>
    [Fact]
    public void TheAspectTestTakesBothOfTheVictimsCones()
    {
        var toQuarry = new Vector3(0f, 0f, -300f);   // the victim is dead ahead of us
        var comingAtUs = new Vector3(0f, 0f, 1f);    // its nose points back down the line
        var flyingAway = new Vector3(0f, 0f, -1f);
        var beam = new Vector3(1f, 0f, 0f);

        Assert.True(AiPilot.IsOnGunAxis(toQuarry, comingAtUs, 50f));
        Assert.True(AiPilot.IsOnGunAxis(toQuarry, flyingAway, 50f));   // the tail chase, decoded
        Assert.False(AiPilot.IsOnGunAxis(toQuarry, beam, 50f));

        // The cone is the pilot's: 60° off the axis is beam to a rating-1 pilot (50°) and on-axis
        // to a rating-9 one (89°).
        var off60 = new Vector3(Mathf.Sin(Mathf.DegToRad(60f)), 0f, -Mathf.Cos(Mathf.DegToRad(60f)));
        Assert.False(AiPilot.IsOnGunAxis(toQuarry, off60, 50f));
        Assert.True(AiPilot.IsOnGunAxis(toQuarry, off60, 89f));

        // Degenerate inputs never claim an axis.
        Assert.False(AiPilot.IsOnGunAxis(Vector3.Zero, comingAtUs, 50f));
        Assert.False(AiPilot.IsOnGunAxis(toQuarry, Vector3.Zero, 50f));
    }

    /// <summary>The merge test's four decoded gates (<c>FUN_0041d9f0</c> at <c>0x0041e130</c>):
    /// inside 400 m, we fly at the victim, the victim flies at us, and either party more than
    /// ~37° off the line of sight disarms it. Pure geometry, so it is pinned directly rather
    /// than through a flown pursuit (which would need a scene tree for the quarry).</summary>
    [Fact]
    public void TheMergeTestArmsOnlyOnATrueHeadOnInsideItsRange()
    {
        var closing = new Vector3(0f, 0f, -100f);    // us, flying at a victim dead ahead
        var oncoming = new Vector3(0f, 0f, 100f);    // the victim, flying back at us

        Assert.True(AiPilot.IsMerging(new Vector3(0f, 0f, -300f), closing, oncoming));
        Assert.False(AiPilot.IsMerging(new Vector3(0f, 0f, -500f), closing, oncoming));
        Assert.False(AiPilot.IsMerging(new Vector3(0f, 0f, -400f), closing, oncoming));  // exclusive

        // A tail chase and a beam pass are both merge-free however close they get.
        Assert.False(AiPilot.IsMerging(new Vector3(0f, 0f, -100f), closing, closing));
        Assert.False(AiPilot.IsMerging(new Vector3(0f, 0f, -100f), closing, new Vector3(100f, 0f, 0f)));
        Assert.False(AiPilot.IsMerging(new Vector3(0f, 0f, -100f), new Vector3(100f, 0f, 0f), oncoming));

        // The closure cone: 30° off the line of sight still merges, 40° does not (cos 37° ≈ 0.8).
        Vector3 Off(float deg, float sign) => new(
            sign * 100f * Mathf.Sin(Mathf.DegToRad(deg)), 0f, sign * 100f * Mathf.Cos(Mathf.DegToRad(deg)));
        Assert.True(AiPilot.IsMerging(new Vector3(0f, 0f, -300f), Off(30f, -1f), Off(30f, 1f)));
        Assert.False(AiPilot.IsMerging(new Vector3(0f, 0f, -300f), Off(40f, -1f), oncoming));
        Assert.False(AiPilot.IsMerging(new Vector3(0f, 0f, -300f), closing, Off(40f, 1f)));

        // A stationary party has no closure to measure and never merges.
        Assert.False(AiPilot.IsMerging(new Vector3(0f, 0f, -300f), Vector3.Zero, oncoming));
        Assert.False(AiPilot.IsMerging(new Vector3(0f, 0f, -300f), closing, Vector3.Zero));
    }

    /// <summary>The merge's vertical bias ramp: dive at or below 50 mph, climb at or above
    /// 90 mph, linear between and zero at the 70 mph the aim velocity collapses to. ⚠ These are
    /// metres per unit of a UNIT vector's horizontal magnitude, so the whole term is worth 0.3 m
    /// at most, see <see cref="AiPilot.MergeVerticalBias"/>.</summary>
    [Fact]
    public void TheMergeVerticalBiasRampsFromDiveToClimbAcrossItsTwoSpeeds()
    {
        Assert.Equal(-0.3f, AiPilot.MergeVerticalBias(10f), 4);
        Assert.Equal(-0.3f, AiPilot.MergeVerticalBias(22.352f), 4);
        Assert.Equal(0f, AiPilot.MergeVerticalBias(31.292799f), 4);
        Assert.Equal(0.3f, AiPilot.MergeVerticalBias(40.2336f), 4);
        Assert.Equal(0.3f, AiPilot.MergeVerticalBias(120f), 4);
        Assert.True(AiPilot.MergeVerticalBias(26f) < 0f);   // still diving below 70 mph
        Assert.True(AiPilot.MergeVerticalBias(36f) > 0f);   // climbing above it
    }
}
