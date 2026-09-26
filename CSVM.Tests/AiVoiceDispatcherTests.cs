using System;
using System.Collections.Generic;
using CSVM.Flight.Ai;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The E16 trigger dispatch, engine-free: the 15 s cooldown armed by a FAILED talker roll exactly
/// as by a successful one, the hardcoded halving on the bearing ids 1–12, the broadcast election
/// (one line per event, a failed roll passing to the NEXT candidate, wrapping; players, the dead
/// and non-owners never eligible), force bypassing ONLY the aliveness check (never the cooldown),
/// the DI tiers at 70/50/30 % most-severe-first, the death-cry team split and Bail/NoBail pick,
/// the null-slot test before anything rolls, the 2 s mute window, and the bearing index
/// arithmetic against the plan's formula.
/// </summary>
public class AiVoiceDispatcherTests
{
    [Fact]
    public void CooldownIsArmedByAFailedRollExactlyAsByASuccessfulOne()
    {
        var d = new AiVoiceDispatcher(new ScriptedRandom(0.99), ResolveAll());
        var s = d.Register(1, 2, AimAssist.NeutralTeam, isPlayer: false,
            talkerChance: 0.5f, constitutionChance: 1f);

        // Draw 0.99 vs chance 0.5: the roll FAILS, and the slot is silenced for 15 s anyway.
        var failed = d.Dispatch(1, AiVoiceDispatcher.DiLowDmg, now: 10f);
        Assert.Null(failed.Clip);
        Assert.True(failed.Rolled);
        Assert.Contains("Talker test failed", failed.Outcome);
        Assert.Equal(25f, s.NextAllowedAt(AiVoiceDispatcher.DiLowDmg));

        // A retry inside the window never reaches the roll, even at certain chance.
        s.TalkerChance = 1f;
        var cooling = d.Dispatch(1, AiVoiceDispatcher.DiLowDmg, now: 24f);
        Assert.Null(cooling.Clip);
        Assert.False(cooling.Rolled);
        Assert.Equal("slot cooling", cooling.Outcome);

        // Past it, the line plays, and the SUCCESS stamps the same 15 s.
        var played = d.Dispatch(1, AiVoiceDispatcher.DiLowDmg, now: 25.5f);
        Assert.NotNull(played.Clip);
        Assert.Contains("Talker test passed", played.Outcome);
        Assert.Equal(40.5f, s.NextAllowedAt(AiVoiceDispatcher.DiLowDmg));

        // Another slot is untouched: cooldowns are per slot, per pilot.
        Assert.Equal(0f, s.NextAllowedAt(AiVoiceDispatcher.DiMedDmg));
    }

    [Fact]
    public void BearingTriggersHalveTheTalkerChanceHardcoded()
    {
        // Draw 0.6 against chance 1.0: any ordinary trigger passes…
        var d = new AiVoiceDispatcher(new ScriptedRandom(0.6, 0.6), ResolveAll());
        d.Register(1, 2, AimAssist.NeutralTeam, false, talkerChance: 1f, constitutionChance: 1f);
        Assert.NotNull(d.Dispatch(1, AiVoiceDispatcher.WaAttack, 10f).Clip);

        // …while a bearing call-out (id 5 = WA-Enemy-3L) fails the same draw at the halved 0.5.
        var bearing = d.Dispatch(1, 5, 10f);
        Assert.Null(bearing.Clip);
        Assert.Contains("Talker test failed", bearing.Outcome);
    }

    [Fact]
    public void BroadcastElectsOneSpeakerAndAFailedRollPassesTheLineOnWrapping()
    {
        // Election: Next(3) → 2 starts at C (the last), whose roll (0.9) fails at chance 0.5;
        // the line WRAPS to A, whose roll (0.1) passes. B is never rolled.
        var rng = new ScriptedRandom(0.9, 0.1) { Ints = { 2 } };
        var d = new AiVoiceDispatcher(rng, ResolveAll());
        var a = d.Register(1, 11, AimAssist.NeutralTeam, false, 0.5f, 1f);
        var b = d.Register(2, 12, AimAssist.NeutralTeam, false, 0.5f, 1f);
        var c = d.Register(3, 13, AimAssist.NeutralTeam, false, 0.5f, 1f);

        var decision = d.Broadcast(AiVoiceDispatcher.WaHighDmg, callerTeam: 1, now: 10f);
        Assert.Same(a, decision.Speaker);
        Assert.Equal("snd_id11_WA-HighDmg-A", decision.Clip);

        // One event, one line: C's failed roll armed C's cooldown, the played line armed A's,
        // and B, never reached, is untouched.
        Assert.Equal(25f, c.NextAllowedAt(AiVoiceDispatcher.WaHighDmg));
        Assert.Equal(25f, a.NextAllowedAt(AiVoiceDispatcher.WaHighDmg));
        Assert.Equal(0f, b.NextAllowedAt(AiVoiceDispatcher.WaHighDmg));
    }

    [Fact]
    public void BroadcastEligibilityExcludesPlayersTheDeadOtherTeamsAndNonOwners()
    {
        var d = new AiVoiceDispatcher(new ScriptedRandom(0.0) { Ints = { 0 } },
            (voId, family) => voId == 99 ? null : $"snd_id{voId}_{family}-A");
        d.Register(1, 11, AimAssist.NeutralTeam, isPlayer: true, 1f, 1f);   // the player: never
        var dead = d.Register(2, 12, AimAssist.NeutralTeam, false, 1f, 1f);
        dead.Alive = false;                                                  // dead: never
        d.Register(3, 99, AimAssist.NeutralTeam, false, 1f, 1f);            // owns no clip: never
        d.Register(4, 13, team: 7, isPlayer: false, 1f, 1f);                // wrong team: never

        var none = d.Broadcast(AiVoiceDispatcher.WaHighDmg, callerTeam: 1, now: 10f);
        Assert.Null(none.Clip);
        Assert.Equal("no eligible speaker", none.Outcome);

        // A teamless living owner IS eligible for any caller (the decoded "or teamless").
        var voiced = d.Register(5, 14, AimAssist.NeutralTeam, false, 1f, 1f);
        var decision = d.Broadcast(AiVoiceDispatcher.WaHighDmg, callerTeam: 1, now: 10f);
        Assert.Same(voiced, decision.Speaker);
    }

    [Fact]
    public void ForceBypassesAlivenessButNeverTheCooldown()
    {
        var d = new AiVoiceDispatcher(new ScriptedRandom(0.0, 0.0, 0.0, 0.0), ResolveAll());
        var s = d.Register(1, 2, AimAssist.NeutralTeam, false, 1f, constitutionChance: 1f);
        s.Alive = false;

        // Dead without force: gated out before anything rolls.
        var unforced = d.Dispatch(1, AiVoiceDispatcher.DeEnemy, 10f);
        Assert.Null(unforced.Clip);
        Assert.Equal("speaker dead", unforced.Outcome);

        // The death cry carries force: the dead speaker's own cry plays (constitution 1 → Bail).
        var cry = d.DeathCry(1, onPlayersTeam: false, now: 10f);
        Assert.Equal("snd_id2_DE-Bail-A", cry.Clip);

        // Force does NOT bypass the armed cooldown: a second forced cry inside 15 s is silent.
        var again = d.DeathCry(1, onPlayersTeam: false, now: 12f);
        Assert.Null(again.Clip);
        Assert.Equal("slot cooling", again.Outcome);
    }

    [Fact]
    public void DeathCrySplitsByTeamAndTheConstitutionRollPicksBailOrNoBail()
    {
        // constitution draw 0.9 vs chance 0.5 → NoBail; the team flag picks DA vs DE (id 20/21).
        var d = new AiVoiceDispatcher(new ScriptedRandom(0.9, 0.0, 0.1, 0.0), ResolveAll());
        d.Register(1, 2, AimAssist.NeutralTeam, false, 1f, constitutionChance: 0.5f);
        d.Register(2, 4, AimAssist.NeutralTeam, false, 1f, constitutionChance: 0.5f);

        var ownTeam = d.DeathCry(1, onPlayersTeam: true, now: 10f);
        Assert.Equal(AiVoiceDispatcher.DaOwnTeam, ownTeam.TriggerId);
        Assert.Equal("snd_id2_DA-NoBail-A", ownTeam.Clip);

        var enemy = d.DeathCry(2, onPlayersTeam: false, now: 10f);
        Assert.Equal(AiVoiceDispatcher.DeEnemy, enemy.TriggerId);
        Assert.Equal("snd_id4_DE-Bail-A", enemy.Clip);
    }

    [Fact]
    public void DamageTiersTestMostSevereFirstAt705030()
    {
        var d = new AiVoiceDispatcher(new ScriptedRandom(0.0, 0.0, 0.0, 0.0), ResolveAll());
        d.Register(1, 2, AimAssist.NeutralTeam, false, 1f, 1f);

        Assert.Equal("above every DI threshold", d.NotifyDamage(1, 0.75f, 10f).Outcome);
        Assert.Equal(AiVoiceDispatcher.DiLowDmg, d.NotifyDamage(1, 0.65f, 10f).TriggerId);
        Assert.Equal(AiVoiceDispatcher.DiMedDmg, d.NotifyDamage(1, 0.45f, 10f).TriggerId);
        // Below 30 % the MOST severe fires, never the lower tiers it also sits under.
        var high = d.NotifyDamage(1, 0.25f, 10f);
        Assert.Equal(AiVoiceDispatcher.DiHighDmg, high.TriggerId);
        Assert.Equal("snd_id2_DI-HighDmg-A", high.Clip);
    }

    [Fact]
    public void ASpeakerAlreadyTalkingIsRefusedPerSpeakerAndArmsNoCooldown()
    {
        var talking = new HashSet<int> { 1 };
        var d = new AiVoiceDispatcher(new ScriptedRandom(0.0), ResolveAll())
        {
            IsTalking = talking.Contains,
        };
        var busy = d.Register(1, 2, AimAssist.NeutralTeam, false, 1f, 1f);
        d.Register(2, 3, AimAssist.NeutralTeam, false, 1f, 1f);

        // The speaker mid-line is refused before the roll, and its slot stays unarmed, so the
        // line it could not speak costs it nothing.
        var over = d.Dispatch(1, AiVoiceDispatcher.WaAttack, now: 10f);
        Assert.Null(over.Clip);
        Assert.False(over.Rolled);
        Assert.Equal("already talking", over.Outcome);
        Assert.Equal(0f, busy.NextAllowedAt(AiVoiceDispatcher.WaAttack));

        // The test is per speaker: nobody else on the channel is held by it.
        Assert.NotNull(d.Dispatch(2, AiVoiceDispatcher.WaAttack, now: 10f).Clip);

        // A broadcast passes a talking candidate over without arming it, and elects the other.
        var elected = d.Broadcast(AiVoiceDispatcher.WaHighDmg, AimAssist.NeutralTeam, now: 10f);
        Assert.Equal("snd_id3_WA-HighDmg-A", elected.Clip);
        Assert.Equal(0f, busy.NextAllowedAt(AiVoiceDispatcher.WaHighDmg));

        // Once the line is done the same speaker speaks again.
        talking.Clear();
        Assert.NotNull(d.Dispatch(1, AiVoiceDispatcher.WaAttack, now: 10f).Clip);
    }

    [Fact]
    public void TheNullSlotIsTestedBeforeAnythingRollsAndArmsNoCooldown()
    {
        var d = new AiVoiceDispatcher(new ScriptedRandom(0.0),
            (_, family) => family.StartsWith("DI") ? null : "clip");
        var s = d.Register(1, 2, AimAssist.NeutralTeam, false, 1f, 1f);

        var decision = d.Dispatch(1, AiVoiceDispatcher.DiLowDmg, 10f);
        Assert.Null(decision.Clip);
        Assert.False(decision.Rolled);
        Assert.Equal("no clip", decision.Outcome);
        Assert.Equal(0f, s.NextAllowedAt(AiVoiceDispatcher.DiLowDmg));
    }

    [Fact]
    public void NothingSpeaksInsideTheTwoSecondMuteWindow()
    {
        var d = new AiVoiceDispatcher(new ScriptedRandom(0.0, 0.0), ResolveAll());
        var s = d.Register(1, 2, AimAssist.NeutralTeam, false, 1f, 1f);

        var muted = d.Dispatch(1, AiVoiceDispatcher.WaAttack, now: 1.9f);
        Assert.Null(muted.Clip);
        Assert.Equal("muted (mission clock < 2 s)", muted.Outcome);
        Assert.Equal(0f, s.NextAllowedAt(AiVoiceDispatcher.WaAttack));
        Assert.NotNull(d.Dispatch(1, AiVoiceDispatcher.WaAttack, now: 2.0f).Clip);
    }

    [Fact]
    public void BearingIndexFollowsThePlansFormula()
    {
        // id = 1 + 3·bearing + altitudeBand, low/level/high within 12/3/6/9 o'clock.
        int id = 1;
        for (int bearing = 0; bearing < 4; bearing++)
        {
            for (int band = 0; band < 3; band++)
            {
                Assert.Equal(id++, AiVoiceDispatcher.BearingTriggerId(bearing, band));
            }
        }
        Assert.Equal(2, AiVoiceDispatcher.BearingTriggerId(4, 1)); // wraps at 4 → 12 o'clock

        // The family table agrees token for token: id 6 is WA-Enemy-3H, id 7 WA-Enemy-6L.
        Assert.Equal("WA-Enemy-3H", CombatVoice.TriggerFamilies[6]);
        Assert.Equal("WA-Enemy-6L", CombatVoice.TriggerFamilies[7]);
    }

    [Fact]
    public void BearingQuantisationReadsTheWarnedPilotsFrame()
    {
        var own = new Vector3(0f, 400f, 0f);
        var fwd = new Vector3(0f, 0f, -1f); // nose -Z

        // Dead ahead, level → WA-Enemy-12 (id 2); ahead-high → 12H; right → 3; behind-low →
        // 6L; left → 9; 350°-ish (10° left of the nose) wraps back to 12 o'clock.
        Assert.Equal(2, AiVoiceDispatcher.BearingTriggerFor(own, fwd, own + new Vector3(0f, 0f, -500f)));
        Assert.Equal(3, AiVoiceDispatcher.BearingTriggerFor(own, fwd, own + new Vector3(0f, 200f, -500f)));
        Assert.Equal(5, AiVoiceDispatcher.BearingTriggerFor(own, fwd, own + new Vector3(500f, 0f, 0f)));
        Assert.Equal(7, AiVoiceDispatcher.BearingTriggerFor(own, fwd, own + new Vector3(0f, -200f, 500f)));
        Assert.Equal(11, AiVoiceDispatcher.BearingTriggerFor(own, fwd, own + new Vector3(-500f, 0f, 0f)));
        Assert.Equal(2, AiVoiceDispatcher.BearingTriggerFor(own, fwd,
            own + new Vector3(-88f, 0f, -500f)));
    }

    [Fact]
    public void SeededRunsDispatchIdentically()
    {
        List<string?> Run()
        {
            var d = new AiVoiceDispatcher(new Random(7), ResolveAll());
            d.Register(1, 11, AimAssist.NeutralTeam, false, 0.5f, 0.5f);
            d.Register(2, 12, AimAssist.NeutralTeam, false, 0.5f, 0.5f);
            var outcomes = new List<string?>();
            for (int i = 0; i < 12; i++)
            {
                float now = 3f + i * 4f;
                outcomes.Add(d.Broadcast(AiVoiceDispatcher.WaHighDmg, 1, now).Clip);
                outcomes.Add(d.NotifyDamage(1, 0.6f - i * 0.04f, now).Clip);
                outcomes.Add(d.Dispatch(2, AiVoiceDispatcher.TaSucShk, now).Clip);
            }
            outcomes.Add(d.DeathCry(1, false, 60f).Clip);
            return outcomes;
        }

        Assert.Equal(Run(), Run());
        Assert.Contains(Run(), clip => clip != null); // the pin measures something
    }

    // A resolver where every family resolves for every pilot.
    private static Func<int, string, string?> ResolveAll() =>
        (voId, family) => $"snd_id{voId}_{family}-A";

    // A Random whose NextDouble()s replay a script (repeating the last entry) and whose
    // Next(max) draws pop Ints (falling back to 0), the election/roll order is
    // the thing under test, so the draws must be exact.
    private sealed class ScriptedRandom : Random
    {
        private readonly Queue<double> _doubles;
        private int _intAt;

        public ScriptedRandom(params double[] doubles)
        {
            _doubles = new Queue<double>(doubles);
        }

        public List<int> Ints { get; } = new();

        public override double NextDouble() =>
            _doubles.Count > 1 ? _doubles.Dequeue() : _doubles.Peek();

        public override int Next(int maxValue) =>
            _intAt < Ints.Count ? Ints[_intAt++] % Math.Max(1, maxValue) : 0;
    }
}
