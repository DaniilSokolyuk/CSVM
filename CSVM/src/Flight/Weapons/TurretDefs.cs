using System;
using System.Collections.Generic;
using CSVM.Mech3;

namespace CSVM.Flight.Weapons;

/// <summary>One <c>TURRET</c> entry from the shared <c>zrdr/ai.zrd</c>, the gunner's whole
/// behavioural spec (docs/formats/turrets.md). Two families: carried entries
/// (<see cref="Carried"/>, looked up by <see cref="Title"/> from a host vehicle's
/// <c>turrets</c> block) and standalone world emplacements placed at <see cref="NodePatterns"/>.
/// Angle limits are degrees; an absent or min==max arc is UNRESTRICTED, never locked, the
/// engine's clamp is gated on <c>min != max</c>.</summary>
public sealed class TurretDef
{
    /// <summary>The loader's default for an absent <c>TEAM</c> key: the original's FIRST ENEMY
    /// team (its team space is 0 = neutral, 1 = ally, 2+ = enemy; the loader writes enemy team
    /// index 0 = id 2 when the key is missing). This is why the 22 no-TEAM world emplacements
    /// are hostile, and the four authored <c>TEAM 1</c> entries (the piratezep set) are the
    /// player's OWN zeppelin's defensive turrets, allied on purpose.</summary>
    public const int DefaultTeamId = 2;

    /// <summary>The <c>MSG_TUR_*</c> string key, and the lookup name for carried turrets. Null
    /// never ships, but the engine's by-title lookup accepts a titleless entry unconditionally,
    /// see <see cref="MatchesTitle"/>.</summary>
    public string? Title;

    /// <summary><c>CREATE_STANDALONE</c> present (always authored 0): the entry is excluded from
    /// the world placement pass and built by a host's by-title lookup instead.</summary>
    public bool Carried;

    /// <summary>Initial awake state. All 16 carried entries ship 1; 22 of the 26 standalone
    /// entries ship 0 and wait for a mission script.</summary>
    public bool Activated;

    /// <summary>Authored team id (always 1 = ally where present; absent =
    /// <see cref="DefaultTeamId"/>). A carried turret takes its HOST's team at runtime; this
    /// matters for the standalone family.</summary>
    public int? Team;

    /// <summary>The host node whose destruction kills the turret (carried family).</summary>
    public string? HealthyNode;

    /// <summary><c>PARTS[0]</c> of the 3-element form, the traverse ring yaw is written to.
    /// Null on the 2-element form, where one node takes the combined rotation.</summary>
    public string? YawNode;

    /// <summary>The elevation node (<c>PARTS[1]</c>, or <c>PARTS[0]</c> of the 2-element form).</summary>
    public string PitchNode = "";

    /// <summary>The muzzle node(s). Multiple firepoints cycle round-robin, one per shot.</summary>
    public IReadOnlyList<string> Firepoints = Array.Empty<string>();

    /// <summary>Standalone placement: name patterns resolved against the scene graph, each inner
    /// list one path of wildcarded segments (<c>[["piratezep","ctur*"]]</c>).</summary>
    public IReadOnlyList<IReadOnlyList<string>> NodePatterns = Array.Empty<IReadOnlyList<string>>();

    /// <summary>Standalone-family hit points; carried turrets die with their <see cref="HealthyNode"/>.</summary>
    public float? Health;

    /// <summary><c>WEAPON.NAME</c>, a BALLISTICS id (<c>wep_140</c>…), resolvable in weapons.zrd.
    /// Not a display name: <c>30slug</c> is a different, inner field.</summary>
    public string WeaponName = "";

    /// <summary><c>WEAPON.AMMO</c>, 9999 or 12000 shipped; real state, effectively unlimited.</summary>
    public int Ammo;

    /// <summary><c>WEAPON.FIRE_RATE</c>, seconds between shots, redrawn uniform(min,max) after
    /// each one. A scalar authors min == max.</summary>
    public float FireRateMin, FireRateMax;

    /// <summary><c>WEAPON.DETECTION_RANGE</c>, metres, the target search field.</summary>
    public float DetectionRange;

    /// <summary>Half-angle of the shot-scatter cone, degrees. Perturbs the SHOT after the aim
    /// solution, not the barrel, the turret aims true and the rounds spread.</summary>
    public float InaccuracyDeg;

    /// <summary>Elevation arc, degrees, or null when the key is absent (unrestricted). min == max
    /// is also unrestricted.</summary>
    public float? PitchMinDeg, PitchMaxDeg;

    /// <summary>Traverse arc, degrees, a DIRECTED interval ([105,255] and [-155,-5] are
    /// different arcs). Null when absent; ⚠ [0,0] (or any min == max) removes the limit, it does
    /// not lock the turret.</summary>
    public float? YawMinDeg, YawMaxDeg;

    /// <summary>How long a firing spell lasts, seconds, redrawn uniform(min,max) per window.</summary>
    public float AttackMin, AttackMax;

    /// <summary>How long the pause between spells lasts. Bored suppresses firing ONLY, the
    /// turret keeps tracking through it.</summary>
    public float BoredMin, BoredMax;

    /// <summary><c>SOUNDS.CANNON</c> (always <c>snd_chaingun</c> where authored).</summary>
    public string? CannonSound;

    /// <summary>The team this emplacement fights on: the authored value, else the enemy default.
    /// There is no authored-to-runtime conversion, here or anywhere, the original stores one
    /// integer per combat object and compares two of them raw
    /// (docs/org/targeting.md "The team space"). An emplacement built by its constructor takes the
    /// same id 2 an Instant Action enemy aircraft carries, which is why the original's zeppelin
    /// turrets do not engage the wave that zeppelin launched.</summary>
    public int TeamId => Team ?? DefaultTeamId;

    /// <summary>Whether the yaw axis is actually limited: the engine clamps only when both
    /// limits exist and differ.</summary>
    public bool YawRestricted => YawMinDeg is { } lo && YawMaxDeg is { } hi && lo != hi;

    /// <summary>Whether the pitch axis is actually limited (same min != max gate).</summary>
    public bool PitchRestricted => PitchMinDeg is { } lo && PitchMaxDeg is { } hi && lo != hi;

    /// <summary>The load-time rest pose: the centre of each arc, 0 on a free axis.</summary>
    public float RestYawDeg => YawRestricted ? (YawMinDeg!.Value + YawMaxDeg!.Value) * 0.5f : 0f;

    public float RestPitchDeg => PitchRestricted ? (PitchMinDeg!.Value + PitchMaxDeg!.Value) * 0.5f : 0f;

    /// <summary>The engine's by-title lookup: the name comparison is only reached when the key is
    /// present, so an entry with no <c>TITLE</c> matches unconditionally.</summary>
    public bool MatchesTitle(string title) =>
        Title == null || string.Equals(Title, title, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Typed reader over the shared <c>zrdr/ai.zrd</c> <c>TURRET</c> section, 42 entries in
/// the retail install, 16 carried + 26 standalone. Tolerates the eight engine-accepted keys the
/// data never authors (<c>DEACTIVATE</c>, <c>STICKINESS</c>, …) and the one shipped entry that
/// mis-nests <c>PITCH</c> inside its <c>WEAPON</c> block. Schema: docs/formats/turrets.md.</summary>
public sealed class TurretDefs
{
    private readonly List<TurretDef> _all = new();

    public IReadOnlyList<TurretDef> All => _all;

    /// <summary>Loads <c>ai.json</c> from a zrdr archive path (zip or unpacked directory).</summary>
    public static TurretDefs Load(string zrdrPath)
    {
        var root = Zrdr.LoadFile(zrdrPath, "ai.json");
        var defs = new TurretDefs();
        foreach (var section in root)
        {
            if (section is not List<object?> { Count: >= 2 } s
                || s[0] is not string name
                || !name.Equals("TURRET", StringComparison.OrdinalIgnoreCase)
                || s[1] is not List<object?> entries)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry is List<object?> props)
                {
                    defs._all.Add(ParseEntry(props));
                }
            }
        }
        return defs;
    }

    /// <summary>The carried-turret lookup a host runs: the first entry whose <c>TITLE</c>
    /// matches (a titleless entry matches unconditionally, the engine's own short-circuit).</summary>
    public TurretDef? FindByTitle(string title)
    {
        foreach (var d in _all)
        {
            if (d.MatchesTitle(title))
            {
                return d;
            }
        }
        return null;
    }

    private static TurretDef ParseEntry(List<object?> props)
    {
        var d = ZrdrDict.FromAlternating(props);
        var def = new TurretDef
        {
            Title = d.Str("TITLE"),
            Carried = d.Has("CREATE_STANDALONE") && d.Float("CREATE_STANDALONE") == 0f,
            Activated = d.Float("ACTIVATED") != 0f,
            Team = d.TryFloat("TEAM", out var team) ? (int)team : null,
            HealthyNode = d.Str("HEALTHY_NODE"),
            Health = d.TryFloat("HEALTH", out var health) ? health : null,
            InaccuracyDeg = d.Float("INACCURACY"),
        };
        ParseParts(d.List("PARTS"), def);
        ParseWeapon(d.Dict("WEAPON"), def);
        ParseArc(d.List("PITCH"), out def.PitchMinDeg, out def.PitchMaxDeg);
        ParseArc(d.List("YAW"), out def.YawMinDeg, out def.YawMaxDeg);
        ParseWindow(d.List("ATTACK_INTERVAL"), out def.AttackMin, out def.AttackMax);
        ParseWindow(d.List("BORED_INTERVAL"), out def.BoredMin, out def.BoredMax);
        def.CannonSound = d.Dict("SOUNDS")?.Str("CANNON");
        if (d.List("NODES") is { } nodes)
        {
            var patterns = new List<IReadOnlyList<string>>();
            foreach (var item in nodes)
            {
                if (item is List<object?> path)
                {
                    var segs = new List<string>();
                    foreach (var seg in path)
                    {
                        if (seg is string sg)
                        {
                            segs.Add(sg);
                        }
                    }
                    patterns.Add(segs);
                }
            }
            def.NodePatterns = patterns;
        }
        return def;
    }

    // PARTS is a kinematic chain, not a name list: 3 elements = [yaw, pitch, firepoint(s)],
    // 2 elements = [pitch, firepoint(s)] with no traverse ring. The last element is a single
    // name or a list of names cycled round-robin, one per shot.
    private static void ParseParts(List<object?>? parts, TurretDef def)
    {
        if (parts == null || parts.Count < 2)
        {
            return;
        }
        var fps = new List<string>();
        if (parts[^1] is List<object?> fpList)
        {
            foreach (var fp in fpList)
            {
                if (fp is string s)
                {
                    fps.Add(s);
                }
            }
        }
        else if (parts[^1] is string one)
        {
            fps.Add(one);
        }
        def.Firepoints = fps;
        def.PitchNode = parts[^2] as string ?? "";
        def.YawNode = parts.Count >= 3 ? parts[0] as string : null;
    }

    private static void ParseWeapon(ZrdrDict? w, TurretDef def)
    {
        if (w == null)
        {
            return;
        }
        def.WeaponName = w.Str("NAME") ?? "";
        def.Ammo = (int)w.Float("AMMO");
        def.DetectionRange = w.Float("DETECTION_RANGE");
        ParseWindowDict(w, "FIRE_RATE", out def.FireRateMin, out def.FireRateMax);
    }

    private static void ParseArc(List<object?>? pair, out float? min, out float? max)
    {
        min = null;
        max = null;
        if (pair is { Count: >= 2 } && pair[0] is float lo && pair[1] is float hi)
        {
            min = lo;
            max = hi;
        }
    }

    // A duty-cycle window: a scalar authors min == max, a pair authors the redraw range.
    private static void ParseWindow(List<object?>? v, out float min, out float max)
    {
        min = 0f;
        max = 0f;
        if (v is { Count: >= 1 } && v[0] is float lo)
        {
            min = lo;
            max = v.Count >= 2 && v[1] is float hi ? hi : lo;
        }
    }

    private static void ParseWindowDict(ZrdrDict d, string key, out float min, out float max) =>
        ParseWindow(d.List(key), out min, out max);
}
