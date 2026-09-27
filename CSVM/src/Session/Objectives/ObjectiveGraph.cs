using System;
using System.Collections.Generic;

namespace CSVM.Session.Objectives;

/// <summary>The four states one objective moves through
/// (docs/formats/objectives.md, "Conceptual model").</summary>
public enum ObjectiveState
{
    /// <summary>Waiting for its <c>BEGIN_DORMANT</c> delay or for another objective to wake it.</summary>
    Dormant = 0,

    /// <summary>Its completion conditions are being evaluated.</summary>
    Awake = 1,

    /// <summary>Asleep on a timer; wakes itself when the timer runs out.</summary>
    Napping = 2,

    /// <summary>Completed, slept permanently, or expired; only an explicit wake revives it.</summary>
    Retired = 3,
}

/// <summary>How a mission ended, or that it has not.</summary>
public enum MissionOutcome
{
    /// <summary>Still flying.</summary>
    None = 0,

    /// <summary>The graph set the won flag.</summary>
    Won = 1,

    /// <summary>The graph set the lost flag.</summary>
    Lost = 2,
}

/// <summary>What one objective just did to the four-state machine.</summary>
public enum ObjectiveTransitionKind
{
    /// <summary>Entered <see cref="ObjectiveState.Awake"/>: its dormancy ran out, its nap ran
    /// out, or another objective's wake list named it.</summary>
    Woke,

    /// <summary>Entered <see cref="ObjectiveState.Napping"/> on a timer, completed flag cleared.</summary>
    Napped,

    /// <summary>Its conditions read true and its completion actions ran.</summary>
    Completed,

    /// <summary>Killed by another objective's completion; it never ticks again.</summary>
    Killed,

    /// <summary>Slept permanently by another objective's completion.</summary>
    Slept,

    /// <summary>Retired by its own deadline.</summary>
    Expired,

    /// <summary>Marked complete and retired by another objective's <c>HIDE_OBJ</c>, running none
    /// of its own completion actions.</summary>
    Hidden,
}

/// <summary>
/// The seam the graph reaches the world through: the conditions it cannot answer itself, and the
/// wake/completion actions that touch world objects. A method returning <c>null</c> means "this
/// engine cannot answer", which makes the condition family report false rather than guess; the
/// graph counts those in <see cref="ObjectiveGraph.UnresolvedConditions"/>.
/// ⚠ Two meanings live behind the word "player" here, and an implementer has to keep them apart:
/// the SCRIPTED PLAYER is the one aeroplane an authored <c>player</c> token names, and the HUMAN
/// FIELD is every human flying the mission. Every condition below reads the field
/// (<see cref="Campaign.CampaignHumanField"/>); the escort leader and a net's trailer target read the
/// scripted player, and neither of those reaches the graph at all.
/// </summary>
public interface IObjectiveWorld
{
    /// <summary>Whether the node at a path (node, then named children) has lost its active bit.
    /// Null when the path resolves to nothing.</summary>
    bool? NodeInactive(IReadOnlyList<string> path);

    /// <summary>The animation's current runtime state (2 RUNNING, 3 EXECUTED, 4 INVALID, 0
    /// none).</summary>
    int AnimState(string anim);

    /// <summary>Live vehicles in an AI group, plus the named generator's remaining capacity when
    /// <c>DEDG</c> authors one, plus every human the group's capture stamped. Null when the session
    /// has no AI-group roster at all.</summary>
    int? GroupLiveCount(int group, string? generator);

    /// <summary>`DEDG`'s side effect: raise every live member of the group to the widened
    /// engagement volume, so a watched group never disengages by distance. Raise only, so a napped
    /// or wiped-out objective leaves the volume where it put it.</summary>
    void WidenGroupEngagement(int group);

    /// <summary>Whether a <c>TRAVELERS</c> proximity condition reads true this tick. An authored
    /// <c>player</c> subject is the human field, nearest human first. Null when the subject or the
    /// reference cannot be resolved.</summary>
    bool? TravelersMet(TravelersSpec spec);

    /// <summary>`WAKEUP_ENEMIES`: reactivate each named vehicle or zeppelin that is
    /// deactivated.</summary>
    void WakeupEnemies(IReadOnlyList<string> names);

    /// <summary>`WAKEUP_TURRETS`: arm every turret matching each pattern.</summary>
    void WakeupTurrets(IReadOnlyList<string> patterns);

    /// <summary>`WAKEUP_ZEP_TURRETS`: arm every turret in each named node's subtree.</summary>
    void WakeupZepTurrets(IReadOnlyList<string> nodes);

    /// <summary>`WAKEUP_GENERATOR`: add launch capacity to the named generator.</summary>
    void WakeupGenerator(string name, int count);

    /// <summary>`WAKE_ANIM`: start the animation, optionally at a node.</summary>
    void WakeAnim(string anim, string? node);

    /// <summary>Plays a sound group by name (a wake group, a completion group, or a class
    /// complete sound).</summary>
    void PlaySoundGroup(string group);

    /// <summary>`STOP_QUEUED_SOUNDS`: drop the named sounds from the radio queue if they have not
    /// started.</summary>
    void StopQueuedSounds(IReadOnlyList<string> names);

    /// <summary>`WARP_VEHICLE`: teleport the vehicle to one waypoint drawn from the list.</summary>
    void WarpVehicle(string vehicle, IReadOnlyList<WarpPoint> points);

    /// <summary>`SET_AI_TEAM`.</summary>
    void SetAiTeam(IReadOnlyList<(string Name, int Team)> entries);

    /// <summary>`SET_AI_NET`.</summary>
    void SetAiNet(IReadOnlyList<(string Name, string Net)> entries);

    /// <summary>`SET_AI_ATTACK_RADIUS`.</summary>
    void SetAiAttackRadius(IReadOnlyList<(string Name, float Radius)> entries);

    /// <summary>`COMPLETED_ZEPCANNONS`: write the flag to the zeppelin record.</summary>
    void CompletedZepcannons(IReadOnlyList<(string Zeppelin, int Flag)> entries);

    /// <summary>`COMPLETED_STOPPOINT`: mark a patrol net's stop point completed.</summary>
    void CompletedStoppoint(IReadOnlyList<(string Net, int Stop, int Flag)> entries);

    /// <summary>`START_TAXI`: release the named vehicles onto their taxi paths.</summary>
    void StartTaxi(IReadOnlyList<string> names);
}

/// <summary>An objective waking, with the sound group its <c>WAKEUP_SOUND_GROUP</c> names (null
/// when it authors none) and its <c>IDENTITY</c> if it has one. D33 owns the cue and the display;
/// D37 reads the group name for the music state.</summary>
public readonly record struct ObjectiveWoke(
    int Number, string? SoundGroup, ObjectiveIdentity? Identity);

/// <summary>An objective completing: its own <c>COMPLETED_SOUND_GROUP</c>, the file-level
/// per-class complete sound its <c>IDENTITY</c> class pays, and that identity.</summary>
public readonly record struct ObjectiveCompleted(
    int Number, string? SoundGroup, string? ClassSoundGroup, ObjectiveIdentity? Identity);

/// <summary>One state transition, for the log: which objective, what it did, the objective whose
/// completion caused it (0 for the graph's own timers and an outside wake), the mission time, and
/// the nap length for <see cref="ObjectiveTransitionKind.Napped"/>. <c>Gated</c> says the
/// objective carries a <c>TICK_DEPENDS_ON_OBJ</c> whose dependency is not awake right now, so a
/// nap it just entered is held rather than counting.</summary>
public readonly record struct ObjectiveTransition(
    int Number, ObjectiveTransitionKind Kind, int Source, float Elapsed, float Seconds, bool Gated);

/// <summary>A win or loss the graph has just decided, before its wrap-up runs down.
/// <c>ObjectivesSound</c> says the objectives-won or objectives-lost group played ahead of the
/// mission sound. The aggregate ending and the docking play it; an instant ending does not.
/// </summary>
public readonly record struct MissionEnding(MissionOutcome Outcome, bool ObjectivesSound);

/// <summary>One row of the player-visible objectives display: unique <c>priority</c> is the row
/// key and the sort key, the message key is its label, and the row is marked when an objective of
/// that priority completes.</summary>
public readonly record struct ObjectiveRow(
    int Priority, ObjectiveClass Class, string? MessageKey, bool Awake, bool Completed);

/// <summary>
/// One mission's objectives runtime: the four-state machine per objective, the rotating
/// one-completion-per-tick scan, the chaining executor with its already-awake truncation, the
/// condition families' OR with <c>DEDG</c>'s engagement widening, the mission countdown, and the
/// win/loss flags. Engine-free by
/// construction (no Godot type, no logging), so <c>CSVM.Tests/ObjectiveGraphTests.cs</c> pins it
/// off-engine and <see cref="Campaign.CampaignDirector"/> owns every log line about it. Every rule here is
/// decoded in docs/formats/objectives.md and implemented as decoded, shipped quirks included.
/// </summary>
public sealed class ObjectiveGraph
{
    // The wrap-up the ordinary win takes before the debrief.
    private const float WonWrapUpS = 3f;

    // The docking's completion code takes none of it. The code's own case sets the won flag and
    // calls the mission-end path in the same breath, never touching the wrap-up timer the objective
    // endings run down, so the mission ends on the frame the film's last sequence raises it. What
    // comes after that ending is the same for every ending, and is CampaignDirector's hold.
    private const float DockingWrapUpS = 0f;

    private readonly ObjectiveScript _script;
    private readonly IObjectiveWorld _world;
    private readonly List<Live> _live = new();
    private readonly List<ObjectiveRow> _rows = new();
    private readonly HashSet<string> _objectiveTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _otherTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _helpLabels = new(StringComparer.OrdinalIgnoreCase);
    private int _scan;
    private int _source;
    private float _late;
    private MissionOutcome _pending;
    private float _wrapUp;
    private bool _playerLost;

    /// <summary>Builds the runtime over a parsed script. Every objective starts in the state its
    /// <c>BEGIN_DORMANT</c> asks for, and the display rows are built once, one per unique
    /// <c>IDENTITY</c> priority in ascending order.</summary>
    public ObjectiveGraph(ObjectiveScript script, IObjectiveWorld world)
    {
        _script = script;
        _world = world;
        foreach (var def in script.Objectives)
        {
            _live.Add(new Live(def));
        }

        TimerRemaining = script.MissionTimer;
        BuildRows();
    }

    /// <summary>Fired as an objective wakes, after its wake actions have run.</summary>
    public event Action<ObjectiveWoke>? Woke;

    /// <summary>Fired as an objective completes, after its completion actions and chaining.</summary>
    public event Action<ObjectiveCompleted>? Completed;

    /// <summary>Fired on every state transition, wake and completion included, in the order they
    /// happen. The engine side turns these into the sortie log's objective lines; the graph itself
    /// logs nothing.</summary>
    public event Action<ObjectiveTransition>? Transitioned;

    /// <summary>Fired once, when the wrap-up delay after a win or loss has run out.</summary>
    public event Action<MissionOutcome>? MissionEnded;

    /// <summary>Fired as the mission clock runs out on a mission no other ending has claimed, at
    /// the head of the tick and ahead of the wrap-up. The HUD's expiry notice hangs off it; a
    /// <c>NOLOSS</c> clock pins at zero and raises nothing.</summary>
    public event Action? TimerExpired;

    /// <summary>Fired whenever a target-list edit or a help-label write changes the display
    /// state.</summary>
    public event Action? TargetsChanged;

    /// <summary>Fired once, as a win or loss is decided and its sounds have played, ahead of the
    /// wrap-up that <see cref="MissionEnded"/> closes.</summary>
    public event Action<MissionEnding>? EndingDecided;

    /// <summary>Whether this graph mirrors another machine's instead of running its own rules. A
    /// replicated graph evaluates no condition, runs no timer to a transition and decides no
    /// ending. It changes only through the <c>Apply</c> calls. Set once, by
    /// <see cref="Replicate"/>.</summary>
    public bool Replicated { get; private set; }

    /// <summary>Mission time in seconds since the graph started ticking.</summary>
    public float Elapsed { get; private set; }

    /// <summary>The mission countdown's remaining seconds. Idle until something starts it.</summary>
    public float TimerRemaining { get; private set; }

    /// <summary>Whether the mission countdown is running.</summary>
    public bool TimerRunning { get; private set; }

    /// <summary>The outcome, once the wrap-up has run out; <see cref="MissionOutcome.None"/> until
    /// then, even while a wrap-up is counting down.</summary>
    public MissionOutcome Outcome { get; private set; }

    /// <summary>Whether the mission has ended.</summary>
    public bool Ended => Outcome != MissionOutcome.None;

    /// <summary>Whether a win or loss has been decided and only the wrap-up delay remains.</summary>
    public bool Ending => _pending != MissionOutcome.None && Outcome == MissionOutcome.None;

    /// <summary>How many objectives the script carries.</summary>
    public int Count => _live.Count;

    /// <summary>How many condition tests this session could not answer, each counted once per
    /// evaluation. A non-zero count means the graph is running against a world that cannot supply
    /// something the mission asks about, and is what a suite asserts against.</summary>
    public int UnresolvedConditions { get; private set; }

    /// <summary>The player-visible objectives display, D33's read model: one row per unique
    /// <c>IDENTITY</c> priority, in ascending priority order.</summary>
    public IReadOnlyList<ObjectiveRow> Rows => _rows;

    /// <summary>The targets currently flagged as objective targets (`ADD_OBJECTIVE_TARGET`), by
    /// <see cref="Flight.Weapons.ObjectiveTarget.Key"/>: a bare name, or <c>parent/child</c> for an authored
    /// path.</summary>
    public IReadOnlyCollection<string> ObjectiveTargets => _objectiveTargets;

    /// <summary>The targets currently flagged as other targets (`ADD_OTHER_TARGET`), by key.</summary>
    public IReadOnlyCollection<string> OtherTargets => _otherTargets;

    /// <summary>The help labels `SET_HELP_LABEL` has written, target key to message key.</summary>
    public IReadOnlyDictionary<string, string> HelpLabels => _helpLabels;

    /// <summary>The completed-objective bitmask a mission attempt records, objective bits only:
    /// a completed display row sets the bit its own <c>IDENTITY</c> priority names, NOT the row's
    /// index. ⚠ Bit 0 is the mission-won flag and belongs to the caller, which is why a priority of
    /// 0 is skipped here exactly as the original skips it
    /// (docs/formats/saved-games.md, "The mission-result array").</summary>
    public int CompletedMask
    {
        get
        {
            int mask = 0;
            foreach (var row in _rows)
            {
                if (row.Completed && row.Priority > 0 && row.Priority < 32)
                {
                    mask |= 1 << row.Priority;
                }
            }

            return mask;
        }
    }

    /// <summary>Whether a target key carries the objective-target display flag right now.</summary>
    public bool IsObjectiveTarget(string key) => _objectiveTargets.Contains(key);

    /// <summary>Whether a target key carries the other-target display flag right now.</summary>
    public bool IsOtherTarget(string key) => _otherTargets.Contains(key);

    /// <summary>One objective's current state, by 1-based number.</summary>
    public ObjectiveState StateOf(int number) =>
        number >= 1 && number <= _live.Count ? _live[number - 1].State : ObjectiveState.Retired;

    /// <summary>Whether an objective is still alive (a `KILL_OBJECTIVE_WHEN_I_COMPLETE` target is
    /// not).</summary>
    public bool AliveOf(int number) => number >= 1 && number <= _live.Count && _live[number - 1].Alive;

    /// <summary>Whether an objective's completed flag is set. `NAP_OBJECTIVE_WHEN_I_COMPLETE` is
    /// the only path that clears it.</summary>
    public bool CompletedOf(int number) =>
        number >= 1 && number <= _live.Count && _live[number - 1].Complete;

    /// <summary>An objective's private timer, the seconds since it last woke or napped.</summary>
    public float TimerOf(int number) => Target(number)?.Timer ?? 0f;

    /// <summary>The seconds left of an objective's nap.</summary>
    public float NapRemainingOf(int number) => Target(number)?.NapRemaining ?? 0f;

    /// <summary>The player's own aircraft is lost: the fourth ending, which stops this runtime dead
    /// rather than setting a flag (docs/formats/objectives.md, "Win and loss"). Nothing advances
    /// afterwards, so no sound, no completion and no countdown belongs to it. Answers whether this
    /// call is the one that closed the gate. ⚠ Take the aircraft's own crash report, never an
    /// altitude: the under-map backstop teleports without one (docs/verification.md INSTR-22).</summary>
    public bool NotifyPlayerLost()
    {
        if (_playerLost || Ended || Replicated)
        {
            return false;
        }

        _playerLost = true;
        return true;
    }

    /// <summary>The lost player's wreck is down, which is where the original reaches its debrief.
    /// The outcome is the won flag alone, so a mission already won when the player died is still
    /// won; anything else is a loss. Answers whether this call ended the mission.</summary>
    public bool EndAfterPlayerLost()
    {
        if (!_playerLost || Ended)
        {
            return false;
        }

        Outcome = _pending == MissionOutcome.Won ? MissionOutcome.Won : MissionOutcome.Lost;
        _pending = Outcome;
        MissionEnded?.Invoke(Outcome);
        return true;
    }

    /// <summary>The docking's own completion code, raised by the animation rather than by an
    /// objective: the original answers it by setting the won flag and running the mission-end path
    /// at once, with no wrap-up. It beats the <c>ANIM_STATE ... EXECUTED</c> objective watching the
    /// same definition, which cannot read EXECUTED until the definition that raised the code has
    /// ended (docs/formats/objectives.md, "Win and loss"). Answers whether this call ended the
    /// mission.</summary>
    public bool NotifyDockingComplete()
    {
        if (Ended || Ending || Replicated)
        {
            return false;
        }

        End(MissionOutcome.Won, DockingWrapUpS, objectivesSound: true);
        // ⚠ Landed here, never left to the next step: the world under the film that raised the code
        // is held, so a graph waiting for a step of its own would not get one until the film ended
        // and the view had cut back to the pilot (docs/formats/objectives.md).
        StepWrapUp(0f);
        return true;
    }

    /// <summary>The player completed a danger zone. The zone module walks every AWAKE objective and
    /// flags matching names; a zone completed while an objective is dormant or napping does not
    /// count for it.</summary>
    public void NotifyDangerZoneCompleted(string zone)
    {
        if (Replicated)
        {
            return;
        }

        foreach (var live in _live)
        {
            if (live.Alive && live.State == ObjectiveState.Awake && live.Def.DangerZones.Count > 0)
            {
                foreach (var name in live.Def.DangerZones)
                {
                    if (string.Equals(name, zone, StringComparison.OrdinalIgnoreCase))
                    {
                        live.Zones.Add(name);
                    }
                }
            }
        }
    }

    /// <summary>Advances the mission by one step: the countdown, every objective's own timers, and
    /// the rotating completion scan that completes at most one objective. ⚠ A lost player stops all
    /// of it, countdown and pending wrap-up included, until
    /// <see cref="EndAfterPlayerLost"/>.</summary>
    public void Step(float dt)
    {
        if (_playerLost)
        {
            return;
        }

        if (Replicated)
        {
            StepReplicated(dt);
            return;
        }

        if (dt <= 0f || _live.Count == 0)
        {
            StepWrapUp(dt);
            return;
        }

        Elapsed += dt;
        StepTimer(dt);
        for (int i = 0; i < _live.Count; i++)
        {
            StepObjective(_live[i], dt);
        }

        ScanForCompletion();
        CheckAggregateEnd();
        StepWrapUp(dt);
    }

    /// <summary>Wakes an objective from outside the graph, the way one objective's chain does. The
    /// cutscene and mission-start paths use it; the already-awake truncation does not apply to a
    /// single call.</summary>
    public void Wake(int number)
    {
        if (!Replicated)
        {
            WakeOne(number);
        }
    }

    /// <summary>Hands this graph over to another machine's. From here it changes only through the
    /// <c>Apply</c> calls. Each replays one of that graph's events with the same state change and
    /// the same world actions. ⚠ Nothing it replays is decided here, so a replicated
    /// graph must never evaluate a condition or run a timer to a transition. Two machines would
    /// then disagree about which objective completed first.</summary>
    public void Replicate() => Replicated = true;

    /// <summary>Replays one transition the owning graph made. The objective's state and its world
    /// actions are the owner's; <see cref="ObjectiveTransition.Elapsed"/> and the nap length are
    /// this graph's own reading. A completion's row and class sound wait for
    /// <see cref="ApplySettled"/>, where the owner plays them. <paramref name="late"/> is how long
    /// ago the owner made it. The timer, nap and countdown it sets start that far along.</summary>
    public void ApplyTransition(ObjectiveTransitionKind kind, int number, int source, float late = 0f)
    {
        if (!Replicated || Target(number) is not { } live)
        {
            return;
        }

        _source = source;
        _late = float.IsFinite(late) ? MathF.Max(0f, late) : 0f;
        switch (kind)
        {
            case ObjectiveTransitionKind.Woke:
                WakeLive(live);
                live.Timer = _late;
                break;
            case ObjectiveTransitionKind.Completed:
                live.Complete = true;
                live.State = ObjectiveState.Retired;
                live.Zones.Clear();
                Note(live, kind, 0f);
                RunCompletionActions(live.Def);
                break;
            case ObjectiveTransitionKind.Napped:
                live.State = ObjectiveState.Napping;
                live.NapRemaining = NapLengthOf(live, source);
                live.Complete = false;
                Note(live, kind, live.NapRemaining);
                live.NapRemaining = MathF.Max(0f, live.NapRemaining - _late);
                live.Timer = _late;
                break;
            case ObjectiveTransitionKind.Killed:
                live.Alive = false;
                live.State = ObjectiveState.Retired;
                Note(live, kind, 0f);
                break;
            case ObjectiveTransitionKind.Slept:
                live.State = ObjectiveState.Retired;
                Note(live, kind, 0f);
                if (live.Def.SleepAnim is { } anim)
                {
                    _world.WakeAnim(anim, null);
                }

                break;
            case ObjectiveTransitionKind.Expired:
                live.State = ObjectiveState.Retired;
                Note(live, kind, 0f);
                break;
            case ObjectiveTransitionKind.Hidden:
                live.Complete = true;
                live.State = ObjectiveState.Retired;
                Note(live, kind, 0f);
                break;
            default:
                break;
        }

        _source = 0;
        _late = 0f;
    }

    /// <summary>Replays the close of a completion the owning graph made: the display row, the
    /// class complete sound and the <see cref="Completed"/> event, after everything its chain
    /// did.</summary>
    public void ApplySettled(int number)
    {
        if (!Replicated || Target(number) is not { } live)
        {
            return;
        }

        var def = live.Def;
        string? classSound = MarkRow(def);
        Completed?.Invoke(new ObjectiveCompleted(
            def.Number, def.CompletedSoundGroup, classSound, def.Identity));
    }

    /// <summary>Replays the owning graph's countdown running out: the notice, and the clock
    /// stopped at zero.</summary>
    public void ApplyTimerExpired()
    {
        if (!Replicated)
        {
            return;
        }

        TimerRemaining = 0f;
        TimerRunning = false;
        TimerExpired?.Invoke();
    }

    /// <summary>Replays a win or loss the owning graph decided, with the sounds it played. The
    /// wrap-up is the owner's, so this graph waits for <see cref="ApplyEnded"/>.</summary>
    public void ApplyEnding(MissionEnding ending)
    {
        if (Replicated)
        {
            End(ending.Outcome, float.PositiveInfinity, ending.ObjectivesSound);
        }
    }

    /// <summary>Replays the owning graph's mission end, the moment its wrap-up ran out.</summary>
    public void ApplyEnded(MissionOutcome outcome)
    {
        if (!Replicated || Ended || outcome == MissionOutcome.None)
        {
            return;
        }

        _pending = outcome;
        Outcome = outcome;
        MissionEnded?.Invoke(outcome);
    }

    private static bool CountMet(int matched, int? authored, int entries) =>
        matched >= (authored ?? entries) && entries > 0;

    // A replicated graph's own step: the mission time, the countdown, the private timers and the
    // naps, which are readings of the clock. Each pins at zero, since what running out does is the
    // owner's to decide and arrives as its own event.
    private void StepReplicated(float dt)
    {
        if (dt <= 0f || Ended)
        {
            return;
        }

        Elapsed += dt;
        if (TimerRunning)
        {
            TimerRemaining = MathF.Max(0f, TimerRemaining - dt);
        }

        foreach (var live in _live)
        {
            if (!live.Alive || !Ticks(live))
                continue;
            live.Timer += dt;
            if (live.State == ObjectiveState.Napping)
                live.NapRemaining = MathF.Max(0f, live.NapRemaining - dt);
        }
    }

    // The nap a replayed Napped entered, which the wire does not carry: a completion's
    // NAP_OBJECTIVE_WHEN_I_COMPLETE when a source names one, the objective's own nap otherwise.
    private float NapLengthOf(Live live, int source) =>
        Target(source) is { Def.NapWhenComplete: { } nap } && nap.Target == live.Def.Number
            ? nap.Seconds
            : live.Def.NapDuration ?? 0f;

    private void BuildRows()
    {
        foreach (var identity in _script.DisplayIdentities())
        {
            _rows.Add(new ObjectiveRow(identity.Priority, identity.Class, identity.MessageKey, false, false));
        }
    }

    private void StepTimer(float dt)
    {
        if (!TimerRunning)
        {
            return;
        }

        TimerRemaining -= dt;
        if (TimerRemaining > 0f)
        {
            return;
        }

        TimerRemaining = 0f;
        if (_script.TimerNoLoss)
        {
            return;
        }

        TimerRunning = false;
        // Only on a mission no other ending has claimed, which is the guard the notice and End
        // share; raised first, so the notice is up while the wrap-up runs.
        if (_pending == MissionOutcome.None)
        {
            TimerExpired?.Invoke();
        }

        // The countdown expiring is the mission's third ending, with the ordinary 3 s wrap-up. Which
        // flag it sets is untraced; CSVM ends it lost, since a run whose primary is unfinished
        // records nothing either way.
        End(MissionOutcome.Lost, 3f);
    }

    private void StepObjective(Live live, float dt)
    {
        if (!live.Alive || !Ticks(live))
        {
            return;
        }

        live.Timer += dt;
        switch (live.State)
        {
            case ObjectiveState.Dormant:
                // -1 never wakes on its own. An objective with no BEGIN_DORMANT never reaches this
                // state at all: it STARTS awake, which is not a wake, so its wake actions never run.
                if (live.Def.DormantUntil >= 0f && Elapsed >= live.Def.DormantUntil)
                {
                    WakeLive(live);
                }

                break;
            case ObjectiveState.Napping:
                live.NapRemaining -= dt;
                if (live.NapRemaining <= 0f)
                {
                    WakeLive(live);
                }

                break;
            case ObjectiveState.Awake:
                StepAwake(live);
                break;
            default:
                break;
        }
    }

    private void StepAwake(Live live)
    {
        if (live.Def.Deadline is { } deadline && Elapsed >= deadline)
        {
            live.State = ObjectiveState.Retired;
            Note(live, ObjectiveTransitionKind.Expired, 0f);
            RunWakeList(live.Def.WakeWhenSleep);
            return;
        }

        if (live.Def.AwakeDuration is { } awake && live.Timer >= awake)
        {
            live.State = ObjectiveState.Napping;
            live.NapRemaining = live.Def.NapDuration ?? 0f;
            Note(live, ObjectiveTransitionKind.Napped, live.NapRemaining);
            RunWakeList(live.Def.WakeWhenSleep);
        }
    }

    private void Note(Live live, ObjectiveTransitionKind kind, float seconds) =>
        Transitioned?.Invoke(new ObjectiveTransition(
            live.Def.Number, kind, _source, Elapsed, seconds, !Ticks(live)));

    // TICK_DEPENDS_ON_OBJ gates the WHOLE objective: it is only ticked, tested or completed while
    // the dependency is AWAKE, not merely alive and not merely complete.
    private bool Ticks(Live live)
    {
        int dep = live.Def.TickDependsOn;
        return dep <= 0 || dep > _live.Count || _live[dep - 1].State == ObjectiveState.Awake;
    }

    private void ScanForCompletion()
    {
        int n = _live.Count;
        for (int i = 0; i < n; i++)
        {
            var live = _live[(_scan + i) % n];
            if (!live.Alive || live.State != ObjectiveState.Awake || live.Complete || !Ticks(live))
            {
                continue;
            }

            if (ConditionsMet(live))
            {
                Complete(live);
                break;
            }
        }

        // The scan index advances every tick, completion or not, so simultaneous completions
        // resolve over consecutive frames in rotating order.
        _scan = (_scan + 1) % n;
    }

    private bool ConditionsMet(Live live)
    {
        var def = live.Def;
        if (!def.HasConditions)
        {
            return true;
        }

        return InactiveMet(def) || AnimStateMet(def) || ZonesMet(live) || DedgMet(def) || TravelersMet(def);
    }

    private bool InactiveMet(ObjectiveDef def)
    {
        if (def.Inactive.Count == 0)
        {
            return false;
        }

        int matched = 0;
        foreach (var path in def.Inactive)
        {
            bool? inactive = _world.NodeInactive(path);
            if (inactive == null)
            {
                UnresolvedConditions++;
            }
            else if (inactive.Value)
            {
                matched++;
            }
        }

        return CountMet(matched, def.InactiveCount, def.Inactive.Count);
    }

    private bool AnimStateMet(ObjectiveDef def)
    {
        if (def.AnimStates.Count == 0)
        {
            return false;
        }

        int matched = 0;
        foreach (var entry in def.AnimStates)
        {
            if (_world.AnimState(entry.Name) == entry.State)
            {
                matched++;
            }
        }

        return CountMet(matched, def.AnimStateCount, def.AnimStates.Count);
    }

    private bool ZonesMet(Live live)
    {
        var def = live.Def;
        return def.DangerZones.Count != 0
            && CountMet(live.Zones.Count, def.DangerZoneCount, def.DangerZones.Count);
    }

    private bool DedgMet(ObjectiveDef def)
    {
        if (def.Dedg is not { } dedg)
        {
            return false;
        }

        // ⚠ Keep the widening on the same tick as the count, and only here: the original's walk
        // (FUN_004658d0) counts and widens in one pass, and a positive group is its own guard, so a
        // group-0 clause reaches no member at all. Nothing narrows the volume back.
        if (dedg.Group > 0)
        {
            _world.WidenGroupEngagement(dedg.Group);
        }

        int? live = _world.GroupLiveCount(dedg.Group, dedg.Generator);
        if (live == null)
        {
            UnresolvedConditions++;
            return false;
        }

        return live.Value <= dedg.Max;
    }

    private bool TravelersMet(ObjectiveDef def)
    {
        if (def.Travelers is not { } spec)
        {
            return false;
        }

        bool? met = _world.TravelersMet(spec);
        if (met == null)
        {
            UnresolvedConditions++;
            return false;
        }

        return met.Value;
    }

    private void Complete(Live live)
    {
        var def = live.Def;
        live.Complete = true;
        live.State = ObjectiveState.Retired;
        live.Zones.Clear();
        Note(live, ObjectiveTransitionKind.Completed, 0f);
        RunCompletionActions(def);
        _source = def.Number;
        RunChaining(def);
        _source = 0;
        string? classSound = MarkRow(def);
        Completed?.Invoke(new ObjectiveCompleted(
            def.Number, def.CompletedSoundGroup, classSound, def.Identity));
        if (def.InstantWin)
        {
            End(MissionOutcome.Won, 0.1f);
        }
        else if (def.InstantLoss)
        {
            End(MissionOutcome.Lost, 0.1f);
        }
    }

    private void RunCompletionActions(ObjectiveDef def)
    {
        if (def.CompletedSoundGroup is { } group)
        {
            _world.PlaySoundGroup(group);
        }

        if (def.Warp is { } warp)
        {
            _world.WarpVehicle(warp.Vehicle, warp.Points);
        }

        _world.SetAiTeam(def.SetAiTeam);
        _world.SetAiNet(def.SetAiNet);
        _world.SetAiAttackRadius(def.SetAiAttackRadius);
        _world.CompletedZepcannons(def.CompletedZepcannons);
        _world.CompletedStoppoint(def.CompletedStoppoint);
        EditTargets(def);
        _world.StartTaxi(def.StartTaxi);
        if (def.HelpLabel is { } label)
        {
            foreach (var target in label.Names)
            {
                _helpLabels[target.Key] = label.MessageKey;
            }

            TargetsChanged?.Invoke();
        }

        _world.StopQueuedSounds(def.StopQueuedSounds);
        RunTimerActions(def);
    }

    private void EditTargets(ObjectiveDef def)
    {
        int before = _objectiveTargets.Count + _otherTargets.Count;
        foreach (var target in def.AddOtherTarget)
        {
            _otherTargets.Add(target.Key);
        }

        foreach (var target in def.RemoveOtherTarget)
        {
            _otherTargets.Remove(target.Key);
        }

        foreach (var target in def.AddObjectiveTarget)
        {
            _objectiveTargets.Add(target.Key);
        }

        foreach (var target in def.RemoveObjectiveTarget)
        {
            _objectiveTargets.Remove(target.Key);
        }

        if (before != _objectiveTargets.Count + _otherTargets.Count)
        {
            TargetsChanged?.Invoke();
        }
    }

    private void RunTimerActions(ObjectiveDef def)
    {
        if (def.AdjustTimer is { } adjust)
        {
            // A replayed SET started late is that far down; a relative adjust moves a countdown
            // that was already running here.
            TimerRemaining = string.Equals(adjust.Op, "SET", StringComparison.OrdinalIgnoreCase)
                ? MathF.Max(0f, adjust.Seconds - _late)
                : TimerRemaining + adjust.Seconds;
        }

        if (def.EndTimer)
        {
            TimerRunning = false;
        }
    }

    private void RunChaining(ObjectiveDef def)
    {
        RunWakeList(def.WakeWhenComplete);
        foreach (int target in def.KillWhenComplete)
        {
            if (Target(target) is { } killed)
            {
                killed.Alive = false;
                killed.State = ObjectiveState.Retired;
                Note(killed, ObjectiveTransitionKind.Killed, 0f);
            }
        }

        if (def.NapWhenComplete is { } nap && Target(nap.Target) is { Alive: true } napped)
        {
            napped.State = ObjectiveState.Napping;
            napped.NapRemaining = nap.Seconds;
            napped.Timer = 0f;
            // The only path that clears another objective's completed flag, and with it the only
            // way a completed objective ever runs again.
            napped.Complete = false;
            Note(napped, ObjectiveTransitionKind.Napped, nap.Seconds);
        }

        foreach (int target in def.SleepWhenComplete)
        {
            if (Target(target) is { Alive: true } slept)
            {
                slept.State = ObjectiveState.Retired;
                Note(slept, ObjectiveTransitionKind.Slept, 0f);
                if (slept.Def.SleepAnim is { } anim)
                {
                    _world.WakeAnim(anim, null);
                }
            }
        }

        if (def.HideObj is { } hidden && Target(hidden) is { } hide)
        {
            hide.Complete = true;
            hide.State = ObjectiveState.Retired;
            Note(hide, ObjectiveTransitionKind.Hidden, 0f);
        }
    }

    // ⚠ The shipped executor's early return: a target that is already awake has its private timer
    // reset and TRUNCATES the rest of the list. No shipped chain is written to hit it, and a
    // runtime that quietly kept going would be diverging.
    private void RunWakeList(List<int> targets)
    {
        foreach (int target in targets)
        {
            if (Target(target) is not { Alive: true, Complete: false } live)
            {
                continue;
            }

            if (live.State == ObjectiveState.Awake)
            {
                live.Timer = 0f;
                return;
            }

            WakeLive(live);
        }
    }

    private void WakeOne(int number)
    {
        if (Target(number) is { Alive: true, Complete: false } live && live.State != ObjectiveState.Awake)
        {
            WakeLive(live);
        }
    }

    private void WakeLive(Live live)
    {
        var def = live.Def;
        bool fromDormant = live.State == ObjectiveState.Dormant;
        live.State = ObjectiveState.Awake;
        live.Timer = 0f;
        live.Zones.Clear();
        _world.WakeupEnemies(def.WakeupEnemies);
        _world.WakeupTurrets(def.WakeupTurrets);
        _world.WakeupZepTurrets(def.WakeupZepTurrets);
        if (def.WakeupGenerator is { } generator)
        {
            _world.WakeupGenerator(generator.Name, generator.Count);
        }

        if (def.WakeAnim is { } anim)
        {
            _world.WakeAnim(anim.Anim, anim.Node);
        }

        if (def.WakeSoundGroup is { } group)
        {
            _world.PlaySoundGroup(group);
        }

        if (fromDormant && def.ResetTimer is { } seconds)
        {
            TimerRemaining = MathF.Max(0f, seconds - _late);
            TimerRunning = true;
        }

        MarkRowAwake(def);
        Note(live, ObjectiveTransitionKind.Woke, 0f);
        Woke?.Invoke(new ObjectiveWoke(def.Number, def.WakeSoundGroup, def.Identity));
    }

    private string? MarkRow(ObjectiveDef def)
    {
        if (def.Identity is not { } identity)
        {
            return null;
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Priority == identity.Priority)
            {
                _rows[i] = _rows[i] with { Completed = true, Awake = false };
            }
        }

        string? sound = identity.Class switch
        {
            ObjectiveClass.Primary => _script.PrimaryCompleteSound,
            ObjectiveClass.Secondary => _script.SecondaryCompleteSound,
            ObjectiveClass.Tertiary => _script.TertiaryCompleteSound,
            _ => null,
        };
        if (sound != null)
        {
            _world.PlaySoundGroup(sound);
        }

        return sound;
    }

    private void MarkRowAwake(ObjectiveDef def)
    {
        if (def.Identity is not { } identity)
        {
            return;
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Priority == identity.Priority && !_rows[i].Completed)
            {
                _rows[i] = _rows[i] with { Awake = true };
            }
        }
    }

    private void CheckAggregateEnd()
    {
        if (_pending != MissionOutcome.None)
        {
            return;
        }

        if (AllFlaggedComplete(won: true))
        {
            End(MissionOutcome.Won, WonWrapUpS, objectivesSound: true);
        }
        else if (AllFlaggedComplete(won: false))
        {
            End(MissionOutcome.Lost, 3f, objectivesSound: true);
        }
    }

    private bool AllFlaggedComplete(bool won)
    {
        bool any = false;
        foreach (var live in _live)
        {
            if (!(won ? live.Def.Won : live.Def.Lost))
            {
                continue;
            }

            any = true;
            if (!live.Complete)
            {
                return false;
            }
        }

        return any;
    }

    private void PlayIfNamed(string? group)
    {
        if (group != null)
        {
            _world.PlaySoundGroup(group);
        }
    }

    private void End(MissionOutcome outcome, float wrapUp, bool objectivesSound = false)
    {
        if (_pending != MissionOutcome.None)
        {
            return;
        }

        if (objectivesSound)
        {
            PlayIfNamed(outcome == MissionOutcome.Won ? _script.ObjectivesWonSound : _script.ObjectivesLostSound);
        }

        _pending = outcome;
        _wrapUp = wrapUp;
        PlayIfNamed(outcome == MissionOutcome.Won ? _script.MissionWonSound : _script.MissionLostSound);
        EndingDecided?.Invoke(new MissionEnding(outcome, objectivesSound));
    }

    private void StepWrapUp(float dt)
    {
        if (_pending == MissionOutcome.None || Outcome != MissionOutcome.None)
        {
            return;
        }

        _wrapUp -= dt;
        if (_wrapUp > 0f)
        {
            return;
        }

        Outcome = _pending;
        MissionEnded?.Invoke(Outcome);
    }

    private Live? Target(int number) =>
        number >= 1 && number <= _live.Count ? _live[number - 1] : null;

    // One objective's mutable state. The zone set is per-wake: DANGER_ZONES_COMPLETED only counts
    // zones flagged while this objective was awake.
    private sealed class Live
    {
        public Live(ObjectiveDef def)
        {
            Def = def;
            State = def.BeginDormant ? ObjectiveState.Dormant : ObjectiveState.Awake;
        }

        public ObjectiveDef Def { get; }

        public ObjectiveState State { get; set; }

        public bool Alive { get; set; } = true;

        public bool Complete { get; set; }

        public float Timer { get; set; }

        public float NapRemaining { get; set; }

        public HashSet<string> Zones { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
