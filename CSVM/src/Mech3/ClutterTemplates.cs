using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Mech3;

/// <summary>One weighted alternative in a kind's <c>substitute</c> list. Weights are relative; the
/// engine normalises by dividing by the list total. Decode: docs/formats/templates.md.</summary>
/// <param name="Weight">The relative weight, exactly as authored.</param>
/// <param name="Fraction">Weight divided by the list total: the draw probability.</param>
/// <param name="Model">The decoration model to place instead.</param>
public readonly record struct ClutterSubstitute(float Weight, float Fraction, string Model);

/// <summary>One <c>OnWeaponHit</c>/<c>OnCrater</c>/<c>OnCollide</c> block. No chapter authors any
/// of the three. Decode: docs/formats/templates.md.</summary>
/// <param name="Health">Arms the block; null Health means unarmed.</param>
/// <param name="Anim">Resolved animation-definition name, or null.</param>
/// <param name="Model">Resolved debris/wreck model name, or null.</param>
public readonly record struct ClutterDamageResponse(float Health, string? Anim, string? Model);

/// <summary>
/// One <c>templates.zrd</c> block: the authored properties of one decoration model, keyed by its
/// own node name, never the template that scatters it. Schema and per-chapter census: docs/
/// formats/templates.md.
/// Every default here is the engine's own. A decoration matching no block is normal; the stamper
/// skips every kind-driven step for it.
/// </summary>
public sealed class ClutterKindProps
{
    /// <summary>The decoration model's node name, as both the file and the gamez spell it,
    /// with the <c>.flt</c> suffix (<c>firtree1.flt</c>). The engine matches it to a decoration by
    /// a plain <c>strcmp</c> against the gamez node's name; every shipped
    /// name agrees exactly, so the case-insensitive lookup here is a courtesy, not a fixup.</summary>
    public string Node { get; init; } = "";

    /// <summary><c>scale_range</c>, the uniform per-instance scale multiplier, drawn once per
    /// stamp between the two components (the stamper's step 10, applied as a uniform scale).
    /// A FLAT pair in the file, unlike the nested pairs beside it.
    /// Default (1, 1), i.e. exactly the authored size. Authored by all 143 shipped kinds; the
    /// install's span is 0.5–3.0.</summary>
    public Vector2 ScaleRange { get; init; } = Vector2.One;

    /// <summary><c>far_fade_range</c> lower bounds, <c>(nearMin, farMin)</c>, in metres, from the
    /// file's FIRST pair. See <see cref="FarFadeMax"/> for why the pairs are read this way.</summary>
    public Vector2 FarFadeMin { get; init; }

    /// <summary><c>far_fade_range</c> upper bounds, <c>(nearMax, farMax)</c>, in metres, from the
    /// file's second pair. Decode: docs/formats/templates.md.
    /// ⚠ The two file pairs are the min and max of the two distances, not the two distances
    /// themselves; both come from one <c>rand()</c> draw. Zero <c>farMax</c> means never fades.</summary>
    public Vector2 FarFadeMax { get; init; }

    /// <summary><c>translate_uv_range</c> lower bounds, <c>(uMin, vMin)</c>, in UV units, from the
    /// file's first pair; the same min-pair/max-pair grouping as
    /// <see cref="FarFadeMax"/> documents. The per-axis jitter added to a lattice candidate in
    /// the stamper's step 5. <b>Authored by no chapter</b>, so the whole of step 5 is inert
    /// on retail data, which is the structural reason the original's placement has no random input
    /// affecting position at all.</summary>
    public Vector2 TranslateUvMin { get; init; }

    /// <inheritdoc cref="TranslateUvMin"/>
    public Vector2 TranslateUvMax { get; init; }

    /// <summary><c>rotation_range</c> lower bounds, the per-axis minimum rotation, <b>in radians</b>
    /// (the file authors DEGREES; the engine multiplies by π/180 as it stores). Same
    /// min-pair/max-pair grouping as the two above. <b>Authored by no chapter</b>, and note that
    /// the engine still draws three <c>rand()</c> values per stamp for it, so it is inert in effect
    /// rather than skipped.</summary>
    public Vector3 RotationMin { get; init; }

    /// <inheritdoc cref="RotationMin"/>
    public Vector3 RotationMax { get; init; }

    /// <summary>The LOWER bound on the ground triangle's normal Y, from <c>max_slope</c>: the file
    /// authors an angle in degrees and the engine stores its COSINE (<c>kind+0x58</c>). Default −1,
    /// i.e. no cull. See <see cref="NormalYMax"/> for the inversion.</summary>
    public float NormalYMin { get; init; } = -1f;

    /// <summary>Upper bound on the ground triangle's normal Y, from <c>min_slope</c>. Decode:
    /// docs/formats/templates.md.
    /// ⚠ Named for the field it gates, not the key it comes from: the mapping inverts, since
    /// cosine decreases with angle.</summary>
    public float NormalYMax { get; init; } = 1f;

    /// <summary><c>align_normal</c>, a BARE FLAG (no value list): present means align the placed
    /// decoration to the ground normal instead of rotating it randomly. <b>Authored by no
    /// chapter</b>; default false.</summary>
    public bool AlignNormal { get; init; }

    /// <summary><c>substitute</c>, the weighted model roll of the stamper's step 9, in file
    /// order (the order matters: the engine walks the list subtracting <see
    /// cref="ClutterSubstitute.Fraction"/> from one uniform draw). Empty when the key is absent, in
    /// which case the stamp is always the decoration's own model. Authored by 41 kinds.</summary>
    public IReadOnlyList<ClutterSubstitute> Substitutes { get; init; } = Array.Empty<ClutterSubstitute>();

    /// <summary><c>OnWeaponHit</c> / <c>OnCrater</c> / <c>OnCollide</c>, null when the block is
    /// absent, which is every kind of every chapter (see <see cref="ClutterDamageResponse"/>).</summary>
    public ClutterDamageResponse? OnWeaponHit { get; init; }

    /// <inheritdoc cref="OnWeaponHit"/>
    public ClutterDamageResponse? OnCrater { get; init; }

    /// <inheritdoc cref="OnWeaponHit"/>
    public ClutterDamageResponse? OnCollide { get; init; }

    /// <summary>The keys this block actually carried, lowercased. Kept because a key authored AT
    /// its default is indistinguishable from an absent one in every parsed value above, and the
    /// census that has to agree with a per-chapter key count (C5 authors <c>scale_range</c> on all
    /// 78 of its blocks, most of them as 1.0/1.0) needs the difference.</summary>
    public IReadOnlyList<string> AuthoredKeys { get; init; } = Array.Empty<string>();

    /// <summary>Keys this block carried that the reader does not model. Empty for every shipped
    /// file, the same unhandled-key assertion <c>Flight.Weapons.WeaponDefs</c> makes, kept because the one
    /// thing a reader over authored data must never do quietly is skip a key.</summary>
    public IReadOnlyList<string> UnknownKeys { get; init; } = Array.Empty<string>();

    /// <summary>One stamp's fade thresholds as the engine stores them on the placed node:
    /// <c>(near², far², 1 / (far² − near²))</c>, with both distances lerped between the min and
    /// max pairs by the ONE draw <paramref name="t"/>. All zero when <c>farMax</c> is 0, the
    /// engine's never-fades sentinel, and the reciprocal is 0 when the two distances coincide.</summary>
    public static Vector3 FadeThresholds(Vector2 min, Vector2 max, float t)
    {
        if (max.Y == 0f)
        {
            return Vector3.Zero;
        }
        float near = min.X + ((max.X - min.X) * t);
        float far = min.Y + ((max.Y - min.Y) * t);
        float nearSq = near * near, farSq = far * far;
        float span = farSq - nearSq;
        return new Vector3(nearSq, farSq, span != 0f ? 1f / span : 0f);
    }

    /// <inheritdoc cref="FadeThresholds(Vector2, Vector2, float)"/>
    public Vector3 FadeThresholds(float t) => FadeThresholds(FarFadeMin, FarFadeMax, t);

    /// <summary>Whether the block authored this key at all, at any value.</summary>
    public bool Authors(string key)
    {
        foreach (var authored in AuthoredKeys)
        {
            if (string.Equals(authored, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// One chapter's <c>zrdr/templates.zrd</c>: per-decoration properties the clutter stamper reads.
/// Schema, census and the decoded/unapplied split: docs/formats/templates.md.
/// ⚠ Keyed by decoration model, not by the chapter's registered templates, a separate list
/// (<c>interp.json</c>'s <c>AddClutterTemplates</c>, docs/formats/clutter.md); nothing here reads
/// it.
/// A decoration with no block is normal and means all defaults; the stamper guards every
/// kind-driven step on a null kind pointer.
/// Static over a reader list, so <c>CSVM.Tests/ClutterTemplatesTests.cs</c> pins it off-engine.
/// </summary>
public sealed class ClutterTemplateSpec
{
    // The engine's degrees→radians factor, written to exactly the digits the binary carries.
    // Godot's Mathf.DegToRad uses the same constant to more digits; this keeps the parse
    // bit-comparable with the original.
    private const float DegreesToRadians = 0.01745329251994f;

    private readonly Dictionary<string, ClutterKindProps> _byNode;

    private ClutterTemplateSpec(
        IReadOnlyList<ClutterKindProps> kinds,
        IReadOnlyList<string> duplicateNodes,
        Dictionary<string, ClutterKindProps> byNode)
    {
        Kinds = kinds;
        DuplicateNodes = duplicateNodes;
        _byNode = byNode;
    }

    /// <summary>Every block, in file order, INCLUDING a duplicate name's later blocks, the file is
    /// a list and the engine keeps it as one. Use <see cref="Find"/> to resolve a name the way the
    /// engine does.</summary>
    public IReadOnlyList<ClutterKindProps> Kinds { get; }

    /// <summary>Node names carrying more than one block, in file order. <b>C5 ships exactly one</b>
    /// (<c>cb05det01.flt</c>, whose second block drops the <c>substitute</c> the first one carries),
    /// and the engine's own lookup is a linear scan that stops at the first match, so the block
    /// that wins is the FIRST, and the substitute survives. Surfaced rather than silently collapsed
    /// because "which one wins" is exactly the question a duplicate raises.</summary>
    public IReadOnlyList<string> DuplicateNodes { get; }

    /// <summary>Loads a chapter's <c>templates.zrd</c>. Null when the chapter's zrdr scope has no
    /// such file, degrades rather than throwing, like every other optional reader here. An EMPTY
    /// file is not that case: C1C and C2B ship one, and both come back as a spec with no kinds.</summary>
    public static ClutterTemplateSpec? Load(string chapterZrdrPath)
    {
        try
        {
            return Parse(Zrdr.LoadFileOrEmpty(chapterZrdrPath, "templates.json"));
        }
        catch (Exception e) when (e is System.IO.FileNotFoundException
                                      or System.IO.DirectoryNotFoundException
                                      or System.IO.InvalidDataException
                                      or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses an already-loaded reader list: one block per decoration model, each an
    /// alternating key/value list. A block with no <c>node</c> key is skipped, that is the engine's
    /// own gate (it allocates nothing without one), not leniency.</summary>
    public static ClutterTemplateSpec Parse(List<object?> root)
    {
        var kinds = new List<ClutterKindProps>();
        var byNode = new Dictionary<string, ClutterKindProps>(StringComparer.OrdinalIgnoreCase);
        List<string>? duplicates = null;

        foreach (var entry in root)
        {
            if (entry is not List<object?> block)
            {
                continue;
            }
            var dict = ZrdrDict.FromAlternating(block);
            if (dict.Str("node") is not { Length: > 0 } node)
            {
                continue;
            }
            var props = ParseBlock(node, dict);
            kinds.Add(props);
            if (!byNode.TryAdd(node, props))
            {
                (duplicates ??= new List<string>()).Add(node);   // first block wins, as the
            }                                                    // engine's linear scan does
        }

        return new ClutterTemplateSpec(
            kinds,
            (IReadOnlyList<string>?)duplicates ?? Array.Empty<string>(),
            byNode);
    }

    /// <summary>The properties for a decoration model name, or null when the file carries no block
    /// for it, which the caller must read as "all defaults", never as "do not place". Resolves a
    /// duplicated name to its FIRST block, matching the engine's scan.</summary>
    public ClutterKindProps? Find(string node) =>
        _byNode.TryGetValue(node, out var props) ? props : null;

    /// <summary>A one-line census: block and distinct-kind counts, then how many blocks author each
    /// accepted key, every key listed, so unauthored reads as a measured zero.
    /// Counted by key presence, not by parsed value, since most of C5's blocks author
    /// <c>scale_range</c> as the default 1.0/1.0.</summary>
    public string Census()
    {
        int scale = 0, fade = 0, translate = 0, rotation = 0, minSlope = 0, maxSlope = 0;
        int align = 0, substitute = 0, damage = 0, unknown = 0;
        foreach (var kind in Kinds)
        {
            scale += kind.Authors("scale_range") ? 1 : 0;
            fade += kind.Authors("far_fade_range") ? 1 : 0;
            translate += kind.Authors("translate_uv_range") ? 1 : 0;
            rotation += kind.Authors("rotation_range") ? 1 : 0;
            minSlope += kind.Authors("min_slope") ? 1 : 0;
            maxSlope += kind.Authors("max_slope") ? 1 : 0;
            align += kind.Authors("align_normal") ? 1 : 0;
            substitute += kind.Authors("substitute") ? 1 : 0;
            damage += kind.OnWeaponHit != null || kind.OnCrater != null || kind.OnCollide != null ? 1 : 0;
            unknown += kind.UnknownKeys.Count;
        }
        return $"blocks={Kinds.Count} kinds={_byNode.Count} duplicates={DuplicateNodes.Count} "
               + $"scale_range={scale} far_fade_range={fade} substitute={substitute} "
               + $"translate_uv_range={translate} rotation_range={rotation} min_slope={minSlope} "
               + $"max_slope={maxSlope} align_normal={align} damage_blocks={damage} "
               + $"unknown_keys={unknown}";
    }

    private static ClutterKindProps ParseBlock(string node, ZrdrDict dict)
    {
        var scale = dict.List("scale_range");
        var translate = NestedPairs(dict.List("translate_uv_range"));
        var fade = NestedPairs(dict.List("far_fade_range"));
        var rotation = dict.List("rotation_range");
        List<string>? unknown = null;
        var authored = new List<string>(dict.Keys.Count);
        foreach (var key in dict.Keys)
        {
            authored.Add(key.ToLowerInvariant());
            if (!Known(key))
            {
                (unknown ??= new List<string>()).Add(key);
            }
        }

        return new ClutterKindProps
        {
            Node = node,
            // ⚠ A flat pair, deliberately not run through NestedPairs like the keys beside it.
            ScaleRange = scale != null ? Pair(scale) : Vector2.One,
            TranslateUvMin = translate.First,
            TranslateUvMax = translate.Second,
            FarFadeMin = fade.First,
            FarFadeMax = fade.Second,
            RotationMin = Triple(rotation, 0) * DegreesToRadians,
            RotationMax = Triple(rotation, 1) * DegreesToRadians,
            // The inversion, in one place: max_slope's cosine is the LOWER bound on the normal's Y
            // and min_slope's the UPPER, as the engine stores them.
            NormalYMin = dict.TryFloat("max_slope", out var maxSlope)
                ? Mathf.Cos(maxSlope * DegreesToRadians) : -1f,
            NormalYMax = dict.TryFloat("min_slope", out var minSlope)
                ? Mathf.Cos(minSlope * DegreesToRadians) : 1f,
            AlignNormal = dict.Has("align_normal"),
            Substitutes = ParseSubstitutes(dict.List("substitute")),
            OnWeaponHit = ParseDamage(dict.Dict("OnWeaponHit")),
            OnCrater = ParseDamage(dict.Dict("OnCrater")),
            OnCollide = ParseDamage(dict.Dict("OnCollide")),
            AuthoredKeys = authored,
            UnknownKeys = (IReadOnlyList<string>?)unknown ?? Array.Empty<string>(),
        };
    }

    private static bool Known(string key) => key.ToLowerInvariant() switch
    {
        "node" or "scale_range" or "translate_uv_range" or "far_fade_range" or "rotation_range"
            or "min_slope" or "max_slope" or "align_normal" or "substitute"
            or "onweaponhit" or "oncrater" or "oncollide" => true,
        _ => false,
    };

    private static IReadOnlyList<ClutterSubstitute> ParseSubstitutes(List<object?>? list)
    {
        if (list == null)
        {
            return Array.Empty<ClutterSubstitute>();
        }
        var raw = new List<(float Weight, string Model)>();
        float total = 0f;
        foreach (var entry in list)
        {
            if (entry is List<object?> pair && pair.Count >= 2
                && pair[0] is float weight && pair[1] is string model)
            {
                raw.Add((weight, model));
                total += weight;
            }
        }
        if (raw.Count == 0)
        {
            return Array.Empty<ClutterSubstitute>();
        }
        // The engine divides by the sum and stores the share; a zero total would make it
        // skip the list entirely, so it is reported as zero shares rather than as a division.
        var subs = new List<ClutterSubstitute>(raw.Count);
        foreach (var (weight, model) in raw)
        {
            subs.Add(new ClutterSubstitute(weight, total != 0f ? weight / total : 0f, model));
        }
        return subs;
    }

    private static ClutterDamageResponse? ParseDamage(ZrdrDict? block)
    {
        // The `health` key is what arms the block, the engine reads the model and anim only when
        // it was found, and stores the "is destructible" bit from that same test.
        if (block == null || !block.TryFloat("health", out var health))
        {
            return null;
        }
        return new ClutterDamageResponse(health, block.Str("anim"), block.Str("model"));
    }

    private static (Vector2 First, Vector2 Second) NestedPairs(List<object?>? list)
    {
        if (list == null)
        {
            return (Vector2.Zero, Vector2.Zero);
        }
        return (
            Pair(list.Count > 0 ? list[0] as List<object?> : null),
            Pair(list.Count > 1 ? list[1] as List<object?> : null));
    }

    private static Vector2 Pair(List<object?>? list) =>
        list == null ? Vector2.Zero : new Vector2(Scalar(list, 0), Scalar(list, 1));

    private static Vector3 Triple(List<object?>? list, int index)
    {
        if (list == null || index >= list.Count || list[index] is not List<object?> axes)
        {
            return Vector3.Zero;
        }
        return new Vector3(Scalar(axes, 0), Scalar(axes, 1), Scalar(axes, 2));
    }

    private static float Scalar(List<object?> list, int index) =>
        index < list.Count && list[index] is float f ? f : 0f;
}
