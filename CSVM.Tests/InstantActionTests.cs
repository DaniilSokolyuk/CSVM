using System;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Instant Action reader (docs/formats/instant-action.md):
/// fixture units for the full record (including D9's <c>WingmanPlane</c> and E11's
/// <c>InstantActionWave.EnemyAccentId</c>), the built-in defaults every optional key falls back
/// to (matching the original's own <c>FUN_00458ff0</c>/<c>FUN_00459390</c> reset-then-overlay),
/// the <c>dogfight_ace</c> zero-forcing rule, and the <c>--ia=</c> plain-JSON-object path, plus
/// golden counts over the install's 8 shipped chapters so a reader change moves a test instead of
/// silently drifting.
/// </summary>
public class InstantActionTests
{
    private static readonly string[] Chapters =
        { "C1", "C1B", "C1C", "C2", "C2B", "C3", "C4", "C5" };

    [Fact]
    public void TheFullRecordParsesFromTheFixture()
    {
        var def = InstantAction.Load(TestData.Fixture("ia"));

        Assert.Equal("dogfight_squadron", def.MissionType);
        Assert.Equal(new[] { "ground_target" }, def.DisallowMissions);
        Assert.Equal("Bloodhawk", def.PlayerPlane);
        Assert.Equal(4, def.NumWingmen);
        Assert.Equal("Warhawk", def.WingmanPlane);
        Assert.Equal("cargo", def.ZeppelinType);
        Assert.Equal("probezep", def.CargoZeppelinNode);
        Assert.Equal("probezep", def.PassengerZeppelinNode);
        Assert.Equal("probezep", def.MilitaryZeppelinNode);

        Assert.Equal(4, def.Waves.Count);
        // Clamped to 6, the fixture authors 9.
        Assert.Equal(new InstantActionWave(6, "MSG_PROBE_GROUP1", "Firebrand", "novice", 27), def.Waves[0]);
        // group2 authors no enemy_accentID, the built-in -1 default.
        Assert.Equal(new InstantActionWave(6, "MSG_PROBE_GROUP2", "Fury", "veteran", -1), def.Waves[1]);
        // group3 authored as a bare "group3", null, reads as the empty/default wave.
        Assert.Equal(new InstantActionWave(0, "Blake Firebrand", "Firebrand", "veteran", -1), def.Waves[2]);
        // group4 authors the wingman accent range's own base (12), stored verbatim; the re-roll
        // (12 + rand() % 5) is a spawn-time policy (InstantActionRuntime), not a parse-time one.
        Assert.Equal(new InstantActionWave(2, "MSG_PROBE_GROUP4", "Warhawk", "ace", 12), def.Waves[3]);

        Assert.Equal("MSG_PROBE_ACE_NAME", def.AceName);
        Assert.Equal("Peacemaker", def.AcePlane);
        Assert.Equal("ace", def.AceSkill);
        Assert.Equal(24, def.AceAccentId);
        Assert.Equal(9, def.AceStats.DareDevil);
        Assert.Equal(9, def.AceStats.Constitution);

        Assert.NotNull(def.AceLivery);
        var livery = def.AceLivery!;
        Assert.Equal("probepattern", livery.Pattern);
        Assert.Equal(21, livery.NoseDecal);
        Assert.Equal(3, livery.TailDecal);
        Assert.Equal(3, livery.WingDecal);
        Assert.Equal(PaintScheme.FromBytes(149, 163, 195), livery.Color1);
        Assert.Equal(PaintScheme.FromBytes(89, 114, 159), livery.Color2);
        Assert.Equal(PaintScheme.FromBytes(233, 228, 240), livery.Color3);

        Assert.Equal(3, def.Lives);
    }

    [Fact]
    public void EveryOptionalKeyFallsBackToTheOriginalsOwnDefault()
    {
        // dogfight_ace with a group1 authoring 6 enemies: the mission-type zero-forcing rule
        // must win over the authored count, exactly as the parser's own reset does.
        var def = InstantAction.Load(TestData.Fixture("ia-minimal"));

        Assert.Equal("dogfight_ace", def.MissionType);
        Assert.Empty(def.DisallowMissions);
        Assert.Equal("Devastator", def.PlayerPlane);   // player_plane/wingman_plane/ace_plane default
        Assert.Equal(0, def.NumWingmen);                // forced to 0 on dogfight_ace
        Assert.Equal("Devastator", def.WingmanPlane);
        Assert.Null(def.ZeppelinType);                  // no decoded default for this key
        Assert.Equal("vostokzep", def.CargoZeppelinNode);
        Assert.Equal("vostokzep", def.PassengerZeppelinNode);
        Assert.Equal("vostokzep", def.MilitaryZeppelinNode);

        Assert.Equal(4, def.Waves.Count);
        foreach (var wave in def.Waves)
        {
            // Every wave count is forced to 0 on dogfight_ace, group1's authored 6 included.
            Assert.Equal(0, wave.NumEnemies);
        }
        Assert.Equal("MSG_PROBE_G1", def.Waves[0].EnemyName); // the authored fields still read
        Assert.Equal(new InstantActionWave(0, "Blake Firebrand", "Firebrand", "veteran", -1), def.Waves[1]);

        Assert.Equal("Marshall Bill Redmann", def.AceName);
        Assert.Equal("Devastator", def.AcePlane);
        Assert.Equal("veteran", def.AceSkill);
        Assert.Equal(-1, def.AceAccentId);
        Assert.Equal(5, def.AceStats.DareDevil);
        Assert.Equal(6, def.AceStats.NaturalTouch);
        Assert.Equal(9, def.AceStats.QuickDraw);
        Assert.Null(def.AceLivery); // no ace_pattern authored, nothing to paint

        Assert.Equal(1, def.Lives); // the INVENTED field's own default
    }

    [Fact]
    public void ANonAceMissionKeepsItsAuthoredWaveCounts()
    {
        // The zero-forcing rule is keyed on the MISSION TYPE, not on absence, a non-ace mission
        // with an authored wave keeps it even when num_wingmen/player_plane are unauthored (C2B's
        // own shape).
        var def = InstantAction.LoadFromJson(TestData.Fixture("ia-cli.json"));
        Assert.Equal("zeppelin_run", def.MissionType);
        Assert.Equal(3, def.Waves[0].NumEnemies);
    }

    [Fact]
    public void TheCliJsonObjectParsesThroughTheSameFieldNames()
    {
        var def = InstantAction.LoadFromJson(TestData.Fixture("ia-cli.json"));

        Assert.Equal("zeppelin_run", def.MissionType);
        Assert.Equal(new[] { "ground_target", "stunt_flying" }, def.DisallowMissions);
        Assert.Equal("Kestrel", def.PlayerPlane);
        Assert.Equal(2, def.NumWingmen);
        Assert.Equal("Devastator", def.WingmanPlane); // unauthored in this fixture, the default
        Assert.Equal("cargo", def.ZeppelinType);
        Assert.Equal("probezep", def.CargoZeppelinNode);

        Assert.Equal(new InstantActionWave(3, "MSG_PROBE_CLI_G1", "Brigand", "veteran", 18), def.Waves[0]);
        // group2 authored as a JSON null, same graceful default as a bare zrd flag.
        Assert.Equal(new InstantActionWave(0, "Blake Firebrand", "Firebrand", "veteran", -1), def.Waves[1]);
        // group3/group4 not authored at all.
        Assert.Equal(new InstantActionWave(0, "Blake Firebrand", "Firebrand", "veteran", -1), def.Waves[2]);

        Assert.Equal("MSG_PROBE_CLI_ACE", def.AceName);
        Assert.Equal("Hellhound", def.AcePlane);
        Assert.Equal(30, def.AceAccentId);
        Assert.NotNull(def.AceLivery);
        var livery = def.AceLivery!;
        Assert.Equal("probepattern", livery.Pattern);
        Assert.Equal(5, livery.NoseDecal);
        Assert.Equal(PaintScheme.FromBytes(10, 20, 30), livery.Color1);

        Assert.Equal(2, def.Lives);
    }

    [Fact]
    public void LoadFromJsonRejectsANonObjectRoot()
    {
        var path = TestData.TempDir();
        var file = System.IO.Path.Combine(path, "not-an-object.json");
        System.IO.File.WriteAllText(file, "[1, 2, 3]");
        Assert.Throws<System.IO.InvalidDataException>(() => InstantAction.LoadFromJson(file));
    }

    // ---- The wizard's own build path --------------------------------------------------------

    /// <summary>H16's own "one build path" check: a wizard-built def and the equivalent hand-authored
    /// <c>--ia=</c> JSON, same mission type/player plane/wingmen/waves/lives, same environment
    /// (the "ia" fixture, whose ace/zeppelin/disallow_missions <see cref="TheFullRecordParsesFromTheFixture"/>
    /// already pins), produce byte-identical <see cref="InstantActionDef"/> field values. This is
    /// what makes the wizard and <c>--ia=</c> converge on one record rather than two similar ones.</summary>
    [Fact]
    public void BuildFromWizardConvergesWithAnEquivalentIaJsonFile()
    {
        var baseDef = InstantAction.Load(TestData.Fixture("ia"));
        var waves = new[]
        {
            new InstantActionWave(4, "Black Hat Warhawk", "Warhawk", "veteran", -1),
            InstantAction.EmptyWave,
            InstantAction.EmptyWave,
            InstantAction.EmptyWave,
        };
        var wizardDef = InstantAction.BuildFromWizard(baseDef, "dogfight_squadron", "Kestrel", 2, "Fury", waves, 2);

        var path = System.IO.Path.Combine(TestData.TempDir(), "ia-wizard-equivalent.json");
        System.IO.File.WriteAllText(path, """
        {
          "mission_type": "dogfight_squadron",
          "disallow_missions": ["ground_target"],
          "player_plane": "Kestrel",
          "num_wingmen": 2,
          "wingman_plane": "Fury",
          "group1": {"num_enemies": 4, "enemy_name": "Black Hat Warhawk", "enemy_plane": "Warhawk", "enemy_skill": "veteran"},
          "zeppelin_type": "cargo",
          "cargo_zeppelin": "probezep",
          "passenger_zeppelin": "probezep",
          "military_zeppelin": "probezep",
          "ace_name": "MSG_PROBE_ACE_NAME",
          "ace_plane": "Peacemaker",
          "ace_skill": "ace",
          "ace_stats": [9, 9, 9, 9, 9, 9, 9, 9, 9],
          "ace_accentID": 24,
          "ace_pattern": "probepattern",
          "ace_decal1": 21,
          "ace_decal2": 3,
          "ace_decal3": 3,
          "ace_color1": [149, 163, 195],
          "ace_color2": [89, 114, 159],
          "ace_color3": [233, 228, 240],
          "lives": 2
        }
        """);
        var jsonDef = InstantAction.LoadFromJson(path);

        Assert.Equal(jsonDef.MissionType, wizardDef.MissionType);
        Assert.Equal(jsonDef.DisallowMissions, wizardDef.DisallowMissions);
        Assert.Equal(jsonDef.PlayerPlane, wizardDef.PlayerPlane);
        Assert.Equal(jsonDef.NumWingmen, wizardDef.NumWingmen);
        Assert.Equal(jsonDef.WingmanPlane, wizardDef.WingmanPlane);
        Assert.Equal(jsonDef.Waves, wizardDef.Waves);
        Assert.Equal(jsonDef.ZeppelinType, wizardDef.ZeppelinType);
        Assert.Equal(jsonDef.CargoZeppelinNode, wizardDef.CargoZeppelinNode);
        Assert.Equal(jsonDef.PassengerZeppelinNode, wizardDef.PassengerZeppelinNode);
        Assert.Equal(jsonDef.MilitaryZeppelinNode, wizardDef.MilitaryZeppelinNode);
        Assert.Equal(jsonDef.AceName, wizardDef.AceName);
        Assert.Equal(jsonDef.AcePlane, wizardDef.AcePlane);
        Assert.Equal(jsonDef.AceSkill, wizardDef.AceSkill);
        Assert.Equal(jsonDef.AceAccentId, wizardDef.AceAccentId);
        Assert.Equal(jsonDef.AceStats, wizardDef.AceStats);
        Assert.NotNull(jsonDef.AceLivery);
        Assert.NotNull(wizardDef.AceLivery);
        Assert.Equal(jsonDef.AceLivery!.Pattern, wizardDef.AceLivery!.Pattern);
        Assert.Equal(jsonDef.AceLivery!.Color1, wizardDef.AceLivery!.Color1);
        Assert.Equal(jsonDef.AceLivery!.Color2, wizardDef.AceLivery!.Color2);
        Assert.Equal(jsonDef.AceLivery!.Color3, wizardDef.AceLivery!.Color3);
        Assert.Equal(jsonDef.AceLivery!.NoseDecal, wizardDef.AceLivery!.NoseDecal);
        Assert.Equal(jsonDef.AceLivery!.TailDecal, wizardDef.AceLivery!.TailDecal);
        Assert.Equal(jsonDef.AceLivery!.WingDecal, wizardDef.AceLivery!.WingDecal);
        Assert.Equal(jsonDef.Lives, wizardDef.Lives);
    }

    /// <summary>Dogfighting an Ace forces the wingman count and every wave's enemy count to 0,
    /// the same rule <c>BuildDef</c> applies reading the file, even when the wizard state handed
    /// in still carries configured wingmen/waves (stale state from before the pilot switched mission
    /// types), so a solo-breaking def can never reach the runtime whichever producer built it.</summary>
    [Fact]
    public void BuildFromWizardForcesTheAceDuelSoloEvenWithStaleWizardState()
    {
        var baseDef = InstantAction.Load(TestData.Fixture("ia"));
        var waves = new[]
        {
            new InstantActionWave(6, "Some Militia Warhawk", "Warhawk", "ace", -1),
            InstantAction.EmptyWave,
            InstantAction.EmptyWave,
            InstantAction.EmptyWave,
        };
        var def = InstantAction.BuildFromWizard(baseDef, "dogfight_ace", "Devastator", 3, "Fury", waves, 1);

        Assert.Equal(0, def.NumWingmen);
        foreach (var wave in def.Waves)
        {
            Assert.Equal(InstantAction.EmptyWave, wave);
        }
    }

    /// <summary>An unconfigured wizard wave slot (0 enemies) and a JSON file's own omitted
    /// <c>groupN</c> key resolve to the exact same <see cref="InstantActionWave"/>, regardless of
    /// what the wizard's militia/aircraft/skill cursors happen to be sitting on, they are not
    /// "configured" until a pilot actually raises the count above 0.</summary>
    [Fact]
    public void AnUnconfiguredWaveMatchesAnOmittedGroupNRegardlessOfCursorPosition()
    {
        var fromCursors = UI.Screens.LaunchMenu.WaveFor(0, militiaIndex: 7, aircraftIndex: 1, skillIndex: 2);
        Assert.Equal(InstantAction.EmptyWave, fromCursors);

        var fromFile = InstantAction.Load(TestData.Fixture("ia-minimal")).Waves[1]; // group2: unauthored
        Assert.Equal(InstantAction.EmptyWave, fromFile);
    }

    // ---- Goldens over the install ---------------------------------------------------------------

    [ExtractedDataFact]
    public void TheInstallWideCensusMatchesTheScopingTable()
    {
        // docs/formats/spawns.md's "Environment → chapter" / mission_type census, re-read this
        // session directly from every chapter's ia.zrd.json.
        var expected = new (string Chapter, string MissionType, string PlayerPlane, int NumWingmen,
            int[] Waves, string AcePlane, string AcePattern, int AceAccentId)[]
        {
            ("C1", "dogfight_squadron", "Bloodhawk", 3, new[] { 6, 5, 4, 3 }, "Peacemaker", "blake", 24),
            ("C1B", "stunt_flying", "Firebrand", 3, new[] { 4, 3, 3, 2 }, "Fury", "blckswan", 23),
            ("C1C", "zeppelin_run", "Fury", 3, new[] { 4, 3, 3, 2 }, "Firebrand", "hollywd", 26),
            ("C2", "stunt_flying", "Brigand", 3, new[] { 4, 4, 3, 2 }, "Bloodhawk", "hughes", 25),
            // C2B ships neither player_plane nor num_wingmen, both read the original's own default.
            ("C2B", "zeppelin_run", "Devastator", 0, new[] { 6, 5, 4, 3 }, "Devastator", "hollywd", 26),
            ("C3", "dogfight_squadron", "Hellhound", 3, new[] { 6, 5, 4, 3 }, "Kestrel", "medusas", 17),
            ("C4", "stunt_flying", "Peacemaker", 3, new[] { 6, 5, 4, 3 }, "Bloodhawk", "hughes", 31),
            ("C5", "stunt_flying", "Kestrel", 3, new[] { 6, 5, 4, 3 }, "Peacemaker", "broadway", 33),
        };
        Assert.Equal(Chapters.Length, expected.Length);

        foreach (var row in expected)
        {
            var def = InstantAction.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, row.Chapter, "IA1"));
            Assert.Equal(row.MissionType, def.MissionType);
            Assert.Equal(row.PlayerPlane, def.PlayerPlane);
            Assert.Equal(row.NumWingmen, def.NumWingmen);
            // wingman_plane is authored by no shipped chapter (docs/formats/instant-action.md
            // "Keys parsed but never authored"), so every chapter reads the built-in default.
            Assert.Equal("Devastator", def.WingmanPlane);
            Assert.Equal(4, def.Waves.Count);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(row.Waves[i], def.Waves[i].NumEnemies);
            }
            Assert.Equal(row.AcePlane, def.AcePlane);
            Assert.Equal(row.AceAccentId, def.AceAccentId);
            Assert.NotNull(def.AceLivery);
            Assert.Equal(row.AcePattern, def.AceLivery!.Pattern);
            // ace_stats is [9,9,9,9,9,9,9,9,9] in all 8 shipped chapters.
            Assert.Equal(9, def.AceStats.DareDevil);
            Assert.Equal(9, def.AceStats.Constitution);
            // None of the 8 chapters' own mission_type is dogfight_ace, so the zero-forcing rule
            // never fires on shipped data, only on a --ia= or wizard-built ace mission.
            Assert.NotEqual("dogfight_ace", def.MissionType);
        }
    }
}
