using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The head-look laws: the snap direction table, the 2 rad/s free-look integration, the elevation
/// clamp and azimuth wrap. The padlock's target bearing and its exit scan are here too, with the
/// exponential smoothing at the decoded rates (elevation 3.0/s, azimuth 5.0/s).
/// Everything in <see cref="HeadLook"/> is engine-free, so none of this needs a live camera.
/// </summary>
public class HeadLookTests
{
    private const float Tol = 1e-4f;

    // A live frame at 60 Hz, and long enough of them for the look stick's lag to arrive inside Tol
    // (its time constant is 40 ms, so one second leaves nothing of it).
    private const float StepDt = 1f / 60f;

    private const int HoldFrames = 60;

    // HeadLook.AutoheadTarget's shipped constants (extracted/zrdr/player.zrd.json via
    // PlaneStatsFlightGlobalsTests' own loader assertion), reused by the lead-law tests near the
    // bottom of this file.
    private const float ShippedTurnTime = 0.75f;
    private const float ShippedTurnMax = 0.0998f;
    private const float ShippedMinPitch = -0.0524f;

    private static HeadLookInput Idle => default;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(System.IO.Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    // The original's two mode keys, K and J, as the frame they are pressed on.
    private static HeadLookInput SnapKey =>
        new(0f, 0f, 0f, 0f, false, 0f, 0f, false, true);

    private static HeadLookInput SmoothKey =>
        new(0f, 0f, 0f, 0f, false, 0f, 0f, false, false, true);

    // Track Target, L, as the frame it is pressed on.
    private static HeadLookInput PadlockKey =>
        new(0f, 0f, 0f, 0f, false, 0f, 0f, false, false, false, true);

    // The eight direction slots as the original's own key-binding menu labels them
    // (OriginalScreenshots/Keybinds Views 2.png), each read as the composed direction its key
    // contributes: the head must land where its label says. Kp1/Kp3 are "Up/…/Rear", which is what
    // settles that ALL four diagonals lift the head, not only the forward pair.
    [Theory]
    [InlineData(-1f, 1f, Mathf.Pi / 4f, Mathf.Pi / 4f)]              // Kp7 Look Up/Left
    [InlineData(0f, 1f, Mathf.Pi / 2f, 0f)]                          // Kp8 Look Up
    [InlineData(1f, 1f, Mathf.Pi / 4f, -Mathf.Pi / 4f)]              // Kp9 Look Up/Right
    [InlineData(-1f, 0f, 0f, Mathf.Pi / 2f)]                         // Kp4 Look Left
    [InlineData(1f, 0f, 0f, -Mathf.Pi / 2f)]                         // Kp6 Look Right
    [InlineData(-1f, -1f, Mathf.Pi / 4f, 3f * Mathf.Pi / 4f)]        // Kp1 Look Up/Left/Rear
    [InlineData(0f, -1f, 0f, Mathf.Pi)]                              // Kp2 Look Back
    [InlineData(1f, -1f, Mathf.Pi / 4f, -3f * Mathf.Pi / 4f)]        // Kp3 Look Up/Right/Rear
    public void TheEightSlotsSnapWhereTheOriginalsOwnLabelsSay(float x, float y, float elevation, float azimuth)
    {
        var t = HeadLook.SnapTargets(x, y);
        Assert.NotNull(t);
        Assert.Equal(elevation, t!.Value.Elevation, Tol);
        Assert.Equal(0f, HeadLook.Wrap(t.Value.Azimuth - azimuth), Tol);
    }

    [Fact]
    public void NoSnapDirectionIsNoSnapAtAll()
    {
        Assert.Null(HeadLook.SnapTargets(0f, 0f));
    }

    // The pad aims absolutely: a deflection IS an angle, scaled by the envelope the chase camera
    // swings through. Half a stick is half the angle, which is exactly what the relative
    // free-look path below does NOT do.
    [Theory]
    [InlineData(1f, 0f, 0f, -HeadLook.PadLookYawMaxDeg)]
    [InlineData(-1f, 0f, 0f, HeadLook.PadLookYawMaxDeg)]
    [InlineData(0f, 1f, HeadLook.PadLookPitchMaxDeg, 0f)]
    [InlineData(0f, -1f, -HeadLook.PadLookPitchMaxDeg, 0f)]
    [InlineData(0.5f, 0.5f, HeadLook.PadLookPitchMaxDeg / 2f, -HeadLook.PadLookYawMaxDeg / 2f)]
    public void ThePadAimsAtTheStickPosition(float right, float up, float elevationDeg, float azimuthDeg)
    {
        var (elevation, azimuth) = HeadLook.PadAimTargets(right, up);
        Assert.Equal(Mathf.DegToRad(elevationDeg), elevation, Tol);
        Assert.Equal(Mathf.DegToRad(azimuthDeg), azimuth, Tol);
    }

    // An absolute stick with first person's decoded level floor would leave the bottom half of its
    // travel inert, so the pad path carries its own symmetric bound instead. The floor still binds
    // every other path, asserted below.
    [Fact]
    public void ThePadLooksBelowLevelWhereTheOtherPathsCannot()
    {
        var head = new HeadLook();          // first person: ElevationFloor 0, level
        HoldStick(head, 0f, -1f);           // held, so the stick's own lag has arrived
        Assert.Equal(Mathf.DegToRad(-HeadLook.PadLookPitchMaxDeg), head.TargetElevation, Tol);

        head = new HeadLook();
        head.Step(0.5f, Free(0f, -1f));     // the decoded relative path stops at the floor
        Assert.Equal(0f, head.TargetElevation, Tol);
    }

    // Precedence: the discrete commands stay on top of a standing absolute aim, and a centred
    // stick claims no frame at all, so it blocks neither the mouse nor the idle rule.
    [Fact]
    public void ADiscreteCommandBeatsTheStickAndACentredStickClaimsNothing()
    {
        var head = new HeadLook();
        head.Step(0.1f, new HeadLookInput(0f, 1f, 0f, 0f, false, 1f, 0f));   // Kp8 with the stick over
        Assert.Equal(HeadLook.MaxElevation, head.TargetElevation, Tol);
        Assert.Equal(0f, head.TargetAzimuth, Tol);

        head = new HeadLook();
        head.Step(0.5f, new HeadLookInput(0f, 0f, 0f, 1f, false, 0f, 0f));   // mouse only, pad centred
        Assert.Equal(1f, head.TargetElevation, Tol);
    }

    // Release is the idle frame, so the head returns to straight ahead on its own: the stick
    // position is the whole state, and nothing of it survives letting go.
    [Fact]
    public void ReleasingTheStickReturnsTheHeadToStraightAhead()
    {
        var head = new HeadLook();
        head.Step(0.1f, Pad(-1f, 0.5f));
        Assert.True(head.TargetAzimuth > 1f);
        head.Step(0.1f, Idle);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
        Assert.Equal(0f, head.TargetElevation, Tol);
    }

    // The filter's whole law: one time constant of a held deflection leaves the filtered pair at
    // 1 - 1/e of it, whatever step size the frames arrive in, and a few more put it on the stick.
    [Fact]
    public void TheStickFilterReachesOneTimeConstantOfAHeldDeflection()
    {
        var filter = new StickLookFilter();
        float tau = 1f / HeadLook.PadAimSmoothRate;
        const int steps = 20;
        for (int i = 0; i < steps; i++)
        {
            filter.Step(tau / steps, 1f, 0f);
        }

        Assert.True(filter.Active);
        Assert.Equal(1f - Mathf.Exp(-1f), filter.X, Tol);
        Assert.Equal(0f, filter.Y, Tol);

        for (int i = 0; i < 5 * steps; i++)
        {
            filter.Step(tau / steps, 1f, 0f);
        }

        Assert.Equal(1f, filter.X, 1e-2f);
    }

    // The centre band, the noise gate: a stick inside it is a stick at rest, so the filter reads
    // zero on both axes and the head's own idle rule owns the frame.
    [Theory]
    [InlineData(HeadLook.PadAimCentreBand * 0.5f, 0f)]
    [InlineData(0f, -HeadLook.PadAimCentreBand * 0.5f)]
    [InlineData(HeadLook.PadAimCentreBand * 0.7f, HeadLook.PadAimCentreBand * 0.7f)]
    public void AStickInsideTheCentreBandReadsAsNoStickAtAll(float x, float y)
    {
        var filter = new StickLookFilter();
        filter.Step(0.1f, x, y);
        Assert.False(filter.Active);
        Assert.Equal(0f, filter.X, Tol);
        Assert.Equal(0f, filter.Y, Tol);

        var head = new HeadLook();
        head.Step(0.1f, Pad(x, y));
        Assert.Equal(0f, head.TargetAzimuth, Tol);
        Assert.Equal(0f, head.TargetElevation, Tol);
    }

    // The filter is on the stick and nowhere else: the mouse pan still integrates at the decoded
    // 2 rad/s and the numpad still lands on the table's own angle, with a below-band stick over both.
    [Fact]
    public void TheFilterLeavesTheNumpadAndTheMousePathsAlone()
    {
        const float idle = HeadLook.PadAimCentreBand * 0.5f;
        var head = new HeadLook();
        head.Step(0.5f, new HeadLookInput(0f, 0f, 0f, 1f, false, idle, 0f));
        Assert.Equal(1f, head.TargetElevation, Tol);

        head = new HeadLook();
        head.Step(0.1f, new HeadLookInput(-1f, 0f, 0f, 0f, false, idle, 0f));   // Kp4
        Assert.Equal(Mathf.Pi / 2f, head.TargetAzimuth, Tol);
    }

    // Release is not slowed by the filter: on the frame the stick centres the targets are back at
    // straight ahead and the shown angle is already decaying at the decoded azimuth rate alone.
    [Fact]
    public void ReleasingTheStickIsAsFastAsItWasBeforeTheFilter()
    {
        var head = new HeadLook();
        HoldStick(head, -1f, 0f);
        float shown = head.Azimuth;
        Assert.True(shown > 1f);

        head.Step(StepDt, Idle);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
        Assert.Equal(
            HeadLook.Approach(shown, 0f, HeadLook.AzimuthSmoothRate, StepDt), head.Azimuth, Tol);
    }

    // The pad reaches the same angles the chase camera swings through, which is the whole
    // request: one envelope, read by both, so they cannot drift apart.
    [Fact]
    public void ThePadSharesTheChaseCamerasEnvelope()
    {
        Assert.Equal(150f, HeadLook.PadLookYawMaxDeg);
        Assert.Equal(60f, HeadLook.PadLookPitchMaxDeg);
        Assert.True(HeadLook.PadLookPitchMaxDeg < Mathf.RadToDeg(HeadLook.MaxElevation),
            "the shared pitch bound must stay inside the head's own ceiling");
    }

    [Fact]
    public void ReleasingASnapReturnsTheTargetsToStraightAhead()
    {
        var head = new HeadLook();
        head.Step(0.1f, Snap(-1f, 0f));
        Assert.True(head.TargetAzimuth > 1f);
        head.Step(0.1f, Idle);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
        Assert.Equal(0f, head.TargetElevation, Tol);
    }

    [Fact]
    public void FreeLookIntegratesAtTwoRadiansPerSecond()
    {
        var head = new HeadLook();
        head.Step(0.5f, Free(0f, 1f));      // straight up for half a second
        Assert.Equal(1f, head.TargetElevation, Tol);
        head = new HeadLook();
        head.Step(0.5f, Free(-1f, 0f));     // and to the left, the positive azimuth direction
        Assert.Equal(1f, head.TargetAzimuth, Tol);
    }

    [Fact]
    public void FreeLookReadsDirectionOnlySoAHalfDeflectedStickPansAsFast()
    {
        var soft = new HeadLook();
        var hard = new HeadLook();
        soft.Step(0.25f, Free(0f, 0.2f));
        hard.Step(0.25f, Free(0f, 1f));
        Assert.Equal(hard.TargetElevation, soft.TargetElevation, Tol);
    }

    [Fact]
    public void FreeLookNeverDrivesTheHeadBelowLevelOrPastStraightUp()
    {
        var head = new HeadLook();
        for (int i = 0; i < 60; i++)
        {
            head.Step(0.1f, Free(0f, 1f));
        }
        Assert.Equal(Mathf.Pi / 2f, head.TargetElevation, Tol);
        for (int i = 0; i < 60; i++)
        {
            head.Step(0.1f, Free(0f, -1f));
        }
        Assert.Equal(0f, head.TargetElevation, Tol);
    }

    // The chase camera's own floor, the literal the chase placement passes the original's
    // controller: the same head pans a further quarter turn down there than it can in the cockpit.
    [Fact]
    public void TheChaseFloorLetsTheSameHeadLookStraightDown()
    {
        Assert.Equal(0f, HeadLook.FirstPersonElevationFloor);
        Assert.Equal(-Mathf.Pi / 2f, HeadLook.ChaseElevationFloor, Tol);

        var head = new HeadLook(HeadLook.ChaseElevationFloor);
        for (int i = 0; i < 60; i++)
        {
            head.Step(0.1f, Free(0f, -1f));
        }
        Assert.Equal(HeadLook.ChaseElevationFloor, head.TargetElevation, Tol);
    }

    // The floor is per frame, not per head: the placing view sets it every step, which is how one
    // shared head can be floored at level in the cockpit and at straight down on the chase camera.
    [Fact]
    public void TheFloorFollowsTheViewThatPlacesTheFrame()
    {
        var head = new HeadLook(HeadLook.ChaseElevationFloor);
        head.Step(0.5f, Free(0f, -1f));
        Assert.True(head.TargetElevation < -0.9f);

        head.ElevationFloor = HeadLook.FirstPersonElevationFloor;
        head.Step(0.1f, Free(0f, -1f));
        Assert.Equal(0f, head.TargetElevation, Tol);
    }

    [Fact]
    public void AzimuthStopsHardAtDeadAstern()
    {
        // The original's head reaches directly behind and goes no further (confirmed at its
        // controls); 8 rad of commanded left pan therefore parks the target exactly at +pi.
        var head = new HeadLook();
        for (int i = 0; i < 40; i++)
        {
            head.Step(0.1f, Free(-1f, 0f));
        }
        Assert.Equal(Mathf.Pi, head.TargetAzimuth, 1e-3f);
        head.Step(0.1f, Free(1f, 0f));         // one step back off the stop moves it again
        Assert.True(head.TargetAzimuth < Mathf.Pi - 0.1f);
    }

    [Fact]
    public void TheCenterKeyZeroesBothTargetsAtOnceAndBeatsAHeldSnap()
    {
        var head = new HeadLook();
        head.Step(0.1f, Free(-1f, 1f));
        Assert.NotEqual(0f, head.TargetAzimuth);
        head.Step(0.1f, new HeadLookInput(-1f, 0f, 0f, 0f, true));
        Assert.Equal(0f, head.TargetAzimuth, Tol);
        Assert.Equal(0f, head.TargetElevation, Tol);
    }

    [Fact]
    public void TheShownAnglesChaseTheTargetsAtTheDecodedRates()
    {
        const float dt = 0.1f;
        var head = new HeadLook();
        head.Step(dt, Snap(0f, 1f));           // target elevation π/2, azimuth 0
        float expected = HeadLook.Approach(0f, Mathf.Pi / 2f, HeadLook.ElevationSmoothRate, dt);
        Assert.Equal(expected, head.Elevation, Tol);
        Assert.Equal(3f, HeadLook.ElevationSmoothRate);
        Assert.Equal(5f, HeadLook.AzimuthSmoothRate);
    }

    [Fact]
    public void SmoothingIsExponentialAndFrameRateIndependent()
    {
        // One 0.2 s step and two 0.1 s steps land on the same angle: e^(−r·2dt) = (e^(−r·dt))².
        var oneStep = new HeadLook();
        var twoSteps = new HeadLook();
        oneStep.Step(0.2f, Snap(0f, 1f));
        twoSteps.Step(0.1f, Snap(0f, 1f));
        twoSteps.Step(0.1f, Snap(0f, 1f));
        Assert.Equal(oneStep.Elevation, twoSteps.Elevation, Tol);
    }

    [Fact]
    public void AzimuthSwingsBackThroughTheFrontNeverAcrossTheStop()
    {
        // A hard-stopped head cannot cross dead astern: retargeting from aft-left (+3π/4) to
        // aft-right (−3π/4) swings the long way round through the front, so the shown azimuth
        // moves TOWARD zero first and stays inside ±π throughout.
        var head = new HeadLook();
        for (int i = 0; i < 200; i++)
        {
            head.Step(0.05f, Snap(-1f, -1f));
        }
        float before = head.Azimuth;
        Assert.Equal(3f * Mathf.Pi / 4f, before, 1e-3f);
        head.Step(0.05f, Snap(1f, -1f));
        Assert.True(head.Azimuth < before);
        Assert.InRange(head.Azimuth, -Mathf.Pi, Mathf.Pi);
    }

    // The two modes differ exactly here, which is why the original carries the state byte. In
    // free-look a released pan holds the head where it was pointed. The snap key puts the head
    // back on the mode whose released direction returns it to straight ahead.
    [Fact]
    public void AReleasedPanHoldsTheHeadInFreeLookAndTheSnapKeyBringsItBack()
    {
        var head = new HeadLook();
        head.Step(0.1f, SmoothKey);
        for (int i = 0; i < 10; i++)
        {
            head.Step(0.1f, Free(-1f, 1f));
        }
        Assert.Equal(LookMode.FreeLook, head.Mode);
        float elevation = head.TargetElevation, azimuth = head.TargetAzimuth;
        Assert.NotEqual(0f, azimuth);

        head.Step(0.1f, Idle);
        Assert.Equal(elevation, head.TargetElevation, Tol);
        Assert.Equal(azimuth, head.TargetAzimuth, Tol);

        head.Step(0.1f, SnapKey);
        Assert.Equal(LookMode.Snap, head.Mode);
        Assert.Equal(0f, head.TargetElevation, Tol);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
    }

    // The mode keys are the original's Access Snap Look Mode and Access Smooth Look Mode. Each
    // states which behaviour is live. The smooth one centres the head on the way in, targets and
    // shown angles together, as its own handler does.
    [Fact]
    public void TheModeKeysStateTheModeAndTheSmoothOneCentresTheHead()
    {
        var head = new HeadLook();
        Assert.Equal(LookMode.Snap, head.Mode);

        head.Step(0.5f, Free(-1f, 1f));
        Assert.NotEqual(0f, head.Azimuth);
        head.Step(0.1f, SmoothKey);
        Assert.Equal(LookMode.FreeLook, head.Mode);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
        Assert.Equal(0f, head.Azimuth, Tol);
        Assert.Equal(0f, head.Elevation, Tol);

        head.Step(0.1f, SnapKey);
        Assert.Equal(LookMode.Snap, head.Mode);
    }

    // No device writes the mode: the keys choose it and both devices obey it. A mouse pan after K
    // leaves the head in snap, and a numpad direction after J leaves it in free-look.
    [Fact]
    public void NeitherDeviceTakesTheModeOffTheKeys()
    {
        var head = new HeadLook();
        head.Step(0.1f, SnapKey);
        head.Step(0.5f, Free(0f, 1f));
        Assert.Equal(LookMode.Snap, head.Mode);
        Assert.Equal(HeadLook.FreeLookRate * 0.5f, head.TargetElevation, Tol);

        head = new HeadLook();
        head.Step(0.1f, SmoothKey);
        head.Step(0.1f, Snap(-1f, 0f));
        Assert.Equal(LookMode.FreeLook, head.Mode);
        Assert.Equal(HeadLook.FreeLookRate * 0.1f, head.TargetAzimuth, Tol);
    }

    // J: a held numpad direction pans at the decoded 2 rad/s along its own direction. The released
    // key leaves the head where it was pointed, the original's state 1 read of the slots.
    [Fact]
    public void InSmoothModeAHeldNumpadKeyPansAtTwoRadiansPerSecondAndTheHeadStaysOnRelease()
    {
        var head = new HeadLook();
        head.Step(0.1f, SmoothKey);
        for (int i = 0; i < 5; i++)
        {
            head.Step(0.1f, Snap(-1f, 0f));                  // Kp4 Look Left for half a second
        }
        Assert.Equal(LookMode.FreeLook, head.Mode);
        Assert.Equal(1f, head.TargetAzimuth, Tol);
        Assert.Equal(0f, head.TargetElevation, Tol);

        for (int i = 0; i < 30; i++)
        {
            head.Step(0.1f, Idle);
        }
        Assert.Equal(1f, head.TargetAzimuth, Tol);
        Assert.Equal(1f, head.Azimuth, 1e-3f);

        head.Step(0.25f, Snap(1f, 1f));                      // Kp9: up and right, normalised
        float leg = HeadLook.FreeLookRate * 0.25f / Mathf.Sqrt(2f);
        Assert.Equal(1f - leg, head.TargetAzimuth, Tol);
        Assert.Equal(leg, head.TargetElevation, Tol);

        // ABLE-TO-FAIL CONTROL: Kp8 in snap mode goes straight up through the table, so the
        // integrated values above are the mode's doing and not the direction's.
        var snap = new HeadLook();
        snap.Step(0.25f, Snap(0f, 1f));
        Assert.Equal(HeadLook.MaxElevation, snap.TargetElevation, Tol);
    }

    // K: the same key snaps through the table and the released key returns the head.
    [Fact]
    public void InSnapModeANumpadKeySnapsAndTheHeadReturnsOnRelease()
    {
        var head = new HeadLook();
        head.Step(0.1f, SmoothKey);
        head.Step(0.1f, SnapKey);
        head.Step(0.1f, Snap(-1f, 0f));
        Assert.Equal(LookMode.Snap, head.Mode);
        Assert.Equal(Mathf.Pi / 2f, head.TargetAzimuth, Tol);
        head.Step(0.1f, Idle);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
    }

    // The mouse follows the same two rules. In K it pans while its control is held, and the head
    // springs back when the control is released. In J it parks where it pointed.
    [Fact]
    public void TheMouseSpringsBackInSnapModeAndParksInSmoothMode()
    {
        var snap = new HeadLook();
        for (int i = 0; i < 5; i++)
        {
            snap.Step(0.1f, Looking(-1f, 0f));
        }
        Assert.Equal(1f, snap.TargetAzimuth, Tol);
        snap.Step(0.1f, Looking());                          // a still mouse under the held control
        Assert.Equal(1f, snap.TargetAzimuth, Tol);
        snap.Step(0.1f, Idle);                               // the control released
        Assert.Equal(LookMode.Snap, snap.Mode);
        Assert.Equal(0f, snap.TargetAzimuth, Tol);

        var smooth = new HeadLook();
        smooth.Step(0.1f, SmoothKey);
        for (int i = 0; i < 5; i++)
        {
            smooth.Step(0.1f, Looking(-1f, 0f));
        }
        smooth.Step(0.1f, Idle);
        Assert.Equal(LookMode.FreeLook, smooth.Mode);
        Assert.Equal(1f, smooth.TargetAzimuth, Tol);
    }

    // Look-back forces the snap state, so it reaches dead astern and returns in either mode.
    [Fact]
    public void AForcedSnapReachesDeadAsternEvenInSmoothMode()
    {
        var head = new HeadLook();
        head.Step(0.1f, SmoothKey);
        head.Step(0.1f, new HeadLookInput(0f, -1f, 0f, 0f, false, ForceSnap: true));
        Assert.Equal(LookMode.Snap, head.Mode);
        Assert.Equal(Mathf.Pi, Mathf.Abs(head.TargetAzimuth), Tol);  // dead astern is either stop
        head.Step(0.1f, Idle);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
    }

    // One source owns the frame, so a numpad direction and a mouse pan arriving together do not
    // both move the head. The numpad decides in either mode, and the pan is not added on top.
    [Fact]
    public void OneSourceOwnsTheFrameWhenBothDevicesMoveAtOnce()
    {
        var both = new HeadLook();
        var snapOnly = new HeadLook();
        var input = new HeadLookInput(-1f, 0f, 1f, 1f, false);
        both.Step(0.5f, input);
        snapOnly.Step(0.5f, Snap(-1f, 0f));
        Assert.Equal(snapOnly.TargetElevation, both.TargetElevation, Tol);
        Assert.Equal(snapOnly.TargetAzimuth, both.TargetAzimuth, Tol);

        var smoothBoth = new HeadLook();
        smoothBoth.Step(0.1f, SmoothKey);
        smoothBoth.Step(0.5f, input);
        Assert.Equal(1f, smoothBoth.TargetAzimuth, Tol);
        Assert.Equal(0f, smoothBoth.TargetElevation, Tol);

        // ABLE-TO-FAIL CONTROL: the same pan without the direction moves the head, so the
        // assertions above are about precedence and not about an inert input.
        var panOnly = new HeadLook();
        panOnly.Step(0.5f, Free(1f, 1f));
        Assert.NotEqual(0f, panOnly.TargetElevation);
    }

    // Autohead is gated on the snap state in the original, so the idle hook is not consulted while
    // free-look owns the head. That mode's idle frame holds the pose instead.
    [Fact]
    public void TheIdleHookIsSilentInFreeLookMode()
    {
        int calls = 0;
        var head = new HeadLook { IdleAim = () => { calls++; return (0.5f, 0.5f); } };
        head.Step(0.1f, SmoothKey);
        head.Step(0.1f, Idle);
        Assert.Equal(0, calls);

        // Back in snap, both frames consult it: the key's own frame carries no look input either.
        head.Step(0.1f, SnapKey);
        head.Step(0.1f, Idle);
        Assert.Equal(2, calls);
    }

    // The held control is its own claim on the free-look arm. A mouse that has stopped moving is
    // not a released button. The head stays where the mouse put it, and the shown angles settle
    // onto that pose rather than chasing the centre.
    [Fact]
    public void AHeldControlOverAStillMouseHoldsTheLook()
    {
        var head = new HeadLook();
        for (int i = 0; i < 10; i++)
        {
            head.Step(0.1f, Looking(-1f, 1f));
        }
        float elevation = head.TargetElevation, azimuth = head.TargetAzimuth;
        Assert.NotEqual(0f, azimuth);
        for (int i = 0; i < 30; i++)
        {
            head.Step(0.1f, Looking());
        }
        Assert.Equal(elevation, head.TargetElevation, Tol);
        Assert.Equal(azimuth, head.TargetAzimuth, Tol);
        Assert.Equal(azimuth, head.Azimuth, 1e-3f);
        Assert.Equal(elevation, head.Elevation, 1e-3f);
    }

    // In free-look, releasing the control leaves the head where the mouse put it. The centre key
    // brings it back: the original's state 1 zeroes the angles on that key alone.
    [Fact]
    public void ReleasingTheHeldControlHoldsTheLookAndTheCentreKeyBringsItBack()
    {
        var head = new HeadLook();
        head.Step(0.1f, SmoothKey);
        for (int i = 0; i < 10; i++)
        {
            head.Step(0.1f, Looking(-1f, 1f));
        }
        float azimuth = head.TargetAzimuth;
        Assert.NotEqual(0f, azimuth);

        head.Step(0.1f, Idle);
        Assert.Equal(LookMode.FreeLook, head.Mode);
        Assert.Equal(azimuth, head.TargetAzimuth, Tol);

        head.Step(0.1f, new HeadLookInput(0f, 0f, 0f, 0f, true));
        Assert.Equal(0f, head.TargetElevation, Tol);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
        Assert.Equal(LookMode.FreeLook, head.Mode);
    }

    // The head still takes the delta while the control is held. The flag decides who owns the
    // frame, not how far the pan goes, so a held pan reads as the motion-only path does.
    [Fact]
    public void AHeldControlStillPansAtTheDecodedRate()
    {
        var held = new HeadLook();
        var motionOnly = new HeadLook();
        held.Step(0.5f, Looking(0f, 1f));
        motionOnly.Step(0.5f, Free(0f, 1f));
        Assert.Equal(HeadLook.FreeLookRate * 0.5f, held.TargetElevation, Tol);
        Assert.Equal(motionOnly.TargetElevation, held.TargetElevation, Tol);
    }

    // The arms above free-look keep their place. The centre key and the pad's absolute aim both
    // claim the frame off a held control. Neither is stranded while the button is down.
    [Fact]
    public void TheCentreKeyAndThePadStillBeatAHeldControl()
    {
        var centred = new HeadLook();
        centred.Step(0.5f, Looking(-1f, 1f));
        centred.Step(0.1f, new HeadLookInput(0f, 0f, 0f, 0f, true, 0f, 0f, true));
        Assert.Equal(0f, centred.TargetAzimuth, Tol);

        var aimed = new HeadLook();
        for (int i = 0; i < HoldFrames; i++)    // held, so the stick's own lag has arrived
        {
            aimed.Step(StepDt, new HeadLookInput(0f, 0f, 0f, 0f, false, 1f, 0f, true));
        }

        Assert.Equal(-Mathf.DegToRad(HeadLook.PadLookYawMaxDeg), aimed.TargetAzimuth, Tol);
    }

    [Fact]
    public void TheIdleHookOwnsAFrameWithNoLookInputAndBypassesTheInputFloor()
    {
        var head = new HeadLook { IdleAim = () => (-0.0524f, 0.2f) };   // C22's −3° autohead floor
        head.Step(0.1f, Idle);
        Assert.Equal(-0.0524f, head.TargetElevation, Tol);
        Assert.Equal(0.2f, head.TargetAzimuth, Tol);
    }

    [Fact]
    public void TheIdleHookIsSilentWhileAnyLookInputIsHeld()
    {
        int calls = 0;
        var head = new HeadLook
        {
            IdleAim = () =>
            {
                calls++;
                return (0.5f, 0.5f);
            },
        };
        head.Step(0.1f, Free(1f, 0f));
        head.Step(0.1f, Snap(0f, 1f));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ResetPutsTheHeadStraightAheadWithNothingLeftToSmooth()
    {
        var head = new HeadLook();
        head.Step(0.5f, Free(-1f, 1f));
        head.Reset();
        Assert.Equal(0f, head.Elevation, Tol);
        Assert.Equal(0f, head.Azimuth, Tol);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
    }

    [Fact]
    public void TheComposedBasisYawsFirstThenPitchesAboutTheHeadsOwnRightAxis()
    {
        // Looking 90° left and 45° up: the view direction must be up-and-left, and the head's own
        // right axis must have swung round with the yaw rather than staying the plane's.
        var (_, basis) = CameraController.FirstPersonPose(Vector3.Zero, Basis.Identity, Vector3.Zero,
            Mathf.Pi / 4f, Mathf.Pi / 2f);
        var forward = -basis.Z;
        Assert.True(forward.X < 0f);           // left
        Assert.True(forward.Y > 0.5f);         // and up
        Assert.True(Mathf.Abs(basis.X.Z) > 0.9f); // the right axis now runs along the plane's Z
    }

    [Fact]
    public void ZeroHeadAnglesLeaveTheA2PlacementExactlyAsItWas()
    {
        var attitude = new Basis(Vector3.Up, 0.4f);
        var (position, basis) = CameraController.FirstPersonPose(Vector3.One, attitude, Vector3.Up);
        // With no head angles, use the fixed −4.70° tilt about the plane's right axis.
        var expected = attitude * new Basis(Vector3.Right, -0.08203f);
        Assert.True(position.IsEqualApprox(Vector3.One + (attitude * Vector3.Up)));
        Assert.True(basis.Z.IsEqualApprox(expected.Z));
        Assert.True(basis.X.IsEqualApprox(expected.X));
    }

    // HeadLook.AutoheadTarget, the lead law fed to IdleAim, exercised as a pure law on body rates,
    // no PlaneStats in the loop. Rates are the plant's half-angle ones: +X nose up, +Y nose left.

    [Fact]
    public void AutoheadCentresTheHeadWhenThePlaneIsNotTurning()
    {
        var still = Lead(0f, 0f, 0f);
        Assert.Equal(0f, still.Elevation, Tol);
        Assert.Equal(0f, still.Azimuth, Tol);
    }

    [Fact]
    public void AutoheadIgnoresAPureRollBecauseTheNoseDoesNotMove()
    {
        var rolling = Lead(0f, 0f, 1.5f);
        Assert.Equal(0f, rolling.Elevation, Tol);
        Assert.Equal(0f, rolling.Azimuth, Tol);
    }

    [Fact]
    public void AutoheadAimsWhereTheNoseWillBeAfterTurnTimeBelowTheCap()
    {
        // 0.04 half-angle rad/s of yaw: the lead's half-angle is 0.03, under the 0.0998 cap. The
        // head turns by the full 2 × 0.03, the angle the plant's own step turns the nose by.
        var t = Lead(0f, 0.04f, 0f);
        Assert.Equal(2f * 0.04f * ShippedTurnTime, t.Azimuth, Tol);
        Assert.Equal(0f, t.Elevation, Tol);
    }

    [Fact]
    public void AutoheadCapsTheLeadAtTwiceTurnMax()
    {
        // A hard pull well past the cap: the half-angle stops at turn_max. The head rises by
        // twice it (the quaternion's doubling), not by the uncapped 2 × 1.5.
        var t = Lead(2f, 0f, 0f);
        Assert.Equal(2f * ShippedTurnMax, t.Elevation, Tol);
        Assert.Equal(0f, t.Azimuth, Tol);
    }

    [Fact]
    public void AutoheadFloorsAPushOverAtTheDecodedMinusThreeDegrees()
    {
        // Nose falling hard: the lead points well below −3°, and the floor, below the input
        // paths' own level floor, decides the elevation.
        Assert.Equal(ShippedMinPitch, Lead(-2f, 0f, 0f).Elevation, Tol);
    }

    [Theory]
    [InlineData(-0.2f, -1f)]   // nose yawing right: the head looks right (azimuth is +left)
    [InlineData(0.2f, 1f)]     // and left
    public void AutoheadLooksTheWayTheNoseIsTurning(float yawRate, float sign)
    {
        var t = Lead(0.1f, yawRate, 0f);
        Assert.True(Mathf.Sign(t.Azimuth) == sign, $"azimuth {t.Azimuth} for yaw rate {yawRate}");
        Assert.True(t.Elevation > 0f, $"elevation {t.Elevation} under a pull");
    }

    /// <summary>The shipped Black Hawk flown by the real plant in a sustained 60° banked pull each
    /// way. The head leads into the turn, on the side the nose is heading. It settles back to
    /// centre once the wings are rolled level and the stick released. The lead's direction is also
    /// checked against where the nose really is <c>turn_time</c> later.</summary>
    [ExtractedDataTheory]
    [InlineData(1f)]    // right bank
    [InlineData(-1f)]   // left bank
    public void AutoheadLeadsIntoASustainedBankedTurnAndCentresAsTheWingsLevel(float right)
    {
        const float dt = 1f / 60f;
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        var model = new FlightModel(stats);
        var banked = Basis.Identity.Rotated(Vector3.Back, -right * Mathf.DegToRad(60f));
        model.Reset(new Vector3(0f, 1500f, 0f), banked, stats.FdSpeed, 1f);
        var pull = new FlightInput { Pitch = 0.6f, Throttle = 1f };
        for (int i = 0; i < 180; i++)
        {
            model.Step(pull, dt);
        }

        var (elevation, azimuth) = HeadLook.AutoheadTarget(model.BodyRates, stats.AutoheadTurnTime,
            stats.AutoheadTurnMax, stats.AutoheadTurnMinPitch);
        Assert.True(Mathf.Sign(azimuth) == -right,
            $"bank {right}: the head must look into the turn, azimuth {azimuth} (+left), rates {model.BodyRates}");
        Assert.True(elevation > 0f, $"bank {right}: the pull lifts the head, elevation {elevation}");

        // Where the nose really goes in turn_time, in the frame the head is measured in.
        var frame = model.Attitude;
        for (float t = 0f; t < stats.AutoheadTurnTime; t += dt)
        {
            model.Step(pull, dt);
        }

        var (noseElevation, noseAzimuth) = HeadLook.PadlockTargets(frame.Inverse() * -model.Attitude.Z);
        Assert.True(Mathf.Sign(noseAzimuth) == -right && noseElevation > 0f,
            $"bank {right}: the nose itself went ({noseElevation}, {noseAzimuth}), the side the head led to");
        Assert.True(Mathf.Abs(azimuth) <= Mathf.Abs(noseAzimuth) + 1e-3f,
            $"bank {right}: the capped lead ({azimuth}) does not overshoot the nose's own turn ({noseAzimuth})");

        // Roll the wings level against the bank, then release the stick.
        for (int i = 0; i < 600 && Mathf.Abs(model.Attitude.X.Y) > 0.02f; i++)
        {
            model.Step(new FlightInput { Roll = model.Attitude.X.Y < 0f ? 1f : -1f, Throttle = 1f }, dt);
        }

        Assert.True(Mathf.Abs(model.Attitude.X.Y) <= 0.02f, $"bank {right}: the wings never came level");
        for (int i = 0; i < 180; i++)
        {
            model.Step(new FlightInput { Throttle = 1f }, dt);
        }

        // The plant keeps a small residual pitch and yaw rate after the roll-out. "Centre" is
        // therefore a small fraction of the turn's own lead, not zero.
        var level = HeadLook.AutoheadTarget(model.BodyRates, stats.AutoheadTurnTime,
            stats.AutoheadTurnMax, stats.AutoheadTurnMinPitch);
        float turning = new Vector2(elevation, azimuth).Length();
        float settled = new Vector2(level.Elevation, level.Azimuth).Length();
        Assert.True(settled < 0.25f * turning,
            $"bank {right}: wings level, the head must be back near centre, lead {settled} against the turn's {turning}, rates {model.BodyRates}");
    }

    // Track Target (the original's state 2): the head holds the selected target's own bearing
    // every frame, with no rate of its own. The shown angles do all the smoothing there is.

    [Theory]
    [InlineData(0f, 0f, -100f, 0f, 0f)]                          // dead ahead
    [InlineData(-100f, 0f, 0f, 0f, Mathf.Pi / 2f)]               // abeam to port, azimuth is +left
    [InlineData(100f, 0f, 0f, 0f, -Mathf.Pi / 2f)]               // abeam to starboard
    [InlineData(0f, 0f, 100f, 0f, Mathf.Pi)]                     // dead astern
    [InlineData(0f, 100f, -100f, Mathf.Pi / 4f, 0f)]             // 45° up, dead ahead
    [InlineData(0f, -100f, -100f, -Mathf.Pi / 4f, 0f)]           // 45° down, dead ahead
    public void ThePadlockBearingIsTheTargetsOwnAngleInThePlanesFrame(
        float x, float y, float z, float elevation, float azimuth)
    {
        var (e, a) = HeadLook.PadlockTargets(new Vector3(x, y, z));
        Assert.Equal(elevation, e, Tol);
        Assert.Equal(0f, HeadLook.Wrap(a - azimuth), Tol);
    }

    [Fact]
    public void TrackTargetTogglesIntoPadlockAndBackToSnapNeverToFreeLook()
    {
        var head = new HeadLook();
        head.Step(0.1f, SmoothKey);
        Assert.Equal(LookMode.FreeLook, head.Mode);

        head.Step(0.1f, PadlockKey);
        Assert.Equal(LookMode.Padlock, head.Mode);
        head.Step(0.1f, Idle);                          // held, not a second press edge
        Assert.Equal(LookMode.Padlock, head.Mode);

        head.Step(0.1f, PadlockKey);
        Assert.Equal(LookMode.Snap, head.Mode);
    }

    [Fact]
    public void APadlockedHeadTurnsOntoATargetAbeamAtTheDecodedAzimuthRate()
    {
        const float dt = 0.1f;
        // The toggle's own frame is already a padlock frame, as the original's is: its command
        // handler writes the state before the controller reads it.
        var head = new HeadLook { TargetOffset = () => new Vector3(-100f, 0f, 0f) };
        head.Step(dt, PadlockKey);
        Assert.Equal(Mathf.Pi / 2f, head.TargetAzimuth, Tol);
        Assert.Equal(HeadLook.Approach(0f, Mathf.Pi / 2f, HeadLook.AzimuthSmoothRate, dt),
            head.Azimuth, Tol);

        for (int i = 0; i < 100; i++)
        {
            head.Step(dt, Idle);
        }

        Assert.Equal(Mathf.Pi / 2f, head.Azimuth, 1e-3f);
    }

    // The only bound the padlock law carries is the placing view's own floor. A target below a
    // cockpit head holds at level, while the chase camera's head follows it down.
    [Fact]
    public void TheViewsFloorIsTheOnlyClampAPadlockedHeadTakes()
    {
        var cockpit = Padlocked(new Vector3(0f, -100f, -100f));
        cockpit.Step(0.1f, Idle);
        Assert.Equal(HeadLook.FirstPersonElevationFloor, cockpit.TargetElevation, Tol);

        var chase = Padlocked(new Vector3(0f, -100f, -100f), HeadLook.ChaseElevationFloor);
        chase.Step(0.1f, Idle);
        Assert.Equal(-Mathf.Pi / 4f, chase.TargetElevation, Tol);
    }

    // A target crossing dead astern flips the bearing's sign. The head crosses the ±π seam by the
    // short arc rather than unwinding through the nose. That is the wrap the original applies
    // before every chase, which only this state reaches.
    [Fact]
    public void ATargetPassingBehindIsFollowedAcrossTheTailNotThroughTheFront()
    {
        var offset = new Vector3(-10f, 0f, 100f);                // aft and a little to port
        var head = new HeadLook { TargetOffset = () => offset };
        head.Step(0.1f, PadlockKey);
        for (int i = 0; i < 200; i++)
        {
            head.Step(0.05f, Idle);
        }

        float before = head.Azimuth;
        Assert.True(before > 3f, $"settled aft-left, read {before}");

        offset = new Vector3(10f, 0f, 100f);                     // now aft and a little to starboard
        head.Step(0.05f, Idle);
        Assert.True(head.Azimuth > before,
            $"the first step goes on toward the tail, not back through the nose (read {head.Azimuth} against {before})");
        for (int i = 0; i < 200; i++)
        {
            head.Step(0.05f, Idle);
        }

        Assert.True(head.Azimuth < -3f, $"and settles aft-right, read {head.Azimuth}");
        Assert.InRange(head.Azimuth, -Mathf.Pi, Mathf.Pi);
    }

    // No target is the state's own idle frame: the angles zero, and the autohead hook owns it, the
    // second arm of the original's own gate.
    [Fact]
    public void APadlockWithNothingSelectedCentresTheHeadAndReachesTheIdleHook()
    {
        var bare = new HeadLook();
        bare.Step(0.1f, PadlockKey);
        bare.Step(0.5f, Free(-1f, 1f));
        // The exit writes SNAP whichever slot caused it, so a pan out of padlock lands there and
        // only J reaches the free-look state.
        Assert.Equal(LookMode.Snap, bare.Mode);

        bare = new HeadLook();
        bare.Step(0.1f, PadlockKey);
        bare.Step(0.1f, Idle);
        Assert.Equal(0f, bare.TargetAzimuth, Tol);
        Assert.Equal(0f, bare.TargetElevation, Tol);

        int calls = 0;
        var leaning = new HeadLook { IdleAim = () => { calls++; return (-0.0524f, 0.2f); } };
        leaning.Step(0.1f, PadlockKey);
        leaning.Step(0.1f, Idle);
        Assert.Equal(2, calls);                                  // the toggle's own frame is one
        Assert.Equal(0.2f, leaning.TargetAzimuth, Tol);
        Assert.Equal(-0.0524f, leaning.TargetElevation, Tol);
    }

    // The exit scan is the eight direction slots and nothing else. The centre key and the pad's
    // absolute aim are polled and discarded while padlocked, as the original's own arm does.
    [Fact]
    public void ASnapDirectionLeavesPadlockWhileTheCentreKeyAndThePadDoNot()
    {
        var head = Padlocked(new Vector3(-100f, 0f, 0f));
        head.Step(0.1f, new HeadLookInput(0f, 0f, 0f, 0f, true));
        Assert.Equal(LookMode.Padlock, head.Mode);
        Assert.Equal(Mathf.Pi / 2f, head.TargetAzimuth, Tol);

        head.Step(0.1f, Pad(1f, 0f));
        Assert.Equal(LookMode.Padlock, head.Mode);
        Assert.Equal(Mathf.Pi / 2f, head.TargetAzimuth, Tol);

        // The exit frame still aims at the target; the snap state owns the frame after it.
        head.Step(0.1f, Snap(0f, 1f));
        Assert.Equal(LookMode.Snap, head.Mode);
        Assert.Equal(Mathf.Pi / 2f, head.TargetAzimuth, Tol);
        head.Step(0.1f, Snap(0f, 1f));
        Assert.Equal(HeadLook.MaxElevation, head.TargetElevation, Tol);
        Assert.Equal(0f, head.TargetAzimuth, Tol);
    }

    // A head already in padlock, fed a target: the helper every case above starts from.
    private static HeadLook Padlocked(Vector3 offset,
        float floor = HeadLook.FirstPersonElevationFloor)
    {
        var head = new HeadLook(floor) { TargetOffset = () => offset };
        head.Step(0.1f, PadlockKey);
        return head;
    }

    private static HeadLookInput Snap(float x, float y) => new(x, y, 0f, 0f, false);

    private static HeadLookInput Free(float right, float up) => new(0f, 0f, right, up, false);

    private static HeadLookInput Pad(float right, float up) => new(0f, 0f, 0f, 0f, false, right, up);

    // A stick held long enough for its filter to arrive, at a live frame rate, so what is read
    // afterwards is the stick's position and not the lag on the way to it.
    private static void HoldStick(HeadLook head, float right, float up)
    {
        for (int i = 0; i < HoldFrames; i++)
        {
            head.Step(StepDt, Pad(right, up));
        }
    }

    // The free-look control held, carrying whatever the mouse moved this frame. Zero stands for a
    // still mouse, which the held flag tells apart from a released button.
    private static HeadLookInput Looking(float right = 0f, float up = 0f) =>
        new(0f, 0f, right, up, false, 0f, 0f, true);

    // The autohead lead for half-angle body rates, at the shipped constants.
    private static (float Elevation, float Azimuth) Lead(float x, float y, float z) =>
        HeadLook.AutoheadTarget(new Vector3(x, y, z), ShippedTurnTime, ShippedTurnMax, ShippedMinPitch);
}
