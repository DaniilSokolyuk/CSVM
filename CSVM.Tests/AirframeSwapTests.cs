using CSVM.Session.Roster;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// What each of the three airframe swap codes carries past the rebuild (docs/formats/
/// anim-definitions/cutscenes.md, "The airframe swap codes 965, 966 and 967"): only 967, the
/// capture, has a captured vehicle to read, so only it hands the player that vehicle's damage and
/// its roster group.
/// </summary>
public class AirframeSwapTests
{
    [Fact]
    public void OnlyTheCaptureCodeCarriesTheCapturedGroup()
    {
        var hangar = AirframeSwapCodes.For(965)!.Value;
        var unhook = AirframeSwapCodes.For(966)!.Value;
        var capture = AirframeSwapCodes.For(967)!.Value;

        Assert.False(AirframeHandover.CarriesCapturedGroup(hangar));
        Assert.False(AirframeHandover.CarriesCapturedGroup(unhook));
        Assert.True(AirframeHandover.CarriesCapturedGroup(capture));
    }

    [Fact]
    public void TheGroupCarryRidesWithTheDamageCarry()
    {
        foreach (var code in AirframeSwapCodes.Table)
        {
            Assert.Equal(AirframeHandover.CarriesCapturedDamage(code),
                AirframeHandover.CarriesCapturedGroup(code));
        }
    }
}
