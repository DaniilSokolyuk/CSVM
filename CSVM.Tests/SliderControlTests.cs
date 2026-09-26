using System.Collections.Generic;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Original shell's continuous control, engine-free: where a pointer's X lands on a track and
/// where a value puts the thumb, the hold-and-move a press begins and a release ends, and the
/// sideways step that clamps at both ends where every other stepped row on this shell wraps. The
/// track is the authored volume slider's own, read off the layout: a 171-pixel slot at X=137 with
/// a 43-pixel thumb, its press region the slot inset by the authored 0, -10, 1 and -10, over
/// <see cref="AudioMix"/>'s range.
/// </summary>
public class SliderControlTests
{
    private const float SlotX = 137f;
    private const float SlotY = 350f;
    private const float SlotWidth = 171f;
    private const float ThumbWidth = 43f;

    // The value 0 puts the thumb's middle here and 100 puts it a full run further right, which is
    // what every reading below is measured from.
    private const float LeftEnd = SlotX + (ThumbWidth / 2f);
    private const float Run = SlotWidth - ThumbWidth;

    private static readonly SliderTrack Track =
        new(SlotX, SlotY, SlotWidth, 3f, ThumbWidth, 21f, AudioMix.MinLevel, AudioMix.MaxLevel);

    [Fact]
    public void TheTrackReadsAPointerAcrossTheSlotAndPutsTheThumbBackWhereItRead()
    {
        Assert.Equal(AudioMix.MinLevel, Track.ValueAt(LeftEnd));
        Assert.Equal(AudioMix.MaxLevel, Track.ValueAt(LeftEnd + Run));
        Assert.Equal(50, Track.ValueAt(LeftEnd + (Run / 2f)));

        // A press past either end of the slot reaches that end rather than reading off the track.
        Assert.Equal(AudioMix.MinLevel, Track.ValueAt(SlotX - 40f));
        Assert.Equal(AudioMix.MaxLevel, Track.ValueAt(SlotX + SlotWidth + 40f));

        // The thumb stands where the value was read, so the drawn control and the pressed point
        // are one reading taken in two directions.
        foreach (int level in new[] { 0, 1, 37, 50, 99, 100 })
        {
            Assert.Equal(level, Track.ValueAt(Track.ThumbX(level) + (ThumbWidth / 2f)));
        }

        Assert.Equal(SlotX, Track.ThumbX(AudioMix.MinLevel));
        Assert.Equal(SlotX + Run, Track.ThumbX(AudioMix.MaxLevel));
        // The thumb is centred on the slot, so it stands clear of the three-pixel line on both sides.
        Assert.Equal(341f, Track.ThumbY);
    }

    [Fact]
    public void ASidewaysStepMovesFiveAndClampsAtBothEndsInsteadOfWrapping()
    {
        var level = new Level(50);
        Assert.True(SliderControl.StepValue(Rows(level), 0, 1));
        Assert.Equal(55, level.Value);
        Assert.True(SliderControl.StepValue(Rows(level), 0, -1));
        Assert.Equal(50, level.Value);
        Assert.Equal(5, SliderControl.KeyStep);

        // Twenty presses cross the range, and the twenty-first stays at the end rather than
        // stepping a player from full volume to silence.
        for (int i = 0; i < 20; i++)
        {
            SliderControl.StepValue(Rows(level), 0, 1);
        }

        Assert.Equal(AudioMix.MaxLevel, level.Value);
        Assert.True(SliderControl.StepValue(Rows(level), 0, 1));
        Assert.Equal(AudioMix.MaxLevel, level.Value);

        for (int i = 0; i < 20; i++)
        {
            SliderControl.StepValue(Rows(level), 0, -1);
        }

        Assert.Equal(AudioMix.MinLevel, level.Value);
        Assert.True(SliderControl.StepValue(Rows(level), 0, -1));
        Assert.Equal(AudioMix.MinLevel, level.Value);

        // A row that is not a slider takes no step, so the sideways press goes on to cross columns.
        var other = new OriginalRow("PLAQUE", "FLY", OriginalRowKind.TextButton, 0f, 0f, 10f, 10f, true, 0, null);
        Assert.False(SliderControl.StepValue(new[] { other }, 0, 1));
    }

    [Fact]
    public void APressTakesHoldSetsTheValueToThePressedPointAndAHeldMoveTracksIt()
    {
        var level = new Level(50);
        var control = new SliderControl();

        Assert.True(control.Drive(Rows(level), Pointer(LeftEnd + (Run / 4f), SlotY, pressed: true, clicked: true), out bool changed));
        Assert.Equal("MASTER", control.Held);
        Assert.Equal(25, level.Value);
        Assert.True(changed);

        Assert.True(control.Drive(Rows(level), Pointer(LeftEnd + Run, SlotY + 6f, pressed: true), out changed));
        Assert.Equal(AudioMix.MaxLevel, level.Value);
        Assert.True(changed);

        // A held move that names the value the slider already stands at changes nothing, so a
        // still pointer does not redraw the board every frame.
        Assert.True(control.Drive(Rows(level), Pointer(LeftEnd + Run + 30f, SlotY, pressed: true), out changed));
        Assert.False(changed);

        // The move tracks off the row as well as on it: the drag owns the pointer until it is let
        // go, so leaving the press region does not park the thumb where it stood.
        Assert.True(control.Drive(Rows(level), Pointer(LeftEnd, SlotY - 200f, pressed: true), out changed));
        Assert.Equal(AudioMix.MinLevel, level.Value);
    }

    [Fact]
    public void LettingTheButtonGoEndsTheDragAndAPressOutsideThePressRegionTakesNoHold()
    {
        var level = new Level(50);
        var control = new SliderControl();
        control.Drive(Rows(level), Pointer(LeftEnd, SlotY, pressed: true, clicked: true), out _);
        Assert.Equal("MASTER", control.Held);

        Assert.False(control.Drive(Rows(level), Pointer(LeftEnd, SlotY, pressed: false), out bool changed));
        Assert.Null(control.Held);
        Assert.True(changed);

        // A moved pointer with the button up moves nothing once the drag has ended.
        Assert.False(control.Drive(Rows(level), Pointer(LeftEnd + Run, SlotY), out _));
        Assert.Equal(AudioMix.MinLevel, level.Value);

        // The authored region reaches ten pixels above and below the slot and stops a pixel short
        // of its right edge; a press outside it takes no hold and moves nothing.
        Assert.False(control.Drive(Rows(level), Pointer(LeftEnd, SlotY - 11f, pressed: true, clicked: true), out _));
        Assert.False(control.Drive(Rows(level), Pointer(SlotX + SlotWidth - 1f, SlotY, pressed: true, clicked: true), out _));
        Assert.Null(control.Held);
        Assert.Equal(AudioMix.MinLevel, level.Value);
    }

    [Fact]
    public void ADragThatBeganOnASliderHoldsThePointerSoItsClickActivatesNothing()
    {
        var level = new Level(50);
        var control = new SliderControl();
        var plaque = new OriginalRow("ACCEPT", "ACCEPT CHANGES", OriginalRowKind.TextButton,
            SlotX, SlotY - 4f, 240f, 12f, true, 0, null);

        // The plaque is drawn over the slider and would take the click on its own, but the frame
        // that carries the click reports the slider holding, which is what spends it.
        Assert.True(control.Drive(new[] { Rows(level)[0], plaque }, Pointer(LeftEnd, SlotY, pressed: true, clicked: true), out _));
        Assert.Equal("MASTER", control.Held);

        // And it keeps holding over the plaque for as long as the button is down, so the release
        // lands on no row at all.
        Assert.True(control.Drive(new[] { Rows(level)[0], plaque }, Pointer(SlotX + 200f, SlotY, pressed: true), out _));
        Assert.False(control.Drive(new[] { Rows(level)[0], plaque }, Pointer(SlotX + 200f, SlotY), out _));
        Assert.Null(control.Held);
    }

    [Fact]
    public void TheValueReadsBackAsAWholeNumberInsideTheAudioRangeWhereverThePointerGoes()
    {
        var level = new Level(50);
        var control = new SliderControl();
        control.Drive(Rows(level), Pointer(LeftEnd, SlotY, pressed: true, clicked: true), out _);
        for (float x = SlotX - 60f; x <= SlotX + SlotWidth + 60f; x += 3f)
        {
            control.Drive(Rows(level), Pointer(x, SlotY, pressed: true), out _);
            Assert.InRange(level.Value, AudioMix.MinLevel, AudioMix.MaxLevel);
        }

        // The drag ends where it ran off the right, at full volume rather than somewhere near it.
        Assert.Equal(AudioMix.MaxLevel, level.Value);
    }

    // The slider row as a page declares one: the authored slot at X=137 with the press region the
    // four insets carve out of it, and the level read and written through the caller's own field.
    // Rebuilt per frame, since the shell rebuilds its rows before every pointer frame.
    private static IReadOnlyList<OriginalRow> Rows(Level level) => new[]
    {
        new OriginalRow("MASTER", string.Empty, OriginalRowKind.Slider, SlotX, SlotY - 10f, SlotWidth - 1f, 23f,
            true, 0, null, Slider: new OriginalSlider(Track, level.Value, v => level.Value = v, null)),
    };

    private static MenuPointer Pointer(float x, float y, bool pressed = false, bool clicked = false) =>
        new(x, y, pressed, clicked);

    private sealed class Level
    {
        public Level(int value) => Value = value;

        public int Value { get; set; }
    }
}
