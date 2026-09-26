using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CSVM;
using CSVM.Flight.Ai;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The named ace: which roster block is one, what names him, and where his voice comes from.
/// CM02 (<c>C3/M05</c>, "The Great British Bomber Heist") is the worked case, and its ace is
/// <c>britpeace_7</c>, Sir Charles Emmett Winthrop. Three authored things bind the block to that
/// identity and none of them is the string adjacency in the file: the roster's own <c>ace</c> flag,
/// the complete nine-value skill vector, and the mission script's <c>DEDG</c> over the block's
/// cohort. Pinned against the shipped install (docs/formats/ai-rosters.md, combat-voice.md).
/// </summary>
public class NamedAceTests
{
    // aiv slot 67, the roster's own ace flag. No production reader takes it yet, so the test names
    // the index the format table documents rather than borrowing one.
    private const int AceSlot = 67;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    private static string Cm02 => SessionPaths.MissionZrdr(TestData.DataRoot!, "C3", "M05");

    private static Messages Strings =>
        Messages.Load(Path.Combine(TestData.ExtractedRoot!, "messages.json"));

    /// <summary>The ace flag is a flag, not an inference from a full skill vector. 29 blocks carry
    /// the complete nine-value vector and only 26 of those are flagged; the three that are not are
    /// flavour pilots (two stunt planes and a Manhattan cabbie), which is exactly the distinction a
    /// vector-counting reading would lose.</summary>
    [ExtractedDataFact]
    public void TheAceFlagIsNarrowerThanACompleteSkillVector()
    {
        var flagged = new List<string>();
        var fullVector = new List<string>();
        foreach (var (chapter, mission) in Missions())
        {
            foreach (var (name, fields) in AiSkills.LoadRoster(SessionPaths.MissionZrdr(TestData.DataRoot!, chapter, mission)))
            {
                if (fields.Count > 30 && Enumerable.Range(22, 9).All(i => fields[i] is float f && f > 0f))
                {
                    fullVector.Add($"{chapter}/{mission} {name}");
                }
                if (IsAce(fields))
                {
                    flagged.Add($"{chapter}/{mission} {name}");
                }
            }
        }

        Assert.Equal(29, fullVector.Count);
        Assert.Equal(26, flagged.Count);
        Assert.Equal(
            new[] { "C2/M02 secfury_5", "C2/M02 secfury_6", "C5/M01 autogyro_1" },
            fullVector.Except(flagged).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        // Every flagged block also carries the vector, so the flag narrows the set and never widens it.
        Assert.Empty(flagged.Except(fullVector));
    }

    /// <summary>Every flagged ace authors a pilot name in slot 20 and every one of those keys
    /// resolves, so the flag and the name travel together across the whole campaign.</summary>
    [ExtractedDataFact]
    public void EveryFlaggedAceAuthorsAPilotNameThatResolves()
    {
        var strings = Strings;
        int checked_ = 0;
        foreach (var (chapter, mission) in Missions())
        {
            foreach (var (name, fields) in AiSkills.LoadRoster(SessionPaths.MissionZrdr(TestData.DataRoot!, chapter, mission)))
            {
                if (!IsAce(fields))
                {
                    continue;
                }
                string? key = AiSkills.RosterTitle(fields);
                Assert.NotNull(key);
                Assert.EndsWith("_NAME", key, StringComparison.Ordinal);
                string resolved = strings.Get(key!);
                Assert.NotEqual(key, resolved);   // an unresolved key comes back as itself
                checked_++;
            }
        }

        Assert.Equal(26, checked_);
    }

    /// <summary>CM02's ace, by name. One flagged block in the mission, and the five other British
    /// Peacemakers author no pilot name at all, so the readout has one name to tell them apart
    /// with and it is not the airframe's.</summary>
    [ExtractedDataFact]
    public void Cm02sAceIsBritpeace7AndHisNameIsSirCharlesEmmettWinthrop()
    {
        var roster = AiSkills.LoadRoster(Cm02);
        var aces = roster.Where(b => IsAce(b.Fields)).Select(b => b.Name).ToArray();
        Assert.Equal(new[] { "britpeace_7" }, aces);

        var ace = roster.Single(b => b.Name == "britpeace_7").Fields;
        Assert.Equal("MSG_SIRWINTHROP_NAME", AiSkills.RosterTitle(ace));
        Assert.Equal("Sir Charles Emmett Winthrop", Strings.Get("MSG_SIRWINTHROP_NAME"));
        Assert.Equal(2, AiSkills.RosterTeam(ace));

        foreach (var (name, fields) in roster.Where(b => b.Name.StartsWith("britpeace_", StringComparison.Ordinal)
                                                         && b.Name != "britpeace_7"))
        {
            Assert.True(AiSkills.RosterTitle(fields) == null, $"{name} authors a pilot name");
        }
    }

    /// <summary>What actually binds the block to the ace role. <c>britpeace_7</c> is the sole
    /// member of cohort 4, and the mission's secondary objective is a <c>DEDG</c> over cohort 4
    /// whose completion plays the ace's death chatter. ⚠ The five blocks that name
    /// <c>britpeace_7</c> at <c>-1.0</c> are the player's OWN flight (team 1): a <c>-1.0</c> is a
    /// hard never-target, so the mission reserves the kill for the player. It is not evidence that
    /// the ace is friendly to them.</summary>
    [ExtractedDataFact]
    public void TheAceIsBoundToTheMissionScriptByItsCohort()
    {
        var roster = AiSkills.LoadRoster(Cm02);
        var cohort4 = roster.Where(b => AiSkills.RosterGroup(b.Fields) == 4).Select(b => b.Name).ToArray();
        Assert.Equal(new[] { "britpeace_7" }, cohort4);

        var script = ObjectiveScript.Load(Cm02);
        var aceObjective = script.Objectives.Single(o => o.CompletedSoundGroup == "snd_HA5AceDead");
        Assert.Equal(4, aceObjective.Dedg!.Value.Group);
        Assert.Equal(ObjectiveClass.Secondary, aceObjective.Identity!.Value.Class);

        var excluders = roster
            .Where(b => AiSkills.RosterRatingBiases(b.Fields).Any(x => x.Pattern == "britpeace_7" && x.Bias <= -1f))
            .ToArray();
        Assert.Equal(5, excluders.Length);
        Assert.All(excluders, b => Assert.Equal(1, AiSkills.RosterTeam(b.Fields)));
    }

    /// <summary>The ace's bespoke lines are mission dialogue, not combat chatter: the wake-up
    /// objective's chain has him speak, the player's reply between his two lines.</summary>
    [ExtractedDataFact]
    public void TheAceSpeaksThroughTheMissionDialogueChain()
    {
        var chain = SoundDefs.LoadGroups(ZrdrPath)["snd_HA5Wave2"].Chains.Single();
        Assert.Equal(
            new[]
            {
                "snd_c1-HA-M5_Winthrop_13",
                "snd_c1-HA-M5_Zachary_14",
                "snd_c1-HA-M5_Winthrop_145",
            },
            chain.ToArray());

        // The chain is a real cue in the mission's own script, and the wave that releases the ace
        // is the same objective that wakes the objective playing it.
        var script = ObjectiveScript.Load(Cm02);
        Assert.Contains(script.Objectives, o => o.CompletedSoundGroup == "snd_HA5Wave2");
        Assert.Contains(script.Objectives, o => o.WakeupEnemies.Contains("britpeace_7"));

        // The cue reaches the session's prewarm set, without which it would decode to nothing once
        // the build's sound archive closes.
        Assert.Contains("snd_HA5Wave2", script.SoundGroupNames());

        // Both of his takes ship as audio, so the chain is playable rather than a def with no WAV.
        var defs = SoundDefs.Load(ZrdrPath);
        foreach (var line in chain.Where(l => l.Contains("Winthrop", StringComparison.Ordinal)))
        {
            Assert.True(HasWav(defs[line].WavName), $"{line} has no shipped WAV");
        }
    }

    /// <summary>⚠ His COMBAT voice is a different channel and it is silent in this install. Roster
    /// slot 65 gives accent 19, the accent table sends that to pilot VO id 15, and id 15 is one of
    /// the five pilots that ship a full set of clip definitions with no WAV behind any of them. The
    /// runtime binds him a voice and then resolves every line to nothing.</summary>
    [ExtractedDataFact]
    public void TheAcesCombatVoiceAccentResolvesToAPilotWithNoAudio()
    {
        var ace = AiSkills.LoadRoster(Cm02).Single(b => b.Name == "britpeace_7").Fields;
        Assert.Equal(19, AiSkills.RosterAccentId(ace));

        var voice = new CombatVoice(SoundDefs.Load(ZrdrPath), SoundDefs.LoadGroups(ZrdrPath),
            CombatVoice.LoadAccents(ZrdrPath));
        int? pilot = voice.PilotFor(19, new Random(1));
        Assert.Equal(15, pilot);

        var defs = SoundDefs.Load(ZrdrPath);
        var clips = CombatVoice.TriggerFamilies
            .SelectMany(family => voice.ClipsFor(15, family))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.NotEmpty(clips);
        Assert.All(clips, clip => Assert.False(HasWav(defs[clip].WavName), $"{clip} unexpectedly has a WAV"));
    }

    /// <summary>The seam that carries the name to the readout: the planned block's authored title
    /// survives into the spawn record the assembler resolves, which is where it used to be parsed
    /// and dropped.</summary>
    [ExtractedDataFact]
    public void TheAcesAuthoredNameReachesTheSpawnRecord()
    {
        var plan = CampaignRosterPlan.Build(
            AiSkills.LoadRoster(Cm02),
            VehicleDefs.Load(ZrdrPath),
            AiNets.Load(SessionPaths.ChapterZrdr(TestData.DataRoot!, "C3")),
            netDraw: _ => 0);
        var ace = plan.Spawns.Single(s => s.Name == "britpeace_7");
        Assert.Equal("MSG_SIRWINTHROP_NAME", ace.Title);

        var spawn = CampaignRosterPlan.SpawnFor(ace, Vector3.Zero, Vector3.Forward,
            AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward));
        Assert.Equal("MSG_SIRWINTHROP_NAME", spawn.PilotName);

        // A block with no authored name hands the assembler nothing, so the airframe title stands.
        var plain = plan.Spawns.Single(s => s.Name == "britpeace_1");
        Assert.Null(plain.Title);
        Assert.Null(CampaignRosterPlan.SpawnFor(plain, Vector3.Zero, Vector3.Forward,
            AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward)).PilotName);
    }

    private static bool IsAce(IReadOnlyList<object?> fields) =>
        fields.Count > AceSlot && fields[AceSlot] is float f && f >= 1f;

    private static IEnumerable<(string Chapter, string Mission)> Missions()
    {
        foreach (string chapter in new[] { "C1", "C1B", "C1C", "C2", "C2B", "C3", "C4", "C5" })
        {
            var dir = Path.Combine(TestData.ExtractedRoot!, chapter);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var mission in Directory.GetDirectories(dir).Select(Path.GetFileName).OrderBy(m => m, StringComparer.Ordinal))
            {
                if (mission != null && mission.StartsWith("M", StringComparison.OrdinalIgnoreCase))
                {
                    yield return (chapter, mission);
                }
            }
        }
    }

    // The sound extraction ships as a directory or as its zip; both shapes answer the same question.
    private static bool HasWav(string wavName)
    {
        string scope = SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "soundsh.zip"));
        if (Directory.Exists(scope))
        {
            return File.Exists(Path.Combine(scope, wavName));
        }
        if (!File.Exists(scope))
        {
            return false;
        }
        using var zip = ZipFile.OpenRead(scope);
        return zip.Entries.Any(e => string.Equals(e.Name, wavName, StringComparison.OrdinalIgnoreCase));
    }
}
