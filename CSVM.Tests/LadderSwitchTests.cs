using System.Collections.Generic;
using CSVM.Session.Campaign;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The rope-ladder switch's decoded rule, engine-free: the cos 45 degree attitude gate on the
/// aircraft's own up axis, the boundary-inclusive sensor sphere, the one-transition-per-tick
/// state machine between the two authored definitions, the silent flip when a mission authors
/// neither, and the settle callback that lands a transient state whatever else is raised.
/// </summary>
public class LadderSwitchTests
{
    [Fact]
    public void LevelIsWithinFortyFiveDegreesOfUprightOnEitherAxis()
    {
        Assert.True(LadderSwitch.IsLevel(Basis.Identity));
        Assert.True(LadderSwitch.IsLevel(Rolled(44f)));
        Assert.False(LadderSwitch.IsLevel(Rolled(46f)));
        Assert.True(LadderSwitch.IsLevel(Pitched(-44f)));
        Assert.False(LadderSwitch.IsLevel(Pitched(46f)));
        Assert.False(LadderSwitch.IsLevel(Rolled(180f)));
    }

    [Fact]
    public void SensorSphereIncludesItsBoundary()
    {
        var sensor = new Vector3(10f, 0f, 0f);
        Assert.True(LadderSwitch.WithinSensor(new Vector3(110f, 0f, 0f), sensor, 100f));
        Assert.False(LadderSwitch.WithinSensor(new Vector3(110.5f, 0f, 0f), sensor, 100f));
        Assert.True(LadderSwitch.WithinSensor(new Vector3(10f, 60f, 60f), sensor, 100f));
    }

    [Fact]
    public void WantedDropsOnceAndSettlesOnTheCallback()
    {
        var started = new List<string>();
        var sw = new LadderSwitch();
        bool Start(string name)
        {
            started.Add(name);
            return true;
        }

        Assert.Equal(LadderSwitch.DropAnim, sw.Step(true, Start));
        Assert.Equal(LadderState.Deploying, sw.State);
        // Still wanted, still deploying: nothing more is started, and losing the gate mid-drop
        // does not retract either, because the drop has not settled.
        Assert.Null(sw.Step(true, Start));
        Assert.Null(sw.Step(false, Start));
        Assert.Equal(LadderState.Deploying, sw.State);

        Assert.True(sw.Settle(LadderSwitch.SettleCode, LadderSwitch.DropAnim));
        Assert.Equal(LadderState.Deployed, sw.State);
        Assert.Null(sw.Step(true, Start));

        Assert.Equal(LadderSwitch.RetractAnim, sw.Step(false, Start));
        Assert.Equal(LadderState.Retracting, sw.State);
        Assert.True(sw.Settle(LadderSwitch.SettleCode, LadderSwitch.RetractAnim));
        Assert.Equal(LadderState.Retracted, sw.State);
        Assert.Equal(new[] { LadderSwitch.DropAnim, LadderSwitch.RetractAnim }, started);
    }

    [Fact]
    public void WithoutDefinitionsTheStateFlipsAtOnce()
    {
        var sw = new LadderSwitch();
        Assert.Null(sw.Step(true, _ => false));
        Assert.Equal(LadderState.Deployed, sw.State);
        Assert.Null(sw.Step(false, _ => false));
        Assert.Equal(LadderState.Retracted, sw.State);
    }

    [Fact]
    public void SettleDeclinesOtherCodesAndDefinitions()
    {
        var sw = new LadderSwitch();
        sw.Step(true, _ => true);
        Assert.False(sw.Settle(20, LadderSwitch.DropAnim));
        Assert.False(sw.Settle(LadderSwitch.SettleCode, "pickup_timing"));
        Assert.False(sw.Settle(LadderSwitch.SettleCode, null));
        Assert.Equal(LadderState.Deploying, sw.State);
        // The settle lands the definition's end state from any state, as the original's host
        // writes the slot unconditionally.
        Assert.True(sw.Settle(LadderSwitch.SettleCode, LadderSwitch.RetractAnim));
        Assert.Equal(LadderState.Retracted, sw.State);
    }

    [Fact]
    public void OneHumanHoldsTheSwitchUntilTheyStopQualifying()
    {
        var p1 = new Human();
        var p2 = new Human();
        var field = new[] { p1, p2 };

        // Nobody qualifies: nobody holds it, which is the answer that retracts the ladder.
        Assert.Null(LadderSwitch.Holder<Human>(null, field, h => h.Qualifies));

        // First in player order takes it.
        p1.Qualifies = true;
        p2.Qualifies = true;
        var held = LadderSwitch.Holder<Human>(null, field, h => h.Qualifies);
        Assert.Same(p1, held);

        // The second human qualifying changes nothing while the first still does: this is the
        // thrash the single-owner latch exists to stop.
        Assert.Same(p1, LadderSwitch.Holder(held, field, h => h.Qualifies));

        // The incumbent drops out and the other one is already there, so the switch moves rather
        // than going empty: no retract is started between the two.
        p1.Qualifies = false;
        held = LadderSwitch.Holder(held, field, h => h.Qualifies);
        Assert.Same(p2, held);

        // And it does not go back to P1 on their return while P2 still qualifies.
        p1.Qualifies = true;
        Assert.Same(p2, LadderSwitch.Holder(held, field, h => h.Qualifies));

        p2.Qualifies = false;
        Assert.Same(p1, LadderSwitch.Holder(held, field, h => h.Qualifies));

        p1.Qualifies = false;
        Assert.Null(LadderSwitch.Holder(p1, field, h => h.Qualifies));
    }

    private static Basis Rolled(float degrees) =>
        new(Vector3.Back, Mathf.DegToRad(degrees));

    private static Basis Pitched(float degrees) =>
        new(Vector3.Right, Mathf.DegToRad(degrees));

    // One human of the field, as much of a rig as the holder rule reads.
    private sealed class Human
    {
        public bool Qualifies;
    }
}
