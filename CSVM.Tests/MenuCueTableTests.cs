using CSVM.Session.Launch;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using Xunit;

namespace CSVM.Tests;

/// <summary>The menu cue table and the pointer seat: every cue the Original presentation asks
/// for resolves to one of the four wavs the original's globals script binds, and the pointer
/// seat turns the mouse into one seat's pointer with the click as a press edge.</summary>
public class MenuCueTableTests
{
    [Fact]
    public void EveryCueTheOriginalPresentationAsksForResolvesToAScriptBoundWav()
    {
        Assert.Equal("MOUSEOVER.WAV", MenuCueTable.FileFor(OriginalCues.Rollover));
        Assert.Equal("MOUSECLICK.WAV", MenuCueTable.FileFor(OriginalCues.Click));
        Assert.Null(MenuCueTable.FileFor("menu.no-such-cue"));
        Assert.Equal(4, MenuCueTable.Names.Count);
    }

    [Fact]
    public void ThePointerSeatAddsTheMouseAsAPointerAndReportsTheClickAsAnEdge()
    {
        var inner = new ScriptedMenuSeat();
        (float X, float Y)? at = (100f, 50f);
        bool held = false;
        var seat = new PointerSeat(inner, () => at, () => held);

        inner.Enqueue(new MenuCommands { Accept = true });
        var frame = seat.Poll(1f / 60f);
        Assert.True(frame.Accept);
        Assert.Equal(new MenuPointer(100f, 50f, false, false), frame.Pointer);

        held = true;
        Assert.Equal(new MenuPointer(100f, 50f, true, true), seat.Poll(1f / 60f).Pointer);
        Assert.Equal(new MenuPointer(100f, 50f, true, false), seat.Poll(1f / 60f).Pointer);
        held = false;
        Assert.Equal(new MenuPointer(100f, 50f, false, false), seat.Poll(1f / 60f).Pointer);

        // A held button through Prime is not a click on the next frame.
        held = true;
        seat.Prime();
        Assert.False(seat.Poll(1f / 60f).Pointer!.Value.Clicked);
        Assert.Equal(1, inner.Primes);

        at = null;
        Assert.Null(seat.Poll(1f / 60f).Pointer);
        Assert.Equal("scripted", seat.DeviceLabel);
    }
}
