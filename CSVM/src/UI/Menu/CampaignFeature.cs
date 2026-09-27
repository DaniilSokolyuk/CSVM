using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;

namespace CSVM.UI.Menu;

/// <summary>
/// The campaign as a shared feature: the profile store and the seated profile, the roster
/// operations in the original's own words, the mission the screens after the cabin are about (the
/// <c>cm_sequence</c> entry, the briefing and its reveal), the picks the flight check's screens
/// write into the profile (ammunition and ordnance, the crew's aircraft, an export), the humans on
/// the sortie, the wallet the hangar prices against, and the launch as one
/// <see cref="CampaignMissionExit"/>. Engine-free and presentation-neutral: Built-in walks it
/// through <c>CampaignFlow</c>'s screen stack and reads every board's content from here, and every
/// write into the profile store goes through an operation below, so what a created player, a fit,
/// a purchase, a sale, a launch and a deletion write is this class's contract. The screen stack,
/// the cursor, the refusal line and the dialog over a screen are a presentation's own.
/// </summary>
public sealed class CampaignFeature : IMenuFeature
{
    /// <summary>The roster's capacity: the original's profile block is 24 slots
    /// (docs/formats/campaign-screens.md), which is the number langui 202 states when it refuses a
    /// new player.</summary>
    public const int MaxProfiles = 24;

    /// <summary>The longest player name: the original's roster is 24 slots of 33 bytes, a
    /// 32-character name plus its terminator (docs/formats/campaign-screens.md).</summary>
    public const int MaxNameLength = 32;

    // The two missions on which FLIGHTCHECK.SCRIPT bars the PILOT's CHANGE PLANE outright, as
    // 1-based ordinals (docs/formats/campaign-screens.md, "Plane change"): the two missions whose
    // reward-table entry is ungated, which the screen grants on entry through uiData 2021.
    private const int FirstGrantOrdinal = 13;
    private const int SecondGrantOrdinal = 17;

    // The owned-plane count both CHANGE PLANE buttons need, uiData 2018 against the script's own
    // floor (docs/formats/campaign-screens.md, "Plane change").
    private const int ChangePlaneFloor = 3;

    // A guest's hangar holds one aeroplane per airframe and no wingman's, so a second airframe is
    // already a choice. The script's floor of three counts the wingman's plane in.
    private const int GuestChangePlaneFloor = 2;

    // The campaign's first aeroplane, which every host's hangar holds from its first mission.
    private const int GuestStarterAirframe = 5;

    private readonly Func<int, string> _nodeOfAirframe;

    // The sequence entry read for MissionSeq, and which sequence that was; -2 is "not read for
    // any", which no MissionSeq ever is, since the cabin's own default is -1.
    private CampaignMission? _mission;
    private int _missionSeq = -2;
    private CampaignBriefing? _briefing;
    private int _briefingSeq = -2;

    /// <summary>A feature labelling from <paramref name="strings"/> and resolving an airframe id
    /// to its planes.zbd node through <paramref name="nodeOfAirframe"/>, which is what a launch
    /// seat carries. <paramref name="chapterCinema"/> is the film a cabin door plays before it
    /// opens and <paramref name="closingCinema"/> the film a finished mission's scrapbook door
    /// plays; null (every off-engine caller) means that door plays none.</summary>
    public CampaignFeature(
        UiStrings strings, Func<int, string> nodeOfAirframe,
        ChapterCinema? chapterCinema = null, ClosingCinema? closingCinema = null)
    {
        Strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _nodeOfAirframe = nodeOfAirframe ?? throw new ArgumentNullException(nameof(nodeOfAirframe));
        ChapterCinema = chapterCinema;
        ClosingCinema = closingCinema;
        Field = new CampaignFlightField(this);
        Roster = Array.Empty<string>();
    }

    /// <summary>The langui table every label here is read from.</summary>
    public UiStrings Strings { get; }

    /// <summary>What the original's four menu cheats have switched on. One store for both
    /// presentations, and the only place any of them is read from.</summary>
    public CampaignCheats Cheats { get; } = new();

    /// <summary>The chapter cinema every cabin door runs its handoff through, or null when the
    /// caller has none and a cabin door simply opens the cabin. One instance serves both
    /// presentations, and its latch is what stops a film replaying (<c>Session/Launch/Launcher.cs</c>).
    /// </summary>
    public ChapterCinema? ChapterCinema { get; }

    /// <summary>The closing cinema the scrapbook door a flown mission takes runs its handoff
    /// through, or null when the caller has none and that door simply opens the book. One instance
    /// serves both presentations (<c>Session/Launch/Launcher.cs</c>), and the flown mission's own result
    /// is what decides whether it plays.</summary>
    public ClosingCinema? ClosingCinema { get; }

    /// <summary>Whether a campaign is open: a store was handed in by <see cref="Open"/>, or a co-op
    /// guest's was opened by <see cref="OpenGuest"/>, and <see cref="Discard"/> has not ended it.
    /// </summary>
    public bool IsOpen => Store != null || IsGuest;

    /// <summary>Whether this is a co-op guest's campaign, a profile that lives only in memory over
    /// no store. Every write below it is a no-op, and no save of this machine is touched.
    /// </summary>
    public bool IsGuest { get; private set; }

    /// <summary>Whether this co-op guest is Ready, as the network door last had it. A presentation
    /// copies it here so the flight check can name its own last button from the feature alone.
    /// </summary>
    public bool GuestReady { get; set; }

    /// <summary>The profile store the open campaign creates, reads and deletes through, or null
    /// when none is open.</summary>
    public CampaignProfileStore? Store { get; private set; }

    /// <summary>The hangar's build store (<c>user://Planes/</c>), or null when the caller has none:
    /// every owned plane then reads as its airframe's stock fit and nothing can be exported.</summary>
    public CustomPlaneStore? Planes { get; private set; }

    /// <summary>The stock-loadout table, or null when the caller has none.</summary>
    public StockLoadouts? Stock { get; private set; }

    /// <summary>The folder <c>extracted/</c> sits in, or null when the caller has none; the
    /// mission entry and the briefing then read as absent.</summary>
    public string? DataRoot { get; private set; }

    /// <summary>Every stored profile's name, sorted, re-read by <see cref="RefreshRoster"/> and by
    /// every operation that creates or deletes one.</summary>
    public IReadOnlyList<string> Roster { get; private set; }

    /// <summary>The name of the profile last seated, or "" when the store has never recorded one
    /// or none is open. A name whose profile is gone still reads back; resolve it against
    /// <see cref="Roster"/>.</summary>
    public string LastPlayed => Store?.LastPlayed ?? string.Empty;

    /// <summary>The profile the player seated, or null while nobody is.</summary>
    public CampaignProfileDef? Profile { get; private set; }

    /// <summary>The <c>cm_sequence.zrd</c> index (0..23) of the mission the briefing, flight check
    /// and ammo screens are about; -1 until the cabin names one.</summary>
    public int MissionSeq { get; private set; } = -1;

    /// <summary>Whose aircraft the ammo screen edits: 0 the pilot's, 1 the wingman's.</summary>
    public int AmmoSlot { get; private set; }

    /// <summary>Which crew slot the plane selection opens focused on, 0 the pilot's and 1 the
    /// wingman's, the original's <c>@globals@ZQ</c> of -1 and -2.</summary>
    public int PlaneSlot { get; private set; }

    /// <summary>How many times the book has been opened through <see cref="EnterScrapbook"/>. A
    /// presentation watches this rather than <see cref="MissionSeq"/> alone, so reopening the book
    /// on the mission it is already browsing still lands on that mission's spread 1, which is what
    /// the original's <c>uiData</c> 2405 mode 1 does however the book is reached.</summary>
    public int ScrapbookEntry { get; private set; }

    /// <summary>The scrap a zoom view is open on: the <c>SCRAPBOOK.CSV</c> mission slot, spread and
    /// item. Null until <see cref="SetScrapbookZoom"/> is called.</summary>
    public (int Mission, int Spread, int Item)? ZoomTarget { get; private set; }

    /// <summary>The humans flying this sortie: how many joined, whose flight check is showing, and
    /// what each guest picked. Solo until a presentation says otherwise.</summary>
    public CampaignFlightField Field { get; }

    /// <summary>The <c>cm_sequence.zrd</c> entry <see cref="MissionSeq"/> names, or null when the
    /// data root, the file or the entry is unavailable. Read once per mission, and here because more
    /// than one screen asks: the flight check and the plane selection both draw a wingman only when
    /// this mission carries one.</summary>
    public CampaignMission? Mission
    {
        get
        {
            if (_missionSeq != MissionSeq)
            {
                _missionSeq = MissionSeq;
                _mission = ReadMission();
            }

            return _mission;
        }
    }

    /// <summary>Whether this mission flies a wingman, its <c>cm_sequence</c> flag.</summary>
    public bool MissionHasWingman => !IsGuest && (Mission?.Wingman ?? false);

    /// <summary>The briefing for <see cref="MissionSeq"/>, loaded on first sight of each mission
    /// and kept with its reveal's progress, or null when the data root or the entry is
    /// unavailable. A presentation advances it on its own clock.</summary>
    public CampaignBriefing? Briefing
    {
        get
        {
            if (_briefingSeq != MissionSeq)
            {
                _briefingSeq = MissionSeq;
                _briefing = DataRoot is { } root && MissionSeq >= 0 ? CampaignBriefing.Load(root, MissionSeq) : null;
            }

            return _briefing;
        }
    }

    /// <summary>The story position Next Mission resolves to for the seated profile, or 0 with
    /// nobody seated.</summary>
    public int NextMissionSeq => Profile is { } profile ? CampaignProgression.NextMissionSeq(profile) : 0;

    /// <summary>Whether the seated profile has finished the campaign, which disables Next Mission.</summary>
    public bool CampaignComplete => Profile is { } profile && CampaignProgression.Complete(profile);

    /// <summary>The airframe a co-op guest flies: the one its selected aeroplane is built on, or -1
    /// with no guest campaign open.</summary>
    public int GuestAirframe =>
        IsGuest && Profile is { Planes.Count: > 0 } profile
            ? profile.Planes[Math.Clamp(profile.SelectedPlane, 0, profile.Planes.Count - 1)].Airframe
            : -1;

    /// <summary>The ammunition and ordnance a co-op guest set on its selected aeroplane, the fit its
    /// pick carries to the host. Stock with no guest campaign open.</summary>
    public Net.CoopFit GuestCoopFit =>
        IsGuest && Profile is { Planes.Count: > 0 } profile
            && profile.Planes[Math.Clamp(profile.SelectedPlane, 0, profile.Planes.Count - 1)] is { } plane
            ? Net.CoopFit.Of(plane.Ammo, plane.Ordnance)
            : default;

    // Whether MissionSeq is one of the two missions the flight check calls uiData 2021 on, which
    // is also what makes uiData 2018 report one plane fewer than the profile owns.
    private bool GrantMission =>
        MissionSeq + 1 == FirstGrantOrdinal || MissionSeq + 1 == SecondGrantOrdinal;

    // uiData 2018's answer: the owned count, less one on the two grant missions.
    private int ChangePlaneCount => (Profile?.Planes.Count ?? 0) - (GrantMission ? 1 : 0);

    /// <summary>Whether one character may be typed into a player name: the original's
    /// alphanumeric-and-space rule (langui 707). Restricting the alphabet this far is also what
    /// keeps <see cref="CampaignProfileStore.DirFor"/>'s sanitisation from ever having anything to
    /// change, since a profile name becomes a directory name.</summary>
    public static bool AcceptsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == ' ';

    /// <summary>Whether a whole name is one a player may carry, empty names excluded.</summary>
    public static bool ValidName(string text)
    {
        if (text.Length == 0 || text.Length > MaxNameLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!AcceptsNameChar(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The airframes a co-op host offers its guests, a bit per airframe id: every airframe
    /// its hangar holds an aeroplane on, and always the starter. Null profile offers the starter.
    /// </summary>
    public static ushort HangarAirframes(CampaignProfileDef? profile)
    {
        int mask = 1 << GuestStarterAirframe;
        if (profile != null)
        {
            foreach (var plane in profile.Planes)
            {
                if (plane.Airframe is >= 0 and < CampaignProgression.AirframeCount and < 16)
                {
                    mask |= 1 << plane.Airframe;
                }
            }
        }

        return (ushort)mask;
    }

    /// <summary>Opens a campaign over <paramref name="store"/>, reading its roster once, with
    /// nobody seated and no mission named. Opening again drops whatever campaign was open, as
    /// leaving a presentation for another door does; nothing saved is touched.</summary>
    public void Open(CampaignProfileStore store, CustomPlaneStore? planes = null, StockLoadouts? stock = null, string? dataRoot = null)
    {
        Discard();
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Planes = planes;
        Stock = stock;
        DataRoot = dataRoot;
        Roster = store.List();
    }

    /// <summary>Opens a co-op guest's campaign, a profile in memory named for the host, as far
    /// through the story as <paramref name="progress"/>. It owns one stock aeroplane per airframe in
    /// <paramref name="airframes"/> (a bit per airframe id). It stands on <paramref name="airframe"/>
    /// fitted with <paramref name="fit"/> where the host still offers that airframe, else on the
    /// starter's stock fit. Nothing is saved. Opening again drops whatever campaign was open.</summary>
    public void OpenGuest(
        string hostName, int progress, ushort airframes, StockLoadouts? stock = null, string? dataRoot = null,
        int airframe = -1, Net.CoopFit fit = default)
    {
        Discard();
        IsGuest = true;
        Stock = stock;
        DataRoot = dataRoot;
        bool offered = airframe is >= 0 and < 16 && (airframes & (1 << airframe)) != 0;
        Profile = offered
            ? GuestProfile(hostName ?? "", progress, airframes, airframe, fit)
            : GuestProfile(hostName ?? "", progress, airframes, StarterAirframeOf(airframes), default);
    }

    /// <summary>Follows the co-op host's hangar and story position on a guest's campaign. A hangar
    /// that changed rebuilds the guest's aeroplanes, keeping the picked airframe and its fit where
    /// the host still offers it. False on a campaign that is not a guest's.</summary>
    public bool FollowHost(int progress, ushort airframes)
    {
        if (!IsGuest || Profile is not { } profile)
        {
            return false;
        }

        profile.MissionsCompleted = Math.Max(0, progress);
        if (GuestAirframes(profile) == (airframes == 0 ? 1 << GuestStarterAirframe : airframes))
        {
            return true;
        }

        int picked = GuestAirframe;
        bool kept = picked >= 0 && (airframes & (1 << picked)) != 0;
        var results = new List<MissionResult>(profile.MissionResults);
        Profile = kept
            ? GuestProfile(profile.Name, progress, airframes, picked, GuestCoopFit)
            : GuestProfile(profile.Name, progress, airframes, StarterAirframeOf(airframes), default);
        Profile.MissionResults.AddRange(results);
        return true;
    }

    /// <summary>A co-op guest's debrief record for mission <paramref name="seq"/>. Its own
    /// <paramref name="attempt"/> gives the time, shots, hits and kills (none when it flew nothing).
    /// The objectives and cash are the host's, since the outcome is the host's. Kept in the guest's profile
    /// in memory for the scrapbook to read, and never saved.</summary>
    public void RecordGuestResult(int seq, MissionAttempt? attempt, int objectives, int cash)
    {
        if (!IsGuest || Profile is not { } profile)
        {
            return;
        }

        var run = new MissionRun
        {
            CompletedMask = objectives,
            BestAttemptMask = objectives,
            Money = cash,
            Airframe = attempt?.Airframe ?? GuestAirframe,
            PlaneName = attempt?.PlaneName ?? string.Empty,
            TimeMs = attempt?.TimeMs ?? 0,
            Shots = attempt?.Shots ?? 0,
            Hits = attempt?.Hits ?? 0,
        };
        if (attempt is { } flown)
        {
            run.Kills = (int[])flown.Kills.Clone();
            run.AceKills = (int[])flown.AceKills.Clone();
        }

        profile.MissionResults.RemoveAll(r => r.Seq == seq);
        profile.MissionResults.Add(new MissionResult { Seq = seq, Latest = run, Best = run });
    }

    /// <summary>Re-reads the store's roster.</summary>
    public void RefreshRoster() => Roster = Store?.List() ?? Array.Empty<string>();

    /// <summary>Whether the store holds a profile of that name.</summary>
    public bool HasPlayer(string name) => Store?.Load(name) != null;

    /// <summary>CONTINUE: seats the named player, creating the profile when it is new, the
    /// original's own commit path from the button, from Enter in the name box and from a
    /// double-click on a roster row alike. Returns the refusal in the original's words (an empty
    /// name, langui 200; a name outside the character rule, 707; one over the length, 212; a full
    /// roster, 202), or null once the player is seated. Nothing is written for a refusal.</summary>
    public string? ContinuePlayer(string name)
    {
        if (Store is not { } store)
        {
            return "No campaign is open.";
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            return Strings.Text(200, "You must enter a player name.");
        }

        if (!ValidName(name))
        {
            return NameRefusal(name);
        }

        var profile = store.Load(name);
        if (profile == null)
        {
            if (Roster.Count >= MaxProfiles)
            {
                string full = Strings.Format(202, MaxProfiles);
                return full.Length > 0
                    ? full
                    : $"Crimson Skies supports only {MaxProfiles} active players. " +
                      "To add a new player, delete a player from the list.";
            }

            profile = CampaignProfileDef.NewProfile(name);
            store.Save(profile);
            RefreshRoster();
        }

        SelectProfile(profile);
        return null;
    }

    /// <summary>DELETE PLAYER's confirmed answer: removes the named profile's own directory and
    /// nothing else, so the planes it referenced by name are still in the hangar afterwards, and
    /// re-reads the roster. False when the store holds no such profile. A seated profile of that
    /// name stays seated: the roster screen is where this is pressed, and the next seat replaces
    /// it.</summary>
    public bool DeletePlayer(string name)
    {
        if (Store is not { } store)
        {
            return false;
        }

        bool gone = store.Delete(name.Trim());
        RefreshRoster();
        return gone;
    }

    /// <summary>Seats a loaded profile, recording it as the store's last-played player, the way
    /// every door onto the cabin does.</summary>
    public void SelectProfile(CampaignProfileDef profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;
        Store?.RecordLastPlayed(profile.Name);
    }

    /// <summary>Seats the named profile re-read from the store, the two flight returns' door: what
    /// the mission just recorded is what the cabin then shows. False, with nobody seated, when the
    /// profile cannot be read.</summary>
    public bool SeatProfile(string name)
    {
        if (Store?.Load(name) is not { } profile)
        {
            return false;
        }

        SelectProfile(profile);
        return true;
    }

    /// <summary>Back from a job a presentation ran on the campaign's behalf, the hangar: the
    /// seated profile is re-read so a purchase or a sale shows on the cabin.</summary>
    public void Resume()
    {
        if (Profile != null && Store?.Load(Profile.Name) is { } fresh)
        {
            Profile = fresh;
        }
    }

    /// <summary>Names the mission the screens after the cabin are about.</summary>
    public void SetMission(int seq) => MissionSeq = seq;

    /// <summary>Points the ammo screen at the pilot's (0) or the wingman's (1) aircraft.</summary>
    public void SetAmmoSlot(int slot) => AmmoSlot = slot;

    /// <summary>Which crew slot's CHANGE PLANE press opened the plane selection.</summary>
    public void SetPlaneSlot(int slot) => PlaneSlot = slot;

    /// <summary>Whether the flight check may offer CHANGE PLANE for crew slot
    /// <paramref name="slot"/>, 0 the pilot's and 1 the wingman's: the two rules
    /// <c>FLIGHTCHECK.SCRIPT</c> applies, the pilot's alone barred outright on the two grant
    /// missions, and both barred while <see cref="ChangePlaneCount"/> is under the floor.</summary>
    public bool ChangePlaneAllowed(int slot) => IsGuest
        ? slot == 0 && (Profile?.Planes.Count ?? 0) >= GuestChangePlaneFloor
        : (slot != 0 || !GrantMission) && ChangePlaneCount >= ChangePlaneFloor;

    /// <summary>The flight check's own <c>uiData</c> 2021 on entry: on the two missions whose
    /// reward-table entry is ungated, the story aircraft joins the profile the first time and
    /// becomes the pilot's plane on every entry (<c>docs/formats/campaign-screens.md</c>, "Plane
    /// change"). Returns whether the profile was written; every other mission grants nothing.</summary>
    public bool GrantMissionAircraft()
    {
        if (IsGuest || !GrantMission || Profile is not { } profile || AwardDue(profile) is not { } reward)
        {
            return false;
        }

        int at = AwardIndex(profile, reward);
        if (at < 0)
        {
            if (profile.GrantedAircraft.Contains(reward.Airframe))
            {
                return false;
            }

            profile.GrantedAircraft.Add(reward.Airframe);
            profile.Planes.Add(new OwnedPlane
            {
                Name = reward.Name,
                Airframe = reward.Airframe,
                Special = true,
            });
            at = profile.Planes.Count - 1;
        }
        else if (profile.SelectedPlane == at)
        {
            return false;
        }

        profile.SelectedPlane = at;
        Store?.Save(profile);
        return true;
    }

    /// <summary>Names the scrap a zoom view opens on.</summary>
    public void SetScrapbookZoom(int mission, int spread, int item) => ZoomTarget = (mission, spread, item);

    /// <summary>Opens the book on a mission's first spread, the original's <c>uiData</c> 2405 mode
    /// 1: the mission-end entry, the table of contents' VIEW SELECTED and both CURRENT MISSION
    /// bookmarks all take this door.</summary>
    public void EnterScrapbook(int seq)
    {
        SetMission(seq);
        ScrapbookEntry++;
    }

    /// <summary>Where a scrapbook capture's file is for the seated profile, or null when there is
    /// none on disk. A <c>Snap_</c> row resolves against the profile's own directory rather than
    /// the asset library (<c>docs/formats/campaign-screens.md</c>, "The scrapbook").</summary>
    public string? CapturePath(string fileName)
    {
        if (Profile is not { } profile || Store is not { } store)
        {
            return null;
        }

        string path = Path.Combine(store.DirFor(profile.Name), fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The record the ammo screen is editing: a guest's own session-scoped aircraft while
    /// their flight check is the screen showing, else the seated profile's plane for
    /// <see cref="AmmoSlot"/>. Null when there is nothing to fit.</summary>
    public OwnedPlane? AmmoTarget()
    {
        if (Field.Current > 0)
        {
            return Field.Plane(Field.Current);
        }

        if (Profile is not { } profile)
        {
            return null;
        }

        int at = AmmoSlot == 0 ? profile.SelectedPlane : profile.WingmanPlane;
        return at >= 0 && at < profile.Planes.Count ? profile.Planes[at] : null;
    }

    /// <summary>ACCEPT LOADOUT: writes the picks into the record and, for the seated player's own
    /// aircraft, saves the profile, the original's <c>uiData</c> 2034 commit path. A guest's record
    /// is session-scoped and belongs to no profile, so their ACCEPT writes the record and saves
    /// nothing. The field (<see cref="CampaignFlightField"/>) keeps that record across the
    /// sortie's missions.</summary>
    public void CommitLoadout(OwnedPlane plane, int[] ammo, int[] ordnance)
    {
        ArgumentNullException.ThrowIfNull(plane);
        plane.Ammo = ammo;
        plane.Ordnance = ordnance;
        if (Field.Current == 0 && Profile is { } profile)
        {
            Store?.Save(profile);
        }
    }

    /// <summary>The script's permit test on the seated pair (<c>PLANESELECTION.SCRIPT</c>'s 10015):
    /// whether putting the profile's plane <paramref name="pick"/> in one crew slot clashes with
    /// plane <paramref name="other"/> in the other, compared by name, since two owned planes may
    /// share an airframe and flying that pair is legal.</summary>
    public bool SeatedPairClashes(int pick, int other)
    {
        var planes = Profile?.Planes;
        return planes != null && pick >= 0 && pick < planes.Count && other >= 0 && other < planes.Count
            && planes[pick].Name == planes[other].Name;
    }

    /// <summary>ACCEPT SELECTIONS on the seated player's check: the pilot's pick and, when the
    /// mission carries one, the wingman's into the profile, and the profile to disk. Nothing is
    /// written with nobody seated or nothing owned.</summary>
    public void CommitPlanes(int pilot, int? wingman)
    {
        if (Profile is not { } profile || profile.Planes.Count == 0)
        {
            return;
        }

        profile.SelectedPlane = pilot;
        if (wingman is { } chosen)
        {
            profile.WingmanPlane = chosen;
        }

        Store?.Save(profile);
    }

    /// <summary>ACCEPT on the memento chooser: the picked picture into the profile and the profile
    /// to disk, the original's <c>uiData</c> 2150 write. A name the award table does not carry is
    /// refused, so the cabin can never end up drawing a picture the profile has not earned.</summary>
    public void CommitMemento(string name)
    {
        if (Profile is not { } profile || CampaignMementos.Current(profile) == name)
        {
            return;
        }

        foreach (var award in CampaignMementos.Table)
        {
            if (award.Name == name && CampaignMementos.Holds(profile, award))
            {
                profile.Memento = name;
                Store?.Save(profile);
                return;
            }
        }
    }

    /// <summary>EXPORT: the plane and the loadout the campaign fitted it with, into the build store
    /// the Instant Action and multiplayer pickers list. An existing record is the build and keeps
    /// its paint, armour and engine; a starter or a granted aircraft has none, so one is created
    /// over the flight check's own build-or-stock resolution. False, with nothing written, for a
    /// stock record (named for its airframe, so a write under that name would land on any hangar
    /// plane sharing it), a nameless plane, or no build store.</summary>
    public bool ExportPlane(OwnedPlane plane)
    {
        ArgumentNullException.ThrowIfNull(plane);
        if (Planes is not { } store || Field.IsStock(plane) || string.IsNullOrWhiteSpace(plane.Name))
        {
            return false;
        }

        var def = store.Load(plane.Name) ?? CampaignProgression.BuildForOwned(plane) ?? StockBuild(plane);
        def.SetLoadout(plane.Ammo, plane.Ordnance);

        // The crossing itself: clearing the marker is what puts the aeroplane in the Instant Action
        // and Free Flight lists, and one press is all it takes.
        def.AwaitingExport = false;
        store.Save(def);
        return true;
    }

    /// <summary>The seated profile as the hangar's wallet, for <see cref="HangarFeature.Open"/>:
    /// null with nobody seated or no build store to own planes in.</summary>
    public CampaignWallet? Wallet() =>
        Profile is { } profile && Store is { } store && Planes is { } planes
            ? new CampaignWallet(store, profile, planes, Strings, Cheats)
            : null;

    /// <summary>FLY MISSION: saves the profile as it stands and builds the launch for the seated
    /// profile at <see cref="MissionSeq"/>, one seat per joined human in player order with the
    /// devices <paramref name="padsPerPlayer"/> names for each (the join flow's own binding, which
    /// the consumer cannot re-derive): the aircraft's stock node, the ammunition and ordnance the
    /// ammo screen stored, and its hangar build where it has one (a reward aircraft with no file
    /// falls back to its own award template). Null with nobody seated.</summary>
    public CampaignMissionExit? BuildExit(IReadOnlyList<IReadOnlyList<int>> padsPerPlayer)
    {
        ArgumentNullException.ThrowIfNull(padsPerPlayer);
        if (Profile is not { } profile)
        {
            return null;
        }

        Store?.Save(profile);

        // A guest flies one stock aeroplane with no build and no profile of its own, which the
        // exit's empty profile name says to the session.
        int players = IsGuest ? Math.Min(1, padsPerPlayer.Count) : padsPerPlayer.Count;
        var seats = new List<MenuSeatChoice>(players);
        for (int player = 0; player < players; player++)
        {
            var plane = Field.Plane(player) ?? new OwnedPlane();
            var custom = IsGuest ? null : Planes?.Load(plane.Name) ?? CampaignProgression.BuildForOwned(plane);
            seats.Add(new MenuSeatChoice(
                _nodeOfAirframe(plane.Airframe),
                padsPerPlayer[player],
                CampaignLoadout.For(plane, Stock),
                custom));
        }

        return new CampaignMissionExit(IsGuest ? "" : profile.Name, MissionSeq, seats);
    }

    /// <summary>Drops the open campaign: the store, the seated profile, the mission and its
    /// briefing, the sortie's field and every intent. Called on a presentation switch and by every
    /// door out of the campaign; nothing saved is touched.</summary>
    public void Discard()
    {
        Store = null;
        IsGuest = false;
        GuestReady = false;
        Planes = null;
        Stock = null;
        DataRoot = null;
        Roster = Array.Empty<string>();
        Profile = null;
        MissionSeq = -1;
        AmmoSlot = 0;
        PlaneSlot = 0;
        ScrapbookEntry = 0;
        ZoomTarget = null;
        Cheats.CloseCampaign();
        _mission = null;
        _missionSeq = -2;
        _briefing = null;
        _briefingSeq = -2;
        Field.SetPlayers(1);
        Field.Rewind();
    }

    // Where the profile already keeps this award, or -1: the reward table's own name first, then a
    // granted airframe, since the grant is what wrote that name onto the record.
    private static int AwardIndex(CampaignProfileDef profile, MissionReward reward)
    {
        int byAirframe = -1;
        for (int i = 0; i < profile.Planes.Count; i++)
        {
            var plane = profile.Planes[i];
            if (plane.Name == reward.Name)
            {
                return i;
            }

            if (byAirframe < 0 && plane.Special && plane.Airframe == reward.Airframe)
            {
                byAirframe = i;
            }
        }

        return byAirframe;
    }

    private static int StarterAirframeOf(ushort airframes)
    {
        if ((airframes & (1 << GuestStarterAirframe)) != 0 || airframes == 0)
        {
            return GuestStarterAirframe;
        }

        for (int a = 0; a < 16; a++)
        {
            if ((airframes & (1 << a)) != 0)
            {
                return a;
            }
        }

        return GuestStarterAirframe;
    }

    private static ushort GuestAirframes(CampaignProfileDef profile)
    {
        int mask = 0;
        foreach (var plane in profile.Planes)
        {
            mask |= 1 << plane.Airframe;
        }

        return (ushort)mask;
    }

    // Writes a fit's stored values onto a record. A stock fit leaves the record at rest, and so does
    // an unset gun slot, since the ammo screen has no "unset" value to show.
    private static void Refit(OwnedPlane plane, Net.CoopFit fit)
    {
        if (fit.IsStock)
        {
            return;
        }

        for (int slot = 0; slot < plane.Ammo.Length && slot < Net.CoopFit.GunSlots; slot++)
        {
            int stored = fit.AmmoAt(slot);
            if (stored >= 0)
            {
                plane.Ammo[slot] = stored;
            }
        }

        for (int cell = 0; cell < plane.Ordnance.Length && cell < Net.CoopFit.Cells; cell++)
        {
            plane.Ordnance[cell] = fit.OrdnanceAt(cell);
        }
    }

    // Which refusal a rejected name earns: too long has its own string (langui 212), anything else
    // is the character rule (707). Both are the original's, and both fall back to their own words.
    private string NameRefusal(string name)
    {
        if (name.Length <= MaxNameLength)
        {
            return Strings.Text(707,
                "Your player name is limited to alphabetic and numeric characters and spaces.");
        }

        string limit = Strings.Format(212, MaxNameLength);
        return limit.Length > 0
            ? limit
            : $"Your player name is limited to {MaxNameLength} characters.";
    }

    // The reward-table entry uiData 2021 would grant on MissionSeq, or null when the mission has
    // none, its record awards no aircraft, or its objective gate is unmet. An objective field of 0
    // is the table's "no gate", which is what makes the two grant missions unconditional.
    private MissionReward? AwardDue(CampaignProfileDef profile)
    {
        foreach (var reward in CampaignProgression.MissionRewards)
        {
            if (reward.Ordinal != MissionSeq + 1 || !reward.AwardsAircraft)
            {
                continue;
            }

            int met = CampaignProgression.ResultOf(profile, MissionSeq)?.Best.CompletedMask ?? 0;
            return reward.ObjectiveBit == 0 || (met & (1 << reward.ObjectiveBit)) != 0 ? reward : null;
        }

        return null;
    }

    // One stock aeroplane per offered airframe, in airframe order, each named for its airframe, with
    // the picked one carrying its fit. Never Special, since an award record resolves to the award's
    // build and a guest flies stock only. Every other airframe opens on its own stock fit.
    private CampaignProfileDef GuestProfile(string hostName, int progress, ushort airframes, int picked, Net.CoopFit fit)
    {
        var profile = new CampaignProfileDef { Name = hostName, MissionsCompleted = Math.Max(0, progress) };
        int offered = airframes == 0 ? 1 << GuestStarterAirframe : airframes;
        for (int a = 0; a < CampaignProgression.AirframeCount && a < 16; a++)
        {
            if ((offered & (1 << a)) == 0)
            {
                continue;
            }

            var plane = new OwnedPlane { Name = Strings.Text(3000 + a, $"Airframe {a}"), Airframe = a };
            if (a == picked)
            {
                profile.SelectedPlane = profile.Planes.Count;
                Refit(plane, fit);
            }

            profile.Planes.Add(plane);
        }

        profile.WingmanPlane = profile.SelectedPlane;
        return profile;
    }

    private CustomPlaneDef StockBuild(OwnedPlane plane)
    {
        var def = new CustomPlaneDef { Name = plane.Name, Airframe = plane.Airframe };
        HangarFeature.LoadStockWeapons(def, Stock?.ForModel(_nodeOfAirframe(plane.Airframe)));
        return def;
    }

    // The sequence entry for MissionSeq. Absent data, an unreadable file or a sequence with no such
    // entry all read as null: a screen then draws no wingman rather than refusing to open.
    private CampaignMission? ReadMission()
    {
        if (DataRoot is not { } root)
        {
            return null;
        }

        try
        {
            string zrdrPath = SessionPaths.PreferUnzipped(Path.Combine(root, "extracted", "zrdr.zip"));
            foreach (var mission in CampaignSequence.Load(zrdrPath))
            {
                if (mission.Seq == MissionSeq)
                {
                    return mission;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }

        return null;
    }
}
