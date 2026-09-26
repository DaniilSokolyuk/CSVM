using CSVM.Mech3;

namespace CSVM.Session.Roster;

/// <summary>
/// The decoded egen launch cycle for ONE generator: pure state over <c>Step</c> calls, no
/// clocks, no randomness, no node reads, so the timing law is unit-testable off-engine.
/// Decode, the hangar door timings and the credit rule:
/// docs/formats/mission-entities/enemy-generators.md.
/// ⚠ Every cycle starts with no launch budget, whatever the authored <c>capacity</c> says: the
/// original never reads that field, and a generator launches only what
/// <see cref="GrantCapacity"/> credits it.
/// </summary>
public sealed class GeneratorCycle
{
    /// <summary>The door opens this many seconds before a due spawn (hardcoded in the original).</summary>
    public const float DoorLeadSeconds = 4f;

    /// <summary>Minimum seconds the door stays open once opened (hardcoded in the original).</summary>
    public const float DoorMinOpenSeconds = 4f;

    /// <summary>The door closes early only when the next spawn is more than this many seconds
    /// away (hardcoded in the original), so a fast-cycling generator leaves its hangar open.</summary>
    public const float DoorEarlyCloseGapSeconds = 8f;

    /// <summary>Seconds a launch bay stays open after its host's kill: the decoded wreck timer
    /// (the kill stamps the zeppelin's sink for 3 s later). A launch already waiting out its door
    /// lead completes past it. ⚠ A remake-only rule: the original disables the generator on
    /// the kill tick, and C5/M04 loses on it when the fourth gasbag dies inside OBJECTIVE10's
    /// 0.5 s nap before OBJECTIVE11 credits Miles's launch
    /// (docs/formats/mission-entities/enemy-generators.md, "The host's death").</summary>
    public const float HostDeathGraceSeconds = 3f;

    private readonly int _maxActive;
    private readonly int _waveSize;
    private readonly float _wavePeriod;
    private readonly float _indPeriod;
    private readonly float? _minAltitude;

    private int _capacityRemaining;
    private int _spawnedThisWave;
    private float _timer;
    private float _nextEvent;
    private float? _sinceHostDeath;
    private bool _launchHeldForDoor;

    public GeneratorCycle(int maxActive, int waveSize, float wavePeriod, float indPeriod,
        float? minAltitude)
    {
        _maxActive = maxActive;
        _waveSize = waveSize;
        _wavePeriod = wavePeriod;
        _indPeriod = indPeriod;
        _minAltitude = minAltitude;
        // The decode does not pin the FIRST threshold; load is treated as "a wave just
        // completed" (the full inter-wave gap), the conservative reading until F20 confirms.
        _nextEvent = indPeriod + wavePeriod;
    }

    public GeneratorCycle(EnemyGeneratorDef def)
        : this(def.MaxActive, def.WaveSize, def.WavePeriod, def.IndPeriod, def.MinAltitude)
    {
    }

    /// <summary>Spawns still alive, the <c>active</c> count of the blocking rule. A spawn's
    /// death frees its slot via <see cref="SpawnRemoved"/>.</summary>
    public int Active { get; private set; }

    /// <summary>Permanently off: the host's death disables its generator for good, once
    /// <see cref="HostDeathGraceSeconds"/> have run since the kill.</summary>
    public bool Disabled { get; private set; }

    /// <summary>The host's kill is recorded and the grace is running; launches still fire.</summary>
    public bool HostDead => _sinceHostDeath != null;

    /// <summary>The hangar door, driven by <see cref="Step"/> under the decoded hardcoded
    /// timings. Closed at load; on the host's death it keeps its last state (the decoded loop
    /// early-outs a disabled generator before any door rule runs). No spawn shares the step this
    /// flips on, so nothing launches through panels that have not moved.</summary>
    public bool DoorOpen { get; private set; }

    /// <summary>Seconds since the last spawn (or load), pushed back to the lead point when a due
    /// spawn waits for its hangar; compared against <see cref="NextEvent"/>.</summary>
    public float Timer => _timer;

    /// <summary>The threshold the timer must reach for the next spawn.</summary>
    public float NextEvent => _nextEvent;

    /// <summary>Launches this cycle still has credit for. Zero at load.</summary>
    public int CapacityRemaining => _capacityRemaining;

    /// <summary>The host died: the bay keeps launching for <see cref="HostDeathGraceSeconds"/>,
    /// then disables permanently (never re-enabled, as decoded). A second kill report while the
    /// grace runs does not restart it.</summary>
    public void HostDied() => _sinceHostDeath ??= 0f;

    /// <summary>The one way a cycle gains launches: a script's <c>WAKEUP_GENERATOR</c>, an
    /// Instant Action wave's member count, or cutscene callback 800, each adding to the
    /// remaining capacity the original keeps at <c>+0x80</c>.</summary>
    public void GrantCapacity(int count) => _capacityRemaining += count;

    /// <summary>A spawned aircraft left the fight (shot down); its max_active slot frees.</summary>
    public void SpawnRemoved()
    {
        if (Active > 0)
        {
            Active--;
        }
    }

    /// <summary>Advances the cycle one sim step. True exactly when a spawn is due this step,
    /// at most one per call, matching the original's one-spawn-per-tick shape. A blocked step
    /// still advances the timer (hold, not cancel); the released spawn then follows by the door
    /// lead whenever the hangar has to open for it, unless <see cref="ReleaseDoorHold"/> says the
    /// door has no animation to travel. <paramref name="hostAltitude"/> is compared against the
    /// authored gate; pass anything when <see cref="EnemyGeneratorDef.MinAltitude"/> is null.</summary>
    public bool Step(float dt, float hostAltitude)
    {
        if (Disabled)
        {
            return false;
        }
        if (_sinceHostDeath is float since)
        {
            _sinceHostDeath = since + dt;
            // A launch waiting out its door lead outlives the grace, or the remake-only rule
            // buys C5/M04 nothing: the credit lands 0.5 s after the kill and the lead runs
            // past 3 s.
            if (since >= HostDeathGraceSeconds && !_launchHeldForDoor)
            {
                Disabled = true;
                return false;
            }
        }
        _timer += dt;
        if (Blocked(hostAltitude))
        {
            // Hold, not cancel: the timer and the wave counter keep running untouched and
            // ONLY the door closes (once it has been open its minimum), the decoded
            // blocked branch. The door does not reopen while blocked.
            if (DoorOpen && _timer >= DoorMinOpenSeconds)
            {
                DoorOpen = false;
            }
            _launchHeldForDoor = false;
            return false;
        }
        if (DoorOpen && _timer >= DoorMinOpenSeconds
            && _timer + DoorEarlyCloseGapSeconds < _nextEvent)
        {
            DoorOpen = false;   // early close: the next spawn is more than 8 s away
        }
        if (!DoorOpen && _timer >= _nextEvent - DoorLeadSeconds)
        {
            DoorOpen = true;    // open 4 s ahead of the due spawn
            if (_timer >= _nextEvent)
            {
                // The panels only start moving on this step, and the original's spawn tests the
                // door for FULLY open, a state its open animation's completion reaches. Give a
                // timer that ran past both thresholds while blocked the lead back.
                _timer = _nextEvent - DoorLeadSeconds;
                _launchHeldForDoor = true;
            }
        }
        if (!DoorOpen || _timer < _nextEvent)
        {
            return false;
        }
        return CommitSpawn();
    }

    /// <summary>Reports that the door this step opened has no animation to travel, so it stands
    /// fully open at once as the decoded no-definition branch does, and any launch held for its
    /// lead goes now. True when that launch fires, which the caller books like any other.</summary>
    public bool ReleaseDoorHold()
    {
        if (!_launchHeldForDoor)
        {
            return false;
        }
        return CommitSpawn();
    }

    private bool CommitSpawn()
    {
        _launchHeldForDoor = false;
        _capacityRemaining--;
        Active++;
        _spawnedThisWave++;
        _timer = 0f;
        if (_spawnedThisWave >= _waveSize)
        {
            _spawnedThisWave = 0;
            _nextEvent = _indPeriod + _wavePeriod;
        }
        else
        {
            _nextEvent = _indPeriod;
        }
        return true;
    }

    // The decoded block: the rest of the wave must fit under max_active AND under the credit.
    private bool Blocked(float hostAltitude) =>
        (_waveSize - _spawnedThisWave) + Active > _maxActive
        || _waveSize - _spawnedThisWave > _capacityRemaining
        || (_minAltitude is float gate && hostAltitude < gate);
}
