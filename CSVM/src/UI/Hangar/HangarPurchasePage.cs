using System.Collections.Generic;
using System.Globalization;
using CSVM.Flight.Hangar;

namespace CSVM.UI.Hangar;

/// <summary>
/// The PURCHASE screen: the itemised review the original's callbacks 2251-2262 build, one row per
/// priced thing the scratch plane actually carries (airframe always; engine when chosen; each
/// armed gun slot as the GUNS screen names it; each armoured zone via 1191-1194 at units x5;
/// each wing with hardpoints via 1176/1177), then the totals row (1198) and the Purchase Now row
/// (1199). Row detail is that line's decoded cost and weight from <see cref="HangarEconomy"/>'s
/// bill. The gate mirrors the original's button, not only its commit: `PURCHASE.SCRIPT` disables
/// `pur_b_purchase` whenever the problems callback 2264 reports (docs/org/hangar.md), so a blocked
/// build flags the Purchase Now row and shows the problems text (1182 + 1227 OVERWEIGHT / 1171)
/// before any press; the press itself still runs <see cref="HangarFlow.Commit"/>, whose refusal is
/// the same rule in the same words.
/// </summary>
public sealed class HangarPurchasePage : HangarPage
{
    private static readonly string[] ZoneFallbacks = { "Nose", "Tail", "Left Wing", "Right Wing" };

    /// <summary>Binds the page to its flow.</summary>
    public HangarPurchasePage(HangarFlow flow)
        : base(flow)
    {
    }

    // What a row prices; Index is the gun slot, armour zone or wing where the kind has one.
    private enum Kind
    {
        Airframe,
        Engine,
        Gun,
        Armour,
        Hardpoint,
        Totals,
        Build,
    }

    /// <inheritdoc/>
    public override HangarScreen Screen => HangarScreen.Purchase;

    /// <inheritdoc/>
    public override int RowCount => Rows().Count;

    /// <summary>Whether the Purchase Now row is live: the original greys the button whenever the
    /// problems callback reports, and this is the remake's reading of that state. Over a campaign
    /// flow (<see cref="HangarFlow.Campaign"/> non-null, B13) the row also greys out for a full
    /// hangar, an unavailable airframe or an unaffordable total; the wallet-free doors see none of
    /// the three.</summary>
    public bool BuildEnabled
    {
        get
        {
            var bill = HangarEconomy.Price(Scratch);
            if (bill.Verdict != PurchaseVerdict.Ok)
            {
                return false;
            }

            return Flow.Campaign is not { } campaign
                || (campaign.HasFreeSlot
                    && campaign.IsAirframeAvailable(Scratch.Airframe)
                    && campaign.CanAfford(bill.Total.Cost));
        }
    }

    /// <inheritdoc/>
    public override string RowText(int row)
    {
        var (kind, index) = Rows()[row];
        return kind switch
        {
            Kind.Airframe => Flow.AirframeName(Scratch.Airframe),
            Kind.Engine => Flow.EngineName(Scratch.Airframe, Scratch.Engine),
            Kind.Gun => GunRowText(index),
            // The armour lines carry the record's own stored scale, presses x5, as the ARMOR
            // screen does.
            Kind.Armour => Line(
                1191 + index,
                ZoneFallbacks[index] + ": {0} units",
                ZoneUnits(index) * HangarArmourPage.DisplayScale),
            Kind.Hardpoint => Line(
                1176 + index, (index == 0 ? "Left" : "Right") + " Wing: {0}", WingCount(index)),
            Kind.Totals => Flow.Strings.Text(1198, "Totals"),
            _ => BuildEnabled
                ? Flow.Strings.Text(1199, "Purchase Now")
                : "✕  " + Flow.Strings.Text(1199, "Purchase Now"),
        };
    }

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        var bill = HangarEconomy.Price(Scratch);
        var (kind, index) = Rows()[row];
        return kind switch
        {
            Kind.Airframe => Priced(bill.Airframe),
            Kind.Engine => Priced(bill.Engine),
            Kind.Gun => Priced(bill.Guns[index]),
            Kind.Armour => Priced(new CostWeight(
                ZoneUnits(index) * HangarEconomy.ArmourStepCost,
                ZoneUnits(index) * HangarEconomy.ArmourStepWeight)),
            Kind.Hardpoint => Priced(new CostWeight(
                WingCount(index) * HangarEconomy.HardpointCost,
                WingCount(index) * HangarEconomy.HardpointWeight)),
            Kind.Totals => Flow.TotalsLine,
            _ => bill.Verdict != PurchaseVerdict.Ok ? ProblemsText(bill.Verdict) : CampaignProblemsText(bill),
        };
    }

    /// <summary>Every bill row prices the build as it stands; the Purchase Now row carries its own
    /// flag through <see cref="BuildEnabled"/> and takes no second mark.</summary>
    public override int? CostWith(int row) =>
        Rows()[row].Kind == Kind.Build ? null : HangarEconomy.Price(Scratch).Total.Cost;

    /// <inheritdoc/>
    public override bool Accept(int row)
    {
        if (Rows()[row].Kind == Kind.Build)
        {
            Flow.Commit();
        }

        // The last screen either way: a review row's press is a no-op, and a refused commit
        // stays here with the reason showing.
        return true;
    }

    // "$C   W lbs.", the same shape every other screen's detail prices in.
    private static string Priced(CostWeight line) => $"${line.Cost}   {line.Weight} lbs.";

    // The dynamic row list, rebuilt from the scratch plane on every read so an edit behind the
    // screen (a saved plane loaded, a test poking the def) is always what shows. The original's
    // counts are dynamic the same way: its per-line callbacks skip absent components.
    private List<(Kind Kind, int Index)> Rows()
    {
        var rows = new List<(Kind, int)> { (Kind.Airframe, 0) };
        if (Scratch.Engine != CustomPlaneDef.EngineNone)
        {
            rows.Add((Kind.Engine, 0));
        }

        for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
        {
            if (!Scratch.Guns[slot].IsEmpty)
            {
                rows.Add((Kind.Gun, slot));
            }
        }

        for (int zone = 0; zone < ZoneFallbacks.Length; zone++)
        {
            if (ZoneUnits(zone) > 0)
            {
                rows.Add((Kind.Armour, zone));
            }
        }

        for (int wing = 0; wing < 2; wing++)
        {
            if (WingCount(wing) > 0)
            {
                rows.Add((Kind.Hardpoint, wing));
            }
        }

        rows.Add((Kind.Totals, 0));
        rows.Add((Kind.Build, 0));
        return rows;
    }

    // A gun row exactly as the GUNS screen names its rows: the airframe's slot title, then the
    // shared calibre naming with its twin prefix.
    private string GunRowText(int slot)
    {
        var stats = HangarEconomy.Airframes[Scratch.Airframe];
        string title = Flow.Strings.Text(stats.SlotTitle(slot), $"Slot {slot + 1}");
        return $"{title}: {Flow.GunName(Scratch.Guns[slot])}";
    }

    // One value-carrying langui line, or its plain-text shape when the table is missing.
    private string Line(int id, string fallbackFormat, int value)
    {
        string text = Flow.Strings.Format(id, value);
        return text.Length > 0 ? text : string.Format(CultureInfo.InvariantCulture, fallbackFormat, value);
    }

    // The problems text under a flagged Purchase Now row, in Commit's own words; empty when the
    // build is clean, which is the original's empty pur_t_problems.
    private string ProblemsText(PurchaseVerdict verdict) => verdict switch
    {
        PurchaseVerdict.Ok => string.Empty,
        PurchaseVerdict.Overweight =>
            Flow.Strings.Text(1182, "CAN'T PURCHASE:").TrimEnd() + " " + Flow.Strings.Text(1227, "OVERWEIGHT"),
        _ =>
            Flow.Strings.Text(1182, "CAN'T PURCHASE:").TrimEnd() + " " + Flow.Strings.Text(1171, "No Engine Selected"),
    };

    // The campaign-only reasons (B13): the underlying verdict is Ok, but the slot cap, the wallet
    // or the availability threshold still refuses. In the commit's own order (HangarFeature.
    // Refusal), so the row says ahead of the press what the press would say. Empty over the two
    // wallet-free doors, where Flow.Campaign is null.
    private string CampaignProblemsText(HangarBill bill)
    {
        if (Flow.Campaign is not { } campaign)
        {
            return string.Empty;
        }

        if (!campaign.HasFreeSlot)
        {
            return Flow.Feature.HangarFullText();
        }

        if (!campaign.IsAirframeAvailable(Scratch.Airframe))
        {
            return Flow.Strings.Text(1182, "CAN'T PURCHASE:").TrimEnd() + " That airframe is not available yet.";
        }

        if (!campaign.CanAfford(bill.Total.Cost))
        {
            return Flow.Strings.Text(1182, "CAN'T PURCHASE:").TrimEnd() + " " +
                Flow.Strings.Text(1226, "INSUFFICIENT FUNDS");
        }

        return string.Empty;
    }

    private int ZoneUnits(int zone) => zone switch
    {
        0 => Scratch.ArmourNose,
        1 => Scratch.ArmourTail,
        2 => Scratch.ArmourLeftWing,
        _ => Scratch.ArmourRightWing,
    };

    private int WingCount(int wing) => wing == 0 ? Scratch.LeftHardpoints : Scratch.RightHardpoints;
}
