using System;
using System.Collections.Generic;
using CSVM.Bindings;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Throttle (lever) action: a whole axis read as one absolute position, 0 at one end of its
/// travel and 1 at the other. It is a full axis on a row in no pair, so it shares the pair rows'
/// token and steal rule. The takeover rule decides when that position drives the commanded lever
/// and when the rate keys do.
/// </summary>
public class ThrottleLeverTests
{
    private static readonly DeviceId Stick = DeviceId.Joypad("03005fcf1d2300000002000000000000");

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 0.5f)]
    [InlineData(0.5f, 0.75f)]
    [InlineData(1f, 1f)]
    public void ALeverMapsItsWholeTravelOntoZeroToOne(float raw, float position)
    {
        var map = LeverMap(BindingControl.FullAxis(2, inverted: false, 0f));

        Assert.Equal(position, Read(map, raw), 5);
    }

    [Fact]
    public void InvertTurnsTheLeverRoundEndForEnd()
    {
        var map = LeverMap(BindingControl.FullAxis(2, inverted: true, 0f));

        Assert.Equal(1f, Read(map, -1f), 5);
        Assert.Equal(0.25f, Read(map, 0.5f), 5);
        Assert.Equal(0f, Read(map, 1f), 5);
    }

    /// <summary>The deadzone trims both ends of the travel, so a lever whose detents stop short of
    /// full scale still reaches idle and full. The middle stays the middle.</summary>
    [Fact]
    public void TheDeadzoneTrimsBothEndsOfTheTravel()
    {
        var map = LeverMap(BindingControl.FullAxis(2, inverted: false, 0.1f));

        Assert.Equal(0f, Read(map, -0.95f), 5);
        Assert.Equal(0f, Read(map, -0.9f), 5);
        Assert.Equal(0.5f, Read(map, 0f), 5);
        Assert.Equal(0.75f, Read(map, 0.45f), 5);
        Assert.Equal(1f, Read(map, 0.9f), 5);
        Assert.Equal(1f, Read(map, 1f), 5);
    }

    /// <summary>A pad trigger rests at 0 and pulls to 1. As a half axis on the lever row it is
    /// already a lever from idle to full.</summary>
    [Fact]
    public void AHalfAxisOnTheLeverRowReadsItsOwnTravel()
    {
        var map = LeverMap(BindingControl.Axis(5, 1, 0f));

        Assert.Equal(0.6f, Read(map, 0.6f), 5);
        Assert.Equal(0f, Read(map, -0.6f), 5);
    }

    [Fact]
    public void TheLeverRowHoldsAFullAxisAloneWithNoPartner()
    {
        var map = new ActionMap();
        var lever = new Binding(Stick, BindingControl.FullAxis(2, false, 0f));

        Assert.True(map.Add(InputAction.ThrottleLever, lever));
        Assert.Equal(new[] { lever }, map.Bindings(InputAction.ThrottleLever));
        Assert.Empty(map.Bindings(InputAction.ThrottleUp));
        Assert.Equal(new[] { InputAction.ThrottleLever }, map.OwnersOf(lever));
        Assert.True(AxisPairs.IsAbsolute(InputAction.ThrottleLever));
        Assert.True(AxisPairs.TakesFullAxis(InputAction.ThrottleLever));
        Assert.Null(AxisPairs.PartnerOf(InputAction.ThrottleLever));
        Assert.Equal(0, AxisPairs.SideOf(InputAction.ThrottleLever));
    }

    /// <summary>The lever's axis is the same physical control a pair row's full axis is, so the
    /// steal rule moves it between them whole.</summary>
    [Fact]
    public void AssigningTheLeverStealsTheAxisFromBothRowsOfAPair()
    {
        var map = new ActionMap();
        map.Assign(InputAction.ThrottleUp, new Binding(Stick, BindingControl.FullAxis(2, false, 0.08f)));
        var lever = new Binding(Stick, BindingControl.FullAxis(2, true, 0.02f));

        Assert.Equal(new[] { InputAction.ThrottleUp, InputAction.ThrottleDown }, map.Assign(InputAction.ThrottleLever, lever));
        Assert.Empty(map.Bindings(InputAction.ThrottleUp));
        Assert.Empty(map.Bindings(InputAction.ThrottleDown));
        Assert.Equal(new[] { lever }, map.Bindings(InputAction.ThrottleLever));

        Assert.Equal(new[] { InputAction.ThrottleLever }, map.Assign(InputAction.PitchUp, new Binding(Stick, BindingControl.FullAxis(2, false, 0f))));
        Assert.Empty(map.Bindings(InputAction.ThrottleLever));
    }

    [Fact]
    public void UnassigningTheLeverClearsOnlyItsOwnRow()
    {
        var map = new ActionMap();
        var lever = new Binding(Stick, BindingControl.FullAxis(2, false, 0f));
        var up = new Binding(Stick, BindingControl.FullAxis(3, false, 0f));
        map.Assign(InputAction.ThrottleLever, lever);
        map.Assign(InputAction.ThrottleUp, up);

        Assert.True(map.Unassign(InputAction.ThrottleLever, lever));
        Assert.Empty(map.Bindings(InputAction.ThrottleLever));
        Assert.Equal(new[] { up }, map.Bindings(InputAction.ThrottleDown));
    }

    /// <summary>Capture on the lever row asks for the lever pushed toward full throttle. The
    /// direction moved decides invert the way it does on a pair's positive row.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    public void ACaptureOnTheLeverRowInfersInvertFromTheDirectionMoved(int moved, bool inverted)
    {
        var control = AxisPairs.FullAxisFor(InputAction.ThrottleLever, 4, moved, 0.02f);

        Assert.Equal(ControlKind.FullAxis, control.Kind);
        Assert.Equal(4, control.Index);
        Assert.Equal(inverted, control.Inverted);
        Assert.Equal(0.02f, control.Deadzone);

        var map = new ActionMap();
        map.Assign(InputAction.ThrottleLever, new Binding(Stick, control));
        var state = new FakeDevices();
        state.Axes[(Stick, 4)] = moved;
        Assert.Equal(1f, map.Resolve(state).Value(InputAction.ThrottleLever), 5);
    }

    [Fact]
    public void TheLeverTokenIsTheFullAxisTokenAndRoundTripsUnderItsOwnRow()
    {
        var profile = BindingProfile.Defaults(default, readsKeyboard: true);
        var lever = new Binding(Stick, BindingControl.FullAxis(2, true, 0.0125f));
        profile.Map(InputContext.Flight).Assign(InputAction.ThrottleLever, lever);

        string json = BindingStore.Serialize(1, profile);
        Assert.Contains("\"ThrottleLever\": [\n", json.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\"pad:03005fcf1d2300000002000000000000/fullaxis:2-@0.0125\"", json, StringComparison.Ordinal);
        Assert.Equal(3, BindingStore.Version);

        var loaded = BindingStore.Deserialize(json, default, readsKeyboard: true).Map(InputContext.Flight);
        Assert.Equal(new[] { lever }, loaded.Bindings(InputAction.ThrottleLever));
        Assert.DoesNotContain(loaded.Bindings(InputAction.ThrottleUp), b => b.Device == Stick);
    }

    [Fact]
    public void AFullAxisStaysUnreadableOnARowThatIsNeitherPairNorLever()
    {
        const string json = """
            { "version": 3, "contexts": { "flight": {
              "FireGuns": ["pad:03005fcf1d2300000002000000000000/fullaxis:2+@0"],
              "ThrottleLever": ["pad:03005fcf1d2300000002000000000000/fullaxis:3+@0"] } } }
            """;
        var map = BindingStore.Deserialize(json, default, readsKeyboard: true).Map(InputContext.Flight);

        Assert.DoesNotContain(map.Bindings(InputAction.FireGuns), b => b.Control.Kind == ControlKind.FullAxis);
        Assert.Single(map.Bindings(InputAction.ThrottleLever));
    }

    [Fact]
    public void TheLeverShipsUnboundInTheFlightContextUnderItsOwnCaption()
    {
        Assert.Equal(InputContext.Flight, DefaultBindings.ContextOf(InputAction.ThrottleLever));
        Assert.Contains(InputAction.ThrottleLever, DefaultBindings.ActionsIn(InputContext.Flight));
        Assert.Contains(InputAction.ThrottleLever, DefaultBindings.Unbound);
        Assert.Empty(DefaultBindings.MapFor(InputContext.Flight, default).Bindings(InputAction.ThrottleLever));
        Assert.Equal("Throttle (lever)", BindingLabels.Name(InputAction.ThrottleLever));
    }

    [Fact]
    public void TheFirstReadingSeedsTheLeverWithoutMovingTheThrottle()
    {
        var takeover = new LeverTakeover();

        Assert.Null(takeover.Step(0.7f, otherCommand: false));
        Assert.Null(takeover.Step(0.7f, otherCommand: false));
        Assert.False(takeover.Engaged);
    }

    [Fact]
    public void ALeverStillWithinEpsilonNeverTakesOver()
    {
        var takeover = new LeverTakeover();
        takeover.Step(0.5f, false);

        Assert.Null(takeover.Step(0.5f + (LeverTakeover.Epsilon * 0.9f), false));
        Assert.Null(takeover.Step(0.5f - (LeverTakeover.Epsilon * 0.9f), false));
        Assert.Null(takeover.Step(0.5f, false));
    }

    /// <summary>Once engaged the lever holds the command every tick, even through a move slower than
    /// the epsilon per tick. It comes to rest where the lever is rather than a step short.</summary>
    [Fact]
    public void AMovePastEpsilonTakesOverAndThenFollowsEveryTick()
    {
        var takeover = new LeverTakeover();
        takeover.Step(0.5f, false);

        Assert.Equal(0.53f, takeover.Step(0.53f, false));
        Assert.True(takeover.Engaged);
        Assert.Equal(0.535f, takeover.Step(0.535f, false));
        Assert.Equal(0.535f, takeover.Step(0.535f, false));
    }

    [Fact]
    public void AnotherCommandWithTheLeverStillHandsTheThrottleBackUntilTheLeverMovesAgain()
    {
        var takeover = new LeverTakeover();
        takeover.Step(0.2f, false);
        takeover.Step(0.6f, false);

        Assert.Null(takeover.Step(0.6f, otherCommand: true));
        Assert.False(takeover.Engaged);
        Assert.Null(takeover.Step(0.61f, false));
        Assert.Null(takeover.Step(0.6f - (LeverTakeover.Epsilon * 0.9f), false));
        Assert.Equal(0.65f, takeover.Step(0.65f, false));
        Assert.True(takeover.Engaged);
    }

    /// <summary>A hand on the lever beats a key held in the same tick: the move is the newer and more
    /// deliberate command.</summary>
    [Fact]
    public void AMovePastEpsilonTakesOverEvenWhileAnotherCommandIsHeld()
    {
        var takeover = new LeverTakeover();
        takeover.Step(0.2f, false);

        Assert.Equal(0.4f, takeover.Step(0.4f, otherCommand: true));
        Assert.True(takeover.Engaged);
    }

    [Fact]
    public void ReleasingReseedsSoAPlacedThrottleIsNotPulledBackToTheLever()
    {
        var takeover = new LeverTakeover();
        takeover.Step(0.2f, false);
        takeover.Step(0.9f, false);

        takeover.Release();

        Assert.False(takeover.Engaged);
        Assert.Null(takeover.Step(0.9f, false));
        Assert.Null(takeover.Step(0.9f, false));
        Assert.Equal(0.5f, takeover.Step(0.5f, false));
    }

    private static ActionMap LeverMap(BindingControl control)
    {
        var map = new ActionMap();
        map.Assign(InputAction.ThrottleLever, new Binding(Stick, control));
        return map;
    }

    private static float Read(ActionMap map, float raw)
    {
        var state = new FakeDevices();
        state.Axes[(Stick, map.Bindings(InputAction.ThrottleLever)[0].Control.Index)] = raw;
        return map.Resolve(state).Value(InputAction.ThrottleLever);
    }

    private sealed class FakeDevices : IDeviceState
    {
        public Dictionary<(DeviceId Device, int Index), float> Axes { get; } = new();

        public bool IsKeyDown(DeviceId device, int keyCode) => false;

        public bool IsButtonDown(DeviceId device, int button) => false;

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public float AxisValue(DeviceId device, int axis) =>
            Axes.TryGetValue((device, axis), out float value) ? value : 0f;

        public HatDirection HatState(DeviceId device, int hat) => HatDirection.None;
    }
}
