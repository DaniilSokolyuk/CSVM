using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// The parameters of one <c>PUFFER_STATE</c> block from a zrdr effects reader
/// (flame_ball.json, fire.json, pufftrails.json, … all share this schema). A puffer
/// is the original engine's billboard-particle emitter. It spits <see cref="Number"/>
/// sprites per <see cref="TimeInterval"/>, each with a random velocity, size, and
/// lifetime, cycling a flipbook of textures over its age.
///
/// Only the fields we currently render are parsed; the reader carries more, added here as
/// they're needed. Distances/velocities are meters and seconds, matching the world;
/// TEXTURE_SEQUENCE times are the exception, being fractions of a particle's lifetime.
/// </summary>
public sealed class PufferState
{
    /// <summary>The emission cadence an unauthored state runs at. The puffer constructor writes
    /// 1.0 s to the interval and its reciprocal before any authored key applies
    /// (<c>docs/org/puffer.md</c>). ⚠ Not the same number as
    /// <see cref="StillHostSputterInterval"/>, which is ours; do not merge them.</summary>
    public const float TimeIntervalDefault = 1f;

    /// <summary>The cadence a <see cref="DistanceInterval"/> state falls back to while its host
    /// holds still. CSVM's own invention, not a decoded default: the engine emits nothing at all
    /// from a motionless distance emitter, and a damaged building would stop smoking. It stays at
    /// 0.1 s whatever the constructor default is (<c>docs/org/puffer.md</c>).</summary>
    public const float StillHostSputterInterval = 0.1f;

    public string Name = "";
    public float TimeInterval = TimeIntervalDefault;
    public Vector3 LocalVelocity, WorldVelocity;
    public Vector3 MinRandomVelocity, MaxRandomVelocity;
    public Vector3 WorldAcceleration;
    public float Friction;
    public float SizeMin = 1f, SizeMax = 1f;
    public float LifetimeMin = 1f, LifetimeMax = 1f;
    public float GrowthFactor = 1f;
    public float DeviationDistance;
    public int Number = 1;

    /// <summary>Random particle birth age in seconds (<c>Rand(StartAgeMin, StartAgeMax)</c>), both
    /// default 0. ⚠ The engine's real birth age adds a sub-frame term computed in
    /// <c>Puffer.SustainAt</c> and can skip creating a particle outright, see
    /// <c>docs/org/puffer.md</c>.</summary>
    public float StartAgeMin, StartAgeMax;

    /// <summary>How strongly this puffer's particles are carried by the world's wind
    /// (<c>Effects.WorldWind</c>). ⚠ Default is 1, not 0, an unauthored puffer is FULLY
    /// wind-carried. Inert unless <see cref="Friction"/> is non-zero. See
    /// <c>docs/org/puffer.md</c>.</summary>
    public float WindFactor = 1f;

    /// <summary>Camera-distance bands, in metres of view-space depth, see
    /// <c>Puffer.DistanceAlpha</c> for their meaning.
    /// ⚠ Do not infer field order from the authored values; most near pairs are descending. See
    /// <c>docs/formats/effects.md</c> and <c>docs/org/puffer.md</c>.</summary>
    public float NearFadeStart, NearFadeEnd;

    /// <inheritdoc cref="NearFadeStart"/>
    public float FarFadeStart = float.MaxValue, FarFadeEnd = float.MaxValue;

    /// <summary>A per-puffer sprite-size nudge, almost certainly a depth-priority constant reused
    /// for this, the size use is the only one known. The original's draw scales the screen radius
    /// by <c>1 + K·PRIORITY</c>, folded into <c>Particle.BaseSize</c> at spawn instead
    /// (<c>Puffer.PriorityScaleDefault</c>). Default 0, so an unauthored puffer's factor is
    /// exactly 1, 47 puffers in the install author a non-zero value, 192 compiled events.</summary>
    public float Priority;

    /// <summary>AT_NODE's optional trailing offset (AT_NODE is [nodeName, dx?, dy?, dz?]),
    /// in the host node's own frame, the same convention as <see cref="LocalVelocity"/>.
    /// It spreads a multi-emitter effect around its anchor instead of stacking every emitter
    /// on the anchor's exact origin. C1's waterfall, for one, offsets its three splash puffers
    /// on <c>waterfall01</c> (<c>docs/formats/effects.md</c>, <c>AT_NODE</c>).</summary>
    public Vector3 AtNodeOffset;

    /// <summary>Trail emission: one sprite per this many meters of the followed node's motion.
    /// The smoke/fire trail puffers in pufftrails.json's dense_firetrail use it.
    /// 0 = burst-style (NUMBER per TIME_INTERVAL).</summary>
    public float DistanceInterval;

    /// <summary>Flipbook: texture name to show once the particle has lived Time of its lifetime
    /// (ascending, 0–1, a FRACTION of LIFETIME_RANGE, not seconds; see
    /// <c>Puffer.FrameFor</c>).</summary>
    public IReadOnlyList<(float Time, string Texture)> TextureSequence = Array.Empty<(float, string)>();

    /// <summary>Static texture pool (the TEXTURES key, smoke101/102/103): each
    /// particle picks one at random and keeps it. Alternative to TextureSequence.</summary>
    public IReadOnlyList<string> Textures = Array.Empty<string>();

    /// <summary>Color-over-age ramp (the COLORS key): (lifeFraction, color) entries,
    /// ascending; rgb are 0–255 in the reader (normalized here), alpha 0–1. The
    /// dense_firetrail smoke is born orange (255,164,90) and turns near-black.</summary>
    public IReadOnlyList<(float Frac, Color Color)> Colors = Array.Empty<(float, Color)>();

    /// <summary>Whether this state authors <c>START_AGE_RANGE</c> at all. It gates the extra
    /// <c>Rand()</c> draw at spawn, so a puffer that does not author it stays bit-identical.</summary>
    public bool HasStartAgeRange => StartAgeMin != 0f || StartAgeMax != 0f;

    /// <summary>Loads a reader file (e.g. "flame_ball.json") from a zrdr zip/dir and returns
    /// the fully-defined <c>PUFFER_STATE</c> named <paramref name="pufferName"/>, or null.</summary>
    public static PufferState? Load(string zrdrPath, string fileName, string pufferName)
    {
        try
        {
            return FindInReader(Zrdr.LoadFile(zrdrPath, fileName), pufferName);
        }
        catch (Exception e)
        {
            GD.PushWarning($"could not load puffer '{pufferName}' from {fileName}: {e.Message}");
            return null;
        }
    }

    /// <summary>Depth-first search for the fully-defined PUFFER_STATE with the given NAME.
    /// The reader also holds "stop" stubs that share the name but only flip ACTIVE_STATE;
    /// those are skipped. Fully-defined = has NUMBER (burst emitters) or
    /// DISTANCE_INTERVAL (trail emitters, e.g. dense_firetrail's smoke/fire).</summary>
    public static PufferState? FindInReader(List<object?> reader, string pufferName)
    {
        PufferState? found = null;
        void Walk(List<object?> list)
        {
            for (int i = 0; i < list.Count && found == null; i++)
            {
                if (list[i] is string key
                    && key.Equals("PUFFER_STATE", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < list.Count && list[i + 1] is List<object?> body)
                {
                    var d = ZrdrDict.FromAlternating(body);
                    if ((d.Has("NUMBER") || d.Has("DISTANCE_INTERVAL"))
                        && string.Equals(d.Str("NAME"), pufferName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = Parse(d);
                        return;
                    }
                }
                if (list[i] is List<object?> child)
                    Walk(child);
            }
        }
        Walk(reader);
        return found;
    }

    /// <summary>Builds a state from a compiled animation's PUFFER_STATE event payload, as opposed
    /// to <see cref="Parse"/>'s reader form. ⚠ The emission interval, whether it's Time or
    /// Distance, and the growth-factor ramp all need special handling, see
    /// <c>docs/formats/anim-definitions.md</c> for PUFFER_STATE before changing this.</summary>
    public static PufferState FromAnimEvent(AnimData d)
    {
        Vector3 Vec(string key) => d.Vec3(key);
        float Range(string key, string end, float fallback) =>
            d.Obj(key)?.Num(end) ?? fallback;

        // The one interval number is seconds when Time, metres when Distance.
        var ig = d.Obj("interval_garbage");
        bool byDistance = string.Equals(ig?.Str("interval_type"), "Distance",
            StringComparison.OrdinalIgnoreCase);
        // A zero is the compiled shape's "never authored". The applier's interval setter refuses
        // one and leaves the constructor's own value standing. Every unauthored state in the
        // install carries 0 in the garbage field.
        float intervalValue = d.Obj("interval")?.Num("value") ?? ig?.Num("interval_value") ?? 0f;

        var s = new PufferState
        {
            Name = d.Str("name") ?? "",
            TimeInterval = byDistance ? StillHostSputterInterval
                : intervalValue != 0f ? intervalValue : TimeIntervalDefault,
            DistanceInterval = byDistance ? intervalValue : 0f,
            LocalVelocity = Vec("local_velocity"),
            WorldVelocity = Vec("world_velocity"),
            MinRandomVelocity = Vec("min_random_velocity"),
            MaxRandomVelocity = Vec("max_random_velocity"),
            WorldAcceleration = Vec("world_acceleration"),
            Friction = d.Num("friction") ?? 0f,
            SizeMin = Range("size_range", "min", 1f),
            SizeMax = Range("size_range", "max", 1f),
            LifetimeMin = Range("lifetime_range", "min", 1f),
            LifetimeMax = Range("lifetime_range", "max", 1f),
            StartAgeMin = Range("start_age_range", "min", 0f),
            StartAgeMax = Range("start_age_range", "max", 0f),
            // Absent (null) falls back to the ctor's 1.0, never to 0, see WindFactor's remark.
            WindFactor = d.Num("wind_factor") ?? 1f,
            // `unk_range` IS NEAR_FADE, see docs/formats/effects.md. Absent stays at the ctor's
            // no-fade, no-cull defaults.
            NearFadeStart = Range("unk_range", "min", 0f),
            NearFadeEnd = Range("unk_range", "max", 0f),
            FarFadeStart = Range("fade_range", "min", float.MaxValue),
            FarFadeEnd = Range("fade_range", "max", float.MaxValue),
            Priority = d.Num("priority") ?? 0f,
            DeviationDistance = d.Num("deviation_distance") ?? 0f,
            Number = Mathf.Max(1, (int)(d.Num("number") ?? 1f)),
            AtNodeOffset = Vec("translate"),
        };

        var growth = new List<AnimData>(d.Objects("growth_factors"));
        s.GrowthFactor = growth.Count >= 2 ? growth[1].Num("max") ?? 1f
            : growth.Count == 1 ? growth[0].Num("max") ?? 1f : 1f;

        // textures[] entries carry an optional run_time: present ⇒ a timed flipbook,
        // absent ⇒ the static pool each particle picks one from.
        var pool = new List<string>();
        var seq = new List<(float, string)>();
        foreach (var t in d.Objects("textures"))
        {
            if (t.Str("name") is not { } texName)
                continue;
            if (t.Num("run_time") is { } runTime)
                seq.Add((runTime, texName));
            else
                pool.Add(texName);
        }
        s.Textures = pool;
        s.TextureSequence = seq;

        // colors[]: {unk00 = life fraction, color = rgb 0–255, unk16 = alpha 0–1}, the same
        // ramp the reader spells [frac, r, g, b, a].
        var colors = new List<(float, Color)>();
        foreach (var c in d.Objects("colors"))
        {
            var rgb = c.Obj("color");
            if (rgb == null)
                continue;
            float r = rgb.Num("r") ?? 0f, g = rgb.Num("g") ?? 0f, b = rgb.Num("b") ?? 0f;
            float scale = r > 1f || g > 1f || b > 1f ? 1f / 255f : 1f;
            colors.Add((c.Num("unk00") ?? 0f,
                new Color(r * scale, g * scale, b * scale, c.Num("unk16") ?? 1f)));
        }
        s.Colors = colors;
        return s;
    }

    private static PufferState Parse(ZrdrDict d)
    {
        Vector3 Vec(string key) =>
            new(d.Float(key, index: 0), d.Float(key, index: 1), d.Float(key, index: 2));

        // FADE_RANGE and FAR_FADE are two spellings of one block (the parser accepts both onto
        // the same flag bit). The install authors FADE_RANGE 574 times and
        // FAR_FADE exactly once, C3's volcanosmoke, so the alias is not hypothetical.
        string farKey = d.Has("FADE_RANGE") ? "FADE_RANGE" : "FAR_FADE";

        var s = new PufferState
        {
            Name = d.Str("NAME") ?? "",
            // Unauthored, the cadence is the constructor's 1.0 s. On a DISTANCE_INTERVAL block it
            // is our still-host sputter instead, and every reader block omitting the key is one.
            TimeInterval = d.Has("TIME_INTERVAL") ? d.Float("TIME_INTERVAL")
                : d.Has("DISTANCE_INTERVAL") ? StillHostSputterInterval : TimeIntervalDefault,
            LocalVelocity = Vec("LOCAL_VELOCITY"),
            WorldVelocity = Vec("WORLD_VELOCITY"),
            MinRandomVelocity = Vec("MIN_RANDOM_VELOCITY"),
            MaxRandomVelocity = Vec("MAX_RANDOM_VELOCITY"),
            WorldAcceleration = Vec("WORLD_ACCELERATION"),
            Friction = d.Float("FRICTION"),
            SizeMin = d.Float("SIZE_RANGE", 1f, 0),
            SizeMax = d.Float("SIZE_RANGE", 1f, 1),
            LifetimeMin = d.Float("LIFETIME_RANGE", 1f, 0),
            LifetimeMax = d.Float("LIFETIME_RANGE", 1f, 1),
            StartAgeMin = d.Float("START_AGE_RANGE", 0f, 0),
            StartAgeMax = d.Float("START_AGE_RANGE", 0f, 1),
            // Absent falls back to the ctor's 1.0, never to 0, see WindFactor's remark. Only 5
            // reader blocks in the install author the key at all (3 at 0.3, 2 at 1.0).
            WindFactor = d.Float("WIND_FACTOR", 1f),
            // NearFadeStart says which end of each band is which. DO NOT read it off these
            // values: five of the six authored near pairs are descending.
            NearFadeStart = d.Float("NEAR_FADE", 0f, 0),
            NearFadeEnd = d.Float("NEAR_FADE", 0f, 1),
            FarFadeStart = d.Float(farKey, float.MaxValue, 0),
            FarFadeEnd = d.Float(farKey, float.MaxValue, 1),
            Priority = d.Float("PRIORITY"),
            GrowthFactor = d.Float("GROWTH_FACTOR", 1f),
            DeviationDistance = d.Float("DEVIATION_DISTANCE"),
            Number = (int)d.Float("NUMBER", 1f),
            DistanceInterval = d.Float("DISTANCE_INTERVAL"),
            // AT_NODE is [nodeName, dx?, dy?, dz?], the offset starts at index 1, past the name.
            AtNodeOffset = new Vector3(d.Float("AT_NODE", 0f, 1), d.Float("AT_NODE", 0f, 2), d.Float("AT_NODE", 0f, 3)),
        };

        var seq = new List<(float, string)>();
        foreach (var item in d.List("TEXTURE_SEQUENCE") ?? new List<object?>())
            if (item is List<object?> pair && pair.Count >= 2
                && pair[0] is float t && pair[1] is string tex)
                seq.Add((t, tex));
        s.TextureSequence = seq;

        var texList = new List<string>();
        foreach (var item in d.List("TEXTURES") ?? new List<object?>())
            if (item is string name)
                texList.Add(name);
        s.Textures = texList;

        // COLORS entries are [lifeFrac, r, g, b, a] with rgb 0–255 (the same
        // integer-encoding rule as weather.json: any component > 1 ⇒ /255).
        var colors = new List<(float, Color)>();
        foreach (var item in d.List("COLORS") ?? new List<object?>())
            if (item is List<object?> { Count: >= 5 } c
                && c[0] is float frac && c[1] is float r && c[2] is float g
                && c[3] is float b && c[4] is float a)
            {
                float scale = r > 1f || g > 1f || b > 1f ? 1f / 255f : 1f;
                colors.Add((frac, new Color(r * scale, g * scale, b * scale, a)));
            }
        s.Colors = colors;
        return s;
    }
}
