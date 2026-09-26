using System;
using System.Diagnostics;
using System.IO;
using CSVM.Session.Campaign;

namespace CSVM.UI.Menu;

/// <summary>
/// The scratch profile store the campaign screenshot aids read, shared by both presentations so
/// their shots show one player: under <c>%TEMP%\CSVM\menu-aid-profiles\&lt;pid&gt;</c>, emptied on
/// every open, and seeded with two profiles for the filled-roster shot. The process id is in the
/// path because the engine stage runs several Godot processes at once and two menu suites opening
/// one shared directory deleted each other's files mid-suite; a sibling directory whose process is
/// gone is swept on open so the tree does not grow. A progressed store additionally flies
/// the first three missions, which is what puts rows on the previous-missions list and moves the
/// cabin's Next Mission off the campaign's first entry. Nothing here can reach <c>user://Profiles</c>.
/// </summary>
public static class CampaignAidProfiles
{
    /// <summary>The one campaign <c>--menu=</c> value that is a player's door rather than a shot:
    /// it opens the campaign over the presentation's own store (the player's <c>user://Profiles</c>
    /// unless a suite set a scratch one) and never over this scratch store. Every other campaign
    /// value opens a screen over <see cref="Store"/>. The store is the presentation's to choose,
    /// so no <c>MenuReturnDestination</c> names one.</summary>
    public const string PlayerDoor = "campaign";

    /// <summary>The seeded player every aid seats.</summary>
    public const string Pilot = "Zachary";

    /// <summary>The second seeded player, the one the filled-roster shot lists beside the pilot.</summary>
    public const string SecondPilot = "Nathan";

    /// <summary>How many missions the progressed store has flown. Three gives the previous-missions
    /// list a body and leaves the cabin's Next Mission somewhere other than the campaign's first
    /// entry; raising it walks the briefing aid onto a longer narration than the repaint suite's
    /// own 90-second window covers.</summary>
    public const int MissionsFlown = 3;

    /// <summary>The <c>campaign-planeselection</c> aid's argument that presses the pilot's EXPORT,
    /// so the shot is the one-button messagebox standing over the screen. A word of the campaign
    /// aids' input script, and the one Original's own aid reads by itself.</summary>
    public const string ExportArgument = "export";

    // The completed-objective mask those runs record. Bit 0 alone would leave every scrapbook page
    // blank, since the story scraps are gated on the objectives that unlock them, so the aid
    // records a clean run: bits 0 to 12, the range the shipped rows' own gates use.
    private const int CompletedMask = 0x1fff;

    /// <summary>The store's directory, one per process.</summary>
    public static string Directory => Path.Combine(Root, Environment.ProcessId.ToString());

    private static string Root => Path.Combine(Path.GetTempPath(), "CSVM", "menu-aid-profiles");

    /// <summary>The build store the scratch-store aids open the campaign over, a subdirectory of
    /// <see cref="Directory"/> emptied with it: the export aid's write lands here and never in
    /// <c>user://Planes</c>.</summary>
    public static Flight.Hangar.CustomPlaneStore Planes() => new(Path.Combine(Directory, "Planes"));

    /// <summary>A fresh scratch store: emptied, then seeded with the two players when
    /// <paramref name="seeded"/>, the first of them progressed through the first three missions
    /// when <paramref name="progressed"/>.</summary>
    public static CampaignProfileStore Store(bool seeded, bool progressed = false)
    {
        string dir = Directory;
        try
        {
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }

            SweepDeadSiblings();
        }
        catch (IOException)
        {
            // A leftover the aid cannot clear is not worth failing a screenshot over.
        }

        var store = new CampaignProfileStore(dir);
        if (!seeded)
        {
            return store;
        }

        var first = CampaignProfileDef.NewProfile(Pilot);
        if (progressed)
        {
            for (int seq = 0; seq < MissionsFlown; seq++)
            {
                CampaignProgression.Record(first, new MissionAttempt(
                    seq, CompletedMask, 300_000 + (seq * 20_000), 400, 120,
                    first.Planes[0].Airframe, first.Planes[0].Name));
            }
        }

        store.Save(first);
        store.Save(CampaignProfileDef.NewProfile(SecondPilot));
        return store;
    }

    // Only a directory whose owning process has exited is removed: a live sibling is another
    // shard's store, mid-suite.
    private static void SweepDeadSiblings()
    {
        if (!System.IO.Directory.Exists(Root))
        {
            return;
        }

        foreach (string sibling in System.IO.Directory.GetDirectories(Root))
        {
            if (!int.TryParse(Path.GetFileName(sibling), out int pid) || pid == Environment.ProcessId)
            {
                continue;
            }

            bool alive;
            try
            {
                using var owner = Process.GetProcessById(pid);
                alive = !owner.HasExited;
            }
            catch (ArgumentException)
            {
                alive = false;
            }
            catch (InvalidOperationException)
            {
                alive = false;
            }

            if (!alive)
            {
                System.IO.Directory.Delete(sibling, recursive: true);
            }
        }
    }
}
