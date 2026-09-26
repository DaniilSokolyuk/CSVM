using System.Collections.Generic;
using CSVM.Flight;

namespace CSVM.Session;

/// <summary>
/// The bridge between a campaign profile's stored picks and a flying aircraft's fit: one
/// <see cref="OwnedPlane"/>'s <see cref="OwnedPlane.Ammo"/> and <see cref="OwnedPlane.Ordnance"/>
/// arrays as the <see cref="LoadoutChoice"/> the launch hands the session, which
/// <see cref="Loadout.Bind"/> then lays over the aircraft's own base fit. Engine-free, so the two
/// encodings test off engine. Both are CSVM's own, one-based with 0 for "never picked", where the
/// original's record holds a plain rocket-table index with 11 for none
/// (<c>docs/formats/saved-games.md</c>, "Where the ammunition and ordnance picks live").
/// </summary>
public static class CampaignLoadout
{
    /// <summary>How many rows the Ammo Selection screen's rocket table has
    /// (<c>docs/formats/campaign-screens.md</c>), which closes the ordnance vocabulary.</summary>
    public const int PylonRows = 12;

    /// <summary>What <see cref="OwnedPlane.Ammo"/>'s index 0..3 names, in the gun matrix's own
    /// order. Index 4 is the record's no-gun marker, which is not an ammunition at all.</summary>
    public static readonly string[] AmmoNames = { "slug", "dumdum", "ap", "magnesium" };

    // The row an unset pylon stands for: high explosive, the universal stock fit (loadouts.md).
    private const int StockPylonRow = 1;

    // The profile record's "this slot mounts no gun" marker, the fifth value of a four-value
    // ammunition field (docs/formats/campaign-screens.md, "Ammo selection").
    private const int NoGun = 4;

    /// <summary>The rocket-table row (0..11) a stored pylon value names. CSVM's own
    /// <see cref="OwnedPlane.Ordnance"/> encoding is one-based with 0 meaning "never picked", the
    /// stand-in the plan's C25 chose over the save format's plain table index; every reader of the
    /// field goes through here so the two spaces cannot drift apart on one screen.</summary>
    public static int PylonRow(int stored) =>
        stored >= 1 && stored <= PylonRows ? stored - 1 : StockPylonRow;

    /// <summary>The plane's stored picks as one choice. <paramref name="stock"/> is the table an
    /// ordnance row's <c>wep_*</c> id comes from; without it the pylons keep the base's fit rather
    /// than being cleared, since a table index means nothing without the table. An unset pylon
    /// (stored 0) is also left to the base: the base's stock fit is the universal high-explosive
    /// load that unset stands for, so writing it back changes nothing.</summary>
    public static LoadoutChoice For(OwnedPlane plane, StockLoadouts? stock) =>
        For(plane.Ammo, plane.Ordnance, stock);

    /// <summary>The same reading of an exported plane's own stored picks (the campaign's EXPORT
    /// writes the record's ammunition and ordnance into <see cref="CustomPlaneDef"/>), so a plane
    /// flown from the Instant Action picker carries what the campaign fitted it with. One decoder
    /// for both records, since they hold the field in one encoding.</summary>
    public static LoadoutChoice For(CustomPlaneDef plane, StockLoadouts? stock) =>
        For(plane.Ammo, plane.Ordnance, stock);

    /// <summary>The same reading of a fit a co-op pick or launch carried over the wire. It holds
    /// the stored values of the plane it came from. A stock fit reads as null, so the seat flies
    /// its base fit untouched.</summary>
    public static LoadoutChoice? For(Net.CoopFit fit, StockLoadouts? stock)
    {
        if (fit.IsStock)
        {
            return null;
        }

        var ammo = new int[Net.CoopFit.GunSlots];
        for (int slot = 0; slot < ammo.Length; slot++)
        {
            ammo[slot] = fit.AmmoAt(slot);
        }

        var ordnance = new int[Net.CoopFit.Cells];
        for (int cell = 0; cell < ordnance.Length; cell++)
        {
            ordnance[cell] = fit.OrdnanceAt(cell);
        }

        return For(ammo, ordnance, stock);
    }

    /// <summary>A launch's fit as the stored values a co-op launch sends, the inverse of
    /// <see cref="For(Net.CoopFit, StockLoadouts?)"/>. Only the picks a stored record can hold
    /// travel: gun ammunition by slot and ordnance by wing cell. A physical pylon pick has no stored
    /// value and stays on this machine.</summary>
    public static Net.CoopFit FitOf(LoadoutChoice? fit, StockLoadouts? stock)
    {
        if (fit == null)
        {
            return default;
        }

        var ammo = new int[Net.CoopFit.GunSlots];
        for (int slot = 0; slot < ammo.Length; slot++)
        {
            string? pick = fit.GunAmmoFor(slot + 1);
            ammo[slot] = pick == LoadoutChoice.None ? NoGun : System.Array.IndexOf(AmmoNames, pick);
        }

        var ordnance = new int[Net.CoopFit.Cells];
        var table = stock?.Options.PylonOrdnance;
        for (int cell = 0; table != null && cell < ordnance.Length; cell++)
        {
            string? pick = fit.WingCellFor(cell);
            for (int row = 0; pick != null && row < table.Count; row++)
            {
                if (table[row].Id == pick)
                {
                    ordnance[cell] = row + 1;
                    break;
                }
            }
        }

        return Net.CoopFit.Of(ammo, ordnance);
    }

    private static LoadoutChoice For(
        IReadOnlyList<int> ammoPicks, IReadOnlyList<int> ordnancePicks, StockLoadouts? stock)
    {
        var fit = new LoadoutChoice();
        for (int slot = 0; slot < LoadoutChoice.MaxGunSlot && slot < ammoPicks.Count; slot++)
        {
            int ammo = ammoPicks[slot];
            if (ammo >= 0 && ammo < AmmoNames.Length)
            {
                fit.SetGunAmmo(slot + 1, AmmoNames[ammo]);
            }
            else if (ammo == NoGun)
            {
                fit.SetGunAmmo(slot + 1, LoadoutChoice.None);
            }
        }

        var table = stock?.Options.PylonOrdnance;
        if (table == null || table.Count == 0)
        {
            return fit;
        }

        // A cell is a wing and an ordinal ("the left wing's second pylon"), and which pylon that is
        // depends on what the aircraft hangs, so the cell travels as a cell for the fit to resolve
        // (LoadoutChoice.SetWingCell).
        for (int cell = 0; cell < LoadoutChoice.OrdnanceCells && cell < ordnancePicks.Count; cell++)
        {
            int row = ordnancePicks[cell] - 1;
            if (row >= 0 && row < table.Count)
            {
                fit.SetWingCell(cell, table[row].Id);
            }
        }

        return fit;
    }
}
