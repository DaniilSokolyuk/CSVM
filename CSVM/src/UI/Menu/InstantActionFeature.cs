using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;

namespace CSVM.UI.Menu;

/// <summary>One Instant Action environment: its dropdown name and the chapter it flies.</summary>
public readonly record struct InstantActionEnvironment(string Name, string Code);

/// <summary>One mission type: its label and the <c>ia.json</c> <c>mission_type</c> key.</summary>
public readonly record struct InstantActionMissionType(string Label, string Key);

/// <summary>One stock airframe: its display name and the planes.zbd node the launch builds.</summary>
public readonly record struct InstantActionAirframe(string Name, string Node);

/// <summary>One militia, the aircraft it flies in the airframe dropdown's order, and the accent
/// id its wave speaks with. The militia is the only thing that gives a wave a voice: the setup
/// screen writes the accent beside the livery, so picking a militia picks the pilots
/// (docs/formats/instant-action.md, "A militia also picks the wave's voice").</summary>
public readonly record struct InstantActionMilitia(string Name, IReadOnlyList<string> Aircraft, int AccentId);

/// <summary>One wave as the setup holds it: how many enemies (0 is unconfigured), and the
/// militia, aircraft and skill cursors. The aircraft index is into the militia's own list.</summary>
public readonly record struct InstantActionWaveSetup(int Count, int MilitiaIndex, int AircraftIndex, int SkillIndex);

/// <summary>
/// Instant Action as a shared feature: the decoded option sets (environments, mission types,
/// airframes, militias, skills, presets), the setup a presentation configures (environment,
/// mission type, lives, four waves, wingmen and their fit, the player's aircraft), the rules
/// between them (a militia change resets its aircraft, the ace duel takes no waves and no wingmen,
/// the clouds bar stunt flying), the launch gate and the typed <see cref="LaunchExit"/> carrying
/// the built <see cref="InstantActionDef"/>. Every option set is the decode in
/// <c>docs/formats/instant-action.md</c>; a presentation offers them however it likes and never
/// re-derives them. The seats stay the presentation's until the shared player setup owns them.
/// </summary>
public sealed class InstantActionFeature : IMenuFeature
{
    /// <summary>The mode every exit this feature builds carries.</summary>
    public const MenuMode Mode = MenuMode.Stunt;

    /// <summary>The <c>mission_type</c> key of the ace duel, the type that takes no waves.</summary>
    public const string AceKey = "dogfight_ace";

    /// <summary>The <c>mission_type</c> key the clouds bar.</summary>
    public const string StuntKey = "stunt_flying";

    /// <summary>The four wave slots the setup screen and the def both carry.</summary>
    public const int WaveSlots = 4;

    /// <summary>The enemy dropdown's range, 0 to 6.</summary>
    public const int MaxEnemies = 6;

    /// <summary>The wingman dropdown's range, 0 to 5.</summary>
    public const int MaxWingmen = 5;

    // The lives stepper's range: 0 is unlimited, 1 the faithful one-life run. INVENTED, since
    // ia.json carries no such field, so there is no decoded range to match.
    /// <summary>The lives stepper's cap.</summary>
    public const int MaxLives = 9;

    // The seven environments in the decoded dropdown order (FUN_004174d0), which is not the
    // alphabetic chapter order. C1C is the chapter Instant Action omits.
    private static readonly InstantActionEnvironment[] EnvironmentRows =
    {
        new("an airfield", "C1"),
        new("the clouds", "C2B"),
        new("Hawaii", "C3"),
        new("Manhattan", "C5"),
        new("the ocean", "C1B"),
        new("Sky Haven", "C4"),
        new("a movie studio", "C2"),
    };

    // The four mission types in the UI dropdown's order (IDS_IA_MISSIONTYPE), not the internal id
    // order; the labels come from one place so no screen names a mission its own way.
    private static readonly InstantActionMissionType[] MissionTypeRows =
    {
        new(InstantAction.MissionTypeLabel(AceKey), AceKey),
        new(InstantAction.MissionTypeLabel("dogfight_squadron"), "dogfight_squadron"),
        new(InstantAction.MissionTypeLabel(StuntKey), StuntKey),
        new(InstantAction.MissionTypeLabel("zeppelin_run"), "zeppelin_run"),
    };

    // The eleven airframes in the langui 3700 order, which the original stores an aircraft as an
    // index into. Names are ia.json's singular vocabulary (Autogyro, not Hoplite).
    private static readonly InstantActionAirframe[] AirframeRows =
    {
        new("Autogyro", "player_autogyro"),
        new("Hellhound", "player_avenger"),
        new("Balmoral", "player_balmoral"),
        new("Bloodhawk", "player_bhawk"),
        new("Brigand", "player_brigand"),
        new("Devastator", "player_pfighter"),
        new("Firebrand", "player_fbrand"),
        new("Fury", "player_fury"),
        new("Kestrel", "player_kestrel"),
        new("Peacemaker", "player_peacemaker"),
        new("Warhawk", "player_warhawk"),
    };

    private static readonly string[] AirframeNames = Array.ConvertAll(AirframeRows, a => a.Name);

    // The thirteen militias in the langui 3670 order, each with the aircraft FUN_00410420's mask
    // allows it, in the airframe order above; the mask filters the dropdown, never reorders it.
    // The accent is the same switch's own write, one per militia; Fortune Hunter's 12 is the
    // wingman range's base and is re-rolled to 12..16 per aircraft at spawn.
    private static readonly InstantActionMilitia[] MilitiaRows =
    {
        new("Black Hat", new[] { "Autogyro", "Brigand", "Warhawk" }, 0),
        new("Black Swan", new[] { "Fury" }, 1),
        new("Blake Aviation", new[] { "Bloodhawk", "Peacemaker" }, 2),
        new("British", new[] { "Balmoral", "Peacemaker" }, 3),
        new("Fortune Hunter", AirframeNames, 12),
        new("Hollywood Knight", new[] { "Firebrand" }, 7),
        new("Hughes Aviation", new[] { "Bloodhawk", "Fury", "Kestrel" }, 10),
        new("Medusa", new[] { "Brigand", "Kestrel" }, 8),
        new("Russian", new[] { "Devastator" }, 5),
        new("Sacred Trust", new[] { "Hellhound", "Warhawk" }, 9),
        new("German", new[] { "Hellhound" }, 6),
        new("Studio Security", new[] { "Autogyro", "Fury" }, 10),
        new("Broadway Bomber", new[] { "Peacemaker" }, 4),
    };

    private static readonly string[] SkillRows = { "novice", "veteran", "ace" };

    private readonly Func<string, InstantActionDef> _loadBase;
    private readonly InstantActionWaveSetup[] _waves = new InstantActionWaveSetup[WaveSlots];

    /// <summary>A feature whose environment defs come from <paramref name="loadBase"/>, called with
    /// a chapter code when an environment is confirmed. The loader's failure policy is its own;
    /// see <see cref="ForDataRoot"/> for the one the game uses.</summary>
    public InstantActionFeature(Func<string, InstantActionDef> loadBase)
    {
        _loadBase = loadBase ?? throw new ArgumentNullException(nameof(loadBase));
        Discard();
    }

    /// <summary>The seven environments in the decoded dropdown order.</summary>
    public static IReadOnlyList<InstantActionEnvironment> Environments => EnvironmentRows;

    /// <summary>All four mission types in the dropdown order, unfiltered.</summary>
    public static IReadOnlyList<InstantActionMissionType> AllMissionTypes => MissionTypeRows;

    /// <summary>The eleven stock airframes in the aircraft dropdown's order.</summary>
    public static IReadOnlyList<InstantActionAirframe> Airframes => AirframeRows;

    /// <summary>The thirteen militias in the dropdown order, each with its aircraft.</summary>
    public static IReadOnlyList<InstantActionMilitia> Militias => MilitiaRows;

    /// <summary>The three skills in the dropdown order, as <c>InstantActionWave.EnemySkill</c> keys.</summary>
    public static IReadOnlyList<string> Skills => SkillRows;

    /// <summary>The presets the Table of Contents offers, in its order.</summary>
    public static IReadOnlyList<InstantActionPresets.Preset> Presets => InstantActionPresets.All;

    /// <summary>The picked environment's row.</summary>
    public int EnvironmentIndex { get; private set; }

    /// <summary>The picked environment.</summary>
    public InstantActionEnvironment Environment => EnvironmentRows[EnvironmentIndex];

    /// <summary>The mission types the picked environment offers, in the dropdown order: all four,
    /// minus stunt flying where the chapter bars it (only the clouds).</summary>
    public IReadOnlyList<InstantActionMissionType> MissionTypes => MissionTypesFor(Environment.Code);

    /// <summary>The picked mission type's row within <see cref="MissionTypes"/>. A presentation
    /// keeps it inside the list by confirming the environment after changing it.</summary>
    public int MissionTypeIndex { get; private set; }

    /// <summary>The picked mission type.</summary>
    public InstantActionMissionType MissionType => MissionTypes[MissionTypeIndex];

    /// <summary>Whether the picked mission is the ace duel, which takes no waves and no wingmen.</summary>
    public bool IsAceDuel => MissionType.Key == AceKey;

    /// <summary>The lives stepper: 0 is unlimited, 1 the default one-life run.</summary>
    public int Lives { get; private set; }

    /// <summary>The four waves as configured.</summary>
    public IReadOnlyList<InstantActionWaveSetup> Waves => _waves;

    /// <summary>The wingman count, 0 to 5.</summary>
    public int NumWingmen { get; private set; }

    /// <summary>The wingmen's airframe row. Starts on the first row, the Autogyro, which is the
    /// value the original's own screen selects when the player has no saved planes.</summary>
    public int WingmanPlaneIndex { get; private set; }

    /// <summary>The wingmen's airframe.</summary>
    public InstantActionAirframe WingmanPlane => AirframeRows[WingmanPlaneIndex];

    /// <summary>The one fit every wingman flies (the original's Player/Wingman radio is not per
    /// wingman), edited in place by a presentation's loadout screen; stock until it is.</summary>
    public LoadoutChoice WingmanFit { get; private set; } = new();

    /// <summary>The fit a launch hands the wingmen: <see cref="WingmanFit"/> once it is edited, and
    /// null for the stock fit. Also null when no wingmen fly (none set, or the ace duel), so a stale
    /// pick cannot outlive the count going to 0.</summary>
    public LoadoutChoice? LaunchWingmanFit =>
        IsAceDuel || NumWingmen <= 0 || WingmanFit.IsStock ? null : WingmanFit;

    /// <summary>The player's airframe row, as the presets and a single-seat presentation pick it.
    /// A presentation with its own roster passes the flown name to <see cref="BuildExit"/> instead.</summary>
    public int PlayerPlaneIndex { get; private set; }

    /// <summary>The player's airframe.</summary>
    public InstantActionAirframe PlayerPlane => AirframeRows[PlayerPlaneIndex];

    /// <summary>The applied preset's row, or -1 on the custom path. A label for the heading only:
    /// what flies is whatever the fields say, byte-identical to the same setup entered by hand.</summary>
    public int PresetIndex { get; private set; }

    /// <summary>The confirmed environment's own <c>ia.zrd.json</c> (the ace, the zeppelin nodes,
    /// <c>disallow_missions</c>), or null until <see cref="ConfirmEnvironment"/> has run.</summary>
    public InstantActionDef? BaseDef { get; private set; }

    /// <summary>The feature the game runs: environment defs read from the chapter's own
    /// <c>IA1/ia.zrd.json</c> under <paramref name="dataRoot"/>, a failed read falling back to the
    /// built-in defaults with a warning rather than leaving the menu unable to proceed.</summary>
    public static InstantActionFeature ForDataRoot(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        return new InstantActionFeature(code =>
        {
            try
            {
                return InstantAction.Load(SessionPaths.MissionZrdr(dataRoot, code, "IA1"));
            }
            catch (Exception e)
            {
                Log.Warn("ui", $"instant action: '{code}/IA1' ia.zrd.json failed to load ({e.Message}); the ace and zeppelin fields use the built-in defaults");
                return InstantAction.Defaults();
            }
        });
    }

    /// <summary>The mission types a chapter offers: all four, minus stunt flying where its
    /// <c>disallow_missions</c> bars it, read off the shared roster's Danger Zones flag.</summary>
    public static IReadOnlyList<InstantActionMissionType> MissionTypesFor(string chapterCode) =>
        MenuChapters.DangerZonesFor(chapterCode)
            ? MissionTypeRows
            : Array.FindAll(MissionTypeRows, m => m.Key != StuntKey);

    /// <summary>Whether an environment row may be picked under a mission type: stunt flying clears
    /// the clouds (<c>FUN_004103b0</c>'s mask), every other type allows all seven.</summary>
    public static bool EnvironmentAllowed(int environmentIndex, string missionTypeKey) =>
        missionTypeKey != StuntKey || MenuChapters.DangerZonesFor(EnvironmentRows[environmentIndex].Code);

    /// <summary>A militia's aircraft by the militia's name. Throws on a name no militia carries.</summary>
    public static IReadOnlyList<string> AircraftFor(string militiaName)
    {
        foreach (var militia in MilitiaRows)
        {
            if (militia.Name == militiaName)
            {
                return militia.Aircraft;
            }
        }

        throw new ArgumentException($"'{militiaName}' is not an Instant Action militia", nameof(militiaName));
    }

    /// <summary>The airframe row carrying a display name, or -1.</summary>
    public static int AirframeIndex(string name) => Array.IndexOf(AirframeNames, name);

    /// <summary>A lives value as a screen writes it: Unlimited at zero, else the count. One
    /// vocabulary, so both presentations name the same setting the same way.</summary>
    public static string LivesLabel(int lives) =>
        lives <= 0 ? "Unlimited" : lives.ToString(CultureInfo.InvariantCulture);

    /// <summary>One wave slot as the <see cref="InstantActionWave"/> the def stores: the empty
    /// wave at 0 enemies whatever the cursors, so an unconfigured slot matches an omitted
    /// <c>groupN</c> byte for byte; else the militia's aircraft at the skill, with a plain label
    /// for the name and the militia's own accent.
    /// ⚠ Keep the accent on the configured wave alone. A slot at 0 enemies must stay byte-equal to
    /// the empty wave, whose accent is the record reset's -1.</summary>
    public static InstantActionWave WaveFor(int count, int militiaIndex, int aircraftIndex, int skillIndex)
    {
        if (count <= 0)
        {
            return InstantAction.EmptyWave;
        }

        var militia = MilitiaRows[militiaIndex];
        string aircraft = militia.Aircraft[aircraftIndex];
        return new InstantActionWave(
            count, $"{militia.Name} {aircraft}", aircraft, SkillRows[skillIndex], militia.AccentId);
    }

    /// <summary>Picks an environment row. The mission type is not re-fitted until
    /// <see cref="ConfirmEnvironment"/>, so a cursor passing over the clouds changes nothing.</summary>
    public void SelectEnvironment(int index)
    {
        EnvironmentIndex = Index(index, EnvironmentRows.Length, nameof(index));
    }

    /// <summary>Settles the picked environment: wraps the mission type onto a row the environment
    /// offers and loads the chapter's own def as the launch's base.</summary>
    public void ConfirmEnvironment()
    {
        MissionTypeIndex = Wrap(MissionTypeIndex, MissionTypes.Count);
        BaseDef = _loadBase(Environment.Code);
    }

    /// <summary>Picks a mission type row within <see cref="MissionTypes"/>.</summary>
    public void SelectMissionType(int index)
    {
        MissionTypeIndex = Index(index, MissionTypes.Count, nameof(index));
    }

    /// <summary>Steps the lives, clamped to 0 (unlimited) and <see cref="MaxLives"/>.</summary>
    public void StepLives(int direction) => Lives = Math.Clamp(Lives + direction, 0, MaxLives);

    /// <summary>One wave's aircraft roster, its militia's.</summary>
    public IReadOnlyList<string> WaveAircraft(int wave) => MilitiaRows[_waves[Slot(wave)].MilitiaIndex].Aircraft;

    /// <summary>Writes one wave whole, every cursor checked against its roster.</summary>
    public void SetWave(int wave, InstantActionWaveSetup setup)
    {
        int militia = Index(setup.MilitiaIndex, MilitiaRows.Length, nameof(setup));
        _waves[Slot(wave)] = new InstantActionWaveSetup(
            Math.Clamp(setup.Count, 0, MaxEnemies),
            militia,
            Index(setup.AircraftIndex, MilitiaRows[militia].Aircraft.Count, nameof(setup)),
            Index(setup.SkillIndex, SkillRows.Length, nameof(setup)));
    }

    /// <summary>Steps a wave's enemy count, clamped to 0 and <see cref="MaxEnemies"/>.</summary>
    public void StepWaveCount(int wave, int direction)
    {
        var w = _waves[Slot(wave)];
        _waves[wave] = w with { Count = Math.Clamp(w.Count + direction, 0, MaxEnemies) };
    }

    /// <summary>Steps a wave's militia with wrap. A new militia resets the wave's aircraft to the
    /// first it flies, the decoded screen's own rule (<c>AV[BA].QG = 0</c>).</summary>
    public void StepWaveMilitia(int wave, int direction) =>
        SelectWaveMilitia(wave, Wrap(_waves[Slot(wave)].MilitiaIndex + direction, MilitiaRows.Length));

    /// <summary>Picks a wave's militia, resetting its aircraft to the militia's first.</summary>
    public void SelectWaveMilitia(int wave, int index)
    {
        var w = _waves[Slot(wave)];
        _waves[wave] = w with { MilitiaIndex = Index(index, MilitiaRows.Length, nameof(index)), AircraftIndex = 0 };
    }

    /// <summary>Steps a wave's aircraft within its militia's roster, with wrap.</summary>
    public void StepWaveAircraft(int wave, int direction)
    {
        var w = _waves[Slot(wave)];
        _waves[wave] = w with { AircraftIndex = Wrap(w.AircraftIndex + direction, MilitiaRows[w.MilitiaIndex].Aircraft.Count) };
    }

    /// <summary>Picks a wave's aircraft within its militia's roster.</summary>
    public void SelectWaveAircraft(int wave, int index)
    {
        var w = _waves[Slot(wave)];
        _waves[wave] = w with { AircraftIndex = Index(index, MilitiaRows[w.MilitiaIndex].Aircraft.Count, nameof(index)) };
    }

    /// <summary>Steps a wave's skill with wrap.</summary>
    public void StepWaveSkill(int wave, int direction)
    {
        var w = _waves[Slot(wave)];
        _waves[wave] = w with { SkillIndex = Wrap(w.SkillIndex + direction, SkillRows.Length) };
    }

    /// <summary>Picks a wave's skill.</summary>
    public void SelectWaveSkill(int wave, int index)
    {
        var w = _waves[Slot(wave)];
        _waves[wave] = w with { SkillIndex = Index(index, SkillRows.Length, nameof(index)) };
    }

    /// <summary>Steps the wingman count, clamped to 0 and <see cref="MaxWingmen"/>.</summary>
    public void StepWingmen(int direction) => NumWingmen = Math.Clamp(NumWingmen + direction, 0, MaxWingmen);

    /// <summary>Sets the wingman count, clamped.</summary>
    public void SetWingmen(int count) => NumWingmen = Math.Clamp(count, 0, MaxWingmen);

    /// <summary>Steps the wingmen's airframe with wrap. A changed airframe drops the fit: gun
    /// slots and pylons are per airframe, so a fit for one has nowhere to live on another.</summary>
    public void StepWingmanPlane(int direction) => SelectWingmanPlane(Wrap(WingmanPlaneIndex + direction, AirframeRows.Length));

    /// <summary>Picks the wingmen's airframe row, dropping the fit when it changes.</summary>
    public void SelectWingmanPlane(int index)
    {
        int next = Index(index, AirframeRows.Length, nameof(index));
        if (next != WingmanPlaneIndex)
        {
            WingmanFit.ResetToStock();
        }

        WingmanPlaneIndex = next;
    }

    /// <summary>Puts the wingmen back on the stock fit.</summary>
    public void ResetWingmanFit() => WingmanFit.ResetToStock();

    /// <summary>Picks the player's airframe row.</summary>
    public void SelectPlayerPlane(int index)
    {
        PlayerPlaneIndex = Index(index, AirframeRows.Length, nameof(index));
    }

    /// <summary>Writes one Table of Contents preset over the fields: environment, mission type,
    /// player aircraft, wingman count (and aircraft where there are wingmen), and all four waves.
    /// Deliberately partial: the lives are invented and have no preset value, and the base def is
    /// left to <see cref="ConfirmEnvironment"/>, so what flies is exactly what the fields say.</summary>
    public void ApplyPreset(int index)
    {
        var applied = InstantActionPresets.Resolve(index, WaveSlots);
        PresetIndex = index;
        EnvironmentIndex = applied.EnvironmentIndex;
        MissionTypeIndex = applied.MissionTypeIndex;
        PlayerPlaneIndex = applied.PlayerPlaneIndex;
        NumWingmen = applied.NumWingmen;
        if (applied.WingmanPlaneIndex is { } wingman)
        {
            WingmanPlaneIndex = wingman;
            WingmanFit.ResetToStock();
        }

        for (int i = 0; i < WaveSlots; i++)
        {
            var wave = applied.Waves[i];
            _waves[i] = new InstantActionWaveSetup(wave.Count, wave.MilitiaIndex, wave.AircraftIndex, wave.SkillIndex);
        }
    }

    /// <summary>Why a launch is refused right now, or null when it may go: at least one seat must
    /// be joined and every joined seat must have confirmed. The setup itself is always complete,
    /// since every field has a value from the start.</summary>
    public string? Refusal(int joinedSeats, int confirmedSeats)
    {
        if (joinedSeats < 1)
        {
            return "no seat joined";
        }

        if (confirmedSeats != joinedSeats)
        {
            return $"{joinedSeats - confirmedSeats} of {joinedSeats} seats not confirmed";
        }

        return null;
    }

    /// <summary>Whether the launch gate is open; see <see cref="Refusal"/> for the rule.</summary>
    public bool CanLaunch(int joinedSeats, int confirmedSeats) => Refusal(joinedSeats, confirmedSeats) == null;

    /// <summary>The waves as the def stores them, the empty wave wherever a slot has no enemies.</summary>
    public IReadOnlyList<InstantActionWave> BuildWaves()
    {
        var waves = new List<InstantActionWave>(WaveSlots);
        foreach (var w in _waves)
        {
            waves.Add(WaveFor(w.Count, w.MilitiaIndex, w.AircraftIndex, w.SkillIndex));
        }

        return waves;
    }

    /// <summary>The built def over the confirmed environment's base (the built-in defaults when
    /// no environment was confirmed, which only an aid that skips the confirm can reach).
    /// <paramref name="playerPlane"/> names the flown aircraft where a presentation's roster is
    /// wider than the stock eleven; null takes <see cref="PlayerPlane"/>.</summary>
    public InstantActionDef BuildDef(string? playerPlane = null) =>
        InstantAction.BuildFromWizard(
            BaseDef ?? InstantAction.Defaults(),
            MissionType.Key,
            playerPlane ?? PlayerPlane.Name,
            NumWingmen,
            WingmanPlane.Name,
            BuildWaves(),
            Lives);

    /// <summary>The typed exit for the confirmed seats, in seat order. It carries the environment's
    /// chapter, the seats, the mode, the built def and <see cref="LaunchWingmanFit"/>. Throws when the gate is closed or a
    /// seat names no plane, so a half-built launch cannot leave the menu.</summary>
    public LaunchExit BuildExit(IReadOnlyList<MenuSeatChoice> seats, string? playerPlane = null)
    {
        ArgumentNullException.ThrowIfNull(seats);
        if (Refusal(seats.Count, seats.Count) is { } refusal)
        {
            throw new InvalidOperationException($"Instant Action cannot launch: {refusal}");
        }

        for (int i = 0; i < seats.Count; i++)
        {
            if (string.IsNullOrEmpty(seats[i].PlaneNode))
            {
                throw new InvalidOperationException($"Instant Action cannot launch: seat {i + 1} names no plane");
            }
        }

        return new LaunchExit(Environment.Code, seats, Mode, BuildDef(playerPlane),
            WingmanLoadout: LaunchWingmanFit);
    }

    /// <summary>Puts every field back to the screen's opening state: the first environment, the
    /// ace duel, one life, four empty waves with their cursors on the first rows, no wingmen on
    /// the first airframe with the stock fit, the first airframe for the player, no preset and no
    /// base def. The option sets are not state and stay.</summary>
    public void Discard()
    {
        EnvironmentIndex = 0;
        MissionTypeIndex = 0;
        Lives = 1;
        NumWingmen = 0;
        WingmanPlaneIndex = 0;
        WingmanFit = new LoadoutChoice();
        PlayerPlaneIndex = 0;
        PresetIndex = -1;
        BaseDef = null;
        for (int i = 0; i < WaveSlots; i++)
        {
            _waves[i] = default;
        }
    }

    private static int Wrap(int index, int count) => ((index % count) + count) % count;

    private static int Index(int index, int count, string name)
    {
        if (index < 0 || index >= count)
        {
            throw new ArgumentOutOfRangeException(name, index, $"{count} rows");
        }

        return index;
    }

    private static int Slot(int wave)
    {
        if (wave < 0 || wave >= WaveSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(wave), wave, $"{WaveSlots} wave slots");
        }

        return wave;
    }
}
