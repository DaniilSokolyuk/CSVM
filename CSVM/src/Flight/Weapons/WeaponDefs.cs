using System;
using System.Collections.Generic;
using CSVM.Mech3;

namespace CSVM.Flight.Weapons;

/// <summary>The per-weapon impact hook the detonation calls first (weapon <c>+0x20c</c>, written by
/// <c>FUN_005aec90</c>). One installer exists in the binary, the <c>TANGLER</c> parse, so this is an
/// enum rather than a delegate: <c>ProjectilePool.RunImpactHook</c> is the dispatch. Decode:
/// <c>docs/org/ordnanceTypes.md</c>, "Half one, the direct impact".</summary>
public enum ImpactHook
{
    None,
    Tangler,
}

/// <summary>A muzzle (<c>FIRE</c>) or per-surface (<c>IMPACT</c>) effect binding: any slot may be
/// null. <c>SurfaceAnimation</c> is the surface-oriented variant of <c>Animation</c> and is only
/// present on <c>IMPACT</c> classes.</summary>
public sealed class WeaponEffect
{
    public string? Animation;
    public string? SurfaceAnimation;
    public string? Effect;
    public string? Sound;

    public bool IsEmpty => Animation == null && SurfaceAnimation == null && Effect == null && Sound == null;
}

/// <summary>The projectile itself (<c>FLYOUT</c>): the <c>.flt</c> model handle, an optional
/// in-flight spin/trail animation, and an optional looped in-flight sound (the torpedo).</summary>
public sealed class WeaponFlyout
{
    public string? Model;
    public string? ModelAnimation;
    public string? Sound;
}

/// <summary>The choker's <c>TANGLER</c> struct: the cloud's lifetime and catch radius, and the
/// engine-dead duration range (a pair of globals in the original, see
/// <see cref="TanglerChoke.EngineDeadBounds"/>). The engine's defaults for a block omitting a key
/// are <see cref="DefaultTime"/> and <see cref="DefaultRadius"/>.</summary>
public sealed class TanglerData
{
    /// <summary>The shared <c>TIME</c> slot's default (extension <c>+0x18</c>), s.</summary>
    public const float DefaultTime = 5f;

    /// <summary>The <c>RADIUS</c> default (extension <c>+0x1c</c>), m, stored raw.</summary>
    public const float DefaultRadius = 10f;

    public float? Time;
    public float? Radius;
    public (float Min, float Max)? EngineDead;
}

/// <summary>
/// One <c>weapons.json</c> <c>BALLISTICS</c> entry, typed. See
/// <see href="../../../../docs/formats/weapons.md">weapons.md</see> for every field's measured range
/// and meaning; this mirrors that page one-to-one. Scalars arrive from the reader as floats
/// (<see cref="Mech3.Zrdr"/> convention); counts/ids are narrowed to <see cref="int"/>.
///
/// <para>Flags (<c>CANNON</c>, <c>ROCKET</c>, <c>HIGH_EXPLOSIVE</c>, …) are stored in the data as a
/// key paired with <c>null</c>; <see cref="ZrdrDict"/>'s bare-flag handling turns those into a
/// present-but-empty key, so <c>Has</c> is the correct test and they read here as bools.</para>
/// </summary>
public sealed class WeaponDef
{
    /// <summary>The <c>IMPACT</c> table, one row per <see cref="SurfaceRegistry"/> id in slot
    /// order, the original's own shape, an array indexed by surface id (<c>FUN_005ad630</c>
    /// writes each parsed block into <c>weapon + 0x15c + id*100</c>). An id the weapon names no
    /// block for holds the <c>default</c> row (see <c>InheritDefaultRow</c>); a null row is an id
    /// it named and bound nothing on, which plays nothing. Read it through
    /// <see cref="ImpactFor"/>.</summary>
    public readonly WeaponEffect?[] Impact = new WeaponEffect?[SurfaceRegistry.Names.Count];

    public string Id = "";            // "wep_00"
    public string DescKey = "";       // "MSG_WEAP_30CAL_SLUG"
    public string DisplayName = "";   // DescKey resolved through Messages (or the key if unresolved)
    public string Name = "";          // short internal handle, "30slug"
    public int? Caliber;              // gun bore, gun entries only
    public int? Priority;            // ordnance selection priority, rocket entries only

    // Allotment (see weapons.md "CLUSTER_SIZE vs AMMO_LIMIT").
    public int? ClusterSize;          // rounds carried per slot (gun group / pylon)
    public int? AmmoLimit;            // a purchase cap, not a carried count

    // Ballistics.
    public float FireRate;            // shots/second
    public float? Velocity;           // muzzle / flyout speed, m/s
    public float? Acceleration;       // rocket-motor accel, m/s²
    public float? Range;              // max effective / despawn range, m (authored; see RangeSqM)
    public float? RangeMinimum;       // flyout visibility gate, m of path travelled (torpedo)
    public float? Gravity;            // projectile-gravity scale (0 throughout this install)
    public float? CannonSpread;       // aim-assist acceptance cone half-angle, degrees, NOT a dispersion cone
    public float? FiringHeat;         // heat per shot (base guns only); dead data, the original parses it and never reads it
    public float? TurnRate;           // guidance turn rate

    // Damage.
    public float? ArmorDamage;
    public float? HealthDamage;
    public float? Damage;             // combined value replacing the split, on the 2 non-damaging specials

    // Guidance / detonation.
    public float? LockOn;             // lock-acquisition time, s
    public (float, float)? LockOnLead;
    public float? DetonationDistance; // proximity-fuse trigger distance, m (authored)
    public float? ImpactProximity;    // blast / effect radius, m (authored; also the query radius)
    public float? DetonationDotProduct;
    public float? DetonationTime;     // timed fuse, s

    /// <summary>The squared radii, held beside the authored ones exactly as <c>FUN_005ad630</c>
    /// holds them (weapon <c>+0x20</c>, <c>+0x44</c>, <c>+0x40</c>; the squaring idiom sits at
    /// <c>0x005add73</c>). Every distance the original compares them against comes from
    /// <c>FUN_00538880</c>, which returns a squared distance, so compare these against a
    /// <c>DistanceSquaredTo</c>. Never take a square root to reach the authored field instead: a
    /// root moves the boundary, and matching the engine's comparison is the whole point.</summary>
    public float? RangeSqM;
    public float? DetonationDistanceSqM;
    public float? ImpactProximitySqM;

    // Class flags.
    public bool IsCannon;             // hitscan-style gun (pairs with LoopedSoundName)
    public bool IsRocket;             // self-propelled projectile
    public bool HighExplosive;
    public bool Sonic;
    public bool Flash;
    public bool BeeperSeeker;
    public bool Rear;                 // fires rearward
    public bool Torpedo;
    public bool Targetable;           // the in-flight projectile can itself be shot down
    public bool DamagesZeppelin;
    public bool ShakesCamera;
    public bool Crater;               // ground-attack munition

    // Specials.
    public float? BeeperTime;         // BEEPER -> TIME
    public float? SmokeScreenTime;    // SMOKE_SCREEN -> TIME
    public TanglerData? Tangler;
    public ImpactHook ImpactHook;     // installed by the TANGLER parse alone (FUN_004ba6f0)
    public int? FlyoutHealth;         // HP of a TARGETABLE flyout (torpedo)
    public int? ProjectileBbox;
    public string? DestroyAnimation;  // effect when a TARGETABLE flyout is destroyed

    // Bindings.
    public string? LoopedSoundName;   // looped firing sound (guns)
    public WeaponEffect? Fire;
    public WeaponFlyout? Flyout;

    /// <summary>Keys present on this entry that the reader does not map, empty for every entry
    /// in this install (asserted by a verify pass). A non-empty list means the data grew a key
    /// this reader has not learned, and is a signal to update it, not to fail silently.</summary>
    public IReadOnlyList<string> UnhandledKeys = Array.Empty<string>();

    /// <summary>Convenience: the caliber+ammo weapon-id rule stock loadouts use lives in
    /// <c>stock_loadouts.json</c>; here, <c>IsGun</c> is just "has a caliber and fires hitscan".</summary>
    public bool IsGun => IsCannon || Caliber.HasValue;

    /// <summary>Whether this rocket's <c>TURN_RATE</c> is a real one rather than the 0.001 sentinel
    /// every dumbfire type authors: a label for the weapon lab and the probes, not the flight
    /// gate. The original steers a round on <c>LOCK_ON</c> AND a held target
    /// (<see cref="ProjectilePool.SteeringStepRuns"/>) and only then reads <c>TURN_RATE</c> for how
    /// hard it may turn, so a sentinel-rate round with a target is steered imperceptibly and a
    /// weapon without <c>LOCK_ON</c> never, whatever this says. <c>wep_11</c> alone is true.</summary>
    public bool IsGuided => TurnRate is > 0.01f;

    /// <summary>This weapon's <c>IMPACT</c> row for a struck surface id, or null when the row binds
    /// nothing. Rows an id inherits from <c>default</c> are filled at parse time, so this is a plain
    /// index (docs/org/weaponImpact.md). The bounds test is ours: the original's own lookup is
    /// unchecked.</summary>
    public WeaponEffect? ImpactFor(int surfaceId) =>
        surfaceId >= 0 && (uint)surfaceId < (uint)Impact.Length ? Impact[surfaceId] : null;
}

/// <summary>
/// Typed reader over the shared <c>weapons.zrd.json</c> <c>BALLISTICS</c> block, the whole
/// install's 48-entry projectile catalogue (guns / rockets / ordnance). Modelled on
/// <see cref="Airframe.PlaneStats"/>: load once, index by <c>wep_*</c> id. The single source every
/// weapon handler reads a <see cref="WeaponDef"/> from.
/// </summary>
public sealed class WeaponDefs
{
    // Every key the reader knows. UnhandledKeys is computed against this, so a new data key
    // surfaces instead of being silently ignored. Must stay in step with weapons.md's field list.
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "DESC", "NAME", "CALIBER", "PRIORITY",
        "CLUSTER_SIZE", "AMMO_LIMIT",
        "FIRE_RATE", "VELOCITY", "ACCELERATION", "RANGE", "RANGE_MINIMUM", "GRAVITY",
        "CANNON_SPREAD", "FIRING_HEAT", "TURN_RATE",
        "ARMOR_DAMAGE", "HEALTH_DAMAGE", "DAMAGE",
        "ROCKET", "LOCK_ON", "LOCK_ON_LEAD", "DETONATION_DISTANCE", "IMPACT_PROXIMITY",
        "DETONATION_DOT_PRODUCT", "DETONATION_TIME", "CRATER",
        "HIGH_EXPLOSIVE", "SONIC", "FLASH", "BEEPER", "BEEPER_SEEKER", "TANGLER",
        "SMOKE_SCREEN", "REAR", "TORPEDO", "TARGETABLE", "FLYOUT_HEALTH", "PROJECTILE_BBOX",
        "DESTROY_ANIMATION", "DAMAGES_ZEPPELIN", "SHAKES_CAMERA",
        "CANNON", "LOOPED_SOUND_NAME", "FIRE", "FLYOUT", "IMPACT",
    };

    private readonly Dictionary<string, WeaponDef> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WeaponDef> _all = new();

    /// <summary>The shared empty-clip sound def (<c>NO_AMMO_WARNING</c>), for the dry-gun cue.</summary>
    public string EmptyClipSound { get; private set; } = "snd_emptyclip";

    /// <summary>Every def, in file order.</summary>
    public IReadOnlyList<WeaponDef> All => _all;

    /// <summary>Loads and types every <c>BALLISTICS</c> entry. <paramref name="messages"/> resolves
    /// each <c>DESC</c> key to a display name; null leaves <see cref="WeaponDef.DisplayName"/> the
    /// raw key (visible, not blank).</summary>
    public static WeaponDefs Load(string zrdrPath, Messages? messages = null)
    {
        var outer = Zrdr.LoadFile(zrdrPath, "weapons.json");
        if (outer.Count == 0 || outer[0] is not List<object?> rootList)
            throw new InvalidOperationException("weapons.json: unexpected root shape");
        var root = ZrdrDict.FromAlternating(rootList);

        var defs = new WeaponDefs();
        defs.EmptyClipSound = root.Str("NO_AMMO_WARNING") ?? defs.EmptyClipSound;

        var ballistics = root.List("BALLISTICS")
            ?? throw new InvalidOperationException("weapons.json: no BALLISTICS block");
        for (int i = 0; i + 1 < ballistics.Count; i += 2)
        {
            if (ballistics[i] is not string id || ballistics[i + 1] is not List<object?> body)
                continue;
            var def = Parse(id, ZrdrDict.FromAlternating(body), messages);
            defs._byId[id] = def;
            defs._all.Add(def);
        }
        return defs;
    }

    public WeaponDef? Get(string id) => _byId.TryGetValue(id, out var d) ? d : null;

    /// <summary>Every sound name this catalogue binds, each paired with the <c>LOOPED</c> flag its
    /// PLAY SITE asks for, which is not the one sounds.json carries: <c>StartGunLoop</c> forces
    /// true on a firing loop, <c>ProjectilePool.PlaySound</c> false on everything else.
    /// ⚠ The pair is the prewarm key, <c>SoundArchive</c> caches per (wav, looped), so a gun loop
    /// decoded unlooped is still a cold read. Whole catalogue, not one loadout: the AI and the
    /// turrets fire from it too.</summary>
    public IReadOnlyCollection<(string Name, bool Looped)> SoundCues()
    {
        var cues = new HashSet<(string, bool)>();
        foreach (var weapon in _all)
        {
            if (!string.IsNullOrEmpty(weapon.LoopedSoundName))
            {
                cues.Add((weapon.LoopedSoundName!, true));
            }
            AddOneShot(weapon.Fire?.Sound);
            AddOneShot(weapon.Flyout?.Sound);
            foreach (var impact in weapon.Impact)
            {
                AddOneShot(impact?.Sound);
            }
        }
        return cues;

        void AddOneShot(string? name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                cues.Add((name!, false));
            }
        }
    }

    public bool TryGet(string id, out WeaponDef def)
    {
        var d = Get(id);
        def = d!;
        return d != null;
    }

    private static WeaponDef Parse(string id, ZrdrDict d, Messages? messages)
    {
        float? F(string k) => d.TryFloat(k, out var f) ? f : null;
        int? I(string k) => d.TryFloat(k, out var f) ? (int)f : null;

        var def = new WeaponDef
        {
            Id = id,
            DescKey = d.Str("DESC") ?? "",
            Name = d.Str("NAME") ?? "",
            Caliber = I("CALIBER"),
            Priority = I("PRIORITY"),
            ClusterSize = I("CLUSTER_SIZE"),
            AmmoLimit = I("AMMO_LIMIT"),
            FireRate = F("FIRE_RATE") ?? 0f,
            Velocity = F("VELOCITY"),
            Acceleration = F("ACCELERATION"),
            Range = F("RANGE"),
            RangeMinimum = F("RANGE_MINIMUM"),
            Gravity = F("GRAVITY"),
            CannonSpread = F("CANNON_SPREAD"),
            FiringHeat = F("FIRING_HEAT"),
            TurnRate = F("TURN_RATE"),
            ArmorDamage = F("ARMOR_DAMAGE"),
            HealthDamage = F("HEALTH_DAMAGE"),
            Damage = F("DAMAGE"),
            LockOn = F("LOCK_ON"),
            DetonationDistance = F("DETONATION_DISTANCE"),
            ImpactProximity = F("IMPACT_PROXIMITY"),
            DetonationDotProduct = F("DETONATION_DOT_PRODUCT"),
            DetonationTime = F("DETONATION_TIME"),
            IsCannon = d.Has("CANNON"),
            IsRocket = d.Has("ROCKET"),
            HighExplosive = d.Has("HIGH_EXPLOSIVE"),
            Sonic = d.Has("SONIC"),
            Flash = d.Has("FLASH"),
            BeeperSeeker = d.Has("BEEPER_SEEKER"),
            Rear = d.Has("REAR"),
            Torpedo = d.Has("TORPEDO"),
            Targetable = d.Has("TARGETABLE"),
            DamagesZeppelin = d.Has("DAMAGES_ZEPPELIN"),
            ShakesCamera = d.Has("SHAKES_CAMERA"),
            Crater = d.Has("CRATER"),
            FlyoutHealth = I("FLYOUT_HEALTH"),
            ProjectileBbox = I("PROJECTILE_BBOX"),
            DestroyAnimation = d.Str("DESTROY_ANIMATION"),
            LoopedSoundName = d.Str("LOOPED_SOUND_NAME"),
        };
        def.DisplayName = messages != null ? messages.Get(def.DescKey) : def.DescKey;

        // Squared once here, as the original squares them once at parse. TANGLER's RADIUS is
        // deliberately absent: FUN_004ba6f0 stores it raw and the choker compares it against a
        // squared distance anyway, a unit mismatch every player of the shipped game flew with.
        def.RangeSqM = Square(def.Range);
        def.DetonationDistanceSqM = Square(def.DetonationDistance);
        def.ImpactProximitySqM = Square(def.ImpactProximity);

        if (d.TryFloat("LOCK_ON_LEAD", out var lead0) && d.TryFloat("LOCK_ON_LEAD", out var lead1, 1))
            def.LockOnLead = (lead0, lead1);

        if (d.Dict("BEEPER") is { } beeper)
            def.BeeperTime = beeper.Float("TIME");
        if (d.Dict("SMOKE_SCREEN") is { } smoke)
            def.SmokeScreenTime = smoke.Float("TIME");
        if (d.Dict("TANGLER") is { } tangler)
        {
            def.ImpactHook = ImpactHook.Tangler;
            def.Tangler = new TanglerData
            {
                Time = tangler.TryFloat("TIME", out var t) ? t : null,
                Radius = tangler.TryFloat("RADIUS", out var r) ? r : null,
                EngineDead = tangler.TryFloat("ENGINE_DEAD", out var e0)
                             && tangler.TryFloat("ENGINE_DEAD", out var e1, 1)
                    ? (e0, e1) : null,
            };
        }

        def.Fire = ParseEffect(d.Dict("FIRE"));
        if (d.Dict("FLYOUT") is { } fly)
        {
            def.Flyout = new WeaponFlyout
            {
                Model = fly.Str("MODEL"),
                ModelAnimation = fly.Str("MODEL_ANIMATION"),
                Sound = fly.Str("SOUND"),
            };
        }
        ParseImpact(d.List("IMPACT"), def.Impact);

        // Anything the reader didn't map, empty for this install; a tripwire if the data grows.
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
            def.UnhandledKeys = unhandled;
        }
        return def;
    }

    private static float? Square(float? v) => v is { } f ? f * f : null;

    private static WeaponEffect? ParseEffect(ZrdrDict? d)
    {
        if (d == null)
        {
            return null;
        }
        var e = new WeaponEffect
        {
            Animation = d.Str("ANIMATION"),
            SurfaceAnimation = d.Str("SURFACE_ANIMATION"),
            Effect = d.Str("EFFECT"),
            Sound = d.Str("SOUND"),
        };
        return e.IsEmpty ? null : e;
    }

    // IMPACT is walked as raw name/value pairs, not through ZrdrDict, so a null row (no effect on
    // that surface) is skipped rather than read back as empty. Which names were NAMED is tracked
    // apart from which parsed to a binding, the two decide different things; see
    // InheritDefaultRow (docs/org/weaponImpact.md).
    private static void ParseImpact(List<object?>? impact, WeaponEffect?[] into)
    {
        if (impact == null)
        {
            return;
        }
        var authored = new bool[into.Length];
        for (int i = 0; i + 1 < impact.Count; i += 2)
        {
            if (impact[i] is not string surfaceName || SurfaceRegistry.IdForName(surfaceName) is not { } id)
            {
                continue;
            }
            authored[id] = true;
            if (impact[i + 1] is List<object?> slots
                && ParseEffect(ZrdrDict.FromAlternating(slots)) is { } effect)
            {
                into[id] = effect;
            }
        }
        InheritDefaultRow(into, authored);
    }

    // Gives every id the weapon names no block for the `default` row, verbatim: the original's
    // per-id loop copies row 0 whole on a miss (docs/org/weaponImpact.md). Naming an id and binding
    // nothing on it is the opposite case and stays empty. The row is shared, not cloned, nothing
    // mutates a parsed row, so one instance per weapon means this id resolves to `default`.
    private static void InheritDefaultRow(WeaponEffect?[] into, bool[] authored)
    {
        // Row 0 has nothing to inherit from: a weapon authoring no `default` row supplies no
        // template, and every id it is silent on stays silent (`wep_26`, which has no IMPACT at all,
        // never reaches here).
        if (into[SurfaceRegistry.Default] is not { } template)
        {
            return;
        }
        for (int id = SurfaceRegistry.Default + 1; id < into.Length; id++)
        {
            if (!authored[id])
            {
                into[id] = template;
            }
        }
    }
}
