using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Modes;

/// <summary>One instant-action spawn: a world position + a heading (yaw, degrees).</summary>
public readonly record struct SpawnPoint(Vector3 Position, float HeadingDeg)
{
    /// <summary>The nose axis the heading yaws to, so a placement reads
    /// <c>Position + Forward</c> as its look-at point rather than restating the conversion.</summary>
    public Vector3 Forward => new Basis(Vector3.Up, Mathf.DegToRad(HeadingDeg)) * Vector3.Forward;
}

/// <summary>A mission's whole <c>PLAYER_INIT</c> record: where the player starts, and the
/// throttle and speed it starts on. ⚠ <paramref name="SpeedMps"/> is already the parsed
/// field scaled by the original's own 0.1, so it is metres per second and needs no further
/// conversion (docs/formats/spawns.md).</summary>
public readonly record struct PlayerStart(SpawnPoint Spawn, float ThrottleFrac, float SpeedMps);

/// <summary>
/// Reads the player spawn from a mission's own zrdr. Three schemas: <c>LoadIa</c>
/// (instant-action <c>ia.json</c> <c>spawn_points</c> per scenario, one picked at random per
/// launch) and <c>LoadNetFreeForAll</c> (the multiplayer <c>net.zrd</c> table) yield
/// <see cref="SpawnPoint"/>s, and <c>LoadPlayerInit</c> (story
/// <c>objectives.json</c> <c>PLAYER_INIT</c>) yields a whole <see cref="PlayerStart"/>.
/// Schema and the decode behind them: docs/formats/spawns.md and docs/formats/net-spawns.md.</summary>
public static class SpawnPoints
{
    /// <summary>What the original multiplies PLAYER_INIT's speed field by on the way in
    /// (the 0.1 at <c>0046788b</c>), which is what makes the result metres per second.</summary>
    public const float SpeedScale = 0.1f;

    /// <summary>The values a mission that authors no throttle/speed falls back to: what 49 of
    /// the 51 shipped records say (0.8) and what 48 of them say (180 × 0.1 = 18 m/s).</summary>
    public const float DefaultThrottleFrac = 0.8f;

    /// <summary>See <see cref="DefaultThrottleFrac"/>.</summary>
    public const float DefaultSpeedMps = 18f;

    /// <summary>The throttle an Instant Action spawn takes instead of the authored one: the
    /// original's mode-3 branch hard-sets 1.0 (<c>0047f3fb</c>) and reads PLAYER_INIT's throttle
    /// not at all, while still taking its speed from the same record as every other mode.</summary>
    public const float InstantActionThrottleFrac = 1f;

    /// <summary>How many <c>net.zrd</c> entries belong to one block: the original adds
    /// <c>team × 16</c> to a pilot's own index before walking the table, so an un-teamed
    /// deathmatch reads the first block and nothing else (docs/formats/net-spawns.md).</summary>
    public const int NetBlock = 16;

    /// <summary>The throttle a multiplayer opening spawn takes. ⚠ Not PLAYER_INIT's: the
    /// original's multiplayer placement passes this and <see cref="MultiplayerSpeedMps"/> as
    /// constants and never reads the mission's record (docs/formats/net-spawns.md).</summary>
    public const float MultiplayerThrottleFrac = 0.85f;

    /// <summary>See <see cref="MultiplayerThrottleFrac"/>.</summary>
    public const float MultiplayerSpeedMps = 25.7f;

    /// <summary>Loads the spawn list for <paramref name="scenario"/> from the mission's
    /// ia.json (a zrdr zip or unpacked dir). Null if the file or scenario is absent.</summary>
    public static List<SpawnPoint>? LoadIa(string missionZrdrPath, string scenario)
    {
        List<object?> root;
        try
        {
            root = Zrdr.LoadFile(missionZrdrPath, "ia.json");
        }
        catch (IOException)
        {
            return null; // no ia.json for this mission (e.g. a multiplayer-only folder)
        }

        var points = ZrdrDict.FromAlternating(root).Dict("spawn_points")?.List(scenario);
        if (points == null)
            return null;

        var spawns = new List<SpawnPoint>();
        foreach (var p in points)
            if (p is List<object?> a && a.Count >= 4
                && a[0] is float x && a[1] is float y && a[2] is float z && a[3] is float h)
                spawns.Add(new SpawnPoint(new Vector3(x, y, z), h));
        return spawns.Count > 0 ? spawns : null;
    }

    /// <summary>Loads the free-for-all block of a multiplayer mission's <c>net.zrd</c> spawn
    /// table: up to <see cref="NetBlock"/> <c>[x, y, z, heading°]</c> entries, the same record
    /// <c>ia.json</c> authors. Null when the file is absent or holds no entries, which is every
    /// campaign mission (their copies are an unread placeholder).</summary>
    public static List<SpawnPoint>? LoadNetFreeForAll(string missionZrdrPath)
    {
        if (string.IsNullOrEmpty(missionZrdrPath))
            return null;

        List<object?> root;
        try
        {
            root = Zrdr.LoadFile(missionZrdrPath, "net.json");
        }
        catch (IOException)
        {
            return null;
        }

        // One flat group, or a bare null where the mission authors no table at all.
        if (root.Count == 0 || root[0] is not List<object?> nodes)
            return null;

        var spawns = new List<SpawnPoint>();
        foreach (var n in nodes)
        {
            if (spawns.Count >= NetBlock)
                break;
            if (n is List<object?> a && a.Count >= 4
                && a[0] is float x && a[1] is float y && a[2] is float z && a[3] is float h)
                spawns.Add(new SpawnPoint(new Vector3(x, y, z), h));
        }
        return spawns.Count > 0 ? spawns : null;
    }

    /// <summary>Loads a mission's objectives.json <c>PLAYER_INIT</c> block
    /// (<c>[pending, [x,y,z], [pitch,yaw,roll]°, throttle, speed]</c>): position + yaw, the
    /// throttle verbatim, and the speed scaled by the original's own 0.1 into m/s. Null if the
    /// file or block is absent. ⚠ Pitch and roll are dropped because the original's own spawn
    /// only ever authors yaw; do not "restore" them without re-reading the data.</summary>
    public static PlayerStart? LoadPlayerInit(string missionZrdrPath)
    {
        // A caller with no mission at all (a lab, a headless test) is asking the same question as
        // a mission whose file is missing, and gets the same answer. Zrdr throws ArgumentException
        // rather than IOException on an empty path, so the guard is here and not in the catch.
        if (string.IsNullOrEmpty(missionZrdrPath))
            return null;

        List<object?> root;
        try
        {
            root = Zrdr.LoadFile(missionZrdrPath, "objectives.json");
        }
        catch (IOException)
        {
            return null;
        }

        // objectives.json root wraps a single alternating key/value list
        if (root.Count == 0 || root[0] is not List<object?> inner)
            return null;
        var pi = ZrdrDict.FromAlternating(inner).List("PLAYER_INIT");
        if (pi == null || pi.Count < 3
            || pi[1] is not List<object?> pos || pos.Count < 3
            || pi[2] is not List<object?> rot || rot.Count < 2
            || pos[0] is not float x || pos[1] is not float y || pos[2] is not float z
            || rot[1] is not float yaw)
            return null;
        // Every shipped record carries all five, but the position half is the part that has been
        // verified in-game, so a truncated record still yields a usable spawn on the defaults.
        float throttle = pi.Count > 3 && pi[3] is float t ? t : DefaultThrottleFrac;
        float speed = pi.Count > 4 && pi[4] is float s ? s * SpeedScale : DefaultSpeedMps;
        return new PlayerStart(new SpawnPoint(new Vector3(x, y, z), yaw), throttle, speed);
    }
}
