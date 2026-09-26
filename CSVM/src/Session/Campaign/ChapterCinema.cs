using System;
using CSVM.Mech3;
using CSVM.UI.Screens;

namespace CSVM.Session.Campaign;

/// <summary>
/// Which cinema plays before a campaign chapter, and the one handoff to the passenger cabin that
/// follows it. The chapter is the profile's own story position and nothing else, so a chapter
/// number is never passed in from a screen; the mapping from chapter to film is the identity
/// <c>CAMPAIGNINTRO.SCRIPT</c> builds, chapter N playing <c>chapN.mpg</c>
/// (docs/formats/cinemas.md). Playing is a delegate the caller supplies, which keeps the whole
/// decision testable with no engine present and leaves the film itself to
/// <c>Launcher.PlayCinema</c>.
/// </summary>
public sealed class ChapterCinema
{
    /// <summary>How many story positions one chapter spans. The five chapters open at <c>seq</c> 0,
    /// 5, 10, 15 and 20, which <c>SCRAPBOOK.CSV</c>'s own chapter keys confirm independently of the
    /// sequence reader; the last chapter is four missions long and the division still lands on it
    /// (docs/formats/campaign-sequence.md).</summary>
    public const int MissionsPerChapter = 5;

    private readonly CinemaPlay _play;

    private int _played;

    /// <summary>Builds a chapter cinema over the call that puts one on screen.</summary>
    public ChapterCinema(CinemaPlay play) => _play = play;

    /// <summary>The chapter last handed to <see cref="CinemaPlay"/>, or 0 when none has been. It
    /// is what keeps a second visit to the same cabin from replaying the film.</summary>
    public int ChapterPlayed => _played;

    /// <summary>The story chapter, 1 to 5, a campaign position belongs to, or 0 for a position the
    /// sequence does not hold. ⚠ Not <see cref="CampaignSequence.Chapter"/>, which answers the
    /// world folder 1 to 8: chapter 2 spans three of those folders.</summary>
    public static int ChapterOf(int seq) =>
        seq < 0 || seq >= CampaignSequence.MissionCount ? 0 : (seq / MissionsPerChapter) + 1;

    /// <summary>Whether a position is the first mission of its chapter, which is where a chapter
    /// cinema belongs and the only place one plays.</summary>
    public static bool OpensChapter(int seq) => ChapterOf(seq) > 0 && seq % MissionsPerChapter == 0;

    /// <summary>The film a chapter plays, without its extension.</summary>
    public static string NameOf(int chapter) => $"chap{chapter}";

    /// <summary>Opens the passenger cabin for a profile, playing its chapter cinema first when the
    /// profile's next mission opens a chapter this instance has not played yet. Answers whether a
    /// cinema was played, so the caller can tell a handoff that already happened from one that is
    /// still to come.</summary>
    public bool OpenCabin(CampaignProfileDef profile, Action showCabin) =>
        OpenCabin(CampaignProgression.NextMissionSeq(profile), showCabin);

    /// <summary>The same, over a story position directly.</summary>
    public bool OpenCabin(int seq, Action showCabin)
    {
        int chapter = ChapterOf(seq);
        if (!OpensChapter(seq) || chapter == _played)
        {
            showCabin();
            return false;
        }

        _played = chapter;

        // ⚠ Do not unify these presses with the closing cinema's. Space and Return skip a chapter
        // cinema and do nothing on the closing one, which the two scripts author separately.
        _play(NameOf(chapter), CinemaHandoff.Once(showCabin), CinemaScreen.ChapterKeys);
        return true;
    }
}
