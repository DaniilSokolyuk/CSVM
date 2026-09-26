using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Roster;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Which militia def each campaign enemy block resolves to, and the livery that def authors. The
/// spawner loads <see cref="Flight.Airframe.PlaneStats.LoadForAi"/> with the block's own def name. This table
/// therefore decides the enemy's damage model, its <c>weapons</c> fit, its pilot vector and its
/// paint (docs/org/paint.md, docs/formats/vehicle.md). The resolution is a function of the
/// mission and the block alone: nothing here reads a spawn order or a running count.
/// </summary>
public class CampaignMilitiaLiveryTests
{
    // CM01 to CM24 in story order: the storage address, then each distinct
    // "<militia def>:<paint_pattern>" the mission's enemy aircraft resolve to. Each carries how
    // many blocks take it, ordinal-ascending by def name. Surface hulls and ground vehicles are not
    // aircraft and are absent; a def whose chain authors no paint_pattern reads ":-".
    private static readonly (string Address, string Enemies)[] EnemyDefsByMission =
    {
        ("C3/M01", "medkestrel:medusas x3"),
        ("C3/M05", "britbalmoral:british x3, britpeace:british x6"),
        ("C3/M02", ""),
        ("C3/M03", "britpeace:british x4"),
        ("C3/M04", "medbrigand:medusas x5, medkestrel:medusas x5"),
        ("C1C/M01", "bsfury:blckswan x1, rusdevastator:cccp x4"),
        ("C1/M02", "blakepeace_2:blake x4, bloodhawk:- x1"),
        ("C1B/M03", "blakepeace_2:blake x4"),
        ("C1/M04", "blakebloodhawk:blake x9, blakepeace_2:blake x4"),
        ("C1/M05", "bhatbrigand_2:blackhat x6"),
        ("C2/M02", "hkfirebrand:hollywd x5, secfury:studio x6, secgyro:studio x4"),
        ("C2/M01", "hkfirebrand:hollywd x4, secfury:studio x4, secgyro:studio x4"),
        ("C2/M03", "habloodhawk:hughes x5, hafury:hughes x6"),
        ("C2B/M04", "habloodhawk:hughes x4, hakestrel:hughes x6"),
        ("C2/M05", "blakepeace:blake x3, blakepeace_2:blake x1, blakepeace_3:blake x1"),
        ("C4/M01", "bhatbrigand:blackhat x3, bhatbrigand_2:blackhat x1, stihellhound:sactrust x4"),
        ("C4/M02", "bhatbrigand:blackhat x9, bhatbrigand_2:blackhat x1, bhatbrigand_5:blackhat x1, bhatgyro:blackhat x1"),
        ("C4/M03", "bhatbrigand:blackhat x6, bhatbrigand_2:blackhat x1, bhatbrigand_5:blackhat x1, bhatwarhawk:blackhat x11, bhatwarhawk_5:blackhat x1"),
        ("C4/M04", "bhatbrigand:blackhat x4, bhatbrigand_2:blackhat x1, bhatbrigand_5:blackhat x1, bhatgyro:blackhat x3, bhatwarhawk:blackhat x5, bhatwarhawk_5:blackhat x1"),
        ("C4/M05", "bhatbrigand:blackhat x5, bhatbrigand_5:blackhat x1, bhatwarhawk:blackhat x3, bhatwarhawk_5:blackhat x1"),
        ("C5/M01", "autogyro:- x1, bhatbrigand_5:blackhat x3, bhatwarhawk_5:blackhat x3"),
        ("C5/M02", "bhatbrigand_5:blackhat x14, stihellhound_5:sactrust x4"),
        ("C5/M03", "stihellhound_5:sactrust x12"),
        ("C5/M04", "bhatwarhawk_5:blackhat x6, stihellhound_5:sactrust x8"),
    };

    // The enemy aircraft an enemy generator launches, from the same missions' disabled parameter
    // blocks. Same resolution, same table shape; missions with no such template are absent.
    private static readonly (string Address, string Templates)[] GeneratorDefsByMission =
    {
        ("C3/M03", "britpeace:british x1"),
        ("C1C/M01", "bsfury:blckswan x1, rusdevastator:cccp x1"),
        ("C1/M02", "blakepeace_2:blake x2"),
        ("C1B/M03", "rusdevastator:cccp x1"),
        ("C1/M04", "blakepeace_2:blake x1"),
        ("C2B/M04", "habloodhawk:hughes x1"),
        ("C2/M05", "blakepeace:blake x1"),
        ("C4/M05", "bhatwarhawk:blackhat x1"),
        ("C5/M03", "germanhellhound:german x1"),
        ("C5/M04", "stihellhound_5:sactrust x1"),
    };

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The campaign's own enemy set, mission by mission. Every enemy aeroplane of every
    /// shipped mission resolves the militia def its block name carries. All but two of those defs
    /// author a <c>paint_pattern</c>. The two that do not are base defs a mission fields directly,
    /// and they keep the shipped skins rather than invented colours.</summary>
    [ExtractedDataFact]
    public void EveryCampaignEnemyBlockResolvesItsMilitiaDefAndLivery()
    {
        var defs = VehicleDefs.Load(ZrdrPath);
        var patterns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missions = CampaignSequence.Load(ZrdrPath);

        Assert.Equal(EnemyDefsByMission.Length, missions.Count);
        for (int i = 0; i < missions.Count; i++)
        {
            var (chapter, folder) = Address(missions[i]);
            Assert.Equal(EnemyDefsByMission[i].Address, $"{chapter}/{folder}");
            var plan = CampaignRosterPlan.Build(
                AiSkills.LoadRoster(SessionPaths.MissionZrdr(TestData.DataRoot!, chapter, folder)),
                defs, AiNets.Load(SessionPaths.ChapterZrdr(TestData.DataRoot!, chapter)));

            Assert.Equal(EnemyDefsByMission[i].Enemies, Table(plan.Spawns, patterns));
        }
    }

    /// <summary>The same resolution on the generator half. A mission's disabled parameter blocks
    /// are planned the way its live ones are. An aircraft a generator launches carries the same
    /// militia def and livery as one standing on the roster at mission start.</summary>
    [ExtractedDataFact]
    public void EveryGeneratorTemplateResolvesItsMilitiaDefAndLivery()
    {
        var defs = VehicleDefs.Load(ZrdrPath);
        var patterns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var expected = GeneratorDefsByMission.ToDictionary(e => e.Address, e => e.Templates,
            StringComparer.OrdinalIgnoreCase);

        foreach (var mission in CampaignSequence.Load(ZrdrPath))
        {
            var (chapter, folder) = Address(mission);
            var templates = CampaignRosterPlan.GeneratorTemplates(
                SessionPaths.MissionZrdr(TestData.DataRoot!, chapter, folder),
                defs, AiNets.Load(SessionPaths.ChapterZrdr(TestData.DataRoot!, chapter)));

            string table = Table(templates.Values.ToList(), patterns);
            Assert.Equal(expected.TryGetValue($"{chapter}/{folder}", out var want) ? want : string.Empty, table);
        }
    }

    /// <summary>The two defs a campaign mission fields that author no livery at all. No colours
    /// are invented for them: the shipped data names the pattern nowhere. The Broadway Bomber is
    /// the same case one step further out, its masks shipping under no vehicle def at all. No def
    /// name below can be written for it.</summary>
    [ExtractedDataFact]
    public void TheDefsThatAuthorNoLiveryStayUnpainted()
    {
        Assert.Null(PaintScheme.ForDef(ZrdrPath, "bloodhawk"));
        Assert.Null(PaintScheme.ForDef(ZrdrPath, "autogyro"));
        Assert.Null(PaintScheme.ForDef(ZrdrPath, "balmoral"));
        Assert.DoesNotContain(PaintScheme.LoadCatalog(ZrdrPath),
            s => s.Pattern.Contains("broadway", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The livery a militia def authors is the whole scheme, not the pattern alone. The
    /// Medusa Kestrels CM01 fields carry three colours and three decals out of their own def. They
    /// are not the player militia's.</summary>
    [ExtractedDataFact]
    public void AMilitiaDefAuthorsItsColoursAndDecalsTooNotJustThePattern()
    {
        var medusa = PaintScheme.ForDef(ZrdrPath, "medkestrel");
        var fortune = PaintScheme.ForDef(ZrdrPath, "wingman");

        Assert.NotNull(medusa);
        Assert.Equal("medusas", medusa!.Pattern);
        Assert.Equal(new[] { 21, 14, 14 }, new[] { medusa.NoseDecal, medusa.TailDecal, medusa.WingDecal });
        Assert.NotEqual(medusa.Color1, medusa.Color2);
        Assert.NotNull(fortune);
        Assert.Equal(LiveryResolver.DefaultPattern, fortune!.Pattern);
        Assert.NotEqual(fortune.Pattern, medusa.Pattern);
    }

    /// <summary>Every campaign aircraft block whose own def the airframe cannot fly as its AI def,
    /// on either team. Each row carries the def its livery is read off and what that def authors.
    /// CM16's Black Swan is <c>bswingman_1</c>, and her <c>bswingman</c> def authors no
    /// paint_pattern. She flies the Fury's shipped skins, her own black and white bars. She wears
    /// neither the player's colours nor <c>bsfury</c>'s scheme, which no friendly block
    /// names.</summary>
    [ExtractedDataFact]
    public void ABlockTheAirframeCannotFlyIsPaintedOffItsOwnDef()
    {
        var defs = VehicleDefs.Load(ZrdrPath);
        var rows = new List<string>();
        foreach (var mission in CampaignSequence.Load(ZrdrPath))
        {
            var (chapter, folder) = Address(mission);
            var plan = CampaignRosterPlan.Build(
                AiSkills.LoadRoster(SessionPaths.MissionZrdr(TestData.DataRoot!, chapter, folder)),
                defs, AiNets.Load(SessionPaths.ChapterZrdr(TestData.DataRoot!, chapter)));
            foreach (var block in plan.Spawns)
            {
                if (block.LiveryDef is { } def)
                {
                    string pattern = PaintScheme.ForDef(ZrdrPath, def)?.Pattern ?? "-";
                    rows.Add($"{chapter}/{folder} {block.Name} team {block.Team}: {def}:{pattern}");
                }
            }
        }

        Assert.Equal(new[]
        {
            "C4/M01 bswingman_1 team 1: bswingman:-",
            "C4/M02 bswingman_1 team 1: bswingman:-",
            "C4/M03 bswingman_1 team 1: bswingman:-",
        }, rows);
    }

    /// <summary>CM16's and CM19's player-side blocks and the def each is painted off. The Black
    /// Swan in CM16 wears her def's, which has no pattern, so the shipped skins. Every Fortune
    /// Hunters wingman in CM19 wears <c>player_fortune</c>, the player's own scheme. CM19 fields no
    /// aeroplane of hers: she is rescued as the <c>p_swan</c> passenger.</summary>
    [ExtractedDataFact]
    public void TheBlackSwanAndTheFortuneHuntersWingmenWearTheirOwnDefsLivery()
    {
        var defs = VehicleDefs.Load(ZrdrPath);
        foreach (var (address, want) in new[]
                 {
                     ("C4/M01", "bswingman_1=bswingman:-"),
                     ("C4/M04", "wingman_4=wingman:player_fortune, devastator_1=devastator:player_fortune, "
                         + "wingman_2=wingman:player_fortune, devastator_2=devastator:player_fortune, "
                         + "wingman_3=wingman:player_fortune"),
                 })
        {
            var parts = address.Split('/');
            var plan = CampaignRosterPlan.Build(
                AiSkills.LoadRoster(SessionPaths.MissionZrdr(TestData.DataRoot!, parts[0], parts[1])),
                defs, AiNets.Load(SessionPaths.ChapterZrdr(TestData.DataRoot!, parts[0])));
            var friends = plan.Spawns
                .Where(p => !p.Surface && p.Team == AimAssist.PlayerTeam)
                .Select(p =>
                {
                    string def = p.LiveryDef ?? p.AiDef ?? "-";
                    return $"{p.Name}={def}:{PaintScheme.ForDef(ZrdrPath, def)?.Pattern ?? "-"}";
                });
            Assert.Equal(want, string.Join(", ", friends));
        }
    }

    // "<def>:<pattern> xN" per distinct militia def among a plan list's enemy aircraft, comma
    // separated in ordinal def order. Surface hulls carry no airframe and no livery, so they are
    // left out. So is everything on the player's own team, which draws the default pattern.
    private static string Table(IReadOnlyList<RosterSpawnPlan> plans, Dictionary<string, string> patterns)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            if (plan.Surface || plan.Team == AimAssist.PlayerTeam || plan.AiDef is not { } def)
            {
                continue;
            }

            if (!patterns.TryGetValue(def, out var pattern))
            {
                pattern = PaintScheme.ForDef(ZrdrPath, def)?.Pattern ?? "-";
                patterns[def] = pattern;
            }

            string key = $"{def}:{pattern}";
            counts[key] = counts.TryGetValue(key, out int seen) ? seen + 1 : 1;
        }
        return string.Join(", ", counts.Select(e => $"{e.Key} x{e.Value}"));
    }

    private static (string Chapter, string Folder) Address(CampaignMission mission) =>
        (mission.ChapterFolder.ToUpperInvariant(), mission.MissionFolder.ToUpperInvariant());
}
