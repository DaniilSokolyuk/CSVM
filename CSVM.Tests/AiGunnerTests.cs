using CSVM.Flight.Ai;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The D14 gunnery gates, engine-free: <see cref="AiGunner.Solve"/> is pure over its arguments
/// (the seeded rng feeds only the per-shot scatter, which these tests never draw, the rng is
/// null here on purpose, and the scatter cone itself is pinned by the in-engine
/// <c>ai-gunnery</c> suite and the aim-assist suite's Scatter cases). Geometry: shooter and
/// target static unless stated, round at 500 m/s inside the gun's shipped 1 to 900 m window.
/// </summary>
public class AiGunnerTests
{
    private const float Speed = 500f;

    private static readonly Vector3 TargetPos = Vector3.Zero;
    private static readonly Vector3 TargetForward = Vector3.Forward; // nose on -Z

    // A shooter dead astern (on +Z), nose on the target: every gate passes.
    [Fact]
    public void ADeadAsternShotPassesEveryGate()
    {
        var g = Gunner();
        var ownPos = new Vector3(0f, 0f, 500f);
        g.Solve(ownPos, Vector3.Zero, Basis.LookingAt(TargetPos - ownPos, Vector3.Up),
            TargetPos, Vector3.Zero, TargetForward, Speed);
        Assert.True(g.WantsFire);
        // Static target, static shooter: the lead IS the bearing.
        Assert.True(g.AimDirWorld.Dot((TargetPos - ownPos).Normalized()) > 0.9999f);
    }

    [Fact]
    public void TheLeadPointsAheadOfACrossingTarget()
    {
        var g = Gunner();
        var ownPos = new Vector3(0f, 0f, 500f);
        var targetVel = new Vector3(60f, 0f, 0f); // sliding left-to-right, nose still on -Z
        g.Solve(ownPos, Vector3.Zero, Basis.LookingAt(TargetPos - ownPos, Vector3.Up),
            TargetPos, targetVel, TargetForward, Speed);
        Assert.True(g.WantsFire);
        Assert.True(g.AimDirWorld.X > 0.01f, $"the lead swings toward the target's motion x={g.AimDirWorld.X}");
    }

    // The traverse clamp costs aim quality rather than vetoing: a lead past the ±11° limits
    // still fires while what the clamp gave away stays inside the gun's 10° gate.
    [Theory]
    [InlineData(10f, true)]   // inside the limits: nothing is given away
    [InlineData(18f, true)]   // clamps to 11°, 7° residual
    [InlineData(22f, false)]  // clamps to 11°, 11° residual, past the gate
    [InlineData(30f, false)]
    public void TheAimGateIsTheResidualLeftByTheTraverseClamp(float noseOffDeg, bool fires)
    {
        var g = Gunner();
        var ownPos = new Vector3(0f, 0f, 500f);
        var offNose = (TargetPos - ownPos).Normalized().Rotated(Vector3.Up, Mathf.DegToRad(noseOffDeg));
        g.Solve(ownPos, Vector3.Zero, Basis.LookingAt(offNose, Vector3.Up),
            TargetPos, Vector3.Zero, TargetForward, Speed);
        Assert.Equal(fires, g.WantsFire);
    }

    // The gate is the slot's authored window against the separation itself, both ends live.
    [Theory]
    [InlineData(0.5f, false)]  // inside the 1 m floor
    [InlineData(500f, true)]
    [InlineData(1200f, false)] // past the 900 m ceiling
    public void OnlyASeparationInsideTheEngagementWindowIsShotAt(float separation, bool fires)
    {
        var g = Gunner();
        var ownPos = new Vector3(0f, 0f, separation);
        g.Solve(ownPos, Vector3.Zero, Basis.LookingAt(TargetPos - ownPos, Vector3.Up),
            TargetPos, Vector3.Zero, TargetForward, Speed);
        Assert.Equal(fires, g.WantsFire);
    }

    [Fact]
    public void QuickDrawAcceptsTheForeAndAftConesAndRefusesTheBeam()
    {
        var g = Gunner(); // 50°
        // Dead ahead of the target's nose (-Z side) and dead astern (+Z) both pass.
        Assert.True(g.QuickDrawAccepts(new Vector3(0f, 0f, -300f), TargetPos, TargetForward));
        Assert.True(g.QuickDrawAccepts(new Vector3(0f, 0f, 300f), TargetPos, TargetForward));
        // 40° off the tail passes a 50° cone; 60° off does not; the beam (90°) never does.
        var at40 = new Vector3(Mathf.Sin(Mathf.DegToRad(40f)), 0f, Mathf.Cos(Mathf.DegToRad(40f))) * 300f;
        var at60 = new Vector3(Mathf.Sin(Mathf.DegToRad(60f)), 0f, Mathf.Cos(Mathf.DegToRad(60f))) * 300f;
        Assert.True(g.QuickDrawAccepts(at40, TargetPos, TargetForward));
        Assert.False(g.QuickDrawAccepts(at60, TargetPos, TargetForward));
        Assert.False(g.QuickDrawAccepts(new Vector3(300f, 0f, 0f), TargetPos, TargetForward));
        // The same 60° bearing passes once the pilot's rating widens the cone (89° at rating 9).
        g.QuickDrawAngleDeg = 89f;
        Assert.True(g.QuickDrawAccepts(at60, TargetPos, TargetForward));
    }

    [Fact]
    public void HoldFireClearsTheTriggerUntilTheNextSolve()
    {
        var g = Gunner();
        var ownPos = new Vector3(0f, 0f, 500f);
        g.Solve(ownPos, Vector3.Zero, Basis.LookingAt(TargetPos - ownPos, Vector3.Up),
            TargetPos, Vector3.Zero, TargetForward, Speed);
        Assert.True(g.WantsFire);
        g.HoldFire();
        Assert.False(g.WantsFire);
    }

    private static AiGunner Gunner() => new(null!)
    {
        DeadEyeAngleDeg = 4f,
        QuickDrawAngleDeg = 50f,
    };
}
