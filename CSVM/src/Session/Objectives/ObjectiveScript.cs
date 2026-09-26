using System;
using System.Collections.Generic;
using CSVM.Flight.Weapons;
using CSVM.Mech3;

namespace CSVM.Session.Objectives;

/// <summary>The <c>IDENTITY</c> display class: <c>PRIMARY</c> 1, <c>SECONDARY</c> 2,
/// <c>TERTIARY</c> 3, and <c>None</c> for an objective that authors no
/// <c>IDENTITY</c> and is therefore invisible to the player.</summary>
public enum ObjectiveClass
{
    /// <summary>No <c>IDENTITY</c>: pure choreography, never shown.</summary>
    None = 0,

    /// <summary>`PRIMARY`.</summary>
    Primary = 1,

    /// <summary>`SECONDARY`.</summary>
    Secondary = 2,

    /// <summary>`TERTIARY`.</summary>
    Tertiary = 3,
}

/// <summary>An <c>IDENTITY [class, priority, MSG_key]</c>. The priority is the display row key and
/// the sort key, never a weight; the message key is display text the mission logic never
/// reads.</summary>
public readonly record struct ObjectiveIdentity(ObjectiveClass Class, int Priority, string? MessageKey);

/// <summary>A <c>DEDG [group, max]</c> with its optional third generator name, whose remaining
/// launch capacity is added to the live count.</summary>
public readonly record struct DedgSpec(int Group, int Max, string? Generator);

/// <summary>One <c>ANIM { NAME [a], STATE [s] }</c> entry of an <c>ANIM_STATE</c> family. The state
/// is the anim runtime's own: <c>RUNNING</c> 2, <c>EXECUTED</c> 3, <c>INVALID</c> 4.</summary>
public readonly record struct AnimStateEntry(string Name, int State);

/// <summary>One <c>WARP_VEHICLE</c> waypoint: a position plus heading, or (when
/// <see cref="PointName"/> is set) the named point the vehicle is warped to instead.</summary>
public readonly record struct WarpPoint(float X, float Y, float Z, float Heading, string? PointName);

/// <summary>A parsed <c>TRAVELERS</c> proximity condition. <see cref="Who"/> is a node name or, when
/// <see cref="Group"/> is set, an AI group; the reference is a node name or a literal point.
/// <see cref="Count"/> is the tally the group form must reach, 1 by default.</summary>
public sealed class TravelersSpec
{
    /// <summary>The subject node name, empty on the group form.</summary>
    public string Who = string.Empty;

    /// <summary>The subject AI group, null on the node form.</summary>
    public int? Group;

    /// <summary>Whether the authored word was <c>APPROACHING</c> (inside the radius). Nothing else
    /// is authored, and any other word means outside.</summary>
    public bool Approaching = true;

    /// <summary>The reference node name, null when the reference is a literal point.</summary>
    public string? WhereNode;

    /// <summary>The literal reference point, null when the reference is a node.</summary>
    public float[]? WherePoint;

    /// <summary>Radius in meters.</summary>
    public float Radius;

    /// <summary>The tally the condition must reach.</summary>
    public int Count = 1;

    /// <summary>`DELETE_ON_SUCCESS`: the counted vehicle is deleted as the condition fires.</summary>
    public bool DeleteOnSuccess;
}

/// <summary>
/// One parsed <c>OBJECTIVEn</c> block: every directive the census found, in the typed shape
/// <see cref="ObjectiveGraph"/> runs. Directive semantics are decoded in
/// docs/formats/objectives.md; nothing here interprets them, and a directive the shipped data
/// misspells simply never lands in a field, which is what keeps it dead.
/// </summary>
public sealed class ObjectiveDef
{
    /// <summary>The 1-based block number, as written in the file.</summary>
    public int Number;

    /// <summary>Whether the block authored <c>BEGIN_DORMANT</c> at all. Absent means "starts
    /// awake".</summary>
    public bool BeginDormant;

    /// <summary>The <c>BEGIN_DORMANT</c> wake time in mission seconds; -1 never wakes on its
    /// own.</summary>
    public float DormantUntil;

    /// <summary>`BEGIN_DORMANT`'s unauthored 2nd float: seconds awake before the objective naps.</summary>
    public float? AwakeDuration;

    /// <summary>`BEGIN_DORMANT`'s unauthored 3rd float: the nap length that awake-duration uses.</summary>
    public float? NapDuration;

    /// <summary>`BEGIN_DORMANT`'s unauthored 4th float: an absolute mission-time deadline that
    /// retires the objective.</summary>
    public float? Deadline;

    /// <summary>`TICK_DEPENDS_ON_OBJ`: the 1-based objective that must be AWAKE, or 0.</summary>
    public int TickDependsOn;

    /// <summary>The <c>INACTIVEn</c> entries, each a node path (node, then named children).</summary>
    public List<List<string>> Inactive = new();

    /// <summary>`INACTIVE_COMPLETION_COUNT`, null for "all entries".</summary>
    public int? InactiveCount;

    /// <summary>The <c>ANIM_STATE</c> family's entries.</summary>
    public List<AnimStateEntry> AnimStates = new();

    /// <summary>The <c>ANIM_STATE</c> family's <c>COMPLETION_COUNT</c>, null for "all".</summary>
    public int? AnimStateCount;

    /// <summary>`DANGER_ZONES_COMPLETED` zone names.</summary>
    public List<string> DangerZones = new();

    /// <summary>`DANGER_ZONES_COMPLETION_COUNT`, null for "all".</summary>
    public int? DangerZoneCount;

    /// <summary>`DEDG`, null when unauthored.</summary>
    public DedgSpec? Dedg;

    /// <summary>`TRAVELERS`, null when unauthored.</summary>
    public TravelersSpec? Travelers;

    /// <summary>`WAKEUP_SOUND_GROUP`.</summary>
    public string? WakeSoundGroup;

    /// <summary>`COMPLETED_SOUND_GROUP`.</summary>
    public string? CompletedSoundGroup;

    /// <summary>`WAKEUP_ENEMIES` names.</summary>
    public List<string> WakeupEnemies = new();

    /// <summary>`WAKEUP_TURRETS` name patterns (a <c>*</c> matches exactly one digit).</summary>
    public List<string> WakeupTurrets = new();

    /// <summary>`WAKEUP_ZEP_TURRETS` node names.</summary>
    public List<string> WakeupZepTurrets = new();

    /// <summary>`WAKEUP_GENERATOR`: the generator and how much launch capacity to add.</summary>
    public (string Name, int Count)? WakeupGenerator;

    /// <summary>`WAKE_ANIM`: the animation and its optional AT_NODE.</summary>
    public (string Anim, string? Node)? WakeAnim;

    /// <summary>`RESET_TIMER` seconds; sets and STARTS the mission countdown on a wake out of
    /// dormancy.</summary>
    public float? ResetTimer;

    /// <summary>`SET_AI_TEAM` pairs.</summary>
    public List<(string Name, int Team)> SetAiTeam = new();

    /// <summary>`SET_AI_NET` pairs.</summary>
    public List<(string Name, string Net)> SetAiNet = new();

    /// <summary>`SET_AI_ATTACK_RADIUS` pairs.</summary>
    public List<(string Name, float Radius)> SetAiAttackRadius = new();

    /// <summary>`COMPLETED_ZEPCANNONS` pairs.</summary>
    public List<(string Zeppelin, int Flag)> CompletedZepcannons = new();

    /// <summary>`COMPLETED_STOPPOINT` triples.</summary>
    public List<(string Net, int Stop, int Flag)> CompletedStoppoint = new();

    /// <summary>`ADD_OTHER_TARGET` targets.</summary>
    public List<ObjectiveTarget> AddOtherTarget = new();

    /// <summary>`REMOVE_OTHER_TARGET` targets.</summary>
    public List<ObjectiveTarget> RemoveOtherTarget = new();

    /// <summary>`ADD_OBJECTIVE_TARGET` targets.</summary>
    public List<ObjectiveTarget> AddObjectiveTarget = new();

    /// <summary>`REMOVE_OBJECTIVE_TARGET` targets.</summary>
    public List<ObjectiveTarget> RemoveObjectiveTarget = new();

    /// <summary>`START_TAXI` names.</summary>
    public List<string> StartTaxi = new();

    /// <summary>`STOP_QUEUED_SOUNDS` names.</summary>
    public List<string> StopQueuedSounds = new();

    /// <summary>`SET_HELP_LABEL`: the targets and the message key their help label becomes.</summary>
    public (List<ObjectiveTarget> Names, string MessageKey)? HelpLabel;

    /// <summary>`WARP_VEHICLE`: the vehicle and the waypoints one is drawn from at random.</summary>
    public (string Vehicle, List<WarpPoint> Points)? Warp;

    /// <summary>`ADJUST_TIMER_WHEN_I_COMPLETE` / `TIMER_ADJUST`: <c>SET</c> or <c>ADJUST</c>.</summary>
    public (string Op, float Seconds)? AdjustTimer;

    /// <summary>`END_TIMER`: stops the mission countdown.</summary>
    public bool EndTimer;

    /// <summary>`WAKE_OBJECTIVE_WHEN_I_COMPLETE` (and its `WAKE_OBJECTIVE` alias) targets.</summary>
    public List<int> WakeWhenComplete = new();

    /// <summary>`KILL_OBJECTIVE_WHEN_I_COMPLETE` targets.</summary>
    public List<int> KillWhenComplete = new();

    /// <summary>`SLEEP_OBJECTIVE_WHEN_I_COMPLETE` targets.</summary>
    public List<int> SleepWhenComplete = new();

    /// <summary>`WAKE_OBJECTIVE_WHEN_I_SLEEP` targets.</summary>
    public List<int> WakeWhenSleep = new();

    /// <summary>`NAP_OBJECTIVE_WHEN_I_COMPLETE`: the single target and its nap seconds.</summary>
    public (int Target, float Seconds)? NapWhenComplete;

    /// <summary>`HIDE_OBJ`: marks another objective completed without running its effects.</summary>
    public int? HideObj;

    /// <summary>`IDENTITY`, null when the objective is not player-visible.</summary>
    public ObjectiveIdentity? Identity;

    /// <summary>`INSTANTWIN`.</summary>
    public bool InstantWin;

    /// <summary>`INSTANTLOSS`.</summary>
    public bool InstantLoss;

    /// <summary>`WON`: a victory condition for the all-`WON` aggregate rule.</summary>
    public bool Won;

    /// <summary>`LOST`: a defeat condition for the all-`LOST` aggregate rule.</summary>
    public bool Lost;

    /// <summary>`SLEEP_ANIM`: played when the objective is retired by a sleep.</summary>
    public string? SleepAnim;

    /// <summary>Whether the block authors any completion condition at all. One that does not
    /// completes on its first eligible tick.</summary>
    public bool HasConditions =>
        Inactive.Count > 0 || AnimStates.Count > 0 || DangerZones.Count > 0
        || Dedg != null || Travelers != null;
}

/// <summary>
/// One mission's parsed <c>objectives.zrd</c>: the file-level keys and the contiguous
/// <c>OBJECTIVEn</c> blocks. Parsing stops at the first missing number, as the original's parser
/// does, and a directive is found by EXACT name, so every misspelling the shipped data carries
/// (`WAKEUP_OBJECTIVE_WHEN_I_COMPLETE`, `SET_AI_`) lands in no field and stays dead.
/// Decode: docs/formats/objectives.md.
/// </summary>
public sealed class ObjectiveScript
{
    /// <summary>The objective blocks, in file order (index 0 is <c>OBJECTIVE1</c>).</summary>
    public List<ObjectiveDef> Objectives { get; } = new();

    /// <summary>`MISSION_TIMER`'s initial value in seconds. Every shipped file authors 0.</summary>
    public float MissionTimer { get; private set; }

    /// <summary>`MISSION_TIMER`'s `NOLOSS`: the clock pins at 0 instead of expiring.</summary>
    public bool TimerNoLoss { get; private set; }

    /// <summary>`PRIMARY_COMPLETE_SOUND`.</summary>
    public string? PrimaryCompleteSound { get; private set; }

    /// <summary>`SECONDARY_COMPLETE_SOUND`.</summary>
    public string? SecondaryCompleteSound { get; private set; }

    /// <summary>`TERTIARY_COMPLETE_SOUND`.</summary>
    public string? TertiaryCompleteSound { get; private set; }

    /// <summary>`MISSION_WON_SOUND`.</summary>
    public string? MissionWonSound { get; private set; }

    /// <summary>`MISSION_LOST_SOUND`.</summary>
    public string? MissionLostSound { get; private set; }

    /// <summary>`OBJECTIVES_WON_SOUND`, played by the all-`WON` aggregate rule.</summary>
    public string? ObjectivesWonSound { get; private set; }

    /// <summary>`OBJECTIVES_LOST_SOUND`, played by the all-`LOST` aggregate rule.</summary>
    public string? ObjectivesLostSound { get; private set; }

    /// <summary>Loads <c>objectives.zrd</c> from a mission's zrdr scope. A mission with no file (or
    /// an unreadable one) yields an empty script, which is what every Instant Action and
    /// multiplayer stub amounts to anyway.</summary>
    public static ObjectiveScript Load(string missionZrdrPath)
    {
        try
        {
            return Parse(Zrdr.LoadFileOrEmpty(missionZrdrPath, "objectives.json"));
        }
        catch (Exception e) when (e is System.IO.IOException or System.Text.Json.JsonException
            or InvalidOperationException)
        {
            return new ObjectiveScript();
        }
    }

    /// <summary>Parses an already-loaded reader root: one outer element holding the flat
    /// alternating key list.</summary>
    public static ObjectiveScript Parse(List<object?> root)
    {
        var script = new ObjectiveScript();
        if (root.Count == 0 || root[0] is not List<object?> body)
        {
            return script;
        }

        var pairs = Pairs(body);
        script.ReadFileKeys(pairs);
        // Contiguous from 1: the original's loader stops at the first missing number, so a gap
        // truncates the mission rather than skipping one block.
        for (int n = 1; ; n++)
        {
            if (Find(pairs, "OBJECTIVE" + n.ToString(System.Globalization.CultureInfo.InvariantCulture))
                is not { } block)
            {
                break;
            }

            script.Objectives.Add(ParseObjective(n, block.Value));
        }

        return script;
    }

    /// <summary>The player-visible objectives display: one entry per unique <c>IDENTITY</c>
    /// priority, in ascending priority order (<c>docs/formats/objectives.md</c>, "IDENTITY and the
    /// objectives display"). An objective authoring no <c>IDENTITY</c> is choreography and never
    /// appears. This is the whole rule, shared by every screen that writes the list: the flight
    /// check's parchment before the mission, <see cref="ObjectiveGraph.Rows"/> once it runs.</summary>
    public IReadOnlyList<ObjectiveIdentity> DisplayIdentities()
    {
        var seen = new List<ObjectiveIdentity>();
        foreach (var def in Objectives)
        {
            if (def.Identity is not { } identity)
            {
                continue;
            }

            bool duplicate = false;
            foreach (var row in seen)
            {
                duplicate |= row.Priority == identity.Priority;
            }

            if (!duplicate)
            {
                seen.Add(identity);
            }
        }

        seen.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return seen;
    }

    /// <summary>The objective with a 1-based number, or null when it is out of range. Every
    /// cross-reference in the file is by this number.</summary>
    public ObjectiveDef? ByNumber(int number) =>
        number >= 1 && number <= Objectives.Count ? Objectives[number - 1] : null;

    /// <summary>Every sound-group name a directive can hand to <c>PlaySoundGroup</c>, de-duplicated.
    /// Never referenced by the anim program, so hand this to
    /// <see cref="WorldSession.Options.ExtraPrewarmNames"/> or the cue decodes to nothing once the
    /// build's sound archive closes.</summary>
    public IReadOnlyList<string> SoundGroupNames()
    {
        var names = new List<string>();
        void Add(string? name)
        {
            if (name != null && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        Add(PrimaryCompleteSound);
        Add(SecondaryCompleteSound);
        Add(TertiaryCompleteSound);
        Add(MissionWonSound);
        Add(MissionLostSound);
        Add(ObjectivesWonSound);
        Add(ObjectivesLostSound);
        foreach (var def in Objectives)
        {
            Add(def.WakeSoundGroup);
            Add(def.CompletedSoundGroup);
        }

        return names;
    }

    // The alternating walk, duplicates preserved (ANIM_STATE repeats its ANIM key). A string
    // followed by a list is a key with a value; anything else is a bare flag, which is also what a
    // null body (C2/M01's OBJECTIVE64) parses to.
    private static List<(string Key, List<object?> Value)> Pairs(List<object?> list)
    {
        var pairs = new List<(string, List<object?>)>();
        int i = 0;
        while (i < list.Count)
        {
            if (list[i] is not string key)
            {
                i++;
                continue;
            }

            if (i + 1 < list.Count && list[i + 1] is List<object?> value)
            {
                pairs.Add((key, value));
                i += 2;
            }
            else
            {
                pairs.Add((key, new List<object?>()));
                i++;
            }
        }

        return pairs;
    }

    // Exact-name lookup, first match wins. The original recurses into nested lists, which no
    // shipped file exercises (docs/formats/objectives.md); staying at the top level is the same
    // answer on all 53 files and cannot false-match a data string.
    private static (string Key, List<object?> Value)? Find(
        List<(string Key, List<object?> Value)> pairs, string name)
    {
        foreach (var pair in pairs)
        {
            if (string.Equals(pair.Key, name, StringComparison.Ordinal))
            {
                return pair;
            }
        }

        return null;
    }

    private static bool Flag(List<(string Key, List<object?> Value)> pairs, string name) =>
        Find(pairs, name) != null;

    private static List<object?>? ValueOf(List<(string Key, List<object?> Value)> pairs, string name) =>
        Find(pairs, name)?.Value;

    private static float? Num(object? o) => o is float f ? f : null;

    private static int? Int(object? o) => o is float f ? (int)f : null;

    private static string? Str(object? o) => o as string;

    private static float? NumAt(List<object?>? list, int index) =>
        list != null && index < list.Count ? Num(list[index]) : null;

    private static string? StrAt(List<object?>? list, int index) =>
        list != null && index < list.Count ? Str(list[index]) : null;

    // Names, accepting both authored shapes: a flat list of strings, and a list of one-name lists.
    private static void ReadNames(List<object?>? list, List<string> into)
    {
        if (list == null)
        {
            return;
        }

        foreach (var item in list)
        {
            if (item is string s)
            {
                into.Add(s);
            }
            else if (item is List<object?> inner)
            {
                foreach (var sub in inner)
                {
                    if (sub is string s2)
                    {
                        into.Add(s2);
                    }
                }
            }
        }
    }

    // Targets: a string is a bare name, a nested list is one [parent, child, ...] path. The
    // shipped data authors paths three deep (C5/M01's [rfspt4, healthy, spprt]).
    private static void ReadTargets(List<object?>? list, List<ObjectiveTarget> into)
    {
        if (list == null)
        {
            return;
        }

        foreach (var item in list)
        {
            if (item is string s)
            {
                into.Add(new ObjectiveTarget(new[] { s }));
            }
            else if (item is List<object?> inner)
            {
                var path = new List<string>();
                ReadNames(inner, path);
                if (path.Count > 0)
                {
                    into.Add(new ObjectiveTarget(path));
                }
            }
        }
    }

    private static void ReadInts(List<object?>? list, List<int> into)
    {
        if (list == null)
        {
            return;
        }

        foreach (var item in list)
        {
            if (Int(item) is { } n)
            {
                into.Add(n);
            }
        }
    }

    private static ObjectiveDef ParseObjective(int number, List<object?> block)
    {
        var pairs = Pairs(block);
        var def = new ObjectiveDef { Number = number };
        ReadTiming(def, pairs);
        ReadConditions(def, pairs);
        ReadWakeActions(def, pairs);
        ReadCompletionActions(def, pairs);
        ReadChaining(def, pairs);
        ReadIdentityAndEnd(def, pairs);
        return def;
    }

    private static void ReadTiming(ObjectiveDef def, List<(string Key, List<object?> Value)> pairs)
    {
        if (ValueOf(pairs, "BEGIN_DORMANT") is { } dormant)
        {
            def.BeginDormant = true;
            def.DormantUntil = NumAt(dormant, 0) ?? 0f;
            def.AwakeDuration = NumAt(dormant, 1);
            def.NapDuration = NumAt(dormant, 2);
            def.Deadline = NumAt(dormant, 3);
        }

        def.TickDependsOn = (int)(NumAt(ValueOf(pairs, "TICK_DEPENDS_ON_OBJ"), 0) ?? 0f);
        def.SleepAnim = StrAt(ValueOf(pairs, "SLEEP_ANIM"), 0);
        if (NumAt(ValueOf(pairs, "HIDE_OBJ"), 0) is { } hide)
        {
            def.HideObj = (int)hide;
        }
    }

    private static void ReadConditions(ObjectiveDef def, List<(string Key, List<object?> Value)> pairs)
    {
        // The parser accepts INACTIVE1..INACTIVE100; the data's maximum is 18. A gap in the
        // numbering is not a stop here, only the loop bound is.
        for (int i = 1; i <= 100; i++)
        {
            if (ValueOf(pairs, "INACTIVE" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                is not { } entry)
            {
                continue;
            }

            var path = new List<string>();
            ReadNames(entry, path);
            if (path.Count > 0)
            {
                def.Inactive.Add(path);
            }
        }

        if (NumAt(ValueOf(pairs, "INACTIVE_COMPLETION_COUNT"), 0) is { } ic)
        {
            def.InactiveCount = (int)ic;
        }

        if (ValueOf(pairs, "ANIM_STATE") is { } anims)
        {
            var animPairs = Pairs(anims);
            foreach (var pair in animPairs)
            {
                if (string.Equals(pair.Key, "COMPLETION_COUNT", StringComparison.Ordinal))
                {
                    def.AnimStateCount = (int)(NumAt(pair.Value, 0) ?? 0f);
                }
                else if (string.Equals(pair.Key, "ANIM", StringComparison.Ordinal))
                {
                    var fields = Pairs(pair.Value);
                    string? name = StrAt(ValueOf(fields, "NAME"), 0);
                    if (name != null)
                    {
                        def.AnimStates.Add(new AnimStateEntry(name, AnimStateCode(StrAt(ValueOf(fields, "STATE"), 0))));
                    }
                }
            }
        }

        ReadNames(ValueOf(pairs, "DANGER_ZONES_COMPLETED"), def.DangerZones);
        if (NumAt(ValueOf(pairs, "DANGER_ZONES_COMPLETION_COUNT"), 0) is { } dz)
        {
            def.DangerZoneCount = (int)dz;
        }

        if (ValueOf(pairs, "DEDG") is { Count: >= 2 } dedg)
        {
            def.Dedg = new DedgSpec(
                (int)(Num(dedg[0]) ?? 0f), (int)(Num(dedg[1]) ?? 0f), StrAt(dedg, 2));
        }

        if (ValueOf(pairs, "TRAVELERS") is { Count: >= 4 } travelers)
        {
            def.Travelers = ParseTravelers(travelers);
        }
    }

    private static TravelersSpec ParseTravelers(List<object?> list)
    {
        var spec = new TravelersSpec();
        if (Int(list[0]) is { } group)
        {
            spec.Group = group;
        }
        else
        {
            spec.Who = Str(list[0]) ?? string.Empty;
        }

        spec.Approaching = string.Equals(Str(list[1]), "APPROACHING", StringComparison.OrdinalIgnoreCase);
        if (list[2] is List<object?> { Count: >= 3 } point)
        {
            spec.WherePoint = new[] { Num(point[0]) ?? 0f, Num(point[1]) ?? 0f, Num(point[2]) ?? 0f };
        }
        else
        {
            spec.WhereNode = Str(list[2]);
        }

        spec.Radius = Num(list[3]) ?? 0f;
        spec.Count = list.Count > 4 && Int(list[4]) is { } c ? c : 1;
        for (int i = 4; i < list.Count; i++)
        {
            if (string.Equals(Str(list[i]), "DELETE_ON_SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                spec.DeleteOnSuccess = true;
            }
        }

        return spec;
    }

    private static void ReadWakeActions(ObjectiveDef def, List<(string Key, List<object?> Value)> pairs)
    {
        ReadNames(ValueOf(pairs, "WAKEUP_ENEMIES"), def.WakeupEnemies);
        ReadNames(ValueOf(pairs, "WAKEUP_TURRETS"), def.WakeupTurrets);
        ReadNames(ValueOf(pairs, "WAKEUP_ZEP_TURRETS"), def.WakeupZepTurrets);
        def.WakeSoundGroup = StrAt(ValueOf(pairs, "WAKEUP_SOUND_GROUP"), 0);
        if (ValueOf(pairs, "WAKEUP_GENERATOR") is { Count: >= 1 } gen && Str(gen[0]) is { } genName)
        {
            def.WakeupGenerator = (genName, gen.Count > 1 ? (int)(Num(gen[1]) ?? 1f) : 1);
        }

        if (ValueOf(pairs, "WAKE_ANIM") is { Count: >= 1 } wake && Str(wake[0]) is { } animName)
        {
            // ⚠ The second argument is read only when it is a plain string: C4/M05 authors a
            // [node, child] pair there and the original ignores it, so the anim starts unanchored.
            def.WakeAnim = (animName, StrAt(wake, 1));
        }

        def.ResetTimer = NumAt(ValueOf(pairs, "RESET_TIMER"), 0);
    }

    private static void ReadCompletionActions(ObjectiveDef def, List<(string Key, List<object?> Value)> pairs)
    {
        def.CompletedSoundGroup = StrAt(ValueOf(pairs, "COMPLETED_SOUND_GROUP"), 0);
        ReadTargets(ValueOf(pairs, "ADD_OTHER_TARGET"), def.AddOtherTarget);
        ReadTargets(ValueOf(pairs, "REMOVE_OTHER_TARGET"), def.RemoveOtherTarget);
        ReadTargets(ValueOf(pairs, "ADD_OBJECTIVE_TARGET"), def.AddObjectiveTarget);
        ReadTargets(ValueOf(pairs, "REMOVE_OBJECTIVE_TARGET"), def.RemoveObjectiveTarget);
        ReadNames(ValueOf(pairs, "START_TAXI"), def.StartTaxi);
        ReadNames(ValueOf(pairs, "STOP_QUEUED_SOUNDS"), def.StopQueuedSounds);
        foreach (var (name, value) in ReadPairEntries(ValueOf(pairs, "SET_AI_TEAM")))
        {
            def.SetAiTeam.Add((name, (int)(Num(value) ?? 0f)));
        }

        foreach (var (name, value) in ReadPairEntries(ValueOf(pairs, "SET_AI_NET")))
        {
            def.SetAiNet.Add((name, Str(value) ?? string.Empty));
        }

        foreach (var (name, value) in ReadPairEntries(ValueOf(pairs, "SET_AI_ATTACK_RADIUS")))
        {
            def.SetAiAttackRadius.Add((name, Num(value) ?? 0f));
        }

        foreach (var (name, value) in ReadPairEntries(ValueOf(pairs, "COMPLETED_ZEPCANNONS")))
        {
            def.CompletedZepcannons.Add((name, (int)(Num(value) ?? 0f)));
        }

        ReadStopPoints(def, ValueOf(pairs, "COMPLETED_STOPPOINT"));
        ReadHelpLabel(def, ValueOf(pairs, "SET_HELP_LABEL"));
        ReadWarp(def, ValueOf(pairs, "WARP_VEHICLE"));
        var timer = ValueOf(pairs, "ADJUST_TIMER_WHEN_I_COMPLETE") ?? ValueOf(pairs, "TIMER_ADJUST");
        if (timer is { Count: >= 2 } && Str(timer[0]) is { } op)
        {
            def.AdjustTimer = (op, Num(timer[1]) ?? 0f);
        }

        def.EndTimer = Flag(pairs, "END_TIMER");
    }

    // The [[name, value], ...] shape SET_AI_* and COMPLETED_ZEPCANNONS share.
    private static IEnumerable<(string Name, object? Value)> ReadPairEntries(List<object?>? list)
    {
        if (list == null)
        {
            yield break;
        }

        foreach (var item in list)
        {
            if (item is List<object?> { Count: >= 2 } entry && Str(entry[0]) is { } name)
            {
                yield return (name, entry[1]);
            }
        }
    }

    private static void ReadStopPoints(ObjectiveDef def, List<object?>? list)
    {
        if (list == null)
        {
            return;
        }

        foreach (var item in list)
        {
            if (item is List<object?> { Count: >= 3 } entry && Str(entry[0]) is { } net)
            {
                def.CompletedStoppoint.Add((net, (int)(Num(entry[1]) ?? 0f), (int)(Num(entry[2]) ?? 0f)));
            }
        }
    }

    private static void ReadHelpLabel(ObjectiveDef def, List<object?>? list)
    {
        if (list is not { Count: >= 2 })
        {
            return;
        }

        // The first argument is one target: a bare name, or a path in its own list. Only the last
        // argument is the message key, so [[parent, child], key] and [name, key] both read.
        var names = new List<ObjectiveTarget>();
        ReadTargets(new List<object?> { list[0] }, names);
        if (names.Count > 0 && Str(list[^1]) is { } key)
        {
            def.HelpLabel = (names, key);
        }
    }

    private static void ReadWarp(ObjectiveDef def, List<object?>? list)
    {
        if (list is not { Count: >= 2 } || Str(list[0]) is not { } vehicle)
        {
            return;
        }

        var points = new List<WarpPoint>();
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i] is List<object?> { Count: >= 4 } p)
            {
                points.Add(new WarpPoint(
                    Num(p[0]) ?? 0f, Num(p[1]) ?? 0f, Num(p[2]) ?? 0f, Num(p[3]) ?? 0f, StrAt(p, 4)));
            }
        }

        if (points.Count > 0)
        {
            def.Warp = (vehicle, points);
        }
    }

    private static void ReadChaining(ObjectiveDef def, List<(string Key, List<object?> Value)> pairs)
    {
        ReadInts(ValueOf(pairs, "WAKE_OBJECTIVE_WHEN_I_COMPLETE") ?? ValueOf(pairs, "WAKE_OBJECTIVE"),
            def.WakeWhenComplete);
        ReadInts(ValueOf(pairs, "KILL_OBJECTIVE_WHEN_I_COMPLETE"), def.KillWhenComplete);
        ReadInts(ValueOf(pairs, "SLEEP_OBJECTIVE_WHEN_I_COMPLETE"), def.SleepWhenComplete);
        ReadInts(ValueOf(pairs, "WAKE_OBJECTIVE_WHEN_I_SLEEP"), def.WakeWhenSleep);
        if (ValueOf(pairs, "NAP_OBJECTIVE_WHEN_I_COMPLETE") is { Count: >= 1 } nap
            && Int(nap[0]) is { } target)
        {
            // No nap_time warns in the original and falls back to 0.3 s.
            def.NapWhenComplete = (target, NumAt(nap, 1) ?? 0.3f);
        }
    }

    private static void ReadIdentityAndEnd(ObjectiveDef def, List<(string Key, List<object?> Value)> pairs)
    {
        if (ValueOf(pairs, "IDENTITY") is { Count: >= 2 } identity)
        {
            def.Identity = new ObjectiveIdentity(
                ClassOf(Str(identity[0])), (int)(Num(identity[1]) ?? 0f), StrAt(identity, 2));
        }

        def.InstantWin = Flag(pairs, "INSTANTWIN");
        def.InstantLoss = Flag(pairs, "INSTANTLOSS");
        def.Won = Flag(pairs, "WON");
        def.Lost = Flag(pairs, "LOST");
    }

    private static ObjectiveClass ClassOf(string? name) => name?.ToUpperInvariant() switch
    {
        "PRIMARY" => ObjectiveClass.Primary,
        "SECONDARY" => ObjectiveClass.Secondary,
        "TERTIARY" => ObjectiveClass.Tertiary,
        _ => ObjectiveClass.None,
    };

    private static int AnimStateCode(string? name) => name?.ToUpperInvariant() switch
    {
        "RUNNING" => 2,
        "EXECUTED" => 3,
        "INVALID" => 4,
        _ => 0,
    };

    private void ReadFileKeys(List<(string Key, List<object?> Value)> pairs)
    {
        if (ValueOf(pairs, "MISSION_TIMER") is { } timer)
        {
            MissionTimer = NumAt(timer, 0) ?? 0f;
            TimerNoLoss = string.Equals(StrAt(timer, 1), "NOLOSS", StringComparison.OrdinalIgnoreCase);
        }

        PrimaryCompleteSound = StrAt(ValueOf(pairs, "PRIMARY_COMPLETE_SOUND"), 0);
        SecondaryCompleteSound = StrAt(ValueOf(pairs, "SECONDARY_COMPLETE_SOUND"), 0);
        TertiaryCompleteSound = StrAt(ValueOf(pairs, "TERTIARY_COMPLETE_SOUND"), 0);
        MissionWonSound = StrAt(ValueOf(pairs, "MISSION_WON_SOUND"), 0);
        MissionLostSound = StrAt(ValueOf(pairs, "MISSION_LOST_SOUND"), 0);
        ObjectivesWonSound = StrAt(ValueOf(pairs, "OBJECTIVES_WON_SOUND"), 0);
        ObjectivesLostSound = StrAt(ValueOf(pairs, "OBJECTIVES_LOST_SOUND"), 0);
    }
}
