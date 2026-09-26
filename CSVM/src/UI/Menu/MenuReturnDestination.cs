using System.Collections.Generic;
using CSVM.Flight;

namespace CSVM.UI.Menu;

/// <summary>
/// The final numbers one ended Instant Action mission hands the menu. They are the outcome, the
/// context line naming the chapter and mission type, and the four counters the wrap-up screen
/// draws. The stunt run's split table comes already flattened to text, with player 1's Danger Zone
/// photographs in marker order. Every number is read at the ending and never again, so the record
/// can outlive the session node that made it. The splits are strings rather than a
/// <c>StuntSummary</c> for the same reason. The photographs are the camera's own records, which a thumbnail still on its way
/// completes in place after the session is gone (<see cref="StuntShot.Landed"/>).
/// </summary>
public sealed record IaWrapupSnapshot(
    bool Won, string Context, float Elapsed, int EnemiesShotDown, int ZonesCompleted, int ShotPercent,
    IReadOnlyList<string>? StuntLines = null, IReadOnlyList<StuntShot>? Shots = null);

/// <summary>
/// Where the menu should stand when it comes back, said semantically so the host never names a
/// screen. Each presentation maps a destination into its own graph on
/// <see cref="IMenuPresentation.Activate"/>, so two presentations may land the same destination on
/// entirely different screens. A destination a graph lacks maps to the nearest one it has.
/// The hierarchy is closed: only these six destinations exist.
/// </summary>
public abstract record MenuReturnDestination
{
    /// <summary>The presentation's own top level: a cold start, a quit to menu, and always the
    /// landing point after a presentation switch.</summary>
    public static readonly MenuReturnDestination TopLevel = new TopLevelReturn();

    /// <summary>The Instant Action screen the sortie was set up on, its settings still standing.</summary>
    public static readonly MenuReturnDestination InstantAction = new InstantActionReturn();

    private protected MenuReturnDestination()
    {
    }

    /// <summary>Where a flight launched by <paramref name="exit"/> comes back to when it is left
    /// early: the screen it was launched from. A campaign mission abandoned this way was not flown,
    /// so the cabin is the landing and never the debrief, which a finished mission raises instead.
    /// Read off the exit rather than off the session's spec, because a spec inherits the command
    /// line's own <c>--campaign=</c> and would call a Free Flight launched afterwards a campaign
    /// mission.</summary>
    public static MenuReturnDestination ForLaunch(MenuExit exit) => exit switch
    {
        CampaignMissionExit { Profile.Length: 0, Net: not null } => new CoopGuestReturn(null),
        CampaignMissionExit mission => new CabinReturn(mission.Profile),
        LaunchExit { InstantAction: not null } => InstantAction,
        _ => TopLevel,
    };
}

/// <summary>The presentation's own top level. Use <see cref="MenuReturnDestination.TopLevel"/>.</summary>
public sealed record TopLevelReturn : MenuReturnDestination;

/// <summary>Back to the Instant Action screen, standing on the setup the sortie flew: the preset,
/// the environment, the mission, the waves, the wingmen and the pilot's plane. The settings live in
/// the feature rather than in the destination, so nothing about the screen crosses the seam. Use
/// <see cref="MenuReturnDestination.InstantAction"/>.</summary>
public sealed record InstantActionReturn : MenuReturnDestination;

/// <summary>Back to the Instant Action wrap-up, carrying one ended mission's final numbers. They
/// are the outcome, the four counters and the stunt splits as they stood at the ending. They travel
/// with the destination because the session that counted them is freed before the page draws. A
/// presentation with no wrap-up page of its own lands on the Instant Action screen instead, which
/// is where this page's CONTINUE goes.</summary>
public sealed record InstantActionWrapupReturn(IaWrapupSnapshot Snapshot) : MenuReturnDestination;

/// <summary>Back to the campaign hub for the named profile, after leaving a campaign screen or
/// abandoning a mission.</summary>
public sealed record CabinReturn(string Profile) : MenuReturnDestination;

/// <summary>Back to the campaign's results for one flown mission: the named profile, opened on
/// the story position just flown, and whether that mission was won. The result travels with the
/// destination because nothing on the far side can recover it: a lost replay of a mission the
/// profile has already completed leaves the profile exactly as it found it, and the closing film is
/// gated on the result (<c>Session/ClosingCinema.cs</c>).</summary>
public sealed record DebriefReturn(string Profile, int MissionSeq, bool MissionWon) : MenuReturnDestination;

/// <summary>A co-op guest back from its host's mission, onto the host's boards again. It carries
/// the guest's own attempt for the host's debrief, or null when it flew nothing. A guest
/// whose link to the host has ended lands on the Connection page instead, which says why.</summary>
public sealed record CoopGuestReturn(CSVM.Session.MissionAttempt? Attempt) : MenuReturnDestination;
