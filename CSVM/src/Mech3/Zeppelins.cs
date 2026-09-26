using System.Collections.Generic;
using System.IO;
using Godot;

namespace CSVM.Mech3;

/// <summary>A critical zone entry from the <c>healthy</c> list: the gasbag node plus its zone
/// kind (<c>"panels"</c> on all 316 shipped entries).</summary>
public readonly record struct ZeppelinHealthyZone(string Node, string Kind);

/// <summary>One <c>gasbags</c> entry: per-gasbag hit points (80–400) and its destruction
/// animation.</summary>
public readonly record struct ZeppelinGasbag(string Node, float Hp, string? DestroyAnim);

/// <summary>One broadside cannon: its node and the deploy/retract animations that run it out
/// and back in (the design's hatch-open-then-fire sequence, F19's to play).</summary>
public readonly record struct ZeppelinCannon(string Node, string DeployAnim, string RetractAnim);

/// <summary>
/// Reads a mission's zeppelin instances (<c>zeppelins.zrd.json</c> in the mission zrdr scope):
/// 58 records across the install, 1–4 per file, <c>[null]</c> on the 12 MP1/MP2 missions that
/// author none. Every value is reported in its AUTHORED unit (degrees stay degrees), the
/// original converts most angles to radians at load but skips <c>min_pitch</c>/<c>max_pitch</c>
/// (the load-time unit bug, docs/formats/mission-entities.md "Units and the load-time pitch
/// clamp"); keeping the reader verbatim leaves that conversion, and the decision not to
/// reproduce the no-op initial-pitch clamp, to the consumer (<c>Flight.Airframe.ZeppelinMotion</c>
/// for F17's motion keys; F18 consumes the damage half). Format page:
/// docs/formats/mission-entities.md.
/// </summary>
public static class Zeppelins
{
    /// <summary>Loads every zeppelin of one mission, in file order. Empty when the mission's
    /// file is <c>[null]</c>; throws <see cref="FileNotFoundException"/> when the mission scope
    /// carries no zeppelins file at all (unseen in this install: 50 of the 53 mission dirs have
    /// one, the other 3 are MP1/MP2 dirs without the file).</summary>
    public static List<ZeppelinDef> Load(string missionZrdrPath)
    {
        var root = Zrdr.LoadFile(missionZrdrPath, "zeppelins.json");
        var defs = new List<ZeppelinDef>();
        // The whole reader is one outer element: [[record0, record1, …]], or [null] for none.
        if (root.Count == 0 || root[0] is not List<object?> records)
        {
            return defs;
        }
        foreach (var entry in records)
        {
            if (entry is List<object?> record)
            {
                defs.Add(ParseRecord(defs.Count, record));
            }
        }
        return defs;
    }

    private static ZeppelinDef ParseRecord(int index, List<object?> record)
    {
        var d = ZrdrDict.FromAlternating(record);
        string node = d.Str("node")
            ?? throw new InvalidDataException($"zeppelin record {index}: no 'node' key");
        var pos = d.List("position");
        if (pos is not { Count: 3 } || pos[0] is not float px || pos[1] is not float py
            || pos[2] is not float pz)
        {
            throw new InvalidDataException($"zeppelin '{node}': 'position' is not [x,y,z]");
        }

        var healthy = new List<ZeppelinHealthyZone>();
        foreach (var h in d.List("healthy") ?? new List<object?>())
        {
            if (h is List<object?> { Count: >= 2 } pair
                && pair[0] is string zoneNode && pair[1] is string kind)
            {
                healthy.Add(new ZeppelinHealthyZone(zoneNode, kind));
            }
        }

        // Decoded load rule: defaults to 1 when a healthy list is present, and is clamped at
        // load to the length of that list (mission-entities.md, the kill threshold).
        int required = d.TryFloat("num_healthy_required", out float req) ? (int)req : 1;
        if (healthy.Count > 0 && required > healthy.Count)
        {
            required = healthy.Count;
        }

        var gasbags = new List<ZeppelinGasbag>();
        foreach (var g in d.List("gasbags") ?? new List<object?>())
        {
            if (g is List<object?> { Count: >= 2 } bag
                && bag[0] is string bagName && bag[1] is float hp)
            {
                string? anim = bag.Count >= 3 && bag[2] is List<object?> { Count: > 0 } anims
                    ? anims[0] as string : null;
                gasbags.Add(new ZeppelinGasbag(bagName, hp, anim));
            }
        }

        var cannonHealth = new List<ZeppelinCannonHealth>();
        foreach (var c in d.List("cannon_health") ?? new List<object?>())
        {
            // [cannonNode, "gunback", "frame", gasbagName, hp, [destroyAnim], [[frac, anim], …]]
            if (c is not List<object?> { Count: >= 7 } ch || ch[0] is not string cannon
                || ch[3] is not string gasbag || ch[4] is not float chp)
            {
                continue;
            }
            var stages = new List<(float Fraction, string Anim)>();
            if (ch[6] is List<object?> stageList)
            {
                foreach (var s in stageList)
                {
                    if (s is List<object?> { Count: >= 2 } stage
                        && stage[0] is float frac && stage[1] is string anim)
                    {
                        stages.Add((frac, anim));
                    }
                }
            }
            cannonHealth.Add(new ZeppelinCannonHealth
            {
                Cannon = cannon,
                GunBackNode = ch[1] as string ?? "gunback",
                FrameNode = ch[2] as string ?? "frame",
                Gasbag = gasbag,
                Hp = chp,
                DestroyAnim = ch[5] is List<object?> { Count: > 0 } da ? da[0] as string : null,
                Stages = stages,
            });
        }

        // The parser accepts the three team names case-insensitively AND a bare integer id;
        // this install authors only 'enemy'/'ally' (mission-entities.md).
        string? team = d.Str("team");
        int? teamId = team == null && d.TryFloat("team", out float tid) ? (int)tid : null;

        return new ZeppelinDef
        {
            Node = node,
            Position = new Vector3(px, py, pz),
            YawDeg = d.Float("yaw"),
            PitchDeg = d.Float("pitch"),
            MaxSpeed = d.Float("max_speed"),
            MaxAccel = d.Float("max_accel"),
            AccelPitchDeg = d.Float("accel_pitch"),
            AccelYawDeg = d.Float("accel_yaw"),
            MaxRateYawDeg = d.Float("max_rate_yaw"),
            MaxRatePitchDeg = d.Float("max_rate_pitch"),
            MinPitchDeg = d.Float("min_pitch"),
            MaxPitchDeg = d.Float("max_pitch"),
            Net = d.Str("net")
                ?? throw new InvalidDataException($"zeppelin '{node}': no 'net' key"),
            Targets = Names(d.List("targets")),
            Healthy = healthy,
            NumHealthyRequired = required,
            Engines = Names(d.List("engines")),
            Gasbags = gasbags,
            CannonFireDelay = d.TryFloat("cannon_fire_delay", out float delay) ? delay : null,
            CannonFireRange = d.TryFloat("cannon_fire_range", out float range) ? range : null,
            LeftCannons = Cannons(d.List("left_cannons")),
            RightCannons = Cannons(d.List("right_cannons")),
            CannonHealth = cannonHealth,
            CannonInaccuracyDeg = d.TryFloat("cannon_inaccuracy", out float inacc) ? inacc : null,
            Team = team,
            TeamId = teamId,
            // The key's VALUE decides: 7 shipped records author 1 (starts off), 2 author 0.
            Deactivated = d.Float("deactivated") != 0f,
        };
    }

    private static List<string> Names(List<object?>? list)
    {
        var names = new List<string>();
        foreach (var n in list ?? new List<object?>())
        {
            if (n is string name)
            {
                names.Add(name);
            }
        }
        return names;
    }

    private static List<ZeppelinCannon> Cannons(List<object?>? list)
    {
        var cannons = new List<ZeppelinCannon>();
        foreach (var c in list ?? new List<object?>())
        {
            if (c is List<object?> { Count: >= 3 } entry && entry[0] is string cannonNode
                && entry[1] is string deploy && entry[2] is string retract)
            {
                cannons.Add(new ZeppelinCannon(cannonNode, deploy, retract));
            }
        }
        return cannons;
    }
}

/// <summary>A <c>cannon_health</c> entry (24 of 58 records author them): the per-cannon damage
/// record F18 consumes, the cannon, its two constant sub-nodes, the gasbag it is attached to
/// (a hatch hit can take out its section), hp (200 throughout), the destruction anim and the
/// descending-fraction damage stages (0.6/0.3).</summary>
public sealed class ZeppelinCannonHealth
{
    public required string Cannon { get; init; }

    public required string GunBackNode { get; init; }

    public required string FrameNode { get; init; }

    public required string Gasbag { get; init; }

    public required float Hp { get; init; }

    public string? DestroyAnim { get; init; }

    public required IReadOnlyList<(float Fraction, string Anim)> Stages { get; init; }
}

/// <summary>
/// One <c>zeppelins.zrd.json</c> instance. 15 keys are universal across the 58 shipped records;
/// the rest are conditional (<c>gasbags</c> misses exactly one record, C5/M01's
/// <c>piratezep</c>). All angles and rates are in their AUTHORED units, degrees, including
/// <see cref="MinPitchDeg"/>/<see cref="MaxPitchDeg"/>, which the ORIGINAL leaves in degrees
/// while converting everything else to radians (its initial-pitch clamp is therefore a no-op;
/// do not reproduce it as a working clamp, and never read the ±30 as radians).
/// </summary>
public sealed class ZeppelinDef
{
    /// <summary>The world node this instance drives (<c>piratezep</c>,
    /// <c>multiplayer1zep</c>, …).</summary>
    public required string Node { get; init; }

    /// <summary>The start position, world metres.</summary>
    public required Vector3 Position { get; init; }

    /// <summary>The start heading, degrees, mission-data convention (0 = −Z).</summary>
    public float YawDeg { get; init; }

    /// <summary>The start pitch, degrees. Applied verbatim, the original's load-time clamp
    /// against <see cref="MinPitchDeg"/>/<see cref="MaxPitchDeg"/> never fires (unit bug) and
    /// no shipped record authors a non-zero value.</summary>
    public float PitchDeg { get; init; }

    /// <summary>m/s, 5–30 across the install.</summary>
    public float MaxSpeed { get; init; }

    /// <summary>m/s², 4.47 on all 58.</summary>
    public float MaxAccel { get; init; }

    /// <summary>Pitch-rate acceleration, °/s².</summary>
    public float AccelPitchDeg { get; init; }

    /// <summary>Yaw-rate acceleration, °/s².</summary>
    public float AccelYawDeg { get; init; }

    /// <summary>Turn-rate limit, °/s (5/14/15).</summary>
    public float MaxRateYawDeg { get; init; }

    /// <summary>Pitch-rate limit, °/s (5 throughout).</summary>
    public float MaxRatePitchDeg { get; init; }

    /// <summary>Flight pitch floor, degrees (−30 throughout). Stays in degrees in the original
    /// too, see the class summary.</summary>
    public float MinPitchDeg { get; init; }

    /// <summary>Flight pitch ceiling, degrees (+30 throughout).</summary>
    public float MaxPitchDeg { get; init; }

    /// <summary>The patrol net, by chapter <c>neindex</c> name (<see cref="AiNets.ByName"/>).
    /// Mission script can retarget it at runtime (<c>SET_AI_NET</c>), so consumers treat the
    /// net as mutable input, never read-once.</summary>
    public required string Net { get; init; }

    /// <summary>Who it shoots at, <c>player</c>, or another zeppelin's node name. F19's
    /// input.</summary>
    public required IReadOnlyList<string> Targets { get; init; }

    /// <summary>The critical zones (the gasbags; 5/6/9 per zeppelin, 316 install-wide).</summary>
    public required IReadOnlyList<ZeppelinHealthyZone> Healthy { get; init; }

    /// <summary>How many of <see cref="Healthy"/> must SURVIVE: the zeppelin dies when the
    /// surviving count drops BELOW this (decoded polarity, the design states the inverse;
    /// mission-entities.md "The kill threshold counts survivors"). Defaulted to 1 and clamped
    /// to the healthy count at load, per the decoded rule.</summary>
    public required int NumHealthyRequired { get; init; }

    /// <summary>The engine nacelle nodes (12/14/18). The denominator of the engine-loss curve
    /// (<c>Flight.Airframe.ZeppelinMotion.EngineFactor</c>).</summary>
    public required IReadOnlyList<string> Engines { get; init; }

    /// <summary>Per-gasbag hit points and destruction anims. Authored on 57 of 58.</summary>
    public required IReadOnlyList<ZeppelinGasbag> Gasbags { get; init; }

    /// <summary>Broadside re-fire, seconds (10/15/20); null when unauthored (48 of 58 author
    /// the cannon keys).</summary>
    public float? CannonFireDelay { get; init; }

    /// <summary>Broadside reach, metres (500–15000).</summary>
    public float? CannonFireRange { get; init; }

    /// <summary>3 or 6 per side, always symmetric with <see cref="RightCannons"/>.</summary>
    public required IReadOnlyList<ZeppelinCannon> LeftCannons { get; init; }

    public required IReadOnlyList<ZeppelinCannon> RightCannons { get; init; }

    /// <summary>Per-cannon damage records (24 of 58). F18's input.</summary>
    public required IReadOnlyList<ZeppelinCannonHealth> CannonHealth { get; init; }

    /// <summary>Broadside scatter, degrees; authored 10.0 on 3 records.</summary>
    public float? CannonInaccuracyDeg { get; init; }

    /// <summary>The authored team name (<c>enemy</c>/<c>ally</c> in this install; the parser
    /// also accepts <c>neutral</c>), or null on the 42 records without one.</summary>
    public string? Team { get; init; }

    /// <summary>A bare integer team id, the parser's other accepted spelling. Never authored
    /// in this install.</summary>
    public int? TeamId { get; init; }

    /// <summary>Starts switched off, waiting on mission script. The KEY is on 9 records but the
    /// VALUE decides: 7 author 1, two (C1/M04, C2/M03) author 0 and start active.</summary>
    public bool Deactivated { get; init; }
}
