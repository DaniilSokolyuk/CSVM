using System.Collections.Generic;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Mech3;
using CSVM.UI.Boards;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The spyglass's engine-free rules (<see cref="Spyglass"/>) and the two <see cref="TargetHud"/>
/// placement helpers the disc changes: the fog-derived range gate with its engage/release
/// asymmetry and its 2000 m ceiling, the constant-apparent-size field of view and both its clamps,
/// the camera pose's roll rule, where the arrow's shaft starts, and the label block's disc variant.
/// Plus the layer band each pilot's own aeroplane is drawn on and the single bit the disc's cull
/// mask drops, which is what keeps the aircraft the eye sits inside out of the picture.
/// Decode: docs/org/spyglass.md.
/// </summary>
public class SpyglassTests
{
    [Fact]
    public void TheGateSitsFourFifthsIntoTheFogBand()
    {
        // 200 + (1200 - 200) * 0.8 = 1000, held; unheld it is the same figure times 0.875.
        Assert.Equal(1000f, Spyglass.RangeGate(new Vector2(200f, 1200f), held: true), 3);
        Assert.Equal(875f, Spyglass.RangeGate(new Vector2(200f, 1200f), held: false), 3);
    }

    [Fact]
    public void ThePictureEngagesNearerThanItReleases()
    {
        var band = new Vector2(0f, 1000f);
        float engage = Spyglass.RangeGate(band, held: false);
        float release = Spyglass.RangeGate(band, held: true);
        Assert.True(engage < release, $"engage {engage} is not inside release {release}");

        // A target between the two gates is picked up by neither and dropped by neither, which is
        // the whole point of the pair: it cannot strobe at the boundary.
        float between = (engage + release) / 2f;
        Assert.True(between > engage && between <= release);
    }

    [Fact]
    public void TheGateIsCappedAtTwoKilometres()
    {
        // The no-fog band (WeatherRig's 1e8/1e9) would otherwise put the gate at 800 000 km.
        Assert.Equal(Spyglass.RangeCapM, Spyglass.RangeGate(new Vector2(1e8f, 1e9f), held: true), 3);
        Assert.Equal(Spyglass.RangeCapM * Spyglass.EngageFactor,
            Spyglass.RangeGate(new Vector2(1e8f, 1e9f), held: false), 3);
    }

    [Fact]
    public void ABandCarryingNoFogInformationTakesTheCapAlone()
    {
        // A pane with no weather rig bound reads (0, 0), and a degenerate band is not a zero gate:
        // that would leave the picture permanently down on the empty stage and in the suite rigs.
        Assert.Equal(Spyglass.RangeCapM, Spyglass.RangeGate(Vector2.Zero, held: true), 3);
        Assert.Equal(Spyglass.RangeCapM, Spyglass.RangeGate(new Vector2(500f, 500f), held: true), 3);
        Assert.Equal(Spyglass.RangeCapM, Spyglass.RangeGate(new Vector2(900f, 100f), held: true), 3);
    }

    [Fact]
    public void TheFieldOfViewHoldsATargetAtAConstantApparentSize()
    {
        // 2 * atan(1.1 R / d): doubling the distance and the radius together is the same picture,
        // which is the property the framing exists for.
        float near = Spyglass.FovDeg(5f, 400f);
        Assert.Equal(near, Spyglass.FovDeg(10f, 800f), 3);
        Assert.Equal(Mathf.RadToDeg(2f * Mathf.Atan(1.1f * 5f / 400f)), near, 3);

        // And a target twice as far at the same size subtends about half the angle.
        Assert.True(Spyglass.FovDeg(5f, 800f) < near);
    }

    [Fact]
    public void TheFieldOfViewIsClampedAtBothEnds()
    {
        Assert.Equal(1.5f, Spyglass.FovDeg(5f, 100000f), 3);   // a target far past the gate
        Assert.Equal(90f, Spyglass.FovDeg(500f, 10f), 3);      // a zeppelin filling the sky
        Assert.Equal(Spyglass.NoTargetFovDeg, Spyglass.FovDeg(0f, 500f), 3);
        Assert.Equal(Spyglass.NoTargetFovDeg, Spyglass.FovDeg(5f, 0f), 3);
    }

    [Fact]
    public void ThePoseStandsAtTheEyeAndLooksAtTheTarget()
    {
        var eye = new Vector3(10f, 200f, -30f);
        var target = new Vector3(10f, 200f, -530f);
        var pose = Spyglass.Pose(eye, target, Basis.Identity, keepRoll: false);
        Assert.Equal(eye, pose.Origin);

        // Godot cameras look down local -Z, so the basis' forward must be the bearing.
        var forward = -pose.Basis.Z;
        Assert.True(forward.IsEqualApprox((target - eye).Normalized()),
            $"forward {forward} is not the bearing to the target");
    }

    [Fact]
    public void KeepRollTurnsTheHorizonWithTheAeroplane()
    {
        var eye = Vector3.Zero;
        var target = new Vector3(0f, 0f, -500f);
        var banked = new Basis(Vector3.Forward, Mathf.DegToRad(30f));

        var level = Spyglass.Pose(eye, target, banked, keepRoll: false);
        Assert.True(Mathf.Abs(level.Basis.Y.Dot(Vector3.Up)) > 0.999f,
            "a level picture must keep world up, whatever the aeroplane is doing");

        var rolled = Spyglass.Pose(eye, target, banked, keepRoll: true);
        Assert.True(rolled.Basis.Y.IsEqualApprox(banked.Y),
            $"a rolled picture's up {rolled.Basis.Y} is not the aeroplane's {banked.Y}");
    }

    [Fact]
    public void ATargetDeadOverheadStillFrames()
    {
        // Straight up leaves no roll to build a basis from; the fallback must produce a finite
        // orthonormal pose rather than NaN, or the picture renders as garbage on a climb.
        var pose = Spyglass.Pose(Vector3.Zero, new Vector3(0f, 500f, 0f), Basis.Identity,
            keepRoll: false);
        Assert.True((-pose.Basis.Z).IsEqualApprox(Vector3.Up));
        Assert.True(Mathf.IsEqualApprox(pose.Basis.X.Length(), 1f));
        Assert.True(Mathf.IsEqualApprox(pose.Basis.Y.Length(), 1f));
    }

    [Fact]
    public void TheShaftStartsAtTheRimOnceThePictureIsUp()
    {
        var anchor = new Vector2(400f, 300f);
        var dir = new Vector2(1f, 0f);
        Assert.Equal(anchor, TargetHud.ShaftTail(anchor, dir, 1f, disc: false));

        // On the rim, on the bearing, so the shaft leaves the disc rather than crossing it, and it
        // scales with the HUD like every other reference-pixel figure.
        Assert.Equal(new Vector2(448f, 300f), TargetHud.ShaftTail(anchor, dir, 1f, disc: true));
        Assert.Equal(new Vector2(496f, 300f), TargetHud.ShaftTail(anchor, dir, 2f, disc: true));
    }

    [Fact]
    public void TheDiscLabelSitsThreeBelowTheRimWhereItFits()
    {
        // discBottom + 3, not anchor + 3: the block clears the picture instead of landing on it.
        var anchor = new Vector2(400f, 300f);
        Assert.Equal(new Vector2(400f, 351f), TargetHud.EdgeLabelAnchor(anchor, 1440f, 1f, disc: true));
        Assert.Equal(new Vector2(400f, 303f), TargetHud.EdgeLabelAnchor(anchor, 1440f, 1f));
    }

    [Fact]
    public void TheDiscLabelFlipsAboveTheRimWhenTheBlockWouldFallOffThePane()
    {
        // 48 below the rim is the three lines plus their gap. A disc low on the pane has no room
        // for that, so the block goes discTop - 45. The bare rule flips on the pane's half instead,
        // deliberately: with no disc there is nothing between the anchor and the label.
        var low = new Vector2(400f, 1380f);
        Assert.Equal(new Vector2(400f, 1287f), TargetHud.EdgeLabelAnchor(low, 1440f, 1f, disc: true));
        Assert.Equal(new Vector2(400f, 1335f), TargetHud.EdgeLabelAnchor(low, 1440f, 1f));

        // The boundary itself: a disc whose bottom plus the block lands exactly on the pane's edge
        // still fits, and one pixel lower does not.
        Assert.True(TargetHud.EdgeLabelAnchor(new Vector2(400f, 1344f), 1440f, 1f, true).Y > 1344f);
        Assert.True(TargetHud.EdgeLabelAnchor(new Vector2(400f, 1345f), 1440f, 1f, true).Y < 1345f);
    }

    [Fact]
    public void TheDiscsOwnGeometryIsTheAuthoredWindow()
    {
        // The sgwin window is 96 x 96 in every chapter's gamez and the mask circle is its
        // ftol((96 + 1) * 0.5); a mismatch between the two would show as a clipped picture.
        Assert.Equal(96f, Spyglass.RefWindow);
        Assert.Equal(48f, Spyglass.RefRadius);
        Assert.Equal(Spyglass.RefWindow / 2f, Spyglass.RefRadius);
    }

    [Fact]
    public void EachPilotsOwnAirframeGetsALayerNothingElseCulls()
    {
        // One bit per seat, and outside both bands that are already spoken for: the zone gate
        // narrows its own band every frame and a pane's cull mask clears the rest of the per-player
        // band, so an airframe on a bit inside either would vanish for a reason of its own.
        var seen = new HashSet<uint>();
        for (int i = 0; i < 4; i++)
        {
            uint layer = SplitScreen.OwnAirframeLayer(i);
            Assert.True(seen.Add(layer), $"seat {i} shares its airframe layer with another seat");
            Assert.Equal(0u, layer & ZoneGate.LayerBand);
            Assert.NotEqual(layer, SplitScreen.PlayerVisualLayer(i));
            Assert.Equal(layer, ZoneGate.CullMask(0xFFFFF, 2) & layer);
            for (int pane = 0; pane < 4; pane++)
            {
                // Every pane draws every pilot's aeroplane, which is what makes this band the
                // opposite of the private per-player one it sits below.
                Assert.Equal(layer, SplitScreen.PlayerCullMask(pane) & layer);
            }
        }
    }

    [Fact]
    public void TheDiscDropsThePilotsOwnAirframeAndNothingElse()
    {
        uint own = SplitScreen.OwnAirframeLayer(0);
        uint pane = SplitScreen.PlayerCullMask(0);
        uint disc = SpyglassView.DiscMask(pane, own);

        Assert.Equal(0u, disc & own);
        Assert.NotEqual(0u, pane & own);            // the control: the pane itself still draws it
        Assert.Equal(pane & ~own, disc);
        Assert.NotEqual(0u, disc & 1u);             // the shared world's own layer 1
        for (int other = 1; other < 4; other++)
        {
            // A splitscreen neighbour's aeroplane is a target like any other and stays in the
            // picture; only the aircraft the eye sits inside is taken out.
            Assert.NotEqual(0u, disc & SplitScreen.OwnAirframeLayer(other));
        }
    }

    [Fact]
    public void ThePictureIsArmedAtLevelLoad()
    {
        // A pilot who never opens the rebinding page still gets the picture; the toggle is what
        // turns it off. TargetHud.SpyglassOn starts here, so flipping this flips the shipped state.
        Assert.True(Spyglass.DefaultOn);
    }
}
