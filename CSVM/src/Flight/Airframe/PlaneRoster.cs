using System;

namespace CSVM.Flight.Airframe;

/// <summary>Pure lookups over a <see cref="SessionSpec"/>'s plane roster: which plane a player
/// flies and a readable display name for it. No session state, so these take the spec explicitly
/// rather than caching one.</summary>
public static class PlaneRoster
{
    /// <summary>The plane player <paramref name="index"/> flies: their own pick when the
    /// launchscreen (or a --plane= list) gave one, else the last one named, so a single
    /// --plane= puts everybody in the same aircraft.</summary>
    public static string PlaneFor(SessionSpec spec, int index) =>
        spec.PlaneNames.Count == 0 ? spec.PlaneName : spec.PlaneNames[Math.Min(index, spec.PlaneNames.Count - 1)];

    /// <summary>The aircraft an Instant Action mission forces on every human, or null to leave
    /// each player the pick <see cref="PlaneFor"/> returns. A def read off a file (<c>--ia=</c>,
    /// <c>ia.zrd.json</c>) names one <c>player_plane</c> and no human behind it picked anything,
    /// so it stands for all of them. The launchscreen wizard's def carries PLAYER 1's pick in that
    /// same field (<c>LaunchMenu.FireLaunch</c>), so honouring it would fly P2..P4 in
    /// player 1's aircraft instead of their own.</summary>
    public static string? InstantActionOverride(SessionSpec spec, string? iaPlayerNode) =>
        spec.IaDef != null ? null : iaPlayerNode;

    /// <summary>A readable plane name: the def's own authored <c>title</c> where it has been
    /// resolved ("Medusa Kestrel"), else derived from the vehicle.json def name, the player defs
    /// are "p&lt;name&gt;" (pbloodhawk, ppeacemaker, pfury, …), so strip the leading p and
    /// title-case → "Bloodhawk". Falls back to the node name.</summary>
    public static string PlaneDisplayName(PlaneStats stats)
    {
        if (stats.AiTitle is { Length: > 0 } title)
        {
            return title;
        }

        var d = stats.DefName;
        string name = d.Length > 1 && (d[0] == 'p' || d[0] == 'P') ? d[1..]
            : d.Length > 0 ? d
            : stats.NodeName;
        return Humanize(name);
    }

    /// <summary>"stunt_flying" → "Stunt Flying": underscores to spaces, each word title-cased.</summary>
    public static string Humanize(string s)
    {
        var words = s.Split(new[] { '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..].ToLowerInvariant();
        return string.Join(' ', words);
    }
}
