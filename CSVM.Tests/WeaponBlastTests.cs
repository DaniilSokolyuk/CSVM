using System.IO;
using CSVM.Flight.Weapons;
using CSVM.Testing;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Blast falloff, authored fuse/radius independence, and zero-damage special exclusion, against
/// the retail install (engine-free: <c>WeaponDefs.Load</c>
/// and <see cref="ProjectilePool"/>'s blast statics touch no live Node).
/// </summary>
public class WeaponBlastTests
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [ExtractedDataFact]
    public void TorpedoAuthoredFuseAndBlastRadiusAreIndependent()
    {
        var weapons = WeaponDefs.Load(ZrdrPath, null);
        Assert.True(weapons.TryGet("wep_14", out var torpedo), "torpedo definition loads");
        Assert.Equal(1f, torpedo!.DetonationDistance ?? -1f);
        Assert.Equal(30f, torpedo.ImpactProximity ?? -1f);
    }

    // C10: FUN_005acac0's `1 − d²/IMPACT_PROXIMITY²`, quadratic in distance, full at the surface
    // and zero at the radius. The half-radius point is where it parts from the linear curve it
    // replaced (0.75 against 0.5), so that value is the one that proves the shape.
    [Fact]
    public void BlastDamageFallsOffQuadraticallyFromFullAtTheSurfaceToZeroAtTheEdge()
    {
        Assert.Equal(200f, ProjectilePool.BlastDamage(200f, 30f, 0f));
        Assert.Equal(187.5f, ProjectilePool.BlastDamage(200f, 30f, 7.5f), 3);
        Assert.Equal(150f, ProjectilePool.BlastDamage(200f, 30f, 15f), 3);
        Assert.Equal(87.5f, ProjectilePool.BlastDamage(200f, 30f, 22.5f), 3);
        Assert.True(Mathf.IsZeroApprox(ProjectilePool.BlastDamage(200f, 30f, 30f)));
        Assert.Equal(0f, ProjectilePool.BlastDamage(200f, 30f, 45f));
    }

    // The sim path feeds the curve squared quantities straight from the engine's stored square
    // (WeaponDef.ImpactProximitySqM) and a DistanceSquaredTo, and an engulfing burst passes 0.
    [Fact]
    public void BlastFalloffTakesSquaredQuantitiesAndClampsBothEnds()
    {
        Assert.Equal(1f, ProjectilePool.BlastFalloff(0f, 900f));
        Assert.Equal(0.75f, ProjectilePool.BlastFalloff(225f, 900f), 5);
        Assert.Equal(0f, ProjectilePool.BlastFalloff(900f, 900f));
        Assert.Equal(0f, ProjectilePool.BlastFalloff(1600f, 900f));
        Assert.Equal(0f, ProjectilePool.BlastFalloff(0f, 0f));
    }

    [ExtractedDataFact]
    public void ZeroDamageSpecialsAreExcludedFromBlastDamage()
    {
        var weapons = WeaponDefs.Load(ZrdrPath, null);
        Assert.True(weapons.TryGet("wep_09", out var flash), "flash definition loads");
        Assert.True(weapons.TryGet("wep_15", out var flare), "flare definition loads");
        Assert.False(ProjectilePool.HasBlastDamage(flash!), "zero-damage FLASH radius is not a damage blast");
        Assert.False(ProjectilePool.HasBlastDamage(flare!), "zero-damage FLARE radius is not a damage blast");
    }

    // The four no-damage types never spend a pair on an aircraft (FUN_004b9bc0's branches each zero
    // it), and nothing else in the catalogue is under that rule.
    [ExtractedDataFact]
    public void ExactlyTheFourNoDamageTypesDiscardTheirAircraftDamage()
    {
        var weapons = WeaponDefs.Load(ZrdrPath, null);
        foreach (var w in weapons.All)
        {
            bool expected = w.Id is "wep_08" or "wep_09" or "wep_10" or "wep_12" or "wep_15";
            Assert.True(expected == ProjectilePool.AircraftDamageDiscarded(w), $"{w.Id} discards={!expected}");
        }
    }

    // C12: the SURFACE_ANIMATION orientation is the shortest rotation from world up onto the
    // struck normal (FUN_0053fd40 from (0,1,0)); flat ground and a surface-less burst are identity.
    [Fact]
    public void SurfaceUpBasisTakesWorldUpOntoTheNormalAndIsIdentityOnFlatGround()
    {
        Assert.Equal(Basis.Identity, ProjectilePool.SurfaceUpBasis(Vector3.Up));
        Assert.Equal(Basis.Identity, ProjectilePool.SurfaceUpBasis(Vector3.Zero));

        var slope = new Vector3(0.5f, 0.8f, -0.3f).Normalized();
        var basis = ProjectilePool.SurfaceUpBasis(slope);
        Assert.True((basis * Vector3.Up).IsEqualApprox(slope), $"{basis * Vector3.Up} vs {slope}");
        Assert.True(basis.IsFinite() && Mathf.IsEqualApprox(basis.Determinant(), 1f));
        // The shortest arc leaves the axis perpendicular to both vectors where it was.
        var axis = Vector3.Up.Cross(slope).Normalized();
        Assert.True((basis * axis).IsEqualApprox(axis));

        var down = ProjectilePool.SurfaceUpBasis(Vector3.Down);
        Assert.True((down * Vector3.Up).IsEqualApprox(Vector3.Down));
    }

    /// <summary>The decoded gate dots unit(round - candidate) with the candidate's backward axis, so
    /// a positive threshold admits a round behind the candidate and refuses one ahead of it.</summary>
    [Fact]
    public void TheAuthoredFuseDotGateAdmitsARoundBehindTheCandidate()
    {
        // A candidate flying nose -Z has its backward axis on +Z.
        var backward = Vector3.Back;
        var candidate = new Vector3(100f, 50f, -20f);
        var behind = candidate + new Vector3(2f, 1f, 12f);
        var ahead = candidate + new Vector3(2f, 1f, -12f);
        Assert.True(ProjectilePool.FuseDotAllows(0.3f, backward, behind - candidate));
        Assert.False(ProjectilePool.FuseDotAllows(0.3f, backward, ahead - candidate));

        // A negative threshold flips which side passes: abeam and behind admit, dead ahead does not.
        var abeam = candidate + new Vector3(12f, 0f, 0f);
        Assert.True(ProjectilePool.FuseDotAllows(-0.5f, backward, abeam - candidate));
        Assert.False(ProjectilePool.FuseDotAllows(-0.5f, backward, ahead - candidate));
    }

    /// <summary>An unauthored threshold never gates, and a round exactly on the candidate's origin
    /// dots to zero, which a positive threshold refuses and a zero one admits.</summary>
    [Fact]
    public void TheFuseDotGateReadsAZeroOffsetAsZero()
    {
        Assert.True(ProjectilePool.FuseDotAllows(null, Vector3.Back, Vector3.Forward));
        Assert.False(ProjectilePool.FuseDotAllows(0.1f, Vector3.Back, Vector3.Zero));
        Assert.True(ProjectilePool.FuseDotAllows(0f, Vector3.Back, Vector3.Zero));
    }

    /// <summary>The fuse's cheap reject measures the whole of a round's step: a round crossing 3 m
    /// abeam mid-step has both endpoints far away, which is why the endpoints are not what is
    /// tested (a gun round covers ~8 m per 60 Hz frame).</summary>
    [Fact]
    public void SegmentDistanceMeasuresThePassNotTheEndpoints()
    {
        var a = new Vector3(-50f, 0f, 0f);
        var b = new Vector3(50f, 0f, 0f);
        var p = new Vector3(0f, 3f, 0f);
        Assert.Equal(3f, ProjectilePool.SegmentPointDistance(a, b, p), 4);
        Assert.True((p - a).Length() > 40f);
        Assert.True((p - b).Length() > 40f);
    }

    [Fact]
    public void SegmentDistanceClampsToTheEndpointsAndSurvivesAZeroStep()
    {
        var a = new Vector3(0f, 0f, 0f);
        var b = new Vector3(10f, 0f, 0f);
        // Behind the segment's start: the nearest point IS the start, not the infinite line.
        Assert.Equal(5f, ProjectilePool.SegmentPointDistance(a, b, new Vector3(-5f, 0f, 0f)), 4);
        Assert.Equal(5f, ProjectilePool.SegmentPointDistance(a, b, new Vector3(15f, 0f, 0f)), 4);
        // A stationary step (dt 0, or a round spawned and killed in one frame) is a point test.
        Assert.Equal(4f, ProjectilePool.SegmentPointDistance(a, a, new Vector3(0f, 4f, 0f)), 4);
    }

    // D31: the one-shot pool's distance term, linear between a sound's own RANGE,
    // full inside the near edge, silent past the far edge, 1 (skip the term) with no nearest
    // human to measure against.
    [Fact]
    public void OneShotDistanceGainIsFullInsideRangeMinAndZeroPastRangeMax()
    {
        Assert.Equal(1f, ProjectilePool.DistanceGain(0f, 20f, 200f));
        Assert.Equal(1f, ProjectilePool.DistanceGain(20f, 20f, 200f));
        Assert.Equal(0f, ProjectilePool.DistanceGain(200f, 20f, 200f));
        Assert.Equal(0f, ProjectilePool.DistanceGain(5000f, 20f, 200f));
    }

    [Fact]
    public void OneShotDistanceGainFallsOffLinearlyBetweenRangeMinAndRangeMax()
    {
        Assert.Equal(0.5f, ProjectilePool.DistanceGain(110f, 20f, 200f));
    }

    [Fact]
    public void OneShotDistanceGainSkipsTheTermWithNoPlayerPositionsWired()
    {
        Assert.Equal(1f, ProjectilePool.DistanceGain(float.MaxValue, 20f, 200f));
    }
}
