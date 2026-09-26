using System;
using System.Collections.Generic;
using CSVM.Mech3;

namespace CSVM.Flight.Camera;

/// <summary>
/// One <c>shakes.json</c> oscillator source: the law (frequency, damping, waveform) plus one
/// magnitude term whose kind varies by source, a per-event <see cref="MagnitudeFactor"/>
/// (fire_bullet × caliber, the impact trio × their event quantity), a speed law
/// (<see cref="MagnitudeQuotient"/> + <see cref="MinSpeed"/>, high_speed only), or an absolute
/// <see cref="Magnitude"/> (nitro only). See
/// <see href="../../../../docs/formats/shakes.md">shakes.md</see>. Magnitudes are radians of roll
/// applied to the plane node (measured, `analysis/gun-wobble-shake/`).
/// </summary>
public sealed class ShakeSource
{
    public string Id = "";            // "fire_bullet"
    public float Frequency;           // oscillation rate, Hz
    public float Damp;                // exponential decay rate of an impulse, 1/s
    public bool Sawtooth;             // 1 = the buzzy waveform (firing, speed rattle, nitro)
    public float? MagnitudeFactor;    // scales the per-event quantity (impulse sources)
    public float? HeFactor;           // extra multiplier for HIGH_EXPLOSIVE hits (missile_impact)
    public float? MinSpeed;           // speed gate (high_speed)
    public float? MagnitudeQuotient;  // magnitude = speed / quotient (high_speed)
    public float? Magnitude;          // absolute magnitude (nitro)

    /// <summary>Keys this reader does not map, empty for every source in this install; a
    /// non-empty list means the data grew a key and the reader must learn it.</summary>
    public IReadOnlyList<string> UnhandledKeys = Array.Empty<string>();
}

/// <summary>
/// Typed reader over the shared <c>shakes.zrd.json</c>, the six authored shake-oscillator
/// sources. Modelled on <see cref="Weapons.WeaponDefs"/>: load once, index by source id, named
/// accessors for the sources the runtime wires.
/// </summary>
public sealed class ShakeDefs
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "frequency", "damp", "sawtooth",
        "magnitude_factor", "he_factor", "min_speed", "magnitude_quotient", "magnitude",
    };

    private readonly Dictionary<string, ShakeSource> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ShakeSource> _all = new();

    /// <summary>Every source, in file order.</summary>
    public IReadOnlyList<ShakeSource> All => _all;

    public ShakeSource? FireBullet => Get("fire_bullet");
    public ShakeSource? BulletImpact => Get("bullet_impact");
    public ShakeSource? MissileImpact => Get("missile_impact");
    public ShakeSource? Explosion => Get("explosion");
    public ShakeSource? HighSpeed => Get("high_speed");
    public ShakeSource? Nitro => Get("nitro");

    public static ShakeDefs Load(string zrdrPath)
    {
        var root = Zrdr.LoadFile(zrdrPath, "shakes.json");
        var defs = new ShakeDefs();
        for (int i = 0; i + 1 < root.Count; i += 2)
        {
            if (root[i] is not string id || root[i + 1] is not List<object?> body)
            {
                continue;
            }
            var src = Parse(id, ZrdrDict.FromAlternating(body));
            defs._byId[id] = src;
            defs._all.Add(src);
        }
        return defs;
    }

    public ShakeSource? Get(string id) => _byId.TryGetValue(id, out var s) ? s : null;

    private static ShakeSource Parse(string id, ZrdrDict d)
    {
        float? F(string k) => d.TryFloat(k, out var f) ? f : null;

        var src = new ShakeSource
        {
            Id = id,
            Frequency = d.Float("frequency"),
            Damp = d.Float("damp"),
            Sawtooth = d.Float("sawtooth") != 0f,
            MagnitudeFactor = F("magnitude_factor"),
            HeFactor = F("he_factor"),
            MinSpeed = F("min_speed"),
            MagnitudeQuotient = F("magnitude_quotient"),
            Magnitude = F("magnitude"),
        };

        List<string>? unhandled = null;
        foreach (var k in d.Keys)
        {
            if (!KnownKeys.Contains(k))
            {
                (unhandled ??= new List<string>()).Add(k);
            }
        }
        if (unhandled != null)
        {
            src.UnhandledKeys = unhandled;
        }
        return src;
    }
}
