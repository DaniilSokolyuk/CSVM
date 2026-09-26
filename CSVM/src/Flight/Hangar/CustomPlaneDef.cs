using System;
using System.Collections.Generic;
using CSVM.Flight.Weapons;

namespace CSVM.Flight.Hangar;

/// <summary>One paint slot's colour as the 0-255 sRGB triple the saved record stores. Not a Godot
/// <c>Color</c> so the model stays engine-free; <c>PaintScheme.FromBytes</c> is the conversion
/// when a build reaches the painter.</summary>
public readonly record struct PaintColour(byte R, byte G, byte B);

/// <summary>One gun slot's pick: the calibre dropdown row (0-4, thirty through seventy cal) or
/// empty, plus the twin bit that doubles the mount. Null calibre is the record's id 5, the
/// explicit empty slot.</summary>
public readonly record struct GunChoice(int? Calibre, bool Twin)
{
    /// <summary>Whether the slot mounts nothing (the record's gun id 5).</summary>
    public bool IsEmpty => Calibre is null;
}

/// <summary>
/// A custom-built plane as CSVM models it: exactly the decoded 204-byte record's chosen fields
/// (docs/formats/paint.md "Saved custom planes"), minus everything the original derives at commit
/// (+0x98 empties, +0xa8 display cells, the +0x28/+0x3c totals), which are recomputed, never
/// stored. Engine-free by design: screens edit it, the economy prices it, and
/// <see cref="CustomPlaneStore"/> persists it as CSVM's own JSON; the 204-byte format itself is
/// import-only.
/// </summary>
public sealed class CustomPlaneDef
{
    /// <summary>Airframe ids run 0-10, the stat table's 11 rows.</summary>
    public const int MaxAirframe = 10;

    /// <summary>Engine ids run 0-6; 6 is the explicit no-engine pick the purchase gate rejects.</summary>
    public const int EngineNone = 6;

    /// <summary>Armour is bought by the press, 0-12 per zone, one press being
    /// <see cref="HangarEconomy.ArmourUnitsPerStep"/> units. ⚠ The zone fields below count
    /// presses, so the record's own 0-60 unit count is five times any of them and the field is
    /// priced through <see cref="HangarEconomy.ArmourStepCost"/>, never the per-unit one.</summary>
    public const int MaxArmourUnits = 12;

    /// <summary>Every airframe has exactly four gun slots; slot titles vary, the count does not.</summary>
    public const int GunSlots = 4;

    /// <summary>Gun calibre dropdown rows 0-4.</summary>
    public const int MaxCalibre = 4;

    /// <summary>Hardpoints are counted per wing, 0-4.</summary>
    public const int MaxHardpointsPerWing = 4;

    /// <summary>Paint pattern indices run 0-13.</summary>
    public const int MaxPaintPattern = 13;

    /// <summary>Swatch-table rows run 0-26 (record +0x44..+0x4c).</summary>
    public const int MaxSwatch = 26;

    /// <summary>The decal set is the gapless 00-49 texture series (record +0x5c..+0x64).</summary>
    public const int MaxDecal = 49;

    /// <summary>A decal slot keeping the aircraft's shipped placeholder texture. Not a value the
    /// original can store: a fresh build has no authored decal (the pattern defaults carry none),
    /// and this says so rather than claiming decal 0.</summary>
    public const int KeepDecal = -1;

    /// <summary>An ammunition slot nobody has picked for. Not a value the campaign record has: its
    /// own field starts at 0, which IS an ammunition (slug), so a stored plane needs a value
    /// meaning "left alone" or every hangar-built plane written before the field existed would
    /// start claiming slug on all four mounts.</summary>
    public const int NoAmmoPick = -1;

    /// <summary>A pylon nobody has picked for, the same 0 the campaign record uses (its ordnance
    /// values are one-based for exactly this reason).</summary>
    public const int NoOrdnancePick = 0;

    // The longest shipped ramp is ten, so this is the ceiling a shade clamps to when no swatch
    // table loaded and the row's own length is unknown.
    private const int MaxShadeWithoutTable = 9;

    /// <summary>The plane's name. Also its identity in the store: the original saves
    /// <c>Planes\%s</c> and overwrites by name, and our store keeps that contract.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Airframe id, 0-10 (record +0x2c).</summary>
    public int Airframe { get; set; }

    /// <summary>Engine id, 0-6 with 6 = none (defaults to none, as an unstarted build has no
    /// engine to fly on).</summary>
    public int Engine { get; set; } = EngineNone;

    /// <summary>Nose armour presses, 0-12 (record +0x74, holding the units, so x5).</summary>
    public int ArmourNose { get; set; }

    /// <summary>Tail armour presses, 0-12 (record +0x78).</summary>
    public int ArmourTail { get; set; }

    /// <summary>Left wing armour presses, 0-12 (record +0x7c).</summary>
    public int ArmourLeftWing { get; set; }

    /// <summary>Right wing armour presses, 0-12 (record +0x80).</summary>
    public int ArmourRightWing { get; set; }

    /// <summary>The four gun slots (record +0x84 twin bits, +0x88-0x94 ids). Fixed length
    /// <see cref="GunSlots"/>; entries default to empty.</summary>
    public GunChoice[] Guns { get; } = new GunChoice[GunSlots];

    /// <summary>Left wing hardpoint count, 0-4 (record +0x34).</summary>
    public int LeftHardpoints { get; set; }

    /// <summary>Right wing hardpoint count, 0-4 (record +0x38).</summary>
    public int RightHardpoints { get; set; }

    /// <summary>Paint pattern index, 0-13 (record +0x40; 13 remaps to 11 for the icon, a display
    /// rule that stays out of the model).</summary>
    public int PaintPattern { get; set; }

    /// <summary>The three paint slots' swatch rows, 0-26 (record +0x44..+0x4c). Slot 0 is the
    /// identity/body colour, 1 the dark trim, 2 the light trim.</summary>
    public int[] PaintColours { get; } = new int[HangarPaintTables.Slots];

    /// <summary>The three paint slots' shade variants within their colour's ramp (record
    /// +0x50..+0x58). A colour pick resets its slot's shade; see <see cref="SetPaintColour"/>.</summary>
    public int[] PaintShades { get; } = new int[HangarPaintTables.Slots];

    /// <summary>Nose decal, 0-49 or <see cref="KeepDecal"/> (record +0x5c).</summary>
    public int NoseDecal { get; set; } = KeepDecal;

    /// <summary>Tail decal, 0-49 or <see cref="KeepDecal"/> (record +0x60).</summary>
    public int TailDecal { get; set; } = KeepDecal;

    /// <summary>Wing decal, 0-49 or <see cref="KeepDecal"/> (record +0x64).</summary>
    public int WingDecal { get; set; } = KeepDecal;

    /// <summary>Per-gun-slot ammunition pick, in <c>Session.Campaign.OwnedPlane.Ammo</c>'s own encoding, or
    /// <see cref="NoAmmoPick"/> for a slot the campaign never fitted. Optional in the stored file:
    /// the campaign's EXPORT is what writes it, so a plane built in the hangar carries none and
    /// flies its base fit.</summary>
    public int[] Ammo { get; } = NothingPicked();

    /// <summary>Per-pylon ordnance pick, eight cells, in <c>Session.Campaign.OwnedPlane.Ordnance</c>'s own
    /// one-based encoding with <see cref="NoOrdnancePick"/> for a cell the campaign never fitted.
    /// Optional in the stored file, exactly as <see cref="Ammo"/> is.</summary>
    public int[] Ordnance { get; } = new int[LoadoutChoice.MaxPylon];

    /// <summary>Whether the campaign has exported a loadout into this record at all. What decides
    /// whether the stored file carries the block, so a hangar-built plane's file is what it was
    /// before this field existed.</summary>
    public bool HasLoadout
    {
        get
        {
            foreach (int pick in Ammo)
            {
                if (pick != NoAmmoPick)
                {
                    return true;
                }
            }

            foreach (int cell in Ordnance)
            {
                if (cell != NoOrdnancePick)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether the campaign built or granted this aircraft and nobody has pressed EXPORT on
    /// it yet, which is what keeps it out of the Instant Action and Free Flight pickers. ⚠ False is
    /// the meaning of an absent field and must stay so: a file written before this existed, and every
    /// plane built at a wallet-free door, reads as exported.</summary>
    public bool AwaitingExport { get; set; }

    /// <summary>Paint slot 1, the identity/body colour, resolved from its index pair. Derived, as
    /// the original's own record +0x68 is: <c>FUN_00406840</c> recomputes that RGBA cache from the
    /// colour and shade indices before every save, so the pair is what a plane's paint IS.</summary>
    public PaintColour Colour1 => PaintColourAt(0);

    /// <summary>Paint slot 2, the dark trim, resolved from its index pair.</summary>
    public PaintColour Colour2 => PaintColourAt(1);

    /// <summary>Paint slot 3, the light trim, resolved from its index pair.</summary>
    public PaintColour Colour3 => PaintColourAt(2);

    /// <summary>A slot's resolved RGB, through the swatch table (<c>FUN_004067c0</c>).</summary>
    public PaintColour PaintColourAt(int slot) =>
        slot >= 0 && slot < PaintColours.Length
            ? HangarPaintTables.Default.Resolve(PaintColours[slot], PaintShades[slot])
            : default;

    /// <summary>Picks a slot's colour, resetting its shade to that swatch row's own default. The
    /// reset is the original's (callback 2237 at <c>0x0040d402</c>) and is why picking a colour in
    /// the paint UI lands on the artist's chosen brightness rather than keeping the old one.</summary>
    public void SetPaintColour(int slot, int colour)
    {
        if (slot < 0 || slot >= PaintColours.Length)
        {
            return;
        }

        PaintColours[slot] = Math.Clamp(colour, 0, MaxSwatch);
        PaintShades[slot] = HangarPaintTables.Default.DefaultShadeFor(PaintColours[slot]);
    }

    /// <summary>Loads a pattern's six colour/shade defaults over this plane's own, which is all
    /// selecting a pattern does in the original (callback 2238's SET at <c>0x0040d4ba</c>).</summary>
    public void LoadPatternDefaults(int pattern)
    {
        PaintPattern = Math.Clamp(pattern, 0, MaxPaintPattern);
        if (HangarPaintTables.Default.PatternEntry(PaintPattern) is not { } entry)
        {
            return;
        }

        for (int slot = 0; slot < PaintColours.Length; slot++)
        {
            if (slot < entry.Colours.Count && slot < entry.Shades.Count)
            {
                PaintColours[slot] = entry.Colours[slot];
                PaintShades[slot] = entry.Shades[slot];
            }
        }

        Clamp();
    }

    /// <summary>Copies a campaign record's ammunition and ordnance picks over this plane's, and
    /// touches nothing else. ⚠ That restraint is the contract: EXPORT sets the loadout of a plane
    /// the hangar may already have built, and rewriting its paint, armour or engine from a campaign
    /// record that holds none of the three would throw the build away.</summary>
    public void SetLoadout(IReadOnlyList<int> ammo, IReadOnlyList<int> ordnance)
    {
        for (int slot = 0; ammo != null && slot < Ammo.Length && slot < ammo.Count; slot++)
        {
            Ammo[slot] = ammo[slot];
        }

        for (int cell = 0; ordnance != null && cell < Ordnance.Length && cell < ordnance.Count; cell++)
        {
            Ordnance[cell] = ordnance[cell];
        }
    }

    /// <summary>Forces every field into its decoded range, in place, and returns this. The store
    /// runs it on both load and save, so an out-of-range value from a hand-edited file (or a
    /// screen bug) can never leave the model claiming an airframe or calibre that does not
    /// exist. <see cref="Ammo"/> and <see cref="Ordnance"/> are the exception: their vocabulary is
    /// <c>Session.Campaign.CampaignLoadout</c>'s, the one decoder of both, which reads anything outside it
    /// as the stock fit, so they are round-tripped as stored rather than pinned here.</summary>
    public CustomPlaneDef Clamp()
    {
        Airframe = Math.Clamp(Airframe, 0, MaxAirframe);
        Engine = Math.Clamp(Engine, 0, EngineNone);
        ArmourNose = Math.Clamp(ArmourNose, 0, MaxArmourUnits);
        ArmourTail = Math.Clamp(ArmourTail, 0, MaxArmourUnits);
        ArmourLeftWing = Math.Clamp(ArmourLeftWing, 0, MaxArmourUnits);
        ArmourRightWing = Math.Clamp(ArmourRightWing, 0, MaxArmourUnits);
        for (int i = 0; i < Guns.Length; i++)
        {
            if (Guns[i].Calibre is { } c)
            {
                Guns[i] = Guns[i] with { Calibre = Math.Clamp(c, 0, MaxCalibre) };
            }
        }

        LeftHardpoints = Math.Clamp(LeftHardpoints, 0, MaxHardpointsPerWing);
        RightHardpoints = Math.Clamp(RightHardpoints, 0, MaxHardpointsPerWing);
        PaintPattern = Math.Clamp(PaintPattern, 0, MaxPaintPattern);
        for (int slot = 0; slot < PaintColours.Length; slot++)
        {
            PaintColours[slot] = Math.Clamp(PaintColours[slot], 0, MaxSwatch);

            // Ramps are eight to ten long, so the ceiling is the row's own, not a shared one.
            int shades = HangarPaintTables.Default.ShadeCount(PaintColours[slot]);
            PaintShades[slot] = Math.Clamp(PaintShades[slot], 0, shades > 0 ? shades - 1 : MaxShadeWithoutTable);
        }

        NoseDecal = ClampDecal(NoseDecal);
        TailDecal = ClampDecal(TailDecal);
        WingDecal = ClampDecal(WingDecal);
        return this;
    }

    // Four slots nobody has picked for, which is what a plane the campaign never exported carries.
    private static int[] NothingPicked()
    {
        var slots = new int[GunSlots];
        Array.Fill(slots, NoAmmoPick);
        return slots;
    }

    // Anything outside the shipped set becomes the keep-the-placeholder sentinel rather than a
    // decal nobody chose: an out-of-range index names no texture at all.
    private static int ClampDecal(int decal) => decal >= 0 && decal <= MaxDecal ? decal : KeepDecal;
}
