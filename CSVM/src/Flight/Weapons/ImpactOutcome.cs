using System;
using CSVM.Mech3;

namespace CSVM.Flight.Weapons;

/// <summary>Which hand-authored burst stands in for an impact when no authored asset renders it.
/// <c>None</c> means nothing stands in: an authored gamez model was instanced, or the original's
/// own row resolves to nothing; <c>Spark</c> is the single-sprite base case every surface falls
/// back to.</summary>
public enum ImpactStandIn { None, Spark, Explosion }

/// <summary>The mask a weapon's impact hook returns to <c>FUN_005ac7a0</c>, each bit removing one
/// piece of the row's own bindings. The direct damage and the splash run before and after the
/// hook and are under no bit. <c>Effects</c> covers the crater and quicksand carves and the row's
/// <c>EFFECT</c>: CSVM gates its crater carve on it and plays no row <c>EFFECT</c>.
/// The one hook in the binary (the choker's) returns <c>Sound</c>.</summary>
[Flags]
public enum ImpactSuppression
{
    None = 0,
    Sound = 1,
    Effects = 2,
    Animation = 4,
}

/// <summary>What should happen when one weapon hits one surface, the effect name, the sound, the
/// stand-in and the damage numbers, as a value with no <c>Node3D</c> and no physics space behind
/// it. <see cref="Resolve"/> decides; a caller performs.</summary>
public readonly record struct ImpactOutcome
{
    /// <summary>The authored effect selected out of the weapon's <c>IMPACT</c> table for this
    /// surface id (its <c>ANIMATION</c>, else its <c>SURFACE_ANIMATION</c>), or null when the row
    /// binds nothing. It is both the gamez node a caller tries to instance and, when that misses,
    /// the name it hands to the world-effects runtime.</summary>
    public string? EffectName { get; init; }

    /// <summary>Whether <see cref="EffectName"/> came from the row's <c>SURFACE_ANIMATION</c> slot,
    /// which the original spawns rotated from world up onto the struck surface's normal, where the
    /// plain <c>ANIMATION</c> slot keeps its fixed axis (<c>FUN_005ac7a0</c>).</summary>
    public bool SurfaceOriented { get; init; }

    /// <summary>The struck surface's own <c>SOUND</c>, null when it authors no row or the weapon's
    /// impact hook silenced it. A <c>SOUND_GROUPS</c> name resolves through the group table at
    /// play time, not here.</summary>
    public string? Sound { get; init; }

    /// <summary>Which stand-in burst applies, <c>None</c> once an authored model has rendered.</summary>
    public ImpactStandIn StandIn { get; init; }

    /// <summary>The weapon's <c>HEALTH_DAMAGE</c>, 0 when it carries none.</summary>
    public float Damage { get; init; }

    /// <summary>The weapon's <c>IMPACT_PROXIMITY</c>, the blast/effect radius in m, 0 when it
    /// carries none. A radius on a zero-damage weapon is an effect radius only.</summary>
    public float BlastRadius { get; init; }

    /// <summary>Whether the authored radius is also a positive-health damage blast, i.e. whether a
    /// caller owes the surrounding-bodies query rather than just damaging what it struck.</summary>
    public bool HasBlastDamage => Damage > 0f && BlastRadius > 0f;

    /// <summary>Decides the impact from the weapon's <c>IMPACT</c> table at the struck surface id;
    /// touches no scene, sink or sound archive (docs/org/weaponImpact.md). <c>default</c> already
    /// backfills ids the weapon names no block for (<see cref="WeaponDefs.InheritDefaultRow"/>), so
    /// do not re-add a fallback here, it would also fire on ids a weapon names and leaves empty,
    /// like <c>player</c>(6). The last parameters are caller facts: whether the round's carve landed,
    /// and whether the effects runtime carries a def of the name selected.</summary>
    public static ImpactOutcome Resolve(WeaponDef weapon, int surfaceId, bool modelResolved,
        bool hasEffectsRuntime, ImpactSuppression suppression = ImpactSuppression.None,
        bool cratered = false, bool effectBound = false)
    {
        // The struck id's IMPACT row, already carrying `default`'s binding if the weapon named no
        // block for this id (WeaponDefs.InheritDefaultRow).
        var effect = weapon.ImpactFor(surfaceId);
        // The hook's Animation bit removes the ANIMATION slot only; SURFACE_ANIMATION is spawned
        // under no bit (FUN_005ac7a0 tests the mask before the first spawn and not the second).
        string? animation = (suppression & ImpactSuppression.Animation) != 0 ? null : effect?.Animation;
        // ⚠ A carve that landed removes BOTH slots, and the stand-in that covers for them, since a
        // weapon that authored ANIMATION_ALWAYS would be the only exception and nothing does. The
        // crater IS the ground effect on the six CRATER weapons (docs/org/craters.md).
        string? name = cratered ? null : animation ?? effect?.SurfaceAnimation;

        return new ImpactOutcome
        {
            EffectName = name,
            SurfaceOriented = name != null && animation == null,
            Sound = (suppression & ImpactSuppression.Sound) != 0 ? null : effect?.Sound,
            StandIn = cratered
                ? ImpactStandIn.None
                : StandInFor(weapon, surfaceId, modelResolved, hasEffectsRuntime, effectBound),
            Damage = weapon.HealthDamage ?? 0f,
            BlastRadius = weapon.ImpactProximity ?? 0f,
        };
    }

    // The stand-in ladder, ordered: model beats all; a hardpoint weapon with no scene gets the
    // explosion; a gun on `buildings` whose name renders nowhere draws nothing; everything else
    // gets the single spark. Ground has no arm of its own but must still return non-`None`:
    // `ProjectilePool` gates the `EffectSink` call on that, and the `blacksmokepuffer` rides it.
    // ⚠ Do not stand anything in for the guns' install-missing `bld_damage.flt`: the original
    // leaves that slot null and draws nothing there (docs/org/weaponImpact.md).
    private static ImpactStandIn StandInFor(WeaponDef weapon, int surfaceId, bool modelResolved,
        bool hasEffectsRuntime, bool effectBound)
    {
        if (modelResolved)
            return ImpactStandIn.None;
        if (!weapon.IsGun && !hasEffectsRuntime)
            return ImpactStandIn.Explosion;
        if (surfaceId == SurfaceRegistry.Buildings && weapon.IsGun && !effectBound)
            return ImpactStandIn.None;
        return ImpactStandIn.Spark;
    }
}
