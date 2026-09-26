using System.Collections.Generic;
using CSVM.Flight.Weapons;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>What a beeper tag reads off the aircraft it paints: the sim position the seeker's
/// query measures to, whether the aircraft is still in play (a tag on one that is not collapses),
/// and the team the tagging gate tests the shooter against.</summary>
public interface IBeeperSubject
{
    /// <summary>The flight model's world position.</summary>
    Vector3 WorldPosition { get; }

    /// <summary>Whether the aircraft is present as a real object.</summary>
    bool InPlay { get; }

    /// <summary>The aircraft's team.</summary>
    int Team { get; }
}

/// <summary>The beeper tag's rules with no aircraft in them, decoded from <c>FUN_004b89c0</c>
/// (the countdown) and <c>FUN_004b8b50</c> (the seeker's pick). <see cref="BeeperTags{TAircraft}"/>
/// runs them over its list; the constants here are the routines' own literals
/// (docs/org/ordnanceTypes.md "The beeper and the seeker, which are one weapon in two halves").</summary>
public static class BeeperTagRule
{
    /// <summary>What a painting tag's countdown is slammed to when its aircraft leaves play.</summary>
    public const float DeadSlamS = -1f;

    /// <summary>A tag is deleted once its countdown sits at or below this: five seconds of tail
    /// after expiry, so nothing holding a reference sees it vanish.</summary>
    public const float DeleteAtOrBelowS = -5f;

    /// <summary>A better-aligned candidate replaces the best if its SQUARED distance is under
    /// this multiple of the best's (about 9.5% farther as a plain distance).</summary>
    public const float BetterAlignedRatioSq = 1.2f;

    /// <summary>A worse-or-equally-aligned candidate must be strictly nearer than this multiple
    /// of the best's squared distance before its alignment is even considered.</summary>
    public const float NearerRatioSq = 1.0f;

    /// <summary>A nearer candidate with a dot at or under this is taken outright: with the
    /// inverted dot, that is any tag less than about 134° off the round's nose. So range leads
    /// the pick, and only a tag well behind the round falls through to the margin below.</summary>
    public const float NearerTakenAtOrBelowDot = 0.7f;

    /// <summary>A nearer candidate behind the round is taken only if its dot is under the best's
    /// plus this margin: it may give up less than 0.1 of alignment.</summary>
    public const float NearerMarginDot = 0.1f;

    /// <summary>One frame of a tag's countdown. While it still paints, an aircraft out of play
    /// slams it to <see cref="DeadSlamS"/> before the frame's decrement; the countdown then runs
    /// down by <paramref name="dt"/>, and the frame that takes it to or below zero ends the paint
    /// (the original releases the effect there and never slams again). Returns the countdown.</summary>
    public static float Step(ref float remainingS, ref bool painting, float dt, bool subjectGone)
    {
        if (subjectGone && painting)
            remainingS = DeadSlamS;
        remainingS -= dt;
        if (painting && remainingS <= 0f)
            painting = false;
        return remainingS;
    }

    /// <summary>The query's alignment score for a tag: the unit vector FROM the tag TOWARD the
    /// round, dotted with the round's heading. A tag dead ahead scores near -1, so LOWER is better
    /// aligned. A tag exactly on the round scores 0.</summary>
    public static float AlignmentDot(Vector3 roundPos, Vector3 roundHeading, Vector3 tagPos)
    {
        var fromTag = roundPos - tagPos;
        float length = fromTag.Length();
        if (length <= 0f)
            return 0f;
        return (fromTag / length).Dot(roundHeading);
    }

    /// <summary>Whether a candidate replaces the running best (<c>0x004b8c03</c>–<c>0x004b8c72</c>):
    /// better aligned (a lower dot) wins under <see cref="BetterAlignedRatioSq"/>; otherwise it must
    /// be strictly nearer AND either sit at or under <see cref="NearerTakenAtOrBelowDot"/> or give up
    /// under <see cref="NearerMarginDot"/> of alignment. Both ratios are of SQUARED distances.</summary>
    public static bool Prefers(float candDot, float candDistSq, float bestDot, float bestDistSq)
    {
        float ratioSq = candDistSq * (1f / bestDistSq);
        if (candDot < bestDot)
            return ratioSq < BetterAlignedRatioSq;
        if (!(ratioSq < NearerRatioSq))
            return false;
        return candDot <= NearerTakenAtOrBelowDot || candDot < bestDot + NearerMarginDot;
    }
}

/// <summary>The world's beeper tags (docs/org/ordnanceTypes.md "The full beeper loop"): a
/// <c>BEEPER</c> hit on an aircraft calls <see cref="TryTag"/>, the session steps the list once per
/// sim step, and a <c>BEEPER_SEEKER</c> round asks <see cref="PickTarget"/> every frame for the
/// painted aircraft to steer at. A tag paints for the weapon's <c>TIME</c>, collapses to -1 s when
/// its aircraft leaves play, and lingers unusable for five seconds after expiry before it is
/// deleted. Every rule that gates a tag's creation lives here too: the shooter must be hostile to
/// the victim, an aircraft with a live tag takes no second one (the first is NOT refreshed), and
/// the original stamps its clock on each creation and refuses another at the same clock, which
/// here is one tag per sim step.</summary>
public sealed class BeeperTags<TAircraft>
    where TAircraft : class, IBeeperSubject
{
    private readonly List<Tag> _tags = new();
    private bool _taggedThisStep;

    /// <summary>Every tag in the list, tail included.</summary>
    public int Count => _tags.Count;

    /// <summary>The tags a seeker can still pick: countdown strictly above zero.</summary>
    public int PaintingCount
    {
        get
        {
            int n = 0;
            foreach (var t in _tags)
            {
                if (t.RemainingS > 0f)
                    n++;
            }
            return n;
        }
    }

    /// <summary>Whether <paramref name="aircraft"/> carries a tag a seeker can pick
    /// (<c>FUN_004b8ca0</c>: countdown strictly above zero). A tag in its tail does not count.</summary>
    public bool IsPainted(TAircraft aircraft) => LiveTagOf(aircraft) != null;

    /// <summary>The countdown of <paramref name="aircraft"/>'s live tag, or null when it has
    /// none: what a suite reads to time the paint.</summary>
    public float? RemainingFor(TAircraft aircraft) => LiveTagOf(aircraft)?.RemainingS;

    /// <summary>The hit-side entry (<c>FUN_004b9bc0</c>'s <c>BEEPER</c> branch through
    /// <c>FUN_004b8ce0</c>): paints <paramref name="victim"/> for <paramref name="timeSeconds"/>
    /// (the weapon's <c>BeeperTime</c>) when the shooter's team is hostile to the victim's, the
    /// victim is in play, it carries no live tag, and no tag was created this step. Returns whether
    /// a tag was made. The round deals no damage either way; that is the caller's to zero.</summary>
    public bool TryTag(int shooterTeam, TAircraft victim, float timeSeconds)
    {
        if (!AimAssist.Hostile(shooterTeam, victim.Team) || !victim.InPlay)
            return false;
        if (LiveTagOf(victim) != null || _taggedThisStep)
            return false;
        _tags.Add(new Tag(victim, timeSeconds));
        _taggedThisStep = true;
        return true;
    }

    /// <summary>Drops every tag: the session teardown and a suite's reset.</summary>
    public void Clear()
    {
        _tags.Clear();
        _taggedThisStep = false;
    }

    /// <summary>One sim step for every tag (<c>FUN_004b8ad0</c>): each runs
    /// <see cref="BeeperTagRule.Step"/> and is deleted once its countdown sits at or below
    /// <see cref="BeeperTagRule.DeleteAtOrBelowS"/>. Re-arms the one-tag-per-step gate.</summary>
    public void SimStep(float dt)
    {
        _taggedThisStep = false;
        for (int i = _tags.Count - 1; i >= 0; i--)
        {
            var tag = _tags[i];
            float remaining = BeeperTagRule.Step(ref tag.RemainingS, ref tag.Painting, dt, !tag.Subject.InPlay);
            if (!(BeeperTagRule.DeleteAtOrBelowS < remaining))
                _tags.RemoveAt(i);
        }
    }

    /// <summary>The seeker's per-frame query (<c>FUN_004b8b50</c>): over every tag with a
    /// countdown strictly above zero, in list order, keeps a running best by
    /// <see cref="BeeperTagRule.Prefers"/> on the squared distance to the round and the inverted
    /// alignment dot, and returns its aircraft, or null with nothing painted. An aircraft with no
    /// tag is never returned. In-play is not tested here: a tag on a dead aircraft collapses on
    /// the next step, as the original's does.</summary>
    public TAircraft? PickTarget(Vector3 roundPos, Vector3 roundHeading)
    {
        Tag? best = null;
        float bestDot = 0f;
        float bestDistSq = 0f;
        foreach (var tag in _tags)
        {
            if (!(tag.RemainingS > 0f))
                continue;
            var tagPos = tag.Subject.WorldPosition;
            float distSq = roundPos.DistanceSquaredTo(tagPos);
            float dot = BeeperTagRule.AlignmentDot(roundPos, roundHeading, tagPos);
            if (best != null && !BeeperTagRule.Prefers(dot, distSq, bestDot, bestDistSq))
                continue;
            best = tag;
            bestDot = dot;
            bestDistSq = distSq;
        }
        return best?.Subject;
    }

    private Tag? LiveTagOf(TAircraft aircraft)
    {
        foreach (var t in _tags)
        {
            if (ReferenceEquals(t.Subject, aircraft) && t.RemainingS > 0f)
                return t;
        }
        return null;
    }

    private sealed class Tag
    {
        public readonly TAircraft Subject;
        public float RemainingS;
        public bool Painting = true;

        public Tag(TAircraft subject, float remainingS)
        {
            Subject = subject;
            RemainingS = remainingS;
        }
    }
}

// The aircraft already carries the three members by these names; this part only names the
// contract, so the registry and its tests can run without an engine.
public partial class FlightController : IBeeperSubject
{
}
