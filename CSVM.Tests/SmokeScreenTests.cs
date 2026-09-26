using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Testing;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The smoke screen's aircraft-free rules (<see cref="SmokeScreenRule"/>) and its tunables
/// (<see cref="SmokeScreenTunables"/>), decoded from <c>FUN_004b8fd0</c> and the loader
/// <c>FUN_004735b0</c>. Every expectation is worked by hand from the routine's literals; the
/// roster walk itself, which needs live aircraft, is the <c>smoke-screen</c> in-engine suite's.
///
/// <para>The angle is the point most likely to be re-derived wrongly: the stored value is the
/// cosine of HALF the authored angle, so 170° reaches 85° off the layer's backward axis. A test
/// that read it as a full cone would put the edge at 42.5° and pass against the wrong reach.</para>
/// </summary>
public class SmokeScreenTests
{
    private const float Dt = 1f / 60f;

    // The shipped player.json values, restated so the tests below read as arithmetic; the data
    // half is asserted against the extracted file in TheShippedTunablesReadOffPlayerJson.
    private const float ShippedRangeM = 600f;
    private const float ShippedAngleDeg = 170f;
    private const float ShippedIntervalS = 5f;

    private static readonly Vector3 LayerPos = new(0f, 500f, 0f);

    // The layer flies toward -Z, so its backward axis is +Z: victims behind it sit at +Z.
    private static readonly Vector3 Backward = Vector3.Back;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    private static float ShippedCos => SmokeScreenTunables.HalfAngleCosOf(ShippedAngleDeg);

    /// <summary>The stored angle is <c>cos(170° / 2)</c>, about 0.087, not <c>cos(170°)</c>.</summary>
    [Fact]
    public void TheAngleIsStoredAsAHalfAngleCosine()
    {
        Assert.Equal(Mathf.Cos(Mathf.DegToRad(85f)), ShippedCos, 5);
        Assert.True(ShippedCos > 0f, "cos(85°) is positive; cos(170°) would be negative");
    }

    /// <summary>Dead astern inside the range is caught, dead ahead is not.</summary>
    [Fact]
    public void BehindTheLayerIsCaughtAndAheadIsNot()
    {
        Assert.True(SmokeScreenRule.Catches(LayerPos, Backward, LayerPos + Backward * 300f, ShippedRangeM, ShippedCos));
        Assert.False(SmokeScreenRule.Catches(LayerPos, Backward, LayerPos - Backward * 300f, ShippedRangeM, ShippedCos));
    }

    /// <summary>The cone's edge is 85° off the backward axis: 84° in, 86° out, at the same range.
    /// The full-cone reading would already reject 84°.</summary>
    [Fact]
    public void TheConeEdgeIsEightyFiveDegreesOffTheBackwardAxis()
    {
        Vector3 OffAxis(float deg) =>
            LayerPos + (Backward * Mathf.Cos(Mathf.DegToRad(deg)) + Vector3.Right * Mathf.Sin(Mathf.DegToRad(deg))) * 300f;
        Assert.True(SmokeScreenRule.Catches(LayerPos, Backward, OffAxis(84f), ShippedRangeM, ShippedCos));
        Assert.False(SmokeScreenRule.Catches(LayerPos, Backward, OffAxis(86f), ShippedRangeM, ShippedCos));
        // 60° off axis: inside the half-angle reading, outside a full-cone misreading's 42.5° edge.
        Assert.True(SmokeScreenRule.Catches(LayerPos, Backward, OffAxis(60f), ShippedRangeM, ShippedCos));
        Assert.False(SmokeScreenRule.Catches(LayerPos, Backward, OffAxis(60f), ShippedRangeM, Mathf.Cos(Mathf.DegToRad(42.5f))),
            "a cosine of the quarter angle would reject 60°, which is what a full-cone reading of the stored value amounts to");
    }

    /// <summary>The range is a raw distance compared strictly: 599 m in, 600 m out, and a victim
    /// standing exactly on the layer normalises to nothing and is not caught.</summary>
    [Fact]
    public void TheRangeIsRawAndStrict()
    {
        Assert.True(SmokeScreenRule.Catches(LayerPos, Backward, LayerPos + Backward * 599f, ShippedRangeM, ShippedCos));
        Assert.False(SmokeScreenRule.Catches(LayerPos, Backward, LayerPos + Backward * 600f, ShippedRangeM, ShippedCos));
        Assert.False(SmokeScreenRule.Catches(LayerPos, Backward, LayerPos, ShippedRangeM, ShippedCos));
    }

    /// <summary>The wash cadence over one victim's re-arm timer: 0.97 on the first frame inside,
    /// nothing while the 2 s timer sits at or above 0.5 s, 0.9 once it drops below (1.5 s later),
    /// and 0.97 again only after the timer has fully run down outside the screen.</summary>
    [Fact]
    public void TheWashFiresAtNinetySevenThenNinetyEveryOneAndAHalfSeconds()
    {
        float rearm = 0f;
        Assert.Equal(SmokeScreenRule.FirstWashWeight, SmokeScreenRule.StepWash(ref rearm, Dt, inside: true));
        Assert.Equal(SmokeScreenRule.WashDurationS, rearm);

        // 1.49 s of frames inside: the timer runs from 2.0 toward 0.51 and never re-arms.
        int frames = 0;
        for (; frames < 89; frames++)
            Assert.Null(SmokeScreenRule.StepWash(ref rearm, Dt, inside: true));
        Assert.True(rearm > SmokeScreenRule.WashRearmBelowS, $"timer still at {rearm:0.000}");

        // The frame that takes it below 0.5 s re-arms at 0.9, since the timer had not reached zero.
        float? weight = null;
        for (int guard = 0; guard < 5 && weight == null; guard++)
            weight = SmokeScreenRule.StepWash(ref rearm, Dt, inside: true);
        Assert.Equal(SmokeScreenRule.WashWeight, weight);
        Assert.Equal(SmokeScreenRule.WashDurationS, rearm);

        // Leave for 2.5 s: the timer runs down to zero and stays there.
        for (int i = 0; i < 150; i++)
            Assert.Null(SmokeScreenRule.StepWash(ref rearm, Dt, inside: false));
        Assert.Equal(0f, rearm);

        // Back inside: a fully expired timer gives the first-hit weight again.
        Assert.Equal(SmokeScreenRule.FirstWashWeight, SmokeScreenRule.StepWash(ref rearm, Dt, inside: true));
    }

    /// <summary>A frame outside the screen still runs the timer down and never washes.</summary>
    [Fact]
    public void AFrameOutsideRunsTheTimerDownAndWashesNothing()
    {
        float rearm = 1f;
        Assert.Null(SmokeScreenRule.StepWash(ref rearm, 0.25f, inside: false));
        Assert.Equal(0.75f, rearm, 5);
        Assert.Null(SmokeScreenRule.StepWash(ref rearm, 5f, inside: false));
        Assert.Equal(0f, rearm);
    }

    /// <summary>The wash's own literals: the grey-green, the 2 s, and the two weights.</summary>
    [Fact]
    public void TheWashLiteralsAreTheRoutines()
    {
        Assert.Equal(new Color(0.2f, 0.29f, 0.145f), SmokeScreenRule.WashColour);
        Assert.Equal(2f, SmokeScreenRule.WashDurationS);
        Assert.Equal(0.9f, SmokeScreenRule.WashWeight);
        Assert.Equal(0.97f, SmokeScreenRule.FirstWashWeight);
    }

    /// <summary>The loader's defaults for absent keys are the static image's, not the shipped
    /// values: 200 m, a stored cosine of 0.8, 3 s.</summary>
    [Fact]
    public void TheImageDefaultsAreTheLoadersOwn()
    {
        Assert.Equal(200f, SmokeScreenTunables.Image.RangeM);
        Assert.Equal(0.8f, SmokeScreenTunables.Image.HalfAngleCos);
        Assert.Equal(3f, SmokeScreenTunables.Image.StunIntervalS);
    }

    /// <summary><c>From</c> stores the angle the way the loader does.</summary>
    [Fact]
    public void FromStoresTheHalfAngleCosine()
    {
        var t = SmokeScreenTunables.From(ShippedRangeM, ShippedAngleDeg, ShippedIntervalS);
        Assert.Equal(ShippedRangeM, t.RangeM);
        Assert.Equal(ShippedCos, t.HalfAngleCos, 6);
        Assert.Equal(ShippedIntervalS, t.StunIntervalS);
    }

    /// <summary>The shipped numbers, read out of the extracted player.json rather than restated:
    /// 600 m, 170° stored as cos 85°, 5 s.</summary>
    [ExtractedDataFact]
    public void TheShippedTunablesReadOffPlayerJson()
    {
        var t = SmokeScreenTunables.Load(ZrdrPath);
        Assert.Equal(ShippedRangeM, t.RangeM);
        Assert.Equal(ShippedCos, t.HalfAngleCos, 6);
        Assert.Equal(ShippedIntervalS, t.StunIntervalS);
    }
}
