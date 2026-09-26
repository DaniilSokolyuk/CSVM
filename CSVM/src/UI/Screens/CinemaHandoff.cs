using System;

namespace CSVM.UI.Screens;

/// <summary>How a cinema reaches the screen: the film's name, the continuation to run on the frame
/// it stops (played out or skipped), and the presses that end it early.
/// <c>Session/Launch/Launcher.cs</c>'s <c>PlayCinema</c> has this shape and is handed over as itself; a
/// suite hands over a stand-in that records what it was asked for. Every flow that plays a film
/// takes this one type, so a caller writes the call once and a suite's recorder fits all of
/// them.</summary>
public delegate void CinemaPlay(string name, Action then, CinemaSkip skip);

/// <summary>What a cinema flow does with the continuation it was handed. The chapter films and the
/// closing film each open one screen when a film stops, and both open it through the same latch.
/// The boot block chains its films unwrapped, since each continuation starts the next film rather
/// than opening a screen.</summary>
public static class CinemaHandoff
{
    /// <summary>Wraps a continuation so it runs on the first call and never again. ⚠ Do not hand a
    /// continuation to a cinema unwrapped. A skip can land on the frame the film plays out and both
    /// paths end the film, which is what the original's own EC latch stands against, so the next
    /// screen opens once however many times the cinema says it stopped.</summary>
    public static Action Once(Action handoff)
    {
        bool ran = false;
        return () =>
        {
            if (ran)
            {
                return;
            }

            ran = true;
            handoff();
        };
    }
}

/// <summary>One film in front of a screen, as the screen behind it reads the span: the frames the
/// film owns, and the tail of the press that ended it. A skip is a press the film takes and the
/// screen never sees go down, so without this the screen reads the release (a pointer) or the edge
/// arriving with the hand-back (a key, a pad button) as a gesture of its own and fires whatever
/// stands under the pointer or in the focus. <c>Flight/Airframe/FlightReentryLatch.cs</c> swallows the same
/// still-held press on the flight side.</summary>
public sealed class CinemaFilm
{
    // The play call has not returned yet. A continuation reaching us inside it is a film that was
    // never put up (none was due, or the file would not read), so no press can be outstanding.
    private bool _handing;

    // A press the screen never saw go down has not been let go of yet.
    private bool _tail;

    /// <summary>Whether a film stands in front of the screen. Every frame belongs to the film
    /// while one does: the screen behind it is not on show and takes no input, not even a press no
    /// skip set reads.</summary>
    public bool Up { get; private set; }

    /// <summary>Plays a film in front of the screen: <paramref name="play"/> is the call that puts
    /// one up, taking the continuation to run on the frame it stops, and <paramref name="then"/> is
    /// what the screen does then. A <paramref name="play"/> that runs the continuation before it
    /// returns put no film up and leaves nothing outstanding.</summary>
    public void Play(Action<Action> play, Action then)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(then);
        _handing = true;
        Up = true;
        play(() =>
        {
            _tail = !_handing;
            Up = false;
            then();
        });
        _handing = false;
    }

    /// <summary>Whether this frame is the tail of the press that ended a film, which the screen
    /// then ignores whole. <paramref name="pointerHeld"/> is whether the pointer's button reads
    /// down this frame: the pointer's tail lasts until it comes up, where every other press
    /// arrives as an edge and is spent on the frame it lands on.</summary>
    public bool Swallows(bool pointerHeld)
    {
        if (!_tail)
        {
            return false;
        }

        _tail = pointerHeld;
        return true;
    }
}
