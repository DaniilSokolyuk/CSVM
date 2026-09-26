using CSVM.Flight.Ai;
using CSVM.Flight.Weapons;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The <c>mode ship</c> gun mount (FUN_004b7670's animated branch, docs/org/aiPilot/aiWeapons.md):
/// the asymmetric elevation guards a def authoring no gun_pitch/gun_yaw gets instead of a per-axis
/// clamp, the 4.0/s slew toward the guarded direction, and the aim quality measured against the
/// RAW lead so the guard's give-away is charged to the shot.
/// </summary>
[Trait("Tier", "Quick")]
public class SurfaceGunMountTests
{
    [Fact]
    public void ADirectionInsideTheBandIsUntouched()
    {
        var level = new Vector3(0f, 0f, -1f);
        Assert.Equal(level, SurfaceGunMount.Guard(level));
        // 20° up is inside the +30° ceiling; 10° down inside the −15° floor.
        foreach (float deg in new[] { 20f, -10f })
        {
            var dir = new Vector3(0f, Mathf.Sin(Mathf.DegToRad(deg)), -Mathf.Cos(Mathf.DegToRad(deg)));
            var guarded = SurfaceGunMount.Guard(dir);
            Assert.Equal(dir.Y, guarded.Y, 4);
        }
    }

    [Fact]
    public void TheBandIsAsymmetricAndClampsAtItsOwnTwoLiterals()
    {
        // Straight up clamps to y = 0.5 (30°), straight down to y = −0.2588 (15°). ⚠ These are two
        // unrelated engine literals, not a ± pair: a mirrored floor would let a boat depress twice
        // as far as the original does.
        Assert.Equal(SurfaceGunMount.MaxElevationY, SurfaceGunMount.Guard(Vector3.Up).Y, 4);
        Assert.Equal(SurfaceGunMount.MinElevationY, SurfaceGunMount.Guard(Vector3.Down).Y, 4);
        Assert.NotEqual(SurfaceGunMount.MaxElevationY, -SurfaceGunMount.MinElevationY, 3);
    }

    [Fact]
    public void GuardingPreservesAzimuthAndUnitLength()
    {
        // A steep lead off the bow at 40° to starboard: the elevation comes down to the ceiling
        // and the bearing survives untouched, which is what keeps a guarded shot pointing at the
        // right piece of sky rather than swinging off the target's bearing.
        var dir = new Vector3(0.5f, 0.8f, -0.33f).Normalized();
        var guarded = SurfaceGunMount.Guard(dir);
        Assert.Equal(1f, guarded.Length(), 4);
        Assert.Equal(SurfaceGunMount.MaxElevationY, guarded.Y, 4);
        Assert.Equal(
            Mathf.Atan2(dir.X, dir.Z),
            Mathf.Atan2(guarded.X, guarded.Z), 4);
    }

    [Fact]
    public void YawIsNeverGuarded()
    {
        // A hull traverses the full circle: a lead dead astern comes back unchanged.
        var astern = new Vector3(0f, 0f, 1f);
        Assert.Equal(astern, SurfaceGunMount.Guard(astern));
    }

    [Fact]
    public void TheSlewSnapsWholeAtAQuarterSecondAndInterpolatesBelowIt()
    {
        var from = new Vector3(0f, 0f, -1f);
        var to = new Vector3(1f, 0f, 0f);
        // dt x 4.0 reaches 1 at 0.25 s: the whole turn lands in one step.
        Assert.Equal(to, SurfaceGunMount.Slew(from, to, 0.25f));
        // Below it the mount is partway there, still unit length, and no further than the target.
        var stepped = SurfaceGunMount.Slew(from, to, 0.1f);
        Assert.Equal(1f, stepped.Length(), 4);
        Assert.True(stepped.Dot(to) > from.Dot(to));
        Assert.True(stepped.Dot(to) < 1f);
    }

    [Fact]
    public void TheSlewClosesAConstantFractionOfWhatIsLeftEachStep()
    {
        // ⚠ The rate is a FRACTION of the remaining angle per step, not degrees per second: each
        // 60 Hz step closes dt × 4.0 = 1/15 of what is left, so a 90° turn is 84° away after one
        // step and 90 × (14/15)^n away after n. It approaches geometrically and never overshoots,
        // which is why a hull swinging onto a beam target keeps failing the aim gate for a while
        // rather than snapping on. Reading it as 4°/s would have the mount arrive 20× too slowly.
        const float step = 1f / 60f;
        float perStep = 1f - (step * SurfaceGunMount.SlewRate);
        var to = new Vector3(1f, 0f, 0f);
        var aim = new Vector3(0f, 0f, -1f);
        foreach (int n in new[] { 1, 15 })
        {
            aim = new Vector3(0f, 0f, -1f);
            for (int i = 0; i < n; i++)
            {
                aim = SurfaceGunMount.Slew(aim, to, step);
            }
            float remainingDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(aim.Dot(to), -1f, 1f)));
            Assert.Equal(90f * Mathf.Pow(perStep, n), remainingDeg, 1);
        }
    }

    [Fact]
    public void OpposedDirectionsSweepRatherThanStall()
    {
        // Exactly reversed: there is no shortest arc, and a naive interpolation sits still or
        // divides by zero. The engine sweeps pi*t about a perpendicular instead.
        var from = new Vector3(0f, 0f, -1f);
        var to = new Vector3(0f, 0f, 1f);
        var stepped = SurfaceGunMount.Slew(from, to, 0.1f);
        Assert.Equal(1f, stepped.Length(), 4);
        Assert.True(stepped.Dot(from) < 1f, "the mount must actually leave where it started");
    }

    [Fact]
    public void AimQualityIsMeasuredAgainstTheRawLeadNotTheGuardedOne()
    {
        // A target 50° up: the guard pins the mount at 30°, leaving 20° of residual. cos 20° is
        // 0.94, under the gun's 0.9848 gate, so the shot is refused, which is the whole point.
        // Measured against the GUARDED direction it would read 1.0 and fire at the sky.
        float rad = Mathf.DegToRad(50f);
        var raw = new Vector3(0f, Mathf.Sin(rad), -Mathf.Cos(rad));
        var guarded = SurfaceGunMount.Guard(raw);
        float residual = SurfaceGunMount.AimQuality(guarded, raw);
        Assert.True(residual < AiGunner.AimQualityCos,
            $"a target above the band must fail the aim gate, residual was {residual}");
        Assert.Equal(1f, SurfaceGunMount.AimQuality(guarded, guarded), 4);
    }

    [Fact]
    public void ATargetJustInsideTheBandStillClearsTheAimGate()
    {
        // The band's own edge fires: 30° up is exactly the ceiling, so the guard gives nothing
        // away and the residual is 1. The gate is what the guard costs, not the guard itself.
        float rad = Mathf.Asin(SurfaceGunMount.MaxElevationY);
        var raw = new Vector3(0f, Mathf.Sin(rad), -Mathf.Cos(rad));
        Assert.True(SurfaceGunMount.AimQuality(SurfaceGunMount.Guard(raw), raw)
            >= AiGunner.AimQualityCos);
    }
}
