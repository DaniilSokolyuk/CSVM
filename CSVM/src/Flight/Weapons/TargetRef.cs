using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>The original's three player-target cycles (<c>FUN_004b5cd0</c>, decoded in
/// docs/org/targeting.md "The class model"). Objective is NOT a fourth class: the engine carries it
/// as a companion flag on whichever cycle the objectives currently ride, and in normal play (the
/// <c>-too</c> switch off) that is <see cref="Enemy"/>, which is why the action is called
/// "Next Enemy/Objective". <see cref="TargetRef.Objective"/> is that companion.</summary>
public enum TargetClass
{
    /// <summary>A different, non-zero team, plus every mission objective in normal play.</summary>
    Enemy,

    /// <summary>The same team, or either side unaffiliated.</summary>
    Ally,

    /// <summary>A turret or structure the mission flagged <c>otherTarget</c>.</summary>
    NonAircraft,
}

/// <summary>One thing the player can select, whatever it actually is: an enemy Fury, a zeppelin
/// engine, a turret emplacement. Every consumer (the pool, the cycles, the label formatter, the
/// marker) reads this and never the underlying C# type, which is the whole point. Pure data with no
/// Godot node dependency, so all of them unit-test with no tree.
/// ⚠ It WRAPS an <see cref="AimCandidate"/> rather than restating it. Position, velocity, team,
/// liveness and the source are the same five facts the aim assist reads off the same pools through
/// the same collectors, so a second copy could only drift. What this adds is what the assist has no
/// use for: which pool, which cycle, what to print, how hurt it is.</summary>
public readonly struct TargetRef
{
    private TargetRef(AimCandidate candidate, AimTargetKind kind, TargetClass cls, bool objective,
        string name, string? displayName, string? typeLabel, string? category, float? health,
        float? armor)
    {
        Candidate = candidate;
        Kind = kind;
        Class = cls;
        Objective = objective;
        Name = name;
        DisplayName = displayName ?? name;
        TypeLabel = typeLabel;
        Category = category;
        Health = health;
        Armor = armor;
    }

    /// <summary>The wrapped candidate, the shared half of the model. Read through
    /// <see cref="Position"/>/<see cref="Velocity"/>/<see cref="Team"/>/<see cref="Live"/>/
    /// <see cref="Source"/>; exposed whole so a caller that already holds one can hand it to
    /// <see cref="AimAssist.Scan"/> without rebuilding it.</summary>
    public AimCandidate Candidate { get; }

    /// <summary>Which of the original's four pools this came out of. Reused rather than mint a
    /// second enum: the pools are the same four, and the targeting path's own class model
    /// (<see cref="Class"/>) is a separate axis over them.</summary>
    public AimTargetKind Kind { get; }

    /// <summary>Which cycle this sits in.</summary>
    public TargetClass Class { get; }

    /// <summary>The mission's <c>objectiveTarget</c> flag (entity <c>+0x4d</c>). It sorts ahead of
    /// everything else in the cycle and colours the marker by its category.</summary>
    public bool Objective { get; }

    /// <summary>The entity's own name: <c>ai1_player_kestrel</c> for an aircraft, <c>gasbag1</c> for
    /// a sub-part. CSVM's IDENTITY string, what <c>--target=</c> matches and what the
    /// breadcrumbs print. Never null; empty is legal.</summary>
    public string Name { get; }

    /// <summary>What the MARKER prints (the original's entity <c>+0x14</c>, its line 2):
    /// <c>Kestrel</c> for an aircraft, <c>Promised Land</c> for a named zeppelin, <c>gasbag1</c> for
    /// a sub-part. Never null; defaults to <see cref="Name"/>.
    /// ⚠ Split from <see cref="Name"/> for CSVM's sake, not the original's, where one string is
    /// both. The marker wants <c>Fury</c>; <c>--target=</c> wants <c>ai2_player_fury</c>, because a
    /// golden pinned on "Fury" could not say which of three Furies it meant.</summary>
    public string DisplayName { get; }

    /// <summary>The label half of the marker's line 1 (entity <c>+0x28</c>), e.g. <c>Zeppelin</c>.
    /// Null on an ordinary aircraft, which carries neither half and so renders line 1
    /// blank.</summary>
    public string? TypeLabel { get; }

    /// <summary>The category half of line 1 (entity <c>+0x3c</c>): <c>Destroy</c>, <c>Disable</c>,
    /// <c>Protect</c>. Null off an objective.</summary>
    public string? Category { get; }

    /// <summary>Health as a fraction of its own maximum, or <b>null where the source has no health
    /// model at all</b>. A turret emplacement is the shipped case: its aliveness is its healthy
    /// node's visibility, and the retail loaders read no HEALTH key. Decision 12 says omit the
    /// figure rather than print a misleading full bar, so this is genuinely optional and never
    /// defaulted.</summary>
    public float? Health { get; }

    /// <summary>Armor as a fraction of its own maximum, or null with no armor model, which is
    /// everything except an aircraft. Kept separate from <see cref="Health"/> (decision 12): a
    /// blended figure would be a number the game does not have.</summary>
    public float? Armor { get; }

    /// <summary>World position.</summary>
    public Vector3 Position => Candidate.Position;

    /// <summary>World velocity, m/s.</summary>
    public Vector3 Velocity => Candidate.Velocity;

    /// <summary>Team id (<see cref="AimAssist.NeutralTeam"/> is unaffiliated).</summary>
    public int Team => Candidate.Team;

    /// <summary>False once dead, the engine's vtable <c>+0x14</c> predicate. A dead target stays
    /// listed for a frame, so the selection drops it rather than the pool hiding it.</summary>
    public bool Live => Candidate.Live;

    /// <summary>What this actually is, handed straight back to the caller. The identity the
    /// selection is held by, never the wrapper (see <see cref="IsSameTarget"/>).</summary>
    public object? Source => Candidate.Source;

    /// <summary>Whether this sorts ahead of every sector (<c>FUN_004bbd60</c>'s two <c>key = −1</c>
    /// overrides): a mission objective, or a hostile round in flight. The second is why incoming
    /// ordnance comes up first on the Enemy cycle rather than waiting its turn by bearing.</summary>
    public bool SortsFirst =>
        Objective || (Kind == AimTargetKind.Ordnance && Class == TargetClass.Enemy);

    /// <summary>The marker's line 1, through the original's four format strings
    /// (<c>0x006253ac</c>/<c>0x006253b8</c>/<c>0x006253c0</c>/blank): both halves, label only,
    /// category only, or empty. The trailing " -" is the original's, not a separator we
    /// added.</summary>
    public string CategoryLine =>
        (TypeLabel, Category) switch
        {
            (not null, not null) => $"{TypeLabel} [{Category}] -",
            (not null, null) => $"{TypeLabel} -",
            (null, not null) => $"[{Category}] -",
            _ => "",
        };

    /// <summary>A live aircraft. Team and liveness come off the candidate;
    /// <paramref name="cls"/> is <see cref="Classify"/>'s answer, passed in rather than
    /// re-derived so the pool decides class exactly once. <paramref name="objective"/> is the
    /// roster block's own flag: this aeroplane IS the marker, labelled off its slots 38/39.</summary>
    /// <param name="displayName">The airframe's common name for the marker (<c>Fury</c>); null
    /// prints the node name, which is what a source with no roster entry has.</param>
    public static TargetRef ForAircraft(AimCandidate candidate, TargetClass cls, string name,
        string? displayName = null, float? health = null, float? armor = null,
        bool objective = false, string? typeLabel = null, string? category = null) =>
        new(candidate, AimTargetKind.Vehicle, cls, objective, name, displayName, typeLabel,
            category, health, armor);

    /// <summary>A surface vehicle's hull, which the original's vehicle pool carries beside the
    /// aircraft. The same shape <see cref="ForAircraft"/> builds, with no health or armour
    /// fraction: a ship block authors neither pool.</summary>
    /// <param name="displayName">The block's own name line; empty draws none, which is what most
    /// ship blocks author.</param>
    public static TargetRef ForHull(AimCandidate candidate, TargetClass cls, string name,
        string? displayName = null, bool objective = false) =>
        new(candidate, AimTargetKind.Vehicle, cls, objective, name, displayName, null, null,
            health: null, armor: null);

    /// <summary>A mission structure, which covers CSVM's zeppelin sub-parts, destructibles and
    /// objective sites. Health only: <c>DestructibleRegistry.Instance</c> carries
    /// <c>Health</c>/<c>MaxHealth</c> and no armor pool.</summary>
    /// <param name="displayName">What the marker prints instead of the node name, which is the
    /// site's own name on an objective; null prints the node name, as a sub-part does.</param>
    public static TargetRef ForStructure(AimCandidate candidate, TargetClass cls, string name,
        string? typeLabel = null, string? category = null, bool objective = false,
        float? health = null, string? displayName = null) =>
        new(candidate, AimTargetKind.Structure, cls, objective, name, displayName, typeLabel,
            category, health, armor: null);

    /// <summary>A turret. **No health figure exists** (see <see cref="Health"/>); do not invent one
    /// from the gate state.</summary>
    public static TargetRef ForTurret(AimCandidate candidate, TargetClass cls, string name,
        string? category = null, bool objective = false) =>
        new(candidate, AimTargetKind.Turret, cls, objective, name, null, null, category,
            health: null, armor: null);

    /// <summary>A <c>TARGETABLE</c> round in flight, the fourth pool's one selectable shape. Health
    /// only: a flyout's armour pool is the literal zero the parser writes, so
    /// <see cref="Fraction"/> reports none. No type label, the original's wrapper carries one
    /// hard-coded display string and no category.</summary>
    public static TargetRef ForOrdnance(AimCandidate candidate, TargetClass cls, string name,
        string? displayName = null, float? health = null) =>
        new(candidate, AimTargetKind.Ordnance, cls, objective: false, name, displayName, null, null,
            health, armor: null);

    /// <summary>A current/maximum pair as a 0..1 fraction, or null when the maximum is zero or
    /// negative, i.e. when the source has no pool of that kind. The one place the
    /// "no source, no figure" rule is decided, so a caller cannot accidentally default it.</summary>
    public static float? Fraction(float current, float max) =>
        max > 0f ? Mathf.Clamp(current / max, 0f, 1f) : null;

    /// <summary>Which cycle a candidate belongs to, or null when it is not selectable at all.
    /// <c>FUN_004b5cd0</c>'s order verbatim: liveness, <c>objectiveTarget</c>, <c>otherTarget</c>,
    /// the vehicle/ordnance restriction, the team split. An objective returns
    /// <see cref="TargetClass.Enemy"/>, the cycle objectives ride in normal play.</summary>
    /// <param name="otherTarget">The mission's <c>otherTarget</c> flag (entity <c>+0x4c</c>).</param>
    /// <param name="objectiveTarget">The mission's <c>objectiveTarget</c> flag (<c>+0x4d</c>).</param>
    public static TargetClass? Classify(AimTargetKind kind, bool live, int targetTeam, int ownTeam,
        bool otherTarget = false, bool objectiveTarget = false)
    {
        if (!live)
        {
            return null;
        }

        if (objectiveTarget)
        {
            return TargetClass.Enemy;
        }

        if (otherTarget)
        {
            return TargetClass.NonAircraft;
        }

        // A turret or structure carrying neither flag is not selectable at all: the mission, not
        // the world, decides what the player may lock onto.
        if (kind is not (AimTargetKind.Vehicle or AimTargetKind.Ordnance))
        {
            return null;
        }

        return targetTeam != ownTeam && targetTeam != AimAssist.NeutralTeam
            && ownTeam != AimAssist.NeutralTeam
            ? TargetClass.Enemy
            : TargetClass.Ally;
    }

    /// <summary>Whether two refs name the same thing. Matched by the SOURCE object, never by the
    /// ref itself: the original rebuilds its candidate list from scratch every frame and
    /// <c>FUN_004b6490</c> re-finds the selection by underlying entity for exactly that reason, so
    /// a sticky selection compared by wrapper would drop on the next frame. A null source matches
    /// nothing, including another null.</summary>
    public bool IsSameTarget(in TargetRef other) =>
        Source != null && ReferenceEquals(Source, other.Source);
}
