using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>The mouse a flight seat takes from the desktop. The guard decides whether it may take
/// one at all. A virtual cursor stands in for the OS pointer while it holds it. A relative stream
/// of mouse counts accumulates to a pane position at a fixed count per half pane. The captured
/// stick's wider centre band sits ahead of the decoded gate.</summary>
public class MouseCaptureTests
{
    /// <summary>The guard: a session takes the mouse only on a real display with somebody at the
    /// controls. The hidden test desktop's suites and every <c>--det</c> run keep the mouse mode
    /// the harness started them with.</summary>
    [Fact]
    public void Allowed_IsOffUnderDetAndOnEveryScriptedRun()
    {
        Assert.True(MouseCapture.Allowed(realDisplay: true, det: false, scripted: false));
        Assert.False(MouseCapture.Allowed(realDisplay: true, det: true, scripted: false));
        Assert.False(MouseCapture.Allowed(realDisplay: true, det: false, scripted: true));
        Assert.False(MouseCapture.Allowed(realDisplay: false, det: false, scripted: false));
        // The test desktop is a REAL display, so the scripted arm alone is what spares it.
        Assert.False(MouseCapture.Allowed(realDisplay: true, det: true, scripted: true));
    }

    /// <summary>A relative stream of mouse counts lands the virtual cursor at the pane position
    /// those counts stand for. On an 800x600 pane one count is 0.2 px across and 0.15 px down.</summary>
    [Fact]
    public void StepCursor_AccumulatesARelativeStreamToTheAbsoluteCursor()
    {
        var pane = new Vector2(800f, 600f);
        var capture = new MouseCapture();
        capture.Take(new Vector2(400f, 300f));

        capture.Moved(new Vector2(200f, -400f));
        capture.Moved(new Vector2(300f, -200f));
        capture.Moved(new Vector2(100f, -400f));
        var cursor = capture.StepCursor(pane);

        Assert.Equal(new Vector2(520f, 150f), cursor);
        var half = pane * 0.5f;
        Assert.Equal(new Vector2(0.3f, -0.5f), MouseFlight.Offset(cursor, half, half));
    }

    /// <summary>Each frame folds only that frame's travel, so the cursor walks the same path a
    /// pointer would rather than re-applying what it has already taken.</summary>
    [Fact]
    public void StepCursor_ConsumesThisFramesTravelOnly()
    {
        var pane = new Vector2(800f, 600f);
        var capture = new MouseCapture();
        capture.Take(new Vector2(100f, 100f));

        capture.Moved(new Vector2(50f, 100f));
        Assert.Equal(new Vector2(110f, 115f), capture.StepCursor(pane));
        Assert.Equal(new Vector2(110f, 115f), capture.StepCursor(pane));
        capture.Moved(new Vector2(25f, 0f));
        Assert.Equal(new Vector2(115f, 115f), capture.StepCursor(pane));
    }

    /// <summary>The travel-to-deflection ratio. The same count of mouse travel reaches the pane's
    /// edge, on a small window and a 4K one alike. Half of it reaches the halfway mark. The hand
    /// movement for full deflection neither grows nor shrinks with the display.</summary>
    [Theory]
    [InlineData(800f, 600f)]
    [InlineData(1920f, 1080f)]
    [InlineData(3840f, 2160f)]
    public void StepCursor_FullDeflectionIsTheSameCountOnEveryPane(float width, float height)
    {
        var pane = new Vector2(width, height);
        var half = pane * 0.5f;
        var capture = new MouseCapture();
        capture.Take(half);

        capture.Moved(new Vector2(MouseCapture.FullDeflectionCounts * 0.5f, -MouseCapture.FullDeflectionCounts * 0.5f));
        var halfway = MouseFlight.Offset(capture.StepCursor(pane), half, half);
        capture.Moved(new Vector2(MouseCapture.FullDeflectionCounts * 0.5f, -MouseCapture.FullDeflectionCounts * 0.5f));
        var edge = MouseFlight.Offset(capture.StepCursor(pane), half, half);

        Assert.Equal(0.5f, halfway.X, 5);
        Assert.Equal(-0.5f, halfway.Y, 5);
        Assert.Equal(1f, edge.X, 5);
        Assert.Equal(-1f, edge.Y, 5);
    }

    /// <summary>A seat's sensitivity divides the counts to full deflection, so a higher setting is
    /// less hand travel. At 2 half the counts reach the edge, at 0.5 twice as many. The default
    /// is the unscaled constant. Out-of-range settings stop at the scale's ends.</summary>
    [Theory]
    [InlineData(1f, 2000f)]
    [InlineData(2f, 1000f)]
    [InlineData(0.5f, 4000f)]
    [InlineData(4f, 500f)]
    [InlineData(0.25f, 8000f)]
    [InlineData(10f, 500f)]
    [InlineData(0f, 8000f)]
    public void StepCursor_SensitivityScalesTheCountsToFullDeflection(float sensitivity, float counts)
    {
        Assert.Equal(counts, MouseCapture.DeflectionCounts(sensitivity), 3);
        var pane = new Vector2(1920f, 1080f);
        var half = pane * 0.5f;
        var capture = new MouseCapture();
        capture.Take(half);

        capture.Moved(new Vector2(counts * 0.5f, 0f));
        var halfway = MouseFlight.Offset(capture.StepCursor(pane, sensitivity), half, half);
        capture.Moved(new Vector2(counts * 0.5f, 0f));
        var edge = MouseFlight.Offset(capture.StepCursor(pane, sensitivity), half, half);

        Assert.Equal(0.5f, halfway.X, 4);
        Assert.Equal(1f, edge.X, 4);
        Assert.Equal(MouseCapture.FullDeflectionCounts, MouseCapture.DeflectionCounts(Bindings.SensitivityScale.Default));
    }

    /// <summary>The slider scale both presentations step: the default at its middle, a doubling
    /// every quarter of it, the range's ends at its ends. One sideways step is the Original
    /// slider's own key step, so the two presentations land on the same values.</summary>
    [Fact]
    public void SensitivityScale_MapsTheSliderEvenlyInRatio()
    {
        Assert.Equal(50, Bindings.SensitivityScale.Level(Bindings.SensitivityScale.Default));
        Assert.Equal(0.25f, Bindings.SensitivityScale.FromLevel(0));
        Assert.Equal(0.5f, Bindings.SensitivityScale.FromLevel(25));
        Assert.Equal(1f, Bindings.SensitivityScale.FromLevel(50));
        Assert.Equal(2f, Bindings.SensitivityScale.FromLevel(75));
        Assert.Equal(4f, Bindings.SensitivityScale.FromLevel(100));
        for (int level = 0; level <= Bindings.SensitivityScale.MaxLevel; level++)
        {
            Assert.Equal(level, Bindings.SensitivityScale.Level(Bindings.SensitivityScale.FromLevel(level)));
        }

        Assert.Equal(UI.Menu.Original.SliderControl.KeyStep, Bindings.SensitivityScale.LevelStep);
        Assert.Equal(Bindings.SensitivityScale.FromLevel(55), Bindings.SensitivityScale.Step(1f, 1));
        Assert.Equal(4f, Bindings.SensitivityScale.Step(4f, 1));
        Assert.Equal(0.25f, Bindings.SensitivityScale.Step(0.25f, -1));
        Assert.Equal("1.00x", Bindings.SensitivityScale.Label(1f));
    }

    /// <summary>The captured stick end to end, counts in and deflection out. Nothing reads inside
    /// the centre band. From the band's edge the line runs straight to full deflection at the
    /// pane's edge.</summary>
    [Fact]
    public void Centred_TakesTheWiderBandOutOfTheMiddleAndKeepsTheEdgeAtFullDeflection()
    {
        const float band = MouseCapture.CentreBand;
        foreach (float fraction in new[] { 0f, 0.5f * band, band, 0.4f, 0.6f, 1f })
        {
            var pane = new Vector2(1920f, 1080f);
            var half = pane * 0.5f;
            var capture = new MouseCapture();
            capture.Take(half);
            capture.Moved(new Vector2(fraction * MouseCapture.FullDeflectionCounts, 0f));
            var offset = MouseFlight.Offset(capture.StepCursor(pane), half, half);

            var stick = MouseFlight.Read(MouseCapture.Centred(offset).X, 0f, 0f, isAutogyro: false);

            float expected = fraction <= band ? 0f : (fraction - band) / (1f - band);
            Assert.Equal(-expected, stick.Roll, 4);
        }
    }

    /// <summary>The band is the captured path's alone. An offset the decoded 0.1 already flies on
    /// the desktop pointer reads nothing through the capture's wider band. The sign of a deflection
    /// survives the band on both axes.</summary>
    [Fact]
    public void Centred_IsWiderThanTheDecodedGateAndKeepsTheSign()
    {
        float between = 0.5f * (MouseFlight.AttitudeDeadzone + MouseCapture.CentreBand);

        Assert.NotEqual(0f, MouseFlight.Read(between, 0f, 0f, isAutogyro: false).Roll);
        Assert.Equal(Vector2.Zero, MouseCapture.Centred(new Vector2(between, -between)));

        var centred = MouseCapture.Centred(new Vector2(-0.6f, 0.6f));
        Assert.True(centred.X < -MouseFlight.AttitudeDeadzone && centred.Y > MouseFlight.AttitudeDeadzone,
            $"{centred}");
        Assert.Equal(new Vector2(-1f, 1f), MouseCapture.Centred(new Vector2(-1f, 1f)));
    }

    /// <summary>The confinement the pane gives: pushing past an edge saturates there. One step
    /// back moves at once, rather than paying off a debt the pane never had.</summary>
    [Fact]
    public void StepCursor_ConfinesTheCursorToThePane()
    {
        var pane = new Vector2(800f, 600f);
        var capture = new MouseCapture();
        capture.Take(new Vector2(400f, 300f));

        capture.Moved(new Vector2(50000f, -50000f));
        Assert.Equal(new Vector2(800f, 0f), capture.StepCursor(pane));
        capture.Moved(new Vector2(-150f, 200f));
        Assert.True(capture.StepCursor(pane).IsEqualApprox(new Vector2(770f, 30f)), $"{capture.Cursor}");

        capture.Moved(new Vector2(-50000f, 50000f));
        Assert.Equal(new Vector2(0f, 600f), capture.StepCursor(pane));
    }

    /// <summary>A pane with no extent pins the cursor at its corner rather than clamping to a
    /// negative span. A seat asked for its stick before its viewport has a size gets that
    /// corner.</summary>
    [Fact]
    public void StepCursor_ReadsACornerWithNoPaneToMeasure()
    {
        var capture = new MouseCapture();
        capture.Take(new Vector2(400f, 300f));

        capture.Moved(new Vector2(10f, 10f));

        Assert.Equal(Vector2.Zero, capture.StepCursor(Vector2.Zero));
    }

    /// <summary>The seed: the virtual cursor starts where the real one stood. The frame the
    /// capture begins reads the same stick as the frame before it.</summary>
    [Fact]
    public void Take_SeedsTheVirtualCursorWhereTheRealOneStood()
    {
        var capture = new MouseCapture();

        capture.Take(new Vector2(613f, 42f));

        Assert.True(capture.Holding);
        Assert.Equal(new Vector2(613f, 42f), capture.Cursor);
        Assert.Equal(new Vector2(613f, 42f), capture.StepCursor(new Vector2(800f, 600f)));
    }

    /// <summary>Head-look's half: the travel since the last read, cleared by the read, so a frame
    /// nobody looks on banks nothing towards the next one.</summary>
    [Fact]
    public void TakeLook_ReturnsTheTravelSinceTheLastReadAndClearsIt()
    {
        var capture = new MouseCapture();
        capture.Take(Vector2.Zero);

        capture.Moved(new Vector2(3f, -4f));
        capture.Moved(new Vector2(1f, 1f));

        Assert.Equal(new Vector2(4f, -3f), capture.TakeLook());
        Assert.Equal(Vector2.Zero, capture.TakeLook());
    }

    /// <summary>The stick's cursor and head-look's delta are fed from the same events and consumed
    /// apart, so reading one does not empty the other.</summary>
    [Fact]
    public void Moved_FeedsTheCursorAndTheLookIndependently()
    {
        var capture = new MouseCapture();
        capture.Take(new Vector2(100f, 100f));

        capture.Moved(new Vector2(100f, 100f));
        var look = capture.TakeLook();
        var cursor = capture.StepCursor(new Vector2(800f, 600f));

        Assert.Equal(new Vector2(100f, 100f), look);
        Assert.Equal(new Vector2(120f, 115f), cursor);
    }

    /// <summary>Nothing is banked while nothing is held. An uncaptured session reads as it would
    /// with no capture at all.</summary>
    [Fact]
    public void Moved_BanksNothingWhileNothingIsHeld()
    {
        var capture = new MouseCapture();

        capture.Moved(new Vector2(500f, 500f));

        Assert.False(capture.Holding);
        Assert.Equal(Vector2.Zero, capture.TakeLook());
    }

    /// <summary>The release drops the pending travel. The next capture starts from where the real
    /// cursor then is, not by replaying the motion that ended the last one.</summary>
    [Fact]
    public void Release_DropsThePendingTravel()
    {
        var capture = new MouseCapture();
        capture.Take(new Vector2(100f, 100f));
        capture.Moved(new Vector2(50f, 50f));

        capture.Release();
        capture.Take(new Vector2(400f, 300f));

        Assert.Equal(Vector2.Zero, capture.TakeLook());
        Assert.Equal(new Vector2(400f, 300f), capture.StepCursor(new Vector2(800f, 600f)));
    }

    /// <summary>A board's close never puts the seat's capture back. The pause sheet saves it on the
    /// frame the seat still holds, and restores it after EXIT has raised the menu.</summary>
    [Fact]
    public void Restorable_NeverHandsBackACapture()
    {
        Assert.Equal(Input.MouseModeEnum.Visible, MouseCapture.Restorable(Input.MouseModeEnum.Captured));
        Assert.Equal(Input.MouseModeEnum.Visible, MouseCapture.Restorable(Input.MouseModeEnum.Visible));
        Assert.Equal(Input.MouseModeEnum.Hidden, MouseCapture.Restorable(Input.MouseModeEnum.Hidden));
    }
}
