using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="CraterGate.For"/>, the decision "does this ground strike ask for a crater", taken
/// apart from the scene that would answer it. Two rules meet here. The original's own is the struck
/// node's <c>CAN_MODIFY</c> flag over a <c>CRATER</c> weapon, which no shipped node ever satisfies
/// (docs/org/craters.md). The remake's own carve option is the other, under which any rocket
/// warhead's ground burst carves. The cases are the four corners of that pair, plus the one that
/// decides which of them a strike goes through. That one matters because only the faithful rule
/// suppresses the weapon's impact row, and the option must never take a burst away.
/// </summary>
public class CraterGateTests
{
    /// <summary>The shipped default: the option is off and no node carries the flag, so a rocket
    /// bursting on the ground asks for nothing. This is what every pinned golden is pinned on.</summary>
    [Fact]
    public void OffAndUnstampedAsksNothing()
    {
        Assert.Equal(CraterAsk.None, CraterGate.For(Rocket(), canModify: false, optionOn: false));
        Assert.Equal(CraterAsk.None, CraterGate.For(CraterRocket(), canModify: false, optionOn: false));
    }

    /// <summary>The option on: the same rocket carves, although the struck node carries no stamp.
    /// The ask is Optional rather than Faithful, which is what leaves its burst playing.</summary>
    [Fact]
    public void OnCarvesForARocket()
    {
        Assert.Equal(CraterAsk.Optional, CraterGate.For(Rocket(), canModify: false, optionOn: true));
    }

    /// <summary>The option is the rocket's alone: a cannon round on the same ground still asks for
    /// nothing, so turning the row on does not pit the terrain under every burst of gunfire.</summary>
    [Fact]
    public void OnLeavesGunsAlone()
    {
        Assert.Equal(CraterAsk.None, CraterGate.For(Gun(), canModify: false, optionOn: true));
        Assert.Equal(CraterAsk.None, CraterGate.For(Gun(), canModify: true, optionOn: true));
    }

    /// <summary>The original's rule, on the node no shipped chapter carries: a CRATER weapon over a
    /// can_modify collider asks through the faithful door whatever the option says, so a stamped
    /// node keeps the decoded suppression instead of the option's additive bowl.</summary>
    [Fact]
    public void AStampedNodeStaysFaithful()
    {
        Assert.Equal(CraterAsk.Faithful, CraterGate.For(CraterRocket(), canModify: true, optionOn: false));
        Assert.Equal(CraterAsk.Faithful, CraterGate.For(CraterRocket(), canModify: true, optionOn: true));
    }

    /// <summary>A stamped node under a weapon that carries no CRATER falls back to the option: the
    /// original's rule is an AND of both halves, so the flag alone never carves.</summary>
    [Fact]
    public void AStampedNodeNeedsACraterWeapon()
    {
        Assert.Equal(CraterAsk.None, CraterGate.For(Rocket(), canModify: true, optionOn: false));
        Assert.Equal(CraterAsk.Optional, CraterGate.For(Rocket(), canModify: true, optionOn: true));
    }

    private static WeaponDef Rocket() => new() { Id = "test_rocket", IsRocket = true };

    private static WeaponDef Gun() => new() { Id = "test_gun", Caliber = 30, IsCannon = true };

    private static WeaponDef CraterRocket() => new() { Id = "test_choker", IsRocket = true, Crater = true };
}
