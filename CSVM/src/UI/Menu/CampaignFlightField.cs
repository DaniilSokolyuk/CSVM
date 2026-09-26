using System;
using System.Collections.Generic;
using CSVM.Flight.Hangar;
using CSVM.Session.Campaign;

namespace CSVM.UI.Menu;

/// <summary>
/// One guest's aircraft on a co-op campaign sortie: the records the picker offers them, and which
/// one they are on. Every record here is session-scoped, a stock airframe at rest, or a COPY of
/// one of the seated profile's aircraft, so a guest's ammunition edits land on something the
/// profile store never sees.
/// </summary>
public sealed class CampaignGuest
{
    private readonly List<OwnedPlane> _choices;

    internal CampaignGuest(int player, List<OwnedPlane> choices, int stockCount)
    {
        Player = player;
        _choices = choices;
        StockCount = stockCount;
    }

    /// <summary>Which human this is: 1 for P2, up to 3 for P4. The seated player is never one.</summary>
    public int Player { get; }

    /// <summary>Every aircraft this guest may fly, the stock airframes first.</summary>
    public IReadOnlyList<OwnedPlane> Choices => _choices;

    /// <summary>How many leading entries of <see cref="Choices"/> are stock airframes rather than
    /// copies of the seated profile's aircraft.</summary>
    public int StockCount { get; }

    /// <summary>Which entry of <see cref="Choices"/> is picked.</summary>
    public int Choice { get; internal set; }

    /// <summary>The aircraft this guest flies.</summary>
    public OwnedPlane Plane => _choices[Math.Clamp(Choice, 0, _choices.Count - 1)];
}

/// <summary>
/// The humans flying one campaign sortie, as the flight check walks them: how many joined, whose
/// check is showing, and what each guest picked. The seated player is 0 and keeps the profile's
/// own aircraft. Players 1 and up are guests, who bring no profile and fly the session-scoped
/// records <see cref="CampaignGuest"/> holds. A guest's pick and its fit last the whole run,
/// across the launches and hangar visits that rebuild the rosters. Engine-free and
/// presentation-neutral: the <see cref="CampaignFeature"/> owns one, so both presentations walk
/// the same no-duplicate and copy-not-reference rules and the same player index.
/// </summary>
public sealed class CampaignFlightField
{
    // The campaign's own starter airframe (CampaignProfileDef.NewProfile's two Devastators), which
    // is where a guest's pick opens rather than at the roster's arbitrary first row.
    private const int StarterAirframe = 5;

    // The stock roster a guest picks from: the 11-airframe stat table's own ids 0-10, the same
    // order stock_loadouts.json, the airframe node table and the langui 3000 titles use.
    private static readonly int StockAirframes = HangarEconomy.Airframes.Length;

    private readonly CampaignFeature _feature;
    private readonly List<CampaignGuest> _guests = new();

    // Every stock record handed out, by reference: a screen resolving one plane's guns asks here
    // rather than reading CustomPlaneStore under a name an airframe title could collide with.
    private readonly HashSet<OwnedPlane> _stock = new();

    // What each guest player had picked when their roster was last rebuilt, by player index. A
    // rebuild is not a decision to start over. The campaign is discarded and reopened around every
    // flight, and a hangar visit re-reads the profile. Without this a guest's aircraft and its
    // ammunition would fall back to stock on the next mission's check.
    private readonly Dictionary<int, CarriedPick> _carried = new();

    // The profile the guest rosters were built from. A different one (Resume re-reads it after a
    // hangar visit) rebuilds them, since a roster is a statement about that profile's aircraft.
    private CampaignProfileDef? _rosterProfile;

    // Whose campaign the carried picks were made on. Another player's campaign is another sortie,
    // so its guests open on the roster's own defaults rather than on what this one flew.
    private string _carriedFor = string.Empty;

    private int _players = 1;
    private bool _locked;

    internal CampaignFlightField(CampaignFeature feature) => _feature = feature;

    /// <summary>How many humans are flying, 1 to <see cref="PlayerSetupFeature.MaxSeats"/>.</summary>
    public int Players => _players;

    /// <summary>Whose flight check is showing: 0 the seated player, 1 and up a guest.</summary>
    public int Current { get; private set; }

    /// <summary>Whether the seated player has committed the field by pressing FLY MISSION.</summary>
    public bool Locked => _locked;

    /// <summary>The guests, in player order. Empty for a solo campaign.</summary>
    public IReadOnlyList<CampaignGuest> Guests
    {
        get
        {
            EnsureGuests();
            return _guests;
        }
    }

    /// <summary>Takes the joined-player count from the shell. A count that shrank drops the
    /// trailing guests and pulls the cursor back into the field, so a guest leaving mid-sequence
    /// cannot leave the screen showing a check nobody is flying.</summary>
    public void SetPlayers(int players)
    {
        _players = Math.Clamp(players, 1, PlayerSetupFeature.MaxSeats);
        Current = Math.Clamp(Current, 0, _players - 1);
        EnsureGuests();
    }

    /// <summary>FLY MISSION: moves to the next joined player's check. False means there is none,
    /// which is the caller's cue to launch.</summary>
    public bool Advance()
    {
        _locked = true;
        if (Current + 1 >= _players)
        {
            return false;
        }

        Current++;
        EnsureGuests();
        return true;
    }

    /// <summary>Back: returns to the previous player's check. False at the seated player's own,
    /// which lets the flow leave the screen the way it always did.</summary>
    public bool Retreat()
    {
        if (Current <= 0)
        {
            return false;
        }

        Current--;
        return true;
    }

    /// <summary>Abandons the sequence and puts the seated player back on their own check: RETURN
    /// TO BRIEFING, which is a decision to start the whole walk again.</summary>
    public void Rewind()
    {
        Current = 0;
        _locked = false;
    }

    /// <summary>The aircraft player <paramref name="player"/> flies: the profile's selected plane
    /// for the seated player, that guest's own pick for anybody else, null when neither exists.</summary>
    public OwnedPlane? Plane(int player)
    {
        return player <= 0 ? SeatedPlane() : GuestAt(player)?.Plane;
    }

    /// <summary>Whether entry <paramref name="pick"/> of a guest's own choices is one another
    /// player already flies, which is what the picker refuses a pick on. False for a player index
    /// that names no guest: only a guest chooses out of this roster.</summary>
    public bool Taken(int player, int pick)
    {
        if (GuestAt(player) is not { } guest)
        {
            return false;
        }

        return pick >= 0 && pick < guest.Choices.Count && Taken(guest, pick);
    }

    /// <summary>Puts a guest on entry <paramref name="pick"/> of their own choices, which is what
    /// the picker's ACCEPT does. An entry another player flies is refused here as well as on the
    /// picker, so the no-duplicate rule cannot be walked around by a caller that skips the screen.
    /// Returns whether the pick moved. ⚠ Nothing here reaches the seated profile: every record a
    /// guest may be put on is session-scoped.</summary>
    public bool Choose(int player, int pick)
    {
        if (GuestAt(player) is not { } guest
            || pick < 0 || pick >= guest.Choices.Count || pick == guest.Choice
            || Taken(guest, pick))
        {
            return false;
        }

        guest.Choice = pick;
        return true;
    }

    /// <summary>Whether this record is a stock airframe at rest rather than a profile aircraft.
    /// ⚠ A screen must ask here before reading <c>CustomPlaneStore</c> under the record's name: a
    /// stock record is named for its airframe, and a hangar plane sharing that name would
    /// otherwise fit a guest with somebody else's build.</summary>
    public bool IsStock(OwnedPlane plane) => _stock.Contains(plane);

    // What "another player already flies this" compares: the airframe for a stock entry, the
    // plane's own name for a profile aircraft. So two guests cannot both take the stock Devastator,
    // and neither can take the aircraft the seated player is flying.
    private static string KeyOf(OwnedPlane plane, bool stock) =>
        stock ? $"stock:{plane.Airframe}" : $"owned:{plane.Name}";

    // ⚠ A copy, never the profile's own record: a guest edits ammunition on whatever this returns,
    // and a reference here would write those edits into the seated profile.
    private static OwnedPlane CopyOf(OwnedPlane plane) => new()
    {
        Name = plane.Name,
        Airframe = plane.Airframe,
        Ammo = (int[])plane.Ammo.Clone(),
        Ordnance = (int[])plane.Ordnance.Clone(),
        Special = plane.Special,
    };

    // The guest player index names, or null for the seated player and for a player who has not
    // joined. Every public entry that takes a player index comes through here.
    private CampaignGuest? GuestAt(int player)
    {
        EnsureGuests();
        int at = player - 1;
        return at >= 0 && at < _guests.Count ? _guests[at] : null;
    }

    private OwnedPlane? SeatedPlane()
    {
        if (_feature.Profile is not { } profile || profile.Planes.Count == 0)
        {
            return null;
        }

        return profile.Planes[Math.Clamp(profile.SelectedPlane, 0, profile.Planes.Count - 1)];
    }

    private bool Taken(CampaignGuest asking, int pick)
    {
        string key = KeyOf(asking.Choices[pick], pick < asking.StockCount);
        if (SeatedPlane() is { } seated && key == KeyOf(seated, stock: false))
        {
            return true;
        }

        foreach (var other in _guests)
        {
            if (!ReferenceEquals(other, asking) && key == KeyOf(other.Plane, other.Choice < other.StockCount))
            {
                return true;
            }
        }

        return false;
    }

    // Builds whatever guests the joined count now needs, drops the trailing ones it does not, and
    // starts over whenever the seated profile changed under them. Every pick standing is carried
    // first, so a rebuild puts each guest back on the aircraft and the fit they chose.
    private void EnsureGuests()
    {
        bool rebuild = !ReferenceEquals(_rosterProfile, _feature.Profile);
        if (rebuild || _guests.Count != _players - 1)
        {
            Carry();
        }

        if (rebuild)
        {
            _rosterProfile = _feature.Profile;
            _guests.Clear();
            _stock.Clear();
        }

        while (_guests.Count > _players - 1)
        {
            _guests.RemoveAt(_guests.Count - 1);
        }

        while (_guests.Count < _players - 1)
        {
            _guests.Add(NewGuest(_guests.Count + 1));
        }
    }

    // Records what every live guest is on, by the same key Taken compares, with the fit they are
    // flying. Called before anything drops a guest, since the record itself goes with them.
    private void Carry()
    {
        if (_guests.Count == 0)
        {
            return;
        }

        _carriedFor = _rosterProfile?.Name ?? string.Empty;
        foreach (var guest in _guests)
        {
            var plane = guest.Plane;
            _carried[guest.Player] = new CarriedPick(
                KeyOf(plane, guest.Choice < guest.StockCount),
                (int[])plane.Ammo.Clone(),
                (int[])plane.Ordnance.Clone());
        }
    }

    // Where this guest's carried pick lands in their freshly built choices, or -1. They may carry
    // none, or it may have been made on another player's campaign. The profile may no longer own
    // that aircraft, or somebody else may fly it now. ⚠ The fit is written onto the entry, which
    // is a stock record or a copy, never the seated profile's own plane.
    private int Restore(CampaignGuest guest)
    {
        if (!_carried.TryGetValue(guest.Player, out var carried)
            || !string.Equals(_carriedFor, _rosterProfile?.Name ?? string.Empty, StringComparison.Ordinal))
        {
            return -1;
        }

        for (int pick = 0; pick < guest.Choices.Count; pick++)
        {
            if (KeyOf(guest.Choices[pick], pick < guest.StockCount) != carried.Key || Taken(guest, pick))
            {
                continue;
            }

            guest.Choices[pick].Ammo = (int[])carried.Ammo.Clone();
            guest.Choices[pick].Ordnance = (int[])carried.Ordnance.Clone();
            return pick;
        }

        return -1;
    }

    // One guest's own copy of the roster: the stock airframes, then a copy of each of the seated
    // profile's aircraft. Per guest rather than shared, so one guest's ammunition edits cannot
    // reach a record another guest later picks up.
    private CampaignGuest NewGuest(int player)
    {
        var choices = new List<OwnedPlane>(StockAirframes + (_rosterProfile?.Planes.Count ?? 0));
        for (int airframe = 0; airframe < StockAirframes; airframe++)
        {
            var record = new OwnedPlane { Name = AirframeTitle(airframe), Airframe = airframe };
            _stock.Add(record);
            choices.Add(record);
        }

        if (_rosterProfile is { } profile)
        {
            foreach (var owned in profile.Planes)
            {
                choices.Add(CopyOf(owned));
            }
        }

        // Built before the guest joins _guests, so Taken sees only the players who went before.
        var guest = new CampaignGuest(player, choices, StockAirframes);
        int carried = Restore(guest);
        guest.Choice = carried >= 0 ? carried : FirstFree(guest);
        return guest;
    }

    // The starter airframe where nobody took it, else the first entry that is free: a guest opens
    // on something they can actually fly.
    private int FirstFree(CampaignGuest guest)
    {
        for (int tries = 0; tries < guest.Choices.Count; tries++)
        {
            int pick = (StarterAirframe + tries) % guest.Choices.Count;
            if (!Taken(guest, pick))
            {
                return pick;
            }
        }

        return StarterAirframe;
    }

    private string AirframeTitle(int airframe) =>
        _feature.Strings.Text(3000 + airframe, $"Airframe {airframe}");

    // One guest's pick as it survives a rebuild: which entry they were on, and the ammunition and
    // ordnance fitted to it. The entry is named by the key Taken compares, not by a row number.
    // The arrays are copies, since the record they came from is rebuilt under them.
    private readonly record struct CarriedPick(string Key, int[] Ammo, int[] Ordnance);
}
