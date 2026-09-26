using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The decoded collision damage law (<c>FUN_0048d2c0</c>), ported as
/// <see cref="CollisionDamage"/>. Decode: docs/org/flightModel.md, "Collision damage".
/// The property that matters most is the NEGATIVE one, there is no airspeed term, so it is
/// asserted directly rather than left to follow from the arithmetic.
/// </summary>
public class CollisionDamageTests
{
    // What this install authors in player.json's `crash` block, for both ranges.
    private const float Floor = 50f;
    private const float Scale = 300f;

    /// <summary>The severity is the cosine between the unit velocity and the contact normal: 1 for
    /// a head-on contact, falling to 0 as the contact goes glancing.</summary>
    [Fact]
    public void SeverityIsTheCosineOfTheIncidenceAngle()
    {
        Assert.Equal(1f, CollisionDamage.Severity(Vector3.Down, Vector3.Up), 4);
        Assert.Equal(0.5f, CollisionDamage.Severity(
            new Vector3(0.8660254f, -0.5f, 0f), Vector3.Up), 4);
        Assert.Equal(0f, CollisionDamage.Severity(Vector3.Forward, Vector3.Up), 4);
    }

    /// <summary>A contact the plane is moving AWAY from is not a hit: the severity floors at zero
    /// rather than going negative and inverting the law.</summary>
    [Fact]
    public void ARecedingContactHasNoSeverity()
    {
        Assert.Equal(0f, CollisionDamage.Severity(Vector3.Up, Vector3.Up), 4);
        Assert.Equal(0f, CollisionDamage.Term(
            CollisionDamage.Severity(Vector3.Up, Vector3.Up), Floor, Scale), 4);
    }

    /// <summary>⚠ The whole point of the port: the damage carries NO airspeed term. The velocity is
    /// a DIRECTION, so a contact at 400 m/s and the same contact at 90 m/s cost exactly the same.
    /// Our old law scaled by |v·n| and could not be repaired by retuning its constant.</summary>
    [Fact]
    public void TheDamageIsIndependentOfSpeed()
    {
        var normal = Vector3.Up;
        var heading = new Vector3(0.5f, -0.8660254f, 0f);   // one attitude, two speeds
        float Damage(float speed) => CollisionDamage.Term(
            CollisionDamage.Severity((heading * speed).Normalized(), normal), Floor, Scale);

        Assert.Equal(Damage(400f), Damage(90f), 4);
        Assert.Equal(Damage(400f), Damage(5f), 4);

        // …and the law IS angle-sensitive, so the equalities above are not a flat constant.
        float shallow = CollisionDamage.Term(
            CollisionDamage.Severity(new Vector3(0.9848f, -0.1736f, 0f).Normalized(), normal),
            Floor, Scale);
        Assert.True(shallow < Damage(400f),
            $"a shallower contact must cost less: {shallow} vs {Damage(400f)}");
    }

    /// <summary>The cube, at the two ends: a head-on contact spends the full authored scale, and a
    /// glancing one bottoms out on the floor rather than falling to nothing.</summary>
    [Fact]
    public void TheTermIsTheCubedSeverityAgainstItsFloor()
    {
        Assert.Equal(Scale, CollisionDamage.Term(1f, Floor, Scale), 3);
        // The cube alone, with the floor taken out of the way: half the severity is an eighth.
        Assert.Equal(Scale * 0.125f, CollisionDamage.Term(0.5f, 0f, Scale), 3);
        // With the authored floor back, that same 37.5 is raised to 50.
        Assert.Equal(Floor, CollisionDamage.Term(0.5f, Floor, Scale), 3);
    }

    /// <summary>The floor takes over below s ≈ 0.550, which is the ~33° off the surface the decode
    /// records. Either side of that crossing the law switches which term it reports.</summary>
    [Fact]
    public void TheFloorDominatesBelowTheDecodedCrossing()
    {
        const float Crossing = 0.5503212f;   // (50/300)^(1/3)

        Assert.Equal(Floor, CollisionDamage.Term(Crossing - 0.05f, Floor, Scale), 3);
        Assert.True(CollisionDamage.Term(Crossing + 0.05f, Floor, Scale) > Floor);
    }

    /// <summary>The law against the real struck object, which is what the original's own graze and
    /// ram outcomes constrain. C1's airfield buildings (<c>m_build01</c>–<c>07</c>) carry
    /// <c>health 60</c> and <c>DAMAGE_SEQUENCE</c> thresholds at 36 and 18. A shallow graze deals
    /// the flat floor 50, leaving 10 HP: alive, past the deepest threshold, the stage-2 burn the
    /// original showed. A steeper contact passes 60 and destroys it, as a full ram did. Nothing
    /// here is tuned; both fall out of the authored ranges.</summary>
    [Fact]
    public void AGrazeLeavesAC1BuildingAliveInItsDeepestDamageStage()
    {
        const float BuildingHealth = 60f;
        const float DeepestThreshold = 18f;

        // ~20° off the surface, the shallow graze: the floor governs.
        float graze = CollisionDamage.Term(
            CollisionDamage.Severity(new Vector3(0.9397f, -0.342f, 0f).Normalized(), Vector3.Up),
            Floor, Scale);
        Assert.Equal(Floor, graze, 3);
        float left = BuildingHealth - graze;
        Assert.True(left > 0f, $"the graze must not kill it: {left} HP left");
        Assert.True(left <= DeepestThreshold, $"…and must reach stage 2: {left} HP vs <= {DeepestThreshold}");

        // ~45°, a real ram: past the pool outright.
        float ram = CollisionDamage.Term(
            CollisionDamage.Severity(new Vector3(0.7071f, -0.7071f, 0f).Normalized(), Vector3.Up),
            Floor, Scale);
        Assert.True(ram > BuildingHealth, $"a ram must destroy it: {ram} vs {BuildingHealth} HP");
    }

    /// <summary>The entity-versus-entity cut is a fifth, and it is the value the non-player branch
    /// applies. The player never reaches it, that asymmetry lives in FlightController, not
    /// here.</summary>
    [Fact]
    public void TheEntityCutIsAFifth()
    {
        Assert.Equal(0.2f, CollisionDamage.EntityCut, 4);
        Assert.Equal(Scale * 0.2f, CollisionDamage.Term(1f, Floor, Scale) * CollisionDamage.EntityCut, 3);
    }
}
