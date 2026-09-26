using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The shared <c>SONIC</c>/<c>FLASH</c> intensity (<see cref="DisablingIntensity"/>) decoded from
/// <c>FUN_0042e840</c>. Every expectation is computed by hand from the decoded rules
/// (<c>ratio = min(1, d²/R²)</c>, a plateau to 0.6 then a fade of 2.5 per unit of ratio, the facing
/// scale of twice the dot below 0.5), never captured from the implementation.
///
/// <para>The point of the squared-ratio tests is the plateau edge: it sits at 77% of the radius, not
/// 60%, and only a squared ratio puts it there.</para>
/// </summary>
public class DisablingIntensityTests
{
    /// <summary>Dead centre is full strength, and the stun is exactly five times it.</summary>
    [Fact]
    public void ADeadCentreHitIsFullStrengthAndStunsForFiveSeconds()
    {
        Assert.True(DisablingIntensity.TryResolve(0f, 10000f, false, 0f, out float intensity, out float stun));
        Assert.Equal(1f, intensity, 5);
        Assert.Equal(5f, stun, 5);
    }

    /// <summary>The plateau runs to a squared ratio of exactly 0.6 and the fade starts past it: at
    /// R² = 10000 that is d² = 6000, so d ≈ 77.5 m of a 100 m radius.</summary>
    [Fact]
    public void ThePlateauHoldsFullStrengthToSixTenthsOfTheSquaredRadius()
    {
        Assert.True(DisablingIntensity.TryResolve(6000f, 10000f, false, 0f, out float atEdge, out _));
        Assert.Equal(1f, atEdge, 5);

        // ratio 0.8 -> 1 - (0.8 - 0.6) * 2.5 = 0.5, half way through the fade in ratio terms.
        Assert.True(DisablingIntensity.TryResolve(8000f, 10000f, false, 0f, out float past, out float stun));
        Assert.Equal(0.5f, past, 5);
        Assert.Equal(2.5f, stun, 5);
    }

    /// <summary>At the radius the fade has consumed the whole intensity, and the ratio clamp holds
    /// everything beyond it there too. Single precision leaves 6e-8 rather than a clean zero, so the
    /// routine still reports a hit; the effect that carries is nothing either way, and the assertion
    /// is on the number rather than on the flag because the flag is a rounding artefact of the
    /// original's own arithmetic.</summary>
    [Fact]
    public void AtTheRadiusAndBeyondNothingIsLeftOfTheIntensity()
    {
        DisablingIntensity.TryResolve(10000f, 10000f, false, 0f, out float atR, out float stunAtR);
        Assert.True(atR < 1e-6f);
        Assert.True(stunAtR < 1e-5f);

        DisablingIntensity.TryResolve(40000f, 10000f, false, 0f, out float beyond, out _);
        Assert.Equal(atR, beyond, 7);
    }

    /// <summary>A FLASH behind the victim does nothing, one off to the side is scaled by twice the
    /// dot, and one within 60° of the nose is untouched. The 0.5 boundary is continuous, so the
    /// scale meets 1.0 exactly there.</summary>
    [Fact]
    public void TheFacingTestRejectsBehindScalesBelowHalfAndLeavesTheRestAlone()
    {
        Assert.False(DisablingIntensity.TryResolve(0f, 10000f, true, 0f, out _, out _));
        Assert.False(DisablingIntensity.TryResolve(0f, 10000f, true, -0.2f, out _, out _));

        Assert.True(DisablingIntensity.TryResolve(0f, 10000f, true, 0.4f, out float side, out float sideStun));
        Assert.Equal(0.8f, side, 5);
        Assert.Equal(4f, sideStun, 5);

        Assert.True(DisablingIntensity.TryResolve(0f, 10000f, true, 0.5f, out float boundary, out _));
        Assert.Equal(1f, boundary, 5);

        Assert.True(DisablingIntensity.TryResolve(0f, 10000f, true, 0.6f, out float ahead, out _));
        Assert.Equal(1f, ahead, 5);
    }

    /// <summary>The facing test is the only behavioural difference between the two flags: a SONIC
    /// burst directly behind the victim lands at full strength.</summary>
    [Fact]
    public void ASonicBurstIgnoresWhichWayTheVictimIsLooking()
    {
        Assert.True(DisablingIntensity.TryResolve(0f, 10000f, false, -1f, out float behind, out _));
        Assert.Equal(1f, behind, 5);
    }

    /// <summary>The facing scale multiplies the distance curve rather than replacing it: at ratio
    /// 0.8 (intensity 0.5) with a dot of 0.4 the result is 0.5 × 0.8.</summary>
    [Fact]
    public void TheFacingScaleAndTheDistanceCurveCompound()
    {
        Assert.True(DisablingIntensity.TryResolve(8000f, 10000f, true, 0.4f, out float i, out float stun));
        Assert.Equal(0.4f, i, 5);
        Assert.Equal(2f, stun, 5);
    }

    /// <summary>The flare <c>wep_15</c> authors <c>IMPACT_PROXIMITY [500]</c> in
    /// <c>weapons.zrd.json</c>, so its plateau reaches √0.6 × 500 ≈ 387.3 m: everything inside that
    /// is stunned for the full five seconds, and only the last 113 m fade.</summary>
    [Fact]
    public void TheFlaresFiveHundredMetreRadiusIsFullStrengthToAboutThreeEightySeven()
    {
        const float radiusSq = 500f * 500f;

        Assert.True(DisablingIntensity.TryResolve(387f * 387f, radiusSq, true, 1f, out float inside, out float stun));
        Assert.Equal(1f, inside, 5);
        Assert.Equal(5f, stun, 5);

        // 388 m: ratio 0.602176, so the fade has just started biting.
        Assert.True(DisablingIntensity.TryResolve(388f * 388f, radiusSq, true, 1f, out float outside, out _));
        Assert.True(outside < 1f);
        Assert.Equal(0.99456f, outside, 4);

        // 450 m: ratio 0.81, well into the fade.
        Assert.True(DisablingIntensity.TryResolve(450f * 450f, radiusSq, true, 1f, out float far, out _));
        Assert.Equal(0.475f, far, 5);
    }

    /// <summary>A weapon with no proximity radius disables nothing, which is what the original's
    /// divide degenerates to.</summary>
    [Fact]
    public void AZeroRadiusWeaponAffectsNobody()
    {
        Assert.False(DisablingIntensity.TryResolve(0f, 0f, false, 1f, out _, out _));
    }

    /// <summary>The dot runs from the victim toward the burst, so a burst ahead of a victim looking
    /// down -Z reads +1 and one behind reads -1.</summary>
    [Fact]
    public void TheFacingDotIsPositiveForABurstTheVictimIsLookingAt()
    {
        var forward = new Vector3(0f, 0f, -1f);
        Assert.Equal(1f, DisablingIntensity.FacingDot(forward, Vector3.Zero, new Vector3(0f, 0f, -50f)), 5);
        Assert.Equal(-1f, DisablingIntensity.FacingDot(forward, Vector3.Zero, new Vector3(0f, 0f, 50f)), 5);
        Assert.Equal(0f, DisablingIntensity.FacingDot(forward, Vector3.Zero, new Vector3(50f, 0f, 0f)), 5);
        Assert.Equal(0f, DisablingIntensity.FacingDot(forward, Vector3.Zero, Vector3.Zero), 5);
    }
}
