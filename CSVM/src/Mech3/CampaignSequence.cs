using System;
using System.Collections.Generic;

namespace CSVM.Mech3;

/// <summary>One entry of the campaign's flat mission list: where the story position <see cref="Seq"/>
/// stores its data, and whether it flies with a wingman. Decode: docs/formats/campaign-sequence.md.
/// ⚠ Three numberings describe the same mission and must not be mixed: <see cref="Seq"/> (0..23,
/// story order), the 1-based <see cref="Ordinal"/> (what the reward table and the statistics records
/// count in), and the (<see cref="Campaign"/>, <see cref="Mission"/>) storage address.</summary>
public readonly record struct CampaignMission(
    int Seq, string Desc, int Campaign, int Mission, string Area, bool Wingman)
{
    /// <summary>The 1-based mission ordinal, <c>Seq + 1</c>.</summary>
    public int Ordinal => Seq + 1;

    /// <summary>The <c>Persist.NNN</c> / <c>Mission.NNN</c> suffix, <c>campaign * 100 + mission</c>.</summary>
    public int SaveId => (Campaign * 100) + Mission;

    /// <summary>The ZBD world-folder name this mission's chapter world is built from. ⚠ The folder
    /// number is not the act: folder 3 (<c>c1c</c>) is act 2 and folder 6 (<c>c3</c>) is act 1.</summary>
    public string ChapterFolder => Campaign switch
    {
        1 => "c1",
        2 => "c1b",
        3 => "c1c",
        4 => "c2",
        5 => "c2b",
        6 => "c3",
        7 => "c4",
        8 => "c5",
        _ => "",
    };

    /// <summary>The mission subfolder name in the campaign family (<c>m01</c>..<c>m05</c>); the
    /// multiplayer and Instant Action families are other session modes' business.</summary>
    public string MissionFolder => $"m{Mission:00}";
}

/// <summary>
/// The campaign's mission order, read from the shared <c>cm_sequence.zrd</c> reader
/// (docs/formats/campaign-sequence.md): 24 flat entries, no branch, no predicate and no
/// alternates. The engine's only selection rule is "the next <c>seq</c>", which is why
/// <c>Session.Campaign.CampaignProgression</c> models a single integer position rather than a graph.
/// </summary>
public static class CampaignSequence
{
    /// <summary>How many missions the campaign has. Three independent statements agree on it: the
    /// 24 reader entries, the profile's 24-record mission-result array, and the reward table's
    /// last record landing on ordinal 24.</summary>
    public const int MissionCount = 24;

    /// <summary>Loads the sequence from a shared zrdr scope (<c>extracted/zrdr.zip</c> or its
    /// unpacked sibling), in file order. The reader is one outer element holding the records.</summary>
    public static List<CampaignMission> Load(string zrdrPath)
    {
        var missions = new List<CampaignMission>();
        var root = Zrdr.LoadFile(zrdrPath, "cm_sequence.json");
        if (root.Count == 0 || root[0] is not List<object?> records)
        {
            return missions;
        }

        foreach (var entry in records)
        {
            if (entry is List<object?> record)
            {
                missions.Add(ParseRecord(record, missions.Count));
            }
        }

        return missions;
    }

    /// <summary>The most recent earlier mission in the same world folder, or null when this is that
    /// folder's first. This is the engine's own backwards walk over the <c>campaign</c> field
    /// (docs/formats/saved-games.md, "<c>Mission.NNN</c>"): the mission whose world state a new
    /// mission loads on top of its bootstrap.</summary>
    public static CampaignMission? PreviousInSameChapter(
        IReadOnlyList<CampaignMission> missions, int seq)
    {
        int chapter = Chapter(missions, seq);
        CampaignMission? found = null;
        foreach (var m in missions)
        {
            if (m.Seq < seq && m.Campaign == chapter && (found == null || m.Seq > found.Value.Seq))
            {
                found = m;
            }
        }

        return found;
    }

    /// <summary>The world-folder number a chapter code names, the inverse of
    /// <see cref="CampaignMission.ChapterFolder"/>, or 0 for a code no chapter world carries. Also
    /// the environment digit an Instant Action pause dialog is keyed by
    /// (docs/org/pause-screen.md), which is why it is here rather than on the campaign's own
    /// row.</summary>
    public static int ChapterNumber(string chapterCode) => chapterCode.ToLowerInvariant() switch
    {
        "c1" => 1,
        "c1b" => 2,
        "c1c" => 3,
        "c2" => 4,
        "c2b" => 5,
        "c3" => 6,
        "c4" => 7,
        "c5" => 8,
        _ => 0,
    };

    /// <summary>The world-folder number a story position is stored in, or 0 when the position is
    /// not one of the sequence's own.</summary>
    public static int Chapter(IReadOnlyList<CampaignMission> missions, int seq)
    {
        foreach (var m in missions)
        {
            if (m.Seq == seq)
            {
                return m.Campaign;
            }
        }

        return 0;
    }

    // The reader defaults seq to the list position when the field is absent, which is what the
    // engine's own loader does rather than trusting list order.
    private static CampaignMission ParseRecord(List<object?> record, int position)
    {
        var d = ZrdrDict.FromAlternating(record);
        int seq = d.TryFloat("seq", out float s) ? (int)s : position;
        return new CampaignMission(
            seq,
            d.Str("desc") ?? "",
            d.TryFloat("campaign", out float c) ? (int)c : 0,
            d.TryFloat("mission", out float m) ? (int)m : 0,
            d.Str("area") ?? "",
            "true".Equals(d.Str("wingman"), StringComparison.OrdinalIgnoreCase));
    }
}
