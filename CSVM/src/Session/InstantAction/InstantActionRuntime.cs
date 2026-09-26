using System;
using System.Collections.Generic;
using CSVM.Flight.Ai;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;

namespace CSVM.Session.InstantAction;

/// <summary>How an Instant Action mission finished. One-way: the
/// first outcome reached stands, so a win and the last human's death landing in the same sim step
/// cannot overwrite each other.</summary>
public enum InstantActionOutcome
{
    Running,
    Won,
    Lost,
}

/// <summary>The one thing that has to happen for a mission to be WON, one per mission type.
/// Every signal source reports the objective it has just satisfied
/// and <see cref="InstantActionRuntime.ReportObjective"/> drops the ones this mission does not run
/// on, which is what lets a zeppelin run clear all four of its waves (F12 credits them either way)
/// without that counting as the win.</summary>
public enum InstantActionObjective
{
    AceDown,
    WavesCleared,
    ZonesFlown,

    /// <summary>The zeppelin run's win. Two decoded paths satisfy it, whichever lands first:
    /// every engine destroyed (the objective the briefing and the target panel name), or the hull
    /// dying on the gasbag survivor threshold. See <see cref="ObjectiveFor"/>.</summary>
    ZeppelinDisabled,
}

/// <summary>Owns one Instant Action mission's actor set: the loaded <see cref="Def"/>, the ace's
/// spawn draw, the wingmen's fan placement, each wave's per-member draws
/// (<see cref="RandomPilotStats"/>, <see cref="ResolveWaveAccentId"/>), the objective-zeppelin
/// selection, and the mission's end (<see cref="Objective"/>, the lives ledger,
/// <see cref="Outcome"/>). Detail: this module's docs/architecture.md entry.
/// The end half holds no engine type and calls no <c>GD.*</c>, the same construction rule
/// <see cref="Flight.Modes.VersusMatch"/> follows.
/// ⚠ Environment→chapter resolution belongs to the launch menu, never here, a --ia=&lt;path&gt;
/// CLI launch already names its chapter via --chapter=.
/// ⚠ <see cref="WingmanSlotFor"/>/<see cref="FlownWingmen"/> place and clamp only; livery, team
/// and the AiGunner/AiModeMachine wiring stay <c>GameSession.BuildFlightRigs</c>'s job.</summary>
public sealed class InstantActionRuntime
{
    /// <summary>Every Instant Action enemy's team ("the
    /// decoded turret convention... every Instant Action enemy is team 2"). Waves are
    /// cohorts inside this one team, not teams of their own.</summary>
    public const int EnemyTeam = AimAssist.PlayerTeam + 1;

    /// <summary><c>mission_type</c> id 2 (docs/formats/instant-action.md "The mission-type ids"),
    /// the one mode whose enemies reach the air out of the objective zeppelin's bay rather than
    /// through the wave teleport (F12).</summary>
    public const string ZeppelinRunMissionType = "zeppelin_run";

    /// <summary>The radius <c>FUN_0045a240</c> writes into all THREE of an Instant Action actor's
    /// volumes, activation, attack and return, with a ±10000 m altitude band
    /// (docs/formats/instant-action.md "Every actor is a synthetic aiv roster block"): the engine's
    /// own way of saying an Instant Action actor is always awake, always willing to engage, and
    /// never returns.</summary>
    public const float ActorVolumeRadiusM = 10000f;

    /// <summary>How long the live world runs on after the ending before the wrap-up board takes
    /// the screen. Decoded: the original's Instant Action tick seeds this countdown on the frame
    /// the goal is reached and ticks it down on frame dt while the world runs in full
    /// (docs/formats/instant-action/wrap-up.md, "The hold after the ending"). ⚠ The original holds
    /// nothing on a death; what it waits out there is the crash animation, which this same hold
    /// stands in for.</summary>
    public const float WrapupHoldS = 3f;

    // The five hand-authored pilot personalities a wave member's nine-stat vector is rolled
    // from, `row = draw % 5` per aircraft (docs/formats/instant-action.md "A wave enemy's nine
    // pilot stats are drawn at random from a table of five, not from its skill").
    private static readonly int[][] PilotPersonalities =
    {
        new[] { 5, 7, 5, 3, 2, 1, 4, 4, 4 },
        new[] { 4, 3, 4, 5, 7, 5, 3, 5, 5 },
        new[] { 3, 4, 3, 7, 3, 6, 4, 5, 4 },
        new[] { 7, 5, 6, 3, 3, 2, 4, 4, 4 },
        new[] { 4, 4, 4, 4, 4, 4, 4, 4, 4 },
    };

    // The lives ledger (decision 15, INVENTED, no ia.json key carries one): one counter per
    // human seat, plus the seats that have run out and are watching. Both keyed by the pilot's own
    // FlightController.PlayerIndex, never by a synthetic index.
    private readonly Dictionary<int, int> _lives = new();
    private readonly HashSet<int> _spectators = new();

    // What is left of the hold between the ending and the wrap-up board, counted down by Advance
    // on sim dt. Zero before the ending and again once the board is due.
    private float _wrapupHold;

    public InstantActionRuntime(InstantActionDef def)
    {
        Def = def;
    }

    /// <summary>Raised once, with the outcome, the instant the mission ends. The wrap-up board
    /// is the subscriber this exists for; G13's own subscriber is the session's log line.
    /// </summary>
    public event Action<InstantActionOutcome>? MissionEnded;

    /// <summary>Raised once, <see cref="WrapupHoldS"/> of sim time after <see cref="MissionEnded"/>:
    /// the cue to present the wrap-up board. The two are separate because the outcome is decided at
    /// the ending and the screen is not, which is the whole of the hold the world runs on
    /// through.</summary>
    public event Action<InstantActionOutcome>? WrapupDue;

    public InstantActionDef Def { get; }

    /// <summary>What this mission must achieve to be won, or null for a mission type with no end
    /// condition here: <c>ground_target</c> (which every shipped map's <c>disallow_missions</c>
    /// bars and this milestone does not implement) and any unrecognised hand-authored value. Such
    /// a mission can still be LOST, it simply cannot be won, which is a deliberate disable in
    /// <see cref="Flight.Modes.VersusMatch"/>'s shape rather than an error.</summary>
    public InstantActionObjective? Objective => ObjectiveFor(Def.MissionType);

    /// <summary><see cref="InstantActionOutcome.Running"/> until the objective is reported or
    /// every human is out of lives; then the outcome that stands, forever.</summary>
    public InstantActionOutcome Outcome { get; private set; }

    public bool Ended => Outcome != InstantActionOutcome.Running;

    /// <summary>Whether the mission has ended and the hold before the wrap-up board is still
    /// running. The outcome already stands; what has not happened yet is the screen, and the world
    /// keeps flying meanwhile.</summary>
    public bool HoldingWrapup => _wrapupHold > 0f;

    /// <summary>Mission time in seconds, advanced by <see cref="Advance"/> on SIM dt alone (never
    /// wall time, a halt freezes it with the simulation, the rule the match clock already
    /// follows) and frozen the moment the mission ends. It is the value the wrap-up's "Time to
    /// Complete Mission" row renders; its stopping point is this item's, because the end is
    /// the only place it can be stopped.</summary>
    public float Elapsed { get; private set; }

    /// <summary>False once <see cref="DisableObjective"/> has recorded that this mission's win
    /// signal can never arrive (a squadron with no wave enemy configured, a stunt mission on a
    /// chapter shipping no <c>dzones</c>, a zeppelin run with no zeppelin runtime). The mission
    /// then runs on and can still be lost. Its caller must say so out loud, a mission that cannot
    /// be won and does not report it reads as a broken end condition.</summary>
    public bool ObjectiveEnabled { get; private set; } = true;

    /// <summary>How many human seats are in this mission's lives ledger
    /// (<see cref="RegisterPilot"/>).</summary>
    public int PilotCount => _lives.Count;

    /// <summary>Whether this mission is the zeppelin run (F12). The mode is exclusive in the
    /// decode rather than additive: its waves take the generator arm and the teleport arm never
    /// runs (<see cref="InstantActionWaves"/>), and its objective zeppelin is the one world node
    /// the builder switches ON rather than off (<see cref="SelectedZeppelinNode"/>).</summary>
    public bool IsZeppelinRun =>
        string.Equals(Def.MissionType, ZeppelinRunMissionType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The mission type's own win condition: the ace down, every configured wave
    /// cleared, every player's zone set flown, or the zeppelin disabled. Null for a type with no
    /// end condition here, see <see cref="Objective"/>.
    /// ⚠ The zeppelin run wins on the engines first, the hull second, see "The zeppelin run is
    /// won on the ENGINES" in docs/formats/instant-action.md. Do not map it to the hull alone.
    /// </summary>
    public static InstantActionObjective? ObjectiveFor(string missionType) =>
        string.Equals(missionType, "dogfight_ace", StringComparison.OrdinalIgnoreCase)
            ? InstantActionObjective.AceDown
        : string.Equals(missionType, "dogfight_squadron", StringComparison.OrdinalIgnoreCase)
            ? InstantActionObjective.WavesCleared
        : string.Equals(missionType, "stunt_flying", StringComparison.OrdinalIgnoreCase)
            ? InstantActionObjective.ZonesFlown
        : string.Equals(missionType, ZeppelinRunMissionType, StringComparison.OrdinalIgnoreCase)
            ? InstantActionObjective.ZeppelinDisabled
        : (InstantActionObjective?)null;

    /// <summary>The three <c>*_zeppelin</c> node names in <c>zeppelin_type</c> order, 0 cargo,
    /// 1 passenger, 2 military (docs/formats/instant-action.md "Which zeppelin, and which spawn
    /// list"). All eight shipped chapters author the same node three times
    /// (<c>multiplayer1zep</c>), so the list is one distinct name in every real case; a
    /// hand-authored <c>--ia=</c> file may name three.</summary>
    public static IReadOnlyList<string> ZeppelinNodes(InstantActionDef def) =>
        new[] { def.CargoZeppelinNode, def.PassengerZeppelinNode, def.MilitaryZeppelinNode };

    /// <summary><c>zeppelin_type</c>'s own index into <see cref="ZeppelinNodes"/>:
    /// <c>cargo</c> 0, <c>passenger</c> 1, <c>military</c> 2. ⚠ Anything else, including an
    /// unauthored key, is <b>0</b>, not an error: <c>FUN_00458f60</c> maps an unrecognised
    /// string to 3 and REJECTS it rather than storing it, over a record whose reset wrote
    /// <c>+0x254 = 0</c> (<c>FUN_00458ff0</c>, <c>param_1[0x95] = 0</c>), so cargo is the
    /// decoded fallback.</summary>
    public static int ZeppelinTypeIndex(string? zeppelinType) =>
        string.Equals(zeppelinType, "passenger", StringComparison.OrdinalIgnoreCase) ? 1
        : string.Equals(zeppelinType, "military", StringComparison.OrdinalIgnoreCase) ? 2
        : 0;

    /// <summary>The objective zeppelin's world node: the <see cref="ZeppelinNodes"/> entry
    /// <c>zeppelin_type</c> selects. On <c>zeppelin_run</c> this is the node the builder
    /// ACTIVATES and whose generator the wave sequencer credits; on every other mode it is
    /// switched off along with the other two.</summary>
    public static string SelectedZeppelinNode(InstantActionDef def) =>
        ZeppelinNodes(def)[ZeppelinTypeIndex(def.ZeppelinType)];

    /// <summary>The ace's own spawn draw (docs/formats/instant-action.md "The ace and the
    /// waves"): <c>rand() % (count - 1)</c> over the scenario's own spawn list, with the LITERAL
    /// last index substituted, not a re-roll, when that draw collides with the player's own
    /// chosen index. Pure over its inputs (<paramref name="draw"/> is the caller's own
    /// <c>rand()</c> pull, so a <c>--det</c> run stays reproducible without this class touching
    /// an RNG itself). <paramref name="spawns"/> must be non-empty.</summary>
    public static (int Index, SpawnPoint Point) ChooseAceSpawn(
        IReadOnlyList<SpawnPoint> spawns, int playerSpawnIndex, uint draw)
    {
        if (spawns.Count == 1)
            return (0, spawns[0]);
        int idx = (int)(draw % (uint)(spawns.Count - 1));
        if (idx == playerSpawnIndex)
            idx = spawns.Count - 1;
        return (idx, spawns[idx]);
    }

    /// <summary>Collapses an <see cref="AiSkillVector"/> into the one flat 1–9 rating CSVM's own
    /// AI tuning takes: the average of whichever slots are set, rounded and clamped. Every
    /// shipped chapter's <c>ace_stats</c> is a uniform 9, so this reduces to exactly 9 in every
    /// real case (docs/formats/spawns.md). 5 when every slot is unset.</summary>
    public static int RepresentativeRating(AiSkillVector v)
    {
        int sum = 0, n = 0;
        foreach (int? s in new[]
                 {
                     v.DareDevil, v.NaturalTouch, v.SixthSense, v.DeadEye, v.QuickDraw,
                     v.SteadyHand, v.StunRecovery, v.Talker, v.Constitution,
                 })
        {
            if (s is { } val)
            {
                sum += val;
                n++;
            }
        }
        return n > 0 ? System.Math.Clamp((int)System.MathF.Round(sum / (float)n), 1, 9) : 5;
    }

    /// <summary>Puts <see cref="ActorVolumeRadiusM"/> on all three of a spawned Instant Action
    /// pilot's range gates. ⚠ Call after the spawn, and set all three: the spawner's own
    /// airframe-def fallback (docs/formats/ai-rosters.md "The three unnamed slots") must not
    /// survive an Instant Action block, which authors them.
    /// ⚠ Stays the last word even once a patrol net is assigned, the original applies the
    /// roster block after the net too (docs/org/aiPilot.md "Net assignment").</summary>
    public static void ApplyActorVolumes(AiModeMachine? machine)
    {
        if (machine == null)
        {
            return;
        }
        machine.ActivationRange = ActorVolumeRadiusM;
        machine.AttackRange = ActorVolumeRadiusM;
        machine.ReturnRange = ActorVolumeRadiusM;
    }

    /// <summary>The five wingman slots' standing orders, 0-based. <paramref name="i"/> must be
    /// 0–4 (<see cref="InstantActionDef.NumWingmen"/>'s own 0–5 range, minus the clamp a flight of
    /// 6 imposes, see <see cref="FlownWingmen"/>).</summary>
    public static WingmanSlot WingmanSlotFor(int i)
    {
        float metres = 100f * ((i >> 1) + 1);
        float sign = (i & 3) is 1 or 2 ? 1f : -1f;
        int? escorts = i switch { 2 => 1, 4 => 3, _ => null };
        int accentId = new[] { 12, 14, 15, 13, 16 }[i];
        string titleKey = new[] { "MSG_JACK_NAME", "MSG_TEX_NAME", "MSG_BUCK_NAME", "MSG_BJOHN_NAME", "MSG_BETTY_NAME" }[i];
        return new WingmanSlot(metres, sign * 45f, escorts, accentId, titleKey);
    }

    /// <summary>Decision 8a's flight-size cap: the friendly flight is capped at 6 aircraft (1
    /// pilot + 5 wingmen, the data's own maximum), and wingmen are the ones that give,
    /// <c>min(configured, 6 - humans)</c>, never negative. Splitscreen humans add to the flight
    /// rather than consuming the wingman budget (Decision 8); below the cap the configured count
    /// is returned untouched. The clamp rule itself is INVENTED (decision 8a) and its caller must
    /// report it rather than apply it silently.</summary>
    public static int FlownWingmen(int configured, int humans) =>
        System.Math.Max(0, System.Math.Min(configured, 6 - humans));

    /// <summary>One wave member's nine pilot stats, the wave sequencer's own per-aircraft roll
    ///: <c>row = draw % 5</c> over <see cref="PilotPersonalities"/>. Pure over the
    /// caller's own <c>rand()</c> pull, same shape as <see cref="ChooseAceSpawn"/>; feed the
    /// result to <see cref="RepresentativeRating"/> for the one flat rating
    /// <c>AiSpawn.AttackRating</c> takes, CSVM's AI tuning having no
    /// per-stat curves of its own to hang the full vector on.</summary>
    public static AiSkillVector RandomPilotStats(uint draw)
    {
        var row = PilotPersonalities[draw % 5];
        return new AiSkillVector
        {
            DareDevil = row[0],
            NaturalTouch = row[1],
            SixthSense = row[2],
            DeadEye = row[3],
            QuickDraw = row[4],
            SteadyHand = row[5],
            StunRecovery = row[6],
            Talker = row[7],
            Constitution = row[8],
        };
    }

    /// <summary>The wingman accent range's own re-roll (docs/formats/instant-action.md "an
    /// accentID of exactly 12 is re-rolled as 12 + rand() % 5"), applied to a wave member's
    /// <see cref="InstantActionWave.EnemyAccentId"/> at spawn time, never to the ace's or a
    /// wingman's own accent, both of which are already decided elsewhere. Pure over the caller's
    /// own <c>rand()</c> pull; any other accent id (including the built-in -1 default) passes
    /// through unchanged.</summary>
    public static int ResolveWaveAccentId(int accentId, uint draw) =>
        accentId == 12 ? 12 + (int)(draw % 5) : accentId;

    /// <summary>Every accent the mission's actor build can register: the ace's on dogfight_ace,
    /// each configured wingman slot's, and every id a wave's re-roll can reach. The voice prewarm
    /// joins these to the roster's. ⚠ Keep the whole re-roll range, since the draw happens at spawn,
    /// after the sound archive has closed.</summary>
    public static IReadOnlyCollection<int> VoiceAccentIds(InstantActionDef def)
    {
        var accents = new SortedSet<int>();
        if (string.Equals(def.MissionType, "dogfight_ace", StringComparison.OrdinalIgnoreCase))
        {
            accents.Add(def.AceAccentId);
        }
        for (int i = 0; i < Math.Min(def.NumWingmen, 5); i++)
        {
            accents.Add(WingmanSlotFor(i).AccentId);
        }
        foreach (var wave in def.Waves)
        {
            if (wave.NumEnemies <= 0)
            {
                continue;
            }
            for (uint draw = 0; draw < 5; draw++)
            {
                accents.Add(ResolveWaveAccentId(wave.EnemyAccentId, draw));
            }
        }
        accents.RemoveWhere(a => a < 0);
        return accents;
    }

    /// <summary>"Every player has completed their zone set", with lives folded in: a pilot out
    /// of lives can never clear another gate, so this is true once every pilot who can still fly
    /// has finished. False when nobody is left flying, that case is a loss, never a win.</summary>
    public static bool ZoneSetsFlown(IReadOnlyList<(bool OutOfLives, bool Finished)> pilots)
    {
        bool anyFlying = false;
        foreach (var (outOfLives, finished) in pilots)
        {
            if (outOfLives)
            {
                continue;
            }
            if (!finished)
            {
                return false;
            }
            anyFlying = true;
        }
        return anyFlying;
    }

    /// <summary>The wrap-up's "Shot %" row: <c>100 × hits / fired</c>, truncating, the decoded
    /// <c>ftol(100.0 × snapshot+0x22 / snapshot+0x20)</c>. ⚠ Zero rounds fired is a divergence,
    /// deliberately taken: the original's unguarded x87 divide yields a large negative number
    /// there (docs/formats/instant-action.md), which is not behaviour worth copying, so this
    /// reads 0 instead.</summary>
    public static int ShotPercent(int hits, int fired) => fired > 0 ? (int)(100f * hits / fired) : 0;

    /// <summary>One sim step of the mission clock, and of the hold after it. <see cref="Elapsed"/>
    /// stops at the outcome, so the wrap-up's time row is the mission's and never the hold's; the
    /// hold counts down from there and raises <see cref="WrapupDue"/> once, on the step that spends
    /// it.</summary>
    public void Advance(float dt)
    {
        if (!Ended)
        {
            Elapsed += dt;
            return;
        }
        if (_wrapupHold <= 0f)
        {
            return;
        }
        _wrapupHold -= dt;
        if (_wrapupHold <= 0f)
        {
            _wrapupHold = 0f;
            WrapupDue?.Invoke(Outcome);
        }
    }

    /// <summary>Records that this mission's win signal can never arrive; see
    /// <see cref="ObjectiveEnabled"/>.</summary>
    public void DisableObjective() => ObjectiveEnabled = false;

    /// <summary>A signal source reporting what it has just satisfied. The mission is WON only if
    /// this is the objective its own type runs on, every other report is dropped, so one
    /// subscription per source is safe on every mission type.</summary>
    public void ReportObjective(InstantActionObjective objective)
    {
        if (Ended || !ObjectiveEnabled || Objective != objective)
        {
            return;
        }
        End(InstantActionOutcome.Won);
    }

    /// <summary>Puts one human seat on the ledger with a full set of lives. Splitscreen registers
    /// every pane; an unregistered pilot keeps its ordinary respawn behaviour untouched.</summary>
    public void RegisterPilot(int playerIndex) => _lives[playerIndex] = Def.Lives;

    /// <summary>One human death: spends a life and answers whether that pilot flies again. False
    /// means it is out, and the mission is lost once every registered pilot is out. <c>lives 0</c>
    /// is unlimited and always answers true, the disabled-end-condition shape
    /// <see cref="Flight.Modes.VersusMatch"/>'s 0 kill target has; an unregistered pilot answers true too.
    /// ⚠ A death after the mission has ENDED spends nothing and answers false: the result was
    /// settled at the ending, and the hold before the board is not the sortie.</summary>
    public bool NotifyPilotDown(int playerIndex)
    {
        if (Ended)
        {
            return false;
        }
        if (Def.Lives == 0 || !_lives.TryGetValue(playerIndex, out int left))
        {
            return true;
        }
        left = Math.Max(0, left - 1);
        _lives[playerIndex] = left;
        if (left > 0)
        {
            return true;
        }
        _spectators.Add(playerIndex);
        if (_spectators.Count >= _lives.Count)
        {
            End(InstantActionOutcome.Lost);
        }
        return false;
    }

    /// <summary>Lives left for a registered pilot (0 = out and spectating); -1 for an unregistered
    /// one, which is distinct from 0 on purpose.</summary>
    public int LivesLeft(int playerIndex) => _lives.TryGetValue(playerIndex, out int n) ? n : -1;

    public bool IsSpectating(int playerIndex) => _spectators.Contains(playerIndex);

    private void End(InstantActionOutcome outcome)
    {
        if (Ended)
        {
            return;
        }
        Outcome = outcome;
        // Armed before the signal, so a subscriber that asks whether the board is still coming
        // gets the answer the ending already settled.
        _wrapupHold = WrapupHoldS;
        MissionEnded?.Invoke(outcome);
    }

    /// <summary>One wingman's standing order (docs/formats/instant-action.md "The player and the
    /// wingmen"): its fan placement off the player's spawn heading,
    /// <c>100 · ((i &gt;&gt; 1) + 1)</c> metres out, at ±45°, the same 100 m/45° pattern the wave
    /// sequencer uses, its <see cref="PrimaryTargetIsWingman"/> escort chain (0, 1 and 3 escort
    /// the player; 2 and 4 escort wingmen 1 and 3), its authored accent id, and the <c>MSG_*</c> key
    /// of the pilot name its marker prints (the builder's hardcoded string ids, one per slot).</summary>
    public readonly record struct WingmanSlot(float MetresOut, float OffsetDeg, int? PrimaryTargetIsWingman,
        int AccentId, string TitleKey);
}
