using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Roster;

/// <summary>Resolves which livery each player flies: the shipped paint catalog, the per-pattern
/// region-mask library, and the per-player scheme pick that reads them against a
/// <see cref="SessionSpec"/>'s <c>--paint=</c>/<c>--paint-color=</c>/<c>--paint-decal=</c>
/// overrides. Constructed once per session; caches the catalog and
/// library across the calls that build every player's plane.</summary>
public sealed class LiveryResolver
{
    /// <summary>vehicle.json's name for the Fortune Hunters pattern, the player militia's own
    /// and the only one covering all eleven airframes, the livery an aircraft wears when
    /// nothing asks for another, as the original's stock planes do.</summary>
    public const string DefaultPattern = "player_fortune";

    private readonly SessionSpec _spec;
    private readonly string _rofPath;
    private readonly Dictionary<string, PaintScheme?> _defSchemes = new(StringComparer.OrdinalIgnoreCase);
    private List<PaintScheme>? _paintCatalog;
    private PatternLibrary? _patternLibrary;

    public LiveryResolver(SessionSpec spec, string rofPath)
    {
        _spec = spec;
        _rofPath = rofPath;
    }

    /// <summary>The original's per-pattern paint region masks, scanned once per session from
    /// the extracted UI archive. Empty (and a one-line note) when ExtractRof.ps1 has not been
    /// run, aircraft then build unpainted rather than failing.</summary>
    public PatternLibrary Patterns => _patternLibrary ??= PatternLibrary.Load(_rofPath);

    /// <summary>True when the launch asked for a livery by hand (<c>--paint=</c>,
    /// <c>--paint-color=</c>, <c>--paint-decal=</c>). An AI aircraft's own militia scheme yields to
    /// that: a typed request is about this run, not about who the plane is.</summary>
    public bool PaintRequested => _spec.PaintNames is { Count: > 0 }
        || _spec.PaintColorOverride != null || _spec.PaintDecalOverride != null;

    /// <summary>The 12 named schemes shipped in vehicle.json, loaded once per session.
    /// Empty on a read failure, paint is cosmetic and must never block a build.</summary>
    public List<PaintScheme> PaintCatalog(string zrdrPath)
    {
        if (_paintCatalog != null)
            return _paintCatalog;
        try
        {
            _paintCatalog = PaintScheme.LoadCatalog(zrdrPath);
            Log.Info("world", $"[paint] {_paintCatalog.Count} shipped patterns: {string.Join(", ", _paintCatalog.ConvertAll(s => s.Pattern))}");
        }
        catch (Exception e)
        {
            Log.Info("world", $"[paint] vehicle.json paint catalog unavailable ({e.Message}), flying unpainted");
            _paintCatalog = new List<PaintScheme>();
        }
        return _paintCatalog;
    }

    /// <summary>The livery a vehicle def authors for itself, cached per def name. This is what an AI
    /// aircraft wears in the original: the spawn resolves each paint field against the def and only
    /// an explicit override displaces it (docs/org/paint.md). Null when the def's chain authors no
    /// pattern, or when vehicle.json cannot be read, paint is cosmetic and never blocks a build.</summary>
    public PaintScheme? DefScheme(string zrdrPath, string defName)
    {
        if (_defSchemes.TryGetValue(defName, out var cached))
            return cached;
        PaintScheme? scheme = null;
        try
        {
            scheme = PaintScheme.ForDef(zrdrPath, defName);
        }
        catch (Exception e)
        {
            Log.Info("world", $"[paint] no authored scheme for '{defName}' ({e.Message}), falling back");
        }
        _defSchemes[defName] = scheme;
        return scheme;
    }

    /// <summary>The patterns this aircraft has masks for, the list the original's paint UI
    /// offers for that plane. Empty when the model carries no skin prefix to key on.</summary>
    public List<string> PatternsForPlane(GameZ planesGamez, string planeNode)
    {
        var root = planesGamez.FindByName(planeNode);
        var prefix = root != null ? PlanePainter.PrefixFor(planesGamez, root) : null;
        return prefix != null ? Patterns.PatternsFor(prefix) : new List<string>();
    }

    /// <summary>The livery player <paramref name="index"/> flies, or null to build the shipped
    /// unpainted skins. With no --paint= this is <see cref="DefaultPattern"/> everywhere;
    /// --paint=none asks for the bare shipped skins. <paramref name="useDefaultPattern"/> false
    /// drops that default for an enemy that must not wear the player militia's colours, which is
    /// the case for a wave whose own militia the file never named (docs/formats/instant-action.md).</summary>
    public PaintScheme? SchemeFor(int index, string zrdrPath, RandomNumberGenerator rng,
        IReadOnlyList<string>? available = null, bool useDefaultPattern = true)
    {
        // --paint= takes one name per player like --plane=; the last covers any remainder.
        string? name = _spec.PaintNames is { Count: > 0 }
            ? _spec.PaintNames[Math.Min(index, _spec.PaintNames.Count - 1)]
            : null;

        // Resolve the no-paint cases before touching vehicle.json, so an unpainted view does
        // no extra work and logs nothing (it is the pre-paint behaviour verbatim).
        if (string.Equals(name, "none", StringComparison.OrdinalIgnoreCase))
            return null;
        if (name == null && !useDefaultPattern)
            return null;

        var catalog = PaintCatalog(zrdrPath);

        // The implicit default resolves against the catalog only: an unknown pattern warning
        // makes no sense for a name the caller never typed, and without vehicle.json there are
        // no Fortune colours to wear, so the aircraft falls back to the shipped skins.
        if (name == null)
        {
            var fortune = catalog.Find(s => string.Equals(s.Pattern, DefaultPattern, StringComparison.OrdinalIgnoreCase));
            if (fortune == null)
                Log.Info("world", $"[paint] no '{DefaultPattern}' entry in the paint catalog, flying unpainted");
            return fortune != null ? WithOverrides(fortune) : null;
        }

        PaintScheme scheme;
        if (string.Equals(name, "random", StringComparison.OrdinalIgnoreCase))
        {
            // A pattern is per aircraft, so a random livery draws from the ones THIS plane
            // actually has masks for, picking one it does not carry would paint nothing.
            scheme = PaintScheme.Random(rng, catalog, available);
        }
        else
        {
            // Named: take the catalog's canonical colours when vehicle.json knows the pattern,
            // else a bare scheme (BROADWAY/ITSTAXI ship masks but no vehicle def names them).
            var known = catalog.Find(s => string.Equals(s.Pattern, name, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(s.FolderName, name, StringComparison.OrdinalIgnoreCase));
            bool haveMasks = available == null || available.Count == 0
                || ContainsPattern(available, name)
                || (known != null && ContainsPattern(available, known.FolderName));
            if (known == null && !haveMasks)
            {
                var offer = available is { Count: > 0 } ? string.Join(", ", available) : "(no pattern library)";
                Log.Info("world", $"[paint] unknown pattern '{name}', this aircraft has: {offer}, random, none");
                return null;
            }
            scheme = known ?? new PaintScheme { Pattern = name };
            if (!haveMasks)
                Log.Info("world", $"[paint] pattern '{name}' ships no skins for this aircraft, it has: {string.Join(", ", available!)}; painting decals only");
        }

        return WithOverrides(scheme);
    }

    /// <summary>The RNG the session's random liveries draw from: the master seed's paint stream,
    /// so an unpinned launch repaints the field and a pinned one repeats it. --paint-seed=N
    /// overrides the derived seed, pinning liveries alone in an otherwise random run.
    /// ⚠ Construct/advance it the same number of times, same order relative to the other
    /// per-session RNGs, every launch, reordering reshuffles pinned liveries under --det.</summary>
    public RandomNumberGenerator NewPaintRng() => new()
    {
        Seed = _spec.PaintSeedExplicit ? _spec.PaintSeed : Rng.SeedFor(Rng.Paint),
    };

    private static bool ContainsPattern(IReadOnlyList<string> list, string name)
    {
        foreach (var p in list)
            if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // An explicit --paint-color=/--paint-decal= list overrides whatever the scheme
    // brought, so a single colour can be dialled in against a chosen pattern.
    private PaintScheme WithOverrides(PaintScheme scheme)
    {
        if (_spec.PaintColorOverride == null && _spec.PaintDecalOverride == null)
            return scheme;
        return new PaintScheme
        {
            Pattern = scheme.Pattern,
            Color1 = _spec.PaintColorOverride?[0] ?? scheme.Color1,
            Color2 = _spec.PaintColorOverride?[1] ?? scheme.Color2,
            Color3 = _spec.PaintColorOverride?[2] ?? scheme.Color3,
            NoseDecal = _spec.PaintDecalOverride?[0] ?? scheme.NoseDecal,
            TailDecal = _spec.PaintDecalOverride?[1] ?? scheme.TailDecal,
            WingDecal = _spec.PaintDecalOverride?[2] ?? scheme.WingDecal,
        };
    }
}
