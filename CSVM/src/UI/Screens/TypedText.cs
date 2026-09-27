using System;
using System.Text;

namespace CSVM.UI.Screens;

/// <summary>
/// The characters the keyboard typed, as the pilot's own layout produced them, which is what every
/// menu seat's <see cref="MenuInput.Typed"/> reads. A key code names a US key position, so a German
/// ':' (Shift and the period key) would read as '&gt;'. Only the key event's Unicode carries the
/// character. The launcher feeds <see cref="Live"/> from its input callback. Each reader keeps its
/// own mark, so two seats polled on one frame both see a character and neither eats it.
/// Engine-free: the event's three fields arrive as plain values.
/// </summary>
public sealed class TypedText
{
    /// <summary>How many of the latest characters are held for a reader that fell behind. A menu
    /// reads every frame, so this only bounds what a reader that stopped reading can be handed.
    /// </summary>
    public const int Kept = 64;

    private readonly char[] _ring = new char[Kept];

    /// <summary>The process-wide feed the launcher writes and every menu seat reads.</summary>
    public static TypedText Live { get; } = new();

    /// <summary>How many characters have arrived since the process started, the count a reader's
    /// mark is kept against.</summary>
    public long Count { get; private set; }

    /// <summary>How many paste chords have been pressed since the process started, the count a
    /// reader's paste mark is kept against. A paste is a count rather than characters in the feed.
    /// The box it lands in reads the clipboard itself and cues the whole paste once.</summary>
    public long Pastes { get; private set; }

    /// <summary>The process frame the launcher last stamped, which a reader compares to tell a
    /// frame it skipped from one it read. Held here so a seat never asks the engine itself, and
    /// a unit test can build one with no engine running.</summary>
    public ulong Frame { get; private set; }

    /// <summary>The character one key event typed, or null for one that typed none. A release, an
    /// auto-repeat echo and a control code such as Enter or Backspace type none. A menu box takes
    /// one character per press, as its polled keys did.</summary>
    public static char? CharOf(bool pressed, bool echo, long unicode)
    {
        if (!pressed || echo || unicode is < 0x20 or > 0xFFFF)
        {
            return null;
        }

        char c = (char)unicode;
        return char.IsControl(c) || char.IsSurrogate(c) ? null : c;
    }

    /// <summary>Takes one key event, answering whether it typed a character.</summary>
    public bool Feed(bool pressed, bool echo, long unicode)
    {
        if (CharOf(pressed, echo, unicode) is not { } c)
        {
            return false;
        }

        _ring[Count % Kept] = c;
        Count++;
        return true;
    }

    /// <summary>Records the frame now running.</summary>
    public void Stamp(ulong frame) => Frame = frame;

    /// <summary>Takes one paste chord.</summary>
    public void FeedPaste() => Pastes++;

    /// <summary>Whether a paste chord was pressed after <paramref name="mark"/>, with the mark moved
    /// up to now. Several on one frame are one paste, since each would insert the same clipboard.
    /// </summary>
    public bool PastedSince(ref long mark)
    {
        bool pasted = mark < Pastes;
        mark = Pastes;
        return pasted;
    }

    /// <summary>Every character that arrived after <paramref name="mark"/>, oldest first, and the
    /// mark moved up to now. A reader further behind than <see cref="Kept"/> gets the newest ones.
    /// </summary>
    public string Since(ref long mark)
    {
        long from = Math.Clamp(mark, Math.Max(0L, Count - Kept), Count);
        mark = Count;
        if (from == Count)
        {
            return string.Empty;
        }

        var typed = new StringBuilder((int)(Count - from));
        for (long i = from; i < Count; i++)
        {
            typed.Append(_ring[i % Kept]);
        }

        return typed.ToString();
    }
}
