using System;
using System.Collections.Generic;
using CSVM.Effects;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>
/// The <c>FLYOUT</c> <c>MODEL_ANIMATION</c> half of the pool. Every ordnance round runs its own
/// instance of the weapon's def (<c>he_rocket</c>, <c>sonic</c>, <c>torpedo_trail</c>, …) on the
/// real sequence interpreter, the pool standing in as the <see cref="ISequenceHost"/> for the
/// event kinds those defs author: node visibility, from-to tweens, spins, puffers, sounds and
/// sequence calls, all against the round's own model instance. The original starts the def at
/// spawn (<c>FUN_005aef40</c> hands weapon <c>+0x110</c> to <c>FUN_004edda0</c>) and its timeline
/// runs on the anim clock alone; nothing about it is gated on distance. Decode:
/// docs/org/ordnanceTypes.md, "The launch look is the def's timeline".
/// </summary>
public sealed partial class ProjectilePool : ISequenceHost
{
    // The def each weapon's MODEL_ANIMATION names, resolved once from the world program (misses
    // cached as null and logged once).
    private readonly Dictionary<string, AnimDefinition?> _flyoutDefs = new();
    // One PufferState per authored PUFFER_STATE event, so every round built from the same event
    // shares the object and TrailEmitter reuse can match on identity.
    private readonly Dictionary<AnimEvent, PufferState> _pufferStates = new();
    private readonly HashSet<string> _flyoutAnimLogged = new();
    // The round whose instance is being advanced, and its pose for anything the dispatch places:
    // the interpreter's host seam carries no round identity, so the caller pins it around Advance.
    private int _animSlot = -1;
    private Vector3 _animPos;
    private Basis _animBasis = Basis.Identity;

    Action<EventDispatch>? ISequenceHost.OnEventDispatched => null;

    Func<bool>? ISequenceHost.PendingWait => null;

    bool ISequenceHost.EvaluateCondition(AnimData? condition, AnimDefinition def, Node3D? anchor) => false;

    bool ISequenceHost.Dispatch(AnimEvent ev, AnimDefinition def, Node3D? anchor, bool instant, out float duration)
    {
        duration = 0f;
        if (_animSlot < 0 || _proj[_animSlot].Rig is not { } rig)
            return true;
        ref var p = ref _proj[_animSlot];
        switch (ev.Kind)
        {
            case "ObjectActiveState":
                if (TargetOf(rig, def, ev) is { } shown)
                    shown.Visible = ev.Data.Bool("state");
                return true;

            case "ObjectScaleState":
                if (TargetOf(rig, def, ev) is { } scaled)
                {
                    var held = scaled.Transform;
                    scaled.Transform = new Transform3D(
                        held.Basis.Orthonormalized().Scaled(AnimRuntime.NonSingularScale(ev.Data.Vec3("state"))),
                        held.Origin);
                }
                return true;

            case "ObjectMotionFromTo":
                {
                    float runTime = ev.Data.Num("run_time") ?? 0f;
                    if (TargetOf(rig, def, ev) is { } tweened && FlyoutTween.Create(tweened, ev.Data, runTime) is { } tween)
                    {
                        if (instant || runTime <= 0f)
                            tween.Seek(runTime);
                        else
                            AddMotion(rig, tween);
                    }
                    duration = instant ? 0f : runTime;
                    return true;
                }

            case "ObjectMotion":
                {
                    // Only the steady spin is a flyout's business; the ballistic forms belong to
                    // debris and would need the full motion runtime.
                    if (ev.Data.Obj("xyz_rotation") is not { } spin || ev.Data.Has("translation")
                        || ev.Data.Has("translation_range") || ev.Data.Has("scale"))
                    {
                        Unsupported(def, ev.Kind);
                        return true;
                    }
                    var rate = spin.Vec3("initial");
                    float runTime = ev.Data.Num("run_time") ?? 0f;
                    if (rate.IsZeroApprox() || TargetOf(rig, def, ev) is not { } spun)
                        return true;
                    if (spun == p.Model)
                    {
                        // The root's roll rides the flight pose, which is rewritten every frame.
                        p.RollRate = rate.Z;
                    }
                    else if (!HasSpinOn(rig, spun, rate, runTime))
                    {
                        var motion = new SpinMotion(spun, rate, runTime);
                        if (instant)
                            motion.Seek(0f);
                        else
                            AddMotion(rig, motion);
                    }
                    duration = instant ? 0f : runTime;
                    return true;
                }

            case "PufferState":
                if ((ev.Data.Num("active_state") ?? 0f) > 0f)
                    AcquireEmitter(rig, PufferStateFor(ev), p.Weapon);
                else if (ev.Data.Str("name") is { } stopped)
                    StopEmitter(rig, stopped);
                return true;

            case "Sound":
                if (!instant && ev.Data.Str("name") is { } sound)
                    PlaySound(sound, p.Pos);
                return true;

            case "CallSequence":
                if (ev.Data.Str("name") is { } called)
                    rig.Inst.CallSequence(called);
                return true;

            case "StopSequence":
                if (ev.Data.Str("name") is { } halted)
                    rig.Inst.StopSequence(halted);
                return true;

            case "CallAnimation":
                // A world effect the def calls at the round; placed where the round is now.
                if (!instant && ev.Data.Str("name") is { } effect)
                    EffectSink?.Invoke(effect, p.Pos, Basis.Identity, null, 0f);
                return true;

            case "Loop":
            case "If":
            case "Elseif":
            case "Else":
            case "Endif":
                return false;

            default:
                Unsupported(def, ev.Kind);
                return true;
        }
    }

    // Lets the round's emitters go, live smoke decaying where it was left, and drops the instance.
    private static void ReleaseRig(ref Proj p)
    {
        if (p.Rig is not { } rig)
            return;
        foreach (var e in rig.Emitters)
        {
            e.Puffer.Stop();
            e.InUse = false;
        }
        rig.Emitters.Clear();
        rig.Motions?.Clear();
        p.Rig = null;
    }

    private static void IndexRigNodes(FlyoutRig rig, Node3D node)
    {
        if (node.HasMeta(AnimRuntime.IndexMeta))
            rig.ByIndex.TryAdd(node.GetMeta(AnimRuntime.IndexMeta).AsInt32(), node);
        if (node.HasMeta(AnimRuntime.NameMeta))
            rig.ByName.TryAdd(node.GetMeta(AnimRuntime.NameMeta).AsString(), node);
        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            if (node.GetChild(i) is Node3D child3d)
                IndexRigNodes(rig, child3d);
        }
    }

    // The event's target inside this round's model: the def's symbol table binds the name to a
    // gamez index first, the original name second. Null when the model lacks it (or is absent).
    private static Node3D? TargetOf(FlyoutRig rig, AnimDefinition def, AnimEvent ev)
    {
        if ((ev.Data.Str("node") ?? ev.Data.Str("name")) is not { } name)
            return null;
        if (def.NodeRefs.TryGetValue(name, out int index) && rig.ByIndex.TryGetValue(index, out var bound))
            return bound;
        return rig.ByName.TryGetValue(name, out var named) ? named : null;
    }

    private static void StopEmitter(FlyoutRig rig, string name)
    {
        for (int i = rig.Emitters.Count - 1; i >= 0; i--)
        {
            var e = rig.Emitters[i];
            if (!string.Equals(e.State.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            e.Puffer.Stop();
            e.InUse = false;
            rig.Emitters.RemoveAt(i);
        }
    }

    // A later motion on the same node replaces the earlier one, the runtime's own eviction rule.
    private static void AddMotion(FlyoutRig rig, IAnimMotion motion)
    {
        rig.Motions ??= new List<IAnimMotion>();
        for (int i = rig.Motions.Count - 1; i >= 0; i--)
        {
            if (rig.Motions[i].Target == motion.Target)
                rig.Motions.RemoveAt(i);
        }
        rig.Motions.Add(motion);
    }

    private static bool HasSpinOn(FlyoutRig rig, Node3D target, Vector3 rate, float runTime)
    {
        if (rig.Motions == null)
            return false;
        foreach (var m in rig.Motions)
        {
            if (m is SpinMotion spin && spin.Target == target && spin.Matches(rate, runTime))
                return true;
        }
        return false;
    }

    // Starts the round's MODEL_ANIMATION def the way the original's spawn does: RESET_STATE
    // posed, the initial sequences added, and their t=0 events fired in the same call.
    private void StartFlyoutAnim(int slot, Vector3 origin, Basis basis)
    {
        ref var p = ref _proj[slot];
        if (FlyoutDefFor(p.Weapon) is not { } def)
            return;
        var rig = new FlyoutRig { Def = def, Inst = new AnimInstance(def, p.Model) };
        if (p.Model != null)
            IndexRigNodes(rig, p.Model);
        p.Rig = rig;
        _animSlot = slot;
        _animPos = origin;
        _animBasis = basis;
        try
        {
            if (def.ResetState != null)
            {
                foreach (var ev in def.ResetState.Events)
                    ((ISequenceHost)this).Dispatch(ev, def, p.Model, instant: true, out _);
            }
            foreach (var seq in def.Sequences)
            {
                if (!seq.OnCallOnly)
                    rig.Inst.AddRunner(seq);
            }
            rig.Inst.Advance(this, 0f);
        }
        finally
        {
            _animSlot = -1;
        }
    }

    // One sim step of the round's def: the interpreter, then the live motions, then every emitter
    // it has running follows the round's new pose.
    private void AdvanceFlyoutAnim(int slot, ref Proj p, float dt, Vector3 pos, Basis basis)
    {
        var rig = p.Rig!;
        _animSlot = slot;
        _animPos = pos;
        _animBasis = basis;
        try
        {
            rig.Inst.Advance(this, dt);
        }
        finally
        {
            _animSlot = -1;
        }
        if (rig.Motions != null)
        {
            for (int i = rig.Motions.Count - 1; i >= 0; i--)
            {
                rig.Motions[i].Tick(dt);
                if (rig.Motions[i].Finished)
                    rig.Motions.RemoveAt(i);
            }
        }
        foreach (var e in rig.Emitters)
            e.Puffer.Emit(pos, basis, dt);
    }

    private AnimDefinition? FlyoutDefFor(WeaponDef weapon)
    {
        if (_flyoutAnims == null || weapon.Flyout?.ModelAnimation is not { } animName)
            return null;
        if (_flyoutDefs.TryGetValue(animName, out var def))
            return def;
        foreach (var candidate in _flyoutAnims.ByAnimName(animName))
        {
            def = candidate;
            break;
        }
        _flyoutDefs[animName] = def;
        if (def != null)
            Log.Info("weapons", $"flyout anim '{animName}' ({weapon.Id}): {def.Sequences.Count} sequence(s), reset {(def.ResetState?.Events.Count ?? 0)} event(s)");
        else
            Log.Info("weapons", $"flyout anim '{animName}' ({weapon.Id}): not in the anim program, no trail");
        return def;
    }

    private PufferState PufferStateFor(AnimEvent ev)
    {
        if (!_pufferStates.TryGetValue(ev, out var state))
        {
            state = PufferState.FromAnimEvent(ev.Data);
            _pufferStates[ev] = state;
        }
        return state;
    }

    // Lends the round an emitter for one authored PUFFER_STATE, reused from an earlier round's
    // once its smoke has decayed, else freshly built; homed at the round's current pose.
    private void AcquireEmitter(FlyoutRig rig, PufferState state, WeaponDef weapon)
    {
        TrailEmitter? emitter = null;
        foreach (var e in _trailEmitters)
        {
            if (!e.InUse && e.State == state && e.Puffer.LiveCount == 0)
            {
                emitter = e;
                break;
            }
        }
        if (emitter == null)
        {
            var puffer = Puffer.Create(state, _textures, sustained: true, ambience: _ambience);
            if (puffer == null)
            {
                if (_flyoutAnimLogged.Add("trail:" + state.Name))
                    Log.Info("weapons", $"flyout puffer '{state.Name}' ({weapon.Id}) has no textures in this chapter, skipped");
                return;
            }
            AddChild(puffer);
            emitter = new TrailEmitter { Puffer = puffer, State = state };
            _trailEmitters.Add(emitter);
        }
        emitter.InUse = true;
        emitter.Puffer.Emit(_animPos, _animBasis, 0f);
        rig.Emitters.Add(emitter);
    }

    private void Unsupported(AnimDefinition def, string kind)
    {
        if (_flyoutAnimLogged.Add(def.AnimName + ":" + kind))
            Log.Info("weapons", $"flyout anim '{def.AnimName}': {kind} is not run on a round, event skipped");
    }

    // One round's running def: the instance, the model's nodes by gamez index and name, the
    // emitters it has on, and the tweens and spins it drives.
    private sealed class FlyoutRig
    {
        public readonly Dictionary<int, Node3D> ByIndex = new();
        public readonly Dictionary<string, Node3D> ByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<TrailEmitter> Emitters = new();
        public AnimDefinition Def = null!;
        public AnimInstance Inst = null!;
        public List<IAnimMotion>? Motions;
    }

    // An OBJECT_MOTION_FROM_TO on a flyout node: FromToMotion's rule without the world runtime.
    // Channels are absolute poses in the parent frame; an absent channel holds the live value.
    private sealed class FlyoutTween : IAnimMotion
    {
        private Basis _heldRot;
        private Vector3 _heldEuler, _heldScale, _heldOrigin;
        private Vector3? _tFrom, _tTo, _rFrom, _rTo, _sFrom, _sTo;
        private float _t, _runTime;

        public Node3D Target { get; private init; } = null!;

        public (AnimDefinition Def, Node3D? Anchor) Owner { get; set; }

        public bool Finished => _t >= _runTime;

        public static FlyoutTween? Create(Node3D target, AnimData data, float runTime)
        {
            var held = target.Transform;
            var rot = held.Basis.Orthonormalized();
            var m = new FlyoutTween
            {
                Target = target,
                _heldRot = rot,
                _heldEuler = rot.GetEuler(EulerOrder.Yxz),
                _heldScale = held.Basis.Scale,
                _heldOrigin = held.Origin,
                _runTime = Mathf.Max(runTime, 0f),
            };
            (m._tFrom, m._tTo) = Channel(data, "translate");
            (m._rFrom, m._rTo) = Channel(data, "rotate");
            (m._sFrom, m._sTo) = Channel(data, "scale");
            return m._tTo != null || m._rTo != null || m._sTo != null ? m : null;
        }

        public void Tick(float dt) => Seek(_t + dt);

        public void Seek(float t)
        {
            _t = t;
            float u = _runTime <= 0f ? 1f : Mathf.Clamp(t / _runTime, 0f, 1f);
            var origin = _tTo is { } tTo ? (_tFrom ?? _heldOrigin).Lerp(tTo, u) : _heldOrigin;
            var rot = _rTo is { } rTo
                ? Basis.FromEuler((_rFrom ?? _heldEuler).Lerp(rTo, u), EulerOrder.Yxz) : _heldRot;
            var scale = _sTo is { } sTo ? (_sFrom ?? _heldScale).Lerp(sTo, u) : _heldScale;
            Target.Transform = new Transform3D(rot.Scaled(AnimRuntime.NonSingularScale(scale)), origin);
        }

        private static (Vector3?, Vector3?) Channel(AnimData data, string name)
        {
            var ch = data.Obj(name);
            if (ch == null)
                return (null, null);
            return (ch.Has("from") ? ch.Vec3("from") : null, ch.Has("to") ? ch.Vec3("to") : null);
        }
    }
}
