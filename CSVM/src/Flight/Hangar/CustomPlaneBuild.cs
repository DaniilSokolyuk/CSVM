using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;

namespace CSVM.Flight.Hangar;

/// <summary>
/// The join from a saved <see cref="CustomPlaneDef"/> to the three things a spawn consumes: a
/// <see cref="LoadoutDef"/> to bind, a <see cref="PaintScheme"/> to build the model with, and a
/// <see cref="PlaneDamage"/> ledger carrying the bought armour. Pure, so it tests without a
/// session or a model; it starts from the airframe's stock def and overwrites only what the
/// record chooses, leaving <c>WeaponId</c> null so <see cref="LoadoutChoice.ApplyTo"/> composes.
///
/// <para>⚠ Total weight reaches nothing here on purpose: it is hangar-only in the original
/// (docs/org/hangar.md, "Into the mission"). The engine pick reaches flight through
/// <see cref="EnginePowerFor"/> (the original's registry override) and <see cref="HasNitrous"/>
/// (the injector, <see cref="NitroSystem.Installed"/>).</para>
/// </summary>
public static class CustomPlaneBuild
{
    /// <summary>Armour is bought in units and stored in the original's record premultiplied by
    /// five, which is the same scale the shipped <c>destroyable_parts</c> armour pools are on
    /// (stock allocations 15/20/25/30/35/40, docs/formats/vehicle.md), so a unit count reaches
    /// the pool by this factor and no other rescaling.</summary>
    public const int ArmourUnitScale = 5;

    /// <summary>Each airframe's Lvl-1 row id in engines.json, the original's own map
    /// (FUN_00416e10, docs/org/hangar.md "Into the mission"); the picked tier adds 0/1/2.</summary>
    public static readonly int[] EngineRegistryBase = { 37, 19, 40, 10, 25, 22, 31, 16, 28, 13, 34 };

    /// <summary>The four hangar zones in the record's own order, spelled as the vehicle defs
    /// spell them.</summary>
    private static readonly string[] ArmourZones = { "nose", "tail", "leftwing", "rightwing" };

    /// <summary>The loadout a custom plane flies: <paramref name="stockBase"/> (its airframe's
    /// stock fit, which names the model and the mounts) with the record's own gun picks written
    /// over it and its own pylons hung from the two hardpoint counts. The base is never
    /// mutated.</summary>
    public static LoadoutDef LoadoutFor(CustomPlaneDef def, LoadoutDef stockBase)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(stockBase);
        var built = new LoadoutDef
        {
            Def = stockBase.Def,
            Model = stockBase.Model,

            // The pilot named this plane, so the dumps and suite messages name it too.
            Display = string.IsNullOrWhiteSpace(def.Name) ? stockBase.Display : def.Name,
        };
        for (int slot = 1; slot <= CustomPlaneDef.GunSlots; slot++)
        {
            if (GunFor(def, stockBase, slot) is { } spec)
            {
                built.Guns.Add(spec);
            }
        }

        built.Hardpoints = HardpointsFor(def);
        return built;
    }

    /// <summary>The livery a custom plane wears: the record's three resolved paint colours and its
    /// three decals under <paramref name="patternName"/> (the caller resolves the record's 0-13
    /// index through the engine's pattern-name table). The decal indices are the same 0-49 space
    /// <c>vehicle.json</c>'s <c>paint_decalN</c> uses, so they carry straight over; a slot the
    /// record leaves unchosen keeps the scheme's leave-the-placeholder sentinel.</summary>
    public static PaintScheme PaintFor(CustomPlaneDef def, string patternName)
    {
        ArgumentNullException.ThrowIfNull(def);
        return new PaintScheme
        {
            Pattern = patternName,
            Color1 = PaintScheme.FromBytes(def.Colour1.R, def.Colour1.G, def.Colour1.B),
            Color2 = PaintScheme.FromBytes(def.Colour2.R, def.Colour2.G, def.Colour2.B),
            Color3 = PaintScheme.FromBytes(def.Colour3.R, def.Colour3.G, def.Colour3.B),
            NoseDecal = def.NoseDecal,
            TailDecal = def.TailDecal,
            WingDecal = def.WingDecal,
        };
    }

    /// <summary>The damage ledger for a custom plane: the airframe's own zones with the bought
    /// armour standing in for each zone's armour pool, structure (hit points) untouched. The
    /// whole-vehicle pair stays whatever <paramref name="stats"/> resolves, which for every player
    /// def is null and therefore the sum over the zones as rebuilt here.</summary>
    public static PlaneDamage DamageFor(PlaneStats stats, CustomPlaneDef def)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new PlaneDamage(ArmouredParts(stats.DestroyableParts, def), stats.VehicleArmor, stats.VehicleHealth);
    }

    /// <summary>The airframe's destroyable parts with the record's armour written onto the four
    /// zones it names, as fresh objects: the parts on a <see cref="PlaneStats"/> are cached and
    /// shared between every plane of that airframe, so overwriting one in place would repaint the
    /// stock aircraft too. A zone the record does not name is carried across untouched, which is
    /// what an airframe modelling some other set of parts gets.</summary>
    public static List<DestroyablePart> ArmouredParts(IReadOnlyList<DestroyablePart> parts, CustomPlaneDef def)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(def);
        var built = new List<DestroyablePart>(parts.Count);
        foreach (var part in parts)
        {
            built.Add(UnitsFor(def, part.Name) is { } units ? WithArmour(part, units * ArmourUnitScale) : part);
        }

        return built;
    }

    /// <summary>The armour units the record buys for a zone, or null when it names no such
    /// zone.</summary>
    public static int? UnitsFor(CustomPlaneDef def, string zone)
    {
        ArgumentNullException.ThrowIfNull(def);
        for (int i = 0; i < ArmourZones.Length; i++)
        {
            if (string.Equals(ArmourZones[i], zone, StringComparison.OrdinalIgnoreCase))
            {
                return i switch
                {
                    0 => def.ArmourNose,
                    1 => def.ArmourTail,
                    2 => def.ArmourLeftWing,
                    _ => def.ArmourRightWing,
                };
            }
        }

        return null;
    }

    // A copy carrying a different armour pool. InjureAnims is shared rather than cloned: it is
    // read-only once PlaneStats has loaded it, and nothing here or downstream writes to it.
    /// <summary>The picked engine's authored power scalar out of engines.json, or null when the
    /// pick is stock (id 6) so the airframe's own row stands. Ids 3-5 are the same tiers with
    /// nitrous (<see cref="HasNitrous"/>).</summary>
    public static float? EnginePowerFor(string zrdrPath, CustomPlaneDef def)
    {
        if (def.Engine is < 0 or >= CustomPlaneDef.EngineNone
            || def.Airframe < 0 || def.Airframe >= EngineRegistryBase.Length)
        {
            return null;
        }

        int rowId = EngineRegistryBase[def.Airframe] + (def.Engine % 3);
        return EnginePowerForRow(zrdrPath, rowId);
    }

    /// <summary>The engine pick's nitrous injector bit: ids 3-5 are the three tiers with it, and
    /// nothing else (the original's <c>FUN_00416ee0</c> decomposition, docs/org/hangar.md).</summary>
    public static bool HasNitrous(CustomPlaneDef def) => def.Engine is >= 3 and <= 5;

    private static float? EnginePowerForRow(string zrdrPath, int rowId)
    {
        foreach (var row in Zrdr.LoadFile(zrdrPath, "engines.json"))
        {
            if (row is List<object?> { Count: >= 3 } r && r[0] is float id && (int)id == rowId
                && r[2] is float power)
            {
                return power;
            }
        }

        return null;
    }

    private static DestroyablePart WithArmour(DestroyablePart part, float armour) => new()
    {
        Name = part.Name,
        MaxHp = part.MaxHp,
        MaxArmor = armour,
        Critical = part.Critical,
        Engine = part.Engine,
        GotHitAnim = part.GotHitAnim,
        InjureAnims = part.InjureAnims,
    };

    // One gun slot, or null when the record leaves it empty (the dropdown's id 5, which is an
    // omitted slot and not a slot with no rounds). Calibre row c is caliber 30 + 10c; the ammo
    // stays the stock slug the whole player matrix starts from, since the record stores no ammo
    // and Ammo Selection is the layer that picks one.
    private static GunSpec? GunFor(CustomPlaneDef def, LoadoutDef stockBase, int slot)
    {
        var choice = def.Guns[slot - 1];
        if (choice.Calibre is not { } calibre)
        {
            return null;
        }

        var stockSpec = StockSlot(stockBase, slot);
        var spec = new GunSpec
        {
            Slot = slot,
            Mount = stockSpec?.Mount ?? $"Gun Group {slot}",
            Caliber = 30 + (10 * Math.Clamp(calibre, 0, CustomPlaneDef.MaxCalibre)),
            Ammo = "slug",
            Turret = stockSpec?.Turret ?? false,
        };
        spec.Markers.AddRange(Markers(stockSpec, slot, choice.Twin));
        return spec;
    }

    // Slot n owns firepoint(9-2n) and firepoint(10-2n): a twin mount is ONE gun over both, a
    // single mount takes the low one (docs/formats/markers.md, "Slot to firepoint binding").
    // ⚠ The stock slot's own marker list narrows the pair when the rig is short of it: the
    // Kestrel has no firepoint8, and its stock slot 1 says so by naming one marker where every
    // other slot names two. Binding a marker the model lacks is a loud throw, so this is the
    // difference between a Kestrel that flies and one that spawns unarmed.
    private static List<string> Markers(GunSpec? stockSpec, int slot, bool twin)
    {
        var pair = new List<string> { $"firepoint{9 - (2 * slot)}", $"firepoint{10 - (2 * slot)}" };
        var wanted = twin ? pair : new List<string> { pair[0] };
        if (stockSpec == null || stockSpec.Markers.Count == 0)
        {
            return wanted;
        }

        var kept = new List<string>(wanted.Count);
        foreach (var marker in wanted)
        {
            if (stockSpec.Markers.Contains(marker, StringComparer.OrdinalIgnoreCase))
            {
                kept.Add(marker);
            }
        }

        return kept.Count > 0 ? kept : wanted;
    }

    // The hardpoint join: a wing's own count opens that many of its pylons, outboard first, and
    // each carries the high explosive the hangar's commit writes into every cell inside the
    // count. The airframe's stock fit bounds nothing here, it is the AI's and Instant Action's
    // fit, while the original reads the record's two counts alone.
    private static HardpointSpec? HardpointsFor(CustomPlaneDef def)
    {
        var chosen = new HashSet<int>();
        Take(Loadout.LeftWingPylons, def.LeftHardpoints);
        Take(Loadout.RightWingPylons, def.RightHardpoints);
        if (chosen.Count == 0)
        {
            return null;
        }

        // An entry sits on PylonFillOrder[i] physically, so an unchosen pylon keeps its slot as
        // the empty sentinel: dropping it would slide every later entry onto another pylon.
        int last = -1;
        var built = new string[Loadout.PylonFillOrder.Length];
        for (int i = 0; i < built.Length; i++)
        {
            bool take = chosen.Contains(Loadout.PylonFillOrder[i]);
            built[i] = take ? Loadout.StockOrdnance : LoadoutChoice.None;
            if (take)
            {
                last = i;
            }
        }

        Array.Resize(ref built, last + 1);
        return new HardpointSpec { Count = built.Length, Stock = built };

        void Take(int[] wing, int count)
        {
            for (int i = 0; i < wing.Length && i < count; i++)
            {
                chosen.Add(wing[i]);
            }
        }
    }

    private static GunSpec? StockSlot(LoadoutDef stockBase, int slot)
    {
        foreach (var gun in stockBase.Guns)
        {
            if (gun.Slot == slot)
            {
                return gun;
            }
        }

        return null;
    }
}
