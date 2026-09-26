using System;
using System.Collections.Generic;
using CSVM.Flight.Weapons;
using CSVM.Mech3;

namespace CSVM.Flight.Airframe;

/// <summary>The zeppelin kill arithmetic (M4 F18), pure and engine-free: the decoded survivor
/// threshold over the record's <c>healthy</c> list, the engine recount that drives
/// <see cref="ZeppelinMotion.AliveEngines"/>, the <c>DAMAGES_ZEPPELIN</c> gasbag gate and the
/// record-authored cannon damage stages. Decode: docs/formats/mission-entities.md. The per-zone
/// hit points live in the world's <c>DestructibleRegistry</c> pools; this class only counts.
/// ⚠ POLARITY: <c>num_healthy_required</c> is how many critical zones must SURVIVE, the
/// zeppelin dies when <c>survivors &lt; required</c>. The design document's destroy-count
/// reading is the inverse and yields an immortal zeppelin.</summary>
public sealed class ZeppelinDamage
{
    public ZeppelinDamage(ZeppelinDef def)
    {
        Def = def;
        var healthy = new List<string>(def.Healthy.Count);
        foreach (var zone in def.Healthy)
        {
            healthy.Add(zone.Node);
        }
        HealthyNodes = healthy;
        Required = def.NumHealthyRequired;
    }

    public ZeppelinDef Def { get; }

    /// <summary>The critical-zone node names, one entry per authored <c>healthy</c> row,
    /// duplicates preserved (counted per entry, the engine's own walk).</summary>
    public IReadOnlyList<string> HealthyNodes { get; }

    /// <summary>How many entries of <see cref="HealthyNodes"/> must remain alive.</summary>
    public int Required { get; }

    /// <summary>The routing gate: only a weapon authoring <c>DAMAGES_ZEPPELIN</c> (the torpedo
    /// <c>wep_14</c> and the zeppelin cannonball <c>wep_28</c> in this install) may damage a
    /// gasbag zone. It gates the critical gasbag zones only, engines, turrets and cannons are
    /// ordinary destructibles any weapon hurts (docs/formats/weapons.md).</summary>
    public static bool MayDamageGasbag(WeaponDef weapon) => weapon.DamagesZeppelin;

    /// <summary>Which of a <c>cannon_health</c> record's descending damage stages have been
    /// crossed at the given health fraction and not yet fired: returns the stage anims to play,
    /// updating <paramref name="firedCount"/>. Every crossed stage fires, each exactly once,
    /// the record's stage list has <c>injure_anims</c>' shape (docs/org/vehicleDamage.md), whose
    /// driver starts every entry whose threshold is crossed.</summary>
    public static List<string> CrossedStages(
        IReadOnlyList<(float Fraction, string Anim)> stages, float healthFraction, ref int firedCount)
    {
        var fire = new List<string>();
        for (int i = firedCount; i < stages.Count; i++)
        {
            if (healthFraction <= stages[i].Fraction)
            {
                fire.Add(stages[i].Anim);
                firedCount = i + 1;
            }
        }
        return fire;
    }

    /// <summary>Healthy entries still alive under the given zone-aliveness view.</summary>
    public int Survivors(Func<string, bool> zoneAlive)
    {
        int alive = 0;
        foreach (var node in HealthyNodes)
        {
            if (zoneAlive(node))
            {
                alive++;
            }
        }
        return alive;
    }

    /// <summary>The decoded kill rule: dead when <c>survivors &lt; required</c>. Never a
    /// destroyed-count threshold, see the class summary's polarity warning.</summary>
    public bool IsDead(Func<string, bool> zoneAlive) => Survivors(zoneAlive) < Required;

    /// <summary>Engines still alive, the value the aggregator writes onto
    /// <see cref="ZeppelinMotion.AliveEngines"/> (the decoded sqrt curve's numerator).</summary>
    public int AliveEngines(Func<string, bool> engineAlive)
    {
        int alive = 0;
        foreach (var engine in Def.Engines)
        {
            if (engineAlive(engine))
            {
                alive++;
            }
        }
        return alive;
    }

}
