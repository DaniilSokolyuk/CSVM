using CSVM.Flight.Hud;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="TargetHud"/>'s off-engine pins: the hostile-marker tag
/// (<see cref="TargetHud.HostileTag"/>), where the AI spawner's "ai1_player_fury" naming reads
/// back as "AI1", and the off-screen marker's decoded geometry
/// (<see cref="TargetHud.ArrowHead"/>, <see cref="TargetHud.EdgeLabelAnchor"/>). The selection
/// itself (<see cref="TargetHud.NearestHostile"/>) filters on live
/// <see cref="Flight.Airframe.FlightController"/> sources, which are engine nodes, so its pins live in the
/// <c>hostile-marker-hud</c> in-engine suite instead.
/// </summary>
public class HostileTagTests
{
    [Theory]
    [InlineData("ai1_player_fury", "AI1")]
    [InlineData("ai12_player_bhawk", "AI12")]
    [InlineData("bandit", "BANDIT")]
    public void TagIsTheFirstNameSegmentUppercased(string name, string expected)
    {
        Assert.Equal(expected, TargetHud.HostileTag(name));
    }

    [Fact]
    public void AnEmptyNameFallsBackToAi()
    {
        Assert.Equal("AI", TargetHud.HostileTag(""));
    }

    [Fact]
    public void ALeadingUnderscoreKeepsTheWholeName()
    {
        // IndexOf 0 is not a cut: "_x" has no head segment to take.
        Assert.Equal("_X", TargetHud.HostileTag("_x"));
    }

    [Fact]
    public void TheArrowheadIs20BackAlongTheBearingAnd5ToEachSide()
    {
        var (point, left, right) = TargetHud.ArrowHead(new Vector2(400f, 100f), Vector2.Right, 1f);
        Assert.Equal(new Vector2(400f, 100f), point);
        Assert.Equal(new Vector2(380f, 105f), left);
        Assert.Equal(new Vector2(380f, 95f), right);
        // 10 across the base, not the 16 the head carried before the decode.
        Assert.Equal(10f, left.DistanceTo(right));
    }

    [Fact]
    public void TheArrowheadTurnsWithTheBearingAndScales()
    {
        var (point, left, right) = TargetHud.ArrowHead(new Vector2(200f, 700f), Vector2.Down, 2f);
        Assert.Equal(new Vector2(200f, 700f), point);
        // Back is up-screen at twice the reference size, and the base is across the bearing.
        Assert.Equal(new Vector2(190f, 660f), left);
        Assert.Equal(new Vector2(210f, 660f), right);
        Assert.Equal(2f * 20f, point.DistanceTo((left + right) / 2f));
    }

    [Fact]
    public void TheEdgeLabelSitsBelowTheAnchorInThePanesUpperHalfAndAboveItInTheLower()
    {
        const float PaneHeight = 800f;
        var upper = TargetHud.EdgeLabelAnchor(new Vector2(120f, 100f), PaneHeight, 1f);
        Assert.Equal(new Vector2(120f, 103f), upper);
        var lower = TargetHud.EdgeLabelAnchor(new Vector2(120f, 700f), PaneHeight, 1f);
        Assert.Equal(new Vector2(120f, 655f), lower);
    }

    [Fact]
    public void TheEdgeLabelKeepsTheAnchorsXAndScalesItsOffset()
    {
        // No back-off along the arrow's direction: x is the anchor's whatever the bearing.
        var placed = EdgeMarker.Resolve(new Vector2(5000f, 400f), false, new Vector2(1000f, 800f));
        Assert.Equal(placed.Anchor.X, TargetHud.EdgeLabelAnchor(placed.Anchor, 800f, 1f).X);
        Assert.Equal(new Vector2(120f, 106f),
            TargetHud.EdgeLabelAnchor(new Vector2(120f, 100f), 800f, 2f));
        Assert.Equal(new Vector2(120f, 610f),
            TargetHud.EdgeLabelAnchor(new Vector2(120f, 700f), 800f, 2f));
    }
}
