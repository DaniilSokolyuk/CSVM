using CSVM.Bindings;
using CSVM.Flight.Airframe;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="FlightReentryLatch"/>: flight's consumed-input latch, armed the instant flight
/// regains input after a cutscene skip or a pause-sheet dismiss so the control that confirmed it
/// cannot also read as a flight command. One control is bound on both sides (gamepad B is the
/// menu's Back and the gun trigger), and the arm takes no button reading, so the first read after
/// it is what decides whether a press was in progress.
/// </summary>
public class FlightReentryLatchTests
{
    [Fact]
    public void AFreshPressWithNothingArmedReadsHeld()
    {
        var latch = new FlightReentryLatch();

        Assert.True(latch.Read(InputAction.FireGuns, true));
    }

    [Fact]
    public void ArmingWithTheButtonUpCostsTheNextPressNothing()
    {
        var latch = new FlightReentryLatch();

        latch.Arm();

        // The first read finds nothing down and clears the latch, so a real press fires.
        Assert.False(latch.Read(InputAction.FireGuns, false));
        Assert.True(latch.Read(InputAction.FireGuns, true));
    }

    [Fact]
    public void HoldingBThroughASkipSuppressesTheGunsUntilItIsReleased()
    {
        var latch = new FlightReentryLatch();

        latch.Arm();                                          // B is still down as flight resumes
        Assert.False(latch.Read(InputAction.FireGuns, true));  // same press: no burst
        Assert.False(latch.Read(InputAction.FireGuns, true));  // still held: still no burst
        Assert.False(latch.Read(InputAction.FireGuns, false)); // released: the latch clears here
        Assert.True(latch.Read(InputAction.FireGuns, true));   // a fresh press fires as before
    }

    [Fact]
    public void HoldingAThroughAResumeSuppressesTheRocketsUntilItIsReleased()
    {
        var latch = new FlightReentryLatch();

        latch.Arm();
        Assert.False(latch.Read(InputAction.FireRockets, true));
        Assert.False(latch.Read(InputAction.FireRockets, false));
        Assert.True(latch.Read(InputAction.FireRockets, true));
    }

    [Fact]
    public void ASecondArmWhileTheSamePressIsDownChangesNothing()
    {
        var latch = new FlightReentryLatch();

        latch.Arm();                                          // the skip hands flight back
        Assert.False(latch.Read(InputAction.FireGuns, true));
        latch.Arm();                                          // and the resume edge arms again
        Assert.False(latch.Read(InputAction.FireGuns, true));
        Assert.False(latch.Read(InputAction.FireGuns, false));
        Assert.True(latch.Read(InputAction.FireGuns, true));
    }

    [Fact]
    public void OneArmCoversEveryLatchedAction()
    {
        var latch = new FlightReentryLatch();

        latch.Arm();

        foreach (var action in FlightReentryLatch.Latched)
        {
            Assert.False(latch.Read(action, true));
            Assert.False(latch.Read(action, false));
            Assert.True(latch.Read(action, true));
        }
    }

    [Fact]
    public void ReleasingOneActionLeavesTheOthersLatched()
    {
        var latch = new FlightReentryLatch();

        latch.Arm();
        Assert.False(latch.Read(InputAction.FireGuns, false));  // Space was never down
        Assert.True(latch.Read(InputAction.FireGuns, true));    // so the gun trigger reads through
        Assert.False(latch.Read(InputAction.FireRockets, true)); // while A, still down, does not
    }

    [Fact]
    public void AnActionOutsideTheLatchedSetReadsThroughAnArm()
    {
        var latch = new FlightReentryLatch();

        latch.Arm();

        // A stick deflection is a posture, not a press, so nothing swallows it.
        Assert.DoesNotContain(InputAction.PitchUp, FlightReentryLatch.Latched);
        Assert.True(latch.Read(InputAction.PitchUp, true));
    }
}
