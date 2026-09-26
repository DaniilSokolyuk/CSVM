using System.IO;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The decoded collision restitution: <c>FUN_0048d7f0</c>'s normal-only impulse,
/// scaled by <c>f_lin · bounce_factor</c>, ported as <see cref="FlightModel.BounceNormalSpeed"/>.
/// Decode: docs/org/flightModel.md, "Collision response and bounce_factor".
/// Its three separately-wrong-able parts (restitution, the doubled rotation term, the rebound/spin
/// partition) are asserted individually here rather than through an aggregate "feels bouncy" probe.
/// </summary>
public class BounceRestitutionTests
{
    private const float Sink = 30f;      // m/s of closing speed along the normal in every case here

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The restitution proper, isolated: a non-rotating contact on an arm along the normal
    /// rebounds at exactly <c>bounce_factor</c>. That is the decode's [0, 0.6] ceiling touched, and
    /// the number measured on flat ground (0.62 ± 0.19 against a shipped 0.60).</summary>
    [Fact]
    public void AnAxialNonRotatingContactReboundsAtExactlyBounceFactor()
    {
        var m = Plant(0.6f);
        Rest(m);

        float outward = m.BounceNormalSpeed(Vector3.Down * Sink, Vector3.Up, new Vector3(0f, 2f, 0f));

        Assert.Equal(0.6f * Sink, outward, 3);
    }

    /// <summary>The rebound is the SHIPPED constant's, not a hardcoded one: the same contact on a
    /// plant carrying the executable's 0.8 fallback rebounds at 0.8.</summary>
    [Fact]
    public void TheReboundScalesWithTheLoadedBounceFactor()
    {
        var authored = Plant(0.6f);
        var fallback = Plant(0.8f);
        Rest(authored);
        Rest(fallback);

        var arm = new Vector3(0f, 2f, 0f);
        Assert.Equal(0.6f * Sink,
            authored.BounceNormalSpeed(Vector3.Down * Sink, Vector3.Up, arm), 3);
        Assert.Equal(0.8f * Sink,
            fallback.BounceNormalSpeed(Vector3.Down * Sink, Vector3.Up, arm), 3);
    }

    /// <summary>The contact-point velocity's rotational term is doubled, so a rotating contact
    /// leaves the surface faster than <c>bounce_factor</c> permits, the decode's answer to the
    /// above-0.6 flat-ground readings (docs/org/flightModel.md), which raising the constant instead
    /// would delete. A 2 m arm with 1 rad/s yaw gives 20.7 m/s outward, above the 18 m/s the same
    /// contact gives at rest.</summary>
    [Fact]
    public void TheContactPointRotationTermIsDoubledAndUnbounded()
    {
        var m = Plant(0.6f);
        Rest(m);
        m.BodyRates = new Vector3(0f, 1f, 0f);   // yaw left: ω × (2,0,0) = (0,0,−2)

        float outward = m.BounceNormalSpeed(
            Vector3.Forward * Sink, Vector3.Back, new Vector3(2f, 0f, 0f));

        Assert.Equal(20.69f, outward, 1);
        Assert.True(outward > 0.6f * Sink,
            $"the doubled term must be able to exceed bounce_factor outward={outward:0.00}");
    }

    /// <summary>The rebound/spin partition runs the opposite direction from the summary sentence
    /// carried with this decode ("a wingtip throws most of the impact into rotation"): a long arm
    /// partitions more into rebound. See docs/org/flightModel.md's withdrawal note.
    /// Asserted as a monotone sequence first, so it fails on direction rather than rounding.</summary>
    [Fact]
    public void ALongerContactArmPartitionsMoreIntoReboundNotLess()
    {
        var m = Plant(0.6f);
        Rest(m);

        // Arm along body X against a +Y normal: r × J is perpendicular to both, so sinθ = 1 and the
        // partition is at its strongest for a given length.
        float Rebound(float armM) =>
            m.BounceNormalSpeed(Vector3.Down * Sink, Vector3.Up, new Vector3(armM, 0f, 0f));

        float near = Rebound(0.5f), mid = Rebound(1f), tip = Rebound(5f);
        Assert.True(near < mid && mid < tip,
            $"rebound must grow with arm length near={near:0.000} mid={mid:0.000} tip={tip:0.000}");

        // The angular share is the INERTIA-multiplied vector (the original divides by the authored
        // reciprocal moments before measuring it), so with roll recI 1.1 these sit slightly above
        // the recI-multiplied values the earlier reading produced (12.1 / 16.4).
        Assert.Equal(12.8f, mid, 1);    // f_lin ≈ 0.712
        Assert.Equal(16.7f, tip, 1);    // f_lin ≈ 0.925
        Assert.True(tip < 0.6f * Sink, "f_lin · bounce_factor stays bounded by the authored 0.6");
    }

    /// <summary>No surface dependence anywhere: the identical contact geometry rotated from a floor
    /// onto a vertical face rebounds identically. The original has no verticality test and no
    /// material lookup, so the flat-versus-vertical split must NOT arrive here as a per-surface
    /// coefficient.</summary>
    [Fact]
    public void TheImpulseHasNoSurfaceDependence()
    {
        var m = Plant(0.6f);
        Rest(m);

        float floor = m.BounceNormalSpeed(Vector3.Down * Sink, Vector3.Up, new Vector3(0f, 2f, 0f));
        float wall = m.BounceNormalSpeed(
            Vector3.Forward * Sink, Vector3.Back, new Vector3(0f, 0f, 2f));

        Assert.Equal(0.6f * Sink, floor, 3);
        Assert.Equal(floor, wall, 4);
    }

    /// <summary>A contact the aircraft is already leaving gets a damped inward answer rather than a
    /// second launch: once the contact velocity is outgoing, <c>J</c> points back into the surface,
    /// so repeated resolutions damp instead of compounding. The frame-stacking failure mode the
    /// decode ruled out, asserted rather than assumed.</summary>
    [Fact]
    public void AnOutgoingContactDampsRatherThanCompounds()
    {
        var m = Plant(0.6f);
        Rest(m);

        float outward = m.BounceNormalSpeed(Vector3.Up * Sink, Vector3.Up, new Vector3(0f, 2f, 0f));

        Assert.Equal(-0.6f * Sink, outward, 3);
        Assert.True(Mathf.Abs(outward) < Sink, "a repeat resolution must lose speed, not gain it");
    }

    /// <summary>The end-to-end read on the shipped data: a Bloodhawk built from the real archives
    /// rebounds at the authored 0.6, which is the whole point of the item, binding a constant this
    /// install already ships rather than inventing a pushback.</summary>
    [ExtractedDataFact]
    public void TheShippedBloodhawkReboundsAtTheAuthoredSixTenths()
    {
        var m = new FlightModel(PlaneStats.Load(ZrdrPath, "player_bhawk"));
        Rest(m);

        Assert.Equal(0.6f * Sink,
            m.BounceNormalSpeed(Vector3.Down * Sink, Vector3.Up, new Vector3(0f, 2f, 0f)), 3);
    }

    // Level, unrotating, at the sink speed, the state every case above varies from.
    private static void Rest(FlightModel m)
    {
        m.Reset(Vector3.Zero, Basis.Identity, Sink, 0f);
        m.BodyRates = Vector3.Zero;
    }

    // The Bloodhawk's reciprocal moments with an explicit `bounce_factor`, the
    // partition reads `I⁻¹`, so it is the one airframe number that matters here.
    private static FlightModel Plant(float bounceFactor) => new(new PlaneStats
    {
        RecInertia = new Vector3(1.18f, 1f, 1.1f),
        FdSpeed = 135f,
        VehWeight = 1900f,
        RefArea = 330f,
        BounceFactor = bounceFactor,
    });
}

/// <summary>
/// <see cref="FlightModel.Collide"/>'s decoded contact response: the placement (0.03 m off the
/// surface for a human, exactly at the stop for an AI), the absence of any tangential or friction
/// term, the decoded angular impulse, and the multi-tick scrape that bleeds speed through repeated
/// normal impulses alone. Restitution's arithmetic stays <see cref="BounceRestitutionTests"/>'s.
/// Decode: docs/org/flightModel.md, "Collision response", the original's whole contact path
/// (sweep, integrator, damage) writes velocity ONLY through the normal impulse, so the filmed
/// tangential losses are the placement, not a velocity term.
/// A contact's fate is decided outside <c>Collide</c>; it sits on
/// <see cref="AircraftContactResolver"/> and is asserted there, not here.
/// </summary>
public class CollideResponseTests
{
    private const float Sink = 30f;

    /// <summary>The human placement is exact arithmetic with no severity scaling: the aircraft
    /// rests at <c>prev + step·stopFrac + normal·0.03</c>, the decoded literal at
    /// <c>0x006080c4</c>, not the 0.15 the fitted push-out used.</summary>
    [Fact]
    public void AHumanContactRestsExactlyPushOutOffTheSweepStop()
    {
        var m = Plant(0.6f);
        m.Reset(Vector3.Zero, Basis.Identity, Sink, 0f);
        m.VelocityDir = Vector3.Forward;

        var prev = new Vector3(10f, 5f, 0f);
        var step = new Vector3(2f, 0f, -1f);

        m.Collide(prev, step, 0.6f, impact: Vector3.Zero, normal: Vector3.Up, humanPiloted: true);

        Assert.Equal(11.2f, m.Position.X, 4);
        Assert.Equal(5.03f, m.Position.Y, 4);
        Assert.Equal(-0.6f, m.Position.Z, 4);
    }

    /// <summary>An AI contact is the position correction ALONE, resting exactly at the sweep's
    /// stop: no push-out (the 0.03 rides the player branch only, <c>0x48dfce</c> against
    /// <c>0x48db30</c>), no velocity change, no kick. The able-to-fail control for the whole
    /// player gate: any response term leaking onto the AI arm moves one of these.</summary>
    [Fact]
    public void AnAiContactIsThePositionCorrectionAloneAndTouchesNothingElse()
    {
        var m = Plant(0.6f);
        m.Reset(Vector3.Zero, Basis.Identity, 0f, 0f);
        var vel = new Vector3(28f, -6f, 0f);
        m.VelocityDir = vel.Normalized();
        m.Speed = vel.Length();

        var prev = new Vector3(10f, 5f, 0f);
        var step = new Vector3(2f, 0f, -1f);
        m.Collide(prev, step, 0.6f, impact: Vector3.Zero, normal: Vector3.Up, humanPiloted: false);

        Assert.Equal(11.2f, m.Position.X, 4);
        Assert.Equal(5f, m.Position.Y, 4);          // exactly the stop: no 0.03, no 0.15
        Assert.Equal(-0.6f, m.Position.Z, 4);
        Assert.Equal(vel.Length(), m.Speed, 4);     // velocity untouched entirely
        Assert.Equal(vel.Normalized().X, m.VelocityDir.X, 4);
        Assert.Equal(vel.Normalized().Y, m.VelocityDir.Y, 4);
        Assert.Equal(0f, m.BodyRates.Length(), 4);  // and no kick
    }

    /// <summary>A head-on human impact has no tangential component to keep, so the outcome is the
    /// impulse alone, straight back the way it came at <c>bounce_factor · Sink</c>, the
    /// single-axis rebound
    /// <see cref="BounceRestitutionTests.AnAxialNonRotatingContactReboundsAtExactlyBounceFactor"/>
    /// pins, arriving through <c>Collide</c>.</summary>
    [Fact]
    public void AHeadOnImpactHumanPilotedReboundsStraightBackAtBounceFactor()
    {
        var m = Plant(0.6f);
        m.Reset(Vector3.Zero, Basis.Identity, Sink, 0f);
        m.VelocityDir = new Vector3(0f, 0f, -1f);

        // impact 2 m down the normal from the resting pose: the arm is parallel to the normal, so
        // the angular share vanishes and the rebound is the pure bounce_factor case.
        m.Collide(Vector3.Zero, Vector3.Zero, 0f, impact: new Vector3(0f, 0f, -2f),
            normal: new Vector3(0f, 0f, 1f), humanPiloted: true);

        Assert.Equal(0.6f * Sink, m.Speed, 2);
        Assert.Equal(0f, m.VelocityDir.X, 4);
        Assert.Equal(0f, m.VelocityDir.Y, 4);
        Assert.Equal(1f, m.VelocityDir.Z, 4);
    }

    /// <summary>The decoded ABSENCE, able to fail: a shallow human graze keeps its tangential
    /// speed EXACTLY, the original's contact path carries no friction and no tangential term
    /// anywhere (the whole path is traced), so the only velocity change is on the normal axis.
    /// The retired fitted friction (0.35 · vn/25 here ≈ 2.35 m/s of tangential loss) fails this
    /// by two decimal places.</summary>
    [Fact]
    public void AShallowHumanGrazeKeepsItsTangentialSpeedExactly()
    {
        var m = Plant(0.6f);
        m.Reset(Vector3.Zero, Basis.Identity, 0f, 0f);
        var vel = new Vector3(28f, -6f, 0f);
        m.VelocityDir = vel.Normalized();
        m.Speed = vel.Length();

        // Arm along the normal so no kick muddies the read; vn = 6 m/s into an Up surface.
        m.Collide(Vector3.Zero, Vector3.Zero, 0f, impact: new Vector3(0f, -2f, 0f),
            normal: Vector3.Up, humanPiloted: true);

        var after = m.VelocityDir * m.Speed;
        Assert.Equal(28f, after.X, 3);              // tangential untouched: no friction term
        Assert.Equal(0f, after.Z, 3);
        Assert.Equal(0.6f * 6f, after.Y, 2);        // and the normal share rebounds at bounce_factor
    }

    /// <summary>The decoded angular impulse replaces the fitted kick: magnitude and axis are
    /// <c>(r × J)/|r|² · (1 + f_ang · bounce_factor) · 0.5</c>, net of the original's accumulator
    /// round-trip. For a 2 m arm under a 30 m/s vertical slam the hand-computed value is
    /// 8.26 rad/s of pitch-down about body Z, an inertia moved off the authored reciprocal
    /// moments fails the partition share.</summary>
    [Fact]
    public void TheAngularImpulseMatchesTheDecodedShape()
    {
        var m = Plant(0.6f);
        m.Reset(Vector3.Zero, Basis.Identity, Sink, 0f);
        m.VelocityDir = Vector3.Down;

        // Position after placement is (0, 0.03, 0), so impact (2, 0.03, 0) gives arm (2, 0, 0).
        // J = 30·Up; u = (r×J)/|r|² = (0,0,15); A = 15/recI.z = 13.636; L = 2.25·30 = 67.5;
        // f_ang = 0.16806; kick_z = 15·(1 + 0.16806·0.6)·0.5 = 8.256.
        m.Collide(Vector3.Zero, Vector3.Zero, 0f, impact: new Vector3(2f, 0.03f, 0f),
            normal: Vector3.Up, humanPiloted: true);

        Assert.Equal(8.256f, m.BodyRates.Z, 2);
        Assert.Equal(0f, m.BodyRates.X, 4);
        Assert.Equal(0f, m.BodyRates.Y, 4);
    }

    /// <summary>The kick's sign follows which side of the aircraft was struck: a contact ahead and
    /// one behind, everything else identical, spin the body rate in opposite directions.</summary>
    [Theory]
    [InlineData(1f, true)]     // impact ahead of the resting pose
    [InlineData(-1f, false)]   // impact behind it
    public void TheKickSignFollowsWhichSideOfTheAircraftWasStruck(float impactX, bool expectPositiveZ)
    {
        var m = Plant(0.6f);
        m.Reset(Vector3.Zero, Basis.Identity, Sink, 0f);
        m.VelocityDir = new Vector3(0f, -1f, 0f);

        m.Collide(Vector3.Zero, Vector3.Zero, 0f, impact: new Vector3(impactX, 0f, 0f),
            normal: Vector3.Up, humanPiloted: true);

        if (expectPositiveZ)
            Assert.True(m.BodyRates.Z > 0f, $"BodyRates.Z={m.BodyRates.Z:0.000}");
        else
            Assert.True(m.BodyRates.Z < 0f, $"BodyRates.Z={m.BodyRates.Z:0.000}");
    }

    /// <summary>The multi-tick scrape, as the decode explains it: an oblique wall scrape bleeds
    /// speed across ticks purely through REPEATED normal impulses, each tick the plant steers the
    /// velocity back into the wall, and the next contact spends the re-accumulated closing share.
    /// With the arm along the normal the per-tick outcome is exact:
    /// <c>speed' = speed · √(cos²θ + (bounce_factor · sinθ)²)</c>, monotone to a 24 % loss over
    /// ten ticks at 20° with no friction term anywhere.</summary>
    [Fact]
    public void AnObliqueWallScrapeBleedsSpeedAcrossTicksThroughRepeatedImpulsesAlone()
    {
        var m = Plant(0.6f);
        m.Reset(Vector3.Zero, Basis.Identity, 60f, 0f);
        var wallNormal = Vector3.Right;                 // wall face to the plane's left
        const float Theta = 20f * Mathf.Pi / 180f;

        float perTick = Mathf.Sqrt(Mathf.Pow(Mathf.Cos(Theta), 2f)
                                   + Mathf.Pow(0.6f * Mathf.Sin(Theta), 2f));
        float prevSpeed = m.Speed;
        for (int tick = 0; tick < 10; tick++)
        {
            // The plant's steer between contacts, scripted: the same speed, re-aimed θ into the wall.
            m.VelocityDir = new Vector3(-Mathf.Sin(Theta), 0f, -Mathf.Cos(Theta));
            m.BodyRates = Vector3.Zero;
            m.Collide(m.Position, Vector3.Zero, 0f, impact: m.Position - wallNormal * 2f,
                normal: wallNormal, humanPiloted: true);

            Assert.True(m.Speed < prevSpeed,
                $"tick {tick}: speed must bleed every contact, {prevSpeed:0.00} → {m.Speed:0.00} m/s");
            Assert.Equal(prevSpeed * perTick, m.Speed, 2);
            prevSpeed = m.Speed;
        }

        Assert.True(m.Speed < 0.78f * 60f,
            $"ten scrape ticks must have bled real speed: {m.Speed:0.00} m/s from 60");
    }

    /// <summary>The scrape control (METHOD-9): a purely tangential drag along the same wall has no
    /// closing share to spend, so the contact costs nothing at all, the bleed above needs the
    /// re-closing, so it is the repeated impulse and not a per-contact friction that produced
    /// it. A friction term of any size fails this exactness.</summary>
    [Fact]
    public void AScrapeWithoutReSteerStopsBleedingAfterTheFirstContact()
    {
        var m = Plant(0.6f);
        m.Reset(Vector3.Zero, Basis.Identity, 60f, 0f);
        var wallNormal = Vector3.Right;
        const float Theta = 20f * Mathf.Pi / 180f;
        m.VelocityDir = new Vector3(-Mathf.Sin(Theta), 0f, -Mathf.Cos(Theta));

        m.Collide(m.Position, Vector3.Zero, 0f, impact: m.Position - wallNormal * 2f,
            normal: wallNormal, humanPiloted: true);
        float afterFirst = m.Speed;

        // A pure tangential drag along the same wall: no closing component, nothing to spend.
        var tangential = m.VelocityDir * m.Speed - wallNormal * wallNormal.Dot(m.VelocityDir * m.Speed);
        m.VelocityDir = tangential.Normalized();
        m.Speed = tangential.Length();
        float beforeSlide = m.Speed;
        m.Collide(m.Position, Vector3.Zero, 0f, impact: m.Position - wallNormal * 2f,
            normal: wallNormal, humanPiloted: true);

        Assert.True(afterFirst < 60f, "the first, closing contact must have cost speed");
        Assert.Equal(beforeSlide, m.Speed, 3);
    }

    private static FlightModel Plant(float bounceFactor) => new(new PlaneStats
    {
        RecInertia = new Vector3(1.18f, 1f, 1.1f),
        FdSpeed = 135f,
        VehWeight = 1900f,
        RefArea = 330f,
        BounceFactor = bounceFactor,
    });
}
