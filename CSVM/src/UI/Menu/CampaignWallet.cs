using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.Session.Campaign;

namespace CSVM.UI.Menu;

/// <summary>
/// The campaign profile as the hangar's wallet, the <see cref="IHangarWallet"/> the campaign
/// feature hands <see cref="HangarFeature.Open"/> when the cabin's Plane Construction opens a
/// build over the seated profile. Absent (null) on every wallet-free door (the Instant Action
/// Build button, the top-level hangar entry), which never consult funds.
///
/// <para>Reads and writes the profile through <see cref="CampaignProfileStore"/>'s public API.
/// A mission-reward aircraft (docs/org/hangar.md, the reward table at <c>0x0061ae80</c>) is
/// recognised by the <see cref="OwnedPlane.Special"/> flag <see cref="CampaignProgression"/> sets
/// when it grants one: the original's class-2 record, which its sell handler refuses (langui 704
/// <c>IDS_PS_SPECIALPLANE</c>).</para>
/// </summary>
public sealed class CampaignWallet : IHangarWallet
{
    /// <summary>The most planes a profile may buy (docs/org/hangar.md, "The slot cap reserves the
    /// five awards"): the 25 records of the slot array less the five the purchase-side free-slot
    /// finder <c>FUN_004111f0</c> holds for the mission awards, which are granted through their own
    /// unreserved finder <c>FUN_00406060</c>.</summary>
    public const int PurchasedPlaneCap = 20;

    // langui 3000 + id, the airframe's own name, the same row every other screen names one by.
    private const int AirframeNameId = 3000;

    private readonly CampaignProfileStore _store;
    private readonly CustomPlaneStore _planes;
    private readonly UiStrings? _strings;
    private readonly CampaignCheats? _cheats;

    /// <summary>Wraps an already-loaded profile. <paramref name="planes"/> is the global build
    /// store (<c>user://Planes/</c>) the profile's owned planes name into. <paramref name="strings"/>
    /// names an airframe for <see cref="UnlockEverything"/> and <paramref name="cheats"/> says what
    /// the menu cheats have switched on; both are absent on a wallet built off the feature.</summary>
    public CampaignWallet(
        CampaignProfileStore store, CampaignProfileDef profile, CustomPlaneStore planes,
        UiStrings? strings = null, CampaignCheats? cheats = null)
    {
        _store = store;
        Profile = profile;
        _planes = planes;
        _strings = strings;
        _cheats = cheats;
    }

    /// <summary>The profile this context prices and gates against. Mutated in place by
    /// <see cref="Purchase"/>/<see cref="Sell"/> and re-saved through the store's own public
    /// <c>Save</c>.</summary>
    public CampaignProfileDef Profile { get; }

    /// <summary>The wallet balance (docs/org/hangar.md "The campaign wallet"): $0 on a fresh
    /// profile, since the first mission has not yet paid out.</summary>
    public int Funds => Profile.Funds;

    /// <summary>Whether the profile has a slot for another bought plane (langui 204). A reward
    /// aircraft never counts against it: the original's finder reads an awarded record as still
    /// holding its own reservation, so the five awards drop out of the arithmetic and the cap
    /// falls on bought planes alone, granted or ungranted awards notwithstanding.</summary>
    public bool HasFreeSlot => Profile.Planes.Count(p => !p.Special) < PurchasedPlaneCap;

    /// <summary>Whether the build's total cost is affordable right now.</summary>
    public bool CanAfford(int cost) => Profile.Funds >= cost;

    /// <summary>Whether airframe <paramref name="airframe"/> is offered yet: the stat table's own
    /// availability threshold (<see cref="HangarEconomy.Airframes"/>) against the save's progress
    /// counter (<see cref="CampaignProfileDef.MissionsCompleted"/>), exactly the comparison
    /// <c>FUN_00410120</c> makes. The unlocking pilot name switches the comparison off, which is
    /// what <c>fAllowAll</c> does in that function and in its two neighbours
    /// (<c>docs/org/hangar.md</c>).</summary>
    public bool IsAirframeAvailable(int airframe) =>
        _cheats?.AllowAll == true
        || Profile.MissionsCompleted + 1 >= HangarEconomy.Airframes[airframe].Availability;

    /// <summary>The airframe of the named owned plane, or null when the profile does not own it.</summary>
    public int? OwnedAirframe(string planeName) =>
        Profile.Planes.FirstOrDefault(p => string.Equals(p.Name, planeName, StringComparison.OrdinalIgnoreCase))?.Airframe;

    /// <summary>Whether the named owned plane is an unsellable reward aircraft.</summary>
    public bool IsSpecial(string planeName) =>
        Profile.Planes.Any(p => p.Special && string.Equals(p.Name, planeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the named owned plane can be sold right now: it is actually owned, it is
    /// not one of the five unsellable reward aircraft, and at least two planes remain afterwards
    /// (docs/org/hangar.md, langui 701, a floor of the sell handler's own and not of the slot
    /// finder, which holds no floor at all).</summary>
    public bool CanSell(string planeName) =>
        Profile.Planes.Any(p => string.Equals(p.Name, planeName, StringComparison.OrdinalIgnoreCase))
        && !IsSpecial(planeName)
        && Profile.Planes.Count > 2;

    /// <summary>The profile's own aircraft as buildable defs, in the ownership list's own order,
    /// which is the campaign hangar's roster (langui 1257 INVENTORY). Ownership is what separates
    /// the two modes, not storage: an Instant Action build sits in the same
    /// <c>user://Planes/</c> directory and is absent here because the profile does not own it.</summary>
    public IReadOnlyList<CustomPlaneDef> OwnedBuilds()
    {
        var builds = new List<CustomPlaneDef>(Profile.Planes.Count);
        foreach (var owned in Profile.Planes)
        {
            builds.Add(BuildFor(owned));
        }

        return builds;
    }

    /// <summary>The full build cost of an owned plane, the decoded sell price (no depreciation,
    /// docs/org/hangar.md "The sell price is the full build cost"). A name the profile does not
    /// own is priced from the build store alone, on the campaign's starting-Devastator spec where
    /// even that is absent.</summary>
    public int SellPrice(string planeName)
    {
        var owned = Profile.Planes.FirstOrDefault(
            p => string.Equals(p.Name, planeName, StringComparison.OrdinalIgnoreCase));
        return HangarEconomy.Price(BuildFor(owned ?? new OwnedPlane { Name = planeName, Airframe = 5 }))
            .Total.Cost;
    }

    /// <summary>Deducts a completed build's total cost, records ownership and saves the profile.
    /// The build itself is already in <see cref="CustomPlaneStore"/> (the flow's own commit); this
    /// only moves money and names the plane into the profile's ownership list.</summary>
    public void Purchase(string planeName, int airframe, int cost)
    {
        Profile.Funds -= cost;
        var owned = Profile.Planes.FirstOrDefault(
            p => string.Equals(p.Name, planeName, StringComparison.OrdinalIgnoreCase));

        // ⚠ Do not add a second record for a name already owned. The commit writes one file per
        // name, so two records would name one aeroplane twice and a later sale would remove both.
        if (owned != null)
        {
            owned.Airframe = airframe;
        }
        else
        {
            Profile.Planes.Add(new OwnedPlane { Name = planeName, Airframe = airframe });
        }

        _store.Save(Profile);
    }

    /// <summary>Credits the wallet at the plane's full build cost, removes it from the profile's
    /// ownership list and deletes the underlying build (the original's sell clears the slot).
    /// Returns false without changing anything when <see cref="CanSell"/> would refuse; callers
    /// that already checked get a defensive no-op rather than a double credit.</summary>
    public bool Sell(string planeName)
    {
        if (!CanSell(planeName))
        {
            return false;
        }

        int price = SellPrice(planeName);
        Profile.Funds += price;
        Profile.Planes.RemoveAll(p => string.Equals(p.Name, planeName, StringComparison.OrdinalIgnoreCase));
        _store.Save(Profile);
        _planes.Delete(planeName);
        return true;
    }

    /// <summary>The plane construction hub's typed grant, the wallet's fifth writer: 25000 while
    /// the balance is under 50000, and nothing at or above it, which is
    /// <c>PLANECONSTRUCTION.SCRIPT</c>'s own test. False when the grant was refused.</summary>
    public bool GrantCheatCash()
    {
        if (Profile.Funds >= CampaignCheats.CashCeiling)
        {
            return false;
        }

        Profile.Funds += CampaignCheats.CashGrant;
        _store.Save(Profile);
        return true;
    }

    /// <summary>The unlocking pilot name's grant: the wallet set to 250000 and the eleven stock
    /// airframes added to the profile, which is what <c>FUN_004113b0</c>'s <c>fAllowAll</c> branch
    /// writes into plane slots 2 to 12 over the two starters. An airframe the profile already
    /// carries under that name is left alone, so pressing it twice adds nothing.</summary>
    public void UnlockEverything()
    {
        Profile.Funds = CampaignCheats.UnlockFunds;
        for (int airframe = 0; airframe < HangarEconomy.Airframes.Length; airframe++)
        {
            string name = _strings?.Text(AirframeNameId + airframe, $"Airframe {airframe}")
                ?? $"Airframe {airframe}";
            if (!Profile.Planes.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Profile.Planes.Add(new OwnedPlane { Name = name, Airframe = airframe });
            }
        }

        _store.Save(Profile);
    }

    // What one ownership record flies. A hangar-built plane is its stored build; a reward aircraft
    // granted before the store held one falls back to its own award template (the same order
    // LaunchMenu's launch path resolves in); the two profile-seeded starters are never built at
    // all, so they take the campaign's own Devastator spec (docs/org/hangar.md, "The campaign
    // instead starts with two aircraft": engine 1, two hardpoints per wing, no armour or guns).
    private CustomPlaneDef BuildFor(OwnedPlane owned)
    {
        var built = _planes.Load(owned.Name) ?? CampaignProgression.BuildForOwned(owned);
        if (built == null)
        {
            built = new CustomPlaneDef
            {
                Airframe = owned.Airframe,
                Engine = 1,
                LeftHardpoints = 2,
                RightHardpoints = 2,
            };
        }

        built.Name = owned.Name;
        return built;
    }
}
