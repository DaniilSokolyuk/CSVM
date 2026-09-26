using System.IO;
using CSVM.Flight.Camera;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The per-plane camera reader (<see cref="CamParams"/>), against
/// <c>fixtures/zrdr/camparam.json</c>, hand-authored from
/// <c>docs/formats/camparam.md</c>, so its numbers are deliberately NOT the shipped ones. The
/// real values are asserted separately, against a live extraction, in
/// <see cref="ExtractedGoldenTests"/>.
/// </summary>
public class CamParamsTests
{
    [Fact]
    public void AnAirframeWithNoBlockOfItsOwnTakesTheDefaultsWhole()
    {
        // The Devastator is one of the four with no override in the shipped file, and has none here.
        var cam = Load("player_pfighter");
        Assert.Null(cam.DisplayName);
        Assert.Equal(11.0f, cam.Dist);
        Assert.Equal(12.5f, cam.DistMin);
    }

    [Fact]
    public void AnAirframesOwnBlockOverlaysOnlyTheKeysItCarries()
    {
        var cam = Load("player_bhawk");
        Assert.Equal("Bloodhawk", cam.DisplayName);
        // Its own three keys win...
        Assert.Equal(19.5f, cam.Dist);
        Assert.Equal(19.5f, cam.DistMin);
        Assert.Equal(26.0f, cam.DistMax);
        // ...and everything it does not name still comes from default. This is the property that
        // makes a three-key block legal at all.
        Assert.Equal(2.5f, cam.PosCatchUp);
        Assert.Equal(0.15f, cam.ThirdpHeight);
        Assert.Equal(0.31f, cam.ThirdpPitch);
    }

    [Fact]
    public void TheFirebrandResolvesThroughItsModelNodeName()
    {
        // player_fbrand -> "Firebrand" only via MarkerRig.PlayerAirframes; stripping the leading
        // 'p' and title-casing instead yields "Fbrand", matching no block.
        var cam = Load("player_fbrand");
        Assert.Equal("Firebrand", cam.DisplayName);
        Assert.Equal(21.5f, cam.Dist);
        Assert.NotEqual(11.0f, cam.Dist);
    }

    [Fact]
    public void ABlockMayOverrideMoreThanDistance()
    {
        // The Balmoral is the one airframe that also moves the third-person height and pitch.
        var cam = Load("player_balmoral");
        Assert.Equal(0.25f, cam.ThirdpHeight);
        Assert.Equal(0.22f, cam.ThirdpPitch);
    }

    [Fact]
    public void AnUnknownPlaneNameFallsBackToTheDefaultsRatherThanThrowing()
    {
        var cam = Load("player_not_an_airframe");
        Assert.Null(cam.DisplayName);
        Assert.Equal(11.0f, cam.Dist);
        Assert.True(cam.FromData);
    }

    [Fact]
    public void AMissingFileYieldsTheHardCodedDefaultsAndSaysSo()
    {
        // A partial extraction must still fly. FromData is how the session knows to report it,
        // and is the difference between "the data says 13" and "we guessed 13".
        var cam = CamParams.Load(TestData.TempDir(), "player_bhawk");
        Assert.False(cam.FromData);
        Assert.Equal(13f, cam.Dist);
    }

    [Fact]
    public void TheShippedDefaultBlockHasAMinimumLargerThanItsBase()
    {
        // Not a fixture property, a claim about the real file, pinned here so the anomaly that
        // blocks the dynamic-distance decode cannot be quietly "fixed" by a future reader. The
        // hard-coded fallbacks carry the shipped default block verbatim.
        var cam = CamParams.Load(TestData.TempDir(), "player_bhawk");
        Assert.Equal(13f, cam.Dist);
        Assert.Equal(15.7f, cam.DistMin);
        Assert.True(cam.DistMin > cam.Dist);
    }

    private static CamParams Load(string planeNodeName) =>
        CamParams.Load(TestData.Fixture("zrdr"), planeNodeName);
}
