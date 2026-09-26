using System;
using System.Collections.Generic;
using System.IO;

namespace CSVM.Mech3;

/// <summary>One roster block's nine-slot skill vector, in the exe's stat order. Null = unset
/// (<c>-1</c> or omitted): the engine falls back to the airframe def's stat. The two slots the
/// designers left at 5 nearly everywhere are <c>Talker</c> and <c>Constitution</c>.</summary>
public readonly struct AiSkillVector
{
    public int? DareDevil { get; init; }

    public int? NaturalTouch { get; init; }

    public int? SixthSense { get; init; }

    public int? DeadEye { get; init; }

    public int? QuickDraw { get; init; }

    public int? SteadyHand { get; init; }

    public int? StunRecovery { get; init; }

    public int? Talker { get; init; }

    public int? Constitution { get; init; }
}

/// <summary>One roster <c>rating_biases</c> entry (slot 33, docs/formats/ai-rosters.md): a
/// node-name pattern with <c>*</c> wildcards and the bias it applies to target ranking. This
/// install authors <c>[pattern, bias]</c> pairs only (measured: 0 triples across all 53 files);
/// the exe's editor comment names a third element, so one is accepted defensively and preserved
/// raw (<see cref="Third"/>, parse it, act on nothing).</summary>
public sealed class AiRatingBias
{
    public AiRatingBias(string pattern, float bias, object? third)
    {
        Pattern = pattern;
        Bias = bias;
        Third = third;
    }

    /// <summary>The candidate-node-name pattern; <c>*</c> matches any run of characters.</summary>
    public string Pattern { get; }

    /// <summary>The authored bias; −1.0 dominates the shipped data and means NEVER target. How it
    /// resolves into a rank is <c>AiTargetRanking.ObjectiveBiasFor</c>.</summary>
    public float Bias { get; }

    /// <summary>The undecoded third element, raw. Preserved, never interpreted.</summary>
    public object? Third { get; }

    /// <summary>Case-insensitive wildcard match of <paramref name="name"/> against
    /// <see cref="Pattern"/>. Only <c>*</c> is special (any run, so <c>**</c> collapses to
    /// <c>*</c>), the shipped data authors nothing else.</summary>
    public bool Matches(string name)
    {
        string p = Pattern;
        int pi = 0, ni = 0, star = -1, mark = 0;
        while (ni < name.Length)
        {
            if (pi < p.Length && (p[pi] == '*'))
            {
                star = pi++;
                mark = ni;
            }
            else if (pi < p.Length && char.ToLowerInvariant(p[pi]) == char.ToLowerInvariant(name[ni]))
            {
                pi++;
                ni++;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                ni = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (pi < p.Length && p[pi] == '*')
            pi++;
        return pi == p.Length;
    }
}

/// <summary>
/// The AI pilot-skill constants (docs/formats/ai-rosters.md): the shared
/// <c>player.json</c>'s <c>ai_skill_parameters</c> block, one <c>[value@1, value@9]</c> endpoint
/// pair per stat, indexed by the roster's 1–9 skill ratings, plus the accessor that reads a
/// roster block's nine-slot skill vector (slots 22–30) by stat name.
/// ⚠ Interpolate linearly from rating 0, not rating 1; see <c>docs/org/aiControlLaw.md</c>
/// "The skill scalar" for the formula. <c>natural_touch</c> has no entry by design: it compares
/// directly against a maneuver's own 1–9 difficulty.
/// </summary>
public sealed class AiSkills
{
    // The roster's nine consecutive skill slots (docs/formats/ai-rosters.md "The skill vector").
    private const int SkillSlotFirst = 22;

    // Roster slot 6: primary_target, the assigned target node name ("Primary target: %s").
    private const int PrimaryTargetSlot = 6;

    // Roster slot 33: rating_biases, [pattern, bias, ?] triples feeding target ranking.
    private const int RatingBiasesSlot = 33;

    // Roster slot 34: nitro, the nitrous injector flag the spawner copies onto the vehicle.
    private const int NitroSlot = 34;

    // The spawn-facing slots a campaign roster block is placed from (docs/formats/ai-rosters.md
    // "Field table"): the net id list, the spawn pose, the side, the cohort, the display key,
    // the deactivated flag, the engagement-altitude weight, the signature mask, the taxi path
    // and the voice id.
    private const int NetIdsSlot = 0;
    private const int PositionSlot = 1;
    private const int YawSlot = 2;
    private const int TeamSlot = 3;
    private const int GroupSlot = 4;
    private const int EnabledSlot = 5;
    private const int InitHealthSlot = 7;
    private const int TitleSlot = 20;
    private const int DeactivatedSlot = 21;
    private const int PrefEngageAltSlot = 31;
    private const int SignatureSlot = 32;
    private const int TaxiPathSlot = 40;
    private const int AccentSlot = 65;
    private const int ArmorSlot = 66;

    // Roster slot 37: objectiveTarget, a strict boolean over all 414 shipped blocks (406 author 0,
    // exactly 8 author 1), not a target-node reference despite the name.
    private const int ObjectiveTargetSlot = 37;

    // Roster slot 38: categoryLabel, the label half of the marker's line 1. Exactly one shipped
    // block authors it (C2/M05's balmoral_1, MSG_BOMBER_NAME); the other 413 leave it empty.
    private const int CategoryLabelSlot = 38;

    // Roster slot 39: helpLabel, the MSG_OBJ_* key an objective-flagged block's own marker carries.
    private const int HelpLabelSlot = 39;

    // Roster slot 67: ace, read twice, the spawn exempts the block's skill ratings from the
    // difficulty offset, and the debrief credits its kill to the starred tally.
    private const int AceSlot = 67;

    private readonly Dictionary<string, (float At1, float At9)> _params =
        new(StringComparer.OrdinalIgnoreCase);

    private AiSkills()
    {
    }

    /// <summary>The raw endpoint pairs, keyed by the shipped stat-parameter names
    /// (<c>dead_eye_angle</c>, <c>quick_draw_angle</c>, …).</summary>
    public IReadOnlyDictionary<string, (float At1, float At9)> Parameters => _params;

    /// <summary>player.json's <c>min_ai_active_dist</c> (2000 m shipped), the AI activation
    /// radius, and the fallback for every roster whose own volume slots are unauthored (all of
    /// them; docs/formats/ai-rosters.md "The three unnamed slots").</summary>
    public float MinAiActiveDist { get; private set; } = 2000f;

    /// <summary>Loads the <c>ai_skill_parameters</c> block from the shared zrdr scope's
    /// <c>player.json</c>. Throws when the block is absent, the shipped install always
    /// carries it, so a miss is a wrong path, not a default to paper over.</summary>
    public static AiSkills Load(string zrdrPath)
    {
        if (Zrdr.LoadFile(zrdrPath, "player.json")[0] is not List<object?> playerList)
            throw new InvalidDataException("player.json: unexpected root shape");
        var player = ZrdrDict.FromAlternating(playerList);
        if (player.List("ai_skill_parameters") is not { } block)
            throw new InvalidDataException("player.json carries no ai_skill_parameters block");
        var skills = new AiSkills();
        for (int i = 0; i + 1 < block.Count; i += 2)
        {
            if (block[i] is string name && block[i + 1] is List<object?> { Count: >= 2 } pair
                && pair[0] is float at1 && pair[1] is float at9)
                skills._params[name] = (at1, at9);
        }
        if (player.TryFloat("min_ai_active_dist", out float activeDist))
            skills.MinAiActiveDist = activeDist;
        return skills;
    }

    /// <summary>Reads a roster block's skill vector: slots 22–30 in the exe's stat order. A slot
    /// authored <c>-1</c>, missing (blocks are not fixed-width) or non-numeric reads null =
    /// UNSET, the engine then falls back to the airframe def's own stat keys
    /// (docs/formats/vehicle.md "AI-combatant tuning"), so null must stay null here rather than
    /// become an invented default. ⚠ <c>0</c> is an authored rating and not unset: the engine's
    /// own test is <c>CMP EAX,-0x1</c>.</summary>
    public static AiSkillVector RosterSkills(IReadOnlyList<object?> fields)
    {
        int? Slot(int offset)
        {
            int idx = SkillSlotFirst + offset;
            if (idx >= fields.Count || fields[idx] is not float f)
                return null;
            int v = (int)f;
            return v >= 0 && v <= 9 ? v : null;
        }

        return new AiSkillVector
        {
            DareDevil = Slot(0),
            NaturalTouch = Slot(1),
            SixthSense = Slot(2),
            DeadEye = Slot(3),
            QuickDraw = Slot(4),
            SteadyHand = Slot(5),
            StunRecovery = Slot(6),
            Talker = Slot(7),
            Constitution = Slot(8),
        };
    }

    /// <summary>Reads a roster block's assigned target (slot 6, <c>primary_target</c>): the
    /// target node's name, or null when unset (<c>""</c> on 346 of 414 shipped blocks, or a
    /// short block omitting the slot). <c>"player"</c> names the player's aircraft.</summary>
    public static string? RosterPrimaryTarget(IReadOnlyList<object?> fields) =>
        fields.Count > PrimaryTargetSlot && fields[PrimaryTargetSlot] is string { Length: > 0 } s
            ? s
            : null;

    /// <summary>Reads a roster block's <c>nitro</c> flag (slot 34): true on the three shipped
    /// blocks that author 1, false on -1, 0, a short block or a non-numeric slot.</summary>
    public static bool RosterNitro(IReadOnlyList<object?> fields) =>
        fields.Count > NitroSlot && fields[NitroSlot] is float f && f >= 1f;

    /// <summary>Reads a roster block's <c>netids</c> (slot 0) as the list the exe's comment
    /// names: a scalar reads as one entry, a list as its numeric entries, and <c>-1</c>, an
    /// empty list, a short block or a non-numeric slot read as no net. ⚠ An empty result selects
    /// the escort behaviour on a <c>mode wingman</c> def, not "no orders" (docs/org/aiPilot.md).</summary>
    public static List<int> RosterNetIds(IReadOnlyList<object?> fields)
    {
        var ids = new List<int>();
        if (fields.Count <= NetIdsSlot)
            return ids;
        switch (fields[NetIdsSlot])
        {
            case float single when single >= 0f:
                ids.Add((int)single);
                break;
            case List<object?> list:
                foreach (var entry in list)
                {
                    if (entry is float id && id >= 0f)
                        ids.Add((int)id);
                }
                break;
            default:
                break;
        }
        return ids;
    }

    /// <summary>Reads a roster block's spawn position (slot 1, the one list-typed slot) and yaw
    /// (slot 2, degrees, the mission-data heading convention). Null when the position is not an
    /// x y z list; a missing yaw reads 0.</summary>
    public static (Godot.Vector3 Position, float YawDeg)? RosterSpawnPose(IReadOnlyList<object?> fields)
    {
        if (fields.Count <= PositionSlot || fields[PositionSlot] is not List<object?> { Count: >= 3 } p
            || p[0] is not float x || p[1] is not float y || p[2] is not float z)
            return null;
        float yaw = fields.Count > YawSlot && fields[YawSlot] is float f ? f : 0f;
        return (new Godot.Vector3(x, y, z), yaw);
    }

    /// <summary>Reads a roster block's <c>team</c> (slot 3), or null when unset.</summary>
    public static int? RosterTeam(IReadOnlyList<object?> fields) => IntSlot(fields, TeamSlot);

    /// <summary>Reads a roster block's <c>group</c> (slot 4), the mission-logic cohort id, 0
    /// when unset (the at-mission-start population).</summary>
    public static int RosterGroup(IReadOnlyList<object?> fields) => IntSlot(fields, GroupSlot) ?? 0;

    /// <summary>Reads a roster block's <c>enabled</c> flag (slot 5). Zero marks a parameter
    /// template consumed by an enemy generator, not an aircraft present at mission start.</summary>
    public static bool RosterEnabled(IReadOnlyList<object?> fields) =>
        fields.Count > EnabledSlot && fields[EnabledSlot] is float f && f >= 1f;

    /// <summary>Reads a roster block's <c>title</c> (slot 20), the <c>MSG_*_NAME</c> display key,
    /// or null when empty or unset.</summary>
    public static string? RosterTitle(IReadOnlyList<object?> fields) => StrSlot(fields, TitleSlot);

    /// <summary>Reads a roster block's <c>deactivated</c> flag (slot 21): true when authored 1,
    /// the block waiting for a <c>WAKEUP_ENEMIES</c> or a generator launch.</summary>
    public static bool RosterDeactivated(IReadOnlyList<object?> fields) =>
        fields.Count > DeactivatedSlot && fields[DeactivatedSlot] is float f && f >= 1f;

    /// <summary>Reads a roster block's <c>pref_engage_alt</c> (slot 31), metres, or null on the
    /// <c>-1.0</c> that defers to the def's <c>preferred_engagement_altitude</c>. ⚠ A maneuver
    /// selection weight, not an altitude order (docs/formats/ai-rosters.md).</summary>
    public static float? RosterPrefEngageAlt(IReadOnlyList<object?> fields) =>
        fields.Count > PrefEngageAltSlot && fields[PrefEngageAltSlot] is float f && f >= 0f ? f : null;

    /// <summary>Reads a roster block's <c>signature_maneuvers</c> bitmask (slot 32), 0 = none.
    /// <see cref="Maneuvers.SignatureNames"/> turns it into names.</summary>
    public static long RosterSignatureMask(IReadOnlyList<object?> fields) =>
        fields.Count > SignatureSlot && fields[SignatureSlot] is float f && f > 0f ? (long)f : 0L;

    /// <summary>Reads a roster block's <c>taxiPath</c> (slot 40): the authored <c>ppN</c> path
    /// name, or null on the <c>0</c> that means none (docs/formats/ai-rosters.md).</summary>
    public static string? RosterTaxiPath(IReadOnlyList<object?> fields) => StrSlot(fields, TaxiPathSlot);

    /// <summary>Reads a roster block's <c>accentID</c> (slot 65), the voice id, or null on
    /// <c>-1</c> or a short block.</summary>
    public static int? RosterAccentId(IReadOnlyList<object?> fields) => IntSlot(fields, AccentSlot);

    /// <summary>Reads a roster block's <c>init_health</c> (slot 7): the whole-vehicle health-pool
    /// override the spawn applies only when authored greater than zero
    /// (docs/org/vehicleDamage.md "Where the numbers come from at spawn"). <c>0.0</c> and
    /// <c>-1</c> both mean "use the airframe default", so both read null, the same as a short
    /// block that omits the slot.</summary>
    public static float? RosterInitHealth(IReadOnlyList<object?> fields) =>
        fields.Count > InitHealthSlot && fields[InitHealthSlot] is float f && f > 0f ? f : null;

    /// <summary>Reads a roster block's <c>armor</c> (slot 66): the whole-vehicle armour-pool
    /// override the spawn applies whenever authored zero or greater
    /// (docs/org/vehicleDamage.md "Where the numbers come from at spawn"). ⚠ The gate differs from
    /// <see cref="RosterInitHealth"/>'s: <c>0.0</c> is a real override here, only <c>-1</c> and a
    /// short block (33 of 414 stop before this slot) read null. A missing slot is not a zero.</summary>
    public static float? RosterArmor(IReadOnlyList<object?> fields) =>
        fields.Count > ArmorSlot && fields[ArmorSlot] is float f && f >= 0f ? f : null;

    /// <summary>Reads a roster block's <c>ace</c> flag (slot 67): true on the 26 blocks a mission
    /// script singles out. It exempts the block's skill ratings from the difficulty offset
    /// (<c>Flight.Hangar.Difficulty.SkillRatingForSpawn</c>) and sends its kill to the debrief's
    /// starred tally (docs/org/debrief.md#what-the-tallies-count).</summary>
    public static bool RosterAce(IReadOnlyList<object?> fields) =>
        fields.Count > AceSlot && fields[AceSlot] is float f && f >= 1f;

    /// <summary>Reads a roster block's <c>objectiveTarget</c> flag (slot 37): true on the 8
    /// shipped blocks (of 414) that author 1. The block ITSELF carries the mission's objective
    /// marker when this is set; gate <see cref="RosterHelpLabel"/> on it, since two blocks author
    /// a non-key slot 39 string with this at 0 (docs/formats/ai-rosters.md "Field table").</summary>
    public static bool RosterObjectiveTarget(IReadOnlyList<object?> fields) =>
        fields.Count > ObjectiveTargetSlot && fields[ObjectiveTargetSlot] is float f && f >= 1f;

    /// <summary>Reads a roster block's <c>helpLabel</c> (slot 39) raw: the MSG_OBJ_* key its own
    /// objective marker carries where <see cref="RosterObjectiveTarget"/> is set, or designer text
    /// (C4/M05's <c>blakepeace_3_1</c>/<c>_2</c> author <c>"Blake Aviation"</c> here with the flag
    /// at 0) otherwise. Null when empty or unset.</summary>
    public static string? RosterHelpLabel(IReadOnlyList<object?> fields) => StrSlot(fields, HelpLabelSlot);

    /// <summary>Reads a roster block's <c>categoryLabel</c> (slot 38) raw: the MSG_* key the label
    /// half of its marker's line 1 prints, beside <see cref="RosterHelpLabel"/>'s category half.
    /// Null when empty or unset, which is all but one shipped block.</summary>
    public static string? RosterCategoryLabel(IReadOnlyList<object?> fields) =>
        StrSlot(fields, CategoryLabelSlot);

    /// <summary>Reads a roster block's <c>rating_biases</c> (slot 33): the authored
    /// [pattern, bias, ?] entries in order, or an empty list when the slot is null, omitted or
    /// malformed. The third element is undecoded and kept raw on each entry.</summary>
    public static List<AiRatingBias> RosterRatingBiases(IReadOnlyList<object?> fields)
    {
        var result = new List<AiRatingBias>();
        if (fields.Count <= RatingBiasesSlot || fields[RatingBiasesSlot] is not List<object?> list)
            return result;
        foreach (var entry in list)
        {
            if (entry is List<object?> { Count: >= 2 } triple
                && triple[0] is string pattern && triple[1] is float bias)
                result.Add(new AiRatingBias(pattern, bias, triple.Count > 2 ? triple[2] : null));
        }

        return result;
    }

    /// <summary>Loads one mission's roster blocks (<c>aiv.json</c> in the mission zrdr scope):
    /// element 0 is the positional header, elements 1…N are <c>["nodename", [fields…]]</c>.
    /// Blocks are NOT fixed-width (42–81 fields ship); the fields list is returned raw so slot
    /// readers stay defensive.</summary>
    public static List<(string Name, List<object?> Fields)> LoadRoster(string missionZrdrPath)
    {
        var root = Zrdr.LoadFileOrEmpty(missionZrdrPath, "aiv.json");
        var blocks = new List<(string, List<object?>)>();
        for (int i = 1; i < root.Count; i++)
        {
            if (root[i] is List<object?> { Count: >= 2 } entry
                && entry[0] is string name && entry[1] is List<object?> fields)
                blocks.Add((name, fields));
        }
        return blocks;
    }

    /// <summary>Loads the disabled roster blocks that an enemy generator's
    /// <c>vehicle.params</c> names. The positional header carries one designer label per following
    /// block; its numeric ids are not contiguous, so block order is the join.</summary>
    public static List<(string Parameter, string Name, List<object?> Fields)> LoadGeneratorRoster(
        string missionZrdrPath)
    {
        var root = Zrdr.LoadFileOrEmpty(missionZrdrPath, "aiv.json");
        var templates = new List<(string, string, List<object?>)>();
        if (root.Count == 0 || root[0] is not List<object?> header)
            return templates;

        int blockIndex = 1;
        for (int i = 1; i + 1 < header.Count && blockIndex < root.Count; i += 2, blockIndex++)
        {
            if (header[i + 1] is not string { Length: > 0 } parameter
                || root[blockIndex] is not List<object?> { Count: >= 2 } entry
                || entry[0] is not string name || entry[1] is not List<object?> fields
                || RosterEnabled(fields))
            {
                continue;
            }
            templates.Add((parameter, name, fields));
        }
        return templates;
    }

    /// <summary>The stat parameter at a 0–9 rating: the engine's own formula, <c>lo + (hi-lo) ·
    /// rating/9</c>, the pair's endpoints sit at rating 0 and 9, not 1 and 9, so a rating of 1
    /// reads <c>lo + (hi-lo)/9</c> rather than <c>lo</c> outright. ⚠ The floor is 0, not 1: the
    /// difficulty offset reaches rating 0 (<c>Flight.Hangar.Difficulty.SkillRatingForSpawn</c>)
    /// and the engine clamps there too.</summary>
    public float At(string key, float rating)
    {
        if (!_params.TryGetValue(key, out var pair))
            throw new KeyNotFoundException($"ai_skill_parameters has no '{key}' (natural_touch has none by design)");
        float t = Math.Clamp(rating, 0f, 9f) / 9f;
        return pair.At1 + (pair.At9 - pair.At1) * t;
    }

    /// <summary>The dead-eye aim-error cone half-angle, degrees (4.0° at 1 → 1.45° at 9,
    /// tighter is the better pilot).</summary>
    public float DeadEyeAngleDeg(float rating) => At("dead_eye_angle", rating);

    /// <summary>The quick-draw shot-acceptance cone half-angle off the target's nose/tail,
    /// degrees (50° at 1 → 89° at 9, a better pilot takes more oblique shots).</summary>
    public float QuickDrawAngleDeg(float rating) => At("quick_draw_angle", rating);

    /// <summary>The per-launch ordnance dice, 0–1 (0.05 at 1 → 0.44 at 9). Despite the shared
    /// name this is not a gun term: it gates nothing but an ordnance launch.</summary>
    public float QuickDrawChance(float rating) => At("quick_draw_chance", rating);

    private static int? IntSlot(IReadOnlyList<object?> fields, int slot) =>
        fields.Count > slot && fields[slot] is float f && f >= 0f ? (int)f : null;

    private static string? StrSlot(IReadOnlyList<object?> fields, int slot) =>
        fields.Count > slot && fields[slot] is string { Length: > 0 } s ? s : null;
}

