using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The per-mode node hiding of the pilot's own aircraft:
/// Cockpit draws the interior, Nose draws neither it nor the <c>markers</c>/<c>dontmove</c>
/// groups, and both hide the healthy body. <see cref="CockpitVisibility.Rules"/> is engine-free,
/// so these need no built plane; the tree walk that finds the four nodes is the in-engine
/// <c>cockpit-interior</c> suite's.
/// </summary>
public class CockpitVisibilityTests
{
    [Fact]
    public void AnExternalPoseRendersTheAircraftExactlyAsItWasBuilt()
    {
        var shown = CockpitVisibility.Rules(PilotViewMode.Chase, firstPerson: false);
        Assert.False(shown.Interior);
        Assert.True(shown.Body);
        Assert.True(shown.Markers);
        Assert.True(shown.Dontmove);
    }

    [Fact]
    public void CockpitShowsTheInteriorAndHidesTheHealthyBody()
    {
        var shown = CockpitVisibility.Rules(PilotViewMode.Cockpit, firstPerson: true);
        Assert.True(shown.Interior);
        Assert.False(shown.Body);
        // Only mode 7 strips these, the original's mode 6 leaves both drawn.
        Assert.True(shown.Markers);
        Assert.True(shown.Dontmove);
    }

    [Fact]
    public void NoseHidesTheInteriorTheBodyAndTheMarkersAndDontmoveGroups()
    {
        var shown = CockpitVisibility.Rules(PilotViewMode.Nose, firstPerson: true);
        Assert.False(shown.Interior);
        Assert.False(shown.Body);
        Assert.False(shown.Markers);
        Assert.False(shown.Dontmove);
    }

    [Fact]
    public void AnExternalPoseRestoresTheAircraftEvenWhileCockpitIsSelected()
    {
        // A held look-behind is an EXTERNAL pose: the selection is untouched (PilotView.Effective),
        // but the camera is outside the aircraft, so the body must be back while it is down.
        foreach (var mode in new[] { PilotViewMode.Cockpit, PilotViewMode.Nose })
        {
            var shown = CockpitVisibility.Rules(mode, firstPerson: false);
            Assert.True(shown.Body);
            Assert.False(shown.Interior);
            Assert.True(shown.Markers);
            Assert.True(shown.Dontmove);
        }
    }

    [Theory]
    [InlineData("bul1a")]
    [InlineData("bul5d")]
    [InlineData("lowalt_on")]
    [InlineData("stallwarning_on")]
    public void TheInteriorsDrivenStatesAreRecognisedByName(string name)
    {
        // All four ship active:true on all 11 airframes, so a pristine cockpit renders every
        // windshield bullet hole and both warning lamps unless the build parks them.
        Assert.True(PlaneBuilder.IsInteriorDrivenState(name));
    }

    [Theory]
    [InlineData("gauges")]        // the panel itself
    [InlineData("structure")]     // canopy frame
    [InlineData("nosedamage")]    // a damage-dial zone: always drawn, RECOLOURED by health
    [InlineData("ggindicator0")]  // a belt segment: likewise
    [InlineData("bullet")]        // the bare stem carries no quad
    [InlineData("bulletx")]
    [InlineData("bul")]
    public void OrdinaryInteriorGeometryIsNotTreatedAsADrivenState(string name)
    {
        Assert.False(PlaneBuilder.IsInteriorDrivenState(name));
    }

    [Theory]
    [InlineData("bullet1")]
    [InlineData("bullet5")]
    public void TheMeshlessHoleGroupsAreLeftAlone(string name)
    {
        // Parking a bulletN group would hide the bulNx quads under it for good, so the hole an
        // opened cockpit_bulletholes def lights would never reach the glass.
        Assert.False(PlaneBuilder.IsInteriorDrivenState(name));
    }

    [Fact]
    public void TheTwoFirstPersonViewsDifferOnlyInWhatTheyHide()
    {
        var cockpit = CockpitVisibility.Rules(PilotViewMode.Cockpit, firstPerson: true);
        var nose = CockpitVisibility.Rules(PilotViewMode.Nose, firstPerson: true);
        Assert.Equal(cockpit.Body, nose.Body);   // both hide it
        Assert.NotEqual(cockpit, nose);
    }
}
