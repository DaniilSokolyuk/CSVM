using System.Collections.Generic;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Modes;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>The player's classed candidate pool: everything selectable right now, split into the
/// original's three cycles. Rebuilt from scratch on every <see cref="Rebuild"/> call, which is the
/// original's own contract, so a runtime spawn appears and a death disappears with no extra
/// plumbing. It reads an <see cref="AimCandidateSet"/> rather than growing a parallel structure:
/// the assist's four typed lists ARE the original's four pools and their collectors are already
/// written, so what this adds is the class split and <see cref="TargetRef"/>'s identity.
/// ⚠ Ordering does not belong here. The cycle order needs the selecting plane's own basis, which a
/// pool has no business holding; it is <see cref="TargetSelection"/>'s.</summary>
public sealed class TargetPool
{
    private readonly List<TargetRef> _enemy = new();
    private readonly List<TargetRef> _ally = new();
    private readonly List<TargetRef> _nonAircraft = new();

    /// <summary>The Enemy/Objective cycle: a different, non-zero team, plus every objective.</summary>
    public IReadOnlyList<TargetRef> Enemy => _enemy;

    /// <summary>The Ally cycle: the same team, or either side unaffiliated.</summary>
    public IReadOnlyList<TargetRef> Ally => _ally;

    /// <summary>The Non-Aircraft cycle: the structures the mission's own <c>targets.zrd</c> flags
    /// <c>other_target</c>, plus the zeppelin sub-parts while a <c>LOCK_ON</c> torpedo is the
    /// selected ordnance. Never a gun emplacement.</summary>
    public IReadOnlyList<TargetRef> NonAircraft => _nonAircraft;

    /// <summary>Everything selectable, across all three cycles.</summary>
    public int Count => _enemy.Count + _ally.Count + _nonAircraft.Count;

    /// <summary>One cycle by name, so a caller stepping "the current class" needs no switch of its
    /// own.</summary>
    public IReadOnlyList<TargetRef> Of(TargetClass cls) => cls switch
    {
        TargetClass.Enemy => _enemy,
        TargetClass.Ally => _ally,
        _ => _nonAircraft,
    };

    public void Clear()
    {
        _enemy.Clear();
        _ally.Clear();
        _nonAircraft.Clear();
    }

    /// <summary>Rebuilds all three cycles through <see cref="TargetRef.Classify"/>, dropping
    /// <paramref name="self"/> by reference. The mission's SITES reach it through
    /// <paramref name="objectives"/> under their own record's flag, and <paramref name="subParts"/>
    /// only while <paramref name="selectedWeapon"/> carries <c>LOCK_ON</c>. ⚠ Never walk
    /// <see cref="AimCandidateSet.Structures"/> or its turret list: every crate and gun would land
    /// on a cycle. ⚠ <paramref name="ownTeam"/> is the <c>FlightController.Team</c> FIELD.</summary>
    public void Rebuild(AimCandidateSet scan, IReadOnlyList<AimCandidate>? subParts, int ownTeam,
        object? self, IReadOnlyList<AimCandidate>? objectives = null,
        WeaponDef? selectedWeapon = null)
    {
        Clear();
        foreach (var c in scan.Vehicles)
        {
            Offer(c, AimTargetKind.Vehicle, ownTeam, self);
        }

        // The fourth pool. Its collector lists every round the engine wraps, fused or TARGETABLE;
        // only a TARGETABLE one carries a source, so the admission byte is what admits it here.
        foreach (var c in scan.Ordnance)
        {
            Offer(c, AimTargetKind.Ordnance, ownTeam, self);
        }

        if (subParts != null && PartsSelectable(selectedWeapon))
        {
            foreach (var c in subParts)
            {
                Offer(c, AimTargetKind.Structure, ownTeam, self);
            }
        }

        if (objectives == null)
        {
            return;
        }

        foreach (var c in objectives)
        {
            // The mission's own record decides the cycle, not the channel: a targets.zrd entry is
            // flagged `objective` (+0x4d, the Enemy cycle) or `other_target` (+0x4c, the
            // Non-Aircraft one). A Danger Zone arrives here with neither and is an objective.
            Offer(c, AimTargetKind.Structure, ownTeam, self,
                objectiveTarget: c.Source is not ObjectiveSite site || site.Objective);
        }
    }

    /// <summary>Files one already-built ref under its own class. Public so a caller with a source
    /// this pool does not know about can still contribute one.</summary>
    public void Add(in TargetRef target)
    {
        switch (target.Class)
        {
            case TargetClass.Enemy:
                _enemy.Add(target);
                break;
            case TargetClass.Ally:
                _ally.Add(target);
                break;
            default:
                _nonAircraft.Add(target);
                break;
        }
    }

    /// <summary>The IDENTITY name for a source: the plain node/label name. What <c>--target=</c>
    /// matches and what the breadcrumbs print, see <see cref="TargetRef.DisplayName"/> for what the
    /// marker prints instead. Internal rather than private: <c>FlightController.SelectRankedTarget</c>
    /// (D12/D36) reuses this same identity for every candidate's <c>rating_biases</c> name match,
    /// hull and turret and structure alike, rather than growing a second name-of-source switch.</summary>
    internal static string NameOf(object? source) => source switch
    {
        FlightController fc => fc.Name,
        SurfaceVehicle hull => hull.Name,
        ObjectiveSite site => site.Node,
        StuntZone zone => zone.DzName,
        TurretController t => t.Label,
        ProjectilePool.Flyout f => f.Name,
        DestructibleRegistry.Instance inst =>
            GodotObject.IsInstanceValid(inst.Anchor) ? inst.Anchor.Name : inst.Def.Name,
        _ => "",
    };

    /// <summary>Every name a candidate ALSO answers to, nearest first, appended to
    /// <paramref name="into"/>: a zeppelin zone's owning airship, then each node above the pool's
    /// anchor in the world tree. Nothing is appended for a whole thing in its own right.
    /// ⚠ The tree half is a docked mission structure's only reach: its engines and guns are pools
    /// named for themselves and no owner is fanned onto them, so the airship's own node name
    /// appears nowhere but above them (docs/org/aiPilot.md).</summary>
    internal static void CollectOwners(object? source, List<string> into)
    {
        if (source is not DestructibleRegistry.Instance inst)
        {
            return;
        }

        if (inst.Owner is { Length: > 0 } owner)
        {
            into.Add(owner);
        }

        if (!GodotObject.IsInstanceValid(inst.Anchor))
        {
            return;
        }

        into.AddRange(OwnerTreeOf(inst));
    }

    /// <summary>Whether a turret candidate stands in the world rather than being carried by an
    /// aircraft. An emplacement is the one with a placement <see cref="TurretController.Site"/>.
    /// The AI's ranked pool takes only emplacements: a carried gunner's host is already ranked in
    /// its own right, and offering both would put two entries on one silhouette. Lives here rather
    /// than on the AI's own path so the carried/emplacement split is written once; the PLAYER's
    /// cycle asks nothing of it, since no turret reaches that cycle at all.</summary>
    internal static bool IsEmplacement(object? source) =>
        source is TurretController { Site: not null };

    // The torpedo gate on the zeppelin sub-part channel. No shipped mission table flags a gasbag,
    // an engine or a cannon, so those parts stand in for a flag nothing authors, and the stand-in
    // is offered only for the capability it exists for: aiming a LOCK_ON torpedo at an airship.
    // Under any other selected ordnance the cycle is the original's flagged list alone
    // (docs/org/targeting.md). ⚠ Read the SELECTED weapon, never the fired one: a torpedo steers
    // to the selection, so a part must be pickable before the launch.
    private static bool PartsSelectable(WeaponDef? selectedWeapon) =>
        selectedWeapon != null && ProjectilePool.CarriesLockOn(selectedWeapon);

    // The gamez tree, by the node's ORIGINAL name: Godot sanitizes and de-duplicates Name, and an
    // authored pattern names the node the mission data named. ⚠ Held on the instance rather than
    // walked per ask: every AI shooter asks every ranked structure candidate for this chain on
    // every physics tick, and each ancestor's name comes back out of Godot as a fresh string and a
    // fresh finalizable StringName wrapper, which is the rate the collector's pause follows
    // (docs/verification.md PERF-20). The walk repeats only when the anchor's own parent changes.
    private static List<string> OwnerTreeOf(DestructibleRegistry.Instance inst)
    {
        var parent = inst.Anchor.GetParent();
        ulong parentId = parent != null ? parent.GetInstanceId() : 0UL;
        if (inst.CachedOwnerTree is { } cached && inst.CachedOwnerTreeParent == parentId)
        {
            return cached;
        }

        var names = new List<string>();
        for (var n = parent; n is Node3D up; n = n.GetParent())
        {
            names.Add(AnimRuntime.NameOf(up));
        }

        inst.CachedOwnerTree = names;
        inst.CachedOwnerTreeParent = parentId;
        return names;
    }

    /// <summary>Wraps one classed candidate as a <see cref="TargetRef"/>. The KIND picks the shape
    /// (which is why an unrecognised source still lands in the right cycle with an empty name rather
    /// than vanishing); the source supplies only the strings and the health figures. This is the one
    /// place in the targeting path that reads a concrete source type at all.</summary>
    private static TargetRef Describe(AimCandidate c, AimTargetKind kind, TargetClass cls,
        bool objective)
    {
        string name = NameOf(c.Source);
        switch (kind)
        {
            case AimTargetKind.Vehicle when c.Source is SurfaceVehicle hull:
                // ⚠ The hull's own block slot 20 alone, and an EMPTY one draws no name line: most
                // ship blocks author none and the original then labels nothing there (docs/org/
                // targeting.md). Never the airframe-style fallback the aeroplane arm takes below.
                return TargetRef.ForHull(c, cls, name, hull.MarkerName, objective: objective);
            case AimTargetKind.Vehicle:
                var plane = c.Source as FlightController;
                var dmg = plane?.Damage;
                // The MARKER prints the airframe's common name (plane type alone, decision 10),
                // not the node name the selection is held and pinned by. A rig with no flight model
                // bound has no airframe to name, and falls back to that node name.
                return TargetRef.ForAircraft(c, cls, name,
                    plane?.Stats is { } stats ? PlaneRoster.PlaneDisplayName(stats) : null,
                    dmg == null ? null : TargetRef.Fraction(dmg.WholeHealth, dmg.WholeHealthMax),
                    dmg == null ? null : TargetRef.Fraction(dmg.WholeArmor, dmg.WholeArmorMax),
                    objective, plane?.ObjectiveTypeLabel, plane?.ObjectiveCategory);
            case AimTargetKind.Turret:
                // No health figure exists for an emplacement: the retail loaders read no HEALTH key
                // and its aliveness is its healthy node's visibility.
                return TargetRef.ForTurret(c, cls, name);
            case AimTargetKind.Ordnance:
                var round = c.Source as ProjectilePool.Flyout;
                // The marker prints the weapon's own DESC. The original hard-codes message 0x2f6a
                // there (MSG_WEAP_AERIAL_TORPEDO) for every wrapper it builds, which is the same
                // string on the one entry that can reach this, and honest on any other.
                return TargetRef.ForOrdnance(c, cls, name, round?.Weapon.DisplayName,
                    round == null ? null : TargetRef.Fraction(round.Health, round.HealthMax));
            default:
                // A flagged site is scenery the MISSION named, so its two label lines and its own
                // name come off the target table rather than off a health model it has none of.
                if (c.Source is ObjectiveSite site)
                {
                    return TargetRef.ForStructure(c, cls, name, site.TypeLabel, site.Category,
                        objective, displayName: site.DisplayName);
                }

                // A Danger Zone is labelled off the same targets.zrd triple every other objective
                // site is, so it reads "Danger Zone [Fly Through] - Train Tunnel Mid" and takes the
                // non-destructive objective's blue, which is the original's own stunt marker.
                if (c.Source is StuntZone zone)
                {
                    return TargetRef.ForStructure(c, cls, name, zone.Category, zone.Help,
                        objective: true,
                        displayName: zone.Description.Length > 0 ? zone.Description : zone.DzName);
                }

                var inst = c.Source as DestructibleRegistry.Instance;
                return TargetRef.ForStructure(c, cls, name,
                    health: inst == null ? null : TargetRef.Fraction(inst.Health, inst.MaxHealth));
        }
    }

    private void Offer(AimCandidate c, AimTargetKind kind, int ownTeam, object? self,
        bool objectiveTarget = false)
    {
        if (c.Source == null || ReferenceEquals(c.Source, self))
        {
            return;
        }

        // The admission byte, read explicitly rather than inferred from a non-null source: a round
        // wrapped only because it is fused is on the same list and must not become selectable.
        if (kind == AimTargetKind.Ordnance
            && c.Source is not ProjectilePool.Flyout { Targetable: true, Live: true })
        {
            return;
        }

        // An aeroplane whose own roster block authors the flag is the mission's marker: one
        // candidate, ranked Objective ahead of every Enemy Target, rather than a synthetic site
        // beside the aeroplane it stands on (docs/org/targeting.md).
        bool objective = objectiveTarget
            || (kind == AimTargetKind.Vehicle && c.Source is FlightController { ObjectiveTarget: true });

        // ⚠ Plumb objectiveTarget, never fake it through otherTarget: that lands an objective site
        // on the Non-Aircraft cycle instead of the Enemy one. A zeppelin sub-part stands in for a
        // flag nothing authors for it; a gun gets no such stand-in (docs/org/targeting.md).
        bool otherTarget = !objective && kind == AimTargetKind.Structure;
        if (TargetRef.Classify(kind, c.Live, c.Team, ownTeam, otherTarget, objective)
            is not { } cls)
        {
            return;
        }

        Add(Describe(c, kind, cls, objective));
    }
}
