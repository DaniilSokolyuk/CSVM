using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace CSVM.Mech3;

/// <summary>One <c>groupN</c> wave: how many enemies, and the militia/aircraft/skill they are
/// authored with. <c>EnemySkill</c> is decoded but read by nothing on this path, a wave's
/// actual pilot stats are rolled from a 5-row table instead, and the skill string's only real
/// effect is a global difficulty multiplier for the one spawn that used it, sourced from the
/// SETUP SCREEN rather than this key (docs/formats/instant-action.md "What novice/veteran/ace
/// becomes"). This reports the authored value verbatim, the same treatment
/// <see cref="EnemyGeneratorDef.Capacity"/> gives its own unresolved key. <c>EnemyAccentId</c>
/// IS read by the wave parser and IS this wave's voice, subject to
/// the decoded re-roll: an authored 12 (the wingman accent range's own base) becomes
/// <c>12 + rand() % 5</c> at spawn, not at parse time, <c>Session.InstantAction.InstantActionRuntime.ResolveWaveAccentId</c>.</summary>
public readonly record struct InstantActionWave(
    int NumEnemies, string EnemyName, string EnemyPlane, string EnemySkill, int EnemyAccentId);

/// <summary>Builds an <see cref="InstantActionDef"/> the three ways decision 2 names: the
/// shipped <c>ia.zrd.json</c> (<see cref="Load"/>), a hand-authored <c>--ia=&lt;path&gt;</c>
/// file (<see cref="LoadFromJson"/>), and the launchscreen's Instant Action wizard
/// (<see cref="BuildFromWizard"/>). All three converge on one record and one build path
/// (<c>BuildDef</c>); plumbing: this module's entry in docs/architecture.md.</summary>
public static class InstantAction
{
    // "The built-in defaults" (docs/formats/instant-action.md), decoded from FUN_00458ff0's
    // record reset and FUN_00458d00's per-wave reset.
    private const string DefaultMissionType = "dogfight_ace";
    private const string DefaultPlaneName = "Devastator"; // player_plane / wingman_plane / ace_plane
    private const string DefaultZeppelinNode = "vostokzep";
    private const string DefaultAceName = "Marshall Bill Redmann";
    private const string DefaultAceSkill = "veteran";
    private const int DefaultAceAccentId = -1;
    private const int DefaultAceDecal = -2;
    private const int DefaultAceColorComponent = -1;
    private const string DefaultWaveEnemyName = "Blake Firebrand";
    private const string DefaultWaveEnemyPlane = "Firebrand";
    private const string DefaultWaveEnemySkill = "veteran";
    private const int DefaultWaveAccentId = -1; // the roster-block constructor's general "unset"
    private static readonly int[] DefaultAceStats = { 5, 6, 6, 8, 9, 6, 7, 6, 9 };

    // Display name -> planes.zbd root node (docs/formats/instant-action.md "The built-in
    // defaults": the IDS_IA_PLANES order). Deliberately duplicates UI.Screens.LaunchMenu.Planes rather
    // than sharing it, which keeps the launchscreen file free of this table. CSVM ships one gamez
    // node per airframe (extracted/planes/nodes.json carries no bare "bhawk", only "player_bhawk"),
    // reused for the player and every AI/generator spawn alike, so there is no separate "plain" or
    // wingman model to pick between.
    private static readonly Dictionary<string, string> PlaneNodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Devastator"] = "player_pfighter",
        ["Bloodhawk"] = "player_bhawk",
        ["Firebrand"] = "player_fbrand",
        ["Brigand"] = "player_brigand",
        ["Fury"] = "player_fury",
        ["Autogyro"] = "player_autogyro",
        ["Hellhound"] = "player_avenger",
        ["Kestrel"] = "player_kestrel",
        ["Peacemaker"] = "player_peacemaker",
        ["Balmoral"] = "player_balmoral",
        ["Warhawk"] = "player_warhawk",
    };

    /// <summary>The built-in defaults for a wave with no <c>groupN</c> key at all
    /// (<c>FUN_00458d00</c>'s per-wave reset, read through <see cref="MakeWave"/> with nothing to
    /// overlay). What an unconfigured launchscreen wizard wave slot resolves to, so a wizard wave
    /// left at 0 enemies and a JSON file's own omitted/null <c>groupN</c> produce byte-identical
    /// <see cref="InstantActionWave"/> values, one build path for both.</summary>
    public static InstantActionWave EmptyWave => MakeWave(null, forceZero: false);

    /// <summary>What a <c>mission_type</c> is called on screen (<c>IDS_IA_MISSIONTYPE</c>, 3660),
    /// the wizard's own four labels, so the launchscreen, the load screen and the wrap-up board all
    /// name a mission the same way. An unknown key (a hand-authored <c>--ia=</c> file) reads back
    /// verbatim rather than throwing: it is a label, not a lookup anything branches on.</summary>
    public static string MissionTypeLabel(string missionType) => missionType.ToLowerInvariant() switch
    {
        "dogfight_ace" => "Dogfighting an Ace",
        "dogfight_squadron" => "Dogfighting a Squadron",
        "stunt_flying" => "Stunt Flying",
        "zeppelin_run" => "Attacking a Zeppelin",
        _ => missionType,
    };

    /// <summary>The gamez node an Instant Action display name (an
    /// <see cref="InstantActionDef.PlayerPlane"/>/<see cref="InstantActionDef.AcePlane"/> value,
    /// e.g. <c>"Bloodhawk"</c>) builds, null when the name matches none of the eleven airframes
    /// (a malformed <c>--ia=</c> file).</summary>
    public static string? PlaneNodeFor(string displayName) =>
        PlaneNodes.TryGetValue(displayName, out var node) ? node : null;

    /// <summary>Loads one chapter's shipped <c>ia.zrd.json</c> (the mission's own zrdr scope,
    /// e.g. <c>&lt;chapter&gt;/IA1/zrdr</c>, only IA1 folders carry one). Throws
    /// <see cref="FileNotFoundException"/> when the mission has no such file.</summary>
    public static InstantActionDef Load(string missionZrdrPath)
    {
        var root = Zrdr.LoadFile(missionZrdrPath, "ia.json");
        return BuildDef(ZrdrDict.FromAlternating(root));
    }

    /// <summary>Loads a hand-authored <c>--ia=&lt;path&gt;</c> file: a plain JSON object using
    /// the same field names as <c>ia.zrd.json</c> (<c>{"mission_type": "dogfight_squadron",
    /// "num_wingmen": 3, "group1": {"num_enemies": 6, …}, …}</c>), not the zrdr archive's
    /// flat-alternating list shape.</summary>
    public static InstantActionDef LoadFromJson(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"'{jsonPath}': not a JSON object");
        }
        return BuildDef(FromJsonObject(doc.RootElement));
    }

    /// <summary>Every field at its built-in default (<see cref="BuildDef"/>'s own reset, no key
    /// overlaid), an empty <c>{}</c> object read through <see cref="LoadFromJson"/> would produce
    /// the same record; this skips the JSON round-trip. The launchscreen wizard's fallback
    /// when an Instant Action environment's own <c>ia.zrd.json</c>
    /// somehow fails to load, should not happen for the seven environments Instant Action offers,
    /// kept for the same reason <c>--ia=</c>'s own load catches and warns rather than crashing.</summary>
    public static InstantActionDef Defaults() => BuildDef(ZrdrDict.FromAlternating(new List<object?>()));

    /// <summary>Builds an <see cref="InstantActionDef"/> from the launchscreen's wizard: the third
    /// of the three producers, converging on the same record. Overlays only what the wizard lets a
    /// pilot configure; everything else carries over from <paramref name="baseDef"/> unedited.
    /// ⚠ Forces wingmen and every wave's enemy count to 0 when <paramref name="missionType"/> is
    /// <c>dogfight_ace</c>, matching <see cref="BuildDef"/>'s parse-time rule.</summary>
    public static InstantActionDef BuildFromWizard(InstantActionDef baseDef, string missionType,
        string playerPlane, int numWingmen, string wingmanPlane,
        IReadOnlyList<InstantActionWave> waves, int lives)
    {
        bool ace = string.Equals(missionType, "dogfight_ace", StringComparison.OrdinalIgnoreCase);
        var resolvedWaves = new List<InstantActionWave>(4);
        for (int i = 0; i < 4; i++)
        {
            resolvedWaves.Add(ace ? EmptyWave : i < waves.Count ? waves[i] : EmptyWave);
        }

        return new InstantActionDef
        {
            MissionType = missionType,
            DisallowMissions = baseDef.DisallowMissions,
            PlayerPlane = playerPlane,
            NumWingmen = ace ? 0 : Math.Clamp(numWingmen, 0, 5),
            WingmanPlane = wingmanPlane,
            Waves = resolvedWaves,
            ZeppelinType = baseDef.ZeppelinType,
            CargoZeppelinNode = baseDef.CargoZeppelinNode,
            PassengerZeppelinNode = baseDef.PassengerZeppelinNode,
            MilitaryZeppelinNode = baseDef.MilitaryZeppelinNode,
            AceName = baseDef.AceName,
            AcePlane = baseDef.AcePlane,
            AceSkill = baseDef.AceSkill,
            AceStats = baseDef.AceStats,
            AceAccentId = baseDef.AceAccentId,
            AceLivery = baseDef.AceLivery,
            Lives = Math.Max(0, lives),
        };
    }

    private static InstantActionDef BuildDef(ZrdrDict d)
    {
        string missionType = d.Str("mission_type") ?? DefaultMissionType;
        bool ace = string.Equals(missionType, "dogfight_ace", StringComparison.OrdinalIgnoreCase);

        int numWingmen = Math.Clamp(d.TryFloat("num_wingmen", out float nw) ? (int)nw : 0, 0, 5);
        if (ace)
        {
            numWingmen = 0;
        }

        var waves = new List<InstantActionWave>(4);
        for (int i = 1; i <= 4; i++)
        {
            waves.Add(MakeWave(d.Dict($"group{i}"), ace));
        }

        return new InstantActionDef
        {
            MissionType = missionType,
            DisallowMissions = Strings(d.List("disallow_missions")),
            PlayerPlane = d.Str("player_plane") ?? DefaultPlaneName,
            NumWingmen = numWingmen,
            WingmanPlane = d.Str("wingman_plane") ?? DefaultPlaneName,
            Waves = waves,
            ZeppelinType = d.Str("zeppelin_type"),
            CargoZeppelinNode = d.Str("cargo_zeppelin") ?? DefaultZeppelinNode,
            PassengerZeppelinNode = d.Str("passenger_zeppelin") ?? DefaultZeppelinNode,
            MilitaryZeppelinNode = d.Str("military_zeppelin") ?? DefaultZeppelinNode,
            AceName = d.Str("ace_name") ?? DefaultAceName,
            AcePlane = d.Str("ace_plane") ?? DefaultPlaneName,
            AceSkill = d.Str("ace_skill") ?? DefaultAceSkill,
            AceStats = MakeAceStats(d.List("ace_stats")),
            AceAccentId = d.TryFloat("ace_accentID", out float aid) ? (int)aid : DefaultAceAccentId,
            AceLivery = MakeAceLivery(d),
            Lives = d.TryFloat("lives", out float lv) ? Math.Max(0, (int)lv) : 1,
        };
    }

    private static InstantActionWave MakeWave(ZrdrDict? g, bool forceZero)
    {
        int numEnemies = forceZero ? 0
            : Math.Clamp(g != null && g.TryFloat("num_enemies", out float n) ? (int)n : 0, 0, 6);
        return new InstantActionWave(
            numEnemies,
            g?.Str("enemy_name") ?? DefaultWaveEnemyName,
            g?.Str("enemy_plane") ?? DefaultWaveEnemyPlane,
            g?.Str("enemy_skill") ?? DefaultWaveEnemySkill,
            g != null && g.TryFloat("enemy_accentID", out float acc) ? (int)acc : DefaultWaveAccentId);
    }

    private static AiSkillVector MakeAceStats(List<object?>? list)
    {
        int? At(int i)
        {
            if (list == null)
            {
                return DefaultAceStats[i];
            }
            if (i < list.Count && list[i] is float f)
            {
                int v = (int)f;
                return v is >= 1 and <= 9 ? v : (int?)null;
            }
            return null; // an authored-but-malformed single slot, left unset, not guessed
        }

        return new AiSkillVector
        {
            DareDevil = At(0),
            NaturalTouch = At(1),
            SixthSense = At(2),
            DeadEye = At(3),
            QuickDraw = At(4),
            SteadyHand = At(5),
            StunRecovery = At(6),
            Talker = At(7),
            Constitution = At(8),
        };
    }

    private static PaintScheme? MakeAceLivery(ZrdrDict d)
    {
        string? pattern = d.Str("ace_pattern");
        if (pattern == null)
        {
            return null;
        }
        return new PaintScheme
        {
            Pattern = pattern,
            Color1 = ReadAceColor(d, "ace_color1"),
            Color2 = ReadAceColor(d, "ace_color2"),
            Color3 = ReadAceColor(d, "ace_color3"),
            NoseDecal = d.TryFloat("ace_decal1", out float d1) ? (int)d1 : DefaultAceDecal,
            TailDecal = d.TryFloat("ace_decal2", out float d2) ? (int)d2 : DefaultAceDecal,
            WingDecal = d.TryFloat("ace_decal3", out float d3) ? (int)d3 : DefaultAceDecal,
        };
    }

    private static Color ReadAceColor(ZrdrDict d, string key)
    {
        var l = d.List(key);
        if (l is { Count: >= 3 } && l[0] is float r && l[1] is float g && l[2] is float b)
        {
            return PaintScheme.FromBytes((int)r, (int)g, (int)b);
        }
        return PaintScheme.FromBytes(DefaultAceColorComponent, DefaultAceColorComponent, DefaultAceColorComponent);
    }

    private static List<string> Strings(List<object?>? list)
    {
        var result = new List<string>();
        foreach (var v in list ?? new List<object?>())
        {
            if (v is string s)
            {
                result.Add(s);
            }
        }
        return result;
    }

    // The --ia= plain-object mapping: each property becomes a "key", [values…] pair in the same
    // shape a zrd reader's alternating list already carries, so BuildDef reads both through one
    // ZrdrDict. A nested object (group1..group4) flattens to its own key/[values…] pairs the same
    // way, which is what lets ZrdrDict.Dict("group1") parse it identically either way.
    private static ZrdrDict FromJsonObject(JsonElement obj)
    {
        var list = new List<object?>();
        foreach (var prop in obj.EnumerateObject())
        {
            list.Add(prop.Name);
            list.Add(JsonToValues(prop.Value));
        }
        return ZrdrDict.FromAlternating(list);
    }

    private static List<object?> JsonToValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var flat = new List<object?>();
            foreach (var prop in value.EnumerateObject())
            {
                flat.Add(prop.Name);
                flat.Add(JsonToValues(prop.Value));
            }
            return flat;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = new List<object?>();
            foreach (var e in value.EnumerateArray())
            {
                items.Add(JsonScalar(e));
            }
            return items;
        }
        return new List<object?> { JsonScalar(value) };
    }

    private static object? JsonScalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetSingle(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}

/// <summary>One Instant Action mission, fully resolved: every optional <c>ia.zrd.json</c> key
/// filled from the original's own reset-then-overlay defaults (docs/formats/instant-action.md
/// "The built-in defaults"), so a hand-authored <c>--ia=&lt;path&gt;</c> file can omit anything
/// and still get the original's fallback. The per-field rules are on that page; plumbing is this
/// module's entry in docs/architecture.md.
/// ⚠ Leaves out <c>ground_target_name</c>/<c>ground_target_node</c>: every chapter's own
/// <c>disallow_missions</c> bars that mission type and this milestone does not implement it.</summary>
public sealed class InstantActionDef
{
    public required string MissionType { get; init; }

    public required IReadOnlyList<string> DisallowMissions { get; init; }

    /// <summary>The player's aircraft, the UI's singular display name (<c>"Bloodhawk"</c>, not
    /// <c>vehicle.json</c>'s def name), defaults to <c>"Devastator"</c> when unauthored (C2B
    /// ships neither this nor <see cref="NumWingmen"/>; the setup screen supplies both at retail
    /// runtime, which this reader does not model).</summary>
    public required string PlayerPlane { get; init; }

    /// <summary>Clamped to 0–5, and forced to 0 whenever <see cref="MissionType"/> is
    /// <c>dogfight_ace</c>, the ace duel is solo in the data, not only in the UI.</summary>
    public required int NumWingmen { get; init; }

    /// <summary>The wingmen's aircraft, the UI's singular display name, defaults to
    /// <c>"Devastator"</c> when unauthored, same as
    /// <see cref="PlayerPlane"/>/<see cref="AcePlane"/>.</summary>
    public required string WingmanPlane { get; init; }

    /// <summary>Waves 1 to 4, in order (<c>group1</c>…<c>group4</c>), always 4 entries; a wave
    /// with no <c>groupN</c> key at all (or an authored <c>null</c>) reads as the built-in
    /// per-wave defaults with 0 enemies. Every <see cref="InstantActionWave.NumEnemies"/> is
    /// forced to 0 alongside <see cref="NumWingmen"/> on <c>dogfight_ace</c>.</summary>
    public required IReadOnlyList<InstantActionWave> Waves { get; init; }

    /// <summary>Which of the three <c>*_zeppelin</c> node names below is in play
    /// (<c>cargo</c>/<c>passenger</c>/<c>military</c>); null when unauthored, the built-in
    /// defaults table gives no decoded fallback for this key, unlike the node names themselves.</summary>
    public string? ZeppelinType { get; init; }

    public required string CargoZeppelinNode { get; init; }

    public required string PassengerZeppelinNode { get; init; }

    public required string MilitaryZeppelinNode { get; init; }

    public required string AceName { get; init; }

    public required string AcePlane { get; init; }

    public required string AceSkill { get; init; }

    /// <summary>The ace's nine pilot-skill modifiers, in <c>vehicle.json</c>'s stat order, an
    /// INFERRED field order (docs/formats/spawns.md "ace_stats"): every shipped chapter carries
    /// nine 9s, so nothing in the data can settle it.</summary>
    public required AiSkillVector AceStats { get; init; }

    public required int AceAccentId { get; init; }

    /// <summary>Null only when <c>ace_pattern</c> itself is unauthored (every shipped chapter
    /// carries one; only reachable via a hand-authored <c>--ia=</c> file).</summary>
    public PaintScheme? AceLivery { get; init; }

    /// <summary>INVENTED, no <c>ia.json</c> key carries this. Default 1 is the faithful
    /// one-life run; N gives N-1 respawns on
    /// the existing 3s <c>VersusRespawnDelay</c> path; 0 is unlimited. Per pilot, not shared.</summary>
    public int Lives { get; init; } = 1;
}
