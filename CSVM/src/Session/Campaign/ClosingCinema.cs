using System;
using CSVM.Mech3;
using CSVM.UI.Screens;

namespace CSVM.Session.Campaign;

/// <summary>
/// Whether the closing cinema plays before the scrapbook a flown mission opens, and the one handoff
/// to that scrapbook. The gate is the mission just flown, its result and its story position: a win
/// on the campaign's last mission plays the film, the first flight and every replay alike, and any
/// other ending reaches the book with no film, which is what <c>FINALCINEMA.SCRIPT</c>'s own false
/// branch does (docs/formats/cinemas.md). The profile's completion state decides nothing, so a
/// failed mission on a finished campaign opens the book silently. Playing is a delegate the caller
/// supplies, which keeps the whole decision testable with no engine present and leaves the film
/// itself to <c>Launcher.PlayCinema</c>.
/// </summary>
public sealed class ClosingCinema
{
    /// <summary>The film, spelled as the <c>FinalCinema</c> screen's own <c>CF_MOVIE</c> layout row
    /// spells it. That row is the only place the name is written down, because
    /// <c>FINALCINEMA.SCRIPT</c> sets no art path (docs/formats/cinemas.md). It is transcribed here
    /// rather than read back, since the row lives in Original's decoded layout and this film plays
    /// under both presentations; the lookup is case-blind either way.</summary>
    public const string Name = "Final.MPG";

    private readonly CinemaPlay _play;

    /// <summary>Builds a closing cinema over the call that puts one on screen.</summary>
    public ClosingCinema(CinemaPlay play) => _play = play;

    /// <summary>Whether a mission that ended this way earns the film: it was won, and it was the
    /// campaign's last story position. A replay of that mission earns it again, since the flown
    /// result is the whole gate and nothing here is latched.</summary>
    public static bool PlaysAfter(int seq, bool missionWon) =>
        missionWon && seq == CampaignSequence.MissionCount - 1;

    /// <summary>Opens the scrapbook the mission just flown from <paramref name="seq"/> earned,
    /// playing the closing film first where <see cref="PlaysAfter"/> says one is due. Answers
    /// whether a cinema was played, so the caller can tell a handoff that already happened from one
    /// still to come.</summary>
    public bool OpenScrapbook(int seq, bool missionWon, Action showScrapbook)
    {
        if (!PlaysAfter(seq, missionWon))
        {
            showScrapbook();
            return false;
        }

        // ⚠ Do not unify these presses with the chapter cinema's. Space and Return skip a chapter
        // cinema and do nothing here, which the two scripts author separately.
        _play(Name, CinemaHandoff.Once(showScrapbook), CinemaScreen.ClosingKeys);
        return true;
    }
}
