using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3.Anim;

/// <summary>A body that has come back down owing its <c>BOUNCE_SEQUENCE</c>: the sequence name and
/// the <c>(def, anchor)</c> instance it belongs to, plus the node for the debug line.
/// <see cref="MotionSet.Tick"/> has already removed the motion before it returns one of these, so a
/// landing can never name a body that is still in the collection.</summary>
internal readonly record struct Landing(
    AnimDefinition Def, Node3D? Anchor, string Bounce, Node3D Target, bool ByContact);

/// <summary>The runtime's live motions and the two rules that govern registering one: an owner
/// stamp, and one motion per <c>(Target, Channel)</c>. Also answers the pending-bounce question the
/// instance walk retires on. Holds <see cref="Node3D"/> references but dereferences none of them,
/// every operation here is identity comparison, so the collection's behaviour is engine-free even
/// though its type is not.</summary>
internal sealed class MotionSet
{
    private readonly List<IAnimMotion> _motions = new();

    /// <summary>Live motions, for <c>LogMotions</c>' truncated list. Cumulative
    /// <see cref="LaunchCount"/>, not this, is the headless answer to whether a launch happened.</summary>
    public IReadOnlyList<IAnimMotion> Live => _motions;

    public int Count => _motions.Count;

    /// <summary>Running count of ballistic <see cref="MotionRuntime"/> bodies registered.
    /// ⚠ <see cref="Reset"/> deliberately leaves it standing, every reader takes a delta across an
    /// event, so zeroing it here would make a crash respawn read as a negative launch count.</summary>
    public int LaunchCount { get; private set; }

    /// <summary>Contact-tested bodies that landed on a collider, vs ran their clock out. Only
    /// bodies that TEST contact are counted, so a zero after a tested launch is a real failure,
    /// not a query finding nothing.
    /// ⚠ Not zeroed by <see cref="Reset"/>, same rule as <see cref="LaunchCount"/>.</summary>
    public int ContactLandings { get; private set; }

    public int ClockEndings { get; private set; }

    /// <summary>The same tally split by tier, the default ground column against the authored
    /// <c>do_intersections</c> sweep. ⚠ The split is what makes the pair readable: the sweep's 166
    /// events can report a healthy-looking total on their own, so a combined counter cannot tell a
    /// working default tier from an inert one. Same <see cref="Reset"/> rule as the totals.</summary>
    public int ColumnLandings { get; private set; }

    public int ColumnClockEndings { get; private set; }

    public int SweepLandings { get; private set; }

    public int SweepClockEndings { get; private set; }

    /// <summary>Registers a motion, replacing any motion already driving the same node's channel. An
    /// object has exactly one motion in the original, and the data relies on it: C1/IA1's
    /// startanims run `hangar3_doors` (doors to ±50) and then `mp_hangar3_open` (the same
    /// doors to ±25), where the later one is meant to win. Without this both tween the same
    /// node every frame and the outcome depends on list order.</summary>
    public void Add(IAnimMotion motion, AnimDefinition def, Node3D? anchor)
    {
        motion.Owner = (def, anchor);
        // Evict only a prior motion on the SAME channel: a node can carry one transform motion
        // AND one opacity fade at once (the crash dust scales via a MotionRuntime while an
        // OpacityFade fades it), and those write different data, so neither displaces the other.
        _motions.RemoveAll(m => m.Target == motion.Target && m.Channel == motion.Channel);
        _motions.Add(motion);
        if (motion is MotionRuntime)
            LaunchCount++;
    }

    /// <summary>Advances the live motions <paramref name="only"/> accepts (all by default), removes
    /// the finished ones, and returns those owing a <c>BOUNCE_SEQUENCE</c>. The caller dispatches
    /// them. Returning them, not calling out, makes remove-before-dispatch structural. The
    /// cutscene fast-forward <paramref name="rate"/> scales a motion by its owner's rate, so a
    /// sped-up shot's pose keeps step with its callbacks.</summary>
    public IReadOnlyList<Landing> Tick(float dt, CutsceneFastForward? rate = null,
        Func<(AnimDefinition Def, Node3D? Anchor), bool>? only = null)
    {
        List<Landing>? landed = null;
        for (int i = _motions.Count - 1; i >= 0; i--)
        {
            // ⚠ A motion outlives the node it drives: an aircraft freed mid-animation (an airframe
            // swap, a rig torn down) leaves one here, and writing a transform to a disposed object
            // throws out of the whole runtime advance rather than losing one motion.
            if (!GodotObject.IsInstanceValid(_motions[i].Target))
            {
                _motions.RemoveAt(i);
                continue;
            }

            if (only != null && !only(_motions[i].Owner))
                continue;

            _motions[i].Tick(dt * (rate?.RateFor(_motions[i].Owner.Def) ?? 1f));
            // Central, rather than in each motion class: every transform motion has the same
            // render problem and the same answer, and one call here cannot be forgotten by a
            // motion added later. Inert on any clock but the realtime one.
            if (_motions[i].Channel == MotionChannel.Transform)
                RenderPoses.Record(_motions[i].Target);
            if (!_motions[i].Finished)
                continue;
            var done = _motions[i];
            _motions.RemoveAt(i);
            if (done is MotionRuntime { TestsContact: true } tested)
            {
                bool column = tested.ContactTier == MotionContactTier.Column;
                if (tested.LandedByContact)
                {
                    ContactLandings++;
                    if (column)
                        ColumnLandings++;
                    else
                        SweepLandings++;
                }
                else
                {
                    ClockEndings++;
                    if (column)
                        ColumnClockEndings++;
                    else
                        SweepClockEndings++;
                }
            }

            // BOUNCE_SEQUENCE fires the def's own sparkoutN elsewhere; this only reports the
            // landing, it does not act on it.
            if (done is MotionRuntime { PendingBounce: { } bounce })
            {
                landed ??= new List<Landing>();
                landed.Add(new Landing(done.Owner.Def, done.Owner.Anchor, bounce, done.Target,
                    done is MotionRuntime { LandedByContact: true }));
            }
        }

        return (IReadOnlyList<Landing>?)landed ?? Array.Empty<Landing>();
    }

    /// <summary>Drops the motions a stopped instance registered, attributed to the exact
    /// <c>(def, anchor)</c> that registered them.</summary>
    public void DiscardFor(AnimDefinition def, Node3D? anchor) =>
        _motions.RemoveAll(m => m.Owner.Def == def && m.Owner.Anchor == anchor);

    /// <summary>Ends, where they stand, the ballistic bodies one of <paramref name="events"/>
    /// launched for this <c>(def, anchor)</c>: a <c>STOP_SEQUENCE</c> on the sequence holding
    /// them. The original integrates an <c>OBJECT_MOTION</c> only inside its handler, and a
    /// stopped sequence is never dispatched again (docs/org/sequences.md). No bounce is owed, and
    /// the pose is left as it is rather than retracted.</summary>
    public int HaltLaunchedBy(AnimDefinition def, Node3D? anchor, IReadOnlyList<AnimEvent> events) =>
        _motions.RemoveAll(m => m is MotionRuntime { LaunchedBy: { } by } body
                                && body.Owner.Def == def && body.Owner.Anchor == anchor
                                && events.Contains(by));

    /// <summary>Drops every live motion, the crash rig's respawn. Leaves
    /// <see cref="LaunchCount"/> alone.</summary>
    public void Reset() => _motions.Clear();

    /// <summary>Whether an instance still has a body in the air: the retirement hold
    /// <see cref="AnimRuntime.Retirable"/> consults (this module's docs/architecture.md entry). A
    /// launch's runner finishes long before a piece falling to a distant sea lands, and the
    /// original's parked <c>OBJECT_MOTION</c> entry holds its record open for the whole flight.
    /// ⚠ Ballistic bodies only, never any live motion. An unbounded <see cref="SpinMotion"/> never
    /// finishes, so pinning on any live motion makes the instance immortal.</summary>
    public bool Airborne(AnimDefinition def, Node3D? anchor) =>
        _motions.Any(m => m is MotionRuntime { Ballistic: true }
                          && m.Owner.Def == def && m.Owner.Anchor == anchor);

    /// <summary>Whether an instance has a body carrying an armed <c>BOUNCE_SEQUENCE</c> branch.
    /// A strict subset of <see cref="Airborne"/>, since only a ballistic body arms one and it is
    /// swept out of the collection on the tick it lands. Observation only; the retirement hold is
    /// <see cref="Airborne"/>, which a body naming no branch also takes.</summary>
    public bool OwesBounce(AnimDefinition def, Node3D? anchor) =>
        _motions.Any(m => m is MotionRuntime { PendingBounce: not null }
                          && m.Owner.Def == def && m.Owner.Anchor == anchor);

    /// <summary>Is some live motion already driving this node's transform? A launch onto such a
    /// node is a TAKEOVER, not a fresh throw, see <see cref="MotionRuntime.Create"/>'s re-home
    /// rule. Asked before <see cref="Add"/> evicts the incumbent, which is the only moment the
    /// answer exists.</summary>
    public bool DrivesTransform(Node3D target) =>
        _motions.Any(m => m.Target == target && m.Channel == MotionChannel.Transform);

    /// <summary>The target's own still-live <see cref="FromToMotion"/>, if any: the seam
    /// <see cref="PoseChannel"/> uses to carry a same-tick sibling event's channel into its
    /// replacement instead of losing it to <see cref="Add"/>'s eviction. Null for a ballistic
    /// <see cref="MotionRuntime"/> body, which has no channel of this shape to carry.</summary>
    public FromToMotion? LiveFromToMotion(Node3D target) =>
        _motions.FirstOrDefault(m => m.Target == target && m.Channel == MotionChannel.Transform)
            as FromToMotion;

    /// <summary>Whether this exact spin is already running, so a <c>Loop{-1}</c> sequence
    /// re-asserting it is left alone instead of rebuilt. Checked before <see cref="Add"/>, whose
    /// evict-then-insert would otherwise treat every re-assert as a fresh launch.
    /// ⚠ A needless rebuild restarts the clock at 0 every frame, so the prop looks driven in the
    /// logs while sitting almost still.</summary>
    public bool HasSpinOn(Node3D target, Vector3 rate, float runTime) =>
        _motions.Any(m => m.Target == target && m is SpinMotion s && s.Matches(rate, runTime));
}
