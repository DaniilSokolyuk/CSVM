using System;
using System.Collections.Generic;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Roster;

/// <summary>Wires the E16 voice dispatch into a running flight session: subscribes the decoded
/// event sources on each registered aircraft, runs them through <see cref="AiVoiceDispatcher"/>,
/// and plays every decision through <c>CombatVoice.PlayableFor</c> only, resolving the
/// name, <c>WorldSounds.HasStream</c> answering availability, and <see cref="MissionRadio.Speak"/>
/// queueing it on the flat Voice channel the objective callouts share, never at the speaker.
/// The wired/unwired dispatch-site table is docs/formats/combat-voice.md "The remake's dispatch
/// sites". Speakers register on their real <see cref="FlightController.Team"/>; a broadcast
/// elects among a caller's own side.
/// ⚠ Free flight and --vs give every pilot a default Team, so a broadcast still only elects
/// within a "teamless" match there, wiring engages once a mission shares an explicit team
/// between AI, or between an AI and the player.</summary>
public sealed partial class AiVoiceRuntime : Node
{
    /// <summary>The player's WA-HighDmg broadcast threshold, decoded (id 13 fires when the
    /// player's health crosses 30 %).</summary>
    public const float PlayerHighDmgFraction = 0.30f;

    private readonly CombatVoice _voice;
    private readonly WorldSounds _sounds;
    private readonly MissionRadio _radio;
    private readonly AiVoiceDispatcher _dispatcher;
    private readonly Random _rng;
    private readonly Dictionary<int, FlightController> _bySpeaker = new();
    private readonly Dictionary<int, FlightController> _byIndex = new();
    private readonly Dictionary<FlightController, float> _lastPlayerFraction = new();
    private readonly HashSet<int> _humans = new();
    private readonly HashSet<int> _watched = new();
    private readonly HashSet<int> _watchedKills = new();
    private readonly HashSet<int> _watchedFriendlyFire = new();
    private readonly HashSet<(int Speaker, string Family)> _noClipLogged = new();
    private readonly Dictionary<int, float> _nextCallOut = new();
    private float _now;

    public AiVoiceRuntime(CombatVoice voice, WorldSounds sounds, MissionRadio radio, Random rng)
    {
        _voice = voice;
        _sounds = sounds;
        _radio = radio;
        _rng = rng;
        _dispatcher = new AiVoiceDispatcher(rng, ResolvePlayable)
        {
            IsTalking = radio.IsSpeaking,
        };
    }

    /// <summary>Every line handed to the radio: (speaker tag, trigger id, resolved clip), the
    /// observability seam the ai-voice suite counts. A line the busy channel keeps waiting past
    /// its QUEUE tolerance is still dropped there, unheard.</summary>
    public event Action<string, int, string>? LinePlayed;

    /// <summary>The dispatcher, exposed for tests/probes (registration order, cooldown stamps).</summary>
    public AiVoiceDispatcher Dispatcher => _dispatcher;

    /// <summary>The mission clock the dispatch runs on, advanced by <see cref="Step"/>.</summary>
    public float Now => _now;

    /// <summary>Advances the mission clock one sim step (the 2 s mute window and every cooldown
    /// run on this, so a halted clock halts the chatter too), then raises the attack pair for
    /// every pursuer holding a human. Called by SessionSimulation.</summary>
    public void Step(float dt)
    {
        _now += dt;
        RaiseAttackCallOuts();
    }

    /// <summary>Takes an AI aircraft, voiced or not. ⚠ Hand over EVERY AI the session builds: the
    /// mode machine is watched either way, because the bearing call-out the commit raises is
    /// broadcast and is spoken by a different aircraft than the one whose mode changed. A null
    /// accent, or one resolving to no voiced pilot, registers no speaker and is silent, not an
    /// error.</summary>
    public void RegisterAi(FlightController ai, int? accentId, float talkerChance,
        float constitutionChance)
    {
        WatchModes(ai);
        IndexAndWatchKills(ai);
        WatchFriendlyFire(ai);
        if (accentId is not { } accent)
        {
            return;
        }
        int? voId = _voice.PilotFor(accent, _rng);
        if (voId is not { } vo)
        {
            Log.Info("sound", $"ai voice: {ai.Name}: accent {accent} resolves to no voiced pilot, silent");
            return;
        }
        var speaker = _dispatcher.Register(ai.PlayerIndex, vo, ai.Team,
            isPlayer: false, talkerChance, constitutionChance);
        _bySpeaker[ai.PlayerIndex] = ai;
        // An inert aircraft neither speaks nor is elected for a broadcast, mirrored here because
        // the dispatcher cannot see a FlightController's own aliveness.
        speaker.Alive = ai.InPlay;
        ai.InertChanged += plane => speaker.Alive = plane.InPlay;
        Log.Info("sound", $"ai voice: {ai.Name}: accent {accent} -> VO id {vo} (talker {talkerChance:0.00})");

        ai.DamageApplied += (damaged, _) =>
        {
            if (damaged.Damage is { } dmg)
            {
                Play(_dispatcher.NotifyDamage(speaker.Id, dmg.SummaryHealthFraction, _now));
            }
        };
        ai.Downed += (_, _) =>
        {
            speaker.Alive = false;
            Play(_dispatcher.DeathCry(speaker.Id, onPlayersTeam: ai.Team == AimAssist.PlayerTeam, _now));
        };
    }

    /// <summary>Registers a human rig as an event source; the player itself never speaks an AI
    /// line. Its health crossing 30 % broadcasts <c>WA-HighDmg</c> to the flight. A kill of its
    /// own addresses the gloat (id 24) to the rig itself and broadcasts <c>PR-EnemyDwn</c>
    /// (id 16). The gloat is silent until a player rig resolves a voice set. A stunt run the rig
    /// already carries is watched for its completed zones (id 15).</summary>
    public void RegisterPlayer(FlightController rig)
    {
        _humans.Add(rig.PlayerIndex);
        IndexAndWatchKills(rig);
        _lastPlayerFraction[rig] = 1f;
        if (rig.Stunt is { } stunt)
        {
            stunt.ZoneCompleted += _ => DangerZoneCompleted(rig);
        }
        rig.DamageApplied += (damaged, _) =>
        {
            if (damaged.Damage is not { } dmg)
            {
                return;
            }
            float fraction = dmg.SummaryHealthFraction;
            if (fraction < PlayerHighDmgFraction
                && _lastPlayerFraction[damaged] >= PlayerHighDmgFraction)
            {
                Play(_dispatcher.Broadcast(AiVoiceDispatcher.WaHighDmg, damaged.Team, _now));
            }
            _lastPlayerFraction[damaged] = fraction;
        };
    }

    /// <summary>Wires the <c>WA-Turret</c> site (id 0): every gunner in the session, carried or
    /// emplaced, reports its first clear sight of a human player through the shared pool, and the
    /// flight broadcasts on that player's team through the ordinary gate. Called once per
    /// session, since the pool is what both turret families are built against.</summary>
    public void WatchTurrets(ProjectilePool projectiles) =>
        projectiles.TurretAcquiredPlayer += OnTurretAcquired;

    /// <summary>The <c>PR-DngrZn</c> site (id 15): a Danger Zone the player has just flown, praised
    /// by one of that player's own flight. The original raises it inside the zone's completion
    /// routine, where both gates read crossed (<c>FUN_00446990</c>, <c>0x004469ff</c>). That
    /// routine is reached for the local player's vehicle alone (<c>FUN_0048e580</c>,
    /// <c>0x0048ea1f</c>), so no AI completion speaks it.</summary>
    public void DangerZoneCompleted(FlightController player) =>
        Play(_dispatcher.Broadcast(AiVoiceDispatcher.PrDngrZn, player.Team, _now));

    // The turret warning is a broadcast, so the gunner is not the speaker: one of the warned
    // player's own flight says it, elected on that player's team (decoded, id 0 broadcasts).
    private void OnTurretAcquired(TurretController turret, FlightController player) =>
        Play(_dispatcher.Broadcast(AiVoiceDispatcher.WaTurret, player.Team, _now));

    // ⚠ Subscribed for every AI, not only for the registered speakers. The commit below raises a
    // broadcast bearing call-out that ANOTHER aircraft speaks, and the shipped rosters leave
    // nearly every enemy on accentID -1, so watching only the voiced ones silences the player's
    // own flight. Idempotent per aircraft, since a spawn can be handed over more than once.
    private void WatchModes(FlightController ai)
    {
        if (ai.Pilot?.Machine is not { } machine || !_watched.Add(ai.PlayerIndex))
        {
            return;
        }
        machine.ModeChanged += (from, to, _) => OnModeChanged(ai, machine, from, to);
    }

    private void OnModeChanged(FlightController ai, AiModeMachine machine, AiMode from, AiMode to)
    {
        int speakerId = ai.PlayerIndex;
        // The commit itself, for the case where it falls after the mute window. ⚠ Only out of
        // patrol: a re-entry from avoid crash happens every few seconds near terrain and is the
        // same engagement, which the raise interval would absorb anyway.
        if (to == AiMode.Pursue && from == AiMode.Patrol
            && ai.Pilot?.Gunner?.AircraftTarget is { IsHumanPiloted: true } quarry)
        {
            RaiseAttackCallOut(ai, quarry);
        }

        // The shake taunt at the evade episode's end, and only with the flag already cleared: the
        // original raises it where the flag drops and nowhere else. An episode left with the flag
        // still up says nothing here, its pursuer speaks the pair off its own geometry instead.
        if (from is AiMode.Evade or AiMode.EvasiveManeuver
            && to is not (AiMode.Evade or AiMode.EvasiveManeuver)
            && !machine.Evading)
        {
            Play(_dispatcher.Dispatch(speakerId, AiVoiceDispatcher.TaSucShk, _now));
        }
    }

    // The decoded raise condition for the attack pair: the original's combat driver raises them
    // every frame its pursuer's target is the local player, and leans on the 15 s slot cooldown
    // for the rate (docs/formats/combat-voice.md, "The pursue path"). ⚠ Do not raise every tick
    // here: the broadcast election walks and resolves every speaker, and no slot can speak twice
    // inside that cooldown anyway, so the raise runs at the cooldown's own interval. Losing the
    // human clears the stamp, so a fresh engagement raises at once.
    private void RaiseAttackCallOuts()
    {
        foreach (var ai in _byIndex.Values)
        {
            if (ai.Pilot?.Gunner?.AircraftTarget is not { IsHumanPiloted: true } quarry)
            {
                _nextCallOut.Remove(ai.PlayerIndex);
                continue;
            }
            if (_nextCallOut.TryGetValue(ai.PlayerIndex, out float next) && _now < next)
            {
                continue;
            }
            RaiseAttackCallOut(ai, quarry);
        }
    }

    // One raise of the pair: the pursuer's own WA-Attack and the flight's bearing call-out,
    // computed in the warned player's frame and broadcast on the player's side. The taunt pair
    // rides the same raise, addressed to the pursuer off its own nose against the quarry.
    // ⚠ The mute window is read here as well as in the gate. A raise the window refuses must not
    // consume the interval, or a pursuer that commits on the mission's first frame would stay
    // silent for the next fifteen seconds, which is the whole complaint.
    private void RaiseAttackCallOut(FlightController ai, FlightController quarry)
    {
        if (_now < AiVoiceDispatcher.MuteWindowS)
        {
            return;
        }
        _nextCallOut[ai.PlayerIndex] = _now + AiVoiceDispatcher.SlotCooldownS;
        // The decoded block refuses the taunt while the pursuer is itself in an evade reaction,
        // and a pilot with no mode machine is not evading.
        if (ai.Pilot?.Machine is not { Evading: true }
            && AiVoiceDispatcher.TauntTriggerFor(ai.WorldPosition, ai.NoseDirection,
                quarry.WorldPosition) is { } taunt)
        {
            Play(_dispatcher.Dispatch(ai.PlayerIndex, taunt, _now));
        }
        Play(_dispatcher.Dispatch(ai.PlayerIndex, AiVoiceDispatcher.WaAttack, _now));
        int bearing = AiVoiceDispatcher.BearingTriggerFor(
            quarry.WorldPosition, quarry.NoseDirection, ai.WorldPosition);
        Play(_dispatcher.Broadcast(bearing, quarry.Team, _now));
    }

    // ⚠ Subscribed for every aircraft handed over, human rigs included. The gloat's speaker is the
    // KILLER, so the site needs the victim's team and the shooter id the death report carries. A
    // killer with no voice of its own simply stays silent. The same handover puts the aircraft
    // into the index the team lookups and the bearing call-outs read. Idempotent per aircraft.
    private void IndexAndWatchKills(FlightController plane)
    {
        _byIndex[plane.PlayerIndex] = plane;
        if (!_watchedKills.Add(plane.PlayerIndex))
        {
            return;
        }
        plane.Downed += (_, killer) => OnDowned(plane, killer);
    }

    // A shooter id may belong to an aircraft never handed over, a turret or a rig that left. The
    // roster's own team table is the fallback for those.
    private int TeamOf(int pilot) =>
        _byIndex.TryGetValue(pilot, out var node) ? node.Team : AimAssist.TeamOfPilot(pilot);

    // The gloat, decoded polarity (combat-voice.md, "The gloat triggers and trigger 28"). The
    // friendly predicate over shooter and victim comes first, so a friendly kill picks no gloat at
    // all; the same predicate over the victim and the player's team then picks 22 or 23 for the
    // killer to speak. A kill by a human rig takes the player arm instead, 24 and then 16.
    private void OnDowned(FlightController victim, int? killer)
    {
        if (killer is not { } shooter || shooter == victim.PlayerIndex)
        {
            return;
        }
        if (!AimAssist.Hostile(TeamOf(shooter), victim.Team))
        {
            return;
        }
        if (_humans.Contains(shooter))
        {
            // ⚠ Do not make the broadcast the else-branch of the addressed line: the decoded
            // null-slot test jumps INTO it, so 16 runs whether or not the player's own rig
            // resolved a voice set, and it always elects from the local player's team.
            Play(_dispatcher.Dispatch(shooter, AiVoiceDispatcher.GlPlyrDwn, _now));
            Play(_dispatcher.Broadcast(AiVoiceDispatcher.PrEnemyDwn, AimAssist.PlayerTeam, _now));
            return;
        }
        int trigger = AimAssist.Hostile(victim.Team, AimAssist.PlayerTeam)
            ? AiVoiceDispatcher.GlEnemyDwn
            : AiVoiceDispatcher.GlAllyDwn;
        Play(_dispatcher.Dispatch(shooter, trigger, _now));
    }

    // ⚠ Subscribed for AI only: the struck aircraft is the speaker here, and a human rig speaks no
    // AI line, so a player hit by a wingman's round stays silent. Idempotent per aircraft.
    private void WatchFriendlyFire(FlightController ai)
    {
        if (!_watchedFriendlyFire.Add(ai.PlayerIndex))
        {
            return;
        }
        ai.DamageApplied += OnFriendlyFire;
    }

    // The ally distress, the decoded first arm (combat-voice.md, "The gloat triggers and trigger
    // 28"): a round from the local player damages an aircraft the team predicate calls friendly,
    // and the STRUCK aircraft speaks. The second arm counts survivors among three undecoded
    // globals and waits on that decode.
    private void OnFriendlyFire(FlightController victim, int? shooter)
    {
        if (shooter is not { } id || !_humans.Contains(id))
        {
            return;
        }
        if (AimAssist.Hostile(TeamOf(id), victim.Team))
        {
            return;
        }
        Play(_dispatcher.Dispatch(victim.PlayerIndex, AiVoiceDispatcher.DsAlly, _now));
    }

    // B8's availability contract: the resolved name must have a decoded stream behind it, for a
    // variant group that means a playable member, which the group name itself cannot answer.
    // ⚠ An accent registered outside the roster (CLI, Instant Action) must join the prewarm set
    // (SessionPrewarmNames' extraAccents); unprewarmed, this returns null and the pilot is silent.
    private string? ResolvePlayable(int voId, string family)
    {
        string? name = _voice.PlayableFor(voId, family);
        if (name == null)
        {
            return null;
        }
        foreach (var clip in _voice.ClipsFor(voId, family))
        {
            if (_sounds.HasStream(clip))
            {
                return name;
            }
        }
        return null;
    }

    private void Play(AiVoiceDispatcher.Decision decision)
    {
        if (decision.Speaker is not { } speaker)
        {
            return;
        }
        string tag = _bySpeaker.TryGetValue(speaker.Id, out var node) ? node.Name : $"#{speaker.Id}";
        if (decision.Clip is { } clip)
        {
            // ⚠ Do not place a line at the speaker; the defs are QUEUE radio lines with no 3D flag,
            // and the original plays them flat through the one queue the objective callouts use.
            // The speaker rides along so the gate's already-talking test can read it back.
            string? resolved = _radio.Speak(clip, _rng, speaker.Id);
            Log.Info("sound", $"ai voice: {tag}: trigger #{decision.TriggerId} -> {clip}{(resolved != null && resolved != clip ? $" ({resolved})" : "")} ({decision.Outcome})");
            if (resolved != null)
            {
                LinePlayed?.Invoke(tag, decision.TriggerId, resolved);
            }
        }
        else if (decision.Outcome == AiVoiceDispatcher.NoClipOutcome)
        {
            LogNoClip(speaker, decision.TriggerId, tag);
        }
        else if (decision.Rolled)
        {
            // Both roll outcomes are logged; the other gate short-circuits (cooling, muted)
            // are silent here, they fire at hit rate.
            Log.Info("sound", $"ai voice: {tag}: trigger #{decision.TriggerId} silent ({decision.Outcome})");
        }
    }

    // Once per speaker and family, since the refusal fires at hit rate. A pilot owning the family's
    // defs with none prewarmed is a missing prewarm accent, so it warns; owning none is data.
    private void LogNoClip(AiVoiceDispatcher.Speaker speaker, int triggerId, string tag)
    {
        string family = triggerId >= 0 && triggerId < CombatVoice.TriggerFamilies.Count
            ? CombatVoice.TriggerFamilies[triggerId]
            : "";
        if (!_noClipLogged.Add((speaker.Id, family)))
        {
            return;
        }
        if (_voice.ClipsFor(speaker.VoId, family).Count > 0)
        {
            Log.Warn("sound", $"ai voice: {tag}: trigger #{triggerId} ({family}) silent, VO id {speaker.VoId} owns the clips but none was prewarmed");
        }
        else
        {
            Log.Info("sound", $"ai voice: {tag}: trigger #{triggerId} ({family}) silent, VO id {speaker.VoId} owns no such clip");
        }
    }
}
