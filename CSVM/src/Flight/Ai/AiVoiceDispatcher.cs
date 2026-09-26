using System;
using System.Collections.Generic;
using CSVM.Flight.Weapons;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>The combat-voice trigger dispatch: events in, (speaker, clip, outcome) decisions out.
/// Decode: <c>docs/formats/combat-voice.md</c>, the talker roll, the cooldown, the DI damage
/// tiers and the broadcast election. Engine-free in the <see cref="AiModeMachine"/> sense: no
/// nodes, no clocks, all randomness from the injected seeded stream, playback delegated to the
/// resolver the caller supplies.
/// ⚠ The broadcast election is one line per event: a failed roll passes to the NEXT candidate,
/// wrapping, never N independent rolls.
/// Invented, named as such: the <c>Bail</c>/<c>NoBail</c> pick below the death-cry family root is
/// a constitution roll (unconfirmed). The bearing quantisation is decoded whole: the nearest of
/// the four quadrants about the warned pilot's heading, and the band at
/// <see cref="LevelBandSin"/>.</summary>
public sealed class AiVoiceDispatcher
{
    /// <summary>Decoded: the per-slot cooldown, stamped on BOTH roll outcomes.</summary>
    public const float SlotCooldownS = 15f;

    /// <summary>Decoded: nothing speaks in the mission's first two seconds.</summary>
    public const float MuteWindowS = 2f;

    /// <summary>Decoded: ids 1–12 halve the talker chance, hardcoded.</summary>
    public const float BearingChanceFactor = 0.5f;

    /// <summary>Decoded: the low/level/high band splits at this vertical component of the UNIT
    /// vector between the two aircraft, about 17.5 degrees off the horizontal. ⚠ An angle, not a
    /// height, so the band does not widen with separation (docs/formats/combat-voice.md, "The
    /// pursue path").</summary>
    public const float LevelBandSin = 0.3f;

    /// <summary>Decoded: a pursuer holding the player within this cosine of its own nose (about
    /// 31.8 degrees) taunts the player's failed shake (docs/formats/combat-voice.md).</summary>
    public const float TauntNoseCos = 0.85f;

    /// <summary>Decoded: a pursuer holding the player within this cosine of dead astern (about
    /// 45.6 degrees) taunts the player on its own tail.</summary>
    public const float TauntTailCos = 0.7f;

    /// <summary>The trigger ids this dispatcher names at its call sites (the full 29-id table is
    /// <c>CombatVoice.TriggerFamilies</c>).</summary>
    public const int WaTurret = 0;

    public const int WaHighDmg = 13;

    public const int WaAttack = 14;

    public const int PrDngrZn = 15;

    public const int PrEnemyDwn = 16;

    public const int DiLowDmg = 17;

    public const int DiMedDmg = 18;

    public const int DiHighDmg = 19;

    public const int DaOwnTeam = 20;

    public const int DeEnemy = 21;

    public const int GlAllyDwn = 22;

    public const int GlEnemyDwn = 23;

    public const int GlPlyrDwn = 24;

    public const int TaFailTail = 25;

    public const int TaFailShk = 26;

    public const int TaSucShk = 27;

    public const int DsAlly = 28;

    /// <summary>The gate's outcome when the resolver finds no playable clip for the speaker.</summary>
    public const string NoClipOutcome = "no clip";

    private const int TriggerCount = 29;

    private readonly Random _rng;
    private readonly Func<int, string, string?> _resolve;
    private readonly Dictionary<int, Speaker> _speakers = new();
    private readonly List<Speaker> _order = new();

    /// <summary><paramref name="resolve"/> answers (pilot VO id, family root) with the one
    /// playable name, or null when the pilot owns no playable clip for it, availability, not
    /// just def presence.</summary>
    public AiVoiceDispatcher(Random rng, Func<int, string, string?> resolve)
    {
        _rng = rng;
        _resolve = resolve;
    }

    /// <summary>Whether a speaker is mid-line (the gate's "must not already be talking"). The
    /// session answers it from the radio channel, which holds the queued and on-air line's own
    /// speaker and length; null = never, for a dispatcher driven without a channel.</summary>
    public Func<int, bool>? IsTalking { get; set; }

    /// <summary>Registered speakers, in registration order, the broadcast election's list.</summary>
    public IReadOnlyList<Speaker> Speakers => _order;

    /// <summary>The bearing trigger id: <c>1 + 3·bearingIndex + altitudeBand</c> (decoded),
    /// bearings 0–3 = 12/3/6/9 o'clock, bands 0–2 = low/level/high.</summary>
    public static int BearingTriggerId(int bearingIndex, int altitudeBand) =>
        1 + (3 * ((bearingIndex % 4 + 4) % 4)) + Math.Clamp(altitudeBand, 0, 2);

    /// <summary>Quantises an enemy's position in the warned aircraft's frame into the bearing
    /// trigger id, both halves decoded: the nearest of the four clock quadrants about world up
    /// (12 ahead, 3 right, 6 behind, 9 left, so the split falls at ±45°) and the low/level/high
    /// band per <see cref="LevelBandSin"/>.</summary>
    public static int BearingTriggerFor(Vector3 ownPos, Vector3 ownForward, Vector3 enemyPos)
    {
        var to = enemyPos - ownPos;
        var fwd = new Vector3(ownForward.X, 0f, ownForward.Z);
        var toH = new Vector3(to.X, 0f, to.Z);
        int bearing = 0;
        if (fwd.LengthSquared() > 1e-6f && toH.LengthSquared() > 1e-6f)
        {
            fwd = fwd.Normalized();
            toH = toH.Normalized();
            // Clockwise-from-ahead angle: +X of the (fwd, up) frame is the pilot's right.
            float deg = Mathf.RadToDeg(Mathf.Atan2(fwd.Cross(toH).Dot(Vector3.Down), fwd.Dot(toH)));
            bearing = (int)Mathf.Round(Mathf.Wrap(deg, 0f, 360f) / 90f) % 4;
        }
        float len = to.Length();
        float rise = len > 1e-6f ? to.Y / len : 0f;
        int band = rise < -LevelBandSin ? 0 : rise > LevelBandSin ? 2 : 1;
        return BearingTriggerId(bearing, band);
    }

    /// <summary>The taunt the pursuer's own geometry against the player raises, decoded whole:
    /// <see cref="TaFailShk"/> with the player inside the nose cone, <see cref="TaFailTail"/> with
    /// the player inside the astern cone, null between them. ⚠ The original dots the line against
    /// the vehicle's BACKWARD basis row, so a reading that takes that row for the nose swaps the
    /// two (docs/formats/combat-voice.md, "The pursue path").</summary>
    public static int? TauntTriggerFor(Vector3 ownPos, Vector3 ownForward, Vector3 playerPos)
    {
        var to = playerPos - ownPos;
        if (to.LengthSquared() < 1e-6f || ownForward.LengthSquared() < 1e-6f)
        {
            return null;
        }
        float cos = to.Normalized().Dot(ownForward.Normalized());
        return cos > TauntNoseCos ? TaFailShk
            : cos < -TauntTailCos ? TaFailTail
            : null;
    }

    /// <summary>Adds a speaker. <paramref name="team"/> uses the broadcast rule's vocabulary:
    /// a candidate is eligible on the caller's team or teamless
    /// (<see cref="AimAssist.NeutralTeam"/>).</summary>
    public Speaker Register(int id, int voId, int team, bool isPlayer, float talkerChance,
        float constitutionChance)
    {
        var speaker = new Speaker
        {
            Id = id,
            VoId = voId,
            Team = team,
            IsPlayer = isPlayer,
            TalkerChance = talkerChance,
            ConstitutionChance = constitutionChance,
        };
        _speakers[id] = speaker;
        _order.Add(speaker);
        return speaker;
    }

    /// <summary>The registered speaker, or null.</summary>
    public Speaker? Find(int id) => _speakers.TryGetValue(id, out var s) ? s : null;

    /// <summary>An addressed trigger on one speaker: the gate, in the decoded order, mute
    /// window, aliveness (force bypasses ONLY this), already-talking, the null-slot test (before
    /// anything rolls), the cooldown, then the talker roll arming the cooldown either way.</summary>
    public Decision Dispatch(int speakerId, int triggerId, float now, bool force = false)
    {
        if (_speakers.TryGetValue(speakerId, out var speaker))
        {
            return Gate(speaker, triggerId, FamilyOf(speaker, triggerId), now, force);
        }
        return new Decision(null, triggerId, null, "unregistered speaker", false);
    }

    /// <summary>The DI distress tiers off a whole-vehicle health fraction, tested
    /// most-severe-first (decoded): below 30 % → <c>DI-HighDmg</c>, else below 50 % →
    /// <c>DI-MedDmg</c>, else below 70 % → <c>DI-LowDmg</c>, else silence.</summary>
    public Decision NotifyDamage(int speakerId, float healthFraction, float now)
    {
        int? trigger = healthFraction < 0.30f ? DiHighDmg
            : healthFraction < 0.50f ? DiMedDmg
            : healthFraction < 0.70f ? DiLowDmg
            : null;
        return trigger is { } t
            ? Dispatch(speakerId, t, now)
            : new Decision(Find(speakerId), -1, null, "above every DI threshold", false);
    }

    /// <summary>The dying pilot's own death cry, id 20 (<c>DA</c>) on the player's team, id 21
    /// (<c>DE</c>) otherwise, dispatched with force because the speaker is already dead. The
    /// <c>Bail</c>/<c>NoBail</c> pick below the family root is a constitution roll, the format
    /// page's unconfirmed natural-candidate reading, invented here and named as such.</summary>
    public Decision DeathCry(int speakerId, bool onPlayersTeam, float now)
    {
        if (_speakers.TryGetValue(speakerId, out var speaker))
        {
            int triggerId = onPlayersTeam ? DaOwnTeam : DeEnemy;
            string root = onPlayersTeam ? "DA" : "DE";
            string family = root + (_rng.NextDouble() < speaker.ConstitutionChance ? "-Bail" : "-NoBail");
            return Gate(speaker, triggerId, family, now, force: true);
        }
        return new Decision(null, onPlayersTeam ? DaOwnTeam : DeEnemy, null, "unregistered speaker", false);
    }

    /// <summary>A broadcast: collects every living non-player speaker on the caller's team or
    /// teamless that owns the slot, picks one at random, and a failed roll passes the line to the
    /// NEXT candidate, wrapping, until one speaks or all have been tried (decoded). A candidate
    /// whose slot is cooling or who is mid-line is passed over without arming anything; a
    /// candidate that ROLLS and fails arms its own cooldown.</summary>
    public Decision Broadcast(int triggerId, int callerTeam, float now)
    {
        var candidates = new List<Speaker>();
        foreach (var s in _order)
        {
            if (s.Alive && !s.IsPlayer
                && (s.Team == callerTeam || s.Team == AimAssist.NeutralTeam)
                && _resolve(s.VoId, FamilyOf(s, triggerId)) != null)
            {
                candidates.Add(s);
            }
        }
        if (candidates.Count == 0)
        {
            return new Decision(null, triggerId, null, "no eligible speaker", false);
        }
        int start = _rng.Next(candidates.Count);
        Decision last = default;
        for (int k = 0; k < candidates.Count; k++)
        {
            var s = candidates[(start + k) % candidates.Count];
            last = Gate(s, triggerId, FamilyOf(s, triggerId), now, force: false);
            if (last.Clip != null)
            {
                return last;
            }
        }
        return last;
    }

    // The trigger's family root; ids 20/21 resolve DA/DE whole-family here (the Bail split is
    // DeathCry's), so availability tests see any death clip the pilot owns.
    private static string FamilyOf(Speaker speaker, int triggerId) =>
        triggerId >= 0 && triggerId < TriggerCount
            ? Mech3.CombatVoice.TriggerFamilies[triggerId]
            : "";

    private Decision Gate(Speaker speaker, int triggerId, string family, float now, bool force)
    {
        if (now < MuteWindowS)
        {
            return new Decision(speaker, triggerId, null, "muted (mission clock < 2 s)", false);
        }
        if (!speaker.Alive && !force)
        {
            return new Decision(speaker, triggerId, null, "speaker dead", false);
        }
        if (IsTalking?.Invoke(speaker.Id) == true)
        {
            return new Decision(speaker, triggerId, null, "already talking", false);
        }
        string? clip = _resolve(speaker.VoId, family);
        if (clip == null)
        {
            return new Decision(speaker, triggerId, null, NoClipOutcome, false);
        }
        if (triggerId >= 0 && triggerId < TriggerCount && now < speaker.NextAllowed[triggerId])
        {
            return new Decision(speaker, triggerId, null, "slot cooling", false);
        }
        float chance = speaker.TalkerChance
            * (triggerId >= 1 && triggerId <= 12 ? BearingChanceFactor : 1f);
        bool passed = _rng.NextDouble() < chance;
        if (triggerId >= 0 && triggerId < TriggerCount)
        {
            // ⚠ Both outcomes arm the cooldown, a failed roll silences the slot for 15 s.
            speaker.NextAllowed[triggerId] = now + SlotCooldownS;
        }
        return passed
            ? new Decision(speaker, triggerId, clip, $"Talker test passed. Play AI sound #{triggerId}.", true)
            : new Decision(speaker, triggerId, null, $"Talker test failed. Don't play AI sound #{triggerId}.", true);
    }

    /// <summary>One dispatch outcome: <see cref="Clip"/> non-null means play it (from
    /// <see cref="Speaker"/>'s aircraft); <see cref="Rolled"/> distinguishes a talker roll (the
    /// engine logs both outcomes) from a gate short-circuit.</summary>
    public readonly record struct Decision(Speaker? Speaker, int TriggerId, string? Clip,
        string Outcome, bool Rolled);

    /// <summary>One voiced pilot: identity, voice, team convention and the per-slot cooldown
    /// stamps. Mutable fields by design (death flips <see cref="Alive"/> mid-session).</summary>
    public sealed class Speaker
    {
        /// <summary>The aircraft's shooter id (<c>FlightController.PlayerIndex</c>).</summary>
        public int Id;

        /// <summary>The resolved pilot VO id (the accent chain's pick).</summary>
        public int VoId;

        /// <summary>The broadcast-eligibility team; <see cref="AimAssist.NeutralTeam"/> =
        /// teamless, eligible for every caller.</summary>
        public int Team;

        /// <summary>The player never speaks AI lines and is skipped by every election.</summary>
        public bool IsPlayer;

        /// <summary>Flipped false on death; only a forced dispatch (the death cries) passes.</summary>
        public bool Alive = true;

        /// <summary>The pilot's <c>talker_chance</c> (0.25 → 0.95 over the shipped pair).</summary>
        public float TalkerChance;

        /// <summary>The pilot's <c>constitution_chance</c> (0.35 → 0.95), the invented
        /// Bail/NoBail pick's probability.</summary>
        public float ConstitutionChance;

        internal float[] NextAllowed { get; } = new float[TriggerCount];

        /// <summary>The slot's next-allowed time, test visibility into the cooldown stamps.</summary>
        public float NextAllowedAt(int triggerId) => NextAllowed[triggerId];
    }
}
