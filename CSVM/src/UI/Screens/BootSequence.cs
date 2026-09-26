using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;
using CSVM.UI.Boards;

namespace CSVM.UI.Screens;

/// <summary>
/// What a boot-sequence still is, one member per action in <c>fmv.zrd</c>'s <c>INTRO</c> block that
/// is not a film. The block puts its image up once, at the start, and the first film takes it down,
/// so the two waits and the fade that follow have no picture to hold.
/// </summary>
public enum BootHold
{
    /// <summary><c>SHOWIMAGE</c>: the copyright card goes up, and the first film takes it
    /// down.</summary>
    Card,

    /// <summary><c>WAIT</c>: whatever is on screen holds for that long, and after the card is down
    /// there is nothing to hold.</summary>
    Wait,

    /// <summary><c>FADEOUT</c>: a ramp over the screen the first film already emptied. ⚠ Do not
    /// drop the action: it is authored, and an action is not deleted for being invisible.</summary>
    Fade,
}

/// <summary>
/// <c>fmv.zrd</c>'s boot block, engine-free: the copyright card it opens with, and the eight
/// actions in the reader's own order. The card, its five-second hold, the publisher logo, a wait,
/// the fade, the developer logo, another wait, then <c>CHAP0</c>'s opening cinema. Every name,
/// position and duration here is that reader's own (docs/formats/cinemas.md), and the three calls
/// that change what is on screen are supplied by the caller, so the order runs with no engine
/// present. <see cref="BootCard"/> is the engine half and <c>Launcher.PlayCinema</c> the films.
/// </summary>
public sealed class BootSequence
{
    /// <summary><c>INTRO</c>'s <c>WAIT 5.0</c>, how long the copyright card holds before the
    /// first logo.</summary>
    public const double CardSeconds = 5.0;

    /// <summary>The <c>WAIT 1.0</c> the block puts after each of its two logos. It is authored and
    /// not spent, for the reason <see cref="Held"/> gives.</summary>
    public const double LogoGapSeconds = 1.0;

    /// <summary><c>FADEOUT 0,0,0 1.0 1.0</c>'s first number, the second it would ramp over before
    /// the second logo. Nobody sees it: the first film took the card down, so there is nothing left
    /// on screen for it to take, and <see cref="Held"/> is why it costs none of the player's time.
    /// ⚠ Its second 1.0 is deliberately not spent, that number being undecoded.</summary>
    public const double FadeSeconds = 1.0;

    /// <summary><c>INTRO</c>'s first <c>PLAYAVI</c>, the publisher logo.</summary>
    public const string FirstLogo = "MSopen1.mpg";

    /// <summary><c>INTRO</c>'s second <c>PLAYAVI</c>, the developer logo.</summary>
    public const string SecondLogo = "zipper.mpg";

    /// <summary>The whole of the <c>CHAP0</c> block, the opening cinema.</summary>
    public const string OpeningCinema = "Chap0.mpg";

    /// <summary>The card's art, verbatim as the extraction spells it. <c>SHOWIMAGE</c> names it
    /// <c>MM_splashbackground</c> with no extension at all, and the file is 800x600, so it fills
    /// the authored board exactly and takes no size of its own.</summary>
    public const string CardArt = "MM_SPLASHBACKGROUND.JPG";

    /// <summary>The <c>CopyrightNotice</c> font's own <c>height</c> from <c>fonts.zrd</c>, read as
    /// a character height in pixels. That font is Courier New, white, shadowed and centre
    /// aligned.</summary>
    public const float CardFontSize = 12f;

    /// <summary>The first <c>TEXT</c> action's authored y. Its x is 400, the board's midline,
    /// which is why the line is centred across the whole authored width instead of placed against
    /// an edge.</summary>
    public const float FirstLineY = 550f;

    /// <summary>The second <c>TEXT</c> action's authored y, fifteen pixels under the first.</summary>
    public const float SecondLineY = 565f;

    // Any key or a left click, on every action alike. The boot sequence's whole contract with a
    // player who has been taught no key is that a press moves on, and a card or a gap that ignored
    // one would teach them the opposite right before a 145-second film.
    private const CinemaSkip Keys = CinemaScreen.BootKeys;

    private readonly CinemaPlay _film;
    private readonly ShowStill _still;
    private readonly DropStill _drop;

    /// <summary>Builds the sequence over the three calls that change what is on screen.</summary>
    public BootSequence(CinemaPlay film, ShowStill still, DropStill drop)
    {
        _film = film;
        _still = still;
        _drop = drop;
    }

    /// <summary>How a still reaches the screen: which one, how long it holds, and the continuation
    /// to run when the hold ends or a press ends it early.</summary>
    public delegate void ShowStill(BootHold hold, double seconds, Action then);

    /// <summary>How the card leaves the screen, called once, as the first film starts.
    /// <c>SHOWIMAGE</c>'s picture does not survive a <c>PLAYAVI</c>: the original shows the
    /// copyright notice at the beginning only and shows no fade between the logos, watched at the
    /// controls (docs/formats/cinemas.md).</summary>
    public delegate void DropStill();

    /// <summary>The copyright card in the original's 800x600 space: the splash art under the two
    /// message-table lines <c>SHOWIMAGE</c> carries. It composes like any other screen, so the art
    /// and the strings resolve out of the extraction rather than out of this file, and a data root
    /// with neither yields a card of the same shape with its keys showing.</summary>
    public static ComposedBoard Card(string dataRoot)
    {
        var messages = Messages.Load(Path.Combine(dataRoot, "extracted", "messages.json"));
        var lines = new List<BoardLine>();
        Write(lines, messages.Get("MSG_COPYRIGHT1"), FirstLineY);
        Write(lines, messages.Get("MSG_COPYRIGHT2"), SecondLineY);
        return new ComposedBoard(
            Array.Empty<BoardPicture>(),
            Array.Empty<BoardStroke>(),
            lines,
            Array.Empty<BoardPlaque>(),
            backdrop: new[] { new BoardPicture(new BoardArt(BoardArtLibrary.Ui, CardArt), 0f, 0f) });
    }

    /// <summary>How much of a hold's authored duration is spent on screen: all of it while the card
    /// is up, none of it once the first film has taken the card down. Film of the original from
    /// launch shows the films running back to back, 0.183 s of black where this block authors 2.0 s
    /// between the two logos (docs/formats/cinemas.md). ⚠ Do not spend the rest again: the reader
    /// authors both waits and the fade, and the block still runs all three in its order.</summary>
    public static double Held(BootHold hold, double authored) =>
        hold == BootHold.Card ? authored : 0.0;

    /// <summary>Runs the block in the reader's own order and calls <paramref name="then"/> after
    /// the opening cinema. ⚠ A press ends the action it lands on and the next one begins; whether
    /// the original abandoned the rest of the block instead is undecoded, its <c>PLAYAVI</c>
    /// handler never having been followed into the executable.</summary>
    public void Run(Action then)
    {
        void Still(BootHold hold, double authored, Action next) =>
            _still(hold, Held(hold, authored), next);

        void First()
        {
            _drop();
            _film(FirstLogo, AfterFirst, Keys);
        }

        void AfterFirst() => Still(BootHold.Wait, LogoGapSeconds, Fade);
        void Fade() => Still(BootHold.Fade, FadeSeconds, Second);
        void Second() => _film(SecondLogo, AfterSecond, Keys);
        void AfterSecond() => Still(BootHold.Wait, LogoGapSeconds, Opening);
        void Opening() => _film(OpeningCinema, then, Keys);

        Still(BootHold.Card, CardSeconds, First);
    }

    // Each line is drawn twice, a black copy one authored pixel down and across under a white one,
    // because the CopyrightNotice font authors a shadow and the board renderer inks a line once.
    // Both inks are the renderer's own white and black rather than a screen palette.
    private static void Write(List<BoardLine> into, string text, float y)
    {
        into.Add(new BoardLine(text, 1f, y + 1f, BoardFit.AuthoredWidth, CardFontSize,
            BoardInk.DialogPressed, Justify: BoardJustify.Center));
        into.Add(new BoardLine(text, 0f, y, BoardFit.AuthoredWidth, CardFontSize,
            BoardInk.Dialog, Justify: BoardJustify.Center));
    }
}
